#Requires -Version 7

<#
让站（批次7-08，control-server#213；规格第 5.1 节第 8 条，出口判据 ③ 前半与 ⑥ 的让站那一半）：一台车在站上持货等单，
另一台车被承诺以这个站为下一停靠——等单的车结束等单、不再接单、前往卸货。

两台合成车，都服务同一个分区。持货超时设成 10 分钟，远长于这条场景，所以装货阶段若是关了，结束原因只能是让站。
1. 需求甲（1 花篮）给主车，主车开到 12 号站装完，进入 CARGO_HOLDING_WAIT（StationYieldCommon 的布置，判据 01）。
2. 需求乙（4 花篮、前侧、同在 12 号站）只能给另一台车：主车前侧只剩 3 格。另一台车受理，下一停靠就是 12 号站（02）。
3. 让站确实触发了：主车 CLOSED/WAITING_STATION_YIELD，触发列记另一台车、时刻不早于乙的受理，车上收到那张快照（03、04）。
4. 另等第二个事实：主车离站开向关卡——关卡腿建了单（05）。
5. 让站之后发的需求戊（后侧、11 号站、1 花篮）本可装进主车（后侧 4 格全空），却不进主车那一趟（06）。它去了哪里不断言：
   另一台车也能追加它，那与本条无关。

红证据（票面）：只在持货超时时结束等单、不看别的车——03 变红，05、06 随之红（主车一直等到 10 分钟）。
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

Initialize-L2YieldRig $Context

# --- 1、2. 主车在站上等单；另一台车被承诺以这个站为下一停靠 -----------------------------------------------

$holder = Start-L2YieldHolder $Context 'L2-WSY'
$comer = Send-L2YieldComer $Context $holder 'L2-WSY'

# --- 3. 让站确实触发了 --------------------------------------------------------------------------------

$null = Confirm-L2YieldTriggered $Context $holder $comer 'L2-WSY'

# --- 4. 另等第二个事实：主车前往卸货 ----------------------------------------------------------------------

$gate = Wait-L2ConditionOrLast -Description 'the holder left for the gate' -Journal $journal -Criterion 'gate-intent' `
    -TimeoutSeconds 60 -Probe { Get-L2YieldGateIntent $connection $holder.JourneyId } -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-WSY-05', '让站之后主车离站开向关卡：关卡腿建了单',
    ($null -ne $gate), 'a gate leg intent', $(if ($gate) { "$($gate.UpperId) $($gate.Status)" } else { '(none)' }))

# --- 5. 让站之后的需求不进主车 ------------------------------------------------------------------------------

$e = New-L2CargoDemand 'E' 'C15-13' 1 $Context.RunId
Publish-L2CargoDemand $Context $e
# 等派车轮对戊下过定论——受理进了某一趟，或者留在积压里、理由不是 ELIGIBLE——再看它在不在主车那一趟：派车轮是另一次写入。
# ELIGIBLE 不算定论：那是「这一轮判它可派」，受理还在后面。第一版把它当定论，读得太早，让站坏了的红证据里它照样绿
# （l2-red-1：主车一直在等单，戊后来追加进了主车那一趟，而这一条早已判过）。
$verdict = Wait-L2ConditionOrLast -Description 'the dispatch round judged demand E' -Journal $journal -Criterion 'e-verdict' `
    -TimeoutSeconds 60 `
    -Probe {
        $backlog = Get-L2CargoBacklog $connection $e.Id
        $journey = Get-L2YieldJourney $connection $e.Id
        [pscustomobject]@{ Backlog = $backlog; Journey = $journey }
    } `
    -Until {
        param($v)
        $null -ne $v.Journey -or ($null -ne $v.Backlog -and -not [string]::IsNullOrEmpty([string]$v.Backlog.ReasonCode) -and
            [string]$v.Backlog.ReasonCode -ne 'ELIGIBLE')
    }
$eJourney = Get-L2YieldJourney $connection $e.Id
$assertions.Add(
    'L2-WSY-06', '让站之后发的需求戊（后侧，主车后侧全空）不进主车那一趟',
    ($null -ne $verdict -and ($null -ne $verdict.Journey -or $null -ne $verdict.Backlog) -and
        ($null -eq $eJourney -or [string]$eJourney.JourneyId -ne $holder.JourneyId)),
    "judged, and not on $($holder.JourneyId)",
    $(if ($eJourney) { "on $($eJourney.JourneyId) ($($eJourney.AgvId))" }
      elseif ($null -ne $verdict -and $null -ne $verdict.Backlog) { "backlog $($verdict.Backlog.ReasonCode)" }
      else { '(never judged)' }))

$snapshots = Get-L2YieldSnapshotsOf $connection $holder.HolderAgvId
$journal.Observe('holder-loading-phase-snapshots',
    ((@($snapshots) | ForEach-Object { "$($_.Revision):$($_.State)$(if ($_.Reason) { "/$($_.Reason)" })" }) -join ' '),
    @{ snapshots = $snapshots })
$journal.Note('主车在站上持货等单，另一台车被承诺以这个站为下一停靠：主车以 WAITING_STATION_YIELD 结束装货阶段、离站，之后的需求不再进它。')
