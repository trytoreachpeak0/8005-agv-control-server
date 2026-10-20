#Requires -Version 7
# Time before "Environment is up", attributed to the event that ENDS each gap (its criterion, or its note's first words).
param([Parameter(Mandatory)][string]$Before, [Parameter(Mandatory)][string]$After)
function Steps($root) {
    $sum = @{}
    foreach ($tl in Get-ChildItem -Path $root -Filter timeline.jsonl -Recurse) {
        $ev = @(Get-Content -LiteralPath $tl.FullName | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable })
        for ($i = 1; $i -lt $ev.Count; $i++) {
            $label = if ($ev[$i].criterion) { "criterion:$($ev[$i].criterion -replace ':AGV-L2-\d+$','')" } else { 'note:' + ((([string]$ev[$i].note) -split '[ :;(]')[0..1] -join ' ') }
            $gap = ([DateTimeOffset]$ev[$i].at - [DateTimeOffset]$ev[$i - 1].at).TotalSeconds
            $sum[$label] = ($sum[$label] ?? 0) + $gap
            if (([string]$ev[$i].note).StartsWith('Environment is up')) { break }
        }
    }
    $sum
}
$b = Steps $Before; $a = Steps $After
@($b.Keys + $a.Keys | Sort-Object -Unique) | ForEach-Object {
    [pscustomobject]@{ Step = $_; Before = [Math]::Round(($b[$_] ?? 0)); After = [Math]::Round(($a[$_] ?? 0)); Delta = [Math]::Round(($a[$_] ?? 0) - ($b[$_] ?? 0)) }
} | Where-Object { [Math]::Abs($_.Delta) -ge 15 } | Sort-Object Delta | Format-Table -AutoSize | Out-String -Width 200
