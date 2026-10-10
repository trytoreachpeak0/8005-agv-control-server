#Requires -Version 7
# Stub for the test commit (control-server#567): always runs.
[CmdletBinding()]
param([string[]]$ChangedPath = @(), [switch]$NotAPullRequest, [string]$GitHubOutput)
[pscustomobject]@{ Run = $true; Reason = 'stub' }
