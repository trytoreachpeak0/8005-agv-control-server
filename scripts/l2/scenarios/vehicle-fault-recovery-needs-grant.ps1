#Requires -Version 7

<#
故障人工恢复入口要按车限时授权（control-server#797，ADR-cross-0066 决策 1～4，REQ-0235 按 CP-0022 修订）。

**为什么要有这条场景。**10-21 起生产 v2 的故障人工恢复入口常开，拿着那把共享凭据的人就能对任何一辆车发 CLEAR_FAULT、
RESUME_HELD_ORDER。本票把它收成「维护人员在控制端给这辆车下限时授权，入口逐请求查」。L1 证的是判定本身；这条场景在真的
服务端进程里走一遍：授权入口真的挂出来了、清扫与登记簿真的在同一个进程里、要授权的开关真的从配置读到了。

1. 车在两站之间行驶，在途单被报 FAILED，服务端判疑似故障（与 vehicle-fault-operator-clearance 同一形状），再摆回站上空闲。
2. 没有授权：CLEAR_FAULT 回 403，理由 RECOVERY_GRANT_MISSING；之后几轮故障仍在（第二个事实）。
3. 演练：授权入口回 PLANNED 与确认码，什么都没下（列表为空，审计不落行）。
4. 带码下发，只点名 CLEAR_FAULT、只对车：GRANTED，列表里有这辆车，审计落一行 RECOVERY_GRANT Succeeded。
5. 授权没点名的动作（RESUME_HELD_ORDER）：403 RECOVERY_GRANT_ACTION_NOT_GRANTED，故障不动。
6. 点名的 CLEAR_FAULT：200 Cleared，故障清掉。
7. 收回：REVOKED；同一个请求再来一次是 403 RECOVERY_GRANT_MISSING，不是 AlreadyCleared（授权在判定之前查）。
8. 下一条 1 分钟的授权，等它在服务端自己到点（列表变空），再来一次：403 RECOVERY_GRANT_MISSING。

**不在这条场景里**：车上开异常处置会话那一半（10-21 agv02 那一形：申请恢复 → 维修后恢复 → 卸货提交）。合成对端不发起
恢复会话（load-result-requires-recovery 开头的说明），那一段由车载端票 onboard-hmi#391 用真车载端联跑。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection
$agvId = [string]$Context.AgvId
$vehicleKey = [string]$Context.VehicleKey
$base = "http://127.0.0.1:$($Context.HealthPort)"
$recoveryUri = "$base/api/safety/v1/vehicle-fault-recoveries"
$grantUri = "$base/api/field-ops/v1/recovery-grants"
$revokeUri = "$base/api/field-ops/v1/recovery-grant-revocations"
if (-not $Context.FaultRecoveryCredential) {
    throw 'The setup file must turn VehicleFaultRecovery on; without it this scenario proves nothing.'
}
$credential = $Context.FaultRecoveryCredential

function Get-Fault {
    return Invoke-L2Query -Connection $connection `
        -Sql "SELECT Level, FaultGeneration, ClearedAt, ClearedReason FROM VehicleFaultStates WHERE AgvId = '$agvId'"
}

function Get-GrantAudits {
    return Invoke-L2Query -Connection $connection `
        -Sql "SELECT Action, Outcome FROM AdministratorAuditRecords WHERE ObjectKind = 'RecoveryGrant' ORDER BY rowid"
}

function Set-Vehicle([hashtable]$fields) {
    $body = @{ vehicleKey = $vehicleKey } + $fields
    $null = $riot.Command('Put', 'vehicle', $body)
}

function Read-Body([object]$response) {
    # A refusal is application/problem+json, which Invoke-WebRequest does not treat as text: Content arrives as bytes.
    if ($response.Content -is [byte[]]) { return [System.Text.Encoding]::UTF8.GetString($response.Content) }
    return [string]$response.Content
}

function Invoke-Json([string]$method, [string]$uri, [hashtable]$body, [string]$label) {
    $parameters = @{
        Method = $method; Uri = $uri; SkipHttpErrorCheck = $true; TimeoutSec = 60
        Headers = @{ Authorization = "Bearer $credential" }
    }
    if ($null -ne $body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $body | ConvertTo-Json -Depth 5
    }
    $response = Invoke-WebRequest @parameters
    $text = Read-Body $response
    $journal.Note("$label -> $([int]$response.StatusCode): $text")
    return [pscustomobject]@{
        Status = [int]$response.StatusCode
        Body   = if ($text) { $text | ConvertFrom-Json } else { $null }
    }
}

function Invoke-Recovery([string]$action, [string]$label) {
    return Invoke-Json 'Post' $recoveryUri @{
        agvId = $agvId; operatorId = 'L2-OPERATOR-797'; action = $action; faultRemedied = $true; note = "L2 $($Context.RunId)"
    } "Fault recovery $action ($label)"
}

function Get-Reasons([object]$response) {
    if ($null -eq $response.Body -or -not ($response.Body.PSObject.Properties.Name -contains 'reasons')) { return , @() }
    return , @($response.Body.reasons | ForEach-Object { [string]$_ })
}

function Get-Grants {
    $listed = Invoke-Json 'Get' $grantUri $null 'Recovery grants'
    return , @($listed.Body | Where-Object { $null -ne $_ })
}

function Request-Grant([string[]]$actions, [int]$minutes, [string]$confirmationCode, [string]$label) {
    $body = @{
        vehicleKey = $vehicleKey; actions = $actions; minutes = $minutes; operatorId = 'maintenance-797'
        reason = '车上有货、卸货结果待恢复（L2）'; siteVerification = "L2-SITE-$($Context.RunId)"; claimedRole = 'MAINTENANCE_ADMINISTRATOR'
    }
    if ($confirmationCode) { $body.confirmationCode = $confirmationCode }
    return Invoke-Json 'Post' $grantUri $body "Recovery grant ($label)"
}

# --- 1. 车在路上订单 FAILED：记故障；摆回站上空闲 ----------------------------------------------------------------

$demandId = [guid]::NewGuid().ToString('N')
$journal.Note("Publishing demand $demandId.")
$null = $mes.Command('Put', "demands/$demandId", @{
    sublot      = "L2-VRG-$($Context.RunId)"
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$journey = Wait-L2Condition -Description 'the vehicle took a journey' `
    -Journal $journal -Criterion 'journey-dispatched' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT JourneyId, DemandId, Stage FROM JourneyRuntimes WHERE AgvId = '$agvId' ORDER BY rowid"
        if ($rows.Count -ge 1) { $rows[0] } else { $null }
    } `
    -Until { param($v) $null -ne $v }

$intent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'pickup-intent' -TimeoutSeconds 90 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$([string]$journey.DemandId)' AND Purpose = 'TO_PICKUP'"
        if ($rows.Count -ge 1 -and [string]$rows[0].Status -eq 'CONFIRMED') { $rows[0] } else { $null }
    } `
    -Until { param($v) $null -ne $v }

$journal.Note("$agvId drives between stations; RIoT reports $($intent.UpperId) FAILED.")
Set-Vehicle @{ movementState = 'MT_RUNNING'; speed = 0.6; currentPosition = 0 }
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 4 })
$recorded = Wait-L2ConditionOrLast -Description 'the server recorded the fault' `
    -Journal $journal -Criterion 'fault-recorded' -TimeoutSeconds 90 `
    -Probe { $rows = Get-Fault; if ($rows.Count -gt 0) { $rows[0] } else { $null } } `
    -Until { param($row) $null -ne $row -and [string]$row.Level -eq 'SuspectedBlocked' }
$journal.Note('The vehicle stands idle at its station, its order FAILED: every premise of a clearing holds but the grant.')
Set-Vehicle @{
    emergencyState = 'OK'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.GateStationRiotId; processingOrder = $false; clearOrderTaskId = $true
}
$null = Wait-L2Iterations -Riot $riot -Count 3 -TimeoutSeconds 60 -Journal $journal
$assertions.Add(
    'L2-VRG-00',
    '订单 FAILED：故障记为 SuspectedBlocked（后面每一步拿这个故障当对象）',
    ($null -ne $recorded -and [string]$recorded.Level -eq 'SuspectedBlocked'),
    'SuspectedBlocked',
    $(if ($recorded) { [string]$recorded.Level } else { '(无故障行)' }))

# --- 2. 没有授权：403，故障不动 -------------------------------------------------------------------------------

$ungranted = Invoke-Recovery 'CLEAR_FAULT' 'no grant'
$null = Wait-L2Iterations -Riot $riot -Count 3 -TimeoutSeconds 60 -Journal $journal
$fault = Get-Fault
$assertions.Add(
    'L2-VRG-01',
    '没有授权：凭据对、条件齐全的 CLEAR_FAULT 回 403，理由只有 RECOVERY_GRANT_MISSING；又评估 3 轮之后故障仍是 SuspectedBlocked',
    ($ungranted.Status -eq 403 -and ((Get-Reasons $ungranted) -join ',') -eq 'RECOVERY_GRANT_MISSING' -and
        $fault.Count -eq 1 -and [string]$fault[0].Level -eq 'SuspectedBlocked'),
    '403 / RECOVERY_GRANT_MISSING / SuspectedBlocked',
    "$($ungranted.Status) / $((Get-Reasons $ungranted) -join ',') / $(if ($fault.Count) { [string]$fault[0].Level } else { '(无故障行)' })")

# --- 3. 演练：给码，什么都不下 ------------------------------------------------------------------------------

$planned = Request-Grant @('CLEAR_FAULT') 5 $null 'rehearsal'
$afterRehearsal = Get-Grants
$auditsAfterRehearsal = Get-GrantAudits
$code = if ($planned.Body -and $planned.Body.PSObject.Properties.Name -contains 'confirmationCode') { [string]$planned.Body.confirmationCode } else { '' }
$assertions.Add(
    'L2-VRG-02',
    '演练：200 PLANNED，给出 12 位确认码，计划只放 CLEAR_FAULT、5 分钟；服务端一条授权也没有，审计不落行',
    ($planned.Status -eq 200 -and [string]$planned.Body.outcome -eq 'PLANNED' -and $code -match '^[0-9A-F]{12}$' -and
        (@($planned.Body.plan.actions) -join ',') -eq 'CLEAR_FAULT' -and [int]$planned.Body.plan.minutes -eq 5 -and
        $afterRehearsal.Count -eq 0 -and $auditsAfterRehearsal.Count -eq 0),
    '200 / PLANNED / 12 位码 / CLEAR_FAULT 5 分钟 / 0 条授权 / 0 行审计',
    "$($planned.Status) / $([string]$planned.Body.outcome) / '$code' / $(@($planned.Body.plan.actions) -join ',') $($planned.Body.plan.minutes) 分钟 / $($afterRehearsal.Count) 条授权 / $($auditsAfterRehearsal.Count) 行审计")

# --- 4. 带码下发 -------------------------------------------------------------------------------------------------

$granted = Request-Grant @('CLEAR_FAULT') 5 $code 'with the code'
$listed = Get-Grants
$audits = Get-GrantAudits
$assertions.Add(
    'L2-VRG-03',
    '带码下发：200 GRANTED，只对车（不限旅程）、只放 CLEAR_FAULT；列表里正是这辆车；审计一行 RECOVERY_GRANT Succeeded',
    ($granted.Status -eq 200 -and [string]$granted.Body.outcome -eq 'GRANTED' -and $null -eq $granted.Body.grant.journeyId -and
        (@($granted.Body.grant.actions) -join ',') -eq 'CLEAR_FAULT' -and
        $listed.Count -eq 1 -and [string]$listed[0].agvId -eq $agvId -and
        $audits.Count -eq 1 -and [string]$audits[0].Action -eq 'RECOVERY_GRANT' -and [string]$audits[0].Outcome -eq 'Succeeded'),
    "200 / GRANTED / 不限旅程 / CLEAR_FAULT / 列表 $agvId / RECOVERY_GRANT Succeeded",
    "$($granted.Status) / $([string]$granted.Body.outcome) / 旅程 '$($granted.Body.grant.journeyId)' / $(@($granted.Body.grant.actions) -join ',') / 列表 $(@($listed | ForEach-Object { $_.agvId }) -join ',') / $(@($audits | ForEach-Object { "$($_.Action) $($_.Outcome)" }) -join '; ')")

# --- 5. 没点名的动作：403 ---------------------------------------------------------------------------------------

$otherAction = Invoke-Recovery 'RESUME_HELD_ORDER' 'not granted'
$fault = Get-Fault
$assertions.Add(
    'L2-VRG-04',
    '授权只放 CLEAR_FAULT：RESUME_HELD_ORDER 回 403，理由 RECOVERY_GRANT_ACTION_NOT_GRANTED；故障不动',
    ($otherAction.Status -eq 403 -and ((Get-Reasons $otherAction) -join ',') -eq 'RECOVERY_GRANT_ACTION_NOT_GRANTED' -and
        [string]$fault[0].Level -eq 'SuspectedBlocked'),
    '403 / RECOVERY_GRANT_ACTION_NOT_GRANTED / SuspectedBlocked',
    "$($otherAction.Status) / $((Get-Reasons $otherAction) -join ',') / $([string]$fault[0].Level)")

# --- 6. 点名的动作：清掉 ---------------------------------------------------------------------------------------

$cleared = Invoke-Recovery 'CLEAR_FAULT' 'granted'
$fault = Get-Fault
$assertions.Add(
    'L2-VRG-05',
    '点名的 CLEAR_FAULT：200 Cleared，故障 Level None，ClearedReason 记着 L2-OPERATOR-797',
    ($cleared.Status -eq 200 -and [string]$cleared.Body.outcome -eq 'Cleared' -and
        [string]$fault[0].Level -eq 'None' -and [string]$fault[0].ClearedReason -like '*L2-OPERATOR-797*'),
    '200 / Cleared / None / L2-OPERATOR-797',
    "$($cleared.Status) / $(if ($cleared.Body -and $cleared.Body.PSObject.Properties.Name -contains 'outcome') { [string]$cleared.Body.outcome } else { '-' }) / $([string]$fault[0].Level) / $([string]$fault[0].ClearedReason)")

# --- 7. 收回：同一个请求再来一次被拒 ---------------------------------------------------------------------------

$revoked = Invoke-Json 'Post' $revokeUri @{ vehicleKey = $vehicleKey; operatorId = 'maintenance-797'; reason = '处理完了（L2）' } 'Revoke'
$afterRevoke = Invoke-Recovery 'CLEAR_FAULT' 'after revocation'
$listedAfterRevoke = Get-Grants
$assertions.Add(
    'L2-VRG-06',
    '收回：200 REVOKED，列表为空；同一个 CLEAR_FAULT 再来一次是 403 RECOVERY_GRANT_MISSING，不是 AlreadyCleared（授权在判定之前查）',
    ($revoked.Status -eq 200 -and [string]$revoked.Body.outcome -eq 'REVOKED' -and $listedAfterRevoke.Count -eq 0 -and
        $afterRevoke.Status -eq 403 -and ((Get-Reasons $afterRevoke) -join ',') -eq 'RECOVERY_GRANT_MISSING'),
    '200 / REVOKED / 0 条 / 403 RECOVERY_GRANT_MISSING',
    "$($revoked.Status) / $([string]$revoked.Body.outcome) / $($listedAfterRevoke.Count) 条 / $($afterRevoke.Status) $((Get-Reasons $afterRevoke) -join ',')")

# --- 8. 1 分钟的授权在服务端到点 ------------------------------------------------------------------------------

$shortPlan = Request-Grant @('CLEAR_FAULT') 1 $null 'one-minute rehearsal'
$short = Request-Grant @('CLEAR_FAULT') 1 ([string]$shortPlan.Body.confirmationCode) 'one minute'
$expiresAt = if ($short.Body -and $short.Body.grant) { [DateTimeOffset]$short.Body.grant.expiresAt } else { $null }
$whileValid = Invoke-Recovery 'CLEAR_FAULT' 'one-minute grant in effect'
$gone = Wait-L2ConditionOrLast -Description 'the one-minute grant ran out on the server' `
    -Journal $journal -Criterion 'grant-expired' -TimeoutSeconds 120 `
    -Probe { (Get-Grants).Count } `
    -Until { param($count) $count -eq 0 }
$afterExpiry = Invoke-Recovery 'CLEAR_FAULT' 'after expiry'
$assertions.Add(
    'L2-VRG-07',
    '1 分钟的授权：有效期内 CLEAR_FAULT 过得了授权这一关（200 AlreadyCleared）；到点后服务端自己列表变空，再来一次 403 RECOVERY_GRANT_MISSING，且是在 expiresAt 之后',
    ($short.Status -eq 200 -and $whileValid.Status -eq 200 -and [string]$whileValid.Body.outcome -eq 'AlreadyCleared' -and
        $gone -eq 0 -and $afterExpiry.Status -eq 403 -and ((Get-Reasons $afterExpiry) -join ',') -eq 'RECOVERY_GRANT_MISSING' -and
        $null -ne $expiresAt -and [DateTimeOffset]::UtcNow -ge $expiresAt),
    '200 / 200 AlreadyCleared / 0 条 / 403 RECOVERY_GRANT_MISSING / 已过 expiresAt',
    "$($short.Status) / $($whileValid.Status) $(if ($whileValid.Body -and $whileValid.Body.PSObject.Properties.Name -contains 'outcome') { [string]$whileValid.Body.outcome } else { '-' }) / $gone 条 / $($afterExpiry.Status) $((Get-Reasons $afterExpiry) -join ',') / expiresAt $expiresAt")
