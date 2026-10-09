#Requires -Version 7

<#
.SYNOPSIS
    Headless self-check of the content-conflict judgments in run-staged-g3.ps1 (control-server#541) against one
    ControlServer build: the commit to check, optionally with a mutation patch applied.

.DESCRIPTION
    CV-RELIABLE-RETRY-DIFFERENT-CONTENT asks for a ProtocolProblem MESSAGE_ID_CONTENT_CONFLICT and that the conflicting
    retry is never applied; it does not ask for the connection to end. control-server#478 made the server do exactly
    that, and the staged runner's four content-conflict assertions, which still required a closed connection, went red
    on it (cs#393, run 20261009T161926941Z). control-server#541 rewrote them. This script shows the rewrite can tell
    the behaviours apart:

      - on a server after #478 (5f3adc42 or later) the four judgments PASS;
      - on a server before #478 (e26c81286^) they FAIL, because the connection ends and no ProtocolProblem arrives;
      - with a mutation that keeps the conflicting retry in place of the original, or answers with the wrong code,
        they FAIL.

    What runs is the runner's own: the synthetic peer is the C# harness taken out of run-staged-g3.ps1, and the store
    judgment is the runner's Get-ContentConflictVerdict and the functions it calls, taken by AST. Only the four probes
    that carry the judgments run -- RunProbeAsync, RunBusinessProbeAsync and RunRecoveryProbeAsync through the fault
    proxy, the same order as the runner -- against a server started the way the runner starts it. No onboard WPF, no
    simulator, no desktop lock, and ports the system hands out rather than the runner's fixed block, so this never
    meets a G3 run that holds those. It is not G3 evidence and grades no slice.

    A server older than the protocol-v3.0.0 identity speaks another release, and the harness is compiled with v3's.
    For such a commit the harness is ported back to the identity the commit's appsettings.json declares: the six
    identity constants, the tag and protocolVersion, and the three v3-only payload fields (checkPurpose on
    PreDepartureSafetyCheckResult, demandId and cargoHandoff on ForcedMechanicalRecoveryResult) removed. That is the
    whole of what git diff 8d0a644e 5f3adc42 -- scripts/run-staged-g3.ps1 changes in the harness; each replacement must
    match exactly once, or the script stops.

    Exits 0 when every judgment came out as expected, 1 otherwise. -Expect Pass wants all four PASS. -Expect Fail
    wants the judgments named in -FailingJudgment FAIL (all four when it is not given) and every other one PASS: a
    mutation reaches only the path it changes, and a judgment it does not reach turning red would be a judgment that
    fails for some other reason.

.EXAMPLE
    pwsh -NoProfile -File scripts/Test-StagedG3ContentConflict.ps1 -ControlServerCommit 5f3adc424... -StageRoot C:/s541a -EvidenceRoot evidence/g3/x -Expect Pass
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ControlServerCommit,
    [Parameter(Mandatory)][string]$StageRoot,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][ValidateSet('Pass', 'Fail')][string]$Expect,
    [ValidateSet('heartbeat', 'business', 'recoverySessionRequestId', 'forcedRecoveryResult')]
    [string[]]$FailingJudgment,
    [string]$MutationPatch,
    [string]$ControlServerRepository = (Split-Path -Parent $PSScriptRoot),
    [string]$RunnerPath = (Join-Path $PSScriptRoot 'run-staged-g3.ps1')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'

$StageRoot = [IO.Path]::GetFullPath($StageRoot)
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
foreach ($path in @($StageRoot, $EvidenceRoot)) {
    if (Test-Path -LiteralPath $path) { throw "Must not exist yet: $path" }
}
if (-not [string]::IsNullOrEmpty($MutationPatch)) {
    $MutationPatch = [IO.Path]::GetFullPath($MutationPatch)
    if (-not (Test-Path -LiteralPath $MutationPatch -PathType Leaf)) { throw "No such patch: $MutationPatch" }
}
New-Item -ItemType Directory -Path $StageRoot, $EvidenceRoot | Out-Null
$logsRoot = Join-Path $EvidenceRoot 'logs'
New-Item -ItemType Directory -Path $logsRoot | Out-Null

# --- what is taken from the runner ------------------------------------------------------------------------------
$tokens = $null
$parseErrors = $null
$runnerAst = [System.Management.Automation.Language.Parser]::ParseFile($RunnerPath, [ref]$tokens, [ref]$parseErrors)
if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) { throw "The runner does not parse: $RunnerPath" }
foreach ($name in @(
        'Get-Sha256Text', 'Wait-HttpJson', 'Stop-ProcessSafely', 'Read-Ndjson',
        'Get-StagedContentConflictExpectation', 'Read-ProtocolInboxRequestRow',
        'Test-ContentConflictNotApplied', 'Get-ContentConflictVerdict')) {
    $found = @($runnerAst.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
            }, $false))
    if ($found.Count -ne 1) { throw "Expected one top-level function $name in the runner, found $($found.Count)." }
    Invoke-Expression $found[0].Extent.Text
}
$harnessLiterals = @($tokens | Where-Object {
        $_.Kind -eq 'HereStringLiteral' -and $_.Value.Contains('class StagedG3TlsHarness') })
if ($harnessLiterals.Count -ne 1) { throw "Expected one harness here-string in the runner, found $($harnessLiterals.Count)." }
$harnessSource = $harnessLiterals[0].Value

# --- the server under test ----------------------------------------------------------------------------------------
function Invoke-Checked {
    param([string]$Name, [string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory)
    $log = Join-Path $logsRoot "$Name.log"
    Push-Location $WorkingDirectory
    try { & $FilePath @Arguments *> $log } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE; see $log" }
}

$controlSource = Join-Path $StageRoot 'cs'
$controlPublish = Join-Path $StageRoot 'publish'
$runtimeRoot = Join-Path $StageRoot 'runtime'
New-Item -ItemType Directory -Path $runtimeRoot | Out-Null
Invoke-Checked -Name 'clone' -FilePath 'git' -WorkingDirectory $StageRoot `
    -Arguments @('clone', '--quiet', '--no-checkout', $ControlServerRepository, $controlSource)
Invoke-Checked -Name 'checkout' -FilePath 'git' -WorkingDirectory $controlSource `
    -Arguments @('checkout', '--quiet', '--detach', $ControlServerCommit)
$checkedOut = (& git -C $controlSource rev-parse HEAD).Trim()
if ($checkedOut -ne $ControlServerCommit) { throw "Checked out $checkedOut, not $ControlServerCommit." }
$patchSha256 = $null
if (-not [string]::IsNullOrEmpty($MutationPatch)) {
    Invoke-Checked -Name 'apply-mutation' -FilePath 'git' -WorkingDirectory $controlSource `
        -Arguments @('apply', '--whitespace=nowarn', $MutationPatch)
    $patchSha256 = (Get-FileHash -LiteralPath $MutationPatch -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-Item -LiteralPath $MutationPatch -Destination (Join-Path $EvidenceRoot (Split-Path -Leaf $MutationPatch))
}
Invoke-Checked -Name 'publish-control-server' -FilePath 'dotnet' -WorkingDirectory $controlSource `
    -Arguments @('publish', '.\src\ControlServer.Host\ControlServer.Host.csproj', '-c', 'Release', '-o', $controlPublish)

$identity = (Get-Content -Raw -LiteralPath (Join-Path $controlSource 'src\ControlServer.Host\appsettings.json') |
        ConvertFrom-Json).ProtocolCandidate
if ($null -eq $identity) { throw 'The commit under test carries no ProtocolCandidate identity.' }

# --- the harness, ported back when the commit speaks an older release ------------------------------------------
function Set-Once {
    param([string]$Text, [string]$Old, [string]$New)
    $count = ([regex]::Matches($Text, [regex]::Escape($Old))).Count
    if ($count -ne 1) { throw "Porting the harness: expected one occurrence of '$Old', found $count." }
    return $Text.Replace($Old, $New)
}
$harnessPorted = $false
if ($identity.releaseVersion -ne '3.0.0') {
    if ($identity.protocolVersion -ge 4) { throw "Unexpected identity $($identity.tag) at protocolVersion $($identity.protocolVersion)." }
    $constants = [ordered]@{
        Release = $identity.releaseVersion
        Commit = $identity.repositoryCommit
        Manifest = $identity.manifestSha256
        Schema = $identity.schemaBundleSha256
        Vectors = $identity.vectorsSha256
    }
    foreach ($name in $constants.Keys) {
        $match = [regex]::Matches($harnessSource, "public const string $name = ""[^""]*"";")
        if ($match.Count -ne 1) { throw "Porting the harness: expected one constant $name, found $($match.Count)." }
        $harnessSource = $harnessSource.Replace($match[0].Value, "public const string $name = ""$($constants[$name])"";")
    }
    $harnessSource = Set-Once $harnessSource '["tag"] = "protocol-v3.0.0",' "[""tag""] = ""$($identity.tag)"","
    $versionCount = ([regex]::Matches($harnessSource, [regex]::Escape('["protocolVersion"] = 4,'))).Count
    if ($versionCount -ne 2) { throw "Porting the harness: expected two protocolVersion literals, found $versionCount." }
    $harnessSource = $harnessSource.Replace('["protocolVersion"] = 4,', "[""protocolVersion""] = $($identity.protocolVersion),")
    foreach ($line in @('["checkPurpose"] = "DEPARTURE",', '["demandId"] = null,', '["cargoHandoff"] = null')) {
        $harnessSource = Set-Once $harnessSource $line ''
    }
    $harnessPorted = $true
}
Add-Type -TypeDefinition $harnessSource -Language CSharp

# --- run ---------------------------------------------------------------------------------------------------------
function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}
$controlPort = Get-FreePort
$healthPort = Get-FreePort
$businessProxyPort = Get-FreePort
$credential = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
$recoveryProof = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
$databasePath = Join-Path $runtimeRoot 'controlserver.db'
$controlEnvironment = @{
    'CONTROL_SERVER_ONBOARD_CREDENTIAL' = $credential
    'CONTROL_SERVER_RECOVERY_AUTHENTICATION_PROOF' = $recoveryProof
    'ConnectionStrings__ControlServer' = "Data Source=$databasePath"
    'Health__url' = "http://127.0.0.1:$healthPort"
    'OnboardTransport__listenAddress' = '127.0.0.1'
    'OnboardTransport__port' = [string]$controlPort
    'OnboardTransport__credentialEnvironmentVariable' = 'CONTROL_SERVER_ONBOARD_CREDENTIAL'
    'JourneyRuntime__enabled' = 'false'
    'MesIngest__baseUrl' = 'http://127.0.0.1:1'
    'RIoT__baseUrl' = 'http://127.0.0.1:1'
    'ControlServerBuild__commit' = $ControlServerCommit
}

$control = $null
$proxyStopping = $null
$proxyTask = $null
$probeResult = $null
$businessProbeResult = $null
$recoveryProbeResult = $null
$runError = $null
$version = $null
try {
    $control = Start-Process -FilePath 'dotnet' -ArgumentList @(Join-Path $controlPublish 'ControlServer.Host.dll') `
        -WorkingDirectory $controlPublish `
        -RedirectStandardOutput (Join-Path $logsRoot 'control.out.log') `
        -RedirectStandardError (Join-Path $logsRoot 'control.err.log') `
        -Environment $controlEnvironment -WindowStyle Hidden -PassThru
    $version = Wait-HttpJson -Uri "http://127.0.0.1:$healthPort/version"
    if ($version.manifestSha256 -ne $identity.manifestSha256) {
        throw 'The running ControlServer reported another protocol identity than its appsettings.json.'
    }

    $probeResult = [StagedG3TlsHarness]::RunProbeAsync(
        $controlPort, $credential, (Join-Path $EvidenceRoot 'probe-events.ndjson'),
        [Threading.CancellationToken]::None).GetAwaiter().GetResult() | ConvertFrom-Json

    $proxyTranscript = Join-Path $EvidenceRoot 'business-fault-proxy-events.ndjson'
    $proxyStopping = [Threading.CancellationTokenSource]::new()
    $proxyTask = [StagedG3TlsHarness]::RunProxyAsync($businessProxyPort, $controlPort, $proxyTranscript, $proxyStopping.Token)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        $ready = @(Read-Ndjson $proxyTranscript | Where-Object event -EQ 'proxy-listening').Count -eq 1
        if (-not $ready) { Start-Sleep -Milliseconds 100 }
    } while (-not $ready -and [DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $ready) { throw 'Business fault proxy did not become ready.' }

    $businessProbeResult = [StagedG3TlsHarness]::RunBusinessProbeAsync(
        $businessProxyPort, $credential, (Join-Path $EvidenceRoot 'business-probe-events.ndjson'),
        [Threading.CancellationToken]::None).GetAwaiter().GetResult() | ConvertFrom-Json
    $recoveryProbeResult = [StagedG3TlsHarness]::RunRecoveryProbeAsync(
        $businessProxyPort, $credential, $recoveryProof, (Join-Path $EvidenceRoot 'recovery-probe-events.ndjson'),
        [Threading.CancellationToken]::None).GetAwaiter().GetResult() | ConvertFrom-Json
}
catch {
    $runError = $_
    Write-Warning "Self-check run errored: $($_.Exception.GetBaseException().Message)"
}
finally {
    Stop-ProcessSafely -Process $control
    if ($null -ne $proxyStopping) {
        $proxyStopping.Cancel()
        if ($null -ne $proxyTask) { try { $proxyTask.Wait(5000) | Out-Null } catch { } }
        $proxyStopping.Dispose()
    }
}

# --- judge, the runner's way --------------------------------------------------------------------------------------
$databaseObservation = $null
if (Test-Path -LiteralPath $databasePath) {
    Add-Type -Path (Join-Path $controlPublish 'Microsoft.Data.Sqlite.dll')
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$databasePath;Mode=ReadOnly")
    try {
        $connection.Open()
        $expectations = Get-StagedContentConflictExpectation -ProbeResult $probeResult `
            -BusinessProbeResult $businessProbeResult -RecoveryProbeResult $recoveryProbeResult
        $databaseObservation = [ordered]@{
            contentConflictExpectations = $expectations
            contentConflictInboxRows = Read-ProtocolInboxRequestRow -Connection $connection `
                -MessageId @($expectations | ForEach-Object { $_.messageId })
        }
    }
    finally { $connection.Dispose() }
}
$verdict = Get-ContentConflictVerdict -ProbeResult $probeResult -BusinessProbeResult $businessProbeResult `
    -RecoveryProbeResult $recoveryProbeResult -DatabaseObservation $databaseObservation
# Keyed by verdict; the comment names the runner's assertion each one feeds.
$judgments = [ordered]@{
    heartbeat = $verdict.heartbeat                               # sameMessageIdDifferentContentStableConflict
    business = $verdict.business                                 # businessMessageSameMessageIdDifferentContentStableConflict
    recoverySessionRequestId = $verdict.recoverySessionRequestId # recoverySessionAuthorisationBoundary
    forcedRecoveryResult = $verdict.forcedRecoveryResult         # forcedRecoveryGenerationAdvancesMonotonically
}
$failing = if ($Expect -eq 'Pass') { @() } elseif ($FailingJudgment.Count -gt 0) { @($FailingJudgment) } else { @($judgments.Keys) }
$expected = [ordered]@{}
foreach ($name in $judgments.Keys) { $expected[$name] = $name -notin $failing }
$asExpected = $null -eq $runError -and
    @($judgments.Keys | Where-Object { $judgments[$_] -ne $expected[$_] }).Count -eq 0

$result = [ordered]@{
    schemaVersion = '1.0.0'
    tool = 'Test-StagedG3ContentConflict.ps1'
    ticket = 'control-server#541'
    gateEvidence = $false
    controlServerCommit = $ControlServerCommit
    mutationPatch = if ($null -ne $patchSha256) { [ordered]@{ file = Split-Path -Leaf $MutationPatch; sha256 = $patchSha256 } } else { $null }
    runnerSha256 = (Get-FileHash -LiteralPath $RunnerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    serverProtocolIdentity = $identity
    harnessPortedToServerIdentity = $harnessPorted
    controlServerVersion = $version
    expect = $Expect
    expectedJudgments = $expected
    outcome = if ($asExpected) { 'AS_EXPECTED' } else { 'NOT_AS_EXPECTED' }
    judgments = $judgments
    contentConflictNotApplied = $verdict.notApplied
    probe = $probeResult
    businessProbeConflicts = $businessProbeResult.conflicts
    recoveryProbe = [ordered]@{
        requestIdConflict = @($recoveryProbeResult.sessionAuthorisation | Where-Object {
                $null -ne $_ -and $_.case -eq 'recovery-session-requestid-content-conflict' })
        forcedRecoveryGenerationBranches = $recoveryProbeResult.forcedRecoveryGenerationBranches
    }
    database = $databaseObservation
    error = if ($null -ne $runError) { $runError.Exception.GetBaseException().Message } else { $null }
}
[IO.File]::WriteAllText((Join-Path $EvidenceRoot 'self-check-result.json'),
    ($result | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))

foreach ($name in $judgments.Keys) {
    Write-Host ('{0,-5} {1} (expected {2})' -f $(if ($judgments[$name]) { 'PASS' } else { 'FAIL' }), $name,
        $(if ($expected[$name]) { 'PASS' } else { 'FAIL' }))
}
Write-Host ("expect {0}: {1}" -f $Expect, $result.outcome)
exit $(if ($asExpected) { 0 } else { 1 })
