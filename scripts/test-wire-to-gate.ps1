#Requires -Version 7
[CmdletBinding()]
param(
    [ValidateSet('G2')][string]$Gate = 'G2',
    [ValidatePattern('^FP-IS-(0[0-9]|1[0-5])$')][string]$Slice,
    [Parameter(Mandatory)][string]$ProtocolManifest,
    [Parameter(Mandatory)][string]$Output
)

$ErrorActionPreference = 'Stop'
# Both paths are made absolute here, against the location the script was called from, and only the
# absolute values are used below. -Output in particular leaves this process: it is handed to the test
# host as WIRE_TO_GATE_SCHEMA_REPORT_DIR, and the test host resolves a relative path against its own
# working directory under bin/. Batch 8's exit ran all fifteen slices that way, and every
# gate-result.json said PASS with schemaConformance null while schema-coverage.json sat in the build
# output (control-server#559). Fixing it at the entry, not where the variable is set, keeps every later
# use of either path from having to know. Same conversion as the G3 runners (control-server#264).
# G2GateScriptReportLocationTests runs this script against a stand-in test host to keep it so.
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$ProtocolManifest = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProtocolManifest)
if (-not (Test-Path -LiteralPath $ProtocolManifest -PathType Leaf)) { throw "Protocol manifest not found: $ProtocolManifest" }
$root = Split-Path -Parent $PSScriptRoot

# The expected identity is not restated here. It lives in ProtocolCandidateIdentity.cs, is mirrored
# into appsettings.json for the release-candidate packager, and this script reads that mirror --
# three hand-kept copies of nine hashes is how a gate ends up certifying a protocol nobody is
# running. ProtocolIdentityArchitectureTests is what keeps the mirror equal to the constants.
$settingsPath = Join-Path $root 'src\ControlServer.Host\appsettings.json'
$expected = (Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json).ProtocolCandidate
if ($null -eq $expected) { throw "appsettings.json carries no ProtocolCandidate identity: $settingsPath" }

# Likewise the slice's vector list is read from the protocol's own index rather than copied. The
# copy under vendor/ is the protocol file byte for byte and ProtocolVectorTestBindingArchitectureTests
# pins its SHA-256; the digest is recorded into the evidence below so a run says which index it used.
$indexPath = Join-Path $root 'vendor\8005-agv-protocol\integration-slices\index.json'
if (-not (Test-Path -LiteralPath $indexPath -PathType Leaf)) { throw "Vendored slice index not found: $indexPath" }
$indexSha256 = (Get-FileHash -LiteralPath $indexPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sliceEntry = ((Get-Content -Raw -LiteralPath $indexPath | ConvertFrom-Json).slices |
    Where-Object { $_.integrationSliceId -eq $Slice })
if ($null -eq $sliceEntry) { throw "Slice '$Slice' is not in the frozen slice family index." }

$actualManifestSha256 = (Get-FileHash -LiteralPath $ProtocolManifest -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualManifestSha256 -ne $expected.manifestSha256) {
    throw "Protocol manifest hash mismatch: expected $($expected.manifestSha256), actual $actualManifestSha256"
}
$manifest = Get-Content -LiteralPath $ProtocolManifest -Raw | ConvertFrom-Json
if ($manifest.releaseVersion -ne $expected.releaseVersion -or
    $manifest.protocolVersion -ne $expected.protocolVersion -or
    $manifest.profileId -ne $expected.profileId -or
    $manifest.schemaBundleSha256 -ne $expected.schemaBundleSha256 -or
    $manifest.vectorsSha256 -ne $expected.vectorsSha256) {
    throw "Protocol manifest composite identity differs from $($expected.tag)."
}
$dotnet = if ($env:WIRE_TO_GATE_DOTNET_EXE) { $env:WIRE_TO_GATE_DOTNET_EXE } else { 'dotnet' }
if ($env:WIRE_TO_GATE_DOTNET_EXE -and -not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "WIRE_TO_GATE_DOTNET_EXE not found: $dotnet"
}

# Before the build, not after: a mistyped -Output should cost a message, not a Release build.
if (Test-Path -LiteralPath $Output) { throw "Output directory already exists: $Output" }

# A filter that selects nothing exits 0, and this script used to write that as PASS. Under the v1
# family it could not happen -- the pattern was ^W2G-IS-0[0-7]$ and all eight had tests. The v2
# family is sixteen and eight of them are scheduled into batches 3 through 8 with no implementation
# and no test yet, so the same command would now mint a green gate result for a slice nobody has
# built. Measured on 2026-09-08 before this check existed: -Slice FP-IS-09 produced
# "status": "PASS", "testExitCode": 0 over zero executed tests. Written up in
# docs/defects/20260908-empty-slice-filter-mints-a-green-g2.md.
#
# Refusing, rather than writing INCONCLUSIVE evidence. An evidence directory that exists is a run
# that happened; a slice with no tests has nothing to run, and the honest artefact is no artefact.
#
# The listing is matched on the test namespace rather than on indentation. VSTest prints each test
# under "The following Tests are available:" indented by four spaces today, and prints
# "No test matches the given testcase filter" when there are none -- but a gate-blocking decision
# should not rest on a console layout, and ControlServer.Tests. is an identifier this repository
# controls.
$projectPath = Join-Path $root 'tests\ControlServer.Tests\ControlServer.Tests.csproj'
$selected = @(& $dotnet test $projectPath -c Release --list-tests --filter "IntegrationSlice=$Slice" |
    Where-Object { $_ -match '^\s+ControlServer\.Tests\.\S' })
if ($selected.Count -eq 0) {
    throw ("Slice '$Slice' selects no test in ControlServer.Tests, so there is nothing for " +
           "CONTROL_SERVER_G2 to certify. Its vectors are " +
           ($sliceEntry.vectorIds -join ', ') + '.')
}

New-Item -ItemType Directory -Path $Output | Out-Null
$startedAt = [DateTimeOffset]::UtcNow
# Every line the slice's tests send is validated against the protocol's JSON Schemas when the run ends
# (tests/ControlServer.Tests/OutboundSchemaConformance.cs). A violation makes dotnet test exit
# non-zero while its console summary still says "Failed: 0", so the summary above is not the verdict
# this script reads: $LASTEXITCODE is. schema-coverage.json always lands here next to the TRX, and
# schema-violations.json when something failed.
$env:WIRE_TO_GATE_SCHEMA_REPORT_DIR = $Output
try {
    & $dotnet test $projectPath -c Release --filter "IntegrationSlice=$Slice" --logger "trx;LogFileName=control-$Slice.trx" --results-directory $Output
    $testExitCode = $LASTEXITCODE
} finally {
    Remove-Item Env:WIRE_TO_GATE_SCHEMA_REPORT_DIR -ErrorAction SilentlyContinue
}
$schemaCoveragePath = Join-Path $Output 'schema-coverage.json'
$schemaCoverage = if (Test-Path -LiteralPath $schemaCoveragePath) { Get-Content -LiteralPath $schemaCoveragePath -Raw | ConvertFrom-Json } else { $null }
# A clean exit with no report is lost evidence, never "this slice sent nothing" (control-server#559).
# Read from the code, not assumed: tools/ControlServer.SchemaConformance writes schema-coverage.json on
# every run that gets as far as validating, zero lines included (linesChecked 0), and every way it stops
# earlier returns non-zero, which OutboundSchemaConformance turns into a failed test run. The onboard
# side's run-w2g-g2.ps1 does record null for a slice that sends nothing; this side has no such case, so
# there is nothing to tell apart and the missing report is a FAIL. The check did run when the tests
# exited 0 -- a violation would have failed them -- but a PASS whose proof of that is gone is not
# evidence an exit can cite.
$schemaReportMissing = $null -eq $schemaCoverage
$schemaReportLost = $testExitCode -eq 0 -and $schemaReportMissing
if ($schemaReportLost) {
    [Console]::Error.WriteLine(
        "SCHEMA_REPORT_MISSING: the tests exited 0 but $schemaCoveragePath is missing or empty. The outbound " +
        "schema report was written somewhere else or not at all, so this slice is recorded as FAIL.")
}
$result = [ordered]@{
    # 1.2.0, not 1.1.0: this run adds schemaConformance. Additive again, and for the same reason the
    # version moved to 1.1.0 -- a consumer that cannot tell the two shapes apart cannot tell an
    # evidence directory that had its outbound lines checked against the contract from one that did
    # not, and from control-server#85 on, CONTROL_SERVER_G2 means both.
    # 1.3.0 adds schemaConformanceMissing, and with it a FAIL whose testExitCode is 0. A consumer that
    # inferred status from testExitCode reads a 1.3.0 result wrongly; read status.
    schemaVersion = '1.3.0'
    gate = $Gate
    integrationSliceId = $Slice
    status = if ($testExitCode -eq 0 -and -not $schemaReportLost) { 'PASS' } else { 'FAIL' }
    startedAt = $startedAt.ToString('O')
    finishedAt = ([DateTimeOffset]::UtcNow).ToString('O')
    implementationRepository = '8005-agv-control-server'
    implementationCommit = (git -c safe.directory=$root -C $root rev-parse HEAD).Trim()
    protocolReleaseVersion = $expected.releaseVersion
    protocolTag = $expected.tag
    protocolProfileId = $expected.profileId
    protocolVersion = $expected.protocolVersion
    protocolApprovalStatus = $expected.approvalStatus
    protocolRepositoryCommit = $expected.repositoryCommit
    protocolManifestSha256 = $actualManifestSha256
    protocolSchemaBundleSha256 = $expected.schemaBundleSha256
    protocolVectorsSha256 = $expected.vectorsSha256
    integrationSliceIndexSha256 = $indexSha256
    selectedTestCount = $selected.Count
    vectorIds = @($sliceEntry.vectorIds)
    # What the outbound schema check made of this slice's traffic. $null means the report is not
    # there: schemaConformanceMissing below says so, and on a run whose tests passed status is FAIL.
    # It does not mean the lines went unchecked -- see where $schemaReportLost is computed.
    schemaConformance = if ($schemaCoverage) {
        [ordered]@{
            linesChecked = $schemaCoverage.linesChecked
            linesInViolation = $schemaCoverage.linesInViolation
            knownViolationsMatched = $schemaCoverage.knownViolationsMatched
            coverage = 'schema-coverage.json'
        }
    } else {
        $null
    }
    # True whenever schema-coverage.json is absent. On a run whose tests failed that is expected (the
    # validator stops before writing it); on a run whose tests passed it is what made status FAIL.
    schemaConformanceMissing = $schemaReportMissing
    testExitCode = $testExitCode
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'gate-result.json') -Encoding utf8NoBOM
# Non-zero whenever status is FAIL, so a caller that reads only the exit code is not told PASS.
exit ($schemaReportLost ? 1 : $testExitCode)
