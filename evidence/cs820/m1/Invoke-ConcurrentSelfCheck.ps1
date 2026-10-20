#Requires -Version 7
# cs#820 M1: start two copies of one self-check at the same moment, -Rounds times, each pinned to CPUs 0-3,
# and record each copy's exit code and full output under -OutDir. Exit 0 when every copy exited 0.
param(
    [Parameter(Mandatory)][string]$Script,
    [Parameter(Mandatory)][string]$OutDir,
    [int]$Rounds = 3,
    [int]$Copies = 2
)
$ErrorActionPreference = 'Stop'
$null = New-Item -ItemType Directory -Path $OutDir -Force
$pwsh = (Get-Process -Id $PID).Path
$summary = [Collections.Generic.List[string]]::new()
$allGreen = $true
for ($round = 1; $round -le $Rounds; $round++) {
    $procs = for ($copy = 1; $copy -le $Copies; $copy++) {
        $log = Join-Path $OutDir ("round{0}-copy{1}.log" -f $round, $copy)
        $p = Start-Process -FilePath $pwsh -ArgumentList @('-NoProfile', '-File', $Script) -PassThru -NoNewWindow `
            -RedirectStandardOutput $log -RedirectStandardError "$log.stderr"
        $p.ProcessorAffinity = [IntPtr]0xF
        [pscustomobject]@{ Process = $p; Log = $log; Copy = $copy; Started = [DateTime]::UtcNow }
    }
    foreach ($entry in $procs) {
        $entry.Process.WaitForExit()
        $code = $entry.Process.ExitCode
        $fails = @(Get-Content -LiteralPath $entry.Log | Where-Object { $_ -like 'FAIL *' })
        $line = "round $round copy $($entry.Copy): exit=$code started=$($entry.Started.ToString('HH:mm:ss.fff'))Z fails=$($fails.Count)"
        $summary.Add($line); $fails | ForEach-Object { $summary.Add("    $_") }
        if ($code -ne 0) { $allGreen = $false }
    }
}
$summary | Set-Content -LiteralPath (Join-Path $OutDir 'SUMMARY.txt') -Encoding utf8NoBOM
$summary | ForEach-Object { Write-Host $_ }
exit $(if ($allGreen) { 0 } else { 1 })
