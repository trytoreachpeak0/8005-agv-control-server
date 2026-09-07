#Requires -Version 7

<#
This repository's half of the machine-wide interactive-desktop lock.

win11-01 has exactly one interactive desktop and hosts self-hosted runners for four repositories.
GitHub's `concurrency` is per-repository, so it cannot serialise this repository's desktop work
against 8005-mes-ingest's. Only a machine-level kernel object can, and the object is identified by
its name.

**The name is the contract; this code is not.** 8005-mes-ingest owns the canonical definition in
`Invoke-WithDesktopLock.ps1` and has done since 2026-09-03. This module is the deliberate second
copy — the two repositories are independent clones with no shared package, and inventing one to
share thirty lines would couple their release cycles for no gain. What must never drift is the
literal, and `Test-DesktopLockQueueing.ps1` in each repository asserts it. Two locks that never meet
is the failure this guards against, and it has no symptom other than two desktop suites occasionally
running at once.

**Why a module rather than a copy of their wrapper script.** Over there the callers are workflow
steps, so wrapping a command in `-Command { }` is the natural shape. Here all four holders acquire
*inside* a long script that is also runnable by hand — a wrapper would leave the bare invocation
unprotected, and the bare invocation is how these scripts are actually run. So the surface is
Enter/Exit around the region that owns the desktop.

**Why a named kernel mutex and not a lock file.** The kernel object dies with its last handle. A
killed job, a restarted runner session, a crashed process — if nobody else held a handle, the object
is simply gone and the next arrival creates a fresh unheld one. A stale lock is physically
impossible. A lock file cannot manage that: it stays on disk, so it needs a "has this expired?"
heuristic, and that heuristic's failure mode is exactly the worst one — two desktop suites at once.

The `Global\` prefix is required: service-mode runners live in session 0, the interactive runner in
session 1, and an unprefixed name is only visible within one session.

Known boundary, inherited from the other repository and still true here: **cross-account is not
solved.** Every holder today runs under the interactive runner's account, so the default DACL is
enough. This repository's four holders are all started by hand in an interactive session, never by
the session-0 service runner, so it does not change that. The day a service account needs the lock,
give the mutex an explicit `MutexSecurity` naming both accounts — the second account would otherwise
take an UnauthorizedAccessException on OpenExisting. Do not guess at that ACL before it is needed.
#>

Set-StrictMode -Version Latest

# The literal. It must match 8005-mes-ingest/Invoke-WithDesktopLock.ps1 character for character.
$script:DesktopLockName = 'Global\W2G-InteractiveDesktop'

# Same default as the other repository, for the same reason: a single hold measured on win11-01 tops
# out around 8 minutes, so 30 gives three times the headroom while staying finite. A finite timeout
# fails with a named reason; an infinite one fails as an unexplained job timeout.
$script:DefaultDesktopLockTimeoutSeconds = 1800

function Get-DesktopLockName {
    <#
    .SYNOPSIS
        The machine-wide interactive-desktop mutex name.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param()

    return $script:DesktopLockName
}

function Enter-DesktopLock {
    <#
    .SYNOPSIS
        Take the machine-wide interactive-desktop lock, queueing if another repository holds it.

    .DESCRIPTION
        Returns a handle to pass to Exit-DesktopLock. Throws on timeout — the caller is a scenario
        runner whose whole point is that it owns the desktop, so continuing without the lock is
        never the right answer.

    .PARAMETER Reason
        What this holder is about to do with the desktop. It goes into the log both here and in the
        waiting message, so an operator watching a queued run can see who is ahead of whom.

    .PARAMETER TimeoutSeconds
        How long to queue. 0 restores fail-fast.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Reason,

        [ValidateRange(0, 86400)]
        [int]$TimeoutSeconds = $script:DefaultDesktopLockTimeoutSeconds
    )

    $mutex = [Threading.Mutex]::new($false, $script:DesktopLockName)
    # Zero-wait first, purely so that a queued run says so. A silent twenty-minute gap in a log is
    # indistinguishable from a hang, and that misdiagnosis is the main cost of queueing at all.
    $owned = Request-DesktopLockCore -Mutex $mutex -Seconds 0
    if (-not $owned -and $TimeoutSeconds -gt 0) {
        Write-Host ("DESKTOP_LOCK_WAITING: another process owns this machine's interactive desktop " +
            "($script:DesktopLockName); queueing up to ${TimeoutSeconds}s for: $Reason")
        $owned = Request-DesktopLockCore -Mutex $mutex -Seconds $TimeoutSeconds
        if ($owned) {
            Write-Host "DESKTOP_LOCK_ACQUIRED: the interactive desktop is now ours for: $Reason"
        }
    }

    if (-not $owned) {
        $mutex.Dispose()
        throw ("DESKTOP_LOCK_BUSY: another process owns this machine's interactive desktop " +
            "($script:DesktopLockName); waited ${TimeoutSeconds}s for: $Reason")
    }

    return [pscustomobject]@{
        Mutex = $mutex
        Reason = $Reason
        AcquiredAt = [DateTimeOffset]::UtcNow
    }
}

function Exit-DesktopLock {
    <#
    .SYNOPSIS
        Release a lock taken by Enter-DesktopLock. Safe to call with $null, so it fits a `finally`
        that may run before the lock was ever taken.
    #>
    [CmdletBinding()]
    param([Parameter()][AllowNull()]$Handle)

    if ($null -eq $Handle) { return }
    try {
        $Handle.Mutex.ReleaseMutex()
    } finally {
        $Handle.Mutex.Dispose()
    }
}

function Request-DesktopLockCore {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][Threading.Mutex]$Mutex,
        [Parameter(Mandatory)][int]$Seconds
    )

    try {
        return $Mutex.WaitOne([TimeSpan]::FromSeconds($Seconds))
    } catch [Threading.AbandonedMutexException] {
        # The previous holder died without releasing *while somebody was waiting* — the waiter's own
        # handle kept the object alive, so it came back marked abandoned instead of vanishing. The
        # wait succeeded and the lock is ours. Treating it as failure would let one killed run
        # poison the lock for everyone, which is the exact failure this whole mechanism exists to
        # avoid.
        Write-Warning 'DESKTOP_LOCK_ABANDONED: the previous holder exited without releasing; the lock is ours.'
        return $true
    }
}

Export-ModuleMember -Function Get-DesktopLockName, Enter-DesktopLock, Exit-DesktopLock
