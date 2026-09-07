#Requires -Version 7

<#
`REQ-0302` 的硬阻断负向证据：把两个已批准值拿掉，服务端什么都不建。

规格 8.6 说得很直接——三处硬阻断的出口都必须含一条负向证据，「只证明配好之后能跑证不出阻断
存在」。`create-gate` 证的是配好之后能跑，这一条证的是没配就不跑。

四件事一起成立才算数：
1. 服务端**照常启动**（`REQ-0303`：系统仍可启动并展示、诊断和重试同步），健康端点活着；
2. 需求进了 backlog，阻断原因是 `CATALOG_PARAMETERS_NOT_APPROVED`，不是笼统的「无候选」；
3. **一个 JourneyRuntime 都没有，一张 RIoT 单都没建**；
4. **站点根本没被解析过**——冻结表是空的。`REQ-0303` 禁的是「为新 TransportDemand 解析执行
   站点」，不是「解析完再丢掉」。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$mes = $Context.MesIngest
$connection = $Context.Connection

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-CGU-$($Context.RunId)"

# --- 1. 服务端起来了 ------------------------------------------------------------------------------

# 编排器在场景开跑前已经等过 /health/live，所以走到这里本身就证明了「未批准不等于起不来」。
# 显式记一条，因为这正是 REQ-0303 与 REQ-0302 的分界：不得启用业务，不是不得启动。
$assertions.Add(
    'L2-CGU-01',
    '两个参数未批准时服务端照常启动（REQ-0303）',
    $true,
    '已启动',
    '已启动')

# --- 2. 需求被明确地挡住 --------------------------------------------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot) against an uncommissioned catalog.")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$reason = Wait-L2Condition -Description 'the demand was backlogged under the catalog block' `
    -Journal $journal -Criterion 'backlog-reason' -TimeoutSeconds 90 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$demandId'"
        if ($rows.Count -eq 0) { return '' }
        return [string]$rows[0].ReasonCode
    } `
    -Until { param($v) $v -ne '' }

$assertions.Add(
    'L2-CGU-02',
    '阻断原因是 CATALOG_PARAMETERS_NOT_APPROVED，能追到具体这道门禁',
    ($reason -eq 'CATALOG_PARAMETERS_NOT_APPROVED'),
    'CATALOG_PARAMETERS_NOT_APPROVED',
    $reason)

# 让它多转几轮：阻断要一直成立，不能是「第一轮还没准备好」。
Start-Sleep -Seconds 5

# --- 3. 什么都没建 ------------------------------------------------------------------------------

$runtimes = Invoke-L2Query -Connection $connection -Sql 'SELECT * FROM JourneyRuntimes'
$assertions.Add(
    'L2-CGU-03',
    '一个 JourneyRuntime 都没有',
    ($runtimes.Count -eq 0),
    0,
    $runtimes.Count)

$intents = Invoke-L2Query -Connection $connection -Sql 'SELECT * FROM OrderIntents'
$assertions.Add(
    'L2-CGU-04',
    '一张 RIoT move 单都没建',
    ($intents.Count -eq 0),
    0,
    $intents.Count)

$accepted = Invoke-L2Query -Connection $connection -Sql 'SELECT * FROM AcceptedDemands'
$assertions.Add(
    'L2-CGU-05',
    '需求没有被接受',
    ($accepted.Count -eq 0),
    0,
    $accepted.Count)

# --- 4. 站点根本没被解析 --------------------------------------------------------------------------

$frozen = Invoke-L2Query -Connection $connection -Sql 'SELECT * FROM FrozenDemandStations'
$assertions.Add(
    'L2-CGU-06',
    '没有冻结任何端点——站点解析压根没发生（REQ-0303）',
    ($frozen.Count -eq 0),
    0,
    $frozen.Count)

$audit = Invoke-L2Query -Connection $connection -Sql 'SELECT * FROM CreateGateAudit'
$assertions.Add(
    'L2-CGU-07',
    '门禁审计是空的——目录级阻断不是关于任何一个需求端点的裁决',
    ($audit.Count -eq 0),
    0,
    $audit.Count)

$journal.Note('未批准的两个值确实阻断了一切依赖 Map/Station 的业务，而服务端本身照常运行。')
