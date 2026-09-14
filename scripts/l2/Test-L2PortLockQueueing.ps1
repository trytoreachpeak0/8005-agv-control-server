#Requires -Version 7

<#
.SYNOPSIS
    Self-check: prove a second L2 orchestrator queues for the port block instead of colliding with
    the first.

.DESCRIPTION
    Invoke-L2Scenario.ps1 binds one fixed block of loopback ports, and on 2026-09-14 three runs that
    overlapped each bound part of it and went on talking to each other's processes. L2PortLock.psm1
    serialises runs on the named mutex `Global\W2G-L2PortBlock`. This script checks that lock at two
    levels.

    The mutex itself, in the style of scripts/Test-DesktopLockQueueing.ps1:

    1. **The name is the contract.** Every checkout of this repository on one machine must spell it
       identically, or two runs take two locks that never meet.
    2. **Every authenticated account can open it.** On win11-01 the CI runner (NetworkService) and a
       hand-run real-onboard scenario (the interactive account) use the same ports. This asserts the
       DACL the module writes; it cannot run a second account from here.
    3. **Fail-fast is still available.** `-TimeoutSeconds 0` against a held lock throws immediately.
    4. **A second holder queues**, with a time lower bound so an implementation that returned
       without the lock cannot pass.
    5. **A queued run says so**: L2_PORT_LOCK_WAITING, then L2_PORT_LOCK_ACQUIRED.
    6. **An abandoned lock is inherited, not poisoned**, and is reported.

    The orchestrator:

    7. **The lock order is port first, desktop second, released in reverse**, and the port lock is
       taken inside the try whose finally releases it -- read from Invoke-L2Scenario.ps1's syntax
       tree. The order is what keeps the port lock from deadlocking against the desktop lock, and a
       deadlock between two queued runs has no symptom but two runs that never finish.
    8. **Two real orchestrators queue rather than collide.** Run A starts a scenario; once A holds the
       block, run B starts the same scenario. B must print L2_PORT_LOCK_WAITING while A is still
       running and before B has built anything, B must start building only after A released, and
       both must PASS. Without the lock B either fails to bind or, as on 2026-09-14, passes a wait
       against A's processes -- which Assert-L2PortOwner now also turns into a failure.

    Every step is driven by a signal (a file, a log line, a timeline note); nothing sleeps and hopes,
    for the reason Test-DesktopLockQueueing.ps1 gives.

.PARAMETER Scenario
    The synthetic scenario the two orchestrators run. normal-load is the cheapest.

.PARAMETER SkipOrchestrators
    Run assertions 1-7 only, which take seconds; 8 builds and runs the scenario twice, about a minute.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/l2/Test-L2PortLockQueueing.ps1

.NOTES
    **It really holds `Global\W2G-L2PortBlock`**, and assertion 8 really binds the L2 ports. Other L2
    runs on this machine queue while it runs, which is the behaviour under test. It refuses to start
    while the lock is held or anything listens on the block, rather than go red about someone else's
    run -- or, worse, collide with a run on a checkout that predates the lock.

    The two orchestrators' evidence goes under a temporary directory, kept and printed when the check
    fails. It is a self-check of the rig, not evidence about the product.

    Exit code is the contract: 0 all passed, 1 something failed.
#>
[CmdletBinding()]
param(
    [string]$Scenario = 'normal-load',

    [switch]$SkipOrchestrators,

    [ValidateRange(1, 60)]
    [int]$QueueSeconds = 3,

    [ValidateRange(5, 600)]
    [int]$WaitTimeoutSeconds = 60,

    [ValidateRange(60, 3600)]
    [int]$OrchestratorTimeoutSeconds = 900
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'L2PortLock.psm1'
$orchestratorPath = Join-Path $PSScriptRoot 'Invoke-L2Scenario.ps1'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
foreach ($path in @($modulePath, $orchestratorPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "File under test not found: $path" }
}
Import-Module $modulePath -Force
Import-Module (Join-Path $PSScriptRoot 'L2.psm1') -Force

$stage = Join-Path ([IO.Path]::GetTempPath()) ("l2-port-lock-selftest-" + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $stage

# Written to files and started with -File, not passed inline with -Command; see
# Test-DesktopLockQueueing.ps1 for why.
$holderScript = Join-Path $stage 'holder.ps1'
@'
#Requires -Version 7
param([Parameter(Mandatory)][string]$LockName,
      [Parameter(Mandatory)][string]$ReadyFile,
      [Parameter(Mandatory)][string]$ReleaseFile,
      [Parameter(Mandatory)][int]$MaxHoldSeconds)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Deliberately not the module under test: it only has to be another process that genuinely holds
# this mutex.
$mutex = [Threading.Mutex]::new($false, $LockName)
if (-not $mutex.WaitOne([TimeSpan]::FromSeconds(30))) {
    throw "The holder could not take the lock itself; something else on this machine holds $LockName"
}
try {
    Set-Content -LiteralPath $ReadyFile -Value 'held' -Encoding utf8NoBOM
    # MaxHoldSeconds is only a backstop against a dead parent.
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($MaxHoldSeconds)
    while (-not (Test-Path -LiteralPath $ReleaseFile) -and [DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
} finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
'@ | Set-Content -LiteralPath $holderScript -Encoding utf8NoBOM

$waiterScript = Join-Path $stage 'waiter.ps1'
@'
#Requires -Version 7
param([Parameter(Mandatory)][string]$ModulePath,
      [Parameter(Mandatory)][int]$TimeoutSeconds,
      [Parameter(Mandatory)][string]$ResultFile)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module $ModulePath -Force

$logFile = $ResultFile + '.log'
$startedAt = [DateTimeOffset]::UtcNow
$acquired = $false
$message = ''
try {
    # Line by line, so the parent sees "blocked" while the waiter is still blocked.
    $handle = Enter-L2PortLock -Reason 'L2 port lock self-check' -TimeoutSeconds $TimeoutSeconds *>&1 |
        ForEach-Object {
            if ($_ -is [Management.Automation.InformationRecord] -or
                $_ -is [Management.Automation.WarningRecord]) {
                $_.ToString() | Out-File -LiteralPath $logFile -Append -Encoding utf8
            } else {
                $_
            }
        }
    $acquired = $null -ne $handle
    if ($acquired) { Exit-L2PortLock -Handle $handle }
} catch {
    $message = $_.Exception.Message
    $message | Out-File -LiteralPath $logFile -Append -Encoding utf8
}
[ordered]@{
    acquired = $acquired
    message = $message
    elapsedSeconds = ([DateTimeOffset]::UtcNow - $startedAt).TotalSeconds
} | ConvertTo-Json | Set-Content -LiteralPath $ResultFile -Encoding utf8NoBOM
'@ | Set-Content -LiteralPath $waiterScript -Encoding utf8NoBOM

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

function Start-Holder {
    param([Parameter(Mandatory)][string]$LockName,
          [Parameter(Mandatory)][string]$ReadyFile,
          [Parameter(Mandatory)][string]$ReleaseFile)

    $process = Start-Process -FilePath 'pwsh' -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-NonInteractive', '-File', $holderScript,
        '-LockName', $LockName, '-ReadyFile', $ReadyFile,
        '-ReleaseFile', $ReleaseFile, '-MaxHoldSeconds', 300)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    while (-not (Test-Path -LiteralPath $ReadyFile) -and [DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "The holder exited early, exitCode=$($process.ExitCode)" }
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $ReadyFile)) { throw 'The holder did not take the lock within 60s' }
    return $process
}

function Start-Waiter {
    param([Parameter(Mandatory)][int]$TimeoutSeconds,
          [Parameter(Mandatory)][string]$ResultFile)

    return Start-Process -FilePath 'pwsh' -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-NonInteractive', '-File', $waiterScript,
        '-ModulePath', $modulePath, '-TimeoutSeconds', $TimeoutSeconds, '-ResultFile', $ResultFile)
}

# Returns as soon as $Path contains $Pattern; throws if $Process exits first or the deadline passes.
function Wait-FileMatch {
    param([Parameter(Mandatory)][string]$Path,
          [Parameter(Mandatory)][string]$Pattern,
          [Parameter(Mandatory)][Diagnostics.Process]$Process,
          [Parameter(Mandatory)][string]$What,
          [int]$TimeoutSeconds = 60)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            $text = Get-Content -LiteralPath $Path -Raw -ErrorAction SilentlyContinue
            if ($text -match $Pattern) { return }
        }
        if ($Process.HasExited) { throw "$What exited before '$Pattern' appeared in $Path, exitCode=$($Process.ExitCode)" }
        Start-Sleep -Milliseconds 100
    }
    throw "'$Pattern' never appeared in $Path within ${TimeoutSeconds}s ($What)"
}

function Read-WaiterResult {
    param([Parameter(Mandatory)][string]$ResultFile)

    if (-not (Test-Path -LiteralPath $ResultFile)) { throw "The waiter wrote no result: $ResultFile" }
    $result = Get-Content -LiteralPath $ResultFile -Raw | ConvertFrom-Json
    $logPath = $ResultFile + '.log'
    $log = if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Raw } else { '' }
    return [pscustomobject]@{
        Acquired = [bool]$result.acquired
        Message = [string]$result.message
        ElapsedSeconds = [double]$result.elapsedSeconds
        Log = $log
    }
}

function Start-Orchestrator {
    param([Parameter(Mandatory)][string]$Name)

    $evidence = Join-Path $stage "orchestrator-$Name"
    $outLog = "$evidence.out.log"
    $process = Start-Process -FilePath 'pwsh' -PassThru -WindowStyle Hidden -WorkingDirectory $repository `
        -RedirectStandardOutput $outLog -RedirectStandardError "$evidence.err.log" -ArgumentList @(
            '-NoProfile', '-NonInteractive', '-File', $orchestratorPath,
            '-Scenario', $Scenario, '-EvidenceRoot', $evidence)
    # Touching Handle keeps ExitCode readable after the process is gone.
    $null = $process.Handle
    return [pscustomobject]@{ Name = $Name; Process = $process; Evidence = $evidence; OutLog = $outLog }
}

# The time of the first timeline note matching $Pattern, or $null.
function Get-TimelineNoteTime {
    param([Parameter(Mandatory)][string]$Evidence,
          [Parameter(Mandatory)][string]$Pattern)

    $timeline = Join-Path $Evidence 'timeline.jsonl'
    if (-not (Test-Path -LiteralPath $timeline)) { return $null }
    foreach ($line in @(Get-Content -LiteralPath $timeline)) {
        $entry = $line | ConvertFrom-Json
        # The time comes from the raw line, not from $entry.at: ConvertFrom-Json turns an ISO string
        # into a DateTime, and formatting that back drops the fractional seconds -- which is the whole
        # resolution this ordering needs, since B starts building within milliseconds of A releasing.
        if ($entry.PSObject.Properties['note'] -and $entry.note -match $Pattern -and $line -match '"at":"([^"]+)"') {
            return [DateTimeOffset]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
        }
    }
    return $null
}

$holders = @()
$orchestrators = @()
$keepStage = $false
try {
    # Refuse to start rather than go red about someone else's run.
    try {
        $probe = Enter-L2PortLock -Reason 'L2 port lock self-check precondition' -TimeoutSeconds 0
    } catch {
        throw "L2_PORT_LOCK_SELFTEST_BLOCKED: another L2 run holds the port block; run this when it is done. $($_.Exception.Message)"
    }

    # 1. The name is the contract.
    $lockName = Get-L2PortLockName
    Assert-Case -Name 'lock-name-is-the-contract' `
        -Condition ($lockName -eq 'Global\W2G-L2PortBlock') `
        -Detail "Get-L2PortLockName returned '$lockName'"

    # 2. Open to every authenticated account. Read from the object this process holds, so it is the
    #    DACL the kernel actually has, not the one the module meant to write.
    try {
        $rules = @([Threading.ThreadingAclExtensions]::GetAccessControl($probe.Mutex).GetAccessRules(
            $true, $true, [Security.Principal.SecurityIdentifier]))
    } finally {
        Exit-L2PortLock -Handle $probe
    }
    $authenticatedUsers = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::AuthenticatedUserSid, $null)
    $grant = @($rules | Where-Object {
        $_.IdentityReference -eq $authenticatedUsers -and
        $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
        ($_.MutexRights -band [Security.AccessControl.MutexRights]::FullControl) -eq
            [Security.AccessControl.MutexRights]::FullControl })
    Assert-Case -Name 'lock-is-open-to-every-authenticated-account' `
        -Condition ($grant.Count -ge 1) `
        -Detail ("rules: " + (($rules | ForEach-Object { "$($_.IdentityReference) $($_.AccessControlType) $($_.MutexRights)" }) -join '; '))

    # 3. Fail-fast is still reachable.
    $release3 = Join-Path $stage 'release-3'
    $holder = Start-Holder -LockName $lockName -ReadyFile (Join-Path $stage 'ready-3') -ReleaseFile $release3
    $holders += $holder
    $resultFile = Join-Path $stage 'result-3.json'
    $waiter = Start-Waiter -TimeoutSeconds 0 -ResultFile $resultFile
    $waiter.WaitForExit()
    $result = Read-WaiterResult -ResultFile $resultFile
    Assert-Case -Name 'zero-timeout-still-fails-fast' `
        -Condition ((-not $result.Acquired) -and $result.Message -match 'L2_PORT_LOCK_BUSY' -and
            $result.ElapsedSeconds -lt 10) `
        -Detail ("acquired={0}, {1:N1}s, message='{2}'" -f
            $result.Acquired, $result.ElapsedSeconds, $result.Message)
    Set-Content -LiteralPath $release3 -Value 'go' -Encoding utf8NoBOM
    $holder.WaitForExit(60000) | Out-Null

    # 4, 5. Queueing: block the waiter, confirm it is blocked, then time the release.
    $release4 = Join-Path $stage 'release-4'
    $holder = Start-Holder -LockName $lockName -ReadyFile (Join-Path $stage 'ready-4') -ReleaseFile $release4
    $holders += $holder
    $resultFile = Join-Path $stage 'result-4.json'
    $waiter = Start-Waiter -TimeoutSeconds $WaitTimeoutSeconds -ResultFile $resultFile
    Wait-FileMatch -Path ($resultFile + '.log') -Pattern 'L2_PORT_LOCK_WAITING' -Process $waiter -What 'the waiter'
    Start-Sleep -Seconds $QueueSeconds
    Set-Content -LiteralPath $release4 -Value 'go' -Encoding utf8NoBOM
    $waiter.WaitForExit()
    $holder.WaitForExit(60000) | Out-Null
    $result = Read-WaiterResult -ResultFile $resultFile
    Assert-Case -Name 'second-holder-queues-instead-of-failing' `
        -Condition ($result.Acquired -and $result.ElapsedSeconds -ge $QueueSeconds) `
        -Detail ("acquired={0}, waited {1:N1}s (lower bound {2}s)" -f
            $result.Acquired, $result.ElapsedSeconds, $QueueSeconds)
    Assert-Case -Name 'queued-run-says-it-is-queueing' `
        -Condition ($result.Log -match 'L2_PORT_LOCK_WAITING' -and $result.Log -match 'L2_PORT_LOCK_ACQUIRED') `
        -Detail 'the log carries both L2_PORT_LOCK_WAITING and L2_PORT_LOCK_ACQUIRED'

    # 6. Abandonment: kill the holder while the waiter is blocked on it.
    $release6 = Join-Path $stage 'release-6'
    $holder = Start-Holder -LockName $lockName -ReadyFile (Join-Path $stage 'ready-6') -ReleaseFile $release6
    $holders += $holder
    $resultFile = Join-Path $stage 'result-6.json'
    $waiter = Start-Waiter -TimeoutSeconds $WaitTimeoutSeconds -ResultFile $resultFile
    Wait-FileMatch -Path ($resultFile + '.log') -Pattern 'L2_PORT_LOCK_WAITING' -Process $waiter -What 'the waiter'
    Stop-Process -Id $holder.Id -Force
    $holder.WaitForExit(60000) | Out-Null
    $waiter.WaitForExit()
    $result = Read-WaiterResult -ResultFile $resultFile
    Assert-Case -Name 'abandoned-lock-is-inherited-not-poisoned' `
        -Condition $result.Acquired `
        -Detail ("acquired={0} after the holder was killed, {1:N1}s" -f
            $result.Acquired, $result.ElapsedSeconds)
    Assert-Case -Name 'abandoned-lock-is-reported' `
        -Condition ($result.Log -match 'L2_PORT_LOCK_ABANDONED') `
        -Detail 'the log carries the L2_PORT_LOCK_ABANDONED warning'

    # 7. The order, from the orchestrator's syntax tree.
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($orchestratorPath, [ref]$null, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw "Invoke-L2Scenario.ps1 does not parse: $($parseErrors[0].Message)" }
    $calls = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true) |
        Sort-Object { $_.Extent.StartOffset })
    $offsetOf = {
        param([string]$Name, [switch]$Last)
        $matched = @($calls | Where-Object { $_.GetCommandName() -eq $Name })
        if ($matched.Count -eq 0) { return -1 }
        return $(if ($Last) { $matched[-1] } else { $matched[0] }).Extent.StartOffset
    }
    $enterPort = & $offsetOf 'Enter-L2PortLock'
    $build = & $offsetOf 'dotnet'
    $enterDesktop = & $offsetOf 'Enter-DesktopLock'
    $exitDesktop = & $offsetOf 'Exit-DesktopLock' -Last
    $exitPort = & $offsetOf 'Exit-L2PortLock' -Last
    $enterPortCall = @($calls | Where-Object { $_.GetCommandName() -eq 'Enter-L2PortLock' }) | Select-Object -First 1
    $guard = $null
    for ($node = $enterPortCall; $null -ne $node; $node = $node.Parent) {
        if ($node -is [Management.Automation.Language.TryStatementAst]) { $guard = $node; break }
    }
    $releasedByGuard = $null -ne $guard -and $null -ne $guard.Finally -and
        @($guard.Finally.FindAll({ param($node)
            $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Exit-L2PortLock' }, $true)).Count -ge 1
    Assert-Case -Name 'orchestrator-takes-port-lock-first-and-releases-it-last' `
        -Condition ($enterPort -ge 0 -and $enterPort -lt $build -and $build -lt $enterDesktop -and
            $exitDesktop -ge 0 -and $exitDesktop -lt $exitPort -and $releasedByGuard) `
        -Detail ("offsets: Enter-L2PortLock $enterPort < dotnet build $build < Enter-DesktopLock $enterDesktop; " +
            "Exit-DesktopLock $exitDesktop < Exit-L2PortLock $exitPort; released by the enclosing finally: $releasedByGuard")

    # 8. Two real orchestrators.
    if (-not $SkipOrchestrators) {
        $blockPorts = @(48405, 48407, 48408, 48409, 48411, 48412, 48413, 48414, 48420, 48421, 48422)
        $busy = @($blockPorts | Where-Object { @(Get-L2ListeningProcess -Port $_).Count -gt 0 })
        if ($busy.Count -gt 0) {
            throw ("L2_PORT_LOCK_SELFTEST_BLOCKED: something already listens on the L2 port block " +
                "($($busy -join ', ')) -- most likely an L2 run on a checkout without the lock, or the " +
                "leftovers of a killed run. Not starting two more orchestrators on top of it.")
        }

        $keepStage = $true
        $first = Start-Orchestrator -Name 'a'
        $orchestrators += $first
        Wait-FileMatch -Path (Join-Path $first.Evidence 'timeline.jsonl') -Pattern 'L2 port block lock acquired' `
            -Process $first.Process -What 'orchestrator A' -TimeoutSeconds 120

        $second = Start-Orchestrator -Name 'b'
        $orchestrators += $second
        Wait-FileMatch -Path $second.OutLog -Pattern 'L2_PORT_LOCK_WAITING' `
            -Process $second.Process -What 'orchestrator B' -TimeoutSeconds 120
        # Sampled at the moment B said it was queued.
        $firstStillRunning = -not $first.Process.HasExited
        $secondHadBuilt = Test-Path -LiteralPath (Join-Path $second.Evidence 'logs\build.log')
        Assert-Case -Name 'second-orchestrator-queues-while-first-holds-the-block' `
            -Condition ($firstStillRunning -and -not $secondHadBuilt) `
            -Detail "B printed L2_PORT_LOCK_WAITING; A still running then: $firstStillRunning; B had started building: $secondHadBuilt"

        foreach ($orchestrator in @($first, $second)) {
            if (-not $orchestrator.Process.WaitForExit($OrchestratorTimeoutSeconds * 1000)) {
                throw "Orchestrator $($orchestrator.Name) did not finish within ${OrchestratorTimeoutSeconds}s"
            }
        }
        $firstExit = $first.Process.ExitCode
        $secondExit = $second.Process.ExitCode
        Assert-Case -Name 'both-orchestrators-pass' `
            -Condition ($firstExit -eq 0 -and $secondExit -eq 0) `
            -Detail "A exited $firstExit, B exited $secondExit (evidence under $stage)"

        $secondOut = Get-Content -LiteralPath $second.OutLog -Raw
        Assert-Case -Name 'queued-orchestrator-says-it-proceeds' `
            -Condition ($secondOut -match 'L2_PORT_LOCK_ACQUIRED') `
            -Detail "B's stdout carries L2_PORT_LOCK_ACQUIRED"

        $released = Get-TimelineNoteTime -Evidence $first.Evidence -Pattern '^L2 port block lock released\.$'
        $building = Get-TimelineNoteTime -Evidence $second.Evidence -Pattern '^Building ControlServer'
        Assert-Case -Name 'second-orchestrator-starts-only-after-first-released' `
            -Condition ($null -ne $released -and $null -ne $building -and $building -ge $released) `
            -Detail "A released at $(${released}?.ToString('o')); B started building at $(${building}?.ToString('o'))"

        $keepStage = $failures.Count -gt 0
    }
} catch {
    $keepStage = $true
    throw
} finally {
    foreach ($process in @($holders)) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    # An orchestrator killed here would leave its doubles bound to the block, so the whole tree goes.
    foreach ($orchestrator in @($orchestrators)) {
        if (-not $orchestrator.Process.HasExited) {
            try { $orchestrator.Process.Kill($true) } catch { Write-Warning "Could not stop orchestrator $($orchestrator.Name): $_" }
        }
    }
    if ($keepStage) {
        Write-Warning "Self-check stage kept for diagnosis: $stage"
    } else {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("L2_PORT_LOCK_SELFTEST_FAILED: $($failures -join ', ')")
    exit 1
}

Write-Host $(if ($SkipOrchestrators) {
    'L2_PORT_LOCK_SELFTEST_PASSED: the port lock queues and is taken in order; the two orchestrators were skipped.'
} else {
    'L2_PORT_LOCK_SELFTEST_PASSED: the port lock queues, and a second orchestrator waits for the first.'
})
exit 0
