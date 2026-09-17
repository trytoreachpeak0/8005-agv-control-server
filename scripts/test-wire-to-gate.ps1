#Requires -Version 7
[CmdletBinding()]
param(
    [ValidateSet('G2')][string]$Gate = 'G2',
    [ValidatePattern('^FP-IS-(0[0-9]|1[0-5])$')][string]$Slice,
    [Parameter(Mandatory)][string]$ProtocolManifest,
    [Parameter(Mandatory)][string]$Output
)

$ErrorActionPreference = 'Stop'
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
$result = [ordered]@{
    # 1.2.0, not 1.1.0: this run adds schemaConformance. Additive again, and for the same reason the
    # version moved to 1.1.0 -- a consumer that cannot tell the two shapes apart cannot tell an
    # evidence directory that had its outbound lines checked against the contract from one that did
    # not, and from control-server#85 on, CONTROL_SERVER_G2 means both.
    schemaVersion = '1.2.0'
    gate = $Gate
    integrationSliceId = $Slice
    status = if ($testExitCode -eq 0) { 'PASS' } else { 'FAIL' }
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
    # there, which is itself worth recording: an evidence directory whose schemaConformance is null
    # says the slice ran without its lines being compared with the contract.
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
    testExitCode = $testExitCode
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'gate-result.json') -Encoding utf8NoBOM
exit $testExitCode
