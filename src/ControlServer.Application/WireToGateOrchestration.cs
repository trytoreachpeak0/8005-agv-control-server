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
        DemandCatalogSnapshot finalCatalog = await catalog.ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
        AcceptedDemandSnapshot? current = finalCatalog.Items.SingleOrDefault(candidate =>
            string.Equals(candidate.DemandId, discovered.DemandId, StringComparison.Ordinal));
        if (current is null)
        {
            return DemandIntakeOutcome.CandidateGone;
        }

        bool sameDecisionFacts = HasSameDecisionFacts(discovered, current) &&
                                 string.Equals(finalCatalog.HistoryEpoch, discovered.HistoryEpoch, StringComparison.Ordinal);
        if (!sameDecisionFacts)
        {
            return DemandIntakeOutcome.CandidateChanged;
        }

        AcceptedDemandSnapshot accepted = current with
        {
            HistoryEpoch = finalCatalog.HistoryEpoch,
            CatalogRevision = finalCatalog.CatalogRevision
        };
        await store.AcceptWithOrderIntentAsync(accepted, orderIntent, cancellationToken).ConfigureAwait(false);
        return DemandIntakeOutcome.Accepted;
    }

    private static bool HasSameDecisionFacts(
        AcceptedDemandSnapshot discovered,
        AcceptedDemandSnapshot current) =>
        string.Equals(current.DemandId, discovered.DemandId, StringComparison.Ordinal) &&
        string.Equals(current.SeriesId, discovered.SeriesId, StringComparison.Ordinal) &&
        string.Equals(current.TransportDemandKey, discovered.TransportDemandKey, StringComparison.Ordinal) &&
        string.Equals(current.WorkType, discovered.WorkType, StringComparison.Ordinal) &&
        string.Equals(current.Sublot, discovered.Sublot, StringComparison.Ordinal) &&
        current.Generation == discovered.Generation &&
        current.DemandRevision == discovered.DemandRevision &&
        current.CreatedAt == discovered.CreatedAt &&
        current.ValueObservedAt == discovered.ValueObservedAt &&
        string.Equals(current.ValuePollTraceId, discovered.ValuePollTraceId, StringComparison.Ordinal) &&
        string.Equals(current.ValueProjectionCommitId, discovered.ValueProjectionCommitId, StringComparison.Ordinal) &&
        Equals(current.LiveMesFields, discovered.LiveMesFields);
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

public sealed record JourneyIntakeResult(
    DemandIntakeOutcome IntakeOutcome,
    MovementDispatchResult? MovementDispatch);

public sealed class JourneyIntakeCoordinator(
    DemandIntakeService intake,
    MovementDispatchService movementDispatch)
{
    public async Task<JourneyIntakeResult> AcceptAndDispatchToPickupAsync(
        AcceptedDemandSnapshot prevalidatedCandidate,
        OrderIntent pickupIntent,
        CancellationToken cancellationToken)
    {
        DemandIntakeOutcome intakeOutcome = await intake.AcceptAsync(
            prevalidatedCandidate,
            pickupIntent,
            cancellationToken).ConfigureAwait(false);
        if (intakeOutcome != DemandIntakeOutcome.Accepted)
        {
            return new JourneyIntakeResult(intakeOutcome, null);
        }

        MovementDispatchResult dispatch = await movementDispatch.ReconcileOrCreateAsync(
            pickupIntent.UpperId,
            cancellationToken).ConfigureAwait(false);
        return new JourneyIntakeResult(intakeOutcome, dispatch);
    }
}
