[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$HostExecutable,

    [Parameter(Mandatory)]
    [string]$InputCsv,

    [Parameter(Mandatory)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$Version
)

$ErrorActionPreference = 'Stop'
$hostPath = (Resolve-Path -LiteralPath $HostExecutable).Path
$csvPath = (Resolve-Path -LiteralPath $InputCsv).Path

& $hostPath --import-package-capacity --input $csvPath --version $Version
if ($LASTEXITCODE -ne 0) {
    throw "Package capacity import failed with exit code $LASTEXITCODE."
}
