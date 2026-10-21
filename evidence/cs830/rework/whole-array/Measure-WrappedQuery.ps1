#Requires -Version 7
# control-server#830: how the two wrapped calls in load-remaining-baskets-skipped.ps1 behaved, measured against a
# stand-in with Invoke-L2Query's return shape (return , $rows), for 0, 1 and 2 outbox rows. Each form is the
# scenario's own expression, old (wrapped) and new (assigned first).
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Rows = @()
function Invoke-L2Query([object]$Connection, [string]$Sql) { return , @($script:Rows) }
function Row([string]$outcome) {
    [pscustomobject]@{ PayloadJson = (@{ payload = @{ outcome = $outcome; operationType = 'UNLOAD'; demandId = 'D' } } | ConvertTo-Json -Compress) }
}
$connection = $null; $demandId = 'D'
foreach ($n in 0, 1, 2) {
    $script:Rows = @(1..$n | Where-Object { $n -gt 0 } | ForEach-Object { Row "AUTHORIZED" })
    foreach ($form in 'old', 'new') {
        try {
            if ($form -eq 'old') {
                $answer = @(Invoke-L2Query -Connection $connection -Sql 'x')
                $command = @(Invoke-L2Query -Connection $connection -Sql 'x') |
                    ForEach-Object { ($_.PayloadJson | ConvertFrom-Json).payload } |
                    Where-Object { [string]$_.operationType -eq 'UNLOAD' -and [string]$_.demandId -eq $demandId }
            } else {
                $answer = Invoke-L2Query -Connection $connection -Sql 'x'
                $commands = Invoke-L2Query -Connection $connection -Sql 'x'
                $command = $commands |
                    ForEach-Object { ($_.PayloadJson | ConvertFrom-Json).payload } |
                    Where-Object { [string]$_.operationType -eq 'UNLOAD' -and [string]$_.demandId -eq $demandId }
            }
            $answers = @($answer | ForEach-Object { ($_.PayloadJson | ConvertFrom-Json).payload })
            "rows=$n $form : answers.Count=$($answers.Count) unloadCommand.Count=$(@($command).Count) RB02-count-criterion=$($answers.Count -eq 1)"
        } catch {
            "rows=$n $form : THREW $($_.Exception.Message)"
        }
    }
}
