param([string] $Ssh, [int] $N)
$s = '#' + ('x' * $N) + [char]10 + "'ok'"
$e = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($s))
$o = & $Ssh -o BatchMode=yes agv02 "pwsh -NoProfile -NonInteractive -EncodedCommand $e" 2>&1
"ssh=$Ssh len=$($e.Length) -> " + (($o | Out-String).Trim() -split "`n" | Select-Object -Last 1)
