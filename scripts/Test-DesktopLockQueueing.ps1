#Requires -Version 7

<#
.SYNOPSIS
    Self-check: prove this repository's half of the machine-wide desktop lock queues instead of
    failing, and that its name still matches 8005-mes-ingest's.

.DESCRIPTION
    win11-01 has one interactive desktop and hosts runners for four repositories. This repository's
    L2 real-onboard scenarios, staged G3 runs and the authorized shadow all put WPF windows on it,
    and so do 8005-mes-ingest's golden renderer and desktop test suite. Only the machine-level mutex
    named in DesktopLock.psm1 serialises the two, and only because both sides spell it identically.

    Five assertions, each guarding something specific:

    1. **The name is the contract.** Get-DesktopLockName must return the literal
       `Global\W2G-InteractiveDesktop`. The two repositories are independent clones with no shared
       package, so this assertion and its twin in 8005-mes-ingest/Test-DesktopLockQueueing.ps1 are
       the only things standing between one lock and two locks that never meet. That failure has no
       symptom other than two desktop suites occasionally running at once.
    2. **Fail-fast is still available.** `-TimeoutSeconds 0` against a held lock throws immediately.
    3. **A second holder queues.** With a real competing process holding the lock, Enter-DesktopLock
       must wait and then succeed. The assertion carries a time lower bound, so an implementation
       that returned without the lock could not pass it.
    4. **A queued run says so.** DESKTOP_LOCK_WAITING then DESKTOP_LOCK_ACQUIRED must reach the log.
       A silent twenty-minute wait is indistinguishable from a hang, and that misdiagnosis is the
       main cost of queueing at all.
    5. **An abandoned lock is inherited, not poisoned.** Kill the holder while a waiter is blocked on
       it and the waiter must take the lock. This path was unreachable while acquisition was
       fail-fast: with nobody waiting, a killed holder's mutex simply ceased to exist.

    **Every step is driven by a signal file; nothing here sleeps and hopes.** The holder holds until
    this script releases it, and this script does not act until the waiter's log actually says it is
    blocked. A version written with fixed sleeps goes red on a machine where pwsh starts slowly, and
    an intermittently red self-check is worse than none.

.PARAMETER QueueSeconds
    How long assertion 3 makes the waiter actually queue, measured from the moment this script has
    confirmed the waiter is blocked. Independent of pwsh startup cost, which is why it can be small.

.PARAMETER WaitTimeoutSeconds
    Upper bound on the waiter's queueing. Normally irrelevant: the waiter returns the instant the
    holder lets go.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/Test-DesktopLockQueueing.ps1

.NOTES
    **It really does hold `Global\W2G-InteractiveDesktop` for a few seconds.** Using the production
    name rather than an isolated test one is the point -- a self-check against a different name
    proves something about code nobody runs. The cost is that desktop jobs queue while it runs, which
    is exactly the behaviour under test. Do not run it in the middle of a baseline render.

    Exit code is the contract: 0 all passed, 1 something failed.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 60)]
    [int]$QueueSeconds = 3,

    [ValidateRange(5, 600)]
    [int]$WaitTimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'DesktopLock.psm1'
if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) {
    throw "Module under test not found: $modulePath"
}
Import-Module $modulePath -Force

$stage = Join-Path ([IO.Path]::GetTempPath()) ("desktop-lock-selftest-" + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $stage

# Holder and waiter are written to script files and started with `-File` rather than passed inline
# with `-Command`. An inline script has to survive Start-Process's argument quoting rules, whose
# failure mode is "the argument was truncated and the process silently did something else" -- which
# is the one failure mode a self-check cannot have.
$holderScript = Join-Path $stage 'holder.ps1'
@'
#Requires -Version 7
param([Parameter(Mandatory)][string]$LockName,
      [Parameter(Mandatory)][string]$ReadyFile,
      [Parameter(Mandatory)][string]$ReleaseFile,
      [Parameter(Mandatory)][int]$MaxHoldSeconds)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The holder deliberately does not use the module under test: it only has to be another process that
# genuinely holds this mutex. Using the module would make the assertions depend on the code they are
# supposed to be testing.
$mutex = [Threading.Mutex]::new($false, $LockName)
if (-not $mutex.WaitOne([TimeSpan]::FromSeconds(30))) {
    throw "The holder could not take the lock itself; something else on this machine holds $LockName"
}
try {
    Set-Content -LiteralPath $ReadyFile -Value 'held' -Encoding utf8NoBOM
    # Hold until released. MaxHoldSeconds is only a backstop: if the parent dies, this process must
    # not sit on the machine's desktop lock forever.
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
    # Line by line, not `| Out-File`. The latter writes only when the whole pipeline ends, so
    # "the waiter is now blocked on the lock" would arrive at the same moment as "the waiter has
    # exited" -- and the parent could never act during the block, which assertions 3 and 5 need.
    $handle = Enter-DesktopLock -Reason 'desktop lock self-check' -TimeoutSeconds $TimeoutSeconds *>&1 |
        ForEach-Object {
            if ($_ -is [Management.Automation.InformationRecord] -or
                $_ -is [Management.Automation.WarningRecord]) {
                $_.ToString() | Out-File -LiteralPath $logFile -Append -Encoding utf8
            } else {
                $_
            }
        }
    $acquired = $null -ne $handle
    if ($acquired) { Exit-DesktopLock -Handle $handle }
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

function Wait-WaiterBlocked {
    param([Parameter(Mandatory)][string]$ResultFile,
          [Parameter(Mandatory)][Diagnostics.Process]$Process)

    $logPath = $ResultFile + '.log'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $logPath) {
            $log = Get-Content -LiteralPath $logPath -Raw -ErrorAction SilentlyContinue
            if ($log -match 'DESKTOP_LOCK_WAITING') { return }
        }
        if ($Process.HasExited) { throw "The waiter exited before blocking, exitCode=$($Process.ExitCode)" }
        Start-Sleep -Milliseconds 100
    }
    throw 'The waiter never reported DESKTOP_LOCK_WAITING within 60s'
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

$holders = @()
try {
    # 1. The name is the contract. This literal must equal the one in
    #    8005-mes-ingest/Invoke-WithDesktopLock.ps1, character for character.
    $lockName = Get-DesktopLockName
    Assert-Case -Name 'lock-name-is-the-contract' `
        -Condition ($lockName -eq 'Global\W2G-InteractiveDesktop') `
        -Detail "Get-DesktopLockName returned '$lockName'"

    # 2. Fail-fast is still reachable.
    $release2 = Join-Path $stage 'release-2'
    $holder = Start-Holder -LockName $lockName -ReadyFile (Join-Path $stage 'ready-2') -ReleaseFile $release2
    $holders += $holder
    $resultFile = Join-Path $stage 'result-2.json'
    $waiter = Start-Waiter -TimeoutSeconds 0 -ResultFile $resultFile
    $waiter.WaitForExit()
    $result = Read-WaiterResult -ResultFile $resultFile
    Assert-Case -Name 'zero-timeout-still-fails-fast' `
        -Condition ((-not $result.Acquired) -and $result.Message -match 'DESKTOP_LOCK_BUSY' -and
            $result.ElapsedSeconds -lt 10) `
        -Detail ("acquired={0}, {1:N1}s, message='{2}'" -f
            $result.Acquired, $result.ElapsedSeconds, $result.Message)
    Set-Content -LiteralPath $release2 -Value 'go' -Encoding utf8NoBOM
    $holder.WaitForExit(60000) | Out-Null

    # 3, 4. Queueing: block the waiter, confirm it is blocked, then time the release.
    $release3 = Join-Path $stage 'release-3'
    $holder = Start-Holder -LockName $lockName -ReadyFile (Join-Path $stage 'ready-3') -ReleaseFile $release3
    $holders += $holder
    $resultFile = Join-Path $stage 'result-3.json'
    $waiter = Start-Waiter -TimeoutSeconds $WaitTimeoutSeconds -ResultFile $resultFile
    Wait-WaiterBlocked -ResultFile $resultFile -Process $waiter
    Start-Sleep -Seconds $QueueSeconds
    Set-Content -LiteralPath $release3 -Value 'go' -Encoding utf8NoBOM
    $waiter.WaitForExit()
    $holder.WaitForExit(60000) | Out-Null
    $result = Read-WaiterResult -ResultFile $resultFile
    Assert-Case -Name 'second-holder-queues-instead-of-failing' `
        -Condition ($result.Acquired -and $result.ElapsedSeconds -ge $QueueSeconds) `
        -Detail ("acquired={0}, waited {1:N1}s (lower bound {2}s)" -f
            $result.Acquired, $result.ElapsedSeconds, $QueueSeconds)
    Assert-Case -Name 'queued-run-says-it-is-queueing' `
        -Condition ($result.Log -match 'DESKTOP_LOCK_WAITING' -and $result.Log -match 'DESKTOP_LOCK_ACQUIRED') `
        -Detail 'the log carries both DESKTOP_LOCK_WAITING and DESKTOP_LOCK_ACQUIRED'

    # 5. Abandonment: kill the holder while the waiter is blocked on it.
    $release5 = Join-Path $stage 'release-5'
    $holder = Start-Holder -LockName $lockName -ReadyFile (Join-Path $stage 'ready-5') -ReleaseFile $release5
    $holders += $holder
    $resultFile = Join-Path $stage 'result-5.json'
    $waiter = Start-Waiter -TimeoutSeconds $WaitTimeoutSeconds -ResultFile $resultFile
    Wait-WaiterBlocked -ResultFile $resultFile -Process $waiter
    Stop-Process -Id $holder.Id -Force
    $holder.WaitForExit(60000) | Out-Null
    $waiter.WaitForExit()
    $result = Read-WaiterResult -ResultFile $resultFile
    Assert-Case -Name 'abandoned-lock-is-inherited-not-poisoned' `
        -Condition $result.Acquired `
        -Detail ("acquired={0} after the holder was killed, {1:N1}s" -f
            $result.Acquired, $result.ElapsedSeconds)
    Assert-Case -Name 'abandoned-lock-is-reported' `
        -Condition ($result.Log -match 'DESKTOP_LOCK_ABANDONED') `
        -Detail 'the log carries the DESKTOP_LOCK_ABANDONED warning'
} finally {
    # If this script throws mid-way, a holder would sit on the machine's desktop lock until its own
    # 5-minute backstop. That is five minutes of stopped desktop, so take them down explicitly.
    foreach ($process in $holders) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("DESKTOP_LOCK_SELFTEST_FAILED: $($failures -join ', ')")
    exit 1
}

Write-Host 'DESKTOP_LOCK_SELFTEST_PASSED: name, fail-fast, queueing, visibility and abandonment all hold.'
exit 0
