using ControlServer.TestDoubles;

namespace ControlServer.FakeOnboard;

/// <summary>
/// The operator's scan as a scenario drives it: entering a sublot the request had no need to offer, and
/// entering again after the server has refused the previous entry (BR-013, control-server#82).
/// </summary>
/// <remarks>
/// <para>
/// The peer answers an entry request on its own, with the first sublot the request offered, and that is
/// enough for everything the server used to do with a submission. It is not enough for either half of
/// what BR-013 adds. A sublot outside the dispatch scope is not among the offered ones — a request that
/// offered it would not be a dispatch scope — so <c>SUBLOT_NOT_IN_DISPATCH_SCOPE</c> is unreachable
/// without naming one. And an entry the server has refused is judged once: the operator's rescan has to
/// arrive as a new <c>SublotSubmitted</c> with a new messageId, where the peer's cached answer would
/// otherwise replay the refused line for as long as the request stands.
/// </para>
/// <para>
/// <b>Applies to every entry request while it stands, rather than to one key.</b> The fake vehicle
/// carries one journey at a time, and a scenario sets this before the stop asks for an entry — or
/// rescans after one — so which request it applies to is never in doubt. Keying it would put the
/// server's <c>operationSessionId</c> and <c>worklistRevision</c> into a scenario that has no other use
/// for them.
/// </para>
/// </remarks>
public sealed record SublotScanCommand : CommandEnvelope
{
    /// <summary>The sublot to enter, or null to enter the first one the request offered.</summary>
    public string? Sublot { get; init; }

    /// <summary>
    /// Scan again: the answers cached for the entry requests are dropped, so the server's next replay of
    /// one is answered with a freshly built submission instead of the line sent before it.
    /// </summary>
    public bool Rescan { get; init; }
}

/// <summary>
/// The control-plane verb a scenario drives a scan with. Mapped beside the rest of the control plane;
/// kept in this file because the behaviour is one thing.
/// </summary>
public static class SublotScanEndpoints
{
    public static void MapControlPlaneSublotScan(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeOnboardState> engine = app.Services.GetRequiredService<CommandEngine<FakeOnboardState>>();
        OnboardPeerHolder holder = app.Services.GetRequiredService<OnboardPeerHolder>();

        app.MapGroup("/control/v1").MapPut("/sublot-scan", (SublotScanCommand command) =>
        {
            OnboardPeerSession? peer = holder.Peer;
            if (peer is null)
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.NotAllowedInState, command.CommandId);
            }
            if (command.Sublot is { } sublot && string.IsNullOrWhiteSpace(sublot))
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.InvalidArgument, command.CommandId);
            }
            if (command.Rescan)
            {
                // Forgotten before the policy moves, because the two are one operator action: by the time
                // this returns, the next replay of an entry request is answered with the sublot set here.
                peer.ForgetEntryAnswers();
            }
            return ControlPlaneConventions.Handle(engine, "sublot-scan", command, state =>
                string.Equals(state.Policy.SublotScan, command.Sublot, StringComparison.Ordinal)
                    ? null
                    : state with { Policy = state.Policy with { SublotScan = command.Sublot } });
        });
    }
}
