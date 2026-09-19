#Requires -Version 7
<#
.SYNOPSIS
    Read-only check of a RIoT map's name against vehicle CurrentMap and the configured
    JourneyRuntime:mapIdentity (control-server#179).

.DESCRIPTION
    Issues GET requests only, and only to an allowlist of read endpoints:
      /api/imap/v1/mapInfo/{mapId}
      /api/imap/v1/mapInfo/stations/{mapId}
      /api/task/vehicles/getVehicleInfoByDeviceKey?key={deviceKey}
    Refuses agv01 (Ask-first category 2) by deviceKey. For a non-loopback base URL, refuses to
    run when the route to the host goes through the Clash TUN adapter.

    The bearer credential is read from an environment variable and is never written anywhere.
    Raw response bodies are not saved: stations carry coordinates, so only the fields this check
    needs are extracted, alongside each body's length and SHA-256.

    Outputs into -OutDir, which must not exist:
      route.txt       the route check result
      requests.jsonl  one line per request: time, method, path, status, bytes, sha256, code
      fields.json     extracted literals with their UTF-8 hex, and the comparison verdicts
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BaseUrl,
    [Parameter(Mandatory)] [int] $MapId,
    [Parameter(Mandatory)] [string[]] $DeviceKeys,
    [Parameter(Mandatory)] [string] $AppSettingsPath,
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $GateStationName = '关卡',
    [string] $ApiKeyEnvironmentVariable = 'CONTROL_SERVER_RIOT_CALL_API_KEY'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# pwsh -File hands a comma list over as one string; split it before any check sees it.
$DeviceKeys = @($DeviceKeys | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

# agv01 is production (Ask-first category 2); never read it from this script.
$forbiddenDeviceKeys = @('BROKERX-0c20ff0600d644869a6a80c186065d85')
foreach ($key in $DeviceKeys) {
    if ($forbiddenDeviceKeys -contains $key) { throw "Refusing to read forbidden vehicle '$key' (agv01)." }
}

if (Test-Path -LiteralPath $OutDir) { throw "OutDir '$OutDir' already exists; evidence directories are never overwritten." }

$apiKey = [Environment]::GetEnvironmentVariable($ApiKeyEnvironmentVariable)
if ([string]::IsNullOrWhiteSpace($apiKey)) { throw "Environment variable $ApiKeyEnvironmentVariable is not set." }

$base = [Uri]$BaseUrl.TrimEnd('/')
New-Item -ItemType Directory -Path $OutDir | Out-Null

# Route check: a Clash TUN adapter answers locally and makes every probe fiction.
$isLoopback = $base.IsLoopback
$routeLines = [Collections.Generic.List[string]]::new()
$routeLines.Add("checkedAt: $([DateTimeOffset]::Now.ToString('o'))")
$routeLines.Add("host: $($base.Host)")
if ($isLoopback) {
    $routeLines.Add('loopback: true (route check not applicable)')
} else {
    $route = Find-NetRoute -RemoteIPAddress $base.Host | Select-Object -First 1
    $routeLines.Add("InterfaceAlias: $($route.InterfaceAlias)")
    $routeLines.Add("InterfaceIndex: $($route.InterfaceIndex)")
    $routeLines.Add("IPAddress: $($route.IPAddress)")
    if ($route.InterfaceAlias -match 'Clash') {
        $routeLines | Set-Content -LiteralPath (Join-Path $OutDir 'route.txt')
        throw "Route to $($base.Host) goes through '$($route.InterfaceAlias)'; results would be fiction. Install the bypass route first."
    }
}
$routeLines | Set-Content -LiteralPath (Join-Path $OutDir 'route.txt')

$allowedPaths = @(
    "^/api/imap/v1/mapInfo/$MapId$",
    "^/api/imap/v1/mapInfo/stations/$MapId$",
    '^/api/task/vehicles/getVehicleInfoByDeviceKey$'
)
$requestLog = Join-Path $OutDir 'requests.jsonl'

function Invoke-ReadOnlyGet([string] $pathAndQuery) {
    $uri = [Uri]::new($base, $pathAndQuery)
    if (-not ($allowedPaths | Where-Object { $uri.AbsolutePath -match $_ })) {
        throw "Path '$($uri.AbsolutePath)' is not on the read-only allowlist."
    }
    $at = [DateTimeOffset]::Now
    $response = Invoke-WebRequest -Method Get -Uri $uri -Headers @{ Authorization = "Bearer $apiKey" } `
        -SkipHttpErrorCheck -TimeoutSec 45 -NoProxy
    $bytes = $response.RawContentStream.ToArray()
    $body = [Text.Encoding]::UTF8.GetString($bytes)
    $json = $null
    try { $json = $body | ConvertFrom-Json -AsHashtable -Depth 64 } catch { }
    $envelope = $json -is [Collections.IDictionary]
    [ordered]@{
        requestedAt = $at.ToString('o')
        method      = 'GET'
        pathAndQuery = $uri.PathAndQuery
        status      = [int]$response.StatusCode
        bytes       = $bytes.Length
        sha256      = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        code        = $envelope -and $json.ContainsKey('code') ? "$($json.code)" : $null
        message     = $envelope -and $json.ContainsKey('message') ? "$($json.message)" : $null
    } | ConvertTo-Json -Compress | Add-Content -LiteralPath $requestLog
    [pscustomobject]@{ Status = [int]$response.StatusCode; Json = $json }
}

function Get-Literal($value) {
    if ($null -eq $value) { return [ordered]@{ present = $false; text = $null; utf8Hex = $null } }
    $text = [string]$value
    [ordered]@{ present = $true; text = $text; utf8Hex = [Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes($text)) }
}

function Get-Result($reply) {
    $json = $reply.Json
    if ($reply.Status -ne 200 -or $json -isnot [Collections.IDictionary] -or "$($json['code'])" -ne '0') { return $null }
    $json['result']
}

# 1. Map object name.
$mapReply = Invoke-ReadOnlyGet "/api/imap/v1/mapInfo/$MapId"
$map = Get-Result $mapReply
$mapFields = [ordered]@{
    status = $mapReply.Status
    obtained = $map -is [Collections.IDictionary]
    id = $map -is [Collections.IDictionary] ? $map['id'] : $null
    name = Get-Literal ($map -is [Collections.IDictionary] ? $map['name'] : $null)
    gmtUpdate = $map -is [Collections.IDictionary] ? $map['gmtUpdate'] : $null
    state = $map -is [Collections.IDictionary] ? $map['state'] : $null
    syncState = $map -is [Collections.IDictionary] ? $map['syncState'] : $null
    resultKeys = $map -is [Collections.IDictionary] ? @($map.Keys | Sort-Object) : @()
}

# 2. Station catalogue: count, and the gate station's id.
$stationReply = Invoke-ReadOnlyGet "/api/imap/v1/mapInfo/stations/$MapId"
$stations = Get-Result $stationReply
$stationList = @($stations | Where-Object { $_ -is [Collections.IDictionary] })
$gateMatches = @($stationList | Where-Object { [string]::Equals([string]$_['name'], $GateStationName, [StringComparison]::Ordinal) })
$stationFields = [ordered]@{
    status = $stationReply.Status
    obtained = $null -ne $stations
    count = $stationList.Count
    gateStationName = Get-Literal $GateStationName
    gateStationIds = @($gateMatches | ForEach-Object { $_['id'] })
}

# 3. Vehicle cards.
$vehicleFields = foreach ($key in $DeviceKeys) {
    $reply = Invoke-ReadOnlyGet "/api/task/vehicles/getVehicleInfoByDeviceKey?key=$([Uri]::EscapeDataString($key))"
    $card = Get-Result $reply
    $has = $card -is [Collections.IDictionary]
    [ordered]@{
        deviceKey = $key
        status = $reply.Status
        obtained = $has
        connected = $has ? ($card['status'] -eq 1) : $null
        vehicleStatus = $has ? $card['status'] : $null
        enable = $has ? $card['enable'] : $null
        procState = $has ? $card['procState'] : $null
        currentPosition = $has ? $card['currentPosition'] : $null
        currentMap = Get-Literal ($has ? $card['currentMap'] : $null)
    }
}

# 4. Configured mapIdentity (read from the file, not hard-coded).
$settings = Get-Content -LiteralPath $AppSettingsPath -Raw | ConvertFrom-Json -AsHashtable
$runtime = $settings['JourneyRuntime']
$configFields = [ordered]@{
    appSettingsPath = $AppSettingsPath
    mapId = $runtime['mapId']
    mapIdentity = Get-Literal $runtime['mapIdentity']
    gateStationId = $runtime['gateStationId']
    gateStationRiotId = $runtime['gateStationRiotId']
}

function Compare-Literal($a, $b) {
    if (-not $a.present -or -not $b.present) { return 'not-comparable' }
    [string]::Equals($a.text, $b.text, [StringComparison]::Ordinal) ? 'same' : 'different'
}

$verdicts = [ordered]@{
    mapNameVsMapIdentity = Compare-Literal $mapFields.name $configFields.mapIdentity
    vehicles = @($vehicleFields | ForEach-Object {
        [ordered]@{
            deviceKey = $_.deviceKey
            currentMapVsMapName = Compare-Literal $_.currentMap $mapFields.name
            currentMapVsMapIdentity = Compare-Literal $_.currentMap $configFields.mapIdentity
        }
    })
}

[ordered]@{
    checkedAt = [DateTimeOffset]::Now.ToString('o')
    baseUrl = $base.ToString()
    mapId = $MapId
    map = $mapFields
    stations = $stationFields
    vehicles = @($vehicleFields)
    config = $configFields
    verdicts = $verdicts
} | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $OutDir 'fields.json')

Get-Content -LiteralPath (Join-Path $OutDir 'fields.json') -Raw
