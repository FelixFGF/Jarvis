using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Jarvis.Update.Core;

public sealed record UpdatePackage(GitHubRelease Release, SignedUpdateManifest Manifest, byte[] ManifestBytes, string Signature, Uri PackageUri);

public sealed class GitHubUpdateClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string _verificationKeyPem;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public GitHubUpdateClient(HttpClient? httpClient = null, string? verificationKeyPem = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _ownsClient = httpClient is null;
        _verificationKeyPem = verificationKeyPem ?? UpdateSecurity.PinnedPublicKeyPem;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Jarvis-Desktop-Updater/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(CancellationToken cancellationToken = default)
    {
        var uri = new Uri($"https://api.github.com/repos/{UpdateSecurity.RepositoryOwner}/{UpdateSecurity.RepositoryName}/releases?per_page=100");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var releases = new List<GitHubRelease>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var draft = GetBool(element, "draft");
            var prerelease = GetBool(element, "prerelease");
            var tag = GetString(element, "tag_name");
            if (draft || !SemanticVersion.TryParse(tag, out var version) || version is null) continue;
            if (!Uri.TryCreate(GetString(element, "html_url"), UriKind.Absolute, out var html) || html.Scheme != Uri.UriSchemeHttps || !html.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) continue;
            var assets = new List<ReleaseAsset>();
            if (element.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
            foreach (var asset in assetArray.EnumerateArray())
            {
                var name = GetString(asset, "name");
                if (!Uri.TryCreate(GetString(asset, "browser_download_url"), UriKind.Absolute, out var download) || !long.TryParse(asset.GetProperty("size").ToString(), out var size)) continue;
                assets.Add(new ReleaseAsset(name, download, size));
            }
            releases.Add(new GitHubRelease(version, tag, GetString(element, "name"), GetString(element, "body"), html, prerelease, draft, assets));
        }
        return releases;
    }

    public async Task<UpdatePackage?> GetSignedPackageAsync(GitHubRelease release, CancellationToken cancellationToken = default)
    {
        var manifestAsset = release.Assets.FirstOrDefault(a => a.Name == UpdateSecurity.ManifestName);
        var signatureAsset = release.Assets.FirstOrDefault(a => a.Name == UpdateSecurity.SignatureName);
        var packageAsset = release.Assets.FirstOrDefault(a => a.Name == UpdateSecurity.PackageName);
        if (manifestAsset is null || signatureAsset is null || packageAsset is null ||
            manifestAsset.Size is < 1 or > 65_536 || signatureAsset.Size is < 1 or > 16_384 || packageAsset.Size is < 1 or > 2_147_483_648 ||
            !UpdateSecurity.IsAllowedReleaseAsset(manifestAsset.DownloadUri, release.Tag, manifestAsset.Name) ||
            !UpdateSecurity.IsAllowedReleaseAsset(signatureAsset.DownloadUri, release.Tag, signatureAsset.Name) ||
            !UpdateSecurity.IsAllowedReleaseAsset(packageAsset.DownloadUri, release.Tag, packageAsset.Name)) return null;

        var manifestBytes = await _http.GetByteArrayAsync(manifestAsset.DownloadUri, cancellationToken).ConfigureAwait(false);
        var signatureBytes = await _http.GetByteArrayAsync(signatureAsset.DownloadUri, cancellationToken).ConfigureAwait(false);
        if (manifestBytes.Length != manifestAsset.Size || signatureBytes.Length != signatureAsset.Size) return null;
        var signature = System.Text.Encoding.ASCII.GetString(signatureBytes);
        if (!UpdateSecurity.VerifyManifest(manifestBytes, signature, _verificationKeyPem)) return null;
        var manifest = UpdateSecurity.ParseManifest(manifestBytes);
        if (!SemanticVersion.TryParse(manifest.Version, out var manifestVersion) || manifestVersion is null || manifestVersion.CompareTo(release.Version) != 0 || manifest.PackageSize != packageAsset.Size) return null;
        return new UpdatePackage(release, manifest, manifestBytes, signature.Trim(), packageAsset.DownloadUri);
    }

    public async Task DownloadPackageAsync(UpdatePackage package, string destination, IProgress<(long Received, long? Total)>? progress, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(package.PackageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        if (total is null || total != package.Manifest.PackageSize) throw new InvalidDataException("The update package size does not match its signed manifest.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long received = 0; int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            received += count;
            if (received > package.Manifest.PackageSize) throw new InvalidDataException("The update package exceeds its signed size.");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            progress?.Report((received, total));
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (received != package.Manifest.PackageSize || !actualHash.Equals(package.Manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            output.Close(); File.Delete(destination);
            throw new InvalidDataException("The downloaded update failed signature-backed integrity validation.");
        }
    }

    private static bool GetBool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    private static string GetString(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
