#Requires -Version 7
<#
.SYNOPSIS
    Self-check of L2ReadSnapshot.psm1 against real SQLite: reads inside one block see one commit, and the block always ends.

.DESCRIPTION
    A few seconds, no rig. It needs the Microsoft.Data.Sqlite that the ControlServer build ships, so it runs after a
    build: -HostDirectory defaults to the Release output Invoke-L2Scenario.ps1 uses, and a missing build is a failure,
    not a skip. The reader is opened with Open-L2Database, the same connection string every scenario reads through; the
    writer is a second connection on the same WAL file, standing in for the server.

    What is pinned (control-server#510):
    - the problem exists: two plain reads on that connection, with a commit between them, see two different states;
    - inside one block they see the same state, across tables, while the writer commits in between, and the writer is
      not held up by the open block (WAL);
    - the next block sees what was committed between the two -- the case that fails when a transaction is left open,
      since the connection would then read its first snapshot for ever and a wait would time out on a changed value;
    - the same after a block that threw, which is what the finally is for;
    - a nested block throws instead of ending its caller's snapshot early.
#>
[CmdletBinding()]
param(
    [string]$HostDirectory = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'src/ControlServer.Host/bin/Release/net8.0/win-x64')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'L2ReadSnapshot.psm1') -Force

if (-not (Test-Path -LiteralPath (Join-Path $HostDirectory 'Microsoft.Data.Sqlite.dll'))) {
    Write-Host "L2ReadSnapshot self-check: no Microsoft.Data.Sqlite.dll under $HostDirectory. Build ControlServer.Host (Release) first."
    exit 1
}

$results = [System.Collections.Generic.List[object]]::new()
function Add-Case([string]$Name, [bool]$Ok, [string]$Actual) {
    $results.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Actual = $Actual })
}

$root = Join-Path ([IO.Path]::GetTempPath()) ("l2-read-snapshot-" + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
$path = Join-Path $root 'store.db'
$writer = $null
$reader = $null
# The writer needs the assemblies before Open-L2Database has loaded them: its Mode=ReadOnly cannot create the file.
foreach ($assembly in @('SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll',
                        'SQLitePCLRaw.batteries_v2.dll', 'Microsoft.Data.Sqlite.dll')) {
    Add-Type -LiteralPath (Join-Path $HostDirectory $assembly) -ErrorAction SilentlyContinue
}
try { [SQLitePCL.Batteries_V2]::Init() } catch { }
try {
    $writer =[Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$path;Mode=ReadWriteCreate;Pooling=False;Default Timeout=2")
    $writer.Open()
    function Write-Store([string]$Sql) {
        $command = $writer.CreateCommand()
        $command.CommandText = $Sql
        $null = $command.ExecuteNonQuery()
        $command.Dispose()
    }
    Write-Store "PRAGMA journal_mode = 'wal'"
    Write-Store 'CREATE TABLE Cycles (State TEXT NOT NULL); CREATE TABLE Holds (Trig TEXT NOT NULL)'
    Write-Store "INSERT INTO Cycles VALUES ('EN_ROUTE')"
    $reader = Open-L2Database -HostDirectory $HostDirectory -DatabasePath $path

    # The server's commit: the cycle and the hold in one transaction, as ConfirmUnableToChargeAsync saves them.
    $step = 0
    function Commit-Next {
        $script:step++
        Write-Store ("BEGIN; UPDATE Cycles SET State = 'S$script:step'; INSERT INTO Holds VALUES ('H$script:step'); COMMIT")
    }
    function Read-Cycle { [string](Invoke-L2Query -Connection $reader -Sql 'SELECT State FROM Cycles')[0].State }
    function Read-Holds { [int](Invoke-L2Query -Connection $reader -Sql 'SELECT COUNT(*) AS N FROM Holds')[0].N }
    # A block that throws is an answer, not the end of the check: every case after it still runs and says what it saw.
    function Read-Block([scriptblock]$Read) {
        try { Invoke-L2ReadSnapshot -Connection $reader -Read $Read } catch { "threw: $($_.Exception.Message)" }
    }

    # ------------------------------------------------------------ the problem: plain reads straddle a commit
    $cycle = Read-Cycle
    Commit-Next
    $holds = Read-Holds
    Add-Case 'without a block: a commit between two reads gives the old cycle next to the new hold (the CI red)' (
        $cycle -ceq 'EN_ROUTE' -and $holds -eq 1) "$cycle | holds=$holds"

    # ------------------------------------------------------------ one block, one state
    $seen = Read-Block {
        $first = Read-Cycle
        Commit-Next
        "$first | $(Read-Holds) | $(Read-Cycle)"
    }
    Add-Case 'inside a block: a commit between the reads is seen by none of them, across tables' (
        $seen -ceq 'S1 | 1 | S1') $seen
    Add-Case 'the writer committed while the block was open (WAL: a read transaction holds no writer up)' (
        $step -eq 2) "step=$step"

    # ------------------------------------------------------------ the next block sees it
    $after = Read-Block { "$(Read-Cycle) | $(Read-Holds)" }
    Add-Case 'the next block sees what was committed between the two (the transaction was ended)' (
        $after -ceq 'S2 | 2') $after

    # ------------------------------------------------------------ a block that throws still ends its transaction
    $threw = Read-Block { $null = Read-Cycle; throw 'probe failed' }
    Commit-Next
    $afterThrow = Read-Block { "$(Read-Cycle) | $(Read-Holds)" }
    Add-Case 'a throwing block rethrows its own error' ($threw -ceq 'threw: probe failed') $threw
    Add-Case 'after a throwing block, the next block opens and sees the newer commit' ($afterThrow -ceq 'S3 | 3') $afterThrow
    $plain = Read-Cycle
    Commit-Next
    $plainNext = Read-Cycle
    Add-Case 'after a block, plain reads are back in autocommit and see each commit' (
        $plain -ceq 'S3' -and $plainNext -ceq 'S4') "$plain -> $plainNext"

    # ------------------------------------------------------------ nesting is refused, and does not end the outer block
    $nested = Read-Block {
        $before = Read-Cycle
        $inner = try { Invoke-L2ReadSnapshot -Connection $reader -Read { 'ran' } } catch { 'refused' }
        Commit-Next
        "$inner | $before | $(Read-Cycle)"
    }
    Add-Case 'a nested block is refused and the outer snapshot holds after it' ($nested -ceq 'refused | S4 | S4') $nested
    $afterNested = Read-Block { Read-Cycle }
    Add-Case 'after the outer block, the next one sees the commit made inside it' ($afterNested -ceq 'S5') $afterNested
} finally {
    if ($null -ne $reader) { $reader.Dispose() }
    if ($null -ne $writer) { $writer.Dispose() }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

$bad = 0
foreach ($result in $results) {
    if (-not $result.Ok) { $bad++ }
    Write-Host ("{0}  {1} -> {2}" -f ($result.Ok ? 'ok  ' : 'BAD '), $result.Name, $result.Actual)
}
if ($results.Count -ne 9) {
    Write-Host "L2ReadSnapshot self-check: $($results.Count) cases ran, 9 expected."
    exit 1
}
if ($bad -gt 0) {
    Write-Host "L2ReadSnapshot self-check: $bad of $($results.Count) cases came out the wrong way."
    exit 1
}
Write-Host "L2ReadSnapshot self-check: all $($results.Count) cases as expected."
