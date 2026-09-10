using System.Text.Json;

namespace ControlServer.Domain;

/// <summary>
/// The one place an outbound protocol line is built. The eleven envelope fields used to be typed out
/// at five sites -- three in the host's transport, one in the synthetic peer -- so a check that must
/// see every line leaving this side (ADR-cross-0058 map, #27) had nowhere single to stand.
/// </summary>
public static class ProtocolEnvelope
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Sees every line built here, plus the one kind of line rewritten after it left here
    /// (OnboardJourneyPublisher.ReplayPendingForSessionAsync). Only the test host sets it:
    /// ControlServer.Tests checks what it collects against the protocol's JSON Schema (#33). Product
    /// code never assigns it, so in production this costs a null check -- what a vehicle-facing server
    /// should do with a line that breaks the contract has no good answer on the wire (#27, point 4).
    /// </summary>
    public static Action<string, string>? OutboundObserver { get; set; }

    /// <remarks>
    /// The payload is serialized exactly as passed. A caller whose line the peer must reproduce byte
    /// for byte materialises it to a JsonElement first (OnboardJourneyPublisher.SerializeWire says
    /// why); doing that here instead would re-escape the '+' of every DateTimeOffset in every other
    /// message and change bytes nothing asked to change. Generating the message id or reading the
    /// clock is left to the caller for the same reason: where those happen relative to building the
    /// payload is observable on the wire.
    /// </remarks>
    public static string Serialize(
        string messageType,
        string messageId,
        string? correlationId,
        string agvId,
        long? sessionGeneration,
        DateTimeOffset sentAt,
        object payload)
    {
        string line = JsonSerializer.Serialize(new
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
        OutboundObserver?.Invoke(messageType, line);
        return line;
    }
}
