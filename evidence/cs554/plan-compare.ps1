#Requires -Version 7
# Plans the default-mode round from a given l2.yml and estimates table, as l2.yml does, and prints the lanes.
param([string]$Yml, [string]$Repo, [string]$OldTableFromYml, [string]$EstimatesJson)
$y = Get-Content -Raw $Yml
$sc = [regex]::Match($y, '(?s)\n          \$scenarios = @\((.*?)\n          \)\n').Groups[1].Value
$scenarios = Invoke-Expression "@($sc)"
if ($OldTableFromYml) {
    $o = Get-Content -Raw $OldTableFromYml
    $tbl = [regex]::Match($o, '(?s)\$estimates = @\{(.*?)\n          \}').Groups[1].Value
    $estimates = Invoke-Expression "@{$tbl}"
} else {
    $estimates = (Get-Content -Raw -LiteralPath $EstimatesJson | ConvertFrom-Json -AsHashtable).estimates
}
$items = @(foreach ($s in $scenarios) {
    [pscustomobject]@{ Name = $s.Name; Runs = ($s.DefaultRuns ?? 1); BatchId = $s.BatchId ?? 'batch-2'; EstimateSeconds = $estimates[$s.Name] }
})
Import-Module (Join-Path $Repo 'scripts/l2/L2Lanes.psm1') -Force
"scenarios $($scenarios.Count), runs $(($items | Measure-Object Runs -Sum).Sum), estimated $(@($items | Where-Object { $null -ne $_.EstimateSeconds }).Count)"
foreach ($lane in Get-L2LanePlan -Items $items -LaneCount 4) {
    "lane $($lane.Lane) about $($lane.EstimateSeconds)s: " + (($lane.Items | ForEach-Object { if ($_.Runs -eq 1) { $_.Name } else { "$($_.Name) x$($_.Runs)" } }) -join ', ')
}
