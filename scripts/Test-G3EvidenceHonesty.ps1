#Requires -Version 7

<#
.SYNOPSIS
    Offline self-check that G3 evidence does not claim more than its run tested (control-server#460): a
    self-check override is never a formal slice pass, an errored demand-bearing run reports its own cause,
    and -FieldRunRoot refuses a store the synthetic rig wrote.

.DESCRIPTION
    About ten seconds: no build, no peer, no window. One child pwsh runs the real demand-bearing runner
    against a throwaway repository whose store generator fails on purpose.

    1. formalSlicePass and SELF_CHECK_OVERRIDE. For each of the four run kinds, an all-PASS assertion report
       is graded by g3-slice-evidence.ps1 -- New-G3Classification and Write-G3GateResult, both -- under ten
       commits records: four with no controlServerCommitSource (no source at all, only an onboard source, an
       empty record, a bare string), an override together with an unknown source, SHARED_BINDING, a
       ControlServer override, an onboard override, and a source nobody has defined. Only SHARED_BINDING may say
       formalSlicePass true; every other record says false with the reason that applies -- COMMIT_SOURCE_MISSING,
       SELF_CHECK_OVERRIDE, UNRECOGNISED_COMMIT_SOURCE, or both of the last two -- while status stays PASS. The
       all-PASS premise is pinned by the SHARED_BINDING case, so a grading that passes nothing cannot satisfy
       the rest.
       Then the wiring, read from each runner's AST: every New-G3Classification call passes -Commits
       $commitsRecord, the gate results' Context carries commits = $commitsRecord, and every runner's record
       carries its *CommitSource keys. run-staged-g3.ps1's own $commitSources statement is then run with its
       param defaults and with each of the four commits replaced in turn: SHARED_BINDING four times, then
       SELF_CHECK_OVERRIDE for the replaced one, which withholds a PASS slice. The journey and demand-bearing
       runners' own source statements (the *CommitSource assignments, the -SelfCheck* branches and the
       $commitsRecord literal) are run the same way, with and without each override parameter, so a record that
       writes a constant instead of the variable goes red.

    2. The demand-bearing runner's error path. A git repository is made in a temporary directory whose
       scripts/l2/Invoke-L2Scenario.ps1 writes an assertions.json with outcome FAIL and a unique
       failureReason, then exits 1 -- the shape of a store generator that did not pass. The real runner is
       pointed at it with -ControlServerRepository and -SelfCheckControlServerCommit. It must fail, and its
       console and its runner-error.json must both carry that unique reason, although what stops the run
       is the gate-result writer's refusal of a store that was never read (the premise, also checked: the
       message cs#453's review found to be all that such a run printed). And, from the AST, the first
       statement after the runner's try is the Write-StagedRunError call.

    3. FIELD_RUN refuses a generated store. Get-GeneratedStoreMarkers and Assert-FieldRunStoreIsNotGenerated,
       taken from the runner's AST, over baselines whose rows are copied from committed evidence:
         - cs#453's control run, a generated store fed through -FieldRunRoot
           (evidence/g3/20261003-cs453-demand-bearing-synthetic-b660f80e/field-path-control): refused,
           naming every marker; and each marker on its own is enough;
         - the 2026-08-29 field store as the 2026-09-22 and 2026-09-01 runs read it: accepted;
         - the generated store under SYNTHETIC_RIG: accepted, that being what the generator is for.
       And, from the AST, the statement after $baseline = Read-ControlDatabase in the runner's try is the
       Assert-FieldRunStoreIsNotGenerated call.

    4. What the runner actually ran (control-server#466). Section 1's records also carry runnerSource, and only
       COMMITTED_RUNNER leaves the pass alone: missing, dirty, an input override, unknown provenance, or a value
       nobody defined each withhold it with its reason. From each runner's AST: one $runnerProvenance statement
       measuring $PSScriptRoot, before the first statement that touches $StageRoot or $EvidenceRoot; every path
       parameter whose default derives from $PSScriptRoot is one of its -Inputs, with the param default verbatim;
       the record's runnerSource is the provenance's. The four runners' source statements are run with HEAD
       binding another commit than the one on disk (or off -SharedRunnerSource): that commit is
       SELF_CHECK_OVERRIDE. Then Get-G3RunnerProvenance against a throwaway repository of the runner scripts:
       committed and clean passes, and so does an earlier run's untracked evidence under evidence/ (review M1 of
       PR #470); a locally edited default, the same edit behind assume-unchanged and behind skip-worktree, an
       untracked file outside evidence/ (beside it included), a changed tracked file under evidence/,
       -SharedRunnerSource at a copy (defaults kept, or one changed), HEAD without the binding, an unborn HEAD
       (its reason on one line) and no repository at all are each withheld. Each runner prints the provenance in
       the statement right after measuring it, loudly with reason and paths when it is not COMMITTED_RUNNER
       (review S2). run-staged-g3.ps1's harnessWorktreeCleanAtStart statement, from its AST, run against the same
       repository takes the same exemption (control-server#567): an earlier run's untracked evidence alone is clean,
       a stray untracked file, one beside evidence/ or a changed tracked file under it is not; it is measured after
       g3-slice-evidence.ps1 is loaded and before the run writes anything.

    The rows are inlined rather than read from evidence/ so that a clone without the evidence tree can run
    this.

    Exits 1 when any check comes out the other way, and prints every check either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\Test-G3EvidenceHonesty.ps1
#>
[CmdletBinding()]
param(
    [string]$ScriptRoot = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

$failures = [System.Collections.Generic.List[string]]::new()
function Check([string]$name, [bool]$ok, [string]$detail) {
    Write-Host ("{0} {1}{2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $(if ($ok) { '' } else { " -- $detail" }))
    if (-not $ok) { $failures.Add($name) }
}

function Get-RunnerAst([string]$Path) {
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) { throw "The runner does not parse: $Path" }
    return $ast
}

function Get-AstFunction($Ast, [string]$Name) {
    return @($Ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name
            }, $true)) | Select-Object -First 1
}

# The first statement after the runner's top-level try whose catch sets $runError.
function Get-StatementAfterRunTry($Ast) {
    $topLevel = @($Ast.EndBlock.Statements)
    $runTry = @($topLevel | Where-Object {
            $_ -is [System.Management.Automation.Language.TryStatementAst] -and
            @($_.CatchClauses | Where-Object { $_.Body.Extent.Text -match '\$runError\s*=\s*\$_' }).Count -eq 1
        })
    if ($runTry.Count -ne 1) { throw "Expected one top-level try whose catch sets `$runError, found $($runTry.Count)." }
    return [pscustomobject]@{ Try = $runTry[0]; Next = $topLevel[[array]::IndexOf($topLevel, $runTry[0]) + 1] }
}

function Get-CommandName($Statement) {
    if ($Statement -is [System.Management.Automation.Language.PipelineAst] -and
        $Statement.PipelineElements[0] -is [System.Management.Automation.Language.CommandAst]) {
        return $Statement.PipelineElements[0].GetCommandName()
    }
    return $null
}

. (Join-Path $ScriptRoot 'g3-slice-evidence.ps1')

# --- 1. formalSlicePass under a self-check override -----------------------------------------------------
$runKinds = @(
    'STAGED_G3_REAL_PEERS_DETERMINISTIC_PLAINTEXT',
    'STAGED_G3_REAL_PEERS_PROCESS_RESTART_NO_MOVEMENT',
    'DEMAND_BEARING_G3_RESULT_AND_RIOT_UNKNOWN_VECTORS_NO_MOVEMENT',
    'JOURNEY_G3_REAL_ONBOARD_SIMULATED_COUNTERPARTS')
$commitCases = @(
    # Absence withheld (review S3): before the review a record with no source graded as a pass.
    @{ Name = 'a record with no commit source'; Commits = [ordered]@{ controlServer = 'a' * 40; runnerSource = 'COMMITTED_RUNNER' }; Formal = $false; Reason = 'COMMIT_SOURCE_MISSING: controlServerCommitSource' },
    @{ Name = 'only an onboard source'; Commits = [ordered]@{ controlServer = 'a' * 40; onboardCommitSource = 'SHARED_BINDING'; runnerSource = 'COMMITTED_RUNNER' }; Formal = $false; Reason = 'COMMIT_SOURCE_MISSING: controlServerCommitSource' },
    @{ Name = 'an empty record'; Commits = [ordered]@{}; Formal = $false; Reason = 'COMMIT_SOURCE_MISSING: controlServerCommitSource; RUNNER_SOURCE_MISSING' },
    @{ Name = 'a bare string'; Commits = 'SHARED_BINDING'; Formal = $false; Reason = 'COMMIT_SOURCE_MISSING: controlServerCommitSource; RUNNER_SOURCE_MISSING' },
    # Both reasons kept (review note): the unknown value is not lost behind the override.
    @{ Name = 'an override and an unknown source'; Commits = [ordered]@{ controlServerCommitSource = 'SELF_CHECK_OVERRIDE'; onboardCommitSource = 'SOMETHING_NEW'; runnerSource = 'COMMITTED_RUNNER' }; Formal = $false; Reason = 'SELF_CHECK_OVERRIDE; UNRECOGNISED_COMMIT_SOURCE: onboardCommitSource=SOMETHING_NEW' },
    @{ Name = 'SHARED_BINDING'; Commits = [ordered]@{ controlServer = 'a' * 40; controlServerCommitSource = 'SHARED_BINDING'; onboardCommitSource = 'SHARED_BINDING'; runnerSource = 'COMMITTED_RUNNER' }; Formal = $true; Reason = $null },
    @{ Name = 'a ControlServer self-check override'; Commits = [ordered]@{ controlServer = 'b' * 40; controlServerCommitSource = 'SELF_CHECK_OVERRIDE'; runnerSource = 'COMMITTED_RUNNER' }; Formal = $false; Reason = 'SELF_CHECK_OVERRIDE' },
    @{ Name = 'an onboard self-check override'; Commits = [ordered]@{ controlServer = 'a' * 40; controlServerCommitSource = 'SHARED_BINDING'; onboardCommitSource = 'SELF_CHECK_OVERRIDE'; runnerSource = 'COMMITTED_RUNNER' }; Formal = $false; Reason = 'SELF_CHECK_OVERRIDE' },
    @{ Name = 'a source nobody defined'; Commits = [ordered]@{ controlServer = 'a' * 40; controlServerCommitSource = 'SOMETHING_NEW'; runnerSource = 'COMMITTED_RUNNER' }; Formal = $false; Reason = 'UNRECOGNISED_COMMIT_SOURCE*' },
    # control-server#466: the shared binding, run by something other than the committed runner.
    @{ Name = 'SHARED_BINDING with no runner source'; Commits = [ordered]@{ controlServerCommitSource = 'SHARED_BINDING' }; Formal = $false; Reason = 'RUNNER_SOURCE_MISSING' },
    @{ Name = 'SHARED_BINDING from a dirty runner worktree'; Commits = [ordered]@{ controlServerCommitSource = 'SHARED_BINDING'; runnerSource = 'RUNNER_WORKTREE_DIRTY' }; Formal = $false; Reason = 'RUNNER_WORKTREE_DIRTY' },
    @{ Name = 'SHARED_BINDING through another shared runner'; Commits = [ordered]@{ controlServerCommitSource = 'SHARED_BINDING'; runnerSource = 'RUNNER_INPUT_OVERRIDE: SharedRunnerSource' }; Formal = $false; Reason = 'RUNNER_INPUT_OVERRIDE: SharedRunnerSource' },
    @{ Name = 'SHARED_BINDING, dirty and through another shared runner'; Commits = [ordered]@{ controlServerCommitSource = 'SHARED_BINDING'; runnerSource = 'RUNNER_WORKTREE_DIRTY; RUNNER_INPUT_OVERRIDE: SharedRunnerSource, ControlServerRepository' }; Formal = $false; Reason = 'RUNNER_WORKTREE_DIRTY; RUNNER_INPUT_OVERRIDE: SharedRunnerSource, ControlServerRepository' },
    @{ Name = 'SHARED_BINDING with unknown provenance'; Commits = [ordered]@{ controlServerCommitSource = 'SHARED_BINDING'; runnerSource = 'RUNNER_PROVENANCE_UNKNOWN: no repository' }; Formal = $false; Reason = 'RUNNER_PROVENANCE_UNKNOWN: no repository' },
    @{ Name = 'an override from a dirty runner worktree'; Commits = [ordered]@{ controlServerCommitSource = 'SELF_CHECK_OVERRIDE'; runnerSource = 'RUNNER_WORKTREE_DIRTY' }; Formal = $false; Reason = 'SELF_CHECK_OVERRIDE; RUNNER_WORKTREE_DIRTY' },
    @{ Name = 'an empty runner source'; Commits = [ordered]@{ controlServerCommitSource = 'SHARED_BINDING'; runnerSource = '' }; Formal = $false; Reason = 'UNRECOGNISED_RUNNER_SOURCE: ' },
    @{ Name = 'a runner source nobody defined'; Commits = [ordered]@{ controlServerCommitSource = 'SHARED_BINDING'; runnerSource = 'SOMETHING_NEW' }; Formal = $false; Reason = 'UNRECOGNISED_RUNNER_SOURCE: SOMETHING_NEW' },
    @{ Name = 'a lowercase committed_runner'; Commits = [ordered]@{ controlServerCommitSource = 'SHARED_BINDING'; runnerSource = 'committed_runner' }; Formal = $false; Reason = 'UNRECOGNISED_RUNNER_SOURCE: committed_runner' }
)
$sliceIndexPath = Join-Path (Split-Path -Parent $ScriptRoot) 'vendor\8005-agv-protocol\integration-slices\index.json'
$gradingRoot = Join-Path ([IO.Path]::GetTempPath()) ("g3-evidence-honesty-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $gradingRoot | Out-Null
try {
    foreach ($runKind in $runKinds) {
        $claim = Get-G3RunnerClaim -RunKind $runKind
        $report = [ordered]@{}
        foreach ($name in @($claim.runWide) + @($claim.slices.Values | ForEach-Object { $_ })) { $report[$name] = 'PASS' }
        $firstSlice = @($claim.slices.Keys)[0]
        foreach ($case in $commitCases) {
            $label = "$runKind, $($case.Name)"
            $classification = New-G3Classification -RunKind $runKind -RunStatus 'ANY' -AssertionReport $report -Commits $case.Commits
            $slices = @($classification.officialSlices)
            $reasonOk = if ($null -eq $case.Reason) { $null -eq $classification.formalSliceWithheldReason } else {
                "$($classification.formalSliceWithheldReason)" -like $case.Reason }
            Check "classification, ${label}: formalSlicePass $($case.Formal), reason $($case.Reason ?? 'null')" `
                ($classification.formalSlicePass -eq $case.Formal -and $reasonOk) `
                "formalSlicePass $($classification.formalSlicePass), reason $($classification.formalSliceWithheldReason)"
            Check "classification, ${label}: every slice status PASS, every slice formalSlicePass $($case.Formal)" `
                (@($slices | Where-Object { $_.status -ne 'PASS' -or $_.formalSlicePass -ne $case.Formal }).Count -eq 0 -and
                 @($slices | Where-Object {
                     if ($null -eq $case.Reason) { $null -ne $_.formalSliceWithheldReason } else {
                         "$($_.formalSliceWithheldReason)" -notlike $case.Reason } }).Count -eq 0) `
                (($slices | ForEach-Object { "$($_.integrationSliceId) $($_.status) $($_.formalSlicePass) $($_.formalSliceWithheldReason)" }) -join '; ')

            $evidenceRoot = Join-Path $gradingRoot ([guid]::NewGuid().ToString('n'))
            $context = @{
                runId = 'honesty'; startedAt = 'now'; commits = $case.Commits
                protocolReleaseVersion = 'x'; protocolTag = 'x'; protocolProfileId = 'x'; protocolVersion = 'x'
                protocolApprovalStatus = 'x'; protocolRepositoryCommit = 'x'; protocolManifestSha256 = 'x'
                protocolSchemaBundleSha256 = 'x'; protocolVectorsSha256 = 'x'
                sliceIndexPath = $sliceIndexPath; sliceIndexSource = 'vendor'
            }
            $path = Write-G3GateResult -RunKind $runKind -EvidenceRoot $evidenceRoot -Slice $firstSlice `
                -AssertionReport $report -Context $context
            $gate = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
            $gateReasonOk = if ($null -eq $case.Reason) { $null -eq $gate.formalSliceWithheldReason } else {
                "$($gate.formalSliceWithheldReason)" -like $case.Reason }
            Check "gate-result.json, $label, ${firstSlice}: status PASS, formalSlicePass $($case.Formal), reason $($case.Reason ?? 'null')" `
                ($gate.status -eq 'PASS' -and $gate.formalSlicePass -eq $case.Formal -and $gateReasonOk) `
                "status $($gate.status), formalSlicePass $($gate.formalSlicePass), reason $($gate.formalSliceWithheldReason)"
        }
    }

    $thrown = $null
    try { $null = New-G3Classification -RunKind $runKinds[0] -RunStatus 'ANY' -AssertionReport ([ordered]@{}) -Commits $null }
    catch { $thrown = $_.Exception.Message }
    Check 'classification without a commits record: refused' ($thrown -like '*No commits record*') "$thrown"
} finally {
    Remove-Item -LiteralPath $gradingRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# The wiring. A runner that grades with a record lacking its *CommitSource keys would pass every check above
# and still write formalSlicePass true for an override.
$allSources = @('controlServerCommitSource', 'onboardCommitSource', 'simulatorCommitSource', 'protocolCommitSource')
$runnerSources = [ordered]@{
    'run-staged-g3.ps1' = $allSources
    'run-staged-g3-restart.ps1' = $allSources
    'run-demand-bearing-g3-vectors.ps1' = @('controlServerCommitSource')
    'run-journey-g3.ps1' = @('controlServerCommitSource', 'onboardCommitSource')
}
foreach ($file in $runnerSources.Keys) {
    $ast = Get-RunnerAst (Join-Path $ScriptRoot $file)
    $calls = @($ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'New-G3Classification'
            }, $true))
    $wired = @($calls | Where-Object { $_.Extent.Text -match '-Commits\s+\$commitsRecord\b' })
    Check "$file grades its slices with -Commits `$commitsRecord" ($calls.Count -ge 1 -and $wired.Count -eq $calls.Count) `
        "$($wired.Count) of $($calls.Count) New-G3Classification call(s)"
    $gateCalls = @($ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Write-G3GateResults'
            }, $true))
    $gateWired = @($gateCalls | Where-Object { $_.Extent.Text -match '\bcommits\s*=\s*\$commitsRecord\b' })
    Check "$file hands its gate results commits = `$commitsRecord" ($gateCalls.Count -ge 1 -and $gateWired.Count -eq $gateCalls.Count) `
        "$($gateWired.Count) of $($gateCalls.Count) Write-G3GateResults call(s)"
    $records = @($ast.EndBlock.Statements | Where-Object {
            $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $_.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $_.Left.VariablePath.UserPath -eq 'commitsRecord'
        })
    Check "$file assigns `$commitsRecord once, at top level" ($records.Count -eq 1) "$($records.Count) found"
    foreach ($key in $runnerSources[$file]) {
        Check "$file's `$commitsRecord carries $key" `
            ($records.Count -eq 1 -and $records[0].Right.Extent.Text -match "(?m)^\s*$key\s*=\s*\S") `
            "$(${records}?[0]?.Right.Extent.Text)"
    }
    # control-server#466: the runner source, from the provenance the runner measured, not a constant.
    Check "$file's `$commitsRecord carries runnerSource = `$runnerProvenance.runnerSource" `
        ($records.Count -eq 1 -and $records[0].Right.Extent.Text -match '(?m)^\s*runnerSource\s*=\s*\$runnerProvenance\.runnerSource\s*$') `
        "$(${records}?[0]?.Right.Extent.Text)"

    # control-server#466: one provenance statement, at top level, measuring the repository the script lives in
    # ($PSScriptRoot, never a parameter), before any top-level statement touches $StageRoot or $EvidenceRoot -- a
    # measurement taken after the run starts writing would be one an EvidenceRoot inside the repository could
    # fool, and one taken after a dirty tree was made by the run itself could not be told from the operator's.
    $topLevel = @($ast.EndBlock.Statements)
    $provenance = @($topLevel | Where-Object {
            $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $_.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $_.Left.VariablePath.UserPath -eq 'runnerProvenance'
        })
    Check "$file assigns `$runnerProvenance once, at top level, from Get-G3RunnerProvenance -ScriptRoot `$PSScriptRoot" `
        ($provenance.Count -eq 1 -and $provenance[0].Right.Extent.Text -match '^Get-G3RunnerProvenance\s+-ScriptRoot\s+\$PSScriptRoot\s') `
        "$($provenance.Count) found: $(${provenance}?[0]?.Right.Extent.Text)"
    # Review S2 of PR #470: the operator sees the source as the run starts, not after it.
    $printed = if ($provenance.Count -eq 1) { $topLevel[[array]::IndexOf($topLevel, $provenance[0]) + 1] } else { $null }
    Check "$file prints the provenance in the statement right after measuring it" `
        ("$(${printed}?.Extent.Text)" -ceq 'Write-G3RunnerProvenance -Provenance $runnerProvenance') "it is: $(${printed}?.Extent.Text)"
    $firstWrite = @($topLevel | Where-Object { $_.Extent.Text -match '\$(EvidenceRoot|StageRoot)\b' }) | Select-Object -First 1
    Check "$file measures its provenance before the first statement that touches `$StageRoot or `$EvidenceRoot" `
        ($provenance.Count -eq 1 -and $null -ne $firstWrite -and
         [array]::IndexOf($topLevel, $provenance[0]) -lt [array]::IndexOf($topLevel, $firstWrite)) `
        "provenance at line $(${provenance}?[0]?.Extent.StartLineNumber), first \$StageRoot/\$EvidenceRoot statement at line $(${firstWrite}?.Extent.StartLineNumber)"
    # Every path parameter whose default the runner derives from its own location decides what the run reads
    # (the shared runner, the binding reader, the repository the slice index and identity are read from), so
    # every one is an -Inputs entry, with its Given the parameter itself and its Default the param block's own
    # default, character for character. A new such parameter that is not added goes red here.
    $inputs = [ordered]@{}
    if ($provenance.Count -eq 1) {
        foreach ($m in [regex]::Matches($provenance[0].Extent.Text,
                '(?m)^\s*(\w+)\s*=\s*@\{\s*Given\s*=\s*\$(\w+)\s*;\s*Default\s*=\s*(.+?)\s*\}\s*$')) {
            $inputs[$m.Groups[1].Value] = @{ Given = $m.Groups[2].Value; Default = $m.Groups[3].Value }
        }
    }
    $located = @($ast.ParamBlock.Parameters | Where-Object { $null -ne $_.DefaultValue -and $_.DefaultValue.Extent.Text -match '\$PSScriptRoot\b' })
    foreach ($parameter in $located) {
        $name = $parameter.Name.VariablePath.UserPath
        Check "${file}: -$name is an -Inputs entry of Get-G3RunnerProvenance, Given `$$name, Default its param default" `
            ($inputs.Contains($name) -and $inputs[$name].Given -eq $name -and $inputs[$name].Default -ceq $parameter.DefaultValue.Extent.Text) `
            "$(if ($inputs.Contains($name)) { "Given $($inputs[$name].Given), Default $($inputs[$name].Default)" } else { 'missing' }); param default $($parameter.DefaultValue.Extent.Text)"
    }
    Check "${file}: -Inputs names only those parameters" ($inputs.Count -eq $located.Count -and $located.Count -ge 1) `
        "$($inputs.Count) entries, $($located.Count) located parameters: $($inputs.Keys -join ', ')"
}

# run-staged-g3.ps1's own sources: its four bindings are its param defaults, so an override is any value passed
# on the command line. Get-G3CommitSources first, then the runner's own $commitSources statement, evaluated with
# the binding and with each commit replaced in turn.
$restartAst = Get-RunnerAst (Join-Path $ScriptRoot 'run-staged-g3-restart.ps1')
. ([scriptblock]::Create((Get-AstFunction $restartAst 'Get-SharedCommitBinding').Extent.Text))
$binding = Get-SharedCommitBinding -Path (Join-Path $ScriptRoot 'run-staged-g3.ps1')
$sourceNames = [ordered]@{
    ControlServerCommit = 'controlServerCommitSource'; OnboardCommit = 'onboardCommitSource'
    SimulatorCommit = 'simulatorCommitSource'; ProtocolCommit = 'protocolCommitSource'
}
$sources = Get-G3CommitSources -Actual $binding -Binding $binding
Check 'Get-G3CommitSources: the binding itself is SHARED_BINDING four times' `
    (@($sources.Values | Where-Object { $_ -ne 'SHARED_BINDING' }).Count -eq 0 -and $sources.Count -eq 4) "$($sources | ConvertTo-Json -Compress)"
$upper = [ordered]@{}; foreach ($k in $binding.Keys) { $upper[$k] = $binding[$k] }
$upper['ControlServerCommit'] = $binding['ControlServerCommit'].ToUpperInvariant()
Check 'Get-G3CommitSources: an uppercase spelling of the bound commit is not the binding' `
    ((Get-G3CommitSources -Actual $upper -Binding $binding)['controlServerCommitSource'] -eq 'SELF_CHECK_OVERRIDE') 'it was taken as SHARED_BINDING'

$stagedAst = Get-RunnerAst (Join-Path $ScriptRoot 'run-staged-g3.ps1')
$sourcesStatements = @($stagedAst.EndBlock.Statements | Where-Object {
        $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $_.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
        $_.Left.VariablePath.UserPath -eq 'commitSources'
    })
Check 'run-staged-g3.ps1 assigns $commitSources once, at top level' ($sourcesStatements.Count -eq 1) "$($sourcesStatements.Count) found"
if ($sourcesStatements.Count -eq 1) {
    # $PSScriptRoot is empty inside a created scriptblock; the runner's directory is this one's.
    $sourcesStatement = [scriptblock]::Create($sourcesStatements[0].Extent.Text.Replace('$PSScriptRoot', "'$ScriptRoot'"))
    $stagedCases = @(@{ Name = 'the defaults'; Override = $null }) + @($sourceNames.Keys | ForEach-Object { @{ Name = "-$_ on the command line"; Override = $_ } }) +
        # control-server#466: HEAD committed another binding than the file on disk says (a locally edited default,
        # run with no parameter). The runner's values are the disk defaults; the comparison must be with HEAD.
        @($sourceNames.Keys | ForEach-Object { @{ Name = "HEAD binding another $_ than the disk default"; Override = $_; AtHead = $true } })
    foreach ($stagedCase in $stagedCases) {
        $ControlServerCommit = $binding['ControlServerCommit']; $OnboardCommit = $binding['OnboardCommit']
        $SimulatorCommit = $binding['SimulatorCommit']; $ProtocolCommit = $binding['ProtocolCommit']
        $atHead = [ordered]@{}; foreach ($k in $binding.Keys) { $atHead[$k] = $binding[$k] }
        if ($stagedCase.AtHead) { $atHead[$stagedCase.Override] = 'f' * 40 }
        elseif ($null -ne $stagedCase.Override) { Set-Variable -Name $stagedCase.Override -Value ('f' * 40) }
        $runnerProvenance = [ordered]@{ bindingAtHead = $atHead; runnerSource = 'COMMITTED_RUNNER' }
        $commitSources = $null
        $thrown = $null
        try { . $sourcesStatement } catch { $thrown = $_.Exception.Message }
        $expected = [ordered]@{}
        foreach ($k in $sourceNames.Keys) { $expected[$sourceNames[$k]] = if ($k -eq $stagedCase.Override) { 'SELF_CHECK_OVERRIDE' } else { 'SHARED_BINDING' } }
        $actualJson = if ($null -ne $commitSources) { $commitSources | ConvertTo-Json -Compress } else { 'null' }
        Check "run-staged-g3.ps1's commit sources, $($stagedCase.Name): $(($expected.Values | Select-Object -Unique) -join '/')" `
            ($null -eq $thrown -and $actualJson -eq ($expected | ConvertTo-Json -Compress)) "$thrown $actualJson"
        if ($null -ne $stagedCase.Override -and $null -ne $commitSources) {
            $graded = Get-G3FormalSlicePass -RunKind 'STAGED_G3_REAL_PEERS_DETERMINISTIC_PLAINTEXT' -SliceStatus 'PASS' -Commits (
                [ordered]@{ runnerSource = 'COMMITTED_RUNNER' } + $commitSources)
            Check "run-staged-g3.ps1, $($stagedCase.Name): a PASS slice is withheld as SELF_CHECK_OVERRIDE" `
                ($graded.formalSlicePass -eq $false -and $graded.formalSliceWithheldReason -eq 'SELF_CHECK_OVERRIDE') "$($graded | ConvertTo-Json -Compress)"
        }
    }
}

# The journey and demand-bearing runners' own sources (review S1). A regex over the record literal cannot tell
# `controlServerCommitSource = $controlServerCommitSource` from `controlServerCommitSource = 'SHARED_BINDING'`, and
# the second is cs#453's case all over again. So each runner's own statements are run, in their order: the
# $bindingSources assignment, every top-level assignment to a *CommitSource variable, every top-level `if` on a
# -SelfCheck* parameter, and the $commitsRecord literal. Run once with no override and once per override parameter;
# the record must carry SELF_CHECK_OVERRIDE exactly under the overridden commit's key, and SHARED_BINDING everywhere
# else. control-server#466 adds the cases where HEAD committed another binding than the one read off
# -SharedRunnerSource (an edited default, or a copy with other defaults), with no override parameter: that commit's
# source must be SELF_CHECK_OVERRIDE too, and the record must carry the provenance's runnerSource.
$overrideRunners = [ordered]@{
    'run-demand-bearing-g3-vectors.ps1' = [ordered]@{ SelfCheckControlServerCommit = 'controlServerCommitSource' }
    'run-journey-g3.ps1' = [ordered]@{
        SelfCheckControlServerCommit = 'controlServerCommitSource'; SelfCheckOnboardCommit = 'onboardCommitSource' }
}
$selfCheckCommit = [ordered]@{ SelfCheckControlServerCommit = 'ControlServerCommit'; SelfCheckOnboardCommit = 'OnboardCommit' }
foreach ($file in $overrideRunners.Keys) {
    $ast = Get-RunnerAst (Join-Path $ScriptRoot $file)
    $parameters = $overrideRunners[$file]
    $statements = @($ast.EndBlock.Statements | Where-Object {
            ($_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
             $_.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
             ($_.Left.VariablePath.UserPath -like '*CommitSource' -or $_.Left.VariablePath.UserPath -in 'commitsRecord', 'bindingSources')) -or
            ($_ -is [System.Management.Automation.Language.IfStatementAst] -and
             $_.Clauses[0].Item1.Extent.Text -match '\$SelfCheck\w*Commit\b')
        })
    $ifCount = @($statements | Where-Object { $_ -is [System.Management.Automation.Language.IfStatementAst] }).Count
    Check "${file}: one top-level -SelfCheck* branch per override parameter" ($ifCount -eq $parameters.Count) "$ifCount found"
    $block = [scriptblock]::Create((@($statements | ForEach-Object { $_.Extent.Text }) -join "`n"))
    $runCases = @(@($null) + @($parameters.Keys) | ForEach-Object { @{ Override = $_; AtHead = $null } }) +
        @($sourceNames.Keys | ForEach-Object { @{ Override = $null; AtHead = $_ } })
    foreach ($runCase in $runCases) {
        $override = $runCase.Override
        foreach ($name in $parameters.Keys) { Set-Variable -Name $name -Value $null }
        $commitBinding = [ordered]@{ ControlServerCommit = 'a' * 40; OnboardCommit = 'b' * 40; SimulatorCommit = 'c' * 40; ProtocolCommit = 'd' * 40 }
        $atHead = [ordered]@{} + $commitBinding
        if ($null -ne $runCase.AtHead) { $atHead[$runCase.AtHead] = '1' * 40 }
        elseif ($null -ne $override) { Set-Variable -Name $override -Value ('e' * 40) }
        $runnerProvenance = [ordered]@{ bindingAtHead = $atHead; runnerSource = 'COMMITTED_RUNNER' }
        $ControlServerCommit = $commitBinding['ControlServerCommit']; $OnboardCommit = $commitBinding['OnboardCommit']
        $commitsRecord = $null
        $thrown = $null
        try { . $block } catch { $thrown = $_.Exception.Message }
        # Every one of the four sources the record carries: SELF_CHECK_OVERRIDE under the overridden parameter's
        # commit, or under the commit HEAD binds differently, SHARED_BINDING under the other three.
        $changed = if ($null -ne $runCase.AtHead) { $runCase.AtHead } elseif ($null -ne $override) { $selfCheckCommit[$override] } else { $null }
        $wrong = @($sourceNames.Keys | ForEach-Object {
                $key = $sourceNames[$_]
                $expected = if ($_ -eq $changed) { 'SELF_CHECK_OVERRIDE' } else { 'SHARED_BINDING' }
                if ($null -eq $commitsRecord -or "$($commitsRecord[$key])" -ne $expected) { "$key=$(${commitsRecord}?[$key]) (expected $expected)" }
            })
        if ($null -ne $commitsRecord -and "$($commitsRecord['runnerSource'])" -cne 'COMMITTED_RUNNER') { $wrong += "runnerSource=$($commitsRecord['runnerSource'])" }
        $label = if ($null -ne $runCase.AtHead) { "HEAD binding another $($runCase.AtHead) than -SharedRunnerSource" } elseif (
            $null -eq $override) { 'no override' } else { "-$override" }
        Check "${file}, ${label}: the commits record carries the source the run set" ($null -eq $thrown -and $wrong.Count -eq 0) "$thrown $($wrong -join '; ')"
        if ($null -ne $changed -and $null -ne $commitsRecord) {
            $graded = Get-G3FormalSlicePass -RunKind $(if ($file -like '*journey*') { 'JOURNEY_G3_REAL_ONBOARD_SIMULATED_COUNTERPARTS' } else {
                    'DEMAND_BEARING_G3_RESULT_AND_RIOT_UNKNOWN_VECTORS_NO_MOVEMENT' }) -SliceStatus 'PASS' -Commits $commitsRecord
            Check "${file}, ${label}: a PASS slice is withheld as SELF_CHECK_OVERRIDE" `
                ($graded.formalSlicePass -eq $false -and $graded.formalSliceWithheldReason -eq 'SELF_CHECK_OVERRIDE') "$($graded | ConvertTo-Json -Compress)"
        }
    }
}

# The restart runner reads its commits off run-staged-g3.ps1 on disk and has no override parameter, so until
# control-server#466 it wrote the literal SHARED_BINDING four times. Its own $commitSources statement and its record,
# run with HEAD committing the same binding, then another one for each commit in turn.
$restartSources = @($restartAst.EndBlock.Statements | Where-Object {
        $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $_.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
        $_.Left.VariablePath.UserPath -eq 'commitSources'
    })
Check 'run-staged-g3-restart.ps1 assigns $commitSources once, at top level' ($restartSources.Count -eq 1) "$($restartSources.Count) found"
$restartRecord = @($restartAst.EndBlock.Statements | Where-Object {
        $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $_.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
        $_.Left.VariablePath.UserPath -eq 'commitsRecord'
    })
if ($restartSources.Count -eq 1 -and $restartRecord.Count -eq 1) {
    $restartBlock = [scriptblock]::Create($restartSources[0].Extent.Text + "`n" + $restartRecord[0].Extent.Text)
    foreach ($changed in @($null) + @($sourceNames.Keys)) {
        $commitBinding = [ordered]@{} + $binding
        $atHead = [ordered]@{} + $binding
        if ($null -ne $changed) { $atHead[$changed] = '1' * 40 }
        $runnerProvenance = [ordered]@{ bindingAtHead = $atHead; runnerSource = 'COMMITTED_RUNNER' }
        $ControlServerCommit = $binding['ControlServerCommit']; $OnboardCommit = $binding['OnboardCommit']
        $SimulatorCommit = $binding['SimulatorCommit']; $ProtocolCommit = $binding['ProtocolCommit']
        $runnerCommit = 'a' * 40; $runnerWorktreeClean = $true
        $commitSources = $null; $commitsRecord = $null; $thrown = $null
        try { . $restartBlock } catch { $thrown = $_.Exception.Message }
        $wrong = @($sourceNames.Keys | ForEach-Object {
                $expected = if ($_ -eq $changed) { 'SELF_CHECK_OVERRIDE' } else { 'SHARED_BINDING' }
                if ($null -eq $commitsRecord -or "$($commitsRecord[$sourceNames[$_]])" -ne $expected) { "$($sourceNames[$_])=$(${commitsRecord}?[$sourceNames[$_]]) (expected $expected)" }
            })
        if ($null -ne $commitsRecord -and "$($commitsRecord['runnerSource'])" -cne 'COMMITTED_RUNNER') { $wrong += "runnerSource=$($commitsRecord['runnerSource'])" }
        $label = if ($null -eq $changed) { 'HEAD committed the disk binding' } else { "HEAD binding another $changed than the disk default" }
        Check "run-staged-g3-restart.ps1, ${label}: the commits record carries the source" ($null -eq $thrown -and $wrong.Count -eq 0) "$thrown $($wrong -join '; ')"
    }
}

# --- 2. the demand-bearing runner reports its own error ----------------------------------------------------
$demandRunner = Join-Path $ScriptRoot 'run-demand-bearing-g3-vectors.ps1'
$demandAst = Get-RunnerAst $demandRunner
$afterTry = Get-StatementAfterRunTry $demandAst
Check 'the first statement after the demand-bearing run''s try is Write-StagedRunError' `
    ((Get-CommandName $afterTry.Next) -eq 'Write-StagedRunError') "it is: $(($afterTry.Next.Extent.Text -split "`n")[0].Trim())"

$work = Join-Path ([IO.Path]::GetTempPath()) ("g3-honesty-run-" + [guid]::NewGuid().ToString('n').Substring(0, 8))
$fakeRepository = Join-Path $work 'repo'
$cause = "CS460-FORCED-GENERATOR-FAILURE-$([guid]::NewGuid().ToString('n'))"
try {
    New-Item -ItemType Directory -Path (Join-Path $fakeRepository 'scripts\l2\scenarios'), (Join-Path $fakeRepository 'vendor\8005-agv-protocol\integration-slices') | Out-Null
    Copy-Item -LiteralPath $sliceIndexPath -Destination (Join-Path $fakeRepository 'vendor\8005-agv-protocol\integration-slices\index.json')
    Set-Content -LiteralPath (Join-Path $fakeRepository 'scripts\l2\scenarios\demand-bearing-store-at-unload.ps1') -Value '# placeholder'
    # The rig's own failure shape: an assertions.json that says why, then exit 1 (Invoke-L2Scenario.ps1's last line).
    Set-Content -LiteralPath (Join-Path $fakeRepository 'scripts\l2\Invoke-L2Scenario.ps1') -Value @"
param([string]`$Scenario, [string]`$EvidenceRoot, [string]`$Repository)
New-Item -ItemType Directory -Path `$EvidenceRoot -Force | Out-Null
[ordered]@{ outcome = 'FAIL'; failureReason = '$cause'; runId = 'forced' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path `$EvidenceRoot 'assertions.json')
Write-Host 'forced generator failure'
exit 1
"@
    & git -C $fakeRepository init --quiet 2>&1 | Out-Null
    & git -C $fakeRepository add --all 2>&1 | Out-Null
    & git -C $fakeRepository -c user.name=selfcheck -c user.email=selfcheck@invalid -c commit.gpgsign=false commit --quiet -m fake 2>&1 | Out-Null
    $fakeCommit = (& git -C $fakeRepository rev-parse HEAD).Trim()
    Check 'premise: the throwaway repository has a commit' ($fakeCommit -match '^[0-9a-f]{40}$') "$fakeCommit"

    $evidenceRoot = Join-Path $work 'e'
    $console = & pwsh -NoProfile -File $demandRunner -StageRoot (Join-Path $work 's') -EvidenceRoot $evidenceRoot `
        -ControlServerRepository $fakeRepository -SelfCheckControlServerCommit $fakeCommit *>&1 | Out-String
    $exitCode = $LASTEXITCODE
    Check 'the forced run fails' ($exitCode -ne 0) "exit $exitCode"
    Check 'premise: what stops the run is the gate-result writer, not the generator' `
        ($console -like '*fieldStoreProvenance with no protocolCommit*') ($console.Trim() -split "`n" | Select-Object -Last 5 | Out-String)
    Check 'the console carries the generator''s own reason' ($console -like "*$cause*") ($console.Trim() -split "`n" | Select-Object -Last 8 | Out-String)
    $saved = Join-Path $evidenceRoot 'runner-error.json'
    $savedJson = if (Test-Path -LiteralPath $saved) { Get-Content -Raw -LiteralPath $saved } else { '' }
    Check 'runner-error.json carries the generator''s own reason and the exit' `
        ($savedJson -like "*$cause*" -and $savedJson -like '*generate-demand-bearing-store exited with code 1*') "runner-error.json: $savedJson"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

# --- 3. FIELD_RUN refuses a store the synthetic rig wrote --------------------------------------------------
foreach ($name in 'Get-GeneratedStoreMarkers', 'Assert-FieldRunStoreIsNotGenerated') {
    $definition = Get-AstFunction $demandAst $name
    if ($null -eq $definition) { throw "The demand-bearing runner defines no $name." }
    . ([scriptblock]::Create($definition.Extent.Text))
}
$baselineStatement = @($afterTry.Try.Body.Statements | Where-Object {
        $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $_.Left.Extent.Text -eq '$baseline' -and $_.Right.Extent.Text -match '^Read-ControlDatabase$'
    })
$guardStatement = if ($baselineStatement.Count -eq 1) {
    $statements = @($afterTry.Try.Body.Statements)
    $statements[[array]::IndexOf($statements, $baselineStatement[0]) + 1]
} else { $null }
Check 'the statement after $baseline = Read-ControlDatabase is the FIELD_RUN guard over that baseline' `
    ((Get-CommandName $guardStatement) -eq 'Assert-FieldRunStoreIsNotGenerated' -and
     $guardStatement.Extent.Text -match '-StoreSource\s+\$storeSource\b' -and $guardStatement.Extent.Text -match '-Baseline\s+\$baseline\b') `
    "$($baselineStatement.Count) baseline read(s); next: $(${guardStatement}?.Extent.Text)"

function New-Baseline([string]$DemandKey, [string]$VehicleKey, [string]$AgvId) {
    return [ordered]@{
        acceptedDemandRows = @([ordered]@{ demandId = 'd'; transportDemandKey = $DemandKey; demandRevision = 1; status = 'Accepted' })
        vehicleClaimRecordRows = if ($null -eq $VehicleKey) { @() } else {
            @([ordered]@{ journeyId = 'j'; vehicleKey = $VehicleKey; acquiredAt = 'a'; releasedAt = $null }) }
        sessionRecoveryRows = @([ordered]@{ agvId = $AgvId; sessionGeneration = 1; readiness = 'Ready'; reasonCode = 'READY'; protocolCommit = 'p' })
    }
}
# evidence/g3/20261003-cs453-demand-bearing-synthetic-b660f80e/field-path-control/run-result.json, controlDatabaseBaseline.
$generated = @{ Demand = 'L2-SUBLOT-20261003T043100565Z|WIRE_TO_GATE'; Vehicle = 'BROKERX-L2-0001'; Agv = 'AGV-L2-001' }
# The 2026-08-29 field store: evidence/g3/20260922-protocol-v2.0.0-demand-bearing-82bfa415/run-result.json (demand key,
# agvId; that store predates the claim records) and evidence/g3/20260901-plaintext-binding-g3-runs/demand-bearing-vectors
# (vehicle key).
$field = @{ Demand = 'Q26081298-1|WIRE_TO_GATE'; Vehicle = 'BROKERX-0c20ff0600d644869a6a80c186065d85'; Agv = '老厂前线新多仓位1' }

$guardCases = @(
    @{ Name = 'cs#453''s generated store under FIELD_RUN'; Source = 'FIELD_RUN'; Baseline = (New-Baseline $generated.Demand $generated.Vehicle $generated.Agv); Refused = 3 },
    @{ Name = 'only the generated demand key'; Source = 'FIELD_RUN'; Baseline = (New-Baseline $generated.Demand $field.Vehicle $field.Agv); Refused = 1 },
    @{ Name = 'only the generated vehicle key'; Source = 'FIELD_RUN'; Baseline = (New-Baseline $field.Demand $generated.Vehicle $field.Agv); Refused = 1 },
    @{ Name = 'only the generated agvId'; Source = 'FIELD_RUN'; Baseline = (New-Baseline $field.Demand $field.Vehicle $generated.Agv); Refused = 1 },
    @{ Name = 'the 2026-08-29 field store'; Source = 'FIELD_RUN'; Baseline = (New-Baseline $field.Demand $field.Vehicle $field.Agv); Refused = 0 },
    @{ Name = 'the 2026-08-29 field store without claim records'; Source = 'FIELD_RUN'; Baseline = (New-Baseline $field.Demand $null $field.Agv); Refused = 0 },
    @{ Name = 'the generated store under SYNTHETIC_RIG'; Source = 'SYNTHETIC_RIG'; Baseline = (New-Baseline $generated.Demand $generated.Vehicle $generated.Agv); Refused = 0 }
)
foreach ($case in $guardCases) {
    $thrown = $null
    try { Assert-FieldRunStoreIsNotGenerated -StoreSource $case.Source -Baseline $case.Baseline } catch { $thrown = $_.Exception.Message }
    if ($case.Refused -gt 0) {
        $named = ([regex]::Matches("$thrown", '(TransportDemandKey|VehicleKey|AgvId)=')).Count
        Check "FIELD_RUN guard, $($case.Name): refused, naming $($case.Refused) marker(s)" `
            ($thrown -like 'FIELD_RUN_STORE_IS_GENERATED:*' -and $named -eq $case.Refused) "$thrown"
    } else {
        Check "FIELD_RUN guard, $($case.Name): accepted" ($null -eq $thrown) "$thrown"
    }
}

# --- 4. what the runner actually ran (control-server#466) --------------------------------------------------
# Get-G3RunnerProvenance against a real throwaway repository carrying copies of the runner scripts, committed.
# Each mutation of that repository the ticket names -- a locally edited default, a dirty worktree, a run through
# another copy of the shared runner -- and the two git hides from status (assume-unchanged, skip-worktree) must
# withhold the pass; the committed, clean, default-input repository must not. Then the same function from outside
# any repository: unknown, withheld.
$provenanceWork = Join-Path ([IO.Path]::GetTempPath()) ("g3-provenance-" + [guid]::NewGuid().ToString('n').Substring(0, 8))
$runnerRepository = Join-Path $provenanceWork 'repo'
$runnerScripts = Join-Path $runnerRepository 'scripts'
$gitAs = @('-c', 'user.name=selfcheck', '-c', 'user.email=selfcheck@invalid', '-c', 'commit.gpgsign=false')
function Get-Graded($Provenance) {
    return Get-G3FormalSlicePass -RunKind 'STAGED_G3_REAL_PEERS_DETERMINISTIC_PLAINTEXT' -SliceStatus 'PASS' -Commits (
        [ordered]@{ controlServerCommitSource = 'SHARED_BINDING'; runnerSource = $Provenance.runnerSource })
}
try {
    New-Item -ItemType Directory -Path $runnerScripts, (Join-Path $runnerRepository 'evidence\g3') | Out-Null
    foreach ($name in 'run-staged-g3.ps1', 'run-staged-g3-restart.ps1', 'g3-slice-evidence.ps1') {
        Copy-Item -LiteralPath (Join-Path $ScriptRoot $name) -Destination (Join-Path $runnerScripts $name)
    }
    $trackedEvidence = Join-Path $runnerRepository 'evidence\g3\SUMMARY.md'
    Set-Content -LiteralPath $trackedEvidence -Value 'committed evidence'
    & git -C $runnerRepository init --quiet 2>&1 | Out-Null
    & git -C $runnerRepository add --all 2>&1 | Out-Null
    & git -C $runnerRepository @gitAs commit --quiet -m runner 2>&1 | Out-Null
    $stagedCopy = Join-Path $runnerScripts 'run-staged-g3.ps1'
    $committedText = Get-Content -Raw -LiteralPath $stagedCopy
    $defaultInputs = { [ordered]@{
            SharedRunnerSource = @{ Given = $stagedCopy; Default = (Join-Path $runnerScripts 'run-staged-g3.ps1') }
            ControlServerRepository = @{ Given = $runnerRepository; Default = (Split-Path -Parent $runnerScripts) }
        } }

    $clean = Get-G3RunnerProvenance -ScriptRoot $runnerScripts -Inputs (& $defaultInputs)
    Check 'provenance, the committed runner: COMMITTED_RUNNER, clean, the binding read back out of HEAD' `
        ($clean.runnerSource -ceq 'COMMITTED_RUNNER' -and $clean.runnerWorktreeClean -eq $true -and
         $clean.runnerCommit -eq (& git -C $runnerRepository rev-parse HEAD).Trim() -and
         ($clean.bindingAtHead | ConvertTo-Json -Compress) -eq ($binding | ConvertTo-Json -Compress)) "$($clean | ConvertTo-Json -Compress)"
    Check 'provenance, the committed runner: a PASS slice is a formal pass' ((Get-Graded $clean).formalSlicePass -eq $true) "$((Get-Graded $clean) | ConvertTo-Json -Compress)"

    # The ticket's first finding: the default edited on disk, run with no parameter.
    $edited = $committedText.Replace($binding['ControlServerCommit'], '1' * 40)
    Set-Content -LiteralPath $stagedCopy -Value $edited -NoNewline
    $dirty = Get-G3RunnerProvenance -ScriptRoot $runnerScripts -Inputs (& $defaultInputs)
    $diskBinding = Get-SharedCommitBinding -Path $stagedCopy
    $sources = Get-G3CommitSources -Binding ($dirty.bindingAtHead ?? $diskBinding) -Actual $diskBinding
    Check 'provenance, a locally edited default: RUNNER_WORKTREE_DIRTY, and the binding is still HEAD''s' `
        ($dirty.runnerSource -ceq 'RUNNER_WORKTREE_DIRTY' -and $dirty.runnerWorktreeClean -eq $false -and
         $dirty.bindingAtHead['ControlServerCommit'] -eq $binding['ControlServerCommit']) "$($dirty | ConvertTo-Json -Compress)"
    Check 'provenance, a locally edited default: the edited commit is SELF_CHECK_OVERRIDE against HEAD' `
        ($sources['controlServerCommitSource'] -eq 'SELF_CHECK_OVERRIDE' -and $sources['onboardCommitSource'] -eq 'SHARED_BINDING') "$($sources | ConvertTo-Json -Compress)"
    Check 'provenance, a locally edited default: withheld' ((Get-Graded $dirty).formalSlicePass -eq $false) "$((Get-Graded $dirty) | ConvertTo-Json -Compress)"

    # The same edit hidden from git status, twice over. The premise is checked: status really says nothing.
    foreach ($flag in '--assume-unchanged', '--skip-worktree') {
        & git -C $runnerRepository update-index $flag scripts/run-staged-g3.ps1 2>&1 | Out-Null
        $statusLines = @(& git -C $runnerRepository status --porcelain)
        $hidden = Get-G3RunnerProvenance -ScriptRoot $runnerScripts -Inputs (& $defaultInputs)
        Check "provenance, a locally edited default behind $flag (premise: git status is empty)" ($statusLines.Count -eq 0) ($statusLines -join '; ')
        Check "provenance, a locally edited default behind ${flag}: RUNNER_WORKTREE_DIRTY, withheld" `
            ($hidden.runnerSource -ceq 'RUNNER_WORKTREE_DIRTY' -and (Get-Graded $hidden).formalSlicePass -eq $false) "$($hidden | ConvertTo-Json -Compress)"
        & git -C $runnerRepository update-index ($flag -replace '^--', '--no-') scripts/run-staged-g3.ps1 2>&1 | Out-Null
    }
    Set-Content -LiteralPath $stagedCopy -Value $committedText -NoNewline

    # Dirty with a file nobody committed, the run's own runner untouched.
    $stray = Join-Path $runnerScripts 'stray.txt'
    Set-Content -LiteralPath $stray -Value 'x'
    $untracked = Get-G3RunnerProvenance -ScriptRoot $runnerScripts -Inputs (& $defaultInputs)
    Check 'provenance, an untracked file outside evidence/: RUNNER_WORKTREE_DIRTY naming it, withheld' `
        ($untracked.runnerSource -ceq 'RUNNER_WORKTREE_DIRTY' -and (Get-Graded $untracked).formalSlicePass -eq $false -and
         @($untracked.runnerDirtyPaths) -ccontains '?? scripts/stray.txt') "$($untracked | ConvertTo-Json -Compress)"
    # Review S2: printed loudly at the start, with the reason and the path.
    $notice = (Write-G3RunnerProvenance -Provenance $untracked 6>&1 | Out-String)
    Check 'provenance printed when not COMMITTED_RUNNER: the warning, the reason and the dirty path' `
        ($notice -like '*NOT COMMITTED_RUNNER*' -and $notice -like '*RUNNER_WORKTREE_DIRTY*' -and $notice -like '*scripts/stray.txt*') $notice
    Remove-Item -LiteralPath $stray
    $quiet = (Write-G3RunnerProvenance -Provenance $clean 6>&1 | Out-String)
    Check 'provenance printed when COMMITTED_RUNNER: one line, no warning' `
        ($quiet -like '*COMMITTED_RUNNER*' -and $quiet -notlike '*NOT COMMITTED_RUNNER*' -and @($quiet.Trim() -split "`n").Count -eq 1) $quiet

    # Review M1 of PR #470: an earlier run's evidence, untracked under evidence/, does not make the next run dirty.
    # Nested, as a runner writes it. A change to evidence/ that git tracks still does.
    $earlierRun = Join-Path $runnerRepository 'evidence\g3\earlier-run\process-restart'
    New-Item -ItemType Directory -Path $earlierRun | Out-Null
    Set-Content -LiteralPath (Join-Path $earlierRun 'run-result.json') -Value '{}'
    $afterEarlierRun = Get-G3RunnerProvenance -ScriptRoot $runnerScripts -Inputs (& $defaultInputs)
    Check 'provenance, an earlier run''s untracked evidence under evidence/: COMMITTED_RUNNER, a formal pass' `
        ($afterEarlierRun.runnerSource -ceq 'COMMITTED_RUNNER' -and $afterEarlierRun.runnerWorktreeClean -eq $true -and
         (Get-Graded $afterEarlierRun).formalSlicePass -eq $true) "$($afterEarlierRun | ConvertTo-Json -Compress)"
    Set-Content -LiteralPath $trackedEvidence -Value 'edited evidence'
    $editedEvidence = Get-G3RunnerProvenance -ScriptRoot $runnerScripts -Inputs (& $defaultInputs)
    Check 'provenance, a tracked file under evidence/ changed: RUNNER_WORKTREE_DIRTY naming it, withheld' `
        ($editedEvidence.runnerSource -ceq 'RUNNER_WORKTREE_DIRTY' -and (Get-Graded $editedEvidence).formalSlicePass -eq $false -and
         @($editedEvidence.runnerDirtyPaths | Where-Object { $_ -like '*evidence/g3/SUMMARY.md' }).Count -eq 1) "$($editedEvidence | ConvertTo-Json -Compress)"
    Set-Content -LiteralPath $trackedEvidence -Value 'committed evidence'
    # A file named like evidence/ but beside it is not under it.
    $lookalike = Join-Path $runnerRepository 'evidence-copy.ps1'
    Set-Content -LiteralPath $lookalike -Value 'x'
    $besideEvidence = Get-G3RunnerProvenance -ScriptRoot $runnerScripts -Inputs (& $defaultInputs)
    Check 'provenance, an untracked file beside evidence/ (evidence-copy.ps1): RUNNER_WORKTREE_DIRTY' `
        ($besideEvidence.runnerSource -ceq 'RUNNER_WORKTREE_DIRTY') "$($besideEvidence | ConvertTo-Json -Compress)"
    Remove-Item -LiteralPath $lookalike

    # control-server#567: run-staged-g3.ps1's harnessWorktreeCleanAtStart takes the same exemption as the provenance
    # above -- the four staged runs of the batch-8 exit recorded harness false beside runner true. Its own statement,
    # from the AST, run against the throwaway repository: an earlier run's untracked evidence alone is clean; an
    # untracked file elsewhere, or a tracked file under evidence/ changed, is not.
    $stagedAst = Get-RunnerAst (Join-Path $ScriptRoot 'run-staged-g3.ps1')
    $harnessStatements = @($stagedAst.EndBlock.Statements | Where-Object {
            $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$harnessWorktreeClean' })
    Check 'harness clean: run-staged-g3.ps1 assigns $harnessWorktreeClean once, at top level' ($harnessStatements.Count -eq 1) "$($harnessStatements.Count)"
    if ($harnessStatements.Count -eq 1) {
        $harnessStatement = $harnessStatements[0]
        $topLevel = @($stagedAst.EndBlock.Statements)
        $sharedSource = @($topLevel | Where-Object { $_.Extent.Text -like ". (Join-Path `$PSScriptRoot 'g3-slice-evidence.ps1')*" })
        $firstWrite = @($topLevel | Where-Object { $_.Extent.Text -match '\$(StageRoot|EvidenceRoot)\b' -and
                $_.Extent.Text -match 'New-Item|Set-Content|Out-File|Copy-Item|WriteAll' }) | Select-Object -First 1
        Check 'harness clean: measured after g3-slice-evidence.ps1 is loaded and before the run writes anything' `
            ($sharedSource.Count -eq 1 -and $topLevel.IndexOf($sharedSource[0]) -lt $topLevel.IndexOf($harnessStatement) -and
             ($null -eq $firstWrite -or $topLevel.IndexOf($harnessStatement) -lt $topLevel.IndexOf($firstWrite))) `
            "harness at $($topLevel.IndexOf($harnessStatement)), shared source at $(@($sharedSource | ForEach-Object { $topLevel.IndexOf($_) }) -join ','), first write at $(if ($firstWrite) { $topLevel.IndexOf($firstWrite) })"
        $measureHarness = {
            $ControlServerRepository = $runnerRepository
            $harnessWorktreeClean = $null
            . ([scriptblock]::Create($harnessStatement.Extent.Text))
            $harnessWorktreeClean
        }
        $harnessWithEvidence = & $measureHarness
        Check 'harness clean: an earlier run''s untracked evidence under evidence/ alone is clean' ($harnessWithEvidence -eq $true) "$harnessWithEvidence"
        Set-Content -LiteralPath $stray -Value 'x'
        $harnessWithStray = & $measureHarness
        Check 'harness clean: that evidence plus an untracked file outside evidence/ is dirty' ($harnessWithStray -eq $false) "$harnessWithStray"
        Remove-Item -LiteralPath $stray
        Set-Content -LiteralPath $lookalike -Value 'x'
        $harnessBeside = & $measureHarness
        Check 'harness clean: an untracked file beside evidence/ (evidence-copy.ps1) is dirty' ($harnessBeside -eq $false) "$harnessBeside"
        Remove-Item -LiteralPath $lookalike
        Set-Content -LiteralPath $trackedEvidence -Value 'edited evidence'
        $harnessEditedEvidence = & $measureHarness
        Check 'harness clean: a tracked file under evidence/ changed is dirty' ($harnessEditedEvidence -eq $false) "$harnessEditedEvidence"
        Set-Content -LiteralPath $trackedEvidence -Value 'committed evidence'
    }
    Remove-Item -LiteralPath (Join-Path $runnerRepository 'evidence\g3\earlier-run') -Recurse -Force

    # The ticket's second finding: -SharedRunnerSource pointing at a copy. One with the defaults left alone and
    # another harness -- the binding comparison cannot see that one -- and one with another default.
    $copyRoot = Join-Path $provenanceWork 'copy'
    New-Item -ItemType Directory -Path $copyRoot | Out-Null
    foreach ($copyCase in @(
            @{ Name = 'a copy keeping the defaults'; Text = $committedText.Replace('class StagedG3TlsHarness', 'class StagedG3TlsHarness /* edited */') },
            @{ Name = 'a copy with another default'; Text = $committedText.Replace($binding['OnboardCommit'], '2' * 40) })) {
        $copy = Join-Path $copyRoot ([guid]::NewGuid().ToString('n') + '.ps1')
        Set-Content -LiteralPath $copy -Value $copyCase.Text -NoNewline
        $inputs = & $defaultInputs
        $inputs['SharedRunnerSource'] = @{ Given = $copy; Default = (Join-Path $runnerScripts 'run-staged-g3.ps1') }
        $throughCopy = Get-G3RunnerProvenance -ScriptRoot $runnerScripts -Inputs $inputs
        $copySources = Get-G3CommitSources -Binding ($throughCopy.bindingAtHead ?? (Get-SharedCommitBinding -Path $copy)) -Actual (Get-SharedCommitBinding -Path $copy)
        Check "provenance, -SharedRunnerSource at $($copyCase.Name): RUNNER_INPUT_OVERRIDE: SharedRunnerSource, withheld" `
            ($throughCopy.runnerSource -ceq 'RUNNER_INPUT_OVERRIDE: SharedRunnerSource' -and $throughCopy.runnerWorktreeClean -eq $true -and
             (Get-Graded $throughCopy).formalSlicePass -eq $false) "$($throughCopy | ConvertTo-Json -Compress)"
        if ($copyCase.Name -like '*another default*') {
            Check "provenance, -SharedRunnerSource at $($copyCase.Name): that commit is SELF_CHECK_OVERRIDE against HEAD" `
                ($copySources['onboardCommitSource'] -eq 'SELF_CHECK_OVERRIDE') "$($copySources | ConvertTo-Json -Compress)"
        }
    }
    # The default, spelled differently, is still the default; an empty value is not.
    Push-Location $runnerRepository
    try {
        $inputs = & $defaultInputs
        $inputs['SharedRunnerSource'] = @{ Given = '.\scripts\run-staged-g3.ps1'; Default = (Join-Path $runnerScripts 'run-staged-g3.ps1') }
        $inputs['ControlServerRepository'] = @{ Given = "$runnerRepository\"; Default = (Split-Path -Parent $runnerScripts) }
        $spelled = Get-G3RunnerProvenance -ScriptRoot $runnerScripts -Inputs $inputs
        Check 'provenance, the default paths spelled relative and with a trailing separator: COMMITTED_RUNNER' `
            ($spelled.runnerSource -ceq 'COMMITTED_RUNNER') "$($spelled | ConvertTo-Json -Compress)"
        $inputs['ControlServerRepository'] = @{ Given = ''; Default = (Split-Path -Parent $runnerScripts) }
        $empty = Get-G3RunnerProvenance -ScriptRoot $runnerScripts -Inputs $inputs
        Check 'provenance, an empty -ControlServerRepository: RUNNER_INPUT_OVERRIDE' `
            ($empty.runnerSource -ceq 'RUNNER_INPUT_OVERRIDE: ControlServerRepository') "$($empty | ConvertTo-Json -Compress)"
    } finally {
        Pop-Location
    }

    # Dirty and through a copy at once: both reasons, dirty first.
    Set-Content -LiteralPath $stray -Value 'x'
    $inputs = & $defaultInputs
    $inputs['SharedRunnerSource'] = @{ Given = $copy; Default = (Join-Path $runnerScripts 'run-staged-g3.ps1') }
    $both = Get-G3RunnerProvenance -ScriptRoot $runnerScripts -Inputs $inputs
    Check 'provenance, dirty and through a copy: both reasons' `
        ($both.runnerSource -ceq 'RUNNER_WORKTREE_DIRTY; RUNNER_INPUT_OVERRIDE: SharedRunnerSource') "$($both | ConvertTo-Json -Compress)"
    Remove-Item -LiteralPath $stray

    # HEAD without the binding: a commit that dropped run-staged-g3.ps1 from the index, the file still on disk.
    & git -C $runnerRepository rm --cached --quiet scripts/run-staged-g3.ps1 2>&1 | Out-Null
    & git -C $runnerRepository @gitAs commit --quiet -m drop 2>&1 | Out-Null
    $noBinding = Get-G3RunnerProvenance -ScriptRoot $runnerScripts -Inputs (& $defaultInputs)
    Check 'provenance, HEAD without run-staged-g3.ps1: RUNNER_PROVENANCE_UNKNOWN, no binding, withheld' `
        ($noBinding.runnerSource -clike 'RUNNER_PROVENANCE_UNKNOWN: *' -and $null -eq $noBinding.bindingAtHead -and
         (Get-Graded $noBinding).formalSlicePass -eq $false) "$($noBinding | ConvertTo-Json -Compress)"

    # A repository with no commit yet (unborn HEAD): unknown, and the reason on one line although git's runs to two.
    $unborn = Join-Path $provenanceWork 'unborn'
    New-Item -ItemType Directory -Path (Join-Path $unborn 'scripts') | Out-Null
    & git -C $unborn init --quiet 2>&1 | Out-Null
    $noHead = Get-G3RunnerProvenance -ScriptRoot (Join-Path $unborn 'scripts')
    Check 'provenance, an unborn HEAD: RUNNER_PROVENANCE_UNKNOWN on one line, withheld' `
        ($noHead.runnerSource -clike 'RUNNER_PROVENANCE_UNKNOWN: *' -and $noHead.runnerSource -notmatch '[\r\n]' -and
         (Get-Graded $noHead).formalSlicePass -eq $false) "$($noHead | ConvertTo-Json -Compress)"

    # Outside any repository.
    $loose = Join-Path $provenanceWork 'loose'
    New-Item -ItemType Directory -Path $loose | Out-Null
    $env:GIT_CEILING_DIRECTORIES = $provenanceWork
    try { $unknown = Get-G3RunnerProvenance -ScriptRoot $loose } finally { Remove-Item Env:GIT_CEILING_DIRECTORIES }
    Check 'provenance, outside any repository: RUNNER_PROVENANCE_UNKNOWN, no commit, withheld' `
        ($unknown.runnerSource -clike 'RUNNER_PROVENANCE_UNKNOWN: *' -and $null -eq $unknown.runnerCommit -and
         $null -eq $unknown.runnerWorktreeClean -and (Get-Graded $unknown).formalSlicePass -eq $false) "$($unknown | ConvertTo-Json -Compress)"
} finally {
    Remove-Item -LiteralPath $provenanceWork -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host "G3EvidenceHonesty self-check: $($failures.Count) check(s) came out the other way."
    exit 1
}
Write-Host 'G3EvidenceHonesty self-check: every check as expected.'
