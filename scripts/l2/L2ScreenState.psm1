#Requires -Version 7

<#
The onboard screen-state snapshot on the real-onboard rig, and the pauses that let a desktop cross-check read it
(control-server#653, for onboard-hmi#316's T3 acceptance).

Off unless Invoke-L2Scenario.ps1 is given -ScreenState. Off means nothing here runs: no setting is written into the
staged onboard appsettings.json, no directory is created, the journal's OnCheckpoint stays null, and assertions.json
carries no screenState identity. Test-L2ScreenState.ps1 holds that.

On, three things:

  1. The staged onboard gets screenState.enabled = true and screenState.directory = <evidence>/screen-state, so its
     hmi-state.json and hmi-state-yyyyMMdd.ndjson (onboard-hmi#315) land in the run's evidence.

  2. Pause points. A point is '<criterion>=<value>': the first time the journal records that criterion flipping to
     exactly that value, the run stops there. '@dialog=<title>' is the moment a dialog helper has found a dialog with
     that title and not yet answered it. At a pause the run writes screen-state-pauses/pause-NN.json and waits until
     pause-NN.release appears next to it, or the pause window runs out, whichever comes first, then carries on.
     Watch-L2ScreenStatePauses.ps1 is the other end: it runs the cross-check and writes the release file.

  3. screen-state-pauses/pauses.json at the end: every point, whether it was reached, how each pause ended, and how
     many 「IO模块离线」 lines the onboard's technical log wrote inside each pause and in the whole run.

What a pause can and cannot change, which is the ticket's failure definition:

  - No product timeout is touched. Nothing here writes a server setting or an onboard timeout; the product's own
    clocks keep running through a pause. That is also the risk: a pause at a step where a product deadline is
    running can let it expire. The plan's points (screen-state-crosscheck.psd1) are all steps where the product is
    waiting for the operator, after the sublot was entered, so the station departure deadline is no longer running.
  - No criterion changes. A pause happens inside the journal, after the flip is written and before the wait that
    observed it judges it; the points are values the wait accepts, so the wait returns the same value it would have.
    A point that is NOT a value the wait accepts would spend that wait's own budget, and the wait could time out --
    which is why the plan lists only accepting values, and why Test-L2ScreenState.ps1 checks each plan point's
    criterion is a literal in its scenario.
  - Never a red. A pause whose window runs out is a note and a line in pauses.json, not a failure; a point never
    reached likewise. Nothing in this module adds an assertion.
#>

Set-StrictMode -Version Latest

$script:PointPattern = '^(?<criterion>[^=]+)=(?<value>.*)$'

<#
Reads -ScreenState, -ScreenStatePauseAt and -ScreenStatePauseSeconds into a plan, or $null when the switch is off.
Throws on every combination that would otherwise be quietly ignored. Points come from -ScreenStatePauseAt when given,
else from the plan file's entry for the scenario, else none (snapshot only).
#>
function Resolve-L2ScreenStatePlan {
    param(
        [Parameter(Mandatory)][bool]$Enabled,
        [AllowNull()][AllowEmptyCollection()][string[]]$PauseAt,
        [Parameter(Mandatory)][int]$PauseSeconds,
        [Parameter(Mandatory)][string]$Scenario,
        [Parameter(Mandatory)][bool]$RealOnboard,
        [Parameter(Mandatory)][string]$PlanPath,
        [Parameter(Mandatory)][string]$EvidenceRoot
    )

    $explicit = $null -ne $PauseAt -and @($PauseAt).Count -gt 0
    if (-not $Enabled) {
        if ($explicit) { throw '-ScreenStatePauseAt needs -ScreenState: without the snapshot there is nothing to pause for.' }
        return $null
    }
    if (-not $RealOnboard) {
        throw "-ScreenState needs a scenario with Onboard = 'Real'; '$Scenario' runs the synthetic peer, which has no screen."
    }
    if ($PauseSeconds -lt 1 -or $PauseSeconds -gt 600) {
        throw "-ScreenStatePauseSeconds must be 1-600, not $PauseSeconds."
    }

    $source = 'none'
    $points = @()
    if ($explicit) {
        $source = 'parameter'
        # Semicolons separate points as well as array elements: `pwsh -File` cannot pass an array, so
        # `-ScreenStatePauseAt a=b,c=d` arrives as one point whose value is 'b,c=d' and is never reached.
        $points = @($PauseAt | ForEach-Object { [string]$_ -split ';' } | Where-Object { $_ -ne '' })
    } else {
        $plan = Import-PowerShellDataFile -LiteralPath $PlanPath
        if ($plan.Scenarios.ContainsKey($Scenario)) {
            $source = 'plan'
            $points = @($plan.Scenarios[$Scenario].PauseAt)
        }
    }
    $parsed = foreach ($point in $points) {
        $match = [regex]::Match([string]$point, $script:PointPattern)
        if (-not $match.Success -or [string]::IsNullOrWhiteSpace($match.Groups['criterion'].Value)) {
            throw "Screen-state pause point '$point' is not '<criterion>=<value>' or '@dialog=<title>'."
        }
        [pscustomobject]@{ Point = [string]$point; Criterion = $match.Groups['criterion'].Value; Value = $match.Groups['value'].Value }
    }
    $parsed = @($parsed)
    $duplicates = @($parsed | Group-Object Point | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) { throw "Screen-state pause point '$($duplicates[0].Name)' is listed twice." }

    return [pscustomobject]@{
        Points             = $parsed
        Source             = $source
        PauseSeconds       = $PauseSeconds
        # Both inside the evidence: the snapshot is what the cross-check reads, and the pause files are its record.
        SnapshotDirectory  = Join-Path $EvidenceRoot 'screen-state'
        PauseDirectory     = Join-Path $EvidenceRoot 'screen-state-pauses'
    }
}

<#
Turns the snapshot on in the staged onboard settings. With a $null plan it returns before touching anything, so the
staged appsettings.json is exactly what it was. An onboard commit from before onboard-hmi#315 has no screenState
section, and setting a property it does not know would be ignored by the onboard -- so that is a throw, not a guess.
#>
function Set-L2ScreenStateOnboardSetting {
    param([Parameter(Mandatory)][object]$Settings, [AllowNull()][object]$Plan)
    if ($null -eq $Plan) { return }
    if ($null -eq $Settings.PSObject.Properties['screenState'] -or $null -eq $Settings.screenState) {
        throw '-ScreenState, but the onboard appsettings.json has no screenState section: that onboard commit predates onboard-hmi#315.'
    }
    $Settings.screenState.enabled = $true
    $Settings.screenState | Add-Member -NotePropertyName directory -NotePropertyValue $Plan.SnapshotDirectory -Force
}

<#
What the journal's OnCheckpoint holds for this run. The state lives in a hashtable the closure captures; the work is
in the exported Invoke-L2ScreenStateCheckpoint, because a closure resolves command names globally and could not
reach an unexported function of this module (Test-L2ProbeClosureResolvable.ps1).
#>
function New-L2ScreenStateCheckpoint {
    param([Parameter(Mandatory)][object]$Plan, [Parameter(Mandatory)][object]$Journal, [int]$PollMilliseconds = 250)
    $null = New-Item -ItemType Directory -Path $Plan.SnapshotDirectory -Force
    $null = New-Item -ItemType Directory -Path $Plan.PauseDirectory -Force
    $state = @{
        Plan             = $Plan
        Journal          = $Journal
        PollMilliseconds = $PollMilliseconds
        Fired            = @{}
        Pauses           = [System.Collections.Generic.List[object]]::new()
    }
    $checkpoint = { param($Point, $Value) Invoke-L2ScreenStateCheckpoint -State $state -Criterion $Point -Value $Value }.GetNewClosure()
    return [pscustomobject]@{ State = $state; Checkpoint = $checkpoint }
}

function Invoke-L2ScreenStateCheckpoint {
    param([Parameter(Mandatory)][hashtable]$State, [Parameter(Mandatory)][string]$Criterion, [AllowEmptyString()][string]$Value)

    $point = @($State.Plan.Points | Where-Object { $_.Criterion -ceq $Criterion -and $_.Value -ceq $Value })
    if ($point.Count -eq 0 -or $State.Fired.ContainsKey($point[0].Point)) { return }
    $point = $point[0]
    $State.Fired[$point.Point] = $true

    $sequence = $State.Pauses.Count + 1
    $name = 'pause-{0:D2}' -f $sequence
    $marker = Join-Path $State.Plan.PauseDirectory "$name.json"
    $release = Join-Path $State.Plan.PauseDirectory "$name.release"
    $reachedAt = [DateTimeOffset]::UtcNow
    $deadline = $reachedAt.AddSeconds($State.Plan.PauseSeconds)
    $document = [ordered]@{
        sequence          = $sequence
        point             = $point.Point
        criterion         = $point.Criterion
        value             = $point.Value
        reachedAt         = $reachedAt.ToString('o')
        deadline          = $deadline.ToString('o')
        pauseSeconds      = $State.Plan.PauseSeconds
        snapshotDirectory = $State.Plan.SnapshotDirectory
        releaseFile       = $release
    }
    # Written aside and moved into place, so a watcher never reads half a marker.
    $partial = "$marker.partial"
    [IO.File]::WriteAllText($partial, ($document | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $partial -Destination $marker -Force
    $State.Journal.Note("Screen-state pause $sequence at '$($point.Point)': waiting up to $($State.Plan.PauseSeconds)s for $name.release.")

    while (-not (Test-Path -LiteralPath $release) -and [DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds $State.PollMilliseconds
    }
    $endedAt = [DateTimeOffset]::UtcNow
    $ending = if (Test-Path -LiteralPath $release) { 'released' } else { 'window-elapsed' }
    $seconds = [math]::Round(($endedAt - $reachedAt).TotalSeconds, 1)
    $State.Pauses.Add([ordered]@{
        sequence = $sequence; point = $point.Point; reachedAt = $reachedAt.ToString('o')
        endedAt = $endedAt.ToString('o'); seconds = $seconds; ending = $ending })
    $State.Journal.Note("Screen-state pause $sequence ended ($ending) after ${seconds}s; the run carries on.")
}

<#
When the onboard logged 「IO模块离线」 (OnboardController, one line per transition), read off its technical log
(agv-yyyyMMdd.log, tab-separated, ISO timestamp first). The reader the watcher starts runs a pwsh that compiles C#,
and the bench has seen the onboard's 1-second Modbus timeout fire while that happened (cs#653 coordinator comments):
an IO-offline inside a pause is the one way the cross-check can reach a scenario's judgement, through the safety
evaluation, so every pause carries its own count.
#>
function Get-L2OnboardIoOfflineTimes {
    param([AllowNull()][AllowEmptyString()][string]$LogDirectory)
    if (-not $LogDirectory -or -not (Test-Path -LiteralPath $LogDirectory -PathType Container)) { return @() }
    $times = foreach ($file in @(Get-ChildItem -LiteralPath $LogDirectory -Filter 'agv-*.log' -File)) {
        foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
            $fields = $line -split "`t"
            if ($fields.Count -lt 2 -or $fields[-1] -notlike 'IO模块离线*') { continue }
            $at = [DateTimeOffset]::MinValue
            if ([DateTimeOffset]::TryParse($fields[0], [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::RoundtripKind, [ref]$at)) { $at }
        }
    }
    return @($times)
}

<#
The end of a run with the snapshot on: pauses.json, and the flat identity assertions.json and SUMMARY.md carry. Never
throws -- it runs in the orchestrator's `finally`, and nothing about the snapshot may decide a run's outcome.
#>
function Complete-L2ScreenState {
    param([Parameter(Mandatory)][object]$Plan, [AllowNull()][object]$Checkpoint, [string]$OnboardLogDirectory)
    $identity = [ordered]@{ enabled = $true; directory = 'screen-state'; pointSource = $Plan.Source }
    try {
        # @() around the whole if: one pause coming out of an if-expression would be unrolled to the record itself,
        # and a record's Count is its number of fields.
        $pauses = @(if ($Checkpoint) { $Checkpoint.State.Pauses })
        $reached = @($pauses | ForEach-Object { $_.point })
        $unreached = @($Plan.Points | Where-Object { $_.Point -notin $reached } | ForEach-Object { $_.Point })
        $ioOffline = @(Get-L2OnboardIoOfflineTimes -LogDirectory $OnboardLogDirectory)
        $ioOfflineInPauses = 0
        foreach ($pause in $pauses) {
            $from = [DateTimeOffset]::Parse($pause.reachedAt); $to = [DateTimeOffset]::Parse($pause.endedAt)
            $pause['ioOfflineLines'] = @($ioOffline | Where-Object { $_ -ge $from -and $_ -le $to }).Count
            $ioOfflineInPauses += $pause['ioOfflineLines']
        }
        $pauseSeconds = 0.0
        foreach ($pause in $pauses) { $pauseSeconds += [double]$pause.seconds }
        $pauseSeconds = [math]::Round($pauseSeconds, 1)
        $current = Test-Path -LiteralPath (Join-Path $Plan.SnapshotDirectory 'hmi-state.json') -PathType Leaf
        $timelines = @(Get-ChildItem -LiteralPath $Plan.SnapshotDirectory -File -ErrorAction SilentlyContinue |
            Where-Object Name -Match '^hmi-state-\d{8}\.ndjson$' | ForEach-Object Name)
        $null = New-Item -ItemType Directory -Path $Plan.PauseDirectory -Force
        [IO.File]::WriteAllText((Join-Path $Plan.PauseDirectory 'pauses.json'), ([ordered]@{
                pauseSeconds  = $Plan.PauseSeconds
                pointSource   = $Plan.Source
                points        = @($Plan.Points | ForEach-Object { $_.Point })
                pauses        = $pauses
                unreached     = $unreached
                pauseSecondsTotal      = $pauseSeconds
                ioOfflineLinesInPauses = $ioOfflineInPauses
                ioOfflineLinesInRun    = $ioOffline.Count
                onboardLogDirectory    = $OnboardLogDirectory
                currentFile   = $current
                timelineFiles = $timelines
            } | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
        $identity['currentFile'] = if ($current) { 'present' } else { 'absent' }
        $identity['timelineFiles'] = $timelines.Count
        $identity['points'] = @($Plan.Points).Count
        $identity['pauses'] = "$($pauses.Count) ($(@($pauses | Where-Object { $_.ending -eq 'released' }).Count) released)"
        $identity['unreached'] = if ($unreached.Count -eq 0) { '(none)' } else { $unreached -join '; ' }
        $identity['pauseSecondsTotal'] = $pauseSeconds
        $identity['ioOffline'] = "$ioOfflineInPauses in pauses, $($ioOffline.Count) in the run"
    } catch {
        $identity['completionError'] = $_.Exception.Message
    }
    return $identity
}

Export-ModuleMember -Function Get-L2OnboardIoOfflineTimes, Resolve-L2ScreenStatePlan, Set-L2ScreenStateOnboardSetting, New-L2ScreenStateCheckpoint,
    Invoke-L2ScreenStateCheckpoint, Complete-L2ScreenState
