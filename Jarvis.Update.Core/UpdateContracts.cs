using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Update.Core;

public sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private static readonly Regex Pattern = new("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z.-]+))?(?:\\+([0-9A-Za-z.-]+))?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private SemanticVersion(BigInteger major, BigInteger minor, BigInteger patch, string? prerelease, string? build) { Major = major; Minor = minor; Patch = patch; Prerelease = prerelease; Build = build; }
    public BigInteger Major { get; }
    public BigInteger Minor { get; }
    public BigInteger Patch { get; }
    public string? Prerelease { get; }
    public string? Build { get; }
    public static bool TryParse(string? value, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim(); if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var match = Pattern.Match(value); if (!match.Success || !BigInteger.TryParse(match.Groups[1].Value, out var major) || !BigInteger.TryParse(match.Groups[2].Value, out var minor) || !BigInteger.TryParse(match.Groups[3].Value, out var patch)) return false;
        var pre = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (pre?.Split('.').Any(x => x.Length == 0 || (x.Length > 1 && x[0] == '0' && x.All(char.IsDigit))) == true) return false;
        version = new(major, minor, patch, pre, match.Groups[5].Success ? match.Groups[5].Value : null); return true;
    }
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var comparison = Major.CompareTo(other.Major); if (comparison != 0) return comparison;
        comparison = Minor.CompareTo(other.Minor); if (comparison != 0) return comparison;
        comparison = Patch.CompareTo(other.Patch); if (comparison != 0) return comparison;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        var left = Prerelease.Split('.'); var right = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = BigInteger.TryParse(left[i], out var ln); var rightNumeric = BigInteger.TryParse(right[i], out var rn);
            comparison = leftNumeric && rightNumeric ? ln.CompareTo(rn) : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : string.CompareOrdinal(left[i], right[i]);
            if (comparison != 0) return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }
    public override string ToString() => $"{Major}.{Minor}.{Patch}{(Prerelease is null ? "" : "-" + Prerelease)}{(Build is null ? "" : "+" + Build)}";
}

public sealed record ReleaseAsset(string Name, Uri DownloadUri, long Size);
public sealed record GitHubRelease(SemanticVersion Version, string Tag, string Name, string Notes, Uri HtmlUri, bool Prerelease, bool Draft, IReadOnlyList<ReleaseAsset> Assets);
public sealed record SignedUpdateManifest(int FormatVersion, string Version, string PackageName, long PackageSize, string Sha256);

public static class UpdateSecurity
{
    public const string RepositoryOwner = "FelixFGF";
    public const string RepositoryName = "Jarvis";
    public const string PackageName = "Jarvis-Update-win-x64.zip";
    public const string ManifestName = "Jarvis-Update-win-x64.json";
    public const string SignatureName = "Jarvis-Update-win-x64.sig";
    public const string PinnedPublicKeyPem = "-----BEGIN PUBLIC KEY-----\nMIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAzEp1Oq9zDX8cZrHftRLd\nMzzIvpFTNHHT2jc2GlxSDXqrcGVSyS0jY5T0uAWzSDY4K0IF4q9YMQvPium0y5sE\n1YGNA9ZfgQeLNm4C5ZSeBPZGqCWS68qsJJ3/cOqACoXzgOFhIh2R4W2ojH/bYfIK\nUBUHoU3j7IT45RouTaJmiBmZoSCCjHL6p4RdfCmsgcAMoBgyBSrkbOPXopgnB33c\nXrW9dYDdMEbg+IeV/lp2FiCcSFNzS5v+ubMBuYzofxXPR9CEqBbxJESGUj9/Gem6\nEjmQwxZ4hmcl8UUZxyWlitJKfGaZNRw+4hPU2XLu5FFsu2NKCw757iGWbAMYS9q/\naUTzo5DA5ObNXug+xnGr9VxjMqnkAcrGGvV7lTb1bB4lFRbpprAV/4IgJ4euBCbM\nzrfwJwfU9SjbHAQU2kvcZvgHFVQBSWVkGQFbhteJV5uTHrNAgPNzGGj4NuSboKbR\nU73xRsXeikgSxQuHb4dkRrjYhJaNF5kHQcLJ8OI4CORlAgMBAAE=\n-----END PUBLIC KEY-----\n";
    public static bool IsAllowedReleaseAsset(Uri uri, string tag, string assetName)
    {
        var expectedPath = $"/{RepositoryOwner}/{RepositoryName}/releases/download/{Uri.EscapeDataString(tag)}/{assetName}";
        return uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal);
    }
    public static GitHubRelease? SelectEligible(IEnumerable<GitHubRelease> releases, SemanticVersion installed, string channel)
    {
        var includePrerelease = channel.Equals("beta", StringComparison.OrdinalIgnoreCase);
        return releases.Where(r => !r.Draft && (includePrerelease || !r.Prerelease) && r.Version.CompareTo(installed) > 0)
            .OrderByDescending(r => r.Version).FirstOrDefault();
    }
    public static bool VerifyManifest(ReadOnlySpan<byte> manifest, string signatureBase64, string? publicKeyPem = null)
    {
        try { using var rsa = System.Security.Cryptography.RSA.Create(); rsa.ImportFromPem(publicKeyPem ?? PinnedPublicKeyPem); return rsa.VerifyData(manifest, Convert.FromBase64String(signatureBase64.Trim()), System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pss); }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException) { return false; }
    }
    public static SignedUpdateManifest ParseManifest(ReadOnlySpan<byte> data)
    {
        var manifest = JsonSerializer.Deserialize<SignedUpdateManifest>(data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Update manifest is empty.");
        if (manifest.FormatVersion != 1 || manifest.PackageName != PackageName || manifest.PackageSize < 1 || !Regex.IsMatch(manifest.Sha256, "^[A-Fa-f0-9]{64}$") || !SemanticVersion.TryParse(manifest.Version, out _))
            throw new InvalidDataException("Update manifest fields are invalid.");
        return manifest;
    }

    public static bool IsSafeArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/') || path.Contains(':')) return false;
        var parts = path.Split('/');
        return parts.All(p => p.Length > 0 && p is not "." and not "..");
    }
}
