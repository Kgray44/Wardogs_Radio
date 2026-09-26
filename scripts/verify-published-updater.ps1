[CmdletBinding()]
param([Parameter(Mandatory)] [string] $ReleaseTag)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot; $version = (Get-Content (Join-Path $repoRoot 'WARDOGS_VERSION') -Raw).Trim()
if ($ReleaseTag -ne "v$version") { throw 'Release tag does not match WARDOGS_VERSION.' }
$url = 'https://github.com/Kgray44/Wardogs_Radio/releases/latest/download/update-manifest.json'
try { $manifest = Invoke-RestMethod -Uri $url -TimeoutSec 15 } catch { throw "Published manifest fetch failed: $($_.Exception.Message)" }
if ($manifest.schema -ne 1 -or $manifest.channel -ne 'stable' -or $manifest.version -ne $version -or $manifest.tag -ne $ReleaseTag) { throw 'Published manifest does not describe the expected stable release.' }
$expectedName = "WARDOGS-Radio-Setup-v$version.exe"; $expectedUrl = "https://github.com/Kgray44/Wardogs_Radio/releases/download/$ReleaseTag/$expectedName"
if ($manifest.installer -ne $expectedName -or $manifest.installer_url -ne $expectedUrl -or $manifest.sha256 -notmatch '^[a-f0-9]{64}$' -or $manifest.release_notes_url -ne "https://github.com/Kgray44/Wardogs_Radio/releases/tag/$ReleaseTag") { throw 'Published manifest has unsafe installer metadata.' }
$temp = Join-Path $env:TEMP $expectedName
try {
    $releaseRoot = "https://github.com/Kgray44/Wardogs_Radio/releases/download/$ReleaseTag"
    $manifestFile = Join-Path $env:TEMP 'wardogs-update-manifest.json'
    Invoke-WebRequest -Uri "$releaseRoot/update-manifest.json" -OutFile $manifestFile -TimeoutSec 30
    $sumResponse = Invoke-WebRequest -Uri "$releaseRoot/SHA256SUMS.txt" -TimeoutSec 30
    $sums = if ($sumResponse.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($sumResponse.Content) } else { [string]$sumResponse.Content }
    $manifestHash = (Get-FileHash -LiteralPath $manifestFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sums -notmatch "(?m)^$manifestHash \*update-manifest\.json$") { throw 'Published SHA256SUMS does not match the manifest.' }
    Invoke-WebRequest -Uri $manifest.installer_url -OutFile $temp -TimeoutSec 120
    if ((Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.sha256 -or $sums -notmatch "(?m)^$($manifest.sha256) \*$([regex]::Escape($expectedName))$") { throw 'Published installer SHA-256 does not match manifest and sums.' }
}
finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
Write-Host "Published updater manifest and installer verified for $ReleaseTag."
