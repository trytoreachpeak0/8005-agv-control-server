#Requires -Version 7

<#
.SYNOPSIS
    Self-check: prove session-established-while-moving no longer races the server's processing of the
    "moving" report, by making the server slow on purpose rather than by counting green runs.

.DESCRIPTION
    control-server#141. The scenario used to report "moving" (SafetyStateChanged) and put a complete
    arrival into the fake RIoT on the very next line. PUT /control/v1/safety returns once the peer has
    sent the report, not once the server has stored it, so on a loaded CI machine the journey runtime
    could read the previous "stopped" state, see the arrival and trust it: L2-MV-08 expected
    AwaitingPickupArrival and read AwaitingSublot. It hit about one run in ten under four lanes, which
    no number of green runs can rule out.

    Set-L2OnboardSafety (L2.psm1) now returns only once the server's session row carries the published
    safetyStateVersion. This script widens the window deterministically and runs the real scenario
    against the real server twice:

    1. **The old ordering fails every time.** W2G_L2_SELFCHECK_HOLD_SERVER_WRITES_MS holds the server
       database's write lock across each safety report, so the server cannot commit it for that long;
       W2G_L2_SELFCHECK_SKIP_DURABLE_WAIT returns right after the PUT, as the scenario did before.
       L2-MV-08 must FAIL with AwaitingSublot. If it passes, the injection did not open the window and
       assertion 2 would prove nothing.
    2. **The new ordering passes under the same hold.** Same lock, same length; the step waits. Every
       assertion must PASS, and the arrival must have been put into the fake RIoT no earlier than the
       hold ended -- the timeline shows the step actually waited the hold out rather than finding the
       revision already there.

.PARAMETER HoldMilliseconds
    How long the server is kept from committing each report. The journey runtime polls every second
    under L2, so the default leaves it several chances to decide inside the window.

.PARAMETER PortSlot
    Passed through to Invoke-L2Scenario.ps1. Slot 0 by default, like a hand-run scenario.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/l2/Test-L2SafetyDurableWait.ps1 -EvidenceRoot ../../evidence/l2/<new dir>

.NOTES
    Builds once (the first run), then runs with -SkipBuild. About two minutes. Takes the L2 port lock
    through the orchestrator like any other run. Exit code is the contract: 0 all passed, 1 something
    failed. The two runs' evidence stays under -EvidenceRoot, red one included.
#>
[CmdletBinding()]
param(
    # Must not exist; the two runs go under it as old-ordering/ and new-ordering/.
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,

    [ValidateRange(1000, 20000)]
    [int]$HoldMilliseconds = 4000,

    [ValidateRange(0, 4)]
    [int]$PortSlot = 0,

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scenario = 'session-established-while-moving'
$orchestrator = Join-Path $PSScriptRoot 'Invoke-L2Scenario.ps1'
if (Test-Path -LiteralPath $EvidenceRoot) { throw "EvidenceRoot must not exist: $EvidenceRoot" }
$null = New-Item -ItemType Directory -Path $EvidenceRoot
$EvidenceRoot = (Resolve-Path -LiteralPath $EvidenceRoot).Path

$failures = @()
function Assert-Case {
    param([Parameter(Mandatory)][string]$Name,
          [Parameter(Mandatory)][bool]$Condition,
          [Parameter(Mandatory)][string]$Detail)

    if ($Condition) {
        Write-Host "PASS  $Name -- $Detail"
    } else {
        Write-Host "FAIL  $Name -- $Detail"
        $script:failures += $Name
    }
}

function Invoke-Run {
    param([Parameter(Mandatory)][string]$Name,
          [Parameter(Mandatory)][bool]$SkipDurableWait,
          [Parameter(Mandatory)][bool]$Build)

    $root = Join-Path $EvidenceRoot $Name
    $arguments = @('-NoProfile', '-NonInteractive', '-File', $orchestrator,
        '-Scenario', $scenario, '-EvidenceRoot', $root, '-PortSlot', [string]$PortSlot)
    if (-not $Build) { $arguments += '-SkipBuild' }

    # The child inherits these; nothing else on the machine reads them.
    $env:W2G_L2_SELFCHECK_HOLD_SERVER_WRITES_MS = [string]$HoldMilliseconds
    $env:W2G_L2_SELFCHECK_SKIP_DURABLE_WAIT = if ($SkipDurableWait) { '1' } else { $null }
    try {
        Write-Host "--- $Name ($(if ($SkipDurableWait) { 'no durable wait' } else { 'durable wait' }), hold $HoldMilliseconds ms)"
        & pwsh @arguments *>&1 | Out-File -LiteralPath "$root.log" -Encoding utf8
        $exitCode = $LASTEXITCODE
    } finally {
        Remove-Item Env:W2G_L2_SELFCHECK_HOLD_SERVER_WRITES_MS -ErrorAction SilentlyContinue
        Remove-Item Env:W2G_L2_SELFCHECK_SKIP_DURABLE_WAIT -ErrorAction SilentlyContinue
    }

    $assertionsPath = Join-Path $root 'assertions.json'
    if (-not (Test-Path -LiteralPath $assertionsPath)) {
        throw "Run $Name wrote no assertions.json (exit $exitCode); see $root.log"
    }
    $timeline = @(Get-Content -LiteralPath (Join-Path $root 'timeline.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    return [pscustomobject]@{
        ExitCode   = $exitCode
        Assertions = (Get-Content -LiteralPath $assertionsPath -Raw | ConvertFrom-Json)
        Timeline   = $timeline
    }
}

function Get-Note {
    param([Parameter(Mandatory)][object[]]$Timeline, [Parameter(Mandatory)][string]$Prefix, [int]$After = -1)
    for ($i = $After + 1; $i -lt $Timeline.Count; $i++) {
        $line = $Timeline[$i]
        if ($line.PSObject.Properties['note'] -and ([string]$line.note).StartsWith($Prefix)) { return $i }
    }
    return -1
}

function Get-Criterion {
    param([object[]]$Assertions, [Parameter(Mandatory)][string]$Id)
    $found = @($Assertions | Where-Object { $_.id -eq $Id })
    # A run that died before reaching the criterion records none; say so rather than index past the end.
    if ($found.Count -eq 0) { return [pscustomobject]@{ outcome = '(not recorded)'; expected = ''; actual = '' } }
    return $found[0]
}

# --- 1. old ordering ------------------------------------------------------------------------------

$old = Invoke-Run -Name 'old-ordering' -SkipDurableWait $true -Build (-not $SkipBuild)
$departs = Get-Note $old.Timeline 'Vehicle departs for the pickup station'
$held = Get-Note $old.Timeline 'Self-check: holding' -After $departs
Assert-Case 'old ordering: the hold was in force across the "moving" report' ($departs -ge 0 -and $held -gt $departs) `
    "departs note at line $departs, hold note at line $held"
$mv08 = Get-Criterion $old.Assertions.assertions 'L2-MV-08'
Assert-Case 'old ordering: the server trusted the arrival (the CI failure, reproduced)' `
    ($mv08.outcome -eq 'FAIL' -and $mv08.actual -eq 'AwaitingSublot') `
    "L2-MV-08 $($mv08.outcome): expected $($mv08.expected), actual $($mv08.actual)"

# --- 2. new ordering ------------------------------------------------------------------------------

$new = Invoke-Run -Name 'new-ordering' -SkipDurableWait $false -Build $false
$notPassed = @($new.Assertions.assertions | Where-Object { $_.outcome -ne 'PASS' } | ForEach-Object { "$($_.id) $($_.outcome)" })
Assert-Case 'new ordering: every assertion passes under the same hold' `
    ($new.Assertions.outcome -eq 'PASS' -and $notPassed.Count -eq 0 -and $new.ExitCode -eq 0) `
    "outcome $($new.Assertions.outcome), exit $($new.ExitCode)$(if ($notPassed) { ', not passed: ' + ($notPassed -join ', ') })"

$departs = Get-Note $new.Timeline 'Vehicle departs for the pickup station'
$held = Get-Note $new.Timeline 'Self-check: holding' -After $departs
$arrival = Get-Note $new.Timeline 'RIoT reports a complete arrival' -After $departs
if ($departs -lt 0 -or $held -lt 0 -or $arrival -lt 0) {
    Assert-Case 'new ordering: the arrival was put in only after the hold ended' $false `
        "timeline lines missing: departs $departs, hold $held, arrival $arrival"
} else {
    $waited = ([DateTimeOffset]$new.Timeline[$arrival].at - [DateTimeOffset]$new.Timeline[$held].at).TotalMilliseconds
    Assert-Case 'new ordering: the arrival was put in only after the hold ended' `
        ($waited -ge $HoldMilliseconds - 50) `
        "arrival $([math]::Round($waited)) ms after the hold began (hold $HoldMilliseconds ms)"
}

Write-Host "Evidence: $EvidenceRoot"
if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed."
    exit 1
}
Write-Host 'All checks passed.'
exit 0
