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

function Find-OverlayMismatch {
    # Every scalar or array leaf of the overlay, compared with what was read back (keys ignoring
    # case, as .NET configuration reads them; values as JSON, so 26 and 26L are one value).
    param($Overlay, $Actual, [string] $Path)
    foreach ($key in @($Overlay.Keys)) {
        $expected = $Overlay[$key]
        $found = $null
        if ($Actual -is [System.Collections.IDictionary]) {
            foreach ($candidate in @($Actual.Keys)) {
                if ([string]::Equals([string] $candidate, [string] $key, [StringComparison]::OrdinalIgnoreCase)) { $found = $Actual[$candidate] }
            }
        }
        if ($expected -is [System.Collections.IDictionary]) {
            Find-OverlayMismatch -Overlay $expected -Actual $found -Path "$Path$key."
        } elseif ((ConvertTo-Json -InputObject $expected -Compress -Depth 12) -cne (ConvertTo-Json -InputObject $found -Compress -Depth 12)) {
            "$Path$key"
        }
    }
}


# The default for the -Writer seams below. A seam, not a convenience: the self-test passes a writer
# that loses or corrupts the write, which is the only way to show the read-back checks are live.
$script:WriteConfigurationFile = {
    param([string] $Path, [string] $Text)
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Update-ParallelInstanceConfigurationFile {
    <#
        .SYNOPSIS
            Merges this instance's overlay into appsettings.Production.json, writes it, reads it back
            and checks every overlay value landed. Returns what was read back.

        .DESCRIPTION
            Run after the product script on every path -- first install, upgrade, rollback -- so what
            the definition says is what the file says, whatever the product script left behind
            (control-server#454: a first install rewrote the file, and the hand-merged clearance
            sections were gone without a word).

            Out of Install-ParallelInstanceLocal.ps1 so that the self-test can run it on a temporary
            file. The check used to cover three keys; it now covers every value the overlay writes --
            the vehicle identity and the MesIngest origin first among them -- because a merge that
            drops one starts a service that answers health and drives the wrong car or has no
            clearance exit.

            Uses New-ParallelInstanceConfigurationOverlay and Merge-ConfigurationTree from
            ParallelInstance.psm1, which every caller imports alongside this module.

        .PARAMETER Writer
            Test seam: { param($Path, $Text) } that writes the file. Callers on the machine omit it.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition,
        [scriptblock] $Writer = $script:WriteConfigurationFile
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The product installer wrote no configuration at $Path."
    }
    $current = Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 12
    $overlay = New-ParallelInstanceConfigurationOverlay -Definition $Definition
    $merged = Merge-ConfigurationTree -Base $current -Overlay $overlay
    $null = & $Writer $Path (ConvertTo-Json -InputObject $merged -Depth 12)

    $verify = Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 12
    $mismatches = @(Find-OverlayMismatch -Overlay $overlay -Actual $verify -Path '')
    if ($mismatches.Count -gt 0) {
        throw "After the merge, $Path does not carry the overlay's value for: $($mismatches -join ', ')."
    }
    return $verify
}

function Set-ParallelInstanceJourneyRuntimeDisabled {
    <#
        .SYNOPSIS
            Sets JourneyRuntime.enabled to false in the installed appsettings.Production.json, and
            nothing else. Returns the value it replaced.

        .DESCRIPTION
            control-server#454. Update-ControlServerLocal.ps1 refuses an installed configuration whose
            JourneyRuntime is on ('JourneyRuntime must remain disabled during upgrade.', ae2f99be9):
            it starts the new, unproven binary on the retained configuration for its lifecycle check,
            and a runtime that is on would poll demand and place orders from inside that check. The
            parallel overlay writes true, so every upgrade and rollback after a first install was
            refused there. Called only from Invoke-ParallelProductUpgrade, which says when and why.

            The section is found ignoring case, as both .NET configuration and the upgrade script's
            ConvertFrom-Json read it; the file is read back the way the upgrade script reads it.

        .PARAMETER Writer
            Test seam, as for Update-ParallelInstanceConfigurationFile.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [scriptblock] $Writer = $script:WriteConfigurationFile
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "No installed configuration at $Path."
    }
    $configuration = Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 12
    $section = @($configuration.Keys | Where-Object { [string]::Equals([string] $_, 'JourneyRuntime', [StringComparison]::OrdinalIgnoreCase) })
    if ($section.Count -ne 1) {
        throw "$Path has $($section.Count) JourneyRuntime sections; expected exactly one."
    }
    $journey = $configuration[$section[0]]
    $flag = @($journey.Keys | Where-Object { [string]::Equals([string] $_, 'enabled', [StringComparison]::OrdinalIgnoreCase) })
    $previous = $flag.Count -gt 0 ? $journey[$flag[0]] : $null
    foreach ($key in $flag) { $journey.Remove($key) }
    $journey['enabled'] = $false
    $null = & $Writer $Path (ConvertTo-Json -InputObject $configuration -Depth 12)

    # Read back exactly as the upgrade script will.
    if ((Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json).JourneyRuntime.enabled -ne $false) {
        throw "JourneyRuntime.enabled in $Path is not false after it was set."
    }
    return $previous
}

function Invoke-ParallelProductUpgrade {
    <#
        .SYNOPSIS
            Runs the product upgrade script on an installed parallel instance, keeping the intent of
            its preflight. The machine-touching steps are injected.

        .DESCRIPTION
            control-server#454. Both the upgrade and -Rollback reach the product's
            Update-ControlServerLocal.ps1 while the service exists. Its preflight refuses an
            installed configuration whose JourneyRuntime is on, because it starts the new binary on
            that configuration for its lifecycle check; this instance's overlay writes true. In order:

              1. Refuse while this instance can place RIoT orders (Get-ParallelUpgradeRefusal):
                 stopping the service also stops the runtime's fault supervision of a vehicle that
                 may be under way. Nothing has been touched yet.
              2. Stop the service (StopService), so a running service never sees the flag change.
              3. Set JourneyRuntime.enabled false in the file. The upgrade's lifecycle check then runs
                 with the runtime off, its backup holds false, and its rollback restores false --
                 the preflight's intent, kept rather than bypassed.
              4. Run the upgrade (InvokeUpdate). The caller writes the definition's value back with
                 the overlay, and only after this returned.
            On a failed upgrade the flag stays false (the safe direction), JOURNEY_RUNTIME_LEFT_DISABLED
            says so and how to recover, and the failure is rethrown unchanged.

        .PARAMETER Actions
            Hashtable of scriptblocks, all required: StopService (returns once the service is
            stopped), InvokeUpdate (runs the product upgrade script; throws on failure), and
            ServiceStatus (returns the service's status, for the failure report).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $ConfigurationPath,
        # Named in the refusal, so the operator restarts this instance's service and not the MVP's.
        [Parameter(Mandatory = $true)][string] $ServiceName,
        [Parameter(Mandatory = $true)][hashtable] $Actions
    )
    $missing = @('StopService', 'InvokeUpdate', 'ServiceStatus' | Where-Object { -not ($Actions.ContainsKey($_) -and $Actions[$_] -is [scriptblock]) })
    if ($missing.Count -gt 0) { throw "Invoke-ParallelProductUpgrade: missing action(s) $($missing -join ', '); nothing was run." }
    if (-not (Test-Path -LiteralPath $ConfigurationPath -PathType Leaf)) {
        throw "No installed configuration at $ConfigurationPath."
    }

    $installed = Get-Content -LiteralPath $ConfigurationPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 12
    $refusal = Get-ParallelUpgradeRefusal -Configuration $installed -ConfigurationPath $ConfigurationPath -ServiceName $ServiceName
    if ($refusal) { throw $refusal }

    $null = & $Actions.StopService
    $wasEnabled = Set-ParallelInstanceJourneyRuntimeDisabled -Path $ConfigurationPath
    Write-Host "JourneyRuntime.enabled set false for the upgrade, with the service stopped (was '$wasEnabled')"
    try {
        & $Actions.InvokeUpdate | Out-Host
    } catch {
        $status = try { & $Actions.ServiceStatus } catch { "unknown: $($_.Exception.Message)" }
        $flagNow = try { (Get-Content -LiteralPath $ConfigurationPath -Raw -Encoding utf8 | ConvertFrom-Json).JourneyRuntime.enabled } catch { "unreadable: $($_.Exception.Message)" }
        Write-Warning ("JOURNEY_RUNTIME_LEFT_DISABLED: the upgrade failed, and $ConfigurationPath has JourneyRuntime.enabled=$flagNow " +
            "(service status: $($status ?? 'absent')). If the upgrade script failed in its own preflight the service is still " +
            'stopped; if it failed after that, its own rollback restarted the previous binaries with the runtime off. Either way ' +
            'this instance dispatches nothing. To recover, redeploy the commit that is running now (or a fixed package): that ' +
            'writes the definition''s value back. Do NOT use -Rollback for this: after a failed install the package swap has not ' +
            'happened, so -Rollback installs the generation before the running one, which the onboard side may not match.')
        throw
    }
    return $wasEnabled
}

function Invoke-ParallelInstanceConfigurationStep {
    <#
        .SYNOPSIS
            Everything Set-InstanceConfiguration does after the product script, on every path, with
            the machine-touching steps injected. Returns the clearance exit readiness.

        .DESCRIPTION
            control-server#454. In order:
              1. merge the overlay into appsettings.Production.json and check every value landed
                 (Update-ParallelInstanceConfigurationFile);
              2. put the fault recovery credential into the service's Environment, when there is one;
              3. create an empty roster where the definition points, if there is no file there --
                 never overwriting one;
              4. judge the clearance exit from what the service will read, and throw
                 CLEARANCE_EXIT_BROKEN BEFORE restarting: a service restarted into a broken state
                 (a section missing, the entry on without its credential) would refuse to start or
                 run without an exit, and the running one is better left as it is;
              5. restart the service, so it reads all of the above.

        .PARAMETER Actions
            Hashtable of scriptblocks, all required: GetEnvironment (returns the service's
            Environment multi-string), SetEnvironment (takes the new one), RestartService.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $ConfigurationPath,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition,
        [AllowNull()][AllowEmptyString()][string] $Credential,
        [Parameter(Mandatory = $true)][string] $CredentialVariable,
        [Parameter(Mandatory = $true)][string] $RosterPath,
        [Parameter(Mandatory = $true)][hashtable] $Actions
    )
    $missing = @('GetEnvironment', 'SetEnvironment', 'RestartService' | Where-Object { -not ($Actions.ContainsKey($_) -and $Actions[$_] -is [scriptblock]) })
    if ($missing.Count -gt 0) { throw "Invoke-ParallelInstanceConfigurationStep: missing action(s) $($missing -join ', '); nothing was run." }

    $effective = Update-ParallelInstanceConfigurationFile -Path $ConfigurationPath -Definition $Definition

    if (-not [string]::IsNullOrWhiteSpace($Credential)) {
        [string[]] $before = @(& $Actions.GetEnvironment)
        $null = & $Actions.SetEnvironment (Set-ParallelServiceEnvironmentEntry -Environment $before -Name $CredentialVariable -Value $Credential)
    }

    # An empty roster rather than none: the two read the same to the server ("nobody holds the
    # permission"), but the file is where somebody fills in names later.
    if (-not (Test-Path -LiteralPath $RosterPath)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $RosterPath) -Force | Out-Null
        [IO.File]::WriteAllText($RosterPath, '{"operators":[]}', [Text.UTF8Encoding]::new($false))
    }

    # Names of entries whose value is not blank; values never leave this function.
    $populated = @(@(& $Actions.GetEnvironment) | Where-Object { $null -ne $_ } | ForEach-Object {
            $parts = $_ -split '=', 2
            if ($parts.Count -eq 2 -and -not [string]::IsNullOrWhiteSpace($parts[1])) { $parts[0] }
        })
    $rosterText = (Test-Path -LiteralPath $RosterPath -PathType Leaf) ? (Get-Content -LiteralPath $RosterPath -Raw -Encoding utf8) : $null
    $readiness = Get-ParallelClearanceExitReadiness -Configuration $effective -EnvironmentNames $populated -RosterText $rosterText
    if (@($readiness.Fatal).Count -gt 0) {
        throw ("CLEARANCE_EXIT_BROKEN (the service was not restarted): " + (@($readiness.Fatal) -join ' ') + " $($readiness.Line)")
    }

    $null = & $Actions.RestartService
    return $readiness
}

function Get-ParallelServiceProcessStartTimeUtc {
    <#
        .SYNOPSIS
            When the process behind a service started, in UTC; $null when there is no process or it
            cannot be read.

        .DESCRIPTION
            control-server#454 incremental review item 2: the installer compares it with the installed
            configuration's last write time (Get-ParallelPreInstallRefusal). $null is "unknown", and
            the check refuses on unknown, so nothing here needs to guess. The service name has passed
            Test-InstanceName (no wildcards, no quotes needed in the WQL filter).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $ServiceName)
    try {
        $wmi = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
        if ($null -eq $wmi -or [int] $wmi.ProcessId -le 0) { return $null }
        return (Get-Process -Id ([int] $wmi.ProcessId) -ErrorAction Stop).StartTime.ToUniversalTime()
    } catch {
        return $null
    }
}

Export-ModuleMember -Function @('Get-MvpFingerprint', 'Assert-MvpUntouched', 'Format-MvpFingerprint', 'Get-ParallelServiceProcessStartTimeUtc',
    'Get-ParallelProductUninstallerPath', 'Test-ParallelProductUninstallerPremise', 'Invoke-ParallelProductUninstaller',
    'Update-ParallelInstanceConfigurationFile', 'Set-ParallelInstanceJourneyRuntimeDisabled',
    'Invoke-ParallelProductUpgrade', 'Invoke-ParallelInstanceConfigurationStep')
