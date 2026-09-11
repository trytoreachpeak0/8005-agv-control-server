using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

public sealed class WireToGateStore(ControlServerDbContext dbContext) : IJourneyAcceptanceStore, IMovementIntentStore
{
    public async Task<long> GetNextSessionGenerationAsync(string agvId, CancellationToken cancellationToken)
    {
        long current = await dbContext.SessionRecoveries
            .Where(row => row.AgvId == agvId)
            .Select(row => (long?)row.SessionGeneration)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        return checked(current + 1);
    }

    public async Task BeginSessionRecoveryAsync(SessionIdentity identity, CancellationToken cancellationToken)
    {
        ValidateProtocolIdentity(identity);
        SessionRecoveryRow? row = await dbContext.SessionRecoveries
            .SingleOrDefaultAsync(item => item.AgvId == identity.AgvId, cancellationToken)
            .ConfigureAwait(false);
        if (row is not null && identity.SessionGeneration <= row.SessionGeneration)
        {
            if (identity.SessionGeneration < row.SessionGeneration)
            {
                throw new StaleSessionGenerationException(
                    $"Session generation {identity.SessionGeneration} is older than {row.SessionGeneration}.");
            }

            return;
        }

        if (row is null)
        {
            row = new SessionRecoveryRow
            {
                AgvId = identity.AgvId,
                ProtocolCommit = identity.ProtocolCommit,
                ManifestSha256 = identity.ManifestSha256,
                ProfileId = identity.ProfileId
            };
            dbContext.SessionRecoveries.Add(row);
        }

        row.SessionGeneration = identity.SessionGeneration;
        row.ProtocolCommit = identity.ProtocolCommit;
        row.ManifestSha256 = identity.ManifestSha256;
        row.ProfileId = identity.ProfileId;
        row.ProtocolVersion = identity.ProtocolVersion;
        row.CapabilityRevision = null;
        row.CapabilityHash = null;
        row.SafetyRevision = null;
        row.SafetyHash = null;
        row.DepartureSafe = null;
        row.RecoveryReportId = null;
        row.PendingAttemptIdsJson = "[]";
        row.PendingResultIdsJson = "[]";
        row.Readiness = SessionReadiness.RecoveryRequired;
        row.ReasonCode = "HANDSHAKE_INCOMPLETE";
        row.UpdatedAt = DateTimeOffset.UtcNow;

        // ADR-cross-0055: a disconnect voids this round's station departure deadline, and the
        // clock is refilled once the recovery handshake and the projection reconciliation are
        // done. It belongs with the facts above for the same reason they are cleared -- a wall
        // clock that kept running while the vehicle was gone is not a fact about this session.
        // Left running, a vehicle offline for longer than the window came back and was timed out
        // on its first pass, cancelling demands nobody ever had the chance to load.
        //
        // Only the deadline is dropped, not the stop: refilling it is the AwaitingSublot branch's
        // job, and that branch runs behind the readiness gate -- which is exactly "after the
        // handshake and the reconciliation".
        JourneyRuntimeRow[] active = await dbContext.JourneyRuntimes
            .Where(item => item.AgvId == identity.AgvId && item.Stage != JourneyRuntimeStage.Completed)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (JourneyRuntimeRow journey in active)
        {
            JourneyStopRow? current = await dbContext.JourneyStops.SingleOrDefaultAsync(
                item => item.JourneyId == journey.JourneyId && item.Sequence == journey.CurrentStopSequence,
                cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                current.SublotWaitStartedAt = null;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyCapabilitySnapshotAsync(
        string agvId, long sessionGeneration, long revision, string contentHash,
        CancellationToken cancellationToken)
    {
        SessionRecoveryRow row = await GetCurrentSessionAsync(agvId, sessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        ApplyRevision(row.CapabilityRevision, row.CapabilityHash, revision, contentHash, "capability");
        row.CapabilityRevision = revision;
        row.CapabilityHash = contentHash;
        row.Readiness = SessionReadiness.RecoveryRequired;
        row.ReasonCode = "HANDSHAKE_INCOMPLETE";
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplySafetySnapshotAsync(
        string agvId, long sessionGeneration, long revision, bool departureSafe, string contentHash,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? reasonCodes = null,
        bool? unknownPresent = null)
    {
        SessionRecoveryRow row = await GetCurrentSessionAsync(agvId, sessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        ApplyRevision(row.SafetyRevision, row.SafetyHash, revision, contentHash, "safety");
        row.SafetyRevision = revision;
        row.SafetyHash = contentHash;
        row.DepartureSafe = departureSafe;
        row.SafetyReasonCodesJson = reasonCodes is null ? null : SerializeSorted(reasonCodes);
        row.SafetyUnknownPresent = unknownPresent;
        row.Readiness = SessionReadiness.RecoveryRequired;
        row.ReasonCode = "HANDSHAKE_INCOMPLETE";
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyRecoveryReportAsync(
        string agvId, long sessionGeneration, string reportId, long forcedRecoveryGeneration,
        string? unsettledSlotOperationAttemptId,
        string? provenRecoveryCheckpoint,
        IReadOnlyCollection<int> activeUnlockSlots,
        IReadOnlyCollection<string> pendingAttemptIds,
        IReadOnlyCollection<string> pendingResultIds,
        CancellationToken cancellationToken)
    {
        SessionRecoveryRow row = await GetCurrentSessionAsync(agvId, sessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        long currentGeneration = await dbContext.VehicleRecoveryGenerations
            .Where(item => item.AgvId == agvId)
            .Select(item => (long?)item.ForcedRecoveryGeneration)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? row.ForcedRecoveryGeneration;
        row.RecoveryReportId = reportId;
        row.ForcedRecoveryGeneration = currentGeneration;
        row.ReportedForcedRecoveryGeneration = forcedRecoveryGeneration;
        row.UnsettledSlotOperationAttemptId = unsettledSlotOperationAttemptId;
        row.ProvenRecoveryCheckpoint = provenRecoveryCheckpoint;
        row.ActiveUnlockSlotsJson = JsonSerializer.Serialize(NormalizeSlots(activeUnlockSlots));
        row.PendingAttemptIdsJson = SerializeSorted(pendingAttemptIds);
        row.PendingResultIdsJson = SerializeSorted(pendingResultIds);
        row.Readiness = SessionReadiness.RecoveryRequired;
        row.ReasonCode = "RECOVERY_RECONCILIATION_PENDING";
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionReadinessDecision> DecideReadinessAsync(
        string agvId, long sessionGeneration, CancellationToken cancellationToken)
    {
        SessionRecoveryRow row = await GetCurrentSessionAsync(agvId, sessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        bool noPendingFacts = !await HasUnreconciledReportedFactsAsync(row, cancellationToken)
            .ConfigureAwait(false);
        bool departureUsable = row.DepartureSafe == true ||
                               await IsUnsafetyExplainedByOwnCommandAsync(row, cancellationToken)
                                   .ConfigureAwait(false);
        // The server's own verdict counts too. It decides whether an OperationResult completed
        // safely and marks the operation RecoveryRequired when it did not -- and that verdict used
        // to reach nothing: readiness was computed purely from what the vehicle reported, so a
        // vehicle whose load needed recovery still reported READY. The onboard shows its recovery
        // entry only while the session says RECOVERY_REQUIRED, so the operator on a blocked vehicle
        // could not open a recovery session at all, for any vector.
        //
        // This can only move a session from Ready to RecoveryRequired, never the other way.
        bool operationNeedsRecovery = await dbContext.StationOperations
            .Join(dbContext.JourneyDemands,
                operation => operation.DemandId,
                demand => demand.DemandId,
                (operation, demand) => new { operation, demand })
            .Join(dbContext.JourneyRuntimes,
                pair => pair.demand.JourneyId,
                runtime => runtime.JourneyId,
                (pair, runtime) => new { pair.operation, runtime })
            .AnyAsync(
                pair => pair.runtime.AgvId == agvId &&
                        pair.operation.Status == StationOperationStatus.RecoveryRequired,
                cancellationToken).ConfigureAwait(false);
        bool ready = row.CapabilityRevision is not null && row.SafetyRevision is not null &&
                     row.RecoveryReportId is not null && departureUsable && noPendingFacts &&
                     !operationNeedsRecovery &&
                     row.ReportedForcedRecoveryGeneration == row.ForcedRecoveryGeneration;
        row.Readiness = ready ? SessionReadiness.Ready : SessionReadiness.RecoveryRequired;
        row.ReasonCode = ready
            ? "READY"
            : GetRecoveryReason(row, noPendingFacts, departureUsable, operationNeedsRecovery);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new SessionReadinessDecision(row.Readiness, row.ReasonCode);
    }

    public async Task<SessionReadinessDecision> GetReadinessAsync(string agvId, CancellationToken cancellationToken)
    {
        SessionRecoveryRow row = await dbContext.SessionRecoveries.SingleAsync(item => item.AgvId == agvId, cancellationToken)
            .ConfigureAwait(false);
        return new SessionReadinessDecision(row.Readiness, row.ReasonCode);
    }

    public async Task<long> GetForcedRecoveryGenerationAsync(
        string agvId, long sessionGeneration, CancellationToken cancellationToken)
    {
        SessionRecoveryRow row = await GetCurrentSessionAsync(agvId, sessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        return row.ForcedRecoveryGeneration;
    }

    public Task<long> GetOperationForcedRecoveryGenerationAsync(
        string slotOperationAttemptId,
        CancellationToken cancellationToken) =>
        dbContext.StationOperations
            .Where(row => row.SlotOperationAttemptId == slotOperationAttemptId)
            .Select(row => row.ForcedRecoveryGeneration)
            .SingleAsync(cancellationToken);

    public Task AcceptWithOrderIntentAsync(
        AcceptedDemandSnapshot snapshot, OrderIntent orderIntent, CancellationToken cancellationToken) =>
        AcceptCoreAsync(snapshot, orderIntent, journey: null, cancellationToken);

    public Task AcceptWithOrderIntentAsync(
        AcceptedDemandSnapshot snapshot,
        OrderIntent orderIntent,
        JourneyExecutionPlan journey,
        CancellationToken cancellationToken) =>
        AcceptCoreAsync(snapshot, orderIntent, journey, cancellationToken);

    private async Task AcceptCoreAsync(
        AcceptedDemandSnapshot snapshot,
        OrderIntent orderIntent,
        JourneyExecutionPlan? journey,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(snapshot.DemandId, orderIntent.DemandId, StringComparison.Ordinal))
        {
            throw new BusinessIdentityConflictException("Order intent demand does not match accepted demand.");
        }

        AcceptedDemandRow? existing = await dbContext.AcceptedDemands
            .SingleOrDefaultAsync(row => row.DemandId == snapshot.DemandId ||
                                         row.TransportDemandKey == snapshot.TransportDemandKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            bool same = existing.DemandId == snapshot.DemandId &&
                        existing.SeriesId == snapshot.SeriesId &&
                        existing.TransportDemandKey == snapshot.TransportDemandKey &&
                        existing.WorkType == snapshot.WorkType &&
                        existing.Sublot == snapshot.Sublot &&
                        existing.Generation == snapshot.Generation &&
                        existing.DemandRevision == snapshot.DemandRevision &&
                        existing.HistoryEpoch == snapshot.HistoryEpoch &&
                        existing.CreatedAt == snapshot.CreatedAt &&
                        existing.ValueObservedAt == snapshot.ValueObservedAt &&
                        existing.ValuePollTraceId == snapshot.ValuePollTraceId &&
                        existing.ValueProjectionCommitId == snapshot.ValueProjectionCommitId &&
                        existing.LiveMesFieldsJson == JsonSerializer.Serialize(snapshot.LiveMesFields);
            if (!same)
            {
                throw new BusinessIdentityConflictException("DemandId or TransportDemandKey is already bound to different content.");
            }

            OrderIntentRow? replayIntent = await dbContext.OrderIntents
                .SingleOrDefaultAsync(row => row.MovementLegId == orderIntent.MovementLegId, cancellationToken)
                .ConfigureAwait(false);
            if (replayIntent is null || !Matches(replayIntent, orderIntent))
            {
                throw new BusinessIdentityConflictException("Accepted demand replay does not match its original order intent.");
            }

            if (journey is not null)
            {
                JourneyRuntimeRow? runtime = await dbContext.JourneyRuntimes
                    .SingleOrDefaultAsync(row => row.JourneyId == journey.JourneyId, cancellationToken)
                    .ConfigureAwait(false);
                JourneyStopRow[] stops = await dbContext.JourneyStops
                    .Where(row => row.JourneyId == journey.JourneyId)
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                JourneyDemandRow[] demands = await dbContext.JourneyDemands
                    .Where(row => row.JourneyId == journey.JourneyId)
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                if (runtime is null || !Matches(runtime, stops, demands, journey))
                {
                    throw new BusinessIdentityConflictException(
                        "Accepted demand replay does not match its persisted journey runtime.");
                }
            }

            return;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        VehicleDispatchLeaseRow? activeLease = await dbContext.VehicleDispatchLeases
            .SingleOrDefaultAsync(
                row => row.VehicleKey == orderIntent.VehicleKey && row.ReleasedAt == null,
                cancellationToken)
            .ConfigureAwait(false);
        if (activeLease is not null)
        {
            throw new BusinessIdentityConflictException(
                $"Vehicle '{orderIntent.VehicleKey}' is already bound to unresolved journey '{activeLease.JourneyId}'.");
        }

        dbContext.AcceptedDemands.Add(new AcceptedDemandRow
        {
            DemandId = snapshot.DemandId,
            SeriesId = snapshot.SeriesId,
            TransportDemandKey = snapshot.TransportDemandKey,
            WorkType = snapshot.WorkType,
            Sublot = snapshot.Sublot,
            Generation = snapshot.Generation,
            DemandRevision = snapshot.DemandRevision,
            HistoryEpoch = snapshot.HistoryEpoch,
            CatalogRevision = snapshot.CatalogRevision,
            CreatedAt = snapshot.CreatedAt,
            ValueObservedAt = snapshot.ValueObservedAt,
            ValuePollTraceId = snapshot.ValuePollTraceId,
            ValueProjectionCommitId = snapshot.ValueProjectionCommitId,
            LiveMesFieldsJson = JsonSerializer.Serialize(snapshot.LiveMesFields),
            AcceptedAt = snapshot.AcceptedAt,
            Status = DemandExecutionStatus.Accepted
        });
        dbContext.VehicleDispatchLeases.Add(new VehicleDispatchLeaseRow
        {
            JourneyId = journey?.JourneyId ?? snapshot.DemandId,
            VehicleKey = orderIntent.VehicleKey,
            AcquiredAt = snapshot.AcceptedAt
        });
        dbContext.OrderIntents.Add(ToRow(orderIntent));
        if (journey is not null)
        {
            JourneyRuntimeRow runtimeRow = ToRuntimeRow(journey);
            await SeedSnapshotRevisionsAsync(runtimeRow, cancellationToken).ConfigureAwait(false);
            dbContext.JourneyRuntimes.Add(runtimeRow);
            dbContext.JourneyStops.AddRange(ToStopRows(runtimeRow, journey));
            dbContext.JourneyDemands.AddRange(ToDemandRows(journey));
            JourneyBacklogRow? backlog = await dbContext.JourneyBacklog
                .SingleOrDefaultAsync(row => row.DemandId == snapshot.DemandId, cancellationToken)
                .ConfigureAwait(false);
            if (backlog is not null)
            {
                backlog.AcceptedAt = snapshot.AcceptedAt;
                backlog.ReasonCode = "ACCEPTED";
                backlog.LastSeenAt = snapshot.AcceptedAt;
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes one more demand onto a journey that is already under way (ADR-cross-0057). No lease and
    /// no order intent: the journey already holds the vehicle, and the leg that reaches a stop is
    /// authorized when the vehicle leaves the previous one.
    /// </summary>
    /// <remarks>
    /// A replay is a no-op rather than a conflict, on the same terms as an accepted demand's replay:
    /// the demand is already on this journey, at this stop, with this reservation. Anything else is
    /// a conflict -- the same demand cannot ride two journeys, and a reservation that moved would
    /// mean slots were promised twice.
    /// </remarks>
    public async Task JoinJourneyAsync(
        AcceptedDemandSnapshot snapshot,
        string journeyId,
        JourneyStopPlan? appendedStop,
        JourneyDemandPlan demand,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(snapshot.DemandId, demand.DemandId, StringComparison.Ordinal))
        {
            throw new BusinessIdentityConflictException("Journey demand does not match the accepted demand.");
        }

        JourneyRuntimeRow runtime = await dbContext.JourneyRuntimes
            .SingleAsync(row => row.JourneyId == journeyId, cancellationToken).ConfigureAwait(false);
        JourneyDemandRow? existingMembership = await dbContext.JourneyDemands
            .SingleOrDefaultAsync(row => row.DemandId == demand.DemandId, cancellationToken)
            .ConfigureAwait(false);
        if (existingMembership is not null)
        {
            if (existingMembership.JourneyId != journeyId || !Matches(existingMembership, demand))
            {
                throw new BusinessIdentityConflictException(
                    "Demand is already bound to a different journey or a different reservation.");
            }
            return;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        dbContext.AcceptedDemands.Add(new AcceptedDemandRow
        {
            DemandId = snapshot.DemandId,
            SeriesId = snapshot.SeriesId,
            TransportDemandKey = snapshot.TransportDemandKey,
            WorkType = snapshot.WorkType,
            Sublot = snapshot.Sublot,
            Generation = snapshot.Generation,
            DemandRevision = snapshot.DemandRevision,
            HistoryEpoch = snapshot.HistoryEpoch,
            CatalogRevision = snapshot.CatalogRevision,
            CreatedAt = snapshot.CreatedAt,
            ValueObservedAt = snapshot.ValueObservedAt,
            ValuePollTraceId = snapshot.ValuePollTraceId,
            ValueProjectionCommitId = snapshot.ValueProjectionCommitId,
            LiveMesFieldsJson = JsonSerializer.Serialize(snapshot.LiveMesFields),
            AcceptedAt = snapshot.AcceptedAt,
            Status = DemandExecutionStatus.Accepted
        });
        if (appendedStop is not null)
        {
            dbContext.JourneyStops.Add(new JourneyStopRow
            {
                JourneyId = journeyId,
                Sequence = appendedStop.Sequence,
                Role = appendedStop.Role,
                StationId = appendedStop.StationId,
                StationRiotId = appendedStop.StationRiotId,
                RouteEvidenceId = appendedStop.RouteEvidenceId,
                MovementLegId = appendedStop.MovementLegId,
                UpperId = appendedStop.UpperId,
                LegType = appendedStop.LegType,
                State = JourneyStopState.Planned,
                VehicleBusinessRevision = 0,
                WorklistRevision = 0,
                PlanRevision = 0,
                VehicleBusinessMessageId = StopId(journeyId, appendedStop.Sequence, "vehicle-state"),
                PlanMessageId = StopId(journeyId, appendedStop.Sequence, "plan"),
                PreDepartureSafetyCheckMessageId = StopId(journeyId, appendedStop.Sequence, "safety-request"),
                PreDepartureSafetyCheckId = StopId(journeyId, appendedStop.Sequence, "safety-check"),
                LoadRound = 0,
                CreatedAt = snapshot.AcceptedAt,
                UpdatedAt = snapshot.AcceptedAt
            });
        }
        dbContext.JourneyDemands.Add(new JourneyDemandRow
        {
            JourneyId = journeyId,
            DemandId = demand.DemandId,
            StopSequence = demand.StopSequence,
            ExpectedBasketCount = demand.ExpectedBasketCount,
            TargetSlotsJson = JsonSerializer.Serialize(demand.TargetSlots),
            LoadCommandMessageId = JourneyDemandId(journeyId, demand.DemandId, "load-command"),
            LoadSlotOperationAttemptId = JourneyDemandId(journeyId, demand.DemandId, "load-attempt"),
            UnloadCommandMessageId = JourneyDemandId(journeyId, demand.DemandId, "unload-command"),
            UnloadSlotOperationAttemptId = JourneyDemandId(journeyId, demand.DemandId, "unload-attempt"),
            State = JourneyDemandState.Planned,
            CreatedAt = snapshot.AcceptedAt
        });
        JourneyBacklogRow? backlog = await dbContext.JourneyBacklog
            .SingleOrDefaultAsync(row => row.DemandId == snapshot.DemandId, cancellationToken)
            .ConfigureAwait(false);
        if (backlog is not null)
        {
            backlog.AcceptedAt = snapshot.AcceptedAt;
            backlog.ReasonCode = "ACCEPTED";
            backlog.LastSeenAt = snapshot.AcceptedAt;
        }
        runtime.UpdatedAt = snapshot.AcceptedAt;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredMovementIntent?> GetByUpperIdAsync(
        string upperId, CancellationToken cancellationToken)
    {
        OrderIntentRow? row = await dbContext.OrderIntents
            .SingleOrDefaultAsync(item => item.UpperId == upperId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        ExperimentalRiotCreateAuthorizationRow? authorization = await dbContext.ExperimentalRiotCreateAuthorizations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => row.CreateAttemptId != null &&
                        item.UpperId == upperId &&
                        item.ConsumedByAttemptId == row.CreateAttemptId,
                cancellationToken)
            .ConfigureAwait(false);
        return new StoredMovementIntent(
            ToDomain(row),
            row.Status,
            row.OrderId,
            row.DispatchAuditVersion,
            row.CreateAttemptId,
            row.CreateAttemptCount,
            authorization?.AuthorizationId,
            authorization is null ? null : "EXPERIMENTAL_ABSENT_AT_OBSERVATION");
    }

    public async Task PersistExperimentalCreateAuthorizationAsync(
        ExperimentalRiotCreateAuthorization authorization,
        DateTimeOffset persistedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ValidateExperimentalAuthorizationShape(authorization, persistedAt);

        OrderIntentRow row = await dbContext.OrderIntents
            .SingleAsync(item => item.UpperId == authorization.UpperId, cancellationToken)
            .ConfigureAwait(false);
        AcceptedDemandRow demand = await dbContext.AcceptedDemands
            .AsNoTracking()
            .SingleAsync(item => item.DemandId == row.DemandId, cancellationToken)
            .ConfigureAwait(false);
        bool hasAuditHistory = await dbContext.RiotDispatchAuditEvents
            .AsNoTracking()
            .AnyAsync(item => item.MovementLegId == row.MovementLegId, cancellationToken)
            .ConfigureAwait(false);
        if (!MatchesExperimentalAuthorization(row, authorization) ||
            !IsFreshExperimentalIntent(row, demand, hasAuditHistory) ||
            row.ExperimentalCreateAuthorizationId is not null &&
            row.ExperimentalCreateAuthorizationId != authorization.AuthorizationId)
        {
            throw new BusinessIdentityConflictException(
                "The experimental RIoT create authorization requires one exact fresh audit-versioned intent.");
        }

        ExperimentalRiotCreateAuthorizationRow? byAuthorization = await dbContext.ExperimentalRiotCreateAuthorizations
            .SingleOrDefaultAsync(
                item => item.AuthorizationId == authorization.AuthorizationId,
                cancellationToken)
            .ConfigureAwait(false);
        ExperimentalRiotCreateAuthorizationRow? byUpperId = await dbContext.ExperimentalRiotCreateAuthorizations
            .SingleOrDefaultAsync(
                item => item.UpperId == authorization.UpperId,
                cancellationToken)
            .ConfigureAwait(false);
        ExperimentalRiotCreateAuthorizationRow? existing = byAuthorization ?? byUpperId;
        if (existing is not null)
        {
            if ((byAuthorization is not null && byUpperId is not null &&
                 byAuthorization.AuthorizationId != byUpperId.AuthorizationId) ||
                !MatchesExperimentalAuthorization(existing, authorization) ||
                existing.ConsumedAt is not null ||
                existing.ConsumedByAttemptId is not null ||
                row.ExperimentalCreateAuthorizationId != authorization.AuthorizationId ||
                !IsFreshExperimentalIntent(row, demand, hasAuditHistory))
            {
                throw new BusinessIdentityConflictException(
                    "An experimental RIoT create authorization cannot be rebound, revived, or reused.");
            }
            return;
        }

        if (row.ExperimentalCreateAuthorizationId is not null ||
            !IsFreshExperimentalIntent(row, demand, hasAuditHistory))
        {
            throw new BusinessIdentityConflictException(
                "The experimental RIoT create authorization requires one exact fresh audit-versioned intent.");
        }

        row.ExperimentalCreateAuthorizationId = authorization.AuthorizationId;
        dbContext.ExperimentalRiotCreateAuthorizations.Add(new ExperimentalRiotCreateAuthorizationRow
        {
            AuthorizationId = authorization.AuthorizationId,
            AuthorizationVersion = authorization.AuthorizationVersion,
            UpperId = authorization.UpperId,
            DemandId = authorization.DemandId,
            MovementLegId = authorization.MovementLegId,
            AgvLifecycleGeneration = authorization.AgvLifecycleGeneration,
            DispatchGeneration = authorization.DispatchGeneration,
            ExpiresAt = authorization.ExpiresAt,
            PersistedAt = persistedAt
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            await RequireExactCommittedFreshAuthorizationAsync(authorization, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DbUpdateException error) when (error.InnerException is SqliteException
        {
            SqliteErrorCode: 19,
            SqliteExtendedErrorCode: 1555 or 2067
        })
        {
            await RequireExactCommittedFreshAuthorizationAsync(authorization, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task RecordReconciliationAsync(
        string upperId,
        DispatchAuditWrite audit,
        bool markResultUnknown,
        CancellationToken cancellationToken)
    {
        OrderIntentRow row = await dbContext.OrderIntents
            .SingleAsync(item => item.UpperId == upperId, cancellationToken).ConfigureAwait(false);
        if (markResultUnknown && row.Status != "CONFIRMED")
        {
            row.Status = "RESULT_UNKNOWN";
        }
        ApplyReconciliationSummary(row, audit);
        await AppendAuditEventAsync(row, audit, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CreateDispatchAttempt> ArmCreateDispatchAsync(
        string upperId,
        string requestSemanticSha256,
        DateTimeOffset armedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestSemanticSha256);
        OrderIntentRow row = await dbContext.OrderIntents
            .SingleAsync(item => item.UpperId == upperId, cancellationToken).ConfigureAwait(false);
        if (row.Status != "PENDING_RECONCILIATION" ||
            row.DispatchAuditVersion != 1 ||
            row.CreateAttemptCount != 0 ||
            row.CreateAttemptId is not null)
        {
            throw new BusinessIdentityConflictException(
                "A RIoT create call is only allowed once from an audit-versioned initial reconciled-not-found state.");
        }

        CreateDispatchAttempt attempt = new(
            Guid.NewGuid().ToString("D"),
            1,
            requestSemanticSha256,
            armedAt);
        row.Status = "CREATE_ATTEMPTED";
        row.CreateAttemptId = attempt.AttemptId;
        row.CreateAttemptCount = attempt.AttemptNumber;
        row.CreateDispatchArmedAt = armedAt;
        row.LastCreateOutcome = "DispatchArmed";
        row.LastCreateOutcomeAt = armedAt;
        await AppendAuditEventAsync(
            row,
            new DispatchAuditWrite(
                RiotDispatchAuditPhase.CreateDispatch,
                RiotDispatchAuditOutcome.Armed,
                armedAt,
                attempt.AttemptId,
                requestSemanticSha256),
            cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return attempt;
    }

    public async Task<CreateDispatchAttempt> ArmExperimentalCreateDispatchAsync(
        string upperId,
        string requestSemanticSha256,
        ExperimentalRiotCreateAuthorization authorization,
        string eligibilityBasis,
        DateTimeOffset armedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestSemanticSha256);
        ArgumentNullException.ThrowIfNull(authorization);
        if (eligibilityBasis != "EXPERIMENTAL_ABSENT_AT_OBSERVATION")
        {
            throw new BusinessIdentityConflictException("The experimental create eligibility basis is invalid.");
        }
        ValidateExperimentalAuthorizationShape(authorization, armedAt);

        OrderIntentRow row = await dbContext.OrderIntents
            .SingleAsync(item => item.UpperId == upperId, cancellationToken)
            .ConfigureAwait(false);
        ExperimentalRiotCreateAuthorizationRow permit = await dbContext.ExperimentalRiotCreateAuthorizations
            .SingleAsync(
                item => item.AuthorizationId == authorization.AuthorizationId && item.UpperId == upperId,
                cancellationToken)
            .ConfigureAwait(false);
        RiotDispatchAuditEventRow[] priorEvents = await dbContext.RiotDispatchAuditEvents
            .Where(item => item.MovementLegId == row.MovementLegId)
            .OrderBy(item => item.Sequence)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        bool exactPreRead = priorEvents.Length == 1 &&
                            priorEvents[0].Phase == "PRE_CREATE_RECONCILIATION" &&
                            priorEvents[0].Outcome == "UNKNOWN" &&
                            priorEvents[0].AttemptId is null &&
                            priorEvents[0].ReceiptOperation == "RECONCILE" &&
                            priorEvents[0].ReceiptClassification == "AbsentAtObservation" &&
                            priorEvents[0].HttpStatusCode is null &&
                            priorEvents[0].BusinessCode is null &&
                            priorEvents[0].ResultPresent == false &&
                            priorEvents[0].ReturnedOrderId is null &&
                            priorEvents[0].FailureCategory is null &&
                            priorEvents[0].ExperimentalAuthorizationId == authorization.AuthorizationId &&
                            priorEvents[0].EligibilityBasis == eligibilityBasis;
        if (row.Status != "RESULT_UNKNOWN" ||
            row.OrderId is not null ||
            row.DispatchAuditVersion != 1 ||
            row.DispatchAuditSequence != 1 ||
            row.ExperimentalCreateAuthorizationId != authorization.AuthorizationId ||
            row.CreateAttemptCount != 0 ||
            row.CreateAttemptId is not null ||
            !MatchesExperimentalAuthorization(row, authorization) ||
            !MatchesExperimentalAuthorization(permit, authorization) ||
            permit.ConsumedAt is not null ||
            permit.ConsumedByAttemptId is not null ||
            !exactPreRead)
        {
            throw new BusinessIdentityConflictException(
                "The experimental RIoT create authorization is not eligible for one audited dispatch.");
        }

        CreateDispatchAttempt attempt = new(
            Guid.NewGuid().ToString("D"),
            1,
            requestSemanticSha256,
            armedAt,
            authorization.AuthorizationId,
            eligibilityBasis);
        row.Status = "CREATE_ATTEMPTED";
        row.CreateAttemptId = attempt.AttemptId;
        row.CreateAttemptCount = attempt.AttemptNumber;
        row.CreateDispatchArmedAt = armedAt;
        row.LastCreateOutcome = "DispatchArmed";
        row.LastCreateOutcomeAt = armedAt;
        permit.ConsumedAt = armedAt;
        permit.ConsumedByAttemptId = attempt.AttemptId;
        await AppendAuditEventAsync(
            row,
            new DispatchAuditWrite(
                RiotDispatchAuditPhase.CreateDispatch,
                RiotDispatchAuditOutcome.Armed,
                armedAt,
                attempt.AttemptId,
                requestSemanticSha256,
                ExperimentalAuthorizationId: authorization.AuthorizationId,
                EligibilityBasis: eligibilityBasis),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new BusinessIdentityConflictException(
                "A concurrent RIoT create decision consumed or invalidated the experimental authorization.");
        }
        return attempt;
    }

    public async Task RecordCreateStartedAsync(
        string upperId,
        CreateDispatchAttempt attempt,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        OrderIntentRow row = await GetMatchingAttemptAsync(upperId, attempt, cancellationToken).ConfigureAwait(false);
        if (attempt.ExperimentalAuthorizationId is not null)
        {
            ExperimentalRiotCreateAuthorizationRow permit = await dbContext.ExperimentalRiotCreateAuthorizations
                .SingleAsync(
                    item => item.AuthorizationId == attempt.ExperimentalAuthorizationId &&
                            item.ConsumedByAttemptId == attempt.AttemptId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (attempt.EligibilityBasis != "EXPERIMENTAL_ABSENT_AT_OBSERVATION" ||
                permit.ExpiresAt <= startedAt)
            {
                throw new BusinessIdentityConflictException(
                    "The experimental RIoT create authorization expired or changed before START could be committed.");
            }
        }
        row.LastCreateOutcome = "CreateRequestStarted";
        row.LastCreateOutcomeAt = startedAt;
        await AppendAuditEventAsync(
            row,
            new DispatchAuditWrite(
                RiotDispatchAuditPhase.CreateRequest,
                RiotDispatchAuditOutcome.Started,
                startedAt,
                attempt.AttemptId,
                attempt.RequestSemanticSha256,
                ExperimentalAuthorizationId: attempt.ExperimentalAuthorizationId,
                EligibilityBasis: attempt.EligibilityBasis),
            cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordCreateResponseAsync(
        string upperId,
        CreateDispatchAttempt attempt,
        DispatchAuditWrite audit,
        bool markResultUnknown,
        CancellationToken cancellationToken)
    {
        if (audit.Phase != RiotDispatchAuditPhase.CreateResponse || audit.AttemptId != attempt.AttemptId)
        {
            throw new ArgumentException("Create response audit identity is invalid.", nameof(audit));
        }
        if (audit.ExperimentalAuthorizationId != attempt.ExperimentalAuthorizationId ||
            audit.EligibilityBasis != attempt.EligibilityBasis)
        {
            throw new ArgumentException("Create response authorization evidence is invalid.", nameof(audit));
        }
        OrderIntentRow row = await GetMatchingAttemptAsync(upperId, attempt, cancellationToken).ConfigureAwait(false);
        if (markResultUnknown && row.Status != "CONFIRMED")
        {
            row.Status = "RESULT_UNKNOWN";
        }
        row.LastCreateOutcome = audit.Outcome == RiotDispatchAuditOutcome.Accepted
            ? "CreateResponseAccepted"
            : "CreateResponseUnknown";
        row.LastCreateOutcomeAt = audit.OccurredAt;
        row.LastCreateReceiptJson = SerializeReceipt(audit.Receipt);
        await AppendAuditEventAsync(row, audit, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkTerminalReconciliationRequiredAsync(
        string upperId,
        string orderId,
        DispatchAuditWrite audit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        OrderIntentRow row = await dbContext.OrderIntents
            .SingleAsync(item => item.UpperId == upperId, cancellationToken).ConfigureAwait(false);
        if (row.Status == "CONFIRMED" && row.OrderId != orderId)
        {
            throw new BusinessIdentityConflictException("The stable upperId was reconciled to a different orderId.");
        }
        if (row.Status == "TERMINAL_RECONCILIATION_REQUIRED" && row.OrderId != orderId)
        {
            throw new BusinessIdentityConflictException("The terminal upperId was reconciled to a different orderId.");
        }
        row.Status = "TERMINAL_RECONCILIATION_REQUIRED";
        row.OrderId = orderId;
        ApplyReconciliationSummary(row, audit);
        await AppendAuditEventAsync(row, audit, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ConfirmAsync(
        string upperId,
        string orderId,
        DispatchAuditWrite audit,
        CancellationToken cancellationToken)
    {
        OrderIntentRow row = await dbContext.OrderIntents
            .SingleAsync(item => item.UpperId == upperId, cancellationToken).ConfigureAwait(false);
        if (row.Status == "CONFIRMED" && row.OrderId != orderId)
        {
            throw new BusinessIdentityConflictException("The stable upperId was reconciled to a different orderId.");
        }
        row.Status = "CONFIRMED";
        row.OrderId = orderId;
        ApplyReconciliationSummary(row, audit);
        await AppendAuditEventAsync(row, audit, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyAdmissionPolicyAsync(
        AdmissionPolicyDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Version <= 0) throw new ArgumentOutOfRangeException(nameof(definition));
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DeploymentId);
        StationTaskTypeAdmission[] relations = definition.Relations
            .OrderBy(item => item.StationId, StringComparer.Ordinal)
            .ThenBy(item => item.TaskType, StringComparer.Ordinal)
            .ToArray();
        if (relations.Any(item => string.IsNullOrWhiteSpace(item.StationId) ||
                                  string.IsNullOrWhiteSpace(item.TaskType)) ||
            relations.Distinct().Count() != relations.Length)
        {
            throw new BusinessIdentityConflictException("Admission policy relations must be unique and complete.");
        }
        string relationsJson = JsonSerializer.Serialize(relations);
        string contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relationsJson)))
            .ToLowerInvariant();
        AdmissionPolicyStateRow? current = await dbContext.AdmissionPolicyState
            .SingleOrDefaultAsync(row => row.Id == 1, cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            if (definition.Version < current.Version)
                throw new BusinessIdentityConflictException("Admission policy version cannot move backwards.");
            if (definition.Version == current.Version)
            {
                if (current.ContentHash != contentHash || current.DeploymentId != definition.DeploymentId)
                    throw new BusinessIdentityConflictException(
                        "Admission policy version is already bound to different content or deployment identity.");
                return;
            }
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        string? previousHash = current?.ContentHash;
        if (current is null)
        {
            current = new AdmissionPolicyStateRow
            {
                Id = 1,
                DeploymentId = definition.DeploymentId,
                ContentHash = contentHash
            };
            dbContext.AdmissionPolicyState.Add(current);
        }
        else
        {
            dbContext.StationTaskTypeAdmissions.RemoveRange(dbContext.StationTaskTypeAdmissions);
        }
        current.Version = definition.Version;
        current.DeploymentId = definition.DeploymentId;
        current.ContentHash = contentHash;
        current.ImportedAt = definition.ImportedAt;
        dbContext.StationTaskTypeAdmissions.AddRange(relations.Select(item =>
            new StationTaskTypeAdmissionRow
            {
                StationId = item.StationId,
                TaskType = item.TaskType,
                PolicyVersion = definition.Version
            }));
        dbContext.AdmissionPolicyAudit.Add(new AdmissionPolicyAuditRow
        {
            Version = definition.Version,
            DeploymentId = definition.DeploymentId,
            PreviousContentHash = previousHash,
            ContentHash = contentHash,
            RelationsJson = relationsJson,
            ImportedAt = definition.ImportedAt
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> IsTaskTypeAllowedAsync(
        string stationId,
        string taskType,
        CancellationToken cancellationToken) =>
        dbContext.StationTaskTypeAdmissions.AnyAsync(
            row => row.StationId == stationId && row.TaskType == taskType,
            cancellationToken);

    public async Task<ProtocolOutboxRow> PrepareSlotOperationAsync(
        StationOperationPlan plan, string messageId, string commandJson, CancellationToken cancellationToken)
    {
        int[] targetSlots = plan.TargetSlots.Distinct().Order().ToArray();
        if (targetSlots.Length == 0 || targetSlots.Length != plan.TargetSlots.Count || targetSlots.Any(slot => slot is < 1 or > 8))
        {
            throw new BusinessIdentityConflictException("Target slots must be a unique non-empty subset of 1..8.");
        }
        bool hasAdmissionIdentity = plan.AdmissionStationId is not null || plan.AdmissionTaskType is not null;
        if ((plan.AdmissionStationId is null) != (plan.AdmissionTaskType is null) ||
            hasAdmissionIdentity && plan.OperationType != SlotOperationType.Load)
        {
            throw new BusinessIdentityConflictException(
                "Only LOAD may carry a complete station/task admission identity.");
        }

        StationOperationRow? existing = await dbContext.StationOperations
            .SingleOrDefaultAsync(row => row.SlotOperationAttemptId == plan.SlotOperationAttemptId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            int[] existingSlots = JsonSerializer.Deserialize<int[]>(existing.TargetSlotsJson) ?? [];
            bool samePlan = existing.DemandId == plan.DemandId &&
                            existing.SublotId == plan.SublotId &&
                            existingSlots.SequenceEqual(targetSlots) &&
                            existing.OperationType == plan.OperationType &&
                            existing.ForcedRecoveryGeneration == plan.ForcedRecoveryGeneration &&
                            existing.ContentHash == plan.ContentHash;
            if (!samePlan)
            {
                throw new ProtocolContentConflictException("SlotOperationAttemptId was replayed with different content.");
            }
            if (hasAdmissionIdentity)
            {
                AdmissionDecisionSnapshotRow decision = await dbContext.AdmissionDecisionSnapshots
                    .SingleAsync(row => row.SlotOperationAttemptId == plan.SlotOperationAttemptId, cancellationToken)
                    .ConfigureAwait(false);
                if (!decision.Allowed || decision.StationId != plan.AdmissionStationId ||
                    decision.TaskType != plan.AdmissionTaskType)
                {
                    throw new BusinessIdentityConflictException(
                        "Frozen admission decision does not match the replayed LOAD operation.");
                }
            }

            ProtocolOutboxRow existingOutbox = await dbContext.ProtocolOutbox
                .SingleAsync(row => row.MessageId == messageId, cancellationToken)
                .ConfigureAwait(false);
            await RefreshOutboundEnvelopeAsync(
                existingOutbox,
                "SlotOperationCommand",
                commandJson,
                plan.CreatedAt,
                cancellationToken).ConfigureAwait(false);
            return existingOutbox;
        }

        if (await dbContext.ProtocolOutbox.AnyAsync(row => row.MessageId == messageId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new ProtocolContentConflictException(
                "Outbound MessageId is already bound without the matching slot operation.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (hasAdmissionIdentity)
        {
            AdmissionPolicyStateRow policy = await dbContext.AdmissionPolicyState
                .SingleAsync(row => row.Id == 1, cancellationToken).ConfigureAwait(false);
            bool allowed = await dbContext.StationTaskTypeAdmissions.AnyAsync(
                row => row.StationId == plan.AdmissionStationId && row.TaskType == plan.AdmissionTaskType,
                cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                throw new BusinessIdentityConflictException("TASK_TYPE_NOT_ALLOWED_AT_STATION");
            }
            dbContext.AdmissionDecisionSnapshots.Add(new AdmissionDecisionSnapshotRow
            {
                SlotOperationAttemptId = plan.SlotOperationAttemptId,
                StationId = plan.AdmissionStationId!,
                TaskType = plan.AdmissionTaskType!,
                AdmissionPolicyVersion = policy.Version,
                AdmittedAt = plan.CreatedAt,
                Allowed = true
            });
        }
        dbContext.StationOperations.Add(new StationOperationRow
        {
            SlotOperationAttemptId = plan.SlotOperationAttemptId,
            DemandId = plan.DemandId,
            SublotId = plan.SublotId,
            TargetSlotsJson = JsonSerializer.Serialize(targetSlots),
            OperationType = plan.OperationType,
            ForcedRecoveryGeneration = plan.ForcedRecoveryGeneration,
            ContentHash = plan.ContentHash,
            Status = StationOperationStatus.Prepared,
            CreatedAt = plan.CreatedAt
        });
        ProtocolOutboxRow outbox = new()
        {
            MessageId = messageId,
            MessageType = "SlotOperationCommand",
            PayloadJson = commandJson,
            CreatedAt = plan.CreatedAt
        };
        dbContext.ProtocolOutbox.Add(outbox);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return outbox;
    }

    public async Task CommitSlotBatchAsync(
        string slotOperationAttemptId, IReadOnlyCollection<SlotPhysicalEvidence> evidence,
        DateTimeOffset committedAt, CancellationToken cancellationToken)
    {
        StationOperationRow row = await dbContext.StationOperations
            .SingleAsync(item => item.SlotOperationAttemptId == slotOperationAttemptId, cancellationToken)
            .ConfigureAwait(false);
        if (row.Status == StationOperationStatus.Committed)
        {
            return;
        }

        int[] expected = JsonSerializer.Deserialize<int[]>(row.TargetSlotsJson) ?? [];
        int[] actual = evidence.Select(item => item.SlotNumber).Distinct().Order().ToArray();
        bool safe = expected.SequenceEqual(actual) && evidence.Count == expected.Length &&
                    evidence.All(item => item.State == SlotBusinessState.Occupied && item.DoorLocked && item.UnlockOutputReset);
        if (!safe)
        {
            throw new UnsafePhysicalEvidenceException("LoadBatch requires complete OCCUPIED, locked and output-reset evidence.");
        }

        row.Status = StationOperationStatus.Committed;
        row.EvidenceJson = JsonSerializer.Serialize(evidence.OrderBy(item => item.SlotNumber));
        row.CommittedAt = committedAt;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AuthorizeMovementAsync(
        OrderIntent intent, SafetyCheckObservation safety, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        bool valid = safety.DepartureSafe && safety.ObservedAt <= now && safety.ValidUntil >= now;
        if (!valid)
        {
            throw new UnsafeMovementAuthorizationException("Movement requires a current SAFE pre-departure observation.");
        }

        OrderIntentRow? existing = await dbContext.OrderIntents
            .SingleOrDefaultAsync(row => row.MovementLegId == intent.MovementLegId || row.UpperId == intent.UpperId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!Matches(existing, intent))
            {
                throw new BusinessIdentityConflictException("Movement identity is already bound to different intent content.");
            }
            return;
        }

        dbContext.OrderIntents.Add(ToRow(intent));
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the movement intent for a leg the vehicle drives with nothing aboard -- today only
    /// the run to the charger. Unlike <see cref="AuthorizeMovementAsync"/> it carries no
    /// pre-departure safety observation, and that is the point: the observation exists to prove
    /// every slot door closed and latched after a load, and no door was opened here. The caller's
    /// own admission facts gate this leg, exactly as they gate the TO_PICKUP leg, which is
    /// recorded through demand acceptance without a safety observation for the same reason.
    /// </summary>
    public async Task CreateUnladenMovementIntentAsync(
        OrderIntent intent,
        CancellationToken cancellationToken)
    {
        OrderIntentRow? existing = await dbContext.OrderIntents
            .SingleOrDefaultAsync(
                row => row.MovementLegId == intent.MovementLegId || row.UpperId == intent.UpperId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!Matches(existing, intent))
            {
                throw new BusinessIdentityConflictException(
                    "Movement identity is already bound to different intent content.");
            }
            return;
        }

        dbContext.OrderIntents.Add(ToRow(intent));
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteDemandAfterUnloadAsync(
        string unloadBatchId, string demandId, string transportDemandKey, long demandRevision,
        IReadOnlyCollection<SlotPhysicalEvidence> evidence, string completionEvidence,
        DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        bool safe = evidence.Count > 0 && evidence.Select(item => item.SlotNumber).Distinct().Count() == evidence.Count &&
                    evidence.All(item => item.State == SlotBusinessState.Empty && item.DoorLocked && item.UnlockOutputReset);
        if (!safe)
        {
            throw new UnsafePhysicalEvidenceException("Completion requires complete EMPTY, locked and output-reset evidence.");
        }

        TransportDemandCompletionRow? replay = await dbContext.TransportDemandCompletions
            .SingleOrDefaultAsync(row => row.TransportDemandKey == transportDemandKey, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.DemandId != demandId || replay.DemandRevision != demandRevision || replay.Evidence != completionEvidence)
            {
                throw new BusinessIdentityConflictException("TransportDemandKey completion replay differs from the permanent fact.");
            }
            return;
        }

        AcceptedDemandRow demand = await dbContext.AcceptedDemands
            .SingleAsync(row => row.DemandId == demandId && row.TransportDemandKey == transportDemandKey, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        dbContext.UnloadBatches.Add(new UnloadBatchRow
        {
            UnloadBatchId = unloadBatchId,
            DemandId = demandId,
            EvidenceJson = JsonSerializer.Serialize(evidence.OrderBy(item => item.SlotNumber)),
            CompletedAt = completedAt
        });
        dbContext.StopClosures.Add(new StopClosureRow { DemandId = demandId, CommittedAt = completedAt });
        demand.Status = DemandExecutionStatus.Succeeded;
        await SettleJourneyDemandAsync(
            demandId, JourneyDemandState.Unloaded, completedAt, cancellationToken).ConfigureAwait(false);
        dbContext.TransportDemandCompletions.Add(new TransportDemandCompletionRow
        {
            TransportDemandKey = transportDemandKey,
            DemandId = demandId,
            DemandRevision = demandRevision,
            Evidence = completionEvidence,
            CompletedAt = completedAt
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The reason codes that permanently ban a transport demand from execution. A demand merely
    /// vanishing from the MesIngest catalog (MES_DISAPPEARED, GONE) does not: it may legitimately
    /// come back, and the ban exists for demands a human decided against. ADR-cross-0047, FR-004.
    /// </summary>
    private static readonly string[] SuppressingReasonCodes =
    [
        "CANCELLED_BY_OPERATOR",
        "CANCELLED_BY_LOAD_COMPENSATION",
        "CANCELLED_BY_STOP_COMPLETE",
        "CANCELLED_BY_STATION_TIMEOUT",
        "TERMINATED_BY_FAULT_CARGO_HANDOFF"
    ];

    public static bool IsSuppressingReasonCode(string reasonCode) =>
        SuppressingReasonCodes.Contains(reasonCode, StringComparer.Ordinal);

    /// <summary>
    /// Records the permanent ban that a cancellation raises, keyed on the demand's business
    /// identity. Writing it is part of the same SaveChanges as the cancellation itself -- a
    /// cancellation that persisted without its ban would let the very next catalog poll dispatch
    /// the vehicle back to the stop the operator just refused.
    /// </summary>
    /// <remarks>
    /// The first ban on a key wins and is never overwritten. A later cancellation of another
    /// instance under the same key changes nothing: the ban is already absolute, and the original
    /// reason and timestamp are the ones that explain it.
    /// </remarks>
    public async Task SuppressTransportDemandAsync(
        string transportDemandKey,
        string demandId,
        string reasonCode,
        DateTimeOffset suppressedAt,
        CancellationToken cancellationToken)
    {
        if (!IsSuppressingReasonCode(reasonCode))
        {
            throw new BusinessIdentityConflictException(
                $"Reason code '{reasonCode}' does not suppress a transport demand.");
        }
        if (await dbContext.TransportDemandSuppressions
                .AnyAsync(row => row.TransportDemandKey == transportDemandKey, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        dbContext.TransportDemandSuppressions.Add(new TransportDemandSuppressionRow
        {
            TransportDemandKey = transportDemandKey,
            DemandId = demandId,
            ReasonCode = reasonCode,
            SuppressedAt = suppressedAt
        });
    }

    public async Task<HashSet<string>> ReadSuppressedTransportDemandKeysAsync(
        CancellationToken cancellationToken) =>
        (await dbContext.TransportDemandSuppressions
            .Select(row => row.TransportDemandKey)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false))
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Ends a journey that never loaded anything: the operator cancelled at the pickup stop before
    /// any slot operation was commanded, or nobody entered a sublot inside the runtime's wait
    /// window. Both leave the vehicle physically untouched -- no slot was ever opened, so there is
    /// no emptiness for the peer to prove -- which is why this path settles without a peer result.
    /// A cancellation raised *after* a slot operation is commanded keeps its five-step handshake
    /// and terminates through OnboardRecoveryCoordinator instead.
    /// </summary>
    /// <returns><c>true</c> when this call performed the termination; <c>false</c> when the demand
    /// was already cancelled, so a replayed operator request terminates exactly once.</returns>
    public async Task<bool> CancelDemandBeforeLoadAsync(
        string demandId,
        string reasonCode,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken)
    {
        AcceptedDemandRow demand = await dbContext.AcceptedDemands
            .SingleAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        if (demand.Status == DemandExecutionStatus.Succeeded)
        {
            throw new BusinessIdentityConflictException(
                "A completed demand cannot be cancelled as if nothing had been loaded.");
        }
        if (demand.Status == DemandExecutionStatus.Cancelled)
        {
            return false;
        }
        // The whole point of this path is that no slot was touched. A commanded operation -- even
        // one that failed -- carries physical state that only the peer can settle, so terminating
        // it here would drop the demand while a door is possibly still open.
        if (await dbContext.StationOperations
                .AnyAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessIdentityConflictException(
                "A demand with a commanded slot operation cannot be cancelled before load.");
        }

        demand.Status = DemandExecutionStatus.Cancelled;
        await SuppressTransportDemandAsync(
            demand.TransportDemandKey, demandId, reasonCode, cancelledAt, cancellationToken)
            .ConfigureAwait(false);
        JourneySettlement settlement = await SettleJourneyDemandAsync(
            demandId, JourneyDemandState.Cancelled, cancelledAt, cancellationToken).ConfigureAwait(false);
        JourneyRuntimeRow? runtime = await dbContext.JourneyRuntimes
            .SingleOrDefaultAsync(row => row.JourneyId == settlement.JourneyId, cancellationToken)
            .ConfigureAwait(false);
        if (runtime is not null)
        {
            // The sublot entry request this demand was the subject of is still an unanswered
            // command, and nobody is going to answer it. Leaving it unsettled replays it into every
            // later session, where the peer refuses it as a business id whose content changed and
            // tears the session down -- the same failure the load command was settled for. This is
            // per stop and per round: the next demand at the same stop asks again under its own id.
            JourneyStopRow? stop = settlement.StopSequence is { } sequence
                ? await dbContext.JourneyStops.SingleOrDefaultAsync(
                    row => row.JourneyId == settlement.JourneyId && row.Sequence == sequence,
                    cancellationToken).ConfigureAwait(false)
                : null;
            if (stop is not null && stop.LoadRound > 0)
            {
                string requestMessageId = SublotRequestId(stop.JourneyId, stop.Sequence, stop.LoadRound);
                ProtocolOutboxRow? sublotRequest = await dbContext.ProtocolOutbox.SingleOrDefaultAsync(
                    row => row.MessageId == requestMessageId, cancellationToken).ConfigureAwait(false);
                if (sublotRequest is not null)
                {
                    sublotRequest.AcknowledgedAt ??= cancelledAt;
                }
            }
            // Cancelling one demand does not end a journey that is still carrying others: the
            // vehicle has their cargo on board and their stops ahead of it. The runtime decides on
            // its next poll whether to keep loading here, move on, or head for the gate.
            if (settlement.JourneyComplete)
            {
                runtime.Stage = JourneyRuntimeStage.Completed;
            }
            runtime.BlockReasonCode = reasonCode;
            runtime.UpdatedAt = cancelledAt;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Ends a demand whose load the vehicle settled as a determinate failure (ADR-cross-0058
    /// decision 5): every target slot read back known, locked and with its unlock output reset, and
    /// nothing was handed over. It is the one commanded operation that needs no peer handshake to
    /// terminate -- the failure result already is the account of the slots that a cancellation would
    /// otherwise have to prove.
    /// </summary>
    /// <remarks>
    /// 8005-agv-program#39. The demand used to be left Accepted for LoadTaskCancellation to settle,
    /// and nothing ever raised one: the onboard offers no cancellation for an attempt whose result is
    /// recorded, and <see cref="CancelDemandBeforeLoadAsync"/> refuses any demand with a commanded
    /// operation. The journey sat in AwaitingLoadResult for good. The runtime's stage is not touched
    /// here: the runtime closes the batch through the same path as every other terminal end, which is
    /// what decides between another round, the next stop and the gate.
    /// </remarks>
    /// <returns><c>true</c> when this call performed the termination; <c>false</c> when the demand
    /// was already cancelled.</returns>
    public async Task<bool> CancelDemandAfterDeterminateLoadFailureAsync(
        string demandId,
        string reasonCode,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken)
    {
        AcceptedDemandRow demand = await dbContext.AcceptedDemands
            .SingleAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        if (demand.Status == DemandExecutionStatus.Cancelled)
        {
            return false;
        }
        if (demand.Status == DemandExecutionStatus.Succeeded)
        {
            throw new BusinessIdentityConflictException(
                "A completed demand cannot be cancelled after a failed load.");
        }
        JourneyDemandRow membership = await dbContext.JourneyDemands
            .SingleAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        StationOperationRow? operation = await dbContext.StationOperations.SingleOrDefaultAsync(
            row => row.SlotOperationAttemptId == membership.LoadSlotOperationAttemptId,
            cancellationToken).ConfigureAwait(false);
        // Only a determinate failure ends here. Anything else still carries physical state the peer
        // has to settle -- an UNKNOWN slot, a door that may be open -- and terminating it would drop
        // the demand behind that state.
        if (operation is not { OperationType: SlotOperationType.Load, Status: StationOperationStatus.Failed })
        {
            throw new BusinessIdentityConflictException(
                "Only a load settled as a determinate failure can be cancelled without a peer handshake.");
        }

        demand.Status = DemandExecutionStatus.Cancelled;
        await SuppressTransportDemandAsync(
            demand.TransportDemandKey, demandId, reasonCode, cancelledAt, cancellationToken)
            .ConfigureAwait(false);
        await SettleJourneyDemandAsync(demandId, JourneyDemandState.Cancelled, cancelledAt, cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        // The load command this failure answered is never followed by a successful result, and only
        // a closed batch settles it. Left pending it is replayed into every later session under a new
        // generation, which the peer refuses as a business id whose content changed.
        await SettleAnsweredCommandAsync(membership.LoadCommandMessageId, cancelledAt, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    public async Task RecordConnectionLossAsync(
        string agvId, long sessionGeneration, IReadOnlyCollection<int> activeUnlockSet,
        DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        int[] slots = NormalizeSlots(activeUnlockSet);
        ConnectionRecoveryRow? row = await dbContext.ConnectionRecoveries
            .SingleOrDefaultAsync(item => item.AgvId == agvId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new ConnectionRecoveryRow { AgvId = agvId };
            dbContext.ConnectionRecoveries.Add(row);
        }
        row.SessionGeneration = sessionGeneration;
        row.ActiveUnlockSetJson = JsonSerializer.Serialize(slots);
        row.Status = ConnectionRecoveryStatus.SafelyFinishing;
        row.ResumeAuthorized = false;
        row.UpdatedAt = observedAt;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordSafeFinishAsync(
        string agvId, long sessionGeneration, IReadOnlyCollection<int> touchedSlots,
        DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        ConnectionRecoveryRow row = await dbContext.ConnectionRecoveries
            .SingleAsync(item => item.AgvId == agvId, cancellationToken).ConfigureAwait(false);
        if (row.SessionGeneration != sessionGeneration)
        {
            throw new StaleSessionGenerationException("Safe-finish evidence belongs to another session generation.");
        }
        int[] allowed = JsonSerializer.Deserialize<int[]>(row.ActiveUnlockSetJson) ?? [];
        int[] actual = NormalizeSlots(touchedSlots);
        if (actual.Except(allowed).Any())
        {
            throw new ActiveUnlockSetExpansionException("Connection loss must not expand ActiveUnlockSet.");
        }
        row.Status = ConnectionRecoveryStatus.AwaitingHandshake;
        row.ResumeAuthorized = false;
        row.UpdatedAt = observedAt;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> CaptureFirstResponseAsync(
        string messageId, string contentHash, Func<Task<string>> responseFactory,
        DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        return await CaptureFirstResponseAsync(
                messageId,
                "UNKNOWN",
                string.Empty,
                contentHash,
                responseFactory,
                receivedAt,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string> CaptureFirstResponseAsync(
        string messageId, string messageType, string requestJson, string contentHash,
        Func<Task<string>> responseFactory, DateTimeOffset receivedAt,
        CancellationToken cancellationToken,
        Func<string, string>? replayEquivalenceHash = null,
        Action<string>? equivalentReplayObserved = null)
    {
        ProtocolInboxRow? existing = await dbContext.ProtocolInbox
            .SingleOrDefaultAsync(row => row.MessageId == messageId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.ContentHash == contentHash)
            {
                return existing.FirstResponseJson;
            }

            if (replayEquivalenceHash is null ||
                replayEquivalenceHash(existing.RequestJson) != replayEquivalenceHash(requestJson))
            {
                throw new ProtocolContentConflictException("MessageId was replayed with different normalized content.");
            }

            await using var replayTransaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            equivalentReplayObserved?.Invoke(existing.FirstResponseJson);
            string reboundResponse = await responseFactory().ConfigureAwait(false);
            existing.MessageType = messageType;
            existing.RequestJson = requestJson;
            existing.ContentHash = contentHash;
            existing.FirstResponseJson = reboundResponse;
            existing.ReceivedAt = receivedAt;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await replayTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return reboundResponse;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string response = await responseFactory().ConfigureAwait(false);
        dbContext.ProtocolInbox.Add(new ProtocolInboxRow
        {
            MessageId = messageId,
            MessageType = messageType,
            RequestJson = requestJson,
            ContentHash = contentHash,
            FirstResponseJson = response,
            ReceivedAt = receivedAt
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return response;
    }

    public async Task<ProtocolOutboxRow> QueueOutboundEnvelopeAsync(
        string messageId,
        string messageType,
        string wireJson,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(wireJson);

        ProtocolOutboxRow? existing = await dbContext.ProtocolOutbox
            .SingleOrDefaultAsync(row => row.MessageId == messageId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            await RefreshOutboundEnvelopeAsync(
                existing, messageType, wireJson, createdAt, cancellationToken).ConfigureAwait(false);
            return existing;
        }

        ProtocolOutboxRow row = new()
        {
            MessageId = messageId,
            MessageType = messageType,
            PayloadJson = wireJson,
            CreatedAt = createdAt
        };
        dbContext.ProtocolOutbox.Add(row);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    public Task<ProtocolOutboxRow?> FindOutboundEnvelopeAsync(
        string messageId,
        CancellationToken cancellationToken) =>
        dbContext.ProtocolOutbox.SingleOrDefaultAsync(
            row => row.MessageId == messageId,
            cancellationToken);

    public async Task<ProtocolOutboxRow[]> GetPendingOutboundEnvelopesAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        ProtocolOutboxRow[] rows = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.AcknowledgedAt == null && row.FencedAt == null)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Where(row =>
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            return document.RootElement.GetProperty("agvId").GetString() == agvId;
        }).OrderBy(row => row.CreatedAt).ThenBy(row => row.MessageId, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Settles a command whose answer is a business result rather than a DurableAck.
    /// </summary>
    /// <remarks>
    /// SublotEntryRequested, SlotOperationCommand and PreDepartureSafetyCheck are answered with
    /// SublotSubmitted, OperationResult and PreDepartureSafetyCheckResult. None of those is a
    /// DurableAck, so the outbox rows stayed unacknowledged forever and were replayed into every
    /// later session -- carrying a new session generation, which the peer refused as a business id
    /// whose content had changed. A journey that had long since consumed the answer was torn down
    /// on every reconnect by a command it had already obeyed. The answer this server has already
    /// validated and acted on is the acknowledgement.
    /// </remarks>
    public async Task SettleAnsweredCommandAsync(
        string messageId,
        DateTimeOffset answeredAt,
        CancellationToken cancellationToken)
    {
        ProtocolOutboxRow? row = await dbContext.ProtocolOutbox
            .SingleOrDefaultAsync(item => item.MessageId == messageId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || row.AcknowledgedAt is not null)
        {
            return;
        }
        row.AcknowledgedAt = answeredAt;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AcknowledgeOutboundEnvelopeAsync(
        string messageId,
        string messageType,
        string contentHash,
        long? appliedRevision,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken)
    {
        ProtocolOutboxRow row = await dbContext.ProtocolOutbox
            .SingleAsync(item => item.MessageId == messageId, cancellationToken)
            .ConfigureAwait(false);
        string storedHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(row.PayloadJson)))
            .ToLowerInvariant();
        if (row.MessageType != messageType || storedHash != contentHash)
        {
            throw new ProtocolContentConflictException(
                "Outbound acknowledgement does not match the persisted message type and wire content.");
        }
        if (appliedRevision is not null)
        {
            string revisionProperty = messageType switch
            {
                "VehicleBusinessStateSnapshot" => "vehicleBusinessStateRevision",
                "CurrentStopWorklistSnapshot" => "worklistRevision",
                "UpcomingStopPlanSnapshot" => "planRevision",
                "ExceptionRecoverySessionSnapshot" => "recoverySessionRevision",
                _ => throw new ProtocolContentConflictException(
                    "Only a persisted snapshot can be acknowledged with an applied revision.")
            };
            using JsonDocument envelope = JsonDocument.Parse(row.PayloadJson);
            long storedRevision = envelope.RootElement
                .GetProperty("payload")
                .GetProperty(revisionProperty)
                .GetInt64();
            if (storedRevision != appliedRevision)
            {
                throw new ProtocolContentConflictException(
                    "Snapshot acknowledgement revision does not match the persisted projection.");
            }
        }

        row.AcknowledgedAt ??= acknowledgedAt;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AdvanceForcedRecoveryGenerationAsync(
        string agvId, long generation, DateTimeOffset advancedAt, CancellationToken cancellationToken)
    {
        VehicleRecoveryGenerationRow? row = await dbContext.VehicleRecoveryGenerations
            .SingleOrDefaultAsync(item => item.AgvId == agvId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new VehicleRecoveryGenerationRow { AgvId = agvId };
            dbContext.VehicleRecoveryGenerations.Add(row);
        }
        if (generation <= row.ForcedRecoveryGeneration)
        {
            throw new BusinessIdentityConflictException("ForcedRecoveryGeneration must advance monotonically.");
        }
        row.ForcedRecoveryGeneration = generation;
        row.UpdatedAt = advancedAt;
        SessionRecoveryRow? session = await dbContext.SessionRecoveries
            .SingleOrDefaultAsync(item => item.AgvId == agvId, cancellationToken).ConfigureAwait(false);
        if (session is not null)
        {
            session.ForcedRecoveryGeneration = generation;
            session.Readiness = SessionReadiness.RecoveryRequired;
            session.ReasonCode = "FORCED_RECOVERY_RECONCILIATION_REQUIRED";
            session.UpdatedAt = advancedAt;
        }
        ProtocolOutboxRow[] pendingOutbound = await dbContext.ProtocolOutbox
            .Where(item => item.AcknowledgedAt == null && item.FencedAt == null)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (ProtocolOutboxRow outbound in pendingOutbound)
        {
            using JsonDocument document = JsonDocument.Parse(outbound.PayloadJson);
            if (document.RootElement.GetProperty("agvId").GetString() == agvId)
            {
                outbound.FencedAt = advancedAt;
            }
        }
        RecoveryWorkflowRow[] staleWorkflows = await dbContext.RecoveryWorkflows
            .Where(item => item.AgvId == agvId &&
                           item.ForcedRecoveryGeneration < generation &&
                           item.State != RecoveryWorkflowState.Reconciled &&
                           item.State != RecoveryWorkflowState.HistoricalOnly)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (RecoveryWorkflowRow workflow in staleWorkflows)
        {
            workflow.State = RecoveryWorkflowState.HistoricalOnly;
            workflow.UpdatedAt = advancedAt;
            if (workflow.CommandMessageId is not null)
            {
                ProtocolOutboxRow? command = await dbContext.ProtocolOutbox
                    .SingleOrDefaultAsync(
                        item => item.MessageId == workflow.CommandMessageId,
                        cancellationToken).ConfigureAwait(false);
                if (command is not null)
                {
                    command.FencedAt ??= advancedAt;
                }
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResultDisposition> RecordOperationResultAsync(
        string resultId, string slotOperationAttemptId, string agvId, long forcedRecoveryGeneration,
        string contentHash, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        OperationResultRow? replay = await dbContext.OperationResults
            .SingleOrDefaultAsync(
                row => row.ResultId == resultId ||
                       row.SlotOperationAttemptId == slotOperationAttemptId &&
                       row.ForcedRecoveryGeneration == forcedRecoveryGeneration &&
                       row.SupersededByResultId == null,
                cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.ResultId != resultId || replay.SlotOperationAttemptId != slotOperationAttemptId ||
                replay.ForcedRecoveryGeneration != forcedRecoveryGeneration || replay.ContentHash != contentHash)
            {
                throw new ProtocolContentConflictException(
                    "Operation result identity was replayed with different message or content.");
            }
            return OperationResultDisposition.Replay;
        }
        long currentGeneration = await dbContext.VehicleRecoveryGenerations
            .Where(row => row.AgvId == agvId)
            .Select(row => (long?)row.ForcedRecoveryGeneration)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        bool historicalOnly = forcedRecoveryGeneration < currentGeneration;
        dbContext.OperationResults.Add(new OperationResultRow
        {
            ResultId = resultId,
            SlotOperationAttemptId = slotOperationAttemptId,
            AgvId = agvId,
            ForcedRecoveryGeneration = forcedRecoveryGeneration,
            ContentHash = contentHash,
            ResultContentSha256 = string.Empty,
            OverallOutcome = "UNKNOWN",
            EvidenceJson = "[]",
            ObservedAt = receivedAt,
            HistoricalOnly = historicalOnly,
            ReceivedAt = receivedAt
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return historicalOnly ? OperationResultDisposition.HistoricalOnly : OperationResultDisposition.Accepted;
    }

    public async Task<OperationResultDisposition> ApplyOperationResultAsync(
        StationOperationResult result,
        string agvId,
        long forcedRecoveryGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        int[] actualSlots = result.SlotEvidence.Select(item => item.SlotNumber).Distinct().Order().ToArray();
        OperationResultRow? replay = await dbContext.OperationResults
            .SingleOrDefaultAsync(row => row.ResultId == result.ResultId, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            bool same = replay.SlotOperationAttemptId == result.SlotOperationAttemptId &&
                        replay.AgvId == agvId &&
                        replay.ForcedRecoveryGeneration == forcedRecoveryGeneration &&
                        replay.ContentHash == result.WireContentSha256 &&
                        replay.ResultContentSha256 == result.ResultContentSha256;
            if (!same)
            {
                throw new ProtocolContentConflictException(
                    "Operation result identity was replayed with different message or content.");
            }
            return OperationResultDisposition.Replay;
        }

        // A second result for an attempt that already has a live one at this generation is a replay
        // conflict, except in the single case the recovery state machine pays for: an administrator
        // authorized RESUME_AFTER_REPAIR for exactly this attempt and the vehicle carried it out.
        OperationResultRow[] settled = await dbContext.OperationResults
            .Where(row => row.SlotOperationAttemptId == result.SlotOperationAttemptId &&
                          row.ForcedRecoveryGeneration == forcedRecoveryGeneration &&
                          row.SupersededByResultId == null)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (settled.Length > 0)
        {
            await RequireResumeAuthorizationAsync(
                result, actualSlots, forcedRecoveryGeneration, cancellationToken).ConfigureAwait(false);
            // The failed result stays as the record of what the vehicle reported; it stops being the
            // operation's live result. Saved on its own because the unique index is enforced per
            // statement, so the replacement cannot be inserted while the old row still holds the slot.
            foreach (OperationResultRow superseded in settled)
            {
                superseded.SupersededByResultId = result.ResultId;
            }
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        long currentGeneration = await dbContext.VehicleRecoveryGenerations
            .Where(row => row.AgvId == agvId)
            .Select(row => (long?)row.ForcedRecoveryGeneration)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        bool historicalOnly = forcedRecoveryGeneration < currentGeneration;
        string evidenceJson = JsonSerializer.Serialize(result.SlotEvidence.OrderBy(item => item.SlotNumber));
        dbContext.OperationResults.Add(new OperationResultRow
        {
            ResultId = result.ResultId,
            SlotOperationAttemptId = result.SlotOperationAttemptId,
            AgvId = agvId,
            ForcedRecoveryGeneration = forcedRecoveryGeneration,
            ContentHash = result.WireContentSha256,
            ResultContentSha256 = result.ResultContentSha256,
            OverallOutcome = result.OverallOutcome,
            EvidenceJson = evidenceJson,
            ObservedAt = result.ObservedAt,
            HistoricalOnly = historicalOnly,
            ReceivedAt = result.ObservedAt
        });
        if (historicalOnly)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return OperationResultDisposition.HistoricalOnly;
        }

        StationOperationRow operation = await dbContext.StationOperations
            .SingleAsync(row => row.SlotOperationAttemptId == result.SlotOperationAttemptId, cancellationToken)
            .ConfigureAwait(false);
        if (operation.DemandId != result.DemandId ||
            operation.OperationType != result.OperationType ||
            operation.ForcedRecoveryGeneration != forcedRecoveryGeneration)
        {
            throw new BusinessIdentityConflictException(
                "OperationResult does not match the persisted slot operation identity.");
        }

        int[] expectedSlots = JsonSerializer.Deserialize<int[]>(operation.TargetSlotsJson) ?? [];
        SlotBusinessState expectedState = operation.OperationType == SlotOperationType.Load
            ? SlotBusinessState.Occupied
            : SlotBusinessState.Empty;
        bool completedSafely = result.OverallOutcome == "COMPLETED" &&
                               result.AllSlotsCompleted &&
                               result.SlotEvidence.Count == expectedSlots.Length &&
                               actualSlots.SequenceEqual(expectedSlots) &&
                               result.SlotEvidence.All(item =>
                                   item.State == expectedState && item.DoorLocked && item.UnlockOutputReset);
        if (!completedSafely)
        {
            // ADR-cross-0058 decision 5. Missing the target state is not the same as not knowing
            // what happened. When every commanded slot came back with a known occupancy state, a
            // locked door and a reset unlock output, the vehicle has given a complete account: the
            // cargo simply was not handed over. Nothing is uncertain, so nothing needs an
            // administrator -- routing this to recovery is exactly what ADR-cross-0040 forbids when
            // it says software must not treat "nobody loaded it" as a sensor fault.
            //
            // Unload is deliberately excluded. ADR-cross-0015 gives it UnloadCompletionRequired with
            // no cancellation branch, so an unload that misses its target keeps closing the loop
            // until the slots are empty; it has no determinate-failure exit to take.
            bool determinateFailure =
                operation.OperationType == SlotOperationType.Load &&
                result.OverallOutcome == "FAILED" &&
                result.SlotEvidence.Count == expectedSlots.Length &&
                actualSlots.SequenceEqual(expectedSlots) &&
                result.SlotEvidence.All(item =>
                    item.State != SlotBusinessState.Unknown && item.DoorLocked && item.UnlockOutputReset);
            operation.Status = determinateFailure
                ? StationOperationStatus.Failed
                : StationOperationStatus.RecoveryRequired;
            if (!determinateFailure)
            {
                AcceptedDemandRow? blockedDemand = await dbContext.AcceptedDemands
                    .SingleOrDefaultAsync(row => row.DemandId == result.DemandId, cancellationToken)
                    .ConfigureAwait(false);
                if (blockedDemand is not null && blockedDemand.Status != DemandExecutionStatus.Succeeded)
                {
                    blockedDemand.Status = DemandExecutionStatus.RecoveryRequired;
                }
            }
            // A determinately failed load leaves the demand Accepted on purpose: it is still a live
            // demand, and LoadTaskCancellation (ADR-cross-0015, ADR-cross-0046) is what settles it.
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return determinateFailure
                ? OperationResultDisposition.DeterminateFailure
                : OperationResultDisposition.RecoveryRequired;
        }

        operation.Status = StationOperationStatus.Committed;
        operation.EvidenceJson = evidenceJson;
        operation.CommittedAt = result.ObservedAt;
        if (operation.OperationType == SlotOperationType.Load)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return OperationResultDisposition.Accepted;
        }

        AcceptedDemandRow demand = await dbContext.AcceptedDemands
            .SingleAsync(row => row.DemandId == result.DemandId, cancellationToken)
            .ConfigureAwait(false);
        TransportDemandCompletionRow? existingCompletion = await dbContext.TransportDemandCompletions
            .SingleOrDefaultAsync(row => row.TransportDemandKey == demand.TransportDemandKey, cancellationToken)
            .ConfigureAwait(false);
        if (existingCompletion is not null)
        {
            bool sameCompletion = existingCompletion.DemandId == demand.DemandId &&
                                  existingCompletion.DemandRevision == demand.DemandRevision &&
                                  existingCompletion.Evidence == result.ResultContentSha256;
            if (!sameCompletion)
            {
                throw new BusinessIdentityConflictException(
                    "TransportDemandKey completion differs from the persisted unload result.");
            }
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return OperationResultDisposition.Accepted;
        }

        dbContext.UnloadBatches.Add(new UnloadBatchRow
        {
            UnloadBatchId = result.SlotOperationAttemptId,
            DemandId = result.DemandId,
            EvidenceJson = evidenceJson,
            CompletedAt = result.ObservedAt
        });
        dbContext.StopClosures.Add(new StopClosureRow
        {
            DemandId = result.DemandId,
            CommittedAt = result.ObservedAt
        });
        demand.Status = DemandExecutionStatus.Succeeded;
        await SettleJourneyDemandAsync(
            result.DemandId, JourneyDemandState.Unloaded, result.ObservedAt, cancellationToken)
            .ConfigureAwait(false);
        dbContext.TransportDemandCompletions.Add(new TransportDemandCompletionRow
        {
            TransportDemandKey = demand.TransportDemandKey,
            DemandId = demand.DemandId,
            DemandRevision = demand.DemandRevision,
            Evidence = result.ResultContentSha256,
            CompletedAt = result.ObservedAt
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return OperationResultDisposition.Accepted;
    }

    /// <summary>
    /// A RESUME_AFTER_REPAIR authorization admits exactly one replacement OperationResult for the
    /// operation it named. Observing that result moves the workflow off AwaitingResult, so a
    /// further result finds no authorization to spend and falls back on the replay conflict.
    /// </summary>
    private async Task RequireResumeAuthorizationAsync(
        StationOperationResult result,
        int[] actualSlots,
        long forcedRecoveryGeneration,
        CancellationToken cancellationToken)
    {
        RecoveryWorkflowRow? authorization = await dbContext.RecoveryWorkflows
            .SingleOrDefaultAsync(
                row => row.WorkflowType == "RESUME_AFTER_REPAIR" &&
                       row.SlotOperationAttemptId == result.SlotOperationAttemptId &&
                       row.State == RecoveryWorkflowState.AwaitingResult,
                cancellationToken).ConfigureAwait(false);
        if (authorization?.CommandContentHash is null ||
            authorization.CommandMessageType != "SlotOperationResumeCommand")
        {
            throw new ProtocolContentConflictException(
                "Operation result identity was replayed with different message or content.");
        }
        if (authorization.ForcedRecoveryGeneration != forcedRecoveryGeneration)
        {
            throw new BusinessIdentityConflictException(
                "Replacement OperationResult was authorized at a different forced recovery generation.");
        }
        // The hash the resume command carried to the vehicle covers the demand, the attempt and the
        // exact slot set. Recomputing it from what came back is one check for all three: a
        // replacement that widens, narrows or redirects its scope cannot reproduce it.
        if (RecoveryCommandHash.ForRecoveryAction(
                authorization.WorkflowId,
                result.DemandId,
                result.SlotOperationAttemptId,
                JsonSerializer.Serialize(actualSlots),
                authorization.ForcedRecoveryGeneration) != authorization.CommandContentHash)
        {
            throw new BusinessIdentityConflictException(
                "Replacement OperationResult falls outside its RESUME_AFTER_REPAIR authorization.");
        }
    }

    public async Task<string> RecordRecoveryDecisionAsync(
        string recoverySessionId, string recoveryActionId, long forcedRecoveryGeneration,
        string contentHash, string decision, DateTimeOffset decidedAt, CancellationToken cancellationToken)
    {
        RecoveryDecisionRow? replay = await dbContext.RecoveryDecisions
            .SingleOrDefaultAsync(row => row.RecoveryActionId == recoveryActionId, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.ContentHash != contentHash || replay.RecoverySessionId != recoverySessionId ||
                replay.ForcedRecoveryGeneration != forcedRecoveryGeneration)
            {
                throw new ProtocolContentConflictException("RecoveryActionId was replayed with different content.");
            }
            return replay.Decision;
        }
        dbContext.RecoveryDecisions.Add(new RecoveryDecisionRow
        {
            RecoveryActionId = recoveryActionId,
            RecoverySessionId = recoverySessionId,
            ForcedRecoveryGeneration = forcedRecoveryGeneration,
            ContentHash = contentHash,
            Decision = decision,
            DecidedAt = decidedAt
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return decision;
    }

    private async Task<OrderIntentRow> GetMatchingAttemptAsync(
        string upperId,
        CreateDispatchAttempt attempt,
        CancellationToken cancellationToken)
    {
        OrderIntentRow row = await dbContext.OrderIntents
            .SingleAsync(item => item.UpperId == upperId, cancellationToken).ConfigureAwait(false);
        if (row.CreateAttemptId != attempt.AttemptId ||
            row.CreateAttemptCount != attempt.AttemptNumber ||
            row.CreateDispatchArmedAt != attempt.ArmedAt)
        {
            throw new BusinessIdentityConflictException("RIoT create audit attempt identity does not match the armed intent.");
        }
        return row;
    }

    private async Task AppendAuditEventAsync(
        OrderIntentRow row,
        DispatchAuditWrite audit,
        CancellationToken cancellationToken)
    {
        long nextSequence;
        if (row.DispatchAuditSequence is null)
        {
            long lastSequence = await dbContext.RiotDispatchAuditEvents
                .Where(item => item.MovementLegId == row.MovementLegId)
                .Select(item => (long?)item.Sequence)
                .MaxAsync(cancellationToken)
                .ConfigureAwait(false) ?? 0;
            nextSequence = checked(lastSequence + 1);
        }
        else
        {
            nextSequence = checked(row.DispatchAuditSequence.Value + 1);
            row.DispatchAuditSequence = nextSequence;
        }
        RiotOrderCallReceipt? receipt = RiotAuditSanitizer.Receipt(audit.Receipt);
        dbContext.RiotDispatchAuditEvents.Add(new RiotDispatchAuditEventRow
        {
            AuditEventId = Guid.NewGuid().ToString("D"),
            MovementLegId = row.MovementLegId,
            DemandId = row.DemandId,
            UpperId = row.UpperId,
            DispatchGeneration = row.DispatchGeneration,
            Sequence = nextSequence,
            AttemptId = audit.AttemptId,
            AttemptNumber = audit.AttemptId is null ? null : row.CreateAttemptCount,
            Phase = AuditPhase(audit.Phase),
            Outcome = AuditOutcome(audit.Outcome),
            OccurredAt = audit.OccurredAt,
            RequestSemanticSha256 = audit.RequestSemanticSha256,
            ReceiptOperation = receipt?.Operation,
            ReceiptClassification = receipt?.Classification,
            ReceiptObservedAt = receipt?.ObservedAt,
            HttpStatusCode = receipt?.HttpStatusCode,
            BusinessCode = receipt?.BusinessCode,
            ResultPresent = receipt?.ResultPresent,
            ReturnedOrderId = audit.ReturnedOrderId,
            FailureCategory = receipt?.FailureCategory,
            ExperimentalAuthorizationId = audit.ExperimentalAuthorizationId,
            EligibilityBasis = audit.EligibilityBasis
        });
    }

    private static void ValidateExperimentalAuthorizationShape(
        ExperimentalRiotCreateAuthorization authorization,
        DateTimeOffset decisionAt)
    {
        if (authorization.AuthorizationVersion != 1 ||
            string.IsNullOrWhiteSpace(authorization.AuthorizationId) ||
            string.IsNullOrWhiteSpace(authorization.UpperId) ||
            string.IsNullOrWhiteSpace(authorization.DemandId) ||
            string.IsNullOrWhiteSpace(authorization.MovementLegId) ||
            authorization.AgvLifecycleGeneration <= 0 ||
            authorization.DispatchGeneration <= 0 ||
            authorization.ExpiresAt <= decisionAt)
        {
            throw new BusinessIdentityConflictException(
                "The experimental RIoT create authorization identity, version, generations, or expiry is invalid.");
        }
    }

    private async Task RequireExactCommittedFreshAuthorizationAsync(
        ExperimentalRiotCreateAuthorization authorization,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        OrderIntentRow? row = await dbContext.OrderIntents
            .SingleOrDefaultAsync(item => item.UpperId == authorization.UpperId, cancellationToken)
            .ConfigureAwait(false);
        AcceptedDemandRow? demand = row is null
            ? null
            : await dbContext.AcceptedDemands
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.DemandId == row.DemandId, cancellationToken)
                .ConfigureAwait(false);
        bool hasAuditHistory = row is not null && await dbContext.RiotDispatchAuditEvents
            .AsNoTracking()
            .AnyAsync(item => item.MovementLegId == row.MovementLegId, cancellationToken)
            .ConfigureAwait(false);
        ExperimentalRiotCreateAuthorizationRow[] permits = await dbContext.ExperimentalRiotCreateAuthorizations
            .AsNoTracking()
            .Where(item => item.AuthorizationId == authorization.AuthorizationId ||
                           item.UpperId == authorization.UpperId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row is null ||
            demand is null ||
            permits.Length != 1 ||
            !MatchesExperimentalAuthorization(row, authorization) ||
            !MatchesExperimentalAuthorization(permits[0], authorization) ||
            permits[0].ConsumedAt is not null ||
            permits[0].ConsumedByAttemptId is not null ||
            row.ExperimentalCreateAuthorizationId != authorization.AuthorizationId ||
            !IsFreshExperimentalIntent(row, demand, hasAuditHistory))
        {
            throw new BusinessIdentityConflictException(
                "A concurrent experimental RIoT create authorization did not preserve one exact fresh identity.");
        }
    }

    private static bool IsFreshExperimentalIntent(
        OrderIntentRow row,
        AcceptedDemandRow demand,
        bool hasAuditHistory) =>
        row.Status == "PENDING_RECONCILIATION" &&
        row.OrderId is null &&
        row.DispatchAuditVersion == 1 &&
        row.DispatchAuditSequence == 0 &&
        row.CreateAttemptCount == 0 &&
        row.CreateAttemptId is null &&
        demand.Generation != 65 &&
        !hasAuditHistory;

    private static bool MatchesExperimentalAuthorization(
        OrderIntentRow row,
        ExperimentalRiotCreateAuthorization authorization) =>
        row.UpperId == authorization.UpperId &&
        row.DemandId == authorization.DemandId &&
        row.MovementLegId == authorization.MovementLegId &&
        row.AgvLifecycleGeneration == authorization.AgvLifecycleGeneration &&
        row.DispatchGeneration == authorization.DispatchGeneration;

    private static bool MatchesExperimentalAuthorization(
        ExperimentalRiotCreateAuthorizationRow row,
        ExperimentalRiotCreateAuthorization authorization) =>
        row.AuthorizationId == authorization.AuthorizationId &&
        row.AuthorizationVersion == authorization.AuthorizationVersion &&
        row.UpperId == authorization.UpperId &&
        row.DemandId == authorization.DemandId &&
        row.MovementLegId == authorization.MovementLegId &&
        row.AgvLifecycleGeneration == authorization.AgvLifecycleGeneration &&
        row.DispatchGeneration == authorization.DispatchGeneration &&
        row.ExpiresAt == authorization.ExpiresAt;

    private static void ApplyReconciliationSummary(OrderIntentRow row, DispatchAuditWrite audit)
    {
        row.LastReconciliationOutcome = audit.Outcome switch
        {
            RiotDispatchAuditOutcome.LegacyAuditUnavailable => "LegacyAuditUnavailable",
            RiotDispatchAuditOutcome.NotFound when audit.Phase == RiotDispatchAuditPhase.PreCreateReconciliation =>
                "PreCreateReconciliationNotFound",
            RiotDispatchAuditOutcome.Unknown when audit.Phase == RiotDispatchAuditPhase.PreCreateReconciliation =>
                "PreCreateReconciliationUnknown",
            RiotDispatchAuditOutcome.Confirmed when audit.Phase == RiotDispatchAuditPhase.PreCreateReconciliation =>
                "PreCreateReconciliationConfirmed",
            RiotDispatchAuditOutcome.Terminal when audit.Phase == RiotDispatchAuditPhase.PreCreateReconciliation =>
                "PreCreateReconciliationTerminal",
            RiotDispatchAuditOutcome.Confirmed => "PostCreateReconciliationConfirmed",
            RiotDispatchAuditOutcome.Terminal => "PostCreateReconciliationTerminal",
            RiotDispatchAuditOutcome.NotFound => "PostCreateReconciliationNotFound",
            _ => "PostCreateReconciliationUnknown"
        };
        row.LastReconciliationOutcomeAt = audit.OccurredAt;
        row.LastReconciliationReceiptJson = SerializeReceipt(audit.Receipt);
    }

    private static string? SerializeReceipt(RiotOrderCallReceipt? receipt)
    {
        RiotOrderCallReceipt? sanitized = RiotAuditSanitizer.Receipt(receipt);
        return sanitized is null ? null : JsonSerializer.Serialize(sanitized);
    }

    private static string AuditPhase(RiotDispatchAuditPhase phase) => phase switch
    {
        RiotDispatchAuditPhase.PreCreateReconciliation => "PRE_CREATE_RECONCILIATION",
        RiotDispatchAuditPhase.CreateDispatch => "CREATE_DISPATCH",
        RiotDispatchAuditPhase.CreateRequest => "CREATE_REQUEST",
        RiotDispatchAuditPhase.CreateResponse => "CREATE_RESPONSE",
        RiotDispatchAuditPhase.PostCreateReconciliation => "POST_CREATE_RECONCILIATION",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
    };

    private static string AuditOutcome(RiotDispatchAuditOutcome outcome) => outcome switch
    {
        RiotDispatchAuditOutcome.Unknown => "UNKNOWN",
        RiotDispatchAuditOutcome.NotFound => "NOT_FOUND",
        RiotDispatchAuditOutcome.Armed => "ARMED",
        RiotDispatchAuditOutcome.Started => "STARTED",
        RiotDispatchAuditOutcome.Accepted => "ACCEPTED",
        RiotDispatchAuditOutcome.Confirmed => "CONFIRMED",
        RiotDispatchAuditOutcome.Terminal => "TERMINAL",
        RiotDispatchAuditOutcome.LegacyAuditUnavailable => "LEGACY_AUDIT_UNAVAILABLE",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    private static OrderIntentRow ToRow(OrderIntent intent) => new()
    {
        MovementLegId = intent.MovementLegId,
        DemandId = intent.DemandId,
        UpperId = intent.UpperId,
        Purpose = intent.Purpose,
        TargetStationId = intent.TargetStationId,
        VehicleKey = intent.VehicleKey,
        MapId = intent.MapId,
        DestinationStationId = intent.DestinationStationId,
        AgvLifecycleGeneration = intent.AgvLifecycleGeneration,
        DispatchGeneration = intent.DispatchGeneration,
        CreatedAt = intent.CreatedAt,
        DispatchAuditVersion = 1,
        DispatchAuditSequence = 0,
        CreateAttemptCount = 0
    };

    private static OrderIntent ToDomain(OrderIntentRow row) => new(
        row.MovementLegId,
        row.DemandId,
        row.UpperId,
        row.Purpose,
        row.TargetStationId,
        row.CreatedAt,
        row.VehicleKey,
        row.MapId,
        row.DestinationStationId,
        row.AgvLifecycleGeneration,
        row.DispatchGeneration);

    private static bool Matches(OrderIntentRow row, OrderIntent intent) =>
        row.MovementLegId == intent.MovementLegId &&
        row.DemandId == intent.DemandId &&
        row.UpperId == intent.UpperId &&
        row.Purpose == intent.Purpose &&
        row.TargetStationId == intent.TargetStationId &&
        row.VehicleKey == intent.VehicleKey &&
        row.MapId == intent.MapId &&
        row.DestinationStationId == intent.DestinationStationId &&
        row.AgvLifecycleGeneration == intent.AgvLifecycleGeneration &&
        row.DispatchGeneration == intent.DispatchGeneration &&
        row.CreatedAt == intent.CreatedAt;

    /// <summary>
    /// Whether a persisted journey is the same journey the caller is replaying. The stop and demand
    /// rows are part of the comparison because that is where the route, the slot reservation and the
    /// legs live now -- comparing the runtime row alone would accept a replay that plans a different
    /// itinerary for the same vehicle.
    /// </summary>
    private static bool Matches(
        JourneyRuntimeRow row,
        IReadOnlyCollection<JourneyStopRow> stops,
        IReadOnlyCollection<JourneyDemandRow> demands,
        JourneyExecutionPlan journey) =>
        row.JourneyId == journey.JourneyId &&
        row.AgvId == journey.AgvId &&
        row.VehicleKey == journey.VehicleKey &&
        row.AgvLifecycleGeneration == journey.AgvLifecycleGeneration &&
        row.MapId == journey.MapId &&
        row.MapIdentity == journey.MapIdentity &&
        row.DispatchZone == journey.DispatchZone &&
        row.GateStationId == journey.GateStationId &&
        row.GateStationRiotId == journey.GateStationRiotId &&
        row.OperationSessionId == journey.OperationSessionId &&
        row.DispatchGeneration == journey.DispatchGeneration &&
        stops.Count == journey.Stops.Count &&
        journey.Stops.All(plan => stops.Any(stop => Matches(stop, plan))) &&
        demands.Count == journey.Demands.Count &&
        journey.Demands.All(plan => demands.Any(demand => Matches(demand, plan)));

    private static bool Matches(JourneyStopRow row, JourneyStopPlan plan) =>
        row.Sequence == plan.Sequence &&
        row.Role == plan.Role &&
        row.StationId == plan.StationId &&
        row.StationRiotId == plan.StationRiotId &&
        row.RouteEvidenceId == plan.RouteEvidenceId &&
        row.MovementLegId == plan.MovementLegId &&
        row.UpperId == plan.UpperId &&
        row.LegType == plan.LegType;

    private static bool Matches(JourneyDemandRow row, JourneyDemandPlan plan) =>
        row.DemandId == plan.DemandId &&
        row.StopSequence == plan.StopSequence &&
        row.ExpectedBasketCount == plan.ExpectedBasketCount &&
        (JsonSerializer.Deserialize<int[]>(row.TargetSlotsJson) ?? []).SequenceEqual(plan.TargetSlots);

    /// <summary>
    /// Carries every snapshot revision on from the highest this vehicle has already published.
    /// </summary>
    /// <remarks>
    /// Onboard journals the adopted revision of each snapshot type in SQLite keyed on the message
    /// type alone -- no demand, no session -- and never deletes the row; on reconnect it restores
    /// that journal over its cleared in-memory copy. So the revision has to be monotonic per vehicle
    /// across the vehicle's whole life, which is what ADR-cross-0048 means by persisted by the
    /// server. A runtime row is created per demand and started every counter at 1, so the second
    /// journey on a vehicle re-published a revision Onboard had already adopted and had it refused
    /// as SNAPSHOT_REVISION_REGRESSION -- which raises a protocol problem and tears the session down.
    ///
    /// The journey's own revision fields are cursors, not counters: every stop takes the next value
    /// from them as it publishes, so the highest value already stored is the highest ever published
    /// and one past it is the first that is safe. That is why nothing is reserved per journey any
    /// more -- a journey whose stop sequence grows would outrun any fixed reservation.
    /// </remarks>
    private async Task SeedSnapshotRevisionsAsync(
        JourneyRuntimeRow runtime,
        CancellationToken cancellationToken)
    {
        var highest = await dbContext.JourneyRuntimes
            .Where(row => row.AgvId == runtime.AgvId)
            .GroupBy(row => row.AgvId)
            .Select(group => new
            {
                VehicleBusiness = group.Max(row => row.VehicleBusinessRevision),
                Worklist = group.Max(row => row.WorklistRevision),
                Plan = group.Max(row => row.PlanRevision)
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (highest is null)
        {
            return;
        }

        runtime.VehicleBusinessRevision = highest.VehicleBusiness + 1;
        runtime.WorklistRevision = highest.Worklist + 1;
        runtime.PlanRevision = highest.Plan + 1;
    }

    /// <summary>
    /// Records that one demand has reached its terminal state inside its journey, and releases the
    /// vehicle if that was the last one the journey was still carrying.
    /// </summary>
    /// <remarks>
    /// The lease belongs to the journey, so finishing one demand releases nothing while its siblings
    /// are still loaded or waiting to load -- the vehicle is physically carrying them. Both facts are
    /// written here, in this order, because a caller that set the state itself and then asked whether
    /// any sibling remains would query the database and read its own unsaved change as the old value.
    /// A demand accepted without a journey holds the lease under its own id and settles alone.
    /// </remarks>
    /// <summary>
    /// Records that one demand reached its terminal state inside its journey, releasing the vehicle
    /// only if it was the last one the journey carried. Returns whether the journey is now finished,
    /// which the caller needs because the state just written is not yet in the database.
    /// </summary>
    /// <remarks>
    /// Exposed for the recovery coordinator, which terminates demands from the peer's side and has
    /// to reach the same conclusion about the lease as the runtime does. It does not save: the
    /// coordinator writes this inside a larger unit of work.
    /// </remarks>
    public async Task<bool> SettleDemandInJourneyAsync(
        string demandId,
        JourneyDemandState terminalState,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        (await SettleJourneyDemandAsync(demandId, terminalState, at, cancellationToken)
            .ConfigureAwait(false)).JourneyComplete;

    private async Task<JourneySettlement> SettleJourneyDemandAsync(
        string demandId,
        JourneyDemandState terminalState,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        JourneyDemandRow? membership = await dbContext.JourneyDemands
            .SingleOrDefaultAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        string journeyId = membership?.JourneyId ?? demandId;
        if (membership is not null)
        {
            membership.State = terminalState;
            bool siblingsOpen = await dbContext.JourneyDemands.AnyAsync(
                row => row.JourneyId == journeyId &&
                       row.DemandId != demandId &&
                       (row.State == JourneyDemandState.Planned || row.State == JourneyDemandState.Loaded),
                cancellationToken).ConfigureAwait(false);
            if (siblingsOpen)
            {
                return new JourneySettlement(journeyId, membership.StopSequence, false);
            }
        }

        VehicleDispatchLeaseRow lease = await dbContext.VehicleDispatchLeases
            .SingleAsync(row => row.JourneyId == journeyId, cancellationToken).ConfigureAwait(false);
        lease.ReleasedAt ??= at;
        return new JourneySettlement(journeyId, membership?.StopSequence, true);
    }

    /// <summary>
    /// The outcome of settling one demand: which journey it belonged to, the stop it was loading at,
    /// and whether that was the last demand the journey carried. Callers need the last flag because
    /// the state they just wrote is not yet in the database, so asking again would read the old
    /// value.
    /// </summary>
    private sealed record JourneySettlement(string JourneyId, int? StopSequence, bool JourneyComplete);

    private static JourneyRuntimeRow ToRuntimeRow(JourneyExecutionPlan journey) => new()
    {
        JourneyId = journey.JourneyId,
        Stage = JourneyRuntimeStage.AwaitingPickupArrival,
        AgvId = journey.AgvId,
        VehicleKey = journey.VehicleKey,
        AgvLifecycleGeneration = journey.AgvLifecycleGeneration,
        MapId = journey.MapId,
        MapIdentity = journey.MapIdentity,
        DispatchZone = journey.DispatchZone,
        GateStationId = journey.GateStationId,
        GateStationRiotId = journey.GateStationRiotId,
        OperationSessionId = journey.OperationSessionId,
        DispatchGeneration = journey.DispatchGeneration,
        CurrentStopSequence = journey.Stops.Min(stop => stop.Sequence),
        VehicleBusinessRevision = 1,
        WorklistRevision = 1,
        PlanRevision = 1,
        CreatedAt = journey.CreatedAt,
        UpdatedAt = journey.CreatedAt
    };

    private static IEnumerable<JourneyStopRow> ToStopRows(
        JourneyRuntimeRow runtime,
        JourneyExecutionPlan journey) =>
        journey.Stops.Select(stop => new JourneyStopRow
        {
            JourneyId = journey.JourneyId,
            Sequence = stop.Sequence,
            Role = stop.Role,
            StationId = stop.StationId,
            StationRiotId = stop.StationRiotId,
            RouteEvidenceId = stop.RouteEvidenceId,
            MovementLegId = stop.MovementLegId,
            UpperId = stop.UpperId,
            LegType = stop.LegType,
            State = JourneyStopState.Planned,
            // Left at zero until the stop actually publishes. The journey's cursors are what say
            // which revision is next, and a stop that is planned but never reached must not consume
            // one -- Onboard would then see the next stop's revision jump, which is allowed, but the
            // gap would be indistinguishable from a snapshot it missed.
            VehicleBusinessRevision = 0,
            WorklistRevision = 0,
            PlanRevision = 0,
            VehicleBusinessMessageId = StopId(journey.JourneyId, stop.Sequence, "vehicle-state"),
            PlanMessageId = StopId(journey.JourneyId, stop.Sequence, "plan"),
            PreDepartureSafetyCheckMessageId = StopId(journey.JourneyId, stop.Sequence, "safety-request"),
            PreDepartureSafetyCheckId = StopId(journey.JourneyId, stop.Sequence, "safety-check"),
            LoadRound = 0,
            CreatedAt = runtime.CreatedAt,
            UpdatedAt = runtime.CreatedAt
        });

    private static IEnumerable<JourneyDemandRow> ToDemandRows(JourneyExecutionPlan journey) =>
        journey.Demands.Select(demand => new JourneyDemandRow
        {
            JourneyId = journey.JourneyId,
            DemandId = demand.DemandId,
            StopSequence = demand.StopSequence,
            ExpectedBasketCount = demand.ExpectedBasketCount,
            TargetSlotsJson = JsonSerializer.Serialize(demand.TargetSlots),
            LoadCommandMessageId = JourneyDemandId(journey.JourneyId, demand.DemandId, "load-command"),
            LoadSlotOperationAttemptId = JourneyDemandId(journey.JourneyId, demand.DemandId, "load-attempt"),
            UnloadCommandMessageId = JourneyDemandId(journey.JourneyId, demand.DemandId, "unload-command"),
            UnloadSlotOperationAttemptId = JourneyDemandId(journey.JourneyId, demand.DemandId, "unload-attempt"),
            State = JourneyDemandState.Planned,
            CreatedAt = journey.CreatedAt
        });

    /// <summary>
    /// The deterministic message and attempt ids a stop or a demand owns. They are derived from the
    /// journey's identity rather than a demand's so that two demands loaded at one stop, and one
    /// demand's two stops, cannot collide.
    /// </summary>
    public static string StopId(string journeyId, int sequence, string purpose) =>
        DeterministicGuid($"{journeyId}|stop-{sequence}|{purpose}");

    public static string JourneyDemandId(string journeyId, string demandId, string purpose) =>
        DeterministicGuid($"{journeyId}|demand-{demandId}|{purpose}");

    /// <summary>
    /// The ids of one load round at a stop: the worklist it publishes and the entry request that
    /// goes with it. A stop asks once per demand it still has to load, and the peer expires a
    /// request when the worklist revision changes, so the round is what keeps the second ask from
    /// reusing the first one's ids.
    /// </summary>
    public static string WorklistId(string journeyId, int sequence, int round) =>
        DeterministicGuid($"{journeyId}|stop-{sequence}|worklist-{round}");

    public static string SublotRequestId(string journeyId, int sequence, int round) =>
        DeterministicGuid($"{journeyId}|stop-{sequence}|sublot-request-{round}");

    private static string DeterministicGuid(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guidBytes = bytes.AsSpan(0, 16);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes).ToString("D");
    }

    private async Task RefreshOutboundEnvelopeAsync(
        ProtocolOutboxRow existing,
        string messageType,
        string candidateWire,
        DateTimeOffset candidateCreatedAt,
        CancellationToken cancellationToken)
    {
        if (existing.MessageType != messageType)
        {
            throw new ProtocolContentConflictException(
                "Outbound MessageId was replayed with a different message type.");
        }
        if (existing.PayloadJson == candidateWire)
        {
            return;
        }
        using JsonDocument storedDocument = JsonDocument.Parse(existing.PayloadJson);
        using JsonDocument candidateDocument = JsonDocument.Parse(candidateWire);
        JsonElement stored = storedDocument.RootElement;
        JsonElement candidate = candidateDocument.RootElement;
        long storedGeneration = stored.GetProperty("sessionGeneration").GetInt64();
        long candidateGeneration = candidate.GetProperty("sessionGeneration").GetInt64();
        bool sameSemanticMessage = SameString(stored, candidate, "protocolVersion") &&
                                   SameString(stored, candidate, "profileId") &&
                                   SameString(stored, candidate, "protocolReleaseVersion") &&
                                   SameString(stored, candidate, "protocolReleaseManifestSha256") &&
                                   SameString(stored, candidate, "messageType") &&
                                   SameString(stored, candidate, "messageId") &&
                                   SameString(stored, candidate, "correlationId") &&
                                   SameString(stored, candidate, "agvId") &&
                                   JsonNode.DeepEquals(
                                       JsonNode.Parse(stored.GetProperty("payload").GetRawText()),
                                       JsonNode.Parse(candidate.GetProperty("payload").GetRawText()));
        if (!sameSemanticMessage || candidateGeneration <= storedGeneration || existing.AcknowledgedAt is not null)
        {
            throw new ProtocolContentConflictException(
                "Outbound MessageId was replayed with different semantics or a non-advancing session generation.");
        }
        existing.PayloadJson = candidateWire;
        existing.CreatedAt = candidateCreatedAt;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool SameString(JsonElement left, JsonElement right, string propertyName) =>
        left.GetProperty(propertyName).GetRawText() == right.GetProperty(propertyName).GetRawText();


    private async Task<SessionRecoveryRow> GetCurrentSessionAsync(
        string agvId, long generation, CancellationToken cancellationToken)
    {
        SessionRecoveryRow row = await dbContext.SessionRecoveries
            .SingleAsync(item => item.AgvId == agvId, cancellationToken).ConfigureAwait(false);
        if (generation != row.SessionGeneration)
        {
            throw new StaleSessionGenerationException(
                $"Session generation {generation} does not match current generation {row.SessionGeneration}.");
        }
        return row;
    }

    private static void ValidateProtocolIdentity(SessionIdentity identity)
    {
        bool matches = identity.ProtocolCommit == ProtocolCandidateIdentity.RepositoryCommit &&
                       identity.ManifestSha256 == ProtocolCandidateIdentity.ManifestSha256 &&
                       identity.ProfileId == ProtocolCandidateIdentity.ProfileId &&
                       identity.ProtocolVersion == ProtocolCandidateIdentity.ProtocolVersion;
        if (!matches)
        {
            throw new ProtocolIdentityMismatchException("Session protocol identity does not match this build.");
        }
    }

    private static void ApplyRevision(
        long? currentRevision, string? currentHash, long revision, string contentHash, string kind)
    {
        if (currentRevision == revision && currentHash != contentHash)
        {
            throw new ProtocolContentConflictException($"{kind} revision {revision} has conflicting content.");
        }
        if (currentRevision > revision)
        {
            throw new ProtocolContentConflictException($"{kind} revision regressed from {currentRevision} to {revision}.");
        }
    }

    /// <summary>
    /// A slot operation cannot be carried out without unlocking a slot, and an unlocked slot is
    /// correctly reported as unsafe to depart. Treating that as a session failure made the command
    /// destroy its own precondition: the peer refused to continue the very operation this server
    /// asked for, and the journey could never leave the load stage.
    /// </summary>
    /// <remarks>
    /// The relaxation is deliberately narrow. It applies only while this server has an authorized
    /// operation in flight <em>on this vehicle</em>, only when every reported reason is one that
    /// operation explains, and never when any evidence was unknown. It changes session readiness alone -- departure itself
    /// is still authorized from a separate, freshness-bounded PreDepartureSafetyCheckResult in
    /// AuthorizeMovementAsync, which this does not touch.
    /// </remarks>
    private static readonly string[] OperationInducedUnsafety =
        ["LOCK_NOT_CLOSED", "UNLOCK_OUTPUT_NOT_RESET"];

    private async Task<bool> IsUnsafetyExplainedByOwnCommandAsync(
        SessionRecoveryRow row,
        CancellationToken cancellationToken)
    {
        if (row.SafetyUnknownPresent != false)
        {
            return false;
        }
        if (row.SafetyReasonCodesJson is null)
        {
            return false;
        }
        string[] reasonCodes = DeserializeStrings(row.SafetyReasonCodesJson);
        if (reasonCodes.Length == 0 ||
            !reasonCodes.All(code => OperationInducedUnsafety.Contains(code, StringComparer.Ordinal)))
        {
            return false;
        }
        // Scoped to this vehicle, by the same join DecideReadinessAsync already takes for
        // operationNeedsRecovery. The probe used to ask whether *any* station operation anywhere was
        // Prepared, so one vehicle mid-load handed every other vehicle an exemption it had not
        // earned: an idle car standing with a door ajar would read Ready for as long as any car in
        // the fleet held a slot open. A single-vehicle deployment can never show that; the fleet is
        // three.
        return await dbContext.StationOperations
            .Join(dbContext.JourneyDemands,
                operation => operation.DemandId,
                demand => demand.DemandId,
                (operation, demand) => new { operation, demand })
            .Join(dbContext.JourneyRuntimes,
                pair => pair.demand.JourneyId,
                runtime => runtime.JourneyId,
                (pair, runtime) => new { pair.operation, runtime })
            .AnyAsync(
                pair => pair.runtime.AgvId == row.AgvId &&
                        pair.operation.Status == StationOperationStatus.Prepared,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether anything the vehicle reported as outstanding at the handshake is still without a
    /// conclusion on this side. The report is written once per session and never revisited, so
    /// reading it as-is held the session out of Ready until the vehicle reconnected, however long
    /// ago the attempt had been answered (8005-agv-program#46).
    /// </summary>
    /// <remarks>
    /// An attempt counts as reconciled only by a conclusion the server holds and acted on: a
    /// current-generation OperationResult for it, or a recovery workflow on it that reached
    /// Reconciled. Never by time, and never by the server's own view of the operation: after a
    /// reconnect the onboard executor may still be running the attempt, and its report is
    /// word-for-word the report of a vehicle that restarted (8005-agv-program#40). What the server
    /// then decides about that conclusion is operationNeedsRecovery's business, not this one's.
    /// A reported result message is reconciled once the inbox has durably accepted it.
    /// </remarks>
    private async Task<bool> HasUnreconciledReportedFactsAsync(
        SessionRecoveryRow row,
        CancellationToken cancellationToken)
    {
        foreach (string attemptId in DeserializeStrings(row.PendingAttemptIdsJson))
        {
            bool answered = await dbContext.OperationResults
                                .AnyAsync(result => result.SlotOperationAttemptId == attemptId &&
                                                    result.AgvId == row.AgvId &&
                                                    !result.HistoricalOnly,
                                    cancellationToken).ConfigureAwait(false) ||
                            await dbContext.RecoveryWorkflows
                                .AnyAsync(workflow => workflow.SlotOperationAttemptId == attemptId &&
                                                      workflow.AgvId == row.AgvId &&
                                                      workflow.State == RecoveryWorkflowState.Reconciled,
                                    cancellationToken).ConfigureAwait(false);
            if (!answered) return true;
        }
        foreach (string messageId in DeserializeStrings(row.PendingResultIdsJson))
        {
            if (!await dbContext.ProtocolInbox.AnyAsync(item => item.MessageId == messageId, cancellationToken)
                    .ConfigureAwait(false))
                return true;
        }
        return false;
    }

    private static string GetRecoveryReason(
        SessionRecoveryRow row,
        bool noPendingFacts,
        bool departureUsable,
        bool operationNeedsRecovery)
    {
        if (row.CapabilityRevision is null) return "CAPABILITY_SNAPSHOT_REQUIRED";
        if (row.SafetyRevision is null) return "SAFETY_SNAPSHOT_REQUIRED";
        if (row.RecoveryReportId is null) return "RECOVERY_REPORT_REQUIRED";
        if (row.ReportedForcedRecoveryGeneration != row.ForcedRecoveryGeneration)
            return "FORCED_RECOVERY_GENERATION_MISMATCH";
        if (!noPendingFacts) return "PENDING_FACT_RECONCILIATION_REQUIRED";
        if (!departureUsable) return "DEPARTURE_SAFETY_NOT_READY";
        // Last of the specific reasons rather than first: the ones above are about facts the server
        // is still missing, and saying "an operation needs recovery" while the handshake is not even
        // complete would point the operator at the wrong thing.
        if (operationNeedsRecovery) return "OPERATION_RECOVERY_REQUIRED";
        return "RECOVERY_REQUIRED";
    }

    private static string SerializeSorted(IEnumerable<string> values) =>
        JsonSerializer.Serialize(values.Order(StringComparer.Ordinal));

    private static string[] DeserializeStrings(string json) =>
        JsonSerializer.Deserialize<string[]>(json) ?? [];

    private static int[] NormalizeSlots(IEnumerable<int> slots)
    {
        int[] normalized = slots.Distinct().Order().ToArray();
        if (normalized.Any(slot => slot is < 1 or > 8))
        {
            throw new BusinessIdentityConflictException("Slot numbers must be in 1..8.");
        }
        return normalized;
    }
}
