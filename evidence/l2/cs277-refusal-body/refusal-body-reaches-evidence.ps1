#Requires -Version 7
# One-off for control-server#277: a command the real fake onboard refuses must reach the evidence
# with its response body. A blank commandId is refused by /connection as INVALID_ARGUMENT (400).
param([Parameter(Mandatory)]$Context)
$ErrorActionPreference = 'Stop'
$Context.Journal.Note('Sending a reconnect with a blank commandId; the fake onboard must refuse it.')
$null = $Context.Onboard.Command('Put', 'connection', @{ commandId = ' '; connected = $true })
throw 'Unreachable: the fake onboard accepted a blank commandId.'
