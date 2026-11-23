#Requires -Version 7

<#
车载程序停掉之后，这台车不被派任何新用途；重连握手就绪后恢复可派（control-server#912）。

**为什么有这条。** 11-23 agv03 停车载程序，服务端 13:19:05 读到连接被重置；会话行没变（仍是 Ready），最后一条入站才几秒，
派车就绪只看「Ready 且最后入站在 MaximumEvidenceAge（30 秒）内」，于是 13:19:10 那一轮给它承诺了空闲返回，车在没有车载程序
看着仓门与 IO 的情况下开到了 214。修好之后，派车、空闲返回、充电分配读车载事实时还要这一代会话此刻有一条已握手的连接。

**装置。** 一台车、一个等待点 214（节点 6，从关卡一条边就到），空闲返回打开。车从假 RIoT 启动起就停在「哪个站都不是」的位置
（setup 的 RiotStartStationId = 0），这样断线之前它既不能空闲返回、也没有需求可接。只在场景里第一条命令才把它挪到 0 不够：
服务端起来后的头几轮可能先于那条命令，在关卡上就给它承诺了空闲返回（PR #913 第一次 CI 就是这样，L2-ODN-01 读到 claims=1）。

**第一个事实（断线后的 30 秒以内）。** 假车载端拔线（PUT /connection connected=false，服务端立刻读到连接断开），接着把车放到关卡、
再发一条正好适合它的搬运需求。之后另等约 20 秒、十几轮派车：没有任何用途占有、没有旅程、没有订单意图——空闲返回不承诺，搬运不受理；
积压行上的原因是 ONBOARD_FACTS_NOT_READY。整段窗口要落在断线后 30 秒以内，并且确实转过了几轮，否则这条在缺陷版本上也会绿：
缺陷版本恰恰是在这 30 秒里把车派出去的。

**第二个事实。** 车重连，完整握手走完回到 READY、会话代前进；那条搬运需求随即派给它。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$mes = $Context.MesIngest
$riot = $Context.Riot
$onboard = $Context.Onboard

# 缺陷版本把车派出去的那段：断线后 MaximumEvidenceAge（30 秒）以内。观察窗口要整段落在里面。
$evidenceAgeSeconds = 30
$observeSeconds = 20

$demand = [guid]::NewGuid()
$demandId = $demand.ToString('D')

function Get-NewPurposeTrace {
    $claims = [int](Invoke-L2Query -Connection $connection -Sql 'SELECT COUNT(*) AS N FROM VehiclePurposeClaims')[0].N
    $journeys = [int](Invoke-L2Query -Connection $connection -Sql 'SELECT COUNT(*) AS N FROM JourneyRuntimes')[0].N
    $intents = [int](Invoke-L2Query -Connection $connection -Sql 'SELECT COUNT(*) AS N FROM OrderIntents')[0].N
    return "claims=$claims journeys=$journeys intents=$intents"
}

$nothing = 'claims=0 journeys=0 intents=0'

function Get-BacklogReason {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].ReasonCode
}

# --- 1. 车停在「哪个站都不是」，路网拉起来 -----------------------------------------------------------------

$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'IDLE'
    movementState   = 'MT_FINISHED'
    speed           = 0
    currentPosition = 0
})

$null = Wait-L2Condition -Description 'the route graph engine finished a refresh cycle' `
    -Journal $journal -Criterion 'route-graph-ready' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql ("SELECT DesignEdgeCount, RuntimeRefreshedAt, StaleReason FROM RouteGraphSnapshots " +
                "WHERE MapId = $($Context.MapId)")
        if ($rows.Count -eq 0) { return $false }
        return [int]$rows[0].DesignEdgeCount -gt 0 -and
            $null -ne $rows[0].RuntimeRefreshedAt -and [string]$rows[0].RuntimeRefreshedAt -ne '' -and
            ($null -eq $rows[0].StaleReason -or [string]$rows[0].StaleReason -eq '')
    } `
    -Until { param($v) $v }

$generationBefore = [long]$onboard.Snapshot().body.sessionGeneration
$before = Get-NewPurposeTrace
$assertions.Add(
    'L2-ODN-01',
    '断线前：车载端 READY，库里还没有任何用途占有、旅程或订单意图（车不在任何站上）',
    ([string]$onboard.Snapshot().body.readiness -eq 'READY' -and $before -eq $nothing),
    "READY / $nothing",
    "$([string]$onboard.Snapshot().body.readiness) / $before")

# --- 2. 拔线，然后车到关卡、来一条适合它的需求 ----------------------------------------------------------------

$journal.Note('Onboard drops its link (the program is stopped); the vehicle then stands idle at the gate and a demand arrives.')
$null = $onboard.Command('Put', 'connection', @{ connected = $false })
$disconnectedAt = [DateTimeOffset]::UtcNow
$readsAtDisconnect = [long]$riot.Snapshot().body.mapStationReads

$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'IDLE'
    movementState   = 'MT_FINISHED'
    speed           = 0
    currentPosition = $Context.GateStationRiotId
})
$null = $mes.Command('Put', "demands/$($demand.ToString('N'))", @{
    sublot = "L2-ODN-$($Context.RunId)"; area = 'N1-3'
    eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})

# 另等：断线后的十几轮里有没有派出任何新用途。正常情形等满，拿最后一次读数。
$during = Wait-L2ConditionOrLast -Description 'a new purpose is given to the disconnected vehicle (it must not)' `
    -Journal $journal -Criterion 'new-purpose-while-disconnected' -TimeoutSeconds $observeSeconds `
    -Probe { Get-NewPurposeTrace } `
    -Until { param($v) $v -ne $nothing }
$windowEnd = [DateTimeOffset]::UtcNow
$windowSeconds = [math]::Round(($windowEnd - $disconnectedAt).TotalSeconds, 1)
$rounds = [long]$riot.Snapshot().body.mapStationReads - $readsAtDisconnect
$backlogReason = Get-BacklogReason
$journal.Observe('while-disconnected', $during,
    @{ windowSeconds = $windowSeconds; rounds = $rounds; backlogReason = $backlogReason })

$assertions.Add(
    'L2-ODN-02',
    "断线后的 $observeSeconds 秒、十几轮派车里没有任何用途占有、旅程或订单意图：空闲返回不承诺、搬运不受理（整段在断线后 $evidenceAgeSeconds 秒以内，至少转了 3 轮）",
    ($during -eq $nothing -and $windowSeconds -lt $evidenceAgeSeconds -and $rounds -ge 3),
    "$nothing / < $evidenceAgeSeconds s / >= 3 rounds",
    "$during / $windowSeconds s / $rounds rounds")

$assertions.Add(
    'L2-ODN-03',
    '那条需求留在积压里，原因是车载端事实没有就绪（ONBOARD_FACTS_NOT_READY）',
    ($backlogReason -eq 'ONBOARD_FACTS_NOT_READY'),
    'ONBOARD_FACTS_NOT_READY',
    $(if ($null -eq $backlogReason) { '(no backlog row)' } else { $backlogReason }))

# --- 3. 重连：握手就绪后恢复可派 ---------------------------------------------------------------------------

$journal.Note('Onboard reconnects through the full handshake.')
$reconnect = $onboard.Command('Put', 'connection', @{ connected = $true })
$assertions.Add(
    'L2-ODN-04',
    '重连走完完整握手：车载端回到 READY，会话代前进',
    ([string]$reconnect.body.readiness -eq 'READY' -and [long]$reconnect.body.sessionGeneration -gt $generationBefore),
    "READY / > $generationBefore",
    "$([string]$reconnect.body.readiness) / $([long]$reconnect.body.sessionGeneration)")

$takenBy = Wait-L2Condition -Description 'the waiting demand is taken once the vehicle is back' `
    -Journal $journal -Criterion 'demand-taken-after-reconnect' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql "SELECT AgvId FROM JourneyRuntimes WHERE DemandId = '$demandId'"
        if ($rows.Count -eq 0) { return $null }
        return [string]$rows[0].AgvId
    } `
    -Until { param($v) $null -ne $v }

$assertions.Add(
    'L2-ODN-05',
    '重连就绪后，那条需求派给了这台车',
    ($takenBy -eq $Context.AgvId),
    $Context.AgvId,
    $takenBy)

$journal.Note('车载端断线后不被派新用途；重连握手就绪后恢复可派。')
