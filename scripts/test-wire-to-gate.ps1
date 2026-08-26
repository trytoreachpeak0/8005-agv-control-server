[CmdletBinding()]
param(
    [ValidateSet('G2')][string]$Gate = 'G2',
    [ValidatePattern('^W2G-IS-0[0-7]$')][string]$Slice,
    [Parameter(Mandatory)][string]$ProtocolManifest,
    [Parameter(Mandatory)][string]$Output
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ProtocolManifest -PathType Leaf)) { throw "Protocol manifest not found: $ProtocolManifest" }
$expectedProtocolCommit = '1531489e42e328f28bfe0c51ed3f8c56e5ce0279'
$expectedManifestSha256 = 'a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f'
$expectedSchemaBundleSha256 = 'e04296e9bcf48c341bc91fef5731f6f465a5ecdbb9adedc17f3bac58e193d30c'
$expectedVectorsSha256 = 'fc5902b71d1b276c674f8a21c738d27193ddcbaf9b352951deffbaf1488d356e'
$sliceVectors = @{
    'W2G-IS-00' = @('CV-SESSION-RECOVERY-HAPPY', 'CV-SESSION-RECONNECT-DURING-RECOVERY', 'CV-SNAPSHOT-REPLACE-AND-ACK', 'CV-SNAPSHOT-SAME-REVISION-CONFLICT')
    'W2G-IS-01' = @('CV-DEMAND-ACCEPT-TO-PICKUP')
    'W2G-IS-02' = @('CV-PICKUP-SUBLOT-LOAD', 'CV-LOAD-CORRECTION', 'CV-LOAD-CANCELLATION-ALL-EMPTY')
    'W2G-IS-03' = @('CV-PREDEPARTURE-SAFETY-EXPIRES', 'CV-OPERATION-RESULT-UNKNOWN-RECONCILE')
    'W2G-IS-04' = @('CV-GATE-UNLOAD-ALL-EMPTY')
    'W2G-IS-05' = @('CV-CONNECTION-LOSS-SAFE-FINISH', 'CV-SESSION-RECONNECT-DURING-RECOVERY')
    'W2G-IS-06' = @('CV-RELIABLE-RETRY-SAME-CONTENT', 'CV-RELIABLE-RETRY-DIFFERENT-CONTENT', 'CV-REQUEST-FIRST-RESULT-REPLAY', 'CV-OPERATION-RESULT-UNKNOWN-RECONCILE')
    'W2G-IS-07' = @('CV-OPERATION-RESULT-UNKNOWN-RECONCILE', 'CV-EXCEPTION-RESUME', 'CV-EXCEPTION-COMPENSATE', 'CV-FAULT-CARGO-HANDOFF', 'CV-FORCED-MECHANICAL-RECOVERY', 'CV-MANUAL-CHARGING-RETURN')
}
$actualManifestSha256 = (Get-FileHash -LiteralPath $ProtocolManifest -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualManifestSha256 -ne $expectedManifestSha256) {
    throw "Protocol manifest hash mismatch: expected $expectedManifestSha256, actual $actualManifestSha256"
}
$manifest = Get-Content -LiteralPath $ProtocolManifest -Raw | ConvertFrom-Json
if ($manifest.releaseVersion -ne '0.1.1' -or
    $manifest.protocolVersion -ne 1 -or
    $manifest.schemaBundleSha256 -ne $expectedSchemaBundleSha256 -or
    $manifest.vectorsSha256 -ne $expectedVectorsSha256) {
    throw 'Protocol manifest composite identity differs from protocol-v0.1.1.'
}
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if ($env:WIRE_TO_GATE_DOTNET_EXE) { $env:WIRE_TO_GATE_DOTNET_EXE } else { 'dotnet' }
if ($env:WIRE_TO_GATE_DOTNET_EXE -and -not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "WIRE_TO_GATE_DOTNET_EXE not found: $dotnet"
}
if (Test-Path -LiteralPath $Output) { throw "Output directory already exists: $Output" }
New-Item -ItemType Directory -Path $Output | Out-Null
$startedAt = [DateTimeOffset]::UtcNow
& $dotnet test (Join-Path $root 'tests\ControlServer.Tests\ControlServer.Tests.csproj') -c Release --filter "IntegrationSlice=$Slice" --logger "trx;LogFileName=control-$Slice.trx" --results-directory $Output
$testExitCode = $LASTEXITCODE
$result = [ordered]@{
    schemaVersion = '1.0.0'
    gate = $Gate
    integrationSliceId = $Slice
    status = if ($testExitCode -eq 0) { 'PASS' } else { 'FAIL' }
    startedAt = $startedAt.ToString('O')
    finishedAt = ([DateTimeOffset]::UtcNow).ToString('O')
    implementationRepository = '8005-agv-control-server'
    implementationCommit = (git -c safe.directory=$root -C $root rev-parse HEAD).Trim()
    protocolReleaseVersion = '0.1.1'
    protocolTag = 'protocol-v0.1.1'
    protocolRepositoryCommit = $expectedProtocolCommit
    protocolManifestSha256 = $actualManifestSha256
    protocolSchemaBundleSha256 = $expectedSchemaBundleSha256
    protocolVectorsSha256 = $expectedVectorsSha256
    vectorIds = $sliceVectors[$Slice]
    testExitCode = $testExitCode
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'gate-result.json') -Encoding utf8NoBOM
exit $testExitCode
