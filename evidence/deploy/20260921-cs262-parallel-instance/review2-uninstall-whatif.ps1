#Requires -Version 7
# Produces the uninstall -WhatIf evidence for control-server#262 re-review 2. Runs the server-side
# uninstall script locally: it validates the definition and lists the plan, then stops at
# ShouldProcess, before the administrator check and before any action.
param([Parameter(Mandatory)][string] $Repo)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $Repo 'scripts\parallel'
$shipped = Join-Path $dir 'instance-factory01-v2.json'
$uninstall = Join-Path $dir 'Uninstall-ParallelInstanceLocal.ps1'

"=== 1. the shipped definition (placeholders still in it) ==="
try { & $uninstall -InstanceDefinitionPath $shipped -ConfirmUninstall -WhatIf; '(no exception -- unexpected)' }
catch { "threw: $($_.Exception.Message)" }

"", "=== 2. a copy with the two map-26 placeholders filled with test values ==="
$filled = Join-Path ([IO.Path]::GetTempPath()) "cs262-whatif-$PID.json"
try {
    $text = [IO.File]::ReadAllText($shipped).
        Replace('REPLACE_WITH_MAP26_DISPATCH_ZONE', 'MAP-26-WIRE_TO_GATE').
        Replace('REPLACE_WITH_MAP26_ADMISSION_POLICY_DEPLOYMENT_ID', 'TEST-ONLY-MAP26-POLICY')
    [IO.File]::WriteAllText($filled, $text, [Text.UTF8Encoding]::new($false))
    & $uninstall -InstanceDefinitionPath $filled -ConfirmUninstall -WhatIf 6>&1 2>&1 | ForEach-Object { "$_" }
    '(returned without an exception)'
} catch { "threw: $($_.Exception.Message)" }
finally { [IO.File]::Delete($filled) }
