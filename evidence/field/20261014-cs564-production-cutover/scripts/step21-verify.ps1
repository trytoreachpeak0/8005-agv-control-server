#Requires -Version 7
# Step 21: submit one vehicle's W1 record to FieldOps verify, then read status back.
param([Parameter(Mandatory)][string] $Record)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$f  = 'D:\zhengyushao\control-server-v2-ops\fieldops-96a88db0\ControlServer.FieldOps.exe'
$db = 'C:\ProgramData\8005\ControlServer.V2\data\controlserver.db'
"### now $(Get-Date -Format o) record=$Record sha256=$((Get-FileHash $Record).Hash)"
"=== verify"; & $f verify --database $db --record $Record; "exit=$LASTEXITCODE"
"=== status"; & $f status --database $db; "exit=$LASTEXITCODE"
