#Requires -Version 7
# READ-ONLY after the s3 switch: FieldOps read verbs with the three-car fleet, and the read-only watch.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$f  = 'D:\zhengyushao\control-server-v2-ops\fieldops-96a88db0\ControlServer.FieldOps.exe'
$db = 'C:\ProgramData\8005\ControlServer.V2\data\controlserver.db'
$fleet = 'BROKERX-0c20ff0600d644869a6a80c186065d85;BROKERX-f38975561adf46ccb1d2f23833c7d0e4;BROKERX-7daca4ee91da498d8026c68b7b941127'
"=== read-waiting-points (three-car fleet)"; & $f read-waiting-points --database $db --map 26 --fleet $fleet; "exit=$LASTEXITCODE"
"=== charging-policy (three-car fleet)"; & $f charging-policy --database $db --fleet $fleet; "exit=$LASTEXITCODE"
"=== charger-roster"; & $f charger-roster --database $db; "exit=$LASTEXITCODE"
"=== task-type-stations"; & $f task-type-stations --database $db --map 26; "exit=$LASTEXITCODE"
"=== status"; & $f status --database $db; "exit=$LASTEXITCODE"
