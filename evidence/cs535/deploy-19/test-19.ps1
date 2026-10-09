#Requires -Version 7
# Offline check of 19-deploy-control-server-parallel.ps1's Assert-MesIngestSourceReported (control-server#535):
# the function is taken from 19's AST and fed simulated installer output; nothing touches a machine.
param([string] $Script, [string] $ParallelDir)
$ErrorActionPreference = 'Stop'
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$null, [ref]$errors)
"ParseFile errors: $(@($errors).Count)"
$errors | ForEach-Object { "  line $($_.Extent.StartLineNumber): $($_.Message)" }
Import-Module (Join-Path $ParallelDir 'ParallelInstance.psm1') -Force
function Write-Step { param($Message) }
function Write-Warning { param($Message) $script:warned = $true }
$fn = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-MesIngestSourceReported' }, $true)[0]
. ([scriptblock]::Create($fn.Extent.Text))
function Check($name, $def, $out, [switch] $Rollback, [bool] $expectOk) {
    $script:definition = Read-ParallelInstanceDefinition -Path (Join-Path $ParallelDir $def)
    $script:sourceProperty = (Get-ParallelInstanceLayout -Definition $script:definition).PSObject.Properties['MesIngestSource']
    $script:mesIngestSource = ${script:sourceProperty}?.Value ?? 'fake'
    $script:productionSource = $script:mesIngestSource -ceq 'production'
    $script:warned = $false
    $j = $script:definition['journeyRuntime']
    $eff = "EFFECTIVE_CONFIGURATION=allowedWorkTypes=$(@($j['allowedWorkTypes']) -join ',') allowedDispatchZones=$(@($j['allowedDispatchZones']) -join ',') mesIngestBaseUrl=$($script:definition['mesIngest']['baseUrl'])"
    $audit = Format-ParallelMesIngestAudit -Definition $script:definition
    $lines = & $out $audit $eff
    $ok = $true; $msg = ''
    try { Assert-MesIngestSourceReported -Output $lines -Rollback:$Rollback } catch { $ok = $false; $msg = $_.Exception.Message }
    "{0} {1} {2}{3}" -f (($ok -eq $expectOk) ? 'PASS' : 'FAIL'), $name, $msg, ($script:warned ? ' [warned]' : '')
}
$m1 = 'EFFECTIVE_CONFIGURATION=allowedWorkTypes=STAGING_TO_WIRE,DIE_TO_OVEN,WIRE_TO_GATE,WIRE_TO_OPTICAL,STAGING_TO_WIRE,WIRE_TO_NITROGEN allowedDispatchZones=WIRE mesIngestBaseUrl=http://127.0.0.1:5088'
$P = 'instance-factory01-v2.production-mes.json'; $F = 'instance-factory01-v2.json'
Check 'production install, audit + effective' $P { param($a, $e) @('[12:00:00] x', $a, $e, 'RESULT_PATH=x') } -expectOk $true
Check 'production install, effective missing' $P { param($a, $e) @($a, 'RESULT_PATH=x') } -expectOk $false
Check 'production install, M1 effective (six types bound)' $P { param($a, $e) @($a, $m1) } -expectOk $false
Check 'production install, extra FAKE line' $P { param($a, $e) @($a, $e, 'FAKE_MES_INGEST=http://127.0.0.1:58188') } -expectOk $false
Check 'production install, fake audit reported' $P { param($a, $e) @('MES_INGEST_SOURCE=fake baseUrl=http://127.0.0.1:58188 allowedWorkTypes=X -- y', $e) } -expectOk $false
Check 'production install, no audit' $P { param($a, $e) @($e, 'RESULT_PATH=x') } -expectOk $false
Check 'production install, audit with wrong work types' $P { param($a, $e) @('MES_INGEST_SOURCE=production baseUrl=http://127.0.0.1:5088 allowedWorkTypes=STAGING_TO_WIRE,WIRE_TO_GATE -- y', $e) } -expectOk $false
Check 'production rollback, audit + effective' $P { param($a, $e) @($a, $e) } -Rollback -expectOk $true
Check 'production rollback, effective missing' $P { param($a, $e) @($a) } -Rollback -expectOk $false
Check 'fake install, audit + FAKE + effective' $F { param($a, $e) @($a, 'FAKE_MES_INGEST=http://127.0.0.1:58188', $e) } -expectOk $true
Check 'fake install, effective missing (old package): warns, passes' $F { param($a, $e) @($a, 'FAKE_MES_INGEST=http://127.0.0.1:58188') } -expectOk $true
Check 'fake install, effective differs from definition' $F { param($a, $e) @($a, 'FAKE_MES_INGEST=x', $e.Replace('58188', '5088')) } -expectOk $false
Check 'fake install, audit without FAKE' $F { param($a, $e) @($a, $e) } -expectOk $false
Check 'fake rollback, audit + effective' $F { param($a, $e) @($a, $e) } -Rollback -expectOk $true
Check 'fake install, production audit' $F { param($a, $e) @('MES_INGEST_SOURCE=production baseUrl=http://127.0.0.1:5088 allowedWorkTypes=STAGING_TO_WIRE -- y', 'FAKE_MES_INGEST=x', $e) } -expectOk $false
