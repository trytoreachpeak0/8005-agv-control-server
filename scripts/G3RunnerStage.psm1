#Requires -Version 7

# The four G3 runners' stage handling (control-server#585). Stub: the self-check comes first.

function Get-G3EvidenceSparseCheckoutArguments { throw 'Not implemented (control-server#585).' }

function Assert-G3FreeDiskSpace {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][double]$MinFreeGiB)
    throw 'Not implemented (control-server#585).'
}

function Complete-G3RunnerStage {
    param(
        [Parameter(Mandatory)][string]$StageRoot,
        [Parameter(Mandatory)][bool]$Passed,
        [int]$Attempts = 3,
        [double]$RetrySeconds = 5
    )
    throw 'Not implemented (control-server#585).'
}

Export-ModuleMember -Function Get-G3EvidenceSparseCheckoutArguments, Assert-G3FreeDiskSpace, Complete-G3RunnerStage
