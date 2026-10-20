#Requires -Version 7
<#
control-server#804 review S1: how the scenario's $Context.InvokeFieldOps reads FieldOps' output under a given console code
page, the old way (`& exe 2>&1`, decoded with the console's code page; the lines verbatim from 45d69f81) against the new
Invoke-L2FieldOpsProcess (UTF-8 whatever the console). Each way runs in its own child pwsh whose console output encoding is
set to -CodePage, so this process's console is never touched.

The command is an import-waiting-points --dry-run whose JSON carries a station named 等待点: nine UTF-8 bytes, an odd
number, right before a closing quote -- review P4's shape.

    pwsh -NoProfile -File Test-FieldOpsDecoding.ps1 -Repository <worktree> -WorkRoot <new dir> [-CodePage 936]
#>
param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$WorkRoot,
    [int]$CodePage = 936
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $WorkRoot) { throw "WorkRoot must not exist: $WorkRoot" }
$null = New-Item -ItemType Directory -Path $WorkRoot
$database = Join-Path $WorkRoot 'controlserver.db'
$fieldOps = Join-Path $Repository 'tools/ControlServer.FieldOps/bin/Release/net8.0/win-x64/ControlServer.FieldOps.exe'
$hostDirectory = Join-Path $Repository 'src/ControlServer.Host/bin/Release/net8.0/win-x64'

# A migrated database at a path named explicitly: --migrate-only without a connection string migrates the MVP default.
Import-Module (Join-Path $Repository 'scripts/l2/L2.psm1') -Force
Import-Module (Join-Path $Repository 'scripts/l2/L2WaitingPoints.psm1') -Force
Invoke-L2HostMigrateOnly -HostDirectory $hostDirectory -LogRoot $WorkRoot -Environment @{
    'ConnectionStrings__ControlServer' = "Data Source=$database"
    'JourneyRuntime__enabled'          = 'false'
}
if (-not (Test-Path -LiteralPath $database)) { throw "The host did not create $database" }

$utf8 = [Text.UTF8Encoding]::new($false)
$catalog = Join-Path $WorkRoot 'catalog-26.json'
[IO.File]::WriteAllText($catalog, '{"mapId":26,"stations":[{"stationId":214,"stationName":"等待点"}]}', $utf8)
$csv = Join-Path $WorkRoot 'waiting-points.csv'
[IO.File]::WriteAllText($csv, "map_id,station_id,station_name,enabled,vehicle_scope`n26,214,等待点,true,`n", $utf8)
$arguments = @('import-waiting-points', '--database', $database, '--input', $csv, '--catalog', $catalog,
    '--map', '26', '--fleet', 'VK-A', '--dry-run')
$argumentsJson = ConvertTo-Json -InputObject $arguments -Compress

$old = @'
param($exe, $argumentsJson, $codePage, $resultPath)
[Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
[Console]::OutputEncoding = [Text.Encoding]::GetEncoding([int]$codePage)
$all = @($argumentsJson | ConvertFrom-Json)
$lines = @(& $exe @all 2>&1)
$exit = $LASTEXITCODE
$json = $lines | Where-Object { $_ -is [string] -and $_.TrimStart().StartsWith('{') } | Select-Object -Last 1
$r = try { $parsed = $json | ConvertFrom-Json; "exit=$exit parsed name=$(($parsed.changes[0].after.stationName).ToCharArray() | ForEach-Object { 'U+{0:X4}' -f [int]$_ })" }
catch { "exit=$exit PARSE FAILED: $($_.Exception.Message)" }
[IO.File]::WriteAllText($resultPath, [string]$r, [Text.UTF8Encoding]::new($false))
'@
$new = @'
param($exe, $argumentsJson, $codePage, $resultPath, $module)
[Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
[Console]::OutputEncoding = [Text.Encoding]::GetEncoding([int]$codePage)
Import-Module $module -Force
$all = @($argumentsJson | ConvertFrom-Json)
$run = Invoke-L2FieldOpsProcess -Executable $exe -Arguments $all
$json = $run.Output | Where-Object { $_.TrimStart().StartsWith('{') } | Select-Object -Last 1
$r = try { $parsed = $json | ConvertFrom-Json; "exit=$($run.ExitCode) parsed name=$(($parsed.changes[0].after.stationName).ToCharArray() | ForEach-Object { 'U+{0:X4}' -f [int]$_ })" }
catch { "exit=$($run.ExitCode) PARSE FAILED: $($_.Exception.Message)" }
[IO.File]::WriteAllText($resultPath, [string]$r, [Text.UTF8Encoding]::new($false))
'@
$oldScript = Join-Path $WorkRoot 'old.ps1'; [IO.File]::WriteAllText($oldScript, $old, $utf8)
$newScript = Join-Path $WorkRoot 'new.ps1'; [IO.File]::WriteAllText($newScript, $new, $utf8)
$module = Join-Path $Repository 'scripts/l2/L2FieldOpsSession.psm1'

"code page $CodePage"
"old (& exe 2>&1):           " + $(& pwsh -NoProfile -File $oldScript $fieldOps $argumentsJson $CodePage (Join-Path $WorkRoot old.txt); [IO.File]::ReadAllText((Join-Path $WorkRoot old.txt)))
"new (Invoke-L2FieldOpsProcess): " + $(& pwsh -NoProfile -File $newScript $fieldOps $argumentsJson $CodePage (Join-Path $WorkRoot new.txt) $module; [IO.File]::ReadAllText((Join-Path $WorkRoot new.txt)))
