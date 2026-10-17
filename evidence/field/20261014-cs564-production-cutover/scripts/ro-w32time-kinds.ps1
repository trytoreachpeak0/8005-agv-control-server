#Requires -Version 7
# READ-ONLY (ws#54 step 1): registry value kind and raw value of the two w32time phase-correction limits.
$p = 'HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Config'
$k = Get-Item -LiteralPath $p
foreach ($n in 'MaxPosPhaseCorrection','MaxNegPhaseCorrection') {
  try { $kind = $k.GetValueKind($n); $raw = $k.GetValue($n, $null, 'DoNotExpandEnvironmentNames'); "$n kind=$kind clrType=$($raw.GetType().FullName) raw=$raw hex=0x$('{0:X}' -f $raw)" } catch { "$n ERR $($_.Exception.Message)" }
}
"reg query:"; reg query 'HKLM\SYSTEM\CurrentControlSet\Services\W32Time\Config' /v MaxPosPhaseCorrection; reg query 'HKLM\SYSTEM\CurrentControlSet\Services\W32Time\Config' /v MaxNegPhaseCorrection
