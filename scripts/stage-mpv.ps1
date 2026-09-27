[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $StageDir,
    [string] $ArchivePath
)

$ErrorActionPreference = 'Stop'

# Pin the portable x64 build rather than using a moving "latest" URL. The
# archive is verified before its files become part of a release package.
$mpvArchiveName = 'mpv-x86_64-20260924-git-2a4eb8067c.7z'
$mpvArchiveUrl = 'https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20260924/mpv-x86_64-20260924-git-2a4eb8067c.7z'
$mpvArchiveSha256 = '0d39c18086f9df02fbd087b18c8b30e7e328a79481bce83e57f86ab70d0e53bf'
$stage = [IO.Path]::GetFullPath($StageDir)
if (-not (Test-Path -LiteralPath $stage -PathType Container)) { throw 'StageDir must be an existing directory.' }

if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    $ArchivePath = Join-Path ([IO.Path]::GetTempPath()) $mpvArchiveName
    if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
        Invoke-WebRequest -Uri $mpvArchiveUrl -OutFile $ArchivePath
    }
}

$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $mpvArchiveSha256) { throw "The MPV archive SHA-256 did not match the pinned value: $actualHash" }

$tar = Get-Command tar.exe -ErrorAction SilentlyContinue
if ($null -eq $tar) { throw 'Windows tar.exe is required to extract the bundled MPV archive.' }
$extract = Join-Path ([IO.Path]::GetTempPath()) ('wardogs-radio-mpv-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $extract | Out-Null
try {
    & $tar.Source -xf $archive -C $extract
    if ($LASTEXITCODE -ne 0) { throw "Unable to extract $mpvArchiveName." }

    foreach ($file in @('mpv.exe', 'mpv.com', 'd3dcompiler_43.dll')) {
        $source = Join-Path $extract $file
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "The verified MPV archive is missing $file." }
        Copy-Item -LiteralPath $source -Destination (Join-Path $stage $file) -Force
    }

    $notice = Join-Path (Split-Path -Parent $PSScriptRoot) 'packaging\mpv-notice.txt'
    Copy-Item -LiteralPath $notice -Destination (Join-Path $stage 'THIRD_PARTY_MPV.txt') -Force
} finally {
    if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
}
