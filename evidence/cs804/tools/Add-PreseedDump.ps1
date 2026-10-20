#Requires -Version 7
# cs804 comparison instrumentation, local only, never committed: right after "Environment is up", dumps every table of
# the run's database into <snapshots>/cs804-preseed-db.json so the before/after rounds can compare the preseed row by row.
param([Parameter(Mandatory)][string]$Repository)
$path = Join-Path $Repository 'scripts/l2/Invoke-L2Scenario.ps1'
$text = [IO.File]::ReadAllText($path)
$anchor = '    $journal.Note("Environment is up; entering scenario ''$Scenario''.")'
if (-not $text.Contains($anchor)) { throw "anchor not found in $path" }
if ($text.Contains('cs804-preseed-db.json')) { throw "already instrumented: $path" }
$nl = $text.Contains("`r`n") ? "`r`n" : "`n"
$dump = @'
    try {
        $cs804Connection = Get-Variable -Name connection -ValueOnly -ErrorAction SilentlyContinue
        if ($null -ne $cs804Connection) {
            $cs804Dump = [ordered]@{}
            # Invoke-L2Query hands its rows back as ONE array object: unroll it before looking at a row.
            foreach ($cs804Table in @(Invoke-L2Query -Connection $cs804Connection -Sql "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name" | ForEach-Object { $_ })) {
                $cs804Dump[[string]$cs804Table.name] = @(Invoke-L2Query -Connection $cs804Connection -Sql "SELECT * FROM `"$($cs804Table.name)`"" |
                    ForEach-Object { $_ } | ForEach-Object { $cs804Row = [ordered]@{}; foreach ($p in $_.PSObject.Properties) { $cs804Row[$p.Name] = if ($p.Value -is [byte[]]) { 'b64:' + [Convert]::ToBase64String($p.Value) } elseif ($p.Value -is [DateTime] -or $p.Value -is [DateTimeOffset]) { $p.Value.ToString('o') } else { $p.Value } }; $cs804Row })
            }
            [IO.File]::WriteAllText((Join-Path $snapshotRoot 'cs804-preseed-db.json'), ($cs804Dump | ConvertTo-Json -Depth 6 -Compress), [Text.UTF8Encoding]::new($false))
        }
    } catch { $journal.Note("cs804 preseed dump failed: $($_.Exception.Message)") }
'@ -replace "`r?`n", $nl
$text = $text.Replace($anchor, $anchor + $nl + $dump.TrimEnd())
[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
"instrumented $path"
