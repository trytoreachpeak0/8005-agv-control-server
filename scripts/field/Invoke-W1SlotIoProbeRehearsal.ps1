#Requires -Version 7
<#
.SYNOPSIS
    Rehearses the W1 field day on the control host: the slot IO probe against a local slots simulator,
    then Invoke-W1FieldWindow.ps1 over the probe's records against a freshly migrated database.

.DESCRIPTION
    Not evidence and not a gate. The simulator stands in for the IO module and a script plays the
    person at the vehicle, which is exactly what REQ-0263 says a field pass must never be; what this
    shows is that the tooling carried to the plant does what it says -- the Modbus exchange, the
    derivation of each boolean, the record FieldOps accepts, and the window script run the way it will
    run on the factory server (prebuilt FieldOps, commit passed in, no repository).

      R-00      bind-io leaves three vehicles SLOT_CONFIGURATION_NEVER_VERIFIED
      R-01      the read-only module check passes an idle, closed, empty module
      R-11..13  three vehicles probed, every slot passes open, close and in-place
      R-21      slot 3 with lock feedback stuck at locked: open fails, close and in-place pass
      R-22      slot 5 where the person says it was not that door: open fails, the rest pass
      R-23      the other six slots of that run are unaffected
      R-30      the W1 window over the three passing records is PASS
      R-31      the evidence carries the probe's raw records
      R-32      the Chinese agvId survives into the verdict

    Needs an interactive desktop: the simulator is a WPF application.

.EXAMPLE
    pwsh -File scripts/field/Invoke-W1SlotIoProbeRehearsal.ps1 -StageRoot $env:TEMP\w1-rehearsal-01 `
        -FieldOpsExecutable <self-contained publish>\ControlServer.FieldOps.exe
#>
[CmdletBinding()]
param(
    # Must not exist.
    [Parameter(Mandatory)][string]$StageRoot,

    # A self-contained publish, the same kind that goes to the factory server.
    [Parameter(Mandatory)][string]$FieldOpsExecutable,

    # Defaults to the newest simulator the L2 rig published under %LOCALAPPDATA%\8005-l2-peers.
    [string]$SimulatorPublish,

    [string]$Repository = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [int]$ModbusPort = 58512,
    [int]$HttpPort = 58511
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

if (Test-Path -LiteralPath $StageRoot) {
    throw "StageRoot already exists: $StageRoot"
}
if (-not (Test-Path -LiteralPath $FieldOpsExecutable -PathType Leaf)) {
    throw "No FieldOps executable at $FieldOpsExecutable"
}
if (-not $SimulatorPublish) {
    $SimulatorPublish = Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA '8005-l2-peers') -Directory -Filter 'slots-simulator-*' |
        Sort-Object LastWriteTime -Descending |
        ForEach-Object { Join-Path $_.FullName 'publish' } |
        Where-Object { Test-Path -LiteralPath (Join-Path $_ 'SQCD_8005AGV_Simulator.exe') } |
        Select-Object -First 1
    if (-not $SimulatorPublish) {
        throw 'No published slots simulator under %LOCALAPPDATA%\8005-l2-peers; pass -SimulatorPublish.'
    }
}
$Repository = (Resolve-Path -LiteralPath $Repository).Path
$hostDirectory = Join-Path $Repository 'src/ControlServer.Host/bin/Release/net8.0/win-x64'
if (-not (Test-Path -LiteralPath (Join-Path $hostDirectory 'ControlServer.Host.exe'))) {
    throw "Build ControlServer.Host in Release first: $hostDirectory"
}

New-Item -ItemType Directory -Path $StageRoot | Out-Null
$StageRoot = (Resolve-Path -LiteralPath $StageRoot).Path
$logs = Join-Path $StageRoot 'logs'
New-Item -ItemType Directory -Path $logs | Out-Null

$results = [System.Collections.Generic.List[object]]::new()
$expectedAssertions = 11
function Assert-Rehearsal {
    param([string]$Id, [string]$Description, [bool]$Passed, [string]$Actual)

    $results.Add([ordered]@{ id = $Id; description = $Description; outcome = $Passed ? 'PASS' : 'FAIL'; actual = $Actual })
    Write-Host ('{0} {1} {2} -- {3}' -f ($Passed ? 'PASS' : 'FAIL'), $Id, $Description, $Actual)
}

# --- simulator -----------------------------------------------------------------------------------

$simulatorDirectory = Join-Path $StageRoot 'simulator'
Copy-Item -LiteralPath $SimulatorPublish -Destination $simulatorDirectory -Recurse
$settingsPath = Join-Path $simulatorDirectory 'simulator.settings.json'
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$settings.instanceId = 'w1-probe-rehearsal'
$settings.modbus.listenAddress = '127.0.0.1'
$settings.modbus.port = $ModbusPort
$settings.automation.listenAddress = '127.0.0.1'
$settings.automation.port = $HttpPort
$settings | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $settingsPath -Encoding utf8NoBOM

$simulatorBase = "http://127.0.0.1:$HttpPort/api/v1"

function Get-Simulator {
    param([Parameter(Mandatory)][string]$Path)
    Invoke-RestMethod -Uri "$simulatorBase$Path" -TimeoutSec 10 -NoProxy
}

function Send-Simulator {
    param([Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Path, [hashtable]$Body = @{})

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        $snapshot = Get-Simulator -Path '/snapshot'
        $payload = @{} + $Body
        $payload.runId = $snapshot.runId
        $payload.expectedRevision = $snapshot.revision
        $payload.commandId = [guid]::NewGuid().ToString('D')
        $response = Invoke-WebRequest -Uri "$simulatorBase$Path" -Method $Method -Body ($payload | ConvertTo-Json -Compress) `
            -ContentType 'application/json' -SkipHttpErrorCheck -TimeoutSec 10 -NoProxy
        if ([int]$response.StatusCode -lt 300) { return }
        $reason = $null
        try { $reason = ($response.Content | ConvertFrom-Json).reasonCode } catch { }
        if ([int]$response.StatusCode -eq 409 -and $reason -eq 'REVISION_CONFLICT') { continue }
        throw "simulator $Method $Path -> $([int]$response.StatusCode) $($response.Content)"
    }
    throw "simulator $Method $Path kept failing on REVISION_CONFLICT"
}

function Wait-Until {
    param([Parameter(Mandatory)][scriptblock]$Condition, [int]$TimeoutMs = 5000)

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt $TimeoutMs) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 100
    }
    $false
}

# The person at the vehicle, played against the simulator. It answers door-opened from what the
# simulator's door actually did, except where the plan says the person saw something else.
$rehearsalAlias = $null
$rehearsalFaults = @{}
$person = {
    param($PromptId, $SlotNumber, $Text)

    switch ($PromptId) {
        'vehicle-safe' { return $rehearsalAlias }
        'slot-ready' {
            if ($rehearsalFaults[$SlotNumber] -eq 'stuck-lock') {
                Send-Simulator -Method PUT -Path "/slots/$SlotNumber/lock-feedback-override" -Body @{ mode = 'FIXED_1' }
            }
            return ''
        }
        'door-opened' {
            if ($rehearsalFaults[$SlotNumber] -eq 'wrong-door') { return 'n' }
            $open = @((Get-Simulator -Path '/snapshot').slots | Where-Object { $_.doorState -ne 'CLOSED' })
            return ($open.Count -eq 1 -and $open[0].slotNo -eq $SlotNumber) ? 'y' : 'n'
        }
        'object-in' {
            Send-Simulator -Method PUT -Path "/slots/$SlotNumber/cargo" -Body @{ state = 'OCCUPIED' }
            return ''
        }
        'object-out' {
            Send-Simulator -Method PUT -Path "/slots/$SlotNumber/cargo" -Body @{ state = 'EMPTY' }
            return ''
        }
        'door-closed' {
            if ($rehearsalFaults[$SlotNumber] -eq 'stuck-lock') {
                Send-Simulator -Method PUT -Path "/slots/$SlotNumber/lock-feedback-override" -Body @{ mode = 'AUTO' }
            }
            Send-Simulator -Method POST -Path "/slots/$SlotNumber/close-door"
            return ''
        }
        default { throw "Unexpected prompt $PromptId" }
    }
}

$simulatorProcess = $null
try {
    $simulatorProcess = Start-Process -FilePath (Join-Path $simulatorDirectory 'SQCD_8005AGV_Simulator.exe') `
        -WorkingDirectory $simulatorDirectory -WindowStyle Normal -PassThru `
        -RedirectStandardOutput (Join-Path $logs 'simulator.out.log') -RedirectStandardError (Join-Path $logs 'simulator.err.log')
    $ready = Wait-Until -TimeoutMs 120000 -Condition {
        try { $health = Get-Simulator -Path '/health'; $health.status -eq 'READY' -and $health.modbus.isRunning } catch { $false }
    }
    if (-not $ready) { throw 'The slots simulator did not become READY within 120 s.' }

    # --- database ---------------------------------------------------------------------------------

    # The server's own package-capacity import migrates a fresh database and exits without listening,
    # which is how the L2 rig gets its database before the server starts.
    $databaseDirectory = Join-Path $StageRoot 'db'
    New-Item -ItemType Directory -Path $databaseDirectory | Out-Null
    $database = Join-Path $databaseDirectory 'controlserver.db'
    $capacityCsv = Join-Path $databaseDirectory 'package-capacity.csv'
    @(
        'pattern,match_type,max_boxes_per_basket,source,status,note'
        'W1-REHEARSAL,exact,4,w1-rehearsal,active,W1 probe rehearsal fixture'
    ) | Set-Content -LiteralPath $capacityCsv -Encoding utf8NoBOM
    $hostEnvironment = @{
        'CONTROL_SERVER_ONBOARD_CREDENTIAL'          = [guid]::NewGuid().ToString('N')
        'ConnectionStrings__ControlServer'           = "Data Source=$database"
        'Health__url'                                = 'http://127.0.0.1:58597'
        'OnboardTransport__listenAddress'            = '127.0.0.1'
        'OnboardTransport__port'                     = '58595'
        'MesIngest__baseUrl'                         = 'http://127.0.0.1:9'
        'RIoT__baseUrl'                              = 'http://127.0.0.1:9'
        'RIoT__callApiKeyEnvironmentVariable'        = 'CONTROL_SERVER_W1_REHEARSAL_DUMMY_RIOT_CALL_API_KEY'
        'CONTROL_SERVER_W1_REHEARSAL_DUMMY_RIOT_CALL_API_KEY' = 'w1-rehearsal-not-a-production-secret'
        'JourneyRuntime__enabled'                    = 'false'
    }
    $import = Start-Process -FilePath (Join-Path $hostDirectory 'ControlServer.Host.exe') `
        -ArgumentList @('--import-package-capacity', '--input', $capacityCsv, '--version', '1') `
        -WorkingDirectory $hostDirectory -Environment $hostEnvironment -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $logs 'db-import.out.log') -RedirectStandardError (Join-Path $logs 'db-import.err.log')
    $null = $import.Handle
    if (-not $import.WaitForExit(180000)) {
        $import.Kill($true)
        throw 'Database migration through --import-package-capacity timed out.'
    }
    if ($import.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $database)) {
        throw "Database migration through --import-package-capacity failed with exit code $($import.ExitCode); see logs/db-import.*.log."
    }

    function Invoke-FieldOpsCommand {
        param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$LogName)

        $output = & $FieldOpsExecutable @Arguments 2> (Join-Path $logs "$LogName.err.log")
        $exit = $LASTEXITCODE
        $output | Set-Content -LiteralPath (Join-Path $logs "$LogName.out.json") -Encoding utf8NoBOM
        if ($exit -ne 0) { throw "FieldOps $($Arguments[0]) exited $exit; see logs/$LogName.*" }
        ($output | Select-Object -Last 1) | ConvertFrom-Json
    }

    $vehicles = @(
        [ordered]@{ Order = 1; Alias = 'agv01'; AgvId = '老厂前线新多仓位1' }
        [ordered]@{ Order = 2; Alias = 'agv02'; AgvId = '老厂前线新多仓位2' }
        [ordered]@{ Order = 3; Alias = 'agv03'; AgvId = '老厂前线新多仓位3' }
    )
    $seed = Invoke-FieldOpsCommand -Arguments @('seed-approved-facts', '--database', $database) -LogName 'seed'
    foreach ($vehicle in $vehicles) {
        $null = Invoke-FieldOpsCommand -Arguments @('bind-io', '--database', $database, '--agv', $vehicle.AgvId) -LogName "bind-$($vehicle.Alias)"
    }
    $status = Invoke-FieldOpsCommand -Arguments @('status', '--database', $database) -LogName 'status-after-bind'
    Assert-Rehearsal -Id 'R-00' -Description 'bind-io 之后三台车都是 SLOT_CONFIGURATION_NEVER_VERIFIED' `
        -Passed (@($status.vehicles | Where-Object { $_.reasonCode -eq 'SLOT_CONFIGURATION_NEVER_VERIFIED' }).Count -eq 3) `
        -Actual ((@($status.vehicles) | ForEach-Object { "$($_.agvId)=$($_.reasonCode)" }) -join '，')

    # --- probe --------------------------------------------------------------------------------------

    $probe = Join-Path $PSScriptRoot 'Invoke-W1SlotIoProbe.ps1'
    $records = Join-Path $StageRoot 'records'
    $probeArguments = @{
        Local              = $true
        Port               = $ModbusPort
        SlotModelVersionId = $seed.slotModelVersionId
        VerifiedBy         = 'W1 rehearsal (scripted person)'
        PhotoPointers      = @('rehearsal-no-photo')
        Responder          = $person
    }

    Send-Simulator -Method POST -Path '/reset'
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-W1SlotIoModule.ps1') -Local -Port $ModbusPort *> (Join-Path $logs 'module-check.log')
    $checkExit = $LASTEXITCODE
    Assert-Rehearsal -Id 'R-01' -Description '只读自检：输出空闲、八仓锁闭且无物的模块判为可以开始' -Passed ($checkExit -eq 0) `
        -Actual "exit $checkExit；$((Get-Content -LiteralPath (Join-Path $logs 'module-check.log') | Select-Object -Last 1))"

    foreach ($vehicle in $vehicles) {
        Send-Simulator -Method POST -Path '/reset'
        $rehearsalAlias = $vehicle.Alias
        $rehearsalFaults = @{}
        & $probe @probeArguments -SiteAlias $vehicle.Alias -Order $vehicle.Order -AgvId $vehicle.AgvId -RecordDirectory $records
        $record = Get-Content -LiteralPath (Join-Path $records ('{0:d2}-{1}.json' -f $vehicle.Order, $vehicle.Alias)) -Raw | ConvertFrom-Json
        $passing = @($record.slots | Where-Object { $_.openSignalConfirmed -and $_.closeSignalConfirmed -and $_.inPlaceSignalConfirmed })
        $failing = @($record.slots | Where-Object { -not ($_.openSignalConfirmed -and $_.closeSignalConfirmed -and $_.inPlaceSignalConfirmed) })
        Assert-Rehearsal -Id "R-1$($vehicle.Order)" -Description "$($vehicle.Alias) 八仓开、关、到位全部通过" `
            -Passed (@($record.slots).Count -eq 8 -and $passing.Count -eq 8) `
            -Actual ("$($passing.Count)/8" + ($failing.Count ? '；' + (($failing | ForEach-Object { "$($_.physicalSlotNumber) 号：$($_.note)" }) -join ' | ') : ''))
    }

    Send-Simulator -Method POST -Path '/reset'
    $negativeRecords = Join-Path $StageRoot 'records-negative'
    $rehearsalAlias = 'agv01'
    $rehearsalFaults = @{ 3 = 'stuck-lock'; 5 = 'wrong-door' }
    & $probe @probeArguments -SiteAlias 'agv01' -Order 1 -AgvId '老厂前线新多仓位1' -RecordDirectory $negativeRecords
    $negative = Get-Content -LiteralPath (Join-Path $negativeRecords '01-agv01.json') -Raw | ConvertFrom-Json
    $bySlot = @{}
    foreach ($slotRecord in $negative.slots) { $bySlot[[int]$slotRecord.physicalSlotNumber] = $slotRecord }
    Assert-Rehearsal -Id 'R-21' -Description '锁反馈卡在锁闭的 3 号仓：开不通过，关与到位通过' `
        -Passed (-not $bySlot[3].openSignalConfirmed -and $bySlot[3].closeSignalConfirmed -and $bySlot[3].inPlaceSignalConfirmed) `
        -Actual $bySlot[3].note
    Assert-Rehearsal -Id 'R-22' -Description '现场人员说不是 5 号仓门弹开：开不通过，关与到位通过' `
        -Passed (-not $bySlot[5].openSignalConfirmed -and $bySlot[5].closeSignalConfirmed -and $bySlot[5].inPlaceSignalConfirmed) `
        -Actual $bySlot[5].note
    $collateral = @(1, 2, 4, 6, 7, 8 | Where-Object { -not ($bySlot[$_].openSignalConfirmed -and $bySlot[$_].closeSignalConfirmed -and $bySlot[$_].inPlaceSignalConfirmed) })
    Assert-Rehearsal -Id 'R-23' -Description '同一轮其余六仓不受影响' -Passed ($collateral.Count -eq 0) `
        -Actual ($collateral.Count ? "受影响：$($collateral -join '、')" : '六仓全部通过')

    # --- window -------------------------------------------------------------------------------------

    [ordered]@{
        windowId                         = 'W1'
        date                             = (Get-Date).ToString('yyyy-MM-dd')
        site                             = '控制端本机彩排：slots-simulator 冒充 IO 模块、脚本冒充现场人员。不是现场，不是证据'
        observers                        = @('W1 rehearsal (scripted person)')
        photoPointers                    = @('rehearsal-no-photo')
        productionContinuityObservations = @($vehicles | ForEach-Object {
                [ordered]@{
                    at                     = (Get-Date).ToString('o')
                    afterVehicle           = $_.AgvId
                    fleetStillTakingOrders = $true
                    note                   = '彩排数据，不是现场观察'
                    photoPointer           = 'rehearsal-no-photo'
                }
            })
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $records 'window.json') -Encoding utf8NoBOM

    $commit = "$(& git -C $Repository rev-parse HEAD)".Trim()
    $windowEvidence = Join-Path $StageRoot 'w1-window'
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-W1FieldWindow.ps1') -EvidenceRoot $windowEvidence `
        -Database $database -RecordDirectory $records -FieldOpsExecutable $FieldOpsExecutable -ControlServerCommit $commit *> (Join-Path $logs 'w1-window.log')
    $windowExit = $LASTEXITCODE
    $assertionsPath = Join-Path $windowEvidence 'assertions.json'
    $windowAssertions = (Test-Path -LiteralPath $assertionsPath) ? (Get-Content -LiteralPath $assertionsPath -Raw | ConvertFrom-Json) : $null
    Assert-Rehearsal -Id 'R-30' -Description 'W1 窗口脚本在三份探针记录上 PASS' `
        -Passed ($windowExit -eq 0 -and $windowAssertions.outcome -eq 'PASS') `
        -Actual ("exit $windowExit；" + (($windowAssertions ? @($windowAssertions.assertions) : @()) | ForEach-Object { "$($_.id)=$($_.outcome)" }) -join ' ')
    Assert-Rehearsal -Id 'R-31' -Description '证据目录带着探针的原始记录' `
        -Passed (Test-Path -LiteralPath (Join-Path $windowEvidence 'field-records/raw/agv03/slot8.json')) `
        -Actual 'field-records/raw/agv03/slot8.json'
    $agvIds = $windowAssertions ? @($windowAssertions.vehicles | ForEach-Object { $_.agvId }) : @()
    Assert-Rehearsal -Id 'R-32' -Description '判据里的中文 agvId 没被控制台编码写坏' `
        -Passed ($agvIds -contains '老厂前线新多仓位1' -and $agvIds -contains '老厂前线新多仓位3') `
        -Actual ($agvIds -join '，')
}
finally {
    if ($simulatorProcess -and -not $simulatorProcess.HasExited) {
        $simulatorProcess.Kill($true)
        $null = $simulatorProcess.WaitForExit(10000)
    }
    [ordered]@{
        stageRoot = $StageRoot
        results   = $results
        outcome   = (@($results | Where-Object { $_.outcome -ne 'PASS' }).Count -eq 0 -and $results.Count -eq $expectedAssertions) ? 'PASS' : 'FAIL'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $StageRoot 'rehearsal-result.json') -Encoding utf8NoBOM
}

$failed = @($results | Where-Object { $_.outcome -ne 'PASS' })
if ($failed.Count -gt 0 -or $results.Count -ne $expectedAssertions) {
    Write-Host "W1 probe rehearsal FAIL ($($failed.Count) failed, $($results.Count)/$expectedAssertions ran) -> $StageRoot"
    exit 1
}
Write-Host "W1 probe rehearsal PASS -> $StageRoot"
