#Requires -Version 7
param([string] $Script, [string] $ParallelDir)
$ErrorActionPreference = 'Stop'
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$null, [ref]$errors)
"ParseFile errors: $(@($errors).Count)"
$errors | ForEach-Object { "  line $($_.Extent.StartLineNumber): $($_.Message)" }
Import-Module (Join-Path $ParallelDir 'ParallelInstance.psm1') -Force
function Write-Step { param($Message) }
$fn = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-MesIngestSourceReported' }, $true)[0]
. ([scriptblock]::Create($fn.Extent.Text))
function Check($name, $def, $out, [switch] $Rollback, [bool] $expectOk) {
    $script:definition = Read-ParallelInstanceDefinition -Path (Join-Path $ParallelDir $def)
    $script:sourceProperty = (Get-ParallelInstanceLayout -Definition $script:definition).PSObject.Properties['MesIngestSource']
    $script:mesIngestSource = ${script:sourceProperty}?.Value ?? 'fake'
    $script:productionSource = $script:mesIngestSource -ceq 'production'
    $audit = Format-ParallelMesIngestAudit -Definition $script:definition
    $lines = & $out $audit
    $ok = $true; $msg = ''
    try { Assert-MesIngestSourceReported -Output $lines -Rollback:$Rollback } catch { $ok = $false; $msg = $_.Exception.Message }
    "{0} {1} {2}" -f (($ok -eq $expectOk) ? 'PASS' : 'FAIL'), $name, $msg
}
Check 'production install, audit only' 'instance-factory01-v2.production-mes.json' { param($a) @('[12:00:00] x', $a, 'RESULT_PATH=x') } -expectOk $true
Check 'production install, extra FAKE line' 'instance-factory01-v2.production-mes.json' { param($a) @($a, 'FAKE_MES_INGEST=http://127.0.0.1:58188') } -expectOk $false
Check 'production install, fake audit reported' 'instance-factory01-v2.production-mes.json' { param($a) @('MES_INGEST_SOURCE=fake baseUrl=http://127.0.0.1:58188 allowedWorkTypes=X -- y') } -expectOk $false
Check 'production install, no audit' 'instance-factory01-v2.production-mes.json' { param($a) @('RESULT_PATH=x') } -expectOk $false
Check 'production install, wrong work types' 'instance-factory01-v2.production-mes.json' { param($a) @('MES_INGEST_SOURCE=production baseUrl=http://127.0.0.1:5088 allowedWorkTypes=STAGING_TO_WIRE,WIRE_TO_GATE -- y') } -expectOk $false
Check 'production rollback, audit' 'instance-factory01-v2.production-mes.json' { param($a) @($a) } -Rollback -expectOk $true
Check 'fake install, audit + FAKE' 'instance-factory01-v2.json' { param($a) @($a, 'FAKE_MES_INGEST=http://127.0.0.1:58188') } -expectOk $true
Check 'fake install, audit without FAKE' 'instance-factory01-v2.json' { param($a) @($a) } -expectOk $false
Check 'fake rollback, audit only' 'instance-factory01-v2.json' { param($a) @($a) } -Rollback -expectOk $true
Check 'fake install, production audit' 'instance-factory01-v2.json' { param($a) @('MES_INGEST_SOURCE=production baseUrl=http://127.0.0.1:5088 allowedWorkTypes=STAGING_TO_WIRE -- y', 'FAKE_MES_INGEST=x') } -expectOk $false
