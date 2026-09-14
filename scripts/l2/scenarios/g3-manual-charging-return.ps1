#Requires -Version 7

<#
G3 `FP-IS-07`：手动充电后由管理员在车上申请返回服务。协议向量 `CV-MANUAL-CHARGING-RETURN`：
`ManualChargingReturnToServiceRequested` → `ManualChargingReturnToServiceResult`；服务端 `REEVALUATE_ELIGIBILITY_AFTER_RETURN`、
`REQUIRE_VERIFIED_ADMINISTRATOR`，车载端 `REQUEST_RETURN_WITH_OPERATOR_CONTEXT`、`NEVER_CLEAR_HOLD_LOCALLY`。

**两次申请。**服务端判返回服务的依据是会话能不能接受重新评估：会话还有事实没对账（`RecoveryRequired`）就拒，
否则受理为 `RETURNED_TO_ELIGIBILITY_EVALUATION`。所以场景先把一个不相干的仓（8 号）的锁反馈固定成未锁，让车不能发车、
会话转 `RecoveryRequired`，申请一次应被拒；放开锁反馈、会话回到 `Ready`，再申请一次应被受理。两次的判定都只来自服务端。

**车载端那一半的「不在本地清保持」**看不到服务端的库，由车载端 G2 测试证；这里核对的是请求带着车上配置的管理员、
服务端据会话当时的状态作答，以及申请本身不产生任何业务或物理副作用。「充电后返回服务」按钮是 2026-09-14 补的入口
（车载端仓 `docs/W2G_FP_IS_07_OPERATOR_ENTRIES.md`）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'G3RecoveryCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$onboard = $Context.Onboard
$simulator = $Context.Simulator
$connection = $Context.Connection
$bystanderSlot = 8
$ids = @('G3-07-51', 'G3-07-52', 'G3-07-53', 'G3-07-54')

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: the return request is sent from the onboard HMI.'
}

# Get-G3Inbound hands its list over as one object. Collected with @() that is a list of one list, whose Count
# is 1 however many requests arrived (manual-001/003 timed out on exactly that). The parentheses take the list,
# the pipe spreads it, and the comma hands the flat list back as one value for a plain assignment.
function Get-Requests {
    return , @((Get-G3Inbound $connection 'ManualChargingReturnToServiceRequested') | Where-Object { $true })
}

function Invoke-ReturnRequest([int]$expectedCount, [string]$criterion) {
    $offered = Wait-G3ButtonOffered $onboard $journal '充电后返回服务' "$criterion-entry" 90
    if (-not $offered) { return $null }
    $journal.Note('Administrator presses 充电后返回服务 and confirms.')
    $null = Invoke-G3ConfirmedButton $onboard $journal '充电后返回服务' '充电后返回服务'
    return Wait-L2Condition -Description 'the server answered the return-to-service request' `
        -Journal $journal -Criterion $criterion -TimeoutSeconds 60 `
        -Probe { $all = Get-Requests; if ($all.Count -ge $expectedCount -and $null -ne $all[$expectedCount - 1].ResponseLine) { $all[$expectedCount - 1] } else { $null } } `
        -Until { param($v) $null -ne $v }
}

$null = Wait-L2Condition -Description 'the onboard session is ready' `
    -Journal $journal -Criterion 'session-readiness' -TimeoutSeconds 180 `
    -Probe { [string](Get-G3Session $connection).Readiness } -Until { param($v) $v -eq 'Ready' }

# --- 1. 车不能发车时申请：应被拒 --------------------------------------------------------------------

$journal.Note("Slot $bystanderSlot lock feedback forced open: the vehicle is not departure-safe, the session needs recovery.")
$null = $simulator.Command('Put', "slots/$bystanderSlot/lock-feedback-override", @{ mode = 'FIXED_0' })
$null = Wait-L2Condition -Description 'the session needs recovery' `
    -Journal $journal -Criterion 'session-readiness' -TimeoutSeconds 60 `
    -Probe { [string](Get-G3Session $connection).Readiness } -Until { param($v) $v -eq 'RecoveryRequired' }
$readinessAtFirst = [string](Get-G3Session $connection).Readiness
$first = Invoke-ReturnRequest 1 'return-request-1'
if ($null -eq $first) {
    Add-G3NotReached $assertions $ids '车载端没有给出「充电后返回服务」入口'
    $null = $simulator.Command('Put', "slots/$bystanderSlot/lock-feedback-override", @{ mode = 'AUTO' })
    return
}
if ([string]$first.ResponsePayload.outcome -ne 'RETURNED_TO_ELIGIBILITY_EVALUATION') {
    try { $null = Confirm-G3Notice $onboard '返回服务未受理' }
    catch { $journal.Note("The rejection notice was not answered: $($_.Exception.Message)") }
}

# --- 2. 车恢复就绪后再申请：应被受理 ----------------------------------------------------------------

$journal.Note("Slot $bystanderSlot lock feedback released: the vehicle is departure-safe again.")
$null = $simulator.Command('Put', "slots/$bystanderSlot/lock-feedback-override", @{ mode = 'AUTO' })
$null = Wait-L2Condition -Description 'the session is ready again' `
    -Journal $journal -Criterion 'session-readiness' -TimeoutSeconds 60 `
    -Probe { [string](Get-G3Session $connection).Readiness } -Until { param($v) $v -eq 'Ready' }
$readinessAtSecond = [string](Get-G3Session $connection).Readiness
$second = Invoke-ReturnRequest 2 'return-request-2'
if ($null -eq $second) {
    Add-G3NotReached $assertions $ids '就绪之后车载端没有再给出「充电后返回服务」入口'
    return
}
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

$requests = Get-Requests
$correlated = @($requests | Where-Object {
    $_.Response -eq 'ManualChargingReturnToServiceResult' -and [string]$_.ResponsePayload.requestId -eq [string]$_.Payload.requestId -and
    [string]$_.ResponseLine.correlationId -eq $_.MessageId })
$assertions.Add(
    'G3-07-51',
    '消息顺序与向量一致：每次申请 ManualChargingReturnToServiceRequested 都得到一份关联到它的 ManualChargingReturnToServiceResult，两次申请请求号不同（CV-MANUAL-CHARGING-RETURN orderedExpectedMessages）',
    ($requests.Count -eq 2 -and $correlated.Count -eq 2 -and [string]$requests[0].Payload.requestId -ne [string]$requests[1].Payload.requestId -and $requests[0].At -lt $requests[1].At),
    '申请 2 / 关联应答 2 / 请求号不同',
    "申请 $($requests.Count) / 关联应答 $($correlated.Count) / 请求号 $(($requests | ForEach-Object { $_.Payload.requestId }) -join ', ')")

$rows = (Invoke-L2Query -Connection $connection -Sql (
    'SELECT RequestId, AdministratorId, AdministratorRole, Outcome, ProblemReasonCode FROM ManualChargingReturnToServiceRequests ORDER BY DecidedAt'))
$adminOk = $rows.Count -eq 2 -and @($rows | Where-Object {
    (Test-G3Present $_.AdministratorId) -and [string]$_.AdministratorRole -eq 'MAINTENANCE_ADMINISTRATOR' }).Count -eq 2 -and
    @($requests | Where-Object { (Test-G3Present $_.Payload.administrator.operatorId) -and (Test-G3Present $_.Payload.administrator.verificationMethod) }).Count -eq 2 -and
    [string]$rows[0].AdministratorId -eq [string]$requests[0].Payload.administrator.operatorId
$assertions.Add(
    'G3-07-52',
    '申请带着已验证的管理员：两次请求都带车上配置的管理员号与核验方式，服务端记下同一管理员与 MAINTENANCE_ADMINISTRATOR 角色（REQUIRE_VERIFIED_ADMINISTRATOR / REQUEST_RETURN_WITH_OPERATOR_CONTEXT）',
    $adminOk,
    '2 行 / 管理员号一致 / MAINTENANCE_ADMINISTRATOR',
    "$($rows.Count) 行 / $(($rows | ForEach-Object { "$($_.AdministratorId)($($_.AdministratorRole))" }) -join ', ')")

$assertions.Add(
    'G3-07-53',
    '服务端据会话当时的状态重新评估：会话 RecoveryRequired 时拒绝（带原因），会话 Ready 时受理为 RETURNED_TO_ELIGIBILITY_EVALUATION（REEVALUATE_ELIGIBILITY_AFTER_RETURN）',
    ($rows.Count -eq 2 -and $readinessAtFirst -eq 'RecoveryRequired' -and [string]$rows[0].Outcome -eq 'REJECTED' -and (Test-G3Present $rows[0].ProblemReasonCode) -and
        $readinessAtSecond -eq 'Ready' -and [string]$rows[1].Outcome -eq 'RETURNED_TO_ELIGIBILITY_EVALUATION'),
    'RecoveryRequired → REJECTED(原因) / Ready → RETURNED_TO_ELIGIBILITY_EVALUATION',
    "$readinessAtFirst → $(if ($rows.Count -ge 1) { "$($rows[0].Outcome)($($rows[0].ProblemReasonCode))" }) / $readinessAtSecond → $(if ($rows.Count -ge 2) { $rows[1].Outcome })")

$sessionAfter = Get-G3Session $connection
$demands = Get-G3Count $connection 'SELECT COUNT(*) AS Total FROM AcceptedDemands'
$operations = Get-G3Count $connection 'SELECT COUNT(*) AS Total FROM StationOperations'
$progress = Get-G3Count $connection "SELECT COUNT(*) AS Total FROM ProtocolInbox WHERE MessageType = 'OperationProgress'"
$orders = @($riot.Snapshot().body.orders).Count
$physical = (1..8 | ForEach-Object { Get-G3SlotState $simulator $_ } | Sort-Object -Unique) -join ' '
$assertions.Add(
    'G3-07-54',
    '申请本身没有副作用：会话仍 Ready，没有需求、仓位操作、开锁进度或 RIoT 单，八个仓都关着、空、锁上（forbidden duplicate-riot-order、duplicate-slot-unlock、ready-before-reconciliation / NO_UNPROVEN_STATE）',
    ([string]$sessionAfter.Readiness -eq 'Ready' -and $demands -eq 0 -and $operations -eq 0 -and $progress -eq 0 -and $orders -eq 0 -and $physical -eq 'CLOSED/EMPTY/1/0'),
    'Ready / 需求 0 / 操作 0 / 进度 0 / RIoT 单 0 / CLOSED/EMPTY/1/0',
    "$($sessionAfter.Readiness) / 需求 $demands / 操作 $operations / 进度 $progress / RIoT 单 $orders / $physical")

$journal.Note('FP-IS-07: a return to service after manual charging was refused while the session needed recovery and accepted once it was ready.')
