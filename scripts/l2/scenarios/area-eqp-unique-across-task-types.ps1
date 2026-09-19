#Requires -Version 7

<#
REQ-0187 唯一性跨任务类型（control-server#160 第 10 条，规格 8.3 批次 6 机制判据 ③）。

假 MesIngest 给两条需求，同一个 AREA N1-3、不同 EQP：

- 另一个任务类型（STAGING_TO_WIRE）的一行：EQP B。它本身因缺绑定不受理，但它仍是本轮目录里的观测行。
- WIRE_TO_GATE：EQP A。一个 AREA 对应两台 EQP，这个 AREA 的候选都挡，原因 AREA_EQP_NOT_UNIQUE——唯一性看全部观测行，
  不按任务类型过滤。

删掉那条其它类型的行之后，下一轮 WIRE_TO_GATE 被受理。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection

function New-Demand([string]$label) {
    $guid = [guid]::NewGuid()
    # MesIngest 报不带连字符的 demandId，服务端入口处归一化成规范 UUID：替身按前者发，断言按后者查。
    return [pscustomobject]@{
        Label  = $label
        Wire   = $guid.ToString('N')
        Id     = $guid.ToString('D')
        Sublot = "L2-SUBLOT-$label-$($Context.RunId)"
    }
}

function Publish-Demand([object]$demand, [string]$workType, [string]$area, [string]$eqp) {
    $journal.Note("Publishing demand $($demand.Wire) ($workType, AREA $area) to the fake MesIngest catalog.")
    $null = $mes.Command('Put', "demands/$($demand.Wire)", @{
        sublot      = $demand.Sublot
        workType    = $workType
        area        = $area
        eqp         = $eqp
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
}

function Get-Backlog([string]$demandId) {
    $rows = @(Invoke-L2Query -Connection $connection -Sql "SELECT ReasonCode, AcceptedAt FROM JourneyBacklog WHERE DemandId = '$demandId'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Count([string]$sql) {
    return [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N
}

function Get-AcceptanceRowCount([string]$demandId) {
    return Get-Count @"
SELECT (SELECT COUNT(*) FROM AcceptedDemands WHERE DemandId = '$demandId')
     + (SELECT COUNT(*) FROM JourneyRuntimes WHERE DemandId = '$demandId')
     + (SELECT COUNT(*) FROM OrderIntents WHERE DemandId = '$demandId') AS N
"@
}

function Get-Stage([string]$demandId) {
    $rows = @(Invoke-L2Query -Connection $connection -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$demandId'")
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
}

function Get-Intent([string]$demandId, [string]$purpose) {
    $rows = @(Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status, DestinationStationId FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# --- 1. 同一 AREA、不同 EQP：其它任务类型的行先放，WIRE_TO_GATE 随后 ---------------------------------

$other = New-Demand 'OTHER'
$gate = New-Demand 'GATE'
Publish-Demand $other 'STAGING_TO_WIRE' 'N1-3' 'EQP-L2-B'
Publish-Demand $gate 'WIRE_TO_GATE' 'N1-3' 'EQP-L2-A'

$null = Wait-L2Condition -Description 'the WIRE_TO_GATE demand was judged and written to the backlog' `
    -Journal $journal -Criterion 'backlog-row' -TimeoutSeconds 60 `
    -Probe { $row = Get-Backlog $gate.Id; if ($row) { [string]$row.ReasonCode } else { $null } } `
    -Until { param($v) -not [string]::IsNullOrEmpty($v) }
# 不是一轮的偶然：再让派车循环转几圈，原因仍然是那一个。
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal

$gateBacklog = Get-Backlog $gate.Id
$assertions.Add(
    'L2-AEUT-01', 'WIRE_TO_GATE 候选未受理：同 AREA 在另一个任务类型的行里对应另一台 EQP，原因 AREA_EQP_NOT_UNIQUE',
    ([string]$gateBacklog.ReasonCode -eq 'AREA_EQP_NOT_UNIQUE' -and
        [string]::IsNullOrEmpty([string]$gateBacklog.AcceptedAt) -and
        (Get-AcceptanceRowCount $gate.Id) -eq 0),
    'AREA_EQP_NOT_UNIQUE / not accepted / 0 rows',
    "$($gateBacklog.ReasonCode) / AcceptedAt=$($gateBacklog.AcceptedAt) / $(Get-AcceptanceRowCount $gate.Id) rows")

# --- 2. 删掉那条其它类型的行：下一轮受理 ------------------------------------------------------------

$journal.Note("Removing demand $($other.Wire) from the fake MesIngest catalog.")
$null = $mes.Command('Delete', "demands/$($other.Wire)", @{})
$stage = Wait-L2Condition -Description 'the WIRE_TO_GATE demand was accepted once the other row was gone' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage $gate.Id } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$assertions.Add(
    'L2-AEUT-02', '删掉其它任务类型那一行后，WIRE_TO_GATE 在后续一轮被受理并派去取货点',
    ($stage -eq 'AwaitingPickupArrival' -and [string](Get-Backlog $gate.Id).ReasonCode -eq 'ACCEPTED'),
    'AwaitingPickupArrival / ACCEPTED', "$stage / $((Get-Backlog $gate.Id).ReasonCode)")

$journal.Note('REQ-0187 的唯一性跨任务类型：另一类的观测行挡住同 AREA 的 WIRE_TO_GATE，删掉后放行。')
