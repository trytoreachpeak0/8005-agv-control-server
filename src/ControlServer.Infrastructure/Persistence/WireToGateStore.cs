using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Application;
using ControlServer.Domain;
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
        CancellationToken cancellationToken)
    {
        SessionRecoveryRow row = await GetCurrentSessionAsync(agvId, sessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        ApplyRevision(row.SafetyRevision, row.SafetyHash, revision, contentHash, "safety");
        row.SafetyRevision = revision;
        row.SafetyHash = contentHash;
        row.DepartureSafe = departureSafe;
        row.Readiness = SessionReadiness.RecoveryRequired;
        row.ReasonCode = "HANDSHAKE_INCOMPLETE";
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyRecoveryReportAsync(
        string agvId, long sessionGeneration, string reportId, long forcedRecoveryGeneration,
        IReadOnlyCollection<string> pendingAttemptIds, IReadOnlyCollection<string> pendingResultIds,
        CancellationToken cancellationToken)
    {
        SessionRecoveryRow row = await GetCurrentSessionAsync(agvId, sessionGeneration, cancellationToken)
            .ConfigureAwait(false);
        row.RecoveryReportId = reportId;
        row.ForcedRecoveryGeneration = forcedRecoveryGeneration;
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
        bool noPendingFacts = DeserializeStrings(row.PendingAttemptIdsJson).Length == 0 &&
                              DeserializeStrings(row.PendingResultIdsJson).Length == 0;
        bool ready = row.CapabilityRevision is not null && row.SafetyRevision is not null &&
                     row.RecoveryReportId is not null && row.DepartureSafe == true && noPendingFacts;
        row.Readiness = ready ? SessionReadiness.Ready : SessionReadiness.RecoveryRequired;
        row.ReasonCode = ready ? "READY" : GetRecoveryReason(row, noPendingFacts);
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
                if (runtime is null || !Matches(runtime, journey))
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
            dbContext.JourneyRuntimes.Add(ToRuntimeRow(snapshot.DemandId, journey));
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
        return row is null
            ? null
            : new StoredMovementIntent(
                ToDomain(row),
                row.Status,
                row.OrderId);
    }

    public async Task MarkResultUnknownAsync(string upperId, CancellationToken cancellationToken)
    {
        OrderIntentRow row = await dbContext.OrderIntents
            .SingleAsync(item => item.UpperId == upperId, cancellationToken).ConfigureAwait(false);
        if (row.Status != "CONFIRMED")
        {
            row.Status = "RESULT_UNKNOWN";
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ConfirmAsync(string upperId, string orderId, CancellationToken cancellationToken)
    {
        OrderIntentRow row = await dbContext.OrderIntents
            .SingleAsync(item => item.UpperId == upperId, cancellationToken).ConfigureAwait(false);
        if (row.Status == "CONFIRMED" && row.OrderId != orderId)
        {
            throw new BusinessIdentityConflictException("The stable upperId was reconciled to a different orderId.");
        }
        row.Status = "CONFIRMED";
        row.OrderId = orderId;
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
        CancellationToken cancellationToken)
    {
        ProtocolInboxRow? existing = await dbContext.ProtocolInbox
            .SingleOrDefaultAsync(row => row.MessageId == messageId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.ContentHash != contentHash)
            {
                throw new ProtocolContentConflictException("MessageId was replayed with different normalized content.");
            }
            return existing.FirstResponseJson;
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
            .Where(row => row.AcknowledgedAt == null)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Where(row =>
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            return document.RootElement.GetProperty("agvId").GetString() == agvId;
        }).OrderBy(row => row.CreatedAt).ThenBy(row => row.MessageId, StringComparer.Ordinal).ToArray();
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
                       row.ForcedRecoveryGeneration == forcedRecoveryGeneration,
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
        OperationResultRow? replay = await dbContext.OperationResults
            .SingleOrDefaultAsync(
                row => row.ResultId == result.ResultId ||
                       row.SlotOperationAttemptId == result.SlotOperationAttemptId &&
                       row.ForcedRecoveryGeneration == forcedRecoveryGeneration,
                cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            bool same = replay.ResultId == result.ResultId &&
                        replay.SlotOperationAttemptId == result.SlotOperationAttemptId &&
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
        int[] actualSlots = result.SlotEvidence.Select(item => item.SlotNumber).Distinct().Order().ToArray();
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
            operation.Status = StationOperationStatus.RecoveryRequired;
            AcceptedDemandRow? blockedDemand = await dbContext.AcceptedDemands
                .SingleOrDefaultAsync(row => row.DemandId == result.DemandId, cancellationToken)
                .ConfigureAwait(false);
            if (blockedDemand is not null && blockedDemand.Status != DemandExecutionStatus.Succeeded)
            {
                blockedDemand.Status = DemandExecutionStatus.RecoveryRequired;
            }
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return OperationResultDisposition.RecoveryRequired;
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
        CreatedAt = intent.CreatedAt
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

    private static string GetRecoveryReason(SessionRecoveryRow row, bool noPendingFacts)
    {
        if (row.CapabilityRevision is null) return "CAPABILITY_SNAPSHOT_REQUIRED";
        if (row.SafetyRevision is null) return "SAFETY_SNAPSHOT_REQUIRED";
        if (row.RecoveryReportId is null) return "RECOVERY_REPORT_REQUIRED";
        if (!noPendingFacts) return "PENDING_FACT_RECONCILIATION_REQUIRED";
        if (row.DepartureSafe != true) return "DEPARTURE_SAFETY_NOT_READY";
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
