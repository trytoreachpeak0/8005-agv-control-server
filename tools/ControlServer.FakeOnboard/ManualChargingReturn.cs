using ControlServer.TestDoubles;

namespace ControlServer.FakeOnboard;

/// <summary>
/// Raises <c>ManualChargingReturnToServiceRequested</c> the way an administrator at the vehicle does once it has been
/// charged by hand (control-server#404).
/// </summary>
public sealed record ManualChargingReturnCommand : CommandEnvelope
{
    /// <summary>
    /// The administrator's role. Null sends <c>MAINTENANCE_ADMINISTRATOR</c>, one of the two the profile allows; a scenario
    /// names another to be refused.
    /// </summary>
    public string? AdministratorRole { get; init; }

    public string? Reason { get; init; }

    /// <summary>What the administrator read off the vehicle; the protocol allows it to be absent.</summary>
    public double? ObservedBatteryPercent { get; init; }
}

/// <summary>
/// The control-plane verb a scenario lifts the server's manual-charging hold with. Mapped beside the rest of the control
/// plane; kept in this file because the behaviour is one thing.
/// </summary>
/// <remarks>
/// <para>
/// The server puts a vehicle that needs charging on a manual-charging hold when the charger roster has no charger for it,
/// and the only way out of that hold is this request (REQ-0171; <c>CV-MANUAL-CHARGING-RETURN</c>). A scenario of the
/// synthetic rig had no way to send it: the peer answers what the server asks, and this is something the vehicle asks.
/// </para>
/// <para>
/// Sent and nothing more. The server's <c>ManualChargingReturnToServiceResult</c> arrives like any other inbound line and is
/// in the wire log; what the request did is asserted where it took effect, in the server's own database. Not routed through
/// the command engine, like a load cancellation: a scenario asserts on what went out, not on a revision.
/// </para>
/// </remarks>
public static class ManualChargingReturnEndpoints
{
    public static void MapControlPlaneManualChargingReturn(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeOnboardState> engine = app.Services.GetRequiredService<CommandEngine<FakeOnboardState>>();
        OnboardPeerHolder holder = app.Services.GetRequiredService<OnboardPeerHolder>();

        app.MapGroup("/control/v1").MapPut("/manual-charging-returns/{requestId}", async (
            string requestId,
            ManualChargingReturnCommand command,
            CancellationToken cancellationToken) =>
        {
            OnboardPeerSession? peer = holder.Peer;
            if (peer is null || !peer.IsConnected)
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.NotAllowedInState, command.CommandId);
            }
            if (!Guid.TryParseExact(requestId, "D", out _))
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.InvalidArgument, command.CommandId);
            }

            string messageId = Guid.NewGuid().ToString("D");
            await peer.SendEnvelopeAsync("ManualChargingReturnToServiceRequested", messageId, new
            {
                requestId,
                administrator = new
                {
                    operatorId = "FAKE-ONBOARD-ADMINISTRATOR",
                    verificationMethod = "BADGE",
                    verifiedAt = DateTimeOffset.UtcNow
                },
                administratorRole = string.IsNullOrWhiteSpace(command.AdministratorRole)
                    ? "MAINTENANCE_ADMINISTRATOR"
                    : command.AdministratorRole,
                reason = string.IsNullOrWhiteSpace(command.Reason) ? "Charged by hand; returned by the scenario." : command.Reason,
                observedBatteryPercent = command.ObservedBatteryPercent
            }, cancellationToken).ConfigureAwait(false);
            return Results.Json(ControlPlaneConventions.Envelope(engine, new
            {
                command.CommandId,
                requestId,
                requestMessageId = messageId
            }));
        });
    }
}
