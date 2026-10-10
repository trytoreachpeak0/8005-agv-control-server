#Requires -Version 7

# The nightly G3 (.github/workflows/g3.yml, control-server#582). What the workflow and Invoke-NightlyG3.ps1 decide is
# kept here, so that scripts/Test-NightlyG3.ps1 can check it offline: no runner here starts a process or a window.

function Get-NightlyG3Verdict {
    param(
        [Parameter(Mandatory)][string]$Runner,
        [Parameter(Mandatory)][AllowEmptyString()][string]$EvidenceRoot,
        [Parameter(Mandatory)][AllowNull()][object]$ExitCode
    )
    return $null
}

Export-ModuleMember -Function Get-NightlyG3Verdict
