#Requires -Version 7

<#
阈值关系不成立的充电策略在导入时整份被拒（批次9-05，control-server#403；REQ-0281）。

编排器的默认前置已经导入、批准、激活一版测试策略（完成线 80 > 入口线 30 >= 余量 30）。场景再经 FieldOps 的 import-charging-policy
导入一版「完成线 30 = 入口线 30」的文件——与现场用的是同一个动词，不直写库。

  1. 导入整份拒绝：退出码 1、outcome REJECTED、原因码 CHARGING_POLICY_THRESHOLD_RELATION_VIOLATED（关系只有
     ChargingPolicyRules.ThresholdRelationViolations 一份定义）；库里版本数不变，生效的仍是默认那一版。
  2. 第二个事实另等（scripts/l2/README.md 第 14 条）：之后发一条需求，照常按默认策略派出，旅程记下的是默认那一版的版本号。

启动拒绝（库里已有一版生效的坏版本时服务端拒绝启动）在 L2 上构造不出：导入已经挡住坏版本，库里造不出生效的坏版本，经 FieldOps 之外的路
写库又不是这里该做的事。那一半由 L1 的宿主级用例证明（ChargingPolicyStartupCheckTests，绕过导入直接经存储写入、批准、激活）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$mes = $Context.MesIngest

function Get-Count([string]$sql) { [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N }

function Get-ActiveVersion {
    $rows = Invoke-L2Query -Connection $connection -Sql 'SELECT Version FROM ChargingPolicyActivations ORDER BY Sequence DESC LIMIT 1'
    if ($rows.Count -eq 0) { return $null }
    return [long]$rows[0].Version
}

<#
一次被拒的导入。$Context.InvokeFieldOps 把非零退出码变成异常，这里要的正是退出码 1，所以把异常拆回「退出码 + 工具打的 JSON」
（写法同 area-assignment-import-rejects）。
#>
function Invoke-RejectedImport([string]$path) {
    try {
        $accepted = & $Context.InvokeFieldOps -Arguments @('import-charging-policy', '--input', $path, '--fleet', $Context.VehicleKey)
        return @{ ExitCode = 0; Payload = $accepted }
    }
    catch {
        $message = [string]$_.Exception.Message
        $match = [regex]::Match($message, 'exited with (?<code>\d+): (?<json>\{.*\})', 'Singleline')
        if (-not $match.Success) { throw "Unexpected FieldOps failure: $message" }
        return @{ ExitCode = [int]$match.Groups['code'].Value; Payload = ($match.Groups['json'].Value | ConvertFrom-Json) }
    }
}

# --- 0. 前置：默认测试策略生效 ---------------------------------------------------------------------------

$versionsBefore = Get-Count 'SELECT COUNT(*) AS N FROM ChargingPolicyVersions'
$activeBefore = Get-ActiveVersion
$assertions.Add(
    'L2-CTR-00',
    '前置：编排器导入并激活了默认测试策略，库里恰好一版',
    ($versionsBefore -eq 1 -and $null -ne $activeBefore),
    '1 版，已激活',
    "$versionsBefore 版，激活 $activeBefore")

# --- 1. 关系不成立的一版：整份拒绝 -------------------------------------------------------------------------

$bad = [ordered]@{
    minimumPostTaskBatteryMarginPercent  = 30
    mandatoryChargeEntryThresholdPercent = 30
    chargingCompletionThresholdPercent   = 30
    estimatedTaskConsumptionPercent      = 0
    progressStabilizationSeconds         = 180
    progressObservationWindowSeconds     = 600
    progressMinimumIncreasePercent       = 3
    vehicleScope                         = @()
    changeNote                           = 'L2 charging-thresholds-relation-refused: completion equals entry, REQ-0281 says greater'
}
$file = Join-Path $Context.SnapshotRoot 'relation-violating-charging-policy.json'
[IO.File]::WriteAllText($file, ($bad | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
$rejected = Invoke-RejectedImport $file
$journal.Observe('relation-violating-import', $rejected.ExitCode, @{ payload = $rejected.Payload })
$reasons = @($rejected.Payload.errors | ForEach-Object { [string]$_.reasonCode })
$versionsAfter = Get-Count 'SELECT COUNT(*) AS N FROM ChargingPolicyVersions'
$activeAfter = Get-ActiveVersion

$assertions.Add(
    'L2-CTR-01',
    '完成线 = 入口线的一版被整份拒绝：退出码 1、REJECTED、原因码是阈值关系那一个，库里版本数不变、生效的仍是默认那一版',
    ($rejected.ExitCode -eq 1 -and [string]$rejected.Payload.outcome -eq 'REJECTED' -and
        ($reasons -join ',') -eq 'CHARGING_POLICY_THRESHOLD_RELATION_VIOLATED' -and
        $versionsAfter -eq $versionsBefore -and $activeAfter -eq $activeBefore),
    "1 / REJECTED / CHARGING_POLICY_THRESHOLD_RELATION_VIOLATED / $versionsBefore 版 / 激活 $activeBefore",
    "$($rejected.ExitCode) / $($rejected.Payload.outcome) / $($reasons -join ',') / $versionsAfter 版 / 激活 $activeAfter")

# --- 2. 第二个事实另等：之后的需求照常按默认策略派出 ------------------------------------------------------

$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')
$journal.Note("Publishing demand $($demandGuid.ToString('N')) after the refused import.")
$null = $mes.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot = "L2-CTR-$($Context.RunId)"; area = 'N1-3'
    eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})

$journey = Wait-L2ConditionOrLast -Description 'the demand published after the refused import was dispatched' `
    -Journal $journal -Criterion 'demand-dispatched-under-default-policy' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql (
            "SELECT VehicleKey, ChargingPolicyVersion, PublishedBatteryState FROM JourneyRuntimes WHERE DemandId = '$demandId'")
        if ($rows.Count -eq 0) { return $null }
        return $rows[0]
    } `
    -Until { param($v) $null -ne $v }

$assertions.Add(
    'L2-CTR-02',
    '被拒之后照常派车：需求派给了车，旅程记下的是默认那一版的版本号、SUFFICIENT',
    ($null -ne $journey -and [string]$journey.VehicleKey -eq $Context.VehicleKey -and
        [string]$journey.ChargingPolicyVersion -eq [string]$activeBefore -and [string]$journey.PublishedBatteryState -eq 'SUFFICIENT'),
    "$($Context.VehicleKey) / v$activeBefore / SUFFICIENT",
    $(if ($null -ne $journey) { "$($journey.VehicleKey) / v$($journey.ChargingPolicyVersion) / $($journey.PublishedBatteryState)" } else { 'not dispatched' }))

$journal.Note('关系不成立的策略导入被整份拒绝，默认策略照常生效、照常派车。')
