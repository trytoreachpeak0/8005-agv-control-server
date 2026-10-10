#Requires -Version 7

<#
A one-off probe for the batch-8 exit (control-server#393), asked for by the coordinator on 2026-10-09: does a
database migrated by the integration branch fp/v2-impl -- which already applied
20261008052643_UnreleasableNotReconciledBlocksBeforeUpgrade -- pick up the two v3 migrations whose ids sort
before it (20260930012829_Batch8SlotFaultDeclarations, 20260930041750_Batch8RecoverySurface) when the v3 build
migrates it, and does it end with the same schema as a database the v3 build creates from nothing?

It drives the real path a deployment takes, not a test fixture: each build's own ControlServer.Host with
--migrate-only, which calls DbContext.Database.MigrateAsync() (Program.cs EnsureDatabaseAsync) and exits.
The database location is forced through ConnectionStrings__ControlServer to a new directory, so the
appsettings default (%ProgramData%/8005/ControlServer/data) is never touched.

  (a) old  = fp/v2-impl build, migrate a new database           -> the database factory01's v2 instance has today
  (b) old  = v3 build, migrate that same database               -> the upgrade
  (c) new  = v3 build, migrate a second new database            -> the reference
Asserts: after (a) neither 0930 migration is in __EFMigrationsHistory and 1008 is; after (b) both are, the
history equals (c)'s as a set, and the schema dump (sqlite_master, table_xinfo, index_list/index_xinfo,
foreign_key_list) of (b) equals (c)'s byte for byte. Exit 0 on PASS, 1 on FAIL.
#>
param(
    [Parameter(Mandatory)][string]$OldHost,   # fp/v2-impl build's ControlServer.Host.exe
    [Parameter(Mandatory)][string]$NewHost,   # v3 build's ControlServer.Host.exe
    [Parameter(Mandatory)][string]$OutputRoot # must not exist
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSStyle.OutputRendering = 'PlainText'

if (Test-Path -LiteralPath $OutputRoot) { throw "OutputRoot $OutputRoot exists; give a new directory" }
$OutputRoot = (New-Item -ItemType Directory -Path $OutputRoot).FullName
$dumper = Join-Path $PSScriptRoot 'dump_schema.py'

function Invoke-MigrateOnly([string]$HostExe, [string]$DataDirectory, [string]$Label) {
    $null = New-Item -ItemType Directory -Path $DataDirectory -Force
    $db = Join-Path $DataDirectory 'controlserver.db'
    $env:ConnectionStrings__ControlServer = "Data Source=$db"
    $log = Join-Path $OutputRoot "$Label.log"
    try {
        Push-Location (Split-Path -Parent $HostExe)
        & $HostExe --migrate-only *> $log
        $code = $LASTEXITCODE
    } finally {
        Pop-Location
        Remove-Item Env:ConnectionStrings__ControlServer
    }
    "$Label exit=$code db=$db" | Tee-Object -FilePath (Join-Path $OutputRoot 'steps.txt') -Append | Write-Host
    if ($code -ne 0) { throw "$Label --migrate-only exited $code; see $log" }
    if (-not (Test-Path -LiteralPath $db)) { throw "$Label wrote no database at $db; the connection-string override did not take" }
    $db
}

function Get-Dump([string]$Db, [string]$Label) {
    $out = Join-Path $OutputRoot "$Label.schema.json"
    & python -I $dumper $Db $out
    if ($LASTEXITCODE -ne 0) { throw "dump of $Db failed" }
    $out
}

$failures = [Collections.Generic.List[string]]::new()
$m1008 = '20261008052643_UnreleasableNotReconciledBlocksBeforeUpgrade'
$v3 = @('20260930012829_Batch8SlotFaultDeclarations', '20260930041750_Batch8RecoverySurface')

$upgradeDb = Invoke-MigrateOnly $OldHost (Join-Path $OutputRoot 'db-upgrade') 'a-old-build-new-db'
$a = Get-Content -LiteralPath (Get-Dump $upgradeDb 'a-after-old-build') -Raw | ConvertFrom-Json
$aIds = @($a.migrationHistory | ForEach-Object { $_[0] })
Write-Host "(a) history: $($aIds.Count) migrations, last applied $($a.appliedOrder[-1])"
if ($aIds -notcontains $m1008) { $failures.Add("(a) $m1008 not applied by the fp/v2-impl build") }
foreach ($id in $v3) { if ($aIds -contains $id) { $failures.Add("(a) $id already applied by the fp/v2-impl build; the probe is not testing an upgrade") } }

$null = Invoke-MigrateOnly $NewHost (Join-Path $OutputRoot 'db-upgrade') 'b-new-build-same-db'
$bPath = Get-Dump $upgradeDb 'b-after-upgrade'
$b = Get-Content -LiteralPath $bPath -Raw | ConvertFrom-Json

$referenceDb = Invoke-MigrateOnly $NewHost (Join-Path $OutputRoot 'db-reference') 'c-new-build-new-db'
$cPath = Get-Dump $referenceDb 'c-reference'
$c = Get-Content -LiteralPath $cPath -Raw | ConvertFrom-Json

$bIds = @($b.migrationHistory | ForEach-Object { $_[0] })
$cIds = @($c.migrationHistory | ForEach-Object { $_[0] })
foreach ($id in $v3) { if ($bIds -notcontains $id) { $failures.Add("(b) $id missing from __EFMigrationsHistory after the upgrade") } }
$onlyB = @($bIds | Where-Object { $cIds -notcontains $_ }); $onlyC = @($cIds | Where-Object { $bIds -notcontains $_ })
if ($onlyB -or $onlyC) { $failures.Add("history differs: only upgrade [$($onlyB -join ', ')], only reference [$($onlyC -join ', ')]") }
Write-Host "(b) applied order tail: $(($b.appliedOrder | Select-Object -Last 3) -join ' -> ')"
Write-Host "(c) applied order tail: $(($c.appliedOrder | Select-Object -Last 3) -join ' -> ')"

$bSchema = $b.schema | ConvertTo-Json -Depth 20 -Compress
$cSchema = $c.schema | ConvertTo-Json -Depth 20 -Compress
if ($bSchema -cne $cSchema) {
    $failures.Add('schema of the upgraded database differs from the reference')
    foreach ($table in @($c.schema.tables.PSObject.Properties.Name) + @($b.schema.tables.PSObject.Properties.Name) | Sort-Object -Unique) {
        $bt = $b.schema.tables.$table | ConvertTo-Json -Depth 20 -Compress
        $ct = $c.schema.tables.$table | ConvertTo-Json -Depth 20 -Compress
        if ($bt -cne $ct) { $failures.Add("  table $table differs") }
    }
}
$tableCount = @($c.schema.tables.PSObject.Properties).Count
$indexCount = @($c.schema.tables.PSObject.Properties | ForEach-Object { @($_.Value.indexes.PSObject.Properties).Count } | Measure-Object -Sum).Sum
Write-Host "reference: $($cIds.Count) migrations, $tableCount tables, $indexCount indexes; upgrade: $($bIds.Count) migrations"

$verdict = $failures.Count -eq 0 ? 'PASS' : 'FAIL'
[ordered]@{
    verdict = $verdict
    oldHost = $OldHost
    newHost = $NewHost
    afterOldBuildMigrations = $aIds.Count
    afterUpgradeMigrations = $bIds.Count
    referenceMigrations = $cIds.Count
    upgradeAppliedOrderTail = @($b.appliedOrder | Select-Object -Last 3)
    referenceTables = $tableCount
    referenceIndexes = $indexCount
    failures = @($failures)
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputRoot 'probe-result.json') -Encoding utf8NoBOM
$failures | ForEach-Object { Write-Host "FAIL: $_" }
Write-Host $verdict
exit ($failures.Count -eq 0 ? 0 : 1)
