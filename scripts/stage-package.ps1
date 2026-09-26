[CmdletBinding()]
param([Parameter(Mandatory)] [string] $StageDir)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repoRoot 'WARDOGS_VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'WARDOGS_VERSION must be major.minor.patch.' }
$dotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { $dotnet = 'dotnet' }
$stage = [IO.Path]::GetFullPath($StageDir)
if ($stage -eq [IO.Path]::GetPathRoot($stage) -or $stage.Length -lt 10) { throw 'Refusing an unsafe StageDir.' }
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
$restore = Join-Path $repoRoot 'WardogsRadio.sln'
& $dotnet restore $restore -r win-x64
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore for win-x64 failed.' }
$publishRoot = Join-Path ([IO.Path]::GetDirectoryName($stage)) ('publish-' + [guid]::NewGuid().ToString('N'))
try {
    foreach ($item in @(
        @{ Project = 'src\WardogsRadio.App\WardogsRadio.App.csproj'; Name = 'app'; Files = $null },
        @{ Project = 'src\WardogsRadio.Launcher\WardogsRadio.Launcher.csproj'; Name = 'launcher'; Files = @('WARDOGS Radio Launcher.exe','WARDOGS Radio Launcher.dll','WARDOGS Radio Launcher.deps.json','WARDOGS Radio Launcher.runtimeconfig.json','WardogsRadio.Update.dll') },
        @{ Project = 'src\WardogsRadio.UpdateAgent\WardogsRadio.UpdateAgent.csproj'; Name = 'agent'; Files = @('WARDOGS Radio Update Agent.exe','WARDOGS Radio Update Agent.dll','WARDOGS Radio Update Agent.deps.json','WARDOGS Radio Update Agent.runtimeconfig.json','WardogsRadio.Update.dll') })) {
        $output = Join-Path $publishRoot $item.Name
        & $dotnet publish (Join-Path $repoRoot $item.Project) -c Release -r win-x64 --self-contained true --no-restore -o $output
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($item.Project)." }
        if ($null -eq $item.Files) { Get-ChildItem -LiteralPath $output -Force | Copy-Item -Destination $stage -Recurse -Force }
        else { foreach ($file in $item.Files) { Copy-Item -LiteralPath (Join-Path $output $file) -Destination $stage -Force } }
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'WARDOGS_VERSION') -Destination (Join-Path $stage 'VERSION')
    & (Join-Path $PSScriptRoot 'refresh-package-inventory.ps1') -StageDir $stage
    $required = @('WARDOGS Radio.exe', 'WARDOGS Radio Launcher.exe', 'WARDOGS Radio Update Agent.exe', 'VERSION', 'WARDOGS Radio.dll', 'WARDOGS Radio.runtimeconfig.json', 'WardogsRadio.Core.dll', 'WardogsRadio.Update.dll', 'youtube-player.html', 'PACKAGE_CONTENTS.sha256')
    foreach ($file in $required) { if (-not (Test-Path -LiteralPath (Join-Path $stage $file) -PathType Leaf)) { throw "Staged package is missing $file." } }
    if ((Get-Content -LiteralPath (Join-Path $stage 'VERSION') -Raw).Trim() -ne $version) { throw 'Staged VERSION does not match WARDOGS_VERSION.' }
    Write-Host "Staged WARDOGS Radio $version at $stage"
} finally {
    if (Test-Path -LiteralPath $publishRoot) { Remove-Item -LiteralPath $publishRoot -Recurse -Force }
}
