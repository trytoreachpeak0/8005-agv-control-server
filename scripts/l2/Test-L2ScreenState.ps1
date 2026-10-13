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
      - the plan's scenarios are real-onboard scenarios and every point is, by the scenario's syntax tree, a value
        one of its waits accepts -- the criterion, the value, and for a dialog the title argument rather than a button
        caption that happens to match -- so a renamed criterion or a changed value does not leave a point that is
        never reached; the check is shown to go red on such points;
      - the orchestrator's calls, its one OnCheckpoint write and its ledger fields all sit under the plan guard;
      - IO-offline lines in the onboard's log are counted per pause;
      - the watcher releases a pause after its rounds, marks a pause too short for every round incomplete, and stops
        when the run writes assertions.json.

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

# The positional or named argument of a command, as a string constant; $null when it is anything else.
function Get-ArgumentConstant([System.Management.Automation.Language.CommandAst]$Command, [int]$Position, [string]$Name) {
    $elements = $Command.CommandElements
    if ($Name) {
        for ($i = 1; $i -lt $elements.Count - 1; $i++) {
            if ($elements[$i] -is [System.Management.Automation.Language.CommandParameterAst] -and $elements[$i].ParameterName -eq $Name) {
                $next = $elements[$i + 1]
                return ($next -is [System.Management.Automation.Language.StringConstantExpressionAst]) ? $next.Value : $null
            }
        }
        return $null
    }
    if ($Position -lt $elements.Count -and $elements[$Position] -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
        return $elements[$Position].Value
    }
    return $null
}
function Get-Commands([System.Management.Automation.Language.Ast]$Ast, [string[]]$Names) {
    return @($Ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.CommandAst] -and $a.GetCommandName() -in $Names }, $true))
}

<#
Where each kind of point can come from, read off the scenario's syntax tree (cs#653 review S3):
  '@dialog=T'   a confirmed-button call whose dialog title (4th argument) is T, or a dialog-button call whose title
                (3rd) is T -- not merely T appearing somewhere, since a button and its dialog often share a caption;
  'c=True'      a Wait-*ButtonOffered call observing criterion c (4th argument): those observe True when offered;
  'c=v'         a Wait-L2Condition / Wait-L2RealOrLast with -Criterion c whose -Until holds the literal v.
#>
function Get-PointProblems([string]$Scenario, [string]$Point, [System.Management.Automation.Language.Ast]$Ast) {
    $criterion, $value = ($Point -split '=', 2)
    if ($criterion -eq '@dialog') {
        $confirmed = @(Get-Commands $Ast @('Invoke-L2RealConfirmedButton', 'Invoke-G3ConfirmedButton') | Where-Object { (Get-ArgumentConstant $_ 4) -ceq $value })
        $direct = @(Get-Commands $Ast @('Invoke-L2RealDialogButton', 'Invoke-G3DialogButton') | Where-Object { (Get-ArgumentConstant $_ 3) -ceq $value })
        if ($confirmed.Count + $direct.Count -eq 0) { return "$Scenario answers no dialog titled '$value'" }
        return $null
    }
    if ($value -ieq 'True' -and $value -cne 'True') { return "$Scenario point '$Point': the journal renders a boolean as 'True', so '$value' is never reached" }
    if ($value -ceq 'True') {
        $offered = @(Get-Commands $Ast @('Wait-L2RealButtonOffered', 'Wait-G3ButtonOffered') | Where-Object { (Get-ArgumentConstant $_ 4) -ceq $criterion })
        if ($offered.Count -eq 0) { return "$Scenario has no button-offered wait observing '$criterion' (the only kind whose accepting value is True)" }
        return $null
    }
    $waits = @(Get-Commands $Ast @('Wait-L2Condition', 'Wait-L2RealOrLast') | Where-Object { (Get-ArgumentConstant $_ 0 'Criterion') -ceq $criterion })
    if ($waits.Count -eq 0) { return "$Scenario has no wait on criterion '$criterion'" }
    $accepting = @($waits | Where-Object {
        $elements = $_.CommandElements
        $until = for ($i = 1; $i -lt $elements.Count - 1; $i++) {
            if ($elements[$i] -is [System.Management.Automation.Language.CommandParameterAst] -and $elements[$i].ParameterName -eq 'Until') { $elements[$i + 1] }
        }
        $null -ne $until -and @($until.FindAll({ param($a) $a -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $a.Value -ceq $value }, $true)).Count -gt 0
    })
    if ($accepting.Count -eq 0) { return "$Scenario's wait on '$criterion' does not accept the literal '$value'" }
    return $null
}

$cases += @{
    Name  = "the plan's scenarios are real-onboard ones and every point is a value a wait in that scenario accepts"
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
        $checked = 0
        $problems += @(foreach ($scenario in $plan.Scenarios.Keys) {
            $setupPath = Join-Path $scenarioRoot "$scenario.setup.psd1"
            if (-not (Test-Path -LiteralPath $setupPath)) { "$scenario has no setup file"; continue }
            if ((Import-PowerShellDataFile -LiteralPath $setupPath).Onboard -ne 'Real') { "$scenario is not a real-onboard scenario" }
            $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $scenarioRoot "$scenario.ps1"), [ref]$null, [ref]$null)
            foreach ($point in @($plan.Scenarios[$scenario].PauseAt)) {
                $checked++
                Get-PointProblems $scenario ([string]$point) $ast
            }
        })
        $problems = @($problems | Where-Object { $_ })
        @{ Ok = ($problems.Count -eq 0 -and $checked -eq 7)
           Actual = $(if ($problems.Count -eq 0) { "$checked points in $($plan.Scenarios.Count) scenarios and 2 dialog helpers checked" } else { $problems -join '; ' }) }
    }
}

$cases += @{
    Name  = 'the point check itself goes red on a wrong value, a bare button caption and a non-accepting value'
    Check = {
        $source = @(
            "`$null = Wait-L2RealButtonOffered `$onboard `$journal '补偿清空' 'entry' 90"
            "`$null = Invoke-L2RealConfirmedButton `$onboard `$journal '补偿清空' '确认补偿'"
            "`$x = Wait-L2Condition -Description 'd' -Journal `$journal -Criterion 'stage' -Probe { 1 } -Until { param(`$v) `$v -eq 'Blocked' }"
        ) -join "`n"
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($source, [ref]$null, [ref]$null)
        $good = @('entry=True', '@dialog=确认补偿', 'stage=Blocked' | ForEach-Object { Get-PointProblems 's' $_ $ast } | Where-Object { $_ })
        $bad = @('entry=true', '@dialog=补偿清空', 'stage=Moving', 'other=True' | ForEach-Object { Get-PointProblems 's' $_ $ast } | Where-Object { $_ })
        @{ Ok = ($good.Count -eq 0 -and $bad.Count -eq 4); Actual = "good flagged $($good.Count) ($($good -join '; ')), bad flagged $($bad.Count) of 4" }
    }
}

# Inside `if ($screenStatePlan) { ... }`, at any depth.
function Test-InsidePlanGuard([System.Management.Automation.Language.Ast]$Ast) {
    for ($node = $Ast.Parent; $null -ne $node; $node = $node.Parent) {
        if ($node -is [System.Management.Automation.Language.IfStatementAst]) {
            foreach ($clause in $node.Clauses) {
                if ($clause.Item1.Extent.Text -eq '$screenStatePlan' -and
                    $Ast.Extent.StartOffset -ge $clause.Item2.Extent.StartOffset -and $Ast.Extent.EndOffset -le $clause.Item2.Extent.EndOffset) { return $true }
            }
        }
    }
    return $false
}

$cases += @{
    Name  = 'the orchestrator only reaches the snapshot through the plan, and only the switch makes a plan'
    Check = {
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Invoke-L2Scenario.ps1'), [ref]$null, [ref]$null)
        $problems = [System.Collections.Generic.List[string]]::new()
        $resolve = @(Get-Commands $ast @('Resolve-L2ScreenStatePlan'))
        if ($resolve.Count -ne 1 -or $resolve[0].Extent.Text -notmatch '-Enabled \$ScreenState\.IsPresent\b') { $problems.Add('the plan is not resolved from -ScreenState alone') }
        # The setting call carries the plan and returns at once when it is $null; the other two run only under the guard.
        $setting = @(Get-Commands $ast @('Set-L2ScreenStateOnboardSetting'))
        if ($setting.Count -ne 1 -or $setting[0].Extent.Text -notmatch '-Plan \$screenStatePlan$') { $problems.Add('the onboard setting is not handed the plan') }
        $guardedCalls = @(Get-Commands $ast @('New-L2ScreenStateCheckpoint', 'Complete-L2ScreenState'))
        foreach ($call in $guardedCalls) {
            if (-not (Test-InsidePlanGuard $call)) { $problems.Add("$($call.GetCommandName()) outside the plan guard") }
        }
        if ($guardedCalls.Count -ne 2) { $problems.Add('expected one checkpoint and one completion call') }
        # Every write of OnCheckpoint, and of the ledger's screen-state fields, under the guard (review L1, S5).
        $writes = @($ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $a.Left.Extent.Text -match "OnCheckpoint|\['screenState'\]|\['pauseSecondsTotal'\]" }, $true))
        foreach ($write in $writes) { if (-not (Test-InsidePlanGuard $write)) { $problems.Add("'$($write.Left.Extent.Text)' written outside the plan guard") } }
        if (@($writes | Where-Object { $_.Left.Extent.Text -match 'OnCheckpoint' }).Count -ne 1) { $problems.Add('OnCheckpoint is not written exactly once') }
        if (@($writes | Where-Object { $_.Left.Extent.Text -match 'ledgerFields' }).Count -ne 3) { $problems.Add('the ledger does not get screenState on start and end and the pause total on end') }
        @{ Ok = ($problems.Count -eq 0); Actual = $(if ($problems.Count -eq 0) { "$($writes.Count) guarded writes, calls as expected" } else { $problems -join '; ' }) }
    }
}

$cases += @{
    Name  = 'semicolons separate points given on the command line'
    Check = {
        $plan = New-Plan -PauseAt @('a=1;@dialog=补偿清空', 'b=x')
        @{ Ok = (($plan.Points.Point -join '|') -ceq 'a=1|@dialog=补偿清空|b=x'); Actual = $plan.Points.Point -join ' | ' }
    }
}

$cases += @{
    Name  = "IO-offline lines in the onboard's log are counted per pause and for the run"
    Check = {
        $plan = New-Plan -PauseAt @('stage=Blocked') -PauseSeconds 30
        $checkpoint = New-L2ScreenStateCheckpoint -Plan $plan -Journal (New-L2Journal -Path (Join-Path $scratch 'io.jsonl'))
        $from = [DateTimeOffset]::Parse('2026-10-12T19:36:00+08:00')
        $checkpoint.State.Pauses.Add([ordered]@{ sequence = 1; point = 'stage=Blocked'; reachedAt = $from.ToString('o')
                endedAt = $from.AddSeconds(60).ToString('o'); seconds = 60.0; ending = 'released' })
        $logs = Join-Path $scratch 'onboard-app'; $null = New-Item -ItemType Directory -Path $logs -Force
        $line = { param($At, $Text) "$($At.ToString('o'))`tWarning`tOnboardController`t$Text" }
        Set-Content -LiteralPath (Join-Path $logs 'agv-20261012.log') -Encoding utf8NoBOM -Value @(
            (& $line $from.AddSeconds(-30) 'IO模块离线。'),
            (& $line $from.AddSeconds(5) 'IO模块离线。'),
            (& $line $from.AddSeconds(6) 'IO模块在线。'),
            (& $line $from.AddSeconds(40).ToUniversalTime() 'IO模块离线。'),
            (& $line $from.AddSeconds(90) 'IO模块离线。'))
        $identity = Complete-L2ScreenState -Plan $plan -Checkpoint $checkpoint -OnboardLogDirectory $logs
        $recorded = Get-Content -LiteralPath (Join-Path $plan.PauseDirectory 'pauses.json') -Raw | ConvertFrom-Json
        @{ Ok = ($identity.ioOffline -eq '2 in pauses, 4 in the run' -and $recorded.pauses[0].ioOfflineLines -eq 2 -and
                 $identity.pauseSecondsTotal -eq 60 -and -not $identity.Contains('completionError'))
           Actual = "ioOffline=$($identity.ioOffline) pause=$($recorded.pauses[0].ioOfflineLines) total=$($identity.pauseSecondsTotal) $($identity['completionError'])" }
    }
}

# One pause driven through the real watcher, with a stand-in reader that answers MATCH after $RoundMilliseconds.
function Invoke-WatchedPause([string]$Name, [int]$PauseSeconds, [int]$Repetitions, [int]$MarginSeconds, [int]$RoundMilliseconds) {
    $root = Join-Path $scratch $Name
    $plan = New-Plan -PauseAt @('stage=Blocked') -PauseSeconds $PauseSeconds -Root $root
    $journal = New-L2Journal -Path (Join-Path $root 'timeline.jsonl')
    $checkpoint = New-L2ScreenStateCheckpoint -Plan $plan -Journal $journal -PollMilliseconds 50
    $journal.OnCheckpoint = $checkpoint.Checkpoint
    $watcher = Start-ThreadJob -ArgumentList (Join-Path $PSScriptRoot 'Watch-L2ScreenStatePauses.ps1'), $root, $Repetitions, $MarginSeconds, $RoundMilliseconds {
        param($Script, $Root, $Repetitions, $MarginSeconds, $RoundMilliseconds)
        & $Script -EvidenceRoot $Root -ReaderScript 'stand-in' -Repetitions $Repetitions -MarginSeconds $MarginSeconds -TimeoutMinutes 1 -ReaderInvoker {
            param($Pause, $Round, $Directory)
            Start-Sleep -Milliseconds $RoundMilliseconds
            Set-Content -LiteralPath (Join-Path $Directory 'x-crosscheck.json') -Value '{"outcome":"MATCH"}'
            0
        }.GetNewClosure() *>&1 | Out-String
    }
    $journal.Observe('stage', 'Blocked', $null)
    Set-Content -LiteralPath (Join-Path $root 'assertions.json') -Value '{}'
    $output = $watcher | Wait-Job -Timeout 60 | Receive-Job; $watcher | Remove-Job -Force
    return [pscustomobject]@{
        Output = "$output"
        Record = Get-Content -LiteralPath (Join-Path $plan.PauseDirectory 'pause-01.crosscheck.json') -Raw | ConvertFrom-Json
        Pause  = @($checkpoint.State.Pauses)[0]
    }
}

$cases += @{
    Name  = 'the watcher runs its rounds, releases the pause and stops when the run writes assertions.json'
    Check = {
        $result = Invoke-WatchedPause -Name 'watched-run' -PauseSeconds 30 -Repetitions 3 -MarginSeconds 20 -RoundMilliseconds 0
        @{ Ok = ($result.Pause.ending -eq 'released' -and $result.Record.roundsRun -eq 3 -and $result.Record.incomplete -eq $false -and
                 @($result.Record.rounds | Where-Object outcome -eq 'MATCH').Count -eq 3 -and $result.Output -like '*MATCH x3*' -and
                 $result.Output -notlike '*INCOMPLETE*')
           Actual = "ending $($result.Pause.ending), $($result.Record.roundsRun) round(s), incomplete $($result.Record.incomplete)" }
    }
}

$cases += @{
    Name  = 'a window too short for every round is released before it runs out and marked incomplete (review S2, S4)'
    Check = {
        # 8 s window, 1 s margin, 2 s rounds: the watcher must stop starting rounds once 1.5 x 2 s no longer fits.
        $result = Invoke-WatchedPause -Name 'short-window' -PauseSeconds 8 -Repetitions 10 -MarginSeconds 1 -RoundMilliseconds 2000
        @{ Ok = ($result.Pause.ending -eq 'released' -and $result.Record.incomplete -eq $true -and
                 $result.Record.roundsRun -ge 1 -and $result.Record.roundsRun -lt 10 -and $result.Output -like '*INCOMPLETE*')
           Actual = "ending $($result.Pause.ending) after $($result.Pause.seconds)s, $($result.Record.roundsRun) round(s), incomplete $($result.Record.incomplete)" }
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
