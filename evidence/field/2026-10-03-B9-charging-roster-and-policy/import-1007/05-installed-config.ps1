$c = Get-Content -LiteralPath 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json' -Raw | ConvertFrom-Json -AsHashtable
'ConnectionStrings:ControlServer = ' + $c['ConnectionStrings']['ControlServer']
'JourneyRuntime.enabled=' + $c['JourneyRuntime']['enabled'] + ' vehicleKey=' + $c['JourneyRuntime']['vehicleKey'] + ' dispatchZone=' + $c['JourneyRuntime']['dispatchZone'] + ' admission=' + $c['JourneyRuntime']['admissionPolicyDeploymentId']
'RouteGraph.enabled=' + $c['RouteGraph']['enabled'] + ' RiotCreateDispatch.enabled=' + $c['RiotCreateDispatch']['enabled'] + ' RiotForeignOrderCancel.enabled=' + $c['RiotForeignOrderCancel']['enabled']
'VehicleFaultRecovery.enabled=' + $c['VehicleFaultRecovery']['enabled'] + ' FieldOperatorRoles.path=' + $c['FieldOperatorRoles']['path']
'MesIngest.baseUrl=' + $c['MesIngest']['baseUrl']
'OnboardTransport=' + ($c['OnboardTransport'] | ConvertTo-Json -Compress -Depth 5)
'Health=' + ($c['Health'] | ConvertTo-Json -Compress -Depth 5)
'minimumBatteryPercent occurrences in installed config: ' + ([regex]::Matches((Get-Content -LiteralPath 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json' -Raw), '(?i)minimumBatteryPercent').Count) + ' / package appsettings.json: ' + ([regex]::Matches((Get-Content -LiteralPath 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.json' -Raw), '(?i)minimumBatteryPercent').Count)
'db: ' + ((Get-ChildItem -LiteralPath 'C:\ProgramData\8005\ControlServer.V2\data' | ForEach-Object { $_.Name + ' ' + $_.Length }) -join ', ')
