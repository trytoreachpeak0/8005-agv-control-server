[CmdletBinding()]
param(
    [ValidateSet('G2')][string]$Gate = 'G2',
    [ValidatePattern('^W2G-IS-0[0-7]$')][string]$Slice,
    # Feed this the manifest of the *released tag*, not whatever `main` currently has in the
    # protocol working tree. `CLAUDE.md` is inside the content manifest, so any commit to the
    # protocol repository -- a doc-only one included -- moves contentManifestSha256 away from the
    # released value while the tag keeps pointing at the old one. A released-mode run then dies on
    # 'Protocol manifest hash mismatch', which reads like the pin is stale when the real problem is
    # that the file came from the wrong commit. `git -C <protocol> show protocol-v0.3.0:manifest/release.json`
    # is the manifest this pin expects.
    [Parameter(Mandatory)][string]$ProtocolManifest,
    [Parameter(Mandatory)][string]$Output,
    # Runs the gate against a protocol candidate that has not been released yet. The four hashes
    # below are pinned to protocol-v0.3.0 on purpose: without that pin anyone could hand this script
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
$expectedProtocolCommit = '345c53c58517968192c87c3e7777ed08ddb48726'
$expectedManifestSha256 = 'b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138'
$expectedSchemaBundleSha256 = '68bfd531c4b9c08bc80f6d9c5a67264891efa200acdb154eb18e1d083bf4ed98'
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
    # The released slice-to-vector table below is frozen at v0.3.0 and a candidate may have moved
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
    if ($manifest.releaseVersion -ne '0.3.0' -or
        $manifest.protocolVersion -ne 3 -or
        $manifest.schemaBundleSha256 -ne $expectedSchemaBundleSha256 -or
        $manifest.vectorsSha256 -ne $expectedVectorsSha256) {
        throw 'Protocol manifest composite identity differs from protocol-v0.3.0.'
    }
    $protocolReleaseStatus = 'RELEASED'
    $protocolTag = 'protocol-v0.3.0'
    $protocolReleaseVersion = '0.3.0'
    $protocolSchemaBundleSha256 = $expectedSchemaBundleSha256
    $protocolVectorsSha256 = $expectedVectorsSha256
    $protocolRepositoryCommit = $expectedProtocolCommit
}
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if ($env:WIRE_TO_GATE_DOTNET_EXE) { $env:WIRE_TO_GATE_DOTNET_EXE } else { 'dotnet' }
if ($env:WIRE_TO_GATE_DOTNET_EXE -and -not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "WIRE_TO_GATE_DOTNET_EXE not found: $dotnet"
}
# Anchored to the caller's location now, because the test run below changes directory and a relative
# --results-directory would follow it into the repository.
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if (Test-Path -LiteralPath $Output) { throw "Output directory already exists: $Output" }
# Run from inside the repository. `dotnet` looks for global.json from the current directory, not from
# the project path it is handed -- an explicit dotnet.exe included -- so a gate started from outside
# the clone would test with the newest installed SDK and still write a PASS. The SDK is resolved
# before the output directory exists, so a missing pinned SDK leaves no half-made evidence behind,
# and it goes into gate-result.json so the evidence says which toolchain it measured.
Push-Location -LiteralPath $root
try {
    $dotnetSdkVersion = & $dotnet --version 2>&1
    if ($LASTEXITCODE -ne 0) { throw "dotnet could not resolve the SDK pinned by $(Join-Path $root 'global.json'): $dotnetSdkVersion" }
    $dotnetSdkVersion = "$dotnetSdkVersion".Trim()
    New-Item -ItemType Directory -Path $Output | Out-Null
    $startedAt = [DateTimeOffset]::UtcNow
    & $dotnet test (Join-Path $root 'tests\ControlServer.Tests\ControlServer.Tests.csproj') -c Release --filter "IntegrationSlice=$Slice" --logger "trx;LogFileName=control-$Slice.trx" --results-directory $Output
    $testExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
$result = [ordered]@{
    schemaVersion = '1.0.0'
    gate = $Gate
    integrationSliceId = $Slice
    status = if ($testExitCode -eq 0) { 'PASS' } else { 'FAIL' }
    startedAt = $startedAt.ToString('O')
    finishedAt = ([DateTimeOffset]::UtcNow).ToString('O')
    implementationRepository = '8005-agv-control-server'
    implementationCommit = (git -c safe.directory=$root -C $root rev-parse HEAD).Trim()
    dotnetSdkVersion = $dotnetSdkVersion
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
