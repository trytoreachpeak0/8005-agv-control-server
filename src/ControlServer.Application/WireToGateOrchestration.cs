using ControlServer.Domain;

namespace ControlServer.Application;

public enum DemandIntakeOutcome
{
    Accepted,
    CandidateGone,
    CandidateChanged
}

public sealed class DemandIntakeService(IMesIngestCatalog catalog, IDemandAcceptanceStore store)
{
    public async Task<DemandIntakeOutcome> AcceptAsync(
        AcceptedDemandSnapshot discovered,
        OrderIntent orderIntent,
        CancellationToken cancellationToken)
    {
        AcceptedDemandSnapshot? current = await catalog.ReadCurrentAsync(discovered.DemandId, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return DemandIntakeOutcome.CandidateGone;
        }

        bool sameDecisionFacts = current.DemandId == discovered.DemandId &&
                                 current.TransportDemandKey == discovered.TransportDemandKey &&
                                 current.DemandRevision == discovered.DemandRevision &&
                                 current.HistoryEpoch == discovered.HistoryEpoch;
        if (!sameDecisionFacts)
        {
            return DemandIntakeOutcome.CandidateChanged;
        }

        await store.AcceptWithOrderIntentAsync(current, orderIntent, cancellationToken).ConfigureAwait(false);
        return DemandIntakeOutcome.Accepted;
    }
}

public enum MovementDispatchOutcome
{
    Confirmed,
    ResultUnknown,
    TerminalReconciliationRequired
}

public sealed record MovementDispatchResult(
    MovementDispatchOutcome Outcome,
    string UpperId,
    string? OrderId);

public sealed class MovementDispatchService(
    IMovementIntentStore store,
    IRiotMovementGateway gateway)
{
    public async Task<MovementDispatchResult> ReconcileOrCreateAsync(
        string upperId,
        CancellationToken cancellationToken)
    {
        StoredMovementIntent intent = await store.GetByUpperIdAsync(upperId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"OrderIntent '{upperId}' does not exist.");
        if (intent.Status == "CONFIRMED" && intent.OrderId is not null)
        {
            return new MovementDispatchResult(MovementDispatchOutcome.Confirmed, upperId, intent.OrderId);
        }

        RiotOrderObservation observed = await gateway.ReconcileByUpperIdAsync(upperId, cancellationToken)
            .ConfigureAwait(false);
        return observed.Kind switch
        {
            RiotOrderObservationKind.Active => await ConfirmAsync(observed, cancellationToken).ConfigureAwait(false),
            RiotOrderObservationKind.NotFound => await CreateAfterConfirmedAbsenceAsync(intent.Intent, cancellationToken)
                .ConfigureAwait(false),
            RiotOrderObservationKind.Terminal =>
                new MovementDispatchResult(MovementDispatchOutcome.TerminalReconciliationRequired, upperId, observed.OrderId),
            _ => await MarkUnknownAsync(upperId, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<MovementDispatchResult> CreateAfterConfirmedAbsenceAsync(
        OrderIntent intent,
        CancellationToken cancellationToken)
    {
        RiotOrderObservation accepted = await gateway.CreateAsync(intent, cancellationToken).ConfigureAwait(false);
        if (accepted.Kind is RiotOrderObservationKind.Unknown)
        {
            return await MarkUnknownAsync(intent.UpperId, cancellationToken).ConfigureAwait(false);
        }

        // A successful mutation response only proves that RIoT accepted the call. The
        // movement is confirmed exclusively by an independent read using the frozen upperId.
        RiotOrderObservation confirmed = await gateway.ReconcileByUpperIdAsync(intent.UpperId, cancellationToken)
            .ConfigureAwait(false);
        return confirmed.Kind switch
        {
            RiotOrderObservationKind.Active => await ConfirmAsync(confirmed, cancellationToken).ConfigureAwait(false),
            RiotOrderObservationKind.Terminal =>
                new MovementDispatchResult(MovementDispatchOutcome.TerminalReconciliationRequired, intent.UpperId, confirmed.OrderId),
            _ => await MarkUnknownAsync(intent.UpperId, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<MovementDispatchResult> ConfirmAsync(
        RiotOrderObservation observation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(observation.OrderId))
        {
            return await MarkUnknownAsync(observation.UpperId, cancellationToken).ConfigureAwait(false);
        }
        await store.ConfirmAsync(observation.UpperId, observation.OrderId, cancellationToken).ConfigureAwait(false);
        return new MovementDispatchResult(
            MovementDispatchOutcome.Confirmed,
            observation.UpperId,
            observation.OrderId);
    }

    private async Task<MovementDispatchResult> MarkUnknownAsync(
        string upperId,
        CancellationToken cancellationToken)
    {
        await store.MarkResultUnknownAsync(upperId, cancellationToken).ConfigureAwait(false);
        return new MovementDispatchResult(MovementDispatchOutcome.ResultUnknown, upperId, null);
    }
}
