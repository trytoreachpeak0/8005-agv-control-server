#Requires -Version 7

<#
.SYNOPSIS
    Offline self-check of the nightly G3's decisions (control-server#582): no runner, no window, no network.

.DESCRIPTION
    Under a minute. Sections 1 to 4 are functions of scripts/NightlyG3.psm1 and section 5 is Invoke-NightlyG3.ps1,
    fed with inputs shaped like what a real round produces, and nothing the nightly run does in the dark is left to be found out the first night.

      1. The verdict of one runner, from its exit code and its run-result.json. Only exit 0 together with a status
         ending in _PASS and no assertion other than PASS is PASS; INCONCLUSIVE_RUNNER_ERROR, a missing or
         unreadable run-result.json, or one that is JSON null or an array, is ERROR, with the runner's own message and
         without throwing (review B of PR #591); anything else is FAIL, naming every
         assertion that is not PASS and every scenario that did not PASS with its failure reason. The rows are
         shaped like the batch-10 exit's own evidence (evidence/g3/20261010-protocol-v3.0.0-*), inlined so that a
         clone without the evidence tree can run this.
      2. The comment on the fixed issue (control-server#581). Named by the plant's date (CST), headed 全绿 only when all
         four runners have a PASS verdict, nothing stopped the round and the g3 job succeeded (two verdicts and no
         reason, or four PASS from a failed job, read 未跑完; a red runner's own job failure is not blamed twice), naming
         each red runner and what is red in it (cut at ten, saying how many more), the commits and
         the run link, saying that it is not gate evidence; a round that stopped early or never started says why. A
         `|` inside a failure reason is escaped, so it cannot break the table. A night is named by the morning it runs
         into, and says when main's trigger was planned, when GitHub fired it and how late, when the round started and
         its deadline (control-server#807); without the trigger's inputs it makes no lateness up.
      3. Waiting for the CI real rig (l2.yml's real-rig job, the other holder of the cs-desktop runner): which runs are
         busy, read from the REST shapes, and the wait on a fake clock -- idle at once, busy then idle, busy for the
         whole limit (NOT_STARTED_RIG_BUSY, naming what was busy), and a query that cannot answer, which is busy, not
         idle. Nothing is ever cancelled; the wait only decides whether this night starts.
      4. The start deadline: a night runs from 19:00 CST to 19:00 CST, and a scheduled night starts no runner after
         07:00 CST of the morning it runs into, whichever side of midnight GitHub started it; a schedule started past
         that runs nothing; both hours are parameters; a manual dispatch is bounded by its own length. And how late
         GitHub fired main's trigger, from its daily cron and the moment its run was created (control-server#807).
      5. The round itself (Invoke-NightlyG3.ps1, most of the minute), against stand-in runners named like the real
         ones: the four run in order, each with the override parameters its real runner declares (demand-bearing has
         no onboard one) and a stage and evidence root of its own; a PASS runner's stage is removed, a red one keeps
         its runtime but not its sources or publish (vm01 has single-digit gigabytes free); results.json carries the
         verdicts and the four commits; past the deadline, under the commit guard or short of disk, the runners left
         are recorded NOT_STARTED_* and never started; a run-result.json that is JSON null makes that runner ERROR and
         the round goes on; an exception mid-round (injected into a copy of the round script's module) stops it with
         ERROR_ROUND and its message, that runner ERROR and the next NOT_STARTED_ERROR, never started; red exits 1,
         green 0.
      6. The workflow's load-bearing lines, as text: nothing in .github/workflows/g3.yml cancels a job, its g3 job runs
         Invoke-NightlyG3.ps1 on [self-hosted, cs-desktop] with the whole history checked out, there is no schedule of
         its own and nightly=true grades a night, the report job runs always(), the g3 job closes the round's leftover
         processes and crash dialogs and reports a work root it cannot remove, and test.yml runs this self-check and
         the override one. The leftover step decides through Select-NightlyG3LeftoverProcess over Win32_Process, runs
         always(), fails the job on a leftover, and does not count the L2 peer cache as the job's.
      7. Whose leftover process it is (Select-NightlyG3LeftoverProcess): dotnet.exe with this job's dll on its command
         line and a WPF peer by its path are the job's, in any letter case; another repository's process, the L2 peer
         cache, a sibling directory whose name only starts like a root, a crash dialog for someone else's process, one
         that was up before the round, and the cleanup itself are not.

    Section 5 also covers the deadline passing during the memory wait (memory reads high through an injected probe
    until after the deadline): the deadline is checked after that wait, so no runner starts. The deadline case's margins
    are wide on purpose, since this runs in test.yml for every pull request.

    Exits 1 when any check comes out the other way, and prints every check either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\Test-NightlyG3.ps1
#>
[CmdletBinding()]
param(
    [string]$ScriptRoot = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $ScriptRoot 'NightlyG3.psm1') -Force

$failures = [System.Collections.Generic.List[string]]::new()
function Check([string]$name, [bool]$ok, [string]$detail) {
    Write-Host ("{0} {1}{2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $(if ($ok) { '' } else { " -- $detail" }))
    if (-not $ok) { $failures.Add($name) }
}

# The written binding, read the way every runner reads it: Get-SharedCommitBinding, taken from the restart runner.
$bindingReader = @([System.Management.Automation.Language.Parser]::ParseFile((Join-Path $ScriptRoot 'run-staged-g3-restart.ps1'), [ref]$null, [ref]$null).FindAll({
            param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-SharedCommitBinding' }, $true))
. ([scriptblock]::Create($bindingReader[0].Extent.Text))
$binding = Get-SharedCommitBinding -Path (Join-Path $ScriptRoot 'run-staged-g3.ps1')

$scratch = Join-Path ([IO.Path]::GetTempPath()) "nightly-g3-selfcheck-$([guid]::NewGuid().ToString('n'))"
New-Item -ItemType Directory -Path $scratch | Out-Null
function New-EvidenceRoot([string]$Name, $RunResult) {
    $root = Join-Path $scratch $Name
    New-Item -ItemType Directory -Path $root | Out-Null
    if ($null -ne $RunResult) {
        if ($RunResult -is [string]) { Set-Content -LiteralPath (Join-Path $root 'run-result.json') -Value $RunResult -NoNewline }
        else { $RunResult | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'run-result.json') }
    }
    return $root
}

try {
    # --- 1. one runner's verdict -------------------------------------------------------------------------------
    $verdictCases = @(
        @{ Name = 'staged red on two content-conflict criteria (the batch-10 exit, 5f3adc42)'; Runner = 'staged'; Exit = 1
           RunResult = [ordered]@{ status = 'STAGED_SLICE_FAIL'; error = $null; assertions = [ordered]@{
                   identityRejections = 'PASS'; sameMessageIdDifferentContentStableConflict = 'FAIL_OR_INCONCLUSIVE'
                   businessMessageSameMessageIdDifferentContentStableConflict = 'FAIL_OR_INCONCLUSIVE'; businessMessageAckDropInSessionReplay = 'PASS' } }
           Result = 'FAIL'; Names = @('sameMessageIdDifferentContentStableConflict', 'businessMessageSameMessageIdDifferentContentStableConflict')
           NotNames = @('identityRejections', 'businessMessageAckDropInSessionReplay') }
        @{ Name = 'restart all PASS'; Runner = 'restart'; Exit = 0
           RunResult = [ordered]@{ status = 'STAGED_G3_PROCESS_RESTART_PASS'; error = $null; failedAssertions = @()
               assertions = [ordered]@{ commitBindingSharedWithMainRunner = 'PASS'; freshDatabaseStartsAtGenerationOne = 'PASS' } }
           Result = 'PASS'; Names = @(); NotNames = @('commitBindingSharedWithMainRunner') }
        @{ Name = 'journey red, failed assertions and a scenario reason (the batch-10 exit, 3411887d)'; Runner = 'journey'; Exit = 1
           RunResult = [ordered]@{ status = 'JOURNEY_G3_SLICE_FAIL'; error = $null
               assertions = [ordered]@{ exactlyOneAcceptedDemandSnapshot = 'PASS'; journeyBlockedOnDeclaredUnknown = 'FAIL'; noScenarioAbortedBeforeItsJudgments = 'FAIL' }
               failedAssertions = @('journeyBlockedOnDeclaredUnknown', 'noScenarioAbortedBeforeItsJudgments')
               scenarios = @(
                   [ordered]@{ name = 'g3-journey-demand-to-pickup'; exitCode = 0; outcome = 'PASS'; failureReason = '' },
                   [ordered]@{ name = 'g3-slot-fault-declaration'; exitCode = 1; outcome = 'FAIL'; failureReason = 'PayloadJson is not a property of the row' }) }
           Result = 'FAIL'; Names = @('journeyBlockedOnDeclaredUnknown', 'noScenarioAbortedBeforeItsJudgments', 'g3-slot-fault-declaration', 'PayloadJson is not a property of the row')
           NotNames = @('exactlyOneAcceptedDemandSnapshot', 'g3-journey-demand-to-pickup') }
        @{ Name = 'a runner error carries its own message'; Runner = 'demand-bearing'; Exit = 1
           RunResult = [ordered]@{ status = 'INCONCLUSIVE_RUNNER_ERROR'; assertions = [ordered]@{ protocolAndBuildIdentityBoundToTheSharedBinding = 'NOT_EVALUATED' }
               error = [ordered]@{ type = 'System.Management.Automation.RuntimeException'; message = 'clone-onboard exited with code 128. See C:\x\clone-onboard.log' } }
           Result = 'ERROR'; Names = @('clone-onboard exited with code 128'); NotNames = @() }
        @{ Name = 'no run-result.json at all'; Runner = 'staged'; Exit = 1; RunResult = $null
           Result = 'ERROR'; Names = @('run-result.json'); NotNames = @() }
        @{ Name = 'an unreadable run-result.json'; Runner = 'staged'; Exit = 1; RunResult = '{ "status": "STAGED_SLICE_PA'
           Result = 'ERROR'; Names = @('run-result.json'); NotNames = @() }
        # The json alone is not trusted: a runner that wrote a PASS and then exited non-zero did not pass.
        @{ Name = 'a PASS status with a non-zero exit'; Runner = 'staged'; Exit = 1
           RunResult = [ordered]@{ status = 'STAGED_SLICE_PASS'; error = $null; assertions = [ordered]@{ identityRejections = 'PASS' } }
           Result = 'FAIL'; Names = @('exit 1'); NotNames = @() }
        # Nor is the status alone: an assertion other than PASS under a _PASS status is red.
        @{ Name = 'a PASS status with an assertion that is not PASS'; Runner = 'staged'; Exit = 0
           RunResult = [ordered]@{ status = 'STAGED_SLICE_PASS'; error = $null; assertions = [ordered]@{ identityRejections = 'PASS'; recoveryReplay = 'NOT_EVALUATED' } }
           Result = 'FAIL'; Names = @('recoveryReplay'); NotNames = @('identityRejections') }
        @{ Name = 'a runner that never started'; Runner = 'journey'; Exit = 'NOT_STARTED_DEADLINE'; RunResult = $null
           Result = 'NOT_STARTED_DEADLINE'; Names = @(); NotNames = @() }
        # Review B of PR #591: a run-result.json that is JSON null used to throw out of the verdict ("Cannot index into a
        # null array") and, through the round, leave a night with two PASS verdicts and no reason headed 全绿.
        @{ Name = 'a run-result.json that is JSON null'; Runner = 'demand-bearing'; Exit = 1; RunResult = 'null'
           Result = 'ERROR'; Names = @('run-result.json'); NotNames = @() }
        @{ Name = 'a run-result.json that is a JSON array'; Runner = 'demand-bearing'; Exit = 0; RunResult = '[1, 2]'
           Result = 'ERROR'; Names = @('run-result.json'); NotNames = @() }
    )
    $i = 0
    foreach ($case in $verdictCases) {
        $i++
        $root = if ($case.Exit -is [string]) { '' } else { New-EvidenceRoot "v$i" $case.RunResult }
        $verdict = $null
        $thrown = $null
        try { $verdict = Get-NightlyG3Verdict -Runner $case.Runner -EvidenceRoot $root -ExitCode $case.Exit } catch { $thrown = $_.Exception.Message }
        $text = if ($null -ne $verdict) { (@($verdict.failed) + @($verdict.detail)) -join ' | ' } else { '' }
        $missing = @($case.Names | Where-Object { -not $text.Contains($_) })
        $extra = @($case.NotNames | Where-Object { $text.Contains($_) })
        Check "verdict, $($case.Name): $($case.Result)" `
            ($null -eq $thrown -and $null -ne $verdict -and $verdict.runner -ceq $case.Runner -and $verdict.result -ceq $case.Result -and $missing.Count -eq 0 -and $extra.Count -eq 0) `
            "$thrown result=$(${verdict}?.result) missing=[$($missing -join ', ')] extra=[$($extra -join ', ')] text=$text"
    }

    # --- 2. the comment on the fixed issue ---------------------------------------------------------------------
    function New-Verdict($Runner, $Result, $Failed = @(), $Detail = '') {
        [pscustomobject][ordered]@{ runner = $Runner; result = $Result; status = $null; failed = @($Failed); detail = $Detail }
    }
    $commits = [ordered]@{ controlServer = '1' * 40; onboardHmi = '2' * 40; slotsSimulator = '3' * 40; protocol = '4' * 40 }
    $common = @{ RunUrl = 'https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/123'; Ref = 'fp/v2-impl'; Commits = $commits
        # 17:05 UTC on the 10th is 01:05 CST on the 11th: the night is named by the plant's date.
        StartedAtUtc = [DateTimeOffset]::Parse('2026-10-10T17:05:00Z') }
    $allPass = @('staged', 'restart', 'demand-bearing', 'journey' | ForEach-Object { New-Verdict $_ 'PASS' })
    $manyFailed = @(1..14 | ForEach-Object { "criterion$_" })
    $commentCases = @(
        @{ Name = 'all green'; Args = @{ Verdicts = $allPass; Trigger = 'schedule' }
           Contains = @('2026-10-11', '全绿', 'actions/runs/123', '不是门禁证据', '11111111', '22222222', '33333333', '44444444'); NotContains = @('2026-10-10', '红：') }
        @{ Name = 'staged and journey red, error on restart'; Args = @{ Trigger = 'schedule'; Verdicts = @(
                   (New-Verdict 'staged' 'FAIL' @('sameMessageIdDifferentContentStableConflict') 'status STAGED_SLICE_FAIL, exit 1'),
                   (New-Verdict 'restart' 'ERROR' @() 'INCONCLUSIVE_RUNNER_ERROR: clone-onboard exited with code 128'),
                   (New-Verdict 'demand-bearing' 'PASS'),
                   (New-Verdict 'journey' 'FAIL' @('scenario g3-slot-fault-declaration: a | b')) ) }
           Contains = @('红：staged、restart、journey', 'sameMessageIdDifferentContentStableConflict', 'clone-onboard exited with code 128', 'a \| b', '不是门禁证据')
           NotContains = @('全绿', 'a | b') }
        @{ Name = 'a round stopped at the deadline'; Args = @{ Trigger = 'schedule'; StoppedBy = 'NOT_STARTED_DEADLINE'; Verdicts = @(
                   (New-Verdict 'staged' 'PASS'), (New-Verdict 'restart' 'PASS'), (New-Verdict 'demand-bearing' 'PASS'), (New-Verdict 'journey' 'NOT_STARTED_DEADLINE')) }
           Contains = @('journey', 'NOT_STARTED_DEADLINE', '未跑完'); NotContains = @('全绿') }
        @{ Name = 'not run at all, the rig busy'; Args = @{ Trigger = 'schedule'; StoppedBy = 'NOT_STARTED_RIG_BUSY'; Verdicts = @(); Note = 'run 999 job real-rig in_progress' }
           Contains = @('未跑', 'NOT_STARTED_RIG_BUSY', 'actions/runs/123', 'run 999 job real-rig in_progress'); NotContains = @('全绿') }
        @{ Name = 'a manual dispatch says so'; Args = @{ Verdicts = $allPass; Trigger = 'workflow_dispatch'; Note = '红证据，临时分支' }
           Contains = @('手动触发', '红证据，临时分支'); NotContains = @() }
        @{ Name = 'a long red list is cut, and says how much'; Args = @{ Trigger = 'schedule'; Verdicts = @((New-Verdict 'staged' 'FAIL' $manyFailed)) }
           Contains = @('criterion1', 'criterion10', '另 4 条'); NotContains = @('criterion11', 'criterion14') }
        # Review B of PR #591: 全绿 only when every runner of the round has a PASS verdict, and only when the g3 job itself
        # succeeded. Two PASS verdicts and no reason, or four PASS from a job that failed after them, are not a green night.
        @{ Name = 'two of four verdicts and no reason'; Args = @{ Trigger = 'schedule'; Verdicts = @((New-Verdict 'staged' 'PASS'), (New-Verdict 'restart' 'PASS')) }
           Contains = @('未跑完', 'demand-bearing', 'journey'); NotContains = @('全绿') }
        @{ Name = 'four PASS from a g3 job that failed'; Args = @{ Trigger = 'schedule'; Verdicts = $allPass; JobResult = 'failure' }
           Contains = @('g3 作业 failure'); NotContains = @('全绿') }
        @{ Name = 'four PASS from a g3 job that succeeded'; Args = @{ Trigger = 'schedule'; Verdicts = $allPass; JobResult = 'success' }
           Contains = @('全绿'); NotContains = @('未跑完') }
        @{ Name = 'a red runner fails the job, and the headline does not blame the job for it'; Args = @{ Trigger = 'schedule'; JobResult = 'failure'; Verdicts = @(
                   (New-Verdict 'staged' 'FAIL' @('secondForcedRecoveryWhileFirstUnsettledIsRejected')), (New-Verdict 'restart' 'PASS'),
                   (New-Verdict 'demand-bearing' 'PASS'), (New-Verdict 'journey' 'PASS')) }
           Contains = @('红：staged**'); NotContains = @('g3 作业', '全绿') }
        # control-server#807: a night says when it was planned, when GitHub fired it and how late, when it started and
        # its deadline, so that a night that did not run reads as late, not as red. Times are CST.
        @{ Name = 'a late night says planned, fired, how late, started and the deadline'; Args = @{ Trigger = 'schedule'; Verdicts = $allPass
                TriggerCron = '37 14 * * *'; TriggeredAtUtc = [DateTimeOffset]::Parse('2026-10-20T17:19:11Z')
                StartedAtUtc = [DateTimeOffset]::Parse('2026-10-20T17:21:00Z'); DeadlineUtc = [DateTimeOffset]::Parse('2026-10-20T23:00:00Z') }
           Contains = @('2026-10-21', '计划 22:37', '实际触发 01:19（晚 162 分钟）', '本轮开始 01:21', '截止 07:00', '全绿'); NotContains = @('2026-10-20') }
        # A night that starts on time, before midnight, is named by the morning it runs into, as one that GitHub started
        # after midnight is: the same night has one name.
        @{ Name = 'a night started before midnight is named by its morning'; Args = @{ Trigger = 'schedule'; Verdicts = $allPass
                TriggerCron = '37 14 * * *'; TriggeredAtUtc = [DateTimeOffset]::Parse('2026-10-20T14:37:09Z')
                StartedAtUtc = [DateTimeOffset]::Parse('2026-10-20T14:38:00Z'); DeadlineUtc = [DateTimeOffset]::Parse('2026-10-20T23:00:00Z') }
           Contains = @('2026-10-21', '实际触发 22:37（晚 0 分钟）', '本轮开始 22:38'); NotContains = @('2026-10-20') }
        @{ Name = 'past the deadline, without the trigger inputs: the deadline, and no lateness made up'; Args = @{ Trigger = 'schedule'; Verdicts = @()
                StoppedBy = 'NOT_STARTED_DEADLINE'; DeadlineUtc = [DateTimeOffset]::Parse('2026-10-10T23:00:00Z') }
           Contains = @('未跑：NOT_STARTED_DEADLINE', '截止 07:00', '本轮开始 01:05'); NotContains = @('计划', '实际触发', '晚 ') }
    )
    foreach ($case in $commentCases) {
        $arguments = @{} + $common
        foreach ($key in $case.Args.Keys) { $arguments[$key] = $case.Args[$key] }
        $comment = $null
        $thrown = $null
        try { $comment = Format-NightlyG3Comment @arguments } catch { $thrown = $_.Exception.Message }
        $missing = @($case.Contains | Where-Object { $null -eq $comment -or -not $comment.Contains($_) })
        $extra = @($case.NotContains | Where-Object { $null -ne $comment -and $comment.Contains($_) })
        Check "comment, $($case.Name)" ($null -eq $thrown -and $missing.Count -eq 0 -and $extra.Count -eq 0) `
            "$thrown missing=[$($missing -join ', ')] extra=[$($extra -join ', ')]"
    }

    # --- 3. waiting for the CI real rig --------------------------------------------------------------------------
    # Which l2.yml runs hold or want the cs-desktop runner, from the REST shapes of /actions/workflows/l2.yml/runs and
    # /actions/runs/{id}/jobs: a real-rig job not completed, whatever its run's event. A synthetic dispatch or a pull
    # request run carries a real-rig job too, skipped, and that is not busy.
    $runs = @(
        [ordered]@{ id = 1; event = 'workflow_dispatch'; status = 'in_progress' },
        [ordered]@{ id = 2; event = 'workflow_dispatch'; status = 'queued' },
        [ordered]@{ id = 3; event = 'workflow_dispatch'; status = 'in_progress' },
        [ordered]@{ id = 4; event = 'pull_request'; status = 'in_progress' },
        [ordered]@{ id = 5; event = 'workflow_dispatch'; status = 'completed' })
    $jobsByRun = @{
        1 = @([ordered]@{ name = 'scenarios'; status = 'completed'; conclusion = 'skipped' }, [ordered]@{ name = 'real-rig'; status = 'in_progress'; conclusion = $null })
        2 = @([ordered]@{ name = 'real-rig'; status = 'queued'; conclusion = $null })
        3 = @([ordered]@{ name = 'scenarios'; status = 'in_progress'; conclusion = $null }, [ordered]@{ name = 'real-rig'; status = 'completed'; conclusion = 'skipped' })
        4 = @([ordered]@{ name = 'scenarios'; status = 'in_progress'; conclusion = $null }, [ordered]@{ name = 'real-rig'; status = 'completed'; conclusion = 'skipped' })
        5 = @([ordered]@{ name = 'real-rig'; status = 'completed'; conclusion = 'success' })
    }
    $busy = $null
    $thrown = $null
    try { $busy = @(Select-NightlyG3BusyRealRigJob -Runs $runs -JobsByRun $jobsByRun) } catch { $thrown = $_.Exception.Message }
    Check 'real-rig busy: the in-progress and the queued real-rig job, not the synthetic dispatch, the pull request or the finished one' `
        ($null -eq $thrown -and $busy.Count -eq 2 -and ($busy -join ';') -match '\brun 1\b.*in_progress' -and ($busy -join ';') -match '\brun 2\b.*queued') "$thrown [$($busy -join '; ')]"

    # The wait itself, on a fake clock: GetBusy answers in turn, Sleep advances the clock and is counted.
    function Invoke-Wait([object[]]$Answers, [double]$WaitMinutes) {
        $state = @{ Now = [DateTimeOffset]::Parse('2026-10-10T17:00:00Z'); Calls = 0; Sleeps = 0 }
        $result = Wait-NightlyG3RigIdle -WaitMinutes $WaitMinutes -PollSeconds 60 `
            -GetBusy {
                $answer = $Answers[[Math]::Min($state.Calls, $Answers.Count - 1)]
                $state.Calls++
                if ($answer -is [string] -and $answer -like 'THROW:*') { throw $answer.Substring(6) }
                return @($answer)
            }.GetNewClosure() `
            -Sleep { param($seconds) $state.Now = $state.Now.AddSeconds($seconds); $state.Sleeps++ }.GetNewClosure() `
            -Now { $state.Now }.GetNewClosure()
        return [pscustomobject]@{ Result = $result; Sleeps = $state.Sleeps; Elapsed = ($state.Now - [DateTimeOffset]::Parse('2026-10-10T17:00:00Z')).TotalMinutes }
    }
    $waitCases = @(
        @{ Name = 'idle at once'; Answers = @(, @()); Idle = $true; Sleeps = 0; Contains = $null }
        @{ Name = 'busy twice, then idle'; Answers = @('run 1 job real-rig in_progress', 'run 1 job real-rig in_progress', @()); Idle = $true; Sleeps = 2; Contains = $null }
        @{ Name = 'busy for the whole 30 minutes'; Answers = @('run 1 job real-rig in_progress'); Idle = $false; Sleeps = 30; Contains = 'run 1 job real-rig in_progress' }
        # A query that cannot answer is not idle: a night that ran blind beside the real rig is what this wait is for.
        @{ Name = 'a query that keeps failing'; Answers = @('THROW:401 Bad credentials'); Idle = $false; Sleeps = 30; Contains = '401 Bad credentials' }
        @{ Name = 'a query that fails once, then idle'; Answers = @('THROW:timeout', @()); Idle = $true; Sleeps = 1; Contains = $null }
    )
    foreach ($case in $waitCases) {
        $outcome = $null
        $thrown = $null
        try { $outcome = Invoke-Wait $case.Answers 30 6>$null } catch { $thrown = $_.Exception.Message }
        $text = if ($null -ne $outcome) { @($outcome.Result.busy) -join '; ' } else { '' }
        Check "rig wait, $($case.Name): $(if ($case.Idle) { 'idle' } else { 'NOT_STARTED_RIG_BUSY' }) after $($case.Sleeps) sleeps" `
            ($null -eq $thrown -and $null -ne $outcome -and $outcome.Result.idle -eq $case.Idle -and $outcome.Sleeps -eq $case.Sleeps -and
             $outcome.Elapsed -le 30 -and ($null -eq $case.Contains -or $text.Contains($case.Contains))) `
            "$thrown idle=$(${outcome}?.Result.idle) sleeps=$(${outcome}?.Sleeps) elapsed=$(${outcome}?.Elapsed) busy=[$text]"
    }

    # --- 4. the start deadline -----------------------------------------------------------------------------------
    # A night runs from 19:00 CST to 19:00 CST the next day, and a scheduled night starts no runner after 07:00 CST
    # (23:00 UTC) of the morning it runs into (control-server#807; 04:00 until then, which only kept clear of the golden
    # renderer that did not move to vm-win-01). main's trigger fires at 22:37 CST and GitHub has started it up to six and
    # a half hours late: a start on either side of midnight belongs to the same night and the same 07:00, a start already
    # past it runs nothing, and from 19:00 it is the next night. The hours are parameters. A manual dispatch is bounded
    # by its own length instead.
    $deadlineCases = @(
        @{ Name = 'scheduled at 22:37 CST, on time'; Event = 'schedule'; Start = '2026-10-20T14:37:00Z'; Deadline = '2026-10-20T23:00:00Z' }
        @{ Name = 'a schedule GitHub started at 01:19 CST, after midnight'; Event = 'schedule'; Start = '2026-10-20T17:19:00Z'; Deadline = '2026-10-20T23:00:00Z' }
        @{ Name = 'a schedule GitHub started at 06:59 CST'; Event = 'schedule'; Start = '2026-10-20T22:59:00Z'; Deadline = '2026-10-20T23:00:00Z' }
        @{ Name = 'a schedule GitHub started at 07:01 CST'; Event = 'schedule'; Start = '2026-10-20T23:01:00Z'; Deadline = '2026-10-20T23:00:00Z' }
        @{ Name = 'a schedule GitHub started at 18:59 CST, still that night'; Event = 'schedule'; Start = '2026-10-21T10:59:00Z'; Deadline = '2026-10-20T23:00:00Z' }
        @{ Name = 'a schedule started at 19:00 CST is the next night'; Event = 'schedule'; Start = '2026-10-21T11:00:00Z'; Deadline = '2026-10-21T23:00:00Z' }
        @{ Name = 'the old 01:05 CST trigger'; Event = 'schedule'; Start = '2026-10-10T17:05:00Z'; Deadline = '2026-10-10T23:00:00Z' }
        @{ Name = 'a deadline hour of 4 still gives 04:00 CST'; Event = 'schedule'; Start = '2026-10-10T17:05:00Z'; Deadline = '2026-10-10T20:00:00Z'; Extra = @{ DeadlineHourCst = 4 } }
        @{ Name = 'a night starting at 21:00 CST puts 20:30 in the night before'; Event = 'schedule'; Start = '2026-10-20T12:30:00Z'; Deadline = '2026-10-19T23:00:00Z'; Extra = @{ NightStartHourCst = 21 } }
        @{ Name = 'a manual dispatch in the afternoon'; Event = 'workflow_dispatch'; Start = '2026-10-11T06:00:00Z'; Deadline = '2026-10-11T09:00:00Z' }
        @{ Name = 'a manual dispatch at 22:37 CST is not a night'; Event = 'workflow_dispatch'; Start = '2026-10-20T14:37:00Z'; Deadline = '2026-10-20T17:37:00Z' }
    )
    foreach ($case in $deadlineCases) {
        $deadline = $null
        $thrown = $null
        $extra = if ($case.Extra) { $case.Extra } else { @{} }
        try { $deadline = Get-NightlyG3StartDeadline -EventName $case.Event -StartedAtUtc ([DateTimeOffset]::Parse($case.Start)) @extra } catch { $thrown = $_.Exception.Message }
        Check "start deadline, $($case.Name): $($case.Deadline)" ($null -eq $thrown -and $deadline -is [DateTimeOffset] -and $deadline -eq [DateTimeOffset]::Parse($case.Deadline)) `
            "$thrown got $deadline"
    }

    # How late GitHub fired main's trigger (control-server#807): the trigger passes its cron and the moment its own run
    # was created, and the latest planned firing at or before that moment is what it was late against. Only a daily
    # 'M H * * *' cron is read; anything else, or no cron (the trigger dispatched by hand), gives nothing, not an error.
    $delayCases = @(
        @{ Name = 'fired on time'; Cron = '37 14 * * *'; At = '2026-10-20T14:37:20Z'; Scheduled = '2026-10-20T14:37:00Z'; Late = 0 }
        @{ Name = 'fired 162 minutes late, past midnight UTC+8'; Cron = '37 14 * * *'; At = '2026-10-20T17:19:11Z'; Scheduled = '2026-10-20T14:37:00Z'; Late = 162 }
        @{ Name = 'fired late enough to cross the UTC date'; Cron = '0 17 * * *'; At = '2026-10-19T21:19:11Z'; Scheduled = '2026-10-19T17:00:00Z'; Late = 259 }
        @{ Name = 'fired before the hour on the next UTC day'; Cron = '37 14 * * *'; At = '2026-10-21T02:10:00Z'; Scheduled = '2026-10-20T14:37:00Z'; Late = 693 }
        @{ Name = 'no cron: the trigger dispatched by hand'; Cron = ''; At = '2026-10-20T14:37:20Z'; Scheduled = $null }
        @{ Name = 'not a daily cron'; Cron = '*/15 * * * *'; At = '2026-10-20T14:37:20Z'; Scheduled = $null }
    )
    foreach ($case in $delayCases) {
        $delay = $null
        $thrown = $null
        try { $delay = Get-NightlyG3TriggerDelay -Cron $case.Cron -TriggeredAtUtc ([DateTimeOffset]::Parse($case.At)) } catch { $thrown = $_.Exception.Message }
        $ok = $null -eq $thrown -and $(if ($null -eq $case.Scheduled) { $null -eq $delay } else {
                $null -ne $delay -and $delay.scheduledAtUtc -eq [DateTimeOffset]::Parse($case.Scheduled) -and $delay.lateMinutes -eq $case.Late })
        Check "trigger delay, $($case.Name)" $ok "$thrown got $(${delay}?.scheduledAtUtc) late $(${delay}?.lateMinutes)"
    }

    # --- 5. the round itself, against stand-in runners ------------------------------------------------------------
    # Invoke-NightlyG3.ps1 run as a child, with -RunnerRoot at four stand-ins named like the real runners. Each records
    # the parameters it was given, makes a stage tree like a real one (sources/, publish/, runtime/) and writes a
    # run-result.json. The stand-ins declare only the parameters the real runner has: the demand-bearing one has no
    # -SelfCheckOnboardCommit, so passing it one fails that runner.
    $standIns = Join-Path $scratch 'runners'
    New-Item -ItemType Directory -Path $standIns | Out-Null
    $standIn = @'
#Requires -Version 7
param([Parameter(Mandatory)][string]$StageRoot, [Parameter(Mandatory)][string]$EvidenceRoot,
    [string]$SelfCheckControlServerCommit, __ONBOARD__ [string]$BatchId, [double]$MinFreeGiB)
if ((Test-Path -LiteralPath $StageRoot) -or (Test-Path -LiteralPath $EvidenceRoot)) { throw 'StageRoot and EvidenceRoot must not already exist' }
New-Item -ItemType Directory -Path $EvidenceRoot, (Join-Path $StageRoot 'sources\control-server'), (Join-Path $StageRoot 'publish'), (Join-Path $StageRoot 'runtime') | Out-Null
Set-Content -LiteralPath (Join-Path $StageRoot 'runtime\controlserver.db') -Value 'db'
[ordered]@{ runner = '__NAME__'; parameters = $PSBoundParameters } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'arguments.json')
Start-Sleep -Seconds ([int]('0' + $env:NIGHTLY_G3_STANDIN_SLEEP___VAR__))
if ($env:NIGHTLY_G3_STANDIN_NULL -split ',' -contains '__NAME__') { Set-Content -LiteralPath (Join-Path $EvidenceRoot 'run-result.json') -Value 'null'; exit 1 }
$red = $env:NIGHTLY_G3_STANDIN_RED -split ',' -contains '__NAME__'
[ordered]@{ status = $(if ($red) { 'STAGED_SLICE_FAIL' } else { 'STAGED_SLICE_PASS' }); error = $null
    assertions = [ordered]@{ standInAssertion = $(if ($red) { 'FAIL' } else { 'PASS' }) } } | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $EvidenceRoot 'run-result.json')
exit $(if ($red) { 1 } else { 0 })
'@
    foreach ($pair in @(@('staged', 'run-staged-g3.ps1', $true), @('restart', 'run-staged-g3-restart.ps1', $true),
            @('demand-bearing', 'run-demand-bearing-g3-vectors.ps1', $false), @('journey', 'run-journey-g3.ps1', $true))) {
        $text = $standIn.Replace('__NAME__', $pair[0]).Replace('__VAR__', ($pair[0] -replace '-', '_').ToUpperInvariant()).Replace(
            '__ONBOARD__', $(if ($pair[2]) { '[string]$SelfCheckOnboardCommit,' } else { '' }))
        Set-Content -LiteralPath (Join-Path $standIns $pair[1]) -Value $text
    }
    $order = @('staged', 'restart', 'demand-bearing', 'journey')
    $cs = 'c' * 40
    $ob = 'b' * 40
    function Invoke-Round([string]$Name, [hashtable]$Environment, [hashtable]$Extra, [string]$RoundScriptRoot = $ScriptRoot) {
        $work = Join-Path $scratch "round-$Name"
        $arguments = @('-NoProfile', '-File', (Join-Path $RoundScriptRoot 'Invoke-NightlyG3.ps1'), '-WorkRoot', $work, '-RunnerRoot', $standIns,
            '-ControlServerCommit', $cs, '-OnboardCommit', $ob, '-CommitCeilingGiB', '100000', '-MinFreeGiB', '0',
            '-StartDeadlineUtc', ([DateTimeOffset]::UtcNow.AddHours(1).ToString('o')))
        foreach ($key in $Extra.Keys) { $index = [array]::IndexOf($arguments, "-$key"); if ($index -ge 0) { $arguments[$index + 1] = $Extra[$key] } else { $arguments += "-$key", $Extra[$key] } }
        $saved = @{}
        foreach ($key in $Environment.Keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key, $Environment[$key]) }
        try { $output = & pwsh @arguments *>&1 | Out-String; $exit = $LASTEXITCODE }
        finally { foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) } }
        $resultsPath = Join-Path $work 'results.json'
        $results = if (Test-Path -LiteralPath $resultsPath) { Get-Content -Raw -LiteralPath $resultsPath | ConvertFrom-Json -AsHashtable } else { $null }
        return [pscustomobject]@{ Work = $work; Exit = $exit; Output = $output; Results = $results }
    }
    function Get-Arguments([string]$Work, [string]$Runner) {
        $path = Join-Path $Work "evidence\$Runner\arguments.json"
        if (Test-Path -LiteralPath $path) { return (Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -AsHashtable)['parameters'] }
        return $null
    }

    # A round with restart red.
    $round = Invoke-Round 'restart-red' @{ NIGHTLY_G3_STANDIN_RED = 'restart' } @{}
    $verdicts = @(${round}.Results?['verdicts'] | Where-Object { $null -ne $_ })
    Check 'round: results.json carries the four verdicts in order staged, restart, demand-bearing, journey' `
        ($verdicts.Count -eq 4 -and ($verdicts | ForEach-Object { $_['runner'] }) -join ',' -ceq ($order -join ',')) "$($round.Output)"
    Check 'round: PASS, FAIL, PASS, PASS, and the red one names its assertion' `
        ($verdicts.Count -eq 4 -and ($verdicts | ForEach-Object { $_['result'] }) -join ',' -ceq 'PASS,FAIL,PASS,PASS' -and @($verdicts[1]['failed']) -contains 'standInAssertion') `
        "$(($verdicts | ForEach-Object { "$($_['runner'])=$($_['result'])" }) -join ', ')"
    Check 'round: a red round exits 1' ($round.Exit -eq 1) "exit $($round.Exit)"
    Check 'round: results.json carries the commits, the simulator and protocol from the written binding' `
        (${round}.Results?['commits']?['controlServer'] -ceq $cs -and $round.Results['commits']['onboardHmi'] -ceq $ob -and
         $round.Results['commits']['slotsSimulator'] -ceq $binding['SimulatorCommit'] -and $round.Results['commits']['protocol'] -ceq $binding['ProtocolCommit']) `
        "$(${round}.Results?['commits'] | ConvertTo-Json -Compress)"
    $stages = @()
    foreach ($runner in $order) {
        $given = Get-Arguments $round.Work $runner
        $wantsOnboard = $runner -ne 'demand-bearing'
        Check "round, ${runner}: given -SelfCheckControlServerCommit$(if ($wantsOnboard) { ' and -SelfCheckOnboardCommit' } else { ' only' })$(if ($runner -eq 'journey') { ', and a -BatchId' })" `
            ($null -ne $given -and $given['SelfCheckControlServerCommit'] -ceq $cs -and
             ($wantsOnboard -eq ($given['SelfCheckOnboardCommit'] -ceq $ob)) -and ($runner -ne 'journey' -or -not [string]::IsNullOrEmpty($given['BatchId']))) `
            "$($given | ConvertTo-Json -Compress)"
        if ($null -ne $given) {
            $stages += $given['StageRoot']
            Check "round, ${runner}: its stage and evidence roots are under the work root" `
                ($given['StageRoot'].StartsWith($round.Work) -and $given['EvidenceRoot'].StartsWith($round.Work)) "$($given['StageRoot']) / $($given['EvidenceRoot'])"
            # The round's own floor (this round passes 0), not the runner's default of 30 (control-server#585).
            Check "round, ${runner}: given the round's -MinFreeGiB" ($given['MinFreeGiB'] -eq 0 -and $given.ContainsKey('MinFreeGiB')) "$($given['MinFreeGiB'])"
        }
    }
    Check 'round: every runner gets a stage root of its own' (@($stages | Select-Object -Unique).Count -eq 4) "$($stages -join ', ')"
    if ($stages.Count -eq 4) {
        Check 'round: a PASS runner''s stage is removed' (-not (Test-Path -LiteralPath $stages[0])) "$($stages[0]) is still there"
        Check 'round: a red runner keeps its runtime but not its sources or publish' `
            ((Test-Path -LiteralPath (Join-Path $stages[1] 'runtime\controlserver.db')) -and -not (Test-Path -LiteralPath (Join-Path $stages[1] 'sources')) -and
             -not (Test-Path -LiteralPath (Join-Path $stages[1] 'publish'))) "$(Get-ChildItem -LiteralPath $stages[1] -ErrorAction SilentlyContinue | ForEach-Object Name)"
    }

    # A round that passes the deadline after its first runner: the others are recorded, not run. The margins are wide on
    # purpose (review 2-1 of PR #591): from here to staged starting was measured at 2.4 to 3.3 seconds, 7.7 on a busy
    # machine, plus a cold CIM query; with the deadline 5 seconds out this case went red once on unchanged logic, and it
    # runs in test.yml for every pull request. Twelve seconds to start staged, twenty seconds of staged.
    $round = Invoke-Round 'deadline' @{ NIGHTLY_G3_STANDIN_SLEEP_STAGED = '20' } @{ StartDeadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(12).ToString('o') }
    $verdicts = @(${round}.Results?['verdicts'] | Where-Object { $null -ne $_ })
    Check 'round past the deadline: staged ran, the other three NOT_STARTED_DEADLINE and never started' `
        (($verdicts | ForEach-Object { $_['result'] }) -join ',' -ceq 'PASS,NOT_STARTED_DEADLINE,NOT_STARTED_DEADLINE,NOT_STARTED_DEADLINE' -and
         $round.Results['stoppedBy'] -ceq 'NOT_STARTED_DEADLINE' -and $null -eq (Get-Arguments $round.Work 'restart') -and $round.Exit -eq 1) `
        "exit $($round.Exit); $(($verdicts | ForEach-Object { "$($_['runner'])=$($_['result'])" }) -join ', ') $($round.Output)"

    # The deadline passing during the memory wait (review F1 of PR #591): memory reads high until after the deadline, then
    # low. The deadline is checked after the wait, so staged is NOT_STARTED_DEADLINE; checked before it, staged would start.
    $highProbe = Join-Path $scratch 'commit-probe.ps1'
    Set-Content -LiteralPath $highProbe -Value @'
#Requires -Version 7
if ([DateTimeOffset]::UtcNow -lt [DateTimeOffset]::Parse($env:NIGHTLY_G3_TEST_COMMIT_HIGH_UNTIL)) { 1000 } else { 1 }
'@
    $deadlineAt = [DateTimeOffset]::UtcNow.AddSeconds(10)
    $round = Invoke-Round 'deadline-during-memory-wait' @{ NIGHTLY_G3_TEST_COMMIT_HIGH_UNTIL = $deadlineAt.AddSeconds(5).ToString('o') } @{
        StartDeadlineUtc = $deadlineAt.ToString('o'); CommitCeilingGiB = '12'; CommitWaitMinutes = '2'; CommitPollSeconds = '1'; CommitProbeScript = $highProbe }
    $verdicts = @(${round}.Results?['verdicts'] | Where-Object { $null -ne $_ })
    Check 'round whose deadline passes during the memory wait: staged NOT_STARTED_DEADLINE, never started' `
        (($verdicts | ForEach-Object { $_['result'] }) -join ',' -ceq 'NOT_STARTED_DEADLINE,NOT_STARTED_DEADLINE,NOT_STARTED_DEADLINE,NOT_STARTED_DEADLINE' -and
         $null -eq (Get-Arguments $round.Work 'staged') -and $round.Exit -eq 1) `
        "exit $($round.Exit); $(($verdicts | ForEach-Object { "$($_['runner'])=$($_['result'])" }) -join ', ') $($round.Output)"

    foreach ($gate in @(
            @{ Name = 'the commit guard'; Code = 'NOT_STARTED_COMMIT_GUARD'; Extra = @{ CommitCeilingGiB = '0'; CommitWaitMinutes = '0' } },
            @{ Name = 'too little free disk'; Code = 'NOT_STARTED_DISK'; Extra = @{ MinFreeGiB = '100000' } })) {
        $round = Invoke-Round ($gate.Code.ToLowerInvariant()) @{} $gate.Extra
        $verdicts = @(${round}.Results?['verdicts'] | Where-Object { $null -ne $_ })
        Check "round stopped by $($gate.Name): four $($gate.Code), nothing started" `
            ($verdicts.Count -eq 4 -and @($verdicts | Where-Object { $_['result'] -cne $gate.Code }).Count -eq 0 -and $round.Results['stoppedBy'] -ceq $gate.Code -and
             $null -eq (Get-Arguments $round.Work 'staged') -and $round.Exit -eq 1) `
            "exit $($round.Exit); $(($verdicts | ForEach-Object { "$($_['runner'])=$($_['result'])" }) -join ', ') $($round.Output)"
    }

    # --- 6. the workflow's load-bearing lines ----------------------------------------------------------------------
    # Text, not YAML (no parser ships with pwsh), and only what would hurt if it drifted: nothing in it cancels a job,
    # the round runs on cs-desktop with the whole history checked out, and it calls the round script.
    $workflowPath = Join-Path (Split-Path -Parent $ScriptRoot) '.github\workflows\g3.yml'
    $workflow = if (Test-Path -LiteralPath $workflowPath) { Get-Content -Raw -LiteralPath $workflowPath } else { '' }
    Check 'g3.yml: cancel-in-progress is false, and nothing cancels a run' `
        ($workflow -match '(?m)^\s*cancel-in-progress:\s*false\s*$' -and $workflow -notmatch '(?m)^\s*cancel-in-progress:\s*true' -and
         $workflow -notmatch '(?i)run\s+cancel|/cancel\b|force-cancel') 'see g3.yml'
    $g3Job = [regex]::Match($workflow, '(?ms)^  g3:\s*$.*?(?=^  \w[\w-]*:\s*$)').Value
    Check 'g3.yml: the g3 job runs on [self-hosted, cs-desktop], checks out fetch-depth 0 and runs Invoke-NightlyG3.ps1' `
        ($g3Job -match 'runs-on:\s*\[self-hosted,\s*cs-desktop\]' -and $g3Job -match 'fetch-depth:\s*0\b' -and $g3Job -match 'Invoke-NightlyG3\.ps1') 'see g3.yml'
    # The night arrives as a workflow_dispatch from main's g3-nightly-trigger.yml (a schedule on this branch never fires),
    # so it is the nightly input, not the event name, that must give it the 04:00 CST deadline and the 定时触发 label.
    Check 'g3.yml: no schedule of its own, a nightly input, and nightly runs are graded as the schedule in gate and report' `
        ($workflow -notmatch '(?m)^\s*schedule:\s*$' -and $workflow -match '(?m)^\s*nightly:\s*$' -and
         ([regex]::Matches($workflow, "G3_NIGHTLY\s*-eq\s*'true'")).Count -ge 2) 'see g3.yml'
    # control-server#807: main's trigger passes its cron and the moment GitHub fired it, and the report job hands both,
    # with the gate's deadline, to the comment. Inputs main sends that this file does not declare make GitHub refuse the
    # dispatch, so both are declared here (this merges before the trigger that sends them).
    $reportJobText = [regex]::Match($workflow, '(?ms)^  report:\s*$.*').Value
    Check 'g3.yml: trigger_cron and triggered_at inputs, and the report passes them and the deadline to the comment' `
        ($workflow -match '(?m)^      trigger_cron:\s*$' -and $workflow -match '(?m)^      triggered_at:\s*$' -and
         $reportJobText -match 'inputs\.trigger_cron' -and $reportJobText -match 'inputs\.triggered_at' -and $reportJobText -match 'needs\.gate\.outputs\.deadline' -and
         $reportJobText -match '-TriggerCron\b' -and $reportJobText -match '-TriggeredAtUtc\b' -and $reportJobText -match '-DeadlineUtc\b') 'see g3.yml'

    # Review A, D, E of PR #591. A: this self-check runs in test.yml, beside the override one, as the documents say.
    # D: the report job runs whatever the other two did, or a red g3 job leaves no comment and "no comment = no run" lies.
    # E: the g3 job closes what the round left on the shared desktop, and a work root it cannot remove is said, not
    # swallowed.
    $testWorkflowPath = Join-Path (Split-Path -Parent $ScriptRoot) '.github\workflows\test.yml'
    $testWorkflow = if (Test-Path -LiteralPath $testWorkflowPath) { Get-Content -Raw -LiteralPath $testWorkflowPath } else { '' }
    Check 'test.yml runs Test-NightlyG3.ps1 and Test-G3SelfCheckOverride.ps1' `
        ($testWorkflow -match 'pwsh -NoProfile -File \./scripts/Test-NightlyG3\.ps1' -and $testWorkflow -match 'pwsh -NoProfile -File \./scripts/Test-G3SelfCheckOverride\.ps1') 'see test.yml'
    $reportJob = [regex]::Match($workflow, '(?ms)^  report:\s*$.*').Value
    Check 'g3.yml: the report job runs always(), outside pull requests' ($reportJob -match "(?m)^    if:\s*always\(\)\s*&&\s*github\.event_name\s*!=\s*'pull_request'\s*$") 'see g3.yml'
    $removeStep = [regex]::Match($g3Job, '(?ms)- name: Remove the work root.*?(?=^      - |\z)').Value
    Check 'g3.yml: a work root the g3 job cannot remove is reported, not swallowed' ($removeStep -ne '' -and $removeStep -notmatch 'SilentlyContinue') 'see g3.yml'
    # Review 2-2/2-3 of PR #591: the leftover step decides through Select-NightlyG3LeftoverProcess (section 7) over
    # Win32_Process -- command lines included, since three runners start `dotnet <dll>` -- runs always(), fails the job on
    # a leftover, and does not count %LOCALAPPDATA%\8005-l2-peers as ours (no G3 runner uses it; a hand-run L2 might).
    $leftoverStep = [regex]::Match($g3Job, '(?ms)- name: Leftover processes and crash dialogs.*?(?=^      - |\z)').Value
    Check 'g3.yml: the leftover step runs always(), reads Win32_Process through Select-NightlyG3LeftoverProcess, and fails the job on a leftover' `
        ($leftoverStep -match '(?m)^        if:\s*always\(\)\s*$' -and $leftoverStep -match 'Win32_Process' -and $leftoverStep -match 'Select-NightlyG3LeftoverProcess' -and
         $leftoverStep -match 'WerBefore' -and $leftoverStep -match '(?m)^\s*if \(\$leftovers\.Count -gt 0\) \{ exit 1 \}\s*$') 'see g3.yml'
    Check 'g3.yml: the g3 job does not count the L2 peer cache as its own' ($g3Job -notmatch '8005-l2-peers') 'see g3.yml'

    # Review B of PR #591, the round's half. The reviewer's probe: the third runner writes a run-result.json that is JSON
    # null. That runner is ERROR and the round goes on.
    $round = Invoke-Round 'null-run-result' @{ NIGHTLY_G3_STANDIN_NULL = 'demand-bearing' } @{}
    $verdicts = @(${round}.Results?['verdicts'] | Where-Object { $null -ne $_ })
    Check 'round with a JSON-null run-result.json: demand-bearing ERROR, journey still runs, exit 1' `
        (($verdicts | ForEach-Object { $_['result'] }) -join ',' -ceq 'PASS,PASS,ERROR,PASS' -and $round.Exit -eq 1) `
        "exit $($round.Exit); $(($verdicts | ForEach-Object { "$($_['runner'])=$($_['result'])" }) -join ', ') $($round.Output)"
    # Anything else that throws mid-round: a copy of the round script whose verdict throws on the third runner. The round
    # still writes results.json, names why it stopped, records that runner ERROR and the one after NOT_STARTED_ERROR.
    $throwing = Join-Path $scratch 'throwing-round'
    New-Item -ItemType Directory -Path $throwing | Out-Null
    Copy-Item -LiteralPath (Join-Path $ScriptRoot 'Invoke-NightlyG3.ps1'), (Join-Path $ScriptRoot 'run-staged-g3.ps1') -Destination $throwing
    $module = Get-Content -Raw -LiteralPath (Join-Path $ScriptRoot 'NightlyG3.psm1')
    $anchor = '    $verdict = [ordered]@{ runner = $Runner;'
    Check 'round, thrown mid-round: the injection point exists in NightlyG3.psm1' ($module.Contains($anchor)) 'Get-NightlyG3Verdict changed shape; move the injection'
    Set-Content -LiteralPath (Join-Path $throwing 'NightlyG3.psm1') -NoNewline -Value $module.Replace($anchor,
        "    if (`$Runner -eq 'demand-bearing' -and `$ExitCode -isnot [string]) { throw 'injected verdict failure' }`n$anchor")
    $round = Invoke-Round 'thrown' @{} @{} $throwing
    $verdicts = @(${round}.Results?['verdicts'] | Where-Object { $null -ne $_ })
    Check 'round, thrown mid-round: results.json says ERROR_ROUND with the message, demand-bearing ERROR, journey NOT_STARTED_ERROR and never started, exit 1' `
        ("$(${round}.Results?['stoppedBy'])" -like 'ERROR_ROUND:*injected verdict failure*' -and
         ($verdicts | ForEach-Object { $_['result'] }) -join ',' -ceq 'PASS,PASS,ERROR,NOT_STARTED_ERROR' -and
         $null -eq (Get-Arguments $round.Work 'journey') -and $round.Exit -eq 1) `
        "exit $($round.Exit); stoppedBy=$(${round}.Results?['stoppedBy']); $(($verdicts | ForEach-Object { "$($_['runner'])=$($_['result'])" }) -join ', ') $($round.Output)"

    # --- 7. whose leftover process is it ------------------------------------------------------------------------
    # Select-NightlyG3LeftoverProcess over Win32_Process-shaped rows (review 2-2/2-3 of PR #591). A process is this job's
    # when its executable path or its command line is under one of the job's roots: staged, restart and demand-bearing
    # start `dotnet <dll>`, whose path is C:\Program Files\dotnet\dotnet.exe and whose dll is only on the command line.
    # A crash dialog is ours when it is new since the round started and raised for one of our processes.
    $roots = @('C:\actions-runner\cs-desktop\_work\_temp\g3', 'C:\actions-runner\cs-desktop\_work\8005-agv-control-server\8005-agv-control-server')
    function New-Row($ProcessId, $Name, $Path, $CommandLine) {
        [pscustomobject]@{ ProcessId = $ProcessId; Name = $Name; ExecutablePath = $Path; CommandLine = $CommandLine }
    }
    $rows = @(
        (New-Row 101 'dotnet.exe' 'C:\Program Files\dotnet\dotnet.exe' '"C:\Program Files\dotnet\dotnet.exe" C:\actions-runner\cs-desktop\_work\_temp\g3\st\publish\control-server\ControlServer.Host.dll --urls http://127.0.0.1:58207'),
        (New-Row 102 'SQCD.Agv.Wpf.exe' 'C:\ACTIONS-RUNNER\cs-desktop\_work\_temp\g3\jo\sources\onboard-hmi\bin\SQCD.Agv.Wpf.exe' 'SQCD.Agv.Wpf.exe'),
        (New-Row 103 'dotnet.exe' 'C:\Program Files\dotnet\dotnet.exe' '"C:\Program Files\dotnet\dotnet.exe" C:\Users\agvops\AppData\Local\8005-l2-peers\abc\SlotsSimulator.dll'),
        (New-Row 104 'MesIngest.Watch.exe' 'C:\actions-runner\win11-01-mes-ingest-desktop\_work\8005-mes-ingest\Watch\MesIngest.Watch.exe' 'MesIngest.Watch.exe'),
        (New-Row 105 'dotnet.exe' 'C:\Program Files\dotnet\dotnet.exe' 'dotnet C:\actions-runner\cs-desktop\_work\_temp\g3x\Host.dll'),
        (New-Row 106 'WerFault.exe' 'C:\Windows\System32\WerFault.exe' 'C:\Windows\system32\WerFault.exe -u -p 101 -s 1234'),
        (New-Row 107 'WerFault.exe' 'C:\Windows\System32\WerFault.exe' 'C:\Windows\system32\WerFault.exe -u -p 104 -s 99'),
        (New-Row 108 'WerFault.exe' 'C:\Windows\System32\WerFault.exe' 'C:\Windows\system32\WerFault.exe -u -p 102 -s 5'),
        (New-Row 109 'pwsh.exe' 'C:\Program Files\PowerShell\7\pwsh.exe' 'pwsh -File C:\actions-runner\cs-desktop\_work\_temp\g3\cleanup.ps1')
    )
    $selected = $null
    $thrown = $null
    try { $selected = @(Select-NightlyG3LeftoverProcess -Processes $rows -OwnRoots $roots -WerBefore @(108) -ExcludeProcessId 109) } catch { $thrown = $_.Exception.Message }
    $ids = @($selected | ForEach-Object { [int]$_.ProcessId } | Sort-Object)
    Check 'leftovers: dotnet with our dll on its command line (101), our WPF by path in another case (102), and the new crash dialog for 101 (106)' `
        ($null -eq $thrown -and ($ids -join ',') -ceq '101,102,106') "$thrown selected [$($ids -join ',')]"
    Check 'leftovers: not the L2 peer cache (103), not another repository (104), not a sibling of our root (105), not a crash dialog for someone else (107)' `
        ($null -eq $thrown -and @($ids | Where-Object { $_ -in 103, 104, 105, 107 }).Count -eq 0) "$thrown selected [$($ids -join ',')]"
    Check 'leftovers: not a crash dialog that was up before the round (108), not the cleanup itself (109)' `
        ($null -eq $thrown -and $ids -notcontains 108 -and $ids -notcontains 109) "$thrown selected [$($ids -join ',')]"
    $none = $null
    try { $none = @(Select-NightlyG3LeftoverProcess -Processes @($rows[2], $rows[3]) -OwnRoots $roots -WerBefore @() -ExcludeProcessId 0) } catch { $thrown = $_.Exception.Message }
    Check 'leftovers: none of ours among others'' processes is an empty answer' ($null -ne $none -and $none.Count -eq 0) "$thrown [$(@($none | ForEach-Object ProcessId) -join ',')]"

    # All green exits 0.
    $round = Invoke-Round 'green' @{} @{}
    Check 'round all green: four PASS, exit 0' ((@(${round}.Results?['verdicts'] | Where-Object { $null -ne $_ }) | ForEach-Object { $_['result'] }) -join ',' -ceq 'PASS,PASS,PASS,PASS' -and $round.Exit -eq 0) `
        "exit $($round.Exit) $($round.Output)"
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed."
    exit 1
}
Write-Host 'All checks passed.'
