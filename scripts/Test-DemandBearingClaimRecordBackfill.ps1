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

    0. The expectation alone, no build: 1a's and 1b's seeds below, turned into the baseline rows Read-ControlDatabase
       would read and handed straight to Get-ExpectedClaimRecordCount, must give the hand-computed parts. Runs under
       -SkipStores too.

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
       Beside each, afterProbe = final is printed, but it is the same file read twice and so true by construction;
       the evidence that the previous criterion stays green over a short backfill is section 3.

    2. Missing tables. Copies of the migrated store, each with one table renamed away, read by the runner's
       Read-ControlDatabase: in the baseline read and in the after-migration read (-AfterMigration, which the
       runner uses for afterProbe and final). Only the claim record table, and only in the baseline read, may be
       missing; each of the other nine counted tables, the claim record table after migration, and the claims a
       baseline's backfill is worked out from are refused with an error that names the table.

    3. The judgement. The runner's own $externalTables, $claimRecordsMatchTheBackfillPass and
       $noExternalSideEffectsPass statements, taken from its AST, run over made-up counts: a migration that left what
       the baseline calls for passes; one that left 14 records fewer fails, where the previous criterion
       (afterProbe = final, printed as a control) passes; and a record added after the restart is left to
       vehicleClaimRecordSurvivesTheHostRestart. From the AST as well: the evidence record's matches is that
       judgement itself, and both branches of the store provenance record (which reaches every gate result) carry
       it. -SkipStores runs sections 0 and 3 only.

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
    [switch]$KeepWork,
    # Only sections 0 and 3, which need no build and no store: seconds instead of minutes.
    [switch]$SkipStores
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

# The store is in WAL mode, and what the migration wrote may still be only in -wal: a copy of the main file alone is
# the store from before the migration (control-server#503). Copied whole, as the runner restores one.
function Copy-Store([string]$From, [string]$To) {
    foreach ($suffix in '', '-wal', '-shm') {
        if (Test-Path -LiteralPath ($From + $suffix) -PathType Leaf) { Copy-Item -LiteralPath ($From + $suffix) -Destination ($To + $suffix) }
    }
}

function Get-RecordCount([string]$Path) {
    return Invoke-SqliteScalarLong -DatabasePath $Path -Sql 'SELECT COUNT(*) FROM VehiclePurposeClaimRecords'
}

# A seed is a list of rows, each a claim, a lease or a record of a vehicle and a journey. The same list becomes SQL
# for a real store (sections 1a, 1b) and an in-memory baseline handed straight to Get-ExpectedClaimRecordCount
# (section 0, no build), so the two can never be seeded differently.
$at = '2026-09-20 08:00:00+00:00'
$later = '2026-09-20 09:00:00+00:00'
function Claim([string]$Vehicle, [string]$Journey) { [pscustomobject]@{ Kind = 'Claim'; Vehicle = $Vehicle; Journey = $Journey; Released = $false } }
function Lease([string]$Vehicle, [string]$Journey, [bool]$Released) { [pscustomobject]@{ Kind = 'Lease'; Vehicle = $Vehicle; Journey = $Journey; Released = $Released } }
function Record([string]$Vehicle, [string]$Journey, [bool]$Released) { [pscustomobject]@{ Kind = 'Record'; Vehicle = $Vehicle; Journey = $Journey; Released = $Released } }

function ConvertTo-SeedSql($Seed) {
    $released = { param($row) if ($row.Released) { "'$later'" } else { 'NULL' } }
    return @(foreach ($row in $Seed) {
            switch ($row.Kind) {
                'Claim' { "INSERT INTO VehiclePurposeClaims (VehicleKey, Purpose, JourneyId, ClaimedAt) VALUES ('$($row.Vehicle)', 'TRANSPORT', '$($row.Journey)', '$at');" }
                'Lease' { "INSERT INTO VehicleDispatchLeases (JourneyId, AcquiredAt, DemandId, ReleasedAt, VehicleKey) VALUES ('$($row.Journey)', '$at', 'demand-$($row.Journey)', $(& $released $row), '$($row.Vehicle)');" }
                'Record' { "INSERT INTO VehiclePurposeClaimRecords (RecordId, VehicleKey, Purpose, JourneyId, AcquiredAt, ReleasedAt, ReleaseReason) VALUES ('seed:$($row.Vehicle):$($row.Journey)', '$($row.Vehicle)', 'TRANSPORT', '$($row.Journey)', '$at', $(& $released $row), $(if ($row.Released) { "'SEEDED'" } else { 'NULL' }));" }
            }
        }) -join "`n"
}

# The keys Read-ControlDatabase's baseline gives the expectation, in its row shape.
function ConvertTo-MemoryBaseline($Seed, [bool]$RecordTablePresent) {
    $releasedAt = { param($row) if ($row.Released) { $later } else { $null } }
    return [ordered]@{
        claimRecordTablePresent = $RecordTablePresent
        vehicleClaimRecordRows = @($Seed | Where-Object Kind -eq 'Record' | ForEach-Object {
                [ordered]@{ journeyId = $_.Journey; vehicleKey = $_.Vehicle; acquiredAt = $at; releasedAt = (& $releasedAt $_) } })
        vehiclePurposeClaimRows = @($Seed | Where-Object Kind -eq 'Claim' | ForEach-Object {
                [ordered]@{ vehicleKey = $_.Vehicle; journeyId = $_.Journey } })
        vehicleDispatchLeaseRows = @($Seed | Where-Object Kind -eq 'Lease' | ForEach-Object {
                [ordered]@{ journeyId = $_.Journey; vehicleKey = $_.Vehicle; releasedAt = (& $releasedAt $_) } })
    }
}

function Test-Parts([string]$Label, $Expectation, [string]$Thrown, [hashtable]$Parts) {
    $wrong = @(foreach ($key in $Parts.Keys) {
            if ($null -eq $Expectation -or "$($Expectation[$key])" -cne "$($Parts[$key])") {
                "$key=$(${Expectation}?[$key]) (hand-computed $($Parts[$key]))"
            }
        })
    Check "${Label}: the runner's expectation has the hand-computed parts" ([string]::IsNullOrEmpty($Thrown) -and $wrong.Count -eq 0) "$Thrown $($wrong -join '; ')"
}

# --- 1a's seed: leases and records both present, between the two batch-8 migrations ----------------------------
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
$seedBetween = [System.Collections.Generic.List[object]]::new()
foreach ($i in 1..8) { $seedBetween.Add((Claim "VA$i" "jA$i")); $seedBetween.Add((Lease "VA$i" "jA$i" $false)); $seedBetween.Add((Record "VA$i" "jA$i" $false)) }
foreach ($i in 1..10) { $seedBetween.Add((Claim "VB$i" "jB$i")); $seedBetween.Add((Lease "VB$i" "jB$i" $false)) }
foreach ($i in 1..5) { $seedBetween.Add((Claim "VC$i" "jC$i")); $seedBetween.Add((Record "VC$i" "jC$i" $true)) }
foreach ($i in 1..4) { $seedBetween.Add((Claim "VF$i" "jF$i")); $seedBetween.Add((Lease "VF$i" "jF$i" $true)) }
foreach ($v in 1..4) { foreach ($k in 1..3) { $seedBetween.Add((Lease "VD$v" "jD$v-$k" $true)) } }
foreach ($i in 1..6) { $seedBetween.Add((Lease "VE$i" "jE$i" $true)); $seedBetween.Add((Record "VE$i" "jE$i" $true)) }
foreach ($i in 1..3) { $seedBetween.Add((Record "VG$i" "jG$i" $false)) }
$seedBetween.Add((Lease 'VG1' 'jG1' $true))
$seedBetween.Add((Lease 'VG2' 'jG2-other' $true))
$seedBetween.Add((Lease 'VA1' 'jH1' $true))
$partsBetween = @{ migrationState = 'LEASES_NOT_YET_RETIRED'; originalRecords = 22; claimBackfills = 19; leaseBackfills = 14; expected = 55 }

# --- 1b's seed: before batch 8, no record table yet --------------------------------------------------------------
#   6 claims with their unreleased lease                                    backfill 6
#   3 claims whose own journey's lease is released                           backfill 3, lease 0 (step 2 wrote one)
#   7 released leases on 2 vehicles with no claim                            lease 7
#   total: records 0, backfill 9, lease 7, expected 16
$seedBefore = [System.Collections.Generic.List[object]]::new()
foreach ($i in 1..6) { $seedBefore.Add((Claim "VP$i" "jP$i")); $seedBefore.Add((Lease "VP$i" "jP$i" $false)) }
foreach ($i in 1..3) { $seedBefore.Add((Claim "VQ$i" "jQ$i")); $seedBefore.Add((Lease "VQ$i" "jQ$i" $true)) }
foreach ($i in 1..7) { $seedBefore.Add((Lease "VR$($i % 2)" "jR$i" $true)) }
$partsBefore = @{ migrationState = 'RECORD_TABLE_NOT_YET_CREATED'; originalRecords = 0; claimBackfills = 9; leaseBackfills = 7; expected = 16 }

# --- 0. the expectation alone, from in-memory baselines (no build, also under -SkipStores) ------------------------
# What -SkipStores could not catch before review: an expectation that, say, forgets step 2's records when it looks for
# step 3's, gives the wrong parts for 1a here without a store being made.
foreach ($case in @(
        @{ Label = 'in memory, leases and records present'; Seed = $seedBetween; RecordTable = $true; Parts = $partsBetween },
        @{ Label = 'in memory, before batch 8'; Seed = $seedBefore; RecordTable = $false; Parts = $partsBefore })) {
    $expectation = $null
    $thrown = $null
    try { $expectation = Get-ExpectedClaimRecordCount -Baseline (ConvertTo-MemoryBaseline $case.Seed $case.RecordTable) }
    catch { $thrown = $_.Exception.Message }
    Test-Parts $case.Label $expectation $thrown $case.Parts
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
    Test-Parts $Label $expectation $thrown $Parts
    Update-Store $Path $null
    $actual = Get-RecordCount $Path
    $expected = ${expectation}?['expected']
    Check "${Label}: the real migration leaves the count the runner expected" ($null -ne $expected -and [long]$expected -eq $actual) `
        "expected $expected, the migration left $actual"
    # The runner's criterion before control-server#544 compared the count after the first host with the count after the
    # second. Here that is the same file read twice with nothing in between, so it is true by construction and shows
    # nothing about the old runner by itself. That the old runner stays green over a short backfill is section 3 against
    # the old statements (evidence red/3, dc15b3ca: 41 against 41 judged true).
    $oldCriterion = (Get-RecordCount $Path) -eq $actual
    Write-Host ("     control: afterProbe = final, the same file read twice, is {0} by construction; see section 3" -f
        $(if ($oldCriterion) { 'PASS' } else { 'FAIL' }))
}

if (-not $SkipStores) {
New-Item -ItemType Directory -Path $work | Out-Null
try {
    Invoke-Dotnet @('tool', 'restore') 'dotnet tool restore'
    Invoke-Dotnet @('build', $hostProject, '-c', 'Debug', '--nologo', '-v', 'q') 'dotnet build ControlServer.Host'
    $hostOutput = Join-Path $hostProject 'bin/Debug/net8.0/win-x64'
    Add-Type -Path (Join-Path $hostOutput 'Microsoft.Data.Sqlite.dll')

    # --- 1a. leases and records both present: between the two batch-8 migrations (seed and parts above) ----------
    $between = Join-Path $work 'between-batch8-migrations.db'
    Update-Store $between $beforeRetirement
    Invoke-StoreSql $between (ConvertTo-SeedSql $seedBetween)
    Test-Expectation 'leases and records present' $between $partsBetween

    # --- 1b. before batch 8: no record table yet (seed and parts above) -------------------------------------------
    $beforeRecords = Join-Path $work 'before-batch8.db'
    Update-Store $beforeRecords $beforeBatch8
    Invoke-StoreSql $beforeRecords (ConvertTo-SeedSql $seedBefore)
    Test-Expectation 'before batch 8, no record table' $beforeRecords $partsBefore

    # --- 1c. already migrated: the lease table is gone, nothing is backfilled ------------------------------------
    # 1a's store after its migration holds the 55 records 1a's hand computation calls for; a constant, not 1a's
    # measured count, so that a migration that wrote the wrong number does not also move what 1c expects.
    $migrated = Join-Path $work 'already-migrated.db'
    Copy-Store $between $migrated
    Test-Expectation 'already migrated' $migrated @{
        migrationState = 'ALREADY_RETIRED'; originalRecords = 55; claimBackfills = 0; leaseBackfills = 0; expected = 55 }

    # --- 2. which tables may be missing ---------------------------------------------------------------------
    # Each read is of a copy of the migrated store with one table renamed away. Only the claim record table, and only
    # in the baseline read, may be missing; every other missing table is a refusal that names it, where the runner
    # before control-server#544 counted it as 0 and went on. The allowed case is 1b's baseline, a store from before
    # batch 8: it has its dispatch leases. A migrated store has none, so with its record table renamed away it is a
    # store that cannot exist (neither the records nor the leases they would be backfilled from), and the refusal
    # names the leases.
    function Test-MissingTable([string]$Label, [string]$Table, [switch]$AfterMigration, [string]$RefusedAs = $Table) {
        $copy = Join-Path $work ("missing-$Table-$([guid]::NewGuid().ToString('n').Substring(0, 6)).db")
        Copy-Store $migrated $copy
        Invoke-StoreSql $copy "ALTER TABLE `"$Table`" RENAME TO `"${Table}_RenamedBySelfCheck`";"
        $script:controlDatabasePath = $copy
        $read = $null
        $thrown = $null
        try { $read = if ($AfterMigration) { Read-ControlDatabase -AfterMigration } else { Read-ControlDatabase } }
        catch { $thrown = $_.Exception.Message }
        $counted = if ($null -ne $read -and $read.counts.Contains($Table)) { " counted $Table = $($read.counts[$Table]) and went on" } else { '' }
        # The runner's own refusal, not whatever a later query happens to throw: SQLite's "no such table: X" names the
        # table too, but only for the tables the read also selects rows from, and only by accident.
        Check "${Label}: a missing $Table is refused, naming $RefusedAs" `
            ($null -ne $thrown -and $thrown.Contains("CONTROL_TABLE_MISSING: $RefusedAs ", [StringComparison]::Ordinal)) `
            "$(if ($null -eq $thrown) { 'Read-ControlDatabase did not throw;' + $counted } else { "it threw, but not the runner's own refusal naming the table: $thrown" })"
    }
    foreach ($table in 'OrderIntents', 'RiotDispatchAuditEvents', 'AcceptedDemands', 'StationOperations', 'OperationResults',
                       'UnloadBatches', 'StopClosures', 'TransportDemandCompletions', 'ProtocolInbox') {
        Test-MissingTable 'baseline' $table
        Test-MissingTable 'after migration' $table -AfterMigration
    }
    Test-MissingTable 'baseline, no leases either' 'VehiclePurposeClaimRecords' -RefusedAs 'VehicleDispatchLeases'
    Test-MissingTable 'after migration' 'VehiclePurposeClaimRecords' -AfterMigration
    # What the backfill is worked out from: without the claims it cannot be, so the baseline refuses rather than
    # expecting a count it never computed.
    Test-MissingTable 'baseline' 'VehiclePurposeClaims'
} finally {
    [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()
    if ($KeepWork) { Write-Host "Stores kept in $work" }
    else { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
}

# --- 3. the judgement uses the expectation --------------------------------------------------------------------
# The runner's own statements, taken from its AST and run over made-up reads: $externalTables, the backfill judgement
# and noExternalSideEffectsPass, in that order. The second case is the one the previous criterion missed: the first
# host's migration left fewer records than the baseline calls for, and nothing changed after it.
$judgementNames = 'externalTables', 'claimRecordsMatchTheBackfillPass', 'noExternalSideEffectsPass'
$judgements = foreach ($name in $judgementNames) {
    $found = @($runnerAst.EndBlock.Statements | Where-Object {
            $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq "`$$name" })
    if ($found.Count -eq 1) { $found[0].Extent.Text } else { $null }
}
Check 'the runner assigns each judgement once, at top level' (@($judgements | Where-Object { $null -eq $_ }).Count -eq 0) `
    "missing or repeated: $(@(for ($i = 0; $i -lt $judgementNames.Count; $i++) { if ($null -eq @($judgements)[$i]) { $judgementNames[$i] } }) -join ', ')"
function Get-Judgement([long]$ExpectedCount, [long]$AfterProbeCount, [long]$FinalCount) {
    $counts = { param($records) [ordered]@{ OrderIntents = 2; RiotDispatchAuditEvents = 10; AcceptedDemands = 1
                                            StationOperations = 2; VehiclePurposeClaimRecords = $records } }
    $baseline = [ordered]@{ counts = (& $counts 0) }
    $afterProbe = [ordered]@{ counts = (& $counts $AfterProbeCount) }
    $final = [ordered]@{ counts = (& $counts $FinalCount) }
    $claimRecordExpectation = [ordered]@{ expected = $ExpectedCount }
    $claimRecordsMatchTheBackfillPass = $null
    $noExternalSideEffectsPass = $null
    foreach ($statement in $judgements) { if ($null -ne $statement) { . ([scriptblock]::Create($statement)) } }
    return [pscustomobject]@{
        backfill = $claimRecordsMatchTheBackfillPass
        noExternal = $noExternalSideEffectsPass
        previous = $AfterProbeCount -eq $FinalCount
    }
}
$matching = Get-Judgement -ExpectedCount 55 -AfterProbeCount 55 -FinalCount 55
Check 'the migration left what the baseline calls for: both judgements pass' ($matching.backfill -eq $true -and $matching.noExternal -eq $true) "$($matching | ConvertTo-Json -Compress)"
$short = Get-Judgement -ExpectedCount 55 -AfterProbeCount 41 -FinalCount 41
Check 'the migration left 14 fewer than the baseline calls for: both judgements fail' ($short.backfill -eq $false -and $short.noExternal -eq $false) "$($short | ConvertTo-Json -Compress)"
Write-Host ("     control: the previous criterion (afterProbe = final) is {0} for that store" -f $(if ($short.previous) { 'PASS' } else { 'FAIL' }))
# afterProbe against final is vehicleClaimRecordSurvivesTheHostRestart's alone now; this judgement no longer repeats it.
$grewAfterRestart = Get-Judgement -ExpectedCount 55 -AfterProbeCount 55 -FinalCount 56
Check 'a record added after the restart is not this judgement''s to catch' ($grewAfterRestart.noExternal -eq $true) "$($grewAfterRestart | ConvertTo-Json -Compress)"

# The evidence says what the judgement said: matches is the judgement variable, not a value that could drift from it
# (review of PR #606 set it to $true and every other self-check stayed green), and both branches of the store
# provenance record carry the record, so every gate result has it whichever store the run restored.
function Get-TopLevelAssignment([string]$Name) {
    return @($runnerAst.EndBlock.Statements | Where-Object {
            $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq "`$$Name" })
}
function Get-HashtableValue($Hashtable, [string]$Key) {
    $pair = @($Hashtable.KeyValuePairs | Where-Object { $_.Item1.Extent.Text -eq $Key })
    if ($pair.Count -ne 1) { return $null }
    return $pair[0].Item2.Extent.Text.Trim()
}
$backfillRecord = Get-TopLevelAssignment 'claimRecordBackfill'
$backfillTable = if ($backfillRecord.Count -eq 1) {
    @($backfillRecord[0].Right.FindAll({ param($node) $node -is [System.Management.Automation.Language.HashtableAst] }, $true)) | Select-Object -First 1
} else { $null }
$matchesValue = if ($null -ne $backfillTable) { Get-HashtableValue $backfillTable 'matches' } else { $null }
Check 'the evidence record''s matches is the judgement itself' ($matchesValue -ceq '$claimRecordsMatchTheBackfillPass') `
    "$($backfillRecord.Count) assignment(s) of `$claimRecordBackfill; matches = $matchesValue"
$provenance = Get-TopLevelAssignment 'fieldStoreProvenanceRecord'
$branches = if ($provenance.Count -eq 1) {
    @($provenance[0].Right.FindAll({
                param($node) $node -is [System.Management.Automation.Language.HashtableAst] -and
                    @($node.KeyValuePairs | Where-Object { $_.Item1.Extent.Text -eq 'storeSource' }).Count -eq 1 }, $true))
} else { @() }
$carrying = @($branches | Where-Object { (Get-HashtableValue $_ 'claimRecordBackfill') -ceq '$claimRecordBackfill' })
Check 'both store provenance branches carry claimRecordBackfill' ($branches.Count -eq 2 -and $carrying.Count -eq 2) `
    "$($branches.Count) branch(es), $($carrying.Count) carrying it"

if ($failures.Count -gt 0) {
    Write-Host "DemandBearingClaimRecordBackfill self-check: $($failures.Count) check(s) came out the other way."
    exit 1
}
Write-Host 'DemandBearingClaimRecordBackfill self-check: every check as expected.'
