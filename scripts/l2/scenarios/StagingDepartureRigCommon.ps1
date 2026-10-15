#Requires -Version 7

<#
program#184 第一波：三条真装置待送点出发场景共用的驱动（real-onboard-staging-depart-partial-load、
real-onboard-staging-depart-nothing-loaded、real-onboard-staging-low-battery-departs-with-cargo）。由各场景点号引入，不是场景本身，
没有 setup 文件，编排器不会单独运行它（与 MultiStopRigCommon.ps1、CargoHoldingCommon.ps1 同一种安排）。它自己点号引入
MultiStopRigCommon.ps1（真装置的录入装货、卸货），那个文件一字不改。

**为什么要有这三条。**出发往返（车载屏按「出发」→ 服务端 CurrentStopDepartureResult → 放回／离站）在 control-server#713 与
onboard-hmi#349 合入时，只在两端各自的假对端上验证过：服务端的 staging-depart-* 合成场景由合成对端直接发请求，车载端的 G2 由假服务端
直接答。两端真组件之间还没有按过一次出发（program#184 评论，hmi#349 真装置 run 38675942172 只证了 v4 握手与补偿后重连）。

**按「出发」走的是操作员那条路**：等 AutomationId 为 CurrentStopDeparture 的按钮可按、并稳定一秒（刚出现的按钮布局未完，按了落空，
L2RealOnboard.psm1 的 Invoke-L2RealConfirmedButton 写了为什么），按下，在标题为「出发」的确认框里点「是」（IDYES = 6）。按 AutomationId
找按钮而不按名字：本站摘要、状态行里都有「出发」两个字。

**判据从两边读**：请求与答复从服务端库里读（ProtocolInbox / ProtocolOutbox，确认时刻 AcknowledgedAt），车载端的结果从 UIA 读
（CurrentStopDepartureStatus、LoadingClosedReason 的 ItemStatus，onboard-hmi#349 给屏幕状态快照与 UIA 共用的那组编码）。

**探针不取闭包**，理由见 CargoHoldingCommon.ps1 开头。
#>

Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'MultiStopRigCommon.ps1')

# 与三条场景 setup 文件的 Stations、TaskTypeStations 同一份事实。
$script:StagingStationRiotId = 305
$script:StagingStationName = '派工待送取货'

# 一条 STAGING_TO_WIRE 需求；New-L2CargoDemand 的形状加上 workType，子批号带场景前缀便于在证据里认。
function New-L2StagingDemand([string]$Label, [string]$Area, [string]$Prefix, [string]$RunId) {
    $demand = New-L2CargoDemand $Label $Area 1 $RunId
    $demand.Sublot = "$Prefix-$Label-$RunId"
    return $demand
}

function Publish-L2StagingDemand([object]$Context, [hashtable]$Demand, [string]$Eqp) {
    $Context.Journal.Note("Publishing STAGING_TO_WIRE demand $($Demand.Label) $($Demand.Wire) (area $($Demand.Area), sublot $($Demand.Sublot)).")
    $null = $Context.MesIngest.Command('Put', "demands/$($Demand.Wire)", @{
        sublot      = $Demand.Sublot
        area        = $Demand.Area
        eqp         = $Eqp
        package     = 'L2-PACKAGE'
        maxBoxCount = 4 * $Demand.Baskets
        workType    = 'STAGING_TO_WIRE'
    })
}

# 一条需求此刻的归属：最近加入的那一行。
function Get-L2StagingMembership([object]$Connection, [string]$DemandId) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT JourneyId, PickupStopId, Status, RemovedAt, IFNULL(RemovalReason, '') AS RemovalReason FROM JourneyDemands " +
        "WHERE DemandId = '$DemandId' ORDER BY AddedAt DESC LIMIT 1")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-L2StagingJourney([object]$Connection, [string]$JourneyId) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT JourneyId, Stage, IFNULL(BlockReasonCode, '') AS BlockReasonCode, IFNULL(LoadingPhaseState, '') AS LoadingPhaseState, " +
        "IFNULL(LoadingClosedReason, '') AS LoadingClosedReason FROM JourneyRuntimes WHERE JourneyId = '$JourneyId'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# 真车载端发来的出发请求（服务端收件箱），按收到的先后。
function Get-L2DepartureRequests([object]$Connection) {
    return , @(Get-L2RealInbound $Connection 'CurrentStopDepartureRequested')
}

# 服务端对某一条出发请求的答复：payload、是否已被车载端确认。
function Get-L2DepartureResult([object]$Connection, [string]$DepartureRequestId) {
    $match = @((Get-L2RealOutbound $Connection 'CurrentStopDepartureResult') | Where-Object {
            [string]$_.Payload.departureRequestId -eq $DepartureRequestId })
    if ($match.Count -eq 0) { return $null }
    return $match[0]
}

# 离站核验的目的站，按发出的先后。
function Get-L2DepartureCheckTargets([object]$Connection) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'PreDepartureSafetyCheck' ORDER BY CreatedAt, MessageId")
    return , @(foreach ($row in $rows) { [string]([string]$row.PayloadJson | ConvertFrom-Json -Depth 20).payload.targetStationId })
}

# 「出发」按钮此刻可不可按：不在树上、不可用都是否。
function Test-L2DepartureButtonPressable([object]$Onboard) {
    try {
        $button = $Onboard.Element('AutomationId', 'CurrentStopDeparture')
        return $null -ne $button -and [bool]$button.Current.IsEnabled -and -not [bool]$button.Current.IsOffscreen
    } catch [System.Windows.Automation.ElementNotAvailableException] {
        return $false
    }
}

<#
操作员按「出发」并在确认框里点「是」。先等按钮可按并稳定一秒（$OfferSeconds 内等不到就抛 RIG_PRECONDITION：车载端没给出入口，
后面的判据都无从谈起，那是场景前提不成立，由调用方记成未到达）。按了十秒内没有确认框就再按，至多三次：没有确认框就没有请求离开车，
再按不会重复。返回按下的时刻（确认框答完的那一刻）。
#>
function Invoke-L2DeparturePress([object]$Context, [int]$OfferSeconds = 60) {
    $onboard = $Context.Onboard
    $journal = $Context.Journal
    $offered = Wait-L2RealOrLast -Description 'the HMI offers 出发 at the staging stop' -Journal $journal -Criterion 'departure-offered' `
        -TimeoutSeconds $OfferSeconds -Probe { Test-L2DepartureButtonPressable $onboard } -Until { param($v) $v }
    if (-not $offered) {
        $notice = Get-L2LoadingPhaseLine $onboard 'CurrentStopDepartureNotice'
        throw "RIG_PRECONDITION: the HMI did not offer 出发 within ${OfferSeconds}s (notice code '$notice')."
    }
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $stableSince = $null
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            if (Test-L2DepartureButtonPressable $onboard) {
                if ($null -eq $stableSince) { $stableSince = [DateTimeOffset]::UtcNow }
                elseif (([DateTimeOffset]::UtcNow - $stableSince).TotalSeconds -ge 1) { break }
            } else { $stableSince = $null }
            Start-Sleep -Milliseconds 200
        }
        $journal.Note("Pressing 出发 (attempt $attempt); summary '$(Get-L2LoadingPhaseLine $onboard 'StagingSummary')'.")
        $button = $onboard.Element('AutomationId', 'CurrentStopDeparture')
        if ($null -eq $button) { throw 'The 出发 button left the automation tree between the stability check and the press.' }
        # Posted, like every entry button: the handler goes straight into the confirmation MessageBox.
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        if (Invoke-L2RealDialogButton $onboard $journal '出发' '6' 10) { return [DateTimeOffset]::UtcNow }
        $journal.Note("No '出发' confirmation after pressing (attempt $attempt).")
    }
    throw "Pressing 出发 never raised the '出发' confirmation in 3 attempts."
}

<#
按下之后两端各自看到了什么：服务端收到真车载端发来的那一条请求（按下之后的第一条）、答了它，车载端确认了答复，屏上状态行读到
$ExpectedStatus。返回 Request、Result、ScreenStatus，等不到的为 $null；不在这里断言，由场景按自己的判据号断言。
#>
function Wait-L2DepartureRoundTrip([object]$Context, [DateTimeOffset]$PressedAt, [string]$ExpectedStatus = 'ACCEPTED') {
    $connection = $Context.Connection
    $journal = $Context.Journal
    $onboard = $Context.Onboard
    # The inbox stamps the server's clock, the press the scenario's: one machine, one clock. $PressedAt is taken once the
    # dialog is seen closed, a moment after Yes sent the request, so the window opens well before it; no earlier request
    # exists in these scenarios, which press once.
    $after = $PressedAt.AddSeconds(-15)
    $request = Wait-L2RealOrLast -Description 'the server received CurrentStopDepartureRequested from the real onboard' `
        -Journal $journal -Criterion 'departure-request-received' -TimeoutSeconds 30 `
        -Probe { @((Get-L2DepartureRequests $connection) | Where-Object { $_.At -gt $after }) | Select-Object -First 1 } `
        -Until { param($v) $null -ne $v }
    $result = $null
    if ($null -ne $request) {
        $requestId = [string]$request.Payload.departureRequestId
        $result = Wait-L2RealOrLast -Description 'the server answered the departure request and the onboard acknowledged it' `
            -Journal $journal -Criterion 'departure-result-acknowledged' -TimeoutSeconds 60 `
            -Probe { Get-L2DepartureResult $connection $requestId } `
            -Until { param($v) $null -ne $v -and $v.Acknowledged }
    }
    $screen = Wait-L2RealOrLast -Description "the HMI's departure status reads $ExpectedStatus" -Journal $journal `
        -Criterion 'departure-status-on-screen' -TimeoutSeconds 30 `
        -Probe { Get-L2LoadingPhaseLine $onboard 'CurrentStopDepartureStatus' } -Until { param($v) $v -eq $ExpectedStatus }
    return [pscustomobject]@{ Request = $request; Result = $result; ScreenStatus = $screen }
}

function Format-L2DepartureRoundTrip([object]$RoundTrip) {
    $request = if ($null -eq $RoundTrip.Request) { '(no request)' } else {
        "request $($RoundTrip.Request.Payload.departureRequestId) by $($RoundTrip.Request.Payload.operator.operatorId) @$($RoundTrip.Request.Payload.stationId)" }
    $result = if ($null -eq $RoundTrip.Result) { '(no result)' } else {
        "$($RoundTrip.Result.Payload.outcome)$(if ($RoundTrip.Result.Acknowledged) { ' acked' } else { ' not acked' })" }
    return "$request / $result / screen '$($RoundTrip.ScreenStatus)'"
}

# 往返成立：请求是 L2-OPERATOR 从待送站发的，答复是 $Outcome 且已被车载端确认，屏上状态行与答复一致。
function Test-L2DepartureRoundTrip([object]$RoundTrip, [string]$Outcome = 'ACCEPTED') {
    return $null -ne $RoundTrip.Request -and $null -ne $RoundTrip.Result -and
        [string]$RoundTrip.Request.Payload.operator.operatorId -eq 'L2-OPERATOR' -and
        [string]$RoundTrip.Request.Payload.stationId -eq $script:StagingStationName -and
        [string]$RoundTrip.Result.Payload.outcome -eq $Outcome -and $RoundTrip.Result.Acknowledged -and
        [string]$RoundTrip.ScreenStatus -eq $Outcome
}
