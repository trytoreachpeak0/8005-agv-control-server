#Requires -Version 7

<#
.SYNOPSIS
    Offline self-check of the nightly G3's decisions (control-server#582): no runner, no window, no network.

.DESCRIPTION
    About twenty seconds. Sections 1 to 4 are functions of scripts/NightlyG3.psm1 and section 5 is Invoke-NightlyG3.ps1,
    fed with inputs shaped like what a real round produces, and nothing the nightly run does in the dark is left to be found out the first night.

      1. The verdict of one runner, from its exit code and its run-result.json. Only exit 0 together with a status
         ending in _PASS and no assertion other than PASS is PASS; INCONCLUSIVE_RUNNER_ERROR, a missing or
         unreadable run-result.json is ERROR, with the runner's own message; anything else is FAIL, naming every
         assertion that is not PASS and every scenario that did not PASS with its failure reason. The rows are
         shaped like the batch-10 exit's own evidence (evidence/g3/20261010-protocol-v3.0.0-*), inlined so that a
         clone without the evidence tree can run this.
      2. The comment on the fixed issue (control-server#581). Named by the plant's date (CST), headed 全绿 only when all
         four PASS, naming each red runner and what is red in it (cut at ten, saying how many more), the commits and
         the run link, saying that it is not gate evidence; a round that stopped early or never started says why. A
         `|` inside a failure reason is escaped, so it cannot break the table.
      3. Waiting for the CI real rig (l2.yml's real-rig job, the other holder of the cs-desktop runner): which runs are
         busy, read from the REST shapes, and the wait on a fake clock -- idle at once, busy then idle, busy for the
         whole limit (NOT_STARTED_RIG_BUSY, naming what was busy), and a query that cannot answer, which is busy, not
         idle. Nothing is ever cancelled; the wait only decides whether this night starts.
      4. The start deadline: a scheduled night starts no runner after 04:00 CST of the night it started in, a schedule
         GitHub started late runs nothing, and a manual dispatch is bounded by its own length.
      5. The round itself (Invoke-NightlyG3.ps1, about twenty seconds), against stand-in runners named like the real
         ones: the four run in order, each with the override parameters its real runner declares (demand-bearing has
         no onboard one) and a stage and evidence root of its own; a PASS runner's stage is removed, a red one keeps
         its runtime but not its sources or publish (vm01 has single-digit gigabytes free); results.json carries the
         verdicts and the four commits; past the deadline, under the commit guard or short of disk, the runners left
         are recorded NOT_STARTED_* and never started; red exits 1, green 0.

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
    )
    foreach ($case in $commentCases) {
        $arguments = @{} + $common + $case.Args
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
    # A scheduled night starts no runner after 04:00 CST (20:00 UTC) of the night it started in, which keeps it clear of
    # the golden renderer's 05:30 verify; GitHub may start a schedule late, and a start already past it runs nothing. A
    # manual dispatch is bounded by its own length instead.
    $deadlineCases = @(
        @{ Name = 'scheduled at 01:05 CST'; Event = 'schedule'; Start = '2026-10-10T17:05:00Z'; Deadline = '2026-10-10T20:00:00Z' }
        @{ Name = 'scheduled at 00:50 CST'; Event = 'schedule'; Start = '2026-10-10T16:50:00Z'; Deadline = '2026-10-10T20:00:00Z' }
        @{ Name = 'scheduled at 03:59 CST'; Event = 'schedule'; Start = '2026-10-10T19:59:00Z'; Deadline = '2026-10-10T20:00:00Z' }
        @{ Name = 'a schedule GitHub started at 05:30 CST'; Event = 'schedule'; Start = '2026-10-10T21:30:00Z'; Deadline = '2026-10-10T20:00:00Z' }
        @{ Name = 'a schedule GitHub started at 09:10 CST, the next UTC day'; Event = 'schedule'; Start = '2026-10-11T01:10:00Z'; Deadline = '2026-10-10T20:00:00Z' }
        @{ Name = 'a manual dispatch in the afternoon'; Event = 'workflow_dispatch'; Start = '2026-10-11T06:00:00Z'; Deadline = '2026-10-11T09:00:00Z' }
    )
    foreach ($case in $deadlineCases) {
        $deadline = $null
        $thrown = $null
        try { $deadline = Get-NightlyG3StartDeadline -EventName $case.Event -StartedAtUtc ([DateTimeOffset]::Parse($case.Start)) } catch { $thrown = $_.Exception.Message }
        Check "start deadline, $($case.Name): $($case.Deadline)" ($null -eq $thrown -and $deadline -is [DateTimeOffset] -and $deadline -eq [DateTimeOffset]::Parse($case.Deadline)) `
            "$thrown got $deadline"
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
    [string]$SelfCheckControlServerCommit, __ONBOARD__ [string]$BatchId)
if ((Test-Path -LiteralPath $StageRoot) -or (Test-Path -LiteralPath $EvidenceRoot)) { throw 'StageRoot and EvidenceRoot must not already exist' }
New-Item -ItemType Directory -Path $EvidenceRoot, (Join-Path $StageRoot 'sources\control-server'), (Join-Path $StageRoot 'publish'), (Join-Path $StageRoot 'runtime') | Out-Null
Set-Content -LiteralPath (Join-Path $StageRoot 'runtime\controlserver.db') -Value 'db'
[ordered]@{ runner = '__NAME__'; parameters = $PSBoundParameters } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'arguments.json')
Start-Sleep -Seconds ([int]('0' + $env:NIGHTLY_G3_STANDIN_SLEEP___VAR__))
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
    function Invoke-Round([string]$Name, [hashtable]$Environment, [hashtable]$Extra) {
        $work = Join-Path $scratch "round-$Name"
        $arguments = @('-NoProfile', '-File', (Join-Path $ScriptRoot 'Invoke-NightlyG3.ps1'), '-WorkRoot', $work, '-RunnerRoot', $standIns,
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
        }
    }
    Check 'round: every runner gets a stage root of its own' (@($stages | Select-Object -Unique).Count -eq 4) "$($stages -join ', ')"
    if ($stages.Count -eq 4) {
        Check 'round: a PASS runner''s stage is removed' (-not (Test-Path -LiteralPath $stages[0])) "$($stages[0]) is still there"
        Check 'round: a red runner keeps its runtime but not its sources or publish' `
            ((Test-Path -LiteralPath (Join-Path $stages[1] 'runtime\controlserver.db')) -and -not (Test-Path -LiteralPath (Join-Path $stages[1] 'sources')) -and
             -not (Test-Path -LiteralPath (Join-Path $stages[1] 'publish'))) "$(Get-ChildItem -LiteralPath $stages[1] -ErrorAction SilentlyContinue | ForEach-Object Name)"
    }

    # A round that passes the deadline after its first runner: the others are recorded, not run.
    $round = Invoke-Round 'deadline' @{ NIGHTLY_G3_STANDIN_SLEEP_STAGED = '8' } @{ StartDeadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(5).ToString('o') }
    $verdicts = @(${round}.Results?['verdicts'] | Where-Object { $null -ne $_ })
    Check 'round past the deadline: staged ran, the other three NOT_STARTED_DEADLINE and never started' `
        (($verdicts | ForEach-Object { $_['result'] }) -join ',' -ceq 'PASS,NOT_STARTED_DEADLINE,NOT_STARTED_DEADLINE,NOT_STARTED_DEADLINE' -and
         $round.Results['stoppedBy'] -ceq 'NOT_STARTED_DEADLINE' -and $null -eq (Get-Arguments $round.Work 'restart') -and $round.Exit -eq 1) `
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
