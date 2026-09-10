#Requires -Version 7

<#
车载告警快照「车载产快照 → 服务端消费 → 看板可见」：FP-IS-15，批次 3 出口，#16 的 L2 验收标准。

断言读的是看板进程渲染出来的那一页，不是服务端的查询端点——「看板可见」说的是人在看板上看得到，而卡片与
端点之间还隔着一次取数与一次渲染。服务端库里的那一行只用来确认快照确实被消费了。

合成车载端能发任意告警，所以这里第一次证到**非空内容**：真车载端的告警板在产品代码里还没有接上任何告警
来源（OnboardAlarmBoard.Raise 没有调用者），G3 那几轮的投影始终是空的。这条场景证的是服务端与看板这一半，
不替车载端把告警来源补上。

五件事，按 REQ-0269／REQ-0270 的口径：
1. 完整握手里报一份，空的也报；
2. 与这台车当下直接相关的告警不进看板，其余进；
3. 后一份整体取代前一份；
4. 失联的车显示失联本身，不显示它失联前的最后一批告警；
5. 重连之后看板直接是当下的事实。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$onboard = $Context.Onboard
$agvId = $Context.AgvId
$dashboardUrl = $Context.DashboardUrl

# 与服务端 OnboardAlarmSnapshotWire 的分类对照：VEHICLE 归车载界面；认不出的主体类型归看板（朝可见方向倒）。
$vehicleOnlyCode = 'L2_ALARM_VEHICLE_ONLY'
$firstFleetCode = 'L2_ALARM_FLEET_FIRST'
$secondFleetCode = 'L2_ALARM_FLEET_SECOND'
$lostContactReason = '车辆失联'

function Get-AlarmRow {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT AgvId, SessionGeneration, SnapshotSequence, AlarmsJson FROM OnboardAlarmSnapshots WHERE AgvId = '$agvId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# 看板的那一行，解码之后的纯文本。解码而不是直接搜 HTML：WebUtility.HtmlEncode 对一部分字符编码成实体，
# 一条对着编码形态写的断言会在换一个字符之后无声地失效。
function Get-DashboardRow {
    $html = (Invoke-WebRequest -Uri $dashboardUrl -TimeoutSec 10).Content
    # 只在告警卡片里找：车队会话卡片同样有一行以这台车的 agvId 开头，匹配到那一行会让每条断言读错卡片。
    $start = $html.IndexOf('车载告警', [StringComparison]::Ordinal)
    if ($start -lt 0) { return $null }
    $card = $html.Substring($start)
    $end = $card.IndexOf('</table>', [StringComparison]::Ordinal)
    if ($end -ge 0) { $card = $card.Substring(0, $end) }
    $match = [regex]::Match($card, "<tr><td>$([regex]::Escape($agvId))</td>.*?</tr>")
    if (-not $match.Success) { return $null }
    return [Net.WebUtility]::HtmlDecode(($match.Value -replace '<[^>]+>', ' ')).Trim()
}

function Set-Alarms([object[]]$alarms) {
    $result = $onboard.Command('Put', 'alarms', @{ alarms = $alarms })
    $revision = [long]$onboard.Snapshot().body.alarmSnapshotRevision
    $journal.Note("Vehicle published alarm snapshot revision $revision with $(@($alarms).Count) alarm(s).")
    return $revision
}

# --- 1. 完整握手里报一份，空的也报 --------------------------------------------------------------------

$handshakeRow = Wait-L2Condition -Description 'the handshake alarm snapshot was consumed' `
    -Journal $journal -Criterion 'alarm-snapshot-handshake' -TimeoutSeconds 30 `
    -Probe { Get-AlarmRow } -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-OAS-01', '完整握手里报了一份告警快照，空的也报，服务端收下了',
    ([long]$handshakeRow.SnapshotSequence -eq 1 -and [string]$handshakeRow.AlarmsJson -eq '[]'),
    '1 / []', "$($handshakeRow.SnapshotSequence) / $($handshakeRow.AlarmsJson)")

$emptyRow = Wait-L2Condition -Description 'the dashboard shows the vehicle with no alarms' `
    -Journal $journal -Criterion 'dashboard-empty' -TimeoutSeconds 30 `
    -Probe { Get-DashboardRow } -Until { param($v) $null -ne $v -and $v -match '无' }
$assertions.Add(
    'L2-OAS-02', '看板上这台车在线、告警为「无」，不是「尚未收到该车快照」',
    ($emptyRow -match '无' -and $emptyRow -notmatch '尚未收到'),
    "$agvId 无", $emptyRow)

# --- 2. 两条告警，一条归车载界面、一条进看板 ----------------------------------------------------------

$revision = Set-Alarms @(
    @{ code = $vehicleOnlyCode; severity = 'WARNING'; subjectType = 'VEHICLE'; subjectId = $agvId; displayMessage = '只该出现在车上' },
    @{ code = $firstFleetCode; severity = 'CRITICAL'; subjectType = 'CHARGER'; subjectId = 'CHARGER-L2-01'; displayMessage = '与任何一台车的当下都无关' })

$secondRow = Wait-L2Condition -Description 'the second alarm snapshot was consumed' `
    -Journal $journal -Criterion 'alarm-snapshot-2' -TimeoutSeconds 30 `
    -Probe { Get-AlarmRow } -Until { param($v) $null -ne $v -and [long]$v.SnapshotSequence -eq $revision }
$assertions.Add(
    'L2-OAS-03', '服务端收下的是整份快照，两条告警都在库里',
    ([string]$secondRow.AlarmsJson).Contains($vehicleOnlyCode) -and ([string]$secondRow.AlarmsJson).Contains($firstFleetCode),
    "$vehicleOnlyCode + $firstFleetCode", [string]$secondRow.AlarmsJson)

$visible = Wait-L2Condition -Description 'the dashboard shows the fleet alarm' `
    -Journal $journal -Criterion 'dashboard-first-alarm' -TimeoutSeconds 30 `
    -Probe { Get-DashboardRow } -Until { param($v) $null -ne $v -and $v.Contains($firstFleetCode) }
$assertions.Add(
    'L2-OAS-04', '看板上看得到进看板的那条，看不到只该出现在车上的那条',
    ($visible.Contains($firstFleetCode) -and -not $visible.Contains($vehicleOnlyCode)),
    "$firstFleetCode, not $vehicleOnlyCode", $visible)

# --- 3. 后一份整体取代前一份 --------------------------------------------------------------------------

$revision = Set-Alarms @(
    @{ code = $secondFleetCode; severity = 'WARNING'; subjectType = 'CHARGER'; subjectId = 'CHARGER-L2-02'; displayMessage = $null })

$replaced = Wait-L2Condition -Description 'the dashboard shows only the replacement alarm' `
    -Journal $journal -Criterion 'dashboard-replaced' -TimeoutSeconds 30 `
    -Probe { Get-DashboardRow } -Until { param($v) $null -ne $v -and $v.Contains($secondFleetCode) }
$assertions.Add(
    'L2-OAS-05', '新快照整体取代旧快照：看板上只剩新的那条，旧的那条不见了',
    ($replaced.Contains($secondFleetCode) -and -not $replaced.Contains($firstFleetCode)),
    "$secondFleetCode, not $firstFleetCode", $replaced)

$rowCount = [int](Invoke-L2Query -Connection $connection `
    -Sql "SELECT COUNT(*) AS N FROM OnboardAlarmSnapshots WHERE AgvId = '$agvId'")[0].N
$latest = Get-AlarmRow
$assertions.Add(
    'L2-OAS-06', '一车一行，停在最新那一份的序号上',
    ($rowCount -eq 1 -and [long]$latest.SnapshotSequence -eq $revision),
    "1 / $revision", "$rowCount / $($latest.SnapshotSequence)")

# --- 4. 失联直述 ---------------------------------------------------------------------------------------

$generationBefore = [long]$onboard.Snapshot().body.sessionGeneration
$null = $onboard.Command('Put', 'connection', @{ connected = $false })
$lost = Wait-L2Condition -Description 'the dashboard says the vehicle is out of contact' `
    -Journal $journal -Criterion 'dashboard-lost-contact' -TimeoutSeconds 60 `
    -Probe { Get-DashboardRow } -Until { param($v) $null -ne $v -and $v.Contains($lostContactReason) }
$assertions.Add(
    'L2-OAS-07', '车失联时看板显示失联本身，不显示它失联前的最后一批告警',
    ($lost.Contains($lostContactReason) -and -not $lost.Contains($secondFleetCode)),
    "$lostContactReason, not $secondFleetCode", $lost)

# --- 5. 重连之后看板直接是当下的事实 -------------------------------------------------------------------

$reconnect = $onboard.Command('Put', 'connection', @{ connected = $true })
$back = Wait-L2Condition -Description 'the dashboard shows the current alarms again after reconnecting' `
    -Journal $journal -Criterion 'dashboard-reconnected' -TimeoutSeconds 60 `
    -Probe { Get-DashboardRow } -Until { param($v) $null -ne $v -and $v.Contains($secondFleetCode) }
$reconnectedRow = Get-AlarmRow
$assertions.Add(
    'L2-OAS-08', '重连的完整握手重报当下的全量告警，服务端按新会话代采纳，看板恢复显示',
    ($back.Contains($secondFleetCode) -and -not $back.Contains($lostContactReason) -and
        [long]$reconnect.body.sessionGeneration -gt $generationBefore -and
        [long]$reconnectedRow.SessionGeneration -eq [long]$reconnect.body.sessionGeneration),
    "$secondFleetCode / generation $([long]$reconnect.body.sessionGeneration)",
    "$back / generation $($reconnectedRow.SessionGeneration)")

$journal.Note('车载产快照 → 服务端消费 → 看板可见走通；收敛、整体取代、失联直述、重连采纳各有一条断言。')
