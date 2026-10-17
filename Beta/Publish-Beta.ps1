param(
    [Parameter(Mandatory = $true)][string]$SupabaseUrl,
    [Parameter(Mandatory = $true)][string]$SupabasePublishableKey,
    [string]$OutputRoot = (Join-Path $PSScriptRoot "artifacts")
)
$ErrorActionPreference = "Stop"

$uri = [Uri]$SupabaseUrl
if ($uri.Scheme -ne "https" -and -not ($uri.Scheme -eq "http" -and $uri.IsLoopback)) {
    throw "Use an HTTPS Supabase project URL (HTTP is allowed only for localhost development)."
}
if ([string]::IsNullOrWhiteSpace($SupabasePublishableKey)) { throw "A public Supabase publishable/anon key is required." }

$repo = Split-Path -Parent $PSScriptRoot
$jarvisOut = Join-Path $OutputRoot "Jarvis-Beta"
$managerOut = Join-Path $OutputRoot "Jarvis-Beta-Manager"
New-Item -ItemType Directory -Force -Path $jarvisOut, $managerOut | Out-Null

dotnet publish (Join-Path $repo "Jarvis.csproj") -c Beta -r win-x64 --self-contained true -p:JarvisSupabaseUrl=$SupabaseUrl -p:JarvisSupabasePublishableKey=$SupabasePublishableKey -p:PublishSingleFile=false -o $jarvisOut
if ($LASTEXITCODE -ne 0) { throw "Jarvis beta publish failed with exit code $LASTEXITCODE" }

dotnet publish (Join-Path $repo "BetaManager\Jarvis.BetaManager.csproj") -c Release -r win-x64 --self-contained true -p:JarvisSupabaseUrl=$SupabaseUrl -p:JarvisSupabasePublishableKey=$SupabasePublishableKey -p:PublishSingleFile=false -o $managerOut
if ($LASTEXITCODE -ne 0) { throw "Beta Manager publish failed with exit code $LASTEXITCODE" }

Write-Host "Jarvis beta:       $jarvisOut\Jarvis.exe"
Write-Host "Beta Manager:      $managerOut\Jarvis.BetaManager.exe"
Write-Host "These are beta artifacts. Do not replace the public release until backend and clean-install acceptance tests pass."
