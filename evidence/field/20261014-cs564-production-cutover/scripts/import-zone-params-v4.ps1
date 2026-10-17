#Requires -Version 7
# Production step 21/22: REQ-0198 en-route addition 65000 mm in both zones (user decision 2026-10-14), starvation unconfigured.
# Checks the file hash, dry-runs, and only with -Commit imports. Prints the read verb afterwards.
param([Parameter(Mandatory)][string] $ExpectedSha256, [switch] $Commit)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$exe = 'D:\zhengyushao\control-server-v2-ops\fieldops-a799c998\ControlServer.FieldOps.exe'
$db  = 'C:\ProgramData\8005\ControlServer.V2\data\controlserver.db'
$csv = 'D:\zhengyushao\control-server-v2-ops\prod-20261014\inputs\zone-parameters-production.csv'
$h = (Get-FileHash $csv).Hash
"### now $(Get-Date -Format o) sha256 $h"
if ($h -ne $ExpectedSha256) { throw "sha256 mismatch: $h" }
Get-Content $csv
"### dry-run"; & $exe import-dispatch-zone-parameters --database $db --input $csv --dry-run; "dry exit=$LASTEXITCODE"
if ($LASTEXITCODE -ne 0) { throw 'dry-run failed' }
if ($Commit) {
  "### import at $(Get-Date -Format o)"; & $exe import-dispatch-zone-parameters --database $db --input $csv; "import exit=$LASTEXITCODE"
  if ($LASTEXITCODE -ne 0) { throw 'import failed' }
  "### read back"; & $exe dispatch-zone-parameters --database $db
}
