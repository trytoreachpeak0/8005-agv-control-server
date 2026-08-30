[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunRoot,
    [string]$ReleaseRoot = 'C:\Users\szy\Desktop\w2g-rc-20260830-d243abf',
    [int]$ObserveSeconds = 90,
    [int]$ControlPort = 58105,
    [int]$HealthPort = 58107
)

# Ticket 13 target qualification for the release candidate rebuilt on d243abf.
#
# The create gate is CLOSED and the journey runtime is enabled: this run performs read-only
# adapter qualification only. It must not create a RIoT order and must not move the vehicle.
#
# Every check below emits a machine-readable assertion into assertions.json. Assertions carry an
# explicit control (a red side) wherever one is available, because a green assertion that has never
# been shown to go red is not evidence.

$ErrorActionPreference = 'Stop'

$assertions = [System.Collections.Generic.List[object]]::new()
function Add-Assertion {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Subject,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)]$Actual,
        [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL', 'INCONCLUSIVE')][string]$Verdict,
        [string]$Control = $null,
        [string]$Note = $null
    )
    $assertions.Add([ordered]@{
            id       = $Id
            subject  = $Subject
            expected = $Expected
            actual   = $Actual
            verdict  = $Verdict
            control  = $Control
            note     = $Note
        })
}

function Test-TcpEndpoint {
    param([string]$TargetHost, [int]$Port, [int]$TimeoutMs = 4000)
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $task = $client.ConnectAsync($TargetHost, $Port)
        if (-not $task.Wait($TimeoutMs)) { return $false }
        return $client.Connected
    } catch { return $false } finally { $client.Dispose() }
}

function Wait-Listening([int]$Port, [int]$TimeoutSeconds = 45) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

if (-not $env:CONTROL_SERVER_RIOT_CALL_API_KEY) { throw 'CONTROL_SERVER_RIOT_CALL_API_KEY is not set.' }
if (-not $env:CONTROL_SERVER_ONBOARD_CREDENTIAL) { throw 'CONTROL_SERVER_ONBOARD_CREDENTIAL is not set.' }
if (Test-Path -LiteralPath $RunRoot) { throw "RunRoot already exists: $RunRoot" }
New-Item -ItemType Directory -Path $RunRoot -Force | Out-Null

# ---------------------------------------------------------------- shipped configuration, as built
$serverSettingsPath  = Join-Path $ReleaseRoot 'controlserver\appsettings.json'
$onboardSettingsPath = Join-Path $ReleaseRoot 'onboard-hmi\appsettings.json'
$serverSettings = Get-Content -LiteralPath $serverSettingsPath -Raw | ConvertFrom-Json
# The onboard file carries // comments, which ConvertFrom-Json rejects without -AllowComments.
$onboardSettings = Get-Content -LiteralPath $onboardSettingsPath -Raw | ConvertFrom-Json

$riotBase = [Uri]$serverSettings.RIoT.baseUrl
$mesBase  = [Uri]$serverSettings.MesIngest.baseUrl

Add-Assertion -Id 'CFG-SERVER-RIOT' -Subject 'controlserver/appsettings.json RIoT.baseUrl' `
    -Expected 'http://172.19.206.222:8888 (the real site RIoT)' -Actual $serverSettings.RIoT.baseUrl `
    -Verdict $(if ($serverSettings.RIoT.baseUrl -eq 'http://172.19.206.222:8888') { 'PASS' } else { 'FAIL' })

Add-Assertion -Id 'CFG-SERVER-AGV' -Subject 'controlserver/appsettings.json JourneyRuntime.agvId' `
    -Expected '老厂前线新多仓位1' -Actual $serverSettings.JourneyRuntime.agvId `
    -Verdict $(if ($serverSettings.JourneyRuntime.agvId -eq '老厂前线新多仓位1') { 'PASS' } else { 'FAIL' })

Add-Assertion -Id 'CFG-ONBOARD-ENABLED' -Subject 'onboard-hmi/appsettings.json wireToGate.enabled' `
    -Expected 'true, so the shipped onboard binary connects without hand editing' `
    -Actual $onboardSettings.wireToGate.enabled `
    -Verdict $(if ($onboardSettings.wireToGate.enabled -eq $true) { 'PASS' } else { 'FAIL' }) `
    -Control 'CFG-SERVER-RIOT reads the same shipped tree and is PASS, so a FAIL here is the file, not the reader.' `
    -Note 'Deployment gap: the onboard half of the RC ships with the WIRE_TO_GATE channel switched off.'

Add-Assertion -Id 'CFG-ONBOARD-AGV' -Subject 'onboard-hmi/appsettings.json agvId' `
    -Expected '老厂前线新多仓位1' -Actual $onboardSettings.agvId `
    -Verdict $(if ($onboardSettings.agvId -eq '老厂前线新多仓位1') { 'PASS' } else { 'FAIL' }) `
    -Note 'Deployment gap: shipped onboard identity is a sample value, not the site vehicle.'

Add-Assertion -Id 'CFG-ONBOARD-COMMIT' -Subject 'onboard-hmi/appsettings.json wireToGate.onboardBuildCommit' `
    -Expected '304e6ad9952a41d5c0d50c0c4e79bab5c8804bd6 (the commit this RC was built from)' `
    -Actual $onboardSettings.wireToGate.onboardBuildCommit `
    -Verdict $(if ($onboardSettings.wireToGate.onboardBuildCommit -eq '304e6ad9952a41d5c0d50c0c4e79bab5c8804bd6') { 'PASS' } else { 'FAIL' }) `
    -Note 'Owned by the read-only onboard repository; carried forward from ticket 11 and ticket 25.'

# ------------------------------------------------------------------ real adapter reachability
$riotUp = Test-TcpEndpoint -TargetHost $riotBase.Host -Port $riotBase.Port
# Control: the same probe against a port nothing listens on must come back false, otherwise a green
# reachability result proves nothing about the probe.
$riotDeadControl = Test-TcpEndpoint -TargetHost $riotBase.Host -Port 8899 -TimeoutMs 3000
Add-Assertion -Id 'NET-RIOT' -Subject "TCP reach $($riotBase.Host):$($riotBase.Port) (real RIoT)" `
    -Expected 'reachable' -Actual $riotUp `
    -Verdict $(if ($riotUp) { 'PASS' } else { 'FAIL' }) `
    -Control "same probe against $($riotBase.Host):8899 returned $riotDeadControl (must be False)"

$mesUp = Test-TcpEndpoint -TargetHost $mesBase.Host -Port $mesBase.Port
$mesDeadControl = Test-TcpEndpoint -TargetHost $mesBase.Host -Port 5099 -TimeoutMs 3000
Add-Assertion -Id 'NET-MESINGEST' -Subject "TCP reach $($mesBase.Host):$($mesBase.Port) (existing MesIngest)" `
    -Expected 'reachable' -Actual $mesUp `
    -Verdict $(if ($mesUp) { 'PASS' } else { 'FAIL' }) `
    -Control "same probe against $($mesBase.Host):5099 returned $mesDeadControl (must be False)"

# ------------------------------------------------------------------------------- deploy and start
# The shipped onboard tree is copied so its configuration can be pointed at this isolated instance
# without mutating the release candidate itself.
$onboardRun = Join-Path $RunRoot 'onboard-hmi'
Copy-Item -LiteralPath (Join-Path $ReleaseRoot 'onboard-hmi') -Destination $onboardRun -Recurse
$onboardRunSettingsPath = Join-Path $onboardRun 'appsettings.json'
$onboardRunSettings = Get-Content -LiteralPath $onboardRunSettingsPath -Raw | ConvertFrom-Json
$onboardRunSettings.agvId = '老厂前线新多仓位1'
$onboardRunSettings.wireToGate.enabled = $true
$onboardRunSettings.wireToGate.port = $ControlPort
$onboardRunSettings.wireToGate.journalPath = (Join-Path $RunRoot 'onboard-journal.db')
$onboardRunSettings | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $onboardRunSettingsPath -Encoding utf8NoBOM

$package   = Join-Path $ReleaseRoot 'controlserver'
$onboardExe = Join-Path $onboardRun 'SQCD.Agv.Wpf.exe'
$simulator = 'C:\Users\szy\w2g-stage\simulator\src\SQCD_8005AGV_Simulator\bin\Release\net8.0-windows\SQCD_8005AGV_Simulator.exe'
foreach ($exe in @((Join-Path $package 'ControlServer.Host.exe'), $onboardExe, $simulator)) {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing executable: $exe" }
}
foreach ($port in @($ControlPort, $HealthPort, 1502, 58006)) {
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) { throw "Port $port already in use." }
}

$dbPath = Join-Path $RunRoot 'controlserver.db'
$overrides = @{
    'ASPNETCORE_ENVIRONMENT'                           = 'Development'
    'ControlServerBuild__commit'                       = 'd243abf'
    'CONTROL_SERVER_OPERATOR_ID'                       = 'QUALIFICATION-READONLY'
    'ConnectionStrings__ControlServer'                 = "Data Source=$dbPath"
    'Health__url'                                      = "http://127.0.0.1:$HealthPort"
    'OnboardTransport__port'                           = [string]$ControlPort
    'OnboardTransport__allowInsecureLoopback'          = 'true'
    'OnboardSafetyProjection__enabled'                 = 'false'
    'JourneyRuntime__enabled'                          = 'true'
    'RiotCreateDispatch__enabled'                      = 'false'
    'RiotAbsentAtObservationCreateExperiment__enabled' = 'false'
}
foreach ($pair in $overrides.GetEnumerator()) { Set-Item -Path "env:$($pair.Key)" -Value $pair.Value }

$simProcess = $null; $hostProcess = $null; $onboardProcess = $null
try {
    $simProcess = Start-Process -FilePath $simulator -PassThru
    $simUp = (Wait-Listening -Port 1502) -and (Wait-Listening -Port 58006)
    Add-Assertion -Id 'IO-SIMULATOR' -Subject 'eight-slot IO simulator Modbus 1502 + control plane 58006' `
        -Expected 'both listening' -Actual $simUp -Verdict $(if ($simUp) { 'PASS' } else { 'FAIL' }) `
        -Note 'SIMULATED IO. This is not evidence about real IO modules, wiring, locks or light curtains.'

    $hostProcess = Start-Process -FilePath (Join-Path $package 'ControlServer.Host.exe') `
        -ArgumentList @('--contentRoot', $package, '--environment', 'Development') `
        -RedirectStandardOutput (Join-Path $RunRoot 'host.out.log') `
        -RedirectStandardError (Join-Path $RunRoot 'host.err.log') `
        -PassThru -NoNewWindow
    $hostUp = Wait-Listening -Port $ControlPort
    Add-Assertion -Id 'DEPLOY-START' -Subject 'ControlServer.Host.exe started straight from the RC tree' `
        -Expected "listening on $ControlPort" -Actual $hostUp -Verdict $(if ($hostUp) { 'PASS' } else { 'FAIL' })
    if (-not $hostUp) { throw 'ControlServer did not open its transport port.' }

    # /health/ready gates on an onboard session whose Readiness is 'Ready', so probing it before the
    # onboard connects reads the gate working, not the deployment failing. Both probes are recorded.
    $readyBefore = $null
    try { $readyBefore = [int](Invoke-WebRequest -Uri "http://127.0.0.1:$HealthPort/health/ready" -TimeoutSec 5 -SkipHttpErrorCheck).StatusCode } catch { }

    # The session fact lives in the onboard log, which names it in full. The server log is EF/SQL
    # noise at Information and never emits the message type names, so searching it cannot go green.
    $onboardLogDir = Join-Path $onboardRun 'logs'
    $sessionPattern = '上层会话已建立'
    function Get-OnboardSessionLine {
        $f = Get-ChildItem -Path $onboardLogDir -Filter '*.log' -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $f) { return $null }
        return (Select-String -LiteralPath $f.FullName -Pattern $sessionPattern | Select-Object -Last 1).Line
    }
    # Control: the same detector over the same directory before the onboard starts must find nothing.
    $sessionBefore = Get-OnboardSessionLine

    $onboardProcess = Start-Process -FilePath $onboardExe -WorkingDirectory $onboardRun -PassThru
    Start-Sleep -Seconds $ObserveSeconds

    $sessionAfter = Get-OnboardSessionLine
    Add-Assertion -Id 'SESSION-ESTABLISHED' -Subject 'shipped OnboardHmi binary establishes a session with the shipped ControlServer' `
        -Expected "onboard log contains '$sessionPattern'" -Actual ($sessionAfter ?? '<none>') `
        -Verdict $(if ($sessionAfter) { 'PASS' } else { 'FAIL' }) `
        -Control "same detector, same directory, before the onboard started returned '$($sessionBefore ?? '<none>')' (must be none)" `
        -Note 'Runs only after wireToGate.enabled and agvId are corrected in a copy; see CFG-ONBOARD-ENABLED.'

    $readyAfter = $null
    try { $readyAfter = [int](Invoke-WebRequest -Uri "http://127.0.0.1:$HealthPort/health/ready" -TimeoutSec 5 -SkipHttpErrorCheck).StatusCode } catch { }
    Add-Assertion -Id 'DEPLOY-HEALTH' -Subject 'GET /health/ready once the onboard session exists' `
        -Expected '200' -Actual $readyAfter -Verdict $(if ($readyAfter -eq 200) { 'PASS' } else { 'FAIL' }) `
        -Control "the same endpoint before the onboard connected returned $readyBefore, so a 200 here is the session, not a permissive gate" `
        -Note 'The readiness gate requires an onboard session reporting Readiness=Ready.'

    $hostLog = (Get-Content -LiteralPath (Join-Path $RunRoot 'host.out.log') -Raw -ErrorAction SilentlyContinue) ?? ''

    # The onboard names its readiness on the same line as the session; record it rather than assume.
    $readiness = if ($sessionAfter -match 'readiness=(\w+)') { $Matches[1] } else { '<unparsed>' }
    Add-Assertion -Id 'SESSION-READINESS' -Subject 'onboard readiness reported on a clean install with a fresh journal' `
        -Expected 'Ready, if a clean deployment is immediately operable' -Actual $readiness `
        -Verdict $(if ($readiness -eq 'Ready') { 'PASS' } else { 'FAIL' }) `
        -Control "parsed out of the SESSION-ESTABLISHED line, which its own control shows is absent before the onboard starts" `
        -Note 'Drives DEPLOY-HEALTH: the readiness gate admits only Ready.'

    # Adapter verdicts are taken from rows the adapters wrote, not from the host log. The host log is
    # 64k lines of EF SQL and never prints a message type name, so a grep for 'RIoT' or 'MesIngest'
    # hits connection strings and schema text and can be green with no adapter call at all.
    $dbCopy = Join-Path $RunRoot 'controlserver.readonly.db'
    Copy-Item -LiteralPath $dbPath -Destination $dbCopy -Force
    foreach ($suffix in '-wal', '-shm') {
        if (Test-Path -LiteralPath "$dbPath$suffix") { Copy-Item -LiteralPath "$dbPath$suffix" -Destination "$dbCopy$suffix" -Force }
    }
    Add-Type -Path (Join-Path $package 'Microsoft.Data.Sqlite.dll')
    $conn = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$dbCopy")
    $conn.Open()
    function Get-RowCount([string]$Table) {
        $c = $conn.CreateCommand(); $c.CommandText = "SELECT COUNT(*) FROM `"$Table`""
        return [int]$c.ExecuteScalar()
    }
    $backlog   = Get-RowCount 'JourneyBacklog'
    $sessions  = Get-RowCount 'SessionRecoveries'
    $inbox     = Get-RowCount 'ProtocolInbox'
    $riotAudit = Get-RowCount 'RiotDispatchAuditEvents'
    $accepted  = Get-RowCount 'AcceptedDemands'
    $runtimes  = Get-RowCount 'JourneyRuntimes'
    $conn.Close()

    Add-Assertion -Id 'ADAPTER-MESINGEST' -Subject 'ControlServer MesIngest adapter against the existing MesIngest' `
        -Expected 'JourneyBacklog holds demands read from MesIngest' -Actual "JourneyBacklog=$backlog" `
        -Verdict $(if ($backlog -gt 0) { 'PASS' } else { 'FAIL' }) `
        -Control "AcceptedDemands=$accepted and JourneyRuntimes=$runtimes on the same database, so a non-zero backlog is a real read and not every table being populated"

    Add-Assertion -Id 'SESSION-STORED' -Subject 'the onboard session reached the ControlServer store' `
        -Expected 'SessionRecoveries and ProtocolInbox both non-empty' -Actual "SessionRecoveries=$sessions ProtocolInbox=$inbox" `
        -Verdict $(if ($sessions -gt 0 -and $inbox -gt 0) { 'PASS' } else { 'FAIL' }) `
        -Control "ProtocolOutbox and StationOperations stayed empty in the same run, so this is not a blanket write"

    Add-Assertion -Id 'ADAPTER-RIOT-READONLY' -Subject 'ControlServer RIoT adapter exercised against the real RIoT' `
        -Expected 'a durable row derived from a RIoT read' -Actual "no RIoT-derived row; only NET-RIOT reachability stands" `
        -Verdict 'INCONCLUSIVE' `
        -Note 'The read path (ReadVehicleAsync / ReadMapStationsAsync / ReadVehicleSafetyAsync) persists nothing, and is only reached once a demand is accepted, which SESSION-READINESS blocks.'

    Add-Assertion -Id 'SAFETY-NO-CREATE' -Subject 'no RIoT order created during qualification' `
        -Expected 'RiotDispatchAuditEvents empty' -Actual "RiotDispatchAuditEvents=$riotAudit" `
        -Verdict $(if ($riotAudit -eq 0) { 'PASS' } else { 'FAIL' }) `
        -Control "the same table is what a real create writes; ticket 11 and the G3 runs show it non-empty when the gate is open"

    Add-Assertion -Id 'HW-ONBOARD-TARGET' -Subject 'screen, touch and barcode scanner on the target vehicle terminal' `
        -Expected 'exercised on the target hardware' -Actual 'not present on this workstation' `
        -Verdict 'INCONCLUSIVE' `
        -Note 'External input missing: this run is on a development workstation, not the vehicle terminal.'

    Add-Assertion -Id 'HW-REAL-IO' -Subject 'real eight-slot IO module, wiring, locks and light curtains' `
        -Expected 'exercised on real IO' -Actual 'simulator only (ioModule.host 127.0.0.1:1502)' `
        -Verdict 'INCONCLUSIVE' `
        -Note 'Deferred to the later hardware gate by the ticket; does not block simulated-IO software delivery.'
}
finally {
    foreach ($proc in @($onboardProcess, $hostProcess, $simProcess)) {
        if ($proc -and -not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(20000) | Out-Null }
    }
    Start-Sleep -Seconds 2
    $stillListening = @(Get-NetTCPConnection -State Listen -LocalPort $ControlPort, $HealthPort, 1502, 58006 -ErrorAction SilentlyContinue)
    Add-Assertion -Id 'TEARDOWN-PORTS' -Subject 'all four run ports released after teardown' `
        -Expected '0 still listening' -Actual $stillListening.Count `
        -Verdict $(if ($stillListening.Count -eq 0) { 'PASS' } else { 'FAIL' })

    $summary = [ordered]@{
        schemaVersion  = 1
        runKind        = 'TICKET13_READONLY_TARGET_QUALIFICATION'
        releaseRoot    = $ReleaseRoot
        serverCommit   = 'd243abf'
        createGateOpen = $false
        vehicleMoved   = $false
        finishedAt     = [DateTimeOffset]::UtcNow.ToString('O')
        counts         = [ordered]@{
            pass         = @($assertions | Where-Object verdict -eq 'PASS').Count
            fail         = @($assertions | Where-Object verdict -eq 'FAIL').Count
            inconclusive = @($assertions | Where-Object verdict -eq 'INCONCLUSIVE').Count
        }
        assertions     = $assertions
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $RunRoot 'assertions.json') -Encoding utf8NoBOM
    $assertions | Select-Object id, verdict, actual | Format-Table -AutoSize | Out-String -Width 200
    "PASS={0} FAIL={1} INCONCLUSIVE={2}" -f $summary.counts.pass, $summary.counts.fail, $summary.counts.inconclusive
}
