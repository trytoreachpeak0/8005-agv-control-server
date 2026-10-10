#Requires -Version 7

<#
.SYNOPSIS
    Offline self-check of the nightly G3's decisions (control-server#582): no runner, no window, no network.

.DESCRIPTION
    A second or two. Everything here is a function of scripts/NightlyG3.psm1, fed with inputs shaped like what a
    real round produces, and nothing the nightly run does in the dark is left to be found out the first night.

      1. The verdict of one runner, from its exit code and its run-result.json. Only exit 0 together with a status
         ending in _PASS and no assertion other than PASS is PASS; INCONCLUSIVE_RUNNER_ERROR, a missing or
         unreadable run-result.json is ERROR, with the runner's own message; anything else is FAIL, naming every
         assertion that is not PASS and every scenario that did not PASS with its failure reason. The rows are
         shaped like the batch-10 exit's own evidence (evidence/g3/20261010-protocol-v3.0.0-*), inlined so that a
         clone without the evidence tree can run this.

    Exits 1 when any check comes out the other way, and prints every check either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\Test-NightlyG3.ps1
#>
[CmdletBinding()]
param(
    [string]$ScriptRoot = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $ScriptRoot 'NightlyG3.psm1') -Force

$failures = [System.Collections.Generic.List[string]]::new()
function Check([string]$name, [bool]$ok, [string]$detail) {
    Write-Host ("{0} {1}{2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $(if ($ok) { '' } else { " -- $detail" }))
    if (-not $ok) { $failures.Add($name) }
}

$scratch = Join-Path ([IO.Path]::GetTempPath()) "nightly-g3-selfcheck-$([guid]::NewGuid().ToString('n'))"
New-Item -ItemType Directory -Path $scratch | Out-Null
function New-EvidenceRoot([string]$Name, $RunResult) {
    $root = Join-Path $scratch $Name
    New-Item -ItemType Directory -Path $root | Out-Null
    if ($null -ne $RunResult) {
        if ($RunResult -is [string]) { Set-Content -LiteralPath (Join-Path $root 'run-result.json') -Value $RunResult -NoNewline }
        else { $RunResult | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'run-result.json') }
    }
    return $root
}

try {
    # --- 1. one runner's verdict -------------------------------------------------------------------------------
    $verdictCases = @(
        @{ Name = 'staged red on two content-conflict criteria (the batch-10 exit, 5f3adc42)'; Runner = 'staged'; Exit = 1
           RunResult = [ordered]@{ status = 'STAGED_SLICE_FAIL'; error = $null; assertions = [ordered]@{
                   identityRejections = 'PASS'; sameMessageIdDifferentContentStableConflict = 'FAIL_OR_INCONCLUSIVE'
                   businessMessageSameMessageIdDifferentContentStableConflict = 'FAIL_OR_INCONCLUSIVE'; businessMessageAckDropInSessionReplay = 'PASS' } }
           Result = 'FAIL'; Names = @('sameMessageIdDifferentContentStableConflict', 'businessMessageSameMessageIdDifferentContentStableConflict')
           NotNames = @('identityRejections', 'businessMessageAckDropInSessionReplay') }
        @{ Name = 'restart all PASS'; Runner = 'restart'; Exit = 0
           RunResult = [ordered]@{ status = 'STAGED_G3_PROCESS_RESTART_PASS'; error = $null; failedAssertions = @()
               assertions = [ordered]@{ commitBindingSharedWithMainRunner = 'PASS'; freshDatabaseStartsAtGenerationOne = 'PASS' } }
           Result = 'PASS'; Names = @(); NotNames = @('commitBindingSharedWithMainRunner') }
        @{ Name = 'journey red, failed assertions and a scenario reason (the batch-10 exit, 3411887d)'; Runner = 'journey'; Exit = 1
           RunResult = [ordered]@{ status = 'JOURNEY_G3_SLICE_FAIL'; error = $null
               assertions = [ordered]@{ exactlyOneAcceptedDemandSnapshot = 'PASS'; journeyBlockedOnDeclaredUnknown = 'FAIL'; noScenarioAbortedBeforeItsJudgments = 'FAIL' }
               failedAssertions = @('journeyBlockedOnDeclaredUnknown', 'noScenarioAbortedBeforeItsJudgments')
               scenarios = @(
                   [ordered]@{ name = 'g3-journey-demand-to-pickup'; exitCode = 0; outcome = 'PASS'; failureReason = '' },
                   [ordered]@{ name = 'g3-slot-fault-declaration'; exitCode = 1; outcome = 'FAIL'; failureReason = 'PayloadJson is not a property of the row' }) }
           Result = 'FAIL'; Names = @('journeyBlockedOnDeclaredUnknown', 'noScenarioAbortedBeforeItsJudgments', 'g3-slot-fault-declaration', 'PayloadJson is not a property of the row')
           NotNames = @('exactlyOneAcceptedDemandSnapshot', 'g3-journey-demand-to-pickup') }
        @{ Name = 'a runner error carries its own message'; Runner = 'demand-bearing'; Exit = 1
           RunResult = [ordered]@{ status = 'INCONCLUSIVE_RUNNER_ERROR'; assertions = [ordered]@{ protocolAndBuildIdentityBoundToTheSharedBinding = 'NOT_EVALUATED' }
               error = [ordered]@{ type = 'System.Management.Automation.RuntimeException'; message = 'clone-onboard exited with code 128. See C:\x\clone-onboard.log' } }
           Result = 'ERROR'; Names = @('clone-onboard exited with code 128'); NotNames = @() }
        @{ Name = 'no run-result.json at all'; Runner = 'staged'; Exit = 1; RunResult = $null
           Result = 'ERROR'; Names = @('run-result.json'); NotNames = @() }
        @{ Name = 'an unreadable run-result.json'; Runner = 'staged'; Exit = 1; RunResult = '{ "status": "STAGED_SLICE_PA'
           Result = 'ERROR'; Names = @('run-result.json'); NotNames = @() }
        # The json alone is not trusted: a runner that wrote a PASS and then exited non-zero did not pass.
        @{ Name = 'a PASS status with a non-zero exit'; Runner = 'staged'; Exit = 1
           RunResult = [ordered]@{ status = 'STAGED_SLICE_PASS'; error = $null; assertions = [ordered]@{ identityRejections = 'PASS' } }
           Result = 'FAIL'; Names = @('exit 1'); NotNames = @() }
        # Nor is the status alone: an assertion other than PASS under a _PASS status is red.
        @{ Name = 'a PASS status with an assertion that is not PASS'; Runner = 'staged'; Exit = 0
           RunResult = [ordered]@{ status = 'STAGED_SLICE_PASS'; error = $null; assertions = [ordered]@{ identityRejections = 'PASS'; recoveryReplay = 'NOT_EVALUATED' } }
           Result = 'FAIL'; Names = @('recoveryReplay'); NotNames = @('identityRejections') }
        @{ Name = 'a runner that never started'; Runner = 'journey'; Exit = 'NOT_STARTED_DEADLINE'; RunResult = $null
           Result = 'NOT_STARTED_DEADLINE'; Names = @(); NotNames = @() }
    )
    $i = 0
    foreach ($case in $verdictCases) {
        $i++
        $root = if ($case.Exit -is [string]) { '' } else { New-EvidenceRoot "v$i" $case.RunResult }
        $verdict = $null
        $thrown = $null
        try { $verdict = Get-NightlyG3Verdict -Runner $case.Runner -EvidenceRoot $root -ExitCode $case.Exit } catch { $thrown = $_.Exception.Message }
        $text = if ($null -ne $verdict) { (@($verdict.failed) + @($verdict.detail)) -join ' | ' } else { '' }
        $missing = @($case.Names | Where-Object { -not $text.Contains($_) })
        $extra = @($case.NotNames | Where-Object { $text.Contains($_) })
        Check "verdict, $($case.Name): $($case.Result)" `
            ($null -eq $thrown -and $null -ne $verdict -and $verdict.runner -ceq $case.Runner -and $verdict.result -ceq $case.Result -and $missing.Count -eq 0 -and $extra.Count -eq 0) `
            "$thrown result=$(${verdict}?.result) missing=[$($missing -join ', ')] extra=[$($extra -join ', ')] text=$text"
    }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed."
    exit 1
}
Write-Host 'All checks passed.'
