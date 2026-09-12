[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$StageRoot,
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,
    # Selects what this run CERTIFIES, not what it runs. A G3 run is one end-to-end scenario against
    # real peers, not a filterable set of tests, so -Slice narrows the evidence written and never the
    # scenario driven: with it, one gate-result.json for that slice; without it, one for each slice
    # this runner claims. Naming a slice this runner does not claim is refused before anything is
    # created -- see scripts/g3-slice-evidence.ps1 for the claim table and the 2026-09-09 ruling.
    [ValidatePattern('^FP-IS-(0[0-9]|1[0-5])$')][string]$Slice,
    [string]$ControlServerRepository = (Split-Path -Parent $PSScriptRoot),
    [string]$OnboardRepository = 'https://github.com/trytoreachpeak0/8005-agv-onboard-hmi.git',
    [string]$SimulatorRepository = 'https://github.com/trytoreachpeak0/slots-simulator.git',
    [string]$ProtocolRepository = 'https://github.com/trytoreachpeak0/8005-agv-protocol.git',
    # These four are literal defaults, so a run inherits whatever the last one froze. CLAUDE.md
    # already says to move them before a G3 run; since the v2 identity switch there is a second
    # reason, and it is sharper.
    #
    # 2026-09-09, ticket 17: all four now name the v2 line, so a run on these defaults no longer
    # starts a server that says protocol-v0.1.1 against a peer that says protocol-v1.0.0.
    #   $ControlServerCommit -> fp/v2-impl (cloned from the local working copy, not from GitHub,
    #     so this one does not need to be pushed and carries no -RemoteRef assertion).
    #   $OnboardCommit -> w2g/fp-v2-impl, pushed 2026-09-09; the -RemoteRef below asserts the
    #     remote tip still equals it. NOT OnboardHmi_MVP -- that branch is pinned to the released
    #     protocol-v0.3.0 and is a different protocol from this line.
    #   $SimulatorCommit unchanged: slots-simulator references no protocol identity at all.
    #   $ProtocolCommit unchanged: the v2 candidate, already on origin/fp/v2-candidate.
    #
    # 2026-09-12: $ProtocolCommit -> 16e2567, the same candidate with the single-owner release rule
    #   carried over from main. Its manifest and schema bundle hashes moved, and so did the copy of
    #   the identity in the synthetic peer below; that commit has to be on origin before a run.
    #   $ControlServerCommit -> 6369616: the server on that identity, plus 5f7a34e (the dashboard
    #     shows every alarm of a vehicle, REQ-0270).
    #   $OnboardCommit -> f9efa30, the w2g/b3-on-v2 tip: the onboard end on that identity (e30d421),
    #     the alarm sources wired (a98679f), the G2 script fix (ad0e507) and its G2 evidence.
    [string]$ControlServerCommit = '63696161d036a4907a39f8597fd64cc6e4c755fd',
    [string]$OnboardCommit = 'f9efa301734128e850cb5460c20265232611ffff',
    [string]$SimulatorCommit = 'fb5f7c593742bf98bc3957b8729a38aad5321f28',
    [string]$ProtocolCommit = '16e2567a7033883f00fc999f7fa08f954dd13a26',
    # The ref whose tip -OnboardCommit must equal. It is a parameter rather than a literal because the
    # branch carrying a line's onboard half moves with the line: batch 3 on the v2 line lives on
    # w2g/b3-on-v2, not on w2g/fp-v2-impl. The assertion is not weakened -- the clone source must
    # still name that commit as a branch tip, so evidence cannot bind a commit that exists only as a
    # detached object somebody handed the runner.
    [string]$OnboardRemoteRef = 'origin/w2g/b3-on-v2'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# R-4 (docs/defects/20260910-g3-runner-red-runs-on-fp-is-14-and-fp-is-15.md): on 2026-09-10 this machine ran
# out of memory mid-run because the publishes left a dozen MSBuild nodes resident. Every dotnet this runner
# starts inherits these two, so no node outlives the command that started it.
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'

# This run puts two WPF windows on the machine's single interactive desktop, which 8005-mes-ingest's
# golden renderer and desktop suite also claim. The mutex name is the cross-repository contract.
Import-Module (Join-Path $PSScriptRoot 'DesktopLock.psm1') -Force

$nodeCommand = Get-Command node -ErrorAction SilentlyContinue
$nodeExecutable = if ($null -ne $nodeCommand) {
    $nodeCommand.Source
} else {
    Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
}
if (-not (Test-Path -LiteralPath $nodeExecutable -PathType Leaf)) {
    throw "A Node.js executable is required for protocol G1: $nodeExecutable"
}
$nodeDirectory = Split-Path -Parent $nodeExecutable
if (($env:PATH -split ';') -notcontains $nodeDirectory) {
    $env:PATH = "$nodeDirectory;$env:PATH"
}

# G1 needs pnpm as well as node. Where pnpm is not on PATH, fall back to the copy that ships
# beside the bundled node as a plain package, invoked as `node pnpm.cjs`.
$pnpmCommand = Get-Command pnpm -ErrorAction SilentlyContinue
if ($null -ne $pnpmCommand) {
    $pnpmFilePath = $pnpmCommand.Source
    $pnpmPrefixArguments = @()
} else {
    $bundledPnpm = Join-Path (Split-Path -Parent $nodeDirectory) 'node_modules\pnpm\bin\pnpm.cjs'
    if (-not (Test-Path -LiteralPath $bundledPnpm -PathType Leaf)) {
        throw "A pnpm executable is required for protocol G1: $bundledPnpm"
    }
    $pnpmFilePath = $nodeExecutable
    $pnpmPrefixArguments = @($bundledPnpm)
}

# Read, not restated. These four used to be literals here, a third hand-kept copy of the identity
# beside ProtocolCandidateIdentity.cs and appsettings.json -- and three copies of nine hashes is how
# a gate ends up certifying a protocol nobody is running. ProtocolIdentityArchitectureTests keeps the
# settings mirror equal to the constants; this reads the mirror.
$expectedProtocol = (Get-Content -Raw -LiteralPath (
    Join-Path (Split-Path -Parent $PSScriptRoot) 'src\ControlServer.Host\appsettings.json') |
    ConvertFrom-Json).ProtocolCandidate
if ($null -eq $expectedProtocol) { throw 'appsettings.json carries no ProtocolCandidate identity.' }
$protocolTag = $expectedProtocol.tag
$manifestSha256 = $expectedProtocol.manifestSha256
$schemaBundleSha256 = $expectedProtocol.schemaBundleSha256
$vectorsSha256 = $expectedProtocol.vectorsSha256
$controlPort = 58205
$healthPort = 58207
$proxyPort = 58215
$businessProxyPort = 58216
$modbusPort = 1502
$simulatorHttpPort = 58006
$agvId = 'AGV-8005-STAGED-G3-01'
# Must stay in step with the constant inside StagedG3TlsHarness.RunRecoveryProbeAsync.
$recoveryAgvId = 'AGV-8005-STAGED-G3-RECOVERY'
$runStartedAt = [DateTimeOffset]::UtcNow
$runId = $runStartedAt.ToString('yyyyMMddTHHmmssfffZ')

foreach ($value in @($ControlServerCommit, $OnboardCommit, $SimulatorCommit, $ProtocolCommit)) {
    if ($value -notmatch '^[0-9a-f]{40}$') {
        throw "Commit identity must be a lowercase full SHA-1: $value"
    }
}

# The published peers come from exact clones at the commits above, but this runner and its embedded
# harness execute from the working tree, so their identity has to be read back rather than restated.
# Read it before anything is written: an EvidenceRoot inside this repository would otherwise show up
# as untracked content and report every run as dirty.
$harnessCommit = (& git -C $ControlServerRepository rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "Unable to read the harness commit from $ControlServerRepository" }
$harnessWorktreeClean = @(& git -C $ControlServerRepository status --porcelain).Count -eq 0

$G3RunKind = 'STAGED_G3_REAL_PEERS_DETERMINISTIC_PLAINTEXT'
. (Join-Path $PSScriptRoot 'g3-slice-evidence.ps1')
# Before the clones and the builds, not after: naming a slice this runner cannot certify should cost
# a message, not an hour of cloning and publishing four repositories.
if (-not [string]::IsNullOrEmpty($Slice)) { Assert-G3SliceIsClaimedBy -RunKind $G3RunKind -Slice $Slice }

if (Test-Path -LiteralPath $StageRoot) {
    throw "StageRoot must not already exist: $StageRoot"
}
if (Test-Path -LiteralPath $EvidenceRoot) {
    throw "EvidenceRoot must not already exist: $EvidenceRoot"
}
New-Item -ItemType Directory -Path $StageRoot, $EvidenceRoot | Out-Null

$sourcesRoot = Join-Path $StageRoot 'sources'
$publishRoot = Join-Path $StageRoot 'publish'
$runtimeRoot = Join-Path $StageRoot 'runtime'
$logsRoot = Join-Path $EvidenceRoot 'logs'
New-Item -ItemType Directory -Path $sourcesRoot, $publishRoot, $runtimeRoot, $logsRoot | Out-Null

$controlSource = Join-Path $sourcesRoot 'control-server'
$onboardSource = Join-Path $sourcesRoot 'onboard-hmi'
$simulatorSource = Join-Path $sourcesRoot 'slots-simulator'
$protocolSource = Join-Path $sourcesRoot 'protocol'
$controlPublish = Join-Path $publishRoot 'control-server'
$onboardPublish = Join-Path $publishRoot 'onboard-hmi'
$simulatorPublish = Join-Path $publishRoot 'slots-simulator'
$fieldOpsPublish = Join-Path $publishRoot 'field-ops'

$commands = [System.Collections.Generic.List[object]]::new()

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$LogPath
    )

    $startedAt = [DateTimeOffset]::UtcNow
    Push-Location $WorkingDirectory
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    $output | Out-File -LiteralPath $LogPath -Encoding utf8NoBOM
    $commands.Add([ordered]@{
        name = $Name
        workingDirectory = $WorkingDirectory
        file = $FilePath
        arguments = $Arguments
        startedAtUtc = $startedAt
        exitCode = $exitCode
        log = [IO.Path]::GetRelativePath($EvidenceRoot, $LogPath).Replace('\', '/')
    })
    if ($exitCode -ne 0) {
        throw "$Name exited with code $exitCode. See $LogPath"
    }
    return @($output)
}

function New-ExactClone {
    param(
        [string]$Name,
        [string]$Repository,
        [string]$Destination,
        [string]$Commit,
        [string]$RemoteRef = ''
    )

    Invoke-LoggedCommand -Name "clone-$Name" -WorkingDirectory $sourcesRoot -FilePath 'git' `
        -Arguments @('-c', 'core.autocrlf=false', 'clone', '--no-hardlinks', '--no-checkout', $Repository, $Destination) `
        -LogPath (Join-Path $logsRoot "clone-$Name.log") | Out-Null
    & git -C $Destination config core.autocrlf false
    if ($LASTEXITCODE -ne 0) { throw "Unable to set core.autocrlf=false for $Name" }
    Invoke-LoggedCommand -Name "fetch-$Name" -WorkingDirectory $Destination -FilePath 'git' `
        -Arguments @('fetch', 'origin', '--prune', '--tags') `
        -LogPath (Join-Path $logsRoot "fetch-$Name.log") | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($RemoteRef)) {
        $remoteTip = (& git -C $Destination rev-parse $RemoteRef).Trim()
        if ($LASTEXITCODE -ne 0 -or $remoteTip -ne $Commit) {
            throw "$Name remote ref mismatch: $RemoteRef=$remoteTip, expected $Commit"
        }
    }
    Invoke-LoggedCommand -Name "checkout-$Name" -WorkingDirectory $Destination -FilePath 'git' `
        -Arguments @('checkout', '--detach', $Commit) `
        -LogPath (Join-Path $logsRoot "checkout-$Name.log") | Out-Null
    $actual = (& git -C $Destination rev-parse HEAD).Trim()
    $status = @(& git -C $Destination status --porcelain)
    if ($actual -ne $Commit -or $status.Count -ne 0) {
        throw "$Name exact checkout is not clean at $Commit"
    }
}

function Get-Sha256Text {
    param([Parameter(Mandatory)][string]$Text)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Wait-HttpJson {
    param([string]$Uri, [int]$TimeoutSeconds = 45)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            return Invoke-RestMethod -Uri $Uri -TimeoutSec 2
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Uri"
}

function Stop-ProcessSafely {
    param([Diagnostics.Process]$Process)
    if ($null -eq $Process) { return }
    try {
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force
            $Process.WaitForExit(5000) | Out-Null
        }
    }
    catch {
        # Continue cleanup for the other isolated peers.
    }
}

function Read-Ndjson {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    return @(Get-Content -LiteralPath $Path | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_ | ConvertFrom-Json })
}

$harnessSource = @'
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public static class StagedG3TlsHarness
{
    private static readonly object LogGate = new();
    private static readonly object FaultGate = new();
    private static readonly List<FaultRule> Faults = new();
    private static int _connectionSequence;

    /// <summary>
    /// One injected transport fault, armed once and consumed by the first matching line.
    /// </summary>
    /// <remarks>
    /// The drop decision used to be three hard-coded lines inside the pump that only ever named the
    /// DurableAck for a RecoveryStateReport, so no other message type could be faulted and delay and
    /// reorder had nowhere to live. A rule table keeps the pump generic; the runner arms whichever
    /// rules a given vector needs. Fired is a field rather than a property because the one-shot latch
    /// is taken with Interlocked.CompareExchange.
    /// </remarks>
    public sealed class FaultRule
    {
        public string Direction = string.Empty;
        public string MessageType = string.Empty;
        public string? AcceptedMessageType;
        public string Action = string.Empty;
        public int DelayMilliseconds;
        public int Fired;
    }

    public static void AddFault(
        string direction,
        string messageType,
        string? acceptedMessageType,
        string action,
        int delayMilliseconds)
    {
        lock (FaultGate)
        {
            Faults.Add(new FaultRule
            {
                Direction = direction,
                MessageType = messageType,
                // PowerShell marshals $null into a string parameter as the empty string, so a rule
                // armed from the runner would otherwise carry "" and never match an accepted type.
                // Empty and null both mean "any accepted type".
                AcceptedMessageType = string.IsNullOrEmpty(acceptedMessageType) ? null : acceptedMessageType,
                Action = action,
                DelayMilliseconds = delayMilliseconds
            });
        }
    }

    private static FaultRule? MatchFault(string direction, Dictionary<string, object?> metadata)
    {
        lock (FaultGate)
        {
            foreach (FaultRule rule in Faults)
            {
                if (rule.Direction != direction) continue;
                if (!Equals(metadata.GetValueOrDefault("messageType"), rule.MessageType)) continue;
                if (rule.AcceptedMessageType is not null &&
                    !Equals(metadata.GetValueOrDefault("acceptedMessageType"), rule.AcceptedMessageType)) continue;
                if (Interlocked.CompareExchange(ref rule.Fired, 1, 0) != 0) continue;
                return rule;
            }
        }
        return null;
    }

    public static async Task<string> RunProbeAsync(
        int port,
        string credential,
        string transcriptPath,
        CancellationToken cancellationToken)
    {
        File.WriteAllText(transcriptPath, string.Empty, new UTF8Encoding(false));
        var rejectionCases = new List<Dictionary<string, object?>>();
        foreach ((string name, string release, string manifest, bool validCredential) in new[]
        {
            ("release-mismatch", "0.1.0", Protocol.Manifest, true),
            ("manifest-mismatch", Protocol.Release, new string('0', 64), true),
            ("credential-mismatch", Protocol.Release, Protocol.Manifest, false)
        })
        {
            await using Connection connection = await Connection.OpenAsync(
                port, cancellationToken).ConfigureAwait(false);
            string hello = Hello(
                name,
                StableGuid("hello:" + name),
                release,
                manifest,
                validCredential ? credential : credential + "-rejected");
            await connection.WriteAsync(hello, cancellationToken).ConfigureAwait(false);
            string? response = await connection.ReadAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
            string? responseType = Property(response, "messageType");
            string? reasonCode = NestedProperty(response, "payload", "problem", "reasonCode");
            bool passed = responseType == "SessionRejected" && reasonCode == "PROTOCOL_RELEASE_IDENTITY_MISMATCH";
            var item = new Dictionary<string, object?>
            {
                ["case"] = name,
                ["status"] = passed ? "PASS" : "FAIL",
                ["responseType"] = responseType,
                ["reasonCode"] = reasonCode,
                ["requestSha256"] = Sha256(hello),
                ["responseSha256"] = response is null ? null : Sha256(response)
            };
            rejectionCases.Add(item);
            Log(transcriptPath, item);
        }

        const string agvId = "AGV-8005-STAGED-G3-PROBE";
        string heartbeatId = StableGuid("heartbeat:duplicate-conflict");
        string? accepted;
        string originalHeartbeat;
        string firstAck;
        string secondAck;
        bool firstConflictClosed;
        long firstGeneration;
        await using (Connection connection = await Connection.OpenAsync(
            port, cancellationToken).ConfigureAwait(false))
        {
            await connection.WriteAsync(
                Hello(agvId, StableGuid("hello:duplicate"), Protocol.Release, Protocol.Manifest, credential),
                cancellationToken).ConfigureAwait(false);
            accepted = await connection.ReadAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            firstGeneration = NumberProperty(accepted, "sessionGeneration");
            originalHeartbeat = Heartbeat(agvId, heartbeatId, firstGeneration, 1);
            await connection.WriteAsync(originalHeartbeat, cancellationToken).ConfigureAwait(false);
            firstAck = await connection.ReadRequiredAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            await connection.WriteAsync(originalHeartbeat, cancellationToken).ConfigureAwait(false);
            secondAck = await connection.ReadRequiredAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            string conflict = Heartbeat(agvId, heartbeatId, firstGeneration, 2);
            await connection.WriteAsync(conflict, cancellationToken).ConfigureAwait(false);
            firstConflictClosed = await connection.ExpectClosedAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
            Log(transcriptPath, new Dictionary<string, object?>
            {
                ["case"] = "same-connection-same-messageId-same-content",
                ["status"] = firstAck == secondAck ? "PASS" : "FAIL",
                ["messageId"] = heartbeatId,
                ["requestSha256"] = Sha256(originalHeartbeat),
                ["firstResponseSha256"] = Sha256(firstAck),
                ["secondResponseSha256"] = Sha256(secondAck)
            });
            Log(transcriptPath, new Dictionary<string, object?>
            {
                ["case"] = "same-messageId-different-content-first-conflict",
                ["status"] = firstConflictClosed ? "PASS" : "FAIL",
                ["messageId"] = heartbeatId,
                ["conflictingRequestSha256"] = Sha256(conflict),
                ["connectionClosed"] = firstConflictClosed
            });
        }

        bool secondConflictClosed;
        long secondGeneration;
        await using (Connection connection = await Connection.OpenAsync(
            port, cancellationToken).ConfigureAwait(false))
        {
            await connection.WriteAsync(
                Hello(agvId, StableGuid("hello:conflict-repeat"), Protocol.Release, Protocol.Manifest, credential),
                cancellationToken).ConfigureAwait(false);
            string acceptedAgain = await connection.ReadRequiredAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
            secondGeneration = NumberProperty(acceptedAgain, "sessionGeneration");
            string conflictAgain = Heartbeat(agvId, heartbeatId, secondGeneration, 2);
            await connection.WriteAsync(conflictAgain, cancellationToken).ConfigureAwait(false);
            secondConflictClosed = await connection.ExpectClosedAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
            Log(transcriptPath, new Dictionary<string, object?>
            {
                ["case"] = "same-messageId-different-content-stable-conflict",
                ["status"] = secondConflictClosed ? "PASS" : "FAIL",
                ["messageId"] = heartbeatId,
                ["conflictingRequestSha256"] = Sha256(conflictAgain),
                ["connectionClosed"] = secondConflictClosed
            });
        }

        bool rejectionPass = rejectionCases.All(item => Equals(item["status"], "PASS"));
        bool duplicatePass = firstAck == secondAck &&
            Property(firstAck, "messageType") == "HeartbeatAck";
        bool conflictPass = firstConflictClosed && secondConflictClosed;
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = "1.0.0",
            ["status"] = rejectionPass && duplicatePass && conflictPass ? "PASS" : "FAIL",
            ["identityRejections"] = rejectionCases,
            ["duplicate"] = new Dictionary<string, object?>
            {
                ["status"] = duplicatePass ? "PASS" : "FAIL",
                ["sameConnection"] = true,
                ["messageId"] = heartbeatId,
                ["requestSha256"] = Sha256(originalHeartbeat),
                ["byteExactResponseReplay"] = firstAck == secondAck,
                ["responseSha256"] = Sha256(firstAck)
            },
            ["conflict"] = new Dictionary<string, object?>
            {
                ["status"] = conflictPass ? "PASS" : "FAIL",
                ["messageId"] = heartbeatId,
                ["firstSameConnectionClosed"] = firstConflictClosed,
                ["repeatConnectionClosed"] = secondConflictClosed,
                ["sessionGenerations"] = new[] { firstGeneration, secondGeneration }
            }
        });
    }

    /// <summary>
    /// Drives the WIRE_TO_GATE business message plane through the fault proxy as a synthetic peer.
    /// </summary>
    /// <remarks>
    /// A staged run sets JourneyRuntime:enabled false and points MesIngest and RIoT at a dead port,
    /// so no demand exists and the server never emits business traffic of its own. The four message
    /// types exercised here are the ones whose handler is a bare DurableAck, so they need no demand,
    /// no station operation and no vehicle. OperationResult and SlotOperationCommand are deliberately
    /// absent: see coverageLimits in the returned document.
    /// </remarks>
    public static async Task<string> RunBusinessProbeAsync(
        int port,
        string credential,
        string transcriptPath,
        CancellationToken cancellationToken)
    {
        File.WriteAllText(transcriptPath, string.Empty, new UTF8Encoding(false));
        const string agvId = "AGV-8005-STAGED-G3-BUSINESS";
        string[] businessTypes =
        {
            "SublotSubmitted",
            "OperationProgress",
            "PreDepartureSafetyCheckResult",
            "SlotOperationCommandRejected"
        };

        var duplicateCases = new List<Dictionary<string, object?>>();
        var conflictCases = new List<Dictionary<string, object?>>();
        int helloSequence = 0;
        foreach (string messageType in businessTypes)
        {
            string messageId = StableGuid("business:" + messageType);
            string original;
            string conflicting;
            string firstAck;
            string secondAck;
            bool sameConnectionClosed;
            await using (Connection connection = await Connection.OpenAsync(
                port, cancellationToken).ConfigureAwait(false))
            {
                long generation = await HandshakeAsync(
                    connection, agvId, StableGuid("hello:business:" + ++helloSequence), credential, cancellationToken)
                    .ConfigureAwait(false);
                original = Business(messageType, messageId, agvId, generation, 1);
                await connection.WriteAsync(original, cancellationToken).ConfigureAwait(false);
                firstAck = await connection.ReadRequiredAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
                await connection.WriteAsync(original, cancellationToken).ConfigureAwait(false);
                secondAck = await connection.ReadRequiredAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
                conflicting = Business(messageType, messageId, agvId, generation, 2);
                await connection.WriteAsync(conflicting, cancellationToken).ConfigureAwait(false);
                sameConnectionClosed = await connection
                    .ExpectClosedAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }

            bool repeatConnectionClosed;
            await using (Connection connection = await Connection.OpenAsync(
                port, cancellationToken).ConfigureAwait(false))
            {
                long generation = await HandshakeAsync(
                    connection, agvId, StableGuid("hello:business:" + ++helloSequence), credential, cancellationToken)
                    .ConfigureAwait(false);
                await connection.WriteAsync(
                    Business(messageType, messageId, agvId, generation, 2), cancellationToken).ConfigureAwait(false);
                repeatConnectionClosed = await connection
                    .ExpectClosedAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }

            bool duplicatePass = firstAck == secondAck &&
                Property(firstAck, "messageType") == "DurableAck" &&
                NestedProperty(firstAck, "payload", "acceptedMessageType") == messageType &&
                NestedProperty(firstAck, "payload", "acceptedMessageId") == messageId;
            var duplicate = new Dictionary<string, object?>
            {
                ["case"] = "business-duplicate-" + messageType,
                ["status"] = duplicatePass ? "PASS" : "FAIL",
                ["messageType"] = messageType,
                ["messageId"] = messageId,
                ["requestSha256"] = Sha256(original),
                ["byteExactResponseReplay"] = firstAck == secondAck,
                ["responseSha256"] = Sha256(firstAck)
            };
            duplicateCases.Add(duplicate);
            Log(transcriptPath, duplicate);

            bool conflictPass = sameConnectionClosed && repeatConnectionClosed;
            var conflict = new Dictionary<string, object?>
            {
                ["case"] = "business-conflict-" + messageType,
                ["status"] = conflictPass ? "PASS" : "FAIL",
                ["messageType"] = messageType,
                ["messageId"] = messageId,
                ["conflictingRequestSha256"] = Sha256(conflicting),
                ["firstSameConnectionClosed"] = sameConnectionClosed,
                ["repeatConnectionClosed"] = repeatConnectionClosed
            };
            conflictCases.Add(conflict);
            Log(transcriptPath, conflict);
        }

        // The faults are armed only now: the duplicate and conflict traffic above would otherwise
        // consume the one-shot rules that the three vectors below depend on.
        string dropMessageId = StableGuid("business:ack-drop");
        AddFault("server-to-client", "DurableAck", "PreDepartureSafetyCheckResult", "drop", 0);
        string? suppressedAck;
        string replayedAck;
        await using (Connection connection = await Connection.OpenAsync(
            port, cancellationToken).ConfigureAwait(false))
        {
            long generation = await HandshakeAsync(
                connection, agvId, StableGuid("hello:business:" + ++helloSequence), credential, cancellationToken)
                .ConfigureAwait(false);
            string line = Business("PreDepartureSafetyCheckResult", dropMessageId, agvId, generation, 1);
            await connection.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            suppressedAck = await connection.ReadAsync(TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(false);
            // Same bytes on the same session generation, so the inbox has to hand back the stored
            // first response rather than acknowledging a second time; durablyAcceptedAt would move
            // if the server recomputed it, and the proxy transcript holds the dropped ack's hash.
            await connection.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            replayedAck = await connection.ReadRequiredAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
        }
        bool ackDropPass = suppressedAck is null &&
            Property(replayedAck, "messageType") == "DurableAck" &&
            NestedProperty(replayedAck, "payload", "acceptedMessageId") == dropMessageId &&
            NestedProperty(replayedAck, "payload", "acceptedMessageType") == "PreDepartureSafetyCheckResult";

        const int injectedDelayMilliseconds = 900;
        string delayMessageId = StableGuid("business:delayed");
        AddFault("client-to-server", "SlotOperationCommandRejected", null, "delay", injectedDelayMilliseconds);
        string delayedAck;
        long observedDelayMilliseconds;
        await using (Connection connection = await Connection.OpenAsync(
            port, cancellationToken).ConfigureAwait(false))
        {
            long generation = await HandshakeAsync(
                connection, agvId, StableGuid("hello:business:" + ++helloSequence), credential, cancellationToken)
                .ConfigureAwait(false);
            string line = Business("SlotOperationCommandRejected", delayMessageId, agvId, generation, 1);
            Stopwatch stopwatch = Stopwatch.StartNew();
            await connection.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            delayedAck = await connection.ReadRequiredAsync(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            observedDelayMilliseconds = stopwatch.ElapsedMilliseconds;
        }
        bool delayPass = Property(delayedAck, "messageType") == "DurableAck" &&
            NestedProperty(delayedAck, "payload", "acceptedMessageId") == delayMessageId &&
            observedDelayMilliseconds >= injectedDelayMilliseconds;

        string heldMessageId = StableGuid("business:reordered-held");
        string overtakingMessageId = StableGuid("business:reordered-overtaking");
        AddFault("client-to-server", "SublotSubmitted", null, "hold-until-next", 0);
        string firstReorderAck;
        string secondReorderAck;
        await using (Connection connection = await Connection.OpenAsync(
            port, cancellationToken).ConfigureAwait(false))
        {
            long generation = await HandshakeAsync(
                connection, agvId, StableGuid("hello:business:" + ++helloSequence), credential, cancellationToken)
                .ConfigureAwait(false);
            await connection.WriteAsync(
                Business("SublotSubmitted", heldMessageId, agvId, generation, 1),
                cancellationToken).ConfigureAwait(false);
            await connection.WriteAsync(
                Business("OperationProgress", overtakingMessageId, agvId, generation, 1),
                cancellationToken).ConfigureAwait(false);
            firstReorderAck = await connection.ReadRequiredAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            secondReorderAck = await connection.ReadRequiredAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
        }
        bool reorderPass =
            Property(firstReorderAck, "messageType") == "DurableAck" &&
            Property(secondReorderAck, "messageType") == "DurableAck" &&
            NestedProperty(firstReorderAck, "payload", "acceptedMessageId") == overtakingMessageId &&
            NestedProperty(secondReorderAck, "payload", "acceptedMessageId") == heldMessageId;

        bool duplicatesPass = duplicateCases.All(item => Equals(item["status"], "PASS"));
        bool conflictsPass = conflictCases.All(item => Equals(item["status"], "PASS"));
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = "1.0.0",
            ["status"] = duplicatesPass && conflictsPass && ackDropPass && delayPass && reorderPass
                ? "PASS"
                : "FAIL",
            ["agvId"] = agvId,
            ["businessMessageTypes"] = businessTypes,
            ["duplicates"] = duplicateCases,
            ["conflicts"] = conflictCases,
            ["ackDropInSessionReplay"] = new Dictionary<string, object?>
            {
                ["status"] = ackDropPass ? "PASS" : "FAIL",
                ["messageType"] = "PreDepartureSafetyCheckResult",
                ["messageId"] = dropMessageId,
                ["firstAckSuppressed"] = suppressedAck is null,
                ["replayedAckSha256"] = Sha256(replayedAck)
            },
            ["delayedDelivery"] = new Dictionary<string, object?>
            {
                ["status"] = delayPass ? "PASS" : "FAIL",
                ["messageType"] = "SlotOperationCommandRejected",
                ["messageId"] = delayMessageId,
                ["injectedDelayMilliseconds"] = injectedDelayMilliseconds,
                ["observedRoundTripMilliseconds"] = observedDelayMilliseconds
            },
            ["reorderedDelivery"] = new Dictionary<string, object?>
            {
                ["status"] = reorderPass ? "PASS" : "FAIL",
                ["heldMessageType"] = "SublotSubmitted",
                ["heldMessageId"] = heldMessageId,
                ["overtakingMessageType"] = "OperationProgress",
                ["overtakingMessageId"] = overtakingMessageId,
                ["firstAcknowledgedMessageId"] = NestedProperty(firstReorderAck, "payload", "acceptedMessageId"),
                ["secondAcknowledgedMessageId"] = NestedProperty(secondReorderAck, "payload", "acceptedMessageId")
            },
            ["coverageLimits"] = new Dictionary<string, object?>
            {
                ["OperationResult"] =
                    "Not reachable in a staged run. OnboardMessageProcessor resolves the forced recovery " +
                    "generation with SingleAsync over StationOperations, so an OperationResult for a " +
                    "fabricated attempt throws before any acknowledgement. StationOperations rows are only " +
                    "written by PrepareSlotOperationAsync, which needs an accepted demand from MesIngest " +
                    "and RIoT.",
                ["SlotOperationCommand"] =
                    "Not reachable in a staged run. It is only published by JourneyRuntimeEngine, which is " +
                    "disabled here, or replayed by OnboardRecoveryCoordinator from an outbox row that the " +
                    "same demand-bearing path creates."
            }
        });
    }

    /// <summary>
    /// Drives the recovery request and result plane as a synthetic peer: session authorisation, the
    /// action families the protocol defines, one forced mechanical recovery interrupted mid-command
    /// by a transport disconnect, and a stale-generation result.
    /// </summary>
    /// <remarks>
    /// Everything asserted end to end here is deliberately demand-free. A recovery session scoped to
    /// a demand needs a blocked JourneyRuntime row and a StationOperations row, and both only exist
    /// once MesIngest and RIoT have accepted a demand, so the demand-scoped half of the plane is
    /// asserted at its authorisation boundary instead -- which is itself a safety property, since a
    /// recovery session must not open against a demand the server has never accepted. What stays
    /// uncovered is named in coverageLimits rather than faked with a fabricated StationOperations row,
    /// which would also destroy the meaning of the noMovementOrExternalSideEffects assertion.
    /// FORCED_MECHANICAL_RECOVERY is the one action whose accepted path needs no demand, so it
    /// carries the disconnect, the monotonic generation and the historical-only result vectors.
    /// </remarks>
    public static async Task<string> RunRecoveryProbeAsync(
        int port,
        string credential,
        string authenticationProof,
        string transcriptPath,
        CancellationToken cancellationToken)
    {
        File.WriteAllText(transcriptPath, string.Empty, new UTF8Encoding(false));
        const string agvId = "AGV-8005-STAGED-G3-RECOVERY";
        const string administratorId = "STAGED-G3-RECOVERY-ADMINISTRATOR";
        string eventId = StableGuid("recovery:event");
        string requestId = StableGuid("recovery:request");
        string absentDemandId = StableGuid("recovery:absent-demand");
        string absentAttemptId = StableGuid("recovery:absent-attempt");
        int[] scope = { 1 };
        int[] outOfScope = { 2 };
        int helloSequence = 0;

        var authorisationCases = new List<Dictionary<string, object?>>();
        var actionBoundaryCases = new List<Dictionary<string, object?>>();
        var demandScopedCases = new List<Dictionary<string, object?>>();
        var hardwareCases = new List<Dictionary<string, object?>>();
        string? sessionId;
        string openedResponse;
        string replayedOpenResponse;

        await using (Connection connection = await Connection.OpenAsync(
            port, cancellationToken).ConfigureAwait(false))
        {
            long generation = await HandshakeAsync(
                connection, agvId, StableGuid("hello:recovery:" + ++helloSequence), credential, cancellationToken)
                .ConfigureAwait(false);

            string rejected = await ExchangeAsync(
                connection,
                SessionRequest(
                    agvId, StableGuid("recovery:request-bad-proof"), generation,
                    StableGuid("recovery:request-bad-proof-id"), eventId, null, scope,
                    authenticationProof + "-rejected", administratorId),
                "ExceptionRecoverySessionRejected", cancellationToken).ConfigureAwait(false);
            authorisationCases.Add(Case(
                transcriptPath, "recovery-session-authentication-required", rejected,
                NestedProperty(rejected, "payload", "problem", "reasonCode") == "RECOVERY_AUTHENTICATION_FAILED",
                new Dictionary<string, object?>
                {
                    ["observedReasonCode"] = NestedProperty(rejected, "payload", "problem", "reasonCode")
                }));

            string demandRejected = await ExchangeAsync(
                connection,
                SessionRequest(
                    agvId, StableGuid("recovery:request-absent-demand"), generation,
                    StableGuid("recovery:request-absent-demand-id"), eventId, absentDemandId, scope,
                    authenticationProof, administratorId),
                "ExceptionRecoverySessionRejected", cancellationToken).ConfigureAwait(false);
            authorisationCases.Add(Case(
                transcriptPath, "recovery-session-refused-for-unaccepted-demand", demandRejected,
                NestedProperty(demandRejected, "payload", "problem", "reasonCode") == "RECOVERY_DEMAND_NOT_BLOCKED",
                new Dictionary<string, object?>
                {
                    ["observedReasonCode"] = NestedProperty(demandRejected, "payload", "problem", "reasonCode")
                }));

            openedResponse = await ExchangeAsync(
                connection,
                SessionRequest(
                    agvId, StableGuid("recovery:request-open"), generation, requestId, eventId, null, scope,
                    authenticationProof, administratorId),
                "ExceptionRecoverySessionOpened", cancellationToken).ConfigureAwait(false);
            sessionId = NestedProperty(openedResponse, "payload", "exceptionRecoverySessionId");
            authorisationCases.Add(Case(
                transcriptPath, "recovery-session-opened-without-demand", openedResponse,
                sessionId is not null &&
                NestedProperty(openedResponse, "payload", "requestId") == requestId,
                new Dictionary<string, object?> { ["exceptionRecoverySessionId"] = sessionId }));

            // A different messageId carrying the same requestId and the same business content goes
            // past the inbox and reaches the coordinator's own replay branch, so this asserts the
            // session is idempotent in requestId rather than only in messageId.
            replayedOpenResponse = await ExchangeAsync(
                connection,
                SessionRequest(
                    agvId, StableGuid("recovery:request-open-replay"), generation, requestId, eventId, null, scope,
                    authenticationProof, administratorId),
                "ExceptionRecoverySessionOpened", cancellationToken).ConfigureAwait(false);
            authorisationCases.Add(Case(
                transcriptPath, "recovery-session-requestid-replay-idempotent", replayedOpenResponse,
                NestedProperty(replayedOpenResponse, "payload", "exceptionRecoverySessionId") == sessionId &&
                NestedProperty(replayedOpenResponse, "payload", "requestId") == requestId &&
                NumberNestedProperty(replayedOpenResponse, "payload", "recoverySessionRevision") ==
                    NumberNestedProperty(openedResponse, "payload", "recoverySessionRevision"),
                new Dictionary<string, object?>
                {
                    ["exceptionRecoverySessionId"] =
                        NestedProperty(replayedOpenResponse, "payload", "exceptionRecoverySessionId")
                }));

            string alreadyOpen = await ExchangeAsync(
                connection,
                SessionRequest(
                    agvId, StableGuid("recovery:request-second"), generation,
                    StableGuid("recovery:request-second-id"), eventId, null, scope,
                    authenticationProof, administratorId),
                "ExceptionRecoverySessionRejected", cancellationToken).ConfigureAwait(false);
            authorisationCases.Add(Case(
                transcriptPath, "recovery-session-single-open-per-vehicle", alreadyOpen,
                NestedProperty(alreadyOpen, "payload", "problem", "reasonCode") == "ACTION_NOT_ALLOWED_IN_STATE",
                new Dictionary<string, object?>
                {
                    ["observedReasonCode"] = NestedProperty(alreadyOpen, "payload", "problem", "reasonCode")
                }));
        }

        bool requestConflictClosed;
        await using (Connection connection = await Connection.OpenAsync(
            port, cancellationToken).ConfigureAwait(false))
        {
            long generation = await HandshakeAsync(
                connection, agvId, StableGuid("hello:recovery:" + ++helloSequence), credential, cancellationToken)
                .ConfigureAwait(false);
            await connection.WriteAsync(
                SessionRequest(
                    agvId, StableGuid("recovery:request-conflict"), generation, requestId, eventId, null, outOfScope,
                    authenticationProof, administratorId),
                cancellationToken).ConfigureAwait(false);
            requestConflictClosed = await connection
                .ExpectClosedAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        authorisationCases.Add(Case(
            transcriptPath, "recovery-session-requestid-content-conflict", null, requestConflictClosed,
            new Dictionary<string, object?> { ["connectionClosed"] = requestConflictClosed }));

        string? acceptedFirstActionId;
        bool commandConnectionClosed;
        await using (Connection connection = await Connection.OpenAsync(
            port, cancellationToken).ConfigureAwait(false))
        {
            long generation = await HandshakeAsync(
                connection, agvId, StableGuid("hello:recovery:" + ++helloSequence), credential, cancellationToken)
                .ConfigureAwait(false);

            foreach (string action in new[]
            {
                "RESUME_AFTER_REPAIR", "COMPENSATE_LOAD_ALL_EMPTY", "FAULT_CARGO_HANDOFF"
            })
            {
                string response = await ExchangeAsync(
                    connection,
                    ActionSubmit(
                        agvId, StableGuid("recovery:action-boundary:" + action), generation,
                        StableGuid("recovery:action-boundary-id:" + action), sessionId!, action,
                        eventId, null, scope, administratorId),
                    "RecoveryActionRejected", cancellationToken).ConfigureAwait(false);
                actionBoundaryCases.Add(Case(
                    transcriptPath, "recovery-action-requires-demand-" + action, response,
                    NestedProperty(response, "payload", "problem", "reasonCode") == "ACTION_NOT_ALLOWED_IN_STATE",
                    new Dictionary<string, object?>
                    {
                        ["action"] = action,
                        ["observedReasonCode"] = NestedProperty(response, "payload", "problem", "reasonCode")
                    }));
            }

            string scopeMismatch = await ExchangeAsync(
                connection,
                ActionSubmit(
                    agvId, StableGuid("recovery:action-scope-mismatch"), generation,
                    StableGuid("recovery:action-scope-mismatch-id"), sessionId!, "FORCED_MECHANICAL_RECOVERY",
                    eventId, null, outOfScope, administratorId),
                "RecoveryActionRejected", cancellationToken).ConfigureAwait(false);
            actionBoundaryCases.Add(Case(
                transcriptPath, "recovery-action-scope-mismatch", scopeMismatch,
                NestedProperty(scopeMismatch, "payload", "problem", "reasonCode") == "RECOVERY_SCOPE_MISMATCH",
                new Dictionary<string, object?>
                {
                    ["action"] = "FORCED_MECHANICAL_RECOVERY",
                    ["observedReasonCode"] = NestedProperty(scopeMismatch, "payload", "problem", "reasonCode")
                }));

            string cancellation = await ExchangeAsync(
                connection,
                LoadCancellationRequest(
                    agvId, StableGuid("recovery:load-cancellation"), generation,
                    StableGuid("recovery:load-cancellation-id"), absentDemandId, absentAttemptId, administratorId),
                "LoadCancellationAuthorization", cancellationToken).ConfigureAwait(false);
            demandScopedCases.Add(Case(
                transcriptPath, "load-cancellation-refused-without-accepted-demand", cancellation,
                NestedProperty(cancellation, "payload", "decision") == "REJECTED" &&
                NestedProperty(cancellation, "payload", "problem", "reasonCode") == "ACTION_NOT_ALLOWED_IN_STATE",
                new Dictionary<string, object?>
                {
                    ["messageType"] = "LoadCancellationStartRequested",
                    ["observedDecision"] = NestedProperty(cancellation, "payload", "decision")
                }));

            string correction = await ExchangeAsync(
                connection,
                LoadCorrectionRequest(
                    agvId, StableGuid("recovery:load-correction"), generation,
                    StableGuid("recovery:load-correction-id"), absentDemandId, absentAttemptId, scope,
                    administratorId),
                "LoadCorrectionRejected", cancellationToken).ConfigureAwait(false);
            demandScopedCases.Add(Case(
                transcriptPath, "load-correction-refused-without-committed-operation", correction,
                NestedProperty(correction, "payload", "problem", "reasonCode") == "ACTION_NOT_ALLOWED_IN_STATE",
                new Dictionary<string, object?>
                {
                    ["messageType"] = "LoadCorrectionRequested",
                    ["observedReasonCode"] = NestedProperty(correction, "payload", "problem", "reasonCode")
                }));

            string compensation = await ExchangeAsync(
                connection,
                LoadCompensationRequest(
                    agvId, StableGuid("recovery:load-compensation"), generation,
                    StableGuid("recovery:load-compensation-id"), sessionId!, absentDemandId, absentAttemptId,
                    administratorId),
                "LoadCompensationRejected", cancellationToken).ConfigureAwait(false);
            demandScopedCases.Add(Case(
                transcriptPath, "load-compensation-refused-without-authorised-workflow", compensation,
                NestedProperty(compensation, "payload", "problem", "reasonCode") == "ACTION_NOT_ALLOWED_IN_STATE",
                new Dictionary<string, object?>
                {
                    ["messageType"] = "LoadCompensationRequested",
                    ["observedReasonCode"] = NestedProperty(compensation, "payload", "problem", "reasonCode")
                }));

            // Armed only now, so none of the refused actions above can consume the one-shot rule.
            AddFault("server-to-client", "ForcedMechanicalRecoveryCommand", null, "drop-and-close", 0);
            string accepted = await ExchangeAsync(
                connection,
                ActionSubmit(
                    agvId, StableGuid("recovery:forced-one"), generation,
                    StableGuid("recovery:forced-one-id"), sessionId!, "FORCED_MECHANICAL_RECOVERY",
                    eventId, null, scope, administratorId),
                "RecoveryActionAccepted", cancellationToken).ConfigureAwait(false);
            acceptedFirstActionId = NestedProperty(accepted, "payload", "recoveryActionId");
            commandConnectionClosed = await connection
                .ExpectClosedAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            Log(transcriptPath, new Dictionary<string, object?>
            {
                ["case"] = "forced-mechanical-command-lost-to-disconnect",
                ["status"] = commandConnectionClosed ? "PASS" : "FAIL",
                ["recoveryActionId"] = acceptedFirstActionId,
                ["connectionClosed"] = commandConnectionClosed
            });
        }

        // Killing the connection from the proxy leaves the server unwinding its single accept slot.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

        string firstActionId = StableGuid("recovery:forced-one-id");
        string secondActionId = StableGuid("recovery:forced-two-id");
        string currentResultMessageId = StableGuid("recovery:forced-two-result");
        string? replayedCommand;
        string staleResultAck;
        string currentResultAck;
        string replayedResultAck;
        await using (Connection connection = await Connection.OpenAsync(
            port, cancellationToken).ConfigureAwait(false))
        {
            long generation = await HandshakeAsync(
                connection, agvId, StableGuid("hello:recovery:" + ++helloSequence), credential, cancellationToken)
                .ConfigureAwait(false);
            await connection.WriteAsync(
                RecoveryReport(agvId, StableGuid("recovery:report-one"), generation,
                    StableGuid("recovery:report-one-id"), 1),
                cancellationToken).ConfigureAwait(false);
            replayedCommand = await ReadUntilAsync(
                connection, "ForcedMechanicalRecoveryCommand", TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
            Log(transcriptPath, new Dictionary<string, object?>
            {
                ["case"] = "forced-mechanical-command-replayed-after-reconnect",
                ["status"] = replayedCommand is not null &&
                    NestedProperty(replayedCommand, "payload", "recoveryActionId") == firstActionId
                    ? "PASS" : "FAIL",
                ["recoveryActionId"] = NestedProperty(replayedCommand, "payload", "recoveryActionId"),
                ["forcedRecoveryGeneration"] = replayedCommand is null
                    ? null
                    : (object)NumberNestedProperty(replayedCommand, "payload", "forcedRecoveryGeneration"),
                ["sessionGeneration"] = replayedCommand is null
                    ? null
                    : (object)NumberProperty(replayedCommand, "sessionGeneration"),
                ["responseSha256"] = replayedCommand is null ? null : Sha256(replayedCommand)
            });

            await ExchangeAsync(
                connection,
                ActionSubmit(
                    agvId, StableGuid("recovery:forced-two"), generation, secondActionId, sessionId!,
                    "FORCED_MECHANICAL_RECOVERY", eventId, null, scope, administratorId),
                "RecoveryActionAccepted", cancellationToken).ConfigureAwait(false);

            staleResultAck = await ExchangeAsync(
                connection,
                ForcedResult(
                    agvId, StableGuid("recovery:forced-one-result"), generation, sessionId!, firstActionId,
                    1, scope, administratorId, "2026-08-26T12:00:00Z"),
                "DurableAck", cancellationToken).ConfigureAwait(false);
            currentResultAck = await ExchangeAsync(
                connection,
                ForcedResult(
                    agvId, currentResultMessageId, generation, sessionId!, secondActionId,
                    2, scope, administratorId, "2026-08-26T12:00:01Z"),
                "DurableAck", cancellationToken).ConfigureAwait(false);
            replayedResultAck = await ExchangeAsync(
                connection,
                ForcedResult(
                    agvId, currentResultMessageId, generation, sessionId!, secondActionId,
                    2, scope, administratorId, "2026-08-26T12:00:01Z"),
                "DurableAck", cancellationToken).ConfigureAwait(false);

            string recorded = await ExchangeAsync(
                connection,
                HardwareRecord(
                    agvId, StableGuid("recovery:hardware-record"), generation,
                    StableGuid("recovery:hardware-record-id"), sessionId!, secondActionId, scope, administratorId),
                "HardwareRecoveryRecordResult", cancellationToken).ConfigureAwait(false);
            hardwareCases.Add(Case(
                transcriptPath, "hardware-recovery-record-recorded", recorded,
                NestedProperty(recorded, "payload", "outcome") == "RECORDED",
                new Dictionary<string, object?> { ["observedOutcome"] = NestedProperty(recorded, "payload", "outcome") }));

            string scopeRejected = await ExchangeAsync(
                connection,
                HardwareRecord(
                    agvId, StableGuid("recovery:hardware-record-mismatch"), generation,
                    StableGuid("recovery:hardware-record-mismatch-id"), sessionId!, secondActionId,
                    outOfScope, administratorId),
                "HardwareRecoveryRecordResult", cancellationToken).ConfigureAwait(false);
            hardwareCases.Add(Case(
                transcriptPath, "hardware-recovery-record-scope-rejected", scopeRejected,
                NestedProperty(scopeRejected, "payload", "outcome") == "REJECTED" &&
                NestedProperty(scopeRejected, "payload", "problem", "reasonCode") == "RECOVERY_SCOPE_MISMATCH",
                new Dictionary<string, object?>
                {
                    ["observedOutcome"] = NestedProperty(scopeRejected, "payload", "outcome"),
                    ["observedReasonCode"] = NestedProperty(scopeRejected, "payload", "problem", "reasonCode")
                }));
        }

        bool resultConflictClosed;
        await using (Connection connection = await Connection.OpenAsync(
            port, cancellationToken).ConfigureAwait(false))
        {
            long generation = await HandshakeAsync(
                connection, agvId, StableGuid("hello:recovery:" + ++helloSequence), credential, cancellationToken)
                .ConfigureAwait(false);
            await connection.WriteAsync(
                ForcedResult(
                    agvId, currentResultMessageId, generation, sessionId!, secondActionId,
                    2, scope, administratorId, "2026-08-26T12:00:09Z"),
                cancellationToken).ConfigureAwait(false);
            resultConflictClosed = await connection
                .ExpectClosedAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        bool authorisationPass = authorisationCases.All(item => Equals(item["status"], "PASS"));
        bool actionBoundaryPass = actionBoundaryCases.All(item => Equals(item["status"], "PASS"));
        bool demandScopedPass = demandScopedCases.All(item => Equals(item["status"], "PASS"));
        bool hardwarePass = hardwareCases.All(item => Equals(item["status"], "PASS"));
        bool disconnectPass = commandConnectionClosed &&
            acceptedFirstActionId == firstActionId &&
            replayedCommand is not null &&
            NestedProperty(replayedCommand, "payload", "recoveryActionId") == firstActionId &&
            NumberNestedProperty(replayedCommand, "payload", "forcedRecoveryGeneration") == 1;
        bool staleResultPass =
            NestedProperty(staleResultAck, "payload", "acceptedMessageType") == "ForcedMechanicalRecoveryResult" &&
            NestedProperty(staleResultAck, "payload", "acceptedMessageId") == StableGuid("recovery:forced-one-result");
        bool currentResultPass =
            NestedProperty(currentResultAck, "payload", "acceptedMessageId") == currentResultMessageId;
        bool resultReplayPass = currentResultAck == replayedResultAck && resultConflictClosed;

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = "1.0.0",
            ["status"] = authorisationPass && actionBoundaryPass && demandScopedPass && hardwarePass &&
                disconnectPass && staleResultPass && currentResultPass && resultReplayPass
                ? "PASS"
                : "FAIL",
            ["agvId"] = agvId,
            ["exceptionRecoverySessionId"] = sessionId,
            ["sessionAuthorisation"] = authorisationCases,
            ["actionBoundary"] = actionBoundaryCases,
            ["demandScopedAuthorisationBoundary"] = demandScopedCases,
            ["hardwareRecoveryRecord"] = hardwareCases,
            ["disconnectDuringRecoveryCommand"] = new Dictionary<string, object?>
            {
                ["status"] = disconnectPass ? "PASS" : "FAIL",
                ["recoveryActionId"] = firstActionId,
                ["connectionClosedBeforeCommandArrived"] = commandConnectionClosed,
                ["commandReplayedAfterReconnect"] = replayedCommand is not null,
                ["replayedCommandRecoveryActionId"] =
                    NestedProperty(replayedCommand, "payload", "recoveryActionId"),
                ["replayedCommandForcedRecoveryGeneration"] = replayedCommand is null
                    ? null
                    : (object)NumberNestedProperty(replayedCommand, "payload", "forcedRecoveryGeneration"),
                ["replayedCommandSha256"] = replayedCommand is null ? null : Sha256(replayedCommand)
            },
            ["forcedRecoveryGenerationBranches"] = new Dictionary<string, object?>
            {
                ["status"] = staleResultPass && currentResultPass && resultReplayPass ? "PASS" : "FAIL",
                ["firstRecoveryActionId"] = firstActionId,
                ["secondRecoveryActionId"] = secondActionId,
                ["staleGenerationResultAcknowledged"] = staleResultPass,
                ["currentGenerationResultAcknowledged"] = currentResultPass,
                ["resultReplayByteExact"] = currentResultAck == replayedResultAck,
                ["resultContentConflictClosedConnection"] = resultConflictClosed,
                ["currentResultAckSha256"] = Sha256(currentResultAck)
            },
            ["coverageLimits"] = new Dictionary<string, object?>
            {
                ["DisconnectDuringSlotOperation"] =
                    "Not reachable in a staged run. Interrupting a slot operation needs a StationOperations " +
                    "row in RecoveryRequired, and StationOperations is only written by " +
                    "PrepareSlotOperationAsync from a demand accepted through MesIngest and RIoT. The " +
                    "disconnect vector is therefore taken on the forced mechanical recovery command, which " +
                    "is the one authorised recovery command that needs no demand.",
                ["ResumeCompensationCorrectionCancellationAndFaultCargoAcceptedPaths"] =
                    "Only their refusal branches are reachable. Each accepted path needs a persisted " +
                    "StationOperations row -- RESUME_AFTER_REPAIR additionally needs a proven recovery " +
                    "checkpoint on the session -- so a staged run can assert that they are refused without " +
                    "a demand, not that they complete with one.",
                ["RiotUnknownReconciliation"] =
                    "Not reachable in a staged run. RIoT is pointed at a dead port and no demand exists, so " +
                    "no create attempt is made and no UNKNOWN disposition can arise. The behaviour is " +
                    "recorded in the audit chain of evidence/g3/20260829-authorized-single-real-create and " +
                    "still needs a demand-bearing run against the real RIoT to become an asserted vector."
            }
        });
    }

    /// <summary>
    /// Drives the OperationResult plane as a synthetic peer against a restored demand-bearing store.
    /// </summary>
    /// <remarks>
    /// This is the one vector a staged run cannot reach. An OperationResult is only accepted for a
    /// persisted StationOperations row, and ApplyOperationResultAsync deduplicates on ResultId or on
    /// (SlotOperationAttemptId, ForcedRecoveryGeneration), so an attempt that already carries a result
    /// can only ever produce a conflict. The accepted-then-replayed half therefore needs a store whose
    /// SlotOperationCommand was published but whose result never arrived -- a state only a real
    /// authorised field run produces, and one this probe restores rather than fabricates.
    ///
    /// Cross-session replay is deliberately not one of the cases: OperationResult has no replay
    /// identity hash that normalises sessionGeneration the way RecoveryStateReport does, so the same
    /// messageId resent under a new generation hashes differently and is a conflict by design. Each
    /// refusal case therefore opens its own connection, because a refusal closes the one it arrives on.
    /// </remarks>
    public static async Task<string> RunDemandBearingResultProbeAsync(
        int port,
        string credential,
        string agvId,
        string demandId,
        string preparedAttemptId,
        string preparedOperationType,
        int[] preparedSlots,
        string committedAttemptId,
        string committedOperationType,
        int[] committedSlots,
        string transcriptPath,
        CancellationToken cancellationToken)
    {
        File.WriteAllText(transcriptPath, string.Empty, new UTF8Encoding(false));
        var cases = new List<Dictionary<string, object?>>();
        int helloSequence = 0;

        string firstAck;
        string replayedAck;
        long acceptedGeneration;
        string? serverBuildCommit;
        string? serverInstanceId;
        string acceptedMessageId = StableGuid("demand-bearing:accepted-result");
        string acceptedLine;

        await using (Connection connection = await Connection.OpenAsync(port, cancellationToken)
            .ConfigureAwait(false))
        {
            // The hello is written out rather than delegated to HandshakeAsync because the identity the
            // running server reports is part of this run's evidence, not just its session number.
            await connection.WriteAsync(
                Hello(agvId, StableGuid("demand-bearing:hello:" + helloSequence++),
                      Protocol.Release, Protocol.Manifest, credential),
                cancellationToken).ConfigureAwait(false);
            string accepted = await connection.ReadRequiredAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            if (Property(accepted, "messageType") != "SessionAccepted")
            {
                throw new InvalidOperationException("The demand-bearing probe was not granted a session.");
            }
            acceptedGeneration = NumberProperty(accepted, "sessionGeneration");
            serverBuildCommit = NestedProperty(accepted, "payload", "serverBuildCommit");
            serverInstanceId = NestedProperty(accepted, "payload", "serverInstanceId");
            acceptedLine = OperationResultLine(
                agvId, acceptedMessageId, acceptedGeneration, demandId, preparedAttemptId,
                preparedOperationType, preparedSlots, "COMPLETED");
            firstAck = await ExchangeAsync(connection, acceptedLine, "DurableAck", cancellationToken)
                .ConfigureAwait(false);
            cases.Add(Case(transcriptPath, "preparedAttemptAcceptsItsFirstResult", firstAck,
                NestedProperty(firstAck, "payload", "acceptedMessageId") == acceptedMessageId,
                new Dictionary<string, object?>
                {
                    ["slotOperationAttemptId"] = preparedAttemptId,
                    ["messageId"] = acceptedMessageId,
                    ["requestSha256"] = Sha256(acceptedLine)
                }));

            // The same bytes a second time: the inbox has to answer from its stored first response
            // instead of running the business path again, so the two acknowledgements are identical.
            replayedAck = await ExchangeAsync(connection, acceptedLine, "DurableAck", cancellationToken)
                .ConfigureAwait(false);
            cases.Add(Case(transcriptPath, "identicalResultReplayReturnsTheStoredAcknowledgement",
                replayedAck, firstAck == replayedAck,
                new Dictionary<string, object?>
                {
                    ["firstAckSha256"] = Sha256(firstAck),
                    ["replayedAckSha256"] = Sha256(replayedAck)
                }));
        }

        bool contentConflictClosed = await ExpectRefusalAsync(
            port, credential, agvId, StableGuid("demand-bearing:hello:" + helloSequence++),
            generation => OperationResultLine(
                agvId, acceptedMessageId, generation, demandId, preparedAttemptId,
                preparedOperationType, preparedSlots, "COMPLETED_WITH_EXCEPTIONS"),
            cancellationToken).ConfigureAwait(false);
        cases.Add(Case(transcriptPath, "sameMessageIdWithDifferentContentIsRefused", null,
            contentConflictClosed,
            new Dictionary<string, object?> { ["messageId"] = acceptedMessageId }));

        bool sameGenerationRenumberClosed = await ExpectRefusalAsync(
            port, credential, agvId, StableGuid("demand-bearing:hello:" + helloSequence++),
            generation => OperationResultLine(
                agvId, StableGuid("demand-bearing:renumbered-result"), generation, demandId,
                preparedAttemptId, preparedOperationType, preparedSlots, "COMPLETED"),
            cancellationToken).ConfigureAwait(false);
        cases.Add(Case(transcriptPath, "sameAttemptAndGenerationUnderANewMessageIdIsRefused", null,
            sameGenerationRenumberClosed,
            new Dictionary<string, object?> { ["slotOperationAttemptId"] = preparedAttemptId }));

        bool committedAttemptClosed = await ExpectRefusalAsync(
            port, credential, agvId, StableGuid("demand-bearing:hello:" + helloSequence++),
            generation => OperationResultLine(
                agvId, StableGuid("demand-bearing:committed-attempt-result"), generation, demandId,
                committedAttemptId, committedOperationType, committedSlots, "COMPLETED"),
            cancellationToken).ConfigureAwait(false);
        cases.Add(Case(transcriptPath, "alreadyCommittedAttemptRefusesASecondResult", null,
            committedAttemptClosed,
            new Dictionary<string, object?> { ["slotOperationAttemptId"] = committedAttemptId }));

        bool staleGenerationClosed = await ExpectRefusalAsync(
            port, credential, agvId, StableGuid("demand-bearing:hello:" + helloSequence++),
            generation => OperationResultLine(
                agvId, StableGuid("demand-bearing:stale-generation-result"), generation - 1, demandId,
                preparedAttemptId, preparedOperationType, preparedSlots, "COMPLETED"),
            cancellationToken).ConfigureAwait(false);
        cases.Add(Case(transcriptPath, "resultFromASupersededSessionGenerationIsRefused", null,
            staleGenerationClosed,
            new Dictionary<string, object?> { ["acceptedGeneration"] = acceptedGeneration }));

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["agvId"] = agvId,
            ["demandId"] = demandId,
            ["serverBuildCommit"] = serverBuildCommit,
            ["serverInstanceId"] = serverInstanceId,
            ["acceptedGeneration"] = acceptedGeneration,
            ["acceptedMessageId"] = acceptedMessageId,
            ["acceptedRequestSha256"] = Sha256(acceptedLine),
            ["acceptedAckSha256"] = Sha256(firstAck),
            ["replayedAckSha256"] = Sha256(replayedAck),
            ["cases"] = cases
        });
    }

    /// <summary>
    /// Opens one plaintext session and reports the identity the server answered with.
    /// </summary>
    /// <remarks>
    /// Used to show that a server restarted onto the same store is serving again, and which process
    /// and session generation it is serving as. It sends nothing else: a restart vector must not also
    /// be a business vector, or a failure in one is reported as the other.
    /// </remarks>
    public static async Task<string> RunSessionHandshakeProbeAsync(
        int port,
        string credential,
        string agvId,
        string helloSalt,
        CancellationToken cancellationToken)
    {
        await using Connection connection = await Connection.OpenAsync(port, cancellationToken)
            .ConfigureAwait(false);
        await connection.WriteAsync(
            Hello(agvId, StableGuid("demand-bearing:handshake:" + helloSalt),
                  Protocol.Release, Protocol.Manifest, credential),
            cancellationToken).ConfigureAwait(false);
        string accepted = await connection.ReadRequiredAsync(TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        if (Property(accepted, "messageType") != "SessionAccepted")
        {
            throw new InvalidOperationException("The handshake probe was not granted a session.");
        }
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["sessionGeneration"] = NumberProperty(accepted, "sessionGeneration"),
            ["serverInstanceId"] = NestedProperty(accepted, "payload", "serverInstanceId"),
            ["serverBuildCommit"] = NestedProperty(accepted, "payload", "serverBuildCommit"),
            ["acceptedSha256"] = Sha256(accepted)
        });
    }

    /// <summary>
    /// Opens a fresh session, sends one line the server has to refuse, and reports whether it closed.
    /// </summary>
    private static async Task<bool> ExpectRefusalAsync(
        int port,
        string credential,
        string agvId,
        string helloMessageId,
        Func<long, string> buildLine,
        CancellationToken cancellationToken)
    {
        await using Connection connection = await Connection.OpenAsync(port, cancellationToken)
            .ConfigureAwait(false);
        long generation = await HandshakeAsync(connection, agvId, helloMessageId, credential, cancellationToken)
            .ConfigureAwait(false);
        await connection.WriteAsync(buildLine(generation), cancellationToken).ConfigureAwait(false);
        while (true)
        {
            string? line = await connection.ReadAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            if (line is null) return true;
            // The server pushes snapshots of its own alongside a session; only a DurableAck for this
            // message would mean the refusal did not happen.
            if (Property(line, "messageType") == "DurableAck") return false;
        }
    }

    /// <summary>
    /// Builds an OperationResult whose resultContentSha256 is computed the way the server recomputes it.
    /// </summary>
    /// <remarks>
    /// ComputeOperationResultContentHash decodes each field back to a CLR value before hashing, so the
    /// hash has to be built from CLR values here too -- hashing the wire text would disagree the moment
    /// the encoder escaped anything, which is exactly the defect that once refused every real result.
    /// </remarks>
    private static string OperationResultLine(
        string agvId,
        string messageId,
        long generation,
        string demandId,
        string slotOperationAttemptId,
        string operationType,
        int[] slots,
        string overallOutcome)
    {
        string finalPhysicalState = operationType == "LOAD" ? "OCCUPIED" : "EMPTY";
        DateTimeOffset observedAt = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        const string journalCheckpoint = "demand-bearing-result-probe";
        var slotResults = slots.Select(slot => new Dictionary<string, object?>
        {
            ["slotNo"] = slot,
            ["outcome"] = "COMPLETED",
            ["finalPhysicalState"] = finalPhysicalState,
            ["lockState"] = "LOCKED",
            ["unlockOutputState"] = "RESET",
            ["reasonCodes"] = Array.Empty<string>()
        }).ToArray();

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        byte[] businessContent = JsonSerializer.SerializeToUtf8Bytes(new
        {
            demandId,
            slotOperationAttemptId,
            operationType,
            overallOutcome,
            slotResults = slots.Select(slot => new
            {
                slotNo = slot,
                outcome = "COMPLETED",
                finalPhysicalState,
                lockState = "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = Array.Empty<string>()
            }).ToArray(),
            observedAt,
            journalCheckpoint
        }, options);
        string resultContentSha256 = Convert.ToHexString(SHA256.HashData(businessContent)).ToLowerInvariant();

        return Envelope(
            Protocol.Release,
            Protocol.Manifest,
            "OperationResult",
            messageId,
            agvId,
            generation,
            new Dictionary<string, object?>
            {
                ["demandId"] = demandId,
                ["slotOperationAttemptId"] = slotOperationAttemptId,
                ["operationType"] = operationType,
                ["overallOutcome"] = overallOutcome,
                ["slotResults"] = slotResults,
                ["observedAt"] = observedAt,
                ["journalCheckpoint"] = journalCheckpoint,
                ["resultContentSha256"] = resultContentSha256
            });
    }

    private static Dictionary<string, object?> Case(
        string transcriptPath,
        string name,
        string? response,
        bool passed,
        Dictionary<string, object?> detail)
    {
        var item = new Dictionary<string, object?>(detail)
        {
            ["case"] = name,
            ["status"] = passed ? "PASS" : "FAIL",
            ["responseType"] = response is null ? null : Property(response, "messageType"),
            ["responseSha256"] = response is null ? null : Sha256(response)
        };
        Log(transcriptPath, item);
        return item;
    }

    /// <summary>
    /// Writes one line and returns the first response of the expected type, skipping the snapshots and
    /// acknowledgements the server pushes alongside a recovery response.
    /// </summary>
    private static async Task<string> ExchangeAsync(
        Connection connection,
        string line,
        string expectedMessageType,
        CancellationToken cancellationToken)
    {
        await connection.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        return await ReadUntilAsync(connection, expectedMessageType, TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false) ??
            throw new EndOfStreamException("Expected a " + expectedMessageType + " response.");
    }

    private static async Task<string?> ReadUntilAsync(
        Connection connection,
        string expectedMessageType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            string? line = await connection.ReadAsync(remaining, cancellationToken).ConfigureAwait(false);
            if (line is null) return null;
            if (Property(line, "messageType") == expectedMessageType) return line;
        }
    }

    private static Dictionary<string, object?> OperatorContext(string operatorId) =>
        new()
        {
            ["operatorId"] = operatorId,
            ["verificationMethod"] = "BADGE",
            ["verifiedAt"] = "2026-08-26T12:00:00Z"
        };

    private static string SessionRequest(
        string agvId, string messageId, long generation, string requestId, string eventId,
        string? demandId, int[] slots, string authenticationProof, string administratorId) =>
        Envelope(
            Protocol.Release, Protocol.Manifest, "ExceptionRecoverySessionRequested", messageId, agvId, generation,
            new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["administrator"] = OperatorContext(administratorId),
                ["administratorRole"] = "MAINTENANCE_ADMINISTRATOR",
                ["eventId"] = eventId,
                ["demandId"] = demandId,
                ["slots"] = slots,
                ["reason"] = "STAGED_G3_RECOVERY_VECTOR",
                ["authenticationProof"] = authenticationProof
            });

    private static string ActionSubmit(
        string agvId, string messageId, long generation, string recoveryActionId, string sessionId,
        string action, string eventId, string? demandId, int[] slots, string operatorId) =>
        Envelope(
            Protocol.Release, Protocol.Manifest, "RecoveryActionSubmitted", messageId, agvId, generation,
            new Dictionary<string, object?>
            {
                ["recoveryActionId"] = recoveryActionId,
                ["exceptionRecoverySessionId"] = sessionId,
                ["action"] = action,
                ["eventId"] = eventId,
                ["demandId"] = demandId,
                ["slots"] = slots,
                ["operator"] = OperatorContext(operatorId),
                ["reason"] = "STAGED_G3_RECOVERY_VECTOR"
            });

    private static string ForcedResult(
        string agvId, string messageId, long generation, string sessionId, string recoveryActionId,
        long forcedRecoveryGeneration, int[] slots, string operatorId, string observedAt) =>
        Envelope(
            Protocol.Release, Protocol.Manifest, "ForcedMechanicalRecoveryResult", messageId, agvId, generation,
            new Dictionary<string, object?>
            {
                ["exceptionRecoverySessionId"] = sessionId,
                ["recoveryActionId"] = recoveryActionId,
                ["forcedRecoveryGeneration"] = forcedRecoveryGeneration,
                ["outcome"] = "MECHANICALLY_ISOLATED",
                ["slots"] = slots,
                ["operator"] = OperatorContext(operatorId),
                ["observedAt"] = observedAt,
                // A forced mechanical recovery is an isolation, never a proof that the vehicle is
                // empty or ready; the server must keep the workflow unreconciled on exactly this.
                ["electronicEmptyProven"] = false,
                ["vehicleReadyProven"] = false
            });

    private static string RecoveryReport(
        string agvId, string messageId, long generation, string reportId, long forcedRecoveryGeneration) =>
        Envelope(
            Protocol.Release, Protocol.Manifest, "RecoveryStateReport", messageId, agvId, generation,
            new Dictionary<string, object?>
            {
                ["reportId"] = reportId,
                ["observedAt"] = "2026-08-26T12:00:00Z",
                ["unsettledSlotOperationAttemptId"] = null,
                ["provenRecoveryCheckpoint"] = null,
                ["activeUnlockSlots"] = Array.Empty<int>(),
                ["forcedRecoveryGeneration"] = forcedRecoveryGeneration,
                ["pendingResults"] = Array.Empty<object>(),
                ["journalContentSha256"] = new string('0', 64)
            });

    private static string LoadCancellationRequest(
        string agvId, string messageId, long generation, string cancellationId, string demandId,
        string slotOperationAttemptId, string operatorId) =>
        Envelope(
            Protocol.Release, Protocol.Manifest, "LoadCancellationStartRequested", messageId, agvId, generation,
            new Dictionary<string, object?>
            {
                ["cancellationId"] = cancellationId,
                ["demandId"] = demandId,
                ["slotOperationAttemptId"] = slotOperationAttemptId,
                ["operator"] = OperatorContext(operatorId),
                ["reason"] = "STAGED_G3_RECOVERY_VECTOR"
            });

    private static string LoadCorrectionRequest(
        string agvId, string messageId, long generation, string correctionId, string demandId,
        string slotOperationAttemptId, int[] slots, string operatorId) =>
        Envelope(
            Protocol.Release, Protocol.Manifest, "LoadCorrectionRequested", messageId, agvId, generation,
            new Dictionary<string, object?>
            {
                ["correctionId"] = correctionId,
                ["demandId"] = demandId,
                ["slotOperationAttemptId"] = slotOperationAttemptId,
                ["slots"] = slots,
                ["operator"] = OperatorContext(operatorId),
                ["reason"] = "STAGED_G3_RECOVERY_VECTOR"
            });

    private static string LoadCompensationRequest(
        string agvId, string messageId, long generation, string recoveryActionId, string sessionId,
        string demandId, string slotOperationAttemptId, string operatorId) =>
        Envelope(
            Protocol.Release, Protocol.Manifest, "LoadCompensationRequested", messageId, agvId, generation,
            new Dictionary<string, object?>
            {
                ["recoveryActionId"] = recoveryActionId,
                ["exceptionRecoverySessionId"] = sessionId,
                ["demandId"] = demandId,
                ["slotOperationAttemptId"] = slotOperationAttemptId,
                ["operator"] = OperatorContext(operatorId)
            });

    private static string HardwareRecord(
        string agvId, string messageId, long generation, string recordId, string sessionId,
        string recoveryActionId, int[] slots, string operatorId) =>
        Envelope(
            Protocol.Release, Protocol.Manifest, "HardwareRecoveryRecordSubmitted", messageId, agvId, generation,
            new Dictionary<string, object?>
            {
                ["recordId"] = recordId,
                ["exceptionRecoverySessionId"] = sessionId,
                ["recoveryActionId"] = recoveryActionId,
                ["operator"] = OperatorContext(operatorId),
                ["administratorRole"] = "MAINTENANCE_ADMINISTRATOR",
                ["slots"] = slots,
                ["checksPerformed"] = new[] { "STAGED_G3_MECHANICAL_ISOLATION_VERIFIED" },
                ["actionsPerformed"] = new[] { "STAGED_G3_MECHANICAL_ISOLATION_APPLIED" },
                ["observations"] = new[] { "STAGED_G3_NO_PHYSICAL_ACTION_TAKEN" },
                ["observedAt"] = "2026-08-26T12:00:00Z"
            });

    private static long NumberNestedProperty(string? json, params string[] names)
    {
        if (json is null) throw new EndOfStreamException("Expected JSON response.");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement current = document.RootElement;
        foreach (string name in names) current = current.GetProperty(name);
        return current.GetInt64();
    }

    private static async Task<long> HandshakeAsync(
        Connection connection,
        string agvId,
        string helloMessageId,
        string credential,
        CancellationToken cancellationToken)
    {
        await connection.WriteAsync(
            Hello(agvId, helloMessageId, Protocol.Release, Protocol.Manifest, credential),
            cancellationToken).ConfigureAwait(false);
        string accepted = await connection.ReadRequiredAsync(TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);
        if (Property(accepted, "messageType") != "SessionAccepted")
        {
            throw new InvalidOperationException("The business probe was not granted a session.");
        }
        return NumberProperty(accepted, "sessionGeneration");
    }

    private static string Business(
        string messageType, string messageId, string agvId, long generation, int variant) =>
        Envelope(
            Protocol.Release,
            Protocol.Manifest,
            messageType,
            messageId,
            agvId,
            generation,
            BusinessPayload(messageType, variant));

    /// <summary>
    /// Protocol-shaped payloads for the four business messages, where variant 2 differs from
    /// variant 1 in exactly one business field so a same-messageId replay is a genuine conflict.
    /// </summary>
    private static Dictionary<string, object?> BusinessPayload(string messageType, int variant)
    {
        switch (messageType)
        {
            case "SublotSubmitted":
                return new Dictionary<string, object?>
                {
                    ["demandId"] = "STAGED-G3-DEMAND",
                    ["operationSessionId"] = StableGuid("business:operation-session"),
                    ["stationId"] = "STAGED-G3-STATION",
                    ["worklistRevision"] = variant,
                    ["sublot"] = "STAGED-G3-SUBLOT",
                    ["entryMethod"] = "SCANNER",
                    ["operator"] = new Dictionary<string, object?>
                    {
                        ["operatorId"] = "STAGED-G3-OPERATOR",
                        ["verificationMethod"] = "BADGE",
                        ["verifiedAt"] = "2026-08-26T12:00:00Z"
                    }
                };
            case "OperationProgress":
                return new Dictionary<string, object?>
                {
                    ["slotOperationAttemptId"] = StableGuid("business:attempt"),
                    ["phase"] = variant == 1 ? "VERIFYING" : "UNLOCKING",
                    ["activeUnlockSlots"] = Array.Empty<int>(),
                    ["completedSlots"] = new[] { 1, 2 },
                    ["observedAt"] = "2026-08-26T12:00:00Z"
                };
            case "PreDepartureSafetyCheckResult":
                return new Dictionary<string, object?>
                {
                    ["preDepartureSafetyCheckId"] = StableGuid("business:pre-departure-check"),
                    ["outcome"] = "SAFE",
                    ["observedAt"] = "2026-08-26T12:00:00Z",
                    ["safetyStateVersion"] = variant,
                    ["validUntil"] = "2026-08-26T12:00:02Z",
                    ["safety"] = new Dictionary<string, object?>
                    {
                        ["departureSafe"] = true,
                        ["vehicleStopped"] = true,
                        ["allTargetSlotsLocked"] = true,
                        ["allUnlockOutputsReset"] = true,
                        ["unknownPresent"] = false,
                        ["reasonCodes"] = Array.Empty<string>()
                    }
                };
            case "SlotOperationCommandRejected":
                return new Dictionary<string, object?>
                {
                    ["slotOperationAttemptId"] = StableGuid("business:attempt"),
                    ["problem"] = new Dictionary<string, object?>
                    {
                        ["reasonCode"] = "ACTION_NOT_ALLOWED_IN_STATE",
                        ["fieldPath"] = null,
                        ["displayMessage"] = null
                    },
                    ["observedCapabilityVersion"] = variant,
                    ["conflictingContentSha256"] = null
                };
            default:
                throw new InvalidOperationException("Unsupported business message type: " + messageType);
        }
    }

    public static async Task RunProxyAsync(
        int listenPort,
        int upstreamPort,
        string transcriptPath,
        CancellationToken cancellationToken)
    {
        File.WriteAllText(transcriptPath, string.Empty, new UTF8Encoding(false));
        TcpListener listener = new(IPAddress.Loopback, listenPort);
        listener.Start();
        using CancellationTokenRegistration registration = cancellationToken.Register(listener.Stop);
        Log(transcriptPath, new Dictionary<string, object?>
        {
            ["event"] = "proxy-listening",
            ["listenPort"] = listenPort,
            ["upstreamPort"] = upstreamPort,
            ["transport"] = "plaintext"
        });
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient downstream;
                try
                {
                    downstream = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
                int connectionId = Interlocked.Increment(ref _connectionSequence);
                await HandleProxyConnectionAsync(
                    downstream, upstreamPort, transcriptPath, connectionId, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task HandleProxyConnectionAsync(
        TcpClient downstream,
        int upstreamPort,
        string transcriptPath,
        int connectionId,
        CancellationToken cancellationToken)
    {
        using (downstream)
        using (TcpClient upstream = new())
        using (CancellationTokenSource connectionStopping =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            try
            {
                await upstream.ConnectAsync(IPAddress.Loopback, upstreamPort, cancellationToken).ConfigureAwait(false);
                Log(transcriptPath, new Dictionary<string, object?>
                {
                    ["event"] = "connection-opened",
                    ["connectionId"] = connectionId,
                    ["transport"] = "plaintext"
                });
                NetworkStream downstreamStream = downstream.GetStream();
                NetworkStream upstreamStream = upstream.GetStream();
                Task clientToServer = PumpAsync(
                    downstreamStream, upstreamStream, "client-to-server", transcriptPath,
                    connectionId, connectionStopping.Token);
                Task serverToClient = PumpAsync(
                    upstreamStream, downstreamStream, "server-to-client", transcriptPath,
                    connectionId, connectionStopping.Token);
                await Task.WhenAny(clientToServer, serverToClient).ConfigureAwait(false);
                connectionStopping.Cancel();
                downstream.Close();
                upstream.Close();
                try { await Task.WhenAll(clientToServer, serverToClient).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            catch (Exception error) when (
                error is IOException or OperationCanceledException or SocketException)
            {
                Log(transcriptPath, new Dictionary<string, object?>
                {
                    ["event"] = "connection-error",
                    ["connectionId"] = connectionId,
                    ["errorType"] = error.GetType().Name,
                    ["messageSha256"] = Sha256(error.Message)
                });
            }
            finally
            {
                Log(transcriptPath, new Dictionary<string, object?>
                {
                    ["event"] = "connection-closed",
                    ["connectionId"] = connectionId
                });
            }
        }
    }

    private static async Task PumpAsync(
        Stream source,
        Stream destination,
        string direction,
        string transcriptPath,
        int connectionId,
        CancellationToken cancellationToken)
    {
        using StreamReader reader = new(source, new UTF8Encoding(false), false, 65536, true);
        await using StreamWriter writer = new(destination, new UTF8Encoding(false), 65536, true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        string? held = null;
        Dictionary<string, object?>? heldMetadata = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                if (held is not null)
                {
                    Log(transcriptPath, Restate(heldMetadata!, "held-and-lost-on-close"));
                }
                return;
            }
            var metadata = Describe(line, direction, connectionId);
            // A held line is already the reorder subject, so the line that releases it is never
            // itself a fault candidate; that keeps a single rule from consuming both halves.
            FaultRule? rule = held is null ? MatchFault(direction, metadata) : null;
            if (rule is null)
            {
                metadata["action"] = held is null ? "forwarded" : "forwarded-ahead-of-held";
                Log(transcriptPath, metadata);
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (held is not null)
                {
                    Log(transcriptPath, Restate(heldMetadata!, "forwarded-after-reorder"));
                    await writer.WriteLineAsync(held.AsMemory(), cancellationToken).ConfigureAwait(false);
                    held = null;
                    heldMetadata = null;
                }
                continue;
            }

            switch (rule.Action)
            {
                case "drop-and-close":
                    metadata["action"] = "dropped-and-connection-closed";
                    Log(transcriptPath, metadata);
                    return;
                case "drop":
                    metadata["action"] = "dropped";
                    Log(transcriptPath, metadata);
                    continue;
                case "delay":
                    metadata["action"] = "delayed";
                    metadata["delayMilliseconds"] = rule.DelayMilliseconds;
                    Log(transcriptPath, metadata);
                    await Task.Delay(rule.DelayMilliseconds, cancellationToken).ConfigureAwait(false);
                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                    continue;
                case "hold-until-next":
                    metadata["action"] = "held-for-reorder";
                    Log(transcriptPath, metadata);
                    held = line;
                    heldMetadata = metadata;
                    continue;
                default:
                    throw new InvalidOperationException("Unknown fault action: " + rule.Action);
            }
        }
    }

    private static Dictionary<string, object?> Restate(Dictionary<string, object?> metadata, string action)
    {
        var restated = new Dictionary<string, object?>(metadata);
        restated["action"] = action;
        return restated;
    }

    private static Dictionary<string, object?> Describe(string line, string direction, int connectionId)
    {
        var value = new Dictionary<string, object?>
        {
            ["event"] = "message",
            ["connectionId"] = connectionId,
            ["direction"] = direction,
            ["wireSha256"] = Sha256(line)
        };
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            value["messageType"] = root.GetProperty("messageType").GetString();
            value["messageId"] = root.GetProperty("messageId").GetString();
            if (root.TryGetProperty("sessionGeneration", out JsonElement generation) &&
                generation.ValueKind == JsonValueKind.Number)
                value["sessionGeneration"] = generation.GetInt64();
            if (root.TryGetProperty("payload", out JsonElement payload))
            {
                value["payloadSha256"] = Sha256(payload.GetRawText());
                if (payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("acceptedMessageType", out JsonElement acceptedType))
                    value["acceptedMessageType"] = acceptedType.GetString();
                if (payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("acceptedMessageId", out JsonElement acceptedId))
                    value["acceptedMessageId"] = acceptedId.GetString();
                // SnapshotAppliedAck names what it applied with snapshotKind, not acceptedMessageType,
                // so without this a snapshot ack is indistinguishable from any other ack in the
                // transcript. FP-IS-15 needs to tell an ONBOARD_ALARM ack from a SAFETY_STATE one.
                if (payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("snapshotKind", out JsonElement snapshotKind))
                    value["snapshotKind"] = snapshotKind.GetString();
            }
        }
        catch (JsonException error) { value["parseErrorSha256"] = Sha256(error.Message); }
        return value;
    }

    private static string Hello(string agvId, string messageId, string release, string manifest, string credential)
    {
        var identity = new Dictionary<string, object?>
        {
            ["repository"] = "8005-agv-protocol",
            ["releaseVersion"] = release,
            ["tag"] = "protocol-v1.0.0",
            ["commit"] = Protocol.Commit,
            ["protocolVersion"] = 2,
            ["profileId"] = Protocol.Profile,
            ["manifestSha256"] = manifest,
            ["schemaBundleSha256"] = Protocol.Schema,
            ["vectorsSha256"] = Protocol.Vectors
        };
        return Envelope(
            release,
            manifest,
            "SessionHello",
            messageId,
            agvId,
            null,
            new Dictionary<string, object?>
            {
                ["onboardInstanceId"] = StableGuid("instance:" + agvId),
                ["onboardBuildCommit"] = new string('1', 40),
                ["supportedProtocolVersion"] = 1,
                ["profileId"] = Protocol.Profile,
                ["protocolReleaseIdentity"] = identity,
                ["credentialProof"] = credential
            });
    }

    private static string Heartbeat(string agvId, string messageId, long generation, long capabilityVersion) =>
        Envelope(
            Protocol.Release,
            Protocol.Manifest,
            "Heartbeat",
            messageId,
            agvId,
            generation,
            new Dictionary<string, object?>
            {
                ["capabilityVersion"] = capabilityVersion,
                ["safetyStateVersion"] = 1
            });

    private static string Envelope(
        string release,
        string manifest,
        string messageType,
        string messageId,
        string agvId,
        long? generation,
        object payload) => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["protocolVersion"] = 2,
            ["profileId"] = Protocol.Profile,
            ["protocolReleaseVersion"] = release,
            ["protocolReleaseManifestSha256"] = manifest,
            ["messageType"] = messageType,
            ["messageId"] = messageId,
            ["correlationId"] = null,
            ["agvId"] = agvId,
            ["sessionGeneration"] = generation,
            ["sentAt"] = "2026-08-26T12:00:00Z",
            ["payload"] = payload
        });

    private static string StableGuid(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString("D");
    }

    private static string? Property(string? json, string name)
    {
        if (json is null) return null;
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(name).GetString();
    }

    private static long NumberProperty(string? json, string name)
    {
        if (json is null) throw new EndOfStreamException("Expected JSON response.");
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(name).GetInt64();
    }

    private static string? NestedProperty(string? json, params string[] names)
    {
        if (json is null) return null;
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement current = document.RootElement;
        foreach (string name in names) current = current.GetProperty(name);
        return current.GetString();
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void Log(string path, Dictionary<string, object?> value)
    {
        value["atUtc"] = DateTimeOffset.UtcNow;
        string line = JsonSerializer.Serialize(value);
        lock (LogGate) File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
    }

    private static class Protocol
    {
        public const string Release = "1.0.0";
        public const string Profile = "AGV_FULL_PRODUCT";
        public const string Commit = "16e2567a7033883f00fc999f7fa08f954dd13a26";
        public const string Manifest = "25fd6689e8234b7d481874b408109cd27eb0f02fbb023225385d6642e9bfd3d0";
        public const string Schema = "225a83340eb5f27c4e6dfd7bf8aba8007cf787d29f1df860deaf0ba039baf3ff";
        public const string Vectors = "51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df";
    }

    private sealed class Connection : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly Stream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private Connection(TcpClient client, Stream stream)
        {
            _client = client;
            _stream = stream;
            _reader = new StreamReader(stream, new UTF8Encoding(false), false, 65536, true);
            _writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
        }

        /// <summary>
        /// Opens the NDJSON framing over a plaintext socket.
        /// </summary>
        /// <remarks>
        /// This used to be two methods. The staged probes pinned the server's TLS leaf fingerprint
        /// because they exercise the deployed transport, while the store-replay runner opened a plain
        /// socket because it tests the message plane instead. The deployed transport is plaintext
        /// now, so there is one way in and nothing left to pin.
        /// </remarks>
        public static async Task<Connection> OpenAsync(
            int port, CancellationToken cancellationToken)
        {
            TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
            return new Connection(client, client.GetStream());
        }

        public async Task WriteAsync(string line, CancellationToken cancellationToken) =>
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);

        public async Task<string?> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            try { return await _reader.ReadLineAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
            catch (IOException) { return null; }
        }

        public async Task<string> ReadRequiredAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            await ReadAsync(timeout, cancellationToken).ConfigureAwait(false) ??
            throw new EndOfStreamException("Expected an NDJSON response.");

        public async Task<bool> ExpectClosedAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            await ReadAsync(timeout, cancellationToken).ConfigureAwait(false) is null;

        public async ValueTask DisposeAsync()
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
            _reader.Dispose();
            await _stream.DisposeAsync().ConfigureAwait(false);
            _client.Dispose();
        }
    }
}
'@

# The synthetic peer's identity is compiled into that literal here-string, so it cannot read
# appsettings.json the way the rest of this script now does. Assert instead of substitute: a peer
# built against a different release than the server it handshakes with is the exact failure the
# identity switch is supposed to make loud, and it must not be discovered as a puzzling rejection
# halfway through a staged run.
$embeddedIdentity = [ordered]@{
    'Protocol.Release'  = $expectedProtocol.releaseVersion
    'Protocol.Profile'  = $expectedProtocol.profileId
    'Protocol.Commit'   = $expectedProtocol.repositoryCommit
    'Protocol.Manifest' = $expectedProtocol.manifestSha256
    'Protocol.Schema'   = $expectedProtocol.schemaBundleSha256
    'Protocol.Vectors'  = $expectedProtocol.vectorsSha256
}
$identityDrift = @(
    foreach ($name in $embeddedIdentity.Keys) {
        $constant = $name.Split('.')[1]
        $pattern = "public const string $constant = ""$($embeddedIdentity[$name])"";"
        if ($harnessSource -notmatch [regex]::Escape($pattern)) { $name }
    }
)
if ($harnessSource -notmatch '\["protocolVersion"\] = ' + $expectedProtocol.protocolVersion + ',') {
    $identityDrift += 'protocolVersion'
}
if ($harnessSource -notmatch '\["tag"\] = "' + [regex]::Escape($expectedProtocol.tag) + '",') {
    $identityDrift += 'tag'
}
if ($identityDrift.Count -gt 0) {
    throw ("The synthetic peer compiled into this script names a different protocol release than " +
           "appsettings.json does. Drifted: " + ($identityDrift -join ', ') +
           ". Update the harness source in this file to match ProtocolCandidateIdentity.")
}

Add-Type -TypeDefinition $harnessSource -Language CSharp

$credential = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
# The recovery administrator proof is a server-side secret compared in fixed time. It is generated
# per run, never written to evidence, and the inbox stores the request with the field redacted.
$recoveryProof = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
# FP-IS-14's entry point credential. A third one rather than a reuse: the secret scan below asserts
# that no evidence file carries a run secret, and three distinct values make that check able to tell
# which surface leaked.
$governanceCredential = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
$control = $null
$onboard = $null
$simulator = $null
$proxyStopping = $null
$proxyTask = $null
$businessProxyStopping = $null
$businessProxyTask = $null
$probeResult = $null
$businessProbeResult = $null
$recoveryProbeResult = $null
$businessAckDropObservation = $null
$recoveryFaultObservation = $null
$runtimeObservation = $null
$runError = $null
$protocolG1Status = 'NOT_RUN'
$protocolTagExists = $false
$version = $null
$simulatorHealth = $null
$proxyTranscript = Join-Path $EvidenceRoot 'fault-proxy-events.ndjson'
$probeTranscript = Join-Path $EvidenceRoot 'probe-events.ndjson'
$businessProxyTranscript = Join-Path $EvidenceRoot 'business-fault-proxy-events.ndjson'
$businessProbeTranscript = Join-Path $EvidenceRoot 'business-probe-events.ndjson'
$recoveryProbeTranscript = Join-Path $EvidenceRoot 'recovery-probe-events.ndjson'
# Released in `finally` after the peers are stopped. Declared here so that release is unconditional
# even when the run dies during the clone or build phase, before the lock was ever taken.
$desktopLock = $null

try {
    New-ExactClone -Name 'control-server' -Repository $ControlServerRepository -Destination $controlSource `
        -Commit $ControlServerCommit
    New-ExactClone -Name 'onboard-hmi' -Repository $OnboardRepository -Destination $onboardSource `
        -Commit $OnboardCommit -RemoteRef $OnboardRemoteRef
    New-ExactClone -Name 'slots-simulator' -Repository $SimulatorRepository -Destination $simulatorSource `
        -Commit $SimulatorCommit -RemoteRef 'origin/main'
    New-ExactClone -Name 'protocol' -Repository $ProtocolRepository -Destination $protocolSource `
        -Commit $ProtocolCommit

    # Bind the candidate commit, not the tag. protocol-v1.0.0 has not been cut -- spec 6.6 wants two
    # product owners' attestation plus an annotated tag, and neither has happened, so the protocol
    # repository still carries only v0.1.0/v0.1.1/v0.2.0/v0.3.0. Asserting the tag resolves is how
    # this runner used to establish protocol identity; on the v2 line that assertion throws before
    # the run starts. Ticket 15 made exactly this change on the onboard side (run-w2g-g2.ps1:370).
    #
    # Not a weakening: the identity that matters is the manifest/schema/vector digests, and G1 below
    # refuses unless its output carries $manifestSha256. The tag only ever named that commit. What
    # stays enforced is that if the tag DOES exist it must point at the candidate -- a tag pointing
    # somewhere else means someone cut a release from other content, and that must not run silently.
    # Three states, kept apart on purpose: tag absent, tag verified, git itself failed. A single
    # `rev-list ... 2>$null` collapses the third into the first and records tagExists=false, which
    # reads as "checked, none" -- the one shape that must not be silent, because the assertion this
    # replaces used to throw. And `rev-list "$tag^{commit}"` is not namespaced: measured 2026-09-09,
    # a *branch* named protocol-v1.0.0 satisfies it while `git tag --list` returns nothing, so that
    # form would report tagExists=true for a branch. Ask the tag namespace explicitly.
    $listedTags = @(& git -C $protocolSource tag --list $protocolTag)
    if ($LASTEXITCODE -ne 0) { throw "Unable to enumerate protocol tags in $protocolSource" }
    $protocolTagExists = $listedTags.Count -gt 0
    if ($protocolTagExists) {
        $tagCommit = (& git -C $protocolSource rev-list -n 1 "refs/tags/$protocolTag^{commit}").Trim()
        if ($LASTEXITCODE -ne 0) { throw "Unable to resolve refs/tags/$protocolTag in $protocolSource" }
        if ($tagCommit -ne $ProtocolCommit) {
            throw "$protocolTag exists but resolves to $tagCommit, not the candidate $ProtocolCommit"
        }
    }
    # The exact clone carries no node_modules, and G1 validates against ajv, so restore first.
    Invoke-LoggedCommand -Name 'protocol-install' -WorkingDirectory $protocolSource -FilePath $pnpmFilePath `
        -Arguments ($pnpmPrefixArguments + @('install', '--frozen-lockfile')) `
        -LogPath (Join-Path $logsRoot 'protocol-install.log') | Out-Null
    $g1Output = Invoke-LoggedCommand -Name 'protocol-g1' -WorkingDirectory $protocolSource -FilePath $pnpmFilePath `
        -Arguments ($pnpmPrefixArguments + @('g1')) -LogPath (Join-Path $logsRoot 'protocol-g1.log')
    $g1Text = $g1Output -join [Environment]::NewLine
    if ($g1Text -notmatch '"status"\s*:\s*"PASS"' -or
        $g1Text -notmatch [regex]::Escape($manifestSha256)) {
        throw 'Protocol G1 did not bind the expected release manifest.'
    }
    $protocolG1Status = 'PASS'

    Invoke-LoggedCommand -Name 'publish-control-server' -WorkingDirectory $controlSource -FilePath 'dotnet' `
        -Arguments @('publish', '.\src\ControlServer.Host\ControlServer.Host.csproj', '-c', 'Release', '-o', $controlPublish) `
        -LogPath (Join-Path $logsRoot 'publish-control-server.log') | Out-Null
    Invoke-LoggedCommand -Name 'publish-onboard-hmi' -WorkingDirectory $onboardSource -FilePath 'dotnet' `
        -Arguments @('publish', '.\src\SQCD.Agv.Wpf\SQCD.Agv.Wpf.csproj', '-c', 'Release', '-o', $onboardPublish) `
        -LogPath (Join-Path $logsRoot 'publish-onboard-hmi.log') | Out-Null
    Invoke-LoggedCommand -Name 'publish-slots-simulator' -WorkingDirectory $simulatorSource -FilePath 'dotnet' `
        -Arguments @('publish', '.\src\SQCD_8005AGV_Simulator\SQCD_8005AGV_Simulator.csproj', '-c', 'Release', '-o', $simulatorPublish) `
        -LogPath (Join-Path $logsRoot 'publish-slots-simulator.log') | Out-Null
    # FP-IS-14. The activation entry point refuses a target whose IO bindings are not published, and
    # nothing in a fresh staged database has published any -- that is a governance act, and the tool
    # that performs it is ControlServer.FieldOps, from the same exact clone as the server.
    Invoke-LoggedCommand -Name 'publish-field-ops' -WorkingDirectory $controlSource -FilePath 'dotnet' `
        -Arguments @('publish', '.\tools\ControlServer.FieldOps\ControlServer.FieldOps.csproj', '-c', 'Release', '-o', $fieldOpsPublish) `
        -LogPath (Join-Path $logsRoot 'publish-field-ops.log') | Out-Null
    # Node reuse is off for this process tree, but the compiler server the publishes started still sits in
    # memory. Shut it down before the peers start, and log it like every other command. R-4.
    Invoke-LoggedCommand -Name 'build-server-shutdown' -WorkingDirectory $controlSource -FilePath 'dotnet' `
        -Arguments @('build-server', 'shutdown') `
        -LogPath (Join-Path $logsRoot 'build-server-shutdown.log') | Out-Null

    $onboardConfig = Join-Path $onboardPublish 'appsettings.json'
    $settings = Get-Content -LiteralPath $onboardConfig -Raw | ConvertFrom-Json
    $settings.environment = 'Development'
    $settings.agvId = $agvId
    $settings.onboardInstanceId = 'OBU-8005-STAGED-G3-01'
    $settings.wireToGate.enabled = $true
    $settings.wireToGate.host = '127.0.0.1'
    $settings.wireToGate.port = $proxyPort
    $settings.wireToGate.onboardInstanceId = '9bd45b8f-b7cb-45d1-bdab-4f6a22347e2e'
    $settings.wireToGate.onboardBuildCommit = $OnboardCommit
    $settings.wireToGate.credentialEnvironmentVariable = 'CONTROL_SERVER_ONBOARD_CREDENTIAL'
    # A TLS-era onboard build still carries these two keys and a plaintext one will not, so touch
    # them only where they exist: this runner has to span both sides of the onboard cutover.
    if ($settings.wireToGate.PSObject.Properties.Name -contains 'useTls') {
        $settings.wireToGate.useTls = $false
    }
    if ($settings.wireToGate.PSObject.Properties.Name -contains 'serverCertificateSha256') {
        $settings.wireToGate.PSObject.Properties.Remove('serverCertificateSha256')
    }
    $settings.wireToGate.connectTimeoutMs = 3000
    $settings.wireToGate.messageTimeoutMs = 3000
    $settings.wireToGate.journalPath = Join-Path $runtimeRoot 'onboard-journal.db'
    $settings.logging.directory = Join-Path $runtimeRoot 'onboard-logs'
    $settings.logging.writeToConsole = $true
    $settings | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $onboardConfig -Encoding utf8NoBOM

    $controlEnvironment = @{
        'CONTROL_SERVER_ONBOARD_CREDENTIAL' = $credential
        'CONTROL_SERVER_RECOVERY_AUTHENTICATION_PROOF' = $recoveryProof
        'ConnectionStrings__ControlServer' = 'Data Source=' + (Join-Path $runtimeRoot 'controlserver.db')
        'Health__url' = "http://127.0.0.1:$healthPort"
        'OnboardTransport__listenAddress' = '127.0.0.1'
        'OnboardTransport__port' = [string]$controlPort
        'OnboardTransport__credentialEnvironmentVariable' = 'CONTROL_SERVER_ONBOARD_CREDENTIAL'
        'JourneyRuntime__enabled' = 'false'
        'MesIngest__baseUrl' = 'http://127.0.0.1:1'
        'RIoT__baseUrl' = 'http://127.0.0.1:1'
        'ControlServerBuild__commit' = $ControlServerCommit
        # FP-IS-14. Off by default in the product; a run that wants to prove the activation path has
        # to turn it on deliberately, exactly as a site would.
        'SlotConfigurationActivation__enabled' = 'true'
        'SlotConfigurationActivation__credentialEnvironmentVariable' = 'CONTROL_SERVER_GOVERNANCE_CREDENTIAL'
        'CONTROL_SERVER_GOVERNANCE_CREDENTIAL' = $governanceCredential
    }
    $control = Start-Process -FilePath 'dotnet' `
        -ArgumentList @(Join-Path $controlPublish 'ControlServer.Host.dll') `
        -WorkingDirectory $controlPublish `
        -RedirectStandardOutput (Join-Path $logsRoot 'control.out.log') `
        -RedirectStandardError (Join-Path $logsRoot 'control.err.log') `
        -Environment $controlEnvironment -WindowStyle Hidden -PassThru
    $version = Wait-HttpJson -Uri "http://127.0.0.1:$healthPort/version"
    if ($version.protocolTag -ne $protocolTag -or
        $version.protocolCommit -ne $ProtocolCommit -or
        $version.manifestSha256 -ne $manifestSha256) {
        throw 'Running ControlServer reported an unexpected protocol identity.'
    }

    # FP-IS-14, and the ordering matters. The activation entry point refuses a target that is not
    # published with complete IO bindings, and a staged database has published none: these two
    # commands are the governance acts a site performs in its W1 field window. They run against the
    # SQLite file the server is already using -- SQLite serialises the write, and the vehicle has not
    # connected yet, so the server is idle here.
    $fieldOpsDatabase = Join-Path $runtimeRoot 'controlserver.db'
    $seedOutput = Invoke-LoggedCommand -Name 'field-ops-seed-approved-facts' -WorkingDirectory $fieldOpsPublish -FilePath 'dotnet' `
        -Arguments @((Join-Path $fieldOpsPublish 'ControlServer.FieldOps.dll'), 'seed-approved-facts', '--database', $fieldOpsDatabase) `
        -LogPath (Join-Path $logsRoot 'field-ops-seed-approved-facts.log')
    # Read the model id the tool reports rather than restating the constant here. A second copy of an
    # identifier is how a runner ends up activating something the database does not have.
    $slotModelVersionId = (($seedOutput -join '') | ConvertFrom-Json).slotModelVersionId
    if ([string]::IsNullOrWhiteSpace($slotModelVersionId)) {
        throw 'ControlServer.FieldOps seed-approved-facts did not report a slotModelVersionId.'
    }
    Invoke-LoggedCommand -Name 'field-ops-bind-io' -WorkingDirectory $fieldOpsPublish -FilePath 'dotnet' `
        -Arguments @((Join-Path $fieldOpsPublish 'ControlServer.FieldOps.dll'), 'bind-io', '--database', $fieldOpsDatabase, '--agv', $agvId) `
        -LogPath (Join-Path $logsRoot 'field-ops-bind-io.log') | Out-Null

    $probeJson = [StagedG3TlsHarness]::RunProbeAsync(
        $controlPort,
        $credential,
        $probeTranscript,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    [IO.File]::WriteAllText(
        (Join-Path $EvidenceRoot 'probe-result.json'),
        $probeJson,
        [Text.UTF8Encoding]::new($false))
    $probeResult = $probeJson | ConvertFrom-Json

    # The simulator and the onboard client are both WPF: from here to teardown this run owns
    # win11-01's single interactive desktop, and 8005-mes-ingest's desktop suites take the same
    # machine-wide mutex. `-WindowStyle Hidden` hides a console that these processes do not have; it
    # does not keep them off the desktop. See scripts/DesktopLock.psm1.
    #
    # Late on purpose: everything above -- clone, protocol G1, publish, the headless ControlServer
    # and the TLS probe -- touches no desktop, so a queued run waits without holding it.
    $desktopLock = Enter-DesktopLock -Reason "staged G3 run $runId"

    $simulator = Start-Process -FilePath 'dotnet' `
        -ArgumentList @(Join-Path $simulatorPublish 'SQCD_8005AGV_Simulator.dll') `
        -WorkingDirectory $simulatorPublish `
        -RedirectStandardOutput (Join-Path $logsRoot 'simulator.out.log') `
        -RedirectStandardError (Join-Path $logsRoot 'simulator.err.log') `
        -WindowStyle Hidden -PassThru
    $simulatorHealth = Wait-HttpJson -Uri "http://127.0.0.1:$simulatorHttpPort/api/v1/health"

    # The real onboard peer only ever sends SessionHello, the two snapshots, RecoveryStateReport and
    # Heartbeat, so a rule naming any business message type cannot fire on its traffic. That is what
    # lets the recovery vector and the business vectors share one armed rule table.
    [StagedG3TlsHarness]::AddFault('server-to-client', 'DurableAck', 'RecoveryStateReport', 'drop-and-close', 0)

    $proxyStopping = [Threading.CancellationTokenSource]::new()
    $proxyTask = [StagedG3TlsHarness]::RunProxyAsync(
        $proxyPort,
        $controlPort,
        $proxyTranscript,
        $proxyStopping.Token)
    $proxyDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        $proxyReady = @(Read-Ndjson $proxyTranscript | Where-Object event -EQ 'proxy-listening').Count -eq 1
        if (-not $proxyReady) { Start-Sleep -Milliseconds 100 }
    } while (-not $proxyReady -and [DateTimeOffset]::UtcNow -lt $proxyDeadline)
    if (-not $proxyReady) { throw 'Loopback fault proxy did not become ready.' }

    $onboard = Start-Process -FilePath 'dotnet' `
        -ArgumentList @(Join-Path $onboardPublish 'SQCD.Agv.Wpf.dll') `
        -WorkingDirectory $onboardPublish `
        -RedirectStandardOutput (Join-Path $logsRoot 'onboard.out.log') `
        -RedirectStandardError (Join-Path $logsRoot 'onboard.err.log') `
        -Environment @{ 'CONTROL_SERVER_ONBOARD_CREDENTIAL' = $credential } `
        -WindowStyle Hidden -PassThru

    $replayDeadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        $events = Read-Ndjson $proxyTranscript
        $reports = @($events | Where-Object {
            $_.event -eq 'message' -and $_.direction -eq 'client-to-server' -and
            $_.messageType -eq 'RecoveryStateReport'
        })
        $dropped = @($events | Where-Object {
            $_.event -eq 'message' -and $_.direction -eq 'server-to-client' -and
            $_.messageType -eq 'DurableAck' -and $_.acceptedMessageType -eq 'RecoveryStateReport' -and
            $_.action -eq 'dropped-and-connection-closed'
        })
        $forwarded = @($events | Where-Object {
            $_.event -eq 'message' -and $_.direction -eq 'server-to-client' -and
            $_.messageType -eq 'DurableAck' -and $_.acceptedMessageType -eq 'RecoveryStateReport' -and
            $_.action -eq 'forwarded'
        })
        $reportConnections = @($reports.connectionId | Sort-Object -Unique)
        if ($dropped.Count -eq 1 -and $forwarded.Count -ge 1 -and
            $reports.Count -ge 2 -and $reportConnections.Count -ge 2) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $replayDeadline)

    Start-Sleep -Seconds 3
    $sessionsJson = (Invoke-WebRequest -Uri "http://127.0.0.1:$healthPort/api/runtime/sessions" -TimeoutSec 5).Content
    [IO.File]::WriteAllText(
        (Join-Path $EvidenceRoot 'runtime-sessions.json'),
        $sessionsJson,
        [Text.UTF8Encoding]::new($false))
    $sessionEvidence = $null
    $sessionsDocument = [Text.Json.JsonDocument]::Parse($sessionsJson)
    try {
        foreach ($candidateSession in $sessionsDocument.RootElement.EnumerateArray()) {
            if ($candidateSession.GetProperty('agvId').GetString() -ceq $agvId) {
                $sessionEvidence = [ordered]@{
                    agvId = $candidateSession.GetProperty('agvId').GetString()
                    sessionGeneration = $candidateSession.GetProperty('sessionGeneration').GetInt64()
                    readiness = $candidateSession.GetProperty('readiness').GetString()
                    reasonCode = $candidateSession.GetProperty('reasonCode').GetString()
                    updatedAt = $candidateSession.GetProperty('updatedAt').GetString()
                }
                break
            }
        }
    }
    finally { $sessionsDocument.Dispose() }
    $events = Read-Ndjson $proxyTranscript
    $reports = @($events | Where-Object {
        $_.event -eq 'message' -and $_.direction -eq 'client-to-server' -and
        $_.messageType -eq 'RecoveryStateReport'
    })
    $dropped = @($events | Where-Object {
        $_.event -eq 'message' -and $_.direction -eq 'server-to-client' -and
        $_.messageType -eq 'DurableAck' -and $_.acceptedMessageType -eq 'RecoveryStateReport' -and
        $_.action -eq 'dropped-and-connection-closed'
    })
    $forwarded = @($events | Where-Object {
        $_.event -eq 'message' -and $_.direction -eq 'server-to-client' -and
        $_.messageType -eq 'DurableAck' -and $_.acceptedMessageType -eq 'RecoveryStateReport' -and
        $_.action -eq 'forwarded'
    })
    $droppedReportId = if ($dropped.Count -eq 1) { $dropped[0].acceptedMessageId } else { $null }
    $replayedReports = @($reports | Where-Object messageId -EQ $droppedReportId)
    $forwardedReplayAcks = @($forwarded | Where-Object acceptedMessageId -EQ $droppedReportId)
    $runtimeObservation = [ordered]@{
        droppedAckCount = $dropped.Count
        droppedReportMessageId = $droppedReportId
        forwardedReplayAckCount = $forwardedReplayAcks.Count
        recoveryReportSendCount = $replayedReports.Count
        allRecoveryReportSendCount = $reports.Count
        messageIds = @($replayedReports.messageId | Sort-Object -Unique)
        payloadSha256 = @($replayedReports.payloadSha256 | Sort-Object -Unique)
        wireSha256 = @($replayedReports.wireSha256 | Sort-Object -Unique)
        connectionIds = @($replayedReports.connectionId | Sort-Object -Unique)
        sessionGenerations = @($replayedReports.sessionGeneration | Sort-Object -Unique)
        sessionAfterFault = $sessionEvidence
    }

    # FP-IS-14: issue one activation against the live peer and let the vehicle answer.
    #
    # This is the slice's whole point, and it is the ONLY place either repository can prove it. The
    # command carries a version name and a fingerprint, never the configuration itself -- no message
    # in the protocol carries slot IO bindings. So the vehicle recomputes the fingerprint of what it
    # actually holds and compares. A success reported back therefore means the two ends computed the
    # SAME digest from their own copies, with their own code, in separate processes. Pinned literals
    # in each repository's unit tests can only say neither drifted from a written-down value; this
    # says they agree.
    #
    # And the command is dropped in flight on purpose. REQ-0264 is why messages 7/8 are RELIABLE and
    # not REQUEST/RESPONSE: a vehicle that never received the command must not leave the server
    # believing anything, and a reconnect has to re-deliver the SAME line rather than mint a second
    # activation. The drop-and-close fault fires once, so the first delivery dies with its connection
    # and the SLOT_CONFIGURATION recovery role has to carry the rest.
    [StagedG3TlsHarness]::AddFault('server-to-client', 'SlotConfigurationActivationCommand', $null, 'drop-and-close', 0)
    $activationIssuedAt = [DateTimeOffset]::UtcNow
    $activationResponse = $null
    $activationIssueError = $null
    try {
        $activationResponse = Invoke-RestMethod -Method Post `
            -Uri "http://127.0.0.1:$healthPort/api/governance/v1/slot-configuration-activations" `
            -Headers @{ Authorization = "Bearer $governanceCredential" } `
            -ContentType 'application/json' `
            -Body (@{
                agvId = $agvId
                slotModelVersionId = $slotModelVersionId
                administrator = @{
                    operatorId = 'op-staged-g3'
                    verificationMethod = 'BADGE'
                    verifiedAt = $activationIssuedAt.ToString('O')
                }
            } | ConvertTo-Json -Depth 5) `
            -TimeoutSec 20
    }
    catch {
        # Recorded, not thrown: a refused issue is a FAIL for this slice, not a reason to abandon the
        # run and lose every other slice's evidence.
        $activationIssueError = $_.Exception.Message
    }

    $activationDeadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    do {
        $activationEvents = Read-Ndjson $proxyTranscript
        $activationResults = @($activationEvents | Where-Object {
            $_.event -eq 'message' -and $_.direction -eq 'client-to-server' -and
            $_.messageType -eq 'SlotConfigurationActivationResult'
        })
        $activationCommandAttempts = @($activationEvents | Where-Object {
            $_.event -eq 'message' -and $_.direction -eq 'server-to-client' -and
            $_.messageType -eq 'SlotConfigurationActivationCommand'
        })
        # Two deliveries and one result: the dropped one, the replay after the reconnect, and the
        # vehicle's answer to the replay.
        if ($activationResults.Count -ge 1 -and $activationCommandAttempts.Count -ge 2) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $activationDeadline)
    # The result is RELIABLE, so the server acks it durably; give that ack a moment to land before
    # the peer is torn down, or the activation reads as still pending for reasons of timing alone.
    Start-Sleep -Seconds 2

    # OnboardTcpServer.ExecuteAsync awaits each accepted connection to completion before accepting
    # the next, so the server holds exactly one onboard peer at a time; a synthetic peer opened while
    # the real one is connected sits in the accept backlog and is never read. Release the peer and its
    # proxy first, then give the business plane the server to itself.
    Stop-ProcessSafely -Process $onboard
    $onboard = $null
    $proxyStopping.Cancel()
    try { $proxyTask.Wait(5000) | Out-Null } catch { }
    $proxyStopping.Dispose()
    $proxyStopping = $null
    $proxyTask = $null
    # Killing the peer closes its socket, but the server still has to unwind HandleClientAsync before
    # its accept loop comes back round.
    Start-Sleep -Seconds 2

    $businessProxyStopping = [Threading.CancellationTokenSource]::new()
    $businessProxyTask = [StagedG3TlsHarness]::RunProxyAsync(
        $businessProxyPort,
        $controlPort,
        $businessProxyTranscript,
        $businessProxyStopping.Token)
    $businessProxyDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        $businessProxyReady = @(Read-Ndjson $businessProxyTranscript |
            Where-Object event -EQ 'proxy-listening').Count -eq 1
        if (-not $businessProxyReady) { Start-Sleep -Milliseconds 100 }
    } while (-not $businessProxyReady -and [DateTimeOffset]::UtcNow -lt $businessProxyDeadline)
    if (-not $businessProxyReady) { throw 'Business fault proxy did not become ready.' }

    $businessProbeJson = [StagedG3TlsHarness]::RunBusinessProbeAsync(
        $businessProxyPort,
        $credential,
        $businessProbeTranscript,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    [IO.File]::WriteAllText(
        (Join-Path $EvidenceRoot 'business-probe-result.json'),
        $businessProbeJson,
        [Text.UTF8Encoding]::new($false))
    $businessProbeResult = $businessProbeJson | ConvertFrom-Json

    # The probe cannot see the acknowledgement the proxy swallowed, so the byte-exactness of the
    # replay is proven from the transcript's hash of the dropped line, not from the probe alone.
    $businessEvents = Read-Ndjson $businessProxyTranscript
    $businessDropped = @($businessEvents | Where-Object {
        $_.event -eq 'message' -and $_.direction -eq 'server-to-client' -and
        $_.messageType -eq 'DurableAck' -and
        $_.acceptedMessageType -eq 'PreDepartureSafetyCheckResult' -and $_.action -eq 'dropped'
    })
    $businessDelayed = @($businessEvents | Where-Object {
        $_.event -eq 'message' -and $_.direction -eq 'client-to-server' -and
        $_.messageType -eq 'SlotOperationCommandRejected' -and $_.action -eq 'delayed'
    })
    $businessHeld = @($businessEvents | Where-Object {
        $_.event -eq 'message' -and $_.direction -eq 'client-to-server' -and
        $_.messageType -eq 'SublotSubmitted' -and $_.action -eq 'held-for-reorder'
    })
    $businessReleased = @($businessEvents | Where-Object {
        $_.event -eq 'message' -and $_.direction -eq 'client-to-server' -and
        $_.messageType -eq 'SublotSubmitted' -and $_.action -eq 'forwarded-after-reorder'
    })
    $businessAckDropObservation = [ordered]@{
        droppedAckCount = $businessDropped.Count
        droppedAckMessageId = if ($businessDropped.Count -eq 1) { $businessDropped[0].acceptedMessageId } else { $null }
        droppedAckWireSha256 = if ($businessDropped.Count -eq 1) { $businessDropped[0].wireSha256 } else { $null }
        replayedAckSha256 = $businessProbeResult.ackDropInSessionReplay.replayedAckSha256
        byteExactStoredAckReplay = $businessDropped.Count -eq 1 -and
            $businessDropped[0].wireSha256 -eq $businessProbeResult.ackDropInSessionReplay.replayedAckSha256
        delayedForwardCount = $businessDelayed.Count
        injectedDelayMilliseconds = if ($businessDelayed.Count -eq 1) { $businessDelayed[0].delayMilliseconds } else { $null }
        heldForReorderCount = $businessHeld.Count
        releasedAfterReorderCount = $businessReleased.Count
    }

    # The recovery plane reuses the business proxy: the server serves one peer at a time, the probes
    # run in sequence, and one transcript keeps the injected disconnect and its replay in the same
    # ordered record.
    $recoveryProbeJson = [StagedG3TlsHarness]::RunRecoveryProbeAsync(
        $businessProxyPort,
        $credential,
        $recoveryProof,
        $recoveryProbeTranscript,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    [IO.File]::WriteAllText(
        (Join-Path $EvidenceRoot 'recovery-probe-result.json'),
        $recoveryProbeJson,
        [Text.UTF8Encoding]::new($false))
    $recoveryProbeResult = $recoveryProbeJson | ConvertFrom-Json

    $recoveryEvents = Read-Ndjson $businessProxyTranscript
    $recoveryDropped = @($recoveryEvents | Where-Object {
        $_.event -eq 'message' -and $_.direction -eq 'server-to-client' -and
        $_.messageType -eq 'ForcedMechanicalRecoveryCommand' -and
        $_.action -eq 'dropped-and-connection-closed'
    })
    $recoveryForwarded = @($recoveryEvents | Where-Object {
        $_.event -eq 'message' -and $_.direction -eq 'server-to-client' -and
        $_.messageType -eq 'ForcedMechanicalRecoveryCommand' -and $_.action -eq 'forwarded'
    })
    $droppedCommandId = if ($recoveryDropped.Count -eq 1) { $recoveryDropped[0].messageId } else { $null }
    $replayedCommands = @($recoveryForwarded | Where-Object messageId -EQ $droppedCommandId)
    $recoveryFaultObservation = [ordered]@{
        droppedCommandCount = $recoveryDropped.Count
        droppedCommandMessageId = $droppedCommandId
        droppedCommandWireSha256 = if ($recoveryDropped.Count -eq 1) { $recoveryDropped[0].wireSha256 } else { $null }
        replayedCommandCount = $replayedCommands.Count
        replayedCommandWireSha256 = @($replayedCommands.wireSha256 | Sort-Object -Unique)
        replayedCommandSessionGenerations = @($replayedCommands.sessionGeneration | Sort-Object -Unique)
        droppedCommandSessionGeneration = if ($recoveryDropped.Count -eq 1) { $recoveryDropped[0].sessionGeneration } else { $null }
        forwardedCommandMessageIds = @($recoveryForwarded.messageId | Sort-Object -Unique)
    }
}
catch {
    $runError = $_
}
finally {
    foreach ($process in @($onboard, $control, $simulator)) {
        Stop-ProcessSafely -Process $process
    }
    if ($null -ne $proxyStopping) {
        $proxyStopping.Cancel()
        if ($null -ne $proxyTask) {
            try { $proxyTask.Wait(5000) | Out-Null } catch { }
        }
        $proxyStopping.Dispose()
    }
    if ($null -ne $businessProxyStopping) {
        $businessProxyStopping.Cancel()
        if ($null -ne $businessProxyTask) {
            try { $businessProxyTask.Wait(5000) | Out-Null } catch { }
        }
        $businessProxyStopping.Dispose()
    }
    # Last, after the peers are stopped. Releasing earlier would hand the desktop to another
    # repository while this run's WPF windows were still closing.
    Exit-DesktopLock -Handle $desktopLock
}

$controlLog = if (Test-Path -LiteralPath (Join-Path $logsRoot 'control.out.log')) {
    Get-Content -LiteralPath (Join-Path $logsRoot 'control.out.log') -Raw
} else { '' }
$controlErrorLog = if (Test-Path -LiteralPath (Join-Path $logsRoot 'control.err.log')) {
    Get-Content -LiteralPath (Join-Path $logsRoot 'control.err.log') -Raw
} else { '' }
$onboardLogFiles = @(Get-ChildItem -LiteralPath (Join-Path $runtimeRoot 'onboard-logs') -File -ErrorAction SilentlyContinue)
foreach ($file in $onboardLogFiles) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $logsRoot $file.Name)
}
$onboardLogs = ($onboardLogFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"

$databaseObservation = $null
$databasePath = Join-Path $runtimeRoot 'controlserver.db'
if (Test-Path -LiteralPath $databasePath) {
    Add-Type -Path (Join-Path $controlPublish 'Microsoft.Data.Sqlite.dll')
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$databasePath;Mode=ReadOnly")
    try {
        $connection.Open()
        function Invoke-Scalar([string]$Sql) {
            $command = $connection.CreateCommand()
            try { $command.CommandText = $Sql; return $command.ExecuteScalar() }
            finally { $command.Dispose() }
        }
        $recoveryRows = @()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT MessageId, ContentHash, COUNT(*) FROM ProtocolInbox WHERE MessageType = 'RecoveryStateReport' GROUP BY MessageId, ContentHash"
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $recoveryRows += [ordered]@{
                    messageId = $reader.GetString(0)
                    contentHash = $reader.GetString(1)
                    rowCount = $reader.GetInt64(2)
                }
            }
        }
        finally { $reader.Dispose(); $command.Dispose() }
        $businessRows = @()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT MessageType, MessageId, ContentHash, COUNT(*) FROM ProtocolInbox WHERE MessageType IN ('SublotSubmitted', 'OperationProgress', 'PreDepartureSafetyCheckResult', 'SlotOperationCommandRejected') GROUP BY MessageType, MessageId, ContentHash ORDER BY MessageType, MessageId"
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $businessRows += [ordered]@{
                    messageType = $reader.GetString(0)
                    messageId = $reader.GetString(1)
                    contentHash = $reader.GetString(2)
                    rowCount = $reader.GetInt64(3)
                }
            }
        }
        finally { $reader.Dispose(); $command.Dispose() }
        # FP-IS-15. The projection is one row per vehicle by design, so what this reads is the row that
        # survived, not a history: (sessionGeneration, snapshotSequence) says which snapshot won.
        # Reading AlarmsJson's length rather than its text keeps the evidence bounded while still
        # distinguishing "a snapshot arrived" from "an empty one did".
        $alarmSnapshotRows = @()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT AgvId, SessionGeneration, SnapshotSequence, LENGTH(AlarmsJson), AlarmsJson FROM OnboardAlarmSnapshots ORDER BY AgvId"
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $alarmsJson = $reader.GetString(4)
                $alarmCount = try { @([Text.Json.JsonDocument]::Parse($alarmsJson).RootElement.EnumerateArray()).Count } catch { -1 }
                $alarmSnapshotRows += [ordered]@{
                    agvId = $reader.GetString(0)
                    sessionGeneration = $reader.GetInt64(1)
                    snapshotSequence = $reader.GetInt64(2)
                    alarmsJsonLength = $reader.GetInt64(3)
                    alarmCount = $alarmCount
                }
            }
        }
        finally { $reader.Dispose(); $command.Dispose() }
        # FP-IS-14. Two tables, and the pair is what carries the claim: the activation row says what
        # was sent and how it settled, the active row exists ONLY where an activation converged --
        # so a matching fingerprint across the two is the vehicle having accepted the digest the
        # server computed.
        $activationRows = @()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT ActivationId, AgvId, State, Kind, RecoveryRole, ConfigurationVersion, Fingerprint, CommandMessageId FROM SlotConfigurationActivations ORDER BY IssuedAt"
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $activationRows += [ordered]@{
                    activationId = $reader.GetString(0)
                    agvId = $reader.GetString(1)
                    state = [string]$reader.GetValue(2)
                    kind = [string]$reader.GetValue(3)
                    recoveryRole = [string]$reader.GetValue(4)
                    configurationVersion = $reader.GetInt64(5)
                    fingerprint = $reader.GetString(6)
                    commandMessageId = if ($reader.IsDBNull(7)) { $null } else { $reader.GetString(7) }
                }
            }
        }
        finally { $reader.Dispose(); $command.Dispose() }
        $activeConfigurationRows = @()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT AgvId, ConfigurationVersion, Fingerprint, ActivationId FROM ActiveSlotConfigurations ORDER BY AgvId"
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $activeConfigurationRows += [ordered]@{
                    agvId = $reader.GetString(0)
                    configurationVersion = $reader.GetInt64(1)
                    fingerprint = $reader.GetString(2)
                    activationId = $reader.GetString(3)
                }
            }
        }
        finally { $reader.Dispose(); $command.Dispose() }
        $recoveryWorkflowRows = @()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT WorkflowId, WorkflowType, State, ForcedRecoveryGeneration, CommandMessageId, ResultMessageId, DemandId, SlotOperationAttemptId FROM RecoveryWorkflows ORDER BY CreatedAt"
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $recoveryWorkflowRows += [ordered]@{
                    workflowId = $reader.GetString(0)
                    workflowType = $reader.GetString(1)
                    state = $reader.GetString(2)
                    forcedRecoveryGeneration = $reader.GetInt64(3)
                    commandMessageId = if ($reader.IsDBNull(4)) { $null } else { $reader.GetString(4) }
                    resultMessageId = if ($reader.IsDBNull(5)) { $null } else { $reader.GetString(5) }
                    demandId = if ($reader.IsDBNull(6)) { $null } else { $reader.GetString(6) }
                    slotOperationAttemptId = if ($reader.IsDBNull(7)) { $null } else { $reader.GetString(7) }
                }
            }
        }
        finally { $reader.Dispose(); $command.Dispose() }
        $recoveryEvidenceRows = @()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT MessageId, WorkflowId, MessageType, ForcedRecoveryGeneration, Outcome, HistoricalOnly FROM RecoveryResultEvidence ORDER BY ReceivedAt"
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $recoveryEvidenceRows += [ordered]@{
                    messageId = $reader.GetString(0)
                    workflowId = $reader.GetString(1)
                    messageType = $reader.GetString(2)
                    forcedRecoveryGeneration = $reader.GetInt64(3)
                    outcome = $reader.GetString(4)
                    historicalOnly = [bool]$reader.GetInt64(5)
                }
            }
        }
        finally { $reader.Dispose(); $command.Dispose() }
        $recoverySessionRows = @()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT ExceptionRecoverySessionId, AgvId, State, Revision, SelectedAction, DemandId, ForcedRecoveryGeneration FROM ExceptionRecoverySessions ORDER BY OpenedAt"
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $recoverySessionRows += [ordered]@{
                    exceptionRecoverySessionId = $reader.GetString(0)
                    agvId = $reader.GetString(1)
                    state = $reader.GetString(2)
                    revision = $reader.GetInt64(3)
                    selectedAction = if ($reader.IsDBNull(4)) { $null } else { $reader.GetString(4) }
                    demandId = if ($reader.IsDBNull(5)) { $null } else { $reader.GetString(5) }
                    forcedRecoveryGeneration = $reader.GetInt64(6)
                }
            }
        }
        finally { $reader.Dispose(); $command.Dispose() }
        $recoveryCommandOutboxRows = @()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT MessageId, MessageType, COUNT(*) FROM ProtocolOutbox WHERE MessageType = 'ForcedMechanicalRecoveryCommand' GROUP BY MessageId, MessageType ORDER BY MessageId"
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $recoveryCommandOutboxRows += [ordered]@{
                    messageId = $reader.GetString(0)
                    messageType = $reader.GetString(1)
                    rowCount = $reader.GetInt64(2)
                }
            }
        }
        finally { $reader.Dispose(); $command.Dispose() }
        $databaseObservation = [ordered]@{
            recoveryStateReportInboxRows = $recoveryRows
            businessMessageInboxRows = $businessRows
            onboardAlarmSnapshotRows = $alarmSnapshotRows
            slotConfigurationActivationRows = $activationRows
            activeSlotConfigurationRows = $activeConfigurationRows
            currentSessionGeneration = [long](Invoke-Scalar "SELECT SessionGeneration FROM SessionRecoveries WHERE AgvId = '$agvId'")
            currentSessionReadiness = [string](Invoke-Scalar "SELECT Readiness FROM SessionRecoveries WHERE AgvId = '$agvId'")
            currentSessionReasonCode = [string](Invoke-Scalar "SELECT ReasonCode FROM SessionRecoveries WHERE AgvId = '$agvId'")
            orderIntentCount = [long](Invoke-Scalar 'SELECT COUNT(*) FROM OrderIntents')
            acceptedDemandCount = [long](Invoke-Scalar 'SELECT COUNT(*) FROM AcceptedDemands')
            stationOperationCount = [long](Invoke-Scalar 'SELECT COUNT(*) FROM StationOperations')
            recoveryWorkflowRows = $recoveryWorkflowRows
            recoveryResultEvidenceRows = $recoveryEvidenceRows
            exceptionRecoverySessionRows = $recoverySessionRows
            forcedMechanicalRecoveryCommandOutboxRows = $recoveryCommandOutboxRows
            hardwareRecoveryRecordCount = [long](Invoke-Scalar 'SELECT COUNT(*) FROM HardwareRecoveryRecords')
            vehicleForcedRecoveryGeneration = [long](Invoke-Scalar "SELECT COALESCE((SELECT ForcedRecoveryGeneration FROM VehicleRecoveryGenerations WHERE AgvId = '$recoveryAgvId'), -1)")
            closedExceptionRecoverySessionCount = [long](Invoke-Scalar "SELECT COUNT(*) FROM ExceptionRecoverySessions WHERE State = 'CLOSED'")
            reconciledRecoveryWorkflowCount = [long](Invoke-Scalar "SELECT COUNT(*) FROM RecoveryWorkflows WHERE State = 'Reconciled'")
        }
    }
    finally { $connection.Dispose() }
}

# FP-IS-15. The onboard alarm board publishes a snapshot as part of every handshake, so a run that
# reconnects produces one per connection without the runner having to drive anything. What is being
# read here is whether the projection tracked them: the server keeps one row per vehicle, and the
# question REQ-0269 asks is which snapshot that row ended up holding.
$alarmEvents = Read-Ndjson $proxyTranscript
$alarmSnapshotsSent = @($alarmEvents | Where-Object {
    $_.event -eq 'message' -and $_.direction -eq 'client-to-server' -and
    $_.messageType -eq 'OnboardAlarmSnapshot'
})
$alarmSnapshotAcks = @($alarmEvents | Where-Object {
    $_.event -eq 'message' -and $_.direction -eq 'server-to-client' -and
    $_.messageType -eq 'SnapshotAppliedAck' -and $_.snapshotKind -eq 'ONBOARD_ALARM'
})
$alarmProjectionRows = @()
if ($null -ne $databaseObservation) {
    $alarmProjectionRows = @($databaseObservation.onboardAlarmSnapshotRows |
        Where-Object { $_.agvId -ceq $agvId })
}
$alarmSnapshotGenerations = @($alarmSnapshotsSent.sessionGeneration | Sort-Object -Unique)
$alarmClientConnectionIds = @($alarmEvents |
    Where-Object { $_.event -eq 'message' -and $_.direction -eq 'client-to-server' } |
    ForEach-Object { $_.connectionId } | Sort-Object -Unique)
# A full handshake is identified by its CapabilitySnapshot, not by being the first connection. This
# run has three connections and TWO of them are full handshakes -- the reconnect after the activation
# command was dropped found a clean journal and handshook from the top.
$alarmFullHandshakeConnectionIds = @($alarmEvents | Where-Object {
    $_.event -eq 'message' -and $_.direction -eq 'client-to-server' -and
    $_.messageType -eq 'CapabilitySnapshot'
} | ForEach-Object { $_.connectionId } | Sort-Object -Unique)
$alarmObservation = [ordered]@{
    snapshotsSent = $alarmSnapshotsSent.Count
    clientConnectionIds = $alarmClientConnectionIds
    snapshotConnectionIds = @($alarmSnapshotsSent.connectionId | Sort-Object -Unique)
    snapshotSessionGenerations = $alarmSnapshotGenerations
    fullHandshakeConnectionIds = $alarmFullHandshakeConnectionIds
    snapshotMessageIds = @($alarmSnapshotsSent.messageId | Sort-Object -Unique)
    appliedAcks = $alarmSnapshotAcks.Count
    projectionRowsForThisVehicle = $alarmProjectionRows.Count
    projectionSessionGeneration = if ($alarmProjectionRows.Count -eq 1) { $alarmProjectionRows[0].sessionGeneration } else { $null }
    projectionSnapshotSequence = if ($alarmProjectionRows.Count -eq 1) { $alarmProjectionRows[0].snapshotSequence } else { $null }
    projectionAlarmCount = if ($alarmProjectionRows.Count -eq 1) { $alarmProjectionRows[0].alarmCount } else { $null }
}
[IO.File]::WriteAllText(
    (Join-Path $EvidenceRoot 'onboard-alarm-snapshot-observation.json'),
    ($alarmObservation | ConvertTo-Json -Depth 10),
    [Text.UTF8Encoding]::new($false))

# FP-IS-14. The activation is one command out and one result back on the same live session.
$activationCommandsSent = @($alarmEvents | Where-Object {
    $_.event -eq 'message' -and $_.direction -eq 'server-to-client' -and
    $_.messageType -eq 'SlotConfigurationActivationCommand'
})
$activationResultsReported = @($alarmEvents | Where-Object {
    $_.event -eq 'message' -and $_.direction -eq 'client-to-server' -and
    $_.messageType -eq 'SlotConfigurationActivationResult'
})
$activationDbRows = @()
$activeConfigurationDbRows = @()
if ($null -ne $databaseObservation) {
    $activationDbRows = @($databaseObservation.slotConfigurationActivationRows |
        Where-Object { $_.agvId -ceq $agvId })
    $activeConfigurationDbRows = @($databaseObservation.activeSlotConfigurationRows |
        Where-Object { $_.agvId -ceq $agvId })
}
$activationObservation = [ordered]@{
    issueHttpError = $activationIssueError
    issuedActivationId = if ($null -ne $activationResponse) { $activationResponse.activationId } else { $null }
    issuedState = if ($null -ne $activationResponse) { $activationResponse.state } else { $null }
    issuedRecoveryRole = if ($null -ne $activationResponse) { $activationResponse.recoveryRole } else { $null }
    slotModelVersionId = $slotModelVersionId
    commandsSent = $activationCommandsSent.Count
    commandConnectionIds = @($activationCommandsSent.connectionId | Sort-Object -Unique)
    commandPayloadSha256 = @($activationCommandsSent.payloadSha256 | Sort-Object -Unique)
    commandMessageIds = @($activationCommandsSent.messageId | Sort-Object -Unique)
    resultsReported = $activationResultsReported.Count
    activationRows = $activationDbRows
    activeConfigurationRows = $activeConfigurationDbRows
}
[IO.File]::WriteAllText(
    (Join-Path $EvidenceRoot 'slot-configuration-activation-observation.json'),
    ($activationObservation | ConvertTo-Json -Depth 10),
    [Text.UTF8Encoding]::new($false))

# One issue, one messageId -- however many times it went out. A second id would mean the replay
# minted a new command, and then the vehicle would be looking at two activations for one decision.
$activationCommandIds = @($activationCommandsSent.messageId | Sort-Object -Unique)
$activationCommandPass = $activationCommandIds.Count -eq 1
# The first delivery was dropped with its connection, so a second one has to exist, on a later
# connection, byte-for-byte identical. That is what PENDING_RESULT_REPLAY means: re-deliver the same
# line, not re-decide. REQ-0264 -- a vehicle that never got the command must leave the server
# believing nothing at all.
$activationPayloadHashes = @($activationCommandsSent.payloadSha256 | Sort-Object -Unique)
$activationCommandConnections = @($activationCommandsSent.connectionId | Sort-Object -Unique)
$activationReplayPass = $activationCommandsSent.Count -ge 2 -and
    $activationCommandPass -and
    $activationPayloadHashes.Count -eq 1 -and
    $activationCommandConnections.Count -ge 2
# durableBeforeSend, read off the two artefacts rather than trusted: the row that names this command
# was already in the database, and it names the very messageId that went out.
$activationDurablePass = $activationCommandPass -and
    $activationDbRows.Count -eq 1 -and
    $activationDbRows[0].commandMessageId -eq $activationCommandIds[0]
# The vehicle answered, once. REQ-0264: no result, no conclusion -- never an assumed success. And a
# re-delivered command must not produce a second answer to the same question.
$activationResultPass = $activationResultsReported.Count -ge 1 -and
    $activationDbRows.Count -eq 1 -and
    $activationDbRows[0].state -eq 'ACTIVATED'
# THE assertion this slice exists for. ActiveSlotConfigurations is written in exactly one place --
# where an activation converges on a reported success -- and the vehicle only reports success when
# the fingerprint IT computed over the configuration IT holds equals the one the command carried.
# So a row here, carrying the activation's own fingerprint, is two independent implementations in two
# separate processes having produced the same digest.
$fingerprintAgreementPass = $activationDurablePass -and
    $activationResultPass -and
    $activeConfigurationDbRows.Count -eq 1 -and
    $activeConfigurationDbRows[0].fingerprint -eq $activationDbRows[0].fingerprint -and
    $activeConfigurationDbRows[0].activationId -eq $activationDbRows[0].activationId

# A FULL handshake publishes one. Not every connection does -- see the resume assertion below, which
# is the corrected form of what the 2026-09-10 FAIL run got wrong.
$alarmSentPass = $alarmSnapshotsSent.Count -ge 1
# Acked one for one. A snapshot the server never applied cannot be evidence that it projected it.
$alarmAckPass = $alarmSnapshotAcks.Count -eq $alarmSnapshotsSent.Count -and $alarmSnapshotAcks.Count -ge 1
# A reconnect that resumes an interrupted recovery replays the unacknowledged message and republishes
# NOTHING: those snapshots were already accepted on the previous connection. A full handshake does
# publish one. So the exact claim is an IFF -- the set of connections carrying an alarm snapshot is
# the set carrying a CapabilitySnapshot, no more and no less.
#
# This replaced a coarser form ("all alarm snapshots on a single connection") that failed on
# 2026-09-10 the moment a run had two full handshakes; the evidence is kept at
# evidence/g3/20260910-fp-is-14-pending-result-replay. Counting connections was never the claim:
# republishing on a RESUME is the defect, because it would hand the projection the same board state
# under a new generation and the adoption rule would take it as news.
$alarmResumePass = $alarmClientConnectionIds.Count -gt $alarmFullHandshakeConnectionIds.Count -and
    $alarmFullHandshakeConnectionIds.Count -ge 1 -and
    $null -eq (Compare-Object `
        -ReferenceObject $alarmFullHandshakeConnectionIds `
        -DifferenceObject $alarmObservation.snapshotConnectionIds)
# Several snapshots arrived across several generations and the projection kept exactly the last one.
# This is the "a later snapshot replaces an earlier one wholesale" half of REQ-0269, which no run
# before 2026-09-10 reached -- only one snapshot had ever arrived.
$alarmSupersedePass = $alarmSnapshotsSent.Count -ge 2 -and
    $alarmSnapshotGenerations.Count -ge 2 -and
    $alarmProjectionRows.Count -eq 1 -and
    $alarmObservation.projectionSessionGeneration -eq ($alarmSnapshotGenerations | Measure-Object -Maximum).Maximum
# One row per vehicle is the design, not an accident of this run: a second row would mean the
# projection is accumulating history and the dashboard has no single current alarm set to read.
$alarmSingletonPass = $alarmProjectionRows.Count -eq 1
# The row carries the generation the snapshot arrived in. This is the half of the (generation,
# sequence) adoption rule a staged run can reach; the other half -- a restarted vehicle whose
# sequence returns to 1 still being adopted -- needs the onboard process to actually restart, which
# is run-staged-g3-restart.ps1's scenario, not this one.
$alarmGenerationPass = $alarmSingletonPass -and
    $alarmSnapshotGenerations.Count -ge 1 -and
    $alarmObservation.projectionSessionGeneration -eq ($alarmSnapshotGenerations | Measure-Object -Maximum).Maximum -and
    $alarmObservation.projectionSnapshotSequence -ge 1

$replayPass = $null -ne $runtimeObservation -and
    $runtimeObservation.droppedAckCount -eq 1 -and
    $runtimeObservation.forwardedReplayAckCount -ge 1 -and
    $runtimeObservation.recoveryReportSendCount -ge 2 -and
    $runtimeObservation.messageIds.Count -eq 1 -and
    $runtimeObservation.payloadSha256.Count -eq 1 -and
    $runtimeObservation.wireSha256.Count -ge 2 -and
    $runtimeObservation.connectionIds.Count -ge 2 -and
    $runtimeObservation.sessionGenerations.Count -ge 2 -and
    $null -ne $runtimeObservation.sessionAfterFault -and
    $runtimeObservation.sessionAfterFault.readiness -eq 'RecoveryRequired' -and
    $runtimeObservation.sessionAfterFault.reasonCode -in @(
        'HANDSHAKE_INCOMPLETE',
        'CAPABILITY_SNAPSHOT_REQUIRED',
        'SAFETY_SNAPSHOT_REQUIRED',
        'DEPARTURE_SAFETY_NOT_READY') -and
    -not $controlLog.Contains('Message does not belong to the current connection session.')

$noMovementPass = $null -ne $databaseObservation -and
    $databaseObservation.orderIntentCount -eq 0 -and
    $databaseObservation.acceptedDemandCount -eq 0 -and
    $databaseObservation.stationOperationCount -eq 0
$probePass = $null -ne $probeResult -and $probeResult.status -eq 'PASS'

$businessProbePass = $null -ne $businessProbeResult -and $businessProbeResult.status -eq 'PASS'
$businessDuplicatePass = $null -ne $businessProbeResult -and
    @($businessProbeResult.duplicates).Count -eq 4 -and
    @($businessProbeResult.duplicates | Where-Object status -NE 'PASS').Count -eq 0
$businessConflictPass = $null -ne $businessProbeResult -and
    @($businessProbeResult.conflicts).Count -eq 4 -and
    @($businessProbeResult.conflicts | Where-Object status -NE 'PASS').Count -eq 0
$businessAckDropPass = $null -ne $businessProbeResult -and
    $businessProbeResult.ackDropInSessionReplay.status -eq 'PASS' -and
    $null -ne $businessAckDropObservation -and
    $businessAckDropObservation.droppedAckCount -eq 1 -and
    $businessAckDropObservation.byteExactStoredAckReplay
$businessDelayPass = $null -ne $businessProbeResult -and
    $businessProbeResult.delayedDelivery.status -eq 'PASS' -and
    $null -ne $businessAckDropObservation -and
    $businessAckDropObservation.delayedForwardCount -eq 1
$businessReorderPass = $null -ne $businessProbeResult -and
    $businessProbeResult.reorderedDelivery.status -eq 'PASS' -and
    $null -ne $businessAckDropObservation -and
    $businessAckDropObservation.heldForReorderCount -eq 1 -and
    $businessAckDropObservation.releasedAfterReorderCount -eq 1

$businessPass = $businessProbePass -and $businessDuplicatePass -and $businessConflictPass -and
    $businessAckDropPass -and $businessDelayPass -and $businessReorderPass

$recoveryProbePass = $null -ne $recoveryProbeResult -and $recoveryProbeResult.status -eq 'PASS'
$recoveryAuthorisationPass = $null -ne $recoveryProbeResult -and
    @($recoveryProbeResult.sessionAuthorisation).Count -eq 6 -and
    @($recoveryProbeResult.sessionAuthorisation | Where-Object status -NE 'PASS').Count -eq 0
$recoveryActionBoundaryPass = $null -ne $recoveryProbeResult -and
    @($recoveryProbeResult.actionBoundary).Count -eq 4 -and
    @($recoveryProbeResult.actionBoundary | Where-Object status -NE 'PASS').Count -eq 0 -and
    @($recoveryProbeResult.demandScopedAuthorisationBoundary).Count -eq 3 -and
    @($recoveryProbeResult.demandScopedAuthorisationBoundary | Where-Object status -NE 'PASS').Count -eq 0
$recoveryHardwareRecordPass = $null -ne $recoveryProbeResult -and
    @($recoveryProbeResult.hardwareRecoveryRecord).Count -eq 2 -and
    @($recoveryProbeResult.hardwareRecoveryRecord | Where-Object status -NE 'PASS').Count -eq 0 -and
    $null -ne $databaseObservation -and $databaseObservation.hardwareRecoveryRecordCount -eq 1

# The command the proxy swallowed and the command the server re-sent after the reconnect have to be
# the same persisted outbox row, so the identity is taken from the transcript rather than the probe:
# one drop, one message id, and a second delivery of that same id on a later session generation.
$recoveryDisconnectPass = $null -ne $recoveryProbeResult -and
    $recoveryProbeResult.disconnectDuringRecoveryCommand.status -eq 'PASS' -and
    $null -ne $recoveryFaultObservation -and
    $recoveryFaultObservation.droppedCommandCount -eq 1 -and
    $recoveryFaultObservation.replayedCommandCount -ge 1 -and
    $recoveryFaultObservation.replayedCommandSessionGenerations.Count -ge 1 -and
    $recoveryFaultObservation.droppedCommandSessionGeneration -lt
        ($recoveryFaultObservation.replayedCommandSessionGenerations | Measure-Object -Maximum).Maximum -and
    $null -ne $databaseObservation -and
    @($databaseObservation.forcedMechanicalRecoveryCommandOutboxRows).Count -eq 2 -and
    @($databaseObservation.forcedMechanicalRecoveryCommandOutboxRows | Where-Object { $_.rowCount -ne 1 }).Count -eq 0

$firstRecoveryActionId = if ($null -ne $recoveryProbeResult) {
    $recoveryProbeResult.forcedRecoveryGenerationBranches.firstRecoveryActionId
} else { $null }
$secondRecoveryActionId = if ($null -ne $recoveryProbeResult) {
    $recoveryProbeResult.forcedRecoveryGenerationBranches.secondRecoveryActionId
} else { $null }
# These rows are ordered dictionaries rather than the parsed JSON objects used elsewhere, so they are
# filtered with a script block: the -Property form of Where-Object is not reliable on a dictionary.
$staleWorkflow = @($databaseObservation.recoveryWorkflowRows | Where-Object { $_.workflowId -eq $firstRecoveryActionId })
$currentWorkflow = @($databaseObservation.recoveryWorkflowRows | Where-Object { $_.workflowId -eq $secondRecoveryActionId })
$staleEvidence = @($databaseObservation.recoveryResultEvidenceRows | Where-Object { $_.workflowId -eq $firstRecoveryActionId })
$currentEvidence = @($databaseObservation.recoveryResultEvidenceRows | Where-Object { $_.workflowId -eq $secondRecoveryActionId })

# Monotonic advance, and a result that names the superseded generation may only become historical
# evidence: it must not reconcile the workflow, close the session, or move the vehicle generation.
$recoveryGenerationPass = $null -ne $recoveryProbeResult -and
    $recoveryProbeResult.forcedRecoveryGenerationBranches.status -eq 'PASS' -and
    $null -ne $databaseObservation -and
    $databaseObservation.vehicleForcedRecoveryGeneration -eq 2 -and
    $staleWorkflow.Count -eq 1 -and $staleWorkflow[0].state -eq 'HistoricalOnly' -and
    $staleWorkflow[0].forcedRecoveryGeneration -eq 1 -and
    $currentWorkflow.Count -eq 1 -and $currentWorkflow[0].state -eq 'RecoveryRequired' -and
    $currentWorkflow[0].forcedRecoveryGeneration -eq 2 -and
    $staleEvidence.Count -eq 1 -and $staleEvidence[0].historicalOnly -and
    $staleEvidence[0].forcedRecoveryGeneration -eq 1 -and
    $currentEvidence.Count -eq 1 -and -not $currentEvidence[0].historicalOnly -and
    $currentEvidence[0].forcedRecoveryGeneration -eq 2

# A forced mechanical recovery is an isolation, not a completion: nothing in this plane may close the
# session, reconcile a workflow, create an order or a demand, or touch a station operation.
$recoveryNoFalseClosurePass = $null -ne $databaseObservation -and
    @($databaseObservation.exceptionRecoverySessionRows).Count -eq 1 -and
    $databaseObservation.exceptionRecoverySessionRows[0].agvId -eq $recoveryAgvId -and
    $databaseObservation.exceptionRecoverySessionRows[0].state -eq 'EXECUTING' -and
    $null -eq $databaseObservation.exceptionRecoverySessionRows[0].demandId -and
    $databaseObservation.closedExceptionRecoverySessionCount -eq 0 -and
    $databaseObservation.reconciledRecoveryWorkflowCount -eq 0 -and
    @($databaseObservation.recoveryWorkflowRows).Count -eq 2 -and
    @($databaseObservation.recoveryWorkflowRows | Where-Object { $null -ne $_.demandId }).Count -eq 0 -and
    @($databaseObservation.recoveryWorkflowRows | Where-Object { $null -ne $_.slotOperationAttemptId }).Count -eq 0

$recoveryPass = $recoveryProbePass -and $recoveryAuthorisationPass -and $recoveryActionBoundaryPass -and
    $recoveryHardwareRecordPass -and $recoveryDisconnectPass -and $recoveryGenerationPass -and
    $recoveryNoFalseClosurePass

$status = if ($null -ne $runError) {
    'INCONCLUSIVE_RUNNER_ERROR'
} elseif ($probePass -and $replayPass -and $noMovementPass -and $businessPass -and $recoveryPass) {
    'STAGED_G3_RECOVERY_REPLAY_PASS'
} else {
    'STAGED_SLICE_FAIL'
}

$configuration = [ordered]@{
    loopbackOnly = $true
    transport = [ordered]@{
        onboardTransport = 'plaintext'
        certificatesGenerated = $false
        temporaryTrustRootInstalled = $false
        realOnboardAckDropTransport = 'PLAINTEXT_LOOPBACK'
        realOnboardAckDropCombination = if ($replayPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    }
    ports = [ordered]@{
        controlOnboard = $controlPort
        controlHealth = $healthPort
        faultProxy = $proxyPort
        businessFaultProxy = $businessProxyPort
        simulatorModbus = $modbusPort
        simulatorHttp = $simulatorHttpPort
    }
    faultInjection = [ordered]@{
        parameterisedByMessageType = $true
        actions = @('drop-and-close', 'drop', 'delay', 'hold-until-next')
        businessMessagePlane = @(
            'SublotSubmitted',
            'OperationProgress',
            'PreDepartureSafetyCheckResult',
            'SlotOperationCommandRejected')
        businessMessagesDrivenBySyntheticPeer = $true
        businessMessagesNotReachableInStagedRun = @('OperationResult', 'SlotOperationCommand')
        recoveryMessagePlane = @(
            'ExceptionRecoverySessionRequested',
            'RecoveryActionSubmitted',
            'HardwareRecoveryRecordSubmitted',
            'LoadCancellationStartRequested',
            'LoadCompensationRequested',
            'LoadCorrectionRequested',
            'ForcedMechanicalRecoveryResult',
            'ForcedMechanicalRecoveryCommand')
        recoveryMessagesDrivenBySyntheticPeer = $true
        recoveryAcceptedPathsNotReachableInStagedRun = @(
            'RESUME_AFTER_REPAIR',
            'COMPENSATE_LOAD_ALL_EMPTY',
            'FAULT_CARGO_HANDOFF',
            'LOAD_CANCELLATION',
            'LOAD_CORRECTION',
            'RiotUnknownReconciliation')
        recoveryAdministratorProofFromEnvironment = $true
    }
    journeyRuntimeEnabled = $false
    realExternalCredentialsUsed = $false
    realRiotOrderCreated = $false
    movementCommandSent = $false
    vehicleSafetyEligibilityFabricated = $false
}
$configurationJson = $configuration | ConvertTo-Json -Depth 20
[IO.File]::WriteAllText(
    (Join-Path $EvidenceRoot 'configuration.json'),
    $configurationJson,
    [Text.UTF8Encoding]::new($false))

$secretLeakFiles = [System.Collections.Generic.List[string]]::new()
foreach ($file in @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File)) {
    try {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        if ($text.Contains($credential, [StringComparison]::Ordinal) -or
            $text.Contains($recoveryProof, [StringComparison]::Ordinal) -or
            $text.Contains($governanceCredential, [StringComparison]::Ordinal)) {
            $secretLeakFiles.Add([IO.Path]::GetRelativePath($EvidenceRoot, $file.FullName).Replace('\', '/'))
        }
    }
    catch {
        # Evidence is text-only in this staged runner; unreadable files are handled by the artifact hash list.
    }
}

$assertionReport = [ordered]@{
    identityRejections = if ($probePass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    sameConnectionSameMessageIdSameContent = if ($probePass -and $probeResult.duplicate.status -eq 'PASS') { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    sameMessageIdDifferentContentStableConflict = if ($probePass -and $probeResult.conflict.status -eq 'PASS') { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    recoveryStateReportFirstAckDropReplay = if ($replayPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    recoveryStateReportFirstAckDropReplayOverPlaintext = if ($replayPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    businessMessageSameMessageIdSameContentReplay = if ($businessDuplicatePass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    businessMessageSameMessageIdDifferentContentStableConflict = if ($businessConflictPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    businessMessageAckDropInSessionReplay = if ($businessAckDropPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    businessMessageDelayedDeliveryAccepted = if ($businessDelayPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    businessMessageReorderedDeliveryAccepted = if ($businessReorderPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    recoverySessionAuthorisationBoundary = if ($recoveryAuthorisationPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    recoveryActionsRefusedWithoutPersistedOperation = if ($recoveryActionBoundaryPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    hardwareRecoveryRecordScopeEnforced = if ($recoveryHardwareRecordPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    recoveryCommandSurvivesMidFlightDisconnect = if ($recoveryDisconnectPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    forcedRecoveryGenerationAdvancesMonotonically = if ($recoveryGenerationPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    supersededGenerationResultIsHistoricalEvidenceOnly = if ($recoveryGenerationPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    recoveryNeverReportsFalseCompletion = if ($recoveryNoFalseClosurePass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    slotConfigurationActivationCarriesOneMessageIdOnly = if ($activationCommandPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    slotConfigurationActivationReplayedByteForByteAfterAMidFlightDrop = if ($activationReplayPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    slotConfigurationActivationPersistedBeforeItWasSent = if ($activationDurablePass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    slotConfigurationActivationResultReportedByTheVehicle = if ($activationResultPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    bothEndsComputedTheSameSlotConfigurationFingerprint = if ($fingerprintAgreementPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    onboardAlarmSnapshotPublishedOnTheFullHandshake = if ($alarmSentPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    onboardAlarmSnapshotAppliedAckOnEverySnapshot = if ($alarmAckPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    onboardAlarmSnapshotNotRepublishedOnRecoveryResume = if ($alarmResumePass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    onboardAlarmProjectionKeptOnlyTheLatestOfSeveralSnapshots = if ($alarmSupersedePass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    onboardAlarmProjectionIsASingletonPerVehicle = if ($alarmSingletonPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    onboardAlarmProjectionCarriesTheGenerationItArrivedIn = if ($alarmGenerationPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    noMovementOrExternalSideEffects = if ($noMovementPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    secretScan = if ($secretLeakFiles.Count -eq 0) { 'PASS' } else { 'FAIL' }
}

# The vendored slice index is what the gate result cites, and this runner is the only one of the
# three that also clones the protocol. Assert the two are the same bytes rather than trusting the
# manifest file table alone: a gate result that names a slice family should be able to say it read
# the protocol's own copy of it.
$sliceIndexPath = Join-Path $ControlServerRepository 'vendor\8005-agv-protocol\integration-slices\index.json'
$clonedSliceIndex = Join-Path $protocolSource 'integration-slices\index.json'
if (Test-Path -LiteralPath $clonedSliceIndex -PathType Leaf) {
    $vendoredHash = (Get-FileHash -LiteralPath $sliceIndexPath -Algorithm SHA256).Hash
    $clonedHash = (Get-FileHash -LiteralPath $clonedSliceIndex -Algorithm SHA256).Hash
    if ($vendoredHash -ne $clonedHash) {
        throw ("The vendored slice index differs from the protocol clone at ${ProtocolCommit}: " +
               "vendored $vendoredHash, cloned $clonedHash.")
    }
}

$gateResultPaths = Write-G3GateResults -RunKind $G3RunKind -EvidenceRoot $EvidenceRoot `
    -AssertionReport $assertionReport -Slice $Slice -RunnerErrored:($null -ne $runError) -Context @{
        runId = $runId
        startedAt = $runStartedAt.ToString('O')
        commits = [ordered]@{
            controlServer = $ControlServerCommit
            onboardEvidenceBinding = $OnboardCommit
            slotsSimulator = $SimulatorCommit
            protocol = $ProtocolCommit
            harness = $harnessCommit
            harnessWorktreeCleanAtStart = $harnessWorktreeClean
        }
        protocolReleaseVersion = $expectedProtocol.releaseVersion
        protocolTag = $protocolTag
        protocolProfileId = $expectedProtocol.profileId
        protocolVersion = $expectedProtocol.protocolVersion
        protocolApprovalStatus = $expectedProtocol.approvalStatus
        protocolRepositoryCommit = $ProtocolCommit
        protocolManifestSha256 = $manifestSha256
        protocolSchemaBundleSha256 = $schemaBundleSha256
        protocolVectorsSha256 = $vectorsSha256
        sliceIndexPath = $sliceIndexPath
        sliceIndexSource = 'vendor/8005-agv-protocol/integration-slices/index.json'
    }

# The secret scan above ran before these files existed. They carry only derived identity, status and
# assertion names, so this throws rather than recording a leak: a value that reached them is already
# sealed into evidence, and the run must not finish claiming it scanned clean.
foreach ($gateResultPath in $gateResultPaths) {
    $gateResultText = Get-Content -Raw -LiteralPath $gateResultPath
    if ($gateResultText.Contains($credential, [StringComparison]::Ordinal) -or
        $gateResultText.Contains($recoveryProof, [StringComparison]::Ordinal) -or
        $gateResultText.Contains($governanceCredential, [StringComparison]::Ordinal)) {
        throw "A gate result carries a run secret: $gateResultPath"
    }
}

$artifactFiles = @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File |
    Where-Object Name -NE 'run-result.json' |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($EvidenceRoot, $_.FullName).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            length = $_.Length
        }
    })

$result = [ordered]@{
    schemaVersion = '1.0.0'
    runKind = 'STAGED_G3_REAL_PEERS_DETERMINISTIC_PLAINTEXT'
    runId = $runId
    startedAtUtc = $runStartedAt
    completedAtUtc = [DateTimeOffset]::UtcNow
    status = $status
    classification = (New-G3Classification -RunKind $G3RunKind -RunStatus $status `
        -AssertionReport $assertionReport -RunnerErrored:($null -ne $runError))
    gateResults = @($gateResultPaths | ForEach-Object {
        [IO.Path]::GetRelativePath($EvidenceRoot, $_).Replace('\', '/') })
    commits = [ordered]@{
        controlServer = $ControlServerCommit
        onboardEvidenceBinding = $OnboardCommit
        slotsSimulator = $SimulatorCommit
        protocol = $ProtocolCommit
        harness = $harnessCommit
        harnessWorktreeCleanAtStart = $harnessWorktreeClean
    }
    protocol = [ordered]@{
        tag = $protocolTag
        # False on the v2 line: the tag is named by the candidate identity but has not been cut.
        # Recorded rather than assumed, so a reader can tell "no tag yet" from "tag verified".
        tagExists = $protocolTagExists
        candidateCommit = $ProtocolCommit
        approvalStatus = $expectedProtocol.approvalStatus
        commit = $ProtocolCommit
        manifestSha256 = $manifestSha256
        schemaBundleSha256 = $schemaBundleSha256
        vectorsSha256 = $vectorsSha256
        g1 = $protocolG1Status
    }
    configurationSha256 = Get-Sha256Text $configurationJson
    configuration = $configuration
    commands = @($commands)
    assertions = $assertionReport
    probe = $probeResult
    businessProbe = $businessProbeResult
    recoveryProbe = $recoveryProbeResult
    businessFaultInjection = $businessAckDropObservation
    recoveryFaultInjection = $recoveryFaultObservation
    recoveryReplay = $runtimeObservation
    database = $databaseObservation
    simulatorHealth = $simulatorHealth
    controlServerVersion = $version
    error = if ($null -ne $runError) { [ordered]@{ type = $runError.Exception.GetType().FullName; message = $runError.Exception.Message } } else { $null }
    secretLeakFiles = @($secretLeakFiles)
    evidenceFiles = $artifactFiles
}

$resultJson = $result | ConvertTo-Json -Depth 40
$resultPath = Join-Path $EvidenceRoot 'run-result.json'
[IO.File]::WriteAllText($resultPath, $resultJson, [Text.UTF8Encoding]::new($false))
$resultJson

if ($status -notin @('STAGED_SLICE_PASS', 'STAGED_G3_RECOVERY_REPLAY_PASS') -or
    $secretLeakFiles.Count -ne 0) {
    throw "Staged G3 did not pass: $status. Evidence: $EvidenceRoot"
}
