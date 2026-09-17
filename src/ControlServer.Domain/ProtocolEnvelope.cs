using System.Text.Json;

namespace ControlServer.Domain;

/// <summary>
/// The one place an outbound protocol line is built. The eleven envelope fields used to be typed out
/// at every sending site -- five of them, three in the host's transport, one in the synthetic peer --
/// so a check that has to see every line leaving this side had nowhere single to stand.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Serialize"/> is the choke point; <see cref="ReleaseIdentity"/> is the nine-value block
/// the same build states inside a payload. They are one file because they are one statement: the peer
/// compares the payload identity with the envelope identity and refuses the session when they differ
/// (<c>OnboardMessageProcessor.ValidateEnvelopeIdentity</c>), so a build that could write two
/// different answers has a bug no test of either half would find.
/// </para>
/// <para>
/// Nothing here reads the clock or generates an id. See <see cref="Serialize"/> for what that buys.
/// </para>
/// </remarks>
public static class ProtocolEnvelope
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds the wire line: the nine release identity values above the payload, then the seven
    /// per-message fields, in the order the protocol's envelope schema and the peer's parser both
    /// expect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The payload is serialized exactly as passed.</b> A caller whose line the peer must reproduce
    /// byte for byte materialises it to a <see cref="JsonElement"/> first
    /// (<c>OnboardJourneyPublisher.SerializeWire</c> says why); doing that here instead would
    /// re-escape the '+' of every DateTimeOffset in every other message and change bytes nothing
    /// asked to change.
    /// </para>
    /// <para>
    /// <b>The message id and the clock stay with the caller</b> for the same reason: where those
    /// happen relative to building the payload is observable on the wire. Argument evaluation is
    /// left to right, so <c>OnboardRecoveryCoordinator.DurableAck</c>'s read of <c>sentAt</c> still
    /// happens before the payload's <c>durablyAcceptedAt</c>.
    /// </para>
    /// </remarks>
    public static string Serialize(
        string messageType,
        string messageId,
        string? correlationId,
        string agvId,
        long? sessionGeneration,
        DateTimeOffset sentAt,
        object payload) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType,
            messageId,
            correlationId,
            agvId,
            sessionGeneration,
            sentAt,
            payload
        }, SerializerOptions);

    /// <summary>
    /// The release identity as a payload body: what <c>SessionAccepted</c> reports it accepted and
    /// what <c>SessionRejected</c> reports it expected. Both ends send this block, which is how a
    /// version mismatch is diagnosed rather than guessed at.
    /// </summary>
    /// <remarks>
    /// Property order is the contract's, not formatting: this object is serialized into a line whose
    /// bytes the other end reads back. It is deliberately not built from the envelope fields by
    /// reflection or by reusing an anonymous type -- the two are separate statements of the same
    /// nine values, and the comparison the peer makes is only meaningful while both are explicit.
    /// </remarks>
    public static object ReleaseIdentity() => new
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
}
