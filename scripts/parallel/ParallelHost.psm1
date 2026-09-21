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

function Get-ParallelProductUninstallerPath {
    <#
        .SYNOPSIS
            The product uninstaller to use for this instance: the first of three that exists.

        .DESCRIPTION
            Three places, in order. The installed package's own copy matches what is installed;
            but a first install that failed after the product installer succeeded has no package
            root -- the package was still in a staging directory the installer's finally block
            removed -- and that half-installed case is exactly the one the uninstaller is the
            recovery for. So the control host ships the product uninstaller beside the parallel
            scripts too ($ScriptRoot).

            Here rather than in Uninstall-ParallelInstanceLocal.ps1 so that the product script's
            name does not appear in the uninstaller at all. A regression guard in the self-test
            looks for it there, among other common ways of calling the product script directly;
            it is not a proof -- aliases, .NET process APIs and the like get past it (S1 re-review,
            round 4). The guarantee is this function's positive confirmation, not the scan.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Layout,
        [Parameter(Mandatory = $true)][string] $ScriptRoot
    )
    $candidates = @(
        (Join-Path $Layout.PackageRoot 'scripts\Uninstall-ControlServerLocal.ps1')
        (Join-Path $Layout.PreviousRoot 'scripts\Uninstall-ControlServerLocal.ps1')
        (Join-Path $ScriptRoot 'Uninstall-ControlServerLocal.ps1')
    )
    $found = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $found) { throw "the product uninstaller was not found at any of: $($candidates -join '; ')" }
    # The premise Invoke-ParallelProductUninstaller's confirmation rests on, checked on THIS copy --
    # the one about to run, which is usually the installed package's or the previous generation's,
    # not the repository's the self-test reads (S1 re-review, round 4). Same check, same limits.
    $broken = @(Test-ParallelProductUninstallerPremise -Source ([IO.File]::ReadAllText($found)))
    if ($broken.Count -gt 0) {
        throw "the product uninstaller at $found breaks the premise the success check rests on: $($broken -join '; ')"
    }
    return $found
}

function Test-ParallelProductUninstallerPremise {
    <#
        .SYNOPSIS
            The reasons a product uninstaller's source breaks the premise of the positive
            confirmation; empty when none of the recognised ones apply.

        .DESCRIPTION
            Invoke-ParallelProductUninstaller treats a fresh PASS result file as success. One failure
            shape passes that: a non-terminating error, the script carrying on, removing the service
            and writing PASS. It cannot happen while the product script sets ErrorActionPreference
            Stop as its first statement and writes PASS once, at its end. This checks those two facts.

            A REGRESSION GUARD, NOT A PROOF. It recognises four ways of breaking them: a first
            statement other than the Stop assignment, 'PASS' written other than exactly once, 'PASS'
            outside the last three top-level statements, and a trap. It does NOT see a later
            statement setting Continue again, $PSDefaultParameterValues, a try/catch that swallows an
            error, or 'PA' + 'SS' built from pieces (S1 re-review, round 4, measured).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Source)
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Source, [ref]$null, [ref]$null)
    $problems = [System.Collections.Generic.List[string]]::new()
    $top = @($ast.EndBlock.Statements)
    if ($top.Count -eq 0 -or $top[0].Extent.Text -cne "`$ErrorActionPreference = 'Stop'") {
        $problems.Add("the first statement is not `$ErrorActionPreference = 'Stop': $(if ($top.Count) { $top[0].Extent.Text })")
    }
    $pass = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $n.Value -ceq 'PASS' }, $true))
    if ($pass.Count -ne 1) {
        $problems.Add("'PASS' appears $($pass.Count) times")
    } else {
        $index = -1
        for ($i = 0; $i -lt $top.Count; $i++) {
            if ($top[$i].Extent.StartOffset -le $pass[0].Extent.StartOffset -and $top[$i].Extent.EndOffset -ge $pass[0].Extent.EndOffset) { $index = $i }
        }
        if ($index -lt $top.Count - 3) { $problems.Add("'PASS' is in top-level statement $index of $($top.Count), not among the last three") }
    }
    if (@($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.TrapStatementAst] }, $true)).Count -gt 0) { $problems.Add('it has a trap') }
    return $problems
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

            One shape still passes all three: the product script hits a non-terminating error,
            carries on, removes the service and writes PASS. It cannot today, because the product
            script sets ErrorActionPreference Stop as its first statement and writes PASS once, at
            its end. Get-ParallelProductUninstallerPath checks those two facts on the copy that is
            about to run (Test-ParallelProductUninstallerPremise), and the self-test on the
            repository's copy -- both against four ways of breaking them, not all of them (S1
            re-review, round 4); re-read the product script when it changes.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $UninstallerPath,
        [Parameter(Mandatory = $true)][string] $ServiceName,
        [Parameter(Mandatory = $true)][string] $InstallRoot,
        [Parameter(Mandatory = $true)][string] $DataRoot,
        [Parameter(Mandatory = $true)][string] $ResultDirectory
    )
    # "Written by this run" rests on a name nobody can predict: a timestamp to the second was
    # guessable, so another process could have put a PASS there during the call (S1 re-review,
    # round 3). The GUID makes the pre-existence check below unreachable in practice; it stays
    # because it costs nothing and says so if the impossible happens.
    New-Item -ItemType Directory -Path $ResultDirectory -Force | Out-Null
    $ResultPath = Join-Path $ResultDirectory ("uninstall-{0:yyyyMMdd-HHmmss}-{1}.json" -f (Get-Date), [guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $ResultPath) {
        throw "The product uninstaller's result path already exists ($ResultPath); a stale PASS there could not be told from this run's."
    }
    Write-Host "    product uninstaller $UninstallerPath, result $ResultPath"
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

Export-ModuleMember -Function @('Get-MvpFingerprint', 'Assert-MvpUntouched', 'Format-MvpFingerprint',
    'Get-ParallelProductUninstallerPath', 'Test-ParallelProductUninstallerPremise', 'Invoke-ParallelProductUninstaller')
