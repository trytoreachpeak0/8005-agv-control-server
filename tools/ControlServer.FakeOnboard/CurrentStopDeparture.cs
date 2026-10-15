using ControlServer.TestDoubles;

namespace ControlServer.FakeOnboard;

/// <summary>
/// The operator's 「出发」 at a staging pickup stop, as a scenario drives it (protocol 4.0.0 candidate
/// <c>CurrentStopDepartureRequested</c>, control-server#713).
/// </summary>
/// <remarks>
/// <para>
/// The request echoes the worklist the vehicle shows, so by default it is built from the last
/// <c>CurrentStopWorklistSnapshot</c> this peer acknowledged: its <c>stationId</c>, <c>worklistRevision</c> and
/// <c>operationSessionId</c> (null when that worklist carried none, as every empty one does). A scenario may override any of
/// the three to be refused with <c>STATION_MISMATCH</c> or <c>WORKLIST_REVISION_STALE</c>.
/// </para>
/// <para>
/// Sent and nothing more, like the manual charging return. The server answers with a
/// <c>CurrentStopDepartureResult</c>, which the peer acknowledges with a <c>DurableAck</c>; what the departure did is
/// asserted in the server's own database.
/// </para>
/// </remarks>
public sealed record CurrentStopDepartureCommand : CommandEnvelope
{
    public string? StationId { get; init; }

    public long? WorklistRevision { get; init; }

    public string? OperationSessionId { get; init; }

    /// <summary>Send <c>operationSessionId: null</c> whatever the last worklist said.</summary>
    public bool WithoutOperationSession { get; init; }

    public string? OperatorId { get; init; }
}

public static class CurrentStopDepartureEndpoints
{
    public static void MapControlPlaneCurrentStopDeparture(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeOnboardState> engine = app.Services.GetRequiredService<CommandEngine<FakeOnboardState>>();
        OnboardPeerHolder holder = app.Services.GetRequiredService<OnboardPeerHolder>();

        app.MapGroup("/control/v1").MapPut("/current-stop-departures/{departureRequestId}", async (
            string departureRequestId,
            CurrentStopDepartureCommand command,
            CancellationToken cancellationToken) =>
        {
            OnboardPeerSession? peer = holder.Peer;
            if (peer is null || !peer.IsConnected)
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.NotAllowedInState, command.CommandId);
            }
            if (!Guid.TryParseExact(departureRequestId, "D", out _))
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.InvalidArgument, command.CommandId);
            }
            ShownWorklist? shown = peer.LastWorklist;
            string? stationId = command.StationId ?? shown?.StationId;
            long? worklistRevision = command.WorklistRevision ?? shown?.WorklistRevision;
            if (stationId is null || worklistRevision is null)
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.NotAllowedInState, command.CommandId);
            }

            string messageId = Guid.NewGuid().ToString("D");
            await peer.SendEnvelopeAsync("CurrentStopDepartureRequested", messageId, new
            {
                departureRequestId,
                operationSessionId = command.WithoutOperationSession
                    ? null
                    : command.OperationSessionId ?? shown?.OperationSessionId,
                stationId,
                worklistRevision = worklistRevision.Value,
                @operator = new
                {
                    operatorId = string.IsNullOrWhiteSpace(command.OperatorId) ? "FAKE-ONBOARD-OPERATOR" : command.OperatorId,
                    verificationMethod = "BADGE",
                    verifiedAt = DateTimeOffset.UtcNow
                }
            }, cancellationToken).ConfigureAwait(false);
            return Results.Json(ControlPlaneConventions.Envelope(engine, new
            {
                command.CommandId,
                departureRequestId,
                requestMessageId = messageId,
                stationId,
                worklistRevision
            }));
        });
    }
}

/// <summary>The worklist the fake vehicle shows: the last <c>CurrentStopWorklistSnapshot</c> it acknowledged.</summary>
public sealed record ShownWorklist(string StationId, long WorklistRevision, string? OperationSessionId);
