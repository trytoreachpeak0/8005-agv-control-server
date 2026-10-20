#Requires -Version 7
# Runs the trigger step of g3-nightly-trigger.yml as written. The first dispatch goes to GitHub with an extra input
# g3.yml never declares, so it is always refused (422) and starts nothing; any dispatch without trigger_cron is caught
# here and only recorded.
param([string]$Workflow)
$text = Get-Content -Raw $Workflow
$run = [regex]::Match($text, '(?ms)        run: \|\r?\n(.*)\z').Groups[1].Value -replace '(?m)^          ', ''
$script:posts = [System.Collections.Generic.List[string]]::new()
function Invoke-RestMethod {
    param([string]$Uri, [string]$Method = 'GET', $Headers, $Body, $ContentType, $TimeoutSec)
    if ($Method -ne 'POST') { return Microsoft.PowerShell.Utility\Invoke-RestMethod -Uri $Uri -Headers $Headers -TimeoutSec $TimeoutSec }
    $parsed = $Body | ConvertFrom-Json -AsHashtable
    $script:posts.Add($Body)
    if ($parsed.inputs.ContainsKey('trigger_cron')) {
        $parsed.inputs['cs807_probe_unknown'] = 'x'   # always refused, never starts a run
        return Microsoft.PowerShell.Utility\Invoke-RestMethod -Uri $Uri -Method POST -Headers $Headers -Body ($parsed | ConvertTo-Json -Compress) -ContentType $ContentType -TimeoutSec $TimeoutSec
    }
    Write-Host "PROBE: retry recorded, not sent: $Body"
}
$env:TRIGGER_CRON = '37 14 * * *'; $env:GITHUB_RUN_ID = '39326530359'; $env:GITHUB_REPOSITORY = 'trytoreachpeak0/8005-agv-control-server'; $env:GITHUB_SERVER_URL = 'https://github.com'
Invoke-Expression $run
Write-Host "POSTs: $($script:posts.Count)"
$script:posts | ForEach-Object { Write-Host "  $_" }
