#Requires -Version 7

<#
规格 5.4 与第 8.3 节批次 8 行的负向判据：投运车辆数大于登记等待点数时服务端拒绝启动（REQ-0289，control-server#388）。

setup 配两台车，只登记一个等待点（站 214），并声明 `ExpectServerStartupRefusal`。编排器先 `--migrate-only` 建库、用 FieldOps
正式导入那一个点，再起服务端；因为预期拒绝，它不等 live，而是等进程退出、收日志，不起对端，再把结果交到这里。

这些一起成立才算数：
1. 前提真的是「登记了、但不够」：库里当前登记版本在 25 号图上正好一个启用点——不是因为没导入才被拒；
2. 服务端进程以**非零**退出码结束，`/health/live` 从头到尾一次都没答过——拒绝发生在监听之前；
3. 服务端日志含原因码 `WAITING_POINTS_FEWER_THAN_VEHICLES`，写明车辆数 2、点数 1、没分到点的那辆车，并给出参数已填好的
   `import-waiting-points` 命令——`--database` 必须是本轮 stage 里的那个库文件的全路径，这正是「拿来能跑」的判据；
4. 第二个事实另等（`Wait-L2ConditionOrLast`，不读一次就断言）：给它 15 秒让缺陷有机会发生，库里没有任何受理的需求与订单意图，
   假 RIoT 没收到任何建单。**这一条在本装置上恒绿**：拒绝启动时编排器不起对端、不发需求，即使服务端起来了也没有车能受理，
   所以它不是守拒绝的判据。真正守拒绝的是编排器等进程退出（服务端不退出就判运行失败）与第 2 条；去掉启动校验的红证据
   红在那里，不在这里。留着它，是因为票要求的「另等第二个事实」在拒绝启动的形状里只能是这样。

红证据（缺陷版本）：去掉 Program.cs 里的 `WaitingPointStartupCheck.EnsureAsync` 那一行，服务端起得来，第 2 条变红。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$refusal = $Context.ServerRefusal
if ($null -eq $refusal) {
    throw 'This scenario needs ExpectServerStartupRefusal in its setup; the orchestrator did not hand over a refusal.'
}

$reasonCode = 'WAITING_POINTS_FEWER_THAN_VEHICLES'
$journal.Note("Server exited with $($refusal.ExitCode); /health/live ever answered: $($refusal.EverLive).")

# --- 1. 登记了、但不够 -----------------------------------------------------------------------------

$registered = Invoke-L2Query -Connection $Context.Connection -Sql @'
SELECT (SELECT COUNT(*) FROM WaitingPointVersions) AS Versions,
       (SELECT COUNT(*) FROM WaitingPoints p
         WHERE p.Version = (SELECT MAX(Version) FROM WaitingPointVersions) AND p.MapId = 25 AND p.Enabled = 1) AS EnabledOnMap,
       (SELECT group_concat(StationId) FROM WaitingPoints) AS Stations
'@
$assertions.Add(
    'L2-WPR-01',
    '库里有一版经 FieldOps 导入的登记，25 号图上正好一个启用等待点（站 214）',
    ([int]$registered[0].Versions -eq 1 -and [int]$registered[0].EnabledOnMap -eq 1 -and [string]$registered[0].Stations -eq '214'),
    'Versions=1 EnabledOnMap=1 Stations=214',
    "Versions=$($registered[0].Versions) EnabledOnMap=$($registered[0].EnabledOnMap) Stations=$($registered[0].Stations)")

# --- 2. 非零退出、从未监听 -------------------------------------------------------------------------

$assertions.Add(
    'L2-WPR-02',
    '服务端进程以非零退出码结束，/health/live 始终不通',
    ($null -ne $refusal.ExitCode -and $refusal.ExitCode -ne 0 -and -not $refusal.EverLive),
    '非零、从未答过',
    "exit=$($refusal.ExitCode) everLive=$($refusal.EverLive)")

# --- 3. 原因码与报错内容 ---------------------------------------------------------------------------

# The command in the refusal is "ready to run" only if it names the database this server actually opened: this run's stage
# copy, as the connection the orchestrator opened on it reports it.
$databasePath = $Context.Connection.DataSource
$refusalLines = @($refusal.Log -split "`r?`n" | Where-Object { $_.Contains($reasonCode) })
$named = @($refusalLines | Where-Object {
        $_.Contains('has 2 vehicles') -and $_.Contains('gives only 1 of them') -and $_.Contains('(1 enabled there)') -and
        $_.Contains('left without a point: BROKERX-L2-0002') -and
        $_.Contains("import-waiting-points --database `"$databasePath`"") -and $_.Contains('--map 25')
    })
$assertions.Add(
    'L2-WPR-03',
    "服务端日志含 $reasonCode，写明车辆数 2、可分到的点数 1、没分到点的车，命令的 --database 是本轮 stage 的库文件",
    ($named.Count -gt 0),
    "$reasonCode + has 2 vehicles + gives only 1 of them + left without a point: BROKERX-L2-0002 + --database `"$databasePath`" + --map 25",
    $(if ($named.Count -gt 0) { $named[0].Trim() } elseif ($refusalLines.Count -gt 0) { $refusalLines[0].Trim() } else { '(没有)' }))

# --- 4. 第二个事实：没有受理、没有建单 --------------------------------------------------------------

$connection = $Context.Connection
$riot = $Context.Riot
$effects = Wait-L2ConditionOrLast -Description 'a refused server accepted a demand or created an order' -Journal $journal `
    -Criterion 'refused-server-side-effects' -TimeoutSeconds 15 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql @'
SELECT (SELECT COUNT(*) FROM AcceptedDemands) AS Accepted, (SELECT COUNT(*) FROM OrderIntents) AS Intents
'@
        [pscustomobject]@{
            Accepted = [int]$rows[0].Accepted
            Intents  = [int]$rows[0].Intents
            Orders   = @($riot.Snapshot().body.orders).Count
        }
    } `
    -Until { param($v) $v.Accepted -gt 0 -or $v.Intents -gt 0 -or $v.Orders -gt 0 }
$assertions.Add(
    'L2-WPR-04',
    '另等 15 秒：库里没有受理的需求与订单意图，假 RIoT 没收到任何建单',
    ($effects.Accepted -eq 0 -and $effects.Intents -eq 0 -and $effects.Orders -eq 0),
    'Accepted=0 Intents=0 Orders=0',
    "Accepted=$($effects.Accepted) Intents=$($effects.Intents) Orders=$($effects.Orders)")
