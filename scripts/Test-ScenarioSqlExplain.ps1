#Requires -Version 7
<#
.SYNOPSIS
    Offline check of the literal SQL the L2 and G3 scripts send, against a freshly migrated temporary database, and its
    self-check (control-server#583).

.DESCRIPTION
    No rig, no window, no fixed port. The SQL every scenario sends through Invoke-L2Query (and the readers that pass their
    -Sql on to it, Get-G3Scalar among them) is otherwise compiled for the first time by SQLite on the rig, so a misspelt
    column or table reads as a red scenario hours later. Here every literal statement is compiled with EXPLAIN against a
    database the built ControlServer.Host migrated with --migrate-only into %TEMP%.

    Only literal SQL is checked: a statement with $ interpolation, or one built in a variable, is skipped and counted. That
    is most of them (they carry a demand id), and the count is printed so the blind spot is visible.

    The temporary database: the path is asserted to be under the system temp directory and nowhere near %ProgramData% or
    a ControlServer install, and is passed to the host as ConnectionStrings__ControlServer. --migrate-only with no
    connection string migrates the MVP production database (%ProgramData%\8005\ControlServer\data\controlserver.db), so
    the check also asserts that file is exactly as it was before and that the temporary one was created.

    Needs ControlServer.Host built in Release (for the host and for Microsoft.Data.Sqlite). -Build builds it first;
    without -Build and without a build it exits 2, never 0, so a skip cannot pass.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/Test-ScenarioSqlExplain.ps1 -Build
#>
[CmdletBinding()]
param(
    [string]$ScriptRoot = $PSScriptRoot,

    [string]$HostDirectory = (Join-Path $PSScriptRoot '..\src\ControlServer.Host\bin\Release\net8.0\win-x64'),

    # Scan these files instead of every script under ScriptRoot (the lower bounds apply only to the full scan).
    [string[]]$Path,

    [switch]$Build,

    [switch]$SkipSelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScriptStaticChecks.psm1') -Force

$cases = [System.Collections.Generic.List[object]]::new()
function Add-Case([string]$Name, [bool]$Ok, [string]$Actual) {
    $cases.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Actual = $Actual })
}
function Format-Sql([object[]]$Statements) {
    return (@($Statements | ForEach-Object { "$([IO.Path]::GetFileName($_.File)):$($_.Line) [$($_.Sql)]" }) -join ' | ')
}

$allScripts = @(Get-ChildItem -LiteralPath $ScriptRoot -Recurse -File -Include '*.ps1', '*.psm1' | ForEach-Object FullName)
# The self-checks build their own tables; their SQL is not the server's.
$serverSql = @($allScripts | Where-Object { [IO.Path]::GetFileName($_) -notlike 'Test-*.ps1' -and [IO.Path]::GetFileName($_) -ne 'ScriptStaticChecks.psm1' })
$sinks = Get-SqlSinkCatalog -Path $serverSql

$scratch = Join-Path ([IO.Path]::GetTempPath()) "scenario-sql-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $scratch -Force
try {
    if (-not $SkipSelfTest) {
        function New-Fixture([string]$Name, [string]$Body) {
            $file = Join-Path $scratch $Name
            Set-Content -LiteralPath $file -Encoding utf8NoBOM -Value $Body
            return $file
        }

        # --- extraction, no database ----------------------------------------------------------------------------------
        Add-Case 'sinks: Invoke-L2Query, and Get-G3Scalar / Get-G3Count derived from G3RecoveryCommon.ps1' (
            $sinks.ContainsKey('Invoke-L2Query') -and $sinks['Get-G3Scalar'] -eq 1 -and $sinks['Get-G3Count'] -eq 1) (($sinks.Keys | Sort-Object) -join ',')

        $fixture = New-Fixture 'sql.ps1' @'
$a = Invoke-L2Query -Connection $c -Sql "SELECT DemandId FROM AcceptedDemands"
$b = Get-G3Scalar $connection 'SELECT Stage AS Value FROM JourneyRuntimes'
$d = Invoke-L2Query -Connection $c -Sql ("SELECT MessageId FROM ProtocolInbox " +
    "WHERE MessageType = 'X'")
$e = Invoke-L2Query -Connection $c -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$demandId'"
$sql = 'SELECT 1'
$f = Invoke-L2Query -Connection $c -Sql $sql
function Get-Count([string]$sql) { return [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N }
$g = Get-Count 'SELECT COUNT(*) AS N FROM OrderIntents'
'@
        $found = Get-ScenarioLiteralSql -Path $fixture -Sinks $sinks
        $lines = @($found.Literal | ForEach-Object Line) -join ','
        Add-Case 'extract: named, positional, folded + and a file-local wrapper are literal' ($lines -eq '1,2,3,9') (Format-Sql $found.Literal)
        Add-Case 'extract: the folded statement is joined' (@($found.Literal | Where-Object { $_.Line -eq 3 -and $_.Sql -eq "SELECT MessageId FROM ProtocolInbox WHERE MessageType = 'X'" }).Count -eq 1) (Format-Sql $found.Literal)
        # Lines 5 and 7. The wrapper's own forwarding of $sql on line 8 is neither: it is not a statement.
        Add-Case 'extract: interpolated and variable SQL are skipped and counted' ($found.NonLiteral -eq 2) "$($found.NonLiteral)"

        # --- the temporary database's path ----------------------------------------------------------------------------
        $refusedProgramData = try { Assert-TempDatabasePath (Join-Path $env:ProgramData '8005\ControlServer\data\controlserver.db'); '' } catch { "$_" }
        Add-Case 'temp database: a path under %ProgramData% is refused' ($refusedProgramData -ne '') $refusedProgramData
        $refusedInstall = try { Assert-TempDatabasePath 'C:\Program Files\8005 AGV\ControlServer\data\controlserver.db'; '' } catch { "$_" }
        Add-Case 'temp database: a path outside the temp directory is refused' ($refusedInstall -ne '') $refusedInstall
        $accepted = try { Assert-TempDatabasePath (Join-Path $scratch 'db\controlserver.db'); 'ok' } catch { "$_" }
        Add-Case 'temp database: a path under the temp directory is accepted' ($accepted -eq 'ok') $accepted
    }

    # --- the migrated database ------------------------------------------------------------------------------------------
    $hostExe = Join-Path $HostDirectory 'ControlServer.Host.exe'
    if ($Build) {
        dotnet build (Join-Path $PSScriptRoot '..\src\ControlServer.Host\ControlServer.Host.csproj') -c Release -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "dotnet build of ControlServer.Host failed ($LASTEXITCODE)." }
    }
    if (-not (Test-Path -LiteralPath $hostExe)) {
        foreach ($c in $cases) { Write-Host ("{0}  {1}{2}" -f ($c.Ok ? 'ok  ' : 'FAIL'), $c.Name, ($c.Ok ? '' : " -- $($c.Actual)")) }
        Write-Host "No ${hostExe}: build src/ControlServer.Host -c Release, or pass -Build. No SQL was compiled."
        exit (@($cases | Where-Object { -not $_.Ok }).Count -gt 0 ? 1 : 2)
    }
    $database = Join-Path $scratch 'db\controlserver.db'
    $migration = New-MigratedTempDatabase -HostDirectory $HostDirectory -DatabasePath $database
    Write-Host "migrated: $database ($($migration.Tables) tables, $([int]$migration.Elapsed.TotalSeconds) s); production database untouched: $($migration.ProductionUntouched)"
    Add-Case 'temp database: created, migrated, and the default database left as it was' (
        $migration.Tables -ge 20 -and $migration.ProductionUntouched) "$($migration.Tables) tables; untouched $($migration.ProductionUntouched)"

    $connection = Open-ExplainConnection -HostDirectory $HostDirectory -DatabasePath $database
    try {
        if (-not $SkipSelfTest) {
            $statements = @(
                [pscustomobject]@{ File = 'fixture'; Line = 1; Sql = 'SELECT DemandId FROM AcceptedDemands' }
                [pscustomobject]@{ File = 'fixture'; Line = 2; Sql = 'SELECT DemandIdd FROM AcceptedDemands' }
                [pscustomobject]@{ File = 'fixture'; Line = 3; Sql = 'SELECT 1 FROM NoSuchTable' }
            )
            $explained = Test-SqlExplain -Connection $connection -Statement $statements
            Add-Case 'explain: a misspelt column and a missing table are reported, a good statement is not' (
                (@($explained | ForEach-Object Line) -join ',') -eq '2,3') ((@($explained | ForEach-Object { "$($_.Line) $($_.Message)" })) -join ' | ')
        }

        $full = -not $Path
        $targets = if ($full) { $serverSql } else { @($Path | ForEach-Object { (Resolve-Path -LiteralPath $_).ProviderPath }) }
        $found = Get-ScenarioLiteralSql -Path $targets -Sinks $sinks
        $explained = @(Test-SqlExplain -Connection $connection -Statement $found.Literal)
        Write-Host ("sql: {0} files, {1} literal statements compiled, {2} skipped as interpolated or built elsewhere, {3} sinks ({4})" -f
            $targets.Count, @($found.Literal).Count, $found.NonLiteral, $sinks.Count, (($sinks.Keys | Sort-Object) -join ', '))
        foreach ($f in $explained) { Write-Host "  SQL  $($f.File):$($f.Line) $($f.Message)`n       $($f.Sql)" }
        Add-Case 'repository: every literal statement compiles against the migrated schema' ($explained.Count -eq 0) "$($explained.Count) failed"
        if ($full) {
            # 168 literal and 693 skipped on fp/v2-impl@da6e40e2.
            Add-Case 'repository: at least 150 literal statements were compiled' (@($found.Literal).Count -ge 150) "$(@($found.Literal).Count)"
        }
    } finally {
        $connection.Dispose()
        [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()
    }
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

$failed = @($cases | Where-Object { -not $_.Ok })
foreach ($c in $cases) { Write-Host ("{0}  {1}{2}" -f ($c.Ok ? 'ok  ' : 'FAIL'), $c.Name, ($c.Ok ? '' : " -- $($c.Actual)")) }
Write-Host "$($cases.Count - $failed.Count)/$($cases.Count) passed"
exit ($failed.Count -eq 0 ? 0 : 1)
