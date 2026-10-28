[CmdletBinding()]
param(
    [string]$Configuration = 'Beta',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$key = [Environment]::GetEnvironmentVariable('JARVIS_UPDATE_SIGNING_PRIVATE_KEY_PEM')
if ([string]::IsNullOrWhiteSpace($key)) { throw 'JARVIS_UPDATE_SIGNING_PRIVATE_KEY_PEM is required to create an authenticated update package.' }
$key = $key.Trim()
# GitHub secrets are often stored as one line with escaped newlines, or as a
# base64-encoded PEM value. Accept both representations without ever writing the
# private key to disk or to the build log.
if ($key.Contains('\n', [StringComparison]::Ordinal)) {
    $key = $key.Replace('\r', "`r", [StringComparison]::Ordinal).Replace('\n', "`n", [StringComparison]::Ordinal)
}
if (-not $key.Contains('-----BEGIN', [StringComparison]::Ordinal)) {
    try {
        $decodedKey = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($key))
        if ($decodedKey.Contains('-----BEGIN', [StringComparison]::Ordinal)) { $key = $decodedKey.Trim() }
    } catch [FormatException] { }
}
if (-not $key.Contains('-----BEGIN', [StringComparison]::Ordinal)) {
    throw 'JARVIS_UPDATE_SIGNING_PRIVATE_KEY_PEM must contain PEM text, escaped-newline PEM text, or base64-encoded PEM text.'
}
$supabaseUrl = $env:JARVIS_SUPABASE_URL
$supabasePublishableKey = $env:JARVIS_SUPABASE_PUBLISHABLE_KEY
if ($Configuration -eq 'Beta') {
    if ([string]::IsNullOrWhiteSpace($supabaseUrl)) { $supabaseUrl = 'https://rlcgexffgmdukpkououa.supabase.co' }
    if ([string]::IsNullOrWhiteSpace($supabasePublishableKey)) { $supabasePublishableKey = (& dotnet msbuild (Join-Path $root 'Jarvis.csproj') -getProperty:JarvisSupabasePublishableKey -p:Configuration=Beta).Trim() }
    if ([string]::IsNullOrWhiteSpace($supabasePublishableKey)) { throw 'A public Supabase publishable key is required for the Beta update package.' }
    if ($supabasePublishableKey.StartsWith('sb_secret_', [StringComparison]::Ordinal)) { throw 'A Supabase secret key cannot be embedded in the Jarvis updater package.' }
}
if ($Configuration -ne 'Beta') { throw 'The distributable update package must contain the mandatory Closed Beta activation build. Use -Configuration Beta.' }
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = (& dotnet msbuild (Join-Path $root 'Jarvis.csproj') -getProperty:Version).Trim() }
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?$') { throw "Invalid semantic version: $Version" }
$versionCore = ($Version -split '-',2)[0]
$stage = Join-Path $PSScriptRoot 'staging\UpdatePackage'
$helper = Join-Path $PSScriptRoot 'staging\Updater'
$artifact = Join-Path $OutputDirectory 'Jarvis-Update-win-x64.zip'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
foreach ($dir in @($stage,$helper)) { if (Test-Path $dir) { Remove-Item -LiteralPath $dir -Recurse -Force } }
$publishArguments = @('-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true', '-o', $stage, '-p:PublishSingleFile=false', "-p:Version=$Version", "-p:AssemblyVersion=$versionCore.0", "-p:FileVersion=$versionCore.0", "-p:InformationalVersion=$Version", '-p:NuGetAudit=false')
if ($Configuration -eq 'Beta') {
    $publishArguments += "-p:JarvisSupabaseUrl=$supabaseUrl"
    $publishArguments += "-p:JarvisSupabasePublishableKey=$supabasePublishableKey"
}
& dotnet publish (Join-Path $root 'Jarvis.csproj') @publishArguments
if ($LASTEXITCODE -ne 0) { throw 'Jarvis update payload publish failed.' }
if (-not (Test-Path (Join-Path $stage 'Jarvis.dll'))) { throw 'Jarvis.dll is missing from the Beta update payload.' }
$assemblyText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes((Join-Path $stage 'Jarvis.dll')))
foreach ($marker in @('BetaActivationWindow', 'BetaSupabaseUrl', 'BetaSupabasePublishableKey', $supabaseUrl, $supabasePublishableKey)) {
    if ($assemblyText.IndexOf($marker, [StringComparison]::Ordinal) -lt 0) { throw "Beta activation/configuration verification failed for marker '$($marker -replace '^sb_publishable_.*$', 'publishable-key')'. Refusing to sign an ungated update payload." }
}
& dotnet publish (Join-Path $root 'Jarvis.Updater\Jarvis.Updater.csproj') -c Release -r win-x64 --self-contained true -o $helper -p:PublishSingleFile=true -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Jarvis updater helper publish failed.' }
Copy-Item (Join-Path $helper 'Jarvis.Updater.exe') (Join-Path $stage 'Jarvis.Updater.exe') -Force
if (Test-Path $artifact) { Remove-Item -LiteralPath $artifact -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $artifact, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$packageInfo = Get-Item $artifact
$sha = (Get-FileHash $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
$manifestPath = Join-Path $OutputDirectory 'Jarvis-Update-win-x64.json'
$signaturePath = Join-Path $OutputDirectory 'Jarvis-Update-win-x64.sig'
$manifest = [ordered]@{ formatVersion=1; version=$Version; packageName='Jarvis-Update-win-x64.zip'; packageSize=$packageInfo.Length; sha256=$sha }
$manifestBytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Compress))
[IO.File]::WriteAllBytes($manifestPath,$manifestBytes)
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem($key)
    $signature = $rsa.SignData($manifestBytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
    [IO.File]::WriteAllBytes($signaturePath,[Text.Encoding]::ASCII.GetBytes([Convert]::ToBase64String($signature)))
} finally { $rsa.Dispose(); $key=$null }
Write-Host "Authenticated update package created for Jarvis $Version ($([math]::Round($packageInfo.Length / 1MB,1)) MB)."
