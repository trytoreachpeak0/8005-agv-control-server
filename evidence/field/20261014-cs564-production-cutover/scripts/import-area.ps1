#Requires -Version 7
# Production: dry-run, and with -Commit the real import, of one area-assignment CSV already copied to the server.
# Import replaces the whole table. The header-only file stops intake (step 16b); demands already accepted keep the version
# they were frozen on.
param([Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][string] $ExpectedSha256, [switch] $Commit)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$exe = 'D:\zhengyushao\control-server-v2-ops\fieldops-96a88db0\ControlServer.FieldOps.exe'
$db  = 'C:\ProgramData\8005\ControlServer.V2\data\controlserver.db'
$csv = "D:\zhengyushao\control-server-v2-ops\prod-20261014\inputs\$Name"
$h = (Get-FileHash $csv).Hash
"### now $(Get-Date -Format o) sha256 $h"
if ($h -ne $ExpectedSha256) { throw "sha256 mismatch: $h" }
Get-Content $csv
"### dry-run"; & $exe import-area-assignments --database $db --input $csv --dry-run; "dry exit=$LASTEXITCODE"
if ($LASTEXITCODE -ne 0) { throw 'dry-run failed' }
if ($Commit) {
  "### import at $(Get-Date -Format o)"; & $exe import-area-assignments --database $db --input $csv; "import exit=$LASTEXITCODE"
  if ($LASTEXITCODE -ne 0) { throw 'import failed' }
  "### read back"; & $exe area-assignments --database $db
}
