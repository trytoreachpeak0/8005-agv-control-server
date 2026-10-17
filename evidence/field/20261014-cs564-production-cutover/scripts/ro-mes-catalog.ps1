#Requires -Version 7
# READ-ONLY: one HTTP GET of the production MesIngest externally-readable demand catalog, with the shared
# secret the V2 instance itself uses (env var named in its config; never printed). No POST/PUT, no SQL.
param([string[]] $WorkTypes = @('STAGING_TO_WIRE','WIRE_TO_GATE','WIRE_TO_OPTICAL','WIRE_TO_NITROGEN','DIE_TO_OVEN','DIE_TO_WIRE_STAGING'), [switch] $Raw)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$cfgFiles = 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.json','C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json'
$base = $null; $var = $null
foreach ($f in $cfgFiles) { $j = Get-Content $f -Raw | ConvertFrom-Json -AsHashtable; if ($j.MesIngest) { $base = $j.MesIngest.baseUrl ?? $base; $var = $j.MesIngest.sharedSecretEnvironmentVariable ?? $var } }
$svcEnv = @((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\8005 AGV ControlServer V2').Environment)
$secret = ($svcEnv | Where-Object { $_ -like "$var=*" } | Select-Object -First 1) -replace '^[^=]+=', ''
"### now $(Get-Date -Format o) base=$base var=$var secretPresent=$([bool]$secret)"
$h = @{ Authorization = "Bearer $secret" }
$cat = Invoke-RestMethod "$base/api/v2/externally-readable-demand-catalog" -Headers $h -NoProxy -TimeoutSec 30
if ($Raw) { $cat | ConvertTo-Json -Depth 6 | Select-Object -First 80; return }
$items = @($cat.items)
"### catalogRevision=$($cat.catalogRevision) count=$($cat.count) committed=$($cat.projectionCommittedAt)"
$items | Where-Object { $WorkTypes -contains $_.transportDemandKey.workType } | ForEach-Object {
  "$($_.transportDemandKey.workType)`t$($_.liveMesFields.area)`t$($_.transportDemandKey.sublot)`t$($_.liveMesFields.eqp)`t$($_.createdAt)`t$($_.liveMesFields.package)"
} | Sort-Object
