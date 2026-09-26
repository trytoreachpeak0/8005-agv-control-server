#Requires -Version 7

<#
一站被期限结束而旅程继续时，车上不残留那一站（control-server#324；program#86 v2 的 B 形态）。

形状：需求甲在 12 号站（N1-3），需求乙在 11 号站（C15-13，车去第一站的路上追加进来）。甲装上车；车到 11 号站，合成车载端
把乙的录入请求挂着不答（操作员没扫码），站点等待到期，服务端终结乙、带着甲的货持货等单，然后离站去关卡卸甲。

修之前这一站结束时服务端什么也不发：车上一直列着乙、录入请求挂着、「取消装货」按钮还在；迟到的扫码只得到 DurableAck、
永远没人回答；迟到的取消答 ACTION_NOT_ALLOWED_IN_STATE。

判据：
1. 本站结束：乙 TERMINATED、甲仍 LOADED、旅程没有收尾。
2. 车收到并确认了一张 11 号站的空清单：号比此前每一版都大，作业会话与期限为 null。
3. 迟到的扫码（这时才把挂着的录入请求答上）得到 SublotRejected / WORKLIST_REVISION_STALE，demandId 为 null，
   currentWorklistRevision 是那张空清单的号。
4. 迟到的取消被拒，原因码是 WORKLIST_REVISION_STALE。
5. 车离站到关卡：关卡那一版清单的号在空清单之上，整条清单流没有两版同号——同号不同内容会让车当场断会话。
6. 全程没断过会话：合成车载端的会话代从到 11 号站起没变，也没进过 FAULTED。
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

function Get-DemandStatuses([string]$JourneyId) {
    $map = @{}
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT DemandId, Status FROM JourneyDemands WHERE JourneyId = '$JourneyId'"
    foreach ($row in $rows) { $map[[string]$row.DemandId] = [string]$row.Status }
    return $map
}

# 这辆车（装置只有一辆）发件箱里每一版清单：号、站、条数、作业会话、是否被确认。
function Get-Worklists {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, PayloadJson, AcknowledgedAt FROM ProtocolOutbox WHERE MessageType = 'CurrentStopWorklistSnapshot'")
    $list = foreach ($row in $rows) {
        $payload = ([string]$row.PayloadJson | ConvertFrom-Json -DateKind String).payload
        [pscustomobject]@{
            MessageId        = [string]$row.MessageId
            Revision         = [long]$payload.worklistRevision
            StationId        = [string]$payload.stationId
            Items            = @($payload.items).Count
            OperationSession = $payload.operationSessionId
            Deadline         = $payload.stationDepartureDeadlineAt
            Acknowledged     = $null -ne $row.AcknowledgedAt -and [string]$row.AcknowledgedAt -ne ''
        }
    }
    return , @($list | Sort-Object Revision)
}

function Format-Worklists([object[]]$Worklists) {
    return (@($Worklists) | ForEach-Object { "$($_.Revision)@$($_.StationId)x$($_.Items)$(if ($_.Acknowledged) { '+' } else { '' })" }) -join ' '
}

function Get-PeerState {
    $body = $onboard.Snapshot().body
    return [pscustomobject]@{ Generation = [long]$body.sessionGeneration; Readiness = [string]$body.readiness }
}

Initialize-L2CargoRig $Context

# --- 1. 甲装上车，乙在 11 号站等录入 ---------------------------------------------------------------------

Publish-L2CargoDemand $Context $a
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
Publish-L2CargoDemand $Context $b
$null = Wait-L2Condition -Description 'demand B joined the journey' -Journal $journal -Criterion 'b-joined' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $b.Id } -Until { param($v) $null -ne $v }

$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $Context.PickupStationRiotId
$null = Wait-L2Condition -Description 'demand A was loaded' -Journal $journal -Criterion 'a-loaded' -TimeoutSeconds 120 `
    -Probe { (Get-DemandStatuses $journeyId)[$a.Id] } -Until { param($v) $v -eq 'LOADED' }

# 第二个取货站上没人扫码：录入请求挂着，到迟到那一步才答。
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Manual' })
$bStop = Move-L2CargoVehicleToCurrentStop $Context $journeyId 11
$bStopRow = @(Invoke-L2Query -Connection $connection -Sql (
        "SELECT StationId, OperationSessionId FROM JourneyStops WHERE StopId = '$($bStop.StopId)'"))
$bStationId = [string]$bStopRow[0].StationId
# 挂起列表只有键与类型、不带载荷；录入请求的键是 sublot:{作业会话}:{清单号}，按这一站的作业会话认。
$bKeyPrefix = "sublot:$([string]$bStopRow[0].OperationSessionId):"
$pending = Wait-L2Condition -Description "the entry request at station 11 is held on the peer" -Journal $journal `
    -Criterion 'entry-held' -TimeoutSeconds 60 `
    -Probe {
        @(@($onboard.Snapshot().body.pending) | Where-Object {
                [string]$_.messageType -eq 'SublotEntryRequested' -and ([string]$_.key).StartsWith($bKeyPrefix, [StringComparison]::Ordinal)
            }) | Select-Object -First 1
    } `
    -Until { param($v) $null -ne $v }
$peerBefore = Get-PeerState
$journal.Observe('peer-at-station-11', "generation $($peerBefore.Generation), $($peerBefore.Readiness)", @{ peer = $peerBefore })

# --- 2. 站点等待到期：乙被终结、旅程继续 ------------------------------------------------------------------

$ended = Wait-L2ConditionOrLast -Description 'the station deadline ended demand B while the journey goes on' -Journal $journal `
    -Criterion 'b-ended' -TimeoutSeconds 90 `
    -Probe { Get-DemandStatuses $journeyId } -Until { param($v) $v[$b.Id] -eq 'TERMINATED' }
$afterEnd = Get-L2CargoJourney $connection $a.Id
$assertions.Add(
    'L2-SEJ-01', '站点等待到期：乙被终结，甲仍在车上，旅程没有收尾（B 形态）',
    ($ended[$b.Id] -eq 'TERMINATED' -and $ended[$a.Id] -eq 'LOADED' -and [string]$afterEnd.Stage -ne 'Completed'),
    'B TERMINATED, A LOADED, not Completed', "B $($ended[$b.Id]), A $($ended[$a.Id]), $(Format-L2CargoJourney $afterEnd)")

$emptyAck = Wait-L2ConditionOrLast -Description 'the vehicle acknowledged an empty worklist for station 11' -Journal $journal `
    -Criterion 'empty-worklist' -TimeoutSeconds 60 `
    -Probe { @((Get-Worklists) | Where-Object { $_.StationId -eq $bStationId -and $_.Items -eq 0 }) } `
    -Until { param($v) @($v).Count -eq 1 -and $v[0].Acknowledged }
$worklistsAtEnd = Get-Worklists
$empty = @($emptyAck) | Select-Object -First 1
$others = @($worklistsAtEnd | Where-Object { $null -eq $empty -or $_.MessageId -ne $empty.MessageId })
$assertions.Add(
    'L2-SEJ-02', '车收到并确认了一张 11 号站的空清单：号比此前每一版都大，作业会话与期限为 null',
    (@($emptyAck).Count -eq 1 -and $empty.Acknowledged -and $null -eq $empty.OperationSession -and $null -eq $empty.Deadline -and
        @($others | Where-Object { $_.Revision -ge $empty.Revision }).Count -eq 0),
    'one acknowledged empty worklist above every other', (Format-Worklists $worklistsAtEnd))

# --- 3. 迟到的扫码 --------------------------------------------------------------------------------------

$lateAt = [DateTimeOffset]::UtcNow
$null = $onboard.Command('Put', "answer/$([string]$pending.key)", @{ completed = $true })
# 合成车载端的线上记录只有类型、id 与时刻：拒收的内容读服务端发件箱那一行，再用 messageId 对到车收到的那一条
# （与 sublot-rejected-after-entry 同一种对法）。
$rejection = Wait-L2ConditionOrLast -Description 'the late entry was answered with SublotRejected' -Journal $journal `
    -Criterion 'late-entry-rejected' -TimeoutSeconds 30 `
    -Probe {
        $wire = @($onboard.Snapshot().body.wire)
        $submitted = @(@($wire | Where-Object {
                    [string]$_.direction -eq 'out' -and [string]$_.messageType -eq 'SublotSubmitted' -and
                    ([DateTimeOffset]::Parse([string]$_.at, [Globalization.CultureInfo]::InvariantCulture)) -ge $lateAt }) |
                ForEach-Object { [string]$_.messageId } | Select-Object -Unique)
        $received = @(@($wire | Where-Object { [string]$_.direction -eq 'in' -and [string]$_.messageType -eq 'SublotRejected' }) |
                ForEach-Object { [string]$_.messageId })
        $rows = Invoke-L2Query -Connection $connection -Sql (
            "SELECT MessageId, PayloadJson FROM ProtocolOutbox WHERE MessageType = 'SublotRejected'")
        $match = foreach ($row in $rows) {
            $envelope = [string]$row.PayloadJson | ConvertFrom-Json
            if ($submitted -contains [string]$envelope.correlationId -and $received -contains [string]$row.MessageId) { $envelope }
        }
        @($match) | Select-Object -First 1
    } `
    -Until { param($v) $null -ne $v }
$rejectionPayload = if ($null -eq $rejection) { $null } else { $rejection.payload }
$assertions.Add(
    'L2-SEJ-03', '迟到的扫码得到 SublotRejected / WORKLIST_REVISION_STALE：demandId 为 null，currentWorklistRevision 是空清单的号',
    ($null -ne $rejectionPayload -and [string]$rejectionPayload.problem.reasonCode -eq 'WORKLIST_REVISION_STALE' -and
        $null -eq $rejectionPayload.demandId -and $null -ne $empty -and
        [long]$rejectionPayload.currentWorklistRevision -eq $empty.Revision),
    "WORKLIST_REVISION_STALE @ $(if ($empty) { $empty.Revision } else { '?' })",
    $(if ($null -eq $rejectionPayload) { '(no SublotRejected for the late entry)' } else {
        "$($rejectionPayload.problem.reasonCode) demand=$($rejectionPayload.demandId) @ $($rejectionPayload.currentWorklistRevision)" }))

# --- 4. 迟到的取消 --------------------------------------------------------------------------------------

$cancellationId = [guid]::NewGuid().ToString('D')
$null = $onboard.Command('Put', "load-cancellations/$cancellationId", @{
    demandId               = $b.Id
    slotOperationAttemptId = $null
    reason                 = '操作员在车上那一版清单上按了取消装货。'
})
$cancellation = Wait-L2ConditionOrLast -Description 'the late cancellation was decided' -Journal $journal `
    -Criterion 'late-cancellation' -TimeoutSeconds 30 `
    -Probe {
        $body = (Invoke-RestMethod -Uri "$($onboard.BaseUrl)/$($onboard.Prefix)/load-cancellations" -TimeoutSec 10).body
        @($body.cancellations) | Where-Object { [string]$_.cancellationId -eq $cancellationId } | Select-Object -First 1
    } `
    -Until { param($v) $null -ne $v -and $null -ne $v.decision }
$assertions.Add(
    'L2-SEJ-04', '迟到的取消被拒，原因码 WORKLIST_REVISION_STALE（不再是误导人去查授权的 ACTION_NOT_ALLOWED_IN_STATE）',
    ($null -ne $cancellation -and [string]$cancellation.decision -eq 'REJECTED' -and
        [string]$cancellation.problemReasonCode -eq 'WORKLIST_REVISION_STALE'),
    'REJECTED / WORKLIST_REVISION_STALE',
    $(if ($null -eq $cancellation) { '(no decision)' } else { "$($cancellation.decision) / $($cancellation.problemReasonCode)" }))

# --- 5. 车离站到关卡：号在空清单之上，全程没有两版同号 ---------------------------------------------------------

$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $Context.GateStationRiotId
$gateStopRow = @(Invoke-L2Query -Connection $connection -Sql (
        "SELECT StationId FROM JourneyStops WHERE JourneyId = '$journeyId' AND StationRiotId = $($Context.GateStationRiotId)"))
$gateStationId = [string]$gateStopRow[0].StationId
$atGate = Wait-L2ConditionOrLast -Description 'the gate worklist went out' -Journal $journal -Criterion 'gate-worklist' `
    -TimeoutSeconds 60 `
    -Probe { @((Get-Worklists) | Where-Object { $_.StationId -eq $gateStationId -and $_.Items -gt 0 }) } `
    -Until { param($v) @($v).Count -ge 1 }
$worklistsAtGate = Get-Worklists
$revisions = @($worklistsAtGate | ForEach-Object { $_.Revision })
$assertions.Add(
    'L2-SEJ-05', '关卡那一版清单的号在空清单之上，整条清单流没有两版同号',
    (@($atGate).Count -ge 1 -and $null -ne $empty -and @($atGate)[0].Revision -gt $empty.Revision -and
        @($revisions | Select-Object -Unique).Count -eq $revisions.Count),
    'gate above the empty one, all distinct', (Format-Worklists $worklistsAtGate))

$completed = Wait-L2ConditionOrLast -Description 'the journey completed after the gate unload' -Journal $journal `
    -Criterion 'completed' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'Completed' }
$peerAfter = Get-PeerState
$assertions.Add(
    'L2-SEJ-06', '从到 11 号站起会话没断过：合成车载端的会话代不变、没进过 FAULTED，旅程在关卡卸完收尾',
    ($peerAfter.Generation -eq $peerBefore.Generation -and $peerAfter.Readiness -ne 'FAULTED' -and
        [string]$completed.Stage -eq 'Completed'),
    "generation $($peerBefore.Generation), Completed",
    "generation $($peerAfter.Generation) ($($peerAfter.Readiness)), $(Format-L2CargoJourney $completed)")

$journal.Note('一站结束而旅程继续：车收到这一站的空清单，迟到的扫码与取消都得到「过时」的明确答复，后面各站的号顺延一号。')
