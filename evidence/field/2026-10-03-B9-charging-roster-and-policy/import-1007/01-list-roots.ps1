foreach ($root in 'C:\Program Files\8005 AGV', 'C:\ProgramData\8005') { "== $root"; Get-ChildItem -LiteralPath $root -Directory -Force | ForEach-Object Name }
