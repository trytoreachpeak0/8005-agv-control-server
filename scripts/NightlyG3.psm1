#Requires -Version 7

# The nightly G3 (.github/workflows/g3.yml, control-server#582). What the workflow and Invoke-NightlyG3.ps1 decide is
# kept here, so that scripts/Test-NightlyG3.ps1 can check it offline: no runner here starts a process or a window.

function Get-NightlyG3Verdict {
    param(
        [Parameter(Mandatory)][string]$Runner,
        [Parameter(Mandatory)][AllowEmptyString()][string]$EvidenceRoot,
        [Parameter(Mandatory)][AllowNull()][object]$ExitCode
    )

    # One runner's verdict: runner, result (PASS, FAIL, ERROR or the NOT_STARTED_* the caller passed as the exit code),
    # status (the runner's own), failed (what is red, each a name the evidence uses) and detail (one line for a person).
    $verdict = [ordered]@{ runner = $Runner; result = $null; status = $null; failed = @(); detail = '' }
    if ($ExitCode -is [string] -and $ExitCode -like 'NOT_STARTED_*') {
        $verdict.result = $ExitCode
        return [pscustomobject]$verdict
    }

    $path = Join-Path $EvidenceRoot 'run-result.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $verdict.result = 'ERROR'
        $verdict.detail = "no run-result.json (exit $ExitCode)"
        return [pscustomobject]$verdict
    }
    try { $runResult = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -AsHashtable }
    catch {
        $verdict.result = 'ERROR'
        $verdict.detail = "run-result.json does not parse (exit $ExitCode): $($_.Exception.Message)"
        return [pscustomobject]$verdict
    }

    # JSON null or an array parses, and then indexing it throws out of here (review B of PR #591).
    if ($runResult -isnot [System.Collections.IDictionary]) {
        $verdict.result = 'ERROR'
        $verdict.detail = "run-result.json is not a JSON object (exit $ExitCode)"
        return [pscustomobject]$verdict
    }
    $verdict.status = "$($runResult['status'])"
    if ($verdict.status -ceq 'INCONCLUSIVE_RUNNER_ERROR') {
        $verdict.result = 'ERROR'
        $message = $runResult['error']?['message']
        $verdict.detail = if ([string]::IsNullOrWhiteSpace($message)) { "INCONCLUSIVE_RUNNER_ERROR (exit $ExitCode), no error message" } else {
            "INCONCLUSIVE_RUNNER_ERROR: $(($message -replace '\s+', ' ').Trim())" }
        return [pscustomobject]$verdict
    }

    # Every assertion that is not PASS, the runner's own failedAssertions list included (the restart, journey and
    # demand-bearing runners write one; the staged runner does not), then every scenario that did not PASS.
    $failed = [System.Collections.Generic.List[string]]::new()
    $assertions = $runResult['assertions']
    if ($assertions -is [System.Collections.IDictionary]) {
        foreach ($key in $assertions.Keys) { if ("$($assertions[$key])" -cne 'PASS' -and -not $failed.Contains($key)) { $failed.Add($key) } }
    }
    foreach ($name in @($runResult['failedAssertions'])) { if ($null -ne $name -and -not $failed.Contains("$name")) { $failed.Add("$name") } }
    foreach ($scenario in @($runResult['scenarios'])) {
        if ($scenario -is [System.Collections.IDictionary] -and "$($scenario['outcome'])" -cne 'PASS') {
            $reason = (("$($scenario['failureReason'])") -replace '\s+', ' ').Trim()
            $failed.Add("scenario $($scenario['name']): $(if ($reason) { $reason } else { "outcome $($scenario['outcome'])" })")
        }
    }
    $verdict.failed = @($failed)

    if ($ExitCode -eq 0 -and $verdict.status -cmatch '_PASS$' -and $failed.Count -eq 0) {
        $verdict.result = 'PASS'
    } else {
        $verdict.result = 'FAIL'
        $verdict.detail = "status $($verdict.status), exit $ExitCode"
    }
    return [pscustomobject]$verdict
}

function Format-NightlyG3Comment {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Verdicts,
        [Parameter(Mandatory)][string]$RunUrl,
        [Parameter(Mandatory)][string]$Trigger,
        [Parameter(Mandatory)][string]$Ref,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Commits,
        [Parameter(Mandatory)][DateTimeOffset]$StartedAtUtc,
        [string]$StoppedBy,
        [string]$Note,
        # The g3 job's result, when it ran. Anything but success keeps a night from reading 全绿 (review B of PR #591).
        [string]$JobResult,
        # The runners a complete round has a verdict for; one missing keeps a night from reading 全绿.
        [string[]]$ExpectedRunners = @('staged', 'restart', 'demand-bearing', 'journey'),
        # main's trigger: its cron and the moment GitHub fired it (control-server#807). Without both, no lateness is written.
        [string]$TriggerCron,
        [Nullable[DateTimeOffset]]$TriggeredAtUtc,
        # The gate's start deadline, said so that a night that did not start reads as late, not as red.
        [Nullable[DateTimeOffset]]$DeadlineUtc,
        # The night start the gate's deadline used, so that the night is named by the same night (review S4 of PR #817).
        [ValidateRange(0, 23)][int]$NightStartHourCst = 19
    )

    # One comment a night on control-server#581, in Chinese because a person reads it. Its absence means the night did
    # not run, so even an all-green night writes one, and a night that never started writes why.
    function ConvertTo-Cell([string]$Text) { return (($Text -replace '\s+', ' ').Trim() -replace '\|', '\|') }

    $cst = [TimeSpan]::FromHours(8)
    # A night is named by the morning it runs into, whichever side of midnight it started; anything else by its own date.
    $night = if ($Trigger -eq 'schedule') { (Get-NightlyG3NightMorning -StartedAtUtc $StartedAtUtc -NightStartHourCst $NightStartHourCst).ToString('yyyy-MM-dd') } else {
        $StartedAtUtc.ToOffset($cst).ToString('yyyy-MM-dd') }
    $red = @($Verdicts | Where-Object { $_.result -in 'FAIL', 'ERROR' })
    $notStarted = @($Verdicts | Where-Object { "$($_.result)" -like 'NOT_STARTED_*' })
    $passed = @($Verdicts | Where-Object { $_.result -eq 'PASS' } | ForEach-Object runner)
    $missing = @($ExpectedRunners | Where-Object { $_ -notin @($Verdicts | ForEach-Object runner) })
    # 全绿 is earned, never the fall-through: every expected runner PASS, no reason to stop, and the job succeeded.
    $green = $Verdicts.Count -gt 0 -and $missing.Count -eq 0 -and $notStarted.Count -eq 0 -and -not $StoppedBy -and
        @($ExpectedRunners | Where-Object { $_ -notin $passed }).Count -eq 0 -and ([string]::IsNullOrEmpty($JobResult) -or $JobResult -eq 'success')
    $incomplete = @(
        if ($StoppedBy) { $StoppedBy }
        if ($notStarted.Count -gt 0 -and -not $StoppedBy) { ($notStarted | ForEach-Object result | Select-Object -Unique) -join '、' }
        if ($missing.Count -gt 0) { "缺 $($missing -join '、') 的结论" }
        # A red runner already fails the job (the round exits 1), so the job's result says something only without one.
        if ($red.Count -eq 0 -and -not [string]::IsNullOrEmpty($JobResult) -and $JobResult -ne 'success') { "g3 作业 $JobResult" }
    )
    $headline = if ($red.Count -gt 0) {
        "红：$(($red | ForEach-Object runner) -join '、')$(if ($incomplete.Count -gt 0) { "；未跑完：$($incomplete -join '；')" })"
    } elseif ($Verdicts.Count -eq 0) {
        "未跑：$(if ($StoppedBy) { $StoppedBy } else { '没有任何 runner 的结论' })"
    } elseif ($green) { '全绿' } else {
        "未跑完：$(if ($incomplete.Count -gt 0) { $incomplete -join '；' } else { '结论不全' })"
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("**每晚 G3 · $night · $headline**")
    $lines.Add('')
    $meta = @("$(if ($Trigger -eq 'schedule') { '定时触发' } else { '手动触发' })")
    $delay = if ($null -ne $TriggeredAtUtc) { Get-NightlyG3TriggerDelay -Cron $TriggerCron -TriggeredAtUtc $TriggeredAtUtc } else { $null }
    if ($null -ne $delay) {
        $meta += "计划 $($delay.scheduledAtUtc.ToOffset($cst).ToString('HH:mm'))"
        $meta += "实际触发 $($TriggeredAtUtc.ToOffset($cst).ToString('HH:mm'))（晚 $($delay.lateMinutes) 分钟）"
    }
    $meta += "本轮开始 $($StartedAtUtc.ToOffset($cst).ToString('HH:mm'))"
    if ($null -ne $DeadlineUtc) { $meta += "截止 $($DeadlineUtc.ToOffset($cst).ToString('HH:mm'))" }
    $meta += @("[run]($RunUrl)", "ref ``$Ref``")
    foreach ($pair in @(@('controlServer', 'control-server', ''), @('onboardHmi', 'onboard-hmi', '（顶端）'),
            @('slotsSimulator', 'slots-simulator', '（写死的绑定）'), @('protocol', 'protocol', '（写死的绑定）'))) {
        if ($Commits.Contains($pair[0]) -and -not [string]::IsNullOrEmpty("$($Commits[$pair[0]])")) {
            $commit = "$($Commits[$pair[0]])"
            $meta += "$($pair[1]) ``$($commit.Substring(0, [Math]::Min(8, $commit.Length)))``$($pair[2])"
        }
    }
    $lines.Add($meta -join ' · ')
    if ($Note) { $lines.Add(''); $lines.Add((ConvertTo-Cell $Note)) }

    if ($headline -ne '全绿' -and $Verdicts.Count -gt 0) {
        $lines.Add('')
        $lines.Add('| runner | 结论 | 红在哪条 |')
        $lines.Add('| --- | --- | --- |')
        foreach ($verdict in $Verdicts) {
            $failed = @($verdict.failed)
            $cell = @($failed | Select-Object -First 10 | ForEach-Object { "``$(ConvertTo-Cell $_)``" }) -join '；'
            if ($failed.Count -gt 10) { $cell += "；另 $($failed.Count - 10) 条" }
            if ($verdict.detail) { $cell = (@($cell, (ConvertTo-Cell $verdict.detail)) | Where-Object { $_ }) -join '；' }
            $lines.Add("| $($verdict.runner) | $($verdict.result) | $cell |")
        }
    }

    $lines.Add('')
    $lines.Add('自检覆盖运行（`SELF_CHECK_OVERRIDE`，`formalSlicePass=false`），不是门禁证据。红了由当班调度第二天看。')
    return ($lines -join "`n")
}

function Select-NightlyG3BusyRealRigJob {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Runs,
        [Parameter(Mandatory)][System.Collections.IDictionary]$JobsByRun
    )

    # A real-rig job of l2.yml that is not completed holds or wants the one cs-desktop runner. Its run's event does not
    # matter: every l2.yml run carries a real-rig job, skipped (completed) unless it is a rig=real dispatch.
    $busy = [System.Collections.Generic.List[string]]::new()
    foreach ($run in $Runs) {
        if ("$($run['status'])" -ceq 'completed') { continue }
        foreach ($job in @($JobsByRun[$run['id']])) {
            if ($null -ne $job -and "$($job['name'])" -ceq 'real-rig' -and "$($job['status'])" -cne 'completed') {
                $busy.Add("run $($run['id']) job real-rig $($job['status'])")
            }
        }
    }
    return @($busy)
}

function Wait-NightlyG3RigIdle {
    param(
        [Parameter(Mandatory)][scriptblock]$GetBusy,
        [Parameter(Mandatory)][double]$WaitMinutes,
        [int]$PollSeconds = 60,
        [scriptblock]$Sleep = { param($seconds) Start-Sleep -Seconds $seconds },
        [scriptblock]$Now = { [DateTimeOffset]::UtcNow }
    )

    # Waits while GetBusy names something, up to WaitMinutes, and never cancels anything: it only decides whether this
    # night starts. A GetBusy that throws counts as busy, with its message, so a query that cannot answer never reads as
    # an idle rig. Returns idle, busy (what was busy last) and waitedMinutes.
    $poll = {
        try { return @(& $GetBusy) } catch { return @("real-rig query failed: $($_.Exception.Message)") }
    }
    $started = & $Now
    $busy = @(& $poll)
    while ($busy.Count -gt 0) {
        $elapsed = ((& $Now) - $started).TotalSeconds
        if ($elapsed -ge $WaitMinutes * 60) { break }
        Write-Host "G3_RIG_BUSY_WAITING: $($busy -join '; ') ($([Math]::Floor($elapsed / 60)) of $WaitMinutes minutes)"
        & $Sleep ([Math]::Min($PollSeconds, $WaitMinutes * 60 - $elapsed))
        $busy = @(& $poll)
    }
    return [pscustomobject][ordered]@{
        idle = $busy.Count -eq 0
        busy = @($busy)
        waitedMinutes = [Math]::Round(((& $Now) - $started).TotalMinutes, 1)
    }
}

function Get-NightlyG3NightMorning {
    param(
        [Parameter(Mandatory)][DateTimeOffset]$StartedAtUtc,
        [int]$NightStartHourCst = 19
    )

    # The plant's date (CST) of the morning a night runs into. A night runs from NightStartHourCst to NightStartHourCst
    # the next day, so main's trigger at 22:37 CST and the same trigger that GitHub started at 01:19 belong to one night.
    $local = $StartedAtUtc.ToOffset([TimeSpan]::FromHours(8))
    return $local.AddHours(-$NightStartHourCst).Date.AddDays(1)
}

function Get-NightlyG3StartDeadline {
    param(
        [Parameter(Mandatory)][string]$EventName,
        [Parameter(Mandatory)][DateTimeOffset]$StartedAtUtc,
        [double]$DispatchMinutes = 180,
        [ValidateRange(0, 23)][int]$DeadlineHourCst = 7,
        [ValidateRange(0, 23)][int]$NightStartHourCst = 19
    )

    # The last moment a runner may start. A schedule: DeadlineHourCst (07:00) CST of the morning its night runs into,
    # which is in the past when GitHub started it later than that, and then nothing runs. 04:00 until control-server#807:
    # it only kept clear of 8005-mes-ingest's golden-renderer verify near 05:30, which did not move to vm-win-01, while
    # GitHub started the 01:00 trigger two to six hours late. A manual dispatch: DispatchMinutes after its start.
    if ($EventName -ceq 'schedule') {
        # A deadline at or after the hour the next night starts would fall inside that next night (review S4 of PR #817).
        if ($DeadlineHourCst -ge $NightStartHourCst) {
            throw "DeadlineHourCst ($DeadlineHourCst) must be earlier than NightStartHourCst ($NightStartHourCst)."
        }
        $morning = Get-NightlyG3NightMorning -StartedAtUtc $StartedAtUtc -NightStartHourCst $NightStartHourCst
        return ([DateTimeOffset]::new($morning.Year, $morning.Month, $morning.Day, $DeadlineHourCst, 0, 0, [TimeSpan]::FromHours(8))).ToUniversalTime()
    }
    return $StartedAtUtc.ToUniversalTime().AddMinutes($DispatchMinutes)
}

function Get-NightlyG3TriggerDelay {
    param(
        [AllowEmptyString()][string]$Cron,
        [Parameter(Mandatory)][DateTimeOffset]$TriggeredAtUtc
    )

    # How late GitHub fired main's g3-nightly-trigger.yml: the latest firing its daily cron ('M H * * *', UTC) planned
    # at or before the moment the trigger's run was created, and the whole minutes since. Anything but a daily cron --
    # none at all when the trigger was dispatched by hand -- gives $null: no lateness is made up.
    if ($Cron -cnotmatch '^\s*(\d{1,2})\s+(\d{1,2})\s+\*\s+\*\s+\*\s*$') { return $null }
    $minute = [int]$Matches[1]
    $hour = [int]$Matches[2]
    if ($minute -gt 59 -or $hour -gt 23) { return $null }
    $at = $TriggeredAtUtc.ToUniversalTime()
    $scheduled = [DateTimeOffset]::new($at.Year, $at.Month, $at.Day, $hour, $minute, 0, [TimeSpan]::Zero)
    if ($scheduled -gt $at) { $scheduled = $scheduled.AddDays(-1) }
    return [pscustomobject][ordered]@{
        scheduledAtUtc = $scheduled
        lateMinutes = [int][Math]::Floor(($at - $scheduled).TotalMinutes)
    }
}

function Select-NightlyG3LeftoverProcess {
    param(
        # Win32_Process-shaped rows: ProcessId, Name, ExecutablePath, CommandLine.
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Processes,
        [Parameter(Mandatory)][string[]]$OwnRoots,
        # Crash dialogs already up before the round: not this round's.
        [AllowEmptyCollection()][int[]]$WerBefore = @(),
        # The cleanup's own process, whose command line may well name a root.
        [int]$ExcludeProcessId = 0
    )

    # What the round left on the shared desktop (review 2-2 of PR #591). A process is ours when its executable path or its
    # command line is under one of the job's roots: staged, restart and demand-bearing start `dotnet <dll>`, so the path
    # is dotnet.exe's and only the command line names the dll. Roots end in a separator, so a sibling directory whose
    # name merely starts the same is not ours. A crash dialog is ours when it is new since the round started and raised
    # for one of our processes. Returns ProcessId, Name and Reason for each.
    $roots = @($OwnRoots | Where-Object { $_ } | ForEach-Object { $_.TrimEnd([char]'\') + [char]'\' })
    $isOurs = {
        param($row)
        foreach ($text in @($row.ExecutablePath, $row.CommandLine)) {
            if ([string]::IsNullOrEmpty($text)) { continue }
            foreach ($root in $roots) { if ($text.IndexOf($root, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true } }
        }
        return $false
    }
    $ours = @{}
    foreach ($row in $Processes) {
        if ([int]$row.ProcessId -ne $ExcludeProcessId -and "$($row.Name)" -ne 'WerFault.exe' -and (& $isOurs $row)) { $ours[[int]$row.ProcessId] = $row }
    }
    $selected = [System.Collections.Generic.List[object]]::new()
    foreach ($row in $ours.Values) {
        $selected.Add([pscustomobject]@{ ProcessId = [int]$row.ProcessId; Name = $row.Name; Reason = "started from this job: $($row.ExecutablePath)" })
    }
    foreach ($row in $Processes) {
        if ("$($row.Name)" -ne 'WerFault.exe' -or [int]$row.ProcessId -in $WerBefore) { continue }
        $crashed = if ("$($row.CommandLine)" -match '-p\s+(\d+)') { [int]$Matches[1] } else { $null }
        if ($null -ne $crashed -and $ours.ContainsKey($crashed)) {
            $selected.Add([pscustomobject]@{ ProcessId = [int]$row.ProcessId; Name = $row.Name; Reason = "crash dialog for pid $crashed ($($ours[$crashed].Name))" })
        }
    }
    return @($selected)
}

# The two calls to GitHub, kept thin: everything they decide is in the functions above, which the self-check covers.
function Invoke-NightlyG3Api {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Token,
        [string]$Method = 'GET',
        [object]$Body
    )
    $request = @{
        Uri = "https://api.github.com/$($Path.TrimStart('/'))"
        Method = $Method
        Headers = @{ Authorization = "Bearer $Token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
        TimeoutSec = 45
    }
    if ($null -ne $Body) { $request.Body = ($Body | ConvertTo-Json -Depth 5); $request.ContentType = 'application/json; charset=utf-8' }
    return Invoke-RestMethod @request
}

function Get-NightlyG3BusyRealRigJob {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Token
    )
    # The most recent l2.yml runs are enough: a run that holds or waits for the rig is among the newest.
    $runs = @((Invoke-NightlyG3Api -Path "repos/$Repository/actions/workflows/l2.yml/runs?per_page=50" -Token $Token).workflow_runs |
        Where-Object { $_.status -ne 'completed' } | ForEach-Object { [ordered]@{ id = $_.id; event = $_.event; status = $_.status } })
    $jobsByRun = @{}
    foreach ($run in $runs) {
        $jobsByRun[$run['id']] = @((Invoke-NightlyG3Api -Path "repos/$Repository/actions/runs/$($run['id'])/jobs?per_page=100" -Token $Token).jobs |
            ForEach-Object { [ordered]@{ name = $_.name; status = $_.status; conclusion = $_.conclusion } })
    }
    return @(Select-NightlyG3BusyRealRigJob -Runs $runs -JobsByRun $jobsByRun)
}

function Send-NightlyG3Comment {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][int]$Issue,
        [Parameter(Mandatory)][string]$Body,
        [Parameter(Mandatory)][string]$Token
    )
    return Invoke-NightlyG3Api -Path "repos/$Repository/issues/$Issue/comments" -Token $Token -Method POST -Body @{ body = $Body }
}

Export-ModuleMember -Function Get-NightlyG3Verdict, Format-NightlyG3Comment, Select-NightlyG3BusyRealRigJob, Wait-NightlyG3RigIdle,
    Get-NightlyG3StartDeadline, Get-NightlyG3TriggerDelay, Get-NightlyG3BusyRealRigJob, Send-NightlyG3Comment, Select-NightlyG3LeftoverProcess
