[CmdletBinding()]
param([Parameter(Mandatory)] [string] $InstallerPath, [Parameter(Mandatory)] [string] $OutputDir, [Parameter(Mandatory)] [string] $ReleaseTag)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repoRoot 'WARDOGS_VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $ReleaseTag -ne "v$version") { throw 'Release tag must match WARDOGS_VERSION.' }
$installer = Get-Item -LiteralPath $InstallerPath
if ($installer.Name -ne "WARDOGS-Radio-Setup-v$version.exe") { throw 'Installer filename does not match WARDOGS_VERSION.' }
$output = [IO.Path]::GetFullPath($OutputDir)
if ($output -eq [IO.Path]::GetPathRoot($output) -or $output.Length -lt 10) { throw 'Refusing an unsafe OutputDir.' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$installerHash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{ schema = 1; channel = 'stable'; version = $version; tag = $ReleaseTag; installer = $installer.Name; installer_url = "https://github.com/Kgray44/Wardogs_Radio/releases/download/$ReleaseTag/$($installer.Name)"; sha256 = $installerHash; minimum_launcher_version = '0.1.0'; published_utc = [DateTime]::UtcNow.ToString('o'); release_notes_url = "https://github.com/Kgray44/Wardogs_Radio/releases/tag/$ReleaseTag" }
$encoding = [Text.UTF8Encoding]::new($false)
$manifestPath = Join-Path $output 'update-manifest.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), $encoding)
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$installerHash *$($installer.Name)`n$manifestHash *update-manifest.json`n", $encoding)
Write-Host "Generated manifest and checksums for $ReleaseTag"
