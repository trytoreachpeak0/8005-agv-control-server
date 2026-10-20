#Requires -Version 7

<#
The ports of the G3 runners that start real peers: run-staged-g3.ps1, run-staged-g3-restart.ps1 and
Invoke-AuthorizedAbsentObservationShadow.ps1 (control-server#809).

Until #809 each hard-coded its ports, the simulator's 1502 and 58006 among them, took no port lock, and the two
staged runners waited on http://127.0.0.1:58006 without asking who answered. A simulator somebody had left open
answered that wait, and the run went on against it: the verdict was about a build nobody had named. Only the
desktop lock kept two of these runs apart (Invoke-NightlyG3.ps1 says so), and it does nothing about a process
that is not a G3 run.

What a runner does with this module, in this order:

    $runPorts = [ordered]@{ ControlPort = $ControlPort; ...; SimulatorHttpPort = $SimulatorHttpPort }
    Assert-G3RunnerPortSet -Ports $runPorts                     # before the run creates anything
    $portLock = Enter-G3PortLock -Port $runPorts.Values ...     # first thing in the try
    Assert-G3PortsFree -Ports $runPorts                         # under the lock, before cloning
    ... Set-G3SimulatorPorts / Set-G3OnboardIoModulePort when writing the peers' settings ...
    ... Assert-G3PortOwner after each component's health wait, before its answer is used ...
    Exit-G3PortLock -Handle $portLock                           # last in the finally, after Exit-DesktopLock

THE LOCK is one machine-wide mutex per port, Global\W2G-Port-<port>, not one of L2PortLock.psm1's slots. The L2
slots are fixed blocks (40405-48429) that no G3 port falls in: taking slot 0's lock would queue a G3 run behind
every real-onboard L2 run and every slot-0 synthetic one while protecting none of the ports a G3 run binds. And
not one lock for all G3 runs either: with the ports parameters, two runs may share some ports and not others (the
staged and restart runners share the simulator's and nothing else), and a lock per port queues exactly the runs
that would collide. Everything else is L2PortLock.psm1's convention, for its reasons: the `Global\` prefix and the
W2G- name, a DACL that lets every authenticated account open it, an abandoned mutex counting as acquired, a
zero-wait first so that a queued run says so, an hour's default wait.

LOCK ORDERING. Several ports are taken in ascending port order and released in reverse, so two runs asking for
overlapping sets cannot each hold what the other waits for. A runner takes them before Enter-DesktopLock and
releases them after Exit-DesktopLock: L2PortLock.psm1's rule, port locks first, never a port lock while holding the
desktop. L2 runs hold only their slot lock, a different family of names, so no cycle forms across the two.

THE GAP between the families is closed by refusing the L2 range: a G3 port inside 40405-48429 is refused by
Assert-G3RunnerPortSet, since the slot locks and these could not see each other there.

THE OWNER CHECK is L2.psm1's Assert-L2PortOwner (2026-09-14: two L2 runs answered each other's health probes),
imported, not copied: every port of a component must be listened on by that component's own process and by
nothing else. The preflight before the clone is the cheap half: a simulator left open fails the run in seconds,
naming itself, instead of after the clone and the publishes. It cannot stand alone -- the 09-14 overlap began after
both runs had looked -- so the owner check after each health wait stays.
#>

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'l2\L2.psm1') -Force

# Every port of L2PortLock.psm1's slots 0-8: 48405-48429 moved down by 1000 per slot, so 40405-48429 in all with the
# gaps between slots. The whole stretch is refused; Test-G3RunnerPorts.ps1 checks every slot port lies inside it.
$script:L2PortRangeLow = 40405
$script:L2PortRangeHigh = 48429

$script:DefaultPortLockTimeoutSeconds = 3600

function Get-G3PortLockName {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][ValidateRange(1, 65535)][int]$Port)

    return "Global\W2G-Port-$Port"
}

function Assert-G3RunnerPortSet {
    <#
    .SYNOPSIS
        Refuse a run's ports when two are the same or one falls in the L2 port slots. Throws G3_PORT_INVALID.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Ports)

    $problems = [System.Collections.Generic.List[string]]::new()
    $byPort = [ordered]@{}
    foreach ($name in $Ports.Keys) {
        $port = [int]$Ports[$name]
        # A string key: an [ordered] dictionary takes an [int] index as a position, not a key.
        $key = [string]$port
        if (-not $byPort.Contains($key)) { $byPort[$key] = [System.Collections.Generic.List[string]]::new() }
        $byPort[$key].Add($name)
        if ($port -ge $script:L2PortRangeLow -and $port -le $script:L2PortRangeHigh) {
            $problems.Add("$name=$port is inside the L2 port slots ($script:L2PortRangeLow-$script:L2PortRangeHigh), whose locks do not see G3's")
        }
    }
    foreach ($key in $byPort.Keys) {
        if ($byPort[$key].Count -gt 1) { $problems.Add("$($byPort[$key] -join ' and ') are both $key") }
    }
    if ($problems.Count -gt 0) { throw "G3_PORT_INVALID: $($problems -join '; ')" }
}

function Enter-G3PortLock {
    <#
    .SYNOPSIS
        Take the machine-wide lock on each of $Port, in ascending order, queueing behind another run that holds one.

    .DESCRIPTION
        Returns a handle for Exit-G3PortLock. Throws G3_PORT_LOCK_BUSY naming the port it could not get, after
        releasing the ones it had. Call it from the thread that will call Exit-G3PortLock: a mutex is owned by a
        thread, and a script's try and finally share one.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int[]]$Port,
        [Parameter(Mandatory)][string]$Reason,
        [ValidateRange(0, 86400)][int]$TimeoutSeconds = $script:DefaultPortLockTimeoutSeconds
    )

    $held = [System.Collections.Generic.List[object]]::new()
    try {
        foreach ($candidate in @($Port | Sort-Object -Unique)) {
            $name = Get-G3PortLockName -Port $candidate
            $mutex = New-G3PortLockMutex -Name $name
            $owned = Request-G3PortLockCore -Mutex $mutex -Seconds 0
            if (-not $owned -and $TimeoutSeconds -gt 0) {
                Write-Host "G3_PORT_LOCK_WAITING: another run holds port $candidate ($name); queueing up to ${TimeoutSeconds}s for: $Reason"
                $owned = Request-G3PortLockCore -Mutex $mutex -Seconds $TimeoutSeconds
                if ($owned) { Write-Host "G3_PORT_LOCK_ACQUIRED: port $candidate is now ours for: $Reason" }
            }
            if (-not $owned) {
                $mutex.Dispose()
                throw "G3_PORT_LOCK_BUSY: another run holds port $candidate ($name); waited ${TimeoutSeconds}s for: $Reason"
            }
            $held.Add([pscustomobject]@{ Port = $candidate; Name = $name; Mutex = $mutex })
        }
    } catch {
        Exit-G3PortLock -Handle ([pscustomobject]@{ Locks = $held })
        throw
    }
    return [pscustomobject]@{
        Locks = $held
        Names = @($held | ForEach-Object Name)
        Reason = $Reason
        AcquiredAt = [DateTimeOffset]::UtcNow
    }
}

function Exit-G3PortLock {
    <#
    .SYNOPSIS
        Release what Enter-G3PortLock took, in reverse order. Takes $null, so it fits a finally that may run before
        the lock was ever taken.
    #>
    [CmdletBinding()]
    param([Parameter()][AllowNull()]$Handle)

    if ($null -eq $Handle) { return }
    $locks = @($Handle.Locks)
    for ($i = $locks.Count - 1; $i -ge 0; $i--) {
        try { $locks[$i].Mutex.ReleaseMutex() } catch { } finally { $locks[$i].Mutex.Dispose() }
    }
}

function New-G3PortLockMutex {
    param([Parameter(Mandatory)][string]$Name)

    # As L2PortLock.psm1: the DACL takes effect only when this call creates the object, and lets every
    # authenticated account open it, so a run under another account queues instead of failing to open it.
    $security = [Security.AccessControl.MutexSecurity]::new()
    $authenticatedUsers = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::AuthenticatedUserSid, $null)
    $security.AddAccessRule([Security.AccessControl.MutexAccessRule]::new(
            $authenticatedUsers,
            [Security.AccessControl.MutexRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow))
    $createdNew = $false
    return [Threading.MutexAcl]::Create($false, $Name, [ref]$createdNew, $security)
}

function Request-G3PortLockCore {
    param(
        [Parameter(Mandatory)][Threading.Mutex]$Mutex,
        [Parameter(Mandatory)][int]$Seconds
    )

    try {
        return $Mutex.WaitOne([TimeSpan]::FromSeconds($Seconds))
    } catch [Threading.AbandonedMutexException] {
        # The holder died without releasing; the lock is ours (DesktopLock.psm1). Its children may still hold the
        # port, which is what Assert-G3PortsFree is for.
        Write-Warning 'G3_PORT_LOCK_ABANDONED: the previous holder exited without releasing; the lock is ours.'
        return $true
    }
}

function Get-G3PortHolderText {
    <#
    .SYNOPSIS
        Who listens on a local TCP port, as "pid <id> <name> (<path>)" joined by '; ', or '' when nobody does.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][int]$Port)

    return (@(Get-L2ListeningProcess -Port $Port) | ForEach-Object {
            $process = Get-Process -Id $_ -ErrorAction SilentlyContinue
            if ($null -eq $process) { "pid $_ (already gone)"; return }
            # The path says which checkout a leftover came from; another account's process may not show it.
            $path = try { $process.Path } catch { $null }
            "pid $_ $($process.ProcessName)" + $(if ($path) { " ($path)" } else { '' })
        }) -join '; '
}

function Assert-G3PortsFree {
    <#
    .SYNOPSIS
        Throw G3_PORT_IN_USE, naming each holder's process id and path, unless nothing listens on any of $Ports.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Ports)

    $held = @(foreach ($name in $Ports.Keys) {
            $holders = Get-G3PortHolderText -Port ([int]$Ports[$name])
            if ($holders) { "$name=$($Ports[$name]) is held by $holders" }
        })
    if ($held.Count -gt 0) {
        throw ("G3_PORT_IN_USE: $($held -join '; '). Something other than this run is on its ports -- a simulator " +
            'or server left open, or the survivors of a killed run; stop it, or pass this run other ports.')
    }
}

function Assert-G3PortOwner {
    <#
    .SYNOPSIS
        Throw G3_PORT_OWNER unless every port in $Port is listened on by $Process and by nothing else.

    .DESCRIPTION
        L2.psm1's Assert-L2PortOwner, for a process this runner started: a port nobody listens on yet is polled
        for up to $TimeoutSeconds; a port another process holds fails at once naming it (pid, name, path), with
        the component's stderr tail when the component died of it.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$Process,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][int[]]$Port,
        [Parameter(Mandatory)][string]$Description,
        [string]$ErrLog,
        [int]$TimeoutSeconds = 30
    )

    $component = [pscustomobject]@{ Name = $Name; Process = $Process; ErrLog = $ErrLog }
    try {
        Assert-L2PortOwner -Component $component -Port $Port -Description $Description -TimeoutSeconds $TimeoutSeconds
    } catch {
        throw "G3_PORT_OWNER: $($_.Exception.Message)"
    }
}

function Set-G3SimulatorPorts {
    <#
    .SYNOPSIS
        Point a parsed simulator.settings.json at loopback on the given Modbus and HTTP ports, as
        Invoke-L2Scenario.ps1 does.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Settings,
        [Parameter(Mandatory)][int]$ModbusPort,
        [Parameter(Mandatory)][int]$HttpPort
    )

    $Settings.modbus.listenAddress = '127.0.0.1'
    $Settings.modbus.port = $ModbusPort
    $Settings.automation.listenAddress = '127.0.0.1'
    $Settings.automation.port = $HttpPort
}

function Set-G3OnboardIoModulePort {
    <#
    .SYNOPSIS
        Point a parsed onboard appsettings.json's IO module at the simulator's loopback Modbus port.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Settings,
        [Parameter(Mandatory)][int]$ModbusPort
    )

    $Settings.ioModule.host = '127.0.0.1'
    $Settings.ioModule.port = $ModbusPort
}

Export-ModuleMember -Function Get-G3PortLockName, Assert-G3RunnerPortSet, Enter-G3PortLock, Exit-G3PortLock,
    Get-G3PortHolderText, Assert-G3PortsFree, Assert-G3PortOwner, Set-G3SimulatorPorts, Set-G3OnboardIoModulePort
