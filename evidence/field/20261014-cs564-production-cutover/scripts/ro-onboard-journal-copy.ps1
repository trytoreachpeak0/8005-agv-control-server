$src='C:/Users/HT/AppData/Local/SQCD/8005AGV'; $dst="$env:TEMP/jcopy-c11"; New-Item -ItemType Directory -Force $dst | Out-Null
foreach ($s in '', '-wal', '-shm') { if (Test-Path "$src/onboard-journal.db$s") { Copy-Item "$src/onboard-journal.db$s" "$dst/onboard-journal.db$s" -Force } }
Get-ChildItem $dst | ForEach-Object { "$($_.FullName) $($_.Length)" }
Get-Content "$src/logs/agv-20261015.log" -Encoding utf8 | Select-Object -Skip 4380 -First 140 | Set-Content "$dst/log-ctx.txt" -Encoding utf8
