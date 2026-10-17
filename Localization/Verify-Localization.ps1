$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$english = [xml](Get-Content (Join-Path $PSScriptRoot 'UiText.resx') -Raw)
$german = [xml](Get-Content (Join-Path $PSScriptRoot 'UiText.de.resx') -Raw)
$enEntries = @{}; $deEntries = @{}
foreach ($entry in $english.root.data) { if ($enEntries.ContainsKey([string]$entry.name)) { throw "Duplicate English resource key: $($entry.name)" }; $enEntries[[string]$entry.name] = [string]$entry.value }
foreach ($entry in $german.root.data) { if ($deEntries.ContainsKey([string]$entry.name)) { throw "Duplicate German resource key: $($entry.name)" }; $deEntries[[string]$entry.name] = [string]$entry.value }
$differences = @(Compare-Object @($enEntries.Keys | Sort-Object) @($deEntries.Keys | Sort-Object))
if ($differences.Count) { throw "English/German resource keys differ: $($differences.InputObject -join ', ')" }
foreach ($key in $enEntries.Keys) {
    if ([string]::IsNullOrWhiteSpace($enEntries[$key]) -or [string]::IsNullOrWhiteSpace($deEntries[$key])) { throw "Empty localization for $key" }
}
$known = @{}
foreach ($value in $enEntries.Values) { $known[$value] = $true }
foreach ($value in $deEntries.Values) { $known[$value] = $true }
$untranslated = [System.Collections.Generic.List[string]]::new()
$skip = '^(\{|https?://|[A-Za-z0-9_.-]+$|\d+[\d:. %/]*$|[⌄·↑＋×⟳🎙]+$|\*\.\*$)'
Get-ChildItem $root -Recurse -Filter *.xaml -File | Where-Object FullName -NotMatch '\\(bin|obj|publish|staging|artifacts[^\\]*)\\' | ForEach-Object {
    $source = [IO.File]::ReadAllText($_.FullName)
    foreach ($match in [regex]::Matches($source, '(?:Text|Content|Header|ToolTip|Title|Tag|Watermark)="([^"]+)"')) {
        $value = [System.Net.WebUtility]::HtmlDecode($match.Groups[1].Value)
        if ($value -and $value -notmatch $skip -and !$known.ContainsKey($value)) { $untranslated.Add("$($_.FullName.Substring($root.Length + 1)): $value") }
    }
}
if ($untranslated.Count) { throw "Unlocalized static XAML text:`n$($untranslated -join "`n")" }
Write-Output "PASS: $($enEntries.Count) resource keys have non-empty English and German values; scanned source XAML has no uncovered literal UI labels."
