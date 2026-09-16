#Requires -Version 7
<#
control-server#71 验收探针（不入库）：SlotStates 让合成对端握手时把 1 号报成 OCCUPIED、2 号报成 DISABLED，
服务端读到的可用仓随之少了这两个；派一条要两个篮的需求，目标仓是 [3,4] 而不是默认种子下的 [1,2]。
顺带在真库上把 L2SlotGroups.psm1 的按组断言跑正反两例。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SlotGroups.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$agvId = $Context.AgvId

# 1. 服务端存下的握手快照里，可用仓少了 1、2。
$available = Get-L2AvailableSlots -Connection $connection -AgvId $agvId
$assertions.Add('B4-07-SS-01', '服务端就当前会话算出的可用仓是 3～8（1 号 OCCUPIED、2 号 DISABLED 被排除）',
    (($available -join ',') -eq '3,4,5,6,7,8'), '3,4,5,6,7,8', ($available -join ','))

$inbox = Invoke-L2Query -Connection $connection -Sql "SELECT RequestJson FROM ProtocolInbox WHERE MessageType = 'CapabilitySnapshot'"
$reported = @($inbox | ForEach-Object { ([string]$_.RequestJson | ConvertFrom-Json).payload.slotStates } |
    Where-Object { $_.slotNo -in 1, 2 } | ForEach-Object { "$($_.slotNo):$($_.physicalState)/$($_.administrativeAvailability)" } | Sort-Object -Unique)
$assertions.Add('B4-07-SS-02', '服务端收件箱里的 CapabilitySnapshot 报的正是种子：1 号 OCCUPIED/ENABLED，2 号 EMPTY/DISABLED',
    (($reported -join ';') -eq '1:OCCUPIED/ENABLED;2:EMPTY/DISABLED'), '1:OCCUPIED/ENABLED;2:EMPTY/DISABLED', ($reported -join ';'))

# 2. 派一条需求：maxBoxCount 8、每篮 4 盒，要两个篮。
$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')
$null = $Context.MesIngest.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot = "L2-SUBLOT-$($Context.RunId)"; area = 'N1-3'; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 8 })
$targets = Wait-L2Condition -Description 'the demand was accepted with its target slots' -Journal $journal `
    -Criterion 'probe:target-slots' -TimeoutSeconds 90 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql "SELECT TargetSlotsJson FROM JourneyRuntimes WHERE DemandId = '$demandId'"
        if ($rows.Count -eq 0) { $null } else { [string]$rows[0].TargetSlotsJson }
    } -Until { param($v) $null -ne $v }
$assertions.Add('B4-07-SS-03', '受理的需求目标仓是 [3,4]：可用仓减少直接体现在派车上（默认种子下是 [1,2]）',
    ((@($targets | ConvertFrom-Json) -join ',') -eq '3,4'), '[3,4]', $targets)

# 3. 按组断言，真库上正反两例。正例进本次判据；反例用一份单独的判据集合跑，只把「它确实判失败」记进来。
$positions = Get-L2VehicleSlotPositions -Connection $connection -AgvId $agvId
$positionText = if ($positions) {
    "$($positions.Source): " + (($positions.Positions.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ',')
} else { '(unresolved)' }
$assertions.Add('B4-07-SS-04', '从 SlotModelSlots 经该车最新已发布绑定读到的分组：1～4 FRONT、5～8 REAR（读出来的，不是写死的）',
    ($positionText -eq 'LatestPublishedIoBinding: 1=FRONT,2=FRONT,3=FRONT,4=FRONT,5=REAR,6=REAR,7=REAR,8=REAR'),
    'LatestPublishedIoBinding: 1=FRONT,2=FRONT,3=FRONT,4=FRONT,5=REAR,6=REAR,7=REAR,8=REAR', $positionText)

$positive = Assert-L2SlotGroupTargets -Assertions $assertions -Id 'B4-07-SS-05' -Connection $connection `
    -DemandId $demandId -SlotPosition 'FRONT' -AvailableSlots $available `
    -Description '正例：目标仓 [3,4] 全属 FRONT、升序、恰是 FRONT 组最小的两个可用仓'
$scratch = New-L2Assertions
$negative = Assert-L2SlotGroupTargets -Assertions $scratch -Id 'NEG' -Connection $connection `
    -DemandId $demandId -SlotPosition 'REAR' -AvailableSlots $available
$assertions.Add('B4-07-SS-06', '反例：同一需求按 REAR 断言判失败，并说明是哪一条不成立',
    (-not $negative.Passed -and [string]$negative.Reason -like '*not in group REAR*'), 'FAIL: not in group REAR',
    "$(if ($negative.Passed) { 'PASS' } else { 'FAIL' }): $($negative.Reason)")
$journal.Note("Group assertion results: FRONT -> $($positive.Passed); REAR -> $($negative.Passed) ($($negative.Reason)).")

$backlog = Get-L2JourneyBacklogRow -Connection $connection -DemandId $demandId
$blocks = Get-L2StructuralDispatchBlock -Connection $connection -DemandId $demandId
$assertions.Add('B4-07-SS-07', '读 JourneyBacklog 与 StructuralDispatchBlocks 的辅助函数可用：积压行 ACCEPTED，结构性阻塞 0 行',
    ($null -ne $backlog -and [string]$backlog.ReasonCode -eq 'ACCEPTED' -and $blocks.Count -eq 0),
    'ACCEPTED / 0', "$(if ($backlog) { $backlog.ReasonCode } else { '(none)' }) / $($blocks.Count)")
