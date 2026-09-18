#Requires -Version 7

<#
Parallel lanes for the synthetic L2 scenarios (control-server#130).

Until port slots existed every L2 run on a machine bound one block of ports under one lock, so CI ran its
scenarios one after another: 19 minutes for 29 runs on 2026-09-17, most of it each run standing up its own
server and doubles and then waiting out business timeouts, with 19 of win11-01's 20 vCPUs idle. Now each
lane takes a port slot of its own (L2PortLock.psm1) and runs its scenarios one after another in it, and the
lanes run side by side.

Three rules the plan keeps, each for a reason:

- **Every run of one scenario stays in one lane, back to back.** Three consecutive passes is a claim about
  one scenario running three times in a row; three copies at the same moment in three lanes would prove
  nothing about consecutiveness.
- **Lanes take slots 1..N, never 0.** Slot 0 is the block and lock the real-onboard rig, run-journey-g3.ps1
  and every checkout older than the slots take. A CI lane there would queue against them for nothing.
- **Longest first onto the least loaded lane** (LPT), by each scenario's estimate times its runs, so the lanes
  finish close together; the job lasts as long as its slowest lane.

The lanes share one build output. The caller builds once before Invoke-L2LanePlan and every run gets
-SkipBuild, because a build under a running lane overwrites the executables it runs.

Run in parallel, a run's console output would interleave with every other lane's, so each run writes its own
next to its evidence (<evidence>.log) and the caller prints them afterwards; while the lanes run, one line per
run start and end says where each lane is.
#>

Set-StrictMode -Version Latest

$script:DefaultEstimateSeconds = 30

function Get-L2LanePlan {
    <#
    .SYNOPSIS
        Deals scenarios out to at most LaneCount lanes, longest first onto the least loaded lane.

    .PARAMETER Items
        One object per scenario: Name, Runs, and optionally EstimateSeconds (one run's) and BatchId. Items pass
        through unchanged into the lane that gets them.

    .OUTPUTS
        One object per lane that got anything: Lane, Slot, Items (in the order they run), EstimateSeconds.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Items,
        # Four: the slots besides slot 0 (L2PortLock.psm1).
        [Parameter(Mandatory)][ValidateRange(1, 4)][int]$LaneCount
    )

    $weighted = @($Items | ForEach-Object {
        $estimate = if ($_.PSObject.Properties['EstimateSeconds'] -and $null -ne $_.EstimateSeconds) {
            [double]$_.EstimateSeconds
        } else {
            $script:DefaultEstimateSeconds
        }
        [pscustomobject]@{ Item = $_; Total = $estimate * [int]$_.Runs }
    } | Sort-Object -Property @{ Expression = 'Total'; Descending = $true }, @{ Expression = { $_.Item.Name } })

    $lanes = @(1..$LaneCount | ForEach-Object {
        [pscustomobject]@{ Lane = $_; Slot = $_; Items = [Collections.Generic.List[object]]::new(); EstimateSeconds = 0 }
    })
    foreach ($entry in $weighted) {
        # The least loaded lane, the lowest-numbered of equals, so a plan is the same every time.
        $lane = $lanes | Sort-Object -Property EstimateSeconds, Lane | Select-Object -First 1
        $lane.Items.Add($entry.Item)
        $lane.EstimateSeconds += $entry.Total
    }
    return @($lanes | Where-Object { $_.Items.Count -gt 0 } | ForEach-Object {
        [pscustomobject]@{ Lane = $_.Lane; Slot = $_.Slot; Items = @($_.Items); EstimateSeconds = $_.EstimateSeconds }
    })
}

function Test-L2PullRequestSuperseded {
    <#
    .SYNOPSIS
        True when this is a pull request run whose head is no longer the commit it was started for.

    .DESCRIPTION
        Read from the environment l2.yml sets. Anything short of a clear answer -- the API unreachable through
        the proxy, a malformed reply -- keeps the run going: a skipped run must never be a guess. One failed
        read turns the check off for every lane (State.CheckEnabled), so an unreachable API costs one timeout
        rather than one per scenario; one lane seeing the head move stops every lane (State.Superseded).
        Outside a pull request it is always false, which is what a hand run on a workstation gets.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory)][hashtable]$State)

    if ($State.Superseded) { return $true }
    if ($env:GITHUB_EVENT_NAME -ne 'pull_request' -or -not $env:L2_PR_NUMBER -or -not $State.CheckEnabled) { return $false }
    try {
        $pull = Invoke-RestMethod -TimeoutSec 45 `
            -Uri "$env:GITHUB_API_URL/repos/$env:GITHUB_REPOSITORY/pulls/$env:L2_PR_NUMBER" `
            -Headers @{ Authorization = "Bearer $env:GITHUB_TOKEN"; Accept = 'application/vnd.github+json' }
    } catch {
        Write-Host "Could not read the pull request's current head; running every scenario without checking again. $($_.Exception.Message)"
        $State.CheckEnabled = $false
        return $false
    }
    $current = [string]$pull.head.sha
    if ($current -match '^[0-9a-f]{40}$' -and $current -ne $env:L2_HEAD_SHA) {
        if (-not $State.Superseded) {
            $State.Superseded = $true
            Write-Host "::notice title=L2 superseded::Pull request #$env:L2_PR_NUMBER head moved from $env:L2_HEAD_SHA to $current; skipping the remaining scenarios. The newer push has its own run."
        }
        return $true
    }
    return $false
}

function Invoke-L2LanePlan {
    <#
    .SYNOPSIS
        Runs a plan from Get-L2LanePlan, its lanes side by side, and returns one record per run.

    .DESCRIPTION
        Within a lane, scenarios run in plan order and each scenario's runs back to back. A red run stops that
        scenario's remaining runs -- the consecutive criterion cannot be met any more -- and the lane moves on to
        its next scenario. Before every run the lane checks whether the pull request was superseded; once it
        was, every lane stops.

        The build is the caller's: every run gets -SkipBuild.

    .OUTPUTS
        Per run, lane by lane and in the order each lane ran them: Lane, Slot, Scenario, Run, Runs, Label,
        ExitCode, Seconds, Evidence, Log. Whether the pull request was superseded is $State.Superseded.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Plan,
        # Created if absent. Each run's evidence goes to <EvidenceRoot>/<scenario>-<run>, as it always has.
        [Parameter(Mandatory)][string]$EvidenceRoot,
        [Parameter(Mandatory)][string]$Orchestrator,
        # Shared by every lane; see Test-L2PullRequestSuperseded. Pass a synchronized hashtable.
        [Parameter(Mandatory)][hashtable]$State
    )

    $null = New-Item -ItemType Directory -Path $EvidenceRoot -Force
    $modulePath = $PSCommandPath
    $results = $Plan | ForEach-Object -ThrottleLimit ([Math]::Max(1, $Plan.Count)) -Parallel {
        $lane = $_
        $state = $using:State
        Import-Module $using:modulePath -Force
        $records = [Collections.Generic.List[object]]::new()
        :lane foreach ($item in $lane.Items) {
            $runs = [int]$item.Runs
            for ($run = 1; $run -le $runs; $run++) {
                if (Test-L2PullRequestSuperseded -State $state) { break lane }
                $label = if ($runs -eq 1) { $item.Name } else { "$($item.Name) ($run/$runs)" }
                # -EvidenceRoot must not exist; the run number keeps a scenario's consecutive runs apart.
                $evidence = Join-Path $using:EvidenceRoot "$($item.Name)-$('{0:d2}' -f $run)"
                $log = "$evidence.log"
                $batch = if ($item.PSObject.Properties['BatchId'] -and $item.BatchId) { $item.BatchId } else { 'batch-2' }
                Write-Host "L2 lane $($lane.Lane) (slot $($lane.Slot)) start: $label"
                $started = [Diagnostics.Stopwatch]::StartNew()
                & pwsh -NoProfile -File $using:Orchestrator -Scenario $item.Name -EvidenceRoot $evidence `
                    -BatchId $batch -PortSlot $lane.Slot -SkipBuild *>&1 |
                    Out-File -LiteralPath $log -Encoding utf8
                $code = $LASTEXITCODE
                $seconds = [Math]::Round($started.Elapsed.TotalSeconds)
                Write-Host ("L2 lane $($lane.Lane) (slot $($lane.Slot)) end: $label -> " +
                    "$(if ($code -eq 0) { 'PASS' } else { "FAIL ($code)" }) in ${seconds}s")
                $records.Add([pscustomobject]@{
                    Lane = $lane.Lane; Slot = $lane.Slot; Scenario = $item.Name; Run = $run; Runs = $runs
                    ExitCode = $code; Seconds = $seconds; Evidence = $evidence; Log = $log; Label = $label
                })
                # Three consecutive passes is the criterion, so once one run is red the rest of this
                # scenario's runs cannot establish it and only cost minutes. The lane's other scenarios go on.
                if ($code -ne 0) { break }
            }
        }
        $records
    }
    # A lane hands back its records all at once, in the order it ran them; lanes finish in any order.
    return @($results | Sort-Object -Property Lane -Stable)
}

Export-ModuleMember -Function Get-L2LanePlan, Test-L2PullRequestSuperseded, Invoke-L2LanePlan
