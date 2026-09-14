#Requires -Version 7

<#
The machine-wide lock on the L2 port block.

Invoke-L2Scenario.ps1 binds one fixed block of loopback ports -- ControlServer 48405/48407, fake
RIoT 48408, fake MesIngest 48409, simulator 48411/48412, clock-skew proxy 48413, dashboard 48414 and
one synthetic peer per vehicle from 48420 up -- and every run binds the same block. Two runs at once
do not fail cleanly. On 2026-09-14 a real-onboard run and two synthetic runs started within seconds
of each other; each bound some of the ports first, and each went on talking to the other's
processes. The G3 run's fake RIoT died on SocketException 10048 while its health probe was answered
by the synthetic run's fake RIoT; the synthetic run's ControlServer died on 48405 while its fake
onboard handshook with the G3 run's server and was rejected. Evidence: C:\g3dbg\resume-002 and
C:\g3dbg\20260914-l2query-slot-configuration-activation-replay-001.

A pre-run "is anything listening" check cannot prevent that: when the second run looked, the first
had not bound anything yet. Only a lock taken before either run touches a port can.

Why a named kernel mutex rather than a lock file, why the `Global\` prefix, and why an abandoned
mutex counts as acquired: the same reasons as scripts/DesktopLock.psm1, which gives them in full.

**The name is the contract here too, but not with another repository.** Only this repository's L2
rig binds the block. Concurrent runs come from worktrees and branches of this repository on one
machine, and from run-journey-g3.ps1's clone at the bound commit; Test-L2PortLockQueueing.ps1
asserts the literal. A checkout older than this module takes no lock at all, and nothing here can
make it -- Assert-L2PortOwner in L2.psm1 is what turns such a run into a named failure.

**Unlike the desktop lock, this one is open to every authenticated account.** On win11-01 the
synthetic scenarios run from the session-0 CI runner as NetworkService, while real-onboard scenarios
are run by hand from the interactive account, against the same ports. A mutex created with the
default DACL can be opened only by the account that created it (and SYSTEM and Administrators), so
the other account would take an UnauthorizedAccessException instead of queueing. The DACL grants
Authenticated Users full control of this one object. The worst another local account can do with
that is hold the lock, which it could equally achieve by binding 48405.

LOCK ORDERING, which is what keeps this from deadlocking against the desktop lock:

    Port lock first, desktop lock second. Never take the port lock while holding the desktop lock.

Invoke-L2Scenario.ps1 is the only process that takes both. It takes the port lock before the build
and the desktop lock after the peers are published, and releases them in reverse. Every other
holder takes exactly one: run-staged-g3.ps1, run-staged-g3-restart.ps1 and
Invoke-AuthorizedAbsentObservationShadow.ps1 take only the desktop lock (their ports are 58xxx/59xxx
and the simulator's defaults 1502/58006, none of them in this block), and 8005-mes-ingest takes only
the desktop lock. One process holding both in a fixed order and everyone else holding one leaves no
cycle. The price is that a real-onboard run queued for the desktop keeps synthetic runs queued
behind it.
#>

Set-StrictMode -Version Latest

# The literal. Every checkout of this repository that runs L2 on this machine must spell it the same.
$script:PortLockName = 'Global\W2G-L2PortBlock'

# An hour, not the desktop lock's thirty minutes, because the holder can itself be queued: a
# real-onboard run holds this lock while it waits up to 1800s for the desktop, and then runs a
# scenario that may wait out the onboard's 240s recovery timeouts. Finite for the same reason as the
# desktop lock -- a named timeout beats an unexplained job timeout.
$script:DefaultPortLockTimeoutSeconds = 3600

function Get-L2PortLockName {
    <#
    .SYNOPSIS
        The machine-wide L2 port block mutex name.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param()

    return $script:PortLockName
}

function Enter-L2PortLock {
    <#
    .SYNOPSIS
        Take the machine-wide L2 port block lock, queueing if another L2 run holds it.

    .DESCRIPTION
        Returns a handle to pass to Exit-L2PortLock. Throws on timeout: the caller is about to bind
        ports that another run owns, and continuing without the lock is the 2026-09-14 failure.

        Must be called from the thread that will call Exit-L2PortLock -- a mutex is owned by a thread.
        A script's try and finally run on the same thread, which is the only shape used here.

    .PARAMETER Reason
        What this run is. It goes into the waiting message, so an operator watching a queued run can
        see what is queued.

    .PARAMETER TimeoutSeconds
        How long to queue. 0 restores fail-fast.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Reason,

        [ValidateRange(0, 86400)]
        [int]$TimeoutSeconds = $script:DefaultPortLockTimeoutSeconds
    )

    $mutex = New-L2PortLockMutex
    # Zero-wait first, purely so that a queued run says so. A silent wait is indistinguishable from a
    # hang.
    $owned = Request-L2PortLockCore -Mutex $mutex -Seconds 0
    if (-not $owned -and $TimeoutSeconds -gt 0) {
        Write-Host ("L2_PORT_LOCK_WAITING: another L2 run owns this machine's L2 port block " +
            "($script:PortLockName); queueing up to ${TimeoutSeconds}s for: $Reason")
        $owned = Request-L2PortLockCore -Mutex $mutex -Seconds $TimeoutSeconds
        if ($owned) {
            Write-Host "L2_PORT_LOCK_ACQUIRED: the L2 port block is now ours for: $Reason"
        }
    }

    if (-not $owned) {
        $mutex.Dispose()
        throw ("L2_PORT_LOCK_BUSY: another L2 run owns this machine's L2 port block " +
            "($script:PortLockName); waited ${TimeoutSeconds}s for: $Reason")
    }

    return [pscustomobject]@{
        Mutex = $mutex
        Reason = $Reason
        AcquiredAt = [DateTimeOffset]::UtcNow
    }
}

function Exit-L2PortLock {
    <#
    .SYNOPSIS
        Release a lock taken by Enter-L2PortLock. Safe to call with $null, so it fits a `finally`
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

function New-L2PortLockMutex {
    [CmdletBinding()]
    [OutputType([Threading.Mutex])]
    param()

    # The DACL only takes effect when this call creates the object. Opening one that already exists
    # asks for full control, which the DACL below grants -- so whichever account created it, every
    # other account can open it.
    $security = [Security.AccessControl.MutexSecurity]::new()
    $authenticatedUsers = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::AuthenticatedUserSid, $null)
    $security.AddAccessRule([Security.AccessControl.MutexAccessRule]::new(
        $authenticatedUsers,
        [Security.AccessControl.MutexRights]::FullControl,
        [Security.AccessControl.AccessControlType]::Allow))
    $createdNew = $false
    return [Threading.MutexAcl]::Create($false, $script:PortLockName, [ref]$createdNew, $security)
}

function Request-L2PortLockCore {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][Threading.Mutex]$Mutex,
        [Parameter(Mandatory)][int]$Seconds
    )

    try {
        return $Mutex.WaitOne([TimeSpan]::FromSeconds($Seconds))
    } catch [Threading.AbandonedMutexException] {
        # The previous holder died without releasing while this run was waiting. The wait succeeded
        # and the lock is ours; see DesktopLock.psm1. Its child processes may well have survived it,
        # still bound to the block -- Assert-L2PortOwner names them when this run's own components
        # fail to bind.
        Write-Warning 'L2_PORT_LOCK_ABANDONED: the previous holder exited without releasing; the lock is ours.'
        return $true
    }
}

Export-ModuleMember -Function Get-L2PortLockName, Enter-L2PortLock, Exit-L2PortLock
