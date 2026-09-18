using System.Collections.Concurrent;
using System.Text.Json;
using ControlServer.TestDoubles;

namespace ControlServer.FakeOnboard;

/// <summary>Raises a load cancellation the way the operator's 「取消装货」 does.</summary>
public sealed record LoadCancellationCommand : CommandEnvelope
{
    public string? DemandId { get; init; }

    /// <summary>Null is the cancellation before any sublot entry: nothing was commanded, so no attempt.</summary>
    public string? SlotOperationAttemptId { get; init; }

    public string? Reason { get; init; }
}

/// <summary>What this peer knows about one cancellation it raised.</summary>
public sealed record FakeLoadCancellation
{
    public string CancellationId { get; init; } = string.Empty;
    public string DemandId { get; init; } = string.Empty;
    public string? SlotOperationAttemptId { get; init; }
    public string RequestMessageId { get; init; } = string.Empty;
    public DateTimeOffset RequestedAt { get; init; }
    public string? Decision { get; init; }
    public IReadOnlyList<int>? AuthorizedSlots { get; init; }
    public string? ProblemReasonCode { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public string? ResultMessageId { get; init; }
    public string? ResultOverallOutcome { get; init; }
    public DateTimeOffset? ResultSentAt { get; init; }
    public DateTimeOffset? ResultAcknowledgedAt { get; init; }
}

/// <summary>
/// The operator-initiated load cancellation (ADR-cross-0046), as much of it as a server scenario needs: raise
/// <c>LoadCancellationStartRequested</c>, and on an authorization with no slots -- the cancellation before any
/// sublot entry, control-server#83 -- report <c>ALL_EMPTY</c> with no slot results, which is the only thing
/// a vehicle that commanded no slot IO can truthfully say.
/// </summary>
/// <remarks>
/// <para>
/// An authorization that names slots is recorded and left unanswered: proving slots empty needs IO this peer
/// does not have, and inventing that proof would test only this script.
/// </para>
/// <para>
/// Kept outside the command engine, like the wire log: a scenario asserts on what went out and came back, not
/// on a revision, and routing every authorization and acknowledgement through the engine would move the
/// revision under a scenario racing an unrelated command.
/// </para>
/// </remarks>
public sealed class FakeLoadCancellations
{
    private readonly ConcurrentDictionary<string, FakeLoadCancellation> cancellations = new(StringComparer.Ordinal);

    public IReadOnlyList<FakeLoadCancellation> Snapshot() =>
        [.. cancellations.Values.OrderBy(item => item.RequestedAt)];

    public async Task<FakeLoadCancellation> RequestAsync(
        OnboardPeerSession peer,
        string cancellationId,
        string demandId,
        string? slotOperationAttemptId,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        string messageId = Guid.NewGuid().ToString("D");
        FakeLoadCancellation record = new()
        {
            CancellationId = cancellationId,
            DemandId = demandId,
            SlotOperationAttemptId = slotOperationAttemptId,
            RequestMessageId = messageId,
            RequestedAt = DateTimeOffset.UtcNow
        };
        // Recorded before it is sent: the authorization can arrive before the send returns.
        cancellations[cancellationId] = record;
        await peer.SendEnvelopeAsync("LoadCancellationStartRequested", messageId, new
        {
            cancellationId,
            demandId,
            slotOperationAttemptId,
            @operator = new
            {
                operatorId = "FAKE-ONBOARD-OPERATOR",
                verificationMethod = "BADGE",
                verifiedAt = DateTimeOffset.UtcNow
            },
            reason
        }, cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task ObserveAuthorizationAsync(
        OnboardPeerSession peer,
        JsonElement authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        JsonElement payload = authorization.GetProperty("payload");
        string cancellationId = payload.GetProperty("cancellationId").GetString() ?? string.Empty;
        if (!cancellations.TryGetValue(cancellationId, out FakeLoadCancellation? record))
        {
            return;
        }
        int[] slots = [.. payload.GetProperty("slots").EnumerateArray().Select(slot => slot.GetInt32())];
        string decision = payload.GetProperty("decision").GetString() ?? string.Empty;
        JsonElement problem = payload.GetProperty("problem");
        record = record with
        {
            Decision = decision,
            AuthorizedSlots = slots,
            ProblemReasonCode = problem.ValueKind == JsonValueKind.Object
                ? problem.GetProperty("reasonCode").GetString()
                : null,
            DecidedAt = DateTimeOffset.UtcNow
        };
        cancellations[cancellationId] = record;
        if (decision != "AUTHORIZED" || slots.Length > 0 || record.ResultMessageId is not null)
        {
            return;
        }

        string resultMessageId = Guid.NewGuid().ToString("D");
        cancellations[cancellationId] = record with
        {
            ResultMessageId = resultMessageId,
            ResultOverallOutcome = "ALL_EMPTY",
            ResultSentAt = DateTimeOffset.UtcNow
        };
        await peer.SendEnvelopeAsync("LoadCancellationResult", resultMessageId, new
        {
            cancellationId,
            demandId = record.DemandId,
            slotOperationAttemptId = record.SlotOperationAttemptId,
            overallOutcome = "ALL_EMPTY",
            slotResults = Array.Empty<object>(),
            observedAt = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);
    }

    public void ObserveDurableAck(JsonElement ack)
    {
        JsonElement payload = ack.GetProperty("payload");
        if (payload.GetProperty("acceptedMessageType").GetString() != "LoadCancellationResult")
        {
            return;
        }
        string? acceptedMessageId = payload.GetProperty("acceptedMessageId").GetString();
        foreach (FakeLoadCancellation record in cancellations.Values)
        {
            if (record.ResultMessageId == acceptedMessageId)
            {
                cancellations[record.CancellationId] = record with { ResultAcknowledgedAt = DateTimeOffset.UtcNow };
            }
        }
    }
}

/// <summary>The control plane for <see cref="FakeLoadCancellations"/>.</summary>
public static class ControlPlaneLoadCancellation
{
    public static void MapControlPlaneLoadCancellation(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeOnboardState> engine = app.Services.GetRequiredService<CommandEngine<FakeOnboardState>>();
        OnboardPeerHolder holder = app.Services.GetRequiredService<OnboardPeerHolder>();
        FakeLoadCancellations cancellations = app.Services.GetRequiredService<FakeLoadCancellations>();
        RouteGroupBuilder control = app.MapGroup("/control/v1");

        control.MapGet("/load-cancellations", () =>
            Results.Json(ControlPlaneConventions.Envelope(engine, new { cancellations = cancellations.Snapshot() })));

        control.MapPut("/load-cancellations/{cancellationId}", async (
            string cancellationId,
            LoadCancellationCommand command,
            CancellationToken cancellationToken) =>
        {
            OnboardPeerSession? peer = holder.Peer;
            if (peer is null || !peer.IsConnected)
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.NotAllowedInState, command.CommandId);
            }
            if (!Guid.TryParseExact(cancellationId, "D", out _) ||
                !Guid.TryParseExact(command.DemandId, "D", out _) ||
                (command.SlotOperationAttemptId is not null &&
                 !Guid.TryParseExact(command.SlotOperationAttemptId, "D", out _)))
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.InvalidArgument, command.CommandId);
            }
            FakeLoadCancellation record = await cancellations.RequestAsync(
                peer,
                cancellationId,
                command.DemandId!,
                command.SlotOperationAttemptId,
                string.IsNullOrWhiteSpace(command.Reason) ? "Cancelled by the scenario operator." : command.Reason,
                cancellationToken).ConfigureAwait(false);
            return Results.Json(ControlPlaneConventions.Envelope(engine, new
            {
                command.CommandId,
                record.CancellationId,
                record.RequestMessageId
            }));
        });
    }
}
