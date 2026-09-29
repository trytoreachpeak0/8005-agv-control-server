#Requires -Version 7
<#
.SYNOPSIS
    Read-only capture of RIoT's Map list response body, for a replay fixture (control-server#186, second read).

.DESCRIPTION
    Issues exactly one GET and nothing else: /api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson. No vehicle is read, no
    order is created, no command is sent, ControlServer is not involved. Refuses to run when the route to the host goes
    through the Clash TUN adapter. A non-2xx answer is recorded and not retried.

    The body is saved with one change: every Map's "url" value is replaced by a placeholder string, so its JSON type is
    kept and nothing else moves. It carries no station coordinates and no mapJson. The bearer credential is read from an
    environment variable and never written anywhere.

    Outputs into -OutDir, which must not exist:
      route.txt          the route check result
      requests.jsonl     time, method, path, status, bytes, sha256 (of the raw body), code
      map-list.json      the body, url values replaced
      value-types.json   for every key of every Map, the JSON kind of its value
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BaseUrl,
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

$route = Find-NetRoute -RemoteIPAddress $base.Host | Select-Object -First 1
@(
    "checkedAt: $([DateTimeOffset]::Now.ToString('o'))"
    "host: $($base.Host)"
    "InterfaceAlias: $($route.InterfaceAlias)"
    "IPAddress: $($route.IPAddress)"
) | Set-Content -LiteralPath (Join-Path $OutDir 'route.txt')
if ($route.InterfaceAlias -match 'Clash') { throw "Route to $($base.Host) goes through '$($route.InterfaceAlias)'." }

$uri = [Uri]::new($base, '/api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson')
if ($uri.AbsolutePath -ne '/api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson') { throw "Unexpected path '$($uri.AbsolutePath)'." }
$at = [DateTimeOffset]::Now
$response = Invoke-WebRequest -Method Get -Uri $uri -Headers @{ Authorization = "Bearer $apiKey" } `
    -SkipHttpErrorCheck -TimeoutSec 45 -NoProxy
$bytes = $response.RawContentStream.ToArray()
$json = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -AsHashtable -Depth 64
[ordered]@{
    requestedAt  = $at.ToString('o')
    method       = 'GET'
    pathAndQuery = $uri.PathAndQuery
    status       = [int]$response.StatusCode
    bytes        = $bytes.Length
    sha256       = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    code         = "$($json['code'])"
} | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $OutDir 'requests.jsonl')
if ([int]$response.StatusCode -ne 200) { throw "RIoT answered $([int]$response.StatusCode); recorded, not retried." }

function Get-Kind($value) {
    if ($null -eq $value) { return 'null' }
    if ($value -is [string]) { return 'string' }
    if ($value -is [bool]) { return 'boolean' }
    if ($value -is [int] -or $value -is [long] -or $value -is [double] -or $value -is [decimal]) { return 'number' }
    if ($value -is [Collections.IDictionary]) { return 'object' }
    return 'array'
}

$types = [ordered]@{}
foreach ($map in @($json['result'])) {
    foreach ($key in @($map.Keys | Sort-Object)) {
        $kind = Get-Kind $map[$key]
        $types[$key] = @(@($types[$key] ?? @()) + $kind | Sort-Object -Unique)
    }
    if ($map.Contains('url') -and $map['url'] -is [string]) { $map['url'] = 'url-redacted' }
}
$json | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath (Join-Path $OutDir 'map-list.json')
$types | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutDir 'value-types.json')
Get-Content -LiteralPath (Join-Path $OutDir 'value-types.json') -Raw
