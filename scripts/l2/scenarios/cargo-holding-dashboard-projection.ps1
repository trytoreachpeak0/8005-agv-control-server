#Requires -Version 7

<#
持货等单与让站上看板（批次7-12，control-server#217；规格第 3.3 节第 12 项的看板那一半，REQ-0354、REQ-0355）。

布置照 waiting-station-yield（StationYieldCommon.ps1）：两台合成车服务同一个分区，持货超时 10 分钟。

**不得「不触发即通过」。**每一段先断言库里的正事实确实发生过，再断言看板上看得到它：
1. 需求甲给主车，主车开到 12 号站装完，库里进入 CARGO_HOLDING_WAIT（判据 01，StationYieldCommon 的布置）。
2. 读端点 /api/dashboard/cargo-holding：主车那一行是持货等单，期限等于库里的起算点加服务端配置的 10 分钟，剩余时间在 (0, 600] 秒（05）。
3. 另等第二个事实：过一会儿再读端点，期限一秒不差、剩余时间比第一次少——它在走，而且不是看板自己推的起算点（06）。
4. 看板那一页的持货等单卡片渲染出主车这一行，写着持货等单与剩余时间（07）。
5. 需求乙只能给另一台车，它的下一停靠就是主车所在的站（02）；库里让站确实触发了，主车 CLOSED/WAITING_STATION_YIELD，
   触发列记另一台车，车上收到了那张快照（03、04）。
6. 另等第二个事实：端点里主车那一行的结束原因变成让站，触发的车是另一台车（08）；看板那一页写着
   「另一辆车以本站为下一停靠，本车结束等单」与那台车（09）。主车的合成车载端把离站核验挂起（safetyCheck = Manual），
   所以这两条读的时候车一定还停在站上——已结束的行只在车还在站上时显示（审查 L3）。
7. 放行离站核验：主车离站之后，库里取货停靠完成（正事实），端点与看板那一页都不再列这台车（10）。

所有看板上的事实都用 Wait-L2ConditionOrLast 等：库里的状态到位之后另等端点与页面，不在同一时刻读库又读看板就断言
（scripts/l2/README.md 第 14 条）。端点与库在同一个服务端进程、同一个库上，端点每次请求现读，所以库里到位之后端点下一次
请求必然读到——另等一次是为了让「等不到」落进判据表，而不是结束场景。

红证据（票面）：端点漏掉 CARGO_HOLDING_WAIT 的车，或把结束原因映射错——01、03 这些库里的正事实照旧绿，红在 05～09 的看板判据上。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
. (Join-Path $PSScriptRoot 'StationYieldCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$endpoint = "http://127.0.0.1:$($Context.HealthPort)/api/dashboard/cargo-holding"
$holdingTimeout = [TimeSpan]::FromMinutes(10)

# 端点里这台车的那一行；不在就是 $null。-DateKind String：期限原样解析，不经本地时区换算。
function Get-L2HoldingEntry([string]$Endpoint, [string]$AgvId) {
    $fact = (Invoke-WebRequest -Uri $Endpoint -NoProxy -TimeoutSec 10).Content | ConvertFrom-Json -DateKind String
    return @($fact.journeys | Where-Object { $_.agvId -eq $AgvId }) | Select-Object -First 1
}

# 看板那一页上持货等单卡片里这台车的那一行，解码成纯文本；不在就是 $null。只在持货等单卡片里找。
function Get-L2HoldingDashboardRow([string]$DashboardUrl, [string]$AgvId) {
    $html = (Invoke-WebRequest -Uri $DashboardUrl -NoProxy -TimeoutSec 10).Content
    $start = $html.IndexOf('id="cargo-holding"', [StringComparison]::Ordinal)
    if ($start -lt 0) { return $null }
    $card = $html.Substring($start)
    $end = $card.IndexOf('</section>', [StringComparison]::Ordinal)
    if ($end -ge 0) { $card = $card.Substring(0, $end) }
    $match = [regex]::Match($card, '<tr><td>' + [regex]::Escape($AgvId) + '</td>.*?</tr>')
    if (-not $match.Success) { return $null }
    return [Net.WebUtility]::HtmlDecode(($match.Value -replace '<[^>]+>', ' ')).Trim()
}

function Format-L2HoldingEntry([object]$Entry) {
    if ($null -eq $Entry) { return '(not listed)' }
    return "$($Entry.loadingPhaseState)$(if ($Entry.closedReason) { "/$($Entry.closedReason)" }) deadline $($Entry.cargoHoldingDeadlineAt) remaining $($Entry.remainingSeconds) s yieldedTo '$($Entry.yieldedToVehicleKey)'"
}

Initialize-L2YieldRig $Context

# --- 1. 主车在站上持货等单（库里的正事实） ------------------------------------------------------------------

$holder = Start-L2YieldHolder $Context 'L2-CHD'
$holderAgvId = $holder.HolderAgvId
$startedAt = ConvertTo-L2YieldInstant (Invoke-L2Query -Connection $connection `
        -Sql "SELECT CargoHoldingStartedAt FROM JourneyRuntimes WHERE JourneyId = '$($holder.JourneyId)'")[0].CargoHoldingStartedAt
$expectedDeadline = if ($null -ne $startedAt) { $startedAt + $holdingTimeout } else { $null }
$journal.Observe('holder-cargo-holding-started-at', $(if ($startedAt) { $startedAt.ToString('o') } else { '(null)' }), $null)

# --- 2. 端点：期限等于库里的值 ---------------------------------------------------------------------------------

$first = Wait-L2ConditionOrLast -Description 'the holder is listed as holding on the dashboard endpoint' -Journal $journal `
    -Criterion 'endpoint-holding' -TimeoutSeconds 30 `
    -Probe { Get-L2HoldingEntry $endpoint $holderAgvId } `
    -Until { param($v) $null -ne $v -and [string]$v.loadingPhaseState -eq 'CARGO_HOLDING_WAIT' }
$firstDeadline = if ($null -ne $first) { ConvertTo-L2YieldInstant $first.cargoHoldingDeadlineAt } else { $null }
$firstRemaining = if ($null -ne $first -and $null -ne $first.remainingSeconds) { [long]$first.remainingSeconds } else { $null }
$assertions.Add(
    'L2-CHD-05', '端点列出主车持货等单，期限等于库里的起算点加服务端配置的 10 分钟，剩余时间在 (0, 600] 秒',
    ($null -ne $expectedDeadline -and $null -ne $firstDeadline -and $firstDeadline -eq $expectedDeadline -and
        $null -ne $firstRemaining -and $firstRemaining -gt 0 -and $firstRemaining -le 600),
    "CARGO_HOLDING_WAIT deadline $(if ($expectedDeadline) { $expectedDeadline.ToString('o') } else { '(no start in db)' }), remaining in (0, 600]",
    (Format-L2HoldingEntry $first))

# --- 3. 另等第二个事实：剩余时间在走，期限不动 ---------------------------------------------------------------------

$later = Wait-L2ConditionOrLast -Description 'the time left on the dashboard endpoint went down' -Journal $journal `
    -Criterion 'endpoint-remaining-runs' -TimeoutSeconds 30 `
    -Probe { Get-L2HoldingEntry $endpoint $holderAgvId } `
    -Until {
        param($v)
        $null -ne $v -and $null -ne $firstRemaining -and $null -ne $v.remainingSeconds -and [long]$v.remainingSeconds -lt $firstRemaining
    }
$laterDeadline = if ($null -ne $later) { ConvertTo-L2YieldInstant $later.cargoHoldingDeadlineAt } else { $null }
$assertions.Add(
    'L2-CHD-06', '过一会儿再读：剩余时间比第一次少，期限一秒不差',
    ($null -ne $later -and $null -ne $later.remainingSeconds -and $null -ne $firstRemaining -and
        [long]$later.remainingSeconds -lt $firstRemaining -and $null -ne $laterDeadline -and $laterDeadline -eq $firstDeadline),
    "remaining < $firstRemaining, deadline $(if ($firstDeadline) { $firstDeadline.ToString('o') })",
    (Format-L2HoldingEntry $later))

# --- 4. 看板那一页渲染出这一行 -----------------------------------------------------------------------------------

$pageHolding = Wait-L2ConditionOrLast -Description 'the dashboard page shows the holder in the cargo holding card' -Journal $journal `
    -Criterion 'page-holding' -TimeoutSeconds 30 `
    -Probe { Get-L2HoldingDashboardRow $Context.DashboardUrl $holderAgvId } `
    -Until { param($v) $null -ne $v -and $v.Contains('持货等单') -and $v -match '\d+ 分 \d+ 秒' }
$assertions.Add(
    'L2-CHD-07', '看板那一页的持货等单卡片渲染出主车这一行：持货等单、剩余时间',
    ($null -ne $pageHolding -and $pageHolding.Contains('持货等单') -and $pageHolding -match '\d+ 分 \d+ 秒'),
    '持货等单 … N 分 N 秒', $(if ($pageHolding) { $pageHolding } else { '(no row)' }))

# --- 5. 另一台车被承诺以这个站为下一停靠；库里让站确实触发了 ------------------------------------------------------

# 主车的离站核验挂起：让站之后车停在站上等这一答，08、09 读的时候它一定还在站上。等单期间车不发离站核验，所以这里设不影响 1～4。
$null = $Context.Onboard.Command('Put', 'policy', @{ safetyCheck = 'Manual' })
$comer = Send-L2YieldComer $Context $holder 'L2-CHD'
$null = Confirm-L2YieldTriggered $Context $holder $comer 'L2-CHD'
$comerKey = Get-L2YieldVehicleKey $Context $holder.ComerAgvId

# --- 6. 另等第二个事实：看板上的结束原因变成让站 -----------------------------------------------------------------

$yielded = Wait-L2ConditionOrLast -Description 'the dashboard endpoint says the holder yielded' -Journal $journal `
    -Criterion 'endpoint-yield' -TimeoutSeconds 30 `
    -Probe { Get-L2HoldingEntry $endpoint $holderAgvId } `
    -Until { param($v) $null -ne $v -and [string]$v.closedReason -eq 'WAITING_STATION_YIELD' }
$assertions.Add(
    'L2-CHD-08', "端点里主车那一行 CLOSED/WAITING_STATION_YIELD，触发的车是另一台车 $comerKey，不再倒计时",
    ($null -ne $yielded -and [string]$yielded.loadingPhaseState -eq 'CLOSED' -and
        [string]$yielded.closedReason -eq 'WAITING_STATION_YIELD' -and [string]$yielded.yieldedToVehicleKey -eq $comerKey -and
        $null -eq $yielded.remainingSeconds),
    "CLOSED/WAITING_STATION_YIELD yieldedTo '$comerKey' remaining (null)", (Format-L2HoldingEntry $yielded))

$yieldText = "另一辆车以本站为下一停靠，本车结束等单（$comerKey）"
$pageYield = Wait-L2ConditionOrLast -Description 'the dashboard page shows the yield' -Journal $journal `
    -Criterion 'page-yield' -TimeoutSeconds 30 `
    -Probe { Get-L2HoldingDashboardRow $Context.DashboardUrl $holderAgvId } `
    -Until { param($v) $null -ne $v -and $v.Contains($yieldText) }
$assertions.Add(
    'L2-CHD-09', '看板那一页主车那一行写着已结束与让站原因、触发的车',
    ($null -ne $pageYield -and $pageYield.Contains('已结束') -and $pageYield.Contains($yieldText)),
    "已结束 … $yieldText", $(if ($pageYield) { $pageYield } else { '(no row)' }))

# --- 7. 放行离站核验：车离站之后，看板不再列这台车 -------------------------------------------------------------

$checkKey = Wait-L2ConditionOrLast -Description 'the holder asked for its pre-departure safety check' -Journal $journal `
    -Criterion 'departure-check-pending' -TimeoutSeconds 60 `
    -Probe {
        $pending = @($Context.Onboard.Snapshot().body.pending | Where-Object { $_.messageType -eq 'PreDepartureSafetyCheck' })
        if ($pending.Count -eq 0) { $null } else { [string]$pending[0].key }
    } `
    -Until { param($v) $null -ne $v }
if ($null -ne $checkKey) {
    $null = $Context.Onboard.Command('Put', "answer/$checkKey", @{ completed = $true })
}
$pickupDone = Wait-L2ConditionOrLast -Description 'the holder left its pickup stop' -Journal $journal -Criterion 'holder-departed' `
    -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql (
            "SELECT Status FROM JourneyStops WHERE JourneyId = '$($holder.JourneyId)' AND StopRole = 'PICKUP'")
        if ($rows.Count -eq 0) { $null } else { [string]$rows[0].Status }
    } `
    -Until { param($v) $v -eq 'COMPLETED' }
$gone = Wait-L2ConditionOrLast -Description 'the dashboard no longer lists the holder' -Journal $journal -Criterion 'endpoint-gone' `
    -TimeoutSeconds 30 `
    -Probe {
        $entry = Get-L2HoldingEntry $endpoint $holderAgvId
        $row = Get-L2HoldingDashboardRow $Context.DashboardUrl $holderAgvId
        [pscustomobject]@{ Entry = $entry; Row = $row; Listed = ($null -ne $entry -or $null -ne $row) }
    } `
    -Until { param($v) -not $v.Listed }
$assertions.Add(
    'L2-CHD-10', '放行离站核验之后主车离站（库里取货停靠 COMPLETED），端点与看板那一页都不再列这台车',
    ($null -ne $checkKey -and $pickupDone -eq 'COMPLETED' -and $null -ne $gone -and -not $gone.Listed),
    'check answered, PICKUP COMPLETED, not listed',
    "check $(if ($checkKey) { 'answered' } else { '(never asked)' }), PICKUP $pickupDone, endpoint $(Format-L2HoldingEntry $gone.Entry), page $(if ($gone.Row) { $gone.Row } else { '(no row)' })")

$journal.Note('主车在站上持货等单：看板端点与页面给出库里的期限、剩余时间在走；另一台车被承诺以这个站为下一停靠之后，看板上的结束原因变成让站、写着触发的车；车离站之后这一行不再列出。')
