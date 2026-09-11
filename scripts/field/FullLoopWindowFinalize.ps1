#Requires -Version 7

<#
.SYNOPSIS
    The -Finalize half of window two (FW-FL2, 8005-agv-program#20). Dot-sourced by
    Invoke-SlotConvergenceFieldWindow.ps1 once it has collected the final checkpoint; never run on its own.

.DESCRIPTION
    Everything this file reads is already in the caller's scope: $rows (this window's rows from the final
    copy of the server's store), $record (the field record the driver wrote), $assertions, $facts,
    $identity, $ioKind, $ioAddress, $snapshotRoot, and the helpers Get-Rows, Get-ScenarioRecord,
    Get-CheckpointRows, Get-CheckpointDirectory, Test-L2Null, Write-Json and Add-TimelineEvent.

    Window two is five claims, each answerable from rows the two ends wrote down, plus one about the
    server's configuration file:

      T   a stop nobody scans ends at the station deadline, suppressed for good, and the vehicle moves on
          (decision 7, which had been in production since #18 without once being watched)
      X   取消订单 before anything is loaded
      NE  an unload closed with the basket still inside reopens and has no cancellation branch
      N   a whole journey, intake to gate: sublot, load, departure safety, unload, completion
      CH  the charging errand between two journeys, and the next demand taken afterwards
      R   a service restart with the vehicle standing still ends in a Ready session, not in the
          RecoveryRequired of docs/defects/20260908-session-recovery-required-never-clears-while-vehicle-idle.md

    What a moment overwrites is judged from the checkpoint taken at that moment (NE, R); the rest from the
    final rows.
#>

function Get-DemandRow([string]$DemandId) {
    return @(Get-Rows -Table 'AcceptedDemands' -Where { $_.DemandId -eq $DemandId }) | Select-Object -First 1
}

function Get-SuppressionRow([string]$DemandId) {
    return @(Get-Rows -Table 'TransportDemandSuppressions' -Where { $_.DemandId -eq $DemandId }) | Select-Object -First 1
}

function Get-OperationRows([string]$DemandId) {
    return @(Get-Rows -Table 'StationOperations' -Where { $_.DemandId -eq $DemandId })
}

function Get-Seconds([object]$From, [object]$To) {
    if (-not $From -or -not $To -or (Test-L2Null $From) -or (Test-L2Null $To)) { return $null }
    return [Math]::Round(([datetimeoffset]$To - [datetimeoffset]$From).TotalSeconds, 1)
}

# Progress events for one slot of one attempt. An unload attempt can cover several slots, so a count per
# attempt would add the other slots' pulses to the one being judged.
function Get-SlotPhaseCount([string]$AttemptId, [string]$Phase, [int]$SlotNo) {
    $count = 0
    foreach ($row in @($rows['ProtocolInbox'])) {
        if ([string]$row.MessageType -ne 'OperationProgress') { continue }
        try { $payload = ([string]$row.RequestJson | ConvertFrom-Json).payload } catch { continue }
        if ($payload.slotOperationAttemptId -ne $AttemptId -or $payload.phase -ne $Phase) { continue }
        if (@($payload.activeUnlockSlots | ForEach-Object { [int]$_ }) -contains $SlotNo) { $count++ }
    }
    return $count
}

$facts['sublotWaitMinutes'] = $SublotWaitMinutes
$facts['journeyIds'] = (@($record.journeyIds) -join ', ')

# --- T: nobody scans (decision 7) ------------------------------------------------------------------------

$scenarioT = Get-ScenarioRecord -Id 'T'
if ($scenarioT) {
    $demand = Get-DemandRow $scenarioT.demandId
    $suppression = Get-SuppressionRow $scenarioT.demandId
    $facts['T.demand'] = "$($scenarioT.demandId) / $($scenarioT.sublot) / 停靠 $($scenarioT.stopSequence)"
    $assertions.Add('FL2-T-01', '到站不录入 SUBLOT：需求被服务端终结为 Cancelled，并按它的 TransportDemandKey 以 CANCELLED_BY_STATION_TIMEOUT 永久抑制',
        ($demand -and [string]$demand.Status -eq 'Cancelled' -and $suppression -and
            [string]$suppression.ReasonCode -eq 'CANCELLED_BY_STATION_TIMEOUT' -and
            [string]$suppression.TransportDemandKey -eq [string]$demand.TransportDemandKey),
        'Cancelled / CANCELLED_BY_STATION_TIMEOUT / 抑制键 = 需求键',
        "$($demand ? $demand.Status : '(无需求行)') / $($suppression ? $suppression.ReasonCode : '(无抑制行)') / 键$(($demand -and $suppression -and [string]$suppression.TransportDemandKey -eq [string]$demand.TransportDemandKey) ? '一致' : '不一致或缺失')")

    $operations = Get-OperationRows $scenarioT.demandId
    $assertions.Add('FL2-T-02', '终结之前一次仓位操作都没有下发——没人扫码就没有装货',
        ($operations.Count -eq 0), 0, $operations.Count)

    $window = Get-Seconds $scenarioT.serverWaitingAt $scenarioT.deadlineAt
    $expectedSeconds = $SublotWaitMinutes * 60
    $assertions.Add('FL2-T-03', "站点期限是 $SublotWaitMinutes 分钟：车载端拿到的期限距驱动脚本看到服务端开始等 SUBLOT 不超过 $SublotWaitMinutes 分钟、且不短于它 60 秒以上",
        ($null -ne $window -and $window -le $expectedSeconds -and $window -ge ($expectedSeconds - 60)),
        "$($expectedSeconds - 60)–$expectedSeconds 秒", ($null -eq $window) ? '(期限未发布或未记录)' : "$window 秒")

    $late = Get-Seconds $scenarioT.deadlineAt $scenarioT.settledObservedAt
    $assertions.Add('FL2-T-04', '到期才结算、没有无限拖着：结算被看到的时刻落在期限之后 0–180 秒',
        ($null -ne $late -and $late -ge 0 -and $late -le 180),
        '0–180 秒', ($null -eq $late) ? '(未记录)' : "$late 秒")

    $journeyOfT = @(Get-Rows -Table 'JourneyDemands' -Where { $_.DemandId -eq $scenarioT.demandId }) | Select-Object -First 1
    $runtimeOfT = $journeyOfT ? (@(Get-Rows -Table 'JourneyRuntimes' -Where { $_.JourneyId -eq $journeyOfT.JourneyId }) | Select-Object -First 1) : $null
    $leaseOfT = $journeyOfT ? (@(Get-Rows -Table 'VehicleDispatchLeases' -Where { $_.JourneyId -eq $journeyOfT.JourneyId }) | Select-Object -First 1) : $null
    $assertions.Add('FL2-T-05', '车被释放：结算后旅程自己离开这一站，这趟旅程最终 Completed、派车租约已释放——没有停在 AwaitingSublot',
        ([string]$scenarioT.positionAfter -ne "$($scenarioT.stopSequence)/AwaitingSublot" -and
            $runtimeOfT -and [string]$runtimeOfT.Stage -eq 'Completed' -and $leaseOfT -and -not (Test-L2Null $leaseOfT.ReleasedAt)),
        '离站 / Completed / 租约已释放',
        "$($scenarioT.positionAfter) / $($runtimeOfT ? $runtimeOfT.Stage : '(无旅程行)') / $(($leaseOfT -and -not (Test-L2Null $leaseOfT.ReleasedAt)) ? '已释放' : '未释放或无租约行')")
} else {
    $assertions.Add('FL2-T-01', '到站不录入 SUBLOT 这一幕演到了', $false, '现场记录里有场景 T', '(无)')
}

# --- X: cancelled before loading ------------------------------------------------------------------------

$scenarioX = Get-ScenarioRecord -Id 'X'
if ($scenarioX) {
    $demand = Get-DemandRow $scenarioX.demandId
    $suppression = Get-SuppressionRow $scenarioX.demandId
    $facts['X.demand'] = "$($scenarioX.demandId) / $($scenarioX.sublot) / 停靠 $($scenarioX.stopSequence)"
    $facts['X.faceAnswer'] = [string]$scenarioX.faceAnswer
    $assertions.Add('FL2-X-01', '到站还没装货就取消：需求 Cancelled，并按它的 TransportDemandKey 以 CANCELLED_BY_OPERATOR 永久抑制（判的是服务端库，不是自动化面的返回）',
        ($demand -and [string]$demand.Status -eq 'Cancelled' -and $suppression -and
            [string]$suppression.ReasonCode -eq 'CANCELLED_BY_OPERATOR' -and
            [string]$suppression.TransportDemandKey -eq [string]$demand.TransportDemandKey),
        'Cancelled / CANCELLED_BY_OPERATOR / 抑制键 = 需求键',
        "$($demand ? $demand.Status : '(无需求行)') / $($suppression ? $suppression.ReasonCode : '(无抑制行)')")

    $operations = Get-OperationRows $scenarioX.demandId
    $assertions.Add('FL2-X-02', '取消发生在装货之前：这条需求没有任何仓位操作', ($operations.Count -eq 0), 0, $operations.Count)

    $assertions.Add('FL2-X-03', '取消用的是车载端界面那一刻真的给出的按钮（availableRecoveryActions 含 LOAD_CANCELLATION），之后旅程自己离开这一站',
        (@($scenarioX.actionsOffered) -contains 'LOAD_CANCELLATION' -and
            [string]$scenarioX.positionAfter -ne "$($scenarioX.stopSequence)/AwaitingSublot"),
        'LOAD_CANCELLATION 在按钮里 / 离站',
        "$(@($scenarioX.actionsOffered) -join ',') / $($scenarioX.positionAfter)")
} else {
    $assertions.Add('FL2-X-01', '到站还没装货就取消这一幕演到了', $false, '现场记录里有场景 X', '(无)')
}

# --- NE: unload closed with the basket inside ------------------------------------------------------------

$scenarioNE = Get-ScenarioRecord -Id 'NE'
if ($scenarioNE) {
    $attemptId = [string]$scenarioNE.slotOperationAttemptId
    $slotNo = [int]$scenarioNE.slotNo
    $rounds = [int]$scenarioNE.rounds
    $unlocking = Get-SlotPhaseCount -AttemptId $attemptId -Phase 'UNLOCKING' -SlotNo $slotNo
    $facts['NE.attempt'] = "$attemptId / 仓 $slotNo / $($scenarioNE.observedRounds)"
    $assertions.Add('FL2-NE-01', "关门时货还在：车载端自己把这一仓重开了 $rounds 轮（该仓 UNLOCKING 至少 $($rounds + 1) 次）",
        ($unlocking -ge ($rounds + 1)), ">= $($rounds + 1)", $unlocking)

    $label = [string]$scenarioNE.checkpoint
    $cpOperation = @(Get-CheckpointRows -Label $label -Table 'StationOperations') | Where-Object { $_.SlotOperationAttemptId -eq $attemptId } | Select-Object -First 1
    $cpDemand = @(Get-CheckpointRows -Label $label -Table 'AcceptedDemands') | Where-Object { $_.DemandId -eq $scenarioNE.demandId } | Select-Object -First 1
    $cpRuntime = @(Get-CheckpointRows -Label $label -Table 'JourneyRuntimes') | Where-Object { [string]$_.Stage -eq 'AwaitingUnloadResult' } | Select-Object -First 1
    $assertions.Add('FL2-NE-02', "重开 $rounds 轮之后卸货仍在进行（checkpoint $label）：操作 Prepared、旅程停在 AwaitingUnloadResult、需求没有被取消",
        ($cpOperation -and [string]$cpOperation.Status -eq 'Prepared' -and $cpRuntime -and $cpDemand -and [string]$cpDemand.Status -ne 'Cancelled'),
        'Prepared / AwaitingUnloadResult / 需求未取消',
        "$($cpOperation ? $cpOperation.Status : '(无操作行)') / $($cpRuntime ? 'AwaitingUnloadResult' : '(旅程不在卸货等待)') / $($cpDemand ? $cpDemand.Status : '(无需求行)')")

    $operation = @(Get-Rows -Table 'StationOperations' -Where { $_.SlotOperationAttemptId -eq $attemptId }) | Select-Object -First 1
    $results = @(Get-Rows -Table 'OperationResults' -Where { $_.SlotOperationAttemptId -eq $attemptId })
    $badResults = @($results | Where-Object { [string]$_.OverallOutcome -ne 'COMPLETED' })
    $demand = Get-DemandRow $scenarioNE.demandId
    $assertions.Add('FL2-NE-03', '唯一的出口是取空：卸货 Committed，这个 attempt 只有 COMPLETED 结果，需求 Succeeded',
        ($operation -and [string]$operation.Status -eq 'Committed' -and $results.Count -gt 0 -and $badResults.Count -eq 0 -and
            $demand -and [string]$demand.Status -eq 'Succeeded'),
        'Committed / 仅 COMPLETED / Succeeded',
        "$($operation ? $operation.Status : '(无操作行)') / $(@($results | ForEach-Object { $_.OverallOutcome }) -join ',') / $($demand ? $demand.Status : '(无需求行)')")
} else {
    $assertions.Add('FL2-NE-01', '关卡卸货未取空这一幕演到了', $false, '现场记录里有场景 NE', '(无)')
}

# --- N: a whole journey ------------------------------------------------------------------------------------

$journeyIds = @($record.journeyIds | Where-Object { $_ })
$journeySummaries = foreach ($journeyId in $journeyIds) {
    $runtime = @(Get-Rows -Table 'JourneyRuntimes' -Where { $_.JourneyId -eq $journeyId }) | Select-Object -First 1
    $lease = @(Get-Rows -Table 'VehicleDispatchLeases' -Where { $_.JourneyId -eq $journeyId }) | Select-Object -First 1
    [pscustomobject]@{
        JourneyId = $journeyId
        Stage     = $runtime ? [string]$runtime.Stage : '(无旅程行)'
        Released  = [bool]($lease -and -not (Test-L2Null $lease.ReleasedAt))
    }
}
$journeySummaries = @($journeySummaries)
$assertions.Add('FL2-N-01', '窗口里的每一趟旅程都走到 Completed，派车租约都已释放',
    ($journeySummaries.Count -ge 1 -and @($journeySummaries | Where-Object { $_.Stage -ne 'Completed' -or -not $_.Released }).Count -eq 0),
    '全部 Completed / 已释放', (($journeySummaries | ForEach-Object { "$($_.JourneyId.Substring(0, 8))=$($_.Stage)/$($_.Released ? '已释放' : '未释放')" }) -join '; '))

# A demand that went the whole way: sublot consumed, load and unload committed, and the stop it was loaded
# at left through a departure safety check. The load committing at all is also the field re-run of
# docs/defects/20260829-commanded-load-invalidates-session-readiness.md, whose symptom was that a
# server-commanded load could never complete.
$fullLoops = [System.Collections.Generic.List[string]]::new()
$loopNotes = [System.Collections.Generic.List[string]]::new()
foreach ($journeyDemand in @(Get-Rows -Table 'JourneyDemands' -Where { $journeyIds -contains $_.JourneyId })) {
    $demand = Get-DemandRow $journeyDemand.DemandId
    if (-not $demand -or [string]$demand.Status -ne 'Succeeded') { continue }
    $operations = Get-OperationRows $journeyDemand.DemandId
    $load = @($operations | Where-Object { $_.OperationType -eq 'Load' }) | Select-Object -First 1
    $unload = @($operations | Where-Object { $_.OperationType -eq 'Unload' }) | Select-Object -First 1
    $stop = @(Get-Rows -Table 'JourneyStops' -Where { $_.JourneyId -eq $journeyDemand.JourneyId -and [int]$_.Sequence -eq [int]$journeyDemand.StopSequence }) | Select-Object -First 1
    $sublotConsumed = -not (Test-L2Null $journeyDemand.ConsumedSublotMessageId)
    $safetyConsumed = $stop -and -not (Test-L2Null $stop.ConsumedSafetyResultMessageId)
    $ok = $sublotConsumed -and $load -and [string]$load.Status -eq 'Committed' -and $unload -and [string]$unload.Status -eq 'Committed' -and $safetyConsumed
    $note = "$($journeyDemand.DemandId.Substring(0, 8)): SUBLOT$($sublotConsumed ? '✓' : '✗') 装$($load ? $load.Status : '-') 安检$($safetyConsumed ? '✓' : '✗') 卸$($unload ? $unload.Status : '-')"
    $loopNotes.Add($note)
    if ($ok) { $fullLoops.Add([string]$journeyDemand.JourneyId) }
}
$assertions.Add('FL2-N-02', '完整闭环至少走通一次：一条需求录了 SUBLOT、装货提交、过出车前安全检查、卸货提交、需求 Succeeded（装货能提交即缺陷 20260829 现场未复现）',
    ($fullLoops.Count -gt 0), '至少一条需求全链路走通', ($loopNotes.Count -gt 0) ? ($loopNotes -join '; ') : '(没有 Succeeded 的需求)')
$assertions.Add('FL2-N-03', '每一条 Succeeded 的需求都是全链路走通的——没有哪一条跳过了 SUBLOT、安全检查或卸货提交',
    ($loopNotes.Count -gt 0 -and $fullLoops.Count -eq $loopNotes.Count), "$($loopNotes.Count) 条都走通", "$($fullLoops.Count) 条走通")

# --- CH: the charging errand --------------------------------------------------------------------------------

$scenarioCH = Get-ScenarioRecord -Id 'CH'
if ($scenarioCH) {
    $override = $scenarioCH.thresholdOverride
    $trigger = $override ? [int]$override.trigger : 20
    $resume = $override ? [int]$override.resume : 80
    $facts['CH.thresholds'] = $override ? "临时覆盖 触发 $trigger% / 恢复 $resume%（出厂 20/80）" : '出厂 20/80'
    $run = @(Get-Rows -Table 'AutoChargingRuns' -Where { $_.ChargingRunId -eq $scenarioCH.chargingRunId }) | Select-Object -First 1
    $facts['CH.seen'] = (@($scenarioCH.seen) | ForEach-Object { "$($_.Stage)$($_.BlockReasonCode ? "($($_.BlockReasonCode))" : '')" }) -join ' -> '

    $assertions.Add('FL2-CH-01', '上一趟旅程结束之后才起充电行程，触发时电量低于当时生效的触发线',
        ($run -and [datetimeoffset]$run.CreatedAt -gt [datetimeoffset]$scenarioCH.after -and [int]$run.TriggeredAtBatteryPercent -lt $trigger),
        "创建于 $($scenarioCH.after) 之后 / 触发电量 < $trigger",
        $run ? "$($run.CreatedAt) / $($run.TriggeredAtBatteryPercent)" : '(无充电行程行)')

    $intent = @(Get-Rows -Table 'ChargingOrderIntents' -Where { $run -and $_.UpperId -eq $run.UpperId }) | Select-Object -First 1
    $assertions.Add('FL2-CH-02', '充电行程派了一条去充电桩 211 的 TO_CHARGER 单并已确认',
        ($intent -and [string]$intent.Purpose -eq 'TO_CHARGER' -and [int]$intent.DestinationStationId -eq 211 -and [string]$intent.Status -eq 'CONFIRMED'),
        'TO_CHARGER / 211 / CONFIRMED', $intent ? "$($intent.Purpose) / $($intent.DestinationStationId) / $($intent.Status)" : '(无单)')

    $chargerBlocks = @(@($scenarioCH.seen) | Where-Object { [string]$_.BlockReasonCode -like 'CHARGER_*' })
    $reachedCharging = @(@($scenarioCH.seen) | Where-Object { [string]$_.Stage -eq 'Charging' }).Count -gt 0
    $assertions.Add('FL2-CH-03', '车到桩并真的接上电：行程进入 Charging，驱动脚本全程没有看到 CHARGER_NOT_ENGAGED 或任何 CHARGER_* 阻塞',
        ($reachedCharging -and $chargerBlocks.Count -eq 0),
        '进入 Charging / 无 CHARGER_* 阻塞', $facts['CH.seen'])

    $assertions.Add('FL2-CH-04', "充到恢复线释放：行程 Completed，释放电量 >= $resume",
        ($run -and [string]$run.Stage -eq 'Completed' -and -not (Test-L2Null $run.ReleasedAtBatteryPercent) -and [int]$run.ReleasedAtBatteryPercent -ge $resume),
        "Completed / >= $resume", $run ? "$($run.Stage) / $($run.ReleasedAtBatteryPercent)" : '(无充电行程行)')

    $nextJourney = $null
    if ($run) {
        $nextJourney = @(Get-Rows -Table 'JourneyRuntimes' -Where { [datetimeoffset]$_.CreatedAt -ge [datetimeoffset]$run.UpdatedAt }) |
            Sort-Object { [datetimeoffset]$_.CreatedAt } | Select-Object -First 1
    }
    $runsBetween = @(Get-Rows -Table 'AutoChargingRuns' -Where {
            [datetimeoffset]$_.CreatedAt -gt [datetimeoffset]$scenarioCH.after -and
            (-not $nextJourney -or [datetimeoffset]$_.CreatedAt -lt [datetimeoffset]$nextJourney.CreatedAt) })
    $assertions.Add('FL2-CH-05', '释放之后接着受理下一单：下一趟旅程在充电行程结束之后创建，两趟之间只充了这一次电',
        ($nextJourney -and $runsBetween.Count -eq 1),
        '有下一趟旅程 / 两趟之间 1 次充电',
        "$($nextJourney ? "$($nextJourney.JourneyId.Substring(0, 8)) 创建于 $($nextJourney.CreatedAt)" : '(无下一趟旅程)') / $($runsBetween.Count) 次")

    if ($identity.Contains('serverProductionJourneyRuntime')) {
        $production = $identity['serverProductionJourneyRuntime']
        $assertions.Add('FL2-CH-06', '收尾时临时充电线已经撤掉：appsettings.Production.json 里不再有 chargeTriggerBatteryPercent / chargeResumeBatteryPercent，两个门都关着',
            ($null -eq $production.chargeTriggerBatteryPercent -and $null -eq $production.chargeResumeBatteryPercent -and
                -not $production.enabled -and -not $production.createDispatchEnabled),
            '无覆盖 / 门 Off',
            "trigger=$($production.chargeTriggerBatteryPercent ?? '-') resume=$($production.chargeResumeBatteryPercent ?? '-') runtime=$($production.enabled) dispatch=$($production.createDispatchEnabled)")
    }
} else {
    $assertions.Add('FL2-CH-01', '两趟之间的自动充电这一幕演到了', $false, '现场记录里有场景 CH', '(无)')
}

# --- R: restarts with the vehicle standing still ------------------------------------------------------------

$restarts = @($record.scenarios | Where-Object { [string]$_.id -like 'R*' -and [string]$_.id -ne 'R' })
$assertions.Add('FL2-R-00', '至少观测到一次车静止时的服务重启（缺陷 20260908 的发现条件：真实服务重启加一台真车）',
    ($restarts.Count -ge 1), '>= 1', $restarts.Count)
foreach ($restart in $restarts) {
    $id = [string]$restart.id
    $label = [string]$restart.checkpoint
    $cpSession = @(Get-CheckpointRows -Label $label -Table 'SessionRecoveries') |
        Where-Object { -not $record.agvId -or $_.AgvId -eq $record.agvId } | Select-Object -First 1
    $series = (@($restart.series) | ForEach-Object { "$($_.Generation)/$($_.Readiness)$($_.ReasonCode ? "($($_.ReasonCode))" : '')" }) -join ' -> '
    $facts["$id.series"] = $series
    $assertions.Add("FL2-$id-01", "重启后新会话（generation > 重启前的 $($restart.generationBefore)）回到 Ready，checkpoint $label 上的会话行同样是 Ready",
        ([long]$restart.generationAfter -gt [long]$restart.generationBefore -and $cpSession -and
            [string]$cpSession.Readiness -eq 'Ready' -and [long]$cpSession.SessionGeneration -gt [long]$restart.generationBefore),
        "generation > $($restart.generationBefore) / Ready",
        "$series / checkpoint: $($cpSession ? "$($cpSession.SessionGeneration)/$($cpSession.Readiness)" : '(无会话行)')")

    $toReady = Get-Seconds $restart.serverStartedAt $restart.readyObservedAt
    $assertions.Add("FL2-$id-02", '从服务进程启动到会话 Ready 不超过 60 秒——缺陷现场是 6 分 36 秒不自愈，直到有人重启车载客户端',
        ($null -ne $toReady -and $toReady -ge 0 -and $toReady -le 60),
        '0–60 秒', ($null -eq $toReady) ? '(未记录服务启动时刻)' : "$toReady 秒")

    $before = @(Get-CheckpointRows -Label $label -Table 'JourneyRuntimes') | Where-Object { $_.JourneyId -eq $restart.afterJourneyId } | Select-Object -First 1
    $assertions.Add("FL2-$id-03", '重启时车是静止的：重启前那趟旅程已经 Completed，这正是缺陷的发现条件',
        ($before -and [string]$before.Stage -eq 'Completed'),
        'Completed', $before ? [string]$before.Stage : '(checkpoint 里没有那趟旅程)')
}

# --- window ---------------------------------------------------------------------------------------------------

$assertions.Add('FL2-W-01', '现场记录由驱动脚本按实际动作写出，IO 是车上的 slots-simulator（无人到场）',
    ([bool]$record.drivenBy -and $ioKind -eq 'SIMULATOR'),
    'drivenBy 非空 / SIMULATOR', "drivenBy=$($record.drivenBy ?? '(空)') / $ioKind")

try { $connection.Close(); $connection.Dispose() } catch { }

$outcome = $assertions.AllPassed() ? 'PASS' : 'FAIL'
$document = [ordered]@{
    schemaVersion = 1
    window        = $windowId
    windowName    = '现场窗口二：完整闭环、取消订单、两趟之间自动充电、车静止时服务重启'
    runId         = $runId
    outcome       = $outcome
    identity      = $identity
    fieldRecord   = $record
    facts         = $facts
    assertions    = $assertions.Items
}
Write-Json -Path $assertionsPath -Value $document

$assertionRows = ($assertions.Items | ForEach-Object {
    $expected = ($_.expected | Out-String).Trim() -replace '\r?\n', ' '
    $actual = ($_.actual | Out-String).Trim() -replace '\r?\n', ' '
    "| $($_.id) | $($_.description) | $($_.outcome) | ``$expected`` | ``$actual`` |"
}) -join "`n"
$factRows = ($facts.Keys | ForEach-Object { "| ``$_`` | ``$($facts[$_])`` |" }) -join "`n"
$checkpointRows = (@(Get-ChildItem -LiteralPath $snapshotRoot -Directory | Sort-Object Name) | ForEach-Object {
    "- ``snapshots/$($_.Name)/``" }) -join "`n"
$rehearsalNote = ($SublotWaitMinutes -lt 5 -or -not $identity.Contains('serverProductionJourneyRuntime')) `
    ? "**注意：本次按 ``-SublotWaitMinutes $SublotWaitMinutes`` 判场景 T，且没有读到生产配置，只能算彩排，不能当现场窗口的证据。**" `
    : ''

$summary = @"
# 现场窗口二证据：完整闭环、取消订单、两趟之间自动充电、车静止时服务重启

$rehearsalNote

结论：**$outcome**

对应地图票 [现场窗口二](https://github.com/trytoreachpeak0/8005-agv-program/issues/20)。

## 身份

| 项 | 值 |
| --- | --- |
| runId | ``$runId`` |
| 窗口 | ``$windowId`` |
| 现场 | $($record.site) |
| 现场人员 | 无人到场，$($record.drivenBy ?? '(未记录驱动)') |
| agvId | ``$($record.agvId)`` |
| IO | ``$ioAddress``（$ioKind） |
| 站点期限 | $SublotWaitMinutes 分钟 |
| 旅程 | $((@($record.journeys) | ForEach-Object { "``$($_.journeyId)``（$($_.role)）" }) -join '、') |
| 数据库 | ``$($identity.database)`` |

## 判据

| 判据 | 说明 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- | --- |
$assertionRows

## 实测事实

| 项 | 值 |
| --- | --- |
$factRows

## checkpoint 序列

场景 NE 的「重开几轮之后仍在进行」与场景 R 的「重启之后会话 Ready」都是会被后面的写入覆盖掉的时刻，
只能由当时抓下的 checkpoint 回答。

$checkpointRows

## 目录内容

- ``assertions.json`` —— 机器可读的判据结论，含现场记录原文与实测事实
- ``timeline.jsonl`` —— 一行一次 checkpoint，只追加
- ``logs/`` —— 每次远端拷贝的输出
- ``snapshots/<序号>-<label>/`` —— 每个 checkpoint 当时的库行、两端安装清单、生产配置里的门与充电线、车载端日志

**整个 ``controlserver.db`` 不在这里**。它是生产库，完整副本落在 ``$StageRoot`` 下的 ``$windowId-<runId>``。

## 这份证据证明了什么，没证明什么

**证明了**：在 ``$($record.agvId)``（$($record.site)）上、车走真实线路、IO 接 slots-simulator（``$ioAddress``）时，
一趟 WIRE_TO_GATE 旅程从受理到关卡收尾的软件闭环；决策 7 的站点期限在生产包里真的会终结一条没人扫码的需求并放车；
扫码前取消；卸货没取空时没有取消分支；两趟之间车会自己去 211 充电、到恢复线后接单；车静止时服务重启后会话自己回到 Ready。

**没有证明**：

- **20% / 80% 这两个数本身。**充电那一幕按用户 2026-09-11 的决定临时抬线（见 $($facts['CH.thresholds'] ?? '(无充电幕)')），证的是机制，不是出厂阈值；出厂值只由 L2 ``auto-charge-endurance`` 覆盖。
- **光幕极性、锁反馈时序与机械弹开。**IO 是模拟器，与现场窗口一（无人）同一个缺口，见地图 Out of scope。
- **另外两台车。**
"@

[IO.File]::WriteAllText((Join-Path $EvidenceRoot 'SUMMARY.md'), $summary + "`n", [Text.UTF8Encoding]::new($false))
Add-TimelineEvent -Kind 'window-finalised' -Data @{ outcome = $outcome }

Write-Host "$windowId $outcome -> $EvidenceRoot"
# No exit here: an exit in a dot-sourced file does not end the script that dot-sourced it, which then ran the
# FW-SC1 assertions over this window and wrote them on top of these (L2 real-onboard-field-window2-rehearsal-001).
# The caller exits with this.
$fullLoopExitCode = ($outcome -eq 'PASS') ? 0 : 1
