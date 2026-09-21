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

function Invoke-ParallelProductUninstaller {
    <#
        .SYNOPSIS
            Runs the product uninstaller for the parallel service; throws unless it positively
            confirmed success.

        .DESCRIPTION
            control-server#262 S1 re-review, M1. The uninstall's "the service step failed, so
            nothing else runs" held only when the product script THREW. A script can also fail by
            exit 1, by Write-Error under ErrorActionPreference Continue, or by a native command's
            exit code -- and in each of those the call returns normally, so the sequence carried on
            (the reviewer measured eight V2 directories deleted after an 'exit 1'). It was safe
            only because Uninstall-ControlServerLocal.ps1 happens to throw on every failure it
            has today.

            So this does not try to recognise failure. It requires success to be shown, three ways,
            all of which a failure that stops the script early cannot produce:

              1. $LASTEXITCODE is 0 after the call (reset to 0 before it). Catches 'exit N' and a
                 failing native command that was the script's last word.
              2. The result JSON the product script writes as its LAST act -- after the service,
                 install root and data root are dealt with -- exists at the path this call chose,
                 did not exist before the call, and says result PASS for this service name, with
                 serviceRemoved true whenever serviceExisted.
              3. The service is gone: Get-Service no longer finds it.

            Measured before choosing this (evidence review3-failure-surface.txt): $? stays True
            after Write-Error under Continue, and -ErrorVariable collects the errors the product
            script silences on purpose (Get-Service -ErrorAction SilentlyContinue), so it would
            fail every ordinary uninstall. Neither is a usable signal; the result file is.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $UninstallerPath,
        [Parameter(Mandatory = $true)][string] $ServiceName,
        [Parameter(Mandatory = $true)][string] $InstallRoot,
        [Parameter(Mandatory = $true)][string] $DataRoot,
        [Parameter(Mandatory = $true)][string] $ResultPath
    )
    if (Test-Path -LiteralPath $ResultPath) {
        throw "The product uninstaller's result path already exists ($ResultPath); a stale PASS there could not be told from this run's."
    }
    $global:LASTEXITCODE = 0
    $output = & $UninstallerPath -ServiceName $ServiceName -InstallRoot $InstallRoot `
        -DataRoot $DataRoot -ResultPath $ResultPath -ConfirmUninstall
    $exitCode = $global:LASTEXITCODE
    foreach ($line in @($output)) { Write-Host "    | $line" }

    if ($exitCode -ne 0) {
        throw "The product uninstaller returned exit code $exitCode; treating the service step as failed."
    }
    if (-not (Test-Path -LiteralPath $ResultPath -PathType Leaf)) {
        throw "The product uninstaller returned without writing its result file ($ResultPath). It writes that file only on reaching its end, so it stopped early; treating the service step as failed."
    }
    $result = Get-Content -Raw -LiteralPath $ResultPath -Encoding utf8 | ConvertFrom-Json -AsHashtable
    $problems = @()
    if ([string] $result['result'] -cne 'PASS') { $problems += "result is '$($result['result'])', not 'PASS'" }
    if ([string] $result['serviceName'] -cne $ServiceName) { $problems += "serviceName is '$($result['serviceName'])', not '$ServiceName'" }
    if ($result['serviceExisted'] -eq $true -and $result['serviceRemoved'] -ne $true) { $problems += 'the service existed and was not removed' }
    if ($problems.Count -gt 0) {
        throw "The product uninstaller's result file does not confirm success: $($problems -join '; ')."
    }
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        throw "The product uninstaller reported PASS but the service '$ServiceName' still exists."
    }
}

Export-ModuleMember -Function @('Get-MvpFingerprint', 'Assert-MvpUntouched', 'Format-MvpFingerprint', 'Invoke-ParallelProductUninstaller')
