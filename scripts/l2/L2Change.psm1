#Requires -Version 7

<#
Wait-L2Change: read a baseline, perform the action it is a baseline for, then wait for the change.

Ported from ControlServer_MVP (c906ace, control-server#26) into its own file rather than into L2.psm1:
batch 4 and batch 5 add shared L2 helpers in parallel, and each one taking a file of its own is what keeps
them from editing the same module. A scenario that wants it imports this module next to L2.psm1:

    Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Change.psm1') -Force

It builds on Wait-L2Condition, so it imports L2.psm1 too. Without -Force that reuses the copy the
orchestrator already loaded, which matters: the L2Journal a scenario holds is that copy's class.
#>

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'L2.psm1')

<#
Reads a baseline, performs the action it is a baseline for, then waits for the change, and returns both:
Baseline, and Value -- the probed value that satisfied Until. Until is handed the baseline first and the
probed value second.

The baseline is read in here, immediately before the action, because a caller reading it anywhere else is
the failure this exists for. README item 14's fourth case took "UNLOCKING before the compensation" after a
few hundred milliseconds of other criteria; the vehicle had pulsed 106 ms after the command, so the baseline
already held the pulse being waited for and the wait could never end. Holding only this function, a caller
cannot write that order.

The wrapper handed to Wait-L2Condition resolves its variables through dynamic scope inside this module, which
is why it reads $changeUntil and not $Until. Scriptblocks written in a scenario keep the scenario's own scope.
#>
function Wait-L2Change {
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Baseline,
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][scriptblock]$Probe,
        [Parameter(Mandatory)][scriptblock]$Until,
        [int]$TimeoutSeconds = 60,
        [int]$PollMilliseconds = 250,
        # Untyped: L2Journal is a class of L2.psm1, and a type constraint here would name it from this module.
        [object]$Journal,
        [string]$Criterion,
        [object]$Component
    )

    $changeBaseline = & $Baseline
    if ($Journal -and $Criterion) { $Journal.Observe("$Criterion-baseline", $changeBaseline, $null) }
    $null = & $Action
    # Distinct names: the wrapper below runs inside Wait-L2Condition, whose own $Until would shadow ours.
    $changeUntil = $Until
    $waitArguments = @{
        Description      = $Description
        Probe            = $Probe
        Until            = { param($v) & $changeUntil $changeBaseline $v }
        TimeoutSeconds   = $TimeoutSeconds
        PollMilliseconds = $PollMilliseconds
    }
    if ($Journal) { $waitArguments['Journal'] = $Journal }
    if ($Criterion) { $waitArguments['Criterion'] = $Criterion }
    if ($Component) { $waitArguments['Component'] = $Component }
    $value = Wait-L2Condition @waitArguments
    return [pscustomobject]@{ Baseline = $changeBaseline; Value = $value }
}

Export-ModuleMember -Function Wait-L2Change
