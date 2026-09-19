#Requires -Version 7

<#
Wait-L2ConditionOrLast: Wait-L2Condition, except that a timeout is an answer rather than an error.

Added for control-server#193 in its own file, the way L2Change.psm1 was: shared L2 helpers each take a new file
instead of editing L2.psm1. A scenario that wants it imports it next to L2.psm1:

    Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

It exists for README item 14's first reading rule: when a criterion needs a second fact that is not committed
together with the one just waited for, that fact gets a wait of its own. That second wait timing out is the
criterion failing, so it has to land in the criteria table with the value last read, not end the scenario as a
failureReason that says only "Timed out". L2RealOnboard.psm1's Wait-L2RealOrLast is the same thing for the real
rig; this one imports nothing from there so a synthetic scenario does not pull the rig module in.
#>

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'L2.psm1')

<#
Waits for Until the way Wait-L2Condition does and returns the value that satisfied it. On a timeout it notes the
timeout in the journal, reads Probe once more and returns that instead, for the caller to assert on. Any other
error still throws.
#>
function Wait-L2ConditionOrLast {
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Probe,
        [Parameter(Mandatory)][scriptblock]$Until,
        # Untyped: L2Journal is a class of L2.psm1, and a type constraint here would name it from this module.
        [Parameter(Mandatory)][object]$Journal,
        [Parameter(Mandatory)][string]$Criterion,
        [int]$TimeoutSeconds = 30
    )

    try {
        return Wait-L2Condition -Description $Description -Journal $Journal -Criterion $Criterion `
            -TimeoutSeconds $TimeoutSeconds -Probe $Probe -Until $Until
    } catch {
        if ($_.Exception.Message -notlike 'Timed out after*') { throw }
        $Journal.Note("Not reached: $($_.Exception.Message)")
        return & $Probe
    }
}

Export-ModuleMember -Function Wait-L2ConditionOrLast
