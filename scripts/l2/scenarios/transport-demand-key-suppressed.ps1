#Requires -Version 7

<#
按业务键抑制（批次7-05，control-server#210；REQ-0155、REQ-0156、REQ-0211）。

  1. 需求 D1（SUBLOT S1）受理后本地取消：到站没人扫码，站点期限到期，服务端以 CANCELLED_BY_STATION_TIMEOUT 结束本站。
     断言抑制行存在、键为 S1|WIRE_TO_GATE、需求与码是 D1 的。
  2. 假 MesIngest 撤下 D1、以新 DemandId D2 发同一个 S1。断言 D2 从未受理、积压原因 TRANSPORT_DEMAND_KEY_SUPPRESSED、
     /api/dashboard/dispatch-backlog 带中文说明列出它、没有它的订单意图。
  3. 同时放一条无关需求 D3（S3），比 D2 晚创建、排在它后面。断言 D3 被受理并走完——整轮没有被 D2 卡住。
     第二事实：D2 的积压原因在 D3 走完之后再读一次（Wait-L2ConditionOrLast 等到一次晚于 D3 结束的判定），不读一次就断言。
  4. GONE 半边：车忙于 D3 时放入 D4（S4），在它被受理前撤下，再以新 DemandId D5 发同一个 S4。断言 D3 完成后 D5 照常受理，
     键 S4 没有抑制行——目录消失不是本地取消。

缺陷版本上（本票之前的 fp/v2-impl）红在第 2、3 步：D2 过完全部判据，唯一那辆车被选中后在受理存储层撞上业务键唯一索引、
抛 BusinessIdentityConflictException、记 2124 退出本轮；每一轮同样如此，于是排在它后面的 D3 永远没车接。第 3 步用
Wait-L2ConditionOrLast 等 D3，等不到时照实记红并在那里收尾，而不是让整趟运行在一个超时上抛掉。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$onboard = $Context.Onboard
$connection = $Context.Connection

$backlogEndpoint = "http://127.0.0.1:$($Context.HealthPort)/api/dashboard/dispatch-backlog"

function New-Demand([string]$suffix) {
    $guid = [guid]::NewGuid()
    return [pscustomobject]@{ Wire = $guid.ToString('N'); Id = $guid.ToString('D'); Label = $suffix }
}

$d1 = New-Demand 'D1'
$d2 = New-Demand 'D2'
$d3 = New-Demand 'D3'
$d4 = New-Demand 'D4'
$d5 = New-Demand 'D5'
$s1 = "L2-TDK-$($Context.RunId)-S1"
$s3 = "L2-TDK-$($Context.RunId)-S3"
$s4 = "L2-TDK-$($Context.RunId)-S4"

function Test-L2Null($value) {
    return $null -eq $value
}

# ControlServerDbContext 把 DateTimeOffset 存成文本；判据要拿它们比先后，所以统一转成 DateTimeOffset。
function ConvertTo-Instant($value) {
    if (Test-L2Null $value) { return $null }
    if ($value -is [DateTimeOffset]) { return $value }
    if ($value -is [DateTime]) { return [DateTimeOffset]$value }
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

function Get-Runtime([string]$demandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Backlog([string]$demandId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT ReasonCode, AcceptedAt, FirstSeenAt, LastSeenAt FROM JourneyBacklog WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Intent([string]$demandId, [string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Count([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    return [int]$rows[0].Total
}

# 不要写成 @(Invoke-L2Query ...)：它以 `return , $rows` 交回整张表，外面再包一层 @() 就成了「一个元素是整张表」，
# 零行、一行、两行数出来都是 1——「全库只有一条抑制」那条判据会在写了两条时照样绿。原样转交同一个形状。
function Get-Suppressions {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql 'SELECT TransportDemandKey, DemandId, ReasonCode, SuppressedAt FROM TransportDemandSuppressions'
    return , $rows
}

# createdAt 决定同一轮里的任务次序（先见先派，再按创建时刻）：D2 要排在 D3 前面，缺陷才会在 D3 之前发作。
function Publish-Demand($demand, [string]$sublot, [DateTimeOffset]$createdAt) {
    $journal.Note("Publishing $($demand.Label) $($demand.Wire) on sublot $sublot.")
    $null = $mes.Command('Put', "demands/$($demand.Wire)", @{
        sublot      = $sublot
        area        = 'N1-3'
        eqp         = 'EQP-L2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
        createdAt   = $createdAt.ToString('o')
    })
}

function Remove-Demand($demand) {
    $journal.Note("MesIngest withdraws $($demand.Label) $($demand.Wire).")
    $null = $mes.Command('Delete', "demands/$($demand.Wire)", @{})
}

# 车跑一条单：接单、行驶、停在终点。
function Invoke-Leg($intent, [int]$stationRiotId) {
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{
        orderState        = 3
        executeVehicleKey = $Context.VehicleKey
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey      = $Context.VehicleKey
        procState       = 'RUNNING'
        movementState   = 'MT_RUNNING'
        speed           = 0.8
        processingOrder = $true
        orderTaskId     = $intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey       = $Context.VehicleKey
        procState        = 'IDLE'
        movementState    = 'MT_FINISHED'
        speed            = 0
        currentPosition  = $stationRiotId
        processingOrder  = $false
        clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })
}

function Wait-ConfirmedIntent([string]$demandId, [string]$purpose, [string]$criterion) {
    return Wait-L2Condition -Description "the $purpose intent of $demandId was confirmed" `
        -Journal $journal -Criterion $criterion -TimeoutSeconds 60 `
        -Probe { Get-Intent $demandId $purpose } -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }
}

$baseCreatedAt = [DateTimeOffset]::UtcNow.AddMinutes(-30)

# --- 1. D1 受理后被站点期限本地取消 --------------------------------------------------------------------

$journal.Note('Synthetic peer stops answering sublot entry requests: nobody comes to scan D1.')
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Silent' })
Publish-Demand $d1 $s1 $baseCreatedAt
$null = Wait-L2Condition -Description 'D1 was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'd1-accepted' -TimeoutSeconds 90 `
    -Probe { $r = Get-Runtime $d1.Id; if ($r) { [string]$r.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }
Invoke-Leg (Wait-ConfirmedIntent $d1.Id 'TO_PICKUP' 'd1-to-pickup-intent') $Context.PickupStationRiotId

$d1Ended = Wait-L2Condition -Description 'the station deadline ended D1' `
    -Journal $journal -Criterion 'd1-ended' -TimeoutSeconds 90 `
    -Probe { Get-Runtime $d1.Id } -Until { param($v) $v -and [string]$v.Stage -eq 'Completed' }
# 抑制与旅程 Completed 是同一次提交（PickupStopTermination 暂存、引擎一次保存），所以等到阶段之后读是安全的。
$suppressions = Get-Suppressions
$d1Key = "$s1|WIRE_TO_GATE"
$assertions.Add(
    'L2-TDK-01', 'D1 被站点期限本地取消，同一次提交里按业务键写下抑制：键 S1|WIRE_TO_GATE、需求 D1、码 CANCELLED_BY_STATION_TIMEOUT',
    ([string]$d1Ended.BlockReasonCode -eq 'CANCELLED_BY_STATION_TIMEOUT' -and $suppressions.Count -eq 1 -and
        [string]$suppressions[0].TransportDemandKey -eq $d1Key -and [string]$suppressions[0].DemandId -eq $d1.Id -and
        [string]$suppressions[0].ReasonCode -eq 'CANCELLED_BY_STATION_TIMEOUT'),
    "CANCELLED_BY_STATION_TIMEOUT / 1 row $d1Key $($d1.Id) CANCELLED_BY_STATION_TIMEOUT",
    "$($d1Ended.BlockReasonCode) / $($suppressions.Count) row(s) $(($suppressions | ForEach-Object { "$($_.TransportDemandKey) $($_.DemandId) $($_.ReasonCode)" }) -join '; ')")

# --- 2 与 3. D1 撤下，D2 以新 DemandId 发同一个 S1；无关的 D3 排在它后面 ------------------------------------

$null = $onboard.Command('Put', 'policy', @{ sublot = 'Auto' })
Remove-Demand $d1
Publish-Demand $d2 $s1 $baseCreatedAt.AddMinutes(1)
Publish-Demand $d3 $s3 $baseCreatedAt.AddMinutes(2)

$d3Accepted = Wait-L2ConditionOrLast -Description 'the unrelated D3 was accepted in spite of D2 ahead of it' `
    -Journal $journal -Criterion 'd3-accepted' -TimeoutSeconds 60 `
    -Probe { $r = Get-Runtime $d3.Id; if ($r) { [string]$r.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$d2Backlog = Get-Backlog $d2.Id
$d2Accepted = Get-Count "SELECT COUNT(*) AS Total FROM AcceptedDemands WHERE DemandId = '$($d2.Id)'"
$d2Intents = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$($d2.Id)'"
# 不得「不触发即通过」：积压行存在才说明 D2 真的出现在目录里并被判定过。
# 这里直读而不另等，是因为 D2 的判定在因果上先于 D3 的受理落库：D3 发布时 D2 已在目录里，而且 D2 创建得更早、排在前面；
# 同一轮里没人出价的 D2 那条判定，随 D3 受理之前那一次保存一起落库（DispatchRoundRunner：受理前先存积压行）。
# 「车空下来之后仍被判为抑制」是另一次写入，那一条在下面用 Wait-L2ConditionOrLast 另等。
$assertions.Add(
    'L2-TDK-02', 'D2（同一个 S1、新 DemandId）被判定过但从未受理：积压原因 TRANSPORT_DEMAND_KEY_SUPPRESSED，没有受理行、没有订单意图',
    ($null -ne $d2Backlog -and [string]$d2Backlog.ReasonCode -eq 'TRANSPORT_DEMAND_KEY_SUPPRESSED' -and
        (Test-L2Null $d2Backlog.AcceptedAt) -and $d2Accepted -eq 0 -and $d2Intents -eq 0),
    'TRANSPORT_DEMAND_KEY_SUPPRESSED / not accepted / 0 accepted rows / 0 intents',
    "$(if ($d2Backlog) { "$($d2Backlog.ReasonCode) / AcceptedAt=$($d2Backlog.AcceptedAt)" } else { '(no backlog row)' }) / $d2Accepted accepted rows / $d2Intents intents")

$backlogFact = Invoke-RestMethod -Uri $backlogEndpoint -NoProxy -TimeoutSec 10
$row = @($backlogFact.backlog | Where-Object { [string]$_.demandId -eq $d2.Id })
$assertions.Add(
    'L2-TDK-03', '/api/dashboard/dispatch-backlog 列出 D2，原因码 TRANSPORT_DEMAND_KEY_SUPPRESSED 带中文说明',
    ($row.Count -eq 1 -and [string]$row[0].reasonCode -eq 'TRANSPORT_DEMAND_KEY_SUPPRESSED' -and
        [string]$row[0].reasonDescription -match '\p{IsCJKUnifiedIdeographs}'),
    'one row / TRANSPORT_DEMAND_KEY_SUPPRESSED / Chinese description',
    $(if ($row.Count -ne 1) { "$($row.Count) rows" } else { "$($row[0].reasonCode) / $($row[0].reasonDescription)" }))

$assertions.Add(
    'L2-TDK-04', '排在 D2 后面的无关需求 D3 在同一段时间里被受理——整轮没有被 D2 卡住',
    ($d3Accepted -eq 'AwaitingPickupArrival'),
    'AwaitingPickupArrival', $(if ($d3Accepted) { $d3Accepted } else { '(no journey)' }))
if ($d3Accepted -ne 'AwaitingPickupArrival') {
    $journal.Note('D3 was never accepted; the round is stuck behind D2 and the rest of the scenario cannot run.')
    return
}

# --- 4. GONE 半边：车忙于 D3 时，D4 出现又在受理前消失，D5 以新 DemandId 发同一个 S4 ----------------------------

Publish-Demand $d4 $s4 $baseCreatedAt.AddMinutes(3)
$d4Judged = Wait-L2Condition -Description 'D4 was judged while the vehicle is busy with D3' `
    -Journal $journal -Criterion 'd4-judged' -TimeoutSeconds 60 `
    -Probe { Get-Backlog $d4.Id } -Until { param($v) $null -ne $v }
Remove-Demand $d4
$d4Gone = Wait-L2Condition -Description 'the round saw D4 leave the catalog unaccepted' `
    -Journal $journal -Criterion 'd4-left-catalog' -TimeoutSeconds 60 `
    -Probe { Get-Backlog $d4.Id } -Until { param($v) $v -and [string]$v.ReasonCode -eq 'DEMAND_LEFT_CATALOG' }
Publish-Demand $d5 $s4 $baseCreatedAt.AddMinutes(4)

Invoke-Leg (Wait-ConfirmedIntent $d3.Id 'TO_PICKUP' 'd3-to-pickup-intent') $Context.PickupStationRiotId
$null = Wait-L2Condition -Description 'D3 loaded and left for the gate' `
    -Journal $journal -Criterion 'd3-gate-leg' -TimeoutSeconds 120 `
    -Probe { $r = Get-Runtime $d3.Id; if ($r) { [string]$r.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingGateArrival' }
Invoke-Leg (Wait-ConfirmedIntent $d3.Id 'TO_GATE' 'd3-to-gate-intent') $Context.GateStationRiotId
$d3Done = Wait-L2Condition -Description 'D3 completed at the gate' `
    -Journal $journal -Criterion 'd3-completed' -TimeoutSeconds 120 `
    -Probe { Get-Runtime $d3.Id } -Until { param($v) $v -and [string]$v.Stage -eq 'Completed' }
$d3Status = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$($d3.Id)'"
$assertions.Add(
    'L2-TDK-05', 'D3 走完：旅程 Completed、需求 Succeeded',
    ([string]$d3Done.Stage -eq 'Completed' -and $d3Status.Count -eq 1 -and [string]$d3Status[0].Status -eq 'Succeeded'),
    'Completed / Succeeded',
    "$($d3Done.Stage) / $(if ($d3Status.Count -eq 1) { $d3Status[0].Status } else { '(no demand row)' })")
$d3DoneAt = ConvertTo-Instant $d3Done.UpdatedAt

# 第二事实：D3 走完之后车空了，下一轮起空闲车又会判 D2。等到一次晚于 D3 结束的判定，再断言它仍是被抑制——
# 这一读与 D3 的完成不在同一次提交里（积压行是派车轮的另一次写入），所以要等，不能顺手读。
$d2Again = Wait-L2ConditionOrLast -Description 'D2 was judged again after D3 completed' `
    -Journal $journal -Criterion 'd2-judged-after-d3' -TimeoutSeconds 60 `
    -Probe { Get-Backlog $d2.Id } `
    -Until { param($v) $v -and (ConvertTo-Instant $v.LastSeenAt) -gt $d3DoneAt }
$d2AgainSeen = if ($d2Again) { ConvertTo-Instant $d2Again.LastSeenAt } else { $null }
$assertions.Add(
    'L2-TDK-06', 'D3 走完、车空下来之后 D2 被再判一次，仍是 TRANSPORT_DEMAND_KEY_SUPPRESSED、仍未受理',
    ($null -ne $d2Again -and $d2AgainSeen -gt $d3DoneAt -and
        [string]$d2Again.ReasonCode -eq 'TRANSPORT_DEMAND_KEY_SUPPRESSED' -and (Test-L2Null $d2Again.AcceptedAt)),
    "TRANSPORT_DEMAND_KEY_SUPPRESSED / not accepted / judged after $($d3DoneAt.ToString('o'))",
    $(if ($d2Again) { "$($d2Again.ReasonCode) / AcceptedAt=$($d2Again.AcceptedAt) / LastSeenAt=$($d2AgainSeen.ToString('o'))" } else { '(no backlog row)' }))

$d5Stage = Wait-L2ConditionOrLast -Description 'D5 was accepted once the vehicle was free' `
    -Journal $journal -Criterion 'd5-accepted' -TimeoutSeconds 60 `
    -Probe { $r = Get-Runtime $d5.Id; if ($r) { [string]$r.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$suppressions = Get-Suppressions
$d4Key = "$s4|WIRE_TO_GATE"
$assertions.Add(
    'L2-TDK-07', 'GONE 半边：D4 在受理前离开目录（DEMAND_LEFT_CATALOG），同一个 S4 的新 DemandId D5 照常受理；S4 没有抑制行，全库仍只有 D1 那一条',
    ([string]$d4Gone.ReasonCode -eq 'DEMAND_LEFT_CATALOG' -and (Test-L2Null $d4Gone.AcceptedAt) -and
        $d5Stage -eq 'AwaitingPickupArrival' -and
        @($suppressions | Where-Object { [string]$_.TransportDemandKey -eq $d4Key }).Count -eq 0 -and
        $suppressions.Count -eq 1),
    "DEMAND_LEFT_CATALOG / D5 AwaitingPickupArrival / 0 rows on $d4Key / 1 row in all",
    "$($d4Gone.ReasonCode) (judged first as $($d4Judged.ReasonCode)) / D5 $(if ($d5Stage) { $d5Stage } else { '(no journey)' }) / $(@($suppressions | Where-Object { [string]$_.TransportDemandKey -eq $d4Key }).Count) rows on $d4Key / $($suppressions.Count) in all")

$journal.Note('Scenario finished.')
