#Requires -Version 7
<#
.SYNOPSIS
    Read-only check that RIoT's map list endpoint without mapJson is usable and reports the
    same map name as mapInfo/{mapId} (control-server#186, step one).

.DESCRIPTION
    Issues exactly two GET requests and nothing else, each checked against an allowlist:
      /api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson
      /api/imap/v1/mapInfo/{mapId}
    No vehicle is read, no order is created, no command is sent, ControlServer is not involved.
    For a non-loopback base URL, refuses to run when the route goes through the Clash TUN adapter.

    The bearer credential is read from an environment variable and never written anywhere.
    Raw bodies are not saved (mapInfo/{mapId} carries the whole mapJson); only the fields this
    check needs are extracted, alongside each body's length and SHA-256.

    Outputs into -OutDir, which must not exist:
      route.txt       route check result
      requests.jsonl  one line per request: time, method, path, status, bytes, sha256, code
      fields.json     every list entry's id/name (UTF-8 hex), its key set, whether mapJson is
                      present, and the name comparison for -MapId
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BaseUrl,
    [Parameter(Mandatory)] [int] $MapId,
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $ApiKeyEnvironmentVariable = 'CONTROL_SERVER_RIOT_CALL_API_KEY'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (Test-Path -LiteralPath $OutDir) { throw "OutDir '$OutDir' already exists; evidence directories are never overwritten." }

$apiKey = [Environment]::GetEnvironmentVariable($ApiKeyEnvironmentVariable)
if ([string]::IsNullOrWhiteSpace($apiKey)) { throw "Environment variable $ApiKeyEnvironmentVariable is not set." }

$base = [Uri]$BaseUrl.TrimEnd('/')
New-Item -ItemType Directory -Path $OutDir | Out-Null

$routeLines = [Collections.Generic.List[string]]::new()
$routeLines.Add("checkedAt: $([DateTimeOffset]::Now.ToString('o'))")
$routeLines.Add("host: $($base.Host)")
if ($base.IsLoopback) {
    $routeLines.Add('loopback: true (route check not applicable)')
} else {
    $route = Find-NetRoute -RemoteIPAddress $base.Host | Select-Object -First 1
    $routeLines.Add("InterfaceAlias: $($route.InterfaceAlias)")
    $routeLines.Add("IPAddress: $($route.IPAddress)")
    if ($route.InterfaceAlias -match 'Clash') {
        $routeLines | Set-Content -LiteralPath (Join-Path $OutDir 'route.txt')
        throw "Route to $($base.Host) goes through '$($route.InterfaceAlias)'; results would be fiction."
    }
}
$routeLines | Set-Content -LiteralPath (Join-Path $OutDir 'route.txt')

$allowedPaths = @(
    '^/api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson$',
    "^/api/imap/v1/mapInfo/$MapId$"
)
$requestLog = Join-Path $OutDir 'requests.jsonl'

function Invoke-ReadOnlyGet([string] $path) {
    $uri = [Uri]::new($base, $path)
    if (-not ($allowedPaths | Where-Object { $uri.AbsolutePath -match $_ })) {
        throw "Path '$($uri.AbsolutePath)' is not on the read-only allowlist."
    }
    $at = [DateTimeOffset]::Now
    $response = Invoke-WebRequest -Method Get -Uri $uri -Headers @{ Authorization = "Bearer $apiKey" } `
        -SkipHttpErrorCheck -TimeoutSec 45 -NoProxy
    $bytes = $response.RawContentStream.ToArray()
    $json = $null
    try { $json = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -AsHashtable -Depth 64 } catch { }
    $envelope = $json -is [Collections.IDictionary]
    [ordered]@{
        requestedAt  = $at.ToString('o')
        method       = 'GET'
        pathAndQuery = $uri.PathAndQuery
        status       = [int]$response.StatusCode
        bytes        = $bytes.Length
        sha256       = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        code         = $envelope -and $json.ContainsKey('code') ? "$($json.code)" : $null
        message      = $envelope -and $json.ContainsKey('message') ? "$($json.message)" : $null
    } | ConvertTo-Json -Compress | Add-Content -LiteralPath $requestLog
    [pscustomobject]@{ Status = [int]$response.StatusCode; Bytes = $bytes.Length; Json = $json }
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

# 1. The list endpoint without mapJson.
$listReply = Invoke-ReadOnlyGet '/api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson'
$list = Get-Result $listReply
$entries = @($list | Where-Object { $_ -is [Collections.IDictionary] })
$listFields = [ordered]@{
    status       = $listReply.Status
    bytes        = $listReply.Bytes
    obtained     = $null -ne $list
    resultShape  = $null -eq $list ? $null : $list.GetType().Name
    count        = $entries.Count
    anyMapJson   = [bool]($entries | Where-Object { $_.ContainsKey('mapJson') -and $null -ne $_['mapJson'] })
    keyUnion     = @($entries | ForEach-Object { $_.Keys } | Sort-Object -Unique)
    entries      = @($entries | ForEach-Object {
        [ordered]@{ id = $_['id']; name = Get-Literal $_['name']; gmtUpdate = $_['gmtUpdate']; state = $_['state'] }
    })
}
$listEntry = @($entries | Where-Object { "$($_['id'])" -eq "$MapId" })

# 2. The single-map endpoint, for the comparison only.
$mapReply = Invoke-ReadOnlyGet "/api/imap/v1/mapInfo/$MapId"
$map = Get-Result $mapReply
$hasMap = $map -is [Collections.IDictionary]
$mapFields = [ordered]@{
    status     = $mapReply.Status
    bytes      = $mapReply.Bytes
    obtained   = $hasMap
    id         = $hasMap ? $map['id'] : $null
    name       = Get-Literal ($hasMap ? $map['name'] : $null)
    gmtUpdate  = $hasMap ? $map['gmtUpdate'] : $null
    hasMapJson = $hasMap -and $map.ContainsKey('mapJson') -and $null -ne $map['mapJson']
}

$listName = Get-Literal ($listEntry.Count -eq 1 ? $listEntry[0]['name'] : $null)
$verdict = (-not $listName.present -or -not $mapFields.name.present) ? 'not-comparable' :
    ([string]::Equals($listName.text, $mapFields.name.text, [StringComparison]::Ordinal) ? 'same' : 'different')

[ordered]@{
    checkedAt = [DateTimeOffset]::Now.ToString('o')
    baseUrl   = $base.ToString()
    mapId     = $MapId
    list      = $listFields
    listEntryCountForMapId = $listEntry.Count
    listNameForMapId = $listName
    mapInfo   = $mapFields
    verdicts  = [ordered]@{ listNameVsMapInfoName = $verdict }
} | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $OutDir 'fields.json')

Get-Content -LiteralPath (Join-Path $OutDir 'fields.json') -Raw
