#Requires -Version 7
param([string]$PublishRoot, [string]$SettingsPath)
$ErrorActionPreference = 'Stop'
$infrastructure = [Reflection.Assembly]::LoadFrom((Join-Path $PublishRoot 'SQCD.Agv.Infrastructure.dll'))
$type = $infrastructure.GetType('SQCD.Agv.Infrastructure.OnboardSettings', $true)
"type=$($type.FullName)"
try {
    $s = $type::Load($SettingsPath)
    "LOAD_OK messageTimeoutMs=$($s.WireToGate.MessageTimeoutMs)"
} catch {
    $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }
    "LOAD_REFUSED $($e.GetType().FullName): $($e.Message)"
}
