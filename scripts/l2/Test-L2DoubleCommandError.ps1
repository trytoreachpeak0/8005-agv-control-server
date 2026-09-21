#Requires -Version 7

<#
.SYNOPSIS
    Self-check for L2Double.Command: a refused command carries the double's response body into the
    exception it throws, and the 409 retry is unchanged.

.DESCRIPTION
    A real HTTP listener on loopback, a few seconds. No rig, no build.

    The orchestrator writes a failed run's exception message into the evidence. pwsh keeps an HTTP
    error's response body in ErrorDetails, not in that message, so until control-server#277 a refused
    command reached the evidence as a bare "409 (Conflict)" -- and the body was the only place the
    double said why it refused. #277 got two such reds from load-cancelled-before-sublot, where the
    fake onboard answered CONNECTION_CHANGE_FAILED with the exception in `detail`, and neither
    sample's evidence had it.

    The double here is a real listener rather than a stub of Invoke-RestMethod on purpose: what puts
    the body into ErrorDetails is the cmdlet itself, so a stub would test the stub.

    Cases:

      - 409 with a JSON body -> the message carries the double, the method, the path, reasonCode and
        detail, and still starts with the cmdlet's own message; the InnerException is the original
        HttpResponseException with status 409;
      - 400 with a body (not a 409, so never retried) -> the body is carried too;
      - an empty body -> says "(empty)" rather than ending mid-sentence;
      - a body that is not JSON -> carried as it came;
      - a double that requires expectedRevision, answering 409 twice then 200 -> the result is
        returned, after exactly three attempts (the retry this change must not have touched);
      - the same double answering 409 every time -> exactly five attempts, then the body is carried.

    Exits 1 when any case comes out the other way, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-L2DoubleCommandError.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'L2.psm1') -Force

$failures = [System.Collections.Generic.List[string]]::new()
function Check([string]$name, [bool]$ok, [string]$detail) {
    Write-Host ("{0} {1}{2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $(if ($ok) { '' } else { " -- $detail" }))
    if (-not $ok) { $failures.Add($name) }
}

# One listener for every case. Snapshot requests always answer; every other request takes the next
# planned response off the queue and is counted.
$port = Get-Random -Minimum 49200 -Maximum 49900
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$port/")
$listener.Start()
$plan = [System.Collections.Concurrent.ConcurrentQueue[object]]::new()
$commands = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()
$server = Start-ThreadJob -ArgumentList $listener, $plan, $commands -ScriptBlock {
    param($l, $plan, $commands)
    while ($l.IsListening) {
        try { $ctx = $l.GetContext() } catch { break }
        $path = $ctx.Request.Url.AbsolutePath
        if ($path -like '*/snapshot') {
            $status = 200; $body = '{"runId":"run-1","revision":3}'
        } else {
            $commands.Enqueue("$($ctx.Request.HttpMethod) $path")
            $next = $null
            if (-not $plan.TryDequeue([ref]$next)) { $next = @{ Status = 500; Body = '{"unplanned":true}' } }
            $status = $next.Status; $body = $next.Body
        }
        $bytes = [Text.Encoding]::UTF8.GetBytes($body)
        $ctx.Response.StatusCode = $status
        $ctx.Response.ContentType = 'application/json'
        $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $ctx.Response.Close()
    }
}

function Reset-Plan([object[]]$responses) {
    $drop = $null
    while ($plan.TryDequeue([ref]$drop)) { }
    while ($commands.TryDequeue([ref]$drop)) { }
    foreach ($r in $responses) { $plan.Enqueue($r) }
}

function Invoke-Refused($double, [string]$method, [string]$path) {
    try { $null = $double.Command($method, $path, @{ connected = $true }); return $null }
    catch { return $_ }
}

$fake = New-L2Double -Name 'fake-onboard' -BaseUrl "http://localhost:$port"
$strict = New-L2Double -Name 'slots-simulator' -BaseUrl "http://localhost:$port" -Prefix 'api/v1' -RequireExpectedRevision

try {
    # --- 409 with a body -------------------------------------------------------------------------
    Reset-Plan @(@{ Status = 409; Body = '{"commandId":"c1","reasonCode":"CONNECTION_CHANGE_FAILED","detail":"IOException: the peer reset the socket"}' })
    $e = Invoke-Refused $fake 'Put' 'connection'
    $message = if ($e) { $e.Exception.Message } else { '' }
    Check '409: the message carries reasonCode and detail' `
        ($message -like '*"reasonCode":"CONNECTION_CHANGE_FAILED"*' -and $message -like '*IOException: the peer reset the socket*') $message
    Check '409: the message names the double, the method and the path' ($message -like '*`[fake-onboard PUT connection`]*') $message
    $inner = if ($e) { $e.Exception.InnerException } else { $null }
    $innerIsHttp = $null -ne $inner -and $inner.GetType().FullName -eq 'Microsoft.PowerShell.Commands.HttpResponseException'
    # Only meaningful once the inner exception is the HTTP one: compared against any other inner
    # exception this passed on a broken build, because a wrapper starts with whatever it wraps.
    Check '409: the message still starts with the cmdlet''s own message' `
        ($innerIsHttp -and $message.StartsWith($inner.Message)) $message
    Check '409: the original HttpResponseException is the InnerException, status 409' `
        ($null -ne $inner -and $inner.GetType().FullName -eq 'Microsoft.PowerShell.Commands.HttpResponseException' -and
            [int]$inner.Response.StatusCode -eq 409) "$(${inner}?.GetType()?.FullName)"
    Check '409: a double without expectedRevision is not retried' ($commands.Count -eq 1) "$($commands.Count) attempts"

    # --- 400 with a body -------------------------------------------------------------------------
    Reset-Plan @(@{ Status = 400; Body = '{"reasonCode":"INVALID_ARGUMENT","detail":"commandId is blank"}' })
    $e = Invoke-Refused $fake 'Post' '/load-cancellations'
    $message = if ($e) { $e.Exception.Message } else { '' }
    Check '400: the body is carried too' ($message -like '*"reasonCode":"INVALID_ARGUMENT"*' -and
        $message -like '*`[fake-onboard POST load-cancellations`]*') $message

    # --- empty body ------------------------------------------------------------------------------
    Reset-Plan @(@{ Status = 409; Body = '' })
    $e = Invoke-Refused $fake 'Put' 'connection'
    $message = if ($e) { $e.Exception.Message } else { '' }
    Check 'empty body: says (empty)' ($message -like '*Response body: (empty)') $message

    # --- body that is not JSON -------------------------------------------------------------------
    Reset-Plan @(@{ Status = 409; Body = 'plain text refusal' })
    $e = Invoke-Refused $fake 'Put' 'connection'
    $message = if ($e) { $e.Exception.Message } else { '' }
    Check 'non-JSON body: carried as it came' ($message -like '*Response body: plain text refusal') $message

    # --- the retry: 409, 409, 200 ----------------------------------------------------------------
    Reset-Plan @(
        @{ Status = 409; Body = '{"reasonCode":"REVISION_CONFLICT"}' },
        @{ Status = 409; Body = '{"reasonCode":"REVISION_CONFLICT"}' },
        @{ Status = 200; Body = '{"accepted":true}' })
    $result = $null; $retryError = $null
    try { $result = $strict.Command('Put', 'doors/1', @{ open = $true }) } catch { $retryError = $_ }
    Check 'retry: 409, 409, 200 returns the result' ($null -eq $retryError -and $null -ne $result -and $result.accepted -eq $true) "$retryError"
    Check 'retry: after exactly three attempts' ($commands.Count -eq 3) "$($commands.Count) attempts"

    # --- the retry: 409 every time ---------------------------------------------------------------
    Reset-Plan @(1..6 | ForEach-Object { @{ Status = 409; Body = "{`"reasonCode`":`"REVISION_CONFLICT`",`"attempt`":$_}" } })
    $e = Invoke-Refused $strict 'Put' 'doors/1'
    $message = if ($e) { $e.Exception.Message } else { '' }
    Check 'retry: 409 every time gives up after exactly five attempts' ($commands.Count -eq 5) "$($commands.Count) attempts"
    Check 'retry: and carries the last refusal''s body' ($message -like '*"attempt":5*' -and
        $message -like '*`[slots-simulator PUT doors/1`]*') $message
} finally {
    $listener.Stop()
    $server | Wait-Job -Timeout 5 | Out-Null
    $server | Remove-Job -Force
}

if ($failures.Count -gt 0) {
    Write-Host "L2DoubleCommandError self-check: $($failures.Count) case(s) came out the other way."
    exit 1
}
Write-Host 'L2DoubleCommandError self-check: all 12 cases as expected.'
