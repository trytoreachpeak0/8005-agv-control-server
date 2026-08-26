using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

public sealed class ControlServerDbContext(DbContextOptions<ControlServerDbContext> options) : DbContext(options)
{
    public DbSet<AcceptedDemandRow> AcceptedDemands => Set<AcceptedDemandRow>();
    public DbSet<VehicleDispatchLeaseRow> VehicleDispatchLeases => Set<VehicleDispatchLeaseRow>();
    public DbSet<OrderIntentRow> OrderIntents => Set<OrderIntentRow>();
    public DbSet<SessionRecoveryRow> SessionRecoveries => Set<SessionRecoveryRow>();
    public DbSet<ProtocolInboxRow> ProtocolInbox => Set<ProtocolInboxRow>();
    public DbSet<ProtocolOutboxRow> ProtocolOutbox => Set<ProtocolOutboxRow>();
    public DbSet<StationOperationRow> StationOperations => Set<StationOperationRow>();
    public DbSet<UnloadBatchRow> UnloadBatches => Set<UnloadBatchRow>();
    public DbSet<StopClosureRow> StopClosures => Set<StopClosureRow>();
    public DbSet<TransportDemandCompletionRow> TransportDemandCompletions => Set<TransportDemandCompletionRow>();
    public DbSet<ConnectionRecoveryRow> ConnectionRecoveries => Set<ConnectionRecoveryRow>();
    public DbSet<VehicleRecoveryGenerationRow> VehicleRecoveryGenerations => Set<VehicleRecoveryGenerationRow>();
    public DbSet<OperationResultRow> OperationResults => Set<OperationResultRow>();
    public DbSet<RecoveryDecisionRow> RecoveryDecisions => Set<RecoveryDecisionRow>();
    public DbSet<JourneyBacklogRow> JourneyBacklog => Set<JourneyBacklogRow>();
    public DbSet<JourneyRuntimeRow> JourneyRuntimes => Set<JourneyRuntimeRow>();
    public DbSet<AdmissionPolicyStateRow> AdmissionPolicyState => Set<AdmissionPolicyStateRow>();
    public DbSet<StationTaskTypeAdmissionRow> StationTaskTypeAdmissions => Set<StationTaskTypeAdmissionRow>();
    public DbSet<AdmissionPolicyAuditRow> AdmissionPolicyAudit => Set<AdmissionPolicyAuditRow>();
    public DbSet<AdmissionDecisionSnapshotRow> AdmissionDecisionSnapshots => Set<AdmissionDecisionSnapshotRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AcceptedDemandRow>().HasKey(row => row.DemandId);
        modelBuilder.Entity<AcceptedDemandRow>().HasIndex(row => row.TransportDemandKey).IsUnique();
        modelBuilder.Entity<AcceptedDemandRow>().Property(row => row.Status).HasConversion<string>();
        modelBuilder.Entity<VehicleDispatchLeaseRow>().HasKey(row => row.DemandId);
        modelBuilder.Entity<VehicleDispatchLeaseRow>()
            .HasIndex(row => row.VehicleKey)
            .IsUnique()
            .HasFilter("ReleasedAt IS NULL");
        modelBuilder.Entity<OrderIntentRow>().HasKey(row => row.MovementLegId);
        modelBuilder.Entity<OrderIntentRow>().HasIndex(row => row.UpperId).IsUnique();
        modelBuilder.Entity<SessionRecoveryRow>().HasKey(row => row.AgvId);
        modelBuilder.Entity<SessionRecoveryRow>().Property(row => row.Readiness).HasConversion<string>();
        modelBuilder.Entity<ProtocolInboxRow>().HasKey(row => row.MessageId);
        modelBuilder.Entity<ProtocolOutboxRow>().HasKey(row => row.MessageId);
        modelBuilder.Entity<StationOperationRow>().HasKey(row => row.SlotOperationAttemptId);
        modelBuilder.Entity<StationOperationRow>().Property(row => row.Status).HasConversion<string>();
        modelBuilder.Entity<StationOperationRow>().Property(row => row.OperationType).HasConversion<string>();
        modelBuilder.Entity<UnloadBatchRow>().HasKey(row => row.UnloadBatchId);
        modelBuilder.Entity<StopClosureRow>().HasKey(row => row.DemandId);
        modelBuilder.Entity<TransportDemandCompletionRow>().HasKey(row => row.TransportDemandKey);
        modelBuilder.Entity<TransportDemandCompletionRow>().HasIndex(row => row.DemandId).IsUnique();
        modelBuilder.Entity<ConnectionRecoveryRow>().HasKey(row => row.AgvId);
        modelBuilder.Entity<ConnectionRecoveryRow>().Property(row => row.Status).HasConversion<string>();
        modelBuilder.Entity<VehicleRecoveryGenerationRow>().HasKey(row => row.AgvId);
        modelBuilder.Entity<OperationResultRow>().HasKey(row => row.ResultId);
        modelBuilder.Entity<OperationResultRow>()
            .HasIndex(row => new { row.SlotOperationAttemptId, row.ForcedRecoveryGeneration })
            .IsUnique();
        modelBuilder.Entity<RecoveryDecisionRow>().HasKey(row => row.RecoveryActionId);
        modelBuilder.Entity<JourneyBacklogRow>().HasKey(row => row.DemandId);
        modelBuilder.Entity<JourneyBacklogRow>().HasIndex(row => row.TransportDemandKey);
        modelBuilder.Entity<JourneyRuntimeRow>().HasKey(row => row.DemandId);
        modelBuilder.Entity<JourneyRuntimeRow>().Property(row => row.Stage).HasConversion<string>();
        modelBuilder.Entity<AdmissionPolicyStateRow>().HasKey(row => row.Id);
        modelBuilder.Entity<AdmissionPolicyStateRow>().Property(row => row.Id).ValueGeneratedNever();
        modelBuilder.Entity<StationTaskTypeAdmissionRow>().HasKey(row => new { row.StationId, row.TaskType });
        modelBuilder.Entity<AdmissionPolicyAuditRow>().HasKey(row => row.Version);
        modelBuilder.Entity<AdmissionPolicyAuditRow>().Property(row => row.Version).ValueGeneratedNever();
        modelBuilder.Entity<AdmissionDecisionSnapshotRow>().HasKey(row => row.SlotOperationAttemptId);
    }
}

public sealed class AcceptedDemandRow
{
    public required string DemandId { get; set; }
    public required string SeriesId { get; set; }
    public required string TransportDemandKey { get; set; }
    public required string WorkType { get; set; }
    public required string Sublot { get; set; }
    public int Generation { get; set; }
    public long DemandRevision { get; set; }
    public required string HistoryEpoch { get; set; }
    public long CatalogRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ValueObservedAt { get; set; }
    public required string ValuePollTraceId { get; set; }
    public required string ValueProjectionCommitId { get; set; }
    public required string LiveMesFieldsJson { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public DemandExecutionStatus Status { get; set; }
}

public sealed class VehicleDispatchLeaseRow
{
    public required string DemandId { get; set; }
    public required string VehicleKey { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
}

public sealed class OrderIntentRow
{
    public required string MovementLegId { get; set; }
    public required string DemandId { get; set; }
    public required string UpperId { get; set; }
    public required string Purpose { get; set; }
    public required string TargetStationId { get; set; }
    public string VehicleKey { get; set; } = "";
    public int MapId { get; set; }
    public int DestinationStationId { get; set; }
    public long AgvLifecycleGeneration { get; set; }
    public long DispatchGeneration { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = "PENDING_RECONCILIATION";
    public string? OrderId { get; set; }
}

public sealed class SessionRecoveryRow
{
    public required string AgvId { get; set; }
    public long SessionGeneration { get; set; }
    public required string ProtocolCommit { get; set; }
    public required string ManifestSha256 { get; set; }
    public required string ProfileId { get; set; }
    public int ProtocolVersion { get; set; }
    public long? CapabilityRevision { get; set; }
    public string? CapabilityHash { get; set; }
    public long? SafetyRevision { get; set; }
    public string? SafetyHash { get; set; }
    public bool? DepartureSafe { get; set; }
    public string? RecoveryReportId { get; set; }
    public long ForcedRecoveryGeneration { get; set; }
    public string PendingAttemptIdsJson { get; set; } = "[]";
    public string PendingResultIdsJson { get; set; } = "[]";
    public SessionReadiness Readiness { get; set; }
    public string ReasonCode { get; set; } = "HANDSHAKE_INCOMPLETE";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ProtocolInboxRow
{
    public required string MessageId { get; set; }
    public required string MessageType { get; set; }
    public required string RequestJson { get; set; }
    public required string ContentHash { get; set; }
    public required string FirstResponseJson { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class ProtocolOutboxRow
{
    public required string MessageId { get; set; }
    public required string MessageType { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
}

public sealed class StationOperationRow
{
    public required string SlotOperationAttemptId { get; set; }
    public required string DemandId { get; set; }
    public required string SublotId { get; set; }
    public required string TargetSlotsJson { get; set; }
    public SlotOperationType OperationType { get; set; }
    public long ForcedRecoveryGeneration { get; set; }
    public required string ContentHash { get; set; }
    public StationOperationStatus Status { get; set; }
    public string? EvidenceJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CommittedAt { get; set; }
}

public sealed class UnloadBatchRow
{
    public required string UnloadBatchId { get; set; }
    public required string DemandId { get; set; }
    public required string EvidenceJson { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}

public sealed class StopClosureRow
{
    public required string DemandId { get; set; }
    public DateTimeOffset CommittedAt { get; set; }
}

public sealed class TransportDemandCompletionRow
{
    public required string TransportDemandKey { get; set; }
    public required string DemandId { get; set; }
    public long DemandRevision { get; set; }
    public required string Evidence { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}

public sealed class ConnectionRecoveryRow
{
    public required string AgvId { get; set; }
    public long SessionGeneration { get; set; }
    public string ActiveUnlockSetJson { get; set; } = "[]";
    public ConnectionRecoveryStatus Status { get; set; }
    public bool ResumeAuthorized { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class VehicleRecoveryGenerationRow
{
    public required string AgvId { get; set; }
    public long ForcedRecoveryGeneration { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class OperationResultRow
{
    public required string ResultId { get; set; }
    public required string SlotOperationAttemptId { get; set; }
    public required string AgvId { get; set; }
    public long ForcedRecoveryGeneration { get; set; }
    public required string ContentHash { get; set; }
    public required string ResultContentSha256 { get; set; }
    public required string OverallOutcome { get; set; }
    public required string EvidenceJson { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public bool HistoricalOnly { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class RecoveryDecisionRow
{
    public required string RecoveryActionId { get; set; }
    public required string RecoverySessionId { get; set; }
    public long ForcedRecoveryGeneration { get; set; }
    public required string ContentHash { get; set; }
    public required string Decision { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}

public sealed class JourneyBacklogRow
{
    public required string DemandId { get; set; }
    public required string TransportDemandKey { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset DemandCreatedAt { get; set; }
    public required string DecisionFingerprint { get; set; }
    public required string ReasonCode { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
}

public sealed class JourneyRuntimeRow
{
    public required string DemandId { get; set; }
    public JourneyRuntimeStage Stage { get; set; }
    public required string AgvId { get; set; }
    public required string VehicleKey { get; set; }
    public long AgvLifecycleGeneration { get; set; }
    public int MapId { get; set; }
    public required string MapIdentity { get; set; }
    public required string DispatchZone { get; set; }
    public required string RouteEvidenceId { get; set; }
    public required string PickupStationId { get; set; }
    public int PickupStationRiotId { get; set; }
    public required string GateStationId { get; set; }
    public int GateStationRiotId { get; set; }
    public int ExpectedBasketCount { get; set; }
    public required string TargetSlotsJson { get; set; }
    public required string OperationSessionId { get; set; }
    public required string PickupMovementLegId { get; set; }
    public required string PickupUpperId { get; set; }
    public required string GateMovementLegId { get; set; }
    public required string GateUpperId { get; set; }
    public long DispatchGeneration { get; set; }
    public long VehicleBusinessRevision { get; set; }
    public long WorklistRevision { get; set; }
    public long PlanRevision { get; set; }
    public required string VehicleBusinessMessageId { get; set; }
    public required string WorklistMessageId { get; set; }
    public required string PlanMessageId { get; set; }
    public required string SublotRequestMessageId { get; set; }
    public required string LoadCommandMessageId { get; set; }
    public required string LoadSlotOperationAttemptId { get; set; }
    public required string PreDepartureSafetyCheckMessageId { get; set; }
    public required string PreDepartureSafetyCheckId { get; set; }
    public required string GateVehicleBusinessMessageId { get; set; }
    public required string GateWorklistMessageId { get; set; }
    public required string GatePlanMessageId { get; set; }
    public required string UnloadCommandMessageId { get; set; }
    public required string UnloadSlotOperationAttemptId { get; set; }
    public string? ConsumedSublotMessageId { get; set; }
    public string? ConsumedSafetyResultMessageId { get; set; }
    public string? BlockReasonCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AdmissionPolicyStateRow
{
    public int Id { get; set; }
    public long Version { get; set; }
    public required string DeploymentId { get; set; }
    public required string ContentHash { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}

public sealed class StationTaskTypeAdmissionRow
{
    public required string StationId { get; set; }
    public required string TaskType { get; set; }
    public long PolicyVersion { get; set; }
}

public sealed class AdmissionPolicyAuditRow
{
    public long Version { get; set; }
    public required string DeploymentId { get; set; }
    public string? PreviousContentHash { get; set; }
    public required string ContentHash { get; set; }
    public required string RelationsJson { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}

public sealed class AdmissionDecisionSnapshotRow
{
    public required string SlotOperationAttemptId { get; set; }
    public required string StationId { get; set; }
    public required string TaskType { get; set; }
    public long AdmissionPolicyVersion { get; set; }
    public DateTimeOffset AdmittedAt { get; set; }
    public bool Allowed { get; set; }
}

public sealed class DemandAcceptanceStore(ControlServerDbContext dbContext) : IDemandAcceptanceStore
{
    public async Task AcceptWithOrderIntentAsync(
        AcceptedDemandSnapshot snapshot,
        OrderIntent orderIntent,
        CancellationToken cancellationToken)
    {
        WireToGateStore store = new(dbContext);
        await store.AcceptWithOrderIntentAsync(snapshot, orderIntent, cancellationToken).ConfigureAwait(false);
    }
}
