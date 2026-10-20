#Requires -Version 7

<#
.SYNOPSIS
    Offline check of the G3 runners' ports (control-server#809): parameters, the per-port lock, the free-port
    preflight and the owner check, without running a G3.

.DESCRIPTION
    Until control-server#809, run-staged-g3.ps1 and run-staged-g3-restart.ps1 hard-coded the simulator's 1502 and
    58006, took no port lock, and waited on http://127.0.0.1:58006 without asking who answered: a simulator somebody
    had left open was silently taken for the run's own. Invoke-AuthorizedAbsentObservationShadow.ps1 hard-coded the
    same two. What this checks:

      1. G3RunnerPorts.psm1, against real processes and real mutexes, on ports no runner uses (39001-39099):
         - the lock: one Global\W2G-Port-<port> mutex per port, taken in ascending order; a port another process
           holds is G3_PORT_LOCK_BUSY naming that port, and the ports taken before it are released again (checked
           from a third process, since a mutex is re-entrant for the thread that holds it); a disjoint port is not
           held up;
         - the port set: duplicates and anything in 40405-48429 (every L2 port slot, Get-L2PortBlock 0-8) refused;
         - the preflight: a port somebody listens on is G3_PORT_IN_USE naming that process's id and path;
         - the owner check: a component whose port another process holds fails naming the holder's id and path,
           where the runners' health wait alone (lifted from fp/v2-impl before #809: any answer will do) was
           satisfied; a component that holds its own port passes;
         - the settings writers put the given ports into a simulator.settings.json and an onboard appsettings.json.
      2. Each of the three runners, from its source: the port parameters with today's defaults and a 1024-65535
         range; no port number left in its body; one $runPorts table naming every port parameter, handed to
         Assert-G3RunnerPortSet, Enter-G3PortLock and Assert-G3PortsFree; the port lock taken before the desktop
         lock and released after it; the simulator's and the onboard's settings written from the parameters; and
         (the two staged runners) the owner check on the simulator's two ports and the ControlServer's two.

    Two copies on one machine queue on Global\W2G-SelfCheck-G3RunnerPorts (control-server#820: test.yml runs on two
    cs-test runners on one machine), since the cases' ports and port locks are fixed.

    Exit 0 when every check passes, 1 otherwise, each check printed PASS or FAIL.
#>
[CmdletBinding()]
param(
    [string]$ScriptRoot = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

# One copy of this check per machine at a time (control-server#820). Its cases bind fixed ports (39011, 39013, 39019),
# take fixed Global\W2G-Port-39001..39031 mutexes, some with no wait, and assert who holds them, so a second copy on
# the same machine sees the first one's listeners and locks and goes red: run on two cs-test runners at once, one of
# each pair exited 1 every time (G3_PORT_IN_USE and G3_PORT_OWNER naming the other copy's pwsh). The cases take
# about ten seconds, so the other copy queues for that rather than the cases moving to ports of their own. Created
# with the same DACL as L2PortLock.psm1's (Authenticated Users), so runners under different accounts can open it; an
# abandoned mutex counts as taken, as there.
$selfCheckLockName = 'Global\W2G-SelfCheck-G3RunnerPorts'
$selfCheckLockSecurity = [Security.AccessControl.MutexSecurity]::new()
$selfCheckLockSecurity.AddAccessRule([Security.AccessControl.MutexAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::AuthenticatedUserSid, $null),
        [Security.AccessControl.MutexRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow))
$selfCheckLockCreated = $false
$selfCheckLock = [Threading.MutexAcl]::Create($false, $selfCheckLockName, [ref]$selfCheckLockCreated, $selfCheckLockSecurity)
$selfCheckLockOwned = $false
try {
    $selfCheckLockOwned = $selfCheckLock.WaitOne(0)
    if (-not $selfCheckLockOwned) {
        Write-Host "SELF_CHECK_LOCK_WAITING: another copy of this check holds $selfCheckLockName; queueing up to 300 s"
        $selfCheckLockOwned = $selfCheckLock.WaitOne([TimeSpan]::FromSeconds(300))
    }
} catch [Threading.AbandonedMutexException] {
    $selfCheckLockOwned = $true
}
if (-not $selfCheckLockOwned) {
    $selfCheckLock.Dispose()
    Write-Host "SELF_CHECK_LOCK_BUSY: $selfCheckLockName still held after 300 s; no check ran."
    exit 1
}

try {

$failures = [System.Collections.Generic.List[string]]::new()
function Check([string]$name, [bool]$ok, [string]$detail) {
    Write-Host ("{0} {1}{2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $(if ($ok) { '' } else { " -- $detail" }))
    if (-not $ok) { $failures.Add($name) }
}

function Get-RunnerAst([string]$Path) {
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$parseErrors)
    if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) { throw "The runner does not parse: $Path" }
    return $ast
}

function Get-Calls([System.Management.Automation.Language.Ast]$Ast, [string]$Command) {
    return @($Ast.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq $Command }, $true))
}

function Get-ParameterArgument([System.Management.Automation.Language.CommandAst]$Call, [string]$Name) {
    $elements = $Call.CommandElements
    for ($i = 0; $i -lt $elements.Count; $i++) {
        if ($elements[$i] -is [System.Management.Automation.Language.CommandParameterAst] -and $elements[$i].ParameterName -eq $Name) {
            if ($null -ne $elements[$i].Argument) { return $elements[$i].Argument }
            if ($i + 1 -lt $elements.Count) { return $elements[$i + 1] }
        }
    }
    return $null
}

function Test-InsideFunction([System.Management.Automation.Language.Ast]$Node) {
    for ($parent = $Node.Parent; $null -ne $parent; $parent = $parent.Parent) {
        if ($parent -is [System.Management.Automation.Language.FunctionDefinitionAst]) { return $true }
    }
    return $false
}

function Invoke-Caught([scriptblock]$Block) {
    try { $null = & $Block; return $null } catch { return $_.Exception.Message }
}

$pwshPath = (Get-Process -Id $PID).Path
$modulePath = Join-Path $ScriptRoot 'G3RunnerPorts.psm1'
$work = Join-Path ([IO.Path]::GetTempPath()) ("g3-ports-" + [guid]::NewGuid().ToString('n').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
$children = [System.Collections.Generic.List[Diagnostics.Process]]::new()

# A child pwsh running $Script, which writes $Marker once it holds what it was started to hold. Returns the process.
function Start-Child([string]$Name, [string]$Script) {
    $marker = Join-Path $work "$Name.ready"
    $file = Join-Path $work "$Name.ps1"
    Set-Content -LiteralPath $file -Value ($Script.Replace('__MARKER__', $marker).Replace('__MODULE__', $modulePath))
    $process = Start-Process -FilePath $pwshPath -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $file) `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $work "$Name.out.log") -RedirectStandardError (Join-Path $work "$Name.err.log")
    $children.Add($process)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $marker)) {
        if ($process.HasExited -or [DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Child '$Name' never got ready: $(Get-Content -Raw -LiteralPath (Join-Path $work "$Name.err.log") -ErrorAction SilentlyContinue)"
        }
        Start-Sleep -Milliseconds 100
    }
    return $process
}

# A child listening on 127.0.0.1:$Port, answering every HTTP request 200 with a READY body -- what a simulator left
# open answers.
function Start-Listener([string]$Name, [int]$Port) {
    return Start-Child $Name @"
`$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
`$listener.Start()
Set-Content -LiteralPath '__MARKER__' -Value ready
while (`$true) {
    `$client = `$listener.AcceptTcpClient()
    try {
        `$stream = `$client.GetStream()
        `$buffer = [byte[]]::new(4096)
        `$null = `$stream.Read(`$buffer, 0, `$buffer.Length)
        `$body = '{"status":"READY","modbus":{"isRunning":true}}'
        `$bytes = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK``r``nContent-Type: application/json``r``nContent-Length: `$(`$body.Length)``r``nConnection: close``r``n``r``n`$body")
        `$stream.Write(`$bytes, 0, `$bytes.Length)
    } catch { } finally { `$client.Dispose() }
}
"@
}

try {
    # --- 1. G3RunnerPorts.psm1 ----------------------------------------------------------------------------------
    $moduleLoaded = $false
    try { Import-Module $modulePath -Force; $moduleLoaded = $true }
    catch { Check 'G3RunnerPorts.psm1 imports' $false $_.Exception.Message }

    if ($moduleLoaded) {
        Check 'Get-G3PortLockName: Global\W2G-Port-<port>' ((Get-G3PortLockName -Port 58006) -ceq 'Global\W2G-Port-58006') (Get-G3PortLockName -Port 58006)

        # The lock.
        $holder = Start-Child 'lock-holder' @'
Import-Module '__MODULE__' -Force
$handle = Enter-G3PortLock -Port 39002 -Reason 'self-check holder'
Set-Content -LiteralPath '__MARKER__' -Value ready
Start-Sleep -Seconds 120
'@
        $busy = Invoke-Caught { Enter-G3PortLock -Port 39003, 39001, 39002 -Reason 'self-check contender' -TimeoutSeconds 0 }
        Check 'lock: a port another process holds is G3_PORT_LOCK_BUSY naming it' `
            ($busy -like 'G3_PORT_LOCK_BUSY*' -and $busy -like '*Global\W2G-Port-39002*') "$busy"
        $probe = Join-Path $work 'probe-39001.txt'
        $prober = Start-Child 'lock-prober' @"
Import-Module '__MODULE__' -Force
try { `$h = Enter-G3PortLock -Port 39001 -Reason 'self-check prober' -TimeoutSeconds 0; Exit-G3PortLock -Handle `$h; Set-Content -LiteralPath '$probe' -Value free }
catch { Set-Content -LiteralPath '$probe' -Value "held: `$(`$_.Exception.Message)" }
Set-Content -LiteralPath '__MARKER__' -Value ready
"@
        $null = $prober.WaitForExit(30000)
        $probed = (Get-Content -Raw -LiteralPath $probe -ErrorAction SilentlyContinue)?.Trim()
        Check 'lock: after G3_PORT_LOCK_BUSY the ports taken before the busy one are released' ($probed -eq 'free') "$probed"
        $disjoint = $null
        $disjointError = Invoke-Caught { $script:disjoint = Enter-G3PortLock -Port 39004 -Reason 'self-check disjoint' -TimeoutSeconds 0 }
        Check 'lock: a disjoint port is not held up by another run''s lock' ($null -eq $disjointError -and $null -ne $script:disjoint) "$disjointError"
        Exit-G3PortLock -Handle $script:disjoint
        Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue
        $null = $holder.WaitForExit(5000)
        $ordered = $null
        $orderedError = Invoke-Caught { $script:ordered = Enter-G3PortLock -Port 39007, 39005, 39006, 39005 -Reason 'self-check order' -TimeoutSeconds 0 }
        Check 'lock: duplicates taken once, in ascending port order' `
            ($null -eq $orderedError -and ($script:ordered.Names -join ',') -ceq 'Global\W2G-Port-39005,Global\W2G-Port-39006,Global\W2G-Port-39007') `
            "$orderedError $($script:ordered.Names -join ',')"
        Exit-G3PortLock -Handle $script:ordered
        Exit-G3PortLock -Handle $null
        Check 'lock: Exit-G3PortLock takes $null' $true ''

        # A holder that exits without releasing leaves the mutex abandoned; the next run takes it (with a warning)
        # instead of reporting it busy. This process keeps a handle open first, so that the named mutex outlives the
        # holder's exit and the abandoned state is what the next wait meets (review P1b of PR #819).
        $abandonedName = Get-G3PortLockName -Port 39031
        $keeper = [Threading.Mutex]::new($false, $abandonedName)
        try {
            $abandoner = Start-Child 'lock-abandoner' @'
Import-Module '__MODULE__' -Force
$handle = Enter-G3PortLock -Port 39031 -Reason 'self-check abandoner'
Set-Content -LiteralPath '__MARKER__' -Value ready
'@
            $null = $abandoner.WaitForExit(30000)
            $abandoned = $null
            $abandonedError = Invoke-Caught { $script:abandoned = Enter-G3PortLock -Port 39031 -Reason 'self-check after an abandoner' -TimeoutSeconds 0 3>$null }
            Check 'lock: a lock its holder exited without releasing is taken, not reported busy' `
                ($abandoner.HasExited -and $null -eq $abandonedError -and $null -ne $script:abandoned) "$abandonedError"
            Exit-G3PortLock -Handle $script:abandoned
        } finally {
            $keeper.Dispose()
        }

        # The port set.
        $duplicate = Invoke-Caught { Assert-G3RunnerPortSet -Ports ([ordered]@{ ControlPort = 39010; SimulatorHttpPort = 39010 }) }
        Check 'port set: a port named twice is refused, naming both' `
            ($duplicate -like 'G3_PORT_INVALID*' -and $duplicate -like '*ControlPort*' -and $duplicate -like '*SimulatorHttpPort*') "$duplicate"
        foreach ($inside in 40405, 44410, 48429) {
            $refused = Invoke-Caught { Assert-G3RunnerPortSet -Ports ([ordered]@{ SimulatorHttpPort = $inside }) }
            Check "port set: $inside is inside the L2 port slots, refused" ($refused -like 'G3_PORT_INVALID*' -and $refused -like "*$inside*") "$refused"
        }
        foreach ($outside in 40404, 48430, 1502, 58006) {
            $accepted = Invoke-Caught { Assert-G3RunnerPortSet -Ports ([ordered]@{ SimulatorHttpPort = $outside }) }
            Check "port set: $outside is accepted" ($null -eq $accepted) "$accepted"
        }
        Import-Module (Join-Path $ScriptRoot 'l2\L2PortLock.psm1') -Force
        $l2Ports = @(0..8 | ForEach-Object { (Get-L2PortBlock -Slot $_).Values } | ForEach-Object { $_; $_ + 9 })
        $escaped = @($l2Ports | Where-Object { $_ -lt 40405 -or $_ -gt 48429 })
        Check 'port set: every L2 slot port (Get-L2PortBlock 0-8, peers up to +9) lies inside the refused range' ($escaped.Count -eq 0) "$($escaped -join ', ')"

        # The preflight and the owner check, against a "simulator left open" on 39011.
        $squatter = Start-Listener 'squatter' 39011
        $inUse = Invoke-Caught { Assert-G3PortsFree -Ports ([ordered]@{ SimulatorHttpPort = 39011; SimulatorModbusPort = 39019 }) }
        Check 'preflight: a held port is G3_PORT_IN_USE naming the holder''s id and path' `
            ($inUse -like 'G3_PORT_IN_USE*' -and $inUse -like '*SimulatorHttpPort*' -and $inUse -like "*pid $($squatter.Id) *" -and $inUse -like "*$pwshPath*") "$inUse"
        $free = Invoke-Caught { Assert-G3PortsFree -Ports ([ordered]@{ SimulatorModbusPort = 39019 }) }
        Check 'preflight: a free port passes' ($null -eq $free) "$free"

        # Before #809: the staged runner's health wait, lifted from its source as it was, is satisfied by the
        # squatter. That is the silent wrong peer; the owner check below is what turns it into a named failure.
        $legacy = 'function Wait-HttpJson {
    param([string]$Uri, [int]$TimeoutSeconds = 45)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            return Invoke-RestMethod -Uri $Uri -TimeoutSec 2
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Uri"
}'
        . ([scriptblock]::Create($legacy))
        $legacyHealth = Wait-HttpJson -Uri 'http://127.0.0.1:39011/api/v1/health' -TimeoutSeconds 10
        Check 'before #809: the health wait alone takes the squatter''s READY for the run''s own simulator' ($legacyHealth.status -eq 'READY') "$($legacyHealth | ConvertTo-Json -Compress)"

        $component = Start-Child 'component' @'
Set-Content -LiteralPath '__MARKER__' -Value ready
Start-Sleep -Seconds 120
'@
        $foreign = Invoke-Caught { Assert-G3PortOwner -Process $component -Name 'slots-simulator' -Port 39011 -Description 'the simulator is serving' -TimeoutSeconds 5 }
        Check 'owner: a port another process holds fails, naming the holder''s id and path and the component' `
            ($foreign -like 'G3_PORT_OWNER*' -and $foreign -like "*pid $($squatter.Id) *" -and $foreign -like "*$pwshPath*" -and $foreign -like '*slots-simulator*') "$foreign"
        $own = Start-Listener 'own' 39013
        $owned = Invoke-Caught { Assert-G3PortOwner -Process $own -Name 'slots-simulator' -Port 39013 -Description 'the simulator is serving' -TimeoutSeconds 5 }
        Check 'owner: a component holding its own port passes' ($null -eq $owned) "$owned"
        $silent = Invoke-Caught { Assert-G3PortOwner -Process $component -Name 'slots-simulator' -Port 39018 -Description 'the simulator is serving' -TimeoutSeconds 1 }
        Check 'owner: nobody listening within the timeout fails, naming the port' ($silent -like 'G3_PORT_OWNER*' -and $silent -like '*39018*') "$silent"

        # The settings writers.
        $simulatorSettings = '{"instanceId":"x","modbus":{"listenAddress":"0.0.0.0","port":1502,"unitId":255},"automation":{"listenAddress":"0.0.0.0","port":58006}}' | ConvertFrom-Json
        Set-G3SimulatorPorts -Settings $simulatorSettings -ModbusPort 39021 -HttpPort 39022
        Check 'Set-G3SimulatorPorts: modbus and automation on loopback at the given ports, the rest kept' `
            ($simulatorSettings.modbus.port -eq 39021 -and $simulatorSettings.automation.port -eq 39022 -and
             $simulatorSettings.modbus.listenAddress -eq '127.0.0.1' -and $simulatorSettings.automation.listenAddress -eq '127.0.0.1' -and
             $simulatorSettings.modbus.unitId -eq 255) ($simulatorSettings | ConvertTo-Json -Compress)
        $onboardSettings = '{"agvId":"x","ioModule":{"host":"192.168.71.150","port":502,"slots":[]}}' | ConvertFrom-Json
        Set-G3OnboardIoModulePort -Settings $onboardSettings -ModbusPort 39021
        Check 'Set-G3OnboardIoModulePort: the onboard''s IO module at the simulator''s loopback Modbus port' `
            ($onboardSettings.ioModule.host -eq '127.0.0.1' -and $onboardSettings.ioModule.port -eq 39021) ($onboardSettings | ConvertTo-Json -Compress)
    }

    # --- 2. the runners, from source -------------------------------------------------------------------------------
    $runners = [ordered]@{
        'run-staged-g3.ps1' = [ordered]@{
            Ports = [ordered]@{ ControlPort = '58205'; HealthPort = '58207'; ProxyPort = '58215'; BusinessProxyPort = '58216'; SimulatorModbusPort = '1502'; SimulatorHttpPort = '58006' }
            Control = 'control'; Simulator = 'simulator'; OwnerChecked = $true; InProcess = @('$ProxyPort', '$BusinessProxyPort')
        }
        'run-staged-g3-restart.ps1' = [ordered]@{
            Ports = [ordered]@{ ControlPort = '58105'; HealthPort = '58107'; SimulatorModbusPort = '1502'; SimulatorHttpPort = '58006' }
            Control = 'process'; Simulator = 'simulator'; OwnerChecked = $true
        }
        'Invoke-AuthorizedAbsentObservationShadow.ps1' = [ordered]@{
            Ports = [ordered]@{ ProxyPort = '58888'; SessionPort = '59005'; HealthPort = '59007'; SimulatorModbusPort = '1502'; SimulatorHttpPort = '58006' }
            OwnerChecked = $false
        }
    }
    foreach ($file in $runners.Keys) {
        $spec = $runners[$file]
        $ast = Get-RunnerAst (Join-Path $ScriptRoot $file)
        $parameters = @($ast.ParamBlock.Parameters)

        foreach ($name in $spec.Ports.Keys) {
            $parameter = @($parameters | Where-Object { $_.Name.VariablePath.UserPath -eq $name })
            $range = @(${parameter}?[0]?.Attributes | Where-Object { $_.TypeName.Name -eq 'ValidateRange' })
            Check "${file}: -$name, an [int] defaulting to $($spec.Ports[$name]), ValidateRange(1024, 65535)" `
                ($parameter.Count -eq 1 -and "$($parameter[0].DefaultValue.Extent.Text)" -ceq $spec.Ports[$name] -and
                 $parameter[0].StaticType -eq [int] -and $range.Count -eq 1 -and
                 ($range[0].PositionalArguments.Extent.Text -join ',') -eq '1024,65535') `
                "$(${parameter}?[0]?.Extent.Text)"
        }

        # No port number left anywhere but the param block -- and the defaults Get-G3RunnerProvenance compares the
        # parameters with, which Test-G3EvidenceHonesty.ps1 pins to the param block's: every use goes through the
        # parameter.
        $portValues = @($spec.Ports.Values)
        $pattern = '(?<![\d.])(' + ($portValues -join '|') + ')(?![\d.])'
        $provenanceSettings = @(Get-Calls $ast 'Get-G3RunnerProvenance' | ForEach-Object { Get-ParameterArgument $_ 'Settings' } |
                Where-Object { $null -ne $_ })
        $literals = @($ast.FindAll({ param($node)
                    ($node -is [System.Management.Automation.Language.ConstantExpressionAst] -or
                     $node -is [System.Management.Automation.Language.ExpandableStringExpressionAst]) -and
                    $node.Extent.StartOffset -gt $ast.ParamBlock.Extent.EndOffset }, $true) |
                Where-Object { $_.Extent.Text -match $pattern } |
                Where-Object { $literal = $_; @($provenanceSettings | Where-Object {
                            $literal.Extent.StartOffset -ge $_.Extent.StartOffset -and $literal.Extent.EndOffset -le $_.Extent.EndOffset }).Count -eq 0 })
        Check "${file}: no port number outside the param block and the provenance's -Settings defaults" ($literals.Count -eq 0) `
            (($literals | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Extent.Text)" }) -join '; ')

        # One $runPorts table: every port parameter, by name, and nothing else.
        $tables = @($ast.EndBlock.Statements | Where-Object {
                $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $_.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and $_.Left.VariablePath.UserPath -eq 'runPorts' })
        $entries = [ordered]@{}
        $hashtable = if ($tables.Count -eq 1) { @($tables[0].Right.FindAll({ param($n) $n -is [System.Management.Automation.Language.HashtableAst] }, $true))[0] }
        foreach ($pair in @(${hashtable}?.KeyValuePairs)) { if ($null -ne $pair) { $entries[$pair.Item1.Extent.Text] = $pair.Item2.Extent.Text } }
        $expected = @($spec.Ports.Keys | ForEach-Object { "$_=`$$_" }) -join ';'
        $actual = @($entries.Keys | ForEach-Object { "$_=$($entries[$_])" }) -join ';'
        Check "${file}: one top-level [ordered] `$runPorts naming each port parameter" `
            ($tables.Count -eq 1 -and $tables[0].Right.Extent.Text -like '`[ordered`]*' -and $actual -ceq $expected) "$($tables.Count) found: $actual"

        $setCalls = Get-Calls $ast 'Assert-G3RunnerPortSet'
        $lockCalls = Get-Calls $ast 'Enter-G3PortLock'
        $freeCalls = Get-Calls $ast 'Assert-G3PortsFree'
        Check "${file}: Assert-G3RunnerPortSet -Ports `$runPorts, once, at top level, before the run creates anything" `
            ($setCalls.Count -eq 1 -and "$(Get-ParameterArgument $setCalls[0] 'Ports')" -ceq '$runPorts' -and
             $setCalls[0].Parent.Parent -eq $ast.EndBlock) "$($setCalls.Count) call(s)"
        Check "${file}: Enter-G3PortLock -Port `$runPorts.Values, once" `
            ($lockCalls.Count -eq 1 -and "$(Get-ParameterArgument $lockCalls[0] 'Port')" -ceq '$runPorts.Values') "$($lockCalls.Count) call(s)"
        Check "${file}: Assert-G3PortsFree -Ports `$runPorts, once, right after the lock" `
            ($freeCalls.Count -eq 1 -and "$(Get-ParameterArgument $freeCalls[0] 'Ports')" -ceq '$runPorts' -and $lockCalls.Count -eq 1 -and
             $freeCalls[0].Extent.StartOffset -gt $lockCalls[0].Extent.StartOffset) "$($freeCalls.Count) call(s)"
        # The run's own statements, not function bodies: a function defined above the try is called inside it.
        $before = @(foreach ($command in 'Enter-DesktopLock', 'Start-Process', 'New-ExactClone', 'Start-ControlServer', 'Start-ExactPeers') {
                Get-Calls $ast $command | Where-Object { -not (Test-InsideFunction $_) }
            })
        Check "${file}: the port lock and the preflight come before the desktop lock, the first clone and the first process" `
            ($freeCalls.Count -eq 1 -and $before.Count -ge 2 -and
             @($before | Where-Object { $_.Extent.StartOffset -lt $freeCalls[0].Extent.StartOffset }).Count -eq 0) `
            "preflight at line $(${freeCalls}?[0]?.Extent.StartLineNumber); $(($before | ForEach-Object { "$($_.GetCommandName()) at line $($_.Extent.StartLineNumber)" }) -join ', ')"
        $exitPort = @(Get-Calls $ast 'Exit-G3PortLock')
        $exitDesktop = @(Get-Calls $ast 'Exit-DesktopLock')
        Check "${file}: Exit-G3PortLock once, after Exit-DesktopLock" `
            ($exitPort.Count -eq 1 -and $exitDesktop.Count -eq 1 -and $exitPort[0].Extent.StartOffset -gt $exitDesktop[0].Extent.StartOffset) `
            "$($exitPort.Count) Exit-G3PortLock, $($exitDesktop.Count) Exit-DesktopLock"

        $simulatorWrites = Get-Calls $ast 'Set-G3SimulatorPorts'
        Check "${file}: Set-G3SimulatorPorts -ModbusPort `$SimulatorModbusPort -HttpPort `$SimulatorHttpPort" `
            ($simulatorWrites.Count -eq 1 -and "$(Get-ParameterArgument $simulatorWrites[0] 'ModbusPort')" -ceq '$SimulatorModbusPort' -and
             "$(Get-ParameterArgument $simulatorWrites[0] 'HttpPort')" -ceq '$SimulatorHttpPort') "$($simulatorWrites.Count) call(s)"
        $onboardWrites = Get-Calls $ast 'Set-G3OnboardIoModulePort'
        Check "${file}: Set-G3OnboardIoModulePort -ModbusPort `$SimulatorModbusPort" `
            ($onboardWrites.Count -eq 1 -and "$(Get-ParameterArgument $onboardWrites[0] 'ModbusPort')" -ceq '$SimulatorModbusPort') "$($onboardWrites.Count) call(s)"

        # The file the ports go into is the one the simulator reads: simulator.settings.json beside the binary the run
        # starts (MainWindow.xaml.cs reads it from AppContext.BaseDirectory), read into the object Set-G3SimulatorPorts
        # is given and written back from it.
        $text = $ast.Extent.Text
        $binaryDirectories = @([regex]::Matches($text, "Join-Path\s+(\$\w+)\s+'SQCD_8005AGV_Simulator\.(?:dll|exe)'") | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        $configs = @([regex]::Matches($text, "(?m)^\s*(\$\w+)\s*=\s*Join-Path\s+(\$\w+)\s+'simulator\.settings\.json'\s*$"))
        $settingsVariable = if ($simulatorWrites.Count -eq 1) { "$(Get-ParameterArgument $simulatorWrites[0] 'Settings')" }
        $configPath = if ($configs.Count -eq 1) { $configs[0].Groups[1].Value }
        $read = $configPath -and $settingsVariable -and
            ($text -match ("(?m)^\s*" + [regex]::Escape($settingsVariable) + "\s*=\s*(Get-Content\s+-LiteralPath\s+" + [regex]::Escape($configPath) +
                "\s+-Raw|\[IO\.File\]::ReadAllText\(" + [regex]::Escape($configPath) + "\))\s*\|\s*ConvertFrom-Json\s*$"))
        $written = $configPath -and $settingsVariable -and
            ($text -match ("(?m)^\s*" + [regex]::Escape($settingsVariable) + "\s*\|\s*ConvertTo-Json\s+-Depth\s+\d+\s*\|\s*Set-Content\s+-LiteralPath\s+" + [regex]::Escape($configPath) + "\b") -or
             $text -match ("\[IO\.File\]::WriteAllText\(\s*" + [regex]::Escape($configPath) + ",\s*\(" + [regex]::Escape($settingsVariable) + "\s*\|\s*ConvertTo-Json"))
        Check "${file}: the ports go into simulator.settings.json beside the simulator binary it starts, read and written back through the object Set-G3SimulatorPorts gets" `
            ($binaryDirectories.Count -eq 1 -and $configs.Count -eq 1 -and $configs[0].Groups[2].Value -eq $binaryDirectories[0] -and $read -and $written) `
            "binary in $($binaryDirectories -join ', '); $($configs.Count) config path(s): $(($configs | ForEach-Object { $_.Value.Trim() }) -join ' | '); settings $settingsVariable; read $read; written $written"

        # The health URLs are on the parameters: the simulator's on -SimulatorHttpPort, ControlServer's /version on
        # -HealthPort, and nothing else answers either.
        $urls = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.ExpandableStringExpressionAst] }, $true) | ForEach-Object { $_.Value })
        $simulatorUrls = @($urls | Where-Object { $_ -like '*/api/v1/health*' })
        Check "${file}: every simulator health URL is http://127.0.0.1:`$SimulatorHttpPort/api/v1/health" `
            ($simulatorUrls.Count -ge 1 -and @($simulatorUrls | Where-Object { $_ -cne 'http://127.0.0.1:$SimulatorHttpPort/api/v1/health' }).Count -eq 0) ($simulatorUrls -join ' | ')
        if ($spec.OwnerChecked) {
            $versionUrls = @($urls | Where-Object { $_ -like '*/version' })
            Check "${file}: every ControlServer /version URL is on `$HealthPort" `
                ($versionUrls.Count -ge 1 -and @($versionUrls | Where-Object { $_ -notmatch '^http://127\.0\.0\.1:\$HealthPort/version$' }).Count -eq 0) ($versionUrls -join ' | ')
        }

        if ($spec.OwnerChecked) {
            $owners = Get-Calls $ast 'Assert-G3PortOwner'
            # The in-process listeners (the fault proxies) are this process's own: the runner's own process is the owner.
            foreach ($port in @($spec['InProcess'] | Where-Object { $_ })) {
                $matching = @($owners | Where-Object {
                        "$(Get-ParameterArgument $_ 'Process')" -ceq '$runnerProcess' -and "$(Get-ParameterArgument $_ 'Port')" -ceq $port })
                Check "${file}: Assert-G3PortOwner -Process `$runnerProcess -Port $port (an in-process proxy)" ($matching.Count -eq 1) `
                    (($owners | ForEach-Object { $_.Extent.Text }) -join ' | ')
            }
            $runnerProcessAssignments = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$runnerProcess' }, $true))
            if (@($spec['InProcess'] | Where-Object { $_ }).Count -gt 0) {
                Check "${file}: `$runnerProcess is this process (Get-Process -Id `$PID), assigned once" `
                    ($runnerProcessAssignments.Count -eq 1 -and $runnerProcessAssignments[0].Right.Extent.Text -ceq 'Get-Process -Id $PID') `
                    (($runnerProcessAssignments | ForEach-Object { $_.Extent.Text }) -join ' | ')
            }
            foreach ($pair in @(
                    @{ Process = $spec.Simulator; Ports = '$SimulatorHttpPort, $SimulatorModbusPort' },
                    @{ Process = $spec.Control; Ports = '$ControlPort, $HealthPort' })) {
                $matching = @($owners | Where-Object {
                        "$(Get-ParameterArgument $_ 'Process')" -ceq "`$$($pair.Process)" -and "$(Get-ParameterArgument $_ 'Port')" -ceq $pair.Ports })
                Check "${file}: Assert-G3PortOwner -Process `$$($pair.Process) -Port $($pair.Ports)" ($matching.Count -ge 1) `
                    (($owners | ForEach-Object { $_.Extent.Text }) -join ' | ')
            }
        } else {
            Check "${file}: its own Assert-PortFree is gone (Assert-G3PortsFree names the holder)" `
                (@($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-PortFree' }, $true)).Count -eq 0) ''
            $exact = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in 'Wait-ExactListeningPort', 'Assert-ExactListeningPort' }, $true))
            Check "${file}: its exact-owner checks name the holder (Get-G3PortHolderText)" `
                ($exact.Count -eq 2 -and @($exact | Where-Object { $_.Extent.Text -match 'Get-G3PortHolderText' }).Count -eq 2) "$($exact.Count) function(s)"
        }
    }

    # --- 3. the shadow runner releases its desktop lock ------------------------------------------------------------
    # Invoke-AuthorizedAbsentObservationShadow.ps1 takes the desktop lock inside Start-ExactPeers and releases it in
    # the script's finally. Until control-server#809 the function assigned a local $desktopLock, the script-level one
    # stayed $null, and Exit-DesktopLock released nothing: the lock went only when the process exited. Here the
    # function's own lock statement and the finally's release, lifted verbatim, run in that same shape -- a script
    # declaring $desktopLock, a function taking the lock, a plain call, the release -- in a child that stays alive
    # afterwards, so that only the release can free the lock. Enter-/Exit-DesktopLock stand in over a mutex of this
    # check's own: the scope is under test, not DesktopLock.psm1 (Test-DesktopLockQueueing.ps1 has that).
    $shadowFile = 'Invoke-AuthorizedAbsentObservationShadow.ps1'
    $shadowAst = Get-RunnerAst (Join-Path $ScriptRoot $shadowFile)
    $peers = @($shadowAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Start-ExactPeers' }, $true))
    $enters = @(if ($peers.Count -eq 1) { Get-Calls $peers[0] 'Enter-DesktopLock' })
    $lockStatement = if ($enters.Count -eq 1 -and $enters[0].Parent.Parent -is [System.Management.Automation.Language.AssignmentStatementAst]) { $enters[0].Parent.Parent }
    $exits = @(Get-Calls $shadowAst 'Exit-DesktopLock')
    $peerCalls = @(Get-Calls $shadowAst 'Start-ExactPeers' | Where-Object { -not (Test-InsideFunction $_) })
    $declarations = @($shadowAst.EndBlock.Statements | Where-Object {
            $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$desktopLock' })
    Check "${shadowFile}: the lock is taken by one assignment in Start-ExactPeers, which the run calls plainly, and released once" `
        ($null -ne $lockStatement -and $exits.Count -eq 1 -and $peerCalls.Count -eq 1 -and $peerCalls[0].InvocationOperator -eq 'Unknown' -and
         $declarations.Count -eq 1) "$($enters.Count) Enter-DesktopLock in Start-ExactPeers, $($exits.Count) Exit-DesktopLock, $($peerCalls.Count) call(s), $($declarations.Count) declaration(s)"
    if ($null -ne $lockStatement -and $exits.Count -eq 1 -and $declarations.Count -eq 1) {
        $lockName = "Global\W2G-SelfCheck-ShadowDesktopLock-$([guid]::NewGuid().ToString('n'))"
        $released = Join-Path $work 'shadow-released'
        $done = Join-Path $work 'shadow-done'
        $harness = @"
`$selfCheckLockName = '$lockName'
function Enter-DesktopLock { param([string]`$Reason) `$m = [Threading.Mutex]::new(`$false, `$selfCheckLockName); `$null = `$m.WaitOne(0); [pscustomobject]@{ Mutex = `$m } }
function Exit-DesktopLock { param(`$Handle) if (`$null -eq `$Handle) { return }; `$Handle.Mutex.ReleaseMutex(); `$Handle.Mutex.Dispose() }
`$stamp = 'self-check'
$($declarations[0].Extent.Text)
function Start-ExactPeers {
    $($lockStatement.Extent.Text)
}
Start-ExactPeers
$($exits[0].Parent.Extent.Text)
Set-Content -LiteralPath '$released' -Value released
Set-Content -LiteralPath '__MARKER__' -Value ready
while (-not (Test-Path -LiteralPath '$done')) { Start-Sleep -Milliseconds 100 }
"@
        $shadowChild = Start-Child 'shadow-desktop-lock' $harness
        $probeMutex = [Threading.Mutex]::new($false, $lockName)
        $free = $false
        try { $free = $probeMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $free = $true }
        if ($free) { $probeMutex.ReleaseMutex() }
        $probeMutex.Dispose()
        Set-Content -LiteralPath $done -Value done
        $null = $shadowChild.WaitForExit(10000)
        Check "${shadowFile}: after its finally's Exit-DesktopLock, with the process still alive, another process takes the desktop lock at once" `
            $free "the lock is still held: Start-ExactPeers' $($lockStatement.Extent.Text) left the script's `$desktopLock `$null"
    }
}
finally {
    foreach ($child in $children) {
        if (-not $child.HasExited) { Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue }
    }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed."
    exit 1
}
Write-Host 'All G3 runner port checks passed.'
exit 0

} finally {
    # exit inside the try still runs this, on the thread that took the mutex.
    $selfCheckLock.ReleaseMutex()
    $selfCheckLock.Dispose()
}
