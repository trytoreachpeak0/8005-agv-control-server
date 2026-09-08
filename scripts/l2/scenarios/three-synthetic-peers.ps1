#Requires -Version 7

<#
三个合成车载端同时在线，服务端同时持有三条会话，两侧都互不冒充。

这条场景证的是会话这一层：合成对端能同时承载三台车（三个进程各是各的），并且服务端同时
接得住三条连接、给三台车各建一条 Ready 会话。它不派单、不装货、不走 journey——三车端到端
是票 18 的出口。

**这条断言集在票 09 之前钉的是相反的事**：那时 ControlServer 的 accept 循环是串行的
（`OnboardTcpServer.cs` 里 `await HandleClientAsync` 写在 `while` 内），处理完一个连接才接
下一个，所以三台车里只有第一台的会话是活的，另两台排队，`L2-3P-06` 断言的就是「只有第一
台」。票 09 把 accept 改成并发、把 `OnboardPeer` 改成按 `AgvId` 持有 N 条连接之后，边界移
动了，断言跟着移动。

为什么值得单独有一条：票 03 要把合成装置扩到能承载三台车，而「能承载」这句话如果没有一条
真的把三台都拉起来的运行，就只是一句配置读起来应该可以。这条跑起来才发现服务端那道串行
accept——光读配置是读不出来的。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$peers = @($Context.OnboardPeers)

$expectedAgvIds = @('AGV-FAKE-001', 'AGV-FAKE-002', 'AGV-FAKE-003')

$journal.Note("合成车载端 $($peers.Count) 个：" +
    (($peers | ForEach-Object { "$($_.Name)=$($_.AgvId)@$($_.Port)" }) -join ', '))

$assertions.Add(
    'L2-3P-01',
    '编排器按 setup 起了三个合成车载端',
    ($peers.Count -eq 3),
    3,
    $peers.Count)

# 端口互不相同，否则第二个进程会绑不上，而「绑不上」在日志里长得像「还没起来」。
$distinctPorts = @($peers | ForEach-Object { $_.Port } | Sort-Object -Unique)
$assertions.Add(
    'L2-3P-02',
    '三个控制面各占一个端口',
    ($distinctPorts.Count -eq 3),
    3,
    $distinctPorts.Count)

$actualAgvIds = @($peers | ForEach-Object { $_.AgvId } | Sort-Object)
$assertions.Add(
    'L2-3P-03',
    '三个车载端各报各的 AgvId',
    (($actualAgvIds -join ',') -eq ($expectedAgvIds -join ',')),
    ($expectedAgvIds -join ','),
    ($actualAgvIds -join ','))

# 每个控制面都活着，而且报的是它自己那台车的身份。一个进程替另一个进程回答，正是「串」的
# 样子；三个进程各答各的，是这条场景真正能证的那件事。
$reportedAgvIds = @()
foreach ($peer in $peers) {
    $snapshot = $peer.Double.Snapshot()
    $reportedAgvIds += [string]$snapshot.body.agvId
    $assertions.Add(
        "L2-3P-04-$($peer.AgvId)",
        "$($peer.AgvId) 的控制面在线并报出自己的身份",
        ([string]$snapshot.body.agvId -eq $peer.AgvId),
        $peer.AgvId,
        [string]$snapshot.body.agvId)
}

$assertions.Add(
    'L2-3P-05',
    '三个控制面报出三个互不相同的身份',
    (@($reportedAgvIds | Sort-Object -Unique).Count -eq 3),
    3,
    @($reportedAgvIds | Sort-Object -Unique).Count)

# 服务端侧：SessionRecoveries 主键是 AgvId，所以一台车一行。三台车都连上之后应当有三行，
# 每行 Ready——这正是票 09 把 accept 改成并发、把 OnboardPeer 改成按 AgvId 分槽换来的东西。
$sessions = @(Invoke-L2Query -Connection $connection `
    -Sql "SELECT AgvId, Readiness FROM SessionRecoveries ORDER BY AgvId")
$sessionAgvIds = @($sessions | ForEach-Object { [string]$_.AgvId })

$assertions.Add(
    'L2-3P-06',
    '服务端同时持有三台车的会话，一台一行',
    (($sessionAgvIds -join ',') -eq ($expectedAgvIds -join ',')),
    ($expectedAgvIds -join ','),
    ($sessionAgvIds -join ','))

# 逐台断言，而不是只数行数：三行里有一行不是 Ready，是「接住了但没握完手」，与「没接住」
# 要人做的事不一样，合并成一条断言就看不出是哪台。
foreach ($expected in $expectedAgvIds) {
    $row = $sessions | Where-Object { [string]$_.AgvId -eq $expected } | Select-Object -First 1
    $assertions.Add(
        "L2-3P-07-$expected",
        "$expected 的会话在服务端是 Ready",
        ($null -ne $row -and [string]$row.Readiness -eq 'Ready'),
        'Ready',
        $(if ($null -eq $row) { '(缺行)' } else { [string]$row.Readiness }))
}

$journal.Note('合成侧三实例各自独立，服务端同时持有三条 Ready 会话。三车端到端出口属票 18。')
