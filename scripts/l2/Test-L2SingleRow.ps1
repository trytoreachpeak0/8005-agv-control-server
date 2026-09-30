#Requires -Version 7
<#
.SYNOPSIS
    Self-check of L2SingleRow.psm1: one row is the row, none is $null, several is a stand-in that says how many.

.DESCRIPTION
    Under a second, no rig and no SQLite: Read-L2SingleRow is driven through a connection double that answers the
    three calls it makes (CreateCommand, ExecuteReader, and the reader's own members).

    Every scenario that uses it runs on a product that gives exactly one row, so the two branches this exists for --
    two rows, and zero rows under -Required -- are never reached by a green run, on any rig (control-server#428).
    What is pinned here is what a scenario relies on when it does reach them: the stand-in is NOT $null (a wait on
    "$null -ne $v" ends), it has the query's columns (a property read does not throw under strict mode), and no
    comparison with an expected value holds against it.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2SingleRow.psm1') -Force

# A connection that returns the given rows for any query, the way Microsoft.Data.Sqlite's does for this caller.
function New-ConnectionDouble([string[]]$Columns, [object[]]$Rows) {
    $state = @{ Columns = $Columns; Rows = $Rows; Index = -1; Closed = $false; Disposed = $false; Sql = $null }
    $reader = [pscustomobject]@{ State = $state }
    $reader | Add-Member -MemberType ScriptProperty -Name FieldCount -Value { $this.State.Columns.Count }
    $reader | Add-Member -MemberType ScriptMethod -Name GetName -Value { param($i) $this.State.Columns[$i] }
    $reader | Add-Member -MemberType ScriptMethod -Name Read -Value { $this.State.Index++; $this.State.Index -lt $this.State.Rows.Count }
    $reader | Add-Member -MemberType ScriptMethod -Name IsDBNull -Value { param($i) $null -eq $this.State.Rows[$this.State.Index][$i] }
    $reader | Add-Member -MemberType ScriptMethod -Name GetValue -Value { param($i) $this.State.Rows[$this.State.Index][$i] }
    $reader | Add-Member -MemberType ScriptMethod -Name Close -Value { $this.State.Closed = $true }
    $command = [pscustomobject]@{ State = $state; Reader = $reader; CommandText = $null }
    $command | Add-Member -MemberType ScriptMethod -Name ExecuteReader -Value { $this.State.Sql = $this.CommandText; $this.Reader }
    $command | Add-Member -MemberType ScriptMethod -Name Dispose -Value { $this.State.Disposed = $true }
    $connection = [pscustomobject]@{ State = $state; Command = $command }
    $connection | Add-Member -MemberType ScriptMethod -Name CreateCommand -Value { $this.Command }
    return $connection
}

$results = [System.Collections.Generic.List[object]]::new()
function Add-Case([string]$Name, [bool]$Ok, [string]$Actual) {
    $results.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Actual = $Actual })
}
function Format-Row($Row) {
    if ($null -eq $Row) { return '$null' }
    return (($Row.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join '; ')
}

$columns = 'Stage', 'AgvId', 'BlockReasonCode'
$one = , @('AwaitingSublot', 'AGV-1', $null)
$two = @('AwaitingSublot', 'AGV-1', $null), @('Completed', 'AGV-2', $null)

# ---------------------------------------------------------------- Read-L2SingleRow, through the connection double

$connection = New-ConnectionDouble $columns $one
$row = Read-L2SingleRow -Connection $connection -Sql 'SELECT the row'
Add-Case 'one row: the row, NULL kept as $null, reader closed and command disposed, the SQL handed over as given' (
    [string]$row.Stage -ceq 'AwaitingSublot' -and [string]$row.AgvId -ceq 'AGV-1' -and $null -eq $row.BlockReasonCode -and
    $connection.State.Closed -and $connection.State.Disposed -and $connection.State.Sql -ceq 'SELECT the row') (Format-Row $row)

$row = Read-L2SingleRow -Connection (New-ConnectionDouble $columns @()) -Sql 'q'
Add-Case 'no row: $null, which is what a wait on this read polls for' ($null -eq $row) (Format-Row $row)

$row = Read-L2SingleRow -Connection (New-ConnectionDouble $columns @()) -Sql 'q' -Required
Add-Case 'no row, -Required: a stand-in with the query''s columns, each saying 0 rows' (
    $null -ne $row -and @($row.PSObject.Properties.Name) -join ',' -ceq 'Stage,AgvId,BlockReasonCode' -and
    [string]$row.Stage -ceq '(0 rows, expected 1)' -and [string]$row.BlockReasonCode -ceq '(0 rows, expected 1)') (Format-Row $row)

$row = Read-L2SingleRow -Connection (New-ConnectionDouble $columns $two) -Sql 'q'
Add-Case 'two rows: a stand-in, not the first row -- and not $null, so a wait on "$null -ne $v" ends' (
    $null -ne $row -and [string]$row.Stage -ceq '(2 rows, expected 1)' -and [string]$row.AgvId -ceq '(2 rows, expected 1)') (Format-Row $row)

$row = Read-L2SingleRow -Connection (New-ConnectionDouble $columns $two) -Sql 'q' -Required
Add-Case 'two rows, -Required: the same stand-in' ([string]$row.Stage -ceq '(2 rows, expected 1)') (Format-Row $row)

# ---------------------------------------------------------------- what a criterion sees of a stand-in

$standIn = Read-L2SingleRow -Connection (New-ConnectionDouble $columns $two) -Sql 'q'
Add-Case 'a stage comparison against the stand-in does not hold' (-not ([string]$standIn.Stage -eq 'AwaitingSublot')) "$([string]$standIn.Stage -eq 'AwaitingSublot')"
Add-Case 'the "is it CONFIRMED" shape the intent waits use does not hold either' (
    -not ($standIn -and [string]$standIn.Stage -eq 'CONFIRMED')) "$($standIn -and [string]$standIn.Stage -eq 'CONFIRMED')"
Add-Case 'printed into a criterion''s actual text, the stand-in names the count' (
    "$($standIn.Stage) / $($standIn.AgvId)" -ceq '(2 rows, expected 1) / (2 rows, expected 1)') "$($standIn.Stage) / $($standIn.AgvId)"
Add-Case 'rendered the way Wait-L2Condition renders its last observation, it names the count' (
    ([string]$standIn) -like '*(2 rows, expected 1)*') ([string]$standIn)
$castThrew = try { $null = [int]$standIn.Stage; $false } catch { $true }
Add-Case 'a numeric cast of a stand-in column throws -- the documented limit: compare such a column as text' $castThrew "threw=$castThrew"

# ---------------------------------------------------------------- Select-L2SingleRow on its own

$picked = Select-L2SingleRow -Rows @([pscustomobject]@{ Stage = 'X' }) -Columns 'Stage'
Add-Case 'Select: one row is that row' ([string]$picked.Stage -ceq 'X') (Format-Row $picked)
Add-Case 'Select: $null rows count as none' ($null -eq (Select-L2SingleRow -Rows $null -Columns 'Stage')) 'n/a'
$picked = Select-L2SingleRow -Rows @(1, 2, 3 | ForEach-Object { [pscustomobject]@{ Stage = "S$_" } }) -Columns 'Stage'
Add-Case 'Select: three rows say three' ([string]$picked.Stage -ceq '(3 rows, expected 1)') (Format-Row $picked)

$bad = 0
foreach ($result in $results) {
    if (-not $result.Ok) { $bad++ }
    Write-Host ("{0}  {1} -> {2}" -f ($result.Ok ? 'ok  ' : 'BAD '), $result.Name, $result.Actual)
}
if ($bad -gt 0) {
    Write-Host "L2SingleRow self-check: $bad of $($results.Count) cases came out the wrong way."
    exit 1
}
Write-Host "L2SingleRow self-check: all $($results.Count) cases as expected."
