#Requires -Version 7

# The nightly G3 (.github/workflows/g3.yml, control-server#582). What the workflow and Invoke-NightlyG3.ps1 decide is
# kept here, so that scripts/Test-NightlyG3.ps1 can check it offline: no runner here starts a process or a window.

function Get-NightlyG3Verdict {
    param(
        [Parameter(Mandatory)][string]$Runner,
        [Parameter(Mandatory)][AllowEmptyString()][string]$EvidenceRoot,
        [Parameter(Mandatory)][AllowNull()][object]$ExitCode
    )

    # One runner's verdict: runner, result (PASS, FAIL, ERROR or the NOT_STARTED_* the caller passed as the exit code),
    # status (the runner's own), failed (what is red, each a name the evidence uses) and detail (one line for a person).
    $verdict = [ordered]@{ runner = $Runner; result = $null; status = $null; failed = @(); detail = '' }
    if ($ExitCode -is [string] -and $ExitCode -like 'NOT_STARTED_*') {
        $verdict.result = $ExitCode
        return [pscustomobject]$verdict
    }

    $path = Join-Path $EvidenceRoot 'run-result.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $verdict.result = 'ERROR'
        $verdict.detail = "no run-result.json (exit $ExitCode)"
        return [pscustomobject]$verdict
    }
    try { $runResult = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -AsHashtable }
    catch {
        $verdict.result = 'ERROR'
        $verdict.detail = "run-result.json does not parse (exit $ExitCode): $($_.Exception.Message)"
        return [pscustomobject]$verdict
    }

    $verdict.status = "$($runResult['status'])"
    if ($verdict.status -ceq 'INCONCLUSIVE_RUNNER_ERROR') {
        $verdict.result = 'ERROR'
        $message = $runResult['error']?['message']
        $verdict.detail = if ([string]::IsNullOrWhiteSpace($message)) { "INCONCLUSIVE_RUNNER_ERROR (exit $ExitCode), no error message" } else {
            "INCONCLUSIVE_RUNNER_ERROR: $(($message -replace '\s+', ' ').Trim())" }
        return [pscustomobject]$verdict
    }

    # Every assertion that is not PASS, the runner's own failedAssertions list included (the restart, journey and
    # demand-bearing runners write one; the staged runner does not), then every scenario that did not PASS.
    $failed = [System.Collections.Generic.List[string]]::new()
    $assertions = $runResult['assertions']
    if ($assertions -is [System.Collections.IDictionary]) {
        foreach ($key in $assertions.Keys) { if ("$($assertions[$key])" -cne 'PASS' -and -not $failed.Contains($key)) { $failed.Add($key) } }
    }
    foreach ($name in @($runResult['failedAssertions'])) { if ($null -ne $name -and -not $failed.Contains("$name")) { $failed.Add("$name") } }
    foreach ($scenario in @($runResult['scenarios'])) {
        if ($scenario -is [System.Collections.IDictionary] -and "$($scenario['outcome'])" -cne 'PASS') {
            $reason = (("$($scenario['failureReason'])") -replace '\s+', ' ').Trim()
            $failed.Add("scenario $($scenario['name']): $(if ($reason) { $reason } else { "outcome $($scenario['outcome'])" })")
        }
    }
    $verdict.failed = @($failed)

    if ($ExitCode -eq 0 -and $verdict.status -cmatch '_PASS$' -and $failed.Count -eq 0) {
        $verdict.result = 'PASS'
    } else {
        $verdict.result = 'FAIL'
        $verdict.detail = "status $($verdict.status), exit $ExitCode"
    }
    return [pscustomobject]$verdict
}

Export-ModuleMember -Function Get-NightlyG3Verdict
