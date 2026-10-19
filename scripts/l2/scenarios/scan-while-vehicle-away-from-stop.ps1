#Requires -Version 7

<#
车不在站上时扫码不开仓（control-server#783）：旅程停在等录入，车已被人开离取货站、在别处停稳，操作员扫码。

10-19 agv03 一事读码坐实的形状：补偿放回、修复续行、重连、服务端重启、急停释放之后，旅程回到等录入，而服务端再也不比对 RIoT 的
当前站，扫码照样下装货命令，车载端第一次开锁也不设闸——车在站外开仓门。修法放在开门的那一处：下装货命令之前读一次 RIoT，
车不在这个停靠上就拒收这次录入。所以不论旅程是怎么回到等录入的，到了这一处车看到的都一样，这条场景造的就是那一处：到站、
车被开走、扫码。

补偿那一段这一层造不出来：合成车载端不自己发起异常处置会话（见 load-result-requires-recovery.ps1 的说明）。补偿放回之后的
同一个判据由 L1 走真实入站处理器证（RecoveryEndingReleasesBlockedJourneyTests.AfterACompensation...），重连、重启、急停释放
与「车回到站上再扫能装」也在 L1（SlotCommandStationRecheckTests）。合成车载端按请求键缓存应答，同一请求再答一次会发出同一个
messageId，被服务端当重复报文吞掉，所以「回到站上再扫一次」这一层也造不出来，不硬造。

判据：
  L2-SNA-01 前提：车到站，旅程停在等录入，录入请求挂在车上
  L2-SNA-02 扫码答 SublotRejected / STATION_MISMATCH
  L2-SNA-03 旅程仍在等录入，挂 SLOT_COMMAND_VEHICLE_NOT_AT_STOP
  L2-SNA-04 运行时又转了几轮之后：车一条仓位命令都没收到，库里没有装货操作也没有装货命令
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

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SUBLOT-$($Context.RunId)"

function Get-Runtime {
    # HasConversion<string>：阶段列存的是枚举成员名。
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Stage, BlockReasonCode FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-HeldEntryRequest {
    return @(@($onboard.Snapshot().body.pending) | Where-Object {
            [string]$_.messageType -eq 'SublotEntryRequested'
        }) | Select-Object -Last 1
}

function Get-Refusal {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'SublotRejected'"
    if ($rows.Count -eq 0) { return $null }
    return [string]($rows[-1].PayloadJson | ConvertFrom-Json -Depth 20).payload.problem.reasonCode
}

function Get-PeerInbound([string]$messageType) {
    return @(@($onboard.Snapshot().body.wire) | Where-Object {
            [string]$_.direction -eq 'in' -and [string]$_.messageType -eq $messageType
        })
}

function Get-Count([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    return [int]$rows[0].Total
}

# 扫码由场景决定什么时候答：要在车被开走之后才答。
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Manual' })

# --- 1. 受理、派车、到站 ---------------------------------------------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { $r = Get-Runtime; if ($r) { [string]$r.Stage } else { $null } } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-UpperId -purpose 'TO_PICKUP'; if ($row -and $row.Status -eq 'CONFIRMED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }

$journal.Note('Vehicle drives to the pickup station and comes to rest there.')
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{
    orderState        = 3
    executeVehicleKey = $Context.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $pickupIntent.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey       = $Context.VehicleKey
    procState        = 'IDLE'
    movementState    = 'MT_FINISHED'
    speed            = 0
    currentPosition  = $Context.PickupStationRiotId
    processingOrder  = $false
    clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{ orderState = 5 })

$waiting = Wait-L2Condition -Description 'the arrival was trusted and the stop waits for a sublot' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { $r = Get-Runtime; if ($r) { [string]$r.Stage } else { $null } } -Until { param($v) $v -eq 'AwaitingSublot' }
# 录入请求先于阶段落库，送到车上要等线上送达：单独等它（README 第 14 条）。
$held = Wait-L2ConditionOrLast -Description 'the entry request is held on the peer' `
    -Journal $journal -Criterion 'entry-held' -TimeoutSeconds 60 `
    -Probe { Get-HeldEntryRequest } -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-SNA-01', '前提：车到站，旅程停在等录入，录入请求挂在车上',
    ($waiting -eq 'AwaitingSublot' -and $null -ne $held),
    'AwaitingSublot / 录入请求挂着',
    "$waiting / $(if ($held) { [string]$held.key } else { '(no entry request held)' })")
if ($null -eq $held) {
    return
}

# --- 2. 车被人开走，在别处停稳；然后有人扫码 --------------------------------------------------------

# 关卡站是这张图上存在的另一个站：车停稳在那里，读数新鲜、速度为零、在线、地图对——除了站号，到站判定里车辆位置那一部分全成立。
$journal.Note("Someone drives the vehicle off the pickup station; it comes to rest at station $($Context.GateStationRiotId).")
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey       = $Context.VehicleKey
    procState        = 'IDLE'
    movementState    = 'MT_FINISHED'
    speed            = 0
    currentPosition  = $Context.GateStationRiotId
    processingOrder  = $false
    clearOrderTaskId = $true
})

$journal.Note('The operator scans the sublot while the vehicle stands away from the stop.')
$null = $onboard.Command('Put', "answer/$([string]$held.key)", @{ completed = $true })

$refusal = Wait-L2ConditionOrLast -Description 'the entry was refused' `
    -Journal $journal -Criterion 'entry-refused' -TimeoutSeconds 60 `
    -Probe { Get-Refusal } -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-SNA-02', '扫码答 SublotRejected / STATION_MISMATCH',
    ($refusal -eq 'STATION_MISMATCH'), 'STATION_MISMATCH', ($refusal ?? '(no SublotRejected)'))

# 阻断码与拒收不一定落在同一次提交里，单独等（README 第 14 条）。
$held = Wait-L2ConditionOrLast -Description 'the journey names the vehicle as away from its stop' `
    -Journal $journal -Criterion 'journey-block' -TimeoutSeconds 30 `
    -Probe { Get-Runtime } -Until { param($v) $null -ne $v -and [string]$v.BlockReasonCode -eq 'SLOT_COMMAND_VEHICLE_NOT_AT_STOP' }
$assertions.Add(
    'L2-SNA-03', '旅程仍在等录入，挂 SLOT_COMMAND_VEHICLE_NOT_AT_STOP',
    ($null -ne $held -and [string]$held.Stage -eq 'AwaitingSublot' -and [string]$held.BlockReasonCode -eq 'SLOT_COMMAND_VEHICLE_NOT_AT_STOP'),
    'AwaitingSublot / SLOT_COMMAND_VEHICLE_NOT_AT_STOP',
    $(if ($held) { "$($held.Stage) / $($held.BlockReasonCode)" } else { '(no runtime)' }))

# 否定判据要有界：等运行时确实又转了几轮，再说「它没有开门」。命令先落库再上线，所以库与车上两边都看。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$slotCommands = @(Get-PeerInbound 'SlotOperationCommand').Count
$operations = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId'"
$commands = Get-Count "SELECT COUNT(*) AS Total FROM ProtocolOutbox WHERE MessageType = 'SlotOperationCommand'"
$assertions.Add(
    'L2-SNA-04', '运行时又转了四轮：车没收到仓位命令，库里没有装货操作、没有装货命令',
    ($slotCommands -eq 0 -and $operations -eq 0 -and $commands -eq 0),
    '0 / 0 / 0', "$slotCommands / $operations / $commands")

$journal.Note('Scenario finished.')
