#Requires -Version 7
<#
.SYNOPSIS
Drives Wait-L2WorklistAcknowledgedAtOtherStation through the state the count-based criterion got wrong.

.DESCRIPTION
The shipped function, not a copy: Invoke-L2Query is replaced inside L2TaskTypeJourney's own module
scope, so Get-L2DemandJourneySnapshots parses real payload JSON on the way through and everything
above it runs as it runs on the rig.

The case this exists for is the first one. control-server#204 wrote the second stop's wait as "at
least 2 acknowledged worklists", which is a PROXY for "the second stop's worklist is acknowledged" --
equal to it only while every stop emits exactly one worklist. Revise the first stop's worklist and
the proxy is satisfied while the thing it stands for is not.

So that case asserts BOTH halves on the SAME input: the old criterion is satisfied, and the new one
is not. Asserting only the second half would not show the old one was ever wrong -- it would pass
just as well against a criterion that had always been right (control-server#265).

No real-rig round has ever been in any of these states: in both scenarios the callback that runs this
wait only fires after the server has sent the operation command, by which time the worklist is long
acknowledged. None of them can be reached by re-running a green scenario.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'L2.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'L2TaskTypeJourney.psm1') -Force
$module = Get-Module L2TaskTypeJourney

# Module scope, not global: L2TaskTypeJourney.psm1 imports its dependencies itself, so its own scope
# wins over anything defined outside. Measured in control-server#203 -- a global stub is not reached.
$global:l2wsThrowAfterUtc = $null
& $module {
    function script:Invoke-L2Query {
        param($Connection, $Sql)
        $global:l2wsCalls++
        # A deadline rather than a call count: the read that matters is the one AFTER the wait gave up,
        # and "after the deadline" is what that read is by construction. A poll that happens to fall on
        # the wrong side throws too, which changes nothing -- Wait-L2Condition swallows it and the wait
        # times out either way (the same shape as Test-L2SecondLegIntentWait.ps1).
        if ($null -ne $global:l2wsThrowAfterUtc -and [DateTime]::UtcNow -gt $global:l2wsThrowAfterUtc) {
            throw [System.InvalidOperationException]::new('the database went away')
        }
        return $global:l2wsRows
    }
}

$results = [System.Collections.Generic.List[object]]::new()
function Add-Case([string]$Name, [bool]$Ok, [string]$Actual) {
    $results.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Actual = $Actual })
}

# A ProtocolOutbox row exactly as Get-L2DemandJourneySnapshots reads one, payload JSON included: the
# station id has to survive the real parse, not be handed to the criterion directly.
function Worklist([string]$id, [string]$station, [int]$revision, [bool]$acknowledged, [string]$demand = 'd-1') {
    $payload = @{ payload = @{ stationId = $station; worklistRevision = $revision; items = @(@{ demandId = $demand }) } }
    [pscustomobject]@{
        MessageId      = $id
        MessageType    = 'CurrentStopWorklistSnapshot'
        PayloadJson    = ($payload | ConvertTo-Json -Depth 8 -Compress)
        CreatedAt      = "2026-09-21T00:00:0$($id -replace '\D', '')Z"
        AcknowledgedAt = $(if ($acknowledged) { '2026-09-21T00:00:30Z' } else { $null })
        FencedAt       = $null
    }
}

# A plan row as Get-L2DemandJourneySnapshots reads one. Only the legs' stationId matters here: the
# same-station diagnostic asks the PLAN whether the journey's stops are one station, because the
# worklists alone cannot tell that from "the second stop has not been reached yet".
function Plan([string]$id, [string[]]$stations, [string]$demand = 'd-1') {
    $legs = @(for ($i = 0; $i -lt $stations.Count; $i++) {
        @{ sequence = $i + 1; demandId = $demand; stationId = $stations[$i]; legType = 'TO_PICKUP'; state = 'PLANNED' }
    })
    $payload = @{ payload = @{ planRevision = 1; legs = $legs } }
    [pscustomobject]@{
        MessageId      = $id
        MessageType    = 'UpcomingStopPlanSnapshot'
        PayloadJson    = ($payload | ConvertTo-Json -Depth 8 -Compress)
        CreatedAt      = '2026-09-21T00:00:00Z'
        AcknowledgedAt = '2026-09-21T00:00:05Z'
        FencedAt       = $null
    }
}

function Invoke-FirstWait([object[]]$Rows) {
    $global:l2wsRows = $Rows
    $global:l2wsCalls = 0
    try {
        $v = Wait-L2FirstAcknowledgedWorklistStation -Connection 'stub' -DemandId 'd-1' `
            -Criterion 'origin-worklist-acknowledged' `
            -Description 'the onboard acknowledged the worklist at the first stop' -TimeoutSeconds 2
        return [pscustomobject]@{ Value = [string]$v; Error = $null; Calls = $global:l2wsCalls }
    } catch {
        return [pscustomobject]@{ Value = $null; Error = $_.Exception.Message; Calls = $global:l2wsCalls }
    }
}

function Invoke-Wait([object[]]$Rows, [string]$Previous) {
    $global:l2wsRows = $Rows
    $global:l2wsCalls = 0
    try {
        $v = Wait-L2WorklistAcknowledgedAtOtherStation -Connection 'stub' -DemandId 'd-1' `
            -PreviousStationId $Previous -Criterion 'destination-worklist-acknowledged' `
            -Description 'the onboard acknowledged the worklist at the second stop' -TimeoutSeconds 2
        return [pscustomobject]@{ Value = [string]$v; Error = $null; Calls = $global:l2wsCalls }
    } catch {
        return [pscustomobject]@{ Value = $null; Error = $_.Exception.Message; Calls = $global:l2wsCalls }
    }
}

# The criterion the old code used, evaluated with the same parser on the same rows. Not a hand count:
# the claim being made is about what the shipped reader would have produced.
function Get-OldCountCriterion([object[]]$Rows) {
    $global:l2wsRows = $Rows
    $global:l2wsCalls = 0
    # Parenthesised exactly as the shipped code was: the reader returns a single-layer array, and
    # `f | Where` hands Where-Object the whole list as ONE object while `(f) | Where` unrolls it.
    # Getting this wrong here would understate the old count and make the case below argue nothing.
    return @((Get-L2DemandJourneySnapshots 'stub' 'd-1') |
        Where-Object { $_.Type -eq 'CurrentStopWorklistSnapshot' -and $_.Acknowledged }).Count
}

# ---------------------------------------------------------------- the case this file exists for

# The plan names two DIFFERENT stations, which is what makes this input "the first stop's worklist was
# revised and the second stop has not been reached yet" rather than a same-station journey. The two are
# indistinguishable in the worklists alone -- that is why the diagnostic below asks the plan.
$firstStopRevised = @(
    (Plan 'p1' @('STATION-A', 'STATION-B')),
    (Worklist '1' 'STATION-A' 1 $true),
    (Worklist '2' 'STATION-A' 2 $true))

$oldCount = Get-OldCountCriterion $firstStopRevised
$new = Invoke-Wait -Rows $firstStopRevised -Previous 'STATION-A'
Add-Case '第一站清单改版：旧的条数判据被满足（>= 2），说明代理指标在这里就会放过' `
    ($oldCount -ge 2) "条数 = $oldCount"
Add-Case '第一站清单改版：新的站点判据不满足，等待超时而不是放过' `
    (($null -eq $new.Value -or $new.Value -eq '') -and $null -ne $new.Error -and $new.Error -like '*Timed out*') `
    "值=$($new.Value) 错误=$($new.Error)"
# 这一条是同站诊断的判别力所在，不是重复上一条：同站那条消息**也**以 "Timed out" 开头，所以上一条
# 两种情况都会通过。这里断的是它**没有**说成同站旅程——「第一站清单改版、第二站还没到」在清单里与
# 同站旅程完全同形，只有计划分得开。
Add-Case '第一站清单改版：不许被诊断成「两站是同一个站点」' `
    ($null -ne $new.Error -and $new.Error -notlike '*two stops are one station*') `
    "错误=$($new.Error)"

# ---------------------------------------------------------------- the ordinary states

$secondStopArrived = @(
    (Worklist '1' 'STATION-A' 1 $true),
    (Worklist '2' 'STATION-B' 1 $true))
$ok = Invoke-Wait -Rows $secondStopArrived -Previous 'STATION-A'
Add-Case '第二站清单已确认：返回的是那一站的站点 id，不是布尔也不是条数' `
    ($ok.Value -ceq 'STATION-B') "值=$($ok.Value) 错误=$($ok.Error)"

$secondStopUnacknowledged = @(
    (Worklist '1' 'STATION-A' 1 $true),
    (Worklist '2' 'STATION-B' 1 $false))
$pending = Invoke-Wait -Rows $secondStopUnacknowledged -Previous 'STATION-A'
Add-Case '第二站清单发了但没被确认：不接受' `
    ($null -ne $pending.Error -and $pending.Error -like '*Timed out*') "值=$($pending.Value) 错误=$($pending.Error)"

# An empty station id on the OTHER worklist must not be read as "different from STATION-A". Without
# the Test-L2RealPresent in the probe this row satisfies the criterion, because '' -ne 'STATION-A'.
$otherStationEmpty = @(
    (Worklist '1' 'STATION-A' 1 $true),
    (Worklist '2' '' 1 $true))
$empty = Invoke-Wait -Rows $otherStationEmpty -Previous 'STATION-A'
Add-Case '另一份清单的站点 id 为空：不算「不同的站点」' `
    ($null -ne $empty.Error -and $empty.Error -like '*Timed out*') "值=$($empty.Value) 错误=$($empty.Error)"

# --------------------------------------- 两站相同的旅程：本票的交付物（control-server#270）
#
# 这道判据按构造不可能被满足，而超时的措辞会把人引向「服务端没发第二站的清单」——票面列的最贵的
# 那部分正是这条错误的诊断方向。所以三条断言分别钉：说清是什么、否掉那个错方向、不声称它非法。

$sameStationJourney = @(
    (Plan 'p1' @('STATION-A', 'STATION-A')),
    (Worklist '1' 'STATION-A' 1 $true))
$same = Invoke-Wait -Rows $sameStationJourney -Previous 'STATION-A'
Add-Case '两站相同：说清这趟旅程的两站是同一个站点，而不是一条光秃秃的超时' `
    ($null -ne $same.Error -and $same.Error -like '*two stops are one station*') "错误=$($same.Error)"
Add-Case '而且明确否掉「服务端没发第二站清单」这个方向' `
    ($null -ne $same.Error -and $same.Error -like '*did not fail to send*') "错误=$($same.Error)"
Add-Case '而且不声称同站旅程非法——那是产品问题，这个检查不回答' `
    ($null -ne $same.Error -and $same.Error -like '*does not answer*') "错误=$($same.Error)"

# 以下三条都是审查（#272）用实测打出来的，自检原本一条都抓不到。共同原因是上面那组同站夹具**恰好只有
# 1 个计划、1 份清单**：`@(f)` 把整张列表读成 1 个元素时，1 个元素的答案碰巧还是对的。**一个夹具的
# 形状太规整，会让一整类错误对自检不可见。**

# 条数：同站 + 2 份已确认清单。`@(f)` 那种写法下这里只可能读成 0 或 1——表示的是「有没有」而不是「有几份」。
$sameStationTwoWorklists = @(
    (Plan 'p1' @('STATION-A', 'STATION-A')),
    (Worklist '1' 'STATION-A' 1 $true),
    (Worklist '2' 'STATION-A' 2 $true))
$sameTwo = Invoke-Wait -Rows $sameStationTwoWorklists -Previous 'STATION-A'
Add-Case '同站 + 2 份已确认清单：消息里报的是 2 份，不是「有没有」' `
    ($null -ne $sameTwo.Error -and $sameTwo.Error -like '*(2 acknowledged worklist(s)*') "错误=$($sameTwo.Error)"

# 计划改过版：按【最新】那版判，不是所有版次的并集。`@(f)` 那种写法下 Select-Object -Last 1 挑到的是
# 「整张列表」，于是旧版 A/B 与新版 A/A 并成 {A, B}，同站诊断不出声。
# （桩函数不执行 SQL 里的 ORDER BY，所以数组顺序就是出厂读取函数会给出的时间顺序：旧版在前。）
$planRevisedToSameStation = @(
    (Plan 'p1' @('STATION-A', 'STATION-B')),
    (Plan 'p2' @('STATION-A', 'STATION-A')),
    (Worklist '1' 'STATION-A' 1 $true))
$revisedPlan = Invoke-Wait -Rows $planRevisedToSameStation -Previous 'STATION-A'
Add-Case '计划改版成同站：按最新那版判，诊断照样出声' `
    ($null -ne $revisedPlan.Error -and $revisedPlan.Error -like '*two stops are one station*') "错误=$($revisedPlan.Error)"

# 计划里有一条腿站点 id 为空：**不许**诊断成同站。这是「第一站改版不许被诊断成同站」的另一半——一个诊断
# 的判别力，一半在它该说时说，另一半在它不该说时不说。先滤掉空值再判唯一性，会把 A 与空串读成「每条腿
# 都在 A」，然后消息会说「服务端没出问题」——而真正的故障恰恰是那条没有站点 id 的腿。
$planWithEmptyLeg = @(
    (Plan 'p1' @('STATION-A', '')),
    (Worklist '1' 'STATION-A' 1 $true))
$emptyLeg = Invoke-Wait -Rows $planWithEmptyLeg -Previous 'STATION-A'
Add-Case '计划里有一条腿站点为空：不许诊断成同站，交还原来的超时' `
    ($null -ne $emptyLeg.Error -and $emptyLeg.Error -like '*Timed out after*' -and
     $emptyLeg.Error -notlike '*two stops are one station*') "错误=$($emptyLeg.Error)"

# 「all at」这句必须按构造成立：用来数的清单要与等待本身用同一个过滤（要求站点 id 非空）。不然一份在 A、
# 一份站点为空时，消息会说「2 份，全在 A」——那一份空的明明不在 A（审查 #272 的 E5）。
$sameStationPlusEmptyWorklist = @(
    (Plan 'p1' @('STATION-A', 'STATION-A')),
    (Worklist '1' 'STATION-A' 1 $true),
    (Worklist '2' '' 1 $true))
$plusEmpty = Invoke-Wait -Rows $sameStationPlusEmptyWorklist -Previous 'STATION-A'
Add-Case '同站 + 一份站点为空的清单：「all at」只数有站点的那份，报 1 不报 2' `
    ($null -ne $plusEmpty.Error -and $plusEmpty.Error -like "*(1 acknowledged worklist(s), all at 'STATION-A'*") `
    "错误=$($plusEmpty.Error)"

# 没有为「别处没有」那道保护写用例，理由写在这里而不是省掉：它防的是最后一次探测之后、失败路径那次读之前
# 另一站的清单刚好到达的竞态。Wait-L2Condition 触发超时的那最后一次探测本身就发生在截止之后，与这次读
# 只隔一个几乎为零的间隔，**没有任何可观测的边界能让「探测看不到、读能看到」**。按墙钟去碰只会得到一条
# 时红时绿的用例，而偶尔不红的红证据等于没有证据。这道保护不冗余（它是唯一覆盖那个竞态的），只是桩数据
# 构造不出它要防的那一刻。

# 失败路径那次读自己也抛时，原来的超时不许被顶替：服务端没了本来就是超时的原因之一，那时这次读也会
# 抛，会把唯一说明「在等什么」的消息一起带走（cs#203 的教训，这里是它的第二次应用）。
$global:l2wsThrowAfterUtc = [DateTime]::UtcNow.AddSeconds(2)
$readDies = Invoke-Wait -Rows $sameStationJourney -Previous 'STATION-A'
$global:l2wsThrowAfterUtc = $null
Add-Case '失败路径那次读也抛时：保留原来的超时，不被这次读自己的错顶替' `
    ($null -ne $readDies.Error -and $readDies.Error -like '*Timed out*' -and
     $readDies.Error -notlike '*went away*') "错误=$($readDies.Error)"

# --------------------------------------- the FIRST stop's half, changed by the same ticket
#
# It went from "at least 1 acknowledged worklist" to "an acknowledged worklist WITH A STATION ID,
# and answer which". That is a new red line, so it gets the same treatment as the second stop's half
# rather than riding on it -- the argument for extracting one applies to the other (review of #269).

$originStationEmpty = @((Worklist '1' '' 1 $true))
$oldFirstCount = Get-OldCountCriterion $originStationEmpty
$firstEmpty = Invoke-FirstWait -Rows $originStationEmpty
Add-Case '第一站清单已确认但站点 id 为空：旧的「条数 >= 1」被满足，说明它会放过' `
    ($oldFirstCount -ge 1) "条数 = $oldFirstCount"
Add-Case '第一站清单已确认但站点 id 为空：新判据超时，不把空值交给第二站' `
    ($null -ne $firstEmpty.Error -and $firstEmpty.Error -like '*Timed out*') `
    "值=$($firstEmpty.Value) 错误=$($firstEmpty.Error)"

$firstOk = Invoke-FirstWait -Rows @((Worklist '1' 'STATION-A' 1 $true))
Add-Case '第一站清单已确认：返回它的站点 id' ($firstOk.Value -ceq 'STATION-A') `
    "值=$($firstOk.Value) 错误=$($firstOk.Error)"

# 已确认清单跨了两个站点 = 「最早那份就是刚做完那一站」这个前提已经破了（control-server#270）。
# 这一条同时钉住两件事，因为它们只能一起观察到：
#   1. 前提破了要**当场抛**，而不是把一个可能指错的值交给第二站；
#   2. 消息里点名的必须是**最早**那一份。取成最晚的话，第二站的等待会拿到自己的站点去比较、永远不可
#      能被满足——而那种错在单看第一站时完全看不出来（它的契约「返回一个非空站点」仍然满足）。
$spansTwoStations = @((Worklist '1' 'STATION-A' 1 $true), (Worklist '2' 'STATION-B' 1 $true))
$spanning = Invoke-FirstWait -Rows $spansTwoStations
Add-Case '已确认清单跨了两个站点：抛前提失效，不把可能指错的值交给第二站' `
    ($null -ne $spanning.Error -and $spanning.Error -like '*span more than one station*') `
    "错误=$($spanning.Error)"
Add-Case '而且消息点名的是【最早】那一份（STATION-A），不是最晚的' `
    ($null -ne $spanning.Error -and $spanning.Error -like "*is at 'STATION-A'*") `
    "错误=$($spanning.Error)"

$firstNone = Invoke-FirstWait -Rows @()
Add-Case '还没有清单：普通超时' ($null -ne $firstNone.Error -and $firstNone.Error -like '*Timed out*') `
    "错误=$($firstNone.Error)"

$firstUnacked = Invoke-FirstWait -Rows @((Worklist '1' 'STATION-A' 1 $false))
Add-Case '清单发了但没被确认：不接受' ($null -ne $firstUnacked.Error -and $firstUnacked.Error -like '*Timed out*') `
    "错误=$($firstUnacked.Error)"

# ---------------------------------------------------------------- the guard on the input itself

# Two assertions, and which one rules out what is worth being exact about -- review of #269 measured
# it, because the obvious reading of the second one is wrong:
#
#   - The MESSAGE assertion is what rules out writing the guard as the probe's first line. Thrown in
#     there it is swallowed by Wait-L2Condition's poll and the caller sees "Timed out ...", not the
#     guard's own words. That variant leaves the query count at 0 as well, so the count cannot tell
#     the two placements apart.
#   - The COUNT assertion rules out the other failure: the guard ran AND the database was read anyway,
#     which is what a guard placed after the wait, or duplicated inside it, would look like.
#
# The count is used rather than elapsed time on purpose: a wall-clock bound is a different claim on a
# slow machine, and slower only makes it pass. "The database was never read" is true by construction
# or not at all.
$vacuous = Invoke-Wait -Rows $secondStopArrived -Previous ''
Add-Case '前一站的站点 id 为空：抛的是护栏自己那条错，而不是让判据退化成「任何清单都算」' `
    ($null -ne $vacuous.Error -and $vacuous.Error -like '*would satisfy the criterion*') `
    "错误=$($vacuous.Error)"
Add-Case '而且没有发生任何数据库读（排除「护栏跑了但查询照样发生」）' `
    ($vacuous.Calls -eq 0) "探针读了 $($vacuous.Calls) 次"

Remove-Variable -Name l2wsRows, l2wsCalls -Scope Global -ErrorAction SilentlyContinue

$bad = 0
foreach ($r in $results) {
    if (-not $r.Ok) { $bad++ }
    Write-Host ("{0}  {1} -> {2}" -f $(if ($r.Ok) { 'ok  ' } else { 'BAD ' }), $r.Name, $r.Actual)
}
if ($bad -gt 0) {
    Write-Host "L2WorklistStationWait self-check: $bad of $($results.Count) cases came out the wrong way."
    exit 1
}
Write-Host "L2WorklistStationWait self-check: all $($results.Count) cases as expected."
