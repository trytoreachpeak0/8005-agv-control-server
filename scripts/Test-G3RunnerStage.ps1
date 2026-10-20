#Requires -Version 7

<#
.SYNOPSIS
    Offline check of the four G3 runners' stage handling (control-server#585): the exact clones without evidence/,
    the journey's one build, the free-disk floor and the removal of a passed run's stage.

.DESCRIPTION
    No runner is started. What each part checks:

      1. The exact clone. Each runner's own New-ExactClone (run-staged-g3.ps1, run-staged-g3-restart.ps1,
         run-journey-g3.ps1, run-demand-bearing-g3-vectors.ps1), lifted out of its source by AST and run against a
         throwaway repository with a top-level evidence/, behind a stand-in Invoke-LoggedCommand. With
         -ExcludeEvidence the checkout has everything but evidence/ (a nested docs/evidence/ stays) and git status
         is clean; without it, evidence/ is there. Its three assertions still bite under sparse checkout: a tracked
         file changed after the checkout, an untracked file under the excluded evidence/, and a HEAD moved off the
         commit each throw, and a -RemoteRef at another commit throws "remote ref mismatch" (demand-bearing's clone
         takes no remote ref and never did). Then, in each runner's source, the ControlServer clone is the one call
         that passes -ExcludeEvidence: no runner reads evidence/ out of a clone, and the peers' are left alone.
      2. The journey builds ControlServer.sln once, in its exact clone, inside the try (so a failed build is a runner
         error and no scenario starts), and every scenario is started with -SkipBuild.
      3. Assert-G3FreeDiskSpace: passes at a floor of 0, throws G3_DISK_LOW naming the drive at an impossible floor.
         Each runner has -MinFreeGiB, defaulting to 30, and checks it before it creates its stage; the nightly
         Invoke-NightlyG3.ps1 hands its own floor down.
      4. Complete-G3RunnerStage: a passed run's stage is gone, a red one's stays, and a stage it cannot remove (a file
         held open) is a warning and removed=false, never a throw, and never a `dotnet build-server shutdown`. Each
         runner ends: $passed assigned once, Complete-G3RunnerStage with it, then the throw on not passed; $passed is
         evaluated against each runner's pass and fail statuses and a secret leak.

    Exit 0 when every check passes, 1 otherwise, each check printed PASS or FAIL.
#>
[CmdletBinding()]
param(
    [string]$ScriptRoot = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $ScriptRoot 'G3RunnerStage.psm1') -Force

$failures = [System.Collections.Generic.List[string]]::new()
function Check([string]$name, [bool]$ok, [string]$detail) {
    Write-Host ("{0} {1}{2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $(if ($ok) { '' } else { " -- $detail" }))
    if (-not $ok) { $failures.Add($name) }
}

function Get-RunnerAst([string]$Path) {
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$parseErrors)
    if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) { throw "The runner does not parse: $Path" }
    return $ast
}

function Get-FunctionText([System.Management.Automation.Language.Ast]$Ast, [string]$Name) {
    $definitions = @($Ast.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name }, $true))
    if ($definitions.Count -ne 1) { return $null }
    return $definitions[0].Extent.Text
}

function Get-Calls([System.Management.Automation.Language.Ast]$Ast, [string]$Command) {
    return @($Ast.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq $Command }, $true))
}

# The argument of -$Name in a call: the element after the parameter, or its -Name:value argument.
function Get-ParameterArgument([System.Management.Automation.Language.CommandAst]$Call, [string]$Name) {
    $elements = $Call.CommandElements
    for ($i = 0; $i -lt $elements.Count; $i++) {
        $element = $elements[$i]
        if ($element -is [System.Management.Automation.Language.CommandParameterAst] -and $element.ParameterName -eq $Name) {
            if ($null -ne $element.Argument) { return $element.Argument }
            if ($i + 1 -lt $elements.Count) { return $elements[$i + 1] }
        }
    }
    return $null
}

function Test-HasSwitch([System.Management.Automation.Language.CommandAst]$Call, [string]$Name) {
    return @($Call.CommandElements | Where-Object {
            $_ -is [System.Management.Automation.Language.CommandParameterAst] -and $_.ParameterName -eq $Name }).Count -gt 0
}

function Test-InsideLoop([System.Management.Automation.Language.Ast]$Node) {
    for ($parent = $Node.Parent; $null -ne $parent; $parent = $parent.Parent) {
        if ($parent -is [System.Management.Automation.Language.LoopStatementAst]) { return $true }
    }
    return $false
}

function Invoke-Git([string]$Repository, [string[]]$Arguments) {
    $output = & git -C $Repository -c core.autocrlf=false -c user.name=g3-selfcheck -c user.email=g3-selfcheck@invalid @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed in ${Repository}: $output" }
    return $output
}

# The runners' Invoke-LoggedCommand, standing in: runs the command where the runner would, throws on a non-zero exit as
# the runners do, and lets a case change the clone between the checkout and New-ExactClone's own assertions.
$script:afterCheckout = $null
function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$LogPath,
        [switch]$AllowNonZeroExit
    )
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    $output | Out-File -LiteralPath $LogPath -Encoding utf8NoBOM
    if ($exitCode -ne 0 -and -not $AllowNonZeroExit) { throw "$Name exited with code $exitCode. See $LogPath" }
    if ($Name -like 'checkout-*' -and $null -ne $script:afterCheckout) { & $script:afterCheckout $WorkingDirectory }
    return $exitCode
}

$runnerFiles = [ordered]@{
    staged = 'run-staged-g3.ps1'
    restart = 'run-staged-g3-restart.ps1'
    'demand-bearing' = 'run-demand-bearing-g3-vectors.ps1'
    journey = 'run-journey-g3.ps1'
}
$runnerAsts = [ordered]@{}
foreach ($runner in $runnerFiles.Keys) { $runnerAsts[$runner] = Get-RunnerAst (Join-Path $ScriptRoot $runnerFiles[$runner]) }

$scratch = Join-Path ([IO.Path]::GetTempPath()) "g3-runner-stage-selfcheck-$([guid]::NewGuid().ToString('n'))"
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    # --- 1. the exact clone ---------------------------------------------------------------------------------------
    # A repository shaped like this one where it matters: a top-level evidence/ (deep), a nested docs/evidence/ that
    # is not the excluded one, and a second branch at another commit for the remote-ref and HEAD cases.
    $origin = Join-Path $scratch 'origin'
    New-Item -ItemType Directory -Path (Join-Path $origin 'scripts'), (Join-Path $origin 'evidence\g3\run\deep'), (Join-Path $origin 'docs\evidence') | Out-Null
    Set-Content -LiteralPath (Join-Path $origin 'README.md') -Value 'readme'
    Set-Content -LiteralPath (Join-Path $origin 'scripts\a.ps1') -Value 'a'
    Set-Content -LiteralPath (Join-Path $origin 'evidence\g3\run\SUMMARY.md') -Value 'summary'
    Set-Content -LiteralPath (Join-Path $origin 'evidence\g3\run\deep\log.txt') -Value 'log'
    Set-Content -LiteralPath (Join-Path $origin 'docs\evidence\note.md') -Value 'note'
    $null = & git init -q -b main $origin
    $null = Invoke-Git $origin @('add', '-A')
    $null = Invoke-Git $origin @('commit', '-q', '-m', 'one')
    $commit = (Invoke-Git $origin @('rev-parse', 'HEAD')).Trim()
    $null = Invoke-Git $origin @('checkout', '-q', '-b', 'other')
    Set-Content -LiteralPath (Join-Path $origin 'README.md') -Value 'readme, other'
    $null = Invoke-Git $origin @('commit', '-q', '-am', 'two')
    $otherCommit = (Invoke-Git $origin @('rev-parse', 'HEAD')).Trim()
    $null = Invoke-Git $origin @('checkout', '-q', 'main')

    $notClean = 'exact checkout is not clean|not a clean exact checkout'
    foreach ($runner in $runnerFiles.Keys) {
        $file = $runnerFiles[$runner]
        $text = Get-FunctionText $runnerAsts[$runner] 'New-ExactClone'
        Check "$file defines one New-ExactClone" ($null -ne $text) 'not exactly one'
        if ($null -eq $text) { continue }
        . ([scriptblock]::Create($text))
        $takesRemoteRef = (Get-Command New-ExactClone).Parameters.ContainsKey('RemoteRef')
        $takesExclude = (Get-Command New-ExactClone).Parameters.ContainsKey('ExcludeEvidence')
        Check "${file}: New-ExactClone takes -ExcludeEvidence" $takesExclude 'no such parameter'
        if (-not $takesExclude) { continue }

        $caseRoot = Join-Path $scratch $runner
        $sourcesRoot = Join-Path $caseRoot 'sources'
        $logsRoot = Join-Path $caseRoot 'logs'
        New-Item -ItemType Directory -Path $sourcesRoot, $logsRoot | Out-Null
        $remote = if ($takesRemoteRef) { @{ RemoteRef = 'origin/main' } } else { @{} }

        function Invoke-Case([string]$Case, [scriptblock]$AfterCheckout, [hashtable]$Extra = @{}, [switch]$Full) {
            $destination = Join-Path $sourcesRoot $Case
            $script:afterCheckout = $AfterCheckout
            $splat = @{ Name = 'control-server'; Repository = $origin; Destination = $destination; Commit = $commit } + $remote
            foreach ($key in $Extra.Keys) { $splat[$key] = $Extra[$key] }
            if (-not $Full) { $splat['ExcludeEvidence'] = $true }
            try { $null = New-ExactClone @splat; return [pscustomobject]@{ Destination = $destination; Error = $null } }
            catch { return [pscustomobject]@{ Destination = $destination; Error = $_.Exception.Message } }
            finally { $script:afterCheckout = $null }
        }

        $green = Invoke-Case 'green' $null
        $d = $green.Destination
        $head = if (Test-Path -LiteralPath $d) { (& git -C $d rev-parse HEAD).Trim() } else { $null }
        $status = if (Test-Path -LiteralPath $d) { @(& git -C $d status --porcelain) } else { @('no clone') }
        Check "${file}: -ExcludeEvidence clones the commit with no evidence/, clean" `
            ($null -eq $green.Error -and $head -ceq $commit -and $status.Count -eq 0 -and
             -not (Test-Path -LiteralPath (Join-Path $d 'evidence')) -and (Test-Path -LiteralPath (Join-Path $d 'scripts\a.ps1')) -and
             (Test-Path -LiteralPath (Join-Path $d 'README.md'))) `
            "error '$($green.Error)', HEAD $head, status '$($status -join '; ')', evidence there: $(Test-Path -LiteralPath (Join-Path $d 'evidence'))"
        Check "${file}: only the top-level evidence/ is left out (docs/evidence/ is checked out, evidence/ is still tracked)" `
            ((Test-Path -LiteralPath (Join-Path $d 'docs\evidence\note.md')) -and
             @(& git -C $d ls-files evidence).Count -eq 2) `
            "docs/evidence/note.md there: $(Test-Path -LiteralPath (Join-Path $d 'docs\evidence\note.md')); tracked under evidence/: $(@(& git -C $d ls-files evidence).Count)"

        $full = Invoke-Case 'full' $null -Full
        Check "${file}: without -ExcludeEvidence the checkout is whole" `
            ($null -eq $full.Error -and (Test-Path -LiteralPath (Join-Path $full.Destination 'evidence\g3\run\deep\log.txt'))) "error '$($full.Error)'"

        $edited = Invoke-Case 'edited' { param($clone) Add-Content -LiteralPath (Join-Path $clone 'scripts\a.ps1') -Value 'changed' }
        Check "${file}: red -- a tracked file changed after a sparse checkout throws" ($edited.Error -match $notClean) "error '$($edited.Error)'"

        $untracked = Invoke-Case 'untracked-evidence' { param($clone)
            New-Item -ItemType Directory -Path (Join-Path $clone 'evidence\stray') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $clone 'evidence\stray\file.txt') -Value 'stray' }
        Check "${file}: red -- an untracked file under the excluded evidence/ still counts as dirty" ($untracked.Error -match $notClean) "error '$($untracked.Error)'"

        $moved = Invoke-Case 'head-moved' { param($clone) $null = & git -C $clone checkout -q --detach $otherCommit }
        Check "${file}: red -- a HEAD off the commit throws" ($moved.Error -match $notClean) "error '$($moved.Error)'"

        if ($takesRemoteRef) {
            $mismatch = Invoke-Case 'remote-mismatch' $null @{ RemoteRef = 'origin/other' }
            Check "${file}: red -- a remote ref at another commit throws remote ref mismatch" `
                ($mismatch.Error -match 'remote ref mismatch' -and $mismatch.Error -match [regex]::Escape($otherCommit)) "error '$($mismatch.Error)'"
        }
        Remove-Item Function:\New-ExactClone

        # Which clones leave evidence/ out: the ControlServer, once, and nothing else.
        $calls = Get-Calls $runnerAsts[$runner] 'New-ExactClone'
        $named = @($calls | ForEach-Object { [pscustomobject]@{
                    Name = "$((Get-ParameterArgument $_ 'Name').Value)"; Exclude = Test-HasSwitch $_ 'ExcludeEvidence' } })
        $control = @($named | Where-Object Name -EQ 'control-server')
        $others = @($named | Where-Object Name -NE 'control-server')
        Check "${file}: the one ControlServer clone passes -ExcludeEvidence, no peer clone does" `
            ($control.Count -eq 1 -and $control[0].Exclude -and @($others | Where-Object Exclude).Count -eq 0) `
            "$(($named | ForEach-Object { "$($_.Name)=$($_.Exclude)" }) -join ', ')"
    }

    # --- 2. the journey builds once -------------------------------------------------------------------------------
    $journeyAst = $runnerAsts['journey']
    $logged = Get-Calls $journeyAst 'Invoke-LoggedCommand'
    $builds = @($logged | Where-Object { "$((Get-ParameterArgument $_ 'Name').Value)" -eq 'build-control-server' })
    $scenarioCalls = @($logged | Where-Object { (Get-ParameterArgument $_ 'Name').Extent.Text -match '^"scenario-' })
    $build = $builds | Select-Object -First 1
    $buildArguments = if ($null -ne $build) { (Get-ParameterArgument $build 'Arguments').Extent.Text } else { '' }
    Check 'run-journey-g3.ps1 builds once: one build-control-server, dotnet build ControlServer.sln -c Release in the clone' `
        ($builds.Count -eq 1 -and "$((Get-ParameterArgument $build 'FilePath').Value)" -eq 'dotnet' -and
         (Get-ParameterArgument $build 'WorkingDirectory').Extent.Text -eq '$controlSource' -and
         $buildArguments -match "'build'" -and $buildArguments -match 'ControlServer\.sln' -and $buildArguments -match "'-c', 'Release'") `
        "$($builds.Count) found: $buildArguments"
    $tries = @($journeyAst.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.TryStatementAst] -and $_.Extent.Text -match 'scenario-' })
    Check 'run-journey-g3.ps1: the build is in the scenarios'' try, before them, outside the loop, and a failed build throws' `
        ($null -ne $build -and $scenarioCalls.Count -eq 1 -and $tries.Count -eq 1 -and
         $build.Extent.StartOffset -gt $tries[0].Body.Extent.StartOffset -and $build.Extent.EndOffset -lt $tries[0].Body.Extent.EndOffset -and
         $build.Extent.StartOffset -lt $scenarioCalls[0].Extent.StartOffset -and -not (Test-InsideLoop $build) -and
         -not (Test-HasSwitch $build 'AllowNonZeroExit')) `
        "builds $($builds.Count), scenario calls $($scenarioCalls.Count), tries $($tries.Count)"
    Check 'run-journey-g3.ps1: every scenario is started with -SkipBuild' `
        ($scenarioCalls.Count -eq 1 -and (Get-ParameterArgument $scenarioCalls[0] 'Arguments').Extent.Text -match "'-SkipBuild'") `
        "$(${scenarioCalls}?[0]?.Extent.Text)"

    # --- 3. the free-disk floor -----------------------------------------------------------------------------------
    $thrown = $null
    try { $free = Assert-G3FreeDiskSpace -Path (Join-Path $scratch 'not-yet\stage') -MinFreeGiB 0 } catch { $thrown = $_.Exception.Message }
    Check 'Assert-G3FreeDiskSpace: a floor of 0 passes and returns the free GiB' ($null -eq $thrown -and $free -is [double] -and $free -gt 0) "$thrown / $free"
    $thrown = $null
    $driveRoot = [IO.Path]::GetPathRoot($scratch)
    try { $null = Assert-G3FreeDiskSpace -Path (Join-Path $scratch 'not-yet\stage') -MinFreeGiB 1000000000 } catch { $thrown = $_.Exception.Message }
    Check 'Assert-G3FreeDiskSpace: red -- below the floor throws G3_DISK_LOW naming the drive and the floor' `
        ($thrown -match '^G3_DISK_LOW: ' -and $thrown.Contains($driveRoot) -and $thrown.Contains('1000000000')) "$thrown"
    Push-Location -LiteralPath $scratch
    $thrown = $null
    try { $null = Assert-G3FreeDiskSpace -Path 'relative\stage' -MinFreeGiB 1000000000 } catch { $thrown = $_.Exception.Message }
    finally { Pop-Location }
    Check 'Assert-G3FreeDiskSpace: a relative path is the drive of the current location' ($thrown -match '^G3_DISK_LOW: ' -and $thrown.Contains($driveRoot)) "$thrown"

    foreach ($runner in $runnerFiles.Keys) {
        $file = $runnerFiles[$runner]
        $ast = $runnerAsts[$runner]
        $parameter = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'MinFreeGiB' })
        Check "${file}: -MinFreeGiB, default 30" ($parameter.Count -eq 1 -and $parameter[0].DefaultValue.Extent.Text -eq '30') "$(${parameter}?[0]?.Extent.Text)"
        $top = @($ast.EndBlock.Statements)
        $diskAt = @(for ($i = 0; $i -lt $top.Count; $i++) {
                if ($top[$i].Extent.Text -match '^(\$null = )?Assert-G3FreeDiskSpace -Path \$StageRoot -MinFreeGiB \$MinFreeGiB') { $i } })
        $stageAt = @(for ($i = 0; $i -lt $top.Count; $i++) { if ($top[$i].Extent.Text -match '^New-Item -ItemType Directory -Path \$StageRoot\b') { $i } })
        Check "${file}: the disk is checked once, at top level, before the stage is created" `
            ($diskAt.Count -eq 1 -and $stageAt.Count -eq 1 -and $diskAt[0] -lt $stageAt[0]) "check at $($diskAt -join ','), stage at $($stageAt -join ',')"
    }
    $nightlyText = Get-Content -Raw -LiteralPath (Join-Path $ScriptRoot 'Invoke-NightlyG3.ps1')
    Check 'Invoke-NightlyG3.ps1 hands its own -MinFreeGiB to every runner' ($nightlyText -match "'-MinFreeGiB', \`$MinFreeGiB") 'not found in $arguments'

    # --- 4. the stage of a passed run goes ------------------------------------------------------------------------
    function New-Stage([string]$Name) {
        $stage = Join-Path $scratch $Name
        New-Item -ItemType Directory -Path (Join-Path $stage 'sources\control-server\src'), (Join-Path $stage 'runtime') | Out-Null
        Set-Content -LiteralPath (Join-Path $stage 'sources\control-server\src\a.cs') -Value 'a'
        Set-Content -LiteralPath (Join-Path $stage 'runtime\controlserver.db') -Value 'db'
        return $stage
    }
    $stage = New-Stage 'stage-pass'
    $thrown = $null
    try { $result = Complete-G3RunnerStage -StageRoot $stage -Passed $true -RetrySeconds 0 6>$null } catch { $thrown = $_.Exception.Message }
    Check 'Complete-G3RunnerStage: a passed run''s stage is removed' ($null -eq $thrown -and $result.removed -eq $true -and -not (Test-Path -LiteralPath $stage)) "$thrown"
    $stage = New-Stage 'stage-fail'
    $thrown = $null
    try { $result = Complete-G3RunnerStage -StageRoot $stage -Passed $false -RetrySeconds 0 6>$null } catch { $thrown = $_.Exception.Message }
    Check 'Complete-G3RunnerStage: a red run''s stage stays, whole' `
        ($null -eq $thrown -and $result.removed -eq $false -and (Test-Path -LiteralPath (Join-Path $stage 'runtime\controlserver.db')) -and
         (Test-Path -LiteralPath (Join-Path $stage 'sources\control-server\src\a.cs'))) "$thrown"
    $stage = New-Stage 'stage-locked'
    $held = [IO.File]::Open((Join-Path $stage 'runtime\controlserver.db'), 'Open', 'Read', 'None')
    $thrown = $null
    $warnings = @()
    try {
        try { $result = Complete-G3RunnerStage -StageRoot $stage -Passed $true -Attempts 2 -RetrySeconds 0 -WarningVariable warnings -WarningAction SilentlyContinue 6>$null }
        catch { $thrown = $_.Exception.Message }
    }
    finally { $held.Dispose() }
    Check 'Complete-G3RunnerStage: a stage it cannot remove is a warning and removed=false, not a throw' `
        ($null -eq $thrown -and $result.removed -eq $false -and @($warnings | Where-Object { "$_" -match '^G3_STAGE_NOT_REMOVED: ' }).Count -eq 1) `
        "$thrown / removed $($result.removed) / warnings '$($warnings -join '; ')'"
    # What staged, restart and demand-bearing do: Add-Type -Path a DLL out of their own publish directory, which a .NET
    # process cannot unload (g3.yml run 39436562681: e_sqlite3.dll, "Access ... is denied"). In a child pwsh, which
    # then exits: everything else goes at once, the held rest once that process has exited.
    $stage = New-Stage 'stage-held-by-self'
    $publish = Join-Path $stage 'publish\control-server'
    New-Item -ItemType Directory -Path $publish | Out-Null
    $child = Join-Path $scratch 'held-by-self.ps1'
    Set-Content -LiteralPath $child -Value @'
param([string]$ModulePath, [string]$Stage, [string]$Publish, [string]$ResultPath)
$ErrorActionPreference = 'Stop'
Import-Module $ModulePath -Force
$dll = Join-Path $Publish 'G3SelfCheckHeld.dll'
Add-Type -TypeDefinition 'public static class G3SelfCheckHeld { public static int Answer() { return 42; } }' -OutputAssembly $dll
Add-Type -Path $dll
$null = [G3SelfCheckHeld]::Answer()
$result = Complete-G3RunnerStage -StageRoot $Stage -Passed $true -Attempts 2 -RetrySeconds 0
[ordered]@{ removed = $result.removed; reason = $result.reason; sourcesLeft = (Test-Path -LiteralPath (Join-Path $Stage 'sources'));
    runtimeLeft = (Test-Path -LiteralPath (Join-Path $Stage 'runtime')); dllLeft = (Test-Path -LiteralPath $dll) } |
    ConvertTo-Json | Set-Content -LiteralPath $ResultPath
'@
    $childResult = Join-Path $scratch 'held-by-self.json'
    $childOutput = & pwsh -NoProfile -File $child -ModulePath (Join-Path $ScriptRoot 'G3RunnerStage.psm1') -Stage $stage -Publish $publish -ResultPath $childResult *>&1 | Out-String
    $held = if (Test-Path -LiteralPath $childResult) { Get-Content -Raw -LiteralPath $childResult | ConvertFrom-Json } else { $null }
    Check 'Complete-G3RunnerStage: a DLL this process loaded from its stage -- the rest goes at once, the DLL stays while it runs' `
        ($null -ne $held -and $held.reason -eq 'HELD_BY_THIS_PROCESS' -and -not $held.sourcesLeft -and -not $held.runtimeLeft -and $held.dllLeft -and
         $childOutput -match 'G3_STAGE_REMOVED_AFTER_EXIT: ') `
        "$($held | ConvertTo-Json -Compress) $childOutput"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    while ((Test-Path -LiteralPath $stage) -and [DateTimeOffset]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 500 }
    Check 'Complete-G3RunnerStage: ... and once that process has exited, the stage is gone' (-not (Test-Path -LiteralPath $stage)) `
        "still there after 60 s: $(@(Get-ChildItem -LiteralPath $stage -Recurse -Force -ErrorAction Ignore | ForEach-Object Name) -join ', ')"

    $moduleAst = Get-RunnerAst (Join-Path $ScriptRoot 'G3RunnerStage.psm1')
    $shutdown = @($moduleAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] -and $node.Extent.Text -match 'build-server' }, $true))
    Check 'G3RunnerStage.psm1 never runs dotnet build-server shutdown (it would stop other sessions'' builds)' ($shutdown.Count -eq 0) "$($shutdown.Count) found"

    $passStatuses = @{
        staged = @('STAGED_SLICE_PASS', 'STAGED_G3_RECOVERY_REPLAY_PASS')
        restart = @('STAGED_G3_PROCESS_RESTART_PASS')
        'demand-bearing' = @('DEMAND_BEARING_G3_VECTORS_PASS')
        journey = @('JOURNEY_G3_PASS')
    }
    foreach ($runner in $runnerFiles.Keys) {
        $file = $runnerFiles[$runner]
        $top = @($runnerAsts[$runner].EndBlock.Statements)
        $assignments = @($top | Where-Object {
                $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$passed' })
        $ending = $top.Count -ge 2 -and
            $top[-2].Extent.Text -match '^\$null = Complete-G3RunnerStage -StageRoot \$StageRoot -Passed \$passed$' -and
            $top[-1] -is [System.Management.Automation.Language.IfStatementAst] -and
            $top[-1].Clauses[0].Item1.Extent.Text -eq '-not $passed' -and $top[-1].Clauses[0].Item2.Extent.Text -match '\bthrow\b'
        Check "${file} ends: `$passed once, Complete-G3RunnerStage with it, then the throw" ($assignments.Count -eq 1 -and $ending) `
            "assignments $($assignments.Count); last two: $(if ($top.Count -ge 2) { $top[-2].Extent.Text + ' | ' + $top[-1].Extent.Text.Split("`n")[0] })"
        if ($assignments.Count -ne 1) { continue }
        $predicate = [scriptblock]::Create("param(`$status, `$secretLeakFiles) $($assignments[0].Right.Extent.Text)")
        $cases = @($passStatuses[$runner] | ForEach-Object { @{ Status = $_; Leaks = @(); Expected = $true }; @{ Status = $_; Leaks = @('leak.txt'); Expected = $false } }) +
            @(@{ Status = 'INCONCLUSIVE_RUNNER_ERROR'; Leaks = @(); Expected = $false }, @{ Status = 'SOMETHING_FAIL'; Leaks = @(); Expected = $false })
        $wrong = @($cases | Where-Object { [bool](& $predicate $_.Status ([System.Collections.Generic.List[string]]$_.Leaks)) -ne $_.Expected } |
                ForEach-Object { "$($_.Status) with $($_.Leaks.Count) leak(s)" })
        Check "${file}: `$passed is true exactly for its pass statuses with no secret leak" ($wrong.Count -eq 0) "wrong for: $($wrong -join ', ')"
    }
}
finally {
    $script:afterCheckout = $null
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed."
    exit 1
}
Write-Host 'All checks passed.'
