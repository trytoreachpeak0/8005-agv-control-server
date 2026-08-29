[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$StageRoot,
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,
    [string]$ControlServerRepository = (Split-Path -Parent $PSScriptRoot),
    [string]$OnboardRepository = 'https://github.com/trytoreachpeak0/8005-agv-onboard-hmi.git',
    [string]$SimulatorRepository = 'https://github.com/trytoreachpeak0/slots-simulator.git',
    [string]$ProtocolRepository = 'https://github.com/trytoreachpeak0/8005-agv-protocol.git',
    [string]$ControlServerCommit = '3d8b00c7558ae700358f1f995a5ac75d12a3250c',
    [string]$OnboardCommit = '304e6ad9952a41d5c0d50c0c4e79bab5c8804bd6',
    [string]$SimulatorCommit = 'fb5f7c593742bf98bc3957b8729a38aad5321f28',
    [string]$ProtocolCommit = '1531489e42e328f28bfe0c51ed3f8c56e5ce0279',
    [switch]$InstallTemporaryCurrentUserRoot
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $InstallTemporaryCurrentUserRoot) {
    throw 'Real Onboard TLS validation requires explicit -InstallTemporaryCurrentUserRoot authorization. The runner installs one unique test root into CurrentUser/Root, records its fingerprint, and removes it in finally.'
}

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

$protocolTag = 'protocol-v0.1.1'
$manifestSha256 = 'a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f'
$schemaBundleSha256 = 'e04296e9bcf48c341bc91fef5731f6f465a5ecdbb9adedc17f3bac58e193d30c'
$vectorsSha256 = 'fc5902b71d1b276c674f8a21c738d27193ddcbaf9b352951deffbaf1488d356e'
$controlPort = 58205
$healthPort = 58207
$proxyPort = 58215
$businessProxyPort = 58216
$modbusPort = 1502
$simulatorHttpPort = 58006
$agvId = 'AGV-8005-STAGED-G3-TLS-01'
$runStartedAt = [DateTimeOffset]::UtcNow
$runId = $runStartedAt.ToString('yyyyMMddTHHmmssfffZ')

foreach ($value in @($ControlServerCommit, $OnboardCommit, $SimulatorCommit, $ProtocolCommit)) {
    if ($value -notmatch '^[0-9a-f]{40}$') {
        throw "Commit identity must be a lowercase full SHA-1: $value"
    }
}

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

function New-TlsMaterial {
    param([string]$Directory)

    $notBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)
    $notAfter = [DateTimeOffset]::UtcNow.AddHours(8)
    $rootKey = [Security.Cryptography.RSA]::Create(2048)
    $rootRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        "CN=8005 staged G3 loopback root $([Guid]::NewGuid().ToString('N'))",
        $rootKey,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $null = $rootRequest.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
    $null = $rootRequest.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign,
            $true))
    $null = $rootRequest.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new(
            $rootRequest.PublicKey,
            $false))
    $rootCertificate = $rootRequest.CreateSelfSigned($notBefore, $notAfter)

    $serverKey = [Security.Cryptography.RSA]::Create(2048)
    $serverRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=localhost',
        $serverKey,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $null = $serverRequest.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $null = $serverRequest.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment,
            $true))
    $oids = [Security.Cryptography.OidCollection]::new()
    $null = $oids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
    $null = $serverRequest.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $true))
    $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddDnsName('localhost')
    $san.AddIpAddress([Net.IPAddress]::Loopback)
    $null = $serverRequest.CertificateExtensions.Add($san.Build())
    $serialNumber = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $issuedServerCertificate = $serverRequest.Create(
        $rootCertificate,
        $notBefore,
        $notAfter,
        $serialNumber)
    $serverCertificate = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey(
        $issuedServerCertificate,
        $serverKey)
    $issuedServerCertificate.Dispose()

    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLowerInvariant()
    $collection = [Security.Cryptography.X509Certificates.X509Certificate2Collection]::new()
    $null = $collection.Add($serverCertificate)
    $pfxPath = Join-Path $Directory 'loopback-server.pfx'
    [IO.File]::WriteAllBytes(
        $pfxPath,
        $collection.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $password))
    $fingerprint = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($serverCertificate.RawData)).ToLowerInvariant()
    $rootFingerprint = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($rootCertificate.RawData)).ToLowerInvariant()
    $rootThumbprint = $rootCertificate.Thumbprint

    $rootStore = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::Root,
        [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $rootStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        # A run that is killed rather than allowed to fail never reaches Remove-TlsMaterial, so its
        # root stays behind; six such roots had accumulated by 2026-08-29. Sweep expired ones from
        # earlier runs before adding this one. Expiry is the safe predicate: these roots live eight
        # hours, so an expired one cannot belong to a run still in progress.
        foreach ($stale in @($rootStore.Certificates)) {
            if ($stale.Subject.StartsWith('CN=8005 staged G3 loopback root ', [StringComparison]::Ordinal) -and
                $stale.NotAfter -lt [DateTime]::Now) {
                $rootStore.Remove($stale)
            }
        }
        $rootStore.Add($rootCertificate)
    }
    finally {
        $rootStore.Close()
        $rootStore.Dispose()
    }

    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.TrustMode = [Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
        $null = $chain.ChainPolicy.CustomTrustStore.Add($rootCertificate)
        $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        if (-not $chain.Build($serverCertificate)) {
            $statuses = @($chain.ChainStatus | ForEach-Object Status) -join ', '
            throw "The temporary loopback server certificate did not build to the generated test root: $statuses"
        }
    }
    catch {
        $cleanupStore = [Security.Cryptography.X509Certificates.X509Store]::new(
            [Security.Cryptography.X509Certificates.StoreName]::Root,
            [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
        try {
            $cleanupStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            foreach ($certificate in @($cleanupStore.Certificates.Find(
                [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                $rootThumbprint,
                $false))) {
                $cleanupStore.Remove($certificate)
            }
        }
        finally {
            $cleanupStore.Close()
            $cleanupStore.Dispose()
        }
        throw
    }
    finally {
        $chain.Dispose()
    }

    return [pscustomobject]@{
        PfxPath = $pfxPath
        Password = $password
        Fingerprint = $fingerprint
        RootFingerprint = $rootFingerprint
        RootThumbprint = $rootThumbprint
        TrustScope = 'CurrentUser/Root'
        TrustInstalled = $true
        TrustCleanupVerified = $false
        ServerCertificate = $serverCertificate
        ServerKey = $serverKey
        RootCertificate = $rootCertificate
        RootKey = $rootKey
    }
}

function Remove-TlsMaterial {
    param($Material)
    if ($null -eq $Material) { return }
    try {
        if ($Material.TrustInstalled) {
            $rootStore = [Security.Cryptography.X509Certificates.X509Store]::new(
                [Security.Cryptography.X509Certificates.StoreName]::Root,
                [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
            try {
                $rootStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                foreach ($certificate in @($rootStore.Certificates.Find(
                    [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                    $Material.RootThumbprint,
                    $false))) {
                    $rootStore.Remove($certificate)
                }
            }
            finally {
                $rootStore.Close()
                $rootStore.Dispose()
            }
        }
        if (Test-Path -LiteralPath $Material.PfxPath) {
            [IO.File]::Delete($Material.PfxPath)
        }
        $verificationStore = [Security.Cryptography.X509Certificates.X509Store]::new(
            [Security.Cryptography.X509Certificates.StoreName]::Root,
            [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
        try {
            $verificationStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
            $remaining = $verificationStore.Certificates.Find(
                [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                $Material.RootThumbprint,
                $false)
            $Material.TrustCleanupVerified = $remaining.Count -eq 0
        }
        finally {
            $verificationStore.Close()
            $verificationStore.Dispose()
        }
    }
    finally {
        $Material.ServerCertificate.Dispose()
        $Material.ServerKey.Dispose()
        $Material.RootCertificate.Dispose()
        $Material.RootKey.Dispose()
    }
}

$harnessSource = @'
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
        string expectedFingerprint,
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
                port, expectedFingerprint, cancellationToken).ConfigureAwait(false);
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
            bool passed = responseType == "SessionRejected" && reasonCode == "PROTOCOL_RELEASE_MISMATCH";
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
            port, expectedFingerprint, cancellationToken).ConfigureAwait(false))
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
            port, expectedFingerprint, cancellationToken).ConfigureAwait(false))
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
        string expectedFingerprint,
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
                port, expectedFingerprint, cancellationToken).ConfigureAwait(false))
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
                port, expectedFingerprint, cancellationToken).ConfigureAwait(false))
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
            port, expectedFingerprint, cancellationToken).ConfigureAwait(false))
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
            port, expectedFingerprint, cancellationToken).ConfigureAwait(false))
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
            port, expectedFingerprint, cancellationToken).ConfigureAwait(false))
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
        string pfxPath,
        string pfxPassword,
        string expectedFingerprint,
        string transcriptPath,
        CancellationToken cancellationToken)
    {
        File.WriteAllText(transcriptPath, string.Empty, new UTF8Encoding(false));
        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(
            pfxPath,
            pfxPassword,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
        TcpListener listener = new(IPAddress.Loopback, listenPort);
        listener.Start();
        using CancellationTokenRegistration registration = cancellationToken.Register(listener.Stop);
        Log(transcriptPath, new Dictionary<string, object?>
        {
            ["event"] = "proxy-listening",
            ["listenPort"] = listenPort,
            ["upstreamPort"] = upstreamPort,
            ["tls"] = true
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
                    downstream,
                    upstreamPort,
                    certificate,
                    expectedFingerprint,
                    transcriptPath,
                    connectionId,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task HandleProxyConnectionAsync(
        TcpClient downstream,
        int upstreamPort,
        X509Certificate2 certificate,
        string expectedFingerprint,
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
                using SslStream downstreamTls = new(downstream.GetStream(), false);
                await downstreamTls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, cancellationToken).ConfigureAwait(false);
                await upstream.ConnectAsync(IPAddress.Loopback, upstreamPort, cancellationToken).ConfigureAwait(false);
                using SslStream upstreamTls = new(
                    upstream.GetStream(),
                    false,
                    (_, remote, _, _) => Fingerprint(remote) == expectedFingerprint);
                await upstreamTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, cancellationToken).ConfigureAwait(false);
                Log(transcriptPath, new Dictionary<string, object?>
                {
                    ["event"] = "connection-opened",
                    ["connectionId"] = connectionId,
                    ["tls"] = true
                });
                Task clientToServer = PumpAsync(
                    downstreamTls, upstreamTls, "client-to-server", transcriptPath,
                    connectionId, connectionStopping.Token);
                Task serverToClient = PumpAsync(
                    upstreamTls, downstreamTls, "server-to-client", transcriptPath,
                    connectionId, connectionStopping.Token);
                await Task.WhenAny(clientToServer, serverToClient).ConfigureAwait(false);
                connectionStopping.Cancel();
                downstream.Close();
                upstream.Close();
                try { await Task.WhenAll(clientToServer, serverToClient).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            catch (Exception error) when (
                error is IOException or OperationCanceledException or SocketException or AuthenticationException)
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

    public static async Task RunPlainProxyAsync(
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
            ["tls"] = false
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
                await HandlePlainProxyConnectionAsync(
                    downstream, upstreamPort, transcriptPath, connectionId, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task HandlePlainProxyConnectionAsync(
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
                    ["tls"] = false
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
            ["tag"] = "protocol-v0.1.1",
            ["commit"] = Protocol.Commit,
            ["protocolVersion"] = 1,
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
            ["protocolVersion"] = 1,
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

    private static string Fingerprint(X509Certificate? certificate) => certificate is null
        ? string.Empty
        : Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();

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
        public const string Release = "0.1.1";
        public const string Profile = "WIRE_TO_GATE_MVP";
        public const string Commit = "1531489e42e328f28bfe0c51ed3f8c56e5ce0279";
        public const string Manifest = "a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f";
        public const string Schema = "e04296e9bcf48c341bc91fef5731f6f465a5ecdbb9adedc17f3bac58e193d30c";
        public const string Vectors = "fc5902b71d1b276c674f8a21c738d27193ddcbaf9b352951deffbaf1488d356e";
    }

    private sealed class Connection : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly SslStream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private Connection(TcpClient client, SslStream stream)
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

        public static async Task<Connection> OpenAsync(
            int port, string fingerprint, CancellationToken cancellationToken)
        {
            TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
            SslStream stream = new(
                client.GetStream(),
                false,
                (_, remote, _, _) => Fingerprint(remote) == fingerprint);
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cancellationToken).ConfigureAwait(false);
            return new Connection(client, stream);
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
            throw new EndOfStreamException("Expected a TLS NDJSON response.");

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

Add-Type -TypeDefinition $harnessSource -Language CSharp

$credential = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
$tlsMaterial = $null
$control = $null
$onboard = $null
$simulator = $null
$proxyStopping = $null
$proxyTask = $null
$businessProxyStopping = $null
$businessProxyTask = $null
$probeResult = $null
$businessProbeResult = $null
$businessAckDropObservation = $null
$runtimeObservation = $null
$runError = $null
$protocolG1Status = 'NOT_RUN'
$version = $null
$simulatorHealth = $null
$proxyTranscript = Join-Path $EvidenceRoot 'fault-proxy-events.ndjson'
$probeTranscript = Join-Path $EvidenceRoot 'probe-events.ndjson'
$businessProxyTranscript = Join-Path $EvidenceRoot 'business-fault-proxy-events.ndjson'
$businessProbeTranscript = Join-Path $EvidenceRoot 'business-probe-events.ndjson'

try {
    New-ExactClone -Name 'control-server' -Repository $ControlServerRepository -Destination $controlSource `
        -Commit $ControlServerCommit
    New-ExactClone -Name 'onboard-hmi' -Repository $OnboardRepository -Destination $onboardSource `
        -Commit $OnboardCommit -RemoteRef 'origin/OnboardHmi_MVP'
    New-ExactClone -Name 'slots-simulator' -Repository $SimulatorRepository -Destination $simulatorSource `
        -Commit $SimulatorCommit -RemoteRef 'origin/main'
    New-ExactClone -Name 'protocol' -Repository $ProtocolRepository -Destination $protocolSource `
        -Commit $ProtocolCommit

    $tagCommit = (& git -C $protocolSource rev-parse "refs/tags/$protocolTag^{}").Trim()
    if ($tagCommit -ne $ProtocolCommit) {
        throw "$protocolTag resolves to $tagCommit, expected $ProtocolCommit"
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

    $tlsMaterial = New-TlsMaterial -Directory $runtimeRoot
    $onboardConfig = Join-Path $onboardPublish 'appsettings.json'
    $settings = Get-Content -LiteralPath $onboardConfig -Raw | ConvertFrom-Json
    $settings.environment = 'Development'
    $settings.agvId = $agvId
    $settings.onboardInstanceId = 'OBU-8005-STAGED-G3-TLS-01'
    $settings.wireToGate.enabled = $true
    $settings.wireToGate.host = '127.0.0.1'
    $settings.wireToGate.port = $proxyPort
    $settings.wireToGate.onboardInstanceId = '9bd45b8f-b7cb-45d1-bdab-4f6a22347e2e'
    $settings.wireToGate.onboardBuildCommit = $OnboardCommit
    $settings.wireToGate.credentialEnvironmentVariable = 'CONTROL_SERVER_ONBOARD_CREDENTIAL'
    $settings.wireToGate.useTls = $true
    $settings.wireToGate | Add-Member -NotePropertyName serverCertificateSha256 `
        -NotePropertyValue $tlsMaterial.Fingerprint -Force
    $settings.wireToGate.connectTimeoutMs = 3000
    $settings.wireToGate.messageTimeoutMs = 3000
    $settings.wireToGate.journalPath = Join-Path $runtimeRoot 'onboard-journal.db'
    $settings.logging.directory = Join-Path $runtimeRoot 'onboard-logs'
    $settings.logging.writeToConsole = $true
    $settings | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $onboardConfig -Encoding utf8NoBOM

    $controlEnvironment = @{
        'CONTROL_SERVER_ONBOARD_CREDENTIAL' = $credential
        'CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD' = $tlsMaterial.Password
        'ConnectionStrings__ControlServer' = 'Data Source=' + (Join-Path $runtimeRoot 'controlserver.db')
        'Health__url' = "http://127.0.0.1:$healthPort"
        'OnboardTransport__listenAddress' = '127.0.0.1'
        'OnboardTransport__port' = [string]$controlPort
        'OnboardTransport__serverCertificatePath' = $tlsMaterial.PfxPath
        'OnboardTransport__serverCertificatePasswordEnvironmentVariable' = 'CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD'
        'OnboardTransport__credentialEnvironmentVariable' = 'CONTROL_SERVER_ONBOARD_CREDENTIAL'
        'OnboardTransport__useTls' = 'true'
        'OnboardTransport__allowInsecureLoopback' = 'false'
        'JourneyRuntime__enabled' = 'false'
        'MesIngest__baseUrl' = 'http://127.0.0.1:1'
        'RIoT__baseUrl' = 'http://127.0.0.1:1'
        'ControlServerBuild__commit' = $ControlServerCommit
    }
    $control = Start-Process -FilePath 'dotnet' `
        -ArgumentList @(Join-Path $controlPublish 'ControlServer.Host.dll') `
        -WorkingDirectory $controlPublish `
        -RedirectStandardOutput (Join-Path $logsRoot 'control-tls.out.log') `
        -RedirectStandardError (Join-Path $logsRoot 'control-tls.err.log') `
        -Environment $controlEnvironment -WindowStyle Hidden -PassThru
    $version = Wait-HttpJson -Uri "http://127.0.0.1:$healthPort/version"
    if ($version.protocolTag -ne $protocolTag -or
        $version.protocolCommit -ne $ProtocolCommit -or
        $version.manifestSha256 -ne $manifestSha256) {
        throw 'Running ControlServer reported an unexpected protocol identity.'
    }

    $probeJson = [StagedG3TlsHarness]::RunProbeAsync(
        $controlPort,
        $tlsMaterial.Fingerprint,
        $credential,
        $probeTranscript,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    [IO.File]::WriteAllText(
        (Join-Path $EvidenceRoot 'probe-result.json'),
        $probeJson,
        [Text.UTF8Encoding]::new($false))
    $probeResult = $probeJson | ConvertFrom-Json

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
        $tlsMaterial.PfxPath,
        $tlsMaterial.Password,
        $tlsMaterial.Fingerprint,
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

    # OnboardTcpServer.ExecuteAsync awaits each accepted connection to completion before accepting
    # the next, so the server holds exactly one onboard peer at a time; a synthetic peer opened while
    # the real one is connected never gets past the TLS handshake. Release the peer and its proxy
    # first, then give the business plane the server to itself.
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
        $tlsMaterial.PfxPath,
        $tlsMaterial.Password,
        $tlsMaterial.Fingerprint,
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
        $tlsMaterial.Fingerprint,
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
    Remove-TlsMaterial -Material $tlsMaterial
}

$controlLog = if (Test-Path -LiteralPath (Join-Path $logsRoot 'control-tls.out.log')) {
    Get-Content -LiteralPath (Join-Path $logsRoot 'control-tls.out.log') -Raw
} else { '' }
$controlErrorLog = if (Test-Path -LiteralPath (Join-Path $logsRoot 'control-tls.err.log')) {
    Get-Content -LiteralPath (Join-Path $logsRoot 'control-tls.err.log') -Raw
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
        $databaseObservation = [ordered]@{
            recoveryStateReportInboxRows = $recoveryRows
            businessMessageInboxRows = $businessRows
            currentSessionGeneration = [long](Invoke-Scalar "SELECT SessionGeneration FROM SessionRecoveries WHERE AgvId = '$agvId'")
            currentSessionReadiness = [string](Invoke-Scalar "SELECT Readiness FROM SessionRecoveries WHERE AgvId = '$agvId'")
            currentSessionReasonCode = [string](Invoke-Scalar "SELECT ReasonCode FROM SessionRecoveries WHERE AgvId = '$agvId'")
            orderIntentCount = [long](Invoke-Scalar 'SELECT COUNT(*) FROM OrderIntents')
            acceptedDemandCount = [long](Invoke-Scalar 'SELECT COUNT(*) FROM AcceptedDemands')
            stationOperationCount = [long](Invoke-Scalar 'SELECT COUNT(*) FROM StationOperations')
        }
    }
    finally { $connection.Dispose() }
}

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

$status = if ($null -ne $runError) {
    'INCONCLUSIVE_RUNNER_ERROR'
} elseif ($probePass -and $replayPass -and $noMovementPass -and $businessPass) {
    'STAGED_G3_TLS_RECOVERY_REPLAY_PASS'
} else {
    'STAGED_SLICE_FAIL'
}

$configuration = [ordered]@{
    loopbackOnly = $true
    tls = [ordered]@{
        probeEnabled = $true
        certificateSha256 = if ($null -ne $tlsMaterial) { $tlsMaterial.Fingerprint } else { $null }
        rootCertificateSha256 = if ($null -ne $tlsMaterial) { $tlsMaterial.RootFingerprint } else { $null }
        trustScope = if ($null -ne $tlsMaterial) { $tlsMaterial.TrustScope } else { $null }
        temporaryTrustInstalled = if ($null -ne $tlsMaterial) { $tlsMaterial.TrustInstalled } else { $false }
        temporaryTrustCleanupVerified = if ($null -ne $tlsMaterial) { $tlsMaterial.TrustCleanupVerified } else { $false }
        leafPinRequired = $true
        realOnboardAckDropTransport = 'TLS_LOOPBACK'
        realOnboardAckDropTlsCombination = if ($replayPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
    }
    ports = [ordered]@{
        controlTls = $controlPort
        controlHealth = $healthPort
        faultProxyPlaintext = $proxyPort
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
            ($null -ne $tlsMaterial -and $text.Contains($tlsMaterial.Password, [StringComparison]::Ordinal))) {
            $secretLeakFiles.Add([IO.Path]::GetRelativePath($EvidenceRoot, $file.FullName).Replace('\', '/'))
        }
    }
    catch {
        # Evidence is text-only in this staged runner; unreadable files are handled by the artifact hash list.
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
    runKind = 'STAGED_G3_REAL_PEERS_DETERMINISTIC_TLS'
    runId = $runId
    startedAtUtc = $runStartedAt
    completedAtUtc = [DateTimeOffset]::UtcNow
    status = $status
    classification = [ordered]@{
        stagedSlice = $status
        formalSlicePass = $false
        officialSlices = @(
            [ordered]@{ integrationSliceId = 'W2G-IS-00'; status = 'INCONCLUSIVE' },
            [ordered]@{ integrationSliceId = 'W2G-IS-06'; status = 'INCONCLUSIVE' }
        )
        fullG3 = 'INCONCLUSIVE'
        releaseCandidate = 'INCONCLUSIVE'
    }
    commits = [ordered]@{
        controlServer = $ControlServerCommit
        onboardEvidenceBinding = $OnboardCommit
        slotsSimulator = $SimulatorCommit
        protocol = $ProtocolCommit
        # The published peers come from exact clones at the commits above, but this runner and its
        # embedded harness execute from the working tree, so their identity has to be read back
        # rather than restated. A dirty tree makes the harness unattributable, and the flag says so.
        harness = (& git -C $ControlServerRepository rev-parse HEAD).Trim()
        harnessWorktreeClean = @(& git -C $ControlServerRepository status --porcelain).Count -eq 0
    }
    protocol = [ordered]@{
        tag = $protocolTag
        commit = $ProtocolCommit
        manifestSha256 = $manifestSha256
        schemaBundleSha256 = $schemaBundleSha256
        vectorsSha256 = $vectorsSha256
        g1 = $protocolG1Status
    }
    configurationSha256 = Get-Sha256Text $configurationJson
    configuration = $configuration
    commands = @($commands)
    assertions = [ordered]@{
        identityRejections = if ($probePass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
        sameConnectionSameMessageIdSameContent = if ($probePass -and $probeResult.duplicate.status -eq 'PASS') { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
        sameMessageIdDifferentContentStableConflict = if ($probePass -and $probeResult.conflict.status -eq 'PASS') { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
        recoveryStateReportFirstAckDropReplay = if ($replayPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
        recoveryStateReportFirstAckDropReplayOverTls = if ($replayPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
        businessMessageSameMessageIdSameContentReplay = if ($businessDuplicatePass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
        businessMessageSameMessageIdDifferentContentStableConflict = if ($businessConflictPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
        businessMessageAckDropInSessionReplay = if ($businessAckDropPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
        businessMessageDelayedDeliveryAccepted = if ($businessDelayPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
        businessMessageReorderedDeliveryAccepted = if ($businessReorderPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
        noMovementOrExternalSideEffects = if ($noMovementPass) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
        secretScan = if ($secretLeakFiles.Count -eq 0) { 'PASS' } else { 'FAIL' }
    }
    probe = $probeResult
    businessProbe = $businessProbeResult
    businessFaultInjection = $businessAckDropObservation
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

if ($status -notin @('STAGED_SLICE_PASS', 'STAGED_G3_TLS_RECOVERY_REPLAY_PASS') -or
    $secretLeakFiles.Count -ne 0) {
    throw "Staged G3 did not pass: $status. Evidence: $EvidenceRoot"
}
