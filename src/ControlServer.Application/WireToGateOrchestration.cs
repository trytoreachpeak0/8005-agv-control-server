using System.Security.Cryptography;
using System.Text.Json;
using ControlServer.Domain;

namespace ControlServer.Application;

public enum DemandIntakeOutcome
{
    Accepted,
    CandidateGone,
    CandidateChanged,
    FinalAdmissionRejected,

    /// <summary>
    /// The plan names the task type station versions but not the catalog revision its endpoints came from, so it could
    /// not be accepted without freezing the versions alone (control-server#198). Nothing was written.
    /// </summary>
    JourneyPlanIncomplete
}

public sealed class DemandIntakeService(IMesIngestCatalog catalog, IDemandAcceptanceStore store)
{
    public async Task<DemandIntakeOutcome> AcceptAsync(
        AcceptedDemandSnapshot discovered,
        OrderIntent orderIntent,
        CancellationToken cancellationToken)
    {
        return await AcceptCoreAsync(
                discovered,
                orderIntent,
                journey: null,
                finalAdmissionGate: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<DemandIntakeOutcome> AcceptJourneyAsync(
        AcceptedDemandSnapshot discovered,
        OrderIntent orderIntent,
        JourneyExecutionPlan journey,
        CancellationToken cancellationToken) =>
        AcceptCoreAsync(discovered, orderIntent, journey, finalAdmissionGate: null, cancellationToken);

    public Task<DemandIntakeOutcome> AcceptJourneyAsync(
        AcceptedDemandSnapshot discovered,
        OrderIntent orderIntent,
        JourneyExecutionPlan journey,
        Func<CancellationToken, Task<bool>> finalAdmissionGate,
        CancellationToken cancellationToken) =>
        AcceptCoreAsync(discovered, orderIntent, journey, finalAdmissionGate, cancellationToken);

    private async Task<DemandIntakeOutcome> AcceptCoreAsync(
        AcceptedDemandSnapshot discovered,
        OrderIntent orderIntent,
        JourneyExecutionPlan? journey,
        Func<CancellationToken, Task<bool>>? finalAdmissionGate,
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
        if (finalAdmissionGate is not null &&
            !await finalAdmissionGate(cancellationToken).ConfigureAwait(false))
        {
            return DemandIntakeOutcome.FinalAdmissionRejected;
        }

        AcceptedDemandSnapshot accepted = current with
        {
            HistoryEpoch = finalCatalog.HistoryEpoch,
            CatalogRevision = finalCatalog.CatalogRevision
        };
        if (journey is null)
        {
            await store.AcceptWithOrderIntentAsync(accepted, orderIntent, cancellationToken).ConfigureAwait(false);
        }
        else if (store is IJourneyAcceptanceStore journeyStore)
        {
            try
            {
                await journeyStore.AcceptWithOrderIntentAsync(accepted, orderIntent, journey, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AreaAssignmentVersionChangedException)
            {
                // A new area assignment version was imported after the round judged this candidate. That is a
                // decision fact changing under intake like any other, and nothing was written.
                return DemandIntakeOutcome.CandidateChanged;
            }
            catch (JourneyPlanFreezeIncompleteException)
            {
                // control-server#198: refused before anything was written. Reported as this demand's outcome, not
                // thrown: thrown, it left the engine's round at this vehicle, and the same demand was picked again
                // every round.
                return DemandIntakeOutcome.JourneyPlanIncomplete;
            }
        }
        else
        {
            throw new InvalidOperationException("The configured demand store cannot atomically accept a journey.");
        }
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
    TerminalReconciliationRequired,

    /// <summary>
    /// The observation was create-eligible but the operational create-dispatch gate is closed,
    /// so no RIoT mutation was attempted and the intent keeps its create eligibility.
    /// </summary>
    CreateDispatchDisabled
}

/// <summary>
/// Operational gate for RIoT create dispatch. It is independent of the journey runtime so an
/// operator can run a mutation-free rehearsal with the runtime enabled, then open this gate for
/// the single authorized create. It never widens what a create is allowed to do: the persisted
/// at-most-once guards still bound an intent to one create attempt.
/// </summary>
public sealed record RiotCreateDispatchPolicy(bool CreateEnabled)
{
    public static readonly RiotCreateDispatchPolicy Denied = new(false);
    public static readonly RiotCreateDispatchPolicy Allowed = new(true);
}

public sealed record MovementDispatchResult(
    MovementDispatchOutcome Outcome,
    string UpperId,
    string? OrderId);

public sealed class MovementDispatchService
{
    private static readonly TimeSpan EvidenceWriteTimeout = TimeSpan.FromSeconds(5);
    private const string ExperimentalAbsentEligibilityBasis = "EXPERIMENTAL_ABSENT_AT_OBSERVATION";
    private const string IdempotentAbsentEligibilityBasis = "ABSENT_AT_OBSERVATION_IDEMPOTENT_CREATE";

    /// <summary>RIoT orderState for SUCCESS: 1 QUEUEING -> 3 EXECUTING -> 5 SUCCESS.</summary>
    private const int SuccessfulOrderState = 5;
    private readonly IMovementIntentStore store;
    private readonly IRiotMovementGateway gateway;
    private readonly TimeProvider timeProvider;
    private readonly IExperimentalRiotCreateAuthorizationSource experimentalAuthorizationSource;
    private readonly RiotCreateDispatchPolicy createDispatchPolicy;

    public MovementDispatchService(IMovementIntentStore store, IRiotMovementGateway gateway)
        : this(store, gateway, TimeProvider.System, DenyExperimentalRiotCreateAuthorizationSource.Instance)
    {
    }

    public MovementDispatchService(
        IMovementIntentStore store,
        IRiotMovementGateway gateway,
        TimeProvider timeProvider)
        : this(store, gateway, timeProvider, DenyExperimentalRiotCreateAuthorizationSource.Instance)
    {
    }

    /// <summary>
    /// Constructs the service without an operational create-dispatch gate, which is how the
    /// unit tests exercise dispatch behaviour directly. The Host never uses this overload: it
    /// resolves the overload below so the gate is always an explicit, configured decision.
    /// </summary>
    public MovementDispatchService(
        IMovementIntentStore store,
        IRiotMovementGateway gateway,
        TimeProvider timeProvider,
        IExperimentalRiotCreateAuthorizationSource experimentalAuthorizationSource)
        : this(store, gateway, timeProvider, experimentalAuthorizationSource, RiotCreateDispatchPolicy.Allowed)
    {
    }

    public MovementDispatchService(
        IMovementIntentStore store,
        IRiotMovementGateway gateway,
        TimeProvider timeProvider,
        IExperimentalRiotCreateAuthorizationSource experimentalAuthorizationSource,
        RiotCreateDispatchPolicy createDispatchPolicy)
    {
        this.store = store;
        this.gateway = gateway;
        this.timeProvider = timeProvider;
        this.experimentalAuthorizationSource = experimentalAuthorizationSource;
        this.createDispatchPolicy = createDispatchPolicy;
    }

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
        if (intent.Status == "TERMINAL_RECONCILIATION_REQUIRED")
        {
            return new MovementDispatchResult(
                MovementDispatchOutcome.TerminalReconciliationRequired,
                upperId,
                intent.OrderId);
        }

        ExperimentalRiotCreateAuthorization? experimentalAuthorization =
            await PersistFreshExperimentalAuthorizationAsync(intent, cancellationToken).ConfigureAwait(false);

        RiotOrderObservation observed = await gateway.ReconcileByUpperIdAsync(upperId, cancellationToken)
            .ConfigureAwait(false);
        RiotDispatchAuditPhase reconciliationPhase = intent.Status == "PENDING_RECONCILIATION"
            ? RiotDispatchAuditPhase.PreCreateReconciliation
            : RiotDispatchAuditPhase.PostCreateReconciliation;
        return observed.Kind switch
        {
            RiotOrderObservationKind.Active => await ConfirmAsync(
                    intent.Intent,
                    observed,
                    reconciliationPhase,
                    intent.CreateAttemptId,
                    cancellationToken,
                    intent.ExperimentalAuthorizationId,
                    intent.EligibilityBasis)
                .ConfigureAwait(false),
            RiotOrderObservationKind.NotFound when intent.Status == "PENDING_RECONCILIATION" &&
                                                   intent.DispatchAuditVersion == 1 &&
                                                   intent.CreateAttemptCount == 0 =>
                await CreateAfterConfirmedAbsenceAsync(intent.Intent, observed, cancellationToken)
                    .ConfigureAwait(false),
            RiotOrderObservationKind.NotFound when intent.Status == "PENDING_RECONCILIATION" =>
                await MarkUnknownAsync(
                        upperId,
                        observed,
                        RiotDispatchAuditPhase.PreCreateReconciliation,
                        RiotDispatchAuditOutcome.LegacyAuditUnavailable,
                        attemptId: null,
                        cancellationToken)
                    .ConfigureAwait(false),
            RiotOrderObservationKind.NotFound => await MarkUnknownAsync(
                    upperId,
                    observed,
                    RiotDispatchAuditPhase.PostCreateReconciliation,
                    RiotDispatchAuditOutcome.Unknown,
                    intent.CreateAttemptId,
                    cancellationToken,
                    intent.ExperimentalAuthorizationId,
                    intent.EligibilityBasis)
                .ConfigureAwait(false),
            // orderState 5 is SUCCESS, not an outcome that needs a human. It shares the Terminal
            // kind with CANCELLED, FAILED and DELETED, which do, so it is split out here: a
            // movement that RIoT reports as completed for this exact frozen intent confirms the
            // leg. Physical arrival is still proven separately by IsTrustedArrivalAsync, which
            // requires the vehicle to be stopped at the target station with no active order.
            // Without this, a leg whose order completes before the first post-create
            // reconciliation -- a vehicle already standing at its target, say -- would dead-end
            // in TERMINAL_RECONCILIATION_REQUIRED despite having succeeded.
            RiotOrderObservationKind.Terminal when observed.OrderState == SuccessfulOrderState =>
                await ConfirmAsync(
                    intent.Intent,
                    observed,
                    reconciliationPhase,
                    intent.CreateAttemptId,
                    cancellationToken,
                    intent.ExperimentalAuthorizationId,
                    intent.EligibilityBasis).ConfigureAwait(false),
            RiotOrderObservationKind.Terminal => await MarkTerminalAsync(
                intent.Intent,
                observed,
                reconciliationPhase,
                intent.CreateAttemptId,
                cancellationToken,
                intent.ExperimentalAuthorizationId,
                intent.EligibilityBasis).ConfigureAwait(false),
            // BC-ORDER-004: RIoT enforces upperId idempotency server-side, so dispatching a
            // create can never produce a second order. An exact absent-at-observation read
            // (HTTP 200 / business code 0, no result) therefore does not have to prove absence
            // before dispatching: the worst case is a definitive "订单已存在" business failure,
            // which reconciles to the order that already holds this frozen upperId.
            RiotOrderObservationKind.Unknown when intent.Status == "PENDING_RECONCILIATION" &&
                                                  intent.DispatchAuditVersion == 1 &&
                                                  intent.CreateAttemptCount == 0 &&
                                                  IsExactAbsentAtObservation(upperId, observed) =>
                await CreateAfterConfirmedAbsenceAsync(
                        intent.Intent,
                        observed,
                        cancellationToken,
                        RiotDispatchAuditOutcome.Unknown,
                        IdempotentAbsentEligibilityBasis)
                    .ConfigureAwait(false),
            RiotOrderObservationKind.Unknown when experimentalAuthorization is not null &&
                                                  IsExactAbsentAtObservation(upperId, observed) =>
                await CreateAfterExperimentalAbsenceAsync(
                        intent.Intent,
                        observed,
                        experimentalAuthorization,
                        cancellationToken)
                    .ConfigureAwait(false),
            _ => await MarkUnknownAsync(
                    upperId,
                    observed,
                    reconciliationPhase,
                    RiotDispatchAuditOutcome.Unknown,
                    intent.CreateAttemptId,
                    cancellationToken,
                    intent.ExperimentalAuthorizationId,
                    intent.EligibilityBasis)
                .ConfigureAwait(false)
        };
    }

    private async Task<ExperimentalRiotCreateAuthorization?> PersistFreshExperimentalAuthorizationAsync(
        StoredMovementIntent storedIntent,
        CancellationToken cancellationToken)
    {
        if (ReferenceEquals(
                experimentalAuthorizationSource,
                DenyExperimentalRiotCreateAuthorizationSource.Instance) ||
            storedIntent.Status != "PENDING_RECONCILIATION" ||
            storedIntent.DispatchAuditVersion != 1 ||
            storedIntent.CreateAttemptCount != 0 ||
            storedIntent.CreateAttemptId is not null)
        {
            return null;
        }

        ExperimentalRiotCreateAuthorization? authorization =
            await experimentalAuthorizationSource.GetAuthorizationAsync(
                    storedIntent.Intent.UpperId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (authorization is null)
        {
            return null;
        }

        DateTimeOffset persistedAt = timeProvider.GetUtcNow();
        if (!MatchesExperimentalAuthorization(storedIntent.Intent, authorization, persistedAt))
        {
            return null;
        }

        await store.PersistExperimentalCreateAuthorizationAsync(
            authorization,
            persistedAt,
            cancellationToken).ConfigureAwait(false);
        return authorization;
    }

    private async Task<MovementDispatchResult> CreateAfterExperimentalAbsenceAsync(
        OrderIntent intent,
        RiotOrderObservation confirmedAbsence,
        ExperimentalRiotCreateAuthorization authorization,
        CancellationToken cancellationToken)
    {
        if (!createDispatchPolicy.CreateEnabled)
        {
            return CreateDispatchDisabled(intent);
        }

        DateTimeOffset absenceRecordedAt = timeProvider.GetUtcNow();
        if (!MatchesExperimentalAuthorization(intent, authorization, absenceRecordedAt))
        {
            return await MarkUnknownAsync(
                    intent.UpperId,
                    confirmedAbsence,
                    RiotDispatchAuditPhase.PreCreateReconciliation,
                    RiotDispatchAuditOutcome.Unknown,
                    attemptId: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await store.RecordReconciliationAsync(
            intent.UpperId,
            Audit(
                RiotDispatchAuditPhase.PreCreateReconciliation,
                RiotDispatchAuditOutcome.Unknown,
                absenceRecordedAt,
                confirmedAbsence,
                experimentalAuthorizationId: authorization.AuthorizationId,
                eligibilityBasis: ExperimentalAbsentEligibilityBasis),
            markResultUnknown: true,
            cancellationToken).ConfigureAwait(false);

        DateTimeOffset armedAt = timeProvider.GetUtcNow();
        CreateDispatchAttempt attempt = await store.ArmExperimentalCreateDispatchAsync(
            intent.UpperId,
            ComputeRequestSemanticSha256(intent),
            authorization,
            ExperimentalAbsentEligibilityBasis,
            armedAt,
            cancellationToken).ConfigureAwait(false);
        attempt = attempt with
        {
            ExperimentalAuthorizationId = authorization.AuthorizationId,
            EligibilityBasis = ExperimentalAbsentEligibilityBasis
        };
        return await DispatchCreateAttemptAsync(intent, attempt, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MovementDispatchResult> CreateAfterConfirmedAbsenceAsync(
        OrderIntent intent,
        RiotOrderObservation confirmedAbsence,
        CancellationToken cancellationToken,
        RiotDispatchAuditOutcome absenceOutcome = RiotDispatchAuditOutcome.NotFound,
        string? eligibilityBasis = null)
    {
        if (!createDispatchPolicy.CreateEnabled)
        {
            return CreateDispatchDisabled(intent);
        }

        DateTimeOffset absenceRecordedAt = timeProvider.GetUtcNow();
        await store.RecordReconciliationAsync(
            intent.UpperId,
            Audit(
                RiotDispatchAuditPhase.PreCreateReconciliation,
                absenceOutcome,
                absenceRecordedAt,
                confirmedAbsence,
                eligibilityBasis: eligibilityBasis),
            markResultUnknown: false,
            cancellationToken).ConfigureAwait(false);

        DateTimeOffset armedAt = timeProvider.GetUtcNow();
        CreateDispatchAttempt attempt = await store.ArmCreateDispatchAsync(
            intent.UpperId,
            ComputeRequestSemanticSha256(intent),
            armedAt,
            cancellationToken).ConfigureAwait(false);
        return await DispatchCreateAttemptAsync(intent, attempt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a create because the operational gate is closed. Nothing is written: the audit
    /// chain, the intent status and the at-most-once counters stay exactly as they were, so a
    /// mutation-free rehearsal leaves the intent still eligible for the later authorized create.
    /// </summary>
    private static MovementDispatchResult CreateDispatchDisabled(OrderIntent intent) =>
        new(MovementDispatchOutcome.CreateDispatchDisabled, intent.UpperId, null);

    private async Task<MovementDispatchResult> DispatchCreateAttemptAsync(
        OrderIntent intent,
        CreateDispatchAttempt attempt,
        CancellationToken cancellationToken)
    {
        await store.RecordCreateStartedAsync(
            intent.UpperId,
            attempt,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        RiotOrderObservation accepted;
        try
        {
            accepted = await gateway.CreateAsync(intent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RiotOrderObservation cancelled = new(
                intent.UpperId,
                RiotOrderObservationKind.Unknown,
                null,
                Receipt: new RiotOrderCallReceipt(
                    "CREATE",
                    "CallerCancellation",
                    timeProvider.GetUtcNow(),
                    FailureCategory: "CALLER_CANCELLATION"));
            await RecordCreateResponseAfterDispatchAsync(
                intent.UpperId,
                attempt,
                cancelled,
                RiotDispatchAuditOutcome.Unknown,
                markResultUnknown: true).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            RiotOrderObservation failed = new(
                intent.UpperId,
                RiotOrderObservationKind.Unknown,
                null,
                Receipt: new RiotOrderCallReceipt(
                    "CREATE",
                    "UnhandledGatewayFailure",
                    timeProvider.GetUtcNow(),
                    FailureCategory: "UNHANDLED_GATEWAY_FAILURE"));
            await RecordCreateResponseAfterDispatchAsync(
                intent.UpperId,
                attempt,
                failed,
                RiotDispatchAuditOutcome.Unknown,
                markResultUnknown: true).ConfigureAwait(false);
            throw;
        }

        if (accepted.Kind is RiotOrderObservationKind.Unknown)
        {
            await RecordCreateResponseAfterDispatchAsync(
                intent.UpperId,
                attempt,
                accepted,
                RiotDispatchAuditOutcome.Unknown,
                markResultUnknown: true).ConfigureAwait(false);
            return new MovementDispatchResult(MovementDispatchOutcome.ResultUnknown, intent.UpperId, null);
        }

        await RecordCreateResponseAfterDispatchAsync(
            intent.UpperId,
            attempt,
            accepted,
            RiotDispatchAuditOutcome.Accepted,
            markResultUnknown: false).ConfigureAwait(false);

        // A successful mutation response only proves that RIoT accepted the call. The
        // movement is confirmed exclusively by an independent read using the frozen upperId.
        RiotOrderObservation confirmed;
        try
        {
            confirmed = await gateway.ReconcileByUpperIdAsync(intent.UpperId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RiotOrderObservation cancelled = new(
                intent.UpperId,
                RiotOrderObservationKind.Unknown,
                null,
                Receipt: new RiotOrderCallReceipt(
                    "RECONCILE",
                    "CallerCancellation",
                    timeProvider.GetUtcNow(),
                    FailureCategory: "CALLER_CANCELLATION"));
            await RecordReconciliationAfterDispatchAsync(
                intent.UpperId,
                attempt,
                cancelled,
                RiotDispatchAuditOutcome.Unknown,
                markResultUnknown: true).ConfigureAwait(false);
            throw;
        }
        return confirmed.Kind switch
        {
            RiotOrderObservationKind.Active => await ConfirmAsync(
                    intent,
                    confirmed,
                    RiotDispatchAuditPhase.PostCreateReconciliation,
                    attempt.AttemptId,
                    cancellationToken,
                    attempt.ExperimentalAuthorizationId,
                    attempt.EligibilityBasis)
                .ConfigureAwait(false),
            RiotOrderObservationKind.Terminal => await MarkTerminalAsync(
                    intent,
                    confirmed,
                    RiotDispatchAuditPhase.PostCreateReconciliation,
                    attempt.AttemptId,
                    cancellationToken,
                    attempt.ExperimentalAuthorizationId,
                    attempt.EligibilityBasis)
                .ConfigureAwait(false),
            _ => await MarkUnknownAfterDispatchAsync(intent.UpperId, attempt, confirmed)
                .ConfigureAwait(false)
        };
    }

    private async Task<MovementDispatchResult> ConfirmAsync(
        OrderIntent intent,
        RiotOrderObservation observation,
        RiotDispatchAuditPhase phase,
        string? attemptId,
        CancellationToken cancellationToken,
        string? experimentalAuthorizationId = null,
        string? eligibilityBasis = null)
    {
        if (!MatchesFrozenIntent(intent, observation))
        {
            return await MarkUnknownAsync(
                    observation.UpperId,
                    observation,
                    phase,
                    RiotDispatchAuditOutcome.Unknown,
                    attemptId,
                    cancellationToken,
                    experimentalAuthorizationId,
                    eligibilityBasis)
                .ConfigureAwait(false);
        }
        DispatchAuditWrite audit = Audit(
            phase,
            RiotDispatchAuditOutcome.Confirmed,
            timeProvider.GetUtcNow(),
            observation,
            attemptId,
            experimentalAuthorizationId,
            eligibilityBasis);
        if (phase == RiotDispatchAuditPhase.PostCreateReconciliation)
        {
            await RecordAfterDispatchAsync(token => store.ConfirmAsync(
                observation.UpperId,
                observation.OrderId!,
                audit,
                token)).ConfigureAwait(false);
        }
        else
        {
            await store.ConfirmAsync(
                observation.UpperId,
                observation.OrderId!,
                audit,
                cancellationToken).ConfigureAwait(false);
        }
        return new MovementDispatchResult(
            MovementDispatchOutcome.Confirmed,
            observation.UpperId,
            observation.OrderId);
    }

    private async Task<MovementDispatchResult> MarkTerminalAsync(
        OrderIntent intent,
        RiotOrderObservation observation,
        RiotDispatchAuditPhase phase,
        string? attemptId,
        CancellationToken cancellationToken,
        string? experimentalAuthorizationId = null,
        string? eligibilityBasis = null)
    {
        if (!MatchesFrozenIntent(intent, observation))
        {
            return await MarkUnknownAsync(
                    observation.UpperId,
                    observation,
                    phase,
                    RiotDispatchAuditOutcome.Unknown,
                    attemptId,
                    cancellationToken,
                    experimentalAuthorizationId,
                    eligibilityBasis)
                .ConfigureAwait(false);
        }
        DispatchAuditWrite audit = Audit(
            phase,
            RiotDispatchAuditOutcome.Terminal,
            timeProvider.GetUtcNow(),
            observation,
            attemptId,
            experimentalAuthorizationId,
            eligibilityBasis);
        if (phase == RiotDispatchAuditPhase.PostCreateReconciliation)
        {
            await RecordAfterDispatchAsync(token => store.MarkTerminalReconciliationRequiredAsync(
                observation.UpperId,
                observation.OrderId!,
                audit,
                token)).ConfigureAwait(false);
        }
        else
        {
            await store.MarkTerminalReconciliationRequiredAsync(
                observation.UpperId,
                observation.OrderId!,
                audit,
                cancellationToken).ConfigureAwait(false);
        }
        return new MovementDispatchResult(
            MovementDispatchOutcome.TerminalReconciliationRequired,
            observation.UpperId,
            observation.OrderId);
    }

    private static bool MatchesFrozenIntent(OrderIntent intent, RiotOrderObservation observation) =>
        string.Equals(observation.UpperId, intent.UpperId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(observation.OrderId) &&
        string.Equals(observation.VehicleKey, intent.VehicleKey, StringComparison.Ordinal) &&
        observation.MapId == intent.MapId &&
        observation.DestinationStationId == intent.DestinationStationId;

    private static bool MatchesExperimentalAuthorization(
        OrderIntent intent,
        ExperimentalRiotCreateAuthorization authorization,
        DateTimeOffset evaluatedAt) =>
        !string.IsNullOrWhiteSpace(authorization.AuthorizationId) &&
        authorization.AuthorizationVersion == 1 &&
        string.Equals(authorization.UpperId, intent.UpperId, StringComparison.Ordinal) &&
        string.Equals(authorization.DemandId, intent.DemandId, StringComparison.Ordinal) &&
        string.Equals(authorization.MovementLegId, intent.MovementLegId, StringComparison.Ordinal) &&
        authorization.AgvLifecycleGeneration == intent.AgvLifecycleGeneration &&
        authorization.DispatchGeneration == intent.DispatchGeneration &&
        authorization.ExpiresAt > evaluatedAt;

    private static bool IsExactAbsentAtObservation(
        string expectedUpperId,
        RiotOrderObservation observation) =>
        observation.Kind == RiotOrderObservationKind.Unknown &&
        string.Equals(observation.UpperId, expectedUpperId, StringComparison.Ordinal) &&
        observation.OrderId is null &&
        observation.Receipt is
        {
            Operation: "RECONCILE",
            Classification: "AbsentAtObservation",
            HttpStatusCode: null,
            BusinessCode: null,
            ResultPresent: false,
            FailureCategory: null
        };

    private async Task<MovementDispatchResult> MarkUnknownAsync(
        string upperId,
        RiotOrderObservation observation,
        RiotDispatchAuditPhase phase,
        RiotDispatchAuditOutcome outcome,
        string? attemptId,
        CancellationToken cancellationToken,
        string? experimentalAuthorizationId = null,
        string? eligibilityBasis = null)
    {
        DispatchAuditWrite audit = Audit(
            phase,
            outcome,
            timeProvider.GetUtcNow(),
            observation,
            attemptId,
            experimentalAuthorizationId,
            eligibilityBasis);
        if (phase == RiotDispatchAuditPhase.PostCreateReconciliation)
        {
            await RecordAfterDispatchAsync(token => store.RecordReconciliationAsync(
                upperId,
                audit,
                markResultUnknown: true,
                token)).ConfigureAwait(false);
        }
        else
        {
            await store.RecordReconciliationAsync(
                upperId,
                audit,
                markResultUnknown: true,
                cancellationToken).ConfigureAwait(false);
        }
        return new MovementDispatchResult(MovementDispatchOutcome.ResultUnknown, upperId, null);
    }

    private async Task<MovementDispatchResult> MarkUnknownAfterDispatchAsync(
        string upperId,
        CreateDispatchAttempt attempt,
        RiotOrderObservation observation)
    {
        await RecordReconciliationAfterDispatchAsync(
            upperId,
            attempt,
            observation,
            RiotDispatchAuditOutcome.Unknown,
            markResultUnknown: true).ConfigureAwait(false);
        return new MovementDispatchResult(MovementDispatchOutcome.ResultUnknown, upperId, null);
    }

    private Task RecordCreateResponseAfterDispatchAsync(
        string upperId,
        CreateDispatchAttempt attempt,
        RiotOrderObservation observation,
        RiotDispatchAuditOutcome outcome,
        bool markResultUnknown) =>
        RecordAfterDispatchAsync(token => store.RecordCreateResponseAsync(
            upperId,
            attempt,
            Audit(
                RiotDispatchAuditPhase.CreateResponse,
                outcome,
                timeProvider.GetUtcNow(),
                observation,
                attempt.AttemptId,
                attempt.ExperimentalAuthorizationId,
                attempt.EligibilityBasis),
            markResultUnknown,
            token));

    private Task RecordReconciliationAfterDispatchAsync(
        string upperId,
        CreateDispatchAttempt attempt,
        RiotOrderObservation observation,
        RiotDispatchAuditOutcome outcome,
        bool markResultUnknown) =>
        RecordAfterDispatchAsync(token => store.RecordReconciliationAsync(
            upperId,
            Audit(
                RiotDispatchAuditPhase.PostCreateReconciliation,
                outcome,
                timeProvider.GetUtcNow(),
                observation,
                attempt.AttemptId,
                attempt.ExperimentalAuthorizationId,
                attempt.EligibilityBasis),
            markResultUnknown,
            token));

    private static DispatchAuditWrite Audit(
        RiotDispatchAuditPhase phase,
        RiotDispatchAuditOutcome outcome,
        DateTimeOffset occurredAt,
        RiotOrderObservation observation,
        string? attemptId = null,
        string? experimentalAuthorizationId = null,
        string? eligibilityBasis = null) =>
        new(
            phase,
            outcome,
            occurredAt,
            attemptId,
            ReturnedOrderId: observation.OrderId,
            Receipt: observation.Receipt ?? new RiotOrderCallReceipt(
                phase is RiotDispatchAuditPhase.CreateResponse ? "CREATE" : "RECONCILE",
                observation.Kind.ToString(),
                occurredAt,
                ResultPresent: observation.OrderId is not null),
            ExperimentalAuthorizationId: experimentalAuthorizationId,
            EligibilityBasis: eligibilityBasis);

    private static string ComputeRequestSemanticSha256(OrderIntent intent)
    {
        byte[] semanticRequest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            endpoint = "byDefaultMissions",
            upperId = intent.UpperId,
            appointVehicleKey = intent.VehicleKey,
            isAppointEnable = 1,
            lockStatus = 0,
            orderName = intent.UpperId,
            mission = new
            {
                type = "move",
                mapId = intent.MapId,
                destination = intent.DestinationStationId
            }
        });
        return Convert.ToHexString(SHA256.HashData(semanticRequest)).ToLowerInvariant();
    }

    private static async Task RecordAfterDispatchAsync(Func<CancellationToken, Task> write)
    {
        using CancellationTokenSource evidenceWrite = new(EvidenceWriteTimeout);
        await write(evidenceWrite.Token).ConfigureAwait(false);
    }

    private sealed class DenyExperimentalRiotCreateAuthorizationSource : IExperimentalRiotCreateAuthorizationSource
    {
        public static DenyExperimentalRiotCreateAuthorizationSource Instance { get; } = new();

        public Task<ExperimentalRiotCreateAuthorization?> GetAuthorizationAsync(
            string upperId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ExperimentalRiotCreateAuthorization?>(null);
        }
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

    public async Task<JourneyIntakeResult> AcceptAndDispatchToPickupAsync(
        AcceptedDemandSnapshot prevalidatedCandidate,
        OrderIntent pickupIntent,
        JourneyExecutionPlan journey,
        CancellationToken cancellationToken)
    {
        DemandIntakeOutcome intakeOutcome = await intake.AcceptJourneyAsync(
            prevalidatedCandidate,
            pickupIntent,
            journey,
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

    public async Task<JourneyIntakeResult> AcceptAndDispatchToPickupAsync(
        AcceptedDemandSnapshot prevalidatedCandidate,
        OrderIntent pickupIntent,
        JourneyExecutionPlan journey,
        Func<CancellationToken, Task<bool>> finalAdmissionGate,
        CancellationToken cancellationToken)
    {
        DemandIntakeOutcome intakeOutcome = await intake.AcceptJourneyAsync(
            prevalidatedCandidate,
            pickupIntent,
            journey,
            finalAdmissionGate,
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
