#Requires -Version 7
# READ-ONLY: installed definition summary and runtime keys of the V2 config.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$i = Get-Content 'D:\zhengyushao\control-server-v2-ops\installed-instance.json' -Raw | ConvertFrom-Json -AsHashtable
"installed name=$($i.name) phase=$($i.productionCutover?.phase) fleet=$(($i.fleet ?? @()) | ForEach-Object { $_.sshAlias ?? $_.agvId } | Join-String -Separator ',')"
$c = Get-Content 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json' -Raw | ConvertFrom-Json -AsHashtable
"JourneyRuntime.enabled=$($c.JourneyRuntime.enabled) RiotCreateDispatch.enabled=$($c.RiotCreateDispatch.enabled)"
"fieldOperatorRoles=$($c | ConvertTo-Json -Depth 20 | Select-String -Pattern 'fieldOperatorRoles[^\n]*' -AllMatches | ForEach-Object { $_.Matches.Value })"
Get-ChildItem 'C:\ProgramData\8005\ControlServer.V2' | ForEach-Object { "dir $($_.Name) $($_.LastWriteTime.ToString('o'))" }
Get-ChildItem 'C:\ProgramData\8005' -Directory | ForEach-Object { "pd $($_.Name)" }
