#Requires -Version 7

<#
.SYNOPSIS
    The other end of an L2 run started with -ScreenState: at every pause, runs the screen-state cross-check a fixed
    number of times and then releases the run (control-server#653, for onboard-hmi#316's T3 acceptance).

.DESCRIPTION
    Start it beside Invoke-L2Scenario.ps1, on the same desktop, with the same -EvidenceRoot. It waits for the run's
    evidence directory to appear, then for each screen-state-pauses/pause-NN.json the run writes:

      1. runs the reader -Repetitions times:
             pwsh -File <ReaderScript> -Local -Directory <evidence>/screen-state -CrossCheck
                  -EvidenceDirectory <evidence>/screen-state-pauses/pause-NN-crosscheck/<i>
         starting a round only while the pause's own deadline is further away than -MarginSeconds and than half again
         the longest round so far, so a round never runs past the point where the run carries on by itself;
      2. writes pause-NN.crosscheck.json (each round's exit code and outcome, read off the reader's own record), with
         incomplete = true when fewer than -Repetitions rounds fitted -- T3 counts ten per point, and a short pause
         must not pass for a full one;
      3. writes pause-NN.release, which lets the run carry on.

    It stops once the run has written assertions.json, and prints one line per pause. It judges nothing about the
    L2 run, and the run judges nothing about it: a cross-check that came out MISMATCH, MOVING or anything but MATCH
    is reported here and in pause-NN.crosscheck.json, and is T3's to explain.

    The reader is 18-read-hmi-screen-state.ps1 in the 8005-workspace repository (onboard-hmi#316); it is passed in
    rather than found, because that repository is not a sibling of this one everywhere this runs.

    Desktop. The reader's UIA reading (agv-capture-hmi-screen.ps1) reads the onboard window on the interactive
    desktop the L2 run holds through Global\W2G-InteractiveDesktop. That is the run's own use of the desktop, so it
    takes no lock of its own -- which also means: run this only beside an L2 run that holds the lock, never alone.

.EXAMPLE
    pwsh -File scripts/l2/Watch-L2ScreenStatePauses.ps1 -EvidenceRoot F:/8005-scratch/l2/cs653-001 -ReaderScript C:/Users/szy/Desktop/8005-workspace/remote-ops/onboard-hmi/scripts/18-read-hmi-screen-state.ps1
#>
[CmdletBinding()]
param(
    # The run's -EvidenceRoot. It may not exist yet: the run creates it.
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][string]$ReaderScript,
    # Ten per pause point is the T3 acceptance count (research section 5); a run is up to that many per point.
    [ValidateRange(1, 50)][int]$Repetitions = 10,
    # Released no later than this many seconds before the pause's own deadline. A round has been measured at 6-12 s
    # (two pwsh starts, an Add-Type, a UIA walk, a 1.5 s settle and a second read); 20 s keeps one whole round clear.
    [ValidateRange(1, 120)][int]$MarginSeconds = 20,
    # How long to wait for the run to start and to finish, in all.
    [ValidateRange(1, 600)][int]$TimeoutMinutes = 90,
    # Off-rig tests only: what runs instead of `pwsh -File <ReaderScript> ...`. Called as (pauseDocument, round, directory)
    # and returns the exit code.
    [Parameter(DontShow)][scriptblock]$ReaderInvoker
)

$ErrorActionPreference = 'Stop'
if (-not $ReaderInvoker -and -not (Test-Path -LiteralPath $ReaderScript -PathType Leaf)) {
    throw "No reader at $ReaderScript (18-read-hmi-screen-state.ps1 in the 8005-workspace repository)."
}
$ReaderScript = if (Test-Path -LiteralPath $ReaderScript) { (Resolve-Path -LiteralPath $ReaderScript).Path } else { $ReaderScript }
$deadline = [DateTimeOffset]::UtcNow.AddMinutes($TimeoutMinutes)
$pauseDirectory = Join-Path $EvidenceRoot 'screen-state-pauses'
$finished = Join-Path $EvidenceRoot 'assertions.json'
$handled = @{}
$summary = [System.Collections.Generic.List[string]]::new()

function Invoke-Reader($Pause, [int]$Round, [string]$Directory) {
    if ($ReaderInvoker) { return [int](& $ReaderInvoker $Pause $Round $Directory) }
    & pwsh -NoProfile -NonInteractive -File $ReaderScript -Local -Directory $Pause.snapshotDirectory -CrossCheck `
        -EvidenceDirectory $Directory 2>&1 | ForEach-Object { Write-Host "    $_" }
    return $LASTEXITCODE
}

# The reader writes one *-crosscheck.json per round; its outcome field is the verdict. The exit code is kept beside it.
function Read-Outcome([string]$Directory) {
    $record = Get-ChildItem -LiteralPath $Directory -Filter '*-crosscheck.json' -File -ErrorAction SilentlyContinue | Select-Object -Last 1
    if (-not $record) { return '(no record)' }
    try { return [string](Get-Content -LiteralPath $record.FullName -Raw -Encoding utf8 | ConvertFrom-Json).outcome } catch { return '(unreadable record)' }
}

Write-Host "Watching $EvidenceRoot for screen-state pauses ($Repetitions cross-checks each)."
while ($true) {
    if (Test-Path -LiteralPath $pauseDirectory) {
        foreach ($markerFile in @(Get-ChildItem -LiteralPath $pauseDirectory -Filter 'pause-*.json' -File |
                Where-Object Name -Match '^pause-\d+\.json$' | Sort-Object Name)) {
            if ($handled.ContainsKey($markerFile.Name)) { continue }
            $handled[$markerFile.Name] = $true
            $pause = Get-Content -LiteralPath $markerFile.FullName -Raw -Encoding utf8 | ConvertFrom-Json -DateKind String
            $name = [IO.Path]::GetFileNameWithoutExtension($markerFile.Name)
            $pauseDeadline = [DateTimeOffset]::Parse($pause.deadline).AddSeconds(-$MarginSeconds)
            Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $name at '$($pause.point)'"
            $rounds = [System.Collections.Generic.List[object]]::new()
            $longest = 0.0
            for ($round = 1; $round -le $Repetitions; $round++) {
                if ([DateTimeOffset]::UtcNow.AddSeconds(1.5 * $longest) -ge $pauseDeadline) {
                    Write-Host "  stopping after $($round - 1) round(s): the pause's window is nearly out."
                    break
                }
                $roundStarted = [DateTimeOffset]::UtcNow
                $directory = Join-Path $pauseDirectory ("$name-crosscheck/{0:D2}" -f $round)
                $null = New-Item -ItemType Directory -Path $directory -Force
                $exit = try { Invoke-Reader $pause $round $directory } catch { Write-Host "  round ${round}: $($_.Exception.Message)"; -1 }
                $outcome = Read-Outcome $directory
                $roundSeconds = ([DateTimeOffset]::UtcNow - $roundStarted).TotalSeconds
                $longest = [math]::Max($longest, $roundSeconds)
                $rounds.Add([ordered]@{ round = $round; exitCode = $exit; outcome = $outcome; seconds = [math]::Round($roundSeconds, 1); directory = $directory })
                Write-Host "  round ${round}: $outcome (exit $exit)"
            }
            [IO.File]::WriteAllText((Join-Path $pauseDirectory "$name.crosscheck.json"), ([ordered]@{
                    point = $pause.point; repetitions = $Repetitions; roundsRun = $rounds.Count
                    incomplete = $rounds.Count -lt $Repetitions; rounds = $rounds
                } | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText((Join-Path $pauseDirectory "$name.release"), [DateTimeOffset]::UtcNow.ToString('o'))
            $counts = ($rounds | Group-Object { $_.outcome } | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', '
            $short = if ($rounds.Count -lt $Repetitions) { " -- INCOMPLETE, $($rounds.Count) of $Repetitions rounds" } else { '' }
            $summary.Add("$name '$($pause.point)': $(if ($counts) { $counts } else { 'no round' })$short")
        }
    }
    if (Test-Path -LiteralPath $finished) { break }
    if ([DateTimeOffset]::UtcNow -ge $deadline) {
        Write-Warning "Gave up after $TimeoutMinutes minutes without the run finishing ($finished)."
        break
    }
    Start-Sleep -Milliseconds 500
}

Write-Host ''
Write-Host "Screen-state cross-checks for $EvidenceRoot"
if ($summary.Count -eq 0) { Write-Host '  (no pause was reached)' } else { $summary | ForEach-Object { Write-Host "  $_" } }
