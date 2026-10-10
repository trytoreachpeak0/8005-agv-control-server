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

function Set-ParallelInstanceConfigurationFlag {
    <#
        .SYNOPSIS
            Sets <Section>.enabled to $Value in the installed appsettings.Production.json, and nothing
            else. Returns the value it replaced.

        .DESCRIPTION
            control-server#454 wrote this for JourneyRuntime; control-server#472 made the section a
            parameter so that the dispatch gate script writes RiotCreateDispatch the same way. The
            section is found ignoring case, as both .NET configuration and the upgrade script's
            ConvertFrom-Json read it, and it must exist exactly once. Two read-backs:

              * the flag, the way the upgrade script reads it (ConvertFrom-Json, properties ignoring
                case), must be the boolean that was written;
              * everything else must be what was read before the write, value for value. That is the
                check that the file was written as UTF-8 and the Chinese agvId survived: a writer that
                re-encodes it (Notepad's "Save As", a code page) reads back as different text.

        .PARAMETER Writer
            Test seam, as for Update-ParallelInstanceConfigurationFile.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Section,
        [Parameter(Mandatory = $true)][bool] $Value,
        [scriptblock] $Writer = $script:WriteConfigurationFile
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "No installed configuration at $Path."
    }
    $configuration = Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 12
    $found = @($configuration.Keys | Where-Object { [string]::Equals([string] $_, $Section, [StringComparison]::OrdinalIgnoreCase) })
    if ($found.Count -ne 1) {
        throw "$Path has $($found.Count) $Section sections; expected exactly one."
    }
    $node = $configuration[$found[0]]
    if ($node -isnot [System.Collections.IDictionary]) {
        throw "$Section in $Path is not a JSON object."
    }
    $flag = @($node.Keys | Where-Object { [string]::Equals([string] $_, 'enabled', [StringComparison]::OrdinalIgnoreCase) })
    $previous = $flag.Count -gt 0 ? $node[$flag[0]] : $null
    foreach ($key in $flag) { $node.Remove($key) }
    $node['enabled'] = $Value
    $expected = ConvertTo-Json -InputObject $configuration -Depth 12 -Compress
    $null = & $Writer $Path (ConvertTo-Json -InputObject $configuration -Depth 12)

    # Read back exactly as the upgrade script will.
    $valueText = $Value ? 'true' : 'false'
    $readBack = (Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json).$Section.enabled
    if ($readBack -isnot [bool] -or $readBack -ne $Value) {
        throw "$Section.enabled in $Path is not $valueText after it was set."
    }
    $actual = Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 12 | ConvertTo-Json -Depth 12 -Compress
    if ($actual -cne $expected) {
        throw "$Path does not read back as written: something besides $Section.enabled differs (was it re-encoded? is the Chinese agvId intact?)."
    }
    return $previous
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
            refused there. Called from Invoke-ParallelProductUpgrade, which says when and why, and since
            control-server#578 from Invoke-ParallelInstanceConfigurationStep, which restarts the service with the
            runtime held off until Invoke-ParallelJourneyRuntimeRelease has read it back.
            The write and its read-backs are Set-ParallelInstanceConfigurationFlag's.

        .PARAMETER Writer
            Test seam, as for Update-ParallelInstanceConfigurationFile.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [scriptblock] $Writer = $script:WriteConfigurationFile
    )
    return Set-ParallelInstanceConfigurationFlag -Path $Path -Section 'JourneyRuntime' -Value $false -Writer $Writer
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

function Get-ParallelJourneyDispatchState {
    <#
        .SYNOPSIS
            Reads, read-only, what Get-ParallelDispatchGateRefusal judges from the instance's SQLite
            store. Never throws: a failure comes back as @{ Error = '...' }, which that function refuses.

        .DESCRIPTION
            control-server#472. Three reads, no writes: the journeys not Completed, the order intents,
            and the audit events of intents that are RESULT_UNKNOWN (all Test-ParallelOrderIntentNeverSent
            needs them for). Microsoft.Data.Sqlite and SQLitePCLRaw come from the installed service
            ($AssemblyDirectory), as scripts/l2/L2.psm1's Open-L2Database borrows them from the build under
            test; Mode=ReadOnly so this can neither block nor alter the service that owns the file, and
            unpooled so nothing keeps the file open afterwards. A missing file is an error, not an empty
            state: opening it would not create it, and "no database" is not "no journey".
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $DatabasePath,
        [Parameter(Mandatory = $true)][string] $AssemblyDirectory
    )
    $connection = $null
    try {
        if (-not (Test-Path -LiteralPath $DatabasePath -PathType Leaf)) { throw "there is no file at $DatabasePath" }
        foreach ($assembly in @('SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'SQLitePCLRaw.batteries_v2.dll', 'Microsoft.Data.Sqlite.dll')) {
            $path = Join-Path $AssemblyDirectory $assembly
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$assembly is not in $AssemblyDirectory" }
            Add-Type -LiteralPath $path -ErrorAction SilentlyContinue
        }
        # As in Open-L2Database: Batteries_V2 reports a missing type on some builds and the reads work regardless.
        try { [SQLitePCL.Batteries_V2]::Init() } catch { }
        $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$DatabasePath;Mode=ReadOnly;Pooling=False")
        $connection.Open()
        $read = {
            param([string] $Sql)
            $command = $connection.CreateCommand()
            $command.CommandText = $Sql
            $reader = $command.ExecuteReader()
            $rows = [System.Collections.Generic.List[object]]::new()
            try {
                while ($reader.Read()) {
                    $row = [ordered]@{}
                    for ($i = 0; $i -lt $reader.FieldCount; $i++) { $row[$reader.GetName($i)] = $reader.IsDBNull($i) ? $null : $reader.GetValue($i) }
                    $rows.Add($row)
                }
            } finally { $reader.Dispose(); $command.Dispose() }
            return , $rows.ToArray()
        }
        return [ordered]@{
            Journeys = & $read "SELECT JourneyId, Stage, AgvId, VehicleKey, PickupUpperId, GateUpperId, CreatedAt FROM JourneyRuntimes WHERE Stage <> 'Completed'"
            OrderIntents = & $read ('SELECT MovementLegId, UpperId, VehicleKey, Status, OrderId, CreateAttemptCount, CreateAttemptId, ' +
                'DispatchAuditVersion, ExperimentalCreateAuthorizationId, CreatedAt FROM OrderIntents')
            AuditEvents = & $read ('SELECT MovementLegId, Phase, Outcome, AttemptId, ReturnedOrderId, ResultPresent FROM RiotDispatchAuditEvents ' +
                "WHERE MovementLegId IN (SELECT MovementLegId FROM OrderIntents WHERE Status = 'RESULT_UNKNOWN')")
        }
    } catch {
        return [ordered]@{ Error = $_.Exception.Message }
    } finally {
        if ($connection) { $connection.Dispose() }
    }
}

function Invoke-ParallelDispatchGateChange {
    <#
        .SYNOPSIS
            Closes or opens this instance's RIoT dispatch gate (RiotCreateDispatch.enabled), with every
            machine-touching step injected. Returns what it did.

        .DESCRIPTION
            control-server#472, replacing the hand edit of the installed configuration on factory01. In
            order, and nothing is changed before step 5:

              1. Refuse the MVP's service name or any production path outright.
              2. The installer's own first checks (Get-ParallelPreInstallRefusal): the configuration
                 exists and is a JSON object, the service is Running or Stopped, and a Running process
                 started after the file was last written -- otherwise what the file says is not what the
                 process does. Its UPGRADE_REFUSED_DISPATCH_OPEN is the one answer ignored here: an open
                 gate is what this closes. A missing service is refused here too.
              3. Already in the requested state: nothing is touched, and the result says so.
              4. The journey state, read while the service runs (Get-ParallelDispatchGateRefusal): close
                 refuses any journey not Completed, open any journey with an order that was or may have
                 been sent. Unreadable refuses.
              5. Stop the service, and read the state again. This read is the one that counts: a stopped
                 service creates nothing, so nothing can slip in between it and the write. If it refuses
                 now, the service is started again on the unchanged file and the refusal says whether it came back
                 Running. A service that was Stopped is not touched, and its refusal carries no AFTER_STOP.
              6. Write the flag (Set-ParallelInstanceConfigurationFlag: section found ignoring case, the
                 flag and every other value read back). If the write fails, the original bytes are put
                 back, checked, and the service started again, so the gate is as it was.
              7. Start the service, and require it Running with a process that started after the file's
                 last write time, both in UTC.
            A service that was Stopped is left stopped: there is no process to restart, and the file is
            then the truth. Steps 4 and 5 still run, since the next start will read it.

        .PARAMETER Actions
            Hashtable of scriptblocks, all required: ServiceStatus (the status as a string, $null when
            the service does not exist), ReadState (Get-ParallelJourneyDispatchState's output),
            StopService, StartService, ProcessStartTimeUtc ($null when unknown).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Close', 'Open')][string] $Direction,
        [Parameter(Mandatory = $true)][string] $ConfigurationPath,
        [Parameter(Mandatory = $true)][string] $ServiceName,
        [Parameter(Mandatory = $true)][string] $DatabasePath,
        [Parameter(Mandatory = $true)][hashtable] $Actions,
        [scriptblock] $Writer = $script:WriteConfigurationFile
    )
    $missing = @('ServiceStatus', 'ReadState', 'StopService', 'StartService', 'ProcessStartTimeUtc' | Where-Object { -not ($Actions.ContainsKey($_) -and $Actions[$_] -is [scriptblock]) })
    if ($missing.Count -gt 0) { throw "Invoke-ParallelDispatchGateChange: missing action(s) $($missing -join ', '); nothing was run." }
    $nothing = ' Nothing was stopped or changed.'

    # 1.
    if ([string]::Equals($ServiceName, $script:ProductionServiceName, [StringComparison]::OrdinalIgnoreCase) -or
        (Test-ParallelInstancePathIsProduction -Path $ConfigurationPath) -or (Test-ParallelInstancePathIsProduction -Path $DatabasePath)) {
        throw ("GATE_REFUSED_PRODUCTION: '$ServiceName' / $ConfigurationPath / $DatabasePath is the MVP's or inside a production " +
            'path. This script changes only the v2 parallel instance.' + $nothing)
    }

    # 2.
    $status = & $Actions.ServiceStatus
    if ($null -eq $status) {
        throw "GATE_SERVICE_MISSING: there is no service '$ServiceName'. A gate belongs to an installed instance; install it first.$nothing"
    }
    $status = [string] $status
    $exists = Test-Path -LiteralPath $ConfigurationPath -PathType Leaf
    $text = $exists ? (Get-Content -LiteralPath $ConfigurationPath -Raw -Encoding utf8) : $null
    $refusal = Get-ParallelPreInstallRefusal -ServiceExists $true -ServiceStatus $status -ServiceName $ServiceName `
        -ConfigurationPath $ConfigurationPath -ConfigurationText $text `
        -ConfigurationWriteTimeUtc ($exists ? [IO.File]::GetLastWriteTimeUtc($ConfigurationPath) : $null) `
        -ProcessStartTimeUtc ($status -ceq 'Running' ? (& $Actions.ProcessStartTimeUtc) : $null)
    if ($refusal -and -not $refusal.StartsWith('UPGRADE_REFUSED_DISPATCH_OPEN:')) { throw $refusal }

    # 3.
    $target = $Direction -eq 'Open'
    $configuration = ConvertFrom-Json -InputObject $text -AsHashtable -Depth 12
    # Keys ignoring case, as .NET configuration reads them.
    $section = @($configuration.Keys | Where-Object { $_ -ieq 'RiotCreateDispatch' } | ForEach-Object { $configuration[$_] })
    $current = $section.Count -eq 1 -and $section[0] -is [System.Collections.IDictionary] ?
        ($section[0].Keys | Where-Object { $_ -ieq 'enabled' } | ForEach-Object { $section[0][$_] } | Select-Object -First 1) : $null
    if ($current -is [bool] -and $current -eq $target) {
        return [pscustomobject]@{ Changed = $false; Direction = $Direction; Previous = $current; Now = $current; ServiceStatus = $status
            Message = "RiotCreateDispatch.enabled is already $($target ? 'true' : 'false') in $ConfigurationPath; nothing was touched." }
    }

    # 4.
    $refusal = Get-ParallelDispatchGateRefusal -Direction $Direction -State (& $Actions.ReadState) -ServiceName $ServiceName -DatabasePath $DatabasePath
    if ($refusal) { throw $refusal }

    # Starting the service again after a refusal or a failed write, and saying what it is now rather than
    # assuming it came back (review N4).
    $startAgain = {
        # A start that throws must not replace the refusal it follows: the caller puts this text into that refusal, so
        # both are reported (review L5).
        try { $null = & $Actions.StartService } catch {
            return "'$ServiceName' could not be started again ($($_.Exception.Message)) -- start it by hand and check it"
        }
        $now = [string] (& $Actions.ServiceStatus)
        $now -ceq 'Running' ? "'$ServiceName' was started again and is Running" : "'$ServiceName' was asked to start again but is '$now' -- check it"
    }

    # 5.
    $wasRunning = $status -ceq 'Running'
    if ($wasRunning) { $null = & $Actions.StopService }
    $refusal = Get-ParallelDispatchGateRefusal -Direction $Direction -State (& $Actions.ReadState) -ServiceName $ServiceName -DatabasePath $DatabasePath
    if ($refusal) {
        # A service that was Stopped was not touched, so the refusal's own "nothing was stopped or changed" holds.
        if (-not $wasRunning) { throw $refusal }
        throw ('AFTER_STOP ' + $refusal.Replace($nothing,
                " The configuration was not changed. The service was stopped for this second read; $(& $startAgain), on the unchanged $ConfigurationPath."))
    }

    # 6.
    $original = [IO.File]::ReadAllBytes($ConfigurationPath)
    try {
        $previous = Set-ParallelInstanceConfigurationFlag -Path $ConfigurationPath -Section 'RiotCreateDispatch' -Value $target -Writer $Writer
    } catch {
        $failure = $_.Exception.Message
        [IO.File]::WriteAllBytes($ConfigurationPath, $original)
        $restored = [Linq.Enumerable]::SequenceEqual([byte[]] [IO.File]::ReadAllBytes($ConfigurationPath), [byte[]] $original)
        if (-not $restored) {
            throw ("GATE_WRITE_FAILED: $failure Putting the original bytes of $ConfigurationPath back did not hold either; the service " +
                "'$ServiceName' is left stopped. Restore the file from the latest backup under the backup root before starting it.")
        }
        throw ("GATE_WRITE_FAILED: $failure The original bytes of $ConfigurationPath were put back and checked" +
            $(if ($wasRunning) { ", and $(& $startAgain)" } else { '' }) + '; the gate is as it was.')
    }
    $writtenUtc = [IO.File]::GetLastWriteTimeUtc($ConfigurationPath)

    # 7.
    if (-not $wasRunning) {
        return [pscustomobject]@{ Changed = $true; Direction = $Direction; Previous = $previous; Now = $target; ServiceStatus = $status
            Message = "RiotCreateDispatch.enabled set $($target ? 'true' : 'false') in $ConfigurationPath. '$ServiceName' was $status and is left so; it reads the file when it next starts." }
    }
    # The flag is already written here, so a start that throws must say what the file now holds and that the
    # service did not come up (review S2) -- a bare Start-Service error would leave both unsaid.
    try { $null = & $Actions.StartService } catch {
        throw ("GATE_RESTART_FAILED: RiotCreateDispatch.enabled is now $($target ? 'true' : 'false') in $ConfigurationPath " +
            "(written $($writtenUtc.ToString('o'))), but '$ServiceName', stopped for this change, could not be started again " +
            "($($_.Exception.Message)). Start it by hand and check it before relying on the gate.")
    }
    $statusAfter = [string] (& $Actions.ServiceStatus)
    $startedUtc = & $Actions.ProcessStartTimeUtc
    if ($statusAfter -cne 'Running' -or $null -eq $startedUtc -or ([datetime] $startedUtc).ToUniversalTime() -le $writtenUtc) {
        throw ("GATE_RESTART_UNVERIFIED: RiotCreateDispatch.enabled is now $($target ? 'true' : 'false') in $ConfigurationPath " +
            "(written $($writtenUtc.ToString('o'))), but '$ServiceName' is '$statusAfter' with a process started at " +
            "$(if ($null -eq $startedUtc) { 'an unknown time' } else { ([datetime] $startedUtc).ToUniversalTime().ToString('o') }), so whether " +
            'the running process reads the new value cannot be shown. Check the service before relying on the gate.')
    }
    return [pscustomobject]@{ Changed = $true; Direction = $Direction; Previous = $previous; Now = $target; ServiceStatus = $statusAfter
        Message = ("RiotCreateDispatch.enabled set $($target ? 'true' : 'false') in $ConfigurationPath at $($writtenUtc.ToString('o')); " +
            "'$ServiceName' restarted, its process started at $(([datetime] $startedUtc).ToUniversalTime().ToString('o')).") }
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
              5. set JourneyRuntime.enabled=false (control-server#578) and restart the service, so it reads all
                 of the above with its journey runtime held off; Invoke-ParallelJourneyRuntimeRelease writes the
                 definition's value back once what this Host bound has been read back.

        .PARAMETER Actions
            Hashtable of scriptblocks, all required: GetEnvironment (returns the service's
            Environment multi-string), SetEnvironment (takes the new one), RestartService, GetMachineEnvironment
            (the machine-level environment as NAME=value; control-server#578).
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
    $missing = @('GetEnvironment', 'SetEnvironment', 'RestartService', 'GetMachineEnvironment' | Where-Object { -not ($Actions.ContainsKey($_) -and $Actions[$_] -is [scriptblock]) })
    if ($missing.Count -gt 0) { throw "Invoke-ParallelInstanceConfigurationStep: missing action(s) $($missing -join ', '); nothing was run." }

    # control-server#578 review item 2: a JourneyRuntime key in an environment layer would turn the runtime on over the
    # file's false. Refused before anything is changed, rather than read back once the Host has started.
    $overrides = @(Get-ParallelJourneyRuntimeEnvironmentOverride -ServiceEnvironment @(& $Actions.GetEnvironment) -MachineEnvironment @(& $Actions.GetMachineEnvironment))
    if ($overrides.Count -gt 0) {
        throw ("JOURNEY_RUNTIME_ENVIRONMENT_OVERRIDE (nothing was changed, the service was not restarted): $($overrides -join ', ') " +
            'set JourneyRuntime above appsettings.Production.json, so the journey runtime cannot be held off for the read-back. ' +
            'Remove the variable(s), then install again.')
    }

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

    # control-server#578: restarted with the journey runtime held off, whatever the definition says. The definition's
    # value is written back only by Invoke-ParallelJourneyRuntimeRelease, after what the Host bound has been read back.
    $null = Set-ParallelInstanceJourneyRuntimeDisabled -Path $ConfigurationPath
    $null = & $Actions.RestartService
    return $readiness
}

function Invoke-ParallelJourneyRuntimeRelease {
    <#
        .SYNOPSIS
            control-server#578. Opens the journey runtime only after a Host that holds it off has read back as
            the definition says. The machine-touching steps are injected. Returns what it did.

        .DESCRIPTION
            The Host logs what it bound (EFFECTIVE_CONFIGURATION) only once every hosted service has started, so
            with the runtime on, the first dispatch round -- reading the production catalog, accepting a demand
            into the database, claiming a vehicle, and with the dispatch gate open creating a RIoT order -- is
            already under way when the installer can first see a wrong binding. Stopping the service then stops
            the next round, not that one, and an accepted journey is reloaded and driven on every later start.
            So Invoke-ParallelInstanceConfigurationStep restarts the service with JourneyRuntime.enabled=false,
            and this function, in order:

              1. ReadBackHeld: the installer's read-back of that Host -- what it bound, that it logged the runtime
                 disabled, and that it logged no dispatch activity. On a refusal it stops the service and throws,
                 and nothing below runs: the runtime is never opened.
              2. A definition whose journeyRuntime.enabled is not true: done, the runtime stays off.
              3. The running process's id, then StopService, then ProcessExited on that id. A phase-one process
                 still alive when the flag is written could overlap the phase-two one; refused, flag unwritten.
              4. JourneyRuntime.enabled=true (Set-ParallelInstanceConfigurationFlag, which reads it back).
              5. StartService, and a process id that is not the phase-one one.
              6. ReadBackReleased: the read-back again, now of the Host that runs journeys -- the second check.
                 Only the flag differs from what step 1 read, so a mismatch here means something outside the
                 file changed between the two starts; the installer's action stops the service and throws.
            A refusal after step 4 sets JourneyRuntime.enabled back to false before it is thrown (review item 1): the
            service starts Automatic, and a reboot must not bring it back with the runtime on.

        .PARAMETER Actions
            Hashtable of scriptblocks, all required: ReadBackHeld, ReadBackReleased (each throws on a refusal,
            having stopped the service), ServiceProcessId ($null or 0 when there is no process), StopService,
            ProcessExited (takes a process id; $true once that process has exited, $false when it has not within
            its own wait), StartService.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $ConfigurationPath,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition,
        [Parameter(Mandatory = $true)][string] $ServiceName,
        [Parameter(Mandatory = $true)][hashtable] $Actions,
        [scriptblock] $Writer = $script:WriteConfigurationFile
    )
    $missing = @('ReadBackHeld', 'ReadBackReleased', 'ServiceProcessId', 'StopService', 'ProcessExited', 'StartService' |
            Where-Object { -not ($Actions.ContainsKey($_) -and $Actions[$_] -is [scriptblock]) })
    if ($missing.Count -gt 0) { throw "Invoke-ParallelJourneyRuntimeRelease: missing action(s) $($missing -join ', '); nothing was run." }

    # 1.
    $null = & $Actions.ReadBackHeld

    # 2.
    $journey = $Definition['journeyRuntime']
    $wanted = $journey -is [System.Collections.IDictionary] -and $journey['enabled'] -is [bool] -and $journey['enabled']
    if (-not $wanted) {
        return [pscustomobject]@{ Released = $false
            Message = "JourneyRuntime stays disabled in ${ConfigurationPath}: the definition does not enable it. '$ServiceName' read back as defined with the runtime off." }
    }

    # 3.
    $heldProcess = & $Actions.ServiceProcessId
    if ($null -eq $heldProcess -or [int] $heldProcess -le 0) {
        throw ("JOURNEY_RUNTIME_RELEASE_REFUSED: '$ServiceName' has no running process after its read-back, so the process to " +
            "replace is unknown. JourneyRuntime.enabled stays false in $ConfigurationPath; nothing dispatches.")
    }
    $null = & $Actions.StopService
    if (-not (& $Actions.ProcessExited ([int] $heldProcess))) {
        throw ("JOURNEY_RUNTIME_RELEASE_REFUSED: '$ServiceName' was stopped, but its process $heldProcess (the one that read back " +
            "with the runtime off) has not exited, and a second process must not start beside it. JourneyRuntime.enabled stays " +
            "false in $ConfigurationPath. Check the process, then install again.")
    }

    # 4.
    $null = Set-ParallelInstanceConfigurationFlag -Path $ConfigurationPath -Section 'JourneyRuntime' -Value $true -Writer $Writer

    # 5.
    $null = & $Actions.StartService
    # Review item 1: a refusal from here on leaves the flag false. The service starts Automatic, and a file still saying
    # true would bring it back on the next reboot with the runtime on and a binding that did not read back.
    $setBackFalse = {
        try {
            $null = Set-ParallelInstanceConfigurationFlag -Path $ConfigurationPath -Section 'JourneyRuntime' -Value $false -Writer $Writer
            "JourneyRuntime.enabled was set back to false in $ConfigurationPath."
        } catch {
            "Setting JourneyRuntime.enabled back to false in $ConfigurationPath FAILED ($($_.Exception.Message)); set it by hand before the service starts again."
        }
    }
    $releasedProcess = & $Actions.ServiceProcessId
    if ($null -eq $releasedProcess -or [int] $releasedProcess -le 0 -or [int] $releasedProcess -eq [int] $heldProcess) {
        throw ("JOURNEY_RUNTIME_RELEASE_UNVERIFIED: '$ServiceName' has no new process after JourneyRuntime.enabled was written true " +
            "(phase one was $heldProcess, now $(if ($null -eq $releasedProcess) { 'none' } else { $releasedProcess })). " +
            "Check the service; what it bound has not been read back. $(& $setBackFalse)")
    }

    # 6.
    try {
        $null = & $Actions.ReadBackReleased
    } catch {
        throw "$($_.Exception.Message) $(& $setBackFalse)"
    }
    return [pscustomobject]@{ Released = $true
        Message = "JourneyRuntime enabled in $ConfigurationPath after '$ServiceName' read back as defined with it off (process $heldProcess); read back again with it on (process $releasedProcess)." }
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
        return Get-ParallelProcessStartTimeUtc -ProcessId ([int] $wmi.ProcessId)
    } catch {
        return $null
    }
}

function Get-ParallelProcessStartTimeUtc {
    <#
        .SYNOPSIS
            When a process started, in UTC (Kind Utc); $null when it cannot be read.

        .DESCRIPTION
            Split out of Get-ParallelServiceProcessStartTimeUtc so the self-test can run it on its own
            process: reading a service's process (svchost and friends, another account) needs the
            elevation the installer has and a test run does not. Get-ParallelPreInstallRefusal converts
            to UTC again itself (third quick review, T4), so this conversion is no longer the only one.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][int] $ProcessId)
    try {
        return (Get-Process -Id $ProcessId -ErrorAction Stop).StartTime.ToUniversalTime()
    } catch {
        return $null
    }
}

function Get-ParallelFakeMesIngestTaskAction {
    <#
        .SYNOPSIS
            The FakeMesIngest scheduled task's action -- executable, argument string, working
            directory -- for Install-ParallelInstanceLocal.ps1 and Test-FakeMesIngestScheduledTask.ps1
            alike. Pure.

        .DESCRIPTION
            control-server#512. The first real install on factory01 registered a task whose action was
            pwsh -File Start-FakeMesIngestResident.ps1, started it, and nothing ran: LastTaskResult -1,
            no log directory, no PowerShellCore/Operational 40961 ("console is starting up"). Rerun on
            2026-10-08 it failed the same way, also with pwsh by absolute path; a SYSTEM task running
            pwsh -ExecutionPolicy Bypass -File on a small script under D:\ on the same machine ran
            normally, and the same task shape on vm01 started the double within two seconds. Which of
            the remaining differences (the trigger and restart settings, or the paths and arguments)
            stopped it was not isolated.

            So the task runs the double's executable itself, as the dashboard task does, with the two
            arguments the wrapper always passed: loopback, explicitly, and the port. No pwsh is in the
            task, and seeding is a separate step (Invoke-ParallelFakeMesIngestSeed) the
            installer runs from its own session once the double answers.

            The executable path is taken as given and must be absolute; Task Scheduler quotes it
            itself, so it may contain spaces but not a double quote. The working directory must not
            end in a backslash, the form the rest of this module writes.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $ExecutablePath,
        [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int] $Port,
        [Parameter(Mandatory = $true)][string] $WorkingDirectory
    )
    foreach ($pair in @(@('ExecutablePath', $ExecutablePath), @('WorkingDirectory', $WorkingDirectory))) {
        $name, $value = $pair
        if (-not [IO.Path]::IsPathFullyQualified($value)) { throw "$name must be an absolute path: '$value'" }
        if ($value.Contains('"')) { throw "$name must not contain a double quote: '$value'" }
        if ($value.EndsWith('\')) { throw "$name must not end in a backslash: '$value'" }
    }
    # listenAddress explicitly, though 127.0.0.1 is the default: this host carries a production
    # service and a CI-reachable internal switch, and a default that quietly changed would put the
    # double on both.
    $argument = "--FakeMesIngest:listenAddress=127.0.0.1 --FakeMesIngest:port=$Port"
    return [pscustomobject]@{ Execute = $ExecutablePath; Argument = $argument; WorkingDirectory = $WorkingDirectory }
}

function Register-ParallelFakeMesIngestTask {
    <#
        .SYNOPSIS
            Registers the FakeMesIngest task as SYSTEM at startup, creates the log directory the
            seeding step writes to, and starts it. Returns the registration time, which
            Wait-ParallelFakeMesIngestTask uses to tell this start from an earlier one.

        .DESCRIPTION
            The log directory is created here, before anything runs: on 2026-10-07 its absence was
            one more thing to rule out before reaching the real question (control-server#512).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $TaskName,
        [Parameter(Mandatory = $true)] $Action,
        [Parameter(Mandatory = $true)][string] $LogPath,
        [Parameter(Mandatory = $true)][string] $Description
    )
    New-Item -ItemType Directory -Path (Split-Path -Parent $LogPath) -Force | Out-Null
    $taskAction = New-ScheduledTaskAction -Execute $Action.Execute -Argument $Action.Argument -WorkingDirectory $Action.WorkingDirectory
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
    $registeredAt = [datetime]::Now
    Register-ScheduledTask -TaskName $TaskName -Action $taskAction -Trigger $trigger `
        -Principal $principal -Settings $settings -Description $Description | Out-Null
    # Discarded explicitly: anything this function emits joins the registration time it returns.
    $null = Start-ScheduledTask -TaskName $TaskName
    return $registeredAt
}

function Get-ParallelFakeMesIngestTaskReport {
    <#
        .SYNOPSIS
            What the machine says about a FakeMesIngest task that did not come up, as lines.

        .DESCRIPTION
            control-server#512 took several rounds of queries on factory01 to learn what this prints
            in one: the task's state and last result (in hex, so -1 reads as 0xFFFFFFFF), whether a
            process of the double's executable is running, and Application-log crash entries for it
            since the task was registered. The double's own console output is not captured in this
            task form, so these are what is left.

            LastRunTime is printed but not to be reasoned from: on factory01 it read about 30 s off
            the real start time on 2026-10-08, and 31 s before the registration on 2026-10-07.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $TaskName,
        [Parameter(Mandatory = $true)][string] $ExecutablePath,
        [Parameter(Mandatory = $true)][datetime] $Since
    )
    $lines = [System.Collections.Generic.List[string]]::new()
    try {
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
        $info = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction Stop
        $lines.Add(('task: state={0} lastTaskResult=0x{1:X8} lastRunTime={2:o} (unreliable on factory01)' -f $task.State, ([uint32] $info.LastTaskResult), $info.LastRunTime))
    } catch {
        $lines.Add("task: unreadable ($($_.Exception.Message))")
    }
    $running = @(Get-CimInstance -ClassName Win32_Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $ExecutablePath, [StringComparison]::OrdinalIgnoreCase) })
    $lines.Add("process ${ExecutablePath}: $(($running.Count -gt 0) ? "running, pid $(($running | ForEach-Object ProcessId) -join ',')" : 'not running')")
    $leaf = Split-Path -Leaf $ExecutablePath
    try {
        $crashes = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000, 1026; StartTime = $Since } -ErrorAction Stop |
                Where-Object { $_.Message -and $_.Message.Contains($leaf) })
        $lines.Add("Application log 1000/1026 naming $leaf since $($Since.ToString('o')): $($crashes.Count)")
        foreach ($crash in $crashes | Select-Object -First 3) { $lines.Add("  $($crash.TimeCreated.ToString('o')) $($crash.Id) $((($crash.Message -split "`n") | Select-Object -First 3) -join ' | ')") }
    } catch {
        $lines.Add(($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') ?
            "Application log 1000/1026 naming $leaf since $($Since.ToString('o')): 0" :
            "Application log unreadable ($($_.Exception.Message))")
    }
    return $lines.ToArray()
}

function Wait-ParallelFakeMesIngestTask {
    <#
        .SYNOPSIS
            Waits for the double behind the task to answer /control/v1/health; throws with
            Get-ParallelFakeMesIngestTaskReport's lines when it does not.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $TaskName,
        [Parameter(Mandatory = $true)][int] $Port,
        [Parameter(Mandatory = $true)][string] $ExecutablePath,
        [Parameter(Mandatory = $true)][datetime] $Since,
        [ValidateRange(1, 600)][int] $TimeoutSeconds = 120
    )
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/control/v1/health" `
                -NoProxy -TimeoutSec 5 -UseBasicParsing
            if ($response.StatusCode -eq 200) { return [string] $response.Content }
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    $report = Get-ParallelFakeMesIngestTaskReport -TaskName $TaskName -ExecutablePath $ExecutablePath -Since $Since
    throw ("FakeMesIngest did not answer http://127.0.0.1:$Port/control/v1/health within $TimeoutSeconds s." +
        [Environment]::NewLine + ($report -join [Environment]::NewLine))
}

function Write-FakeMesIngestSeedLog {
    # Not exported. A line in the seed log and on the console; Write-Host, so it never joins a
    # caller's return value.
    param([string] $LogPath, [string] $Message)
    $line = '{0} {1}' -f [DateTimeOffset]::Now.ToString('O'), $Message
    $directory = Split-Path -Parent $LogPath
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    [IO.File]::AppendAllText($LogPath, $line + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Write-Host $line
}

function Invoke-ParallelFakeMesIngestSeed {
    <#
        .SYNOPSIS
            Seeds the running double from the seed file -- wait for health, reset, PUT each demand,
            read the catalog back -- and returns the read-back line. Throws when the double does not
            answer, the reset carries no runId, or the catalog does not hold what the file lists.

        .DESCRIPTION
            control-server#512. Until then Start-FakeMesIngestResident.ps1 did this as the scheduled
            task's action, after starting the double. The task now runs the double itself (see
            Get-ParallelFakeMesIngestTaskAction), so the seed is applied here: by the installer once
            the double answers, and by an operator through Start-FakeMesIngestResident.ps1 after
            editing the seed file or after the double restarted -- in this task form a restart
            empties the catalog and nothing re-seeds it by itself.

            In this session and over HTTP only: no child process, nothing for the module's code-running
            scan in Test-ParallelInstance.ps1 to allow.

            A refused demand is logged and the rest carry on; the read-back count is the judge, and a
            mismatch throws, so that an install does not report complete over a catalog that is not
            the seed file.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int] $Port,
        [Parameter(Mandatory = $true)][string] $SeedPath,
        [Parameter(Mandatory = $true)][string] $LogPath,
        [ValidateRange(1, 600)][int] $ReadyTimeoutSeconds = 90
    )
    $baseUrl = "http://127.0.0.1:$Port"
    Write-FakeMesIngestSeedLog $LogPath "FakeMesIngest seeding the running double: port=$Port seed=$SeedPath"

    # Invoke-WebRequest, not curl.exe: Windows Server 2016 does not ship curl.
    $deadline = [datetime]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
    $ready = $false
    while ([datetime]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri "$baseUrl/control/v1/health" -NoProxy -TimeoutSec 5 -UseBasicParsing
            if ($response.StatusCode -eq 200) { $ready = $true; break }
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $ready) {
        Write-FakeMesIngestSeedLog $LogPath "FATAL: the double did not answer $baseUrl/control/v1/health within $ReadyTimeoutSeconds s"
        throw "Seeding the FakeMesIngest catalog failed: $baseUrl/control/v1/health did not answer within $ReadyTimeoutSeconds s. Log: $LogPath"
    }

    $demands = @()
    if ($SeedPath -and (Test-Path -LiteralPath $SeedPath -PathType Leaf)) {
        $seed = Get-Content -LiteralPath $SeedPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 10
        if ($seed.ContainsKey('demands') -and $seed['demands']) { $demands = @($seed['demands']) }
    } else {
        Write-FakeMesIngestSeedLog $LogPath "No seed file at '$SeedPath'; the catalog stays empty."
    }

    # The runId is the double's, not ours. CommandEngine.Apply refuses any command whose runId is
    # not the round it is currently in (RUN_ID_MISMATCH, HTTP 409), and a freshly started double
    # generates its own. Reset both starts a round and returns that round's id, which also makes
    # this seeding idempotent: whatever the catalog held, it now holds the seed file and nothing else.
    $resetBody = ConvertTo-Json -InputObject ([ordered]@{ commandId = "reset-$([guid]::NewGuid().ToString('N'))" }) -Depth 4
    $resetResponse = Invoke-WebRequest -Uri "$baseUrl/control/v1/reset" -Method Post `
        -ContentType 'application/json' -Body $resetBody -NoProxy -TimeoutSec 15 -UseBasicParsing
    $runId = ($resetResponse.Content | ConvertFrom-Json).runId
    if ([string]::IsNullOrWhiteSpace($runId)) {
        throw "The double's reset response carried no runId: $($resetResponse.Content)"
    }
    Write-FakeMesIngestSeedLog $LogPath "Catalog reset; this round is $runId"

    $seeded = 0
    foreach ($demand in $demands) {
        if (-not $demand.ContainsKey('demandId')) {
            Write-FakeMesIngestSeedLog $LogPath 'SKIP: a seed entry has no demandId'
            continue
        }
        $demandId = [string] $demand['demandId']
        $body = [ordered]@{ runId = $runId; commandId = "seed-$demandId" }
        foreach ($key in $demand.Keys) {
            if ($key -eq 'demandId') { continue }
            $body[$key] = $demand[$key]
        }
        try {
            $null = Invoke-WebRequest -Uri "$baseUrl/control/v1/demands/$demandId" -Method Put `
                -ContentType 'application/json' -Body (ConvertTo-Json -InputObject $body -Depth 8) `
                -NoProxy -TimeoutSec 15 -UseBasicParsing
            $seeded++
        } catch {
            Write-FakeMesIngestSeedLog $LogPath "SEED FAILED for '$demandId': $($_.Exception.Message)"
        }
    }
    Write-FakeMesIngestSeedLog $LogPath "Seeded $seeded of $($demands.Count) demand(s) under runId $runId"

    # Counting what the double reports, rather than what this believes it sent, is what tells
    # "two demands are in the catalog" from "two PUTs returned 200".
    $snapshot = (Invoke-WebRequest -Uri "$baseUrl/control/v1/snapshot" -NoProxy -TimeoutSec 15 -UseBasicParsing).Content |
        ConvertFrom-Json
    $inCatalog = @($snapshot.body.demands).Count
    $readBack = "Catalog now holds $inCatalog demand(s) at revision $($snapshot.body.catalogRevision)"
    Write-FakeMesIngestSeedLog $LogPath $readBack
    if ($inCatalog -ne $demands.Count) {
        Write-FakeMesIngestSeedLog $LogPath "WARNING: the seed file lists $($demands.Count) demand(s) but the catalog holds $inCatalog."
        throw "Seeding the FakeMesIngest catalog failed: the seed file lists $($demands.Count) demand(s) but the catalog holds $inCatalog. Log: $LogPath"
    }
    Write-FakeMesIngestSeedLog $LogPath 'Seeded. The double keeps running under its scheduled task.'
    return $readBack
}

Export-ModuleMember -Function @('Get-MvpFingerprint', 'Assert-MvpUntouched', 'Format-MvpFingerprint', 'Get-ParallelServiceProcessStartTimeUtc', 'Get-ParallelProcessStartTimeUtc',
    'Get-ParallelProductUninstallerPath', 'Test-ParallelProductUninstallerPremise', 'Invoke-ParallelProductUninstaller',
    'Update-ParallelInstanceConfigurationFile', 'Set-ParallelInstanceJourneyRuntimeDisabled', 'Set-ParallelInstanceConfigurationFlag',
    'Get-ParallelJourneyDispatchState', 'Invoke-ParallelDispatchGateChange',
    'Invoke-ParallelProductUpgrade', 'Invoke-ParallelInstanceConfigurationStep', 'Invoke-ParallelJourneyRuntimeRelease',
    'Get-ParallelFakeMesIngestTaskAction', 'Register-ParallelFakeMesIngestTask', 'Get-ParallelFakeMesIngestTaskReport',
    'Wait-ParallelFakeMesIngestTask', 'Invoke-ParallelFakeMesIngestSeed')
