#Requires -Version 7
<#
.SYNOPSIS
Reads one Map's station catalog from RIoT, read-only, and writes it in the shape FieldOps --catalog takes.

.DESCRIPTION
Exactly one GET: /api/imap/v1/mapInfo/stations/{MapId}, authorised with the same CallApiKey the server uses
(CONTROL_SERVER_RIOT_CALL_API_KEY). No retry, no other request, nothing is written to RIoT. A non-2xx answer or a business
code other than 0 is reported and nothing is written.

The output is {"mapId": N, "stations": [{"stationId": .., "stationName": ..}]} -- what import-waiting-points and the batch 6
task type station verbs read with --catalog. The raw answer is kept next to it (*.raw.json): it carries the coordinates
(pos.x, pos.y -- the live field names; the OpenAPI document says posX) the server never reads, which is where "a waiting point shares no physical position with a business or
charging station" (REQ-0289) is checked by a person.

Before trusting the answer, check the route to RIoT does not go through the Clash TUN adapter (workspace CLAUDE.md):
the script refuses when it does.

.EXAMPLE
pwsh -File scripts/field/Export-RiotStationCatalog.ps1 -MapId 26 -OutFile C:/8005/catalog-26.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$MapId,
    [Parameter(Mandatory)][string]$OutFile,
    [string]$BaseUrl = 'http://172.19.206.222:8888',
    [string]$ApiKeyVariable = 'CONTROL_SERVER_RIOT_CALL_API_KEY'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$key = [Environment]::GetEnvironmentVariable($ApiKeyVariable)
if ([string]::IsNullOrWhiteSpace($key)) { throw "$ApiKeyVariable is not set." }
$uri = [Uri]"$($BaseUrl.TrimEnd('/'))/api/imap/v1/mapInfo/stations/$MapId"
$address = [System.Net.Dns]::GetHostAddresses($uri.Host) | Select-Object -First 1
$route = (Find-NetRoute -RemoteIPAddress $address.IPAddressToString | Select-Object -First 1).InterfaceAlias
if ($route -like '*Clash*') { throw "The route to $($uri.Host) goes through $route, which answers for any host; refusing." }

$at = [DateTimeOffset]::Now
$response = Invoke-WebRequest -Method Get -Uri $uri -Headers @{ Authorization = "Bearer $key" } `
    -TimeoutSec 30 -MaximumRetryCount 0 -SkipHttpErrorCheck -NoProxy
if ([int]$response.StatusCode -lt 200 -or [int]$response.StatusCode -ge 300) {
    throw "GET $uri answered $([int]$response.StatusCode) at $($at.ToString('o')); nothing written, not retried."
}
$body = $response.Content | ConvertFrom-Json
# The success codes riot-sdk's RiotBusinessResponse.IsSuccessCode accepts; an absent code is success there too.
$code = ([string]$body.code).Trim()
if ($code.Length -gt 0 -and $code -cnotin @('0', '200', 'OK', 'ok', 'success', 'SUCCESS')) {
    throw "GET $uri answered business code '$($body.code)' ($($body.message)) at $($at.ToString('o')); nothing written."
}

$raw = [IO.Path]::ChangeExtension($OutFile, '.raw.json')
[IO.File]::WriteAllText($raw, $response.Content, [Text.UTF8Encoding]::new($false))
[ordered]@{
    mapId    = $MapId
    stations = @($body.result | Sort-Object { [int]$_.id } | ForEach-Object {
            [ordered]@{ stationId = [int]$_.id; stationName = [string]$_.name }
        })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutFile -Encoding utf8NoBOM
"Map $MapId`: $(@($body.result).Count) stations read at $($at.ToString('o')) via $route; wrote $OutFile and $raw."
