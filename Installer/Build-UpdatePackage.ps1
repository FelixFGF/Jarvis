[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$key = [Environment]::GetEnvironmentVariable('JARVIS_UPDATE_SIGNING_PRIVATE_KEY_PEM')
if ([string]::IsNullOrWhiteSpace($key)) { throw 'JARVIS_UPDATE_SIGNING_PRIVATE_KEY_PEM is required to create an authenticated update package.' }
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = (& dotnet msbuild (Join-Path $root 'Jarvis.csproj') -getProperty:Version).Trim() }
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?$') { throw "Invalid semantic version: $Version" }
$versionCore = ($Version -split '-',2)[0]
$stage = Join-Path $PSScriptRoot 'staging\UpdatePackage'
$helper = Join-Path $PSScriptRoot 'staging\Updater'
$artifact = Join-Path $OutputDirectory 'Jarvis-Update-win-x64.zip'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
foreach ($dir in @($stage,$helper)) { if (Test-Path $dir) { Remove-Item -LiteralPath $dir -Recurse -Force } }
& dotnet publish (Join-Path $root 'Jarvis.csproj') -c $Configuration -r win-x64 --self-contained true -o $stage -p:PublishSingleFile=false -p:Version=$Version "-p:AssemblyVersion=$versionCore.0" "-p:FileVersion=$versionCore.0" -p:InformationalVersion=$Version -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Jarvis update payload publish failed.' }
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
