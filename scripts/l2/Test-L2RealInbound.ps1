#Requires -Version 7

<#
.SYNOPSIS
    Self-check for L2RealOnboard.psm1's Get-L2RealInbound: an inbox row with no answer, with exactly one answer, and
    with an answer spanning two lines must each read back as the answer it holds.

.DESCRIPTION
    Pure input, no rig and no database, a second. The module's one database read (Invoke-L2Query) is replaced inside
    the module with constructed rows shaped like the server's ProtocolInbox.

    The no-answer row is the case that matters. `$x = if (...) { @(...) } else { @() }` sends the array down the
    pipeline, and an empty array arrives as nothing at all: under StrictMode `.Count` on that throws.
    LoadCompensationRequested is never answered in its own inbox row -- the server's reply is a LoadCompensationCommand
    in the outbox, or a LoadCompensationRejected only on refusal -- so real-onboard-compensate-then-reconnect's probe
    threw on every poll, Wait-L2Condition logged "(nothing)" and the scenario timed out with the compensation already
    reconciled (control-server#154, found by onboard-hmi#112). The one-answer row guards the other half of the same
    unwrapping.

    Exits 1 when any case comes out the other way, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-L2RealInbound.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2RealOnboard.psm1') -Force
$realOnboard = Get-Module L2RealOnboard

function New-InboxRow([string]$MessageId, [string[]]$Answers) {
    [pscustomobject]@{
        MessageId         = $MessageId
        RequestJson       = (@{ messageType = 'LoadCompensationRequested'; sessionGeneration = 2; payload = @{ demandId = 'D-1' } } |
            ConvertTo-Json -Compress)
        FirstResponseJson = $Answers.Count -eq 0 ? $null : ($Answers -join "`n")
        ReceivedAt        = '2026-09-19T01:00:42.2990000+00:00'
    }
}

function New-Answer([string]$MessageType) {
    return (@{ messageType = $MessageType; payload = @{ reasonCode = 'X' } } | ConvertTo-Json -Compress)
}

$cases = @(
    @{ Name = 'no answer yet'; Answers = @(); Response = ''; Types = '' }
    @{ Name = 'exactly one answer'; Answers = @((New-Answer 'LoadCompensationRejected')); Response = 'LoadCompensationRejected'
       Types = 'LoadCompensationRejected' }
    @{ Name = 'an answer on two lines'; Answers = @((New-Answer 'DurableAck'), (New-Answer 'SessionReadiness')); Response = 'DurableAck'
       Types = 'DurableAck,SessionReadiness' }
)

$wrong = 0
foreach ($case in $cases) {
    $row = New-InboxRow "m-$($cases.IndexOf($case))" $case.Answers
    & $realOnboard {
        param($row)
        $script:stubRows = @($row)
        function script:Invoke-L2Query([object]$Connection, [string]$Sql) { return , @($script:stubRows) }
    } $row
    try {
        $messages = Get-L2RealInbound ([pscustomobject]@{}) 'LoadCompensationRequested'
        $message = $messages[0]
        $actual = "Response='$($message.Response)' Types='$($message.ResponseTypes -join ',')' Answers=$(@($message.Answers).Count)"
        $asExpected = $messages.Count -eq 1 -and $message.Response -ceq $case.Response -and
            ($message.ResponseTypes -join ',') -ceq $case.Types -and @($message.Answers).Count -eq $case.Answers.Count
    } catch {
        $actual = "threw: $($_.Exception.Message)"
        $asExpected = $false
    }
    if (-not $asExpected) { $wrong++ }
    Write-Host ("{0}  {1} -> {2}" -f $(if ($asExpected) { 'ok  ' } else { 'BAD ' }), $case.Name, $actual)
}

if ($wrong -gt 0) {
    Write-Host "Get-L2RealInbound self-check: $wrong of $($cases.Count) cases came out the wrong way."
    exit 1
}
Write-Host "Get-L2RealInbound self-check: all $($cases.Count) cases as expected."
