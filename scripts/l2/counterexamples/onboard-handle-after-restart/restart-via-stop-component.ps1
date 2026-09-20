#Requires -Version 7

<#
Counterexample for Test-L2OnboardHandleAfterRestart.ps1, miss #2: the onboard is restarted with
`& $Context.StopComponent 'onboard-hmi'` rather than Invoke-G3UnknownLoad or $Context.RestartOnboard.

StopComponent kills the process (the same shape as a power cut, real-onboard-restart-while-waiting-operator).
Asking the old driver whether a button is offered after that always answers False, and an assertion
built on that answer is green for the wrong reason -- it proves the window is gone, not that the
onboard withheld the entry.

The check must report the use below. Before the miss was fixed it reported nothing: StopComponent was
not one of the calls it counted as a restart, and the RestartOnboard further down comes too late.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$onboard = $Context.Onboard
$assertions = $Context.Assertions

& $Context.StopComponent 'onboard-hmi'

# No re-read after the kill: this is the finding.
$offered = $onboard.ButtonAvailable('补偿清空')
$assertions.Add('CE-01', '车载端进程没了之后恢复入口不可用', (-not $offered), 'False', $offered)

$null = & $Context.RestartOnboard
