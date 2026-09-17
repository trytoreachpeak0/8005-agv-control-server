#Requires -Version 7

<#
所需分组暂时空仓不足（control-server#73，规格 8.3 批次 4 场景②）。

握手种子把本车 REAR 组的仓全报成 OCCUPIED，N1-3 指 REAR，放一条要 2 个花篮的 N1-3 需求。断言：
  - 整车 FRONT 组的仓全空着，放行旧的 I8 取仓足够装下，但需求不受理；
  - 积压原因是「所需分组暂时空仓不足」（SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE），几轮之后仍是，不建单；
  - StructuralDispatchBlocks 里没有这条需求的行：这是正常积压（REQ-0210），不是结构性问题。
然后让 REAR 组编号最小的两个仓变空，断言下一轮受理、目标仓就是这两个（默认模型下 [5,6]）。

「变空」怎么做：服务端只从会话的两份握手快照读可用仓，而合成对端没有运行中改逐仓状态的入口（README「SlotStates」），
所以按票面的第二条路走——断线、换种子、重连：停掉原对端进程，用同样的参数加新的逐仓种子在同一端口重起一个，
它会以新会话完整握手。重起用到编排器的几项局部量（对端目录、凭据、端口、日志目录、重起进程清单），场景经 PowerShell
的动态作用域读它们；读之前逐项检查，缺一项就直接报错，不猜。重起的进程登记进 $restartedHandles，由编排器收尾时停掉。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SlotGroups.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection

# L2-PACKAGE 每篮 4 盒，8 盒就是 2 个花篮。
$basketCount = 2
$maxBoxCount = 8
$waitingReason = 'SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE'

function Get-Runtime([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM JourneyRuntimes WHERE DemandId = '$DemandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Count([string]$Sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $Sql
    return [int]$rows[0].Total
}

# --- 0. 前置：种子占住的正是本车 REAR 组，FRONT 组全空 ---------------------------------------------------

$positions = Get-L2VehicleSlotPositions -Connection $connection -AgvId $Context.AgvId
if ($null -eq $positions) { throw "Vehicle $($Context.AgvId) has no resolvable slot model; the preseed did not bind it." }
$rearSlots = @($positions.Positions.GetEnumerator() | Where-Object { $_.Value -ceq 'REAR' } | ForEach-Object { [int]$_.Key } | Sort-Object)
$frontSlots = @($positions.Positions.GetEnumerator() | Where-Object { $_.Value -ceq 'FRONT' } | ForEach-Object { [int]$_.Key } | Sort-Object)
$available = Get-L2AvailableSlots -Connection $connection -AgvId $Context.AgvId
$assertions.Add(
    'L2-SGF-01', "握手种子下本车 REAR 组没有可用仓、FRONT 组全部可用，且 FRONT 组足够装下 $basketCount 个花篮",
    (($available -join ',') -eq ($frontSlots -join ',') -and $rearSlots.Count -ge $basketCount -and $frontSlots.Count -ge $basketCount),
    "available [$($frontSlots -join ',')] = FRONT group; REAR group [$($rearSlots -join ',')] all unavailable",
    "available [$($available -join ',')]; FRONT [$($frontSlots -join ',')]; REAR [$($rearSlots -join ',')]")

# --- 1. 需求等在「所需分组暂时空仓不足」，不借 FRONT 组 --------------------------------------------------

$guid = [guid]::NewGuid()
$demandId = $guid.ToString('D')
$sublot = "L2-SGF-$($Context.RunId)"
$journal.Note("Publishing demand $($guid.ToString('N')) (sublot $sublot, area N1-3, $maxBoxCount boxes).")
$null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-SGF-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = $maxBoxCount
})

$reason = Wait-L2Condition -Description "the demand is held back as $waitingReason" `
    -Journal $journal -Criterion 'backlog-reason' -TimeoutSeconds 90 `
    -Probe { $row = Get-L2JourneyBacklogRow -Connection $connection -DemandId $demandId; if ($row) { [string]$row.ReasonCode } else { $null } } `
    -Until { param($v) $v -ceq $waitingReason }
# A few more rounds, so "not accepted" is a steady state rather than the first round's verdict.
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal

$backlog = Get-L2JourneyBacklogRow -Connection $connection -DemandId $demandId
$reasonAfterRounds = if ($backlog) { [string]$backlog.ReasonCode } else { '(no backlog row)' }
$assertions.Add(
    'L2-SGF-02', '积压原因为「所需分组暂时空仓不足」，再过三轮仍是',
    ($reason -ceq $waitingReason -and $reasonAfterRounds -ceq $waitingReason),
    $waitingReason, "first $reason, after three rounds $reasonAfterRounds")

$accepted = Get-Count "SELECT COUNT(*) AS Total FROM AcceptedDemands WHERE DemandId = '$demandId'"
$journeys = Get-Count "SELECT COUNT(*) AS Total FROM JourneyRuntimes WHERE DemandId = '$demandId'"
$riotOrders = @($riot.Snapshot().body.orders).Count
$assertions.Add(
    'L2-SGF-03', '整车 FRONT 组空着但不受理：没有受理行、没有旅程、没有建 RIoT 单',
    ($accepted -eq 0 -and $journeys -eq 0 -and $riotOrders -eq 0),
    'AcceptedDemands 0, JourneyRuntimes 0, RIoT orders 0',
    "AcceptedDemands $accepted, JourneyRuntimes $journeys, RIoT orders $riotOrders")

$blocks = Get-L2StructuralDispatchBlock -Connection $connection -DemandId $demandId -IncludeCleared
$assertions.Add(
    'L2-SGF-04', 'StructuralDispatchBlocks 没有这条需求的行（正常积压，不是结构性问题）',
    ($blocks.Count -eq 0), 0, $blocks.Count)

# --- 2. 断线、换种子、重连：REAR 组编号最小的两个仓变空 ---------------------------------------------------

$freed = @($rearSlots | Select-Object -First $basketCount)
$stillOccupied = @($rearSlots | Select-Object -Skip $basketCount)
$journal.Note("Reseeding the synthetic peer: slots [$($freed -join ',')] become EMPTY, [$($stillOccupied -join ',')] stay OCCUPIED.")

# The orchestrator's locals this needs, read through the dynamic scope the scenario runs in. Checked up front: a
# missing one would otherwise surface as a peer that never connects.
foreach ($name in 'onboardDirectory', 'credential', 'ControlPort', 'FakeOnboardPort', 'logRoot', 'restartedHandles') {
    if ($null -eq (Get-Variable -Name $name -ValueOnly -ErrorAction Ignore)) {
        throw "Reseeding the synthetic peer needs the orchestrator's `$$name, which is not visible from the scenario."
    }
}
$reseedArguments = @(
    "--FakeOnboard:port=$FakeOnboardPort",
    '--FakeOnboard:instanceId=l2-onboard',
    "--FakeOnboard:Peer:port=$ControlPort",
    "--FakeOnboard:Peer:agvId=$($Context.AgvId)")
if ($stillOccupied.Count -gt 0) {
    $reseedArguments += ConvertTo-SlotStateArguments -Where 'slot-group-temporarily-full (reseed)' `
        -Entries @($stillOccupied | ForEach-Object { @{ SlotNo = $_; physicalState = 'OCCUPIED' } })
}

& $Context.StopComponent 'fake-onboard'
$reseeded = Start-L2Process -Name 'fake-onboard-reseeded' `
    -FilePath (Join-Path $onboardDirectory 'ControlServer.FakeOnboard.exe') `
    -ArgumentList $reseedArguments `
    -WorkingDirectory $onboardDirectory `
    -Environment @{ 'CONTROL_SERVER_ONBOARD_CREDENTIAL' = $credential } `
    -LogRoot $logRoot |
    ForEach-Object { $_ | Add-Member -NotePropertyName Order -NotePropertyValue 6 -PassThru }
$restartedHandles.Add($reseeded)

$null = Wait-L2Condition -Description 'the reseeded synthetic peer reached READY' -Journal $journal `
    -Criterion 'onboard-readiness' -TimeoutSeconds 60 -Component $reseeded -Port $FakeOnboardPort `
    -Probe { $Context.Onboard.Snapshot().body.readiness } -Until { param($v) $v -eq 'READY' }

$expectedAvailable = @(@($frontSlots) + @($freed) | Sort-Object)
$availableAfter = Wait-L2Condition -Description 'the server counts the freed REAR slots available on the new session' `
    -Journal $journal -Criterion 'available-slots' -TimeoutSeconds 60 -Component $reseeded `
    -Probe { (Get-L2AvailableSlots -Connection $connection -AgvId $Context.AgvId) -join ',' } `
    -Until { param($v) $v -eq ($expectedAvailable -join ',') }
$assertions.Add(
    'L2-SGF-05', "换种子重连后，服务端按新会话的快照算出 REAR 组 [$($freed -join ',')] 可用",
    ($availableAfter -eq ($expectedAvailable -join ',')),
    "[$($expectedAvailable -join ',')]", "[$availableAfter]")

# --- 3. 下一轮受理，目标仓就是刚变空的两个 --------------------------------------------------------------

$stage = Wait-L2Condition -Description 'the demand was accepted once its group had room' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 -Component $reseeded `
    -Probe { $r = Get-Runtime $demandId; if ($r) { [string]$r.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$targets = @([string](Get-Runtime $demandId).TargetSlotsJson | ConvertFrom-Json | ForEach-Object { [int]$_ })
$assertions.Add(
    'L2-SGF-06', "REAR 组有空仓后受理，目标仓恰好是变空的 [$($freed -join ',')]",
    ($stage -eq 'AwaitingPickupArrival' -and ($targets -join ',') -eq ($freed -join ',')),
    "AwaitingPickupArrival, [$($freed -join ',')]", "$stage, [$($targets -join ',')]")
# Passed the availability read above rather than re-read: it is the same session's snapshot either way, and the
# assertion is then about the facts the demand was judged against.
$null = Assert-L2SlotGroupTargets -Assertions $assertions -Id 'L2-SGF-07' -Connection $connection `
    -DemandId $demandId -SlotPosition 'REAR' -AvailableSlots $expectedAvailable `
    -Description "目标仓全部属于本车 REAR 组、升序，且恰好是该组编号最小的 $basketCount 个可用仓"

$journal.Note('Scenario finished.')
