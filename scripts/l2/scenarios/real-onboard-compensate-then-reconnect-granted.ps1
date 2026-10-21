#Requires -Version 7

<#
服务端要恢复授权时，补偿清空照样走得通（control-server#797 方案 C，ADR-cross-0066；车载端半边 onboard-hmi#391）。与
`real-onboard-compensate-then-reconnect` 同一个脚本，只是 setup 打开了故障人工恢复入口（授权入口挂在它上面）和
RecoveryGrantRequired，脚本因此在补偿之前多走一段（`L2-CR-G1`、`G2`）：

- 反面：没有授权，车载端按「补偿清空」发出的开会话请求被以 RECOVERY_AUTHENTICATION_FAILED 拒，补偿不发生；
- 维护人员在控制端演练、带码给这辆车这一趟旅程下授权（RESUME_AFTER_REPAIR、COMPENSATE_LOAD_ALL_EMPTY，`Grant-L2Recovery`）；
- 再按一次「补偿清空」，之后的判据（`L2-CR-02`～`08`）与原场景相同。

为什么单列：factory01 上恢复入口打开之后授权随之打开（control-server#844），原场景跑的是产品缺省（不要授权），证不到这一形。
第一次真跑是 cs#797 与 onboard-hmi#391 的真装置联跑；真装置不上 CI，不进 l2.yml。判据的说明在被调用的那个脚本里。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Context.RecoveryGrantRequired) { throw 'This scenario needs RecoveryGrantRequired = $true in its setup file.' }
& (Join-Path $PSScriptRoot 'real-onboard-compensate-then-reconnect.ps1') -Context $Context
