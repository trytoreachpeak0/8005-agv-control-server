#Requires -Version 7
<#
.SYNOPSIS
    Runs l2.yml's synthetic scenario list on this machine the way the workflow's default mode does (control-server#428).

.DESCRIPTION
    The scenario list, the per-scenario run counts and the lane estimates are READ OUT of .github/workflows/l2.yml,
    not copied: a copy would be a second list to keep in step. Builds once, then runs the lanes with -SkipBuild
    through scripts/l2/L2Lanes.psm1, exactly as the workflow step does.

    -KeepStage holds a read handle on every run's controlserver.db (and its -wal and -shm), so the orchestrator's
    end-of-run cleanup cannot delete the stage root of a PASSING run. That is what the offline criterion checks read.
    It is off for the run whose verdict is quoted; the kept databases come from a second run of the affected scenarios.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [ValidateRange(1, 4)][int]$Lanes = 2,
    [string[]]$Only = @(),
    [switch]$KeepStage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $EvidenceRoot) { throw "EvidenceRoot must not exist: $EvidenceRoot" }
Set-Location -LiteralPath $Repository

$workflow = Get-Content -Raw -LiteralPath (Join-Path $Repository '.github/workflows/l2.yml')
$listMatch = [regex]::Match($workflow, '(?s)\$scenarios = @\(\r?\n(.*?)\r?\n\s*\)\r?\n\s*# One run''s usual length')
$estimateMatch = [regex]::Match($workflow, '(?s)\$estimates = @\{\r?\n(.*?)\r?\n\s*\}\r?\n')
if (-not $listMatch.Success -or -not $estimateMatch.Success) { throw 'Could not find $scenarios / $estimates in l2.yml' }
$scenarios = @(Invoke-Expression "@(`n$($listMatch.Groups[1].Value)`n)")
$estimates = Invoke-Expression "@{`n$($estimateMatch.Groups[1].Value)`n}"
if ($scenarios.Count -lt 60) { throw "Read only $($scenarios.Count) scenarios out of l2.yml" }

# `pwsh -File ... -Only a,b` hands the list over as ONE string; split it rather than run nothing.
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object Trim | Where-Object { $_ })
$unknown = @($Only | Where-Object { $_ -notin $scenarios.Name })
if ($unknown.Count -gt 0) { throw "Not in l2.yml's scenario list: $($unknown -join ', ')" }

# Default mode: DefaultRuns ?? 1, as in the workflow.
$items = @(foreach ($scenario in $scenarios) {
        if ($Only.Count -gt 0 -and $scenario.Name -notin $Only) { continue }
        [pscustomobject]@{
            Name = $scenario.Name; Runs = ($scenario['DefaultRuns'] ?? 1); BatchId = ($scenario['BatchId'] ?? 'batch-2')
            EstimateSeconds = $estimates[$scenario.Name]
        }
    })

Import-Module (Join-Path $Repository 'scripts/l2/L2Lanes.psm1') -Force
$plan = @(Get-L2LanePlan -Items $items -LaneCount $Lanes)
$head = (git -C $Repository rev-parse HEAD).Trim()
$dirty = @(git -C $Repository status --porcelain -- scripts src tools tests).Count
Write-Host "HEAD $head, $dirty uncommitted path(s) under scripts/src/tools/tests; $($items.Count) scenarios, $(($items | Measure-Object Runs -Sum).Sum) runs, $Lanes lanes"
foreach ($lane in $plan) {
    Write-Host "L2 lane $($lane.Lane) (slot $($lane.Slot), about $($lane.EstimateSeconds)s): $(($lane.Items | ForEach-Object Name) -join ', ')"
}

& dotnet build ./ControlServer.sln -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet build exited with $LASTEXITCODE" }

$null = New-Item -ItemType Directory -Path $EvidenceRoot -Force
$stageTemp = Join-Path $EvidenceRoot '_stage'
$null = New-Item -ItemType Directory -Path $stageTemp -Force
$env:TEMP = $stageTemp
$env:TMP = $stageTemp

$keeper = $null
$stop = [hashtable]::Synchronized(@{ Stop = $false })
if ($KeepStage) {
    $keeper = Start-ThreadJob -ArgumentList $stageTemp, $stop -ScriptBlock {
        param($root, $stop)
        $held = @{}
        while (-not $stop.Stop) {
            foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Filter 'controlserver.db*' -ErrorAction SilentlyContinue) {
                if ($held.ContainsKey($file.FullName)) { continue }
                try {
                    $held[$file.FullName] = [IO.FileStream]::new($file.FullName, 'Open', 'Read', 'ReadWrite')
                } catch { }
            }
            Start-Sleep -Milliseconds 150
        }
        foreach ($stream in $held.Values) { $stream.Dispose() }
        $held.Count
    }
}

$state = [hashtable]::Synchronized(@{ CheckEnabled = $false; Superseded = $false })
$clock = [Diagnostics.Stopwatch]::StartNew()
$records = @(Invoke-L2LanePlan -Plan $plan -EvidenceRoot $EvidenceRoot `
        -Orchestrator (Join-Path $Repository 'scripts/l2/Invoke-L2Scenario.ps1') -State $state)
$wall = [Math]::Round($clock.Elapsed.TotalSeconds)
if ($keeper) { $stop.Stop = $true; $heldCount = Receive-Job -Job $keeper -Wait -AutoRemoveJob; Write-Host "Kept $heldCount database file(s) open until the end." }

$failed = @($records | Where-Object ExitCode -ne 0)
$summary = @("HEAD $head", "default mode, $Lanes lanes, ${wall}s wall, $($records.Count) runs: $($records.Count - $failed.Count) PASS, $($failed.Count) FAIL", '')
$summary += $records | Sort-Object Scenario, Run | ForEach-Object {
    "{0,-4} {1,4}s  lane {2} slot {3}  {4}" -f ($_.ExitCode -eq 0 ? 'PASS' : 'FAIL'), $_.Seconds, $_.Lane, $_.Slot, $_.Label
}
$summary | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'RESULT.txt') -Encoding utf8NoBOM
$summary | ForEach-Object { Write-Host $_ }
if ($failed.Count -gt 0) { exit 1 }
