#Requires -Version 7

<#
.SYNOPSIS
    Self-check for Select-TestSelfCheckRun.ps1 and TestSelfCheckScope.psd1 (control-server#803): which pull requests run
    which self-check step of test.yml, and how test.yml is wired to the answer.

.DESCRIPTION
    Seconds, pure input plus a few throwaway files under the system temp directory. A pull request that changes none of a
    self-check's inputs skips that step. A table that skipped a step on a change it should have run would leave that
    self-check silent on exactly the change that broke it, so this checks:

    1. Every step of test.yml is in the table, as a self-check or as one of the few that are not, and nothing in the
       table is missing from test.yml. A step added to test.yml and registered nowhere turns this red. A step left out
       as "not a self-check" must not run a script under scripts/.
    2. test.yml asks the selector before the first self-check, diffs the merge commit against its first parent without
       rename detection, and gates each registered step on its own output -- the steps after Test keep !cancelled().
    3. Never skipped: an event that is not a pull request, a change list that could not be read, a change to anything
       in Always.
    4. Changing a registered input never skips its step: the step's own script, every explicit input (a file under each
       prefix, a name matching each pattern), and every file the selector reports reaching by name. Plus an independent
       source for the last: the files each step's scripts load by a literal path (Import-Module, dot-source, &), read
       from the syntax tree and resolved on disk, must each run that step.
    5. Expected answers written out here, not computed the way the selector computes them: which steps a handful of
       real changes run and which they skip, and that a docs-only pull request skips them all.
    6. The closure on a throwaway repository: transitive, by code and not comments, a repository path and a directory
       path, a file the pull request deleted.

    Exits 1 when any case comes out the other way, and prints every failed case and a count of the rest.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/Test-TestSelfCheckScope.ps1
#>
[CmdletBinding()]
param(
    [string]$Selector = (Join-Path $PSScriptRoot 'Select-TestSelfCheckRun.ps1'),
    [string]$ScopeFile = (Join-Path $PSScriptRoot 'TestSelfCheckScope.psd1'),
    [string]$Repository = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:wrong = 0
$script:passed = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    if ($Ok) { $script:passed++; return }
    $script:wrong++
    Write-Host "BAD   $Name -> $Detail"
}
function Finish {
    Write-Host "$script:passed case(s) passed"
    if ($script:wrong -gt 0) {
        Write-Host "TEST_SELF_CHECK_SCOPE_SELFTEST_FAILED: $script:wrong case(s)"
        exit 1
    }
    Write-Host 'TEST_SELF_CHECK_SCOPE_SELFTEST_PASSED'
    exit 0
}
function Decide([string[]]$Changed, [hashtable]$Extra = @{}) {
    & $Selector -ChangedPath $Changed -Repository $Repository -ScopeFile $ScopeFile @Extra 6>$null
}
function StepOf($Decision, [string]$Key) { @($Decision.Steps | Where-Object Key -ceq $Key)[0] }

foreach ($required in $Selector, $ScopeFile) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        Check "$(Split-Path -Leaf $required) exists" $false "$required is missing"
        Finish
    }
}
$errors = $null
$null = [System.Management.Automation.Language.Parser]::ParseFile($Selector, [ref]$null, [ref]$errors)
Check 'the selector parses' ($errors.Count -eq 0) "$($errors.Count) parse errors"
$scope = Import-PowerShellDataFile -LiteralPath $ScopeFile
$registered = @($scope.Steps)
$ownSteps = @($registered | Where-Object { -not $_.ContainsKey('DecidedBy') })

# --- 1. Every step of test.yml is accounted for. ----------------------------------------------------------------------
$workflow = Get-Content -Raw -LiteralPath (Join-Path $Repository '.github/workflows/test.yml')
$stepsText = [regex]::Match($workflow, '(?ms)^    steps:\r?\n(.*)\z').Groups[1].Value
$blocks = @([regex]::Matches($stepsText, '(?ms)^      - .*?(?=^      - |\z)') | ForEach-Object Value)
Check 'test.yml has steps' ($blocks.Count -gt 30) "$($blocks.Count) steps"
$workflowSteps = @(foreach ($block in $blocks) {
        $title = [regex]::Match($block, '^      - (?:name|uses): ([^\r\n]+)').Groups[1].Value.Trim()
        [pscustomobject]@{ Title = $title; Block = $block; Index = [array]::IndexOf($blocks, $block) }
    })
$notSelfChecks = @($scope.NotSelfChecks | ForEach-Object { $_.Step })
$registeredNames = @($registered | ForEach-Object { $_.Name })
foreach ($step in $workflowSteps) {
    $known = ($registeredNames -ccontains $step.Title) -or ($notSelfChecks -ccontains $step.Title)
    Check "test.yml step '$($step.Title)' is registered in TestSelfCheckScope.psd1" $known 'in neither Steps nor NotSelfChecks'
}
foreach ($title in $registeredNames + $notSelfChecks) {
    $count = @($workflowSteps | Where-Object Title -ceq $title).Count
    Check "'$title' is exactly one step of test.yml" ($count -eq 1) "$count step(s)"
}
foreach ($title in $notSelfChecks) {
    $step = @($workflowSteps | Where-Object Title -ceq $title)[0]
    if ($null -ne $step) {
        Check "'$title', listed as not a self-check, runs no script under scripts/ by -File" (
            $step.Block -notmatch '-File \./scripts/') $step.Block
    }
}
$keys = @($registered | ForEach-Object { $_.Key })
Check 'every Key is unique' (@($keys | Sort-Object -Unique).Count -eq $keys.Count) ($keys -join ', ')
foreach ($entry in $registered) {
    Check "Key '$($entry.Key)' is letters, digits and underscores" ($entry.Key -cmatch '^[a-z0-9_]+$') $entry.Key
    $step = @($workflowSteps | Where-Object Title -ceq $entry.Name)[0]
    if ($null -eq $step) { continue }
    Check "'$($entry.Name)' runs $($entry.Script)" ($step.Block -match [regex]::Escape("-File ./$($entry.Script)")) $step.Block
    Check "$($entry.Script) exists" (Test-Path -LiteralPath (Join-Path $Repository $entry.Script) -PathType Leaf) 'missing'
}

# --- 2. test.yml's wiring. -----------------------------------------------------------------------------------------------
$scopeStep = @($workflowSteps | Where-Object { $_.Block -match '(?m)^        id: self-check-scope\s*$' })
Check 'test.yml has one step with id self-check-scope' ($scopeStep.Count -eq 1) "$($scopeStep.Count)"
$testStep = @($workflowSteps | Where-Object Title -ceq 'Test')
Check 'test.yml has a Test step' ($testStep.Count -eq 1) "$($testStep.Count)"
if ($scopeStep.Count -eq 1 -and $testStep.Count -eq 1) {
    $decide = $scopeStep[0].Block
    Check 'self-check-scope asks Select-TestSelfCheckRun.ps1, with -NotAPullRequest off pull requests' (
        $decide -match [regex]::Escape('./scripts/Select-TestSelfCheckRun.ps1') -and $decide -match '-NotAPullRequest' -and
        $decide -match [regex]::Escape("`$env:GITHUB_EVENT_NAME -ne 'pull_request'") -and
        $decide -match '-GitHubOutput \$env:GITHUB_OUTPUT') $decide
    Check 'self-check-scope diffs the merge commit against its first parent, without renames' (
        $decide -match [regex]::Escape('git diff --no-renames --name-only "$env:GITHUB_SHA^1" $env:GITHUB_SHA') -and
        $decide -match [regex]::Escape('--depth=2 origin $env:GITHUB_SHA')) $decide
    $firstRegistered = @($workflowSteps | Where-Object { $registeredNames -ccontains $_.Title } | ForEach-Object Index | Measure-Object -Minimum).Minimum
    Check 'self-check-scope comes before the first self-check' ($scopeStep[0].Index -lt $firstRegistered) "scope at $($scopeStep[0].Index), first self-check at $firstRegistered"
    foreach ($entry in $registered) {
        $step = @($workflowSteps | Where-Object Title -ceq $entry.Name)[0]
        if ($null -eq $step) { continue }
        $condition = [regex]::Match($step.Block, '(?m)^        if: ([^\r\n]+)').Groups[1].Value.Trim()
        $afterTest = $step.Index -gt $testStep[0].Index
        $expected = if ($entry.ContainsKey('DecidedBy')) {
            "`${{ !cancelled() && steps.$($entry.DecidedBy).outputs.run == 'true' }}"
        } elseif ($afterTest) {
            "`${{ !cancelled() && steps.self-check-scope.outputs.$($entry.Key) == 'true' }}"
        } else {
            "steps.self-check-scope.outputs.$($entry.Key) == 'true'"
        }
        Check "'$($entry.Name)' is gated on its own output" ($condition -ceq $expected) "if: $condition (expected $expected)"
    }
}

# --- 3. Never skipped. -------------------------------------------------------------------------------------------------
function AllRun($Decision) { @($Decision.Steps | Where-Object { -not $_.Run }).Count -eq 0 -and @($Decision.Steps).Count -eq $ownSteps.Count }
$notPr = & $Selector -NotAPullRequest -ChangedPath @('docs/README.md') -Repository $Repository -ScopeFile $ScopeFile 6>$null
Check 'not a pull request: every step runs' ((AllRun $notPr) -and $notPr.Reason -like '*not a pull request*') $notPr.Reason
$empty = Decide @()
Check 'no changed paths (unreadable diff): every step runs' ((AllRun $empty) -and $empty.Reason -like '*unknown*') $empty.Reason
$blank = Decide @('', '  ')
Check 'only blank paths: every step runs' (AllRun $blank) $blank.Reason
$alwaysSamples = @(foreach ($always in $scope.Always) { if ($always.EndsWith('/')) { "${always}anything/action.yml" } else { $always } })
foreach ($path in $alwaysSamples + @('.GitHub/Workflows/Test.yml')) {
    $decision = Decide @('docs/README.md', $path)
    Check "a change to $path runs every step" ((AllRun $decision) -and $decision.Reason -like "*$path*") $decision.Reason
}

# --- 4. Changing a registered input never skips its step. ----------------------------------------------------------------
function Get-Samples([string]$Pattern) {
    if ($Pattern.Contains('*')) {
        $existing = @(Get-ChildItem -Path (Join-Path $Repository $Pattern) -File -ErrorAction SilentlyContinue | Select-Object -First 2 |
                ForEach-Object { [IO.Path]::GetRelativePath($Repository, $_.FullName).Replace('\', '/') })
        return @($existing) + @($Pattern.Replace('*', 'a-new-file'))
    }
    if ($Pattern.EndsWith('/')) {
        $existing = @(Get-ChildItem -LiteralPath (Join-Path $Repository $Pattern) -Recurse -File -ErrorAction SilentlyContinue |
                Select-Object -First 2 | ForEach-Object { [IO.Path]::GetRelativePath($Repository, $_.FullName).Replace('\', '/') })
        return @($existing) + @("${Pattern}a-new-directory/a-new-file.ps1")
    }
    return @($Pattern)
}
$expectations = [Collections.Generic.Dictionary[string, Collections.Generic.HashSet[string]]]::new()
foreach ($entry in $ownSteps) {
    $set = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $null = $set.Add($entry.Script)
    foreach ($pattern in @($entry.Inputs)) { foreach ($sample in Get-Samples $pattern) { $null = $set.Add($sample) } }
    $expectations[$entry.Key] = $set
}
$listing = Decide @('docs/README.md')
foreach ($entry in $ownSteps) {
    $reported = StepOf $listing $entry.Key
    Check "the selector reports a decision for $($entry.Key)" ($null -ne $reported) 'no such step in its answer'
    if ($null -eq $reported) { continue }
    Check "$($entry.Key) reaches at least its own script" (@($reported.Reached) -ccontains $entry.Script) (@($reported.Reached) -join ', ')
    foreach ($path in @($reported.Reached)) { $null = $expectations[$entry.Key].Add($path) }
}

# The independent source: what each step's scripts load by a literal path, resolved on disk from the loading file.
function Get-LiteralLoads([string]$File) {
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($File, [ref]$null, [ref]$null)
    foreach ($command in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) {
        $isLoad = $command.InvocationOperator -in 'Dot', 'Ampersand' -or $command.GetCommandName() -eq 'Import-Module'
        if (-not $isLoad) { continue }
        $match = [regex]::Match($command.Extent.Text, 'Join-Path \$PSScriptRoot ''([^'']+\.ps[dm]?1)''')
        if (-not $match.Success) { continue }
        $target = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $File) $match.Groups[1].Value))
        if (Test-Path -LiteralPath $target -PathType Leaf) { [IO.Path]::GetRelativePath($Repository, $target).Replace('\', '/') }
    }
}
$loadedTotal = 0
foreach ($entry in $ownSteps) {
    $queue = [Collections.Generic.Queue[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $queue.Enqueue($entry.Script); $null = $seen.Add($entry.Script)
    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        foreach ($loaded in @(Get-LiteralLoads (Join-Path $Repository $current))) {
            if ($seen.Add($loaded)) { $queue.Enqueue($loaded); $null = $expectations[$entry.Key].Add($loaded); $loadedTotal++ }
        }
    }
}
Check 'the step scripts load at least twenty files by a literal path between them' ($loadedTotal -ge 20) "$loadedTotal"

$all = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($set in $expectations.Values) { foreach ($path in $set) { $null = $all.Add($path) } }
$sweep = Decide (@($all) + @('docs/README.md'))
foreach ($entry in $ownSteps) {
    $decided = StepOf $sweep $entry.Key
    if ($null -eq $decided) { continue }
    $hits = [Collections.Generic.HashSet[string]]::new([string[]]@($decided.Hits), [StringComparer]::Ordinal)
    foreach ($path in $expectations[$entry.Key]) {
        Check "$($entry.Key) runs when $path changes" ($decided.Run -and $hits.Contains($path)) 'not among the paths it counted'
    }
    Check "$($entry.Key) does not count docs/README.md" (-not $hits.Contains('docs/README.md')) (@($decided.Hits) -join ', ')
}
Write-Host "swept $($all.Count) input paths over $($ownSteps.Count) steps"

# --- 5. Written-out expectations. ---------------------------------------------------------------------------------------------
$docsOnly = Decide @('docs/README.md', 'evidence/cs803/green/anything.txt', 'tests/ControlServer.Tests/Anything.cs', 'README.md')
Check 'docs, evidence and tests only: every step is skipped' (@($docsOnly.Steps | Where-Object Run).Count -eq 0) (
    (@($docsOnly.Steps | Where-Object Run | ForEach-Object Key) -join ', '))
Check 'a skip says so' ($docsOnly.Reason -like '*skip*') $docsOnly.Reason
$cases = @(
    @{ Path = 'src/ControlServer.Host/Program.cs'; Runs = @('scenario_sql_explain', 'parallel_instance'); Skips = @('l2_hmi_phrase_watch', 'install_data_root_lock_wait', 'g3_nightly_decisions') }
    @{ Path = 'scripts/l2/L2.psm1'; Runs = @('l2_worklist_station_wait', 'l2_double_command_error', 'l2_closure_capture', 'script_static_checks'); Skips = @('install_data_root_lock_wait', 'parallel_instance') }
    @{ Path = 'scripts/Install-ControlServerLocal.ps1'; Runs = @('install_data_root_lock_wait', 'install_two_car_paths'); Skips = @('l2_hmi_phrase_watch', 'l2_worklist_station_wait') }
    @{ Path = 'scripts/LoopbackListener.psm1'; Runs = @('l2_double_command_error', 'g3_staged_error_path'); Skips = @('l2_hmi_phrase_watch') }
    @{ Path = '.github/workflows/g3.yml'; Runs = @('g3_nightly_decisions'); Skips = @('l2_hmi_phrase_watch', 'scenario_sql_explain') }
    @{ Path = '.github/workflows/l2.yml'; Runs = @('l2_scenario_round_scope'); Skips = @('l2_hmi_phrase_watch') }
    @{ Path = 'scripts/l2/scenarios/g3-forced-mechanical-recovery.ps1'; Runs = @('l2_forced_recovery_confirm', 'l2_onboard_handle'); Skips = @('l2_hmi_phrase_watch', 'install_two_car_paths') }
    @{ Path = 'vendor/8005-agv-protocol/integration-slices/index.json'; Runs = @('g3_evidence_honesty', 'scenario_sql_explain'); Skips = @('l2_hmi_phrase_watch') }
    @{ Path = 'scripts/l2/L2HmiPhraseWatch.psm1'; Runs = @('l2_hmi_phrase_watch'); Skips = @('install_data_root_lock_wait', 'parallel_instance') }
    @{ Path = 'scripts/parallel/ParallelInstance.psm1'; Runs = @('parallel_instance'); Skips = @('l2_hmi_phrase_watch', 'install_data_root_lock_wait') }
)
foreach ($case in $cases) {
    $decision = Decide @($case.Path)
    foreach ($key in $case.Runs) {
        $step = StepOf $decision $key
        Check "$($case.Path) runs $key" ($null -ne $step -and $step.Run) ($step | ConvertTo-Json -Compress -Depth 2)
    }
    foreach ($key in $case.Skips) {
        $step = StepOf $decision $key
        Check "$($case.Path) skips $key" ($null -ne $step -and -not $step.Run) ($step | ConvertTo-Json -Compress -Depth 2)
    }
}

$output = Join-Path ([IO.Path]::GetTempPath()) "self-check-scope-output-$([guid]::NewGuid().ToString('N')).txt"
try {
    $null = Decide @('scripts/l2/L2HmiPhraseWatch.psm1') @{ GitHubOutput = $output }
    $lines = @(Get-Content -LiteralPath $output)
    Check 'GitHub output has one key=value line per step the table decides' ($lines.Count -eq $ownSteps.Count) "$($lines.Count) lines"
    Check 'GitHub output says l2_hmi_phrase_watch=true and install_data_root_lock_wait=false' (
        $lines -ccontains 'l2_hmi_phrase_watch=true' -and $lines -ccontains 'install_data_root_lock_wait=false') ($lines -join '|')
    Check 'GitHub output has nothing for the step decided elsewhere' (-not ($lines -match '^staged_content_conflict=')) ($lines -join '|')
} finally {
    Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue
}

# --- 6. The closure on a throwaway repository. -----------------------------------------------------------------------------
$sandbox = Join-Path ([IO.Path]::GetTempPath()) "self-check-scope-$([guid]::NewGuid().ToString('N'))"
try {
    foreach ($dir in 'scripts/l2', 'src/Lib/Deep', 'docs') { $null = New-Item -ItemType Directory -Path (Join-Path $sandbox $dir) -Force }
    Set-Content -LiteralPath (Join-Path $sandbox 'scripts/l2/Test-One.ps1') -Value @'
# Commented.psm1 is named only in this comment.
Import-Module (Join-Path $PSScriptRoot 'Direct.psm1')
$source = Get-Content -Raw (Join-Path $PSScriptRoot '..\..\src\Lib\Options.cs')
$tree = Join-Path (Split-Path -Parent $PSScriptRoot) '../src/Lib/Deep'
'@
    Set-Content -LiteralPath (Join-Path $sandbox 'scripts/l2/Direct.psm1') -Value ". (Join-Path `$PSScriptRoot `"..\Transitive.ps1`")"
    foreach ($leaf in 'scripts/Transitive.ps1', 'scripts/l2/Commented.psm1', 'scripts/Unnamed.ps1', 'src/Lib/Options.cs', 'src/Lib/Other.cs',
        'src/Lib/Deep/Inner.cs', 'docs/README.md') {
        Set-Content -LiteralPath (Join-Path $sandbox $leaf) -Value '# nothing'
    }
    $sandboxScope = Join-Path $sandbox 'scripts/TestSelfCheckScope.psd1'
    Set-Content -LiteralPath $sandboxScope -Value @'
@{
    Always = @('scripts/TestSelfCheckScope.psd1')
    NotSelfChecks = @()
    Steps = @(@{ Key = 'one'; Name = 'One'; Script = 'scripts/l2/Test-One.ps1'; Inputs = @() })
}
'@
    $expect = @(
        @{ Path = 'scripts/l2/Test-One.ps1'; Run = $true }
        @{ Path = 'scripts/l2/Direct.psm1'; Run = $true }
        @{ Path = 'scripts/Transitive.ps1'; Run = $true }
        @{ Path = 'src/Lib/Options.cs'; Run = $true }
        @{ Path = 'src/Lib/Deep/Inner.cs'; Run = $true }
        @{ Path = 'src/Lib/Deep/a-new-file.cs'; Run = $true }
        @{ Path = 'scripts/l2/direct.PSM1'; Run = $true }
        @{ Path = 'scripts/l2/Commented.psm1'; Run = $false }
        @{ Path = 'scripts/Unnamed.ps1'; Run = $false }
        @{ Path = 'src/Lib/Other.cs'; Run = $false }
        @{ Path = 'docs/README.md'; Run = $false }
    )
    foreach ($case in $expect) {
        $decision = & $Selector -ChangedPath @($case.Path) -Repository $sandbox -ScopeFile $sandboxScope 6>$null
        $step = StepOf $decision 'one'
        Check "closure: $($case.Path) $(if ($case.Run) { 'runs' } else { 'skips' })" ($null -ne $step -and $step.Run -eq $case.Run) ($decision | ConvertTo-Json -Compress -Depth 3)
    }
    # A file the pull request deletes is gone from the checkout the selector reads, and the code naming it may be
    # unchanged: it still counts.
    Remove-Item -LiteralPath (Join-Path $sandbox 'scripts/Transitive.ps1')
    $deleted = & $Selector -ChangedPath @('scripts/Transitive.ps1') -Repository $sandbox -ScopeFile $sandboxScope 6>$null
    $step = StepOf $deleted 'one'
    Check 'closure: Transitive.ps1 deleted, only in the change list, runs' ($null -ne $step -and $step.Run) ($deleted | ConvertTo-Json -Compress -Depth 3)
} finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

Finish
