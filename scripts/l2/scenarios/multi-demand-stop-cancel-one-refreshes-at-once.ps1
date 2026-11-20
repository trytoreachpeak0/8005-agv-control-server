#Requires -Version 7

<#
多需求停靠上扫码前取消一条，去掉它的新一版清单与录入请求随取消落定那一次保存发出，不等引擎下一轮（control-server#909）。

现场：11-17～11-20 agv02 这样的取消 91 次，新清单中位 11.7 秒、最长 41.6 秒才到车上，期间 91 次全部夹着一遍同版录入请求重发（仍期待被取消的
子批），被取消的那一行留在屏上、约 10 秒后又能按——客户反馈「取消后又跳出来」。本站只剩这一条时 0.2 秒就发空清单，没有这个问题。

形状：需求甲在 12 号站（N1-3），丁、乙在 11 号站（C15-13），先丁后乙，都在车去 12 号站的路上追加进来，并进同一个 11 号站停靠。甲装上；车到 11 号站，
合成车载端把录入请求挂着不答（期待丁、乙），乙扫码前取消。

判据不靠计时：合成装置的引擎一轮很快，「几秒内到」修前修后都可能成立。看的是库里的事实——新一版清单的 CreatedAt 与乙的业务键抑制的
SuppressedAt 是同一个时刻，两者都是取消落定那一次处理用同一个时刻暂存、同一次保存落库的；修之前新的一版由引擎下一轮按它那一轮的时钟写，对不上。
这条判据依赖 OnboardRecoveryCoordinator 扫码前取消那一支的 cancelledAt：终结与新一版清单共用它。那里改成各读各的时钟，L2-MDR-02 会红，要同步改这里。

判据：
1. 前置：乙、丁进了甲那一趟旅程，并进同一个 11 号站停靠；到站那一版清单列着丁、乙。
2. 乙终结；11 号站有一版只列丁的清单，号比到站那一版高，CreatedAt 等于乙的抑制时刻（与取消同一次保存）。
3. 发件箱里没有一条未确认、未退役的 11 号站录入请求还期待乙的子批（补发没有东西可重发）；同号的录入请求只期待丁。
4. 合成车载端收到新号的录入请求，答它，丁装上。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
. (Join-Path $PSScriptRoot 'CargoHoldingCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$onboard = $Context.Onboard

$a = New-L2CargoDemand 'A' 'N1-3' 1 $Context.RunId
$b = New-L2CargoDemand 'B' 'C15-13' 1 $Context.RunId
$d = New-L2CargoDemand 'D' 'C15-13' 1 $Context.RunId

function ConvertTo-Instant($value) {
    if ($null -eq $value -or [string]$value -eq '') { return $null }
    if ($value -is [DateTimeOffset]) { return $value }
    if ($value -is [DateTime]) { return [DateTimeOffset]$value }
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

function Format-Instant($value) {
    if ($null -eq $value) { return '(none)' }
    return $value.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
}

function Get-Members([string]$JourneyId) {
    $map = @{}
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT DemandId, Status, PickupStopId FROM JourneyDemands WHERE JourneyId = '$JourneyId' AND RemovedAt IS NULL")
    foreach ($row in $rows) { $map[[string]$row.DemandId] = $row }
    return $map
}

# 一个站上排给车的每一版清单：号、列的需求、写进发件箱的时刻。按号排。
function Get-StationWorklists([string]$StationId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT PayloadJson, CreatedAt FROM ProtocolOutbox WHERE MessageType = 'CurrentStopWorklistSnapshot'")
    $list = foreach ($row in $rows) {
        $payload = ([string]$row.PayloadJson | ConvertFrom-Json -DateKind String).payload
        if ([string]$payload.stationId -ne $StationId) { continue }
        [pscustomobject]@{
            Revision  = [long]$payload.worklistRevision
            Demands   = @(@($payload.items) | ForEach-Object { [string]$_.demandId })
            CreatedAt = ConvertTo-Instant $row.CreatedAt
        }
    }
    return , @($list | Sort-Object Revision)
}

# 一个站上还会被补发的录入请求（未确认、未退役）：号与期待的子批。
function Get-PendingEntryRequests([string]$StationId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'SublotEntryRequested' AND AcknowledgedAt IS NULL AND FencedAt IS NULL")
    $list = foreach ($row in $rows) {
        $payload = ([string]$row.PayloadJson | ConvertFrom-Json -DateKind String).payload
        if ([string]$payload.stationId -ne $StationId) { continue }
        [pscustomobject]@{
            Revision = [long]$payload.worklistRevision
            Sublots  = @(@($payload.expectedSublots) | ForEach-Object { [string]$_ })
        }
    }
    return , @($list | Sort-Object Revision)
}

function Format-Worklists([object[]]$Worklists) {
    return (@($Worklists) | ForEach-Object { "$($_.Revision)[$(@($_.Demands).Count)]@$(Format-Instant $_.CreatedAt)" }) -join ' '
}

function Get-PeerCancellation([string]$cancellationId) {
    $body = (Invoke-RestMethod -Uri "$($onboard.BaseUrl)/$($onboard.Prefix)/load-cancellations" -TimeoutSec 10).body
    return @($body.cancellations) | Where-Object { [string]$_.cancellationId -eq $cancellationId } | Select-Object -First 1
}

function Get-HeldEntryRequest([string]$Key) {
    return @(@($onboard.Snapshot().body.pending) | Where-Object {
            [string]$_.messageType -eq 'SublotEntryRequested' -and [string]$_.key -eq $Key
        }) | Select-Object -Last 1
}

function Get-HeldEntryRequestWithPrefix([string]$KeyPrefix) {
    return @(@($onboard.Snapshot().body.pending) | Where-Object {
            [string]$_.messageType -eq 'SublotEntryRequested' -and ([string]$_.key).StartsWith($KeyPrefix, [StringComparison]::Ordinal)
        }) | Select-Object -Last 1
}

Initialize-L2CargoRig $Context

# --- 1. 甲受理；丁、乙追加，并进同一个 11 号站停靠 ------------------------------------------------------

Publish-L2CargoDemand $Context $a
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
Publish-L2CargoDemand $Context $d
$null = Wait-L2Condition -Description 'demand D joined the journey' -Journal $journal -Criterion 'd-joined' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $d.Id } -Until { param($v) $null -ne $v }
Publish-L2CargoDemand $Context $b
$joined = Wait-L2ConditionOrLast -Description 'demand B joined the journey' -Journal $journal -Criterion 'b-joined' -TimeoutSeconds 120 `
    -Probe { Get-Members $journeyId } -Until { param($v) $v.ContainsKey($b.Id) }

# --- 2. 12 号站装甲；11 号站的录入请求挂着不答 -------------------------------------------------------------

$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $Context.PickupStationRiotId
$null = Wait-L2Condition -Description 'demand A was loaded' -Journal $journal -Criterion 'a-loaded' -TimeoutSeconds 120 `
    -Probe { [string](Get-Members $journeyId)[$a.Id].Status } -Until { param($v) $v -eq 'LOADED' }

$null = $onboard.Command('Put', 'policy', @{ sublot = 'Manual' })
$bStop = Move-L2CargoVehicleToCurrentStop $Context $journeyId 11
$bStopRow = Invoke-L2Query -Connection $connection -Sql (
    "SELECT StationId, OperationSessionId FROM JourneyStops WHERE StopId = '$($bStop.StopId)'")
$bStationId = [string]$bStopRow[0].StationId
$keyPrefix = "sublot:$([string]$bStopRow[0].OperationSessionId):"
$held = Wait-L2Condition -Description 'the entry request at station 11 is held on the peer' -Journal $journal `
    -Criterion 'entry-held' -TimeoutSeconds 60 -Probe { Get-HeldEntryRequestWithPrefix $keyPrefix } -Until { param($v) $null -ne $v }
$arrival = @(Get-StationWorklists $bStationId) | Select-Object -Last 1
$assertions.Add(
    'L2-MDR-01', '前置：乙、丁进了甲那一趟旅程，并进同一个 11 号站停靠；到站那一版清单列着丁、乙',
    ($joined.ContainsKey($b.Id) -and $joined.ContainsKey($d.Id) -and
        [string]$joined[$b.Id].PickupStopId -eq [string]$joined[$d.Id].PickupStopId -and
        $null -ne $arrival -and @($arrival.Demands).Count -eq 2 -and $arrival.Demands -contains $b.Id -and $arrival.Demands -contains $d.Id),
    'B and D on one stop, arrival worklist lists both',
    "B $(if ($joined.ContainsKey($b.Id)) { $joined[$b.Id].PickupStopId } else { '(missing)' }), D $(if ($joined.ContainsKey($d.Id)) { $joined[$d.Id].PickupStopId } else { '(missing)' }); arrival $(Format-Worklists @($arrival))")

# --- 3. 乙扫码前取消 ------------------------------------------------------------------------------------------

$cancellationId = [guid]::NewGuid().ToString('D')
$journal.Note("Operator cancels demand B before any sublot entry ($cancellationId); demand D is still to be entered.")
$null = $onboard.Command('Put', "load-cancellations/$cancellationId", @{
    demandId               = $b.Id
    slotOperationAttemptId = $null
    reason                 = '现场确认本站没有要装的货。'
})
$null = Wait-L2ConditionOrLast -Description "the peer's ALL_EMPTY result was acknowledged" -Journal $journal `
    -Criterion 'b-result-acknowledged' -TimeoutSeconds 60 `
    -Probe { Get-PeerCancellation $cancellationId } -Until { param($v) $v -and $null -ne $v.resultAcknowledgedAt }
$ended = Wait-L2ConditionOrLast -Description 'the cancellation ended demand B' -Journal $journal -Criterion 'b-ended' -TimeoutSeconds 60 `
    -Probe { [string](Get-Members $journeyId)[$b.Id].Status } -Until { param($v) $v -eq 'TERMINATED' }
$suppressedRows = @(Invoke-L2Query -Connection $connection -Sql (
        "SELECT SuppressedAt FROM TransportDemandSuppressions WHERE DemandId = '$($b.Id)'"))
$suppressedAt = if ($suppressedRows.Count -eq 1) { ConvertTo-Instant $suppressedRows[0].SuppressedAt } else { $null }
$worklists = Wait-L2ConditionOrLast -Description 'a worklist listing D alone was queued' -Journal $journal `
    -Criterion 'refreshed' -TimeoutSeconds 60 -Probe { Get-StationWorklists $bStationId } `
    -Until { param($v) @($v | Where-Object { @($_.Demands).Count -eq 1 -and $_.Demands[0] -eq $d.Id }).Count -ge 1 }
$refreshed = @($worklists | Where-Object { @($_.Demands).Count -eq 1 -and $_.Demands[0] -eq $d.Id }) | Select-Object -First 1
$assertions.Add(
    'L2-MDR-02', '乙终结；只列丁的那一版号更高，写进发件箱的时刻就是乙的抑制时刻（与取消同一次保存）',
    ($ended -eq 'TERMINATED' -and $null -ne $refreshed -and $null -ne $suppressedAt -and
        $refreshed.Revision -gt $arrival.Revision -and $refreshed.CreatedAt -eq $suppressedAt),
    "worklist [D] above $($arrival.Revision) created at $(Format-Instant $suppressedAt)",
    "B $ended; suppressed $(Format-Instant $suppressedAt); worklists $(Format-Worklists $worklists)")

# 只读一次：同一份行既判又写进证据。录入请求车答了就确认（之后不在这里），所以紧接着取消读。
$pending = Get-PendingEntryRequests $bStationId
$stillB = 0
$dAloneAtNew = 0
$seen = [Collections.Generic.List[string]]::new()
foreach ($request in $pending) {
    $sublots = [string[]]@($request.Sublots)
    $seen.Add("$($request.Revision):$($sublots -join ',')")
    if ($sublots -contains $b.Sublot) { $stillB++ }
    if ($null -ne $refreshed -and $request.Revision -eq $refreshed.Revision -and $sublots.Count -eq 1 -and $sublots[0] -eq $d.Sublot) {
        $dAloneAtNew++
    }
}
$assertions.Add(
    'L2-MDR-03', '没有一条还会补发的 11 号站录入请求期待乙；同号的录入请求只期待丁',
    ($stillB -eq 0 -and $dAloneAtNew -eq 1),
    "no pending request for $($b.Sublot); [$($d.Sublot)] at the new revision",
    "pending $($seen -join ' ') (expecting B: $stillB, D alone at the new revision: $dAloneAtNew)")

# --- 4. 答新号的录入请求，丁装上 ---------------------------------------------------------------------------

$newKey = if ($null -eq $refreshed) { "$keyPrefix?" } else { "$keyPrefix$($refreshed.Revision)" }
$newHeld = Wait-L2ConditionOrLast -Description 'the peer holds the entry request of the new revision' -Journal $journal `
    -Criterion 'new-entry-held' -TimeoutSeconds 60 -Probe { Get-HeldEntryRequest $newKey } -Until { param($v) $null -ne $v }
if ($null -ne $newHeld) {
    $null = $onboard.Command('Put', "answer/$newKey", @{ completed = $true })
}
$dStatus = Wait-L2ConditionOrLast -Description 'demand D was loaded at station 11' -Journal $journal -Criterion 'd-loaded' -TimeoutSeconds 120 `
    -Probe { [string](Get-Members $journeyId)[$d.Id].Status } -Until { param($v) $v -eq 'LOADED' }
$assertions.Add(
    'L2-MDR-04', '合成车载端收到新号的录入请求，答它，丁装上',
    ($null -ne $newHeld -and $dStatus -eq 'LOADED'),
    "entry $newKey held and answered, D LOADED", "held $(if ($newHeld) { 'yes' } else { 'no' }), D $dStatus")

$journal.Note('多需求停靠扫码前取消一条，去掉它的新一版清单与录入请求随取消落定那一次保存发出，旧版不再补发。')
