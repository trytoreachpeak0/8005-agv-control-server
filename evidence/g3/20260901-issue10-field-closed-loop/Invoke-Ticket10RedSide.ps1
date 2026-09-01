[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GreenRunRoot,
    [Parameter(Mandatory)][string]$RedRoot
)

# Red side for the four observations that carry the "this run was really plaintext, and it left the
# machine alone" claim in Stop-Ticket10FieldRun.ps1. Each detector is exercised twice: once on the
# untouched green inputs (must stay silent) and once on a single-field mutation (must fire). The red
# side runs the SAME file — Stop-Ticket10FieldRun.ps1 -DetectorsOnly — not a reimplementation, so a
# detector that is broken cannot pass here and fail there.
#
# Both directions are printed for every detector. A table that only shows the mutations cannot tell
# a working detector from one that fires unconditionally.

$ErrorActionPreference = 'Stop'
$stopScript = Join-Path $PSScriptRoot 'Stop-Ticket10FieldRun.ps1'
if (Test-Path -LiteralPath $RedRoot) { throw "RedRoot already exists: $RedRoot" }
New-Item -ItemType Directory -Path $RedRoot -Force | Out-Null

function New-Case {
    param([string]$Name, [scriptblock]$Mutate)
    $caseRoot = Join-Path $RedRoot $Name
    New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
    foreach ($file in 'run-start.json', 'host.out.log', 'host.err.log', 'controlserver.db') {
        $source = Join-Path $GreenRunRoot $file
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $caseRoot -Force }
    }
    foreach ($suffix in '-wal', '-shm') {
        $source = (Join-Path $GreenRunRoot 'controlserver.db') + $suffix
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination ((Join-Path $caseRoot 'controlserver.db') + $suffix) -Force
        }
    }
    # The copied run-start.json still points its databasePath at the green run. Repoint it at the
    # copy so the case is self-contained, and neutralise the PIDs it would otherwise try to kill.
    $startPath = Join-Path $caseRoot 'run-start.json'
    $start = Get-Content -LiteralPath $startPath -Raw | ConvertFrom-Json
    $start.databasePath = Join-Path $caseRoot 'controlserver.db'
    if ($Mutate) { & $Mutate $start $caseRoot }
    $start | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $startPath -Encoding utf8NoBOM

    & $stopScript -RunRoot $caseRoot -DetectorsOnly | Out-Null
    return (Get-Content -LiteralPath (Join-Path $caseRoot 'run-result.json') -Raw | ConvertFrom-Json)
}

$rows = [System.Collections.Generic.List[object]]::new()
function Add-Row {
    param([string]$Detector, [string]$Side, [string]$Expected, [string]$Observed)
    $verdict = if ($Expected -eq $Observed) { 'PASS' } else { 'FAIL' }
    $rows.Add([pscustomobject]@{
        Detector = $Detector; Side = $Side; Expected = $Expected; Observed = $Observed; Verdict = $verdict
    })
}

# ---------------------------------------------------------------- control: unmutated green inputs
$control = New-Case -Name 'control' -Mutate $null
Add-Row 'store-digest-drift'   'CONTROL' '0'    ([string]@($control.storeDrift).Count)
Add-Row 'key-material-scan'    'CONTROL' '0'    ([string]@($control.keyMaterialFiles).Count)
Add-Row 'tls-diagnostic-scan'  'CONTROL' '0'    ([string]@($control.tlsDiagnosticHits).Count)
Add-Row 'production-pid-guard' 'CONTROL' 'True' ([string]$control.productionServicePidUnchanged)

# ----------------------------------------------------- red 1: one thumbprint changed in a baseline
$red1 = New-Case -Name 'red-store-digest' -Mutate {
    param($start, $caseRoot)
    # Same count, different digest: a detector that only compares counts cannot see this.
    $start.storeDigestsAtStart[0].Digest = ('0' * 64)
}
Add-Row 'store-digest-drift' 'RED(one digest changed, count identical)' '1' ([string]@($red1.storeDrift).Count)

# --------------------------------------------------------- red 2: one key-material file in the run
$red2 = New-Case -Name 'red-key-material' -Mutate {
    param($start, $caseRoot)
    # A named decoy inside the object under test, not in the harness root.
    Set-Content -LiteralPath (Join-Path $caseRoot 'decoy-server.pfx') -Value 'not a real key' -Encoding ascii
}
Add-Row 'key-material-scan' 'RED(one .pfx planted)' '1' ([string]@($red2.keyMaterialFiles).Count)

# --------------------------------------------------------------- red 3: one TLS diagnostic in a log
$red3 = New-Case -Name 'red-tls-diagnostic' -Mutate {
    param($start, $caseRoot)
    Add-Content -LiteralPath (Join-Path $caseRoot 'host.out.log') `
        -Value '[00:00:00 ERR] AuthenticationException: Cannot determine the frame size or a corrupted frame was received.'
}
Add-Row 'tls-diagnostic-scan' 'RED(one AuthenticationException line)' '1' ([string]@($red3.tlsDiagnosticHits).Count)

# ------------------------------------------------------------ red 4: production service PID drifted
$red4 = New-Case -Name 'red-production-pid' -Mutate {
    param($start, $caseRoot)
    $start.productionServicePid = [int]$start.productionServicePid + 1
}
Add-Row 'production-pid-guard' 'RED(baseline PID off by one)' 'False' ([string]$red4.productionServicePidUnchanged)

$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Output
$failed = @($rows | Where-Object { $_.Verdict -eq 'FAIL' })
$summary = 'RED-SIDE {0}/{1} PASS' -f ($rows.Count - $failed.Count), $rows.Count
Write-Output $summary
$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $RedRoot 'red-side.json') -Encoding utf8NoBOM
