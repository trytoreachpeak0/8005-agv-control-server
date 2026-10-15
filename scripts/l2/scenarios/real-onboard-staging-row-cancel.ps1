#Requires -Version 7

<#
真装置：待送点清单按行取消（control-server#726，CP-0014 修订项二，REQ-0211；车载端 onboard-hmi#353 每行一个「取消」）。真车载端 WPF +
真 slots-simulator，按钮与确认框走 UI Automation。

**为什么要有这一条。**扫码前取消在待送点上由拒绝改成终结并抑制，取消一行之后其余行要能继续扫码，靠的是服务端补发去掉那一行的新清单与
同号的新录入请求（cs#723），车载端在取消结果确认之后丢掉手上的录入请求、换上新的那一版（hmi#353）。两端各自在假对端上验证过；10-15 生产
上 cs#723 那一类两端时序问题，只有两端真组件之间才看得见（#733 审查第二阶段）。

**形状**（setup 头注释）：甲在 305 的第一个停靠装上；丙、乙、丁追加进来，丙新开同站的第二个停靠，乙丁并进去，第二个停靠的清单三行。

两格：
- (a) 车上有甲、本站还有别的行：按乙那一行的「取消」。
- (b) 车上有货、取消的是本站最后一行：先把丙装上，再按丁那一行的「取消」，本站以空清单结束，屏上仍有「出发」，按下后离站。

判据（`L2-RSC-*`）：
- 01 前提：甲已装上（Committed）；丙乙丁都在这趟旅程上、同在第二个停靠（不是甲的那一个）；车停在第二个停靠上，车载端确认了列着三行的清单，
  UIA 清单里三行都有可按的「取消」（WorklistItemCancellation）。
- 02 (a) 往返：真车载端为乙发出 LoadCancellationStartRequested（slotOperationAttemptId 为 null），服务端答 AUTHORIZED；车载端报
  LoadCancellationResult / ALL_EMPTY，服务端回 DurableAck。
- 03 (a) 乙终结并抑制：需求 Cancelled，抑制一条（TransportDemandSuppressions），归属不是放回（RemovalReason 不是 RELEASED_FOR_REDISPATCH）。
- 04 (a) 补发：结果之后建出、被车载端确认的新一版清单只列丙丁；同号的 SublotEntryRequested 只期待丙丁两个子批；UIA 清单读到丙丁两行。
- 05 (a) 其余行能扫：在真车载端上扫丙、装上（Committed）。
- 06 (b) 往返与终结：真车载端为丁发出扫码前取消、AUTHORIZED、ALL_EMPTY、DurableAck；丁 Cancelled 并抑制。
- 07 (b) 本站结束：丁的结果之后，服务端发出待送站上的空清单（items 为空、带 stopEndedReason），车载端确认了它；车载端屏上仍给出可按的「出发」。
- 08 (b) 出发：按「出发」，服务端答 ACCEPTED、车载端确认；发出开往卸货站（不是待送站）的离站核验。
- 09 送达：甲、丙各卸一次（Committed）、都 Succeeded，旅程 Completed；乙丁仍 Cancelled、没有回到这趟旅程上。

每个第二事实都先等（README 第 14 条）：读取消结果前等收件箱里那一条；读补发清单前等它被确认；读空清单前等丁的结果；按出发前等按钮稳定。

探针不取闭包，理由见 CargoHoldingCommon.ps1 开头。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2MultiStopJourney.psm1') -Force
. (Join-Path $PSScriptRoot 'StagingDepartureRigCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$onboard = $Context.Onboard

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw "This scenario needs the real onboard rig: it presses a worklist row's 取消 on the shipped HMI through UI Automation."
}

$all = @('L2-RSC-01', 'L2-RSC-02', 'L2-RSC-03', 'L2-RSC-04', 'L2-RSC-05', 'L2-RSC-06', 'L2-RSC-07', 'L2-RSC-08', 'L2-RSC-09')
$a = New-L2StagingDemand 'A' 'N1-3' 'L2-RSC' $Context.RunId
$c = New-L2StagingDemand 'C' 'C15-13' 'L2-RSC' $Context.RunId
$b = New-L2StagingDemand 'B' 'N1-7' 'L2-RSC' $Context.RunId
$d = New-L2StagingDemand 'D' 'C15-13' 'L2-RSC' $Context.RunId
$rowCaption = '取消这条需求'

# --- helpers --------------------------------------------------------------------------------------------------------

# The row's 「取消」 for one sublot (onboard-hmi#353's WorklistItemCancellation).
function Find-L2RowCancellation([object]$Onboard, [string]$Sublot) {
    return Find-L2WorklistRowButton $Onboard $Sublot 'WorklistItemCancellation'
}

function Test-L2RowCancellationPressable([object]$Onboard, [string]$Sublot) {
    $button = Find-L2RowCancellation $Onboard $Sublot
    try {
        return $null -ne $button -and [bool]$button.Current.IsEnabled -and -not [bool]$button.Current.IsOffscreen
    } catch [System.Windows.Automation.ElementNotAvailableException] {
        return $false
    }
}

<#
The operator presses one row's 「取消」 and answers 「取消这条需求」 with Yes (IDYES = 6). The button must stay pressable for a second
first, and with no confirmation within ten seconds it is pressed again, at most three times -- the same reasoning as
Invoke-L2DeparturePress: no confirmation means no request left the vehicle. Returns the moment the dialog was seen closed.
#>
function Invoke-L2RowCancellationPress([object]$Context, [hashtable]$Demand) {
    $onboard = $Context.Onboard
    $journal = $Context.Journal
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $stableSince = $null
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            if (Test-L2RowCancellationPressable $onboard $Demand.Sublot) {
                if ($null -eq $stableSince) { $stableSince = [DateTimeOffset]::UtcNow }
                elseif (([DateTimeOffset]::UtcNow - $stableSince).TotalSeconds -ge 1) { break }
            } else { $stableSince = $null }
            Start-Sleep -Milliseconds 200
        }
        $journal.Note("Pressing 取消 on row $($Demand.Sublot) ($($Demand.Label), attempt $attempt).")
        $button = Find-L2RowCancellation $onboard $Demand.Sublot
        if ($null -eq $button) { throw "RIG_PRECONDITION: row $($Demand.Sublot) has no 取消 in the automation tree." }
        # Posted, like every entry button: the handler goes straight into the confirmation MessageBox.
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        if (Invoke-L2RealDialogButton $onboard $journal $rowCaption '6' 10) { return [DateTimeOffset]::UtcNow }
        $journal.Note("No '$rowCaption' confirmation after pressing row $($Demand.Sublot) (attempt $attempt).")
    }
    throw "Pressing 取消 on row $($Demand.Sublot) never raised the '$rowCaption' confirmation in 3 attempts."
}

# The before-scan cancellation of one demand as both ends saw it: the request and its authorization, the result and its ack.
function Wait-L2RowCancellationRoundTrip([object]$Context, [hashtable]$Demand, [string]$Criterion) {
    $connection = $Context.Connection
    $journal = $Context.Journal
    $demandId = $Demand.Id
    $request = Wait-L2RealOrLast -Description "the server authorized the real onboard's cancellation of $($Demand.Label)" `
        -Journal $journal -Criterion "$Criterion-authorized" -TimeoutSeconds 30 `
        -Probe {
            $requests = Get-L2RealInbound $connection 'LoadCancellationStartRequested'
            @($requests | Where-Object { [string]$_.Payload.demandId -eq $demandId }) | Select-Object -Last 1
        } -Until { param($v) $null -ne $v -and $v.Response -eq 'LoadCancellationAuthorization' }
    $result = $null
    if ($null -ne $request) {
        $cancellationId = [string]$request.Payload.cancellationId
        $result = Wait-L2RealOrLast -Description "the real onboard reported the cancellation of $($Demand.Label) and the server acked it" `
            -Journal $journal -Criterion "$Criterion-result" -TimeoutSeconds 60 `
            -Probe {
                $results = Get-L2RealInbound $connection 'LoadCancellationResult'
                @($results | Where-Object { [string]$_.Payload.cancellationId -eq $cancellationId }) | Select-Object -Last 1
            } -Until { param($v) $null -ne $v -and $v.Response -eq 'DurableAck' }
    }
    return [pscustomobject]@{ Request = $request; Result = $result }
}

function Test-L2RowCancellationRoundTrip([object]$RoundTrip) {
    $request = $RoundTrip.Request
    $result = $RoundTrip.Result
    return $null -ne $request -and $null -ne $result -and
        $null -eq $request.Payload.slotOperationAttemptId -and
        $request.Response -eq 'LoadCancellationAuthorization' -and
        [string]$request.ResponsePayload.decision -eq 'AUTHORIZED' -and
        [string]$result.Payload.overallOutcome -eq 'ALL_EMPTY' -and $result.Response -eq 'DurableAck'
}

function Format-L2RowCancellationRoundTrip([object]$RoundTrip) {
    $request = if ($null -eq $RoundTrip.Request) { '(no request)' } else {
        "attempt '$($RoundTrip.Request.Payload.slotOperationAttemptId)' → $($RoundTrip.Request.Response) $(if ($null -ne $RoundTrip.Request.ResponsePayload) { $RoundTrip.Request.ResponsePayload.decision })" }
    $result = if ($null -eq $RoundTrip.Result) { '(no result)' } else {
        "$($RoundTrip.Result.Payload.overallOutcome) → $($RoundTrip.Result.Response)" }
    return "$request / $result"
}

# Ended for good: Cancelled, one suppression, the membership not put back for redispatch.
function Get-L2CancelledFacts([object]$Connection, [hashtable]$Demand) {
    $status = Get-L2RealScalar $Connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($Demand.Id)'"
    $suppressions = Get-L2RealCount $Connection "SELECT COUNT(*) AS Total FROM TransportDemandSuppressions WHERE DemandId = '$($Demand.Id)'"
    $member = Get-L2StagingMembership $Connection $Demand.Id
    $reason = if ($null -eq $member) { '(no membership)' } else { [string]$member.RemovalReason }
    return [pscustomobject]@{ Status = $status; Suppressions = $suppressions; RemovalReason = $reason }
}

function Test-L2CancelledFacts([object]$Facts) {
    return $Facts.Status -eq 'Cancelled' -and $Facts.Suppressions -eq 1 -and $Facts.RemovalReason -ne 'RELEASED_FOR_REDISPATCH'
}

function Format-L2CancelledFacts([object]$Facts) {
    return "$($Facts.Status) / 抑制 $($Facts.Suppressions) / 移除原因 '$($Facts.RemovalReason)'"
}

# The worklists and entry requests the server queued, with the fields this scenario judges.
function Get-L2StagingWorklists([object]$Connection) {
    $rows = Get-L2RealOutbound $Connection 'CurrentStopWorklistSnapshot'
    return , @(foreach ($row in $rows) {
            [pscustomobject]@{
                At           = $row.At
                Revision     = [long]$row.Payload.worklistRevision
                StationId    = [string]$row.Payload.stationId
                Demands      = @(@($row.Payload.items) | ForEach-Object { [string]$_.demandId })
                EndedReason  = if ($row.Payload.PSObject.Properties['stopEndedReason']) { [string]$row.Payload.stopEndedReason } else { '' }
                Acknowledged = $row.Acknowledged
            }
        })
}

function Get-L2EntryRequests([object]$Connection) {
    $rows = Get-L2RealOutbound $Connection 'SublotEntryRequested'
    return , @(foreach ($row in $rows) {
            [pscustomobject]@{
                At       = $row.At
                Revision = [long]$row.Payload.worklistRevision
                Sublots  = @(@($row.Payload.expectedSublots) | ForEach-Object { [string]$_ })
            }
        })
}

function Format-L2Sorted([object[]]$Values) { return (@($Values | Sort-Object) -join ',') }

# --- 1. 甲在待送点装上，丙乙丁追加进同一个后续停靠 ----------------------------------------------------------------

Initialize-L2CargoRig $Context
Assert-L2RigBaselineSlots $Context 3
Publish-L2StagingDemand $Context $a 'EQP-L2-RSC-01'
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $script:StagingStationRiotId
$loadA = Invoke-L2RigLoad $Context $journeyId $a
$stopA = [string](Get-L2StagingMembership $connection $a.Id).PickupStopId

# 丙先挂，新开同站的第二个停靠；乙丁再挂，并进它。每一条等它挂上再发下一条，免得三条在同一轮派车里争同一个停靠。
$joinedAll = $true
foreach ($demand in @($c, $b, $d)) {
    Publish-L2StagingDemand $Context $demand 'EQP-L2-RSC-01'
    $demandId = $demand.Id
    $joined = Wait-L2RealOrLast -Description "demand $($demand.Label) joined the journey" -Journal $journal `
        -Criterion "joined-$($demand.Label)" -TimeoutSeconds 60 `
        -Probe { Get-L2StagingMembership $connection $demandId } `
        -Until { param($v) $null -ne $v -and (-not (Test-L2RealPresent $v.RemovedAt)) -and [string]$v.JourneyId -eq $journeyId }
    if ($null -eq $joined -or [string]$joined.JourneyId -ne $journeyId) { $joinedAll = $false }
}
$stopOf = @{}
foreach ($demand in @($c, $b, $d)) {
    $member = Get-L2StagingMembership $connection $demand.Id
    $stopOf[$demand.Label] = if ($null -eq $member) { '' } else { [string]$member.PickupStopId }
}
$sameStop = $joinedAll -and $stopOf['C'] -ne '' -and $stopOf['C'] -ne $stopA -and
    $stopOf['B'] -eq $stopOf['C'] -and $stopOf['D'] -eq $stopOf['C']
$threeRows = $null
$offered = $false
if ($sameStop) {
    $null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $script:StagingStationRiotId $stopOf['C']
    $expectedThree = Format-L2Sorted @($c.Id, $b.Id, $d.Id)
    $threeRows = Wait-L2RealOrLast -Description 'the onboard acknowledged the three-row worklist and the HMI lists the three rows' `
        -Journal $journal -Criterion 'three-rows' -TimeoutSeconds 60 `
        -Probe {
            $lists = Get-L2StagingWorklists $connection
            $acked = @($lists | Where-Object { $_.Acknowledged -and (Format-L2Sorted $_.Demands) -eq $expectedThree })
            $rows = Get-L2WorklistRows $onboard
            "$($acked.Count -gt 0)/$(Format-L2Sorted @(@($rows) | ForEach-Object { $_.Sublot }))"
        } -Until { param($v) $v -eq "True/$(Format-L2Sorted @($c.Sublot, $b.Sublot, $d.Sublot))" }
    $offered = Wait-L2RealOrLast -Description "every row offers 取消" -Journal $journal -Criterion 'row-cancellation-offered' `
        -TimeoutSeconds 30 `
        -Probe { @(@($c, $b, $d) | Where-Object { Test-L2RowCancellationPressable $onboard $_.Sublot }).Count -eq 3 } `
        -Until { param($v) $v }
}
$assertions.Add(
    'L2-RSC-01', '前提：甲已装上；丙乙丁同在甲之后的一个同站停靠上；车停在那里，车载端确认了三行的清单，屏上三行都有可按的「取消」',
    ($loadA.Status -eq 'Committed' -and $sameStop -and $threeRows -eq "True/$(Format-L2Sorted @($c.Sublot, $b.Sublot, $d.Sublot))" -and $offered),
    'A Committed / C,B,D on one later stop / three rows acked and shown / 取消 pressable on each',
    "A $($loadA.Status) / stops A=$stopA C=$($stopOf['C']) B=$($stopOf['B']) D=$($stopOf['D']) / $threeRows / 取消 $(if ($offered) { 'pressable' } else { 'not offered on every row' })")
if (-not ($sameStop -and $offered)) {
    Add-L2RealNotReached $assertions @($all | Where-Object { $_ -ne 'L2-RSC-01' }) '三条没有挂进同一个后续停靠，或屏上不是每行都有「取消」，后面几条的前提不成立'
    return
}

# --- 2. (a) 取消乙，其余行继续 ------------------------------------------------------------------------------------

$null = Invoke-L2RowCancellationPress $Context $b
$tripB = Wait-L2RowCancellationRoundTrip $Context $b 'cancel-b'
$assertions.Add(
    'L2-RSC-02', '(a) 往返：真车载端为乙发出扫码前取消（slotOperationAttemptId 为 null），服务端答 AUTHORIZED；车载端报 ALL_EMPTY，服务端回 DurableAck',
    (Test-L2RowCancellationRoundTrip $tripB), "attempt '' → LoadCancellationAuthorization AUTHORIZED / ALL_EMPTY → DurableAck",
    (Format-L2RowCancellationRoundTrip $tripB))

$bId = $b.Id
$null = Wait-L2RealOrLast -Description 'demand B is cancelled' -Journal $journal -Criterion 'b-cancelled' -TimeoutSeconds 30 `
    -Probe { Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$bId'" } `
    -Until { param($v) $v -eq 'Cancelled' }
$factsB = Get-L2CancelledFacts $connection $b
$assertions.Add(
    'L2-RSC-03', '(a) 乙终结并抑制：需求 Cancelled、抑制一条、归属不是放回待派',
    (Test-L2CancelledFacts $factsB), 'Cancelled / 抑制 1 / 移除原因不是 RELEASED_FOR_REDISPATCH', (Format-L2CancelledFacts $factsB))

$resultBAt = if ($null -ne $tripB.Result) { $tripB.Result.At } else { [DateTimeOffset]::UtcNow.AddSeconds(-30) }
$expectedTwo = Format-L2Sorted @($c.Id, $d.Id)
$expectedTwoSublots = Format-L2Sorted @($c.Sublot, $d.Sublot)
$refreshed = Wait-L2RealOrLast -Description 'the onboard acknowledged a worklist without B issued after its cancellation' `
    -Journal $journal -Criterion 'worklist-without-b' -TimeoutSeconds 60 `
    -Probe {
        $lists = Get-L2StagingWorklists $connection
        @($lists | Where-Object { $_.At -gt $resultBAt -and $_.Acknowledged -and (Format-L2Sorted $_.Demands) -eq $expectedTwo }) |
            Select-Object -Last 1
    } -Until { param($v) $null -ne $v }
$entry = $null
if ($null -ne $refreshed) {
    $revision = $refreshed.Revision
    $entries = Get-L2EntryRequests $connection
    $entry = @($entries | Where-Object { $_.Revision -eq $revision }) | Select-Object -Last 1
}
$rowsAfterB = Wait-L2RealOrLast -Description 'the HMI lists only C and D' -Journal $journal -Criterion 'rows-without-b' -TimeoutSeconds 30 `
    -Probe { $rows = Get-L2WorklistRows $onboard; Format-L2Sorted @(@($rows) | ForEach-Object { $_.Sublot }) } `
    -Until { param($v) $v -eq $expectedTwoSublots }
$assertions.Add(
    'L2-RSC-04', '(a) 补发：取消之后建出、车载端确认的新一版清单只列丙丁；同号的录入请求只期待丙丁；屏上清单读到丙丁两行',
    ($null -ne $refreshed -and $null -ne $entry -and (Format-L2Sorted $entry.Sublots) -eq $expectedTwoSublots -and $rowsAfterB -eq $expectedTwoSublots),
    "worklist [C,D] acked / entry request same revision [$expectedTwoSublots] / rows $expectedTwoSublots",
    "$(if ($null -eq $refreshed) { '(no acked worklist without B)' } else { "worklist r$($refreshed.Revision) [$(Format-L2Sorted $refreshed.Demands)]" }) / " +
    "$(if ($null -eq $entry) { '(no entry request at that revision)' } else { "entry r$($entry.Revision) [$(Format-L2Sorted $entry.Sublots)]" }) / rows $rowsAfterB")

$loadC = Invoke-L2RigLoad $Context $journeyId $c -WorklistAfter $resultBAt
$assertions.Add(
    'L2-RSC-05', '(a) 其余行能扫：在真车载端上扫丙、装上',
    ($loadC.Status -eq 'Committed'), 'C Committed', "C $($loadC.Status)")

# --- 3. (b) 车上有货，取消本站最后一行 -------------------------------------------------------------------------------

$cCommittedAt = $loadC.CommittedAt
$dId = $d.Id
$null = Wait-L2RealOrLast -Description 'the onboard acknowledged the worklist listing only D after C was loaded' -Journal $journal `
    -Criterion 'worklist-only-d' -TimeoutSeconds 60 `
    -Probe {
        $lists = Get-L2StagingWorklists $connection
        @($lists | Where-Object { $_.At -gt $cCommittedAt -and $_.Acknowledged -and (Format-L2Sorted $_.Demands) -eq $dId }) |
            Select-Object -Last 1
    } -Until { param($v) $null -ne $v }
$null = Invoke-L2RowCancellationPress $Context $d
$tripD = Wait-L2RowCancellationRoundTrip $Context $d 'cancel-d'
$null = Wait-L2RealOrLast -Description 'demand D is cancelled' -Journal $journal -Criterion 'd-cancelled' -TimeoutSeconds 30 `
    -Probe { Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$dId'" } `
    -Until { param($v) $v -eq 'Cancelled' }
$factsD = Get-L2CancelledFacts $connection $d
$assertions.Add(
    'L2-RSC-06', '(b) 往返与终结：真车载端为丁发出扫码前取消、AUTHORIZED、ALL_EMPTY、DurableAck；丁 Cancelled、抑制一条、不是放回',
    ((Test-L2RowCancellationRoundTrip $tripD) -and (Test-L2CancelledFacts $factsD)),
    "attempt '' → AUTHORIZED / ALL_EMPTY → DurableAck / Cancelled / 抑制 1",
    "$(Format-L2RowCancellationRoundTrip $tripD) / $(Format-L2CancelledFacts $factsD)")

$resultDAt = if ($null -ne $tripD.Result) { $tripD.Result.At } else { [DateTimeOffset]::UtcNow.AddSeconds(-30) }
$ended = Wait-L2RealOrLast -Description 'the onboard acknowledged the empty worklist that ends the staging stop' -Journal $journal `
    -Criterion 'stop-ended-worklist' -TimeoutSeconds 60 `
    -Probe {
        $lists = Get-L2StagingWorklists $connection
        @($lists | Where-Object {
                $_.At -gt $resultDAt -and $_.Acknowledged -and $_.Demands.Count -eq 0 -and $_.EndedReason -ne '' -and
                $_.StationId -eq $script:StagingStationName }) | Select-Object -Last 1
    } -Until { param($v) $null -ne $v }
$stillOffered = Wait-L2RealOrLast -Description 'the HMI still offers 出发' -Journal $journal -Criterion 'departure-offered-after-last-row' `
    -TimeoutSeconds 60 -Probe { Test-L2DepartureButtonPressable $onboard } -Until { param($v) $v }
$assertions.Add(
    'L2-RSC-07', '(b) 本站结束：丁的结果之后服务端发出待送站上的空清单（带 stopEndedReason）、车载端确认；屏上仍有可按的「出发」',
    ($null -ne $ended -and $stillOffered), 'empty worklist with stopEndedReason acked / 出发 pressable',
    "$(if ($null -eq $ended) { '(no acked empty worklist)' } else { "worklist r$($ended.Revision) ended '$($ended.EndedReason)'" }) / 出发 $(if ($stillOffered) { 'pressable' } else { "not offered (notice '$(Get-L2LoadingPhaseLine $onboard 'CurrentStopDepartureNotice')')" })")
if (-not $stillOffered) {
    Add-L2RealNotReached $assertions @('L2-RSC-08', 'L2-RSC-09') '本站结束后屏上没有「出发」，后面两条的前提不成立'
    return
}

# --- 4. (b) 出发，送达 ---------------------------------------------------------------------------------------------

$pressedAt = Invoke-L2DeparturePress $Context
$roundTrip = Wait-L2DepartureRoundTrip $Context $pressedAt 'ACCEPTED'
$leaving = Wait-L2RealOrLast -Description 'a departure check towards a drop-off was sent' -Journal $journal -Criterion 'leaving' `
    -TimeoutSeconds 60 -Probe { Get-L2DepartureCheckTargets $connection } `
    -Until { param($v) @($v | Where-Object { $_ -ne $script:StagingStationName }).Count -ge 1 }
$towardsDropOff = @($leaving | Where-Object { $_ -ne $script:StagingStationName })
$assertions.Add(
    'L2-RSC-08', '(b) 出发：真车载端发出出发请求、服务端答 ACCEPTED、车载端确认；发出开往卸货站的离站核验',
    ((Test-L2DepartureRoundTrip $roundTrip 'ACCEPTED') -and $towardsDropOff.Count -ge 1),
    'request by L2-OPERATOR @派工待送取货 / ACCEPTED acked / check to a drop-off',
    "$(Format-L2DepartureRoundTrip $roundTrip) / checks to [$(@($leaving) -join ', ')]")

$unloads = [System.Collections.Generic.List[object]]::new()
for ($i = 0; $i -lt 2; $i++) {
    $next = Wait-L2Condition -Description 'the journey has an unload stop ahead' -Journal $journal -Criterion "unload-stop-$i" `
        -TimeoutSeconds 120 -Probe { Get-L2CargoCurrentStop $connection $journeyId } `
        -Until { param($v) $null -ne $v -and [string]$v.StopRole -eq 'UNLOAD' }
    $null = Move-L2CargoVehicleToCurrentStop $Context $journeyId ([int]$next.StationRiotId) ([string]$next.StopId)
    $unloads.Add((Invoke-L2RigUnloadNext $Context $journeyId @($unloads | ForEach-Object { $_.AttemptId })))
}
$completed = Wait-L2RealOrLast -Description 'the journey completed' -Journal $journal -Criterion 'journey-completed' `
    -TimeoutSeconds 120 -Probe { [string](Get-L2StagingJourney $connection $journeyId).Stage } -Until { param($v) $v -eq 'Completed' }
$statusA = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($a.Id)'"
$statusC = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($c.Id)'"
$statusB = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($b.Id)'"
$statusD = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($d.Id)'"
$backOnJourney = Get-L2RealCount $connection (
    "SELECT COUNT(*) AS Total FROM JourneyDemands WHERE DemandId IN ('$($b.Id)', '$($d.Id)') AND JourneyId <> '$journeyId'")
$unloaded = Format-L2Sorted @($unloads | Where-Object { $_.Status -eq 'Committed' } | ForEach-Object { $_.DemandId })
$assertions.Add(
    'L2-RSC-09', '送达：甲、丙各卸一次（Committed）、都 Succeeded，旅程 Completed；乙丁仍 Cancelled、没有被再派进别的旅程',
    ($unloaded -eq (Format-L2Sorted @($a.Id, $c.Id)) -and $statusA -eq 'Succeeded' -and $statusC -eq 'Succeeded' -and
        $completed -eq 'Completed' -and $statusB -eq 'Cancelled' -and $statusD -eq 'Cancelled' -and $backOnJourney -eq 0),
    'A,C unloaded Committed / Succeeded,Succeeded / Completed / B,D Cancelled, on no other journey',
    "unloaded [$unloaded] / $statusA,$statusC / $completed / B $statusB, D $statusD, on another journey $backOnJourney")

$journal.Note('真装置：待送点一站三行，按行取消乙之后丙照常扫码装上；车上有货时取消最后一行丁，本站以空清单结束，按出发离站送达。')
