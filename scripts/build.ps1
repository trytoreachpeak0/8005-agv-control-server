[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if ($env:WIRE_TO_GATE_DOTNET_EXE) { $env:WIRE_TO_GATE_DOTNET_EXE } else { 'dotnet' }
if ($env:WIRE_TO_GATE_DOTNET_EXE -and -not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "WIRE_TO_GATE_DOTNET_EXE not found: $dotnet"
}

# Run from inside the repository. `dotnet` looks for global.json from the current directory, not from
# the solution path it is handed -- an explicit dotnet.exe included -- so a caller standing outside
# the clone silently builds with the newest installed SDK instead of the pinned one.
Push-Location -LiteralPath $root
try {
    $sdk = & $dotnet --version 2>&1
    if ($LASTEXITCODE -ne 0) { throw "dotnet could not resolve the SDK pinned by $(Join-Path $root 'global.json'): $sdk" }
    Write-Output "dotnet SDK: $sdk"
    & $dotnet restore (Join-Path $root 'ControlServer.sln') --locked-mode
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        & $dotnet build (Join-Path $root 'ControlServer.sln') -c $Configuration --no-restore
        $exitCode = $LASTEXITCODE
    }
}
finally {
    Pop-Location
}
exit $exitCode
