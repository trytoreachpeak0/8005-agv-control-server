#Requires -Version 7

<#
.SYNOPSIS
    Self-check for Select-L2ScenarioRun.ps1 (control-server#630): which pull requests run the synthetic L2 round in
    l2.yml, and how l2.yml is wired to it.

.DESCRIPTION
    Seconds, pure input plus a few throwaway files under the system temp directory. A pull request whose changes reach
    nothing the L2 round consumes ends that job green without running a scenario. A selector that skipped a change it
    should have run would leave L2 silent on exactly the change that broke it, so this checks:

    1. Every tracked file outside the few places the rule leaves out -- docs/, evidence/, tests/, three root documents,
       the other three workflows, and the files directly under scripts/ that no L2 code names -- runs the round,
       alone and beside a docs change. That is "changing any input file never skips", swept over the repository
       as it is, not over a list written here.
    2. The places it leaves out skip it, with the reason printed, and anything unknown -- a new top-level directory,
       a new workflow, a scripts/ subdirectory -- runs it.
    3. A file directly under scripts/ runs it once any L2 code names it in a string, directly or through another
       script L2 code names; a comment naming it does not. Checked on a throwaway copy of the closure's shape.
    4. Never skipped: an event that is not a pull request, a body with an L2-Consecutive line, a change list that is
       empty because it could not be read.
    5. l2.yml asks the selector before the scenarios run, gates the run and the evidence upload on its answer, and
       diffs the merge commit against its first parent without rename detection.
    6. The scenarios job turns off the MSBuild node reuse, the MSBuild server and the shared compiler, as the real-rig
       job does, so no build server stays resident through the round.

    The expected answers are written out here, not computed the way the selector computes them.

    Exits 1 when any case comes out the other way, and prints every failed case and a count of the rest.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/Test-L2ScenarioRunScope.ps1

.NOTES
    Here, not under scripts/l2: the selector reads every string in scripts/l2's code, and this file names the very
    scripts it expects to be left out, which would put them in.
#>
[CmdletBinding()]
param(
    [string]$Selector = (Join-Path $PSScriptRoot 'l2/Select-L2ScenarioRun.ps1'),
    [string]$Repository = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$script:wrong = 0
$script:passed = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    if ($Ok) { $script:passed++; return }
    $script:wrong++
    Write-Host "BAD   $Name -> $Detail"
}
function Decide([string[]]$Changed, [hashtable]$Extra = @{}) {
    & $Selector -ChangedPath $Changed -Repository $Repository @Extra 6>$null
}

if (-not (Test-Path -LiteralPath $Selector)) {
    Write-Host "BAD   the selector exists -> $Selector is missing"
    Write-Host 'L2_SCENARIO_RUN_SCOPE_SELFTEST_FAILED: 1 case(s)'
    exit 1
}
$errors = $null
$null = [System.Management.Automation.Language.Parser]::ParseFile($Selector, [ref]$null, [ref]$errors)
Check 'the selector parses' ($errors.Count -eq 0) "$($errors.Count) parse errors"

# --- 1. The sweep: every tracked file the rule does not leave out runs the round. ---------------------------------
$leftOutRoot = @('README.md', 'CLAUDE.md', 'ControlServer.http',
    '.github/workflows/test.yml', '.github/workflows/g3.yml', '.github/workflows/release.yml')
$tracked = @(git -C $Repository ls-files -- . ':!evidence')
Check 'git ls-files read the repository' ($LASTEXITCODE -eq 0 -and $tracked.Count -gt 500) "$($tracked.Count) files"
$inputs = @($tracked | Where-Object {
        $_ -notmatch '^(docs|evidence|tests)/' -and $_ -cnotin $leftOutRoot -and $_ -notmatch '^scripts/[^/]+$'
    })
$sweep = Decide (@('docs/README.md', 'tests/ControlServer.Tests/Anything.cs') + $inputs)
Check 'the sweep runs the round' ($sweep.Run -eq $true) $sweep.Reason
$counted = [Collections.Generic.HashSet[string]]::new([string[]]@($sweep.Inputs), [StringComparer]::Ordinal)
foreach ($path in $inputs) {
    Check "counts $path as an input" ($counted.Contains($path)) 'not among the inputs the selector reported'
}
Check 'the sweep does not count the docs and tests paths beside them' (
    -not $counted.Contains('docs/README.md') -and -not $counted.Contains('tests/ControlServer.Tests/Anything.cs')) ($sweep.Inputs -join ', ')
Write-Host "swept $($inputs.Count) tracked input files"

# --- 2. What it leaves out, and what is unknown. ---------------------------------------------------------------------
$skips = @(
    'docs/README.md'
    'docs/defects/20260916-arrival-trusted-on-a-session-row-pinned-for-one-iteration.md'
    'evidence/cs630/green/anything.txt'
    'tests/ControlServer.Tests/MultiVehicleExecutionTests.cs'
    'README.md'
    'CLAUDE.md'
    'ControlServer.http'
    '.github/workflows/test.yml'
    '.github/workflows/g3.yml'
    '.github/workflows/release.yml'
    # Directly under scripts/ and named by no L2 code (2026-10-11): G3 runners, install scripts, their self-checks.
    'scripts/run-staged-g3.ps1'
    'scripts/run-journey-g3.ps1'
    'scripts/Invoke-NightlyG3.ps1'
    'scripts/Install-ControlServerLocal.ps1'
    'scripts/Update-ControlServerLocal.ps1'
    'scripts/Test-DataRootLockWait.ps1'
    'scripts/a-script-that-does-not-exist-yet.ps1'
)
foreach ($path in $skips) {
    $decision = Decide @($path)
    Check "skips when only $path changed" ($decision.Run -eq $false -and $decision.Reason -like 'skipped*') ($decision | ConvertTo-Json -Compress)
}
$all = Decide $skips
Check 'skips when all of those changed together' ($all.Run -eq $false) ($all | ConvertTo-Json -Compress)

$runs = @(
    # What l2.yml and the orchestrator load from outside scripts/l2 today.
    'scripts/DesktopLock.psm1'
    # Named by an L2 self-check's code; not part of the round, but named, so it counts.
    'scripts/LoopbackListener.psm1'
    '.github/workflows/l2.yml'
    'scripts/l2/README.md'
    'scripts/l2/scenarios/g3-unable-to-charge-field-confirmation.ps1'
    'scripts/parallel/ParallelHost.psm1'
    'scripts/field/anything.ps1'
    'src/ControlServer.Host/Program.cs'
    'tools/ControlServer.FakeRiot/Program.cs'
    'vendor/anything.nupkg'
    'global.json'
    'Directory.Build.props'
    'Directory.Packages.props'
    'Directory.Build.targets'
    'NuGet.Config'
    'ControlServer.sln'
    '.gitattributes'
    '.editorconfig'
    '.config/dotnet-tools.json'
    # Unknown: whatever is not known to be left out runs the round.
    'a-new-top-level-directory/file.txt'
    'docs.md'
    'testsuite/x.cs'
    '.github/workflows/a-new-workflow.yml'
    '.github/actions/anything/action.yml'
    'Docs/README.md'
)
foreach ($path in $runs) {
    $decision = Decide @('docs/README.md', $path)
    Check "runs when $path changed" ($decision.Run -eq $true -and $decision.Reason -like "*$path*") ($decision | ConvertTo-Json -Compress)
}

# --- 3. The closure follows code, transitively, and not comments. ----------------------------------------------------
$sandbox = Join-Path ([IO.Path]::GetTempPath()) "l2-run-scope-$([guid]::NewGuid().ToString('N'))"
try {
    $null = New-Item -ItemType Directory -Path (Join-Path $sandbox 'scripts/l2/scenarios') -Force
    $null = New-Item -ItemType Directory -Path (Join-Path $sandbox '.github/workflows') -Force
    Set-Content -LiteralPath (Join-Path $sandbox '.github/workflows/l2.yml') -Value "run: ./scripts/l2/Invoke-L2Scenario.ps1`n# Workflow.psm1 is only a comment here, but the workflow is read as text."
    Set-Content -LiteralPath (Join-Path $sandbox 'scripts/l2/Invoke-L2Scenario.ps1') -Value @'
# Commented.psm1 is named only in this comment.
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'Direct.psm1')
'@
    Set-Content -LiteralPath (Join-Path $sandbox 'scripts/l2/scenarios/one.setup.psd1') -Value "@{ Helper = 'FromData.ps1' }"
    Set-Content -LiteralPath (Join-Path $sandbox 'scripts/Direct.psm1') -Value ". (Join-Path `$PSScriptRoot `"Transitive.ps1`")"
    foreach ($leaf in 'Transitive.ps1', 'FromData.ps1', 'Commented.psm1', 'Workflow.psm1', 'Unnamed.ps1') {
        Set-Content -LiteralPath (Join-Path $sandbox "scripts/$leaf") -Value '# nothing'
    }
    $expect = @(
        [pscustomobject]@{ Path = 'scripts/Direct.psm1'; Run = $true }
        [pscustomobject]@{ Path = 'scripts/Transitive.ps1'; Run = $true }
        [pscustomobject]@{ Path = 'scripts/FromData.ps1'; Run = $true }
        [pscustomobject]@{ Path = 'scripts/Workflow.psm1'; Run = $true }
        [pscustomobject]@{ Path = 'scripts/Commented.psm1'; Run = $false }
        [pscustomobject]@{ Path = 'scripts/Unnamed.ps1'; Run = $false }
        # Windows resolves names case-insensitively, so a name found in code does too.
        [pscustomobject]@{ Path = 'scripts/direct.psm1'; Run = $true }
    )
    foreach ($case in $expect) {
        $decision = & $Selector -ChangedPath @($case.Path) -Repository $sandbox 6>$null
        Check "closure: $($case.Path) $(if ($case.Run) { 'runs' } else { 'skips' })" ($decision.Run -eq $case.Run) ($decision | ConvertTo-Json -Compress)
    }
} finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

# --- 4. Never skipped. -----------------------------------------------------------------------------------------------
$notPr = & $Selector -NotAPullRequest -Repository $Repository 6>$null
Check 'not a pull request: runs' ($notPr.Run -eq $true -and $notPr.Reason -like '*not a pull request*') ($notPr | ConvertTo-Json -Compress)
$notPrWithDocs = & $Selector -NotAPullRequest -ChangedPath @('docs/README.md') -Repository $Repository 6>$null
Check 'not a pull request, docs only: still runs' ($notPrWithDocs.Run -eq $true) ($notPrWithDocs | ConvertTo-Json -Compress)
foreach ($body in "Fixes a thing.`nL2-Consecutive: normal-load", "> - l2-consecutive : ``a``, ``b``", "text`r`n  L2-Consecutive:x") {
    $decision = Decide @('docs/README.md') @{ PullRequestBody = $body }
    Check "an L2-Consecutive line runs it: $($body -replace '\s+', ' ')" ($decision.Run -eq $true -and $decision.Reason -like '*L2-Consecutive*') ($decision | ConvertTo-Json -Compress)
}
$noLine = Decide @('docs/README.md') @{ PullRequestBody = "Mentions L2-Consecutive: in the middle of a line? no, it starts one.`nNot this: x L2-Consecutive: y" }
Check 'L2-Consecutive not at the start of a line is not the line' ($noLine.Run -eq $false) ($noLine | ConvertTo-Json -Compress)
$plain = Decide @('docs/README.md') @{ PullRequestBody = "Only docs.`nL2 consecutive is spelled differently here." }
Check 'a body without the line does not force a run' ($plain.Run -eq $false) ($plain | ConvertTo-Json -Compress)
$empty = Decide @()
Check 'no changed paths (unreadable diff): runs' ($empty.Run -eq $true -and $empty.Reason -like '*unknown*') ($empty | ConvertTo-Json -Compress)
$blank = Decide @('', '  ')
Check 'only blank paths: runs' ($blank.Run -eq $true) ($blank | ConvertTo-Json -Compress)

$output = Join-Path ([IO.Path]::GetTempPath()) "l2-run-scope-output-$([guid]::NewGuid().ToString('N')).txt"
try {
    $null = & $Selector -ChangedPath @('docs/README.md') -Repository $Repository -GitHubOutput $output 6>$null
    $null = & $Selector -ChangedPath @('src/ControlServer.Host/Program.cs') -Repository $Repository -GitHubOutput $output 6>$null
    $lines = @(Get-Content -LiteralPath $output)
    Check 'GitHub output carries run=false then run=true' (($lines -join '|') -ceq 'run=false|run=true') ($lines -join '|')
} finally {
    Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue
}

# --- 5. l2.yml's wiring. -----------------------------------------------------------------------------------------------
$workflow = Get-Content -Raw -LiteralPath (Join-Path $Repository '.github/workflows/l2.yml')
$scenariosJob = [regex]::Match($workflow, '(?ms)^  scenarios:\r?\n.*?(?=^  [a-z][\w-]*:\r?\n)').Value
Check 'l2.yml has a scenarios job' ($scenariosJob.Length -gt 0) 'no "  scenarios:" job found'
$decide = [regex]::Match($scenariosJob, '(?ms)^      - name: [^\r\n]*\r?\n        id: l2-scope\r?\n.*?(?=^      - )').Value
Check 'the scenarios job has a step with id l2-scope' ($decide.Length -gt 0) 'no step with id l2-scope'
Check 'l2-scope asks Select-L2ScenarioRun.ps1, with -NotAPullRequest off pull requests' (
    $decide -match [regex]::Escape('./scripts/l2/Select-L2ScenarioRun.ps1') -and $decide -match '-NotAPullRequest' -and
    $decide -match '-PullRequestBody \$env:L2_PR_BODY' -and $decide -match '-GitHubOutput \$env:GITHUB_OUTPUT') $decide
Check 'l2-scope diffs the merge commit against its first parent, without renames' (
    $decide -match [regex]::Escape('git diff --no-renames --name-only "$env:GITHUB_SHA^1" $env:GITHUB_SHA') -and
    $decide -match [regex]::Escape('--depth=2 origin $env:GITHUB_SHA')) $decide
$order = @('id: l2-scope', '- name: Run synthetic L2 scenarios') | ForEach-Object { $scenariosJob.IndexOf($_) }
Check 'l2-scope comes before the scenarios run' ($order[0] -ge 0 -and $order[0] -lt $order[1]) "offsets $($order -join ', ')"
$runStep = [regex]::Match($scenariosJob, '(?ms)^      - name: Run synthetic L2 scenarios\r?\n(.*?)(?=^        run: )').Groups[1].Value
Check 'the scenarios run is gated on l2-scope' ($runStep -match "if: steps\.l2-scope\.outputs\.run == 'true'") $runStep
$upload = [regex]::Match($scenariosJob, '(?ms)^      - name: Upload L2 evidence\r?\n(.*?)(?=^        uses: )').Groups[1].Value
Check 'the evidence upload is gated on l2-scope' ($upload -match "if: always\(\) && steps\.l2-scope\.outputs\.run == 'true'") $upload

# --- 6. No build server outlives the scenarios job's build (control-server#630, item 2). -----------------------------
# The real-rig job has set these since 2026-09-19; on 2026-10-11 the synthetic job's MSBuild nodes and VBCSCompiler stayed
# resident for the whole round, about 1.4 GiB on win11-01 (the v2 workspace's ci-research/l2-second-runner.md).
$scenariosEnv = [regex]::Match($scenariosJob, '(?ms)^    env:\r?\n(.*?)(?=^    steps:)').Groups[1].Value
foreach ($setting in "MSBUILDDISABLENODEREUSE: '1'", "DOTNET_CLI_USE_MSBUILD_SERVER: '0'", "UseSharedCompilation: 'false'") {
    Check "the scenarios job sets $setting" ($scenariosEnv -match "(?m)^      $([regex]::Escape($setting))\s*$") 'not in the job env'
}

Write-Host "$script:passed case(s) passed"
if ($script:wrong -gt 0) {
    Write-Host "L2_SCENARIO_RUN_SCOPE_SELFTEST_FAILED: $script:wrong case(s)"
    exit 1
}
Write-Host 'L2_SCENARIO_RUN_SCOPE_SELFTEST_PASSED'
exit 0
