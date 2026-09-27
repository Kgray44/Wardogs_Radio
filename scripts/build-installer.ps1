[CmdletBinding()]
param([Parameter(Mandatory)] [string] $StageDir, [Parameter(Mandatory)] [string] $OutputDir, [Parameter(Mandatory)] [string] $Iscc)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repoRoot 'WARDOGS_VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'WARDOGS_VERSION must be major.minor.patch.' }
$source = (Resolve-Path -LiteralPath $StageDir).Path
$compiler = (Resolve-Path -LiteralPath $Iscc).Path
$output = [IO.Path]::GetFullPath($OutputDir)
if ($output -eq [IO.Path]::GetPathRoot($output) -or $output.Length -lt 10) { throw 'Refusing an unsafe OutputDir.' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
foreach ($file in @('WARDOGS Radio.exe', 'WARDOGS Radio Launcher.exe', 'WARDOGS Radio Update Agent.exe', 'VERSION', 'mpv.exe', 'mpv.com', 'd3dcompiler_43.dll', 'THIRD_PARTY_MPV.txt', 'PACKAGE_CONTENTS.sha256')) { if (-not (Test-Path -LiteralPath (Join-Path $source $file) -PathType Leaf)) { throw "Incomplete stage: $file is missing." } }
if ((Get-Content -LiteralPath (Join-Path $source 'VERSION') -Raw).Trim() -ne $version) { throw 'Stage VERSION does not match WARDOGS_VERSION.' }
$icon = (Resolve-Path -LiteralPath (Join-Path $repoRoot 'assets\icons\WARDOGS-Radio.ico')).Path
& $compiler "/DMyAppVersion=$version" "/DSourceDir=$source" "/DOutputDir=$output" "/DIconFile=$icon" (Join-Path $repoRoot 'packaging\WARDOGS-Radio.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }
$installer = Join-Path $output "WARDOGS-Radio-Setup-v$version.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Installer was not created: $installer" }
Write-Host "Built $installer"
