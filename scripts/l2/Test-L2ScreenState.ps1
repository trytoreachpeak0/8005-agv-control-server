#Requires -Version 7

<#
.SYNOPSIS
    Self-check for the real-onboard rig's opt-in screen-state snapshot and pause points (L2ScreenState.psm1,
    control-server#653).

.DESCRIPTION
    No rig, no window, a few seconds of deliberate pauses. What it holds is the ticket's failure definition:

      - off changes nothing: the staged onboard settings come out byte for byte what they were, the journal writes the
        same lines and calls nothing, the dialog checkpoint is inert, and the orchestrator only reaches the snapshot
        code through the plan it resolves to $null;
      - on writes screenState.enabled and the evidence directory, and refuses an onboard without the section;
      - a pause holds the run until its release file or its window, writes a marker and a record, and never throws;
      - a wait whose accepting value is a pause point returns that value even when the pause outlasts the wait's own
        timeout -- the pause does not turn a green criterion red;
      - the plan's scenarios are real-onboard scenarios and every point's criterion or dialog title is a literal in
        that scenario or the helpers it uses, so a renamed criterion does not leave a point that is never reached;
      - the watcher releases a pause after its rounds and stops when the run writes assertions.json.

    No real-rig round reaches any of this with the switch off, and with it on a point that never fires looks like a
    run that simply had no pause. That is why it is checked here.

    Exits 1 when any case comes out the other way, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-L2ScreenState.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'L2ScreenState.psm1') -Force

$scratch = Join-Path ([IO.Path]::GetTempPath()) "l2-screen-state-selftest-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $scratch -Force
$planPath = Join-Path $PSScriptRoot 'screen-state-crosscheck.psd1'

# The shipped onboard appsettings.json's screenState section and a neighbour, as the stage step reads them (ConvertFrom-Json).
$shippedSettings = @'
{
  "agvId": "AGV-01",
  "screenState": { "enabled": false, "flushIntervalMs": 500, "heartbeatIntervalMs": 5000, "timelineMaxFileBytes": 20971520, "timelineRetentionDays": 30 },
  "vehicleSafety": { "enabled": false }
}
'@

function New-Plan([string[]]$PauseAt, [int]$PauseSeconds = 2, [string]$Root = (Join-Path $scratch ([guid]::NewGuid().ToString('N')))) {
    $plan = Resolve-L2ScreenStatePlan -Enabled $true -PauseAt $PauseAt -PauseSeconds $PauseSeconds -Scenario 'x' `
        -RealOnboard $true -PlanPath $planPath -EvidenceRoot $Root
    $null = New-Item -ItemType Directory -Path $Root -Force
    return $plan
}
function Get-Throw([scriptblock]$Block) { try { & $Block; return $null } catch { return $_.Exception.Message } }
function Read-Lines([string]$Path) { return @(Get-Content -LiteralPath $Path | ForEach-Object { $_ -replace '"at":"[^"]+",?', '' }) }

$cases = @()

$cases += @{
    Name  = 'off: no plan, and a pause point without the switch is refused'
    Check = {
        $plan = Resolve-L2ScreenStatePlan -Enabled $false -PauseAt $null -PauseSeconds 120 -Scenario 'real-onboard-normal-load' `
            -RealOnboard $true -PlanPath $planPath -EvidenceRoot $scratch
        $refused = Get-Throw { Resolve-L2ScreenStatePlan -Enabled $false -PauseAt @('a=b') -PauseSeconds 120 -Scenario 'x' `
                -RealOnboard $true -PlanPath $planPath -EvidenceRoot $scratch }
        @{ Ok = ($null -eq $plan -and $refused -like '*needs -ScreenState*'); Actual = "plan=$plan; refused=$refused" }
    }
}

$cases += @{
    Name  = 'off: the staged onboard settings come out byte for byte unchanged'
    Check = {
        $before = $shippedSettings | ConvertFrom-Json | ConvertTo-Json -Depth 16
        $settings = $shippedSettings | ConvertFrom-Json
        Set-L2ScreenStateOnboardSetting -Settings $settings -Plan $null
        $after = $settings | ConvertTo-Json -Depth 16
        @{ Ok = ($before -ceq $after); Actual = $(if ($before -ceq $after) { 'identical' } else { $after }) }
    }
}

$cases += @{
    Name  = 'off: the journal writes the same lines as with no hook and calls nothing; the dialog checkpoint is inert'
    Check = {
        $journal = New-L2Journal -Path (Join-Path $scratch 'off-timeline.jsonl')
        $journal.Observe('c', 'a', $null); $journal.Observe('c', 'a', $null); $journal.Observe('c', 'b', $null)
        $journal.Note('n')
        Invoke-L2JournalCheckpoint -Journal $journal -Point '@dialog' -Value '补偿清空'
        Invoke-L2JournalCheckpoint -Journal ([pscustomobject]@{}) -Point '@dialog' -Value 'stand-in without Checkpoint'
        Invoke-L2JournalCheckpoint -Journal $null -Point '@dialog' -Value 'no journal'
        $lines = Read-Lines $journal.Path
        $expected = @('{"criterion":"c","value":"a"}', '{"criterion":"c","value":"b"}', '{"note":"n"}')
        @{ Ok = ($null -eq $journal.OnCheckpoint -and ($lines -join '|') -ceq ($expected -join '|'))
           Actual = $lines -join ' | ' }
    }
}

$cases += @{
    Name  = 'on: needs a real-onboard scenario, a sane window and well-formed points'
    Check = {
        $synthetic = Get-Throw { Resolve-L2ScreenStatePlan -Enabled $true -PauseAt $null -PauseSeconds 120 -Scenario 'x' `
                -RealOnboard $false -PlanPath $planPath -EvidenceRoot $scratch }
        $window = Get-Throw { New-Plan -PauseAt @('a=b') -PauseSeconds 0 }
        $malformed = Get-Throw { New-Plan -PauseAt @('no-equals-sign') }
        $twice = Get-Throw { New-Plan -PauseAt @('a=b', 'a=b') }
        @{ Ok = ($synthetic -like "*Onboard = 'Real'*" -and $window -like '*1-600*' -and $malformed -like '*is not*' -and $twice -like '*twice*')
           Actual = "$synthetic / $window / $malformed / $twice" }
    }
}

$cases += @{
    Name  = 'on: points come from the parameter, else the plan file, else none'
    Check = {
        $fromParameter = Resolve-L2ScreenStatePlan -Enabled $true -PauseAt @('k=v') -PauseSeconds 5 `
            -Scenario 'real-onboard-compensate-then-reconnect' -RealOnboard $true -PlanPath $planPath -EvidenceRoot $scratch
        $fromPlan = Resolve-L2ScreenStatePlan -Enabled $true -PauseAt $null -PauseSeconds 5 `
            -Scenario 'real-onboard-compensate-then-reconnect' -RealOnboard $true -PlanPath $planPath -EvidenceRoot $scratch
        $none = Resolve-L2ScreenStatePlan -Enabled $true -PauseAt $null -PauseSeconds 5 `
            -Scenario 'real-onboard-normal-load' -RealOnboard $true -PlanPath $planPath -EvidenceRoot $scratch
        $dialog = @($fromPlan.Points | Where-Object Criterion -eq '@dialog')
        @{ Ok = ($fromParameter.Source -eq 'parameter' -and @($fromParameter.Points).Count -eq 1 -and
                 $fromPlan.Source -eq 'plan' -and @($fromPlan.Points).Count -eq 3 -and $dialog.Count -eq 1 -and $dialog[0].Value -eq '补偿清空' -and
                 $none.Source -eq 'none' -and @($none.Points).Count -eq 0)
           Actual = "$($fromParameter.Source)/$(@($fromParameter.Points).Count) $($fromPlan.Source)/$(@($fromPlan.Points).Count) $($none.Source)/$(@($none.Points).Count)" }
    }
}

$cases += @{
    Name  = 'on: the staged settings turn the snapshot on into the evidence; an onboard without the section is refused'
    Check = {
        $plan = New-Plan -PauseAt @()
        $settings = $shippedSettings | ConvertFrom-Json
        Set-L2ScreenStateOnboardSetting -Settings $settings -Plan $plan
        $old = '{ "agvId": "AGV-01", "vehicleSafety": { "enabled": false } }' | ConvertFrom-Json
        $refused = Get-Throw { Set-L2ScreenStateOnboardSetting -Settings $old -Plan $plan }
        @{ Ok = ($settings.screenState.enabled -eq $true -and $settings.screenState.directory -eq $plan.SnapshotDirectory -and
                 $settings.screenState.heartbeatIntervalMs -eq 5000 -and $refused -like '*predates onboard-hmi#315*')
           Actual = "enabled=$($settings.screenState.enabled) directory=$($settings.screenState.directory) refused=$refused" }
    }
}

$cases += @{
    Name  = 'a pause holds until its release file, writes its marker and record, and fires once'
    Check = {
        $plan = New-Plan -PauseAt @('stage=Blocked') -PauseSeconds 30
        $journal = New-L2Journal -Path (Join-Path $scratch 'release-timeline.jsonl')
        $checkpoint = New-L2ScreenStateCheckpoint -Plan $plan -Journal $journal -PollMilliseconds 50
        $journal.OnCheckpoint = $checkpoint.Checkpoint
        $release = Join-Path $plan.PauseDirectory 'pause-01.release'
        $releaser = Start-ThreadJob -ArgumentList $release { param($Path) Start-Sleep -Milliseconds 1500; Set-Content -LiteralPath $Path -Value 'go' }
        $started = [DateTimeOffset]::UtcNow
        $journal.Observe('stage', 'Moving', $null)
        $journal.Observe('stage', 'Blocked', $null)
        $held = ([DateTimeOffset]::UtcNow - $started).TotalSeconds
        $null = $releaser | Wait-Job | Receive-Job; $releaser | Remove-Job
        $journal.Observe('stage', 'Moving', $null); $journal.Observe('stage', 'Blocked', $null)
        $marker = Get-Content -LiteralPath (Join-Path $plan.PauseDirectory 'pause-01.json') -Raw | ConvertFrom-Json
        $pauses = @($checkpoint.State.Pauses)
        @{ Ok = ($held -ge 1.2 -and $held -lt 10 -and $marker.point -eq 'stage=Blocked' -and $marker.snapshotDirectory -eq $plan.SnapshotDirectory -and
                 $pauses.Count -eq 1 -and $pauses[0].ending -eq 'released' -and
                 -not (Test-Path -LiteralPath (Join-Path $plan.PauseDirectory 'pause-02.json')))
           Actual = "held ${held}s, $($pauses.Count) pause(s), ending $($pauses[0].ending)" }
    }
}

$cases += @{
    Name  = 'a pause nobody releases ends with its window, as a record and not a failure'
    Check = {
        $plan = New-Plan -PauseAt @('@dialog=补偿清空') -PauseSeconds 1
        $journal = New-L2Journal -Path (Join-Path $scratch 'elapsed-timeline.jsonl')
        $checkpoint = New-L2ScreenStateCheckpoint -Plan $plan -Journal $journal -PollMilliseconds 50
        $journal.OnCheckpoint = $checkpoint.Checkpoint
        Invoke-L2JournalCheckpoint -Journal $journal -Point '@dialog' -Value '申请恢复原操作'
        Invoke-L2JournalCheckpoint -Journal $journal -Point '@dialog' -Value '补偿清空'
        $pauses = @($checkpoint.State.Pauses)
        $identity = Complete-L2ScreenState -Plan $plan -Checkpoint $checkpoint
        $recorded = Get-Content -LiteralPath (Join-Path $plan.PauseDirectory 'pauses.json') -Raw | ConvertFrom-Json
        @{ Ok = ($pauses.Count -eq 1 -and $pauses[0].ending -eq 'window-elapsed' -and $identity.unreached -eq '(none)' -and
                 $identity.pauses -eq '1 (0 released)' -and
                 $identity.currentFile -eq 'absent' -and @($recorded.pauses).Count -eq 1 -and -not $identity.Contains('completionError'))
           Actual = "$($pauses.Count) pause(s) $($pauses[0].ending); identity pauses=$($identity.pauses) currentFile=$($identity.currentFile)" }
    }
}

$cases += @{
    Name  = 'a wait whose accepting value is a pause point returns it even when the pause outlasts the wait'
    Check = {
        $plan = New-Plan -PauseAt @('entry=True') -PauseSeconds 3
        $journal = New-L2Journal -Path (Join-Path $scratch 'wait-timeline.jsonl')
        $checkpoint = New-L2ScreenStateCheckpoint -Plan $plan -Journal $journal -PollMilliseconds 50
        $journal.OnCheckpoint = $checkpoint.Checkpoint
        $value = Wait-L2Condition -Description 'entry offered' -Journal $journal -Criterion 'entry' -TimeoutSeconds 1 `
            -Probe { $true } -Until { param($v) $v }
        $completion = Complete-L2ScreenState -Plan $plan -Checkpoint $checkpoint
        @{ Ok = ($value -eq $true -and @($checkpoint.State.Pauses).Count -eq 1 -and $completion.unreached -eq '(none)')
           Actual = "returned $value after a $(@($checkpoint.State.Pauses)[0].seconds)s pause on a 1s wait" }
    }
}

$cases += @{
    Name  = 'a point never reached is listed as such, and the run identity says so'
    Check = {
        $plan = New-Plan -PauseAt @('never=True', 'stage=Blocked') -PauseSeconds 1
        $checkpoint = New-L2ScreenStateCheckpoint -Plan $plan -Journal (New-L2Journal -Path (Join-Path $scratch 'unreached.jsonl'))
        $null = New-Item -ItemType File -Path (Join-Path $plan.SnapshotDirectory 'hmi-state.json') -Force
        $null = New-Item -ItemType File -Path (Join-Path $plan.SnapshotDirectory 'hmi-state-20261012.ndjson') -Force
        $identity = Complete-L2ScreenState -Plan $plan -Checkpoint $checkpoint
        @{ Ok = ($identity.unreached -eq 'never=True; stage=Blocked' -and $identity.currentFile -eq 'present' -and $identity.timelineFiles -eq 1 -and
                 $identity.pauses -eq '0 (0 released)' -and -not $identity.Contains('completionError'))
           Actual = "unreached=$($identity.unreached) currentFile=$($identity.currentFile) timelineFiles=$($identity.timelineFiles) pauses=$($identity.pauses) error=$($identity['completionError'])" }
    }
}

$cases += @{
    Name  = "the plan's scenarios are real-onboard ones and every point is a literal where the scenario can reach it"
    Check = {
        $plan = Import-PowerShellDataFile -LiteralPath $planPath
        $scenarioRoot = Join-Path $PSScriptRoot 'scenarios'
        # The two dialog helpers (Invoke-L2RealDialogButton, Invoke-G3DialogButton): each must call the checkpoint itself,
        # since a scenario reaches only one of them.
        $problems = @(foreach ($helper in @((Join-Path $PSScriptRoot 'L2RealOnboard.psm1'), (Join-Path $scenarioRoot 'G3RecoveryCommon.ps1'))) {
            if (-not (Get-Content -Raw -LiteralPath $helper).Contains("Invoke-L2JournalCheckpoint `$Journal '@dialog' `$Title")) {
                "$(Split-Path -Leaf $helper) lost its dialog checkpoint"
            }
        })
        $problems += @(foreach ($scenario in $plan.Scenarios.Keys) {
            $setupPath = Join-Path $scenarioRoot "$scenario.setup.psd1"
            if (-not (Test-Path -LiteralPath $setupPath)) { "$scenario has no setup file"; continue }
            if ((Import-PowerShellDataFile -LiteralPath $setupPath).Onboard -ne 'Real') { "$scenario is not a real-onboard scenario" }
            $body = Get-Content -Raw -LiteralPath (Join-Path $scenarioRoot "$scenario.ps1")
            foreach ($point in @($plan.Scenarios[$scenario].PauseAt)) {
                $criterion, $value = ([string]$point -split '=', 2)
                if ($criterion -eq '@dialog') {
                    if (-not $body.Contains("'$value'")) { "$scenario never names the dialog '$value'" }
                } elseif (-not $body.Contains("'$criterion'")) {
                    "$scenario has no criterion '$criterion'"
                }
            }
        })
        @{ Ok = ($problems.Count -eq 0); Actual = $(if ($problems.Count -eq 0) { "$($plan.Scenarios.Count) scenarios and 2 dialog helpers checked" } else { $problems -join '; ' }) }
    }
}

$cases += @{
    Name  = 'the orchestrator only reaches the snapshot through the plan, and the dialog helpers only through the checkpoint'
    Check = {
        $orchestrator = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Invoke-L2Scenario.ps1')
        $calls = [regex]::Matches($orchestrator, '(?m)^(?!\s*#).*\b(Set-L2ScreenStateOnboardSetting|New-L2ScreenStateCheckpoint|Complete-L2ScreenState)\b.*$') |
            ForEach-Object { $_.Value.Trim() }
        $guarded = @($calls | Where-Object { $_ -match 'Set-L2ScreenStateOnboardSetting -Settings \$settings -Plan \$screenStatePlan' -or
                $_ -match '^\$screenStateCheckpoint = New-L2ScreenStateCheckpoint' -or $_ -match '^if \(\$screenStatePlan\) \{ \$identity' })
        $checkpointBlock = [regex]::Match($orchestrator, '(?s)if \(\$screenStatePlan\) \{\s*\$screenStatePlan\.SnapshotDirectory.*?\$journal\.OnCheckpoint = ').Success
        @{ Ok = (@($calls).Count -eq 3 -and $guarded.Count -eq 3 -and $checkpointBlock)
           Actual = "$(@($calls).Count) call(s), $($guarded.Count) as expected, checkpoint inside the plan guard: $checkpointBlock" }
    }
}

$cases += @{
    Name  = 'the watcher runs its rounds, releases the pause and stops when the run writes assertions.json'
    Check = {
        $root = Join-Path $scratch 'watched-run'
        $plan = New-Plan -PauseAt @('stage=Blocked') -PauseSeconds 30 -Root $root
        $journal = New-L2Journal -Path (Join-Path $root 'timeline.jsonl')
        $checkpoint = New-L2ScreenStateCheckpoint -Plan $plan -Journal $journal -PollMilliseconds 50
        $journal.OnCheckpoint = $checkpoint.Checkpoint
        $watcher = Start-ThreadJob -ArgumentList (Join-Path $PSScriptRoot 'Watch-L2ScreenStatePauses.ps1'), $root {
            param($Script, $Root)
            & $Script -EvidenceRoot $Root -ReaderScript 'stand-in' -Repetitions 3 -TimeoutMinutes 1 -ReaderInvoker {
                param($Pause, $Round, $Directory)
                Set-Content -LiteralPath (Join-Path $Directory 'x-crosscheck.json') -Value '{"outcome":"MATCH"}'
                0
            } *>&1 | Out-String
        }
        $journal.Observe('stage', 'Blocked', $null)
        Set-Content -LiteralPath (Join-Path $root 'assertions.json') -Value '{}'
        $output = $watcher | Wait-Job -Timeout 60 | Receive-Job; $watcher | Remove-Job -Force
        $record = Get-Content -LiteralPath (Join-Path $plan.PauseDirectory 'pause-01.crosscheck.json') -Raw | ConvertFrom-Json
        $pauses = @($checkpoint.State.Pauses)
        @{ Ok = ($pauses.Count -eq 1 -and $pauses[0].ending -eq 'released' -and @($record.rounds).Count -eq 3 -and
                 @($record.rounds | Where-Object outcome -eq 'MATCH').Count -eq 3 -and "$output" -like '*MATCH x3*')
           Actual = "ending $($pauses[0].ending), $(@($record.rounds).Count) round(s)" }
    }
}

$failures = 0
foreach ($case in $cases) {
    try { $result = & $case.Check } catch { $result = @{ Ok = $false; Actual = "threw: $($_.Exception.Message)" } }
    $mark = if ($result.Ok) { 'PASS' } else { $failures++; 'FAIL' }
    Write-Host "[$mark] $($case.Name)"
    Write-Host "       $($result.Actual)"
}
Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ''
Write-Host "$($cases.Count - $failures) of $($cases.Count) cases as expected."
if ($failures -ne 0) { exit 1 }
