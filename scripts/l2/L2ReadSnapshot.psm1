#Requires -Version 7

<#
Invoke-L2ReadSnapshot: run several reads of the server's database inside one read transaction, so they all see the
same committed state.

Added for control-server#510 in its own file, the way L2ConditionOrLast.psm1 was. A scenario that wants it imports it
next to L2.psm1:

    Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ReadSnapshot.psm1') -Force

Why it exists. The connection Open-L2Database hands out runs every statement in autocommit, so each SELECT sees the
database as it is at that statement. A probe that reads the charging cycle, then the holds, then the purpose claim, is
three snapshots. When the server commits between the first and the second, the probe returns the cycle from before the
commit and everything else from after it -- a state that never existed in the database. A wait whose Until looks only at
a later segment then stops on that line, and the criterion compares a mix: CI run 37595371819 read "EN_ROUTE ACTIVE"
next to a pause written in the same SaveChanges as "UNABLE_TO_CHARGE CLEARING". "Written in one commit" (README item
14) is only safe to read in pieces when what is read first is what was waited for; inside this block it is safe in any
order.

The server's store is in WAL mode (ControlServerSqlite.cs), so a read transaction takes no lock the writer waits on: the
server keeps committing while the block runs, and the block keeps seeing the state of its first read. What an open read
transaction does block is the WAL checkpoint, and a transaction left open would make every later read on this connection
return that same old state for ever -- a wait would then time out on a value that has long changed. So COMMIT is in a
finally, and Test-L2ReadSnapshot.ps1 pins both: a later call sees a write made between two calls, including after a
block that threw.

Plain BEGIN/COMMIT statements rather than SqliteConnection.BeginTransaction(): with a SqliteTransaction open,
Microsoft.Data.Sqlite refuses every command whose Transaction property is not set, and Invoke-L2Query and
Read-L2SingleRow do not set it. With the statements it sees none, and the readers run unchanged inside the block.

A reader left open inside the block keeps the snapshot too: SQLite cannot end a read transaction while one of its statements
is still running, so the COMMIT here does not end it, no error is raised, and every later read on the connection -- plain or
in a block -- sees that same old state until the reader is disposed (measured in the review of control-server#510). Read
through Invoke-L2Query and Read-L2SingleRow, which close their reader in a finally; a block that creates its own command
must dispose its reader before it returns.

Not nestable: a BEGIN inside an open transaction is an SQLite error, which throws, and that is the intent -- a nested
block would otherwise end its caller's snapshot at its own COMMIT.
#>

Set-StrictMode -Version Latest

function Invoke-L2SnapshotStatement([object]$Connection, [string]$Sql) {
    $command = $Connection.CreateCommand()
    try {
        $command.CommandText = $Sql
        $null = $command.ExecuteNonQuery()
    } finally {
        $command.Dispose()
    }
}

<#
Runs Read with the connection in one read transaction and returns what Read output. The transaction is committed when
Read returns or throws; an exception from Read is rethrown after that.
#>
function Invoke-L2ReadSnapshot {
    param(
        [Parameter(Mandatory)][object]$Connection,
        [Parameter(Mandatory)][scriptblock]$Read
    )

    Invoke-L2SnapshotStatement $Connection 'BEGIN'
    try {
        return & $Read
    } finally {
        Invoke-L2SnapshotStatement $Connection 'COMMIT'
    }
}

Export-ModuleMember -Function Invoke-L2ReadSnapshot
