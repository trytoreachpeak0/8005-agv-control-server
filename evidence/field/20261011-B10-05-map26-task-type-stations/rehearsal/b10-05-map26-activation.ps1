#Requires -Version 7

<#
control-server#548 本地演练（不是台架场景，不进 scripts/l2/scenarios/）：在一份把 mapId 改成 26 的 scripts/l2 副本上跑。

假 RIoT 的 26 号图站点表就是 2026-10-11 只读读回的 216 个站（catalog-26.json），服务端开着旅程运行时、按改动前的 26 号图预置
建库（只有 WIRE_TO_GATE → 210，旧身份核对号）。服务端完整确认一次目录后，在它正在用的库上按 scripts/parallel/README.md 的
步骤跑 FieldOps：

  0. task-type-stations：读出预置装进来的版本 1；
  1. 复现 factory01 10-10 的状态：激活 217 + 210（旧记录号），得到版本 2；
  2. 本票候选 --dry-run；
  3. 本票候选正式激活，得到版本 3；
  4. task-type-stations 核对；
  5. rollback-task-type-stations --version 2，得到版本 4（内容同版本 2）；
  6. task-type-stations 核对；
  7. 三份错误候选各跑一次（正式激活，不带 --dry-run），各自被拒；最后再读一次，确认它们什么都没切换。

路径由环境变量给：B10_05_OUT（本次输出目录）、B10_05_CATALOG、B10_05_CANDIDATE（仓库里的候选）、B10_05_HELPERS（辅助候选目录）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$mapId = $Context.MapId
$out = $env:B10_05_OUT
$catalog = $env:B10_05_CATALOG
$candidate = $env:B10_05_CANDIDATE
$helpers = $env:B10_05_HELPERS
foreach ($path in @($catalog, $candidate, $helpers)) { if (-not (Test-Path -LiteralPath $path)) { throw "missing: $path" } }
$log = Join-Path $out 'fieldops-transcript.txt'

function Invoke-Step([string]$Name, [string[]]$Arguments) {
    $exit = 0
    $json = $null
    try {
        $json = & $Context.InvokeFieldOps -Arguments $Arguments
        $text = $json | ConvertTo-Json -Depth 20 -Compress
    } catch {
        $message = $_.Exception.Message
        $exit = if ($message -match 'exited with (-?\d+)') { [int]$Matches[1] } else { -1 }
        # The thrown text is "... exited with N: {json}": the JSON follows a prefix on the same line (run 3 missed it).
        $line = @($message -split "`n" | Where-Object { $_.Contains('{"command"') }) | Select-Object -Last 1
        if ($line) { $json = $line.Substring($line.IndexOf('{"command"')) | ConvertFrom-Json }
        $text = $message
    }
    Add-Content -LiteralPath $log -Encoding utf8NoBOM -Value @(
        "### $Name",
        "> ControlServer.FieldOps $($Arguments[0]) --database <rig database> $(@($Arguments | Select-Object -Skip 1) -join ' ')",
        "exit=$exit",
        $text,
        '')
    return [pscustomobject]@{ Exit = $exit; Json = $json }
}

function Format-Bindings($Version) {
    if ($null -eq $Version) { return '(none)' }
    (@($Version.bindings) | ForEach-Object { "$($_.stationRiotId)/$($_.stationName)/$($_.siteVerificationRef)" }) -join '; '
}

# The revision the server must have confirmed: TaskTypeStationCatalogEvidence.Fingerprint / RevisionOf over catalog-26.json.
$stations = @((Get-Content -LiteralPath $catalog -Raw | ConvertFrom-Json).stations | Sort-Object { [int]$_.stationId })
$canonical = ($stations | ForEach-Object { "$mapId`t$($_.stationId)`t$($_.stationName)" }) -join "`n"
$hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))
$expectedRevision = [Convert]::ToInt64([Convert]::ToHexString($hash).Substring(0, 16), 16) -band [long]::MaxValue

$state = Wait-L2Condition -Description "the server completely confirmed Map $mapId's catalog at revision $expectedRevision" `
    -Journal $journal -Criterion 'catalog-confirmed' -TimeoutSeconds 90 `
    -Probe {
        $rows = Invoke-L2Query -Connection $Context.Connection -Sql (
            "SELECT CatalogRevision, LastCompleteConfirmationAt, ApprovedMaxUnconfirmedSeconds FROM MapStationCatalogStates WHERE MapId = $mapId")
        , $rows
    } `
    -Until { param($v) @($v).Count -eq 1 -and $null -ne $v[0].LastCompleteConfirmationAt -and [long]$v[0].CatalogRevision -eq $expectedRevision }
$assertions.Add('B10-05-00', "服务端完整确认的 26 号图目录就是 catalog-26.json（216 个站，修订 $expectedRevision）",
    ([long]$state[0].CatalogRevision -eq $expectedRevision), $expectedRevision, $state[0].CatalogRevision)

$reason = 'control-server#548 local rehearsal'
$read = { (Invoke-Step $args[0] @('task-type-stations', '--map', "$mapId")) }

$r0 = & $read '0 read: what the preset loaded'
$assertions.Add('B10-05-01', '预置装进来的是版本 1：只有 WIRE_TO_GATE → 210，旧身份核对号',
    ($r0.Exit -eq 0 -and $r0.Json.activeVersion -eq 1 -and (Format-Bindings $r0.Json.active) -eq '210/关卡/MAP-26-WIRE_TO_GATE-B6-IDENTITY-20260919'),
    '1: 210/关卡/MAP-26-WIRE_TO_GATE-B6-IDENTITY-20260919', "$($r0.Json.activeVersion): $(Format-Bindings $r0.Json.active)")

$r1 = Invoke-Step '1 activate: replicate factory01 version 2 (10-10)' @(
    'activate-task-type-stations', '--input', (Join-Path $helpers '00-replicate-factory01-version-2.json'),
    '--catalog', $catalog, '--reason', "${reason}: replicate factory01 version 2")
$assertions.Add('B10-05-02', '复现 factory01：激活 217 + 210 得到版本 2',
    ($r1.Exit -eq 0 -and $r1.Json.outcome -eq 'OK' -and $r1.Json.targetVersion -eq 2), 'OK, version 2', "$($r1.Json.outcome), version $($r1.Json.targetVersion)")

$r2 = Invoke-Step '2 activate --dry-run: this ticket''s candidate' @(
    'activate-task-type-stations', '--input', $candidate, '--catalog', $catalog, '--reason', "${reason}: dry run", '--dry-run')
# The preview lists a task type only when its station id or name changes (TaskTypeStationActivationService.Diff): 210 and 217
# keep their stations and only get new site verification references, so they are not listed. Run 1 asserted six and was red.
$changed = @($r2.Json.changes | ForEach-Object { $_.taskType } | Sort-Object)
$expectedChanged = @('DIE_TO_OVEN', 'DIE_TO_WIRE_STAGING', 'WIRE_TO_NITROGEN', 'WIRE_TO_OPTICAL')
$assertions.Add('B10-05-03', 'dry-run 预览通过、无违规，变化恰好是四类新增（210、217 只换记录号，预览不列），什么都没切换',
    ($r2.Exit -eq 0 -and $r2.Json.outcome -eq 'PREVIEW' -and @($r2.Json.violations).Count -eq 0 -and
        ($changed -join ',') -eq ($expectedChanged -join ',') -and
        @($r2.Json.changes | Where-Object { $null -ne $_.before }).Count -eq 0 -and $null -eq $r2.Json.targetVersion),
    "PREVIEW, 0 violations, added $($expectedChanged -join ',')", "$($r2.Json.outcome), $(@($r2.Json.violations).Count) violations, $($changed -join ',')")

$afterDryRun = & $read '2b read: the dry run switched nothing'
$assertions.Add('B10-05-04', 'dry-run 之后生效版本仍是 2',
    ($afterDryRun.Json.activeVersion -eq 2), 2, $afterDryRun.Json.activeVersion)

$r3 = Invoke-Step '3 activate: this ticket''s candidate' @(
    'activate-task-type-stations', '--input', $candidate, '--catalog', $catalog, '--reason', "${reason}: activate four same-direction task types")
$assertions.Add('B10-05-05', '正式激活得到版本 3', ($r3.Exit -eq 0 -and $r3.Json.outcome -eq 'OK' -and $r3.Json.targetVersion -eq 3),
    'OK, version 3', "$($r3.Json.outcome), version $($r3.Json.targetVersion)")

$r4 = & $read '4 read: verify version 3'
$expected = (@((Get-Content -LiteralPath $candidate -Raw | ConvertFrom-Json).bindings) |
        ForEach-Object { "$($_.stationRiotId)/$($_.stationName)/$($_.siteVerificationRef)" } | Sort-Object) -join '; '
$actual = (@($r4.Json.active.bindings) | ForEach-Object { "$($_.stationRiotId)/$($_.stationName)/$($_.siteVerificationRef)" } | Sort-Object) -join '; '
$assertions.Add('B10-05-06', '只读核对：生效版本 3、六条绑定与候选逐条相同、需求集六类、没有暂停、没有未结尝试',
    ($r4.Json.activeVersion -eq 3 -and $actual -eq $expected -and @($r4.Json.active.requiredTaskTypes).Count -eq 6 -and
        @($r4.Json.unreleasedHolds).Count -eq 0 -and $null -eq $r4.Json.openAttempt),
    "3: $expected", "$($r4.Json.activeVersion): $actual")

$r5 = Invoke-Step '5 rollback to version 2' @(
    'rollback-task-type-stations', '--map', "$mapId", '--version', '2', '--catalog', $catalog, '--reason', "${reason}: roll back")
$r6 = & $read '6 read: verify the rollback'
$assertions.Add('B10-05-07', '回滚到版本 2 生效为新版本 4，内容与版本 2 相同',
    ($r5.Exit -eq 0 -and $r5.Json.outcome -eq 'OK' -and $r6.Json.activeVersion -eq 4 -and
        (Format-Bindings $r6.Json.active) -eq (Format-Bindings (@($r6.Json.versions) | Where-Object { $_.version -eq 2 }))),
    '4 = content of 2', "$($r6.Json.activeVersion): $(Format-Bindings $r6.Json.active)")

$bad = [ordered]@{
    'bad-1-area-named-station.json'        = 'TASK_TYPE_BINDING_AREA_NAMED_STATION'
    'bad-2-station-reused.json'            = 'TASK_TYPE_STATION_REUSED'
    'bad-3-site-verification-missing.json' = 'TASK_TYPE_BINDING_SITE_VERIFICATION_MISSING'
}
$index = 8
foreach ($file in $bad.Keys) {
    $r = Invoke-Step "7 activate: $file" @(
        'activate-task-type-stations', '--input', (Join-Path $helpers $file), '--catalog', $catalog, '--reason', "${reason}: $file")
    $codes = @($r.Json.violations | ForEach-Object { $_.reasonCode } | Sort-Object -Unique)
    $assertions.Add(('B10-05-{0:00}' -f $index++), "$file 被拒，退出码 1，原因码恰好是 $($bad[$file])",
        ($r.Exit -eq 1 -and $r.Json.outcome -eq 'REJECTED' -and ($codes -join ',') -eq $bad[$file] -and $null -eq $r.Json.targetVersion),
        "1 REJECTED $($bad[$file])", "$($r.Exit) $($r.Json.outcome) $($codes -join ',')")
}

$final = & $read '8 read: the refused candidates switched nothing'
$assertions.Add(('B10-05-{0:00}' -f $index), '三份错误候选之后生效版本仍是 4，没有版本 5',
    ($final.Json.activeVersion -eq 4 -and @($final.Json.versions).Count -eq 4), '4 of 4', "$($final.Json.activeVersion) of $(@($final.Json.versions).Count)")
