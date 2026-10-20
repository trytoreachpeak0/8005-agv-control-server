#Requires -Version 7

<#
.SYNOPSIS
    Self-check: two runs publishing the same peer commit at once build it once and both get it
    (control-server#810).

.DESCRIPTION
    Get-L2PeerPublish (L2.psm1) caches each peer's publish under <CacheRoot>\<Name>-<commit>, marked
    complete by publish.ok. Before control-server#810 a run that found no publish.ok removed the whole
    directory and built in it, so a second run arriving while the first was still building deleted the
    first one's build out from under it. The real-onboard rig publishes after the port lock and before
    the desktop lock, so two runs on different port slots really do overlap there.

    Every case runs Get-L2PeerPublish in separate pwsh processes against a throwaway git repository,
    with a stand-in build (-PublishCommand) that writes a marker named after its process and can be held
    mid-build until this script releases it. Each step waits on a signal -- a file, a log line -- never
    on a sleep, so the window "the first run is still building" is held open rather than hoped for.

    1. Two runs, same commit, same cache root: the second waits (L2_PEER_PUBLISH_WAITING) instead of
       building, the first run's build is untouched while it waits, both succeed with the same path,
       exactly one build ran, and the second one reports FromCache.
    2. A build that fails halfway leaves nothing behind -- no cache directory, no publish.ok, no
       temporary directory -- and the next call builds afresh.
    3. A holder killed mid-build: the waiter inherits the abandoned lock (and says so), clears the dead
       run's temporary directory and builds itself.
    4. A waiter that runs out of -LockTimeoutSeconds fails with L2_PEER_PUBLISH_LOCK_BUSY and touches
       nothing of the holder's build, which then completes.
    5. A third call after a completed publish reuses it: no build, no wait.
    6. A cache directory without publish.ok (what a run before control-server#810 leaves when it dies)
       is replaced by a fresh build.
    7. Two different cache roots do not wait for each other: the lock is per cache root.
    8. The journal notes still match what run-journey-g3.ps1's everyScenarioPublishedThePeersAtTheBinding
       assertion looks for, including the note of a run that reused a publish after waiting.
    9. A relative cache root is resolved against PowerShell's location, as the cmdlets always did, not
       against the process's current directory, and the returned path is absolute (review S2 of PR #822).
    10. A cache directory that appears while the build is running -- a checkout before control-server#810
        building in place -- is neither overwritten nor deleted: the rename fails with a named reason and
        the run's own temporary directory is removed (review S4 of PR #822).

.EXAMPLE
    pwsh -NoProfile -File ./scripts/Test-L2PeerPublishCache.ps1

.NOTES
    No dotnet, no rig, no fixed port; git and pwsh only, everything under %TEMP%. About half a minute.
    The stage is removed on success and kept, with its path printed, on failure.

    Here, not under scripts/l2, for the reason scripts/Test-L2ScenarioRunScope.ps1 gives: Select-L2ScenarioRun.ps1
    reads every string in scripts/l2's code, and this file names run-journey-g3.ps1, which would make a change to the
    G3 runners run the synthetic L2 round.

    Exit code is the contract: 0 all passed, 1 something failed.
#>
[CmdletBinding()]
param(
    [ValidateRange(5, 600)]
    [int]$SignalTimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'l2/L2.psm1'
if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) { throw "File under test not found: $modulePath" }
$journeyRunner = Join-Path $PSScriptRoot 'run-journey-g3.ps1'
if (-not (Test-Path -LiteralPath $journeyRunner -PathType Leaf)) { throw "File under test not found: $journeyRunner" }

$stage = Join-Path ([IO.Path]::GetTempPath()) ('l2-peer-publish-selftest-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
$null = New-Item -ItemType Directory -Path $stage

# The peer: a throwaway repository with one commit, clean, as Get-L2PeerPublish insists.
$source = Join-Path $stage 'peer'
$null = New-Item -ItemType Directory -Path $source
& git -C $source init --quiet 2>&1 | Out-Null
Set-Content -LiteralPath (Join-Path $source 'peer.txt') -Value 'peer' -Encoding utf8NoBOM
& git -C $source add peer.txt 2>&1 | Out-Null
& git -C $source -c user.name=selfcheck -c user.email=selfcheck@invalid -c commit.gpgsign=false commit --quiet -m peer 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not create the throwaway peer repository' }
$commit = (& git -C $source rev-parse HEAD).Trim()
$peerName = 'selfcheck-peer'

# Written to a file and started with -File, for the reason Test-DesktopLockQueueing.ps1 gives.
$publisherScript = Join-Path $stage 'publisher.ps1'
@'
#Requires -Version 7
param([Parameter(Mandatory)][string]$ModulePath,
      [Parameter(Mandatory)][string]$Name,
      [Parameter(Mandatory)][string]$SourceRepository,
      [Parameter(Mandatory)][string]$CacheRoot,
      [Parameter(Mandatory)][string]$SignalDirectory,
      [Parameter(Mandatory)][string]$ReleaseFile,
      [Parameter(Mandatory)][ValidateSet('gate', 'fail', 'quick')][string]$Mode,
      [Parameter(Mandatory)][string]$ResultFile,
      [int]$LockTimeoutSeconds = -1,
      [Parameter(Mandatory)][string]$Location)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module $ModulePath -Force
# PowerShell's location only; the process's current directory stays where the parent started it.
Set-Location -LiteralPath $Location

$logRoot = $ResultFile + '.logs'
$null = New-Item -ItemType Directory -Path $logRoot -Force
$journal = New-L2Journal -Path ($ResultFile + '.journal.jsonl')

# The stand-in build. It records that it ran (started-<pid>, holding the directory it built into) and
# leaves marker-<pid> in the publish, so the parent can tell whose build a directory holds.
$stub = {
    param([string]$Clone, [string]$ProjectPath, [string]$PublishDirectory, [string]$Log)
    $null = New-Item -ItemType Directory -Path $PublishDirectory -Force
    Set-Content -LiteralPath (Join-Path $PublishDirectory "marker-$PID") -Value $PID -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $SignalDirectory "started-$PID") -Value $PublishDirectory -Encoding utf8NoBOM
    if ($Mode -eq 'fail') { throw 'the stand-in build failed halfway' }
    if ($Mode -eq 'gate') {
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(300)
        while (-not (Test-Path -LiteralPath $ReleaseFile)) {
            if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'the stand-in build was never released' }
            Start-Sleep -Milliseconds 50
        }
    }
}.GetNewClosure()

$arguments = @{
    Name = $Name; SourceRepository = $SourceRepository; ProjectPath = 'peer.csproj'
    CacheRoot = $CacheRoot; LogRoot = $logRoot; Journal = $journal; PublishCommand = $stub
}
if ($LockTimeoutSeconds -ge 0) { $arguments['LockTimeoutSeconds'] = $LockTimeoutSeconds }

$result = [ordered]@{ ok = $false; message = ''; path = ''; fromCache = $false; commit = '' }
try {
    $publish = Get-L2PeerPublish @arguments
    $result.ok = $true
    $result.path = [string]$publish.Path
    $result.fromCache = [bool]$publish.FromCache
    $result.commit = [string]$publish.Commit
} catch {
    $result.message = $_.Exception.Message
}
$result | ConvertTo-Json | Set-Content -LiteralPath $ResultFile -Encoding utf8NoBOM
'@ | Set-Content -LiteralPath $publisherScript -Encoding utf8NoBOM

$failures = [Collections.Generic.List[string]]::new()
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()

function Assert-Case {
    param([Parameter(Mandatory)][string]$Name,
          [Parameter(Mandatory)][bool]$Condition,
          [Parameter(Mandatory)][AllowEmptyString()][string]$Detail)

    if ($Condition) {
        Write-Host "PASS  $Name -- $Detail"
    } else {
        Write-Host "FAIL  $Name -- $Detail"
        $script:failures.Add($Name)
    }
}

function New-CaseDirectory {
    param([Parameter(Mandatory)][string]$Case)
    $directory = Join-Path $stage $Case
    $null = New-Item -ItemType Directory -Path (Join-Path $directory 'signals') -Force
    $null = New-Item -ItemType Directory -Path (Join-Path $directory 'cache') -Force
    return [pscustomobject]@{
        Directory = $directory
        Cache = Join-Path $directory 'cache'
        Signals = Join-Path $directory 'signals'
        Release = Join-Path $directory 'release'
    }
}

function Start-Publisher {
    param([Parameter(Mandatory)][object]$Case,
          [Parameter(Mandatory)][string]$Tag,
          [Parameter(Mandatory)][string]$Mode,
          [string]$CacheRoot = $Case.Cache,
          [int]$LockTimeoutSeconds = -1,
          # The publisher's PowerShell location. The process itself always starts in $stage, so a relative
          # -CacheRoot means something different to a cmdlet and to a .NET call (review S2 of PR #822).
          [string]$Location = $stage)

    $resultFile = Join-Path $Case.Directory "$Tag.json"
    $process = Start-Process -FilePath 'pwsh' -PassThru -WindowStyle Hidden -WorkingDirectory $stage `
        -RedirectStandardOutput "$resultFile.out" -RedirectStandardError "$resultFile.err" -ArgumentList @(
            '-NoProfile', '-NonInteractive', '-File', $publisherScript,
            '-ModulePath', $modulePath, '-Name', $peerName, '-SourceRepository', $source,
            '-CacheRoot', $CacheRoot, '-SignalDirectory', $Case.Signals, '-ReleaseFile', $Case.Release,
            '-Mode', $Mode, '-ResultFile', $resultFile, '-LockTimeoutSeconds', $LockTimeoutSeconds, '-Location', $Location)
    $script:processes.Add($process)
    return [pscustomobject]@{ Tag = $Tag; Process = $process; ResultFile = $resultFile; Case = $Case }
}

function Get-PublisherOutput {
    param([Parameter(Mandatory)][object]$Publisher)
    $text = ''
    foreach ($file in @("$($Publisher.ResultFile).out", "$($Publisher.ResultFile).err")) {
        if (Test-Path -LiteralPath $file) { $text += (Get-Content -Raw -LiteralPath $file) ?? '' }
    }
    return $text
}

function Get-StartedFile {
    param([Parameter(Mandatory)][object]$Publisher)
    return Join-Path $Publisher.Case.Signals "started-$($Publisher.Process.Id)"
}

function Get-BuildCount {
    param([Parameter(Mandatory)][object]$Case)
    return @(Get-ChildItem -LiteralPath $Case.Signals -Filter 'started-*' -File).Count
}

function Wait-Signal {
    param([Parameter(Mandatory)][string]$What,
          [Parameter(Mandatory)][scriptblock]$Condition)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($SignalTimeoutSeconds)
    while (-not (& $Condition)) {
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw "Timed out after ${SignalTimeoutSeconds}s waiting for: $What" }
        Start-Sleep -Milliseconds 50
    }
}

function Wait-Started {
    param([Parameter(Mandatory)][object]$Publisher)
    Wait-Signal "$($Publisher.Tag)'s stand-in build to start" {
        if ($Publisher.Process.HasExited) {
            throw "$($Publisher.Tag) exited before its build started: $(Get-PublisherOutput $Publisher)"
        }
        Test-Path -LiteralPath (Get-StartedFile $Publisher)
    }
}

# Either of the two things a second run can do: queue (the fix) or start building (the defect).
function Wait-QueuedOrBuilding {
    param([Parameter(Mandatory)][object]$Publisher)
    Wait-Signal "$($Publisher.Tag) to queue or to start building" {
        (Get-PublisherOutput $Publisher) -match 'L2_PEER_PUBLISH_WAITING' -or
            (Test-Path -LiteralPath (Get-StartedFile $Publisher)) -or $Publisher.Process.HasExited
    }
}

function Wait-Publisher {
    param([Parameter(Mandatory)][object]$Publisher)
    if (-not $Publisher.Process.WaitForExit($SignalTimeoutSeconds * 1000)) {
        throw "$($Publisher.Tag) did not finish within ${SignalTimeoutSeconds}s"
    }
    if (-not (Test-Path -LiteralPath $Publisher.ResultFile)) {
        return [pscustomobject]@{ ok = $false; message = "no result file; output: $(Get-PublisherOutput $Publisher)"
            path = ''; fromCache = $false; commit = '' }
    }
    return Get-Content -Raw -LiteralPath $Publisher.ResultFile | ConvertFrom-Json
}

function Get-Leftover {
    param([Parameter(Mandatory)][string]$CacheRoot)
    return @(Get-ChildItem -LiteralPath $CacheRoot -Directory |
        Where-Object { $_.Name -ne "$peerName-$commit" } | ForEach-Object Name)
}

function Get-JournalNote {
    param([Parameter(Mandatory)][object]$Publisher)
    $path = "$($Publisher.ResultFile).journal.jsonl"
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    return @(Get-Content -LiteralPath $path | ForEach-Object { ($_ | ConvertFrom-Json).note })
}

$target = { param($CacheRoot) Join-Path $CacheRoot "$peerName-$commit" }
$journalNotes = [Collections.Generic.List[object]]::new()

try {
    # --- 1. two runs, same commit, same cache root --------------------------------------------------
    $case = New-CaseDirectory 'concurrent'
    $a = Start-Publisher $case 'a' 'gate'
    Wait-Started $a
    $aBuild = (Get-Content -Raw -LiteralPath (Get-StartedFile $a)).Trim()
    $aMarker = Join-Path $aBuild "marker-$($a.Process.Id)"
    $b = Start-Publisher $case 'b' 'gate'
    Wait-QueuedOrBuilding $b
    $bQueued = (Get-PublisherOutput $b) -match 'L2_PEER_PUBLISH_WAITING'
    $bBuilding = Test-Path -LiteralPath (Get-StartedFile $b)
    $aIntact = Test-Path -LiteralPath $aMarker
    Assert-Case '1 second run queues' ($bQueued -and -not $bBuilding) "queued=$bQueued building=$bBuilding"
    Assert-Case '1 first run''s build untouched while the second is there' $aIntact "marker $aMarker present=$aIntact"
    Set-Content -LiteralPath $case.Release -Value 'go'
    $aResult = Wait-Publisher $a
    $bResult = Wait-Publisher $b
    Assert-Case '1 both succeed' ($aResult.ok -and $bResult.ok) "a: ok=$($aResult.ok) $($aResult.message); b: ok=$($bResult.ok) $($bResult.message)"
    $expectedPath = Join-Path (& $target $case.Cache) 'publish'
    Assert-Case '1 both get the published path' ($aResult.path -eq $expectedPath -and $bResult.path -eq $expectedPath) "a=$($aResult.path) b=$($bResult.path)"
    $builds = Get-BuildCount $case
    Assert-Case '1 built exactly once' ($builds -eq 1) "builds=$builds"
    $published = Join-Path $expectedPath "marker-$($a.Process.Id)"
    Assert-Case '1 the publish is the first run''s build' (Test-Path -LiteralPath $published) "expected $published"
    Assert-Case '1 the second run reports FromCache' ([bool]$bResult.fromCache -and -not [bool]$aResult.fromCache) "a=$($aResult.fromCache) b=$($bResult.fromCache)"
    $stamp = Join-Path (& $target $case.Cache) 'publish.ok'
    Assert-Case '1 publish.ok written' (Test-Path -LiteralPath $stamp) $stamp
    $leftover = @(Get-Leftover $case.Cache)
    Assert-Case '1 nothing left beside the cache directory' ($leftover.Count -eq 0) ($leftover -join ', ')
    $journalNotes.Add(@{ Tag = '1a built'; Notes = (Get-JournalNote $a) })
    $journalNotes.Add(@{ Tag = '1b reused after waiting'; Notes = (Get-JournalNote $b) })

    # --- 5. a third call reuses it -------------------------------------------------------------------
    $c = Start-Publisher $case 'c' 'quick'
    $cResult = Wait-Publisher $c
    Assert-Case '5 a later call reuses without building or waiting' (
        $cResult.ok -and [bool]$cResult.fromCache -and (Get-BuildCount $case) -eq 1 -and
        (Get-PublisherOutput $c) -notmatch 'L2_PEER_PUBLISH_WAITING') "ok=$($cResult.ok) fromCache=$($cResult.fromCache) builds=$(Get-BuildCount $case)"
    $journalNotes.Add(@{ Tag = '5 reused'; Notes = (Get-JournalNote $c) })

    # --- 2. a build that fails halfway -----------------------------------------------------------------
    $case = New-CaseDirectory 'failure'
    $failed = Wait-Publisher (Start-Publisher $case 'failed' 'fail')
    $entries = @(Get-ChildItem -LiteralPath $case.Cache -Force | ForEach-Object Name)
    Assert-Case '2 the failed build is reported' (-not $failed.ok -and $failed.message -match 'failed halfway') $failed.message
    Assert-Case '2 the failed build leaves nothing in the cache root' ($entries.Count -eq 0) ($entries -join ', ')
    $retry = Wait-Publisher (Start-Publisher $case 'retry' 'quick')
    Assert-Case '2 the next call builds afresh' ($retry.ok -and -not [bool]$retry.fromCache -and (Get-BuildCount $case) -eq 2) "ok=$($retry.ok) $($retry.message) fromCache=$($retry.fromCache) builds=$(Get-BuildCount $case)"

    # --- 3. a holder killed mid-build ----------------------------------------------------------------
    $case = New-CaseDirectory 'killed'
    $holder = Start-Publisher $case 'holder' 'gate'
    Wait-Started $holder
    $waiter = Start-Publisher $case 'waiter' 'gate'
    Wait-QueuedOrBuilding $waiter
    $waiterQueued = (Get-PublisherOutput $waiter) -match 'L2_PEER_PUBLISH_WAITING'
    Assert-Case '3 the waiter queues behind the holder' ($waiterQueued -and -not (Test-Path -LiteralPath (Get-StartedFile $waiter))) "queued=$waiterQueued"
    Stop-Process -Id $holder.Process.Id -Force
    $null = $holder.Process.WaitForExit(30000)
    Set-Content -LiteralPath $case.Release -Value 'go'
    $waited = Wait-Publisher $waiter
    $waiterBuilt = Join-Path (Join-Path (& $target $case.Cache) 'publish') "marker-$($waiter.Process.Id)"
    Assert-Case '3 the waiter builds after the holder died' ($waited.ok -and -not [bool]$waited.fromCache -and (Test-Path -LiteralPath $waiterBuilt)) "ok=$($waited.ok) $($waited.message) fromCache=$($waited.fromCache)"
    Assert-Case '3 the abandoned lock is reported' ((Get-PublisherOutput $waiter) -match 'L2_PEER_PUBLISH_ABANDONED') (Get-PublisherOutput $waiter).Trim()
    $leftover = @(Get-Leftover $case.Cache)
    Assert-Case '3 the dead run''s temporary directory is cleared' ($leftover.Count -eq 0) ($leftover -join ', ')

    # --- 4. a waiter that runs out of time --------------------------------------------------------------
    $case = New-CaseDirectory 'timeout'
    $holder = Start-Publisher $case 'holder' 'gate'
    Wait-Started $holder
    $holderBuild = (Get-Content -Raw -LiteralPath (Get-StartedFile $holder)).Trim()
    $holderMarker = Join-Path $holderBuild "marker-$($holder.Process.Id)"
    $impatient = Wait-Publisher (Start-Publisher $case 'impatient' 'quick' -LockTimeoutSeconds 2)
    Assert-Case '4 the impatient waiter fails with a named reason' (-not $impatient.ok -and $impatient.message -match 'L2_PEER_PUBLISH_LOCK_BUSY') $impatient.message
    Assert-Case '4 the holder''s build is untouched' (Test-Path -LiteralPath $holderMarker) $holderMarker
    Set-Content -LiteralPath $case.Release -Value 'go'
    $held = Wait-Publisher $holder
    Assert-Case '4 the holder then completes, and nobody else built' ($held.ok -and (Get-BuildCount $case) -eq 1) "ok=$($held.ok) $($held.message) builds=$(Get-BuildCount $case)"

    # --- 6. a cache directory without publish.ok ---------------------------------------------------------
    $case = New-CaseDirectory 'stale'
    $staleTarget = & $target $case.Cache
    $null = New-Item -ItemType Directory -Path (Join-Path $staleTarget 'publish') -Force
    Set-Content -LiteralPath (Join-Path $staleTarget 'publish\half-built') -Value 'x'
    $fresh = Wait-Publisher (Start-Publisher $case 'fresh' 'quick')
    $leftover = @(Get-Leftover $case.Cache)
    Assert-Case '6 an unstamped cache directory is rebuilt' (
        $fresh.ok -and -not [bool]$fresh.fromCache -and -not (Test-Path -LiteralPath (Join-Path $staleTarget 'publish\half-built')) -and
        (Test-Path -LiteralPath (Join-Path $staleTarget 'publish.ok'))) "ok=$($fresh.ok) $($fresh.message)"
    Assert-Case '6 nothing left beside the cache directory' ($leftover.Count -eq 0) ($leftover -join ', ')

    # --- 7. different cache roots ---------------------------------------------------------------------
    $case = New-CaseDirectory 'roots'
    $otherRoot = Join-Path $case.Directory 'other-cache'
    $null = New-Item -ItemType Directory -Path $otherRoot
    $first = Start-Publisher $case 'first' 'gate'
    Wait-Started $first
    $second = Start-Publisher $case 'second' 'gate' -CacheRoot $otherRoot
    Wait-QueuedOrBuilding $second
    $secondQueued = (Get-PublisherOutput $second) -match 'L2_PEER_PUBLISH_WAITING'
    Assert-Case '7 a run on another cache root does not wait' (-not $secondQueued -and (Test-Path -LiteralPath (Get-StartedFile $second))) "queued=$secondQueued"
    Set-Content -LiteralPath $case.Release -Value 'go'
    $firstResult = Wait-Publisher $first
    $secondResult = Wait-Publisher $second
    Assert-Case '7 both roots publish' ($firstResult.ok -and $secondResult.ok) "first=$($firstResult.message) second=$($secondResult.message)"

    # --- 9. a relative cache root -----------------------------------------------------------------------
    $case = New-CaseDirectory 'relative'
    $relative = Wait-Publisher (Start-Publisher $case 'relative' 'quick' -CacheRoot 'cache' -Location $case.Directory)
    $expectedPath = Join-Path (& $target $case.Cache) 'publish'
    Assert-Case '9 a relative cache root resolves against PowerShell''s location' (
        $relative.ok -and $relative.path -eq $expectedPath -and (Test-Path -LiteralPath (Join-Path (& $target $case.Cache) 'publish.ok'))) "ok=$($relative.ok) $($relative.message) path=$($relative.path)"
    $strays = @(Get-ChildItem -LiteralPath $stage -Directory -Filter "$peerName-*" | ForEach-Object Name) +
        @(if (Test-Path -LiteralPath (Join-Path $stage 'cache')) { 'cache' })
    Assert-Case '9 nothing is built under the process''s current directory' ($strays.Count -eq 0) ($strays -join ', ')

    # --- 10. a cache directory appears while the build runs -----------------------------------------------
    $case = New-CaseDirectory 'appears'
    $builder = Start-Publisher $case 'builder' 'gate'
    Wait-Started $builder
    # What a checkout before control-server#810 does: build in the cache directory itself, no lock, no stamp.
    $oldCodeFile = Join-Path (& $target $case.Cache) 'publish\old-checkout-building'
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $oldCodeFile) -Force
    Set-Content -LiteralPath $oldCodeFile -Value 'x'
    Set-Content -LiteralPath $case.Release -Value 'go'
    $collided = Wait-Publisher $builder
    Assert-Case '10 the rename fails with a named reason' (-not $collided.ok -and $collided.message -match 'Could not rename the finished build') $collided.message
    Assert-Case '10 the other checkout''s directory is left alone' (
        (Test-Path -LiteralPath $oldCodeFile) -and -not (Test-Path -LiteralPath (Join-Path (& $target $case.Cache) 'publish.ok'))) $oldCodeFile
    $leftover = @(Get-Leftover $case.Cache)
    Assert-Case '10 the run''s own temporary directory is removed' ($leftover.Count -eq 0) ($leftover -join ', ')

    # --- 8. the journal notes run-journey-g3.ps1 matches ------------------------------------------------
    # The pattern is read out of run-journey-g3.ps1 rather than copied, so a change there is checked too.
    $runnerText = Get-Content -Raw -LiteralPath $journeyRunner
    Assert-Case '8 run-journey-g3.ps1 still matches peer notes as "<name> (publish for|at) <commit>"' (
        $runnerText.Contains('"onboard-hmi (publish for|at) $OnboardCommit"') -and
        $runnerText.Contains('"slots-simulator (publish for|at) $SimulatorCommit"')) 'everyScenarioPublishedThePeersAtTheBinding'
    $pattern = "$peerName (publish for|at) $commit"
    foreach ($entry in $journalNotes) {
        $matching = @($entry.Notes | Where-Object { $_ -match $pattern })
        Assert-Case "8 journal of $($entry.Tag) matches" ($matching.Count -ge 1) (($entry.Notes) -join ' | ')
    }
} finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) { try { $process.Kill($true) } catch { } }
    }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "FAILED: $($failures.Count) assertion(s): $($failures -join '; ')"
    Write-Host "Stage kept for inspection: $stage"
    exit 1
}
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ''
Write-Host 'PASSED: all peer publish cache assertions.'
exit 0
