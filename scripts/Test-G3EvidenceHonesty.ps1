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
       is graded by g3-slice-evidence.ps1 -- New-G3Classification and Write-G3GateResult, both -- under five
       commits records: the staged shape (no source), SHARED_BINDING, a ControlServer override, an onboard
       override, and a source nobody has defined. Only the first two may say formalSlicePass true; the
       overrides must say false with formalSliceWithheldReason SELF_CHECK_OVERRIDE while status stays PASS;
       the unknown source must be withheld too. The all-PASS premise is pinned by the first two, so a grading
       that passes nothing cannot satisfy the rest.
       Then the wiring, read from each runner's AST: every New-G3Classification call passes -Commits
       $commitsRecord, the gate results' Context carries commits = $commitsRecord, and every runner's record
       carries its *CommitSource keys. run-staged-g3.ps1's own $commitSources statement is then run with its
       param defaults and with each of the four commits replaced in turn: SHARED_BINDING four times, then
       SELF_CHECK_OVERRIDE for the replaced one, which withholds a PASS slice.

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
    @{ Name = 'no commit source (the staged shape)'; Commits = [ordered]@{ controlServer = 'a' * 40 }; Formal = $true; Reason = $null },
    @{ Name = 'SHARED_BINDING'; Commits = [ordered]@{ controlServer = 'a' * 40; controlServerCommitSource = 'SHARED_BINDING'; onboardCommitSource = 'SHARED_BINDING' }; Formal = $true; Reason = $null },
    @{ Name = 'a ControlServer self-check override'; Commits = [ordered]@{ controlServer = 'b' * 40; controlServerCommitSource = 'SELF_CHECK_OVERRIDE' }; Formal = $false; Reason = 'SELF_CHECK_OVERRIDE' },
    @{ Name = 'an onboard self-check override'; Commits = [ordered]@{ controlServer = 'a' * 40; controlServerCommitSource = 'SHARED_BINDING'; onboardCommitSource = 'SELF_CHECK_OVERRIDE' }; Formal = $false; Reason = 'SELF_CHECK_OVERRIDE' },
    @{ Name = 'a source nobody defined'; Commits = [ordered]@{ controlServer = 'a' * 40; controlServerCommitSource = 'SOMETHING_NEW' }; Formal = $false; Reason = 'UNRECOGNISED_COMMIT_SOURCE*' }
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
    $stagedCases = @(@{ Name = 'the defaults'; Override = $null }) + @($sourceNames.Keys | ForEach-Object { @{ Name = "-$_ on the command line"; Override = $_ } })
    foreach ($stagedCase in $stagedCases) {
        $ControlServerCommit = $binding['ControlServerCommit']; $OnboardCommit = $binding['OnboardCommit']
        $SimulatorCommit = $binding['SimulatorCommit']; $ProtocolCommit = $binding['ProtocolCommit']
        if ($null -ne $stagedCase.Override) { Set-Variable -Name $stagedCase.Override -Value ('f' * 40) }
        $commitSources = $null
        $thrown = $null
        try { . $sourcesStatement } catch { $thrown = $_.Exception.Message }
        $expected = [ordered]@{}
        foreach ($k in $sourceNames.Keys) { $expected[$sourceNames[$k]] = if ($k -eq $stagedCase.Override) { 'SELF_CHECK_OVERRIDE' } else { 'SHARED_BINDING' } }
        $actualJson = if ($null -ne $commitSources) { $commitSources | ConvertTo-Json -Compress } else { 'null' }
        Check "run-staged-g3.ps1's commit sources, $($stagedCase.Name): $(($expected.Values | Select-Object -Unique) -join '/')" `
            ($null -eq $thrown -and $actualJson -eq ($expected | ConvertTo-Json -Compress)) "$thrown $actualJson"
        if ($null -ne $stagedCase.Override -and $null -ne $commitSources) {
            $graded = Get-G3FormalSlicePass -RunKind 'STAGED_G3_REAL_PEERS_DETERMINISTIC_PLAINTEXT' -SliceStatus 'PASS' -Commits $commitSources
            Check "run-staged-g3.ps1, $($stagedCase.Name): a PASS slice is withheld as SELF_CHECK_OVERRIDE" `
                ($graded.formalSlicePass -eq $false -and $graded.formalSliceWithheldReason -eq 'SELF_CHECK_OVERRIDE') "$($graded | ConvertTo-Json -Compress)"
        }
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

if ($failures.Count -gt 0) {
    Write-Host "G3EvidenceHonesty self-check: $($failures.Count) check(s) came out the other way."
    exit 1
}
Write-Host 'G3EvidenceHonesty self-check: every check as expected.'
