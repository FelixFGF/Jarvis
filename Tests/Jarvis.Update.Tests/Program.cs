using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http;
using Jarvis.Update.Core;

static void Assert(bool condition, string label) { if (!condition) throw new InvalidOperationException("FAILED: " + label); Console.WriteLine("PASS: " + label); }
static SemanticVersion Parse(string value) => SemanticVersion.TryParse(value, out var parsed) ? parsed! : throw new InvalidOperationException("Bad test semver");
static async Task ThrowsAsync<T>(Func<Task> action, string label) where T : Exception { try { await action(); } catch (T) { Console.WriteLine("PASS: " + label); return; } throw new InvalidOperationException("FAILED: " + label); }
var rc = Parse("v1.2.3-rc.2"); var stable = Parse("1.2.3"); var newer = Parse("1.10.0"); var older = Parse("1.9.99");
Assert(stable.CompareTo(rc) > 0, "SemVer stable outranks prerelease");
Assert(newer.CompareTo(older) > 0, "SemVer compares numeric components");
var installedOld = Parse("1.2.2");
var releases = new[] {
    new GitHubRelease(stable!, "v1.2.3", "", "", new Uri("https://github.com/FelixFGF/Jarvis/releases/tag/v1.2.3"), false, false, []),
    new GitHubRelease(rc!, "v1.2.3-rc.2", "", "", new Uri("https://github.com/FelixFGF/Jarvis/releases/tag/v1.2.3-rc.2"), true, false, []),
    new GitHubRelease(Parse("1.3.0-beta.1"), "v1.3.0-beta.1", "", "", new Uri("https://github.com/FelixFGF/Jarvis/releases/tag/v1.3.0-beta.1"), true, false, []),
    new GitHubRelease(newer!, "v1.10.0", "", "", new Uri("https://github.com/FelixFGF/Jarvis/releases/tag/v1.10.0"), false, true, [])
};
Assert(UpdateSecurity.SelectEligible(releases, installedOld, "stable")?.Version.CompareTo(stable) == 0, "stable channel ignores prerelease and draft");
Assert(UpdateSecurity.SelectEligible(releases, stable, "beta")?.Version.CompareTo(Parse("1.3.0-beta.1")) == 0, "beta channel includes newer prerelease but ignores draft");
Assert(UpdateSecurity.SelectEligible(releases, stable, "stable") is null, "equal installed version does not offer update");
Assert(UpdateSecurity.IsAllowedReleaseAsset(new Uri("https://github.com/FelixFGF/Jarvis/releases/download/v1.2.3/Jarvis-Update-win-x64.zip"), "v1.2.3", UpdateSecurity.PackageName), "expected HTTPS release asset accepted");
Assert(!UpdateSecurity.IsAllowedReleaseAsset(new Uri("https://evil.example/file.zip"), "v1.2.3", UpdateSecurity.PackageName), "foreign package host rejected");
Assert(UpdateSecurity.IsSafeArchivePath("runtimes/win-x64/app.dll") && !UpdateSecurity.IsSafeArchivePath("../escape") && !UpdateSecurity.IsSafeArchivePath("C:/escape"), "archive traversal paths rejected");
using var rsa = RSA.Create(2048);
var manifestBytes = Encoding.UTF8.GetBytes("{\"formatVersion\":1,\"version\":\"1.2.3\",\"packageName\":\"Jarvis-Update-win-x64.zip\",\"packageSize\":42,\"sha256\":\"" + new string('a',64) + "\"}");
var signature = Convert.ToBase64String(rsa.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
Assert(UpdateSecurity.VerifyManifest(manifestBytes, signature, rsa.ExportSubjectPublicKeyInfoPem()), "valid detached manifest signature accepted");
Assert(!UpdateSecurity.VerifyManifest(manifestBytes, signature + "x", rsa.ExportSubjectPublicKeyInfoPem()), "invalid manifest signature rejected");
Assert(UpdateSecurity.ParseManifest(manifestBytes).PackageSize == 42, "signed manifest schema parsed");
var missingAssetsRelease = new GitHubRelease(stable,"v1.2.3","","",new Uri("https://github.com/FelixFGF/Jarvis/releases/tag/v1.2.3"),false,false,[]);
using (var missingClient = new GitHubUpdateClient(new HttpClient(new FakeHandler(_ => throw new Exception("Unexpected HTTP request")))))
    Assert(await missingClient.GetSignedPackageAsync(missingAssetsRelease) is null, "missing signed release assets are rejected without download");
using (var apiFailure = new GitHubUpdateClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)))))
    await ThrowsAsync<HttpRequestException>(() => apiFailure.GetReleasesAsync(), "GitHub rate-limit/API error surfaced as failure");
using (var malformed = new GitHubUpdateClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not-json") }))))
    await ThrowsAsync<System.Text.Json.JsonException>(() => malformed.GetReleasesAsync(), "malformed GitHub response rejected");
using (var timeout = new GitHubUpdateClient(new HttpClient(new FakeHandler(_ => throw new TaskCanceledException("timeout")))))
    await ThrowsAsync<TaskCanceledException>(() => timeout.GetReleasesAsync(), "GitHub timeout propagated for graceful caller handling");
var txRoot = Path.Combine(Path.GetTempPath(), "JarvisUpdaterTest", Guid.NewGuid().ToString("N"));
try
{
    var source = Path.Combine(txRoot,"source"); var install = Path.Combine(txRoot,"install"); var backup = Path.Combine(txRoot,"backup");
    Directory.CreateDirectory(source); Directory.CreateDirectory(install);
    File.WriteAllText(Path.Combine(source,"a.txt"),"new"); File.WriteAllText(Path.Combine(source,"b"),"blocked");
    File.WriteAllText(Path.Combine(install,"a.txt"),"old"); Directory.CreateDirectory(Path.Combine(install,"b"));
    Directory.CreateDirectory(Path.Combine(source,"JarvisData")); File.WriteAllText(Path.Combine(source,"JarvisData","model.bin"),"must-not-update");
    try { UpdateFileTransaction.Apply(source,install,backup); throw new InvalidOperationException("Expected a copy collision."); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    Assert(File.ReadAllText(Path.Combine(install,"a.txt")) == "old", "failed update restores overwritten files");
    Assert(Directory.Exists(Path.Combine(install,"b")), "failed update preserves conflicting destination");
    Assert(!File.Exists(Path.Combine(install,"JarvisData","model.bin")), "user model data is excluded from updates");
}
finally { try { Directory.Delete(txRoot,true); } catch { } }
if (args.Length == 3)
{
    var shippedManifest = await File.ReadAllBytesAsync(args[0]);
    var shippedSignature = await File.ReadAllTextAsync(args[1]);
    var parsed = UpdateSecurity.ParseManifest(shippedManifest);
    Assert(UpdateSecurity.VerifyManifest(shippedManifest, shippedSignature), "release package manifest verifies with the pinned production public key");
    var archive = new FileInfo(args[2]);
    Assert(archive.Length == parsed.PackageSize, "release archive size matches signed manifest");
    await using var archiveStream = File.OpenRead(args[2]);
    var archiveHash = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream));
    Assert(archiveHash.Equals(parsed.Sha256, StringComparison.OrdinalIgnoreCase), "release archive hash matches signed manifest");
    using var zip = System.IO.Compression.ZipFile.OpenRead(args[2]);
    Assert(zip.Entries.Any(entry => entry.FullName == "Jarvis.exe") && zip.Entries.Any(entry => entry.FullName == "Jarvis.Updater.exe"), "release archive contains app and updater helper");
    Assert(zip.Entries.All(entry => UpdateSecurity.IsSafeArchivePath(entry.FullName.TrimEnd('/'))), "release archive contains no unsafe paths");
}
Console.WriteLine("All updater checks passed.");

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
}
