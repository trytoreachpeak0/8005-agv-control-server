#Requires -Version 7
# READ-ONLY: installed V2 config waits, redispatch limit, switches, services.
$c = Get-Content -Raw 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json' | ConvertFrom-Json -AsHashtable
$j = $c.JourneyRuntime
"stationDepartureWaitTimeout=$($j.stationDepartureWaitTimeout)"
"cargoHoldingTimeout=$($j.cargoHoldingTimeout)"
"stationTimeoutRedispatchLimit=$($j.stationTimeoutRedispatchLimit ?? '(absent: package default)')"
"JourneyRuntime.enabled=$($j.enabled) RiotCreateDispatch.enabled=$($c.RiotCreateDispatch.enabled)"
"fleet=$(($j.fleet | ForEach-Object { $_.vehicleKey }) -join ',')"
Get-Service '8005 AGV ControlServer V2', '8005 AGV ControlServer' | ForEach-Object { "$($_.Name) $($_.Status)/$($_.StartType)" }
