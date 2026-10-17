[CmdletBinding()]
param(
    [string]$Configuration = 'Beta',
    [string]$OutputDirectory = '',
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$appProject = Join-Path $projectRoot 'Jarvis.csproj'
$setupProject = Join-Path $PSScriptRoot 'Setup\Jarvis.Setup.csproj'
$staging = Join-Path $PSScriptRoot 'staging'
$OutputDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $PSScriptRoot 'artifacts' } else { $OutputDirectory }
$appPublish = Join-Path $staging 'Jarvis'
$updaterPublish = Join-Path $staging 'Jarvis-Updater'
$payload = Join-Path $staging 'Jarvis-payload.zip'
$setupPublish = Join-Path $staging ('Setup-' + [Guid]::NewGuid().ToString('N'))
$setupOutput = Join-Path $OutputDirectory 'Jarvis-Setup.exe'
$supabaseUrl = $env:JARVIS_SUPABASE_URL
$supabasePublishableKey = $env:JARVIS_SUPABASE_PUBLISHABLE_KEY
if ($Configuration -eq 'Beta') {
    if ([string]::IsNullOrWhiteSpace($supabaseUrl)) { $supabaseUrl = 'https://rlcgexffgmdukpkououa.supabase.co' }
    if ([string]::IsNullOrWhiteSpace($supabasePublishableKey)) { $supabasePublishableKey = (& dotnet msbuild $appProject -getProperty:JarvisSupabasePublishableKey -p:Configuration=Beta).Trim() }
    if ([string]::IsNullOrWhiteSpace($supabasePublishableKey)) { throw 'A public Supabase publishable key is required for the Beta installer.' }
    if ($supabasePublishableKey.StartsWith('sb_secret_', [StringComparison]::Ordinal)) { throw 'A Supabase secret key cannot be embedded in the Jarvis installer.' }
}
if ($Configuration -ne 'Beta') { throw 'The distributable Jarvis-Setup.exe must contain the mandatory Closed Beta activation build. Use -Configuration Beta.' }
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = (& dotnet msbuild $appProject -getProperty:Version).Trim() }
$versionCore = ($Version -split '[-+]',2)[0]

New-Item -ItemType Directory -Force -Path $staging, $OutputDirectory | Out-Null
if (Test-Path $appPublish) { Remove-Item -LiteralPath $appPublish -Recurse -Force }
if (Test-Path $payload) { Remove-Item -LiteralPath $payload -Force }

Write-Host 'Publishing Jarvis self-contained for Windows x64…'
$publishArguments = @('--configuration', $Configuration, '--runtime', 'win-x64', '--self-contained', 'true', '--output', $appPublish, '-p:PublishSingleFile=false', '-p:NuGetAudit=false', "-p:Version=$Version", "-p:AssemblyVersion=$versionCore.0", "-p:FileVersion=$versionCore.0", "-p:InformationalVersion=$Version")
if ($Configuration -eq 'Beta') {
    $publishArguments += "-p:JarvisSupabaseUrl=$supabaseUrl"
    $publishArguments += "-p:JarvisSupabasePublishableKey=$supabasePublishableKey"
}
& dotnet publish $appProject @publishArguments
if ($LASTEXITCODE -ne 0) { throw "Jarvis publish failed (exit $LASTEXITCODE)." }
if (-not (Test-Path (Join-Path $appPublish 'Jarvis.exe'))) { throw 'Jarvis.exe is missing from the publish output.' }
$jarvisAssembly = Join-Path $appPublish 'Jarvis.dll'
if (-not (Test-Path $jarvisAssembly)) { throw 'Jarvis.dll is missing from the Beta publish output.' }
$assemblyText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($jarvisAssembly))
foreach ($marker in @('BetaActivationWindow', 'BetaSupabaseUrl', 'BetaSupabasePublishableKey', $supabaseUrl, $supabasePublishableKey)) {
    if (-not $assemblyText.Contains($marker, [StringComparison]::Ordinal)) { throw "Beta activation/configuration verification failed for marker '$($marker -replace '^sb_publishable_.*$', 'publishable-key')'. Refusing to package an ungated application." }
}
Write-Host 'Verified Beta activation gate and client configuration in the application assembly.'

Write-Host 'Publishing the self-contained update helper…'
if (Test-Path $updaterPublish) { Remove-Item -LiteralPath $updaterPublish -Recurse -Force }
& dotnet publish (Join-Path $projectRoot 'Jarvis.Updater\Jarvis.Updater.csproj') --configuration Release --runtime win-x64 --self-contained true --output $updaterPublish -p:PublishSingleFile=true -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "Update helper publish failed (exit $LASTEXITCODE)." }
Copy-Item -LiteralPath (Join-Path $updaterPublish 'Jarvis.Updater.exe') -Destination (Join-Path $appPublish 'Jarvis.Updater.exe') -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($appPublish, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$appBytes = (Get-ChildItem -LiteralPath $appPublish -File -Recurse | Measure-Object -Property Length -Sum).Sum
Write-Host ("Application payload: {0:N1} MB unpacked, {1:N1} MB compressed." -f ($appBytes / 1MB), ((Get-Item $payload).Length / 1MB))

Write-Host 'Publishing the custom, self-contained WPF setup…'
& dotnet publish $setupProject --configuration $Configuration --runtime win-x64 --self-contained true --output $setupPublish `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:NuGetAudit=false "-p:PayloadZip=$payload" "-p:Version=$Version" "-p:AssemblyVersion=$versionCore.0" "-p:FileVersion=$versionCore.0" "-p:InformationalVersion=$Version"
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed (exit $LASTEXITCODE)." }
$builtSetup = Join-Path $setupPublish 'Jarvis.Setup.exe'
if (-not (Test-Path $builtSetup)) { throw 'The installer executable was not created.' }
Copy-Item -LiteralPath $builtSetup -Destination $setupOutput -Force
$hash = (Get-FileHash -LiteralPath $setupOutput -Algorithm SHA256).Hash
$hashFile = "$setupOutput.sha256"
Set-Content -LiteralPath $hashFile -Value "$hash  Jarvis-Setup.exe" -Encoding ascii
Write-Host "Installer ready: $setupOutput"
Write-Host ("Size: {0:N1} MB" -f ((Get-Item $setupOutput).Length / 1MB))
Write-Host "SHA-256: $hash"

