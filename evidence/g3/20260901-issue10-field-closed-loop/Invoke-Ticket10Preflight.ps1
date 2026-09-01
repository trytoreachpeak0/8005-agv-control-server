#requires -Version 7
<#
票 10 现场闭环的开跑前预检：在不动车、不建单、不改任何状态的前提下，
按 JourneyRuntimeEngine 的静态闸门顺序回答一个问题——
「此刻 MES 里有没有一条 WIRE_TO_GATE 需求能走到受理？」

只做 GET。不写库、不建单、不碰生产服务。
#>
$ErrorActionPreference = 'Stop'

$mesBase = 'http://127.0.0.1:5088'
$riotBase = 'http://172.19.206.222:8888'
$mapId = 25

function Test-AreaCode { param([string]$Token) $Token -match '^[A-Z][A-Z0-9]*-[0-9]+$' }

function Get-StationAreaCodes {
    param([string]$StationName)
    $tokens = $StationName -split '_'
    if ($tokens.Count -lt 1 -or $tokens.Count -gt 3) { return @() }
    if (($tokens | Sort-Object -Unique).Count -ne $tokens.Count) { return @() }
    foreach ($t in $tokens) { if (-not (Test-AreaCode $t)) { return @() } }
    return $tokens
}

# --- 1. MES 需求目录 ---
$catalogResponse = Invoke-WebRequest -Method Get -Uri "$mesBase/api/v2/externally-readable-demand-catalog" -NoProxy
$catalog = $catalogResponse.Content | ConvertFrom-Json
Write-Output ('MES catalogRevision={0} historyEpoch={1} count={2}' -f $catalog.catalogRevision, $catalog.historyEpoch, $catalog.count)

$w2g = @($catalog.items | Where-Object { $_.transportDemandKey.workType -eq 'WIRE_TO_GATE' })
Write-Output ('WIRE_TO_GATE 需求: {0} 条' -f $w2g.Count)

# --- 2. RIoT 地图站点（只读，票 20260827-map25-readiness 已批准的 GET）---
$apiKey = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', 'Machine')
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    $apiKey = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', 'User')
}
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    $apiKey = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY')
}
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    throw 'CONTROL_SERVER_RIOT_CALL_API_KEY 不可读，无法查地图站点。'
}

$stationResponse = Invoke-WebRequest -Method Get -Uri "$riotBase/api/imap/v1/mapInfo/stations/$mapId" `
    -Headers @{ 'Authorization' = "Bearer $apiKey" } -NoProxy
$stationBody = $stationResponse.Content | ConvertFrom-Json
if ($stationBody.code -ne 0) { throw ('RIoT 地图查询失败: code={0} message={1}' -f $stationBody.code, $stationBody.message) }
$stations = @($stationBody.result)
Write-Output ('RIoT map {0} 站点: {1} 个（body 往返成功，非 ping／connect）' -f $mapId, $stations.Count)

$areaIndex = @{}
foreach ($station in $stations) {
    $name = $station.name
    if ([string]::IsNullOrWhiteSpace($name)) { continue }
    foreach ($area in (Get-StationAreaCodes $name)) {
        if (-not $areaIndex.ContainsKey($area)) { $areaIndex[$area] = [System.Collections.Generic.List[object]]::new() }
        $areaIndex[$area].Add($station)
    }
}
Write-Output ('可解析出 AREA 的站点覆盖 {0} 个 AREA' -f $areaIndex.Count)

# --- 3. 包装容量规则（与 PackageCapacitySeed 一致）---
$exactCapacity = @{
    'PDFN5×6-8L(12R)' = 4; 'PDFNWB3.3×3.34' = 4; 'PDFNWB5×6' = 4
    'TO-126' = 8; 'TO-220-2L-C' = 8; 'TO-220-3L' = 8; 'TO-220-3L-C(T0.5mm)' = 8
    'TO-220-5L' = 8; 'TO-220D-5L' = 8; 'TO-220F' = 8; 'TO-247' = 6; 'TO-247-2L-A' = 6
    'TO-247-4L' = 6; 'TO-247A-4L' = 6; 'TO-247B-3L' = 6; 'TO-247Plus-4L' = 6
    'TO-251' = 10; 'TO-252-2L(4R)' = 5; 'TO-252-2L(6R)' = 4; 'TO-252-2L(8R)' = 4
    'TO-252-5L' = 10; 'TO-263-2L' = 8; 'TO-263-5L' = 8; 'TO-263-7L(2R)' = 6
    'TO-263C-2L' = 8; 'TO-264-3L' = 8; 'TO-92' = 12
}
function Resolve-PackageCapacity {
    param([string]$Package)
    if ([string]::IsNullOrWhiteSpace($Package)) { return $null }
    if ($exactCapacity.ContainsKey($Package)) { return $exactCapacity[$Package] }
    if ($Package.StartsWith('TOLL-')) { return 3 }
    return $null
}

# --- 4. 逐条按引擎顺序判定 ---
$rows = [System.Collections.Generic.List[object]]::new()
foreach ($item in $w2g) {
    $live = $item.liveMesFields
    $area = $live.area
    $eqp = $live.eqp
    $package = $live.package
    $sublot = $item.transportDemandKey.sublot
    $reason = 'ELIGIBLE_STATIC'
    $station = $null
    $capacity = $null
    $boxCount = $null

    if ([string]::IsNullOrWhiteSpace($area) -or [string]::IsNullOrWhiteSpace($eqp) -or [string]::IsNullOrWhiteSpace($package)) {
        $reason = 'REQUIRED_MES_FACT_MISSING'
    }
    elseif (-not $area.StartsWith('N')) {
        $reason = 'OUT_OF_SCOPE_AREA'
    }
    else {
        $areaEqps = @($w2g |
            Where-Object { $_.liveMesFields.area -eq $area -and -not [string]::IsNullOrWhiteSpace($_.liveMesFields.eqp) } |
            ForEach-Object { $_.liveMesFields.eqp } |
            Sort-Object -Unique)
        if ($areaEqps.Count -ne 1 -or $areaEqps[0] -ne $eqp) {
            $reason = 'AREA_EQP_NOT_UNIQUE'
        }
        elseif (-not $areaIndex.ContainsKey($area)) {
            $reason = 'AREA_STATION_NOT_FOUND'
        }
        elseif ($areaIndex[$area].Count -ne 1) {
            $reason = 'AREA_STATION_NOT_UNIQUE'
        }
        else {
            $station = $areaIndex[$area][0]
            $capacity = Resolve-PackageCapacity $package
            if ($null -eq $capacity -or $capacity -le 0) {
                $reason = 'PACKAGE_CAPACITY_NOT_UNIQUE'
            }
            else {
                try {
                    $boxResponse = Invoke-WebRequest -NoProxy `
                        -Uri ("{0}/api/v2/sublot-box-count?sublot={1}" -f $mesBase, [uri]::EscapeDataString($sublot))
                    $boxBody = $boxResponse.Content | ConvertFrom-Json
                    $boxCount = $boxBody.maxBoxCount
                    if ($null -eq $boxCount -or $boxCount -le 0) { $reason = 'SUBLOT_BOX_COUNT_UNAVAILABLE' }
                }
                catch {
                    $reason = 'SUBLOT_BOX_COUNT_UNAVAILABLE'
                }
            }
        }
    }

    $stationLabel = ''
    if ($station) { $stationLabel = '{0}/{1}' -f $station.name, $station.id }
    $baskets = $null
    if ($capacity -and $boxCount) { $baskets = [math]::Ceiling($boxCount / $capacity) }

    $rows.Add([pscustomobject]@{
        DemandId = $item.demandId
        Area = $area
        Eqp = $eqp
        Sublot = $sublot
        Package = $package
        Reason = $reason
        Station = $stationLabel
        Capacity = $capacity
        MaxBoxCount = $boxCount
        Baskets = $baskets
    })
}

Write-Output ''
Write-Output '=== 静态闸门逐条判定（动态事实：电量／车辆／会话就绪未含，需现场）==='
$rows | Sort-Object Reason, Area | Format-Table -AutoSize | Out-String -Width 200 | Write-Output

Write-Output '=== 判定分布 ==='
$rows | Group-Object Reason | Sort-Object Count -Descending | ForEach-Object { '{0,5}  {1}' -f $_.Count, $_.Name }

$ready = @($rows | Where-Object { $_.Reason -eq 'ELIGIBLE_STATIC' })
Write-Output ''
Write-Output ('=== 静态闸门全通过: {0} 条 ===' -f $ready.Count)
foreach ($row in $ready) {
    Write-Output ('  {0} area={1} station={2} package={3} 容量={4} 箱数={5} 预计筐数={6}' -f `
        $row.DemandId, $row.Area, $row.Station, $row.Package, $row.Capacity, $row.MaxBoxCount, $row.Baskets)
}
