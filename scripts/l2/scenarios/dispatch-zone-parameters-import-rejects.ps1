#Requires -Version 7

<#
每区派车参数整表导入：五类配置错误各自整份拒绝，正确的一份不停服务端就形成新版本（批次7-11，control-server#216；REQ-0198、REQ-0203）。

每区派车参数有两个：途中追加最大允许增量（计划路径代价，毫米）与防饥饿阈值（秒），都按分区批准，没有全项目默认值。填错的后果在派车
里才显出来，而且不报错——途中追加上限写大了，车会绕远路去接单；阈值写小了，低优先级的单全被提上来。所以拦截只有导入这一刻，而且必须
整份拒绝：收下一半的表会让现场以为改好了。

场景走现场那条路：同一个 `ControlServer.FieldOps.exe`，同一个服务端正在用的 SQLite 文件，服务端不停。五份各含一类错误的 CSV 逐份被拒
（退出码 1、原因码与行号精确、库里版本数不变），最后一份正确的表被收下，形成恰好一个新版本并留下快照与业务审计各一条；只读动词读回的
取值与导入一致；服务端还是导入前那个进程，并且照常就绪。

编排器的默认前置是「每区参数未配置」（control-server#206），一版都不写，所以场景开始时这张表是空的，不需要 setup 文件。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection

$zone = [string]$Context.DispatchZone
$header = 'dispatch_zone,en_route_addition_max_path_cost_increase_mm,starvation_threshold_seconds'
$csvRoot = Join-Path $Context.SnapshotRoot 'dispatch-zone-parameters-csv'
$null = New-Item -ItemType Directory -Path $csvRoot -Force

function Get-Count([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    return [int]$rows[0].N
}

function Get-VersionCount { Get-Count 'SELECT COUNT(*) AS N FROM DispatchZoneParameterVersions' }

function Get-SnapshotCount { Get-Count "SELECT COUNT(*) AS N FROM GovernedConfigurationSnapshots WHERE ObjectKind = 'DispatchZoneParameters'" }

function Get-ImportAuditCount {
    Get-Count "SELECT COUNT(*) AS N FROM BusinessAuditRecords WHERE Action = 'DISPATCH_ZONE_PARAMETERS_VERSION_IMPORTED'"
}

function Write-Csv([string]$name, [string[]]$lines) {
    $path = Join-Path $csvRoot "$name.csv"
    # 无 BOM 的 UTF-8、LF 换行：工具文档里的受控格式。喂进去的这份留在证据目录。
    [IO.File]::WriteAllText($path, (($lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
    return $path
}

# 服务端进程的身份：在健康端口上监听的那个进程的 PID 与启动时刻。只比 PID 挡不住「重启后恰好拿到同一个号」。
function Get-ServerIdentity {
    $ids = @(Get-L2ListeningProcess -Port $Context.HealthPort)
    if ($ids.Count -ne 1) { return "listeners: $($ids -join ',')" }
    $process = Get-Process -Id $ids[0] -ErrorAction SilentlyContinue
    if (-not $process) { return "pid $($ids[0]) (gone)" }
    return "pid $($ids[0]) $($process.ProcessName) started $($process.StartTime.ToString('o'))"
}

<#
一次导入，退出码与工具打的那个 JSON 一起拿回来。

`$Context.InvokeFieldOps` 把非零退出码变成异常，消息的形状是编排器写死的（`... exited with <code>: <stdout>`）。这条场景要的正是退出码 1，
所以把异常拆回「退出码 + JSON」；场景不改编排器。
#>
function Invoke-Import([string]$path) {
    try {
        $accepted = & $Context.InvokeFieldOps -Arguments @('import-dispatch-zone-parameters', '--input', $path)
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

# --- 1. 前置：参数未配置，服务端在跑 ------------------------------------------------------------------------

$serverBefore = Get-ServerIdentity
$zoneCount = Get-Count "SELECT COUNT(*) AS N FROM DispatchZoneVehicles WHERE Zone = '$zone'"
$assertions.Add(
    'L2-DZP-01', "服务端以目标配置起过一次，调度策略里有分区 $zone；导入判「分区存在」就按这张表",
    ($zoneCount -ge 1), '>= 1', $zoneCount)

$before = @((Get-VersionCount), (Get-SnapshotCount), (Get-ImportAuditCount))
$assertions.Add(
    'L2-DZP-02', '默认前置是「每区参数未配置」：导入之前一版都没有，也没有快照与导入审计',
    (($before -join '/') -eq '0/0/0'), '0/0/0', ($before -join '/'))

# --- 2. 五类配置错误，各自整份拒绝 -------------------------------------------------------------------------

# 顺序有意为之：第三份是「同一分区重复」。红证据在校验里临时去掉这一类，第三份就会被收下。
$cases = @(
    @{
        Id     = 'L2-DZP-03'
        Name   = 'header-wrong-unit'
        Why    = '表头把途中追加一列写成了秒——单位写在列名里，单位错的表在表头就被拦下'
        Errors = @('1 DISPATCH_ZONE_PARAMETERS_CSV_HEADER_INVALID')
        Lines  = @('dispatch_zone,en_route_addition_max_path_cost_increase_seconds,starvation_threshold_seconds', "$zone,600,600")
    }
    @{
        Id     = 'L2-DZP-04'
        Name   = 'row-malformed'
        Why    = '一行少了一个字段'
        Errors = @('2 DISPATCH_ZONE_PARAMETERS_CSV_ROW_MALFORMED')
        Lines  = @($header, "$zone,20000")
    }
    @{
        Id     = 'L2-DZP-05'
        Name   = 'zone-duplicated'
        Why    = '同一个分区出现两次，两行的途中追加上限还不一样'
        Errors = @('3 DISPATCH_ZONE_DUPLICATED')
        Lines  = @($header, "$zone,20000,600", "$zone,0,600")
    }
    @{
        Id     = 'L2-DZP-06'
        Name   = 'zone-not-found'
        Why    = '分区不在库内当前调度策略里'
        Errors = @('3 DISPATCH_ZONE_NOT_FOUND')
        Lines  = @($header, "$zone,20000,600", 'MAP-25-DIE_ATTACH,20000,600')
    }
    @{
        Id     = 'L2-DZP-07'
        Name   = 'value-invalid'
        Why    = '途中追加上限写成负数'
        Errors = @('2 DISPATCH_ZONE_PARAMETER_VALUE_INVALID')
        Lines  = @($header, "$zone,-1,600")
    }
)

foreach ($case in $cases) {
    $path = Write-Csv $case.Name $case.Lines
    $result = Invoke-Import $path
    $errors = @($result.Payload.errors | ForEach-Object { "$($_.line) $($_.reasonCode)" })
    $counts = @((Get-VersionCount), (Get-SnapshotCount), (Get-ImportAuditCount))
    $journal.Observe("dispatch-zone-parameters-import:$($case.Name)", [string]$result.Payload.outcome, @{ exitCode = $result.ExitCode; output = $result.Payload })
    $assertions.Add(
        $case.Id,
        "$($case.Why)：退出码 1、整份拒绝、原因码与行号精确，库里版本、快照、导入审计都没多",
        ($result.ExitCode -eq 1 -and [string]$result.Payload.outcome -eq 'REJECTED' -and
            ($errors -join ',') -eq ($case.Errors -join ',') -and ($counts -join '/') -eq '0/0/0'),
        "1 / REJECTED / $($case.Errors -join ',') / 0/0/0",
        "$($result.ExitCode) / $($result.Payload.outcome) / $($errors -join ',') / $($counts -join '/')")
}

# --- 3. 正确的一份被收下：恰好一个新版本，快照与审计各一条 -----------------------------------------------

$good = Write-Csv 'accepted' @($header, "$zone,20000,600")
$import = Invoke-Import $good
$journal.Observe('dispatch-zone-parameters-import:accepted', [string]$import.Payload.outcome, @{ exitCode = $import.ExitCode; output = $import.Payload })
$after = @((Get-VersionCount), (Get-SnapshotCount), (Get-ImportAuditCount))
$assertions.Add(
    'L2-DZP-08', '正确的表被收下：退出码 0，恰好形成第 1 版，快照一条、业务审计一条',
    ($import.ExitCode -eq 0 -and [string]$import.Payload.outcome -eq 'OK' -and [long]$import.Payload.version -eq 1 -and
        ($after -join '/') -eq '1/1/1'),
    '0 / OK / v1 / 1/1/1',
    "$($import.ExitCode) / $($import.Payload.outcome) / v$($import.Payload.version) / $($after -join '/')")

$read = & $Context.InvokeFieldOps -Arguments @('dispatch-zone-parameters')
$journal.Observe('dispatch-zone-parameters-read', [string]$read.outcome, @{ output = $read })
$readZone = @($read.zones | Where-Object { [string]$_.dispatchZone -eq $zone })
$readBack = if ($readZone.Count -eq 1) {
    "$($readZone[0].enRouteAddition.state)/$($readZone[0].enRouteAddition.maxPathCostIncreaseMm) " +
    "$($readZone[0].starvation.state)/$($readZone[0].starvation.thresholdSeconds)"
} else { "zones matching: $($readZone.Count)" }
$assertions.Add(
    'L2-DZP-09', '只读动词读回的就是导入的那一版：途中追加上限 20000 毫米、防饥饿阈值 600 秒，内容哈希与导入时一致',
    ([string]$read.outcome -eq 'OK' -and [long]$read.version -eq 1 -and
        [string]$read.contentSha256 -eq [string]$import.Payload.contentSha256 -and $readBack -eq 'ALLOWED/20000 CONFIGURED/600'),
    "OK / v1 / $($import.Payload.contentSha256) / ALLOWED/20000 CONFIGURED/600",
    "$($read.outcome) / v$($read.version) / $($read.contentSha256) / $readBack")

# 第二个事实：服务端没有因为这次导入重启，而且导入之后仍然就绪。进程身份与就绪状态不在导入的那次提交里，所以另等一次，
# 不在读完版本之后顺手直读（scripts/l2/README.md 第 14 条）。
$server = Wait-L2ConditionOrLast -Description 'the same server process is still ready after the import' `
    -Journal $journal -Criterion 'server-unchanged-after-import' -TimeoutSeconds 30 `
    -Probe {
        $ready = try {
            [int](Invoke-WebRequest -Uri "http://127.0.0.1:$($Context.HealthPort)/health/ready" -SkipHttpErrorCheck -TimeoutSec 5).StatusCode
        } catch { 0 }
        @{ Identity = Get-ServerIdentity; Ready = $ready }
    } `
    -Until { param($s) $s.Identity -eq $serverBefore -and $s.Ready -eq 200 }
$assertions.Add(
    'L2-DZP-10', '不停车生效：导入前后是同一个服务端进程（PID 与启动时刻都相同），导入之后它仍然就绪',
    ($server.Identity -eq $serverBefore -and $server.Ready -eq 200),
    "$serverBefore / 200", "$($server.Identity) / $($server.Ready)")

$journal.Note('五类配置错误各自整份拒绝；正确的一份在服务端不停的情况下形成新版本，留下快照与业务审计，只读动词读回一致。')
