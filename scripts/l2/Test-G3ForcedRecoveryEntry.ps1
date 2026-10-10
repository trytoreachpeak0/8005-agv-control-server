#Requires -Version 7

<#
.SYNOPSIS
    Self-check for the forced mechanical recovery confirm step of g3-forced-mechanical-recovery.ps1 (control-server#541).

.DESCRIPTION
    A few seconds, no rig, no window. The scenario is a journey G3 scenario: the journey runner takes it from the bound
    ControlServer commit, so it only runs for real once the exit moves the binding. This covers what can be said before:

      - against a stand-in onboard driver whose 「已隔离并完成机械取出」 enables only once both hand-off boxes are filled (the
        rule onboard-hmi#216 brought), waiting for the enabled button before filling -- the order the scenario had -- times
        out, while waiting for the form, filling it and then waiting for the button gets through;
      - Wait-G3ElementPresent finds an element whatever its IsEnabled, and reports a missing one as absent;
      - the scenario's own statements, read from its AST, are in that order: the wait for ForcedHandoffSublot, both
        SetTextBox calls, then the wait for the enabled button, then the confirm;
      - the failure titles the scenario watches include 「强制机械取出未上报」, the onboard's own title for an unreported
        result, and the scenario reads them from Get-G3ForcedRecoveryFailureTitle at both places it checks for a refusal.

    Exits 1 when any case comes out the other way, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-G3ForcedRecoveryEntry.ps1
#>
[CmdletBinding()]
param(
    [string]$ScenarioPath = (Join-Path $PSScriptRoot 'scenarios\g3-forced-mechanical-recovery.ps1')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scenarios\G3RecoveryCommon.ps1')

$wrong = 0
function Test-Case([string]$Name, [scriptblock]$Body) {
    $failure = $null
    try { $null = & $Body } catch { $failure = $_.Exception.Message }
    if ($null -ne $failure) { $script:wrong++ }
    Write-Host ("{0}  {1}{2}" -f $(if ($null -eq $failure) { 'ok  ' } else { 'BAD ' }), $Name,
        $(if ($failure) { " -> $failure" } else { '' }))
}

$journal = [pscustomobject]@{}
$journal | Add-Member -MemberType ScriptMethod -Name Observe -Value { param($c, $v, $d) }
$journal | Add-Member -MemberType ScriptMethod -Name Note -Value { param($m) }

# The onboard as onboard-hmi#216 has it after the command arrived: the hand-off form is shown, and the confirm button is
# there but enabled only once both boxes hold text.
function New-StandInOnboard {
    $onboard = [pscustomobject]@{ Boxes = @{ ForcedHandoffSublot = ''; ForcedHandoffReceiverName = '' } }
    $onboard | Add-Member -MemberType ScriptMethod -Name Element -Value {
        param($By, $Value)
        if ($By -eq 'AutomationId' -and $this.Boxes.ContainsKey($Value)) { return [pscustomobject]@{ AutomationId = $Value } }
        return $null
    }
    $onboard | Add-Member -MemberType ScriptMethod -Name SetTextBox -Value {
        param($AutomationId, $Text)
        if (-not $this.Boxes.ContainsKey($AutomationId)) { throw "No element with AutomationId '$AutomationId'." }
        $this.Boxes[$AutomationId] = $Text
    }
    $onboard | Add-Member -MemberType ScriptMethod -Name ButtonAvailable -Value {
        param($Name)
        return $Name -eq '已隔离并完成机械取出' -and
            -not [string]::IsNullOrEmpty($this.Boxes['ForcedHandoffSublot']) -and
            -not [string]::IsNullOrEmpty($this.Boxes['ForcedHandoffReceiverName'])
    }
    return $onboard
}

Test-Case 'the old order: waiting for the enabled confirm before filling the form times out' {
    $onboard = New-StandInOnboard
    if (Wait-G3ButtonOffered $onboard $journal '已隔离并完成机械取出' 'c' 1) { throw 'the button was offered before the form was filled' }
}
Test-Case 'the new order: form present, fill both boxes, then the confirm is enabled' {
    $onboard = New-StandInOnboard
    if (-not (Wait-G3ElementPresent $onboard $journal 'ForcedHandoffSublot' 'c' 1)) { throw 'the form was not found' }
    $onboard.SetTextBox('ForcedHandoffSublot', 'SUBLOT-1')
    if (Wait-G3ButtonOffered $onboard $journal '已隔离并完成机械取出' 'c' 1) { throw 'enabled with one box filled' }
    $onboard.SetTextBox('ForcedHandoffReceiverName', 'G3 交接人')
    if (-not (Wait-G3ButtonOffered $onboard $journal '已隔离并完成机械取出' 'c' 1)) { throw 'not enabled with both boxes filled' }
}
Test-Case 'Wait-G3ElementPresent reports an element the window does not have as absent' {
    if (Wait-G3ElementPresent (New-StandInOnboard) $journal 'NoSuchBox' 'c' 1) { throw 'reported present' }
}
Test-Case 'the failure titles include the onboard''s 「强制机械取出未上报」 and the two the scenario knew' {
    $titles = Get-G3ForcedRecoveryFailureTitle
    foreach ($title in @('强制机械取出未上报', '强制机械恢复失败', '确认失败')) {
        if ($title -notin $titles) { throw "missing $title" }
    }
}

# --- the scenario itself, read from its AST --------------------------------------------------------------------------------
$parseErrors = $null
$scenario = [System.Management.Automation.Language.Parser]::ParseFile($ScenarioPath, [ref]$null, [ref]$parseErrors)
if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) { throw "The scenario does not parse: $ScenarioPath" }
$commands = @($scenario.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))
$calls = @($scenario.FindAll({ param($n) $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] }, $true))
function Get-Offset([object[]]$Nodes, [scriptblock]$Match, [string]$What) {
    $found = @($Nodes | Where-Object $Match)
    if ($found.Count -ne 1) { throw "Expected one $What in the scenario, found $($found.Count)." }
    return $found[0].Extent.StartOffset
}

Test-Case 'the scenario waits for the hand-off form, fills both boxes, then waits for the enabled confirm, then presses it' {
    $form = Get-Offset $commands {
        $_.GetCommandName() -eq 'Wait-G3ElementPresent' -and $_.Extent.Text -like "*'ForcedHandoffSublot'*" } 'wait for the hand-off form'
    $sublot = Get-Offset $calls { $_.Member.Value -eq 'SetTextBox' -and $_.Extent.Text -like "*'ForcedHandoffSublot'*" } 'sublot fill'
    $receiver = Get-Offset $calls { $_.Member.Value -eq 'SetTextBox' -and $_.Extent.Text -like "*'ForcedHandoffReceiverName'*" } 'receiver fill'
    $enabled = Get-Offset $commands {
        $_.GetCommandName() -eq 'Wait-G3ButtonOffered' -and $_.Extent.Text -like "*'已隔离并完成机械取出'*" } 'wait for the enabled confirm'
    $press = Get-Offset $commands {
        $_.GetCommandName() -eq 'Invoke-G3ConfirmedButton' -and $_.Extent.Text -like "*'已隔离并完成机械取出'*" } 'confirm press'
    if (-not ($form -lt $sublot -and $sublot -lt $enabled -and $receiver -lt $enabled -and $enabled -lt $press)) {
        throw "out of order: form $form, sublot $sublot, receiver $receiver, enabled $enabled, press $press"
    }
}
Test-Case 'the scenario reads its failure titles from Get-G3ForcedRecoveryFailureTitle and names no title by hand' {
    $source = Get-Content -Raw -LiteralPath $ScenarioPath
    if (@($commands | Where-Object { $_.GetCommandName() -eq 'Get-G3ForcedRecoveryFailureTitle' }).Count -ne 1) {
        throw 'the scenario does not call Get-G3ForcedRecoveryFailureTitle exactly once'
    }
    foreach ($title in @("-contains '强制机械恢复失败'", "-contains '确认失败'")) {
        if ($source.Contains($title)) { throw "the scenario still checks $title by hand" }
    }
    if (([regex]::Matches($source, [regex]::Escape('-in $failureTitles'))).Count -lt 3) {
        throw 'expected the form wait, the result probe and the refusal message to read $failureTitles'
    }
}

if ($wrong -gt 0) {
    Write-Host "$wrong case(s) came out wrong."
    exit 1
}
Write-Host 'All cases as expected.'
