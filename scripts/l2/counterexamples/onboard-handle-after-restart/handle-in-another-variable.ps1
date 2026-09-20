#Requires -Version 7

<#
Counterexample for Test-L2OnboardHandleAfterRestart.ps1, miss #1: the onboard driver is held in a
variable that is not called $onboard.

Invoke-G3UnknownLoad restarts the onboard (G3RecoveryCommon.ps1), so $hmi points at a window that no
longer exists. Every lookup through it answers False, which reads exactly like "the onboard never
offered the entry" -- the failure control-server#222 spent a real-rig round on.

The check must report the use below. Before the miss was fixed it reported nothing, because it only
ever tracked the name $onboard.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$hmi = $Context.Onboard
$journal = $Context.Journal

$load = Invoke-G3UnknownLoad $Context 'CE'
$journal.Note("Load $($load.DemandId) ended UNKNOWN.")

# No re-read after the restart: this is the finding.
$offered = $hmi.ButtonAvailable('补偿清空')
$journal.Note("Compensation entry offered: $offered")
