$ErrorActionPreference='Continue'
$dirs = Get-ChildItem C:/Users -Directory -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName 'AppData/Local/SQCD/8005AGV/logs' } | Where-Object { Test-Path $_ }
"dirs: $($dirs -join '; ')"
foreach ($d in $dirs) {
  $fs = Get-ChildItem $d -File | Sort-Object LastWriteTime | Select-Object -Last 2
  "files: $($fs.Name -join ', ')"
  foreach ($f in $fs) { Select-String -LiteralPath $f.FullName -Pattern 'LoadCancellation|CONTENT_CONFLICT|7a2376ce|2cfe544c' -ErrorAction SilentlyContinue | Where-Object { $_.Line -match '2026-10-15T0?7:1|15:1[2-6]' } | ForEach-Object { "$($f.Name):$($_.LineNumber): " + $_.Line.Substring(0,[Math]::Min(1500,$_.Line.Length)) } }
}
