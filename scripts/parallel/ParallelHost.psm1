#Requires -Version 7

<#
    Host-side helpers shared by Install-ParallelInstanceLocal.ps1 and
    Uninstall-ParallelInstanceLocal.ps1. Kept out of ParallelInstance.psm1 on purpose: that one
    is pure and is what the self-test exercises; everything here reads the machine.
#>

Set-StrictMode -Version 3.0

$script:ProductionServiceName = '8005 AGV ControlServer'

function Get-MvpFingerprint {
    <#
        .SYNOPSIS
            Enough of the MVP service to notice if a parallel operation moved it.

        .DESCRIPTION
            Existence, status, start type, binary path and process id. Compared before and
            after every install, rollback and uninstall of the parallel instance, because "I
            did not touch it" is a claim worth having evidence for on a machine serving
            customers.

            What this does NOT see: the MVP's files on disk. It compares a service, not a
            directory tree -- the *.incoming-* cleanup that deleted any instance's staging
            directory (control-server#262 review, finding 1) would have passed it. Guards on
            paths live in ParallelInstance.psm1, not here.
    #>
    [CmdletBinding()]
    param()
    $service = Get-Service -Name $script:ProductionServiceName -ErrorAction SilentlyContinue
    if (-not $service) { return [ordered]@{ present = $false } }
    $wmi = Get-CimInstance -ClassName Win32_Service -Filter "Name='$script:ProductionServiceName'" -ErrorAction SilentlyContinue
    return [ordered]@{
        present = $true
        status = [string] $service.Status
        startType = [string] $service.StartType
        pathName = $wmi ? [string] $wmi.PathName : '(unavailable)'
        processId = $wmi ? [int] $wmi.ProcessId : 0
    }
}

function Assert-MvpUntouched {
    <#
        .SYNOPSIS
            Throws when any fingerprint field changed.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Before,
        [Parameter(Mandatory = $true)] $After
    )
    $diff = @()
    foreach ($key in $Before.Keys) {
        if ("$($Before[$key])" -cne "$($After[$key])") {
            $diff += "$key : '$($Before[$key])' -> '$($After[$key])'"
        }
    }
    if ($diff.Count -gt 0) {
        throw ("The MVP service changed during this operation, which must never happen: " + ($diff -join '; '))
    }
}

function Format-MvpFingerprint {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Fingerprint)
    return (($Fingerprint.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ')
}

Export-ModuleMember -Function @('Get-MvpFingerprint', 'Assert-MvpUntouched', 'Format-MvpFingerprint')
