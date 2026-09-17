using System.Text.Json;
using ControlServer.Domain;

namespace ControlServer.Tests;

/// <summary>
/// Pins the bytes of the single outbound envelope builder. Every line this side sends goes through
/// it, and nothing else would notice a change: the peer tolerates field order, and only the journey
/// publisher's lines are hashed back -- the acknowledgements and recovery answers would drift
/// silently.
/// </summary>
/// <remarks>
/// The expected strings are built from <see cref="ProtocolCandidateIdentity"/> rather than written
/// out, so a contract refresh moves this fixture with it instead of turning it red for a reason that
/// is not the envelope.
/// </remarks>
public sealed class ProtocolEnvelopeTests
{
    private const string MessageId = "00000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset At = new(2026, 9, 10, 14, 0, 0, TimeSpan.FromHours(8));

    // The encoder's six-character escape for '+', spelled without a backslash so no tool on the way
    // into this file can decode it back into the character it stands for.
    private static readonly string EscapedPlus = (char)92 + "u002B";

    private static string Identity =>
        $$$"""
        "protocolVersion":{{{ProtocolCandidateIdentity.ProtocolVersion}}},"profileId":"{{{ProtocolCandidateIdentity.ProfileId}}}","protocolReleaseVersion":"{{{ProtocolCandidateIdentity.ReleaseVersion}}}","protocolReleaseManifestSha256":"{{{ProtocolCandidateIdentity.ManifestSha256}}}"
        """;

    [Fact]
    public void WritesTheElevenFieldsInContractOrderAndAClrPayloadThroughItsConverters()
    {
        string line = ProtocolEnvelope.Serialize(
            "DurableAck", MessageId, correlationId: null, "AGV-001", 7, At, new { observedAt = At });

        Assert.Equal(
            "{" + Identity +
            $$$""","messageType":"DurableAck","messageId":"{{{MessageId}}}","correlationId":null,"agvId":"AGV-001","sessionGeneration":7,"sentAt":"2026-09-10T14:00:00+08:00","payload":{"observedAt":"2026-09-10T14:00:00+08:00"}}""",
            line);
    }

    /// <summary>
    /// The journey publisher materialises its payload before calling in, which re-escapes the '+'
    /// (OnboardJourneyPublisher.SerializeWire). The envelope must keep that difference rather than
    /// normalise it either way: doing it for everyone changes every other message's bytes, and doing
    /// it for no one breaks the content hash the peer reports back for snapshots.
    /// </summary>
    [Fact]
    public void LeavesAMaterialisedPayloadEscapedAndTheEnvelopeFieldsUnescaped()
    {
        JsonElement payload = JsonSerializer.SerializeToElement(
            new { observedAt = At }, ProtocolEnvelope.SerializerOptions);

        string line = ProtocolEnvelope.Serialize(
            "VehicleBusinessStateSnapshot", MessageId, correlationId: null, "AGV-001", sessionGeneration: null, At, payload);

        Assert.Equal(
            "{" + Identity +
            $$$""","messageType":"VehicleBusinessStateSnapshot","messageId":"{{{MessageId}}}","correlationId":null,"agvId":"AGV-001","sessionGeneration":null,"sentAt":"2026-09-10T14:00:00+08:00","payload":{"observedAt":"2026-09-10T14:00:00{{{EscapedPlus}}}08:00"}}""",
            line);
    }

    /// <summary>
    /// The block the two ends compare to decide whether they are the same build. It is a payload, not
    /// an envelope, so it is not covered by the line above: its property order is what the other end
    /// reads back field by field, and its values have to be the same nine the envelope header carries.
    /// </summary>
    [Fact]
    public void ReleaseIdentityWritesTheNineValuesInTheOrderTheOtherEndReadsThem()
    {
        Assert.Equal(
            JsonSerializer.Serialize(new
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
            }, ProtocolEnvelope.SerializerOptions),
            JsonSerializer.Serialize(ProtocolEnvelope.ReleaseIdentity(), ProtocolEnvelope.SerializerOptions));
    }
}
