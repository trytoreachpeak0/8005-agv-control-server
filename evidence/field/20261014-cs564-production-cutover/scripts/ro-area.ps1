#Requires -Version 7
# READ-ONLY: current area-assignment table summary via the FieldOps read verb (no import).
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$r = & 'D:\zhengyushao\control-server-v2-ops\fieldops-96a88db0\ControlServer.FieldOps.exe' area-assignments --database 'C:\ProgramData\8005\ControlServer.V2\data\controlserver.db' | ConvertFrom-Json
"version=$($r.version) entryCount=$($r.entryCount) sha=$($r.contentSha256) importedAt=$($r.importedAt)"
$r.entries | Group-Object dispatchZone | ForEach-Object { "$($_.Name)=$($_.Count)" }
