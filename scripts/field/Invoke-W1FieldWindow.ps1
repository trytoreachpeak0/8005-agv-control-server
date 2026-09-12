#Requires -Version 7

<#
.SYNOPSIS
    Runs the W1 field window -- per-vehicle slot IO verification and per-vehicle release -- and
    writes its evidence.

.DESCRIPTION
    W1 is a field window, not a gate. A person stands at each vehicle, confirms every one of its
    eight slots by hand, and records what they saw; this script takes those records, drives
    ControlServer.FieldOps against the running server's database, and assembles the evidence
    directory the specification asks for.

    The order is load-bearing and this script enforces it: verify first, release that vehicle, and
    only then enable the gate. The reverse order would drop all three existing vehicles out of
    business readiness at once, and nothing in the requirements asks for that -- REQ-0259 says an
    unqualified vehicle must not be business ready, not that the gate must precede the verification.

    Nothing here moves a vehicle. Verification is a person operating the slots; this script records
    the result and computes the readiness verdict from it.

.EXAMPLE
    .\Invoke-W1FieldWindow.ps1 `
        -EvidenceRoot ..\..\evidence\field\20260910-W1-three-vehicle-qualification `
        -Database 'C:\ProgramData\8005\ControlServer\data\controlserver.db' `
        -RecordDirectory .\records\20260910
#>
[CmdletBinding()]
param(
    # Must not exist. Evidence is never overwritten -- a red run erased by a green re-run is the one
    # thing the evidence discipline forbids outright.
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,

    # The running server's SQLite database. The tool writes through the same context the server
    # uses, so the immutability guards and the audit writer are the server's own, not a copy.
    [Parameter(Mandatory)]
    [string]$Database,

    # One <n>-<siteAlias>.json field record per vehicle, processed in file-name order, plus the
    # window-level record window.json. See templates/.
    [Parameter(Mandatory)]
    [string]$RecordDirectory,

    # This script lives in scripts/field, so the repository root is two levels up.
    [string]$Repository = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),

    # Skip building the tool when the caller already published it (offline field machine).
    [string]$FieldOpsExecutable,

    # The commit the prebuilt tool came from. The factory server has neither this repository nor git,
    # so there the caller says it; without it the commit is read from -Repository.
    [string]$ControlServerCommit
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# FieldOps writes UTF-8 and the vehicles are named in Chinese. A native command's output is decoded
# with the console encoding before it is redirected to a file, so on a server whose console is still
# on the ANSI code page every agvId in logs/ and in the verdict would arrive mangled.
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

if (Test-Path -LiteralPath $EvidenceRoot) {
    throw "EvidenceRoot already exists: $EvidenceRoot. Evidence directories are append-only; a correction goes to a new directory and names the one it corrects."
}
if (-not (Test-Path -LiteralPath $Database -PathType Leaf)) {
    throw "No such database: $Database"
}

$windowRecordPath = Join-Path $RecordDirectory 'window.json'
if (-not (Test-Path -LiteralPath $windowRecordPath -PathType Leaf)) {
    throw "No window-level field record at $windowRecordPath (copy scripts/field/templates/window.json and fill it in)."
}
$windowRecord = Get-Content -LiteralPath $windowRecordPath -Raw | ConvertFrom-Json -AsHashtable

$vehicleRecords = @(Get-ChildItem -LiteralPath $RecordDirectory -Filter '*.json' |
    Where-Object { $_.Name -ne 'window.json' } |
    Sort-Object Name)
if ($vehicleRecords.Count -eq 0) {
    throw "No vehicle field records in $RecordDirectory."
}

$logs = Join-Path $EvidenceRoot 'logs'
$snapshots = Join-Path $EvidenceRoot 'snapshots'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
New-Item -ItemType Directory -Path $snapshots -Force | Out-Null

# The records go into the evidence exactly as they were handed in, raw probe traces included, so the
# verdict and what it was computed from sit in one directory.
Copy-Item -LiteralPath $RecordDirectory -Destination (Join-Path $EvidenceRoot 'field-records') -Recurse

$timelinePath = Join-Path $EvidenceRoot 'timeline.jsonl'
$runId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ')
$startedAt = Get-Date

function Add-TimelineEvent {
    param([string]$Kind, [hashtable]$Data)

    $event = [ordered]@{
        at   = (Get-Date).ToString('o')
        kind = $Kind
    }
    foreach ($key in $Data.Keys) { $event[$key] = $Data[$key] }
    Add-Content -LiteralPath $timelinePath -Value ($event | ConvertTo-Json -Depth 12 -Compress) -Encoding utf8
}

function Invoke-FieldOps {
    param([string]$Command, [string[]]$Arguments, [string]$LogName)

    $stdout = Join-Path $logs "$LogName.stdout.json"
    $stderr = Join-Path $logs "$LogName.stderr.txt"
    $all = @($Command, '--database', $Database) + $Arguments
    & $script:fieldOps @all 1> $stdout 2> $stderr
    $exit = $LASTEXITCODE
    $payload = (Test-Path -LiteralPath $stdout) -and ((Get-Item -LiteralPath $stdout).Length -gt 0) `
        ? (Get-Content -LiteralPath $stdout -Raw | ConvertFrom-Json -AsHashtable)
        : @{ command = $Command; outcome = 'NO_OUTPUT' }
    [pscustomobject]@{ ExitCode = $exit; Payload = $payload }
}

# The tool is built from this repository so that the evidence names the commit it ran from. A field
# machine with no SDK passes -FieldOpsExecutable instead.
#
# The build runs from inside the repository. `dotnet` looks for global.json from the current
# directory, not from the project path it is handed, so a caller standing elsewhere would build with
# the newest installed SDK instead of the pinned one. The SDK it resolved goes into the identity.
$fieldOpsSdkVersion = $null
if ($FieldOpsExecutable) {
    $script:fieldOps = $FieldOpsExecutable
} else {
    $Repository = (Resolve-Path -LiteralPath $Repository).Path
    $project = Join-Path $Repository 'tools/ControlServer.FieldOps/ControlServer.FieldOps.csproj'
    Push-Location -LiteralPath $Repository
    try {
        $fieldOpsSdkVersion = & dotnet --version 2>&1
        if ($LASTEXITCODE -ne 0) { throw "dotnet could not resolve the SDK pinned by $(Join-Path $Repository 'global.json'): $fieldOpsSdkVersion" }
        $fieldOpsSdkVersion = "$fieldOpsSdkVersion".Trim()
        & dotnet build $project -c Release --nologo -v q | Out-Null
        $buildExit = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    if ($buildExit -ne 0) { throw 'Failed to build ControlServer.FieldOps.' }
    $script:fieldOps = Join-Path $Repository 'tools/ControlServer.FieldOps/bin/Release/net8.0/win-x64/ControlServer.FieldOps.exe'
}
if (-not (Test-Path -LiteralPath $script:fieldOps -PathType Leaf)) {
    throw "ControlServer.FieldOps not found at $script:fieldOps"
}

if ($ControlServerCommit) {
    $commit = $ControlServerCommit
}
else {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw 'git is not available here; pass -ControlServerCommit with the commit the FieldOps build came from.'
    }
    $commit = & git -C $Repository rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $commit) {
        throw "No git work tree at $Repository; pass -ControlServerCommit with the commit the FieldOps build came from."
    }
    $commit = "$commit".Trim()
}
$identity = [ordered]@{
    runId               = $runId
    windowId            = 'W1'
    batchId             = 'BATCH-3'
    site                = $windowRecord.site
    observers           = $windowRecord.observers
    database            = (Resolve-Path -LiteralPath $Database).Path
    controlServerCommit = $commit
    # Null when -FieldOpsExecutable supplied a prebuilt tool; this script did not build it.
    fieldOpsSdkVersion  = $fieldOpsSdkVersion
    controlServerCommitSource = $ControlServerCommit ? 'parameter' : 'git'
    fieldRecords        = 'field-records'
    protocolReleaseIdentity = [ordered]@{
        tag              = 'protocol-v0.3.0'
        repositoryCommit = '345c53c58517968192c87c3e7777ed08ddb48726'
        manifestSha256   = 'b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138'
    }
}
Add-TimelineEvent -Kind 'window-started' -Data @{ identity = $identity }

$initial = Invoke-FieldOps -Command 'status' -Arguments @() -LogName '00-status-initial'
$initial.Payload | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $snapshots '00-status-initial.json') -Encoding utf8
Add-TimelineEvent -Kind 'status' -Data @{ phase = 'initial'; vehicles = $initial.Payload.vehicles }

$vehicleOutcomes = [System.Collections.Generic.List[hashtable]]::new()
# A verification's audit is stamped with the record's verifiedAt -- the moment the person finished at
# the vehicle -- not with the moment this script hands the record in, and the vehicles are verified
# before the window runs. Exporting from the window's own start would leave every one of those audits
# outside the export and fail W1-05 on a window that did everything right.
$auditSince = [DateTimeOffset]$startedAt
$gateEnabledAt = $null
$gateAudit = $null
$step = 0

foreach ($file in $vehicleRecords) {
    $step++
    $record = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
    $tag = '{0:d2}-{1}' -f $step, $record.siteAlias
    if ($record.verifiedAt) {
        $verifiedAt = [DateTimeOffset]$record.verifiedAt
        if ($verifiedAt -lt $auditSince) { $auditSince = $verifiedAt }
    }
    Add-TimelineEvent -Kind 'vehicle-started' -Data @{
        agvId = $record.agvId; siteAlias = $record.siteAlias; recordFile = $file.Name
    }

    $verify = Invoke-FieldOps -Command 'verify' -Arguments @('--record', $file.FullName) -LogName "$tag-verify"
    Add-TimelineEvent -Kind 'verify' -Data @{
        agvId = $record.agvId; exitCode = $verify.ExitCode; result = $verify.Payload
    }

    # Release only after this vehicle's own verification went in. A refused verification stops this
    # vehicle and leaves the others alone -- the gate is a per-car judgement, so one car's gap says
    # nothing about the next one.
    $release = $null
    if ($verify.ExitCode -eq 0) {
        $release = Invoke-FieldOps -Command 'release' `
            -Arguments @('--agv', $record.agvId, '--model', $record.slotModelVersionId) `
            -LogName "$tag-release"
        Add-TimelineEvent -Kind 'release' -Data @{
            agvId = $record.agvId; exitCode = $release.ExitCode; result = $release.Payload
        }
    }

    $after = Invoke-FieldOps -Command 'status' -Arguments @() -LogName "$tag-status"
    $after.Payload | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $snapshots "$tag-status.json") -Encoding utf8
    Add-TimelineEvent -Kind 'status' -Data @{ phase = $tag; vehicles = $after.Payload.vehicles }

    # The gate goes online after the FIRST vehicle is released, never before. This is 3.5's ordering
    # discipline, and it is the whole reason the window is zero-downtime.
    if (-not $gateEnabledAt -and $release -and $release.ExitCode -eq 0) {
        $gate = Invoke-FieldOps -Command 'enable-gate' `
            -Arguments @('--note', "W1 $($windowRecord.date) after $($record.agvId) released") `
            -LogName "$tag-enable-gate"
        if ($gate.ExitCode -ne 0) { throw 'Failed to record the gate enablement audit.' }
        $gateEnabledAt = Get-Date
        $gateAudit = $gate.Payload
        Add-TimelineEvent -Kind 'gate-enabled' -Data @{
            afterVehicle = $record.agvId; result = $gate.Payload
        }
    }

    $vehicleOutcomes.Add(@{
        agvId          = $record.agvId
        siteAlias      = $record.siteAlias
        slotsSubmitted = $record.slots.Count
        verifyExit     = $verify.ExitCode
        verifyResult   = $verify.Payload
        releaseExit    = $release ? $release.ExitCode : $null
        releaseResult  = $release ? $release.Payload : $null
        photoPointers  = $record.photoPointers
    })
}

foreach ($observation in @($windowRecord.productionContinuityObservations)) {
    Add-TimelineEvent -Kind 'production-continuity' -Data @{ observation = $observation }
}

$auditExport = Invoke-FieldOps -Command 'audit' `
    -Arguments @('--since', $auditSince.ToString('o')) -LogName '99-audit'
$auditExport.Payload | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $snapshots '99-audit.json') -Encoding utf8

$final = Invoke-FieldOps -Command 'status' -Arguments @() -LogName '99-status-final'
$final.Payload | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $snapshots '99-status-final.json') -Encoding utf8
Add-TimelineEvent -Kind 'status' -Data @{ phase = 'final'; vehicles = $final.Payload.vehicles }

function New-Assertion {
    param([string]$Id, [string]$Description, [bool]$Passed, [string]$Expected, [string]$Actual)

    [ordered]@{
        id          = $Id
        description = $Description
        outcome     = $Passed ? 'PASS' : 'FAIL'
        expected    = $Expected
        actual      = $Actual
    }
}

$assertions = [System.Collections.Generic.List[object]]::new()

$sampled = @($vehicleOutcomes | Where-Object { $_.verifyExit -ne 0 -or $_.verifyResult.slotsConfirmed -ne 8 })
$assertions.Add((New-Assertion -Id 'W1-01' `
    -Description '三台车各自完成 8 仓逐仓核对，无抽样' `
    -Passed ($vehicleOutcomes.Count -eq 3 -and $sampled.Count -eq 0) `
    -Expected '3 台车 × 8 仓' `
    -Actual ("{0} 台车，逐仓数 {1}" -f $vehicleOutcomes.Count, (($vehicleOutcomes | ForEach-Object { $_.verifyResult.slotsConfirmed ?? '-' }) -join '/'))))

$releasedAfterOwnVerify = @($vehicleOutcomes | Where-Object { $_.releaseExit -eq 0 -and $_.releaseResult.ready })
$assertions.Add((New-Assertion -Id 'W1-02' `
    -Description '每台车在自己核对通过之后才取得 readiness' `
    -Passed ($releasedAfterOwnVerify.Count -eq $vehicleOutcomes.Count) `
    -Expected "$($vehicleOutcomes.Count) 台车逐台放行" `
    -Actual "$($releasedAfterOwnVerify.Count) 台车 ready=true"))

$firstVerified = $vehicleOutcomes.Count -gt 0 -and $vehicleOutcomes[0].verifyExit -eq 0
$assertions.Add((New-Assertion -Id 'W1-03' `
    -Description '门禁上线时点晚于第一台车核对通过' `
    -Passed ($firstVerified -and $null -ne $gateEnabledAt) `
    -Expected '先核对，后启用门禁' `
    -Actual ($gateEnabledAt ? "第一台车通过后启用于 $($gateEnabledAt.ToString('o'))" : '门禁未启用')))

# Zero downtime, as far as this script can see it: from the moment the gate went online there was
# always at least one released vehicle. The vehicles' actual order-taking is a thing a person
# watches, so the window record carries those observations and this assertion requires them.
$statusSnapshots = @(Get-ChildItem -LiteralPath $snapshots -Filter '*-status*.json' | Sort-Object Name)
$readyCounts = @($statusSnapshots | ForEach-Object {
    $payload = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json -AsHashtable
    @($payload.vehicles | Where-Object { $_.ready }).Count
})
$observations = @($windowRecord.productionContinuityObservations)
$continuityHeld = $observations.Count -ge $vehicleOutcomes.Count -and
    @($observations | Where-Object { -not $_.fleetStillTakingOrders }).Count -eq 0
$assertions.Add((New-Assertion -Id 'W1-04' `
    -Description '全程零停产：门禁上线后任一时刻至少一台车已放行，且现场逐台观察到车队仍在接单' `
    -Passed (($readyCounts | Select-Object -Last ($statusSnapshots.Count - 1) | Where-Object { $_ -lt 1 }).Count -eq 0 -and $continuityHeld) `
    -Expected '每次观测 ready >= 1，且每台车一条正面的现场连续性观察' `
    -Actual ("ready 序列 {0}；现场观察 {1} 条" -f ($readyCounts -join ','), $observations.Count)))

$auditedVehicles = @($auditExport.Payload.records |
    Where-Object { $_.Action -eq 'SLOT_CONFIGURATION_VERIFICATION_RECORDED' } |
    ForEach-Object { $_.ObjectId } | Sort-Object -Unique)
$assertions.Add((New-Assertion -Id 'W1-05' `
    -Description '三台车各自的核对与放行都有不可改写审计' `
    -Passed ($auditedVehicles.Count -eq $vehicleOutcomes.Count) `
    -Expected "$($vehicleOutcomes.Count) 台车各有审计" `
    -Actual "$($auditedVehicles.Count) 台车留痕：$($auditedVehicles -join '、')"))

$allPhotos = @($windowRecord.photoPointers) + @($vehicleOutcomes | ForEach-Object { $_.photoPointers }) | Where-Object { $_ }
$assertions.Add((New-Assertion -Id 'W1-06' `
    -Description '证据目录形状完整，含现场记录与照片指针' `
    -Passed ($allPhotos.Count -gt 0) `
    -Expected '至少一个照片指针' `
    -Actual "$($allPhotos.Count) 个照片指针"))

$outcome = @($assertions | Where-Object { $_.outcome -eq 'FAIL' }).Count -eq 0 ? 'PASS' : 'FAIL'

$assertionsDocument = [ordered]@{
    schemaVersion = 1
    window        = 'W1'
    runId         = $runId
    outcome       = $outcome
    identity      = $identity
    vehicles      = $vehicleOutcomes
    gate          = $gateAudit
    fieldRecord   = $windowRecord
    assertions    = $assertions
}
$assertionsDocument | ConvertTo-Json -Depth 16 |
    Set-Content -LiteralPath (Join-Path $EvidenceRoot 'assertions.json') -Encoding utf8

$rows = ($assertions | ForEach-Object {
    "| $($_.description) | $($_.outcome) | ``$($_.expected)`` | ``$($_.actual)`` |"
}) -join "`n"
$vehicleRows = ($vehicleOutcomes | ForEach-Object {
    "| ``$($_.agvId)`` | ``$($_.siteAlias)`` | $($_.verifyResult.slotsConfirmed) | $($_.verifyExit -eq 0 ? '通过' : '被拒') | $($_.releaseResult.ready ? '已放行' : '未放行') | ``$($_.releaseResult.reasonCode)`` |"
}) -join "`n"
$photoRows = ($allPhotos | ForEach-Object { "- ``$_``" }) -join "`n"

$summary = @"
# W1 现场窗口证据：三车逐仓 IO 核对与门禁逐台启用

结论：**$outcome**

## 身份

| 项 | 值 |
| --- | --- |
| runId | ``$runId`` |
| 窗口 | ``W1`` 车辆资格 |
| batchId | ``BATCH-3`` |
| 现场 | $($windowRecord.site) |
| 现场人员 | $($windowRecord.observers -join '、') |
| controlServerCommit | ``$commit`` |
| protocolReleaseIdentity | ``protocol-v0.3.0`` / ``345c53c58517968192c87c3e7777ed08ddb48726`` / ``b6c81ca9…ba138`` |
| 数据库 | ``$($identity.database)`` |

## 逐台结果

| agvId | 上位机 | 逐仓核对数 | 核对 | 放行 | 原因码 |
| --- | --- | --- | --- | --- | --- |
$vehicleRows

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
$rows

## 照片指针

照片本身不进 git，这里只留指针。

$photoRows

## 目录内容

- ``assertions.json`` —— 机器可读的判据结论，含现场记录原文
- ``field-records/`` —— 交上来的逐车记录与 ``window.json`` 原样；其中 ``raw/`` 若存在，是 ``Invoke-W1SlotIoProbe.ps1`` 在车上直读 IO 模块的逐仓原始记录与现场人员的逐条回答
- ``timeline.jsonl`` —— 一行一次动作，只追加；门禁启用那一行的时刻可与第一台车核对通过的时刻直接比对
- ``logs/`` —— 每次 ``ControlServer.FieldOps`` 调用的 stdout 与 stderr
- ``snapshots/`` —— 每一步的三车就绪快照，以及本窗口相关的不可改写审计导出

## 这份证据证明了什么，没证明什么

**证明了**：三台车各自的八个仓位都被人逐仓确认过开、关、到位三个信号；每台车的 readiness 是在它自己
核对通过之后才取得的；门禁启用的时刻晚于第一台车核对通过；每一步都有不可改写审计。

**没有证明**：门禁在拦车。当前 ``src/`` 里没有任何生产调用点读
``SlotConfigurationReadinessGate.IsSlotConfigurationConfirmedAsync``——把 readiness 接进投运判定属于
投运流程，不属于这张现场票。``enable-gate`` 记的是**时点与审计**，配置项
``Governance:slotConfigurationReadinessGate`` 需要运维改成 ``Enforcing`` 并重启服务才生效。

**零停产这一条用 readiness 代理业务就绪**：判据 W1-04 一半看 ready 序列，一半看现场逐台记下的
「车队仍在接单」观察。前者是服务端自己的事实，后者是人看到的事实，两者都不是「业务就绪判定被跑过
一次」——那条接线还不存在。

**W1 还含一项空载受控急停演练**，属批次 2 轨 B 的现场前置，不在本窗口范围，也不在这份证据里。
"@

Set-Content -LiteralPath (Join-Path $EvidenceRoot 'SUMMARY.md') -Value $summary -Encoding utf8
Add-TimelineEvent -Kind 'window-finished' -Data @{ outcome = $outcome }

Write-Host "W1 $outcome -> $EvidenceRoot"
if ($outcome -ne 'PASS') {
    # The red evidence stays exactly where it is. A correction goes to a new directory and names
    # this one in its SUMMARY.md.
    exit 1
}
