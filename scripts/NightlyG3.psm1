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
        [string]$Note
    )

    # One comment a night on control-server#581, in Chinese because a person reads it. Its absence means the night did
    # not run, so even an all-green night writes one, and a night that never started writes why.
    function ConvertTo-Cell([string]$Text) { return (($Text -replace '\s+', ' ').Trim() -replace '\|', '\|') }

    $night = $StartedAtUtc.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd')
    $red = @($Verdicts | Where-Object { $_.result -in 'FAIL', 'ERROR' })
    $notStarted = @($Verdicts | Where-Object { "$($_.result)" -like 'NOT_STARTED_*' })
    $headline = if ($red.Count -gt 0) {
        "红：$(($red | ForEach-Object runner) -join '、')$(if ($StoppedBy) { "；未跑完：$StoppedBy" })"
    } elseif ($Verdicts.Count -eq 0) {
        "未跑：$(if ($StoppedBy) { $StoppedBy } else { '没有任何 runner 的结论' })"
    } elseif ($StoppedBy -or $notStarted.Count -gt 0) {
        "未跑完：$(if ($StoppedBy) { $StoppedBy } else { ($notStarted | ForEach-Object result | Select-Object -Unique) -join '、' })"
    } else { '全绿' }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("**每晚 G3 · $night · $headline**")
    $lines.Add('')
    $meta = @("$(if ($Trigger -eq 'schedule') { '定时触发' } else { '手动触发' })", "[run]($RunUrl)", "ref ``$Ref``")
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

function Get-NightlyG3StartDeadline {
    param(
        [Parameter(Mandatory)][string]$EventName,
        [Parameter(Mandatory)][DateTimeOffset]$StartedAtUtc,
        [double]$DispatchMinutes = 180
    )
    return $null
}

Export-ModuleMember -Function Get-NightlyG3Verdict, Format-NightlyG3Comment, Select-NightlyG3BusyRealRigJob, Wait-NightlyG3RigIdle,
    Get-NightlyG3StartDeadline
