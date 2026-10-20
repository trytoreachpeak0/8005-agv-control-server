#Requires -Version 7

<#
.SYNOPSIS
    Self-check that a reused runner workspace never leaves a job with half a tree: the sparse-checkout state an earlier
    job left behind, and actions/checkout@v7 not clearing it.

.DESCRIPTION
    What went wrong (l2 run 39457961360, both attempts, 2026-10-20). Every job of this repository on one runner checks
    out into the same directory, whatever its branch or workflow. actions/checkout@v7 with
    sparse-checkout-cone-mode: false runs `git config core.sparseCheckout true`, which lands in .git/config. A later job
    that asks for no sparse checkout runs `git sparse-checkout disable` and then `git config --unset-all
    extensions.worktreeConfig`: disable writes core.sparseCheckout=false into .git/config.worktree, and unsetting the
    extension hides that file again, so the true in .git/config is back in force with whatever patterns are left in
    .git/info/sparse-checkout. The next `git checkout --force` applies them. On vm-win-01's headless runner they were a
    g3 gate job's cone patterns (`scripts`) read in non-cone mode: the checkout printed "Path '...' not uptodate; will
    not remove from working tree." for the files the new tree drops, exited 0, moved HEAD to the right commit and left
    nearly every file out, ControlServer.sln among them -- MSB1009 in the next step. On 2026-10-21 three of the six
    runner workspaces on vm-win-01 had core.sparseCheckout=true in .git/config.

    The fix, which this holds the workflows to:
      - no checkout of this repository uses sparse-checkout-cone-mode: false (the only thing that writes the stale
        setting); one that leaves out evidence/ lists every other top-level directory in cone mode instead;
      - every checkout of this repository on a self-hosted runner is preceded by the step "Clear sparse-checkout state
        an earlier job left behind" (the same script everywhere; CHECKOUT_PATH is the checkout's path) and followed by
        "Check the checkout is the tree it asked for", which runs Assert-CheckoutTree.ps1 with CHECKOUT_SPARSE set to
        the checkout's own sparse-checkout list.

    What is checked:
      1. The workflows, statically: the rules above, for every job of .github/workflows/*.yml that runs on
         [self-hosted, ...] and every actions/checkout step there without a repository: input.
      2. The runner history of run 39457961360, on a throwaway repository with actions/checkout@v7's own git commands
         (the sequence its log shows): l2.yml's scenarios checkout, g3.yml's gate checkout, then a full checkout of an
         older commit the way main's workflows and an older pull request do it, with no step of ours around it. Each
         of our checkouts runs with the steps its workflow puts around it. The last tree must be whole.
      3. A workspace poisoned the way three on vm-win-01 are, before each of our checkouts in turn: with its steps
         around it the checkout is the tree it asked for, and so is a plain full checkout after it.
      4. Assert-CheckoutTree.ps1 on its own: green on a whole tree and on a cone checkout, red naming the cause on the
         half tree, on a file deleted from the worktree and on a cone list the checkout did not apply.

    Seconds; everything under the system temp directory. Exit 0 when every case passes, 1 otherwise.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/Test-CheckoutSparseState.ps1
#>
[CmdletBinding()]
param(
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

$healName = 'Clear sparse-checkout state an earlier job left behind'
$assertName = 'Check the checkout is the tree it asked for'
$assertScript = Join-Path $Repository 'scripts/Assert-CheckoutTree.ps1'

# --- Reading the workflows. ---------------------------------------------------------------------------------------------
# Just enough YAML for these files: a job's runs-on and its steps; a step's scalar keys, its with: and env: maps, and
# block scalars (|) at either level. Comments outside a block scalar are skipped.
function Read-BlockScalar([string[]]$Lines, [ref]$Index, [int]$Indent) {
    $prefix = ' ' * $Indent
    $text = [System.Collections.Generic.List[string]]::new()
    while ($Index.Value -lt $Lines.Count -and ($Lines[$Index.Value].StartsWith($prefix) -or $Lines[$Index.Value].Trim() -eq '')) {
        $line = $Lines[$Index.Value]
        $text.Add($(if ($line.Length -ge $Indent) { $line.Substring($Indent) } else { '' }))
        $Index.Value++
    }
    return ($text -join "`n").TrimEnd()
}
function Get-Scalar([string]$Value) {
    $v = $Value.Trim()
    if ($v -match "^'(.*)'$") { return $Matches[1] -replace "''", "'" }
    if ($v -match '^"(.*)"$') { return $Matches[1] }
    return $v
}
function ConvertFrom-StepBlock([string]$Block) {
    $lines = @($Block -split '\r?\n')
    $lines[0] = $lines[0] -replace '^      - ', '        '
    $map = [ordered]@{}
    $i = 0
    while ($i -lt $lines.Count) {
        if ($lines[$i] -notmatch '^        ([A-Za-z0-9_-]+):\s*(.*)$') { $i++; continue }
        $key = $Matches[1]; $value = $Matches[2]; $i++
        if ($value -eq '|') { $map[$key] = Read-BlockScalar $lines ([ref]$i) 10; continue }
        if ($value -ne '') { $map[$key] = Get-Scalar $value; continue }
        $sub = [ordered]@{}
        while ($i -lt $lines.Count -and ($lines[$i].StartsWith('          ') -or $lines[$i].Trim() -eq '')) {
            if ($lines[$i] -match '^          ([A-Za-z0-9_-]+):\s*(.*)$') {
                $subKey = $Matches[1]; $subValue = $Matches[2]; $i++
                $sub[$subKey] = if ($subValue -eq '|') { Read-BlockScalar $lines ([ref]$i) 12 } else { Get-Scalar $subValue }
            } else { $i++ }
        }
        $map[$key] = $sub
    }
    return $map
}
function Get-WorkflowJobs([string]$File) {
    $text = (Get-Content -Raw -LiteralPath $File) -replace '\r\n', "`n"
    $jobsText = [regex]::Match($text, '(?ms)^jobs:\n(.*)\z').Groups[1].Value
    foreach ($job in [regex]::Matches($jobsText, '(?ms)^  ([A-Za-z0-9_-]+):\n(.*?)(?=^  [A-Za-z0-9_-]+:\n|\z)')) {
        $body = $job.Groups[2].Value
        $stepsText = [regex]::Match($body, '(?ms)^    steps:\n(.*)\z').Groups[1].Value
        $blocks = @([regex]::Matches($stepsText, '(?ms)^      - .*?(?=^      - |\z)') | ForEach-Object Value)
        [pscustomobject]@{
            Workflow = Split-Path -Leaf $File
            Job      = $job.Groups[1].Value
            RunsOn   = [regex]::Match($body, '(?m)^    runs-on: (.+)$').Groups[1].Value.Trim()
            Steps    = @(foreach ($block in $blocks) { ConvertFrom-StepBlock $block })
        }
    }
}
function Get-With($Step, [string]$Key) {
    if ($Step.Contains('with') -and $Step['with'] -is [System.Collections.IDictionary] -and $Step['with'].Contains($Key)) {
        return [string]$Step['with'][$Key]
    }
    return $null
}
function Get-Env($Step, [string]$Key) {
    if ($Step.Contains('env') -and $Step['env'] -is [System.Collections.IDictionary] -and $Step['env'].Contains($Key)) {
        return [string]$Step['env'][$Key]
    }
    return $null
}
function Split-List([string]$Text) { @(($Text ?? '') -split '\n' | ForEach-Object Trim | Where-Object { $_ }) }

$workflowFiles = @(Get-ChildItem -LiteralPath (Join-Path $Repository '.github/workflows') -Filter '*.yml' | Sort-Object Name)
$jobs = @(foreach ($file in $workflowFiles) { Get-WorkflowJobs $file.FullName })
Check 'the workflows have jobs' ($jobs.Count -gt 5) "$($jobs.Count) jobs"

# Every checkout of this repository on a self-hosted runner, with the steps around it.
$ownCheckouts = @(foreach ($job in $jobs) {
        if ($job.RunsOn -notmatch 'self-hosted') { continue }
        for ($n = 0; $n -lt $job.Steps.Count; $n++) {
            $step = $job.Steps[$n]
            if (-not ($step.Contains('uses') -and $step['uses'] -like 'actions/checkout@*')) { continue }
            if ($null -ne (Get-With $step 'repository')) { continue }
            [pscustomobject]@{
                Label    = "$($job.Workflow) $($job.Job)"
                Job      = $job
                Checkout = $step
                Before   = if ($n -gt 0) { $job.Steps[$n - 1] } else { $null }
                After    = if ($n + 1 -lt $job.Steps.Count) { $job.Steps[$n + 1] } else { $null }
                Path     = Get-With $step 'path'
                Sparse   = @(Split-List (Get-With $step 'sparse-checkout'))
                Cone     = (Get-With $step 'sparse-checkout-cone-mode') -ne 'false'
            }
        }
    })
Check 'there are checkouts of this repository on self-hosted runners' ($ownCheckouts.Count -ge 7) "$($ownCheckouts.Count)"
foreach ($expected in 'test.yml test', 'l2.yml scenarios', 'l2.yml real-rig', 'g3.yml gate', 'g3.yml g3', 'g3.yml report',
    'release.yml package') {
    Check "$expected checks out this repository" (@($ownCheckouts | Where-Object Label -ceq $expected).Count -eq 1) 'not found'
}

# --- 1. The workflows. ---------------------------------------------------------------------------------------------------
foreach ($file in $workflowFiles) {
    Check "$($file.Name) never uses sparse-checkout-cone-mode: false" (
        (Get-Content -Raw -LiteralPath $file.FullName) -notmatch '(?m)^\s*sparse-checkout-cone-mode:\s*false') (
        'it writes core.sparseCheckout into .git/config, which the next job''s sparse-checkout disable cannot clear')
}
$topLevel = @(& git -C $Repository ls-tree -d --name-only HEAD | Where-Object { $_ -cne 'evidence' })
Check 'the repository has top-level directories besides evidence/' ($topLevel.Count -ge 5) ($topLevel -join ', ')
$healRuns = [System.Collections.Generic.List[string]]::new()
$assertRuns = [System.Collections.Generic.List[string]]::new()
foreach ($c in $ownCheckouts) {
    $before = $c.Before; $after = $c.After
    $healOk = $null -ne $before -and $before.Contains('name') -and $before['name'] -ceq $healName
    Check "$($c.Label): '$healName' comes right before the checkout" $healOk "$(if ($before -and $before.Contains('name')) { $before['name'] } else { '(none)' })"
    if ($healOk) {
        Check "$($c.Label): '$healName' runs in pwsh, on every event" (
            $before['shell'] -ceq 'pwsh' -and -not $before.Contains('if') -and $before.Contains('run')) ($before | Out-String)
        Check "$($c.Label): '$healName' has CHECKOUT_PATH the checkout's path" ((Get-Env $before 'CHECKOUT_PATH') -ceq $c.Path) (
            "CHECKOUT_PATH '$(Get-Env $before 'CHECKOUT_PATH')', path '$($c.Path)'")
        if ($before.Contains('run')) { $healRuns.Add([string]$before['run']) }
    }
    $assertOk = $null -ne $after -and $after.Contains('name') -and $after['name'] -ceq $assertName
    Check "$($c.Label): '$assertName' comes right after the checkout" $assertOk "$(if ($after -and $after.Contains('name')) { $after['name'] } else { '(none)' })"
    if ($assertOk) {
        Check "$($c.Label): '$assertName' runs Assert-CheckoutTree.ps1 in pwsh, on every event" (
            $after['shell'] -ceq 'pwsh' -and -not $after.Contains('if') -and $after.Contains('run') -and
            $after['run'] -cmatch '(?m)^if \(Test-Path -LiteralPath scripts/Assert-CheckoutTree\.ps1\) \{ pwsh -NoProfile -File scripts/Assert-CheckoutTree\.ps1; exit \$LASTEXITCODE \}') ($after | Out-String)
        if ($after.Contains('run')) { $assertRuns.Add([string]$after['run']) }
        Check "$($c.Label): '$assertName' runs in the checkout's path" (
            ($after.Contains('working-directory') ? $after['working-directory'] : $null) -ceq $c.Path) (
            "working-directory '$($after['working-directory'])', path '$($c.Path)'")
        Check "$($c.Label): '$assertName' has CHECKOUT_SPARSE the checkout's sparse-checkout list" (
            ((Split-List (Get-Env $after 'CHECKOUT_SPARSE')) -join '|') -ceq ($c.Sparse -join '|')) (
            "CHECKOUT_SPARSE '$((Split-List (Get-Env $after 'CHECKOUT_SPARSE')) -join ' ')', sparse-checkout '$($c.Sparse -join ' ')'")
    }
    # A checkout that leaves out evidence/ and keeps the build: every other top-level directory, or a new one would be
    # silently missing from it.
    if ($c.Sparse -contains 'src') {
        Check "$($c.Label): its cone list is every top-level directory but evidence/" (
            (@($c.Sparse | Sort-Object) -join '|') -ceq (@($topLevel | Sort-Object) -join '|')) (
            "listed: $($c.Sparse -join ' '); in the repository: $($topLevel -join ' ')")
    }
}
Check "every '$healName' step runs the same script" (@($healRuns | Sort-Object -Unique).Count -le 1) "$(@($healRuns | Sort-Object -Unique).Count) variants"
Check "every '$assertName' step runs the same script" (@($assertRuns | Sort-Object -Unique).Count -le 1) "$(@($assertRuns | Sort-Object -Unique).Count) variants"

# --- A throwaway repository and actions/checkout@v7's git commands. ------------------------------------------------------
$root = Join-Path ([IO.Path]::GetTempPath()) "checkout-sparse-state-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $root
$savedEnv = @{}
foreach ($name in 'GIT_CONFIG_NOSYSTEM', 'GIT_CONFIG_GLOBAL', 'GITHUB_WORKSPACE', 'CHECKOUT_PATH', 'CHECKOUT_SPARSE') {
    $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name)
}
$gitConfig = Join-Path $root 'gitconfig'
Set-Content -LiteralPath $gitConfig -Value "[user]`n`tname = checkout-selfcheck`n`temail = checkout-selfcheck@invalid`n[init]`n`tdefaultBranch = main`n[core]`n`tautocrlf = false`n"
$env:GIT_CONFIG_NOSYSTEM = '1'
$env:GIT_CONFIG_GLOBAL = $gitConfig

function Invoke-Git([string]$Directory, [string[]]$Arguments) {
    $output = & git -C $Directory @Arguments 2>&1
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = @($output | ForEach-Object { "$_" }) }
}
function Write-RepoFile([string]$Directory, [string]$Path, [string]$Content) {
    $full = Join-Path $Directory $Path
    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $full)
    Set-Content -LiteralPath $full -Value $Content -NoNewline
}

try {
    # The older commit (a pull request's base before control-server#803) and the newer one, which adds evidence/cs803/:
    # the files the newer tree has and the older one drops are the ones the log names.
    $source = Join-Path $root 'source'
    $null = New-Item -ItemType Directory -Path $source
    $null = Invoke-Git $source @('init', '-q')
    foreach ($dir in $topLevel) { Write-RepoFile $source "$dir/keep.txt" $dir }
    Write-RepoFile $source 'ControlServer.sln' 'sln'
    $null = Invoke-Git $source @('add', '-A')
    $null = Invoke-Git $source @('commit', '-q', '-m', 'ancient, before the check existed')
    $ancient = (Invoke-Git $source @('rev-parse', 'HEAD')).Output[0]
    # Missing, the cases below that need it come out red on their own (section 4 says it is missing).
    if (Test-Path -LiteralPath $assertScript) {
        Write-RepoFile $source 'scripts/Assert-CheckoutTree.ps1' (Get-Content -Raw -LiteralPath $assertScript)
    }
    Write-RepoFile $source 'ControlServer.sln' 'sln'
    Write-RepoFile $source 'scripts/l2/Invoke-L2Scenario.ps1' 'l2'
    Write-RepoFile $source 'src/ControlServer.Host/Program.cs' 'host'
    Write-RepoFile $source 'evidence/cs584/note.txt' 'old evidence'
    $null = Invoke-Git $source @('add', '-A')
    $null = Invoke-Git $source @('commit', '-q', '-m', 'older')
    $older = (Invoke-Git $source @('rev-parse', 'HEAD')).Output[0]
    foreach ($n in 1..3) { Write-RepoFile $source "evidence/cs803/red/0$n.txt" "red $n" }
    Write-RepoFile $source 'src/ControlServer.Host/Added.cs' 'added'
    $null = Invoke-Git $source @('add', '-A')
    $null = Invoke-Git $source @('commit', '-q', '-m', 'newer')
    $newer = (Invoke-Git $source @('rev-parse', 'HEAD')).Output[0]
    $origin = Join-Path $root 'origin.git'
    $null = & git clone -q --bare $source $origin 2>&1

    # actions/checkout@v7 on a workspace that may already hold an earlier job's checkout (dist/index.js, and the
    # commands its log prints): clean and reset an existing repository, fetch one commit, set up or disable sparse
    # checkout, check out with --force.
    function Invoke-ActionsCheckout([string]$Directory, [string]$Commit, [string[]]$Sparse, [bool]$Cone) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory '.git'))) {
            $null = New-Item -ItemType Directory -Force -Path $Directory
            $null = Invoke-Git $Directory @('init', '-q')
            $null = Invoke-Git $Directory @('remote', 'add', 'origin', $origin)
        } else {
            $null = Invoke-Git $Directory @('clean', '-ffdx')
            $null = Invoke-Git $Directory @('reset', '--hard', 'HEAD')
        }
        $null = Invoke-Git $Directory @('-c', 'protocol.version=2', 'fetch', '--no-tags', '--prune', '--depth=1', 'origin', "+${Commit}:refs/remotes/origin/ci")
        if ($Sparse.Count -eq 0) {
            $null = Invoke-Git $Directory @('sparse-checkout', 'disable')
            $null = Invoke-Git $Directory @('config', '--local', '--unset-all', 'extensions.worktreeConfig')
        } elseif ($Cone) {
            $null = Invoke-Git $Directory (@('sparse-checkout', 'set') + $Sparse)
        } else {
            $null = Invoke-Git $Directory @('config', 'core.sparseCheckout', 'true')
            $patterns = Join-Path $Directory (Invoke-Git $Directory @('rev-parse', '--git-path', 'info/sparse-checkout')).Output[0]
            $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $patterns)
            [IO.File]::AppendAllText($patterns, "`n$($Sparse -join "`n")`n")
        }
        $checkout = Invoke-Git $Directory @('checkout', '--progress', '--force', 'refs/remotes/origin/ci')
        return $checkout
    }
    # A run: step the way GitHub runs a pwsh one: $ErrorActionPreference stop before it, the native exit code after it,
    # in the workspace, with the step's env.
    function Invoke-RunStep($Step, [string]$Workspace, [string]$Directory = $Workspace) {
        $file = Join-Path $root "step-$([guid]::NewGuid().ToString('N')).ps1"
        Set-Content -LiteralPath $file -Value ("`$ErrorActionPreference = 'stop'`n" + $Step['run'] +
            "`nif ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit `$LASTEXITCODE }`n")
        $env:GITHUB_WORKSPACE = $Workspace
        $env:CHECKOUT_PATH = Get-Env $Step 'CHECKOUT_PATH'
        $env:CHECKOUT_SPARSE = Get-Env $Step 'CHECKOUT_SPARSE'
        Push-Location -LiteralPath $Directory
        try { $output = & pwsh -NoProfile -NonInteractive -Command ". '$file'" 2>&1 } finally { Pop-Location }
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = @($output | ForEach-Object { "$_" }) }
    }
    function Invoke-Assert([string]$Directory, [string[]]$Sparse) {
        if (-not (Test-Path -LiteralPath $assertScript)) {
            return [pscustomobject]@{ ExitCode = -1; Output = @("$assertScript is missing") }
        }
        $env:CHECKOUT_SPARSE = $Sparse -join "`n"
        Push-Location -LiteralPath $Directory
        try { $output = & pwsh -NoProfile -NonInteractive -File $assertScript 2>&1 } finally { Pop-Location }
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = @($output | ForEach-Object { "$_" }) }
    }
    # One of our checkouts with the steps its workflow puts around it; the assert result, or $null when there is none.
    function Invoke-OwnCheckout($C, [string]$Workspace, [string]$Commit) {
        $directory = if ($C.Path) { Join-Path $Workspace $C.Path } else { $Workspace }
        # The runner creates the workspace before the first step.
        $null = New-Item -ItemType Directory -Force -Path $Workspace
        $heal = $null
        if ($null -ne $C.Before -and $C.Before.Contains('name') -and $C.Before['name'] -ceq $healName) {
            $heal = Invoke-RunStep $C.Before $Workspace
        }
        $checkout = Invoke-ActionsCheckout $directory $Commit $C.Sparse $C.Cone
        $assert = $null
        if ($null -ne $C.After -and $C.After.Contains('name') -and $C.After['name'] -ceq $assertName) {
            $assert = Invoke-RunStep $C.After $Workspace $directory
        }
        [pscustomobject]@{ Directory = $directory; Heal = $heal; Checkout = $checkout; Assert = $assert }
    }
    # What a tree holds, against what a full checkout of the commit holds.
    function Get-TreeState([string]$Directory, [string]$Commit) {
        $tracked = @((Invoke-Git $Directory @('ls-tree', '-r', '--name-only', $Commit)).Output)
        $missing = @($tracked | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Directory $_)) })
        $skipped = @((Invoke-Git $Directory @('ls-files', '-t')).Output | Where-Object { $_ -clike 'S *' })
        $head = (Invoke-Git $Directory @('rev-parse', 'HEAD')).Output[0]
        $localSparse = (Invoke-Git $Directory @('config', '--file', (Join-Path $Directory '.git/config'), '--get', 'core.sparseCheckout')).Output -join ''
        [pscustomobject]@{ Head = $head; Missing = $missing; Skipped = $skipped; LocalSparse = $localSparse
            Whole = $head -ceq $Commit -and $missing.Count -eq 0 -and $skipped.Count -eq 0 }
    }
    function Show-Tree($State) {
        "HEAD $($State.Head.Substring(0, 9)), $($State.Missing.Count) missing ($((@($State.Missing) | Select-Object -First 3) -join ', ')), " +
        "$($State.Skipped.Count) skip-worktree, core.sparseCheckout in .git/config '$($State.LocalSparse)'"
    }
    $byLabel = @{}
    foreach ($c in $ownCheckouts) { $byLabel[$c.Label] = $c }
    $plainFull = [pscustomobject]@{ Label = 'a plain full checkout'; Before = $null; After = $null; Path = $null; Sparse = @(); Cone = $true }

    # --- 2. Run 39457961360's history on one workspace. -----------------------------------------------------------------
    if ($byLabel.ContainsKey('l2.yml scenarios') -and $byLabel.ContainsKey('g3.yml gate')) {
        $workspace = Join-Path $root 'history'
        $l2 = Invoke-OwnCheckout $byLabel['l2.yml scenarios'] $workspace $newer
        if ($null -ne $l2.Assert) { Check "history: l2.yml scenarios' own check passes" ($l2.Assert.ExitCode -eq 0) ($l2.Assert.Output -join ' | ') }
        $gate = Invoke-OwnCheckout $byLabel['g3.yml gate'] $workspace $newer
        if ($null -ne $gate.Assert) { Check "history: g3.yml gate's own check passes" ($gate.Assert.ExitCode -eq 0) ($gate.Assert.Output -join ' | ') }
        $last = Invoke-OwnCheckout $plainFull $workspace $older
        $state = Get-TreeState $workspace $older
        $notUptodate = @($last.Checkout.Output | Where-Object { $_ -match 'not uptodate; will not remove' })
        Check 'history: l2 scenarios, g3 gate, then a plain full checkout of an older commit leaves the whole tree' $state.Whole (Show-Tree $state)
        Check 'history: that checkout prints no "not uptodate; will not remove from working tree"' ($notUptodate.Count -eq 0) (
            "$($notUptodate.Count) lines, the first: $($notUptodate | Select-Object -First 1)")
        Check 'history: nothing is left in .git/config for a later job' (-not $state.LocalSparse) "core.sparseCheckout '$($state.LocalSparse)'"
    }

    # --- 3. A poisoned workspace before each of our checkouts. ----------------------------------------------------------
    # The state on vm-win-01's headless runner on 2026-10-21: core.sparseCheckout=true in .git/config, and a cone list
    # read as non-cone patterns.
    function New-PoisonedWorkspace([string]$Directory) {
        $null = Invoke-ActionsCheckout $Directory $newer @('/*', '!/evidence/') $false
        $null = Invoke-ActionsCheckout $Directory $newer @('scripts') $true
    }
    foreach ($c in $ownCheckouts) {
        $workspace = Join-Path $root "poisoned-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        New-PoisonedWorkspace $(if ($c.Path) { Join-Path $workspace $c.Path } else { $workspace })
        $run = Invoke-OwnCheckout $c $workspace $older
        $state = Get-TreeState $run.Directory $older
        if ($null -ne $run.Heal) { Check "poisoned, $($c.Label): '$healName' succeeds" ($run.Heal.ExitCode -eq 0) ($run.Heal.Output -join ' | ') }
        $expectedMissing = @(if ($c.Sparse.Count -gt 0) {
                (Invoke-Git $run.Directory @('ls-tree', '-r', '--name-only', $older)).Output | Where-Object {
                    $_.Contains('/') -and ($c.Sparse -notcontains $_.Split('/')[0]) }
            })
        Check "poisoned, $($c.Label): the checkout is the tree it asked for" (
            $state.Head -ceq $older -and (($state.Missing | Sort-Object) -join '|') -ceq (($expectedMissing | Sort-Object) -join '|')) (
            "$(Show-Tree $state); expected missing: $($expectedMissing.Count)")
        Check "poisoned, $($c.Label): its own check passes" ($null -ne $run.Assert -and $run.Assert.ExitCode -eq 0) (
            $(if ($null -eq $run.Assert) { 'no check step' } else { $run.Assert.Output -join ' | ' }))
        $null = Invoke-OwnCheckout $plainFull $run.Directory $newer
        $after = Get-TreeState $run.Directory $newer
        Check "poisoned, $($c.Label): a plain full checkout after it leaves the whole tree" $after.Whole (Show-Tree $after)
    }

    if ($byLabel.ContainsKey('test.yml test')) {
        $run = Invoke-OwnCheckout $byLabel['test.yml test'] (Join-Path $root 'ancient') $ancient
        $text = if ($null -eq $run.Assert) { 'no check step' } else { $run.Assert.Output -join "`n" }
        Check "a commit older than Assert-CheckoutTree.ps1: '$assertName' warns and passes" (
            $null -ne $run.Assert -and $run.Assert.ExitCode -eq 0 -and $text -match '::warning title=Checkout not checked::') $text
        $half = Join-Path $root 'half-without-the-check'
        New-PoisonedWorkspace $half
        $null = Invoke-Git $half @('config', '--local', 'core.sparseCheckout', 'true')
        Set-Content -LiteralPath (Join-Path $half '.git/info/sparse-checkout') -Value "/ControlServer.sln`n"
        $null = Invoke-Git $half @('checkout', '--force', 'HEAD')
        $null = Invoke-Git $half @('read-tree', '-mu', 'HEAD')
        $checkStep = $byLabel['test.yml test'].After
        $r = if ($null -ne $checkStep -and $checkStep.Contains('name') -and $checkStep['name'] -ceq $assertName) {
            Invoke-RunStep $checkStep $half
        } else { [pscustomobject]@{ ExitCode = -1; Output = @('no check step') } }
        Check "a half tree without scripts/: '$assertName' is red, not a warning" ($r.ExitCode -eq 1 -and
            ($r.Output -join "`n") -match 'is in HEAD but not in the worktree') ($r.Output -join ' | ')
    }

    # --- 4. Assert-CheckoutTree.ps1 on its own. -------------------------------------------------------------------------
    Check 'Assert-CheckoutTree.ps1 exists' (Test-Path -LiteralPath $assertScript) $assertScript
    if (Test-Path -LiteralPath $assertScript) {
        $whole = Join-Path $root 'assert-whole'
        $null = Invoke-ActionsCheckout $whole $newer @() $true
        $r = Invoke-Assert $whole @()
        Check 'assert: a whole tree passes' ($r.ExitCode -eq 0 -and ($r.Output -join "`n") -match 'CHECKOUT_TREE_OK') ($r.Output -join ' | ')

        $cone = Join-Path $root 'assert-cone'
        $null = Invoke-ActionsCheckout $cone $newer @('scripts') $true
        $r = Invoke-Assert $cone @('scripts')
        Check 'assert: a cone checkout of scripts passes against scripts' ($r.ExitCode -eq 0) ($r.Output -join ' | ')
        $r = Invoke-Assert $cone @()
        Check 'assert: the same checkout is red against a full checkout, naming a left-out file' (
            $r.ExitCode -eq 1 -and ($r.Output -join "`n") -match 'CHECKOUT_TREE_INCOMPLETE' -and ($r.Output -join "`n") -match 'src/ControlServer\.Host/Program\.cs') ($r.Output -join ' | ')

        $half = Join-Path $root 'assert-half'
        New-PoisonedWorkspace $half
        $null = Invoke-ActionsCheckout $half $older @() $true
        $r = Invoke-Assert $half @()
        $text = $r.Output -join "`n"
        Check 'assert: the half tree of run 39457961360 is red, naming the stale setting and a left-out file' (
            $r.ExitCode -eq 1 -and $text -match 'CHECKOUT_TREE_INCOMPLETE' -and $text -match 'core\.sparseCheckout' -and
            $text -match 'ControlServer\.sln') ($r.Output -join ' | ')

        $deleted = Join-Path $root 'assert-deleted'
        $null = Invoke-ActionsCheckout $deleted $newer @() $true
        Remove-Item -LiteralPath (Join-Path $deleted 'ControlServer.sln')
        $r = Invoke-Assert $deleted @()
        Check 'assert: a tracked file missing from the worktree is red, by name' (
            $r.ExitCode -eq 1 -and ($r.Output -join "`n") -match 'ControlServer\.sln') ($r.Output -join ' | ')

        $notApplied = Join-Path $root 'assert-not-applied'
        $null = Invoke-ActionsCheckout $notApplied $newer @() $true
        $r = Invoke-Assert $notApplied @('scripts')
        Check 'assert: a cone list the checkout did not apply is red' ($r.ExitCode -eq 1) ($r.Output -join ' | ')
    }
} finally {
    foreach ($name in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnv[$name]) }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "$script:passed case(s) passed"
if ($script:wrong -gt 0) {
    Write-Host "CHECKOUT_SPARSE_STATE_SELFTEST_FAILED: $script:wrong case(s)"
    exit 1
}
Write-Host 'CHECKOUT_SPARSE_STATE_SELFTEST_PASSED'
exit 0
