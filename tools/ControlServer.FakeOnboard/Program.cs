using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ControlServer.Domain;

Dictionary<string, string?> arguments = ParseArguments(args);
string host = arguments.GetValueOrDefault("host") ?? "127.0.0.1";
int port = int.Parse(arguments.GetValueOrDefault("port") ?? "58005", System.Globalization.CultureInfo.InvariantCulture);
string agvId = arguments.GetValueOrDefault("agv") ?? "AGV-FAKE-001";
string credentialVariable = arguments.GetValueOrDefault("credential-env") ?? "CONTROL_SERVER_ONBOARD_CREDENTIAL";
string credential = Environment.GetEnvironmentVariable(credentialVariable)
    ?? throw new InvalidOperationException($"Credential environment variable '{credentialVariable}' is not set.");
bool useTls = arguments.ContainsKey("tls");

using TcpClient client = new();
await client.ConnectAsync(host, port);
await using Stream stream = await CreateStreamAsync(client, host, useTls);
using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
await using StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true)
{
    AutoFlush = true,
    NewLine = "\n"
};

string helloId = NewId();
await SendAsync(writer, Envelope(
    "SessionHello", helloId, agvId, null,
    new
    {
        onboardInstanceId = NewId(),
        onboardBuildCommit = "FAKE_ONBOARD_WORKTREE_BUILD",
        supportedProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
        profileId = ProtocolCandidateIdentity.ProfileId,
        protocolReleaseIdentity = ReleaseIdentity(),
        credentialProof = credential
    }));
using JsonDocument accepted = JsonDocument.Parse(await ReadAsync(reader));
if (accepted.RootElement.GetProperty("messageType").GetString() != "SessionAccepted")
{
    throw new InvalidOperationException("ControlServer rejected the Fake Onboard session.");
}
long generation = accepted.RootElement.GetProperty("sessionGeneration").GetInt64();

await SendAsync(writer, Envelope(
    "CapabilitySnapshot", NewId(), agvId, generation,
    new
    {
        capabilityVersion = 1,
        observedAt = DateTimeOffset.UtcNow,
        slotModelVersion = "fake-slot-model-v1",
        activeSlotConfigurationVersion = "fake-slot-config-v1",
        slotStates = SlotStates(),
        supportsBatchUnlock = false,
        onboardJournalFormatVersion = 1
    }));
RequireMessageType(await ReadAsync(reader), "SnapshotAppliedAck");

await SendAsync(writer, Envelope(
    "SafetyStateSnapshot", NewId(), agvId, generation,
    new
    {
        safetyStateVersion = 1,
        observedAt = DateTimeOffset.UtcNow,
        safety = new
        {
            departureSafe = true,
            vehicleStopped = true,
            allTargetSlotsLocked = true,
            allUnlockOutputsReset = true,
            unknownPresent = false,
            reasonCodes = Array.Empty<string>()
        },
        slotStates = SlotStates()
    }));
RequireMessageType(await ReadAsync(reader), "SnapshotAppliedAck");

await SendAsync(writer, Envelope(
    "RecoveryStateReport", NewId(), agvId, generation,
    new
    {
        reportId = NewId(),
        observedAt = DateTimeOffset.UtcNow,
        unsettledSlotOperationAttemptId = (string?)null,
        provenRecoveryCheckpoint = "NONE",
        activeUnlockSlots = Array.Empty<int>(),
        forcedRecoveryGeneration = 0,
        pendingResults = Array.Empty<object>(),
        journalContentSha256 = new string('0', 64)
    }));
RequireMessageType(await ReadAsync(reader), "DurableAck");
using JsonDocument readiness = JsonDocument.Parse(await ReadAsync(reader));
string? readinessValue = readiness.RootElement.GetProperty("payload").GetProperty("readiness").GetString();
if (readinessValue != "READY")
{
    throw new InvalidOperationException($"ControlServer readiness is '{readinessValue}'.");
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "PASS",
    scenario = "FAKE_ONBOARD_HANDSHAKE_READY",
    agvId,
    sessionGeneration = generation,
    protocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
    manifestSha256 = ProtocolCandidateIdentity.ManifestSha256
}));

static async Task<Stream> CreateStreamAsync(TcpClient client, string host, bool useTls)
{
    NetworkStream network = client.GetStream();
    if (!useTls)
    {
        return network;
    }
    SslStream ssl = new(network, leaveInnerStreamOpen: false);
    await ssl.AuthenticateAsClientAsync(host);
    return ssl;
}

static async Task SendAsync(StreamWriter writer, object message) =>
    await writer.WriteLineAsync(JsonSerializer.Serialize(message));

static async Task<string> ReadAsync(StreamReader reader) =>
    await reader.ReadLineAsync() ?? throw new EndOfStreamException("ControlServer closed the connection.");

static void RequireMessageType(string line, string expected)
{
    using JsonDocument document = JsonDocument.Parse(line);
    string? actual = document.RootElement.GetProperty("messageType").GetString();
    if (actual != expected)
    {
        throw new InvalidDataException($"Expected '{expected}', received '{actual}'.");
    }
}

static object Envelope(string messageType, string messageId, string agvId, long? generation, object payload) => new
{
    protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
    profileId = ProtocolCandidateIdentity.ProfileId,
    protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
    protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
    messageType,
    messageId,
    correlationId = (string?)null,
    agvId,
    sessionGeneration = generation,
    sentAt = DateTimeOffset.UtcNow,
    payload
};

static object ReleaseIdentity() => new
{
    repository = "8005-agv-protocol",
    releaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
    tag = ProtocolCandidateIdentity.Tag,
    commit = ProtocolCandidateIdentity.RepositoryCommit,
    protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
    profileId = ProtocolCandidateIdentity.ProfileId,
    manifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
    schemaBundleSha256 = ProtocolCandidateIdentity.SchemaBundleSha256,
    vectorsSha256 = ProtocolCandidateIdentity.VectorsSha256
};

static object[] SlotStates() => Enumerable.Range(1, 8)
    .Select(slotNo => (object)new
    {
        slotNo,
        operability = "OPERABLE",
        administrativeAvailability = "ENABLED",
        physicalState = "EMPTY",
        lockState = "LOCKED",
        unlockOutputState = "RESET",
        reasonCodes = Array.Empty<string>()
    })
    .ToArray();

static string NewId() => Guid.NewGuid().ToString("D");

static Dictionary<string, string?> ParseArguments(string[] values)
{
    Dictionary<string, string?> result = new(StringComparer.OrdinalIgnoreCase);
    for (int index = 0; index < values.Length; index++)
    {
        string current = values[index];
        if (!current.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected argument '{current}'.");
        }
        string key = current[2..];
        if (key == "tls")
        {
            result[key] = null;
            continue;
        }
        if (++index >= values.Length)
        {
            throw new ArgumentException($"Argument '--{key}' requires a value.");
        }
        result[key] = values[index];
    }
    return result;
}
