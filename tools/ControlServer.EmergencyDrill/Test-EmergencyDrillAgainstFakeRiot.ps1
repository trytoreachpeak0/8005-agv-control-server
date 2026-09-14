#Requires -Version 7

<#
.SYNOPSIS
ControlServer.EmergencyDrill 对本机回环上的 ControlServer.FakeRiot 走完整个演练流程的自测。

.DESCRIPTION
**只连本机回环上本脚本自己起的 FakeRiot，永远不连真实 RIoT（172.19.206.222），不碰任何车。**

收尾顺序是「先取消演练单、再解除急停」（用户 2026-09-14 裁定）。跑四段：

- A 正向：init → status → create-move（先证没有 preflight 时拒绝）→ preflight → create-move → cancel-order
  （先证发令前拒绝）→（控制面：单进入执行、车离站在两站之间行驶）→ watch-moving → trigger →（控制面：约 0.8 秒后
  闩锁 CAN_RECOVER，再 0.4 秒车停下）→ release（先证取消单之前拒绝）→ cancel-order →（控制面：单取消）→
  release →（控制面：闩锁回 OK）→ summarize。
- B 反向：闩锁一直不锁上时 trigger 只发一次、第二次被拒；闩锁 OK 时 cancel-order 被拒；CAN_NOT_RECOVER 时
  release 在取消单之前被拒、cancel-order 被允许、取消后 release 仍被拒且不发 cancelEmergency。
- D 取消单迟迟不到终态：cancel-order 发一次后报 NOT_CONFIRMED 并提示停下回报用户；此后 release 在 CAN_RECOVER 下仍拒绝。
- C 离线守卫：init 拒绝 agv01、其它 key、不带 --fake-riot 的自测 key、非回环地址、map 26、复用已用目录；
  已删除的 --force-after-can-not-recover 是用法错误。

**FakeRiot 不替工具锁闩锁、不让车停**（它的一贯设计：命令只记录），所以闩锁、停车、取消单、解除的后果都由
本脚本在看到对应调用落到 FakeRiot 之后，照真实 RIoT 的样子在控制面上写出来。

端口取系统分配的空闲回环端口，避开 L2 端口段（48405–48414、合成车载端 48420 起），所以不占 L2 端口锁。

.PARAMETER EvidenceRoot
自测证据目录，必须不存在。默认在 %TEMP% 下新建。**不要指到仓库的 evidence/ 下**——自测不是现场证据。

.PARAMETER NoBuild
跳过构建（两个项目已按 Release 构建过时）。
#>
[CmdletBinding()]
param(
    [string]$EvidenceRoot,
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $EvidenceRoot) {
    $EvidenceRoot = Join-Path ([IO.Path]::GetTempPath()) ('emergency-drill-selftest-' + (Get-Date -Format 'yyyyMMddTHHmmss'))
}
if (Test-Path -LiteralPath $EvidenceRoot) {
    throw "EvidenceRoot must be a new directory: $EvidenceRoot"
}
$null = New-Item -ItemType Directory -Path $EvidenceRoot
$EvidenceRoot = (Resolve-Path -LiteralPath $EvidenceRoot).Path
$logRoot = Join-Path $EvidenceRoot 'logs'
$null = New-Item -ItemType Directory -Path $logRoot

$drillExe = Join-Path $repository 'tools/ControlServer.EmergencyDrill/bin/Release/net8.0/win-x64/ControlServer.EmergencyDrill.exe'
$riotDirectory = Join-Path $repository 'tools/ControlServer.FakeRiot/bin/Release/net8.0/win-x64'
$riotExe = Join-Path $riotDirectory 'ControlServer.FakeRiot.exe'

$selfTestKey = 'BROKERX-DRILL-SELFTEST-0001'
$agv02Key = 'BROKERX-f38975561adf46ccb1d2f23833c7d0e4'
$agv01Key = 'BROKERX-0c20ff0600d644869a6a80c186065d85'
$otherKey = 'BROKERX-00000000000000000000000000000003'
$confirmation = 'Selftest-Safety stopped,empty,doors-closed'

$assertions = [System.Collections.Generic.List[object]]::new()
$started = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function Add-Assertion {
    param([string]$Id, [string]$Description, [bool]$Passed, [string]$Expected, [string]$Actual)
    $assertions.Add([pscustomobject]@{
        id = $Id; description = $Description; passed = $Passed; expected = $Expected; actual = $Actual
    })
    $mark = if ($Passed) { 'PASS' } else { 'FAIL' }
    Write-Host "[$mark] $Id $Description -- expected: $Expected; actual: $Actual"
}

# --- the drill executable ----------------------------------------------------------------------------

# ProcessStartInfo.ArgumentList rather than Start-Process: the field confirmation carries spaces, and
# Start-Process joins an argument array without quoting it.
function Start-Drill {
    param([string]$Label, [string[]]$Arguments)
    $info = [System.Diagnostics.ProcessStartInfo]::new($drillExe)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
    $process = [System.Diagnostics.Process]::Start($info)
    $started.Add($process)
    return [pscustomobject]@{
        Label   = $Label
        Process = $process
        Stdout  = $process.StandardOutput.ReadToEndAsync()
        Stderr  = $process.StandardError.ReadToEndAsync()
    }
}

function Wait-Drill {
    param($Handle, [int]$TimeoutSeconds = 180)
    if (-not $Handle.Process.WaitForExit($TimeoutSeconds * 1000)) {
        $Handle.Process.Kill($true)
        throw "$($Handle.Label) did not finish within $TimeoutSeconds s"
    }
    $Handle.Process.WaitForExit()
    $stdout = $Handle.Stdout.GetAwaiter().GetResult()
    $stderr = $Handle.Stderr.GetAwaiter().GetResult()
    Set-Content -LiteralPath (Join-Path $logRoot "$($Handle.Label).out.log") -Value $stdout -Encoding utf8NoBOM
    if ($stderr) {
        Set-Content -LiteralPath (Join-Path $logRoot "$($Handle.Label).err.log") -Value $stderr -Encoding utf8NoBOM
    }
    $lines = @($stdout -split "`r?`n" | Where-Object { $_ })
    $json = $null
    if ($lines.Count -gt 0) {
        try { $json = $lines[-1] | ConvertFrom-Json } catch { $json = $null }
    }
    $outcome = if ($json) { [string]$json.outcome } else { '(no json)' }
    Write-Host "  $($Handle.Label): exit $($Handle.Process.ExitCode) $outcome"
    return [pscustomobject]@{
        ExitCode = $Handle.Process.ExitCode
        Outcome  = $outcome
        Message  = if ($json) { [string]$json.message } else { '' }
        Json     = $json
        Stdout   = $stdout
    }
}

function Invoke-Drill {
    param([string]$Label, [string[]]$Arguments)
    return Wait-Drill (Start-Drill $Label $Arguments)
}

function Read-DrillState([string]$Run) {
    return Get-Content -LiteralPath (Join-Path $Run 'drill-state.json') -Raw | ConvertFrom-Json
}

# --- the fake RIoT control plane ---------------------------------------------------------------------

function Get-Riot {
    return Invoke-RestMethod -Uri "$riotBase/control/v1/snapshot" -TimeoutSec 10 -NoProxy
}

function Set-Riot {
    param([string]$Path, [hashtable]$Body)
    $payload = @{ runId = (Get-Riot).runId; commandId = [guid]::NewGuid().ToString('N') } + $Body
    $null = Invoke-RestMethod -Uri "$riotBase/control/v1/$Path" -Method Put -ContentType 'application/json' `
        -Body ($payload | ConvertTo-Json -Depth 8) -TimeoutSec 10 -NoProxy
}

function Set-Vehicle([hashtable]$Fields) {
    Set-Riot 'vehicle' (@{ vehicleKey = $selfTestKey } + $Fields)
}

function Reset-Riot {
    $payload = @{ runId = (Get-Riot).runId; commandId = [guid]::NewGuid().ToString('N') }
    $null = Invoke-RestMethod -Uri "$riotBase/control/v1/reset" -Method Post -ContentType 'application/json' `
        -Body ($payload | ConvertTo-Json) -TimeoutSec 10 -NoProxy
}

# `return , @(...)`: a one-element result must stay an array under strict mode.
function Get-Invocations([string]$Type) {
    return , @(@((Get-Riot).body.commandInvocations) | Where-Object { [string]$_.commandType -eq $Type })
}

function Get-Orders {
    return , @(@((Get-Riot).body.orders) | Where-Object { $_ })
}

# Waits until FakeRiot has recorded the call, so the scenario reacts to the call and not to a guess.
function Wait-Invocation {
    param([string]$Type, [int]$Count, $Handle, [int]$TimeoutSeconds = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Invocations $Type).Count -lt $Count) {
        if ($Handle.Process.HasExited -or (Get-Date) -gt $deadline) { return $false }
        Start-Sleep -Milliseconds 100
    }
    return $true
}

function Get-FreeLoopbackPort {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()
        if ($port -lt 48400 -or $port -gt 48499) { return $port }
    }
    throw 'no free loopback port outside the L2 block'
}

# One run up to "RIoT executes the order and the vehicle is between stations".
function Start-MovingRun {
    param([string]$Run, [string]$Prefix, [int]$Destination)
    $results = @(
        (Invoke-Drill "$Prefix-init" @('init', '--evidence', $Run, '--device-key', $selfTestKey, '--map-id', '25', '--riot-base-url', $riotBase, '--fake-riot')),
        (Invoke-Drill "$Prefix-preflight" @('preflight', '--evidence', $Run)),
        (Invoke-Drill "$Prefix-create-move" @('create-move', '--evidence', $Run, '--to', "$Destination")))
    $state = Read-DrillState $Run
    return [pscustomobject]@{
        ExitCodes = @($results | ForEach-Object ExitCode)
        UpperId   = "W1-DRILL-$($state.runId)"
        OrderId   = [string]$state.order.orderId
    }
}

# --- run ---------------------------------------------------------------------------------------------

$secret = 'selftest-secret-' + [guid]::NewGuid().ToString('N')
$previousKey = $env:CONTROL_SERVER_RIOT_CALL_API_KEY
$riotProcess = $null
$failed = $false

try {
    if (-not $NoBuild) {
        foreach ($project in @(
                'tools/ControlServer.FakeRiot/ControlServer.FakeRiot.csproj',
                'tools/ControlServer.EmergencyDrill/ControlServer.EmergencyDrill.csproj')) {
            Write-Host "Building $project (Release)"
            # Built from the repository directory so global.json pins the SDK.
            $build = pwsh -NoProfile -WorkingDirectory $repository -Command "dotnet build '$project' -c Release --nologo -v q -p:TreatWarningsAsErrors=true; exit `$LASTEXITCODE" 2>&1
            $build | Out-File -LiteralPath (Join-Path $logRoot 'build.log') -Append -Encoding utf8NoBOM
            if ($LASTEXITCODE -ne 0) { throw "build failed for $project; see $logRoot\build.log" }
        }
    }
    foreach ($exe in @($drillExe, $riotExe)) {
        if (-not (Test-Path -LiteralPath $exe)) { throw "missing $exe (build first)" }
    }

    $env:CONTROL_SERVER_RIOT_CALL_API_KEY = $secret
    $port = Get-FreeLoopbackPort
    $script:riotBase = "http://127.0.0.1:$port"
    Write-Host "Starting FakeRiot on $riotBase"
    $riotProcess = Start-Process -FilePath $riotExe -WorkingDirectory $riotDirectory -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $logRoot 'fake-riot.out.log') `
        -RedirectStandardError (Join-Path $logRoot 'fake-riot.err.log') `
        -ArgumentList @(
            "--FakeRiot:port=$port",
            '--FakeRiot:instanceId=emergency-drill-selftest',
            "--FakeRiot:Seed:vehicleKey=$selfTestKey",
            '--FakeRiot:Seed:mapId=25',
            '--FakeRiot:Seed:startStationId=210')
    $started.Add($riotProcess)

    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        if ($riotProcess.HasExited) { throw "FakeRiot exited with $($riotProcess.ExitCode); see logs" }
        try {
            if ((Invoke-RestMethod -Uri "$riotBase/control/v1/health" -TimeoutSec 2 -NoProxy).body.status -eq 'live') { break }
        } catch { }
        if ((Get-Date) -gt $deadline) { throw 'FakeRiot did not become live within 60 s' }
        Start-Sleep -Milliseconds 200
    }
    # A health answer only says something is listening; make sure it is the process started here.
    $owner = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
    if ($owner -notcontains $riotProcess.Id) { throw "port $port is held by process(es) $($owner -join ',') rather than FakeRiot $($riotProcess.Id)" }

    # ===== A: the whole drill, positive path =========================================================
    Write-Host "`n== A: positive path (trigger -> cancel-order -> release)"
    $runA = Join-Path $EvidenceRoot 'run-a-positive'

    $r = Invoke-Drill 'A01-init' @('init', '--evidence', $runA, '--device-key', $selfTestKey, '--map-id', '25', '--riot-base-url', $riotBase, '--fake-riot')
    Add-Assertion 'DRILL-A01' 'init 接受自测 key（--fake-riot + 回环地址），建出 drill-state.json' `
        ($r.ExitCode -eq 0 -and (Test-Path -LiteralPath (Join-Path $runA 'drill-state.json'))) 'exit 0 + 状态文件' "exit $($r.ExitCode) $($r.Outcome)"

    $r = Invoke-Drill 'A02-status' @('status', '--evidence', $runA, '--stations')
    Add-Assertion 'DRILL-A02' 'status 只读：读到车辆卡片、闩锁 OK、map 25 的 3 个站点，FakeRiot 无任何写调用' `
        ($r.ExitCode -eq 0 -and [string]$r.Json.data.emergencyState -eq 'OK' -and [int]$r.Json.data.stationCount -eq 3 -and
            @((Get-Riot).body.commandInvocations).Count -eq 0 -and (Get-Orders).Count -eq 0) `
        'exit 0 / OK / 3 站 / 0 调用' "exit $($r.ExitCode) / $($r.Json.data.emergencyState) / $($r.Json.data.stationCount) 站 / $(@((Get-Riot).body.commandInvocations).Count) 调用"

    $r = Invoke-Drill 'A03-create-before-preflight' @('create-move', '--evidence', $runA, '--to', '12')
    Add-Assertion 'DRILL-A03' '本 run 没有通过的 preflight 时 create-move 拒绝，FakeRiot 上没有订单' `
        ($r.ExitCode -eq 1 -and (Get-Orders).Count -eq 0 -and $r.Stdout -match '\[NO\] preflight passed') 'exit 1 / 0 单' "exit $($r.ExitCode) / $((Get-Orders).Count) 单"

    $r = Invoke-Drill 'A04-preflight' @('preflight', '--evidence', $runA)
    Add-Assertion 'DRILL-A04' 'preflight 对回环 FakeRiot 判直连通过：DIRECT_LOOPBACK、5 次卡片读全成功、最大 ≤1000 ms' `
        ($r.ExitCode -eq 0 -and [string]$r.Json.data.route.verdict -eq 'DIRECT_LOOPBACK' -and
            @($r.Json.data.latency.roundTripsMs).Count -eq 5 -and [double]$r.Json.data.latency.maxMs -le 1000) `
        'exit 0 / DIRECT_LOOPBACK / 5 / ≤1000' "exit $($r.ExitCode) / $($r.Json.data.route.verdict) / $(@($r.Json.data.latency.roundTripsMs).Count) / $($r.Json.data.latency.maxMs)"

    $r = Invoke-Drill 'A05-trigger-before-order' @('trigger', '--evidence', $runA)
    Add-Assertion 'DRILL-A05' '没有演练单时 trigger 拒绝，0 次 triggerEmergency' `
        ($r.ExitCode -eq 1 -and (Get-Invocations 'triggerEmergency').Count -eq 0) 'exit 1 / 0 次' "exit $($r.ExitCode) / $((Get-Invocations 'triggerEmergency').Count) 次"

    $r = Invoke-Drill 'A06-release-before-trigger' @('release', '--evidence', $runA, '--field-confirmed', $confirmation)
    Add-Assertion 'DRILL-A06' '没有发令时 release 拒绝，0 次 cancelEmergency' `
        ($r.ExitCode -eq 1 -and (Get-Invocations 'cancelEmergency').Count -eq 0) 'exit 1 / 0 次' "exit $($r.ExitCode) / $((Get-Invocations 'cancelEmergency').Count) 次"

    $r = Invoke-Drill 'A07-create-move' @('create-move', '--evidence', $runA, '--to', '12')
    $stateA = Read-DrillState $runA
    $orders = Get-Orders
    $upperIdA = "W1-DRILL-$($stateA.runId)"
    Add-Assertion 'DRILL-A07' 'create-move 恰好建一张单：本车、map 25、终点 12、upperId W1-DRILL-<runId>，状态文件记下 orderId' `
        ($r.ExitCode -eq 0 -and $orders.Count -eq 1 -and [string]$orders[0].upperId -eq $upperIdA -and
            [string]$orders[0].appointVehicleKey -eq $selfTestKey -and [int]$orders[0].missions[0].mapId -eq 25 -and
            [int]$orders[0].missions[0].destination -eq 12 -and [string]$stateA.order.orderId -eq [string]$orders[0].orderId) `
        "exit 0 / 1 单 / $upperIdA" "exit $($r.ExitCode) / $($orders.Count) 单 / $(if ($orders.Count) { $orders[0].upperId }) / state orderId $($stateA.order.orderId)"
    $orderIdA = [string]$stateA.order.orderId

    $r = Invoke-Drill 'A08-create-again' @('create-move', '--evidence', $runA, '--to', '11')
    Add-Assertion 'DRILL-A08' '同一 run 第二次 create-move 拒绝，FakeRiot 仍只 1 张单' `
        ($r.ExitCode -eq 1 -and (Get-Orders).Count -eq 1) 'exit 1 / 1 单' "exit $($r.ExitCode) / $((Get-Orders).Count) 单"

    $r = Invoke-Drill 'A09-cancel-order-before-trigger' @('cancel-order', '--evidence', $runA)
    Add-Assertion 'DRILL-A09' '发令之前 cancel-order 拒绝（有单也拒），0 次 CMD_ORDER_CANCEL，文案保留「cleanup after the stop」' `
        ($r.ExitCode -eq 1 -and (Get-Invocations 'CMD_ORDER_CANCEL').Count -eq 0 -and
            $r.Stdout -match '\[NO\] a trigger is recorded' -and $r.Message -match 'never a substitute for it') `
        'exit 1 / 0 次 / 文案' "exit $($r.ExitCode) / $((Get-Invocations 'CMD_ORDER_CANCEL').Count) 次 / $($r.Message)"

    # watch-moving first sees the vehicle at station 210, then RIoT starts the order and it leaves.
    $h = Start-Drill 'A10-watch-moving' @('watch-moving', '--evidence', $runA, '--timeout', '30')
    Start-Sleep -Milliseconds 1500
    Set-Riot "orders/$upperIdA" @{ orderState = 3; executeVehicleKey = $selfTestKey }
    Set-Vehicle @{ procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8; currentPosition = 0; orderTaskId = $orderIdA; processingOrder = $true }
    $r = Wait-Drill $h
    Add-Assertion 'DRILL-A10' 'watch-moving 先看到车在站上、车离站后在两站之间行驶时打印 READY TO TRIGGER' `
        ($r.ExitCode -eq 0 -and $r.Stdout -match 'READY TO TRIGGER') 'exit 0 + READY TO TRIGGER' "exit $($r.ExitCode) $($r.Outcome)"

    # RIoT latches about a second after the call (Round 19) and the vehicle comes to rest after that.
    $h = Start-Drill 'A11-trigger' @('trigger', '--evidence', $runA, '--observe-seconds', '20')
    if (Wait-Invocation 'triggerEmergency' 1 $h) {
        Start-Sleep -Milliseconds 800
        Set-Vehicle @{ emergencyState = 'CAN_RECOVER' }
        Start-Sleep -Milliseconds 400
        Set-Vehicle @{ speed = 0; movementState = 'MT_PAUSED' }
    }
    $r = Wait-Drill $h
    $stateA = Read-DrillState $runA
    Add-Assertion 'DRILL-A11' 'trigger 在车于两站之间行驶时发令，读到 CAN_RECOVER 闩锁与停稳（≥3 采样、≥1 s、站点不变），提示下一步 cancel-order' `
        ($r.ExitCode -eq 0 -and $stateA.trigger.latched -and [string]$stateA.trigger.latchState -eq 'CAN_RECOVER' -and $stateA.trigger.stopped -and $r.Message -match 'cancel-order') `
        'exit 0 / latched CAN_RECOVER / stopped' "exit $($r.ExitCode) / latched $($stateA.trigger.latched) $($stateA.trigger.latchState) after $($stateA.trigger.msToLatch) ms / stopped $($stateA.trigger.stopped) after $($stateA.trigger.msToStop) ms"
    $calls = Get-Invocations 'triggerEmergency'
    Add-Assertion 'DRILL-A12' 'FakeRiot 恰好收到 1 次 triggerEmergency，打在自测车的 deviceKey 上' `
        ($calls.Count -eq 1 -and [string]$calls[0].target -eq $selfTestKey) "1 次 @ $selfTestKey" "$($calls.Count) 次 @ $(if ($calls.Count) { $calls[0].target })"

    $r = Invoke-Drill 'A13-trigger-again' @('trigger', '--evidence', $runA, '--allow-stationary')
    Add-Assertion 'DRILL-A13' '第二次 trigger（即使带 --allow-stationary）拒绝，FakeRiot 仍只 1 次' `
        ($r.ExitCode -eq 1 -and (Get-Invocations 'triggerEmergency').Count -eq 1) 'exit 1 / 1 次' "exit $($r.ExitCode) / $((Get-Invocations 'triggerEmergency').Count) 次"

    # Latch CAN_RECOVER, vehicle stopped, a valid confirmation: only the ordering guard can refuse this.
    $r = Invoke-Drill 'A14-release-before-cancel-order' @('release', '--evidence', $runA, '--field-confirmed', $confirmation)
    Add-Assertion 'DRILL-A14' 'release 在 cancel-order 之前拒绝（闩锁 CAN_RECOVER、停稳、现场确认齐全也拒），提示先 cancel-order，0 次 cancelEmergency' `
        ($r.ExitCode -eq 1 -and (Get-Invocations 'cancelEmergency').Count -eq 0 -and
            $r.Stdout -match '\[NO\] cancel-order is recorded' -and $r.Message -match 'Run cancel-order first') `
        'exit 1 / 0 次 / 先 cancel-order' "exit $($r.ExitCode) / $((Get-Invocations 'cancelEmergency').Count) 次 / $($r.Message)"

    $h = Start-Drill 'A15-cancel-order' @('cancel-order', '--evidence', $runA)
    if (Wait-Invocation 'CMD_ORDER_CANCEL' 1 $h) {
        Start-Sleep -Milliseconds 300
        # The order goes; the vehicle stays held by the latch.
        Set-Riot "orders/$upperIdA" @{ orderState = 2 }
        Set-Vehicle @{ clearOrderTaskId = $true; processingOrder = $false }
    }
    $r = Wait-Drill $h
    $cancels = Get-Invocations 'CMD_ORDER_CANCEL'
    $stateA = Read-DrillState $runA
    Add-Assertion 'DRILL-A15' '急停锁着（CAN_RECOVER）、车停稳时 cancel-order 发一次 CMD_ORDER_CANCEL，打在演练单 orderId 上，读回终态；此时仍 0 次 cancelEmergency' `
        ($r.ExitCode -eq 0 -and $cancels.Count -eq 1 -and [string]$cancels[0].target -eq $orderIdA -and
            $stateA.cancelOrder.terminalObserved -and [string]$stateA.cancelOrder.latchAtCancel -eq 'CAN_RECOVER' -and
            (Get-Invocations 'cancelEmergency').Count -eq 0) `
        "exit 0 / 1 次 @ $orderIdA / 终态 / CAN_RECOVER" "exit $($r.ExitCode) / $($cancels.Count) 次 @ $(if ($cancels.Count) { $cancels[0].target }) / 终态 $($stateA.cancelOrder.terminalObserved) / $($stateA.cancelOrder.latchAtCancel)"

    $r = Invoke-Drill 'A16-cancel-order-again' @('cancel-order', '--evidence', $runA)
    Add-Assertion 'DRILL-A16' '第二次 cancel-order 拒绝，FakeRiot 仍只 1 次 CMD_ORDER_CANCEL' `
        ($r.ExitCode -eq 1 -and (Get-Invocations 'CMD_ORDER_CANCEL').Count -eq 1) 'exit 1 / 1 次' "exit $($r.ExitCode) / $((Get-Invocations 'CMD_ORDER_CANCEL').Count) 次"

    $r = Invoke-Drill 'A17-release-bad-confirmation' @('release', '--evidence', $runA, '--field-confirmed', 'looks fine to me')
    Add-Assertion 'DRILL-A17' '现场确认不是「<姓名> stopped,empty,doors-closed」时 release 拒绝，0 次 cancelEmergency' `
        ($r.ExitCode -eq 1 -and (Get-Invocations 'cancelEmergency').Count -eq 0) 'exit 1 / 0 次' "exit $($r.ExitCode) / $((Get-Invocations 'cancelEmergency').Count) 次"

    $h = Start-Drill 'A18-release' @('release', '--evidence', $runA, '--field-confirmed', $confirmation, '--observe-seconds', '15')
    if (Wait-Invocation 'cancelEmergency' 1 $h) {
        Start-Sleep -Milliseconds 500
        Set-Vehicle @{ emergencyState = 'OK'; procState = 'IDLE'; movementState = 'MT_FINISHED' }
    }
    $r = Wait-Drill $h
    $stateA = Read-DrillState $runA
    Add-Assertion 'DRILL-A18' '演练单已终态 + CAN_RECOVER + 停稳 + 现场确认下 release 发一次 cancelEmergency，并读回 emergencyState=OK' `
        ($r.ExitCode -eq 0 -and $stateA.release.okObserved -and (Get-Invocations 'cancelEmergency').Count -eq 1) `
        'exit 0 / OK / 1 次' "exit $($r.ExitCode) / okObserved $($stateA.release.okObserved) after $($stateA.release.msToOk) ms / $((Get-Invocations 'cancelEmergency').Count) 次"

    $r = Invoke-Drill 'A19-release-again' @('release', '--evidence', $runA, '--field-confirmed', $confirmation)
    Add-Assertion 'DRILL-A19' '第二次 release 拒绝，FakeRiot 仍只 1 次 cancelEmergency' `
        ($r.ExitCode -eq 1 -and (Get-Invocations 'cancelEmergency').Count -eq 1) 'exit 1 / 1 次' "exit $($r.ExitCode) / $((Get-Invocations 'cancelEmergency').Count) 次"

    $r = Invoke-Drill 'A20-summarize' @('summarize', '--evidence', $runA)
    $summaryPath = Join-Path $runA 'SUMMARY.md'
    $summary = if (Test-Path -LiteralPath $summaryPath) { Get-Content -LiteralPath $summaryPath -Raw } else { '' }
    Add-Assertion 'DRILL-A20' 'summarize 写出 SUMMARY.md：「RIoT 侧确实停车」PASS，「8005 只发一次」PASS（SENDING 1 / POST 1），写明先取消单后解除' `
        ($r.ExitCode -eq 0 -and $r.Json.data.stopProven -and $r.Json.data.sentOnce -and [int]$r.Json.data.triggerIntents -eq 1 -and
            [int]$r.Json.data.triggerPosts -eq 1 -and $summary -match 'RIoT 侧确实停车' -and $summary -match '8005 只发一次' -and
            $summary -match '先取消演练单、再解除急停' -and $summary -notmatch '与裁定的收尾顺序不符') `
        'exit 0 / PASS / PASS / 1 / 1 / 顺序' "exit $($r.ExitCode) / $($r.Json.data.stopProven) / $($r.Json.data.sentOnce) / $($r.Json.data.triggerIntents) / $($r.Json.data.triggerPosts)"

    $r = Invoke-Drill 'A21-summarize-again' @('summarize', '--evidence', $runA)
    Add-Assertion 'DRILL-A21' '第二次 summarize 拒绝，不覆盖已有 SUMMARY.md' ($r.ExitCode -eq 1) 'exit 1' "exit $($r.ExitCode)"

    $snapshotA = Get-Riot
    $snapshotA | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'fake-riot-snapshot-run-a.json') -Encoding utf8NoBOM
    $sequenceA = @(@($snapshotA.body.commandInvocations) | ForEach-Object { [string]$_.commandType })
    Add-Assertion 'DRILL-A22' 'A 全程 FakeRiot 只收到三条写命令，依次 triggerEmergency、CMD_ORDER_CANCEL、cancelEmergency 各一次；建单 1 张' `
        (($sequenceA -join ',') -eq 'triggerEmergency,CMD_ORDER_CANCEL,cancelEmergency' -and @($snapshotA.body.orders).Count -eq 1) `
        'triggerEmergency,CMD_ORDER_CANCEL,cancelEmergency / 1 单' "$($sequenceA -join ',') / $(@($snapshotA.body.orders).Count) 单"

    # ===== B: negative path -- no latch, then CAN_NOT_RECOVER =========================================
    Write-Host "`n== B: latch never engages, then CAN_NOT_RECOVER"
    Reset-Riot
    $runB = Join-Path $EvidenceRoot 'run-b-can-not-recover'
    $b = Start-MovingRun $runB 'B01' 11
    Add-Assertion 'DRILL-B01' 'B 段 init / preflight / create-move 成功（重置后的 FakeRiot，车在 210，终点 11）' `
        ((($b.ExitCodes -join ',') -eq '0,0,0') -and (Get-Orders).Count -eq 1) '0,0,0 / 1 单' "$($b.ExitCodes -join ',') / $((Get-Orders).Count) 单"

    $r = Invoke-Drill 'B02-trigger-stationary' @('trigger', '--evidence', $runB)
    Add-Assertion 'DRILL-B02' '车还停在站上（未给 --allow-stationary）时 trigger 拒绝，0 次 triggerEmergency，且没有记下发令' `
        ($r.ExitCode -eq 1 -and (Get-Invocations 'triggerEmergency').Count -eq 0 -and $null -eq (Read-DrillState $runB).trigger) `
        'exit 1 / 0 次 / 无 trigger 记录' "exit $($r.ExitCode) / $((Get-Invocations 'triggerEmergency').Count) 次"

    Set-Riot "orders/$($b.UpperId)" @{ orderState = 3; executeVehicleKey = $selfTestKey }
    Set-Vehicle @{ procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.7; currentPosition = 0; orderTaskId = $b.OrderId; processingOrder = $true }
    $r = Invoke-Drill 'B03-trigger-no-latch' @('trigger', '--evidence', $runB, '--observe-seconds', '3')
    $stateB = Read-DrillState $runB
    Add-Assertion 'DRILL-B03' '闩锁一直不锁上、车一直在动：trigger 发一次后如实报 NOT_CONFIRMED（exit 3），latched=false' `
        ($r.ExitCode -eq 3 -and -not $stateB.trigger.latched -and -not $stateB.trigger.stopped -and (Get-Invocations 'triggerEmergency').Count -eq 1) `
        'exit 3 / latched false / 1 次' "exit $($r.ExitCode) / latched $($stateB.trigger.latched) / $((Get-Invocations 'triggerEmergency').Count) 次"

    $r = Invoke-Drill 'B04-trigger-again-without-latch' @('trigger', '--evidence', $runB)
    Add-Assertion 'DRILL-B04' '闩锁没观察到时第二次 trigger 仍拒绝，FakeRiot 仍只 1 次 triggerEmergency' `
        ($r.ExitCode -eq 1 -and (Get-Invocations 'triggerEmergency').Count -eq 1) 'exit 1 / 1 次' "exit $($r.ExitCode) / $((Get-Invocations 'triggerEmergency').Count) 次"

    # The vehicle is at rest but the latch still reads OK: cancel-order needs the latch engaged.
    Set-Vehicle @{ speed = 0; movementState = 'MT_PAUSED' }
    $r = Invoke-Drill 'B05-cancel-order-unlatched' @('cancel-order', '--evidence', $runB)
    Add-Assertion 'DRILL-B05' '闩锁没锁着（emergencyState=OK）时 cancel-order 拒绝，0 次 CMD_ORDER_CANCEL' `
        ($r.ExitCode -eq 1 -and (Get-Invocations 'CMD_ORDER_CANCEL').Count -eq 0 -and $r.Stdout -match '\[NO\] latch is engaged') `
        'exit 1 / 0 次' "exit $($r.ExitCode) / $((Get-Invocations 'CMD_ORDER_CANCEL').Count) 次"

    Set-Vehicle @{ emergencyState = 'CAN_NOT_RECOVER' }
    $r = Invoke-Drill 'B06-release-before-cancel-can-not-recover' @('release', '--evidence', $runB, '--field-confirmed', $confirmation)
    Add-Assertion 'DRILL-B06' 'CAN_NOT_RECOVER 且还没取消单时 release 拒绝（提示先 cancel-order），0 次 cancelEmergency' `
        ($r.ExitCode -eq 1 -and (Get-Invocations 'cancelEmergency').Count -eq 0 -and $r.Message -match 'Run cancel-order first') `
        'exit 1 / 0 次' "exit $($r.ExitCode) / $((Get-Invocations 'cancelEmergency').Count) 次 / $($r.Message)"

    $h = Start-Drill 'B07-cancel-order-can-not-recover' @('cancel-order', '--evidence', $runB)
    if (Wait-Invocation 'CMD_ORDER_CANCEL' 1 $h) {
        Start-Sleep -Milliseconds 300
        Set-Riot "orders/$($b.UpperId)" @{ orderState = 2 }
    }
    $r = Wait-Drill $h
    Add-Assertion 'DRILL-B07' 'CAN_NOT_RECOVER 时 cancel-order 被允许（无需任何 force 参数）：发一次 CMD_ORDER_CANCEL，读回终态，提示转 RIoT 人工' `
        ($r.ExitCode -eq 0 -and (Get-Invocations 'CMD_ORDER_CANCEL').Count -eq 1 -and
            [string](Read-DrillState $runB).cancelOrder.latchAtCancel -eq 'CAN_NOT_RECOVER' -and $r.Message -match 'RIoT staff') `
        'exit 0 / 1 次 / CAN_NOT_RECOVER' "exit $($r.ExitCode) / $((Get-Invocations 'CMD_ORDER_CANCEL').Count) 次 / $($r.Message)"

    $r = Invoke-Drill 'B08-release-can-not-recover' @('release', '--evidence', $runB, '--field-confirmed', $confirmation)
    Add-Assertion 'DRILL-B08' '演练单已终态后 CAN_NOT_RECOVER 时 release 仍拒绝、提示转 RIoT 人工，0 次 cancelEmergency' `
        ($r.ExitCode -eq 1 -and (Get-Invocations 'cancelEmergency').Count -eq 0 -and $r.Stdout -match '\[ok\] cancel-order is recorded' -and
            $r.Message -match 'CAN_NOT_RECOVER' -and $r.Message -match 'RIoT staff') `
        'exit 1 / 0 次 / 转 RIoT' "exit $($r.ExitCode) / $((Get-Invocations 'cancelEmergency').Count) 次 / $($r.Message)"

    $r = Invoke-Drill 'B09-summarize' @('summarize', '--evidence', $runB)
    $summaryB = Get-Content -LiteralPath (Join-Path $runB 'SUMMARY.md') -Raw
    Add-Assertion 'DRILL-B09' 'B 段摘要：停车「未证实」、只发一次 PASS、演练单已清理、解除「不适用」并写明 CAN_NOT_RECOVER 转 RIoT 人工' `
        ($r.ExitCode -eq 0 -and -not $r.Json.data.stopProven -and $r.Json.data.sentOnce -and
            $summaryB -match 'CAN_NOT_RECOVER' -and $summaryB -match '转 RIoT 人工' -and $summaryB -notmatch 'force-after-can-not-recover') `
        'exit 0 / false / true' "exit $($r.ExitCode) / $($r.Json.data.stopProven) / $($r.Json.data.sentOnce)"
    $sequenceB = @(@((Get-Riot).body.commandInvocations) | ForEach-Object { [string]$_.commandType })
    Add-Assertion 'DRILL-B10' 'B 全程 FakeRiot 只收到 triggerEmergency、CMD_ORDER_CANCEL 各一次，没有 cancelEmergency' `
        (($sequenceB -join ',') -eq 'triggerEmergency,CMD_ORDER_CANCEL') 'triggerEmergency,CMD_ORDER_CANCEL' ($sequenceB -join ',')
    Get-Riot | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'fake-riot-snapshot-run-b.json') -Encoding utf8NoBOM

    # ===== D: the cancel never lands -- stop and report, never release first ===========================
    Write-Host "`n== D: CMD_ORDER_CANCEL accepted but the order never goes terminal"
    Reset-Riot
    $runD = Join-Path $EvidenceRoot 'run-d-cancel-not-confirmed'
    $d = Start-MovingRun $runD 'D01' 12
    Set-Riot "orders/$($d.UpperId)" @{ orderState = 3; executeVehicleKey = $selfTestKey }
    Set-Vehicle @{ procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8; currentPosition = 0; orderTaskId = $d.OrderId; processingOrder = $true }
    $h = Start-Drill 'D02-trigger' @('trigger', '--evidence', $runD, '--observe-seconds', '20')
    if (Wait-Invocation 'triggerEmergency' 1 $h) {
        Start-Sleep -Milliseconds 800
        Set-Vehicle @{ emergencyState = 'CAN_RECOVER'; speed = 0; movementState = 'MT_PAUSED' }
    }
    $rt = Wait-Drill $h
    # Nothing reacts to the cancel: the order stays EXECUTING, as if RIoT would not cancel a latched vehicle's order.
    $r = Invoke-Drill 'D03-cancel-order-never-terminal' @('cancel-order', '--evidence', $runD)
    Add-Assertion 'DRILL-D01' 'RIoT 接受取消却 20 秒内不见终态：cancel-order 发一次后报 NOT_CONFIRMED（exit 3），提示停下回报用户、不要改用先解除' `
        ((($d.ExitCodes -join ',') -eq '0,0,0') -and $rt.ExitCode -eq 0 -and $r.ExitCode -eq 3 -and
            (Get-Invocations 'CMD_ORDER_CANCEL').Count -eq 1 -and $r.Message -match 'STOP: report to the user' -and
            $r.Message -match 'Do NOT switch to releasing the emergency stop first') `
        'exit 3 / 1 次 / 停下回报' "setup $($d.ExitCodes -join ','), trigger $($rt.ExitCode) / exit $($r.ExitCode) / $((Get-Invocations 'CMD_ORDER_CANCEL').Count) 次 / $($r.Message)"

    $r = Invoke-Drill 'D04-release-after-unconfirmed-cancel' @('release', '--evidence', $runD, '--field-confirmed', $confirmation)
    $r2 = Invoke-Drill 'D05-cancel-order-again' @('cancel-order', '--evidence', $runD)
    Add-Assertion 'DRILL-D02' '取消单未证实时 release 在 CAN_RECOVER 下仍拒绝、cancel-order 也不再发；0 次 cancelEmergency、仍 1 次 CMD_ORDER_CANCEL' `
        ($r.ExitCode -eq 1 -and $r.Stdout -match '\[NO\] cancel-order is recorded' -and $r2.ExitCode -eq 1 -and
            (Get-Invocations 'cancelEmergency').Count -eq 0 -and (Get-Invocations 'CMD_ORDER_CANCEL').Count -eq 1) `
        'exit 1 / exit 1 / 0 / 1' "exit $($r.ExitCode) / exit $($r2.ExitCode) / $((Get-Invocations 'cancelEmergency').Count) / $((Get-Invocations 'CMD_ORDER_CANCEL').Count)"

    # ===== C: offline init guards -- no RIoT contact at all ============================================
    Write-Host "`n== C: init guards"
    $refusedRuns = @(
        @{ Label = 'C01-init-agv01-fake'; Key = $agv01Key; Url = $riotBase; Fake = $true; Map = '25' },
        @{ Label = 'C01-init-agv01'; Key = $agv01Key; Url = $riotBase; Fake = $false; Map = '25' },
        @{ Label = 'C02-init-other-key'; Key = $otherKey; Url = $riotBase; Fake = $true; Map = '25' },
        @{ Label = 'C03-init-selftest-without-fake'; Key = $selfTestKey; Url = $riotBase; Fake = $false; Map = '25' },
        # TEST-NET-1 (RFC 5737). init makes no network call, so nothing is ever sent there.
        @{ Label = 'C04-init-selftest-non-loopback'; Key = $selfTestKey; Url = 'http://192.0.2.10:8888'; Fake = $true; Map = '25' },
        @{ Label = 'C05-init-map-26'; Key = $selfTestKey; Url = $riotBase; Fake = $true; Map = '26' }
    )
    $results = @{}
    foreach ($case in $refusedRuns) {
        $dir = Join-Path $EvidenceRoot "run-$($case.Label)"
        $arguments = @('init', '--evidence', $dir, '--device-key', $case.Key, '--map-id', $case.Map, '--riot-base-url', $case.Url)
        if ($case.Fake) { $arguments += '--fake-riot' }
        $r = Invoke-Drill $case.Label $arguments
        $results[$case.Label] = [pscustomobject]@{ ExitCode = $r.ExitCode; Created = (Test-Path -LiteralPath $dir) }
    }
    Add-Assertion 'DRILL-C01' 'init 拒绝 agv01 的 key（带不带 --fake-riot 都拒），不建目录' `
        ($results['C01-init-agv01-fake'].ExitCode -eq 1 -and -not $results['C01-init-agv01-fake'].Created -and
            $results['C01-init-agv01'].ExitCode -eq 1 -and -not $results['C01-init-agv01'].Created) `
        'exit 1 ×2 / 无目录' "$($results['C01-init-agv01-fake'].ExitCode),$($results['C01-init-agv01'].ExitCode) / created $($results['C01-init-agv01-fake'].Created),$($results['C01-init-agv01'].Created)"
    Add-Assertion 'DRILL-C02' 'init 拒绝 agv02 以外的任何 key（agv03 及其它）' `
        ($results['C02-init-other-key'].ExitCode -eq 1 -and -not $results['C02-init-other-key'].Created) 'exit 1' "exit $($results['C02-init-other-key'].ExitCode)"
    Add-Assertion 'DRILL-C03' 'init 拒绝不带 --fake-riot 的自测 key' `
        ($results['C03-init-selftest-without-fake'].ExitCode -eq 1) 'exit 1' "exit $($results['C03-init-selftest-without-fake'].ExitCode)"
    Add-Assertion 'DRILL-C04' 'init 拒绝 --fake-riot 配非回环地址' `
        ($results['C04-init-selftest-non-loopback'].ExitCode -eq 1) 'exit 1' "exit $($results['C04-init-selftest-non-loopback'].ExitCode)"
    Add-Assertion 'DRILL-C05' 'init 拒绝 map 25 以外的地图' `
        ($results['C05-init-map-26'].ExitCode -eq 1) 'exit 1' "exit $($results['C05-init-map-26'].ExitCode)"

    $stateBefore = Get-Content -LiteralPath (Join-Path $runA 'drill-state.json') -Raw
    $r = Invoke-Drill 'C06-init-reuse' @('init', '--evidence', $runA, '--device-key', $selfTestKey, '--map-id', '25', '--riot-base-url', $riotBase, '--fake-riot')
    Add-Assertion 'DRILL-C06' 'init 拒绝复用已完成 run 的目录，原状态文件不变' `
        ($r.ExitCode -eq 1 -and (Get-Content -LiteralPath (Join-Path $runA 'drill-state.json') -Raw) -eq $stateBefore) 'exit 1 / 不变' "exit $($r.ExitCode)"

    $runAgv02 = Join-Path $EvidenceRoot 'run-c07-init-agv02-offline'
    $r = Invoke-Drill 'C07-init-agv02' @('init', '--evidence', $runAgv02, '--device-key', $agv02Key, '--map-id', '25', '--riot-base-url', $riotBase)
    Add-Assertion 'DRILL-C07' 'init 接受 agv02 的 key（只建目录与状态文件，init 不连 RIoT，本 run 之后不再使用）' `
        ($r.ExitCode -eq 0 -and [string](Read-DrillState $runAgv02).vehicleAlias -eq 'agv02') 'exit 0 / agv02' "exit $($r.ExitCode)"

    $r = Invoke-Drill 'C08-unknown-option' @('trigger', '--evidence', $runA, '--allow-stationery')
    Add-Assertion 'DRILL-C08' '拼错的选项（--allow-stationery）是用法错误，不当作「没给」' ($r.ExitCode -eq 2) 'exit 2' "exit $($r.ExitCode)"

    $r = Invoke-Drill 'C09-removed-force-option' @('cancel-order', '--evidence', $runA, '--force-after-can-not-recover', 'x')
    Add-Assertion 'DRILL-C09' '已删除的 --force-after-can-not-recover 是用法错误' ($r.ExitCode -eq 2) 'exit 2' "exit $($r.ExitCode)"

    # ===== global =====================================================================================
    # @() around the whole pipeline: one unique host would otherwise come back as a bare string, and
    # strict mode has no .Count on that. The first self-test run (drill-selftest-001) aborted here.
    $wireHosts = @(@(foreach ($run in @($runA, $runB, $runD)) {
                Get-Content -LiteralPath (Join-Path $run 'wire.jsonl') | ForEach-Object { ($_ | ConvertFrom-Json).host }
            }) | Sort-Object -Unique)
    Add-Assertion 'DRILL-G01' '各段 run 的每个 HTTP 请求都只发往本脚本起的回环 FakeRiot' `
        ($wireHosts.Count -eq 1 -and $wireHosts[0] -eq "127.0.0.1:$port") "127.0.0.1:$port" ($wireHosts -join ', ')
} catch {
    $failed = $true
    Write-Host "SELF-TEST ABORTED: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
} finally {
    foreach ($process in $started) {
        try {
            if (-not $process.HasExited) {
                $process.Kill($true)
                $null = $process.WaitForExit(10000)
            }
        } catch {
            Write-Warning "could not stop process $($process.Id): $_"
        }
    }
    $env:CONTROL_SERVER_RIOT_CALL_API_KEY = $previousKey
}

# After every process is gone, so nothing can still be writing.
$leftRunning = @($started | Where-Object { -not $_.HasExited })
Add-Assertion 'DRILL-G02' '本脚本起的进程（FakeRiot 与每条工具命令）全部已停' ($leftRunning.Count -eq 0) '0 个在跑' "$($leftRunning.Count) 个在跑"
$leaks = @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File | Select-String -SimpleMatch -Pattern $secret -List)
Add-Assertion 'DRILL-G03' 'API key 的值不出现在自测目录的任何文件里（证据、状态、stdout 日志、FakeRiot 日志、快照）' `
    ($leaks.Count -eq 0) '0 个文件' "$($leaks.Count) 个文件$(if ($leaks.Count) { ': ' + (($leaks | ForEach-Object Path) -join ', ') })"
$locks = @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File -Filter 'drill.lock')
Add-Assertion 'DRILL-G04' '没有遗留的 drill.lock' ($locks.Count -eq 0) '0' "$($locks.Count)"

$assertions | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'assertions.json') -Encoding utf8NoBOM
$failures = @($assertions | Where-Object { -not $_.passed })
Write-Host ''
Write-Host "Evidence: $EvidenceRoot"
Write-Host "Assertions: $($assertions.Count), passed $($assertions.Count - $failures.Count), failed $($failures.Count)$(if ($failed) { ', run aborted' })"
if ($failed -or $failures.Count -gt 0) {
    exit 1
}
exit 0
