#Requires -Version 7
param([Parameter(Mandatory)][string]$Root, [switch]$PerScenario)
$rows = foreach ($tl in Get-ChildItem -Path $Root -Filter timeline.jsonl -Recurse) {
    $ev = @(Get-Content -LiteralPath $tl.FullName | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable })
    $start = ([DateTimeOffset]$ev[0].at); $up = $null; $fo = 0.0; $n = 0; $pc = 0.0
    for ($i = 0; $i -lt $ev.Count; $i++) {
        $note = [string]$ev[$i].note
        if ($note.StartsWith('Environment is up')) { $up = ([DateTimeOffset]$ev[$i].at); break }
        if ($note.StartsWith('FieldOps')) {
            $fo += (([DateTimeOffset]$ev[$i + 1].at) - ([DateTimeOffset]$ev[$i].at)).TotalSeconds; $n++ }
        if ($note.StartsWith('Package capacity rules imported')) {
            $pc = (([DateTimeOffset]$ev[$i].at) - ([DateTimeOffset]$ev[$i - 1].at)).TotalSeconds }
    }
    if ($up) { [pscustomobject]@{ Scenario = $tl.Directory.Name; Setup = ($up - $start).TotalSeconds; FieldOps = $fo; Calls = $n; PackageCapacity = $pc } }
}
if ($PerScenario) { $rows | Sort-Object Scenario | Format-Table -AutoSize | Out-String -Width 200 }
function Median($v) { $s = @($v | Sort-Object); if ($s.Count % 2) { $s[[int][math]::Floor($s.Count / 2)] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 } }
[pscustomobject]@{
    Count = $rows.Count
    SetupMedian = [math]::Round((Median $rows.Setup), 1); SetupSum = [math]::Round(($rows.Setup | Measure-Object -Sum).Sum)
    FieldOpsMedian = [math]::Round((Median $rows.FieldOps), 1); FieldOpsSum = [math]::Round(($rows.FieldOps | Measure-Object -Sum).Sum)
    CallsMedian = Median $rows.Calls; PackageCapacityMedian = [math]::Round((Median $rows.PackageCapacity), 1)
} | Format-List
