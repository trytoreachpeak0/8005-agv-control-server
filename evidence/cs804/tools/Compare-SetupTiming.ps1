#Requires -Version 7
param([Parameter(Mandatory)][string]$Before, [Parameter(Mandatory)][string]$After)
function Rows($root) {
    foreach ($tl in Get-ChildItem -Path $root -Filter timeline.jsonl -Recurse) {
        $ev = @(Get-Content -LiteralPath $tl.FullName | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable })
        $start = ([DateTimeOffset]$ev[0].at); $fo = 0.0; $up = $null
        for ($i = 0; $i -lt $ev.Count; $i++) {
            $note = [string]$ev[$i].note
            if ($note.StartsWith('Environment is up')) { $up = ([DateTimeOffset]$ev[$i].at); break }
            if ($note.StartsWith('FieldOps')) { $fo += (([DateTimeOffset]$ev[$i + 1].at) - ([DateTimeOffset]$ev[$i].at)).TotalSeconds }
        }
        if ($up) { [pscustomobject]@{ Scenario = $tl.Directory.Name; Setup = ($up - $start).TotalSeconds; FieldOps = $fo } }
    }
}
function Q($v, $q) { $s = @($v | Sort-Object); $s[[int][Math]::Floor(($s.Count - 1) * $q)] }
$b = @(Rows $Before); $a = @(Rows $After)
'| | before median | after median | before p90 | after p90 | before sum | after sum |'
'| --- | --- | --- | --- | --- | --- | --- |'
foreach ($m in 'Setup', 'FieldOps') {
    '| {0} | {1:N1} s | {2:N1} s | {3:N1} s | {4:N1} s | {5:N0} s | {6:N0} s |' -f $m, (Q $b.$m 0.5), (Q $a.$m 0.5), (Q $b.$m 0.9), (Q $a.$m 0.9), ($b.$m | Measure-Object -Sum).Sum, ($a.$m | Measure-Object -Sum).Sum
}
$pairs = foreach ($r in $b) { $o = $a | Where-Object Scenario -eq $r.Scenario; if ($o) { $r.Setup - $o.Setup } }
'per-scenario setup saving: median {0:N1} s, min {1:N1} s, max {2:N1} s, scenarios slower after: {3}' -f (Q $pairs 0.5), ($pairs | Measure-Object -Minimum).Minimum, ($pairs | Measure-Object -Maximum).Maximum, @($pairs | Where-Object { $_ -lt 0 }).Count
