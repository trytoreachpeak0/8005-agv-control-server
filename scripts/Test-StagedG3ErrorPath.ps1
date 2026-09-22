#Requires -Version 7

<#
.SYNOPSIS
    Offline self-check of run-staged-g3.ps1's error path: a run that errors reports its own error, and a
    refused slot-configuration activation keeps the response body.

.DESCRIPTION
    A few seconds, a real loopback listener, no clone, no build, no window.

    control-server#306: on 2026-09-22 a staged run on the moved binding errored, and what it printed was

        Exception calling "IndexOf" with "2" argument(s): "Value cannot be null. (Parameter 'array')"

    from the judgement over the reconnect sequence, which ran on an observation the run never filled in:
    `$x = if (...) { @(...) } else { @() }` assigns $null whenever the branch yields an empty array, because
    a statement's output is enumerated. The error the run had actually hit sat in $runError and was never
    printed. The activation it had issued was refused with a 409 whose body said why, and the evidence
    kept only "Response status code does not indicate success: 409 (Conflict).": pwsh puts an HTTP error's
    body in ErrorDetails, not in the exception message. Nothing green ever reaches either path.

    The checks read the runner's own statements and functions out of its AST and run them, so they test
    the runner, not a copy of it:

      - the two statements that read the reconnect sequence, evaluated with no observation, with an
        observation whose sequence is empty, with one message and with a full handshake: none throws, the
        sequence is never $null, and the index is what it should be;
      - the first statement after the run's try/catch/finally is the call to Write-StagedRunError, so the
        original error is printed and saved before any judgement can throw;
      - a real 409 with a problem+json body, from Invoke-RestMethod against a loopback listener (a stub
        would test the stub: what puts the body into ErrorDetails is the cmdlet itself):
          * Get-HttpErrorObservation reads the status and the body;
          * Write-StagedRunError prints the message and the body, and writes runner-error.json with both;
      - the activation's catch records the refusal through Get-HttpErrorObservation, and the runner's own
        $activationObservation literal, built over that real refusal, carries its status, body and message
        in issueHttpStatusCode / issueHttpResponseBody / issueHttpError (and nulls for an accepted issue);
      - the runner's never-connected guard throws, naming the configuration to check, on a transcript with
        no connection, and stays quiet on one with a connection.

    Exits 1 when any check comes out the other way, and prints every check either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\Test-StagedG3ErrorPath.ps1
#>
[CmdletBinding()]
param(
    [string]$RunnerPath = (Join-Path $PSScriptRoot 'run-staged-g3.ps1')
)

$ErrorActionPreference = 'Stop'

$failures = [System.Collections.Generic.List[string]]::new()
function Check([string]$name, [bool]$ok, [string]$detail) {
    Write-Host ("{0} {1}{2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $(if ($ok) { '' } else { " -- $detail" }))
    if (-not $ok) { $failures.Add($name) }
}

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($RunnerPath, [ref]$tokens, [ref]$parseErrors)
if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) { throw "The runner does not parse: $RunnerPath" }
$topLevel = @($ast.EndBlock.Statements)

# The one top-level assignment to $<Variable>, as text.
function Get-TopLevelAssignment([string]$Variable) {
    $found = @($topLevel | Where-Object {
            $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $_.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $_.Left.VariablePath.UserPath -eq $Variable
        })
    if ($found.Count -ne 1) { throw "Expected one top-level assignment to `$$Variable in the runner, found $($found.Count)." }
    return $found[0].Extent.Text
}

function Get-RunnerFunction([string]$Name) {
    return @($ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name
            }, $true)) | Select-Object -First 1
}

# --- the reconnect sequence --------------------------------------------------------------------------
$sequenceStatement = [scriptblock]::Create((Get-TopLevelAssignment 'supersedingMessages'))
$indexStatement = [scriptblock]::Create((Get-TopLevelAssignment 'supersedingReportIndex'))
$handshake = @(
    'client-to-server:SessionHello', 'server-to-client:SessionAccepted',
    'client-to-server:CapabilitySnapshot', 'client-to-server:SafetyStateSnapshot',
    'client-to-server:RecoveryStateReport', 'server-to-client:DurableAck', 'server-to-client:SessionReadiness')
$sequenceCases = @(
    # The 2026-09-22 run: the observation was filled in, the onboard peer never connected, so the
    # superseding connection had no messages at all. This is the case that threw.
    @{ Name = 'an observation with an empty sequence'; Observation = [ordered]@{ supersedingConnectionMessages = @() }; Count = 0; Index = -1 },
    @{ Name = 'no observation at all'; Observation = $null; Count = 0; Index = -1 },
    @{ Name = 'a one-message sequence'; Observation = [ordered]@{ supersedingConnectionMessages = @('client-to-server:RecoveryStateReport') }; Count = 1; Index = 0 },
    @{ Name = 'a full handshake'; Observation = [ordered]@{ supersedingConnectionMessages = $handshake }; Count = 7; Index = 4 }
)
foreach ($case in $sequenceCases) {
    $runtimeObservation = $case.Observation
    $supersedingMessages = 'unset'
    $supersedingReportIndex = 'unset'
    $thrown = $null
    try {
        . $sequenceStatement
        . $indexStatement
    } catch { $thrown = $_.Exception.Message }
    Check "reconnect sequence, $($case.Name): the judgement does not throw" ($null -eq $thrown) "$thrown"
    if ($null -ne $thrown) { continue }
    Check "reconnect sequence, $($case.Name): the sequence is not `$null" ($null -ne $supersedingMessages) 'it is $null'
    Check "reconnect sequence, $($case.Name): $($case.Count) message(s), report at $($case.Index)" `
        (@($supersedingMessages).Count -eq $case.Count -and $supersedingReportIndex -eq $case.Index) `
        "count $(@($supersedingMessages).Count), index $supersedingReportIndex"
}

# --- the original error is surfaced before any judgement -------------------------------------------
$runTry = @($topLevel | Where-Object {
        $_ -is [System.Management.Automation.Language.TryStatementAst] -and
        @($_.CatchClauses | Where-Object { $_.Body.Extent.Text -match '\$runError\s*=\s*\$_' }).Count -eq 1
    })
if ($runTry.Count -ne 1) { throw "Expected one top-level try whose catch sets `$runError, found $($runTry.Count)." }
$next = $topLevel[[array]::IndexOf($topLevel, $runTry[0]) + 1]
$nextCommand = if ($next -is [System.Management.Automation.Language.PipelineAst] -and
    $next.PipelineElements[0] -is [System.Management.Automation.Language.CommandAst]) {
    $next.PipelineElements[0].GetCommandName()
} else { $null }
Check 'the first statement after the run''s try is Write-StagedRunError' ($nextCommand -eq 'Write-StagedRunError') `
    "it is: $(($next.Extent.Text -split "`n")[0].Trim())"

$writeError = Get-RunnerFunction 'Write-StagedRunError'
$httpError = Get-RunnerFunction 'Get-HttpErrorObservation'
Check 'the runner defines Write-StagedRunError' ($null -ne $writeError) 'not found'
Check 'the runner defines Get-HttpErrorObservation' ($null -ne $httpError) 'not found'

$activationCatches = @($ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.CatchClauseAst] -and
            $node.Body.Extent.Text -match '\$activationIssueError\s*='
        }, $true))
Check 'the activation''s catch records the refusal through Get-HttpErrorObservation' `
    ($activationCatches.Count -eq 1 -and $activationCatches[0].Body.Extent.Text -match '\$activationIssueError\s*=\s*Get-HttpErrorObservation\b') `
    "$($activationCatches.Count) catch(es): $(@($activationCatches | ForEach-Object { $_.Body.Extent.Text.Trim() }) -join ' | ')"

# --- the onboard peer never connected ----------------------------------------------------------------
# The runner's own `if`, run on a transcript with no connection (the 2026-09-22 shape: one proxy-listening
# line) and on one with a connection. Only a peer that failed to start reaches the first, so no green run
# would notice the guard going quiet.
$neverConnectedGuards = @($ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.IfStatementAst] -and
            $node.Clauses[0].Item1.Extent.Text -match "'connection-opened'" -and
            $node.Clauses[0].Item2.Extent.Text -match '\bthrow\b'
        }, $true))
Check 'the runner has one never-connected guard' ($neverConnectedGuards.Count -eq 1) "$($neverConnectedGuards.Count) found"
if ($neverConnectedGuards.Count -eq 1) {
    $guard = [scriptblock]::Create($neverConnectedGuards[0].Extent.Text)
    $onboard = [pscustomobject]@{ HasExited = $false }
    $onboardConfig = 'C:\stage\publish\onboard-hmi\appsettings.json'
    $logsRoot = 'C:\evidence\logs'
    $listening = [pscustomobject]@{ event = 'proxy-listening'; listenPort = 58215 }
    $opened = [pscustomobject]@{ event = 'connection-opened'; connectionId = 1 }
    foreach ($guardCase in @(
            @{ Name = 'only proxy-listening'; Events = @($listening); Throws = $true },
            @{ Name = 'an empty transcript'; Events = @(); Throws = $true },
            @{ Name = 'a connection'; Events = @($listening, $opened); Throws = $false })) {
        $events = $guardCase.Events
        $thrown = $null
        try { . $guard } catch { $thrown = $_.Exception.Message }
        if ($guardCase.Throws) {
            Check "never-connected guard, $($guardCase.Name): throws, naming the configuration" `
                ($thrown -like '*never connected*' -and $thrown -like "*$onboardConfig*") "$thrown"
        } else {
            Check "never-connected guard, $($guardCase.Name): does not throw" ($null -eq $thrown) "$thrown"
        }
    }
}

# --- a real 409 --------------------------------------------------------------------------------------
if ($null -ne $writeError -and $null -ne $httpError) {
    . ([scriptblock]::Create($httpError.Extent.Text))
    . ([scriptblock]::Create($writeError.Extent.Text))

    $refusal = '{"type":"about:blank","title":"No session for this vehicle","status":409,"detail":"No session has ever been established for ''AGV-8005-STAGED-G3-01''."}'
    $port = Get-Random -Minimum 49200 -Maximum 49900
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add("http://localhost:$port/")
    $listener.Start()
    $server = Start-ThreadJob -ArgumentList $listener, $refusal -ScriptBlock {
        param($l, $body)
        while ($l.IsListening) {
            try { $ctx = $l.GetContext() } catch { break }
            $bytes = [Text.Encoding]::UTF8.GetBytes($body)
            $ctx.Response.StatusCode = 409
            $ctx.Response.ContentType = 'application/problem+json'
            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
            $ctx.Response.Close()
        }
    }
    $evidenceRoot = Join-Path ([IO.Path]::GetTempPath()) ("staged-g3-error-path-" + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $evidenceRoot | Out-Null
    try {
        $refused = $null
        try {
            Invoke-RestMethod -Method Post -Uri "http://localhost:$port/api/governance/v1/slot-configuration-activations" `
                -ContentType 'application/json' -Body '{}' -TimeoutSec 10 | Out-Null
        } catch { $refused = $_ }
        Check 'the listener refused with an HTTP error' ($null -ne $refused) 'no error'
        # The premise this whole change rests on: the body is not in the message.
        Check 'premise: the exception message does not carry the body' `
            ($null -ne $refused -and $refused.Exception.Message -notlike '*No session for this vehicle*') "$(${refused}?.Exception.Message)"

        # The activation observation's three issueHttp* entries, from the runner's own literal, over the same
        # real refusal. A renamed key reads $null through `?.` and nothing else notices: the evidence file
        # would lose the 409 body again, which is the symptom this ticket started from.
        $activationStatement = [scriptblock]::Create((Get-TopLevelAssignment 'activationObservation'))
        foreach ($activationCase in @(
                @{ Name = 'a refused issue'; Error = (Get-HttpErrorObservation $refused) },
                @{ Name = 'an accepted issue'; Error = $null })) {
            $activationIssueError = $activationCase.Error
            $activationResponse = $null
            $slotModelVersionId = 'staged-model'
            $activationCommandsSent = @()
            $activationResultsReported = @()
            $activationDbRows = @()
            $activeConfigurationDbRows = @()
            $activationObservation = $null
            $thrown = $null
            try { . $activationStatement } catch { $thrown = $_.Exception.Message }
            Check "activation observation, $($activationCase.Name): builds" ($null -eq $thrown -and $null -ne $activationObservation) "$thrown"
            if ($null -ne $thrown -or $null -eq $activationObservation) { continue }
            if ($null -ne $activationCase.Error) {
                Check 'activation observation, a refused issue: issueHttpStatusCode 409' `
                    ($activationObservation.issueHttpStatusCode -eq 409) "$($activationObservation.issueHttpStatusCode)"
                Check 'activation observation, a refused issue: issueHttpResponseBody carries the body' `
                    ($activationObservation.issueHttpResponseBody -like '*No session for this vehicle*') "$($activationObservation.issueHttpResponseBody)"
                Check 'activation observation, a refused issue: issueHttpError carries the message' `
                    ($activationObservation.issueHttpError -like '*409*') "$($activationObservation.issueHttpError)"
            } else {
                Check 'activation observation, an accepted issue: the three issueHttp* entries are null' `
                    ($null -eq $activationObservation.issueHttpStatusCode -and $null -eq $activationObservation.issueHttpResponseBody -and
                        $null -eq $activationObservation.issueHttpError) `
                    "$($activationObservation.issueHttpStatusCode) / $($activationObservation.issueHttpResponseBody) / $($activationObservation.issueHttpError)"
            }
        }

        $observation = Get-HttpErrorObservation $refused
        Check 'Get-HttpErrorObservation: status 409' ($observation.statusCode -eq 409) "$($observation.statusCode)"
        Check 'Get-HttpErrorObservation: the body' ($observation.responseBody -like '*No session for this vehicle*') "$($observation.responseBody)"
        Check 'Get-HttpErrorObservation: the cmdlet''s own message' ($observation.message -like '*409*') "$($observation.message)"

        $printed = (Write-StagedRunError -ErrorRecord $refused -EvidenceRoot $evidenceRoot 3>&1 6>&1 | Out-String)
        Check 'Write-StagedRunError prints the message' ($printed -like '*409*') $printed
        Check 'Write-StagedRunError prints the body' ($printed -like '*No session for this vehicle*') $printed
        $saved = Join-Path $evidenceRoot 'runner-error.json'
        $savedJson = if (Test-Path -LiteralPath $saved) { Get-Content -LiteralPath $saved -Raw } else { '' }
        Check 'Write-StagedRunError writes runner-error.json with the body' `
            ($savedJson -like '*No session for this vehicle*' -and $savedJson -like '*409*') "$savedJson"

        # And a run with no error prints nothing and writes nothing.
        $quietRoot = Join-Path $evidenceRoot 'quiet'
        New-Item -ItemType Directory -Path $quietRoot | Out-Null
        $quiet = (Write-StagedRunError -ErrorRecord $null -EvidenceRoot $quietRoot 3>&1 6>&1 | Out-String)
        Check 'Write-StagedRunError with no error: silent, no file' `
            ([string]::IsNullOrWhiteSpace($quiet) -and -not (Test-Path -LiteralPath (Join-Path $quietRoot 'runner-error.json'))) $quiet
    } finally {
        $listener.Stop()
        $server | Wait-Job -Timeout 5 | Out-Null
        $server | Remove-Job -Force
        Remove-Item -LiteralPath $evidenceRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failures.Count -gt 0) {
    Write-Host "StagedG3ErrorPath self-check: $($failures.Count) check(s) came out the other way."
    exit 1
}
Write-Host 'StagedG3ErrorPath self-check: every check as expected.'
