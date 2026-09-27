[CmdletBinding()]
param([Parameter(Mandatory)] [string] $InstallerPath, [Parameter(Mandatory)] [string] $InstallDir)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot; $version = (Get-Content (Join-Path $repoRoot 'WARDOGS_VERSION') -Raw).Trim()
$target = [IO.Path]::GetFullPath($InstallDir)
if ($target -eq [IO.Path]::GetPathRoot($target) -or $target.Length -lt 10) { throw 'Refusing an unsafe InstallDir.' }
$result = Start-Process -FilePath $InstallerPath -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',"/DIR=`"$target`"") -Wait -PassThru
if ($result.ExitCode -ne 0) { throw "Installer failed with $($result.ExitCode)." }
foreach ($file in @('WARDOGS Radio.exe','WARDOGS Radio Launcher.exe','WARDOGS Radio Update Agent.exe','VERSION','PACKAGE_CONTENTS.sha256','WARDOGS Radio.dll','WardogsRadio.Core.dll','youtube-player.html','mpv.exe','mpv.com','d3dcompiler_43.dll','THIRD_PARTY_MPV.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $target $file) -PathType Leaf)) { throw "Installed package verification failed: $file is missing." }
}
if ((Get-Content -LiteralPath (Join-Path $target 'VERSION') -Raw).Trim() -ne $version) { throw 'Installed VERSION does not match WARDOGS_VERSION.' }
$health = Start-Process -FilePath (Join-Path $target 'WARDOGS Radio.exe') -ArgumentList '--startup-health-check' -WorkingDirectory $target -Wait -PassThru
if ($health.ExitCode -ne 0) { throw 'Installed application startup health check failed.' }
Write-Host 'Installer smoke test passed.'
