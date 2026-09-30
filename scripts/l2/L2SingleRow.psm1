#Requires -Version 7

<#
Read-L2SingleRow: a read that is supposed to find ONE row, and says so when it did not.

Added for control-server#428 in its own file, the way L2Change.psm1 and L2ConditionOrLast.psm1 were. A scenario that
wants it imports it next to L2.psm1:

    Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SingleRow.psm1') -Force

Why it exists. The scenarios read "the journey of this demand" or "the TO_PICKUP intent of this demand" as

    $rows = Invoke-L2Query ...; if ($rows.Count -eq 0) { return $null }; return $rows[0]

and neither key is unique in the schema, nor is there an ORDER BY. With two rows that read hands back whichever came
first and the criterion built on it goes on as if nothing were wrong. (Until control-server#428 the same reads were
written `@(Invoke-L2Query ...)`, which by accident went red or threw on two rows; unwrapping them made them quietly
lenient, and this is what puts the strictness back on purpose.)

What comes back:

    1 row     the row
    0 rows    $null -- "not there yet" is what every wait on these reads polls for -- unless -Required
    N rows    a stand-in with the same columns, every one of which reads "(N rows, expected 1)"

The stand-in is a value, not an exception, for two reasons. These reads run inside Wait-L2Condition probes, which
swallow every exception into a bare timeout (L2.psm1), so a throw would lose the very count it carries. And a
stand-in makes the criterion itself go red with the count in its actual text: no comparison with an expected stage,
vehicle or status holds against "(2 rows, expected 1)", and wherever the scenario prints the value it read, it
prints that.

-Required is for a read that is not polled -- the row was waited for a moment earlier -- where zero rows used to be an
index-out-of-range that ended the scenario before its criteria table. With it, zero rows is a stand-in too.

A numeric cast of a stand-in column throws ([int]'(2 rows, expected 1)'). Compare such a column as text, or put the
comparison after one that fails first.
#>

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    The pure half: one row out of the rows a query returned, or the stand-in. No database.
#>
function Select-L2SingleRow {
    param(
        [Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()][object[]]$Rows,
        # The query's column names: a stand-in for zero rows has no row to take them from.
        [Parameter(Mandatory)][string[]]$Columns,
        [switch]$Required
    )

    # Not @($Rows).Count: @($null) is one element.
    $count = $null -eq $Rows ? 0 : $Rows.Count
    if ($count -eq 1) { return $Rows[0] }
    if ($count -eq 0 -and -not $Required) { return $null }
    $standIn = [ordered]@{}
    foreach ($column in $Columns) { $standIn[$column] = "($count rows, expected 1)" }
    return [pscustomobject]$standIn
}

<#
.SYNOPSIS
    Runs the query and returns its one row, $null for none (a stand-in with -Required), a stand-in for several.
#>
function Read-L2SingleRow {
    param(
        [Parameter(Mandatory)][object]$Connection,
        [Parameter(Mandatory)][string]$Sql,
        [switch]$Required
    )

    # Its own reader rather than Invoke-L2Query: the column names have to be known when there is no row.
    $command = $Connection.CreateCommand()
    $command.CommandText = $Sql
    $reader = $command.ExecuteReader()
    $columns = @(for ($index = 0; $index -lt $reader.FieldCount; $index++) { $reader.GetName($index) })
    $rows = [System.Collections.Generic.List[object]]::new()
    while ($reader.Read()) {
        $row = [ordered]@{}
        for ($index = 0; $index -lt $reader.FieldCount; $index++) {
            $row[$columns[$index]] = if ($reader.IsDBNull($index)) { $null } else { $reader.GetValue($index) }
        }
        $rows.Add([pscustomobject]$row)
    }
    $reader.Close()
    $command.Dispose()
    return Select-L2SingleRow -Rows $rows.ToArray() -Columns $columns -Required:$Required
}

Export-ModuleMember -Function Select-L2SingleRow, Read-L2SingleRow
