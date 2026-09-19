#Requires -Version 7

<#
规格 8.3 批次 6 机制判据 ④：同一 Station 被两个任务类型绑定时，服务端在启动期拒绝（control-server#159）。

setup 把关卡（210）同时绑给 `WIRE_TO_GATE` 与 `STAGING_TO_WIRE`，并声明 `ExpectServerStartupRefusal`。
编排器因此不等服务端 live，而是等它的进程退出、收集日志，不起对端，再把结果交到这里。

五件事一起成立才算数：
1. 服务端进程以**非零**退出码结束；
2. `/health/live` 从头到尾**一次都没答过**——拒绝发生在监听之前，不是起来之后再倒下；
3. 服务端日志含原因码 `TASK_TYPE_STATION_REUSED`；
4. 那一条拒绝**点名了那个 Station（关卡／210）与两个任务类型**，现场一眼就知道该改哪一行；
5. 规则与绑定**一版都没落库**——拒绝就是整份不装，没有「规则放行了、绑定还没写」的中间态。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$refusal = $Context.ServerRefusal
if ($null -eq $refusal) {
    throw 'This scenario needs ExpectServerStartupRefusal in its setup; the orchestrator did not hand over a refusal.'
}

$reasonCode = 'TASK_TYPE_STATION_REUSED'
$journal.Note("Server exited with $($refusal.ExitCode); /health/live ever answered: $($refusal.EverLive).")

# --- 1. 非零退出 ----------------------------------------------------------------------------------

$assertions.Add(
    'L2-TTSR-01',
    '服务端进程以非零退出码结束',
    ($null -ne $refusal.ExitCode -and $refusal.ExitCode -ne 0),
    '非零',
    $refusal.ExitCode)

# --- 2. 从未监听 ----------------------------------------------------------------------------------

$assertions.Add(
    'L2-TTSR-02',
    '/health/live 始终不通',
    (-not $refusal.EverLive),
    '从未答过',
    $(if ($refusal.EverLive) { '答过 live' } else { '从未答过' }))

# --- 3. 原因码 ------------------------------------------------------------------------------------

$refusalLines = @($refusal.Log -split "`r?`n" | Where-Object { $_.Contains($reasonCode) })
$assertions.Add(
    'L2-TTSR-03',
    "服务端日志含 $reasonCode",
    ($refusalLines.Count -gt 0),
    $reasonCode,
    $(if ($refusalLines.Count -gt 0) { $refusalLines[0].Trim() } else { '(没有)' }))

# --- 4. 点名 Station 与两个任务类型 --------------------------------------------------------------

$named = @($refusalLines | Where-Object {
        $_.Contains('关卡/210') -and $_.Contains('WIRE_TO_GATE') -and $_.Contains('STAGING_TO_WIRE')
    })
$assertions.Add(
    'L2-TTSR-04',
    '拒绝点名 Station 关卡/210 与 WIRE_TO_GATE、STAGING_TO_WIRE',
    ($named.Count -gt 0),
    '关卡/210 + WIRE_TO_GATE + STAGING_TO_WIRE',
    $(if ($named.Count -gt 0) { $named[0].Trim() } elseif ($refusalLines.Count -gt 0) { $refusalLines[0].Trim() } else { '(没有)' }))

# --- 5. 一版都没落库 -----------------------------------------------------------------------------

$landed = Invoke-L2Query -Connection $Context.Connection -Sql @'
SELECT (SELECT COUNT(*) FROM TaskTypeStationRuleVersions) AS Rules,
       (SELECT COUNT(*) FROM TaskTypeStationBindingSetVersions) AS BindingSets
'@
$assertions.Add(
    'L2-TTSR-05',
    '规则版本与绑定集版本都没有落库',
    ([int]$landed[0].Rules -eq 0 -and [int]$landed[0].BindingSets -eq 0),
    'Rules=0 BindingSets=0',
    "Rules=$($landed[0].Rules) BindingSets=$($landed[0].BindingSets)")
