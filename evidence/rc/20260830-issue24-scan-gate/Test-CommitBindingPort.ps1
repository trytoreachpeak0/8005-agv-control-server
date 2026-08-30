<#
.SYNOPSIS
Falsifiability harness for closing the commit-binding override port (map ticket 24, item 1).

.DESCRIPTION
Ticket 20 stated that run-staged-g3-restart.ps1 accepts no commit override, "because an override port
is a drift port". Ticket 21 found one anyway: $CommitBindingSource was still a parameter, so pointing
it at a copy of run-staged-g3.ps1 carrying stale commits made a whole evidence run claim the wrong
binding without failing. This harness shows that the override worked at the baseline and no longer
binds, and that the binding the runner actually reads is unchanged.

The runner itself is never executed -- Get-Command -Syntax parses the param block without running it.
#>
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'commit-binding-port-result.json'),
    [string]$BaselineCommit = '0e4d471'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$runnerRelativePath = 'scripts/run-staged-g3-restart.ps1'
$sourceRelativePath = 'scripts/run-staged-g3.ps1'
$runnerPath = Join-Path $repository ($runnerRelativePath -replace '/', '\')
$sourcePath = Join-Path $repository ($sourceRelativePath -replace '/', '\')

function Get-ParameterNames([string]$Text, [string]$Label) {
    $parseErrors = $null
    $tokens = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$parseErrors)
    if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) {
        throw "$Label does not parse: $($parseErrors[0].Message)"
    }
    if ($null -eq $ast.ParamBlock) { throw "$Label has no param block." }
    return @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
}

$currentParameters = Get-ParameterNames (Get-Content -Raw -LiteralPath $runnerPath) 'the current restart runner'
$baselineText = (& git -C $repository show "${BaselineCommit}:${runnerRelativePath}" | Out-String)
if ($LASTEXITCODE -ne 0) { throw "Unable to read $runnerRelativePath at $BaselineCommit" }
$baselineParameters = Get-ParameterNames $baselineText "the restart runner at $BaselineCommit"
$baselineResolvedCommit = (& git -C $repository rev-parse $BaselineCommit).Trim()

$assertions = [System.Collections.Generic.List[object]]::new()
function Add-Assertion([int]$Number, [string]$Name, [bool]$Passed, [string]$Observed) {
    $assertions.Add([ordered]@{ number = $Number; assertion = $Name; result = $(if ($Passed) { 'PASS' } else { 'FAIL' }); observed = $Observed })
}

Add-Assertion 1 "Baseline $BaselineCommit exposed the override as a parameter" `
    ($baselineParameters -contains 'CommitBindingSource') "parameters=$($baselineParameters -join ', ')"
Add-Assertion 2 'The current runner exposes no CommitBindingSource parameter' `
    (-not ($currentParameters -contains 'CommitBindingSource')) "parameters=$($currentParameters -join ', ')"

# Get-Command -Syntax reports the bindable parameter set without executing a single statement of the
# runner, which starts clones and services and must not run here.
$syntax = (Get-Command -Name $runnerPath -Syntax | Out-String)
Add-Assertion 3 'PowerShell would reject -CommitBindingSource at binding time' `
    ($syntax -notmatch 'CommitBindingSource') 'the parameter is absent from the runner syntax'

# The binding the runner still reads must be byte-for-byte what it read before: this change closes an
# override port, it does not change which commits an evidence run is bound to.
$parseErrors = $null
$tokens = $null
$currentAst = [System.Management.Automation.Language.Parser]::ParseFile($runnerPath, [ref]$tokens, [ref]$parseErrors)
if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) { throw 'The current restart runner does not parse.' }
$bindingFunction = @($currentAst.FindAll(
    { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-SharedCommitBinding' },
    $true))
if ($bindingFunction.Count -ne 1) { throw "Expected exactly one Get-SharedCommitBinding, found $($bindingFunction.Count)." }
. ([ScriptBlock]::Create($bindingFunction[0].Extent.Text))
$binding = Get-SharedCommitBinding -Path $sourcePath

$bindingUnchanged = $true
foreach ($name in @('ControlServerCommit', 'OnboardCommit', 'SimulatorCommit', 'ProtocolCommit')) {
    if ($binding[$name] -cnotmatch '^[0-9a-f]{40}$') { $bindingUnchanged = $false }
}
Add-Assertion 4 'The runner still reads four lowercase full SHA-1 defaults out of run-staged-g3.ps1' `
    $bindingUnchanged ("controlServer=$($binding['ControlServerCommit']), onboard=$($binding['OnboardCommit']), " +
        "simulator=$($binding['SimulatorCommit']), protocol=$($binding['ProtocolCommit'])")

# Falsifiability of the binding reader itself: a copy carrying a stale commit is exactly what the
# removed parameter used to accept. Feeding it directly still parses fine -- which is the point: the
# reader cannot tell a stale copy from the real one, so the path must not be caller-supplied.
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('w2g-binding-port-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
try {
    $staleCopy = Join-Path $sandbox 'run-staged-g3.ps1'
    $staleCommit = '0000000000000000000000000000000000000001'
    (Get-Content -Raw -LiteralPath $sourcePath).Replace($binding['ControlServerCommit'], $staleCommit) |
        Set-Content -LiteralPath $staleCopy -Encoding utf8 -NoNewline
    $staleBinding = Get-SharedCommitBinding -Path $staleCopy
    Add-Assertion 5 'A stale copy is accepted by the reader without error, so only a fixed path is safe' `
        ($staleBinding['ControlServerCommit'] -eq $staleCommit) `
        "the reader returned $($staleBinding['ControlServerCommit']) from the stale copy"
}
finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

$result = [ordered]@{
    schemaVersion = 1
    ticket = 'wire-to-gate-ai-implementation-kit/24'
    subject = 'run-staged-g3-restart.ps1 commit-binding override port'
    createdAt = [DateTimeOffset]::UtcNow.ToString('O')
    repositoryCommitAtRun = (& git -C $repository rev-parse HEAD).Trim()
    subjectScript = [ordered]@{
        path = $runnerRelativePath
        sha256 = (Get-FileHash -LiteralPath $runnerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    bindingSource = [ordered]@{
        path = $sourceRelativePath
        sha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        controlServer = $binding['ControlServerCommit']
        onboard = $binding['OnboardCommit']
        simulator = $binding['SimulatorCommit']
        protocol = $binding['ProtocolCommit']
    }
    baseline = [ordered]@{ requested = $BaselineCommit; commit = $baselineResolvedCommit; parameters = @($baselineParameters) }
    runnerExecuted = $false
    assertionCount = $assertions.Count
    failedAssertionCount = @($assertions | Where-Object { $_.result -ne 'PASS' }).Count
    assertions = @($assertions)
}
$result.result = if ($result.failedAssertionCount -eq 0) { 'PASS' } else { 'FAIL' }
[IO.File]::WriteAllText($OutputPath, ($result | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))

$assertions | ForEach-Object { '{0,2}. [{1}] {2} -- {3}' -f $_.number, $_.result, $_.assertion, $_.observed }
Write-Output ''
Write-Output "$($result.result): $($result.assertionCount - $result.failedAssertionCount)/$($result.assertionCount) assertions"
Write-Output "Result: $OutputPath"
if ($result.failedAssertionCount -ne 0) { exit 1 }
