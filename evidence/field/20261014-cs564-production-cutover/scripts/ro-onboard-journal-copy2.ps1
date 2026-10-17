$src='C:/Users/HT/AppData/Local/SQCD/8005AGV'; $dst="$env:TEMP/jcopy-c11b"; New-Item -ItemType Directory -Force $dst | Out-Null
foreach ($s in '', '-wal', '-shm') { if (Test-Path "$src/onboard-journal.db$s") { Copy-Item "$src/onboard-journal.db$s" "$dst/onboard-journal.db$s" -Force } }
Get-Content "$src/logs/agv-20261015.log" -Encoding utf8 | Where-Object { $_ -match '^2026-10-15T15:(3[7-9]|4[0-2])' -and $_ -notmatch 'Heartbeat' } | Set-Content "$dst/log-1537-1542.txt" -Encoding utf8
Get-ChildItem $dst | ForEach-Object { "$($_.Name) $($_.Length)" }
