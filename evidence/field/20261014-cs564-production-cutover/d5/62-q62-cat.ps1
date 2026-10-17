$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$cfgFiles = 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.json','C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json'
$base = $null; $var = $null
foreach ($f in $cfgFiles) { $j = Get-Content $f -Raw | ConvertFrom-Json -AsHashtable; if ($j.MesIngest) { $base = $j.MesIngest.baseUrl ?? $base; $var = $j.MesIngest.sharedSecretEnvironmentVariable ?? $var } }
$svcEnv = @((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\8005 AGV ControlServer V2').Environment)
$secret = ($svcEnv | Where-Object { $_ -like "$var=*" } | Select-Object -First 1) -replace '^[^=]+=', ''
$cat = Invoke-RestMethod "$base/api/v2/externally-readable-demand-catalog" -Headers @{ Authorization = "Bearer $secret" } -NoProxy -TimeoutSec 30
"### now $(Get-Date -Format o) catalogRevision=$($cat.catalogRevision) count=$($cat.count) items=$(@($cat.items).Count) committed=$($cat.projectionCommittedAt)"
"### top-level keys: " + (($cat.PSObject.Properties.Name) -join ',')
"### match exact Q26101962-10:"
@($cat.items) | Where-Object { $_.transportDemandKey.sublot -eq 'Q26101962-10' } | ConvertTo-Json -Depth 6
"### match prefix Q26101962:"
@($cat.items) | Where-Object { $_.transportDemandKey.sublot -like 'Q26101962*' } | ForEach-Object { "$($_.transportDemandKey.workType)`t$($_.liveMesFields.area)`t$($_.transportDemandKey.sublot)`t$($_.liveMesFields.eqp)`t$($_.liveMesFields.step)`t$($_.createdAt)" }
"### workType counts:"
@($cat.items) | Group-Object { $_.transportDemandKey.workType } | ForEach-Object { "$($_.Name)`t$($_.Count)" }
