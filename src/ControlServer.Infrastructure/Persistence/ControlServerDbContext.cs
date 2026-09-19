using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

public sealed class ControlServerDbContext(DbContextOptions<ControlServerDbContext> options) : DbContext(options)
{
    public DbSet<AcceptedDemandRow> AcceptedDemands => Set<AcceptedDemandRow>();
    public DbSet<VehicleDispatchLeaseRow> VehicleDispatchLeases => Set<VehicleDispatchLeaseRow>();
    public DbSet<OrderIntentRow> OrderIntents => Set<OrderIntentRow>();
    public DbSet<RiotDispatchAuditEventRow> RiotDispatchAuditEvents => Set<RiotDispatchAuditEventRow>();
    public DbSet<ExperimentalRiotCreateAuthorizationRow> ExperimentalRiotCreateAuthorizations =>
        Set<ExperimentalRiotCreateAuthorizationRow>();
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
    public DbSet<ExceptionRecoverySessionRow> ExceptionRecoverySessions => Set<ExceptionRecoverySessionRow>();
    public DbSet<RecoveryWorkflowRow> RecoveryWorkflows => Set<RecoveryWorkflowRow>();
    public DbSet<ManualChargingReturnToServiceRow> ManualChargingReturnToServiceRequests =>
        Set<ManualChargingReturnToServiceRow>();
    public DbSet<HardwareRecoveryRecordRow> HardwareRecoveryRecords => Set<HardwareRecoveryRecordRow>();
    public DbSet<RecoveryResultEvidenceRow> RecoveryResultEvidence => Set<RecoveryResultEvidenceRow>();
    public DbSet<JourneyBacklogRow> JourneyBacklog => Set<JourneyBacklogRow>();
    public DbSet<JourneyRuntimeRow> JourneyRuntimes => Set<JourneyRuntimeRow>();
    public DbSet<AdmissionPolicyStateRow> AdmissionPolicyState => Set<AdmissionPolicyStateRow>();
    public DbSet<StationTaskTypeAdmissionRow> StationTaskTypeAdmissions => Set<StationTaskTypeAdmissionRow>();
    public DbSet<AdmissionPolicyAuditRow> AdmissionPolicyAudit => Set<AdmissionPolicyAuditRow>();
    public DbSet<AdmissionDecisionSnapshotRow> AdmissionDecisionSnapshots => Set<AdmissionDecisionSnapshotRow>();
    public DbSet<PackageCapacityRuleRow> PackageCapacityRules => Set<PackageCapacityRuleRow>();
    public DbSet<MissingPackageRow> MissingPackages => Set<MissingPackageRow>();

    // 批次 2 轨 B 的持久化地基（票 06）。四条能力轨的表在一次 migration 里落齐，
    // 票 09／10／11／12／13 因此零 migration、零 Ports.cs 改动。
    public DbSet<VehicleTaskTypeAdmissionRow> VehicleTaskTypeAdmissions => Set<VehicleTaskTypeAdmissionRow>();
    public DbSet<DispatchZoneVehicleRow> DispatchZoneVehicles => Set<DispatchZoneVehicleRow>();
    public DbSet<VehicleDispatchBudgetRow> VehicleDispatchBudgets => Set<VehicleDispatchBudgetRow>();
    public DbSet<RouteGraphSnapshotRow> RouteGraphSnapshots => Set<RouteGraphSnapshotRow>();
    public DbSet<RouteGraphEdgeRow> RouteGraphEdges => Set<RouteGraphEdgeRow>();
    public DbSet<RouteGraphStationRow> RouteGraphStations => Set<RouteGraphStationRow>();
    public DbSet<RouteGraphRemovedEdgeRow> RouteGraphRemovedEdges => Set<RouteGraphRemovedEdgeRow>();
    public DbSet<RouteGraphRemovedStationRow> RouteGraphRemovedStations => Set<RouteGraphRemovedStationRow>();
    public DbSet<RouteGraphEdgeGroupRow> RouteGraphEdgeGroups => Set<RouteGraphEdgeGroupRow>();
    public DbSet<VehicleFaultStateRow> VehicleFaultStates => Set<VehicleFaultStateRow>();
    public DbSet<FaultedVehicleCargoRow> FaultedVehicleCargo => Set<FaultedVehicleCargoRow>();
    public DbSet<RiotOrderCommandAuditRow> RiotOrderCommandAudit => Set<RiotOrderCommandAuditRow>();
    public DbSet<MapStationCatalogStateRow> MapStationCatalogStates => Set<MapStationCatalogStateRow>();
    public DbSet<FrozenDemandStationRow> FrozenDemandStations => Set<FrozenDemandStationRow>();
    public DbSet<CreateGateAuditRow> CreateGateAudit => Set<CreateGateAuditRow>();

    /// <summary>
    /// How long audit records are protected from deletion. Defaults to the REQ-0271 floor of 180
    /// days; the host binds the configured value over it. Changing it never permits an update.
    /// </summary>
    public AuditRetentionPolicy AuditRetention { get; set; } = AuditRetentionPolicy.Default;

    /// <summary>The clock the retention check reads.</summary>
    public TimeProvider AuditClock { get; set; } = TimeProvider.System;

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
        modelBuilder.Entity<OrderIntentRow>().Property(row => row.Status).IsConcurrencyToken();
        modelBuilder.Entity<OrderIntentRow>().Property(row => row.CreateAttemptCount).IsConcurrencyToken();
        modelBuilder.Entity<OrderIntentRow>().Property(row => row.DispatchAuditSequence).IsConcurrencyToken();
        modelBuilder.Entity<OrderIntentRow>().Property(row => row.ExperimentalCreateAuthorizationId).IsConcurrencyToken();
        modelBuilder.Entity<OrderIntentRow>()
            .HasIndex(row => row.CreateAttemptId)
            .IsUnique()
            .HasFilter("CreateAttemptId IS NOT NULL");
        modelBuilder.Entity<OrderIntentRow>()
            .HasIndex(row => row.ExperimentalCreateAuthorizationId)
            .IsUnique()
            .HasFilter("ExperimentalCreateAuthorizationId IS NOT NULL");
        modelBuilder.Entity<RiotDispatchAuditEventRow>().HasKey(row => row.AuditEventId);
        modelBuilder.Entity<RiotDispatchAuditEventRow>()
            .HasIndex(row => new { row.MovementLegId, row.Sequence })
            .IsUnique();
        modelBuilder.Entity<RiotDispatchAuditEventRow>().HasIndex(row => row.AttemptId);
        modelBuilder.Entity<RiotDispatchAuditEventRow>().HasIndex(row => row.ExperimentalAuthorizationId);
        modelBuilder.Entity<ExperimentalRiotCreateAuthorizationRow>().HasKey(row => row.AuthorizationId);
        modelBuilder.Entity<ExperimentalRiotCreateAuthorizationRow>()
            .HasIndex(row => row.UpperId)
            .IsUnique();
        modelBuilder.Entity<ExperimentalRiotCreateAuthorizationRow>()
            .HasIndex(row => row.ConsumedByAttemptId)
            .IsUnique()
            .HasFilter("ConsumedByAttemptId IS NOT NULL");
        modelBuilder.Entity<ExperimentalRiotCreateAuthorizationRow>()
            .Property(row => row.ConsumedByAttemptId)
            .IsConcurrencyToken();
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
        // One live result per attempt per generation. A result superseded by an authorized
        // RESUME_AFTER_REPAIR replacement stays in the table as the record of what the vehicle
        // reported when it failed, and steps out of the uniqueness scope rather than being erased.
        modelBuilder.Entity<OperationResultRow>()
            .HasIndex(row => new { row.SlotOperationAttemptId, row.ForcedRecoveryGeneration })
            .IsUnique()
            .HasFilter("SupersededByResultId IS NULL");
        modelBuilder.Entity<RecoveryDecisionRow>().HasKey(row => row.RecoveryActionId);
        modelBuilder.Entity<ExceptionRecoverySessionRow>().HasKey(row => row.ExceptionRecoverySessionId);
        modelBuilder.Entity<ExceptionRecoverySessionRow>().HasIndex(row => row.RequestId).IsUnique();
        modelBuilder.Entity<RecoveryWorkflowRow>().HasKey(row => row.WorkflowId);
        modelBuilder.Entity<RecoveryWorkflowRow>().Property(row => row.State).HasConversion<string>();
        modelBuilder.Entity<RecoveryWorkflowRow>().HasIndex(row => row.CommandMessageId).IsUnique();
        modelBuilder.Entity<ManualChargingReturnToServiceRow>().HasKey(row => row.RequestId);
        modelBuilder.Entity<ManualChargingReturnToServiceRow>().HasIndex(row => row.RequestMessageId).IsUnique();
        modelBuilder.Entity<HardwareRecoveryRecordRow>().HasKey(row => row.RecordId);
        modelBuilder.Entity<RecoveryResultEvidenceRow>().HasKey(row => row.MessageId);
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
        modelBuilder.Entity<PackageCapacityRuleRow>().HasKey(row => row.RuleId);
        modelBuilder.Entity<PackageCapacityRuleRow>()
            .HasIndex(row => new { row.Pattern, row.MatchType })
            .IsUnique()
            .HasFilter("SupersededAt IS NULL");
        modelBuilder.Entity<PackageCapacityRuleRow>().HasData(PackageCapacitySeed.Rows);
        modelBuilder.Entity<MissingPackageRow>().HasKey(row => row.Package);

        Batch2CapabilityModel.Configure(modelBuilder);
        // 批次 3 的表全部走每实体一个 IEntityTypeConfiguration<T>，放在 Persistence/Configurations
        // 下——加一张表是加一个文件，不是在这里再加一段。上面那些手写配置是 v2 线既有的，两种写法
        // 并存：程序集扫描只会捡到 Configurations/ 里的那些，不会碰上面任何一行。
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ControlServerDbContext).Assembly);
    }

    // Audit immutability lives here rather than in the stores that write audit, so that it is a
    // property of the context every caller already goes through instead of a rule each new caller
    // has to remember.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AuditImmutabilityGuard.Enforce(ChangeTracker, AuditRetention, AuditClock.GetUtcNow());
        PublishedVersionImmutabilityGuard.Enforce(ChangeTracker);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        AuditImmutabilityGuard.Enforce(ChangeTracker, AuditRetention, AuditClock.GetUtcNow());
        PublishedVersionImmutabilityGuard.Enforce(ChangeTracker);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
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
    public int? DispatchAuditVersion { get; set; }
    public long? DispatchAuditSequence { get; set; }
    public string? ExperimentalCreateAuthorizationId { get; set; }
    public string? CreateAttemptId { get; set; }
    public int? CreateAttemptCount { get; set; }
    public DateTimeOffset? CreateDispatchArmedAt { get; set; }
    public string? LastCreateOutcome { get; set; }
    public DateTimeOffset? LastCreateOutcomeAt { get; set; }
    public string? LastCreateReceiptJson { get; set; }
    public string? LastReconciliationOutcome { get; set; }
    public DateTimeOffset? LastReconciliationOutcomeAt { get; set; }
    public string? LastReconciliationReceiptJson { get; set; }

    // Vehicle-occupancy uniqueness moved down from the lease table (specification 5.1).
    // Nothing writes these yet — see Batch2CapabilityModel for why the index is filtered on
    // ClaimedAt being set, and why that keeps current behaviour unchanged.
    public DateTimeOffset? VehicleOccupancyClaimedAt { get; set; }
    public DateTimeOffset? VehicleOccupancyReleasedAt { get; set; }
}

public sealed class RiotDispatchAuditEventRow
{
    public required string AuditEventId { get; set; }
    public required string MovementLegId { get; set; }
    public required string DemandId { get; set; }
    public required string UpperId { get; set; }
    public long DispatchGeneration { get; set; }
    public long Sequence { get; set; }
    public string? AttemptId { get; set; }
    public int? AttemptNumber { get; set; }
    public required string Phase { get; set; }
    public required string Outcome { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? RequestSemanticSha256 { get; set; }
    public string? ReceiptOperation { get; set; }
    public string? ReceiptClassification { get; set; }
    public DateTimeOffset? ReceiptObservedAt { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? BusinessCode { get; set; }
    public bool? ResultPresent { get; set; }
    public string? ReturnedOrderId { get; set; }
    public string? FailureCategory { get; set; }
    public string? ExperimentalAuthorizationId { get; set; }
    public string? EligibilityBasis { get; set; }
}

public sealed class ExperimentalRiotCreateAuthorizationRow
{
    public required string AuthorizationId { get; set; }
    public int AuthorizationVersion { get; set; }
    public required string UpperId { get; set; }
    public required string DemandId { get; set; }
    public required string MovementLegId { get; set; }
    public long AgvLifecycleGeneration { get; set; }
    public long DispatchGeneration { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset PersistedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public string? ConsumedByAttemptId { get; set; }
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

    /// <summary>
    /// 车在最近一份 <c>CapabilitySnapshot</c> 里报的 <c>activeSlotConfigurationFingerprint</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 记下来而不是当场拒收，是 2026-09-10 改的。此前不一致就回 <c>ProtocolProblem</c>、会话不建立，
    /// 理由是 fail-closed；G3 跑出来的后果是一台被动过配置的车永远上不了线，**而唯一能把它改回来的
    /// 手段——下发一次激活——要走会话**。不一致本身堵死了修复不一致的那条路，人必须到车前。
    /// </para>
    /// <para>
    /// 现在会话照建，这一份指纹存在这里，由 <c>DecideReadinessAsync</c> 与服务端认定的那一版比对：
    /// 不一致的车拿不到业务就绪、不会被派活，但连接在，激活下得去。fail-closed 的实质保住了——
    /// 服务端不确定车装着什么就不给它干活——关掉的只是「连都不让连」那一层。
    /// </para>
    /// </remarks>
    public string? ReportedSlotConfigurationFingerprint { get; set; }
    public string? CapabilityHash { get; set; }
    public long? SafetyRevision { get; set; }
    public string? SafetyHash { get; set; }
    public bool? DepartureSafe { get; set; }

    /// <summary>
    /// Why the peer reported the vehicle unsafe to depart, and whether any of its evidence was
    /// unknown. Both were discarded before, which left the session unable to tell unsafety this
    /// server's own in-flight slot operation causes from unsafety that must fail the session.
    /// </summary>
    public string? SafetyReasonCodesJson { get; set; }

    public bool? SafetyUnknownPresent { get; set; }
    public string? RecoveryReportId { get; set; }
    public long ForcedRecoveryGeneration { get; set; }
    public long ReportedForcedRecoveryGeneration { get; set; }
    public string PendingAttemptIdsJson { get; set; } = "[]";
    public string PendingResultIdsJson { get; set; } = "[]";
    public string? UnsettledSlotOperationAttemptId { get; set; }
    public string? ProvenRecoveryCheckpoint { get; set; }
    public string ActiveUnlockSlotsJson { get; set; } = "[]";
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
    public DateTimeOffset? FencedAt { get; set; }
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
    public string? SupersededByResultId { get; set; }
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

public sealed class ExceptionRecoverySessionRow
{
    public required string ExceptionRecoverySessionId { get; set; }
    public required string RequestId { get; set; }
    public required string RequestContentHash { get; set; }
    public required string AgvId { get; set; }
    public required string EventId { get; set; }
    public string? DemandId { get; set; }
    public required string SlotsJson { get; set; }
    public required string AdministratorId { get; set; }
    public required string AdministratorRole { get; set; }
    public required string Reason { get; set; }
    public required string State { get; set; }
    public long Revision { get; set; }
    public string? SelectedAction { get; set; }
    public long ForcedRecoveryGeneration { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One ManualChargingReturnToServiceRequested and the conclusion the server reached about it.
/// </summary>
/// <remarks>
/// The row exists for business idempotency: the protocol's <c>businessDedupKeys</c> for both
/// messages of the pair is <c>requestId</c>, so the same request arriving under a new
/// <c>messageId</c> must return the conclusion already reached rather than decide again. The
/// transport-level replay (same <c>messageId</c>) is handled a layer above by the protocol inbox.
/// </remarks>
public sealed class ManualChargingReturnToServiceRow
{
    public required string RequestId { get; set; }
    public required string AgvId { get; set; }
    public long SessionGeneration { get; set; }
    public required string RequestMessageId { get; set; }
    public required string RequestContentHash { get; set; }
    public required string AdministratorId { get; set; }
    public required string AdministratorRole { get; set; }
    public required string Reason { get; set; }
    public double? ObservedBatteryPercent { get; set; }
    public required string Outcome { get; set; }
    public string? ProblemReasonCode { get; set; }
    public string? ProblemFieldPath { get; set; }
    public string? ProblemDisplayMessage { get; set; }
    public long VehicleBusinessStateRevision { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}

public sealed class RecoveryWorkflowRow
{
    public required string WorkflowId { get; set; }
    public required string WorkflowType { get; set; }
    public string? ExceptionRecoverySessionId { get; set; }
    public required string AgvId { get; set; }
    public string? DemandId { get; set; }
    public string? SlotOperationAttemptId { get; set; }
    public required string SlotsJson { get; set; }
    public long ForcedRecoveryGeneration { get; set; }
    public RecoveryWorkflowState State { get; set; }
    public required string RequestMessageId { get; set; }
    public required string RequestContentHash { get; set; }
    public string? CommandMessageId { get; set; }
    public string? CommandMessageType { get; set; }
    public string? CommandContentHash { get; set; }
    public string? HandoffId { get; set; }
    public string? ResultMessageId { get; set; }
    public string? ResultContentHash { get; set; }
    public string? Outcome { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class HardwareRecoveryRecordRow
{
    public required string RecordId { get; set; }
    public required string ExceptionRecoverySessionId { get; set; }
    public required string RecoveryActionId { get; set; }
    public required string ContentHash { get; set; }
    public required string OperatorId { get; set; }
    public required string AdministratorRole { get; set; }
    public required string SlotsJson { get; set; }
    public required string ChecksJson { get; set; }
    public required string ActionsJson { get; set; }
    public required string ObservationsJson { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class RecoveryResultEvidenceRow
{
    public required string MessageId { get; set; }
    public required string WorkflowId { get; set; }
    public required string MessageType { get; set; }
    public long ForcedRecoveryGeneration { get; set; }
    public required string ContentHash { get; set; }
    public required string Outcome { get; set; }
    public bool HistoricalOnly { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
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
    /// <summary>
    /// Why the journey is not advancing, or null. Written only through <see cref="SetBlockReason"/>,
    /// which keeps <see cref="BlockReasonSince"/> in step with it.
    /// </summary>
    public string? BlockReasonCode { get; private set; }
    /// <summary>
    /// When the current station departure wait began, by this server's clock: at the pickup arrival,
    /// again at the load commit, and again whenever a load correction closes or a new session is
    /// ready after a disconnect voided it (ADR-cross-0055). Null before the arrival, after the
    /// vehicle is sent on, and between a disconnect and the readiness that refills it.
    /// </summary>
    public DateTimeOffset? StationDepartureWaitStartedAt { get; set; }
    /// <summary>
    /// When <see cref="BlockReasonCode"/> took its current value, by this server's clock
    /// (control-server#80, program#55). <see cref="UpdatedAt"/> could not say it: every later write
    /// to the row moves that one, so a block that had held for half an hour read as a minute old.
    /// Null when the code is null, and for a block already held when the column was added, whose start
    /// nobody recorded.
    /// </summary>
    public DateTimeOffset? BlockReasonSince { get; private set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// The one way to write <see cref="BlockReasonCode"/>: the first write of a code records when it
    /// began, writing the code it already holds keeps that time, a different code starts it over, and
    /// clearing the code clears it.
    /// </summary>
    /// <remarks>
    /// The setter is private so that a write which bypasses this is a compile error rather than a
    /// block whose start is silently wrong -- the escalation schedule of program#55 is measured from
    /// this time, and a missed write site would reset or never start it.
    /// </remarks>
    public void SetBlockReason(string? reasonCode, DateTimeOffset now)
    {
        if (string.Equals(BlockReasonCode, reasonCode, StringComparison.Ordinal))
        {
            return;
        }
        BlockReasonCode = reasonCode;
        BlockReasonSince = reasonCode is null ? null : now;
    }

    /// <summary>
    /// Names the block a journey already holds more precisely, keeping when it began: the same wait escalated, not a new
    /// one (control-server#198). The escalation schedule of program#55 is measured from <see cref="BlockReasonSince"/>,
    /// and restarting it here would send a block that has already climbed the ladder back to its lowest tier.
    /// </summary>
    public void EscalateBlockReason(string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        if (BlockReasonCode is null)
        {
            throw new InvalidOperationException("Only a block the journey already holds can be escalated.");
        }
        BlockReasonCode = reasonCode;
    }
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

public sealed class PackageCapacityRuleRow
{
    public required string RuleId { get; set; }
    public required string Pattern { get; set; }
    public required string MatchType { get; set; }
    public int MaxBoxesPerBasket { get; set; }
    public int Version { get; set; }
    public required string Source { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
}

public sealed class MissingPackageRow
{
    public required string Package { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public required string Status { get; set; }
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
