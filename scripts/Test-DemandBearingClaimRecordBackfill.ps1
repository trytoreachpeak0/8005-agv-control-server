#Requires -Version 7

<#
.SYNOPSIS
    Offline self-check of what run-demand-bearing-g3-vectors.ps1 expects the first host's migration to do to the
    vehicle claim record table of a restored store (control-server#544), and of which tables it lets be missing.

.DESCRIPTION
    A few minutes: one build of ControlServer.Host, then a handful of `dotnet ef database update` runs against
    throwaway SQLite files under -WorkRoot. No server is started, no peer, no window, and nothing it makes is gate
    evidence: the stores are seeded by hand with made-up vehicles, so they must never be fed to the runner's
    -FieldRunRoot (control-server#460 refuses generated stores for a reason; do not rename keys to get past it).

    Every `dotnet ef` call names its database with --connection. Never drop that: without a connection string the
    host's own migration path falls back to the MVP production database.

    1. Backfill count, from three starting states. The runner's Get-ExpectedClaimRecordCount, taken from its AST,
       is given the runner's own Read-ControlDatabase baseline of a seeded store; then the real migrations take the
       store to the latest schema and the claim record table is counted directly. They must agree, and the parts
       must equal the hand-computed values written beside each seed:
         - leases present, records present (between Batch8VehiclePurposePersistence and
           Batch8RetireOldVehicleOccupancy): dozens of claims, leases and records in every combination the
           migration's three steps tell apart, including released leases whose journey gets a backfilled record
           in step two and so no lease record in step three;
         - before batch 8 (MapNameBaselines): no claim record table at all;
         - already migrated: the expected count is the count it already has.
       The runner's previous criterion (afterProbe equal to final) is printed beside it as a control: it never
       looks at the baseline, so it is green whatever the migration wrote.

    Exits 1 when any check comes out the other way, and prints every check either way.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/Test-DemandBearingClaimRecordBackfill.ps1 -WorkRoot F:/8005-scratch/cs544-selfcheck
#>
[CmdletBinding()]
param(
    # Where the throwaway stores go; a new subdirectory is made per run and removed afterwards.
    [string]$WorkRoot = [IO.Path]::GetTempPath(),
    [string]$RunnerSource = (Join-Path $PSScriptRoot 'run-demand-bearing-g3-vectors.ps1'),
    [string]$Repository = (Split-Path -Parent $PSScriptRoot),
    # Keep the stores for inspection instead of removing them.
    [switch]$KeepWork
)

$ErrorActionPreference = 'Stop'

$failures = [System.Collections.Generic.List[string]]::new()
function Check([string]$name, [bool]$ok, [string]$detail) {
    Write-Host ("{0} {1}{2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $(if ($ok) { '' } else { " -- $detail" }))
    if (-not $ok) { $failures.Add($name) }
}

# The migration the leases were last written under, and the one before batch 8 created the record table.
$beforeRetirement = '20260929044052_Batch8VehiclePurposePersistence'
$beforeBatch8 = '20260928153736_MapNameBaselines'

$tokens = $null
$parseErrors = $null
$runnerAst = [System.Management.Automation.Language.Parser]::ParseFile($RunnerSource, [ref]$tokens, [ref]$parseErrors)
if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) { throw "The runner does not parse: $RunnerSource" }
$missingFunctions = [System.Collections.Generic.List[string]]::new()
foreach ($name in 'Invoke-SqliteRows', 'Invoke-SqliteScalarLong', 'Get-FileFingerprint', 'Read-ControlDatabase',
                  'Get-ExpectedClaimRecordCount') {
    $definition = @($runnerAst.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
            }, $true)) | Select-Object -First 1
    if ($null -eq $definition) { $missingFunctions.Add($name); continue }
    . ([scriptblock]::Create($definition.Extent.Text))
}
Check 'the runner defines every function this self-check calls' ($missingFunctions.Count -eq 0) "missing: $($missingFunctions -join ', ')"

$work = Join-Path $WorkRoot ('cs544-claim-records-' + [guid]::NewGuid().ToString('n').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
$hostProject = Join-Path $Repository 'src/ControlServer.Host'
$infrastructureProject = Join-Path $Repository 'src/ControlServer.Infrastructure'

function Invoke-Dotnet([string[]]$Arguments, [string]$Label) {
    Push-Location $Repository
    try { $output = & dotnet @Arguments 2>&1; $exitCode = $LASTEXITCODE } finally { Pop-Location }
    if ($exitCode -ne 0) {
        throw "$Label exited with code $exitCode`n$(@($output | Select-Object -Last 25) -join "`n")"
    }
    if ($output -match 'controlserver\.design\.db') { throw "$Label touched the design-time database: $output" }
}

# Never without --connection: see the description.
function Update-Store([string]$Path, [string]$Migration) {
    $arguments = @('ef', 'database', 'update')
    if (-not [string]::IsNullOrEmpty($Migration)) { $arguments += $Migration }
    $arguments += @('--no-build', '--project', $infrastructureProject, '--startup-project', $hostProject,
                    '--connection', "Data Source=$Path")
    Invoke-Dotnet $arguments "dotnet ef database update $Migration ($Path)"
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "dotnet ef did not write $Path" }
}

function Invoke-StoreSql([string]$Path, [string]$Sql) {
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$Path;Pooling=False")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        try { $command.CommandText = $Sql; [void]$command.ExecuteNonQuery() } finally { $command.Dispose() }
    } finally { $connection.Dispose() }
}

function Get-RecordCount([string]$Path) {
    return Invoke-SqliteScalarLong -DatabasePath $Path -Sql 'SELECT COUNT(*) FROM VehiclePurposeClaimRecords'
}

$at = "'2026-09-20 08:00:00+00:00'"
$later = "'2026-09-20 09:00:00+00:00'"
function Claim([string]$Vehicle, [string]$Journey) {
    "INSERT INTO VehiclePurposeClaims (VehicleKey, Purpose, JourneyId, ClaimedAt) VALUES ('$Vehicle', 'TRANSPORT', '$Journey', $at);"
}
function Lease([string]$Vehicle, [string]$Journey, [bool]$Released) {
    "INSERT INTO VehicleDispatchLeases (JourneyId, AcquiredAt, DemandId, ReleasedAt, VehicleKey) VALUES ('$Journey', $at, 'demand-$Journey', $(if ($Released) { $later } else { 'NULL' }), '$Vehicle');"
}
function Record([string]$Vehicle, [string]$Journey, [bool]$Released) {
    "INSERT INTO VehiclePurposeClaimRecords (RecordId, VehicleKey, Purpose, JourneyId, AcquiredAt, ReleasedAt, ReleaseReason) VALUES ('seed:${Vehicle}:${Journey}', '$Vehicle', 'TRANSPORT', '$Journey', $at, $(if ($Released) { "$later, 'SEEDED'" } else { 'NULL, NULL' }));"
}

# The baseline as the runner reads it, then the expectation as the runner computes it from that baseline alone.
function Get-Expectation([string]$Path) {
    $script:controlDatabasePath = $Path
    $baseline = Read-ControlDatabase
    return Get-ExpectedClaimRecordCount -Baseline $baseline
}

function Test-Expectation([string]$Label, [string]$Path, [hashtable]$Parts) {
    $expectation = $null
    $thrown = $null
    try { $expectation = Get-Expectation $Path } catch { $thrown = $_.Exception.Message }
    $wrong = @(foreach ($key in $Parts.Keys) {
            if ($null -eq $expectation -or "$($expectation[$key])" -cne "$($Parts[$key])") {
                "$key=$(${expectation}?[$key]) (hand-computed $($Parts[$key]))"
            }
        })
    Check "${Label}: the runner's expectation has the hand-computed parts" ($null -eq $thrown -and $wrong.Count -eq 0) "$thrown $($wrong -join '; ')"
    Update-Store $Path $null
    $actual = Get-RecordCount $Path
    $expected = ${expectation}?['expected']
    Check "${Label}: the real migration leaves the count the runner expected" ($null -ne $expected -and [long]$expected -eq $actual) `
        "expected $expected, the migration left $actual"
    # The runner's criterion before control-server#544: the count after the first host equal to the count after the
    # second. Here the second read follows the first with nothing in between, as in a run where nothing writes a
    # record after the migration, so it holds whatever the migration wrote.
    $oldCriterion = (Get-RecordCount $Path) -eq $actual
    Write-Host ("     control: the previous criterion (afterProbe = final) is {0} here; it never reads the baseline" -f
        $(if ($oldCriterion) { 'PASS' } else { 'FAIL' }))
    return $actual
}

try {
    Invoke-Dotnet @('tool', 'restore') 'dotnet tool restore'
    Invoke-Dotnet @('build', $hostProject, '-c', 'Debug', '--nologo', '-v', 'q') 'dotnet build ControlServer.Host'
    $hostOutput = Join-Path $hostProject 'bin/Debug/net8.0/win-x64'
    Add-Type -Path (Join-Path $hostOutput 'Microsoft.Data.Sqlite.dll')

    # --- 1a. leases and records both present: between the two batch-8 migrations --------------------------------
    # Hand-computed, group by group, in the migration's own order (step 1 closes, step 2 backfills claims, step 3
    # backfills released leases whose vehicle and journey have no record after step 2):
    #   A  8 claims, each with its unreleased lease and an open record          records 8, backfill 0, lease 0
    #   B 10 claims, each with its unreleased lease, no record                  records 0, backfill 10, lease 0
    #   C  5 claims with only a released record of the same journey             records 5, backfill 5, lease 0
    #   F  4 claims whose own journey's lease is released, no record            records 0, backfill 4, lease 0
    #        (step 2 writes their record, so step 3 writes none: the order matters)
    #   D 12 released leases, 3 on each of 4 vehicles, no claim, no record      records 0, backfill 0, lease 12
    #   E  6 released leases, each with a released record of its journey       records 6, backfill 0, lease 0
    #   G  3 open records without a claim (step 1 closes them, adds nothing);   records 3, backfill 0, lease 0
    #        one has a released lease of the same journey (has a record: none),
    #        another a released lease of a journey with no record                                      lease 1
    #   H  1 released lease on an A vehicle under a journey with no record      records 0, backfill 0, lease 1
    #   total: records 22, backfill 19, lease 14, expected 55
    $between = Join-Path $work 'between-batch8-migrations.db'
    Update-Store $between $beforeRetirement
    $seed = [System.Collections.Generic.List[string]]::new()
    foreach ($i in 1..8) { $seed.Add((Claim "VA$i" "jA$i")); $seed.Add((Lease "VA$i" "jA$i" $false)); $seed.Add((Record "VA$i" "jA$i" $false)) }
    foreach ($i in 1..10) { $seed.Add((Claim "VB$i" "jB$i")); $seed.Add((Lease "VB$i" "jB$i" $false)) }
    foreach ($i in 1..5) { $seed.Add((Claim "VC$i" "jC$i")); $seed.Add((Record "VC$i" "jC$i" $true)) }
    foreach ($i in 1..4) { $seed.Add((Claim "VF$i" "jF$i")); $seed.Add((Lease "VF$i" "jF$i" $true)) }
    foreach ($v in 1..4) { foreach ($k in 1..3) { $seed.Add((Lease "VD$v" "jD$v-$k" $true)) } }
    foreach ($i in 1..6) { $seed.Add((Lease "VE$i" "jE$i" $true)); $seed.Add((Record "VE$i" "jE$i" $true)) }
    foreach ($i in 1..3) { $seed.Add((Record "VG$i" "jG$i" $false)) }
    $seed.Add((Lease 'VG1' 'jG1' $true))
    $seed.Add((Lease 'VG2' 'jG2-other' $true))
    $seed.Add((Lease 'VA1' 'jH1' $true))
    Invoke-StoreSql $between ($seed -join "`n")
    $migratedCount = Test-Expectation 'leases and records present' $between @{
        migrationState = 'LEASES_NOT_YET_RETIRED'; originalRecords = 22; claimBackfills = 19; leaseBackfills = 14; expected = 55 }

    # --- 1b. before batch 8: no record table yet --------------------------------------------------------------
    #   6 claims with their unreleased lease                                    backfill 6
    #   3 claims whose own journey's lease is released                           backfill 3, lease 0 (step 2 wrote one)
    #   7 released leases on 2 vehicles with no claim                            lease 7
    #   total: records 0, backfill 9, lease 7, expected 16
    $beforeRecords = Join-Path $work 'before-batch8.db'
    Update-Store $beforeRecords $beforeBatch8
    $seed = [System.Collections.Generic.List[string]]::new()
    foreach ($i in 1..6) { $seed.Add((Claim "VP$i" "jP$i")); $seed.Add((Lease "VP$i" "jP$i" $false)) }
    foreach ($i in 1..3) { $seed.Add((Claim "VQ$i" "jQ$i")); $seed.Add((Lease "VQ$i" "jQ$i" $true)) }
    foreach ($i in 1..7) { $seed.Add((Lease "VR$($i % 2)" "jR$i" $true)) }
    Invoke-StoreSql $beforeRecords ($seed -join "`n")
    [void](Test-Expectation 'before batch 8, no record table' $beforeRecords @{
        migrationState = 'RECORD_TABLE_NOT_YET_CREATED'; originalRecords = 0; claimBackfills = 9; leaseBackfills = 7; expected = 16 })

    # --- 1c. already migrated: the lease table is gone, nothing is backfilled ------------------------------------
    $migrated = Join-Path $work 'already-migrated.db'
    Copy-Item -LiteralPath $between -Destination $migrated
    [void](Test-Expectation 'already migrated' $migrated @{
        migrationState = 'ALREADY_RETIRED'; originalRecords = $migratedCount; claimBackfills = 0; leaseBackfills = 0; expected = $migratedCount })
} finally {
    [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()
    if ($KeepWork) { Write-Host "Stores kept in $work" }
    else { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($failures.Count -gt 0) {
    Write-Host "DemandBearingClaimRecordBackfill self-check: $($failures.Count) check(s) came out the other way."
    exit 1
}
Write-Host 'DemandBearingClaimRecordBackfill self-check: every check as expected.'
