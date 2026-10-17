#Requires -Version 7
# Measure agv01's offset against factory01 (midpoint of two server reads) and step agv01's clock back by it once.
# User-authorised clock fix ("对时") on 2026-10-15. Changes only the system clock on agv01.
function Get-Ms([string] $Alias) { [long](ssh -o BatchMode=yes $Alias 'pwsh -NoProfile -Command "[DateTimeOffset]::Now.ToUnixTimeMilliseconds()"') }
function Get-Offset { $a = Get-Ms factory01; $c = Get-Ms agv01; $b = Get-Ms factory01; $c - [long](($a + $b) / 2) }
$before = Get-Offset
"before=$before ms"
if ([Math]::Abs($before) -lt 300) { 'within 300 ms, nothing to do'; return }
$cmd = "Set-Date -Adjust ([TimeSpan]::FromMilliseconds($(-$before))) | Out-Null; 'stepped'"
ssh -o BatchMode=yes agv01 "pwsh -NoProfile -Command `"$cmd`""
"after=$(Get-Offset) ms"
