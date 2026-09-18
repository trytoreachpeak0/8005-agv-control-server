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
    /// <summary>
    /// A journey publishes its stored revision at the pickup stop and that value plus one at the
    /// gate stop (JourneyRuntimeEngine publishes both stops), so the next journey on the same
    /// vehicle has to start two above the stored one.
    /// </summary>
    private const long RevisionsPerJourney = 2;

    /// <summary>
    /// The plan stream takes three per journey rather than two. Since 2026-09-13 the plan also goes
    /// out before the pickup arrival (CV-DEMAND-ACCEPT-TO-PICKUP) at the stored revision, so the
    /// pickup and the gate publish it one and two above that.
    /// </summary>
    private const long PlanRevisionsPerJourney = 3;

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
        // Reset with the capability it arrived in. Left alone, the previous generation's report would
        // stand in for a vehicle that has not reported yet in this one -- and a restarted vehicle is
        // exactly the one whose configuration may have changed in between.
        row.ReportedSlotConfigurationFingerprint = null;
        row.SafetyRevision = null;
        row.SafetyHash = null;
        row.DepartureSafe = null;
        row.RecoveryReportId = null;
        row.PendingAttemptIdsJson = "[]";
        row.PendingResultIdsJson = "[]";
        row.Readiness = SessionReadiness.RecoveryRequired;
        row.ReasonCode = "HANDSHAKE_INCOMPLETE";
        row.UpdatedAt = DateTimeOffset.UtcNow;

        // ADR-cross-0055: a disconnect voids the station departure wait, and it is refilled in full
        // once the recovery handshake and the projection reconciliation are done. It is dropped here
        // for the reason the facts above are: a clock that kept running while the vehicle was gone is
        // not a fact about this session. Left running, a vehicle offline for longer than the wait came
        // back to a stop ended before anyone could scan, or to a correction window already spent.
        // Only voided, not refilled: the runtime refills it behind its readiness gate.
        JourneyRuntimeRow[] waiting = await dbContext.JourneyRuntimes
            .Where(item => item.AgvId == identity.AgvId && item.StationDepartureWaitStartedAt != null)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (JourneyRuntimeRow journey in waiting)
        {
            journey.StationDepartureWaitStartedAt = null;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyCapabilitySnapshotAsync(
        string agvId, long sessionGeneration, long revision, string contentHash,
        string? reportedSlotConfigurationFingerprint,
        CancellationToken cancellationToken)
    {
        SessionRecoveryRow row = await GetCurrentSessionAsync(agvId, sessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        ApplyRevision(row.CapabilityRevision, row.CapabilityHash, revision, contentHash, "capability");
        row.CapabilityRevision = revision;
        row.CapabilityHash = contentHash;
        row.ReportedSlotConfigurationFingerprint = reportedSlotConfigurationFingerprint;
        row.Readiness = SessionReadiness.RecoveryRequired;
        row.ReasonCode = "HANDSHAKE_INCOMPLETE";
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the current session already holds its safety baseline -- the SafetyStateSnapshot of its
    /// handshake. A later one is the vehicle answering SafetyStateSnapshotRequested mid-session
    /// (control-server#142), which is a safety change like any other and must not drop the session back
    /// into the handshake.
    /// </summary>
    public async Task<bool> HasSafetyBaselineAsync(
        string agvId, long sessionGeneration, CancellationToken cancellationToken)
    {
        SessionRecoveryRow row = await GetCurrentSessionAsync(agvId, sessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        return row.SafetyRevision is not null;
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
        // The report can name an attempt this server settled long ago, and nothing may arrive for it
        // afterwards: its result was accepted in an earlier session. Settling reported attempts only when
        // a result arrives left such a session on PENDING_FACT_RECONCILIATION_REQUIRED until some later
        // result or reconnect (8005-agv-program#61, residual of MVP 369919f5; 8005-agv-control-server#78).
        // The same rule applies here as there, so an attempt with no conclusion yet stays pending.
        await TryTakeOffSettledReportedAttemptsAsync(row, cancellationToken).ConfigureAwait(false);
        row.Readiness = SessionReadiness.RecoveryRequired;
        row.ReasonCode = "RECOVERY_RECONCILIATION_PENDING";
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes one result off the pending list the current session's RecoveryStateReport gave, once that
    /// result has arrived in this session.
    /// </summary>
    /// <remarks>
    /// Nothing did before 2026-09-13: the list was only ever replaced by the next report or emptied by
    /// the next handshake. It stayed harmless only because the vehicle always reported it empty.
    /// </remarks>
    public async Task ReconcileReportedPendingResultAsync(
        string agvId, long sessionGeneration, string resultMessageId, CancellationToken cancellationToken)
    {
        SessionRecoveryRow row = await GetCurrentSessionAsync(agvId, sessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        string[] pending = DeserializeStrings(row.PendingResultIdsJson);
        if (!pending.Contains(resultMessageId, StringComparer.Ordinal))
        {
            return;
        }

        row.PendingResultIdsJson = SerializeSorted(
            pending.Where(id => !string.Equals(id, resultMessageId, StringComparison.Ordinal)));
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes off the current session every attempt its RecoveryStateReport named as unsettled whose
    /// operation this server has since settled -- committed by a result or a resume, or cancelled by a
    /// reconciled cancellation, compensation or fault handoff.
    /// </summary>
    /// <remarks>
    /// The report is the vehicle's view at its handshake. Nothing changed it afterwards, so a session
    /// whose attempt a resume had just committed still read PENDING_FACT_RECONCILIATION_REQUIRED until
    /// the vehicle reconnected, and the journey waited on ONBOARD_SESSION_NOT_READY over a committed
    /// load (G3 FP-IS-07 resume-007, 2026-09-14). A determinate load failure (Failed) is as settled as a
    /// commit: its result is known and nothing about the slots is uncertain. An attempt whose operation is
    /// still Prepared or RecoveryRequired stays pending.
    /// </remarks>
    public async Task SettleReportedAttemptsAsync(
        string agvId, long sessionGeneration, CancellationToken cancellationToken)
    {
        SessionRecoveryRow row = await GetCurrentSessionAsync(agvId, sessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        if (!await TryTakeOffSettledReportedAttemptsAsync(row, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        row.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes off <paramref name="row"/> every reported attempt whose operation is committed, failed or cancelled,
    /// without saving: the caller saves. Returns whether anything was taken off.
    /// </summary>
    private async Task<bool> TryTakeOffSettledReportedAttemptsAsync(
        SessionRecoveryRow row, CancellationToken cancellationToken)
    {
        string[] pending = DeserializeStrings(row.PendingAttemptIdsJson);
        if (pending.Length == 0)
        {
            return false;
        }

        string[] settled = await dbContext.StationOperations
            .Where(operation => pending.Contains(operation.SlotOperationAttemptId) &&
                                (operation.Status == StationOperationStatus.Committed ||
                                 operation.Status == StationOperationStatus.Failed ||
                                 operation.Status == StationOperationStatus.Cancelled))
            .Select(operation => operation.SlotOperationAttemptId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (settled.Length == 0)
        {
            return false;
        }

        row.PendingAttemptIdsJson = SerializeSorted(pending.Except(settled, StringComparer.Ordinal));
        if (row.UnsettledSlotOperationAttemptId is not null &&
            settled.Contains(row.UnsettledSlotOperationAttemptId, StringComparer.Ordinal))
        {
            row.UnsettledSlotOperationAttemptId = null;
        }
        return true;
    }

    public async Task<SessionReadinessDecision> DecideReadinessAsync(
        string agvId, long sessionGeneration, CancellationToken cancellationToken)
    {
        SessionRecoveryRow row = await GetCurrentSessionAsync(agvId, sessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        bool noPendingFacts = DeserializeStrings(row.PendingAttemptIdsJson).Length == 0 &&
                              DeserializeStrings(row.PendingResultIdsJson).Length == 0;
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
            .Join(dbContext.JourneyRuntimes,
                operation => operation.DemandId,
                runtime => runtime.DemandId,
                (operation, runtime) => new { operation, runtime })
            .AnyAsync(
                pair => pair.runtime.AgvId == agvId &&
                        pair.operation.Status == StationOperationStatus.RecoveryRequired,
                cancellationToken).ConfigureAwait(false);
        // REQ-0316. The vehicle reported which slot configuration it is carrying; this server knows
        // which one it activated. Disagreement means nobody can say what the eight slots on that
        // vehicle will actually do, so it must not be given work -- but it stays connected, because
        // the only way to correct the disagreement is to send it an activation, and that needs the
        // session. Refusing the session instead (which this server did until 2026-09-10) left such a
        // vehicle permanently unreachable; see evidence/g3/20260910-fp-is-14-fingerprint-mismatch.
        //
        // A vehicle this server has never activated reports a fingerprint with nothing to compare
        // against, and that is not a disagreement: ReconcileReportedFingerprintAsync treats it as the
        // restoration-candidate question instead.
        ActiveSlotConfigurationRow? activeSlotConfiguration = await dbContext
            .Set<ActiveSlotConfigurationRow>().AsNoTracking()
            .FirstOrDefaultAsync(active => active.AgvId == agvId, cancellationToken)
            .ConfigureAwait(false);
        // No report yet is not a disagreement. The capability snapshot has not arrived in this session,
        // CapabilityRevision is null so the session is not ready anyway, and the honest reason for that
        // is HANDSHAKE_INCOMPLETE -- naming the fingerprint mismatch first would send an operator after
        // a vehicle whose only problem is that it is still handshaking.
        bool slotConfigurationAgrees = activeSlotConfiguration is null ||
            row.ReportedSlotConfigurationFingerprint is null ||
            string.Equals(
                activeSlotConfiguration.Fingerprint,
                row.ReportedSlotConfigurationFingerprint,
                StringComparison.Ordinal);
        bool ready = slotConfigurationAgrees &&
                     row.CapabilityRevision is not null && row.SafetyRevision is not null &&
                     row.RecoveryReportId is not null && departureUsable && noPendingFacts &&
                     !operationNeedsRecovery &&
                     row.ReportedForcedRecoveryGeneration == row.ForcedRecoveryGeneration;
        row.Readiness = ready ? SessionReadiness.Ready : SessionReadiness.RecoveryRequired;
        row.ReasonCode = ready
            ? "READY"
            : slotConfigurationAgrees
                ? GetRecoveryReason(row, noPendingFacts, departureUsable, operationNeedsRecovery)
                // Named first when it applies: every other reason here is about this session's own
                // progress, and an operator who reads one of those would go looking in the wrong
                // place for a vehicle whose configuration is simply not the approved one.
                : SlotConfigurationFingerprintVerdict.MismatchCode;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new SessionReadinessDecision(row.Readiness, row.ReasonCode);
    }

    /// <summary>
    /// Decides one ManualChargingReturnToServiceRequested, durably and at most once per
    /// <c>requestId</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server holds no manual-charging hold of its own -- <c>VehicleBusinessProjection.ManualChargingHold</c>
    /// is published as false from both sites that build it -- so the hold being lifted is the
    /// vehicle's, and this request is the vehicle asking the server to put it back into eligibility
    /// evaluation. The server's part is therefore to say whether it is in a position to evaluate the
    /// vehicle at all, and the only fact it holds that can answer no is the session's own readiness:
    /// a session in RecoveryRequired has facts to reconcile before the vehicle may take work again.
    /// </para>
    /// <para>
    /// The role check is here rather than left to the schema because neither end validates against
    /// the schema at runtime, so a role outside the enum reaches this method as an ordinary string.
    /// </para>
    /// </remarks>
    public async Task<ManualChargingReturnToServiceDecision> DecideManualChargingReturnToServiceAsync(
        ManualChargingReturnToServiceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ManualChargingReturnToServiceRow? replay = await dbContext.ManualChargingReturnToServiceRequests
            .SingleOrDefaultAsync(row => row.RequestId == request.RequestId, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.RequestContentHash != request.RequestContentHash || replay.AgvId != request.AgvId)
            {
                throw new ProtocolContentConflictException(
                    "ManualChargingReturnToService requestId was replayed with different content.");
            }

            return ToDecision(replay);
        }

        long revision = await dbContext.JourneyRuntimes
            .Where(row => row.AgvId == request.AgvId)
            .Select(row => (long?)row.VehicleBusinessRevision)
            .MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        SessionRecoveryRow session = await GetCurrentSessionAsync(
            request.AgvId, request.SessionGeneration, cancellationToken).ConfigureAwait(false);

        (string outcome, string? reasonCode, string? fieldPath, string? displayMessage) =
            request.AdministratorRole is not ("MAINTENANCE_ADMINISTRATOR" or "SYSTEM_ADMINISTRATOR")
                ? (ManualChargingReturnToServiceDecision.Rejected,
                    ServerReasonCodes.ProtocolSchemaInvalid,
                    "payload.administratorRole",
                    "administratorRole is not one of the roles the profile allows.")
                : session.Readiness == SessionReadiness.RecoveryRequired
                    ? (ManualChargingReturnToServiceDecision.Rejected,
                        ServerReasonCodes.SessionRecoveryRequired,
                        "payload.requestId",
                        "The session has facts to reconcile before the vehicle can take work again.")
                    : (ManualChargingReturnToServiceDecision.ReturnedToEligibilityEvaluation,
                        (string?)null, (string?)null, (string?)null);

        ManualChargingReturnToServiceRow row = new()
        {
            RequestId = request.RequestId,
            AgvId = request.AgvId,
            SessionGeneration = request.SessionGeneration,
            RequestMessageId = request.RequestMessageId,
            RequestContentHash = request.RequestContentHash,
            AdministratorId = request.AdministratorId,
            AdministratorRole = request.AdministratorRole,
            Reason = request.Reason,
            ObservedBatteryPercent = request.ObservedBatteryPercent,
            Outcome = outcome,
            ProblemReasonCode = reasonCode,
            ProblemFieldPath = fieldPath,
            ProblemDisplayMessage = displayMessage,
            VehicleBusinessStateRevision = revision,
            DecidedAt = DateTimeOffset.UtcNow
        };
        dbContext.ManualChargingReturnToServiceRequests.Add(row);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToDecision(row);
    }

    private static ManualChargingReturnToServiceDecision ToDecision(ManualChargingReturnToServiceRow row) =>
        new(row.Outcome, row.ProblemReasonCode, row.ProblemFieldPath, row.ProblemDisplayMessage,
            row.VehicleBusinessStateRevision);

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
                    .SingleOrDefaultAsync(row => row.DemandId == snapshot.DemandId, cancellationToken)
                    .ConfigureAwait(false);
                if (runtime is null || !Matches(runtime, journey) ||
                    !await AreaAssignmentFreezeMatchesAsync(snapshot, journey, cancellationToken).ConfigureAwait(false))
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
                $"Vehicle '{orderIntent.VehicleKey}' is already bound to unresolved demand '{activeLease.DemandId}'.");
        }

        if (journey?.AreaAssignmentVersion is long areaAssignmentVersion)
        {
            await FreezeAreaAssignmentAsync(snapshot, areaAssignmentVersion, cancellationToken).ConfigureAwait(false);
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
            DemandId = snapshot.DemandId,
            VehicleKey = orderIntent.VehicleKey,
            AcquiredAt = snapshot.AcceptedAt
        });
        dbContext.OrderIntents.Add(ToRow(orderIntent));
        if (journey is not null)
        {
            JourneyRuntimeRow runtimeRow = ToRuntimeRow(snapshot.DemandId, journey);
            await SeedSnapshotRevisionsAsync(runtimeRow, cancellationToken).ConfigureAwait(false);
            dbContext.JourneyRuntimes.Add(runtimeRow);
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
        VehicleDispatchLeaseRow lease = await dbContext.VehicleDispatchLeases
            .SingleAsync(row => row.DemandId == demandId, cancellationToken)
            .ConfigureAwait(false);
        lease.ReleasedAt ??= completedAt;
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
        Action<string>? equivalentReplayObserved = null,
        Func<string, Task<string?>>? equivalentReplayResponse = null)
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

            // An equivalent resend answered from its first acceptance rather than processed again. The
            // row keeps the bytes that were accepted; the caller decides whether the first response can
            // be answered that way at all, and null means it cannot.
            if (equivalentReplayResponse is not null)
            {
                return await equivalentReplayResponse(existing.FirstResponseJson).ConfigureAwait(false)
                    ?? throw new ProtocolContentConflictException(
                        "MessageId was replayed with different normalized content.");
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

    /// <param name="receipt">
    /// When the result arrived and the station deadline that stood then. Without it no failure is
    /// determinate (<see cref="DeterminateLoadFailure"/>): whether one may be settled turns on that
    /// deadline, and a result judged without one goes to recovery as before.
    /// </param>
    public async Task<OperationResultDisposition> ApplyOperationResultAsync(
        StationOperationResult result,
        string agvId,
        long forcedRecoveryGeneration,
        CancellationToken cancellationToken,
        OperationResultReceipt? receipt = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        int[] actualSlots = result.SlotEvidence.Select(item => item.SlotNumber).Distinct().Order().ToArray();
        OperationResultRow? replay = await dbContext.OperationResults
            .SingleOrDefaultAsync(row => row.ResultId == result.ResultId, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            // Not the wire hash: a result replayed in a later session is rebound to that session's
            // generation, so its line differs from the first one while the result does not. The inbox
            // has already refused anything else that differs (GenerationRebindReplayHash).
            bool same = replay.SlotOperationAttemptId == result.SlotOperationAttemptId &&
                        replay.AgvId == agvId &&
                        replay.ForcedRecoveryGeneration == forcedRecoveryGeneration &&
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
        OperationResultRow resultRow = new()
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
        };
        dbContext.OperationResults.Add(resultRow);
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

        // An operation a reconciled cancellation, compensation or fault handoff already terminated is
        // settled by that proof, not by this result. The load executor of a load cancelled mid-way
        // still gives up on its own timeout and reports UNKNOWN afterwards; judging that as an unsafe
        // result reopened the operation and the demand as RecoveryRequired and held the vehicle over
        // a load that had been proven empty (G3 FP-IS-02, 2026-09-13). Kept and acknowledged as the
        // record of what the vehicle said, changing nothing.
        if (operation.Status == StationOperationStatus.Cancelled)
        {
            resultRow.HistoricalOnly = true;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return OperationResultDisposition.HistoricalOnly;
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
            // ADR-cross-0058 decision 2 and 5: missing the target is not the same as not knowing what
            // happened. A load that ran out its station deadline and came back with every slot's state
            // known, door locked and unlock output reset is a complete account -- nobody handed the cargo
            // over -- and sending it to manual recovery is exactly what ADR-cross-0040 forbids. Only that
            // shape is lifted out; everything else still needs an administrator. Defensive: the v2
            // onboard never reports it, the protocol lets another onboard version do so.
            DeterminateLoadFailureVerdict verdict = DeterminateLoadFailure.Judge(result, expectedSlots, receipt);
            if (verdict == DeterminateLoadFailureVerdict.Settleable)
            {
                // The demand stays Accepted: the runtime ends it, together with the vehicle's release.
                operation.Status = StationOperationStatus.Failed;
                operation.EvidenceJson = evidenceJson;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return OperationResultDisposition.DeterminateFailure;
            }

            operation.Status = StationOperationStatus.RecoveryRequired;
            AcceptedDemandRow? blockedDemand = await dbContext.AcceptedDemands
                .SingleOrDefaultAsync(row => row.DemandId == result.DemandId, cancellationToken)
                .ConfigureAwait(false);
            if (blockedDemand is not null && blockedDemand.Status != DemandExecutionStatus.Succeeded)
            {
                blockedDemand.Status = DemandExecutionStatus.RecoveryRequired;
            }
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return verdict switch
            {
                DeterminateLoadFailureVerdict.BeforeStationDeadline =>
                    OperationResultDisposition.FailedBeforeStationDeadline,
                DeterminateLoadFailureVerdict.ReasonWithoutTerminalState =>
                    OperationResultDisposition.FailureReasonWithoutTerminalState,
                _ => OperationResultDisposition.RecoveryRequired
            };
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
        VehicleDispatchLeaseRow lease = await dbContext.VehicleDispatchLeases
            .SingleAsync(row => row.DemandId == result.DemandId, cancellationToken)
            .ConfigureAwait(false);
        lease.ReleasedAt ??= result.ObservedAt;
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
        // The replacement has to settle exactly what was authorized: the same demand and attempt, the
        // exact slot set, and the command the resume named. A replacement that widens, narrows or
        // redirects its scope is refused outright. Checked field by field since 2026-09-14, when the
        // resume command started carrying the resumed SlotOperationCommand's own content hash (the
        // vehicle refuses any other); before that this recomputed an action-scoped hash from the same
        // three facts.
        string? resumedCommandHash = await dbContext.StationOperations
            .Where(row => row.SlotOperationAttemptId == result.SlotOperationAttemptId)
            .Select(row => row.ContentHash)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        int[] authorizedSlots = (JsonSerializer.Deserialize<int[]>(authorization.SlotsJson) ?? [])
            .Distinct().Order().ToArray();
        if (authorization.DemandId != result.DemandId ||
            !authorizedSlots.SequenceEqual(actualSlots) ||
            resumedCommandHash is null ||
            authorization.CommandContentHash != resumedCommandHash)
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

    private static bool Matches(JourneyRuntimeRow row, JourneyExecutionPlan journey) =>
        row.AgvId == journey.AgvId &&
        row.VehicleKey == journey.VehicleKey &&
        row.AgvLifecycleGeneration == journey.AgvLifecycleGeneration &&
        row.MapId == journey.MapId &&
        row.MapIdentity == journey.MapIdentity &&
        row.DispatchZone == journey.DispatchZone &&
        row.RouteEvidenceId == journey.RouteEvidenceId &&
        row.PickupStationId == journey.PickupStationId &&
        row.PickupStationRiotId == journey.PickupStationRiotId &&
        row.GateStationId == journey.GateStationId &&
        row.GateStationRiotId == journey.GateStationRiotId &&
        row.ExpectedBasketCount == journey.ExpectedBasketCount &&
        (JsonSerializer.Deserialize<int[]>(row.TargetSlotsJson) ?? []).SequenceEqual(journey.TargetSlots) &&
        row.OperationSessionId == journey.OperationSessionId &&
        row.PickupMovementLegId == journey.PickupMovementLegId &&
        row.PickupUpperId == journey.PickupUpperId &&
        row.GateMovementLegId == journey.GateMovementLegId &&
        row.GateUpperId == journey.GateUpperId &&
        row.DispatchGeneration == journey.DispatchGeneration;

    /// <summary>
    /// Whether a plan's area assignment version and slot group are what the demand durably froze.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>JourneyRuntimes</c> has no column for either, deliberately: batch 7 rewrites that table's key, so
    /// control-server#66 put the frozen version in <c>ConfigurationConsumerBindings</c>, and control-server#72
    /// writes it in the same transaction as the journey row. That binding is the journey's record of
    /// <see cref="JourneyExecutionPlan.AreaAssignmentVersion"/>, and the slot group is recovered from it: a
    /// table version is immutable once written, so the group it assigns the demand's AREA is the group the
    /// plan was built with.
    /// </para>
    /// <para>
    /// No binding means nothing was frozen, and only a plan carrying neither field matches that. The binding
    /// is written inside the transaction that writes the accepted demand (<see cref="FreezeAreaAssignmentAsync"/>),
    /// so an accepted demand whose plan carried a version always has one: a crash between the two cannot leave
    /// the demand accepted and unfrozen, and so cannot make its replay read as a different acceptance.
    /// </para>
    /// </remarks>
    private async Task<bool> AreaAssignmentFreezeMatchesAsync(
        AcceptedDemandSnapshot snapshot,
        JourneyExecutionPlan journey,
        CancellationToken cancellationToken)
    {
        long? frozenVersion = (await new DemandAreaAssignmentFreezeStore(dbContext)
            .ReadAsync(snapshot.DemandId, cancellationToken)
            .ConfigureAwait(false))?.Version;
        if (frozenVersion != journey.AreaAssignmentVersion)
        {
            return false;
        }

        string? area = snapshot.LiveMesFields?.Area;
        string? frozenSlotPosition = frozenVersion is null || area is null
            ? null
            : await dbContext.Set<DispatchZoneAreaAssignmentRow>()
                .AsNoTracking()
                .Where(row => row.Version == frozenVersion.Value && row.Area == area)
                .Select(row => row.SlotPosition)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        return string.Equals(frozenSlotPosition, journey.RequiredSlotPosition, StringComparison.Ordinal);
    }

    /// <summary>
    /// Freezes the area assignment version the demand was evaluated against (REQ-0350), inside the acceptance
    /// transaction and ahead of the rows that accept it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same transaction, because the frozen version has to be the one the slot group was chosen from: frozen
    /// separately, a crash in between leaves an accepted demand whose replay no binding vouches for, and an
    /// import in between freezes a version other than the one the plan was built from.
    /// </para>
    /// <para>
    /// The current version is compared first, inside the transaction the acceptance holds. If a new version was
    /// imported after the round read the table, the demand was judged against a table that no longer governs
    /// the site — its door side may have changed — so nothing is accepted and
    /// <see cref="AreaAssignmentVersionChangedException"/> sends intake back to judge it next round.
    /// </para>
    /// </remarks>
    private async Task FreezeAreaAssignmentAsync(
        AcceptedDemandSnapshot snapshot,
        long evaluatedVersion,
        CancellationToken cancellationToken)
    {
        long? currentVersion = await dbContext.Set<DispatchZoneAreaAssignmentVersionRow>()
            .MaxAsync(row => (long?)row.Version, cancellationToken)
            .ConfigureAwait(false);
        if (currentVersion != evaluatedVersion)
        {
            throw new AreaAssignmentVersionChangedException(FormattableString.Invariant(
                $"Demand {snapshot.DemandId} was evaluated against area assignment version {evaluatedVersion}, but the current version is {currentVersion}."));
        }

        await new DemandAreaAssignmentFreezeStore(dbContext)
            .FreezeAsync(snapshot.DemandId, evaluatedVersion, snapshot.AcceptedAt, cancellationToken)
            .ConfigureAwait(false);
    }

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

        runtime.VehicleBusinessRevision = highest.VehicleBusiness + RevisionsPerJourney;
        runtime.WorklistRevision = highest.Worklist + RevisionsPerJourney;
        runtime.PlanRevision = highest.Plan + PlanRevisionsPerJourney;
    }

    private static JourneyRuntimeRow ToRuntimeRow(string demandId, JourneyExecutionPlan journey)
    {
        string Id(string purpose) => DeterministicGuid($"{demandId}|{purpose}");
        return new JourneyRuntimeRow
        {
            DemandId = demandId,
            Stage = JourneyRuntimeStage.AwaitingPickupArrival,
            AgvId = journey.AgvId,
            VehicleKey = journey.VehicleKey,
            AgvLifecycleGeneration = journey.AgvLifecycleGeneration,
            MapId = journey.MapId,
            MapIdentity = journey.MapIdentity,
            DispatchZone = journey.DispatchZone,
            RouteEvidenceId = journey.RouteEvidenceId,
            PickupStationId = journey.PickupStationId,
            PickupStationRiotId = journey.PickupStationRiotId,
            GateStationId = journey.GateStationId,
            GateStationRiotId = journey.GateStationRiotId,
            ExpectedBasketCount = journey.ExpectedBasketCount,
            TargetSlotsJson = JsonSerializer.Serialize(journey.TargetSlots),
            OperationSessionId = journey.OperationSessionId,
            PickupMovementLegId = journey.PickupMovementLegId,
            PickupUpperId = journey.PickupUpperId,
            GateMovementLegId = journey.GateMovementLegId,
            GateUpperId = journey.GateUpperId,
            DispatchGeneration = journey.DispatchGeneration,
            VehicleBusinessRevision = 1,
            WorklistRevision = 1,
            PlanRevision = 1,
            VehicleBusinessMessageId = Id("pickup-vehicle-state"),
            WorklistMessageId = Id("pickup-worklist"),
            PlanMessageId = Id("pickup-plan"),
            SublotRequestMessageId = Id("pickup-sublot-request"),
            LoadCommandMessageId = Id("load-command"),
            LoadSlotOperationAttemptId = Id("load-attempt"),
            PreDepartureSafetyCheckMessageId = Id("gate-safety-request"),
            PreDepartureSafetyCheckId = Id("gate-safety-check"),
            GateVehicleBusinessMessageId = Id("gate-vehicle-state"),
            GateWorklistMessageId = Id("gate-worklist"),
            GatePlanMessageId = Id("gate-plan"),
            UnloadCommandMessageId = Id("unload-command"),
            UnloadSlotOperationAttemptId = Id("unload-attempt"),
            CreatedAt = journey.CreatedAt,
            UpdatedAt = journey.CreatedAt
        };
    }

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
    /// operation in flight on this vehicle, only when every reported reason is one that operation explains, and
    /// never when any evidence was unknown. It changes session readiness alone -- departure itself
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
        // This vehicle's own operation, by the join operationNeedsRecovery takes in DecideReadinessAsync.
        // The probe used to ask whether any operation anywhere was Prepared, so one vehicle mid-load
        // handed every other vehicle an exemption it had not earned: an idle vehicle standing with a
        // door ajar read Ready for as long as any vehicle in the fleet held a slot open.
        return await dbContext.StationOperations
            .Join(dbContext.JourneyRuntimes,
                operation => operation.DemandId,
                runtime => runtime.DemandId,
                (operation, runtime) => new { operation, runtime })
            .AnyAsync(
                pair => pair.runtime.AgvId == row.AgvId &&
                        pair.operation.Status == StationOperationStatus.Prepared,
                cancellationToken)
            .ConfigureAwait(false);
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
