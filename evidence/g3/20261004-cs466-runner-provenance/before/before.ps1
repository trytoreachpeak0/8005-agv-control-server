#Requires -Version 7
# The ticket's findings, reproduced on the base (origin/fp/v2-impl) scripts, offline. Each case evaluates the base
# runners' own statements the way Test-G3EvidenceHonesty.ps1 does, then grades a PASS slice with the base
# Get-G3FormalSlicePass. Before control-server#466 every case says formalSlicePass True.
param([string]$Repo, [string]$BaseRef = 'origin/fp/v2-impl')
$ErrorActionPreference = 'Stop'

$work = Join-Path ([IO.Path]::GetTempPath()) ("cs466-before-" + [guid]::NewGuid().ToString('n').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $base = (& git -C $Repo rev-parse $BaseRef).Trim()
    "base $BaseRef = $base"
    foreach ($name in 'g3-slice-evidence.ps1', 'run-staged-g3.ps1', 'run-staged-g3-restart.ps1', 'run-journey-g3.ps1') {
        & git -C $Repo show "${base}:scripts/$name" | Set-Content -LiteralPath (Join-Path $work $name)
    }
    . (Join-Path $work 'g3-slice-evidence.ps1')
    function Get-Ast($Path) { [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$null) }
    $restartAst = Get-Ast (Join-Path $work 'run-staged-g3-restart.ps1')
    . ([scriptblock]::Create(@($restartAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-SharedCommitBinding' }, $true))[0].Extent.Text))
    $committed = Get-SharedCommitBinding -Path (Join-Path $work 'run-staged-g3.ps1')
    $stagedText = Get-Content -Raw -LiteralPath (Join-Path $work 'run-staged-g3.ps1')

    # 1. run-staged-g3.ps1 with its ControlServerCommit default edited on disk and no parameter: its own
    #    $commitSources statement reads the binding from that same file, so the edited value is the binding.
    $edited = Join-Path $work 'edited\run-staged-g3.ps1'
    New-Item -ItemType Directory -Path (Split-Path -Parent $edited) | Out-Null
    Set-Content -LiteralPath $edited -Value $stagedText.Replace($committed['ControlServerCommit'], '1' * 40) -NoNewline
    $editedBinding = Get-SharedCommitBinding -Path $edited
    $stagedAst = Get-Ast (Join-Path $work 'run-staged-g3.ps1')
    $statement = @($stagedAst.EndBlock.Statements | Where-Object {
            $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$commitSources' })[0]
    $ControlServerCommit = $editedBinding['ControlServerCommit']; $OnboardCommit = $editedBinding['OnboardCommit']
    $SimulatorCommit = $editedBinding['SimulatorCommit']; $ProtocolCommit = $editedBinding['ProtocolCommit']
    . ([scriptblock]::Create($statement.Extent.Text.Replace("(Join-Path `$PSScriptRoot 'run-staged-g3.ps1')", "'$edited'")))
    "1. run-staged-g3.ps1, default edited to $('1' * 40), no parameter"
    "   controlServer used: $ControlServerCommit (committed binding $($committed['ControlServerCommit']))"
    "   commitSources: $($commitSources | ConvertTo-Json -Compress)"
    "   graded: $((Get-G3FormalSlicePass -RunKind 'STAGED_G3_REAL_PEERS_DETERMINISTIC_PLAINTEXT' -SliceStatus 'PASS' -Commits $commitSources) | ConvertTo-Json -Compress)"

    # 2. run-journey-g3.ps1 with -SharedRunnerSource at a copy whose OnboardCommit default differs: the commits come
    #    from the copy, and the base sources are the literal SHARED_BINDING unless a -SelfCheck* parameter is given.
    $copy = Join-Path $work 'copy\run-staged-g3.ps1'
    New-Item -ItemType Directory -Path (Split-Path -Parent $copy) | Out-Null
    Set-Content -LiteralPath $copy -Value $stagedText.Replace($committed['OnboardCommit'], '2' * 40) -NoNewline
    $journeyAst = Get-Ast (Join-Path $work 'run-journey-g3.ps1')
    $statements = @($journeyAst.EndBlock.Statements | Where-Object {
            ($_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
             ($_.Left.Extent.Text -in '$commitBinding', '$ControlServerCommit', '$OnboardCommit', '$SimulatorCommit', '$ProtocolCommit' -or
              $_.Left.Extent.Text -like '*CommitSource')) -or
            ($_ -is [System.Management.Automation.Language.IfStatementAst] -and $_.Clauses[0].Item1.Extent.Text -match '\$SelfCheck\w*Commit\b')
        })
    $SharedRunnerSource = $copy; $SelfCheckControlServerCommit = $null; $SelfCheckOnboardCommit = $null
    . ([scriptblock]::Create((@($statements | ForEach-Object { $_.Extent.Text }) -join "`n")))
    $record = [ordered]@{ controlServerCommitSource = $controlServerCommitSource; onboardCommitSource = $onboardCommitSource }
    "2. run-journey-g3.ps1 -SharedRunnerSource <a copy whose OnboardCommit default is $('2' * 40)>"
    "   onboard used: $OnboardCommit (committed binding $($committed['OnboardCommit']))"
    "   sources: $($record | ConvertTo-Json -Compress)"
    "   graded: $((Get-G3FormalSlicePass -RunKind 'JOURNEY_G3_REAL_ONBOARD_SIMULATED_COUNTERPARTS' -SliceStatus 'PASS' -Commits $record) | ConvertTo-Json -Compress)"

    # 3. A dirty runner worktree: the base records carry runnerWorktreeCleanAtStart, and grading never reads it.
    $dirtyRecord = [ordered]@{ controlServerCommitSource = 'SHARED_BINDING'; onboardCommitSource = 'SHARED_BINDING'; runnerWorktreeCleanAtStart = $false }
    "3. a record with runnerWorktreeCleanAtStart false (the 2026-09-22 formal evidence's shape)"
    "   graded: $((Get-G3FormalSlicePass -RunKind 'JOURNEY_G3_REAL_ONBOARD_SIMULATED_COUNTERPARTS' -SliceStatus 'PASS' -Commits $dirtyRecord) | ConvertTo-Json -Compress)"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
