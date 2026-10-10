#Requires -Version 7

<#
全厂订单在变时，车照样能装货、能离站（control-server#573）。与 `real-onboard-departure-under-listing-churn` 同一个脚本，
只是清单搅动从车到取货站就打开（`-ChurnFrom PickupArrival`），多判装货那一段（`L2-LC-05`～`07`），再判离站那一段（`01`～`04`）。

为什么单列一个场景：修复前的 CI 真装置（run 38067577157）在装货就红了——开锁后车载端撞上一次未就绪，`WAITING_OPERATOR`
进度发不出去，服务端等不到，判据一条都走不到。离站那一路的红要在装完之后搅才看得见，装货这一路的绿要从到站就搅才证明得了，
两件事不能放在同一次运行里。修复前的装货红以那次运行为证（`evidence/cs573/rig-red-load-phase-38067577157`），本场景只跑修复后。
判据的说明在被调用的那个脚本头注释里。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'real-onboard-departure-under-listing-churn.ps1') -Context $Context -ChurnFrom PickupArrival
