[CmdletBinding()]
param([Parameter(Mandatory)] [string] $StageDir)
$ErrorActionPreference = 'Stop'
$stage = [IO.Path]::GetFullPath($StageDir)
if ($stage -eq [IO.Path]::GetPathRoot($stage) -or -not (Test-Path -LiteralPath $stage -PathType Container)) { throw 'StageDir must be an existing non-root directory.' }
$prerequisitePattern = Join-Path $stage 'Prerequisites\*'
$inventory = Get-ChildItem -LiteralPath $stage -File -Recurse | Where-Object { $_.Name -ne 'PACKAGE_CONTENTS.sha256' -and $_.FullName -notlike $prerequisitePattern } | Sort-Object FullName | ForEach-Object {
    "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) *$([IO.Path]::GetRelativePath($stage, $_.FullName))"
}
[IO.File]::WriteAllLines((Join-Path $stage 'PACKAGE_CONTENTS.sha256'), [string[]]$inventory, [Text.UTF8Encoding]::new($false))
