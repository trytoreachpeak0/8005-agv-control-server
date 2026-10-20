#Requires -Version 7

<#
.SYNOPSIS
    One round of the nightly G3 (control-server#582): the four G3 runners in a row, at the integration branch's tip,
    under a self-check override. Not gate evidence.

.DESCRIPTION
    Run by .github/workflows/g3.yml on the interactive cs-desktop runner, and runnable by hand on any machine that can
    run the four runners. In order -- staged, restart, demand-bearing, journey, the order of a batch exit -- each is
    started as a child pwsh with -SelfCheckControlServerCommit (and -SelfCheckOnboardCommit where the runner has one;
    demand-bearing clones no onboard). The written binding is never moved: every slice of every run is graded
    formalSlicePass false with SELF_CHECK_OVERRIDE, by the runners themselves (scripts/g3-slice-evidence.ps1). The
    simulator and protocol stay at the binding, because run-journey-g3.ps1 has no override for either and all four
    runners of a night should name one identity.

    In a row, never two at once: staged and restart both bind Modbus 1502, and three of the four put WPF windows on
    the one interactive desktop. Before each runner, three gates, and the first that holds stops the round:
    whole-machine committed memory above -CommitCeilingGiB after waiting -CommitWaitMinutes (NOT_STARTED_COMMIT_GUARD,
    l2.yml's real-rig guard), the start deadline, checked after that wait (NOT_STARTED_DEADLINE), and less than
    -MinFreeGiB free on the work root's drive (NOT_STARTED_DISK; vm01 had 7.9 GB free on 2026-10-11 and a runner's
    stage is about 1.7 GB). The runners left are recorded with that code and never started. Anything that throws while
    a runner is handled stops the round too: stoppedBy is ERROR_ROUND: <runner>: <message>, that runner ERROR and the
    ones left NOT_STARTED_ERROR, so a round never ends with fewer verdicts and no reason.

    After each runner its stage goes: all of it on PASS; on anything else sources/, publish/ and peers/ (clones and
    builds, rebuilt from the commits in a minute) and the rest -- runtime databases, journals, logs -- stays for the
    workflow to upload. Its evidence root always stays.

    Writes <WorkRoot>/results.json (stoppedBy, commits, verdicts, runs) whatever happens, and exits 0 only when all
    four PASS.

.PARAMETER RunnerRoot
    Where the four runner scripts are. Defaults to this directory; scripts/Test-NightlyG3.ps1 points it at stand-ins.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ControlServerCommit,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$OnboardCommit,
    [Parameter(Mandatory)][DateTimeOffset]$StartDeadlineUtc,
    [string]$RunnerRoot = $PSScriptRoot,
    [double]$CommitCeilingGiB = 12,
    [double]$CommitWaitMinutes = 30,
    [double]$MinFreeGiB = 4,
    [int]$CommitPollSeconds = 30,
    # A script printing the committed GiB, in place of the machine's own reading. Only scripts/Test-NightlyG3.ps1 passes
    # it, to make memory read high until a given moment (the deadline passing during the memory wait).
    [string]$CommitProbeScript
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'NightlyG3.psm1') -Force

$runners = @(
    [ordered]@{ Name = 'staged'; Script = 'run-staged-g3.ps1'; Stage = 'st'; Onboard = $true }
    [ordered]@{ Name = 'restart'; Script = 'run-staged-g3-restart.ps1'; Stage = 'rs'; Onboard = $true }
    [ordered]@{ Name = 'demand-bearing'; Script = 'run-demand-bearing-g3-vectors.ps1'; Stage = 'db'; Onboard = $false }
    [ordered]@{ Name = 'journey'; Script = 'run-journey-g3.ps1'; Stage = 'jo'; Onboard = $true }
)

if (Test-Path -LiteralPath $WorkRoot) { throw "WorkRoot must not already exist: $WorkRoot" }
$WorkRoot = (New-Item -ItemType Directory -Path $WorkRoot).FullName
$evidenceRoot = Join-Path $WorkRoot 'evidence'
$logsRoot = Join-Path $WorkRoot 'logs'
New-Item -ItemType Directory -Path $evidenceRoot, $logsRoot | Out-Null

# The simulator and protocol commits every runner uses: the written binding, read off run-staged-g3.ps1 beside this
# script (not -RunnerRoot) the way the runners read it.
$bindingAst = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'run-staged-g3.ps1'), [ref]$null, [ref]$null)
function Get-BindingDefault([string]$Name) {
    $parameter = @($bindingAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq $Name })
    if ($parameter.Count -ne 1) { throw "Expected one `$$Name in run-staged-g3.ps1's param block." }
    return $parameter[0].DefaultValue.Value
}
$commits = [ordered]@{
    controlServer = $ControlServerCommit
    onboardHmi = $OnboardCommit
    slotsSimulator = Get-BindingDefault 'SimulatorCommit'
    protocol = Get-BindingDefault 'ProtocolCommit'
}

function Get-CommittedGiB {
    if ($CommitProbeScript) { return [double](& pwsh -NoProfile -File $CommitProbeScript | Select-Object -Last 1) }
    $memory = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory
    return [Math]::Round($memory.CommittedBytes / 1GB, 2)
}
function Get-FreeGiB {
    return [Math]::Round(([IO.DriveInfo]::new([IO.Path]::GetPathRoot($WorkRoot))).AvailableFreeSpace / 1GB, 2)
}
function Remove-Tree([string]$Path) {
    for ($attempt = 1; $attempt -le 3 -and (Test-Path -LiteralPath $Path); $attempt++) {
        try { Remove-Item -LiteralPath $Path -Recurse -Force } catch {
            Write-Host "G3_NIGHTLY_REMOVE_RETRY: $Path ($($_.Exception.Message))"
            Start-Sleep -Seconds 5
        }
    }
    return -not (Test-Path -LiteralPath $Path)
}

$verdicts = [System.Collections.Generic.List[object]]::new()
$runs = [System.Collections.Generic.List[object]]::new()
$stoppedBy = $null
# What the runners left are recorded as once the round stops: the stop reason itself, except after an exception, whose
# stoppedBy carries the message (ERROR_ROUND: ...) and whose runners left are NOT_STARTED_ERROR (review B of PR #591).
$notStartedCode = $null
try {
    foreach ($runner in $runners) {
      $verdictAdded = $false
      try {
        # The memory wait first and the deadline after it (review F of PR #591): up to CommitWaitMinutes may pass in
        # that wait, and a runner must not start past the deadline because it was checked before the wait.
        if ($null -eq $stoppedBy) {
            $waited = [Diagnostics.Stopwatch]::StartNew()
            $committed = Get-CommittedGiB
            while ($committed -gt $CommitCeilingGiB -and $waited.Elapsed.TotalMinutes -lt $CommitWaitMinutes) {
                Write-Host "G3_NIGHTLY_COMMIT_WAITING: $committed GiB committed, starting at or below $CommitCeilingGiB"
                Start-Sleep -Seconds $CommitPollSeconds
                $committed = Get-CommittedGiB
            }
            if ($committed -gt $CommitCeilingGiB) {
                Write-Host "::error title=Nightly G3::$($runner.Name) not started: $committed GiB committed after waiting $CommitWaitMinutes minutes (ceiling $CommitCeilingGiB)"
                $stoppedBy = $notStartedCode = 'NOT_STARTED_COMMIT_GUARD'
            }
        }
        if ($null -eq $stoppedBy) {
            if ([DateTimeOffset]::UtcNow -gt $StartDeadlineUtc) {
                Write-Host "::error title=Nightly G3::$($runner.Name) not started: past the start deadline $($StartDeadlineUtc.ToString('o'))"
                $stoppedBy = $notStartedCode = 'NOT_STARTED_DEADLINE'
            }
        }
        if ($null -eq $stoppedBy) {
            $free = Get-FreeGiB
            if ($free -lt $MinFreeGiB) {
                Write-Host "::error title=Nightly G3::$($runner.Name) not started: $free GiB free on $([IO.Path]::GetPathRoot($WorkRoot)), below $MinFreeGiB"
                $stoppedBy = $notStartedCode = 'NOT_STARTED_DISK'
            }
        }
        if ($null -ne $stoppedBy) {
            $verdicts.Add((Get-NightlyG3Verdict -Runner $runner.Name -EvidenceRoot '' -ExitCode $notStartedCode))
            continue
        }

        $stage = Join-Path $WorkRoot $runner.Stage
        $evidence = Join-Path $evidenceRoot $runner.Name
        $log = Join-Path $logsRoot "$($runner.Name).log"
        $arguments = @('-NoProfile', '-File', (Join-Path $RunnerRoot $runner.Script), '-StageRoot', $stage, '-EvidenceRoot', $evidence,
            '-SelfCheckControlServerCommit', $ControlServerCommit,
            # This round's floor, checked above, not the runner's own default of 30 (control-server#585): vm01's C:
            # is smaller than that, and the round already stops at its own floor before each runner.
            '-MinFreeGiB', $MinFreeGiB)
        if ($runner.Onboard) { $arguments += '-SelfCheckOnboardCommit', $OnboardCommit }
        if ($runner.Name -eq 'journey') { $arguments += '-BatchId', 'nightly-g3' }

        Write-Host "::group::$($runner.Name)"
        Write-Host "G3_NIGHTLY_RUNNER_START: $($runner.Name) at $(Get-CommittedGiB) GiB committed, $(Get-FreeGiB) GiB free"
        $clock = [Diagnostics.Stopwatch]::StartNew()
        & pwsh @arguments *>&1 | Tee-Object -FilePath $log | ForEach-Object { Write-Host $_ }
        $exitCode = $LASTEXITCODE
        $seconds = [Math]::Round($clock.Elapsed.TotalSeconds)
        Write-Host '::endgroup::'

        $verdict = Get-NightlyG3Verdict -Runner $runner.Name -EvidenceRoot $evidence -ExitCode $exitCode
        $verdicts.Add($verdict)
        $verdictAdded = $true
        $stageRemoved = if ($verdict.result -eq 'PASS') { Remove-Tree $stage } else {
            foreach ($bulk in 'sources', 'publish', 'peers') { $null = Remove-Tree (Join-Path $stage $bulk) }
            $false
        }
        $runs.Add([ordered]@{ runner = $runner.Name; exitCode = $exitCode; seconds = $seconds; stage = $stage; stageRemoved = $stageRemoved
                evidence = $evidence; log = $log })
        Write-Host "G3_NIGHTLY_RUNNER_DONE: $($runner.Name) $($verdict.result) in ${seconds}s$(if ($verdict.failed.Count) { ': ' + ($verdict.failed -join '; ') })$(if ($verdict.detail) { " ($($verdict.detail))" })"
      }
      catch {
        # Anything that throws while a runner is handled stops the round with its own message, and that runner says
        # ERROR -- never a night with fewer verdicts and no reason, which would read as green.
        $message = ($_.Exception.Message -replace '\s+', ' ').Trim()
        Write-Host "::error title=Nightly G3::$($runner.Name): $message"
        $stoppedBy = "ERROR_ROUND: $($runner.Name): $message"
        $notStartedCode = 'NOT_STARTED_ERROR'
        if (-not $verdictAdded) {
            $verdicts.Add([pscustomobject][ordered]@{ runner = $runner.Name; result = 'ERROR'; status = $null; failed = @(); detail = "ERROR_ROUND: $message" })
        }
      }
    }
}
finally {
    [ordered]@{
        stoppedBy = $stoppedBy
        startDeadlineUtc = $StartDeadlineUtc.ToUniversalTime().ToString('o')
        commits = $commits
        verdicts = @($verdicts)
        runs = @($runs)
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $WorkRoot 'results.json') -Encoding utf8NoBOM
}

$green = $null -eq $stoppedBy -and $verdicts.Count -eq $runners.Count -and @($verdicts | Where-Object { $_.result -ne 'PASS' }).Count -eq 0
exit $(if ($green) { 0 } else { 1 })
