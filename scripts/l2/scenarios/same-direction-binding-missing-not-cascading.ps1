#Requires -Version 7

<#
同向四类缺绑定不投运、不连带 WIRE_TO_GATE（批次10-02，control-server#546；规格 8.3 批次 10，REQ-0335、REQ-0342）。

本图需求集合只含 WIRE_TO_GATE 且已绑定（编排器默认前置装的预置配置：map 25、WIRE_TO_GATE → 210「关卡」），同向四类
DIE_TO_WIRE_STAGING、DIE_TO_OVEN、WIRE_TO_OPTICAL、WIRE_TO_NITROGEN 一个都没绑。假 MesIngest 先放四类各一条，再放一条
WIRE_TO_GATE，五条的 AREA 互不相同：

- 先等 WIRE_TO_GATE 那条被受理（L2-SDBM-01）。它发得最晚，受理它的那一轮读到的目录里四条未绑定的早已在场，
  所以「它受理了」证明派车轮确实跑过、有机会判那四条；再另等四条的积压原因都落到缺绑定——下面「四条都不受理」
  因此不是读一次就断言；
- 四类都不受理：积压原因都是 TASK_TYPE_BINDING_MISSING，没有受理行、旅程、订单意图（L2-SDBM-02）；
  /api/dashboard/dispatch-backlog 四条都列出、带中文说明（L2-SDBM-03）；不形成结构性派车阻断（L2-SDBM-04）；
- WIRE_TO_GATE 那条照常走完两段（L2-SDBM-05）；车空下来之后再转三轮，四条仍不受理、原因不变（L2-SDBM-06）。

批次10-01（control-server#545）之前这条也是绿的：缺绑定判据本来就排在「尚未可执行」之前。它测的是批次 6 已有的机制
在四类上照样成立，不是批次10-01 新加的行为——红绿对照见 PR。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SingleRow.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection
$backlogEndpoint = "http://127.0.0.1:$($Context.HealthPort)/api/dashboard/dispatch-backlog"

function New-Demand([string]$TaskType, [string]$Area) {
    $guid = [guid]::NewGuid()
    # MesIngest 报不带连字符的 demandId，服务端入口处归一化成规范 UUID：替身按前者发，断言按后者查。
    return [pscustomobject]@{
        TaskType = $TaskType
        Area     = $Area
        Wire     = $guid.ToString('N')
        Id       = $guid.ToString('D')
        Sublot   = "L2-SDBM-$Area-$($Context.RunId)"
    }
}

function Publish-Demand([object]$Demand) {
    $journal.Note("Publishing $($Demand.TaskType) demand $($Demand.Wire) (AREA $($Demand.Area)).")
    $null = $mes.Command('Put', "demands/$($Demand.Wire)", @{
        sublot      = $Demand.Sublot
        workType    = $Demand.TaskType
        area        = $Demand.Area
        eqp         = "EQP-L2-SDBM-$($Demand.Area)"
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
}

function Get-Stage([string]$DemandId) {
    $row = Read-L2SingleRow -Connection $connection -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$DemandId'"
    if ($null -eq $row) { return $null }
    return [string]$row.Stage
}

function Get-Intent([string]$DemandId, [string]$Purpose) {
    return Read-L2SingleRow -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status, DestinationStationId FROM OrderIntents WHERE DemandId = '$DemandId' AND Purpose = '$Purpose'"
}

# 四条未绑定需求此刻的样子：积压原因、受理时刻、受理行／旅程／订单意图的行数。
function Get-UnboundFacts([object[]]$Demands) {
    return , @($Demands | ForEach-Object {
            $id = $_.Id
            $row = (Invoke-L2Query -Connection $connection -Sql @"
SELECT (SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$id') AS ReasonCode,
       (SELECT AcceptedAt FROM JourneyBacklog WHERE DemandId = '$id') AS AcceptedAt,
       (SELECT COUNT(*) FROM AcceptedDemands WHERE DemandId = '$id')
     + (SELECT COUNT(*) FROM JourneyRuntimes WHERE DemandId = '$id')
     + (SELECT COUNT(*) FROM JourneyDemands WHERE DemandId = '$id')
     + (SELECT COUNT(*) FROM OrderIntents WHERE DemandId = '$id') AS AcceptanceRows
"@)[0]
            [pscustomobject]@{
                TaskType       = $_.TaskType
                ReasonCode     = [string]$row.ReasonCode
                AcceptedAt     = [string]$row.AcceptedAt
                AcceptanceRows = [int]$row.AcceptanceRows
            }
        })
}

function Test-AllUnboundRefused([object[]]$Facts) {
    return @($Facts | Where-Object {
            $_.ReasonCode -ceq 'TASK_TYPE_BINDING_MISSING' -and [string]::IsNullOrEmpty($_.AcceptedAt) -and $_.AcceptanceRows -eq 0
        }).Count -eq 4
}

function Format-UnboundFacts([object[]]$Facts) {
    return (@($Facts) | ForEach-Object {
            "$($_.TaskType)=$(if ($_.ReasonCode) { $_.ReasonCode } else { '(no backlog row)' })" +
            "$(if ($_.AcceptedAt) { ' ACCEPTED' } else { '' }) rows=$($_.AcceptanceRows)"
        }) -join '; '
}

# --- 1. 四类各一条（未绑定）先放，WIRE_TO_GATE（已绑定）随后 ------------------------------------------------

$unbound = @(
    New-Demand 'DIE_TO_WIRE_STAGING' 'N1-7'
    New-Demand 'DIE_TO_OVEN' 'C15-13'
    New-Demand 'WIRE_TO_OPTICAL' 'N2-1'
    New-Demand 'WIRE_TO_NITROGEN' 'N2-2'
)
$bound = New-Demand 'WIRE_TO_GATE' 'N1-3'
foreach ($demand in $unbound) { Publish-Demand $demand }
Publish-Demand $bound

# 垫底的事实：WIRE_TO_GATE 被受理，说明派车轮跑过了，而比它先到的四条那时已在目录里。
$boundStage = Wait-L2ConditionOrLast -Description 'the bound WIRE_TO_GATE demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'wire-to-gate-accepted' -TimeoutSeconds 90 `
    -Probe { Get-Stage $bound.Id } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$assertions.Add(
    'L2-SDBM-01', 'WIRE_TO_GATE（已绑定、最后发布）被受理：派车轮已经跑过，比它先发布的四条同向需求那时已在目录里',
    ($boundStage -eq 'AwaitingPickupArrival'), 'AwaitingPickupArrival', $(if ($boundStage) { $boundStage } else { '(no journey)' }))

# 受理之后再等四条的积压行都落到缺绑定：积压行与受理不一定同一次提交，另等，不直读。
$facts = Wait-L2ConditionOrLast -Description 'the four unbound same-direction demands were refused for a missing binding' `
    -Journal $journal -Criterion 'unbound-refused' -TimeoutSeconds 60 `
    -Probe { Get-UnboundFacts $unbound } -Until { param($v) Test-AllUnboundRefused $v }
$facts = @($facts)
$assertions.Add(
    'L2-SDBM-02', '四类都不受理：积压原因都是 TASK_TYPE_BINDING_MISSING，没有受理时刻，没有受理行、旅程、旅程归属、订单意图',
    (Test-AllUnboundRefused $facts),
    (($unbound | ForEach-Object { "$($_.TaskType)=TASK_TYPE_BINDING_MISSING rows=0" }) -join '; '),
    (Format-UnboundFacts $facts))

$backlogFact = Invoke-RestMethod -Uri $backlogEndpoint -NoProxy -TimeoutSec 10
$listed = @($unbound | ForEach-Object {
        $id = $_.Id
        $row = @($backlogFact.backlog | Where-Object { [string]$_.demandId -eq $id })
        [pscustomobject]@{
            TaskType = $_.TaskType
            Rows     = $row.Count
            Reason   = if ($row.Count -eq 1) { [string]$row[0].reasonCode } else { '' }
            Chinese  = $row.Count -eq 1 -and [string]$row[0].reasonDescription -match '\p{IsCJKUnifiedIdeographs}'
        }
    })
$assertions.Add(
    'L2-SDBM-03', '/api/dashboard/dispatch-backlog 四条都列出，原因码 TASK_TYPE_BINDING_MISSING 带中文说明',
    (@($listed | Where-Object { $_.Rows -eq 1 -and $_.Reason -ceq 'TASK_TYPE_BINDING_MISSING' -and $_.Chinese }).Count -eq 4),
    'four rows / TASK_TYPE_BINDING_MISSING / Chinese description',
    (($listed | ForEach-Object { "$($_.TaskType): $($_.Rows) row(s) $($_.Reason)$(if ($_.Chinese) { ' +zh' } else { '' })" }) -join '; '))

$ids = ($unbound | ForEach-Object { "'$($_.Id)'" }) -join ','
$blocks = [int](Invoke-L2Query -Connection $connection -Sql "SELECT COUNT(*) AS N FROM StructuralDispatchBlocks WHERE DemandId IN ($ids)")[0].N
$assertions.Add(
    'L2-SDBM-04', 'StructuralDispatchBlocks 没有这四条的行（含已清除的）：缺绑定是配置造成的不投运，不是结构性告警',
    ($blocks -eq 0), '0', $blocks)

# --- 2. WIRE_TO_GATE 同一次运行内走完两段 ----------------------------------------------------------------

$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { Get-Intent $bound.Id 'TO_PICKUP' } -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = $pickupIntent.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.PickupStationRiotId; processingOrder = $false; clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{ orderState = 5 })

$null = Wait-L2Condition -Description 'the journey reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage $bound.Id } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$gateIntent = Wait-L2Condition -Description 'the TO_GATE intent was confirmed' `
    -Journal $journal -Criterion 'to-gate-intent' -TimeoutSeconds 60 `
    -Probe { Get-Intent $bound.Id 'TO_GATE' } -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
$null = $riot.Command('Put', "orders/$($gateIntent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = $gateIntent.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.GateStationRiotId; processingOrder = $false; clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($gateIntent.UpperId)", @{ orderState = 5 })

$stage = Wait-L2ConditionOrLast -Description 'the WIRE_TO_GATE journey completed at the gate' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage $bound.Id } -Until { param($v) $v -eq 'Completed' }
$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-SDBM-05', 'WIRE_TO_GATE 在同一次运行内被受理并走完两段：同向四类缺绑定不连带它',
    ($stage -eq 'Completed' -and [string]$gateIntent.DestinationStationId -eq [string]$Context.GateStationRiotId -and $riotOrders.Count -eq 2),
    "Completed / gate leg to $($Context.GateStationRiotId) / 2 RIoT orders",
    "$stage / gate leg to $($gateIntent.DestinationStationId) / $($riotOrders.Count) RIoT orders")

# --- 3. 车空下来之后：四条仍不受理 --------------------------------------------------------------------------

# 车空着、它们有机会被派的时候再转三轮：仍然不受理，原因仍是那一个。
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$after = Get-UnboundFacts $unbound
$assertions.Add(
    'L2-SDBM-06', '车空下来之后又转三轮：四条仍不受理，原因仍是 TASK_TYPE_BINDING_MISSING',
    (Test-AllUnboundRefused $after),
    (($unbound | ForEach-Object { "$($_.TaskType)=TASK_TYPE_BINDING_MISSING rows=0" }) -join '; '),
    (Format-UnboundFacts $after))

$journal.Note('同向四类缺绑定各自不投运；WIRE_TO_GATE 同一次运行内受理并走完。')
