#Requires -Version 7
<#
.SYNOPSIS
    W1 field probe for one vehicle: opens each slot in turn on the vehicle's IO module, follows lock
    feedback and light curtain while the person at the vehicle operates that slot, and writes the
    vehicle's field record for ControlServer.FieldOps verify.

.DESCRIPTION
    Why it goes around the onboard client. On the ControlServer_MVP line the onboard client pulses an
    unlock output only under server authority -- a scan authorised at a station, a SlotOperationCommand,
    or a recovery vector -- and the server serves one registered vehicle over one connection. There is
    no path by which it opens slot 5 of agv03 on request, and W1 needs every slot of every car. So the
    probe talks to the module itself, with the onboard client closed, exactly the way the client does:
    FC05 to fire, FC01/FC02 to read (see W1SlotIo.ps1).

    Nobody at a keyboard. The person at the vehicle only operates the slots; the probe follows them on
    the IO. For each slot in order: wait until every slot reads locked and every output idle, fire the
    unlock, then follow the image until lock feedback has gone released and come back locked -- the
    door closed -- while the person puts something in the slot, takes it out and closes the door. Only
    then does the next slot open, so at most one door is ever open. There are two questions per vehicle,
    through -Responder: before anything is fired, that the vehicle is stopped and clear; and after the
    eighth slot, which doors did not spring open as the one and only door of the slot being fired.

    What decides each boolean, all of it written under raw/<siteAlias>/slot<n>.json:

      open     before the pulse every output idle and this slot locked; the module accepted FC05; the
               unlock output was seen set and cleared by the module within OutputResetTimeoutMs; lock
               feedback went released within UnlockFeedbackTimeoutMs; while the slot was open no other
               output and no other slot's lock feedback moved; and the person did not name this slot
               among the doors that did not open correctly
      close    lock feedback came back locked for StableMs, and every output was idle afterwards
      inPlace  the curtain read empty before, then read object for StableMs, then empty again for StableMs

    The levels are ticket 35's approved facts, the same ones ApprovedSlotHardwareFacts.SignalPolarity
    spells out per signal: unlock 1 cleared by the module's 500 ms pulse, locked 1, released 0, curtain
    object 0, curtain empty 1.

    A person at the vehicle is the point. The door springing open, the object in the slot, the door
    closed by hand -- the IO image cannot produce those by itself (REQ-0263: a simulated result must
    never stand in for a field pass). Nothing here types true on anyone's behalf.

    Stops for the whole vehicle, writing aborted.json and no record, when it cannot know it is safe to
    go on: another slot not locked before a pulse, lock feedback never going released after one (there
    is then no signal for the door closing, and firing the next slot could leave two doors open), or a
    door not closed within DoorCloseTimeoutSeconds. An output the module left set is cleared at once and
    the slot fails; that does not stop the vehicle.

    Refuses while the onboard client or the slots simulator runs on the vehicle. Records are
    append-only: the record file and the raw directory must not exist.

.EXAMPLE
    pwsh -File scripts/field/Invoke-W1SlotIoProbe.ps1 -SiteAlias agv01 -Order 1 -AgvId '老厂前线新多仓位1' `
        -IoModuleHost 192.168.71.150 -SlotModelVersionId <from FieldOps status> -VerifiedBy 'Zhengyu Shao' `
        -RecordDirectory scripts/field/records/20260913-W1
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

    # The vehicle's real module address, measured, never guessed (remote-ops/fleet.md).
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

    # How long the person has, per slot, from the door springing open to the door closed.
    [int]$DoorCloseTimeoutSeconds = 300,

    # Answers the two questions. Receives (promptId, slotNumber, text) and returns the answer. The
    # default reads the console.
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

function Say {
    param([string]$Text)
    Write-Host "$(Get-Date -Format 'HH:mm:ss') $SiteAlias $Text"
}

$slotStates = [System.Collections.Generic.List[object]]::new()

function Stop-Vehicle {
    param([Parameter(Mandatory)][string]$Reason)

    Write-Json -Value ([ordered]@{
            aborted   = $Reason
            at        = (Get-Date).ToString('o')
            agvId     = $AgvId
            siteAlias = $SiteAlias
            slots     = $slotStates
            answers   = $answers
        }) -Path (Join-Path $rawDirectory 'aborted.json')
    Say "停止：$Reason"
    throw "W1 probe stopped on ${SiteAlias}: $Reason No record was written."
}

# Index of the first image at or after StartIndex where the channel reads Value and keeps reading it for
# MinimumMs -- until the next image that differs, or the last image.
function Find-StableLevel {
    param([object[]]$Images, [int]$Channel, [int]$Value, [int]$StartIndex, [int]$MinimumMs)

    for ($i = [math]::Max($StartIndex, 0); $i -lt $Images.Count; $i++) {
        if ($Images[$i].di[$Channel] -ne $Value) { continue }
        $end = $Images[$Images.Count - 1].elapsedMs
        for ($j = $i + 1; $j -lt $Images.Count; $j++) {
            if ($Images[$j].di[$Channel] -ne $Value) { $end = $Images[$j].elapsedMs; break }
        }
        if ($end - $Images[$i].elapsedMs -ge $MinimumMs) { return $i }
    }
    -1
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

$confirmation = Ask -Id 'vehicle-safe' -Slot 0 -Text "确认 $SiteAlias（$AgvId）已停稳、周边无人、车载端与模拟器都已关闭、八个仓门都关着且仓内无物、有人在车前按顺序操作仓门。输入 $SiteAlias 继续"
if ($confirmation -ne $SiteAlias) {
    Write-Json -Value ([ordered]@{ aborted = 'vehicle-safe not confirmed'; answers = $answers }) -Path (Join-Path $rawDirectory 'aborted.json')
    throw "Not confirmed (expected '$SiteAlias'). Nothing was fired."
}

$initial = Invoke-Io -Arguments @{ Operation = 'image' }
Write-Json -Value $initial -Path (Join-Path $rawDirectory 'initial-image.json')

# --- per slot ------------------------------------------------------------------------------------

foreach ($slot in 1..$slotCount) {
    $doChannel = $slot - 1
    $lockChannel = $slot - 1
    $curtainChannel = $slot - 1 + $slotCount
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
            source        = 'ticket 35 (8005-agv-program .scratch/current-requirements-baseline/issues/35)'
            unlockActive  = 1
            locked        = $lockedLevel
            released      = $releasedLevel
            curtainObject = $curtainObjectLevel
            curtainEmpty  = $curtainEmptyLevel
        }
        checks             = [ordered]@{}
    }
    $checks = $raw.checks
    $state = [ordered]@{ slot = $slot; raw = $raw }
    $slotStates.Add($state)

    # 1. Nothing else open: every output idle, every slot locked. The previous door has to be shut
    #    before this one springs.
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $closedImage = $null
    while ($clock.Elapsed.TotalSeconds -lt $DoorCloseTimeoutSeconds) {
        $image = (Invoke-Io -Arguments @{ Operation = 'image' }).before
        $idle = @($image.do | Where-Object { $_ -ne 0 }).Count -eq 0
        $allLocked = @(0..($slotCount - 1) | Where-Object { $image.di[$_] -ne $lockedLevel }).Count -eq 0
        if ($idle -and $allLocked) { $closedImage = $image; break }
        Start-Sleep -Seconds 2
    }
    if (-not $closedImage) {
        Stop-Vehicle "开 $slot 号仓之前，车上仍有仓未锁闭或有输出置位，等了 $DoorCloseTimeoutSeconds 秒。"
    }

    # 2. Fire, and follow the slot until its door is closed again.
    Say "[$slot/8] 开 $slot 号仓：放一个物体进去挡住光幕，取出，再关门"
    $pulse = $null
    try {
        $pulse = Invoke-Io -Arguments @{
            Operation       = 'pulse'
            Channel         = $doChannel
            DurationMs      = $DoorCloseTimeoutSeconds * 1000
            IntervalMs      = 50
            UntilDiChannel  = $lockChannel
            ArmValue        = $releasedLevel
            ArmTimeoutMs    = $UnlockFeedbackTimeoutMs
            UntilValue      = $lockedLevel
            StableMs        = $StableMs
            ResetDeadlineMs = $OutputResetTimeoutMs
            ChangesOnly     = $true
        }
        $checks.pulseAccepted = $true
    }
    catch {
        $raw.pulseError = $_.Exception.Message
        $checks.pulseAccepted = $false
        Stop-Vehicle "$slot 号仓开锁写入失败：$($_.Exception.Message)"
    }
    $raw.pulse = $pulse

    $before = $pulse.before
    $images = @(@($pulse.trace) + @($pulse.after))
    $checks.outputsIdleBefore = @($before.do | Where-Object { $_ -ne 0 }).Count -eq 0
    $checks.lockedBefore = $before.di[$lockChannel] -eq $lockedLevel
    $checks.emptyBefore = $before.di[$curtainChannel] -eq $curtainEmptyLevel

    $firstSet = $images | Where-Object { $_.do[$doChannel] -eq 1 } | Select-Object -First 1
    $checks.outputObserved = $null -ne $firstSet
    $firstCleared = $firstSet ? ($images | Where-Object { $_.elapsedMs -gt $firstSet.elapsedMs -and $_.do[$doChannel] -eq 0 } | Select-Object -First 1) : $null
    $raw.outputResetMs = $firstCleared ? $firstCleared.elapsedMs - $pulse.pulseSentAtMs : $null
    $checks.outputReset = $checks.outputObserved -and -not $pulse.forcedReset -and $null -ne $firstCleared -and
        $raw.outputResetMs -le $OutputResetTimeoutMs

    $releasedIndex = -1
    for ($i = 0; $i -lt $images.Count; $i++) { if ($images[$i].di[$lockChannel] -eq $releasedLevel) { $releasedIndex = $i; break } }
    $raw.lockReleaseMs = $releasedIndex -ge 0 ? $images[$releasedIndex].elapsedMs - $pulse.pulseSentAtMs : $null
    $checks.lockReleased = $releasedIndex -ge 0 -and $raw.lockReleaseMs -le $UnlockFeedbackTimeoutMs

    if (-not $checks.lockReleased) {
        Stop-Vehicle "$slot 号仓开锁后锁反馈没有在 $UnlockFeedbackTimeoutMs ms 内变为打开：没有信号能说明门什么时候关上，为免同时开着两扇门，不再开下一仓。"
    }
    if (-not $pulse.untilMet) {
        Stop-Vehicle "$slot 号仓门在 $DoorCloseTimeoutSeconds 秒内没有关上（锁反馈没有稳定回到锁闭）。"
    }

    # Channel uniqueness: while this slot was open nothing but its own output and lock feedback may
    # move. Other light curtains are not held to it -- a person leaning on the vehicle is not a wiring
    # fault -- but any movement is kept.
    $moved = [System.Collections.Generic.SortedSet[string]]::new()
    $curtainsMoved = [System.Collections.Generic.SortedSet[string]]::new()
    foreach ($image in $images) {
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

    $objectIndex = Find-StableLevel -Images $images -Channel $curtainChannel -Value $curtainObjectLevel -StartIndex $releasedIndex -MinimumMs $StableMs
    $emptyAgainIndex = $objectIndex -ge 0 ? (Find-StableLevel -Images $images -Channel $curtainChannel -Value $curtainEmptyLevel -StartIndex ($objectIndex + 1) -MinimumMs $StableMs) : -1
    $checks.curtainDetectsObject = $objectIndex -ge 0
    $checks.curtainClearsWhenEmpty = $emptyAgainIndex -ge 0
    $raw.objectSeenMs = $objectIndex -ge 0 ? $images[$objectIndex].elapsedMs : $null
    $raw.emptyAgainMs = $emptyAgainIndex -ge 0 ? $images[$emptyAgainIndex].elapsedMs : $null
    $raw.doorClosedMs = $pulse.after.elapsedMs

    $checks.lockReturnedOnClose = $pulse.untilMet -and $pulse.after.di[$lockChannel] -eq $lockedLevel
    $checks.outputsIdleAfter = @($pulse.after.do | Where-Object { $_ -ne 0 }).Count -eq 0

    Say ("[$slot/8] {0} 号仓门已关（光幕有物 {1}，取出 {2}，锁反馈打开 {3} ms，DO 复位 {4} ms）" -f $slot,
        ($checks.curtainDetectsObject ? '读到' : '没读到'), ($checks.curtainClearsWhenEmpty ? '读到' : '没读到'),
        ($raw.lockReleaseMs ?? '-'), ($raw.outputResetMs ?? '-'))
}

# --- what the person saw -------------------------------------------------------------------------

$doorAnswer = Ask -Id 'doors-observed' -Slot 0 -Text "$SiteAlias 的 8 个仓是否都按 1 到 8 的顺序、每次只弹开当时那一仓的门？都对就回答 all；有不对的写出仓号，用逗号隔开（例如 3,5）"
$wrongDoors = @($doorAnswer -split '[,，、\s]+' | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ })
$doorAnswerUnderstood = $doorAnswer -eq 'all' -or ($wrongDoors.Count -gt 0 -and @($wrongDoors | Where-Object { $_ -lt 1 -or $_ -gt $slotCount }).Count -eq 0)

$checkLabels = [ordered]@{
    outputsIdleBefore      = '开锁前有输出处于置位'
    lockedBefore           = '开锁前锁反馈不是锁闭'
    pulseAccepted          = '模块未接受开锁写入'
    outputObserved         = '没读到开锁输出置位'
    outputReset            = '开锁输出没有被模块按时复位'
    lockReleased           = '锁反馈没有按时变为打开'
    channelUnique          = '开着这一仓时有别的输出或别的仓锁反馈变化'
    doorOpenedHere         = '现场人员说这一仓的门没有正确弹开'
    emptyBefore            = '开锁前光幕不是无物'
    curtainDetectsObject   = '光幕没有稳定读到有物'
    curtainClearsWhenEmpty = '取出后光幕没有稳定回到无物'
    lockReturnedOnClose    = '关门后锁反馈没有稳定回到锁闭'
    outputsIdleAfter       = '关门后仍有输出处于置位'
}

$recordSlots = [System.Collections.Generic.List[object]]::new()
$summaryRows = [System.Collections.Generic.List[object]]::new()
foreach ($state in $slotStates) {
    $slot = $state.slot
    $raw = $state.raw
    $checks = $raw.checks
    $checks.doorOpenedHere = $doorAnswerUnderstood -and $wrongDoors -notcontains $slot

    $open = $checks.outputsIdleBefore -and $checks.lockedBefore -and $checks.pulseAccepted -and $checks.outputObserved -and
        $checks.outputReset -and $checks.lockReleased -and $checks.channelUnique -and $checks.doorOpenedHere
    $close = $checks.lockReturnedOnClose -and $checks.outputsIdleAfter
    $inPlace = $checks.emptyBefore -and $checks.curtainDetectsObject -and $checks.curtainClearsWhenEmpty

    $failed = @($checkLabels.Keys | Where-Object { -not $checks[$_] } | ForEach-Object { $checkLabels[$_] })
    $timing = 'DO 复位 {0} ms，锁反馈打开 {1} ms' -f ($raw.outputResetMs ?? '-'), ($raw.lockReleaseMs ?? '-')
    $note = $failed.Count -eq 0 ? "探针直读模块，全部检查通过；$timing" : "未通过：$($failed -join '；')；$timing"

    $raw.doorObservation = [ordered]@{ answer = $doorAnswer; understood = $doorAnswerUnderstood; wrongDoors = $wrongDoors }
    $raw.derived = [ordered]@{ openSignalConfirmed = $open; closeSignalConfirmed = $close; inPlaceSignalConfirmed = $inPlace }
    Write-Json -Value $raw -Path (Join-Path $rawDirectory "slot$slot.json")

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
