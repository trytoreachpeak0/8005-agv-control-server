$t = Get-Content -Raw C:/8005/OnboardHmi/appsettings.json
[regex]::Matches($t, '"[A-Za-z]*(?:[Pp]ath|[Dd]irectory|[Dd]ir|[Jj]ournal|[Ll]og|[Dd]atabase|[Cc]onnection)[A-Za-z]*"\s*:\s*"[^"]*"') | ForEach-Object Value
Get-ChildItem C:/8005/OnboardHmi -Directory | ForEach-Object Name
Get-ChildItem C:/ProgramData -Directory -ErrorAction SilentlyContinue | Where-Object Name -match '8005|SQCD|Onboard' | ForEach-Object { $_.FullName; Get-ChildItem $_.FullName -Recurse -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 8 | ForEach-Object { "  $($_.FullName) $($_.Length) $($_.LastWriteTime)" } }
