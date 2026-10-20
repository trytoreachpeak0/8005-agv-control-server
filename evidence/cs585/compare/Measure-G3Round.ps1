#Requires -Version 7
# control-server#585: per-runner and per-scenario timings of one g3.yml round, read from its downloaded artifact.
# build  = a scenario's own "Building ControlServer" step (0 when the scenario ran with -SkipBuild);
# setup  = from the scenario's start to its ControlServer being live (build, peers, doubles, presets);
# total  = the scenario's command in the journey's run-result.json, start to the next command's start.
param([Parameter(Mandatory)][string]$ArtifactRoot)
$ErrorActionPreference = 'Stop'

$results = Get-Content -Raw (Join-Path $ArtifactRoot 'results.json') | ConvertFrom-Json
foreach ($run in $results.runs) {
    "{0,-15} exit {1}  {2,5}s  stageRemoved={3}  stage there now: {4}" -f $run.runner, $run.exitCode, $run.seconds, $run.stageRemoved, 'n/a (runner machine)'
}
foreach ($v in $results.verdicts) { "verdict {0,-15} {1}" -f $v.runner, $v.result }
''
$journey = Get-Content -Raw (Join-Path $ArtifactRoot 'evidence/journey/run-result.json') | ConvertFrom-Json
"journey status: $($journey.status)"
$commands = @($journey.commands)
$sum = [ordered]@{ build = 0.0; setup = 0.0; total = 0.0; clone = 0.0; runnerBuild = 0.0 }
'{0,-50} {1,-7} {2,7} {3,7} {4,7}' -f 'scenario', 'outcome', 'build', 'setup', 'total'
for ($i = 0; $i -lt $commands.Count; $i++) {
    $command = $commands[$i]
    $end = if ($i + 1 -lt $commands.Count) { [DateTimeOffset]$commands[$i + 1].startedAtUtc } else { [DateTimeOffset]$journey.completedAtUtc }
    $seconds = ($end - [DateTimeOffset]$command.startedAtUtc).TotalSeconds
    if ($command.name -match '^(clone|fetch|checkout|sparse)-') { $sum.clone += $seconds; continue }
    if ($command.name -eq 'build-control-server') { $sum.runnerBuild += $seconds; "{0,-50} {1,-7} {2,7:N0}" -f $command.name, "exit $($command.exitCode)", $seconds; continue }
    if ($command.name -notmatch '^scenario-(.+)$') { continue }
    $scenario = $Matches[1]
    $timeline = @(Get-Content (Join-Path $ArtifactRoot "evidence/journey/scenarios/$scenario/timeline.jsonl") | ForEach-Object { $_ | ConvertFrom-Json })
    $start = [DateTimeOffset]$timeline[0].at
    $build = 0.0
    $buildAt = [array]::FindIndex([object[]]$timeline, [Predicate[object]] { param($e) "$($e.note)" -eq 'Building ControlServer and the test doubles.' })
    if ($buildAt -ge 0) { $build = ([DateTimeOffset]$timeline[$buildAt + 1].at - [DateTimeOffset]$timeline[$buildAt].at).TotalSeconds }
    $live = @($timeline | Where-Object { $_.criterion -eq 'control-server-live' }) | Select-Object -First 1
    $setup = if ($null -ne $live) { ([DateTimeOffset]$live.at - $start).TotalSeconds } else { [double]::NaN }
    $outcome = (@($journey.scenarios | Where-Object name -EQ $scenario) | Select-Object -First 1).outcome
    $sum.build += $build; $sum.setup += $setup; $sum.total += $seconds
    '{0,-50} {1,-7} {2,7:N0} {3,7:N0} {4,7:N0}' -f $scenario, $outcome, $build, $setup, $seconds
}
''
'clone+fetch+checkout (four repositories): {0:N0}s' -f $sum.clone
'runner build (once):                       {0:N0}s' -f $sum.runnerBuild
'scenario builds (sum):                     {0:N0}s' -f $sum.build
'scenario setup to ControlServer live (sum): {0:N0}s' -f $sum.setup
'scenarios (sum):                           {0:N0}s' -f $sum.total
