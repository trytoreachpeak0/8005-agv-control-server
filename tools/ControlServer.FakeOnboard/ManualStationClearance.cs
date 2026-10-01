using System.Collections.Concurrent;
using ControlServer.TestDoubles;

namespace ControlServer.FakeOnboard;

/// <summary>
/// Raises <c>ManualStationClearanceConfirmationRequested</c> the way a person with the clearance permission does at the vehicle
/// once they have moved it off the charger it could not charge at (control-server#406; onboard-hmi#221 is the real entry).
/// </summary>
public sealed record ManualStationClearanceCommand : CommandEnvelope
{
    /// <summary>The charger's station as the plan's CHARGER leg names it (the station name).</summary>
    public string? StationId { get; init; }

    /// <summary>Null sends <c>FAKE-ONBOARD-CLEARANCE</c>; the scenario's operator roster decides whether it may confirm.</summary>
    public string? OperatorId { get; init; }

    /// <summary>Null, as the real entry always sends it (no enum value names a charger).</summary>
    public string? PublicStationFunction { get; init; }

    /// <summary>Null sends <c>STATION_EMPTY</c>, the one value the real entry offers.</summary>
    public string? ClearedCondition { get; init; }
}

/// <summary>
/// The control-plane verb a scenario confirms a charger clear with. Kept in this file because the behaviour is one thing.
/// </summary>
/// <remarks>
/// <para>
/// <b>A resubmission is the first payload, byte for byte, under a new <c>messageId</c></b> (onboard-hmi#221's shape, the
/// coordinator's alignment of 09-30): the first request for a confirmation number is remembered, and the same number again
/// resends exactly that payload, whatever the second command says. So a scenario can show the server answers a resubmission
/// from what it stored.
/// </para>
/// <para>
/// Sent and nothing more, like <see cref="ManualChargingReturnEndpoints"/>: the server's
/// <c>ManualStationClearanceConfirmationResult</c> is in the wire log, and what the confirmation did is asserted in the
/// server's own database.
/// </para>
/// </remarks>
public static class ManualStationClearanceEndpoints
{
    private static readonly ConcurrentDictionary<string, object> Sent = new(StringComparer.Ordinal);

    public static void MapControlPlaneManualStationClearance(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeOnboardState> engine = app.Services.GetRequiredService<CommandEngine<FakeOnboardState>>();
        OnboardPeerHolder holder = app.Services.GetRequiredService<OnboardPeerHolder>();

        app.MapGroup("/control/v1").MapPut("/station-clearances/{confirmationRequestId}", async (
            string confirmationRequestId,
            ManualStationClearanceCommand command,
            CancellationToken cancellationToken) =>
        {
            OnboardPeerSession? peer = holder.Peer;
            if (peer is null || !peer.IsConnected)
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.NotAllowedInState, command.CommandId);
            }
            if (!Guid.TryParseExact(confirmationRequestId, "D", out _) || string.IsNullOrWhiteSpace(command.StationId))
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.InvalidArgument, command.CommandId);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            object payload = Sent.GetOrAdd(confirmationRequestId, _ => new
            {
                confirmationRequestId,
                stationId = command.StationId,
                publicStationFunction = command.PublicStationFunction,
                clearedCondition = string.IsNullOrWhiteSpace(command.ClearedCondition) ? "STATION_EMPTY" : command.ClearedCondition,
                @operator = new
                {
                    operatorId = string.IsNullOrWhiteSpace(command.OperatorId) ? "FAKE-ONBOARD-CLEARANCE" : command.OperatorId,
                    verificationMethod = "SESSION",
                    verifiedAt = now
                },
                observedAt = now
            });
            string messageId = Guid.NewGuid().ToString("D");
            await peer.SendEnvelopeAsync("ManualStationClearanceConfirmationRequested", messageId, payload, cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(ControlPlaneConventions.Envelope(engine, new
            {
                command.CommandId,
                confirmationRequestId,
                requestMessageId = messageId
            }));
        });
    }
}
