#Requires -Version 7

<#
分区归属表整表导入的五类配置错误各自整份拒绝：批次 4 出口场景④（规格 8.3、8.6，REQ-0350）。

这是**负向证据**。分区归属表把每个区域号（AREA）归入一个分区，并指派该 AREA 的机台只能开哪一侧仓门
（`FRONT` 前侧 1～4 号、`REAR` 后侧 5～8 号）。填错的后果是车停到机台前开错侧的仓门、取不出产品，而
服务端在运行时看不出来——规格 5.1 第 4 条说得很清楚，某个区域号的侧填反系统发现不了，首次真实作业才暴露。
所以拦截只有一次机会，就是导入这一刻，而且必须整份拒绝：收下一半的表会让现场以为改好了。

场景走的是现场那条路：同一个 `ControlServer.FieldOps.exe`，同一个服务端正在用的 SQLite 文件，服务端
不停。五份各含一类错误的 CSV 逐份被拒（退出码 1、原因码对应、一行都没写进库），最后一份正确的表被收下，
形成第 1 版并留下快照与业务审计各一条。

场景自己把已批准八仓事实入库（`seed-approved-facts`），不依赖编排器的默认前置：分组取值的合法集合来自库内
已发布的整车仓位模型，没有模型就连 `FRONT` 都判不了。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection

$zone = 'MAP-25-WIRE_TO_GATE'
$csvRoot = Join-Path $Context.SnapshotRoot 'area-assignment-csv'
$null = New-Item -ItemType Directory -Path $csvRoot -Force

function Get-Count([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    return [int]$rows[0].N
}

function Get-VersionCount {
    return Get-Count 'SELECT COUNT(*) AS N FROM DispatchZoneAreaAssignmentVersions'
}

function Get-ImportAuditCount {
    return Get-Count @"
SELECT COUNT(*) AS N FROM BusinessAuditRecords
WHERE Action = 'DISPATCH_ZONE_AREA_ASSIGNMENT_VERSION_IMPORTED'
"@
}

function Write-Csv([string]$name, [string[]]$lines) {
    $path = Join-Path $csvRoot "$name.csv"
    # 无 BOM 的 UTF-8、LF 换行，就是工具文档里那个受控格式。逐字节写在这里，证据目录留下的正是喂进去的那份。
    [IO.File]::WriteAllText($path, (($lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
    return $path
}

<#
一次被拒的导入。

`$Context.InvokeFieldOps` 把任何非零退出码变成异常——对别的动词那是对的，一个悄悄什么都没做的治理动作
会让场景后面证的是别的东西。但**这条场景要的正是退出码 1**，所以这里把异常拆回「退出码 + 工具打的那个
JSON」。消息的形状是编排器写死的（`... exited with <code>: <stdout>`），场景不改编排器（批次 4 内编排器
归 control-server#71）。
#>
function Invoke-RejectedImport([string]$path) {
    try {
        $accepted = & $Context.InvokeFieldOps -Arguments @('import-area-assignments', '--input', $path)
        return @{ ExitCode = 0; Payload = $accepted }
    }
    catch {
        $message = [string]$_.Exception.Message
        $match = [regex]::Match($message, 'exited with (?<code>\d+): (?<json>\{.*\})', 'Singleline')
        if (-not $match.Success) { throw "Unexpected FieldOps failure: $message" }
        return @{
            ExitCode = [int]$match.Groups['code'].Value
            Payload  = ($match.Groups['json'].Value | ConvertFrom-Json)
        }
    }
}

# --- 1. 前置：已批准八仓事实入库，调度策略里确实有这个分区 -------------------------------------------

$seed = & $Context.InvokeFieldOps -Arguments @('seed-approved-facts')
$groups = Invoke-L2Query -Connection $connection -Sql @"
SELECT DISTINCT s.SlotPosition AS SlotPosition
FROM SlotModelSlots s
JOIN SlotModelVersions m ON m.SlotModelVersionId = s.SlotModelVersionId
WHERE m.Status = 'PUBLISHED'
"@
$groupValues = @($groups | ForEach-Object { [string]$_.SlotPosition } | Sort-Object)
$assertions.Add(
    'L2-AAI-01', '已发布整车模型把八个仓位分成 FRONT／REAR 两组，导入的合法分组取值就是从这里读的',
    ($seed.outcome -eq 'OK' -and ($groupValues -join ',') -eq 'FRONT,REAR'),
    'OK / FRONT,REAR', "$($seed.outcome) / $($groupValues -join ',')")

$zoneCount = Get-Count "SELECT COUNT(*) AS N FROM DispatchZoneVehicles WHERE Zone = '$zone'"
$assertions.Add(
    'L2-AAI-02', "服务端以目标配置起过一次，调度策略里有分区 $zone；导入判「分区存在」就按这张表",
    ($zoneCount -ge 1), '>= 1', $zoneCount)

$versionsBefore = Get-VersionCount
$auditsBefore = Get-ImportAuditCount
$assertions.Add(
    'L2-AAI-03', '导入之前库里一版分区归属表都没有，也没有导入审计',
    ($versionsBefore -eq 0 -and $auditsBefore -eq 0), '0 / 0', "$versionsBefore / $auditsBefore")

# --- 2. 五类配置错误，各自整份拒绝 ---------------------------------------------------------------------

$cases = @(
    @{
        Id     = 'L2-AAI-04'
        Name   = 'group-not-in-model'
        Reason = 'SLOT_POSITION_NOT_IN_PUBLISHED_MODEL'
        Why    = '分组取值不属于当前车型的分组集合（这里写的是改名前的 LEFT）'
        Lines  = @('area,dispatch_zone,slot_position', "C15-13,$zone,FRONT", "C15-14,$zone,LEFT")
    }
    @{
        Id     = 'L2-AAI-05'
        Name   = 'group-missing'
        Reason = 'SLOT_POSITION_MISSING'
        Why    = '缺分组：缺少分组指派的映射不成立（REQ-0191）'
        Lines  = @('area,dispatch_zone,slot_position', "C15-13,$zone,FRONT", "C15-14,$zone,")
    }
    @{
        Id     = 'L2-AAI-06'
        Name   = 'area-duplicated'
        Reason = 'AREA_DUPLICATED'
        Why    = '同一个区域号出现两次，两行还指了相反的侧'
        Lines  = @('area,dispatch_zone,slot_position', "C15-13,$zone,FRONT", "C15-13,$zone,REAR")
    }
    @{
        Id     = 'L2-AAI-07'
        Name   = 'area-format-invalid'
        Reason = 'AREA_FORMAT_INVALID'
        Why    = '区域号写法不合规，与站名里解析区域号用的是同一条规则'
        Lines  = @('area,dispatch_zone,slot_position', "C15-13,$zone,FRONT", "c15 14,$zone,REAR")
    }
    @{
        Id     = 'L2-AAI-08'
        Name   = 'dispatch-zone-not-found'
        Reason = 'DISPATCH_ZONE_NOT_FOUND'
        Why    = '分区不在库内当前调度策略里'
        Lines  = @('area,dispatch_zone,slot_position', "C15-13,$zone,FRONT", 'C15-14,MAP-25-DIE_ATTACH,REAR')
    }
)

foreach ($case in $cases) {
    $path = Write-Csv $case.Name $case.Lines
    $result = Invoke-RejectedImport $path
    $reasons = @($result.Payload.errors | ForEach-Object { [string]$_.reasonCode } | Sort-Object -Unique)
    $versions = Get-VersionCount
    $audits = Get-ImportAuditCount
    $journal.Note("Rejected import '$($case.Name)': exit $($result.ExitCode), reasons $($reasons -join ',').")
    $assertions.Add(
        $case.Id,
        "$($case.Why)：整份拒绝，原因码 $($case.Reason)，库里一版都没多、没有成功导入的审计",
        ($result.ExitCode -eq 1 -and
            [string]$result.Payload.outcome -eq 'REJECTED' -and
            ($reasons -join ',') -eq $case.Reason -and
            $versions -eq 0 -and $audits -eq 0),
        "1 / REJECTED / $($case.Reason) / 0 versions / 0 audits",
        "$($result.ExitCode) / $($result.Payload.outcome) / $($reasons -join ',') / $versions versions / $audits audits")
}

# 被拒的一份表连自己的错误行号都要报全，否则现场得一轮一轮试。
$multi = Write-Csv 'every-kind-at-once' @(
    'area,dispatch_zone,slot_position'
    "C15-13,$zone,FRONT"
    "c15 14,$zone,REAR"
    "C15-15,$zone,LEFT"
    "C15-16,$zone,"
    "C15-13,$zone,REAR"
    'C15-17,MAP-25-DIE_ATTACH,FRONT'
)
$multiResult = Invoke-RejectedImport $multi
$multiReasons = @($multiResult.Payload.errors | ForEach-Object { [string]$_.reasonCode } | Sort-Object)
$expectedReasons = @(
    'AREA_DUPLICATED', 'AREA_FORMAT_INVALID', 'DISPATCH_ZONE_NOT_FOUND',
    'SLOT_POSITION_MISSING', 'SLOT_POSITION_NOT_IN_PUBLISHED_MODEL') | Sort-Object
$assertions.Add(
    'L2-AAI-09', '一份同时含五类错误的表，一次把五行全报出来，而不是停在第一处',
    (($multiReasons -join ',') -eq ($expectedReasons -join ',') -and
        @($multiResult.Payload.errors).Count -eq 5),
    ($expectedReasons -join ','), ($multiReasons -join ','))

# --- 3. 正确的一份被收下：版本加一，快照与审计各一条 ---------------------------------------------------

$goodLines = @('area,dispatch_zone,slot_position', "C15-13,$zone,FRONT", "C15-14,$zone,REAR")
$good = Write-Csv 'accepted' $goodLines

$dryRun = & $Context.InvokeFieldOps -Arguments @('import-area-assignments', '--input', $good, '--dry-run')
$assertions.Add(
    'L2-AAI-10', '先 --dry-run 过一遍：校验通过，但库里还是一版都没有',
    ([string]$dryRun.outcome -eq 'OK' -and [bool]$dryRun.dryRun -and
        $null -eq $dryRun.version -and (Get-VersionCount) -eq 0),
    'OK / dryRun / no version written', "$($dryRun.outcome) / $($dryRun.dryRun) / $($dryRun.version)")

$import = & $Context.InvokeFieldOps -Arguments @('import-area-assignments', '--input', $good)
$versionsAfter = Get-VersionCount
$auditsAfter = Get-ImportAuditCount
$snapshots = Get-Count @"
SELECT COUNT(*) AS N FROM GovernedConfigurationSnapshots
WHERE ObjectKind = 'DispatchZoneAreaAssignment'
"@
$assertions.Add(
    'L2-AAI-11', '正确的表被收下：第 1 版落库，快照一条、业务审计一条',
    ([string]$import.outcome -eq 'OK' -and [long]$import.version -eq 1 -and [int]$import.entryCount -eq 2 -and
        $versionsAfter -eq 1 -and $snapshots -eq 1 -and $auditsAfter -eq 1),
    'OK / v1 / 2 entries / 1 version / 1 snapshot / 1 audit',
    "$($import.outcome) / v$($import.version) / $($import.entryCount) entries / $versionsAfter version / $snapshots snapshot / $auditsAfter audit")

$stored = Invoke-L2Query -Connection $connection -Sql @"
SELECT v.ContentSha256 AS ContentSha256, s.ContentSha256 AS SnapshotSha256
FROM DispatchZoneAreaAssignmentVersions v
JOIN GovernedConfigurationSnapshots s ON s.SnapshotId = v.SnapshotId
WHERE v.Version = 1
"@
$assertions.Add(
    'L2-AAI-12', '版本行记的内容哈希就是它那条快照的哈希，工具打出来的也是同一个',
    ($stored.Count -eq 1 -and
        [string]$stored[0].ContentSha256 -eq [string]$stored[0].SnapshotSha256 -and
        [string]$stored[0].ContentSha256 -eq [string]$import.contentSha256),
    [string]$import.contentSha256,
    $(if ($stored.Count -eq 0) { '(无行)' } else { "$($stored[0].ContentSha256) / $($stored[0].SnapshotSha256)" }))

# 同内容再导入一次形成第 2 版：回滚就是把旧内容再导入一次，所以「内容没变」不能被当成「不用写」。
$again = & $Context.InvokeFieldOps -Arguments @('import-area-assignments', '--input', $good)
$assertions.Add(
    'L2-AAI-13', '内容一模一样的一份再导入，形成第 2 版而不是被当成无变化跳过',
    ([long]$again.version -eq 2 -and [string]$again.contentSha256 -eq [string]$import.contentSha256 -and
        (Get-VersionCount) -eq 2 -and (Get-ImportAuditCount) -eq 2),
    'v2 / same sha / 2 versions / 2 audits',
    "v$($again.version) / $($again.contentSha256 -eq $import.contentSha256) / $(Get-VersionCount) versions / $(Get-ImportAuditCount) audits")

# --- 4. 只读动词：当前那一版与指定那一版都读得出来 -----------------------------------------------------

$current = & $Context.InvokeFieldOps -Arguments @('area-assignments')
$first = & $Context.InvokeFieldOps -Arguments @('area-assignments', '--version', '1')
$currentAreas = @($current.entries | ForEach-Object { [string]$_.area })
$currentSides = @($current.entries | ForEach-Object { [string]$_.slotPosition })
$assertions.Add(
    'L2-AAI-14', 'area-assignments 打印当前那一版的全部行，两个区域号各自指了哪一侧',
    ([string]$current.outcome -eq 'OK' -and [long]$current.version -eq 2 -and
        ($currentAreas -join ',') -eq 'C15-13,C15-14' -and ($currentSides -join ',') -eq 'FRONT,REAR'),
    'OK / v2 / C15-13,C15-14 / FRONT,REAR',
    "$($current.outcome) / v$($current.version) / $($currentAreas -join ',') / $($currentSides -join ',')")
$assertions.Add(
    'L2-AAI-15', '--version 1 读的是第 1 版，读这两次没有再写出任何版本或审计',
    ([long]$first.version -eq 1 -and (Get-VersionCount) -eq 2 -and (Get-ImportAuditCount) -eq 2),
    'v1 / 2 versions / 2 audits',
    "v$($first.version) / $(Get-VersionCount) versions / $(Get-ImportAuditCount) audits")

$journal.Note('五类配置错误各自整份拒绝，正确的一份形成新版本并留下快照与不可改写审计。')
