[CmdletBinding()]
param(
    [ValidateSet('G2')][string]$Gate = 'G2',
    [ValidatePattern('^W2G-IS-0[0-7]$')][string]$Slice,
    [Parameter(Mandatory)][string]$ProtocolManifest,
    [Parameter(Mandatory)][string]$Output,
    # Runs the gate against a protocol candidate that has not been released yet. The four hashes
    # below are pinned to protocol-v0.2.0 on purpose: without that pin anyone could hand this script
    # a locally edited manifest and get a green G2 out of it. That protection is exactly what has to
    # stay, so this switch does not weaken it -- it takes a different path that reads the identity
    # out of the supplied manifest and stamps the evidence UNRELEASED_CANDIDATE, so a run against an
    # unsigned candidate can never be mistaken for one against a released contract.
    #
    # It exists because implementation has to be written before there is anything to sign: a
    # breaking candidate leaves the server unverifiable by the released gate (the code no longer
    # matches it) and by the new one (no tag yet). Without this the whole window has unit tests and
    # L2 only, and neither checks conformance against a frozen contract identity.
    [switch]$UnreleasedCandidate
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ProtocolManifest -PathType Leaf)) { throw "Protocol manifest not found: $ProtocolManifest" }
$expectedProtocolCommit = 'dff1686751d1d05c4c06b19ac024b41e84bb8078'
$expectedManifestSha256 = '31bb730565f21b8011b88d447b5b81fc4be28ba34b2fdfb3592bd7586f0a59d6'
$expectedSchemaBundleSha256 = 'bc069d6ab5db55c658d67c2457fcb12812582a94febaa5d2d2a9921aaaca5616'
$expectedVectorsSha256 = 'bd272b63a1d0663d61c4a38d6e8633d7e7d4f7b561a7915c3df51c7a93bd4576'
$sliceVectors = @{
    'W2G-IS-00' = @('CV-SESSION-RECOVERY-HAPPY', 'CV-SESSION-RECONNECT-DURING-RECOVERY', 'CV-SNAPSHOT-REPLACE-AND-ACK', 'CV-SNAPSHOT-SAME-REVISION-CONFLICT')
    'W2G-IS-01' = @('CV-DEMAND-ACCEPT-TO-PICKUP')
    'W2G-IS-02' = @('CV-PICKUP-SUBLOT-LOAD', 'CV-LOAD-CORRECTION', 'CV-LOAD-CANCELLATION-ALL-EMPTY', 'CV-LOAD-CANCELLATION-BEFORE-LOAD')
    'W2G-IS-03' = @('CV-PREDEPARTURE-SAFETY-EXPIRES', 'CV-OPERATION-RESULT-UNKNOWN-RECONCILE')
    'W2G-IS-04' = @('CV-GATE-UNLOAD-ALL-EMPTY')
    'W2G-IS-05' = @('CV-CONNECTION-LOSS-SAFE-FINISH', 'CV-SESSION-RECONNECT-DURING-RECOVERY')
    'W2G-IS-06' = @('CV-RELIABLE-RETRY-SAME-CONTENT', 'CV-RELIABLE-RETRY-DIFFERENT-CONTENT', 'CV-REQUEST-FIRST-RESULT-REPLAY', 'CV-OPERATION-RESULT-UNKNOWN-RECONCILE')
    'W2G-IS-07' = @('CV-OPERATION-RESULT-UNKNOWN-RECONCILE', 'CV-EXCEPTION-RESUME', 'CV-EXCEPTION-COMPENSATE', 'CV-FAULT-CARGO-HANDOFF', 'CV-FORCED-MECHANICAL-RECOVERY', 'CV-MANUAL-CHARGING-RETURN')
}
$actualManifestSha256 = (Get-FileHash -LiteralPath $ProtocolManifest -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = Get-Content -LiteralPath $ProtocolManifest -Raw | ConvertFrom-Json
if ($UnreleasedCandidate) {
    # No pinned comparison to make -- the candidate has no released identity yet. What is still
    # enforced is that the manifest is a real content snapshot and that the evidence carries the
    # exact identity this run was measured against, so a later released run can be told apart from
    # this one by inspection rather than by memory.
    if ($manifest.status -ne 'CONTENT_SNAPSHOT') {
        throw "Protocol manifest is not a content snapshot: $($manifest.status)"
    }
    foreach ($field in @('releaseVersion', 'protocolVersion', 'schemaBundleSha256', 'vectorsSha256')) {
        if (-not $manifest.$field) { throw "Protocol manifest is missing $field." }
    }
    $protocolReleaseStatus = 'UNRELEASED_CANDIDATE'
    $protocolTag = "(unreleased candidate $($manifest.releaseVersion))"
    $protocolReleaseVersion = $manifest.releaseVersion
    $protocolSchemaBundleSha256 = $manifest.schemaBundleSha256
    $protocolVectorsSha256 = $manifest.vectorsSha256
    # The manifest sits inside the protocol repository, so its own HEAD is the candidate's commit.
    # A candidate has no tag to name it by, and recording nothing would make the evidence
    # unreproducible.
    $protocolRoot = Split-Path -Parent (Split-Path -Parent (Resolve-Path -LiteralPath $ProtocolManifest))
    $protocolRepositoryCommit = try {
        (git -c safe.directory=$protocolRoot -C $protocolRoot rev-parse HEAD 2>$null).Trim()
    } catch { $null }
    if (-not $protocolRepositoryCommit) { $protocolRepositoryCommit = '(unknown)' }
    # The released slice-to-vector table below is frozen at v0.2.0 and a candidate may have moved
    # it, so read the candidate's own index instead of reporting a stale vector list.
    $indexPath = Join-Path $protocolRoot 'integration-slices/index.json'
    if (Test-Path -LiteralPath $indexPath -PathType Leaf) {
        $index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
        $candidateSlice = $index.slices | Where-Object { $_.integrationSliceId -eq $Slice }
        if ($candidateSlice) { $sliceVectors[$Slice] = @($candidateSlice.vectorIds) }
    }
} else {
    if ($actualManifestSha256 -ne $expectedManifestSha256) {
        throw "Protocol manifest hash mismatch: expected $expectedManifestSha256, actual $actualManifestSha256"
    }
    if ($manifest.releaseVersion -ne '0.2.0' -or
        $manifest.protocolVersion -ne 2 -or
        $manifest.schemaBundleSha256 -ne $expectedSchemaBundleSha256 -or
        $manifest.vectorsSha256 -ne $expectedVectorsSha256) {
        throw 'Protocol manifest composite identity differs from protocol-v0.2.0.'
    }
    $protocolReleaseStatus = 'RELEASED'
    $protocolTag = 'protocol-v0.2.0'
    $protocolReleaseVersion = '0.2.0'
    $protocolSchemaBundleSha256 = $expectedSchemaBundleSha256
    $protocolVectorsSha256 = $expectedVectorsSha256
    $protocolRepositoryCommit = $expectedProtocolCommit
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
    protocolReleaseStatus = $protocolReleaseStatus
    protocolReleaseVersion = $protocolReleaseVersion
    protocolTag = $protocolTag
    protocolRepositoryCommit = $protocolRepositoryCommit
    protocolManifestSha256 = $actualManifestSha256
    protocolSchemaBundleSha256 = $protocolSchemaBundleSha256
    protocolVectorsSha256 = $protocolVectorsSha256
    vectorIds = $sliceVectors[$Slice]
    testExitCode = $testExitCode
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'gate-result.json') -Encoding utf8NoBOM
exit $testExitCode
