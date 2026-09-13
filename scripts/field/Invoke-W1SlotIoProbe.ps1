#Requires -Version 7
<#
.SYNOPSIS
    W1 field probe for one vehicle: fires each slot's unlock output on the vehicle's IO module, watches
    lock feedback and light curtain while a person operates the slot, and writes the vehicle's field
    record for ControlServer.FieldOps verify.

.DESCRIPTION
    Why it goes around the onboard client. On the ControlServer_MVP line the onboard client pulses an
    unlock output only under server authority -- a scan authorised at a station, a SlotOperationCommand,
    or a recovery vector -- and the server serves one registered vehicle over one connection. There is
    no path by which it opens slot 5 of agv03 on request, and W1 needs every slot of every car. So the
    probe talks to the module itself, with the onboard client closed, exactly the way the client does:
    FC05 to fire, FC01/FC02 to read (see W1SlotIo.ps1).

    What is measured and what a person says. The three booleans in the record are derived from both,
    and every input to the derivation is written next to it under raw/<siteAlias>/slot<n>.json:

      open     outputs idle and slot locked before the pulse, the module accepted FC05, the unlock
               output was seen set and cleared by the module within OutputResetTimeoutMs, lock feedback
               went to released within UnlockFeedbackTimeoutMs, no other output and no other slot's lock
               feedback moved, and the person saw this slot's door, and only this one, spring open
      close    after the person closed the door, lock feedback read locked for StableMs, and every
               output was idle
      inPlace  the curtain read empty before, read object while the person held something in the slot,
               and read empty again once it was taken out

    The levels are ticket 35's approved facts, the same ones ApprovedSlotHardwareFacts.SignalPolarity
    spells out per signal: unlock 1 cleared by the module's 500 ms pulse, locked 1, released 0, curtain
    object 0, curtain empty 1.

    A person on site is the point. The door springing open, the object in the slot, the door closed by
    hand -- those are physical facts the IO image cannot see on its own (REQ-0263: a simulated result
    must never stand in for a field pass). Nothing here types true on anyone's behalf.

    Safety. The probe refuses while the onboard client or the slots simulator runs on the vehicle (a
    second Modbus master would race the client, and the simulator would make the reading meaningless),
    asks the person to type the vehicle's alias to confirm it is stopped and clear, fires one slot at a
    time only after the person says that slot is closed and hands are off, and clears an output the
    module left set rather than leave a lock energised.

    Records are append-only. The record file and the raw directory must not exist; a second attempt on
    the same vehicle goes to a new -RecordDirectory.

.EXAMPLE
    pwsh -File scripts/field/Invoke-W1SlotIoProbe.ps1 -SiteAlias agv01 -Order 1 -AgvId '老厂前线新多仓位1' `
        -IoModuleHost 192.168.71.150 -SlotModelVersionId <from FieldOps status> -VerifiedBy 'Zhengyu Shao' `
        -RecordDirectory scripts/field/records/20260914-W1 -PhotoPointers 'field-photos/20260914-W1/agv01-panel.jpg'
#>
[CmdletBinding(DefaultParameterSetName = 'Ssh')]
param(
    # The ssh alias of the vehicle's HMI machine -- the only coordinate that tells the three cars apart
    # from the control host (remote-ops/fleet.md).
    [Parameter(Mandatory)][string]$SiteAlias,

    # Position of this vehicle in the window; the record is <Order>-<SiteAlias>.json and
    # Invoke-W1FieldWindow.ps1 processes records in file-name order.
    [Parameter(Mandatory)][ValidateRange(1, 99)][int]$Order,

    [Parameter(Mandatory)][string]$AgvId,
    [Parameter(Mandatory)][string]$SlotModelVersionId,
    [Parameter(Mandatory)][string]$VerifiedBy,
    [Parameter(Mandatory)][string]$RecordDirectory,
    [string[]]$PhotoPointers = @(),

    # The vehicle's real module address. Never read from a template and never guessed: agv01's is in
    # site-agv01.json, agv02's and agv03's are measured on the day.
    [Parameter(ParameterSetName = 'Ssh', Mandatory)][string]$IoModuleHost,

    # Rehearsal only: the module is a slots simulator on this machine.
    [Parameter(ParameterSetName = 'Local', Mandatory)][switch]$Local,
    [Parameter(ParameterSetName = 'Local')][string]$LocalHost = '127.0.0.1',

    [int]$Port = 502,
    [int]$UnitId = 255,
    [int]$DoStartAddress = 100,
    [int]$DiStartAddress = 200,
    [int]$ChannelCount = 16,

    # The onboard client's own tolerances (workflow.unlockFeedbackTimeoutMs, unlockOutputResetTimeoutMs,
    # feedbackStableMs), so a slot the probe passes is a slot the client would not time out on.
    [int]$UnlockFeedbackTimeoutMs = 3000,
    [int]$OutputResetTimeoutMs = 3000,
    [int]$StableMs = 300,
    [int]$CloseFeedbackTimeoutMs = 10000,
    [int]$CurtainSettleTimeoutMs = 5000,

    # Answers the prompts. Receives (promptId, slotNumber, text) and returns the answer. The default
    # reads the console; the rehearsal passes one that plays the person against the simulator.
    [scriptblock]$Responder
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$slotCount = 8
$lockedLevel = 1
$releasedLevel = 0
$curtainObjectLevel = 0
$curtainEmptyLevel = 1

$library = Join-Path $PSScriptRoot 'W1SlotIo.ps1'
. $library

$transport = $Local ? 'Local' : 'Ssh'
$endpoint = @{
    HostName = $Local ? $LocalHost : $IoModuleHost
    Port     = $Port
    UnitId   = $UnitId
    DoStart  = $DoStartAddress
    DiStart  = $DiStartAddress
    Channels = $ChannelCount
}

if (-not (Test-Path -LiteralPath $RecordDirectory)) {
    New-Item -ItemType Directory -Path $RecordDirectory | Out-Null
}
$recordPath = Join-Path $RecordDirectory ('{0:d2}-{1}.json' -f $Order, $SiteAlias)
$rawDirectory = Join-Path (Join-Path $RecordDirectory 'raw') $SiteAlias
if (Test-Path -LiteralPath $recordPath) {
    throw "Field record already exists: $recordPath. Records are append-only; a second attempt goes to a new -RecordDirectory."
}
if (Test-Path -LiteralPath $rawDirectory) {
    throw "Raw directory already exists: $rawDirectory. Records are append-only; a second attempt goes to a new -RecordDirectory."
}
New-Item -ItemType Directory -Path $rawDirectory -Force | Out-Null

if (-not $Responder) {
    $Responder = { param($PromptId, $SlotNumber, $Text) Read-Host $Text }
}

function Invoke-VehicleScript {
    param([Parameter(Mandatory)][string]$Script)

    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
    $output = & ssh -o BatchMode=yes -o ConnectTimeout=15 $SiteAlias "pwsh -NoProfile -NonInteractive -EncodedCommand $encoded" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "ssh $SiteAlias failed: $(($output | Out-String).Trim())"
    }
    $json = @($output | ForEach-Object { "$_" } | Where-Object { $_.StartsWith('{') }) | Select-Object -Last 1
    if (-not $json) {
        throw "ssh $SiteAlias returned no JSON: $(($output | Out-String).Trim())"
    }
    $json | ConvertFrom-Json -AsHashtable
}

# Comments stripped: the library travels inside -EncodedCommand, and a Windows command line stops at
# 32767 characters. The library has no '#' inside code, only in comment lines and blocks.
$libraryText = (Get-Content -LiteralPath $library -Raw) -replace '(?s)<#.*?#>', '' -replace '(?m)^\s*#.*$', ''

function Invoke-Io {
    param([Parameter(Mandatory)][hashtable]$Arguments)

    if ($transport -eq 'Local') {
        return Invoke-W1IoOperation -Endpoint $endpoint @Arguments
    }
    $call = @{ Endpoint = $endpoint; Arguments = $Arguments } | ConvertTo-Json -Depth 5 -Compress
    Invoke-VehicleScript -Script (@"
$libraryText
`$ErrorActionPreference = 'Stop'
`$call = '$($call.Replace("'", "''"))' | ConvertFrom-Json -AsHashtable
`$arguments = `$call.Arguments
Invoke-W1IoOperation -Endpoint `$call.Endpoint @arguments | ConvertTo-Json -Depth 8 -Compress
"@)
}

$answers = [System.Collections.Generic.List[object]]::new()
function Ask {
    param([string]$Id, [int]$Slot, [string]$Text)

    $answer = "$(& $Responder $Id $Slot $Text)".Trim()
    $answers.Add([ordered]@{ at = (Get-Date).ToString('o'); promptId = $Id; slot = $Slot; prompt = $Text; answer = $answer })
    $answer
}

function Write-Json {
    param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$Path)
    $Value | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

# --- preflight -----------------------------------------------------------------------------------

$preflight = [ordered]@{
    at        = (Get-Date).ToString('o')
    transport = $transport
    siteAlias = $SiteAlias
    agvId     = $AgvId
    endpoint  = $endpoint
}
if ($transport -eq 'Ssh') {
    $vehicle = Invoke-VehicleScript -Script @"
`$ErrorActionPreference = 'Stop'
`$blocking = @(Get-Process -Name 'SQCD.Agv.Wpf', 'SQCD_8005AGV_Simulator' -ErrorAction SilentlyContinue | ForEach-Object { `$_.ProcessName })
`$ioTools = @(Get-Process -Name 'C2000Service' -ErrorAction SilentlyContinue | ForEach-Object { `$_.ProcessName })
`$client = [System.Net.Sockets.TcpClient]::new()
`$reachable = `$false
try { `$reachable = `$client.ConnectAsync('$IoModuleHost', $Port).Wait(3000) } catch { } finally { `$client.Dispose() }
[ordered]@{ blockingProcesses = `$blocking; otherIoTools = `$ioTools; moduleReachable = `$reachable; vehicleClock = (Get-Date).ToString('o') } | ConvertTo-Json -Compress
"@
    $preflight.vehicle = $vehicle
    Write-Json -Value $preflight -Path (Join-Path $rawDirectory 'preflight.json')
    if (@($vehicle.blockingProcesses).Count -gt 0) {
        throw "$SiteAlias is running $(@($vehicle.blockingProcesses) -join ', '). Close the onboard client and the slots simulator from the vehicle's own desktop first."
    }
    if (-not $vehicle.moduleReachable) {
        throw "$SiteAlias cannot reach the IO module at ${IoModuleHost}:$Port."
    }
    if (@($vehicle.otherIoTools).Count -gt 0) {
        Write-Warning "$SiteAlias is running $(@($vehicle.otherIoTools) -join ', '). If it is polling or writing the module, readings may include its traffic; it is recorded in preflight.json."
    }
}
else {
    Write-Json -Value $preflight -Path (Join-Path $rawDirectory 'preflight.json')
}

$confirmation = Ask -Id 'vehicle-safe' -Slot 0 -Text "确认 $SiteAlias（$AgvId）已停稳、周边无人、车载端与模拟器都已关闭、八个仓门都关着且仓内无物。输入 $SiteAlias 继续"
if ($confirmation -ne $SiteAlias) {
    Write-Json -Value ([ordered]@{ aborted = 'vehicle-safe not confirmed'; answers = $answers }) -Path (Join-Path $rawDirectory 'aborted.json')
    throw "Not confirmed (expected '$SiteAlias'). Nothing was fired."
}

$initial = Invoke-Io -Arguments @{ Operation = 'image' }
Write-Json -Value $initial -Path (Join-Path $rawDirectory 'initial-image.json')

# --- per slot ------------------------------------------------------------------------------------

$checkLabels = [ordered]@{
    outputsIdleBefore      = '开锁前有输出处于置位'
    lockedBefore           = '开锁前锁反馈不是锁闭'
    pulseAccepted          = '模块未接受开锁写入'
    outputObserved         = '没读到开锁输出置位'
    outputReset            = '开锁输出没有被模块按时复位'
    lockReleased           = '锁反馈没有按时变为打开'
    channelUnique          = '开锁时有别的输出或别的仓锁反馈变化'
    doorOpenedHere         = '现场人员没看到本仓（且只有本仓）弹开'
    emptyBefore            = '开锁前光幕不是无物'
    curtainDetectsObject   = '放入物体后光幕没有读到有物'
    curtainClearsWhenEmpty = '取出物体后光幕没有回到无物'
    lockReturnedOnClose    = '关门后锁反馈没有稳定回到锁闭'
    outputsIdleAfter       = '关门后仍有输出处于置位'
}

$recordSlots = [System.Collections.Generic.List[object]]::new()
$summaryRows = [System.Collections.Generic.List[object]]::new()

foreach ($slot in 1..$slotCount) {
    $doChannel = $slot - 1
    $lockChannel = $slot - 1
    $curtainChannel = $slot - 1 + $slotCount
    $answerStart = $answers.Count
    $rawPath = Join-Path $rawDirectory "slot$slot.json"
    $raw = [ordered]@{
        agvId              = $AgvId
        siteAlias          = $SiteAlias
        physicalSlotNumber = $slot
        channels           = [ordered]@{
            unlockOutput = "DO$slot (coil $($DoStartAddress + $doChannel))"
            lockFeedback = "DI$slot (input $($DiStartAddress + $lockChannel))"
            lightCurtain = "DI$($slot + $slotCount) (input $($DiStartAddress + $curtainChannel))"
        }
        expectedLevels     = [ordered]@{
            source       = 'ticket 35 (8005-agv-program .scratch/current-requirements-baseline/issues/35)'
            unlockActive = 1
            locked       = $lockedLevel
            released     = $releasedLevel
            curtainObject = $curtainObjectLevel
            curtainEmpty = $curtainEmptyLevel
        }
        steps              = [ordered]@{}
        checks             = [ordered]@{}
    }
    $checks = $raw.checks

    $begin = Ask -Id 'slot-ready' -Slot $slot -Text "[$slot/8] 确认 $slot 号仓门关着、仓内无物、手已离开，回车开锁；输入 skip 跳过本仓（本仓记为未通过）"
    if ($begin -eq 'skip') {
        $raw.skipped = $true
        $raw.answers = @($answers | Select-Object -Skip $answerStart)
        Write-Json -Value $raw -Path $rawPath
        $recordSlots.Add([ordered]@{
            physicalSlotNumber     = $slot
            openSignalConfirmed    = $false
            closeSignalConfirmed   = $false
            inPlaceSignalConfirmed = $false
            fieldRecordReference   = "raw/$SiteAlias/slot$slot.json"
            note                   = '现场跳过，未核对'
        })
        $summaryRows.Add([pscustomobject]@{ slot = $slot; open = $false; close = $false; inPlace = $false; note = '跳过' })
        continue
    }

    # 1. Fire and watch.
    $pulse = $null
    try {
        $pulse = Invoke-Io -Arguments @{ Operation = 'pulse'; Channel = $doChannel; DurationMs = [math]::Max($UnlockFeedbackTimeoutMs, $OutputResetTimeoutMs) }
        $checks.pulseAccepted = $true
    }
    catch {
        $raw.pulseError = $_.Exception.Message
        $checks.pulseAccepted = $false
    }
    $raw.steps.pulse = $pulse

    if ($pulse) {
        $before = $pulse.before
        $trace = @($pulse.trace)
        $checks.outputsIdleBefore = @($before.do | Where-Object { $_ -ne 0 }).Count -eq 0
        $checks.lockedBefore = $before.di[$lockChannel] -eq $lockedLevel
        $checks.emptyBefore = $before.di[$curtainChannel] -eq $curtainEmptyLevel

        $firstSet = $trace | Where-Object { $_.do[$doChannel] -eq 1 } | Select-Object -First 1
        $checks.outputObserved = $null -ne $firstSet
        $firstCleared = $firstSet ? ($trace | Where-Object { $_.elapsedMs -gt $firstSet.elapsedMs -and $_.do[$doChannel] -eq 0 } | Select-Object -First 1) : $null
        $raw.outputResetMs = $firstCleared ? $firstCleared.elapsedMs - $pulse.pulseSentAtMs : $null
        $checks.outputReset = $checks.outputObserved -and -not $pulse.forcedReset -and $null -ne $firstCleared -and
            $raw.outputResetMs -le $OutputResetTimeoutMs -and $pulse.after.do[$doChannel] -eq 0

        $released = $trace | Where-Object { $_.di[$lockChannel] -eq $releasedLevel } | Select-Object -First 1
        $raw.lockReleaseMs = $released ? $released.elapsedMs - $pulse.pulseSentAtMs : $null
        $checks.lockReleased = $null -ne $released -and $raw.lockReleaseMs -le $UnlockFeedbackTimeoutMs

        # Channel uniqueness: during the pulse nothing but this slot's own output and lock feedback may
        # move. Other light curtains are not held to it -- a person leaning on the vehicle is not a
        # wiring fault -- but any movement is kept in the raw record.
        $moved = [System.Collections.Generic.SortedSet[string]]::new()
        $curtainsMoved = [System.Collections.Generic.SortedSet[string]]::new()
        foreach ($image in @($trace) + @($pulse.after)) {
            for ($channel = 0; $channel -lt $ChannelCount; $channel++) {
                if ($channel -ne $doChannel -and $image.do[$channel] -ne $before.do[$channel]) { $null = $moved.Add("DO$($channel + 1)") }
            }
            for ($other = 0; $other -lt $slotCount; $other++) {
                if ($other -ne $lockChannel -and $image.di[$other] -ne $before.di[$other]) { $null = $moved.Add("DI$($other + 1)") }
                $otherCurtain = $other + $slotCount
                if ($otherCurtain -ne $curtainChannel -and $image.di[$otherCurtain] -ne $before.di[$otherCurtain]) { $null = $curtainsMoved.Add("DI$($otherCurtain + 1)") }
            }
        }
        $raw.otherChannelsMoved = @($moved)
        $raw.otherCurtainsMoved = @($curtainsMoved)
        $checks.channelUnique = $moved.Count -eq 0
    }

    # 2. What the person saw.
    $door = Ask -Id 'door-opened' -Slot $slot -Text "[$slot/8] 弹开的是 $slot 号仓门、而且只有它吗？(y/n)"
    $checks.doorOpenedHere = $door -match '^(y|yes|是)$'

    # 3. Light curtain with an object in and out.
    $null = Ask -Id 'object-in' -Slot $slot -Text "[$slot/8] 往 $slot 号仓里放一个物体挡住光幕，手离开后回车"
    $objectIn = Invoke-Io -Arguments @{ Operation = 'watch'; DurationMs = $CurtainSettleTimeoutMs; UntilDiChannel = $curtainChannel; UntilValue = $curtainObjectLevel; StableMs = $StableMs }
    $raw.steps.objectIn = $objectIn
    $checks.curtainDetectsObject = $objectIn.after.di[$curtainChannel] -eq $curtainObjectLevel

    $null = Ask -Id 'object-out' -Slot $slot -Text "[$slot/8] 把物体取出，回车"
    $objectOut = Invoke-Io -Arguments @{ Operation = 'watch'; DurationMs = $CurtainSettleTimeoutMs; UntilDiChannel = $curtainChannel; UntilValue = $curtainEmptyLevel; StableMs = $StableMs }
    $raw.steps.objectOut = $objectOut
    $checks.curtainClearsWhenEmpty = $objectOut.after.di[$curtainChannel] -eq $curtainEmptyLevel

    # 4. Close by hand.
    $null = Ask -Id 'door-closed' -Slot $slot -Text "[$slot/8] 关上 $slot 号仓门，回车"
    $closed = Invoke-Io -Arguments @{ Operation = 'watch'; DurationMs = $CloseFeedbackTimeoutMs; UntilDiChannel = $lockChannel; UntilValue = $lockedLevel; StableMs = $StableMs }
    $raw.steps.closed = $closed
    $checks.lockReturnedOnClose = $closed.after.di[$lockChannel] -eq $lockedLevel
    $checks.outputsIdleAfter = @($closed.after.do | Where-Object { $_ -ne 0 }).Count -eq 0

    foreach ($name in $checkLabels.Keys) {
        if (-not $checks.Contains($name)) { $checks[$name] = $false }
    }

    $open = $checks.outputsIdleBefore -and $checks.lockedBefore -and $checks.pulseAccepted -and $checks.outputObserved -and
        $checks.outputReset -and $checks.lockReleased -and $checks.channelUnique -and $checks.doorOpenedHere
    $close = $checks.lockReturnedOnClose -and $checks.outputsIdleAfter
    $inPlace = $checks.emptyBefore -and $checks.curtainDetectsObject -and $checks.curtainClearsWhenEmpty

    $failed = @($checkLabels.Keys | Where-Object { -not $checks[$_] } | ForEach-Object { $checkLabels[$_] })
    $timing = 'DO 复位 {0} ms，锁反馈打开 {1} ms' -f ($raw.outputResetMs ?? '-'), ($raw.lockReleaseMs ?? '-')
    $note = $failed.Count -eq 0 ? "探针直读模块，全部检查通过；$timing" : "未通过：$($failed -join '；')；$timing"

    $raw.derived = [ordered]@{ openSignalConfirmed = $open; closeSignalConfirmed = $close; inPlaceSignalConfirmed = $inPlace }
    $raw.answers = @($answers | Select-Object -Skip $answerStart)
    Write-Json -Value $raw -Path $rawPath

    $recordSlots.Add([ordered]@{
        physicalSlotNumber     = $slot
        openSignalConfirmed    = $open
        closeSignalConfirmed   = $close
        inPlaceSignalConfirmed = $inPlace
        fieldRecordReference   = "raw/$SiteAlias/slot$slot.json"
        note                   = $note
    })
    $summaryRows.Add([pscustomobject]@{ slot = $slot; open = $open; close = $close; inPlace = $inPlace; note = $note })
}

$final = Invoke-Io -Arguments @{ Operation = 'image' }
Write-Json -Value $final -Path (Join-Path $rawDirectory 'final-image.json')

$record = [ordered]@{
    agvId              = $AgvId
    siteAlias          = $SiteAlias
    slotModelVersionId = $SlotModelVersionId
    verifiedBy         = $VerifiedBy
    verifiedAt         = (Get-Date).ToString('o')
    photoPointers      = @($PhotoPointers)
    slots              = $recordSlots
}
Write-Json -Value $record -Path $recordPath
Write-Json -Value ([ordered]@{ answers = $answers }) -Path (Join-Path $rawDirectory 'answers.json')

$summaryRows | Format-Table -AutoSize | Out-String -Width 240 | Write-Host
$passed = @($recordSlots | Where-Object { $_.openSignalConfirmed -and $_.closeSignalConfirmed -and $_.inPlaceSignalConfirmed }).Count
Write-Host "$SiteAlias：$passed/8 仓三项全部通过 -> $recordPath"
