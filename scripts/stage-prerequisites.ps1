[CmdletBinding()]
param([Parameter(Mandatory)] [string] $StageDir)

$ErrorActionPreference = 'Stop'
$stage = [IO.Path]::GetFullPath($StageDir)
if (-not (Test-Path -LiteralPath $stage -PathType Container)) { throw 'StageDir must be an existing directory.' }
$destination = Join-Path $stage 'Prerequisites'
New-Item -ItemType Directory -Force -Path $destination | Out-Null

$packages = @(
    [pscustomobject]@{
        Name = 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
        Url = 'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/06fb6ad8-1976-4e78-9ceb-3ae170edebde/MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
        Sha256 = '771042db15cb5c463bac51a8408e70183d7130e8ac946709384c2223da582c1b'
        Cache = 'WARDOGS-WebView2RuntimeInstallerX64-20260927.exe'
    },
    [pscustomobject]@{
        Name = 'VoicemeeterBananaSetup.exe'
        Url = 'https://download.vb-audio.com/Download_CABLE/VoicemeeterSetup_v2122.zip'
        Sha256 = 'fda1c82522b4a8c87a89c5c50a56e7e25e3519b9e1f1a8e63475b8590ff09ee5'
        Cache = 'WARDOGS-VoicemeeterSetup_v2122.zip'
    }
)

function Get-VerifiedPackage([pscustomobject] $package) {
    $cache = Join-Path ([IO.Path]::GetTempPath()) $package.Cache
    if (-not (Test-Path -LiteralPath $cache -PathType Leaf)) { Invoke-WebRequest -Uri $package.Url -OutFile $cache }
    $hash = (Get-FileHash -LiteralPath $cache -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $package.Sha256) { throw "Prerequisite archive hash mismatch for $($package.Name): $hash" }
    return $cache
}

$webView = Get-VerifiedPackage $packages[0]
Copy-Item -LiteralPath $webView -Destination (Join-Path $destination $packages[0].Name) -Force

$bananaZip = Get-VerifiedPackage $packages[1]
$extract = Join-Path ([IO.Path]::GetTempPath()) ('wardogs-radio-banana-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $extract | Out-Null
try {
    Expand-Archive -LiteralPath $bananaZip -DestinationPath $extract -Force
    $installer = Get-ChildItem -LiteralPath $extract -File -Recurse | Where-Object Name -eq 'voicemeeterprosetup.exe' | Select-Object -First 1
    if ($null -eq $installer) { throw 'The verified Voicemeeter archive did not contain voicemeeterprosetup.exe.' }
    Copy-Item -LiteralPath $installer.FullName -Destination (Join-Path $destination $packages[1].Name) -Force
} finally {
    if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
}

$notice = Join-Path (Split-Path -Parent $PSScriptRoot) 'packaging\dependency-notice.txt'
Copy-Item -LiteralPath $notice -Destination (Join-Path $stage 'THIRD_PARTY_DEPENDENCIES.txt') -Force
