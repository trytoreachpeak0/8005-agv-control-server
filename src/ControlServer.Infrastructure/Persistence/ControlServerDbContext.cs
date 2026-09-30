using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ControlServer.Infrastructure.Persistence;

public sealed class ControlServerDbContext(DbContextOptions<ControlServerDbContext> options) : DbContext(options)
{
    public DbSet<AcceptedDemandRow> AcceptedDemands => Set<AcceptedDemandRow>();
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
    public DbSet<OwnOrderRebuildRow> OwnOrderRebuilds => Set<OwnOrderRebuildRow>();
    public DbSet<ForeignRiotOrderRow> ForeignRiotOrders => Set<ForeignRiotOrderRow>();

    /// <summary>
    /// How long audit records are protected from deletion. Defaults to the REQ-0271 floor of 180
    /// days; the host binds the configured value over it. Changing it never permits an update.
    /// </summary>
    public AuditRetentionPolicy AuditRetention { get; set; } = AuditRetentionPolicy.Default;

    /// <summary>The clock the retention check reads.</summary>
    public TimeProvider AuditClock { get; set; } = TimeProvider.System;

    /// <summary>
    /// The journey whose tracked row the work under way was decided from; null when there is none (control-server#357).
    /// While it is set, every save re-checks that row's <see cref="JourneyRuntimeRow.Version"/> and raises it, even a save
    /// that changes nothing on the row.
    /// </summary>
    /// <remarks>
    /// The runtime sets it for one vehicle's advance. Most of what an advance writes is not the journey row -- an order intent
    /// before the RIoT create, an outbound message before it goes out -- and a token on the journey row alone would not stop
    /// those: the inbound could block the journey after the round read it, and the order intent would still be saved and the
    /// order created. With the row guarded, the first save after such a commit fails before anything leaves this server.
    /// <para>
    /// The price: while it is set, a save that would otherwise change nothing on the journey row still updates it (the version).
    /// The movement dispatch takes it off just before an external side effect (after the pre-create reconciliation audit,
    /// before the at-most-once create counter is spent: <c>IMovementIntentStore.ReleaseJourneyGuardBeforeExternalEffect</c>),
    /// so the saves that record that effect are never thrown away by it; the row's own changes after that still meet the token.
    /// </para>
    /// </remarks>
    public string? GuardedJourneyId { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AcceptedDemandRow>().HasKey(row => row.DemandId);
        modelBuilder.Entity<AcceptedDemandRow>().HasIndex(row => row.TransportDemandKey).IsUnique();
        modelBuilder.Entity<AcceptedDemandRow>().Property(row => row.Status).HasConversion<string>();
        modelBuilder.Entity<OrderIntentRow>().HasKey(row => row.MovementLegId);
        modelBuilder.Entity<OrderIntentRow>().HasIndex(row => row.UpperId).IsUnique();
        modelBuilder.Entity<OrderIntentRow>().Property(row => row.Status).IsConcurrencyToken();
        modelBuilder.Entity<OrderIntentRow>().Property(row => row.CreateAttemptCount).IsConcurrencyToken();
        modelBuilder.Entity<OrderIntentRow>().Property(row => row.DispatchAuditSequence).IsConcurrencyToken();
        modelBuilder.Entity<OrderIntentRow>().Property(row => row.ExperimentalCreateAuthorizationId).IsConcurrencyToken();
        // The database default is what the migration back-fills existing rows with (control-server#399).
        modelBuilder.Entity<OrderIntentRow>().Property(row => row.OrderShape).HasDefaultValue(OrderShapes.SingleMove);
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
        // Batch 7 (control-server#206): keyed on the journey. DemandId stays as the anchor demand -- the first one the
        // journey accepted -- with an ordinary index: after a redispatch (control-server#215) the same demand may anchor
        // a second journey, so the index is deliberately not unique.
        modelBuilder.Entity<JourneyRuntimeRow>().HasKey(row => row.JourneyId);
        modelBuilder.Entity<JourneyRuntimeRow>().HasIndex(row => row.DemandId);
        modelBuilder.Entity<JourneyRuntimeRow>().Property(row => row.Stage).HasConversion<string>();
        // control-server#357: raised on every save by StampJourneyVersions, never by a writer.
        modelBuilder.Entity<JourneyRuntimeRow>().Property(row => row.Version).IsConcurrencyToken();
        // Batch 8 (control-server#386): an idle return is a journey without a demand (choice A), so the anchor demand and the
        // columns only a transport has become nullable in the database. The CLR properties stay non-nullable on purpose:
        // until batch 8-18/8-19 (control-server#389, #390) change a type and meet every reader the compiler then names, no
        // code can write a null here, and nothing any engine path writes changes.
        foreach (string column in IdleReturnNullableColumns.JourneyRuntimes)
        {
            modelBuilder.Entity<JourneyRuntimeRow>().Property(column).IsRequired(false);
        }
        modelBuilder.Entity<OrderIntentRow>().Property(row => row.DemandId).IsRequired(false);
        modelBuilder.Entity<RiotDispatchAuditEventRow>().Property(row => row.DemandId).IsRequired(false);
        modelBuilder.Entity<ExperimentalRiotCreateAuthorizationRow>().Property(row => row.DemandId).IsRequired(false);
        // An idle return rides on the journey so that fault supervision and the own-order rebuild reach it too; the
        // rebuild's record names the journey's anchor demand.
        modelBuilder.Entity<OwnOrderRebuildRow>().Property(row => row.DemandId).IsRequired(false);
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
    /// <summary>
    /// Keeps <see cref="JourneyRuntimeRow.WaitingSince"/> in step with every journey row this save adds or changes
    /// (control-server#273), by the one definition of waiting in <see cref="JourneyWaitClassification"/>. Here rather than
    /// at the writers for the reason the audit guard below is: a rule each writer had to remember would be broken by the
    /// next one.
    /// </summary>
    private void ReconcileJourneyWaits()
    {
        ChangeTracker.DetectChanges();
        foreach (EntityEntry<JourneyRuntimeRow> entry in ChangeTracker.Entries<JourneyRuntimeRow>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }
            JourneyRuntimeRow row = entry.Entity;
            // Arriving is a new wait: the vehicle has moved since whatever wait the leg carried (incremental review of
            // #320). Blocked is the exception -- a leg is blocked where it stands.
            if (entry.State == EntityState.Modified &&
                row.Stage != JourneyRuntimeStage.Blocked &&
                JourneyWaitClassification.Of(row.Stage) == JourneyStageActivity.Stationary &&
                JourneyWaitClassification.Of(entry.Property(item => item.Stage).OriginalValue) == JourneyStageActivity.Travelling)
            {
                row.ReconcileWait(waiting: false, row.UpdatedAt);
            }
            DateTimeOffset startedAt = JourneyWaitClassification.Of(row.Stage) == JourneyStageActivity.Travelling
                ? row.BlockReasonSince ?? row.UpdatedAt
                : row.UpdatedAt;
            row.ReconcileWait(JourneyWaitClassification.IsWaiting(row.Stage, row.BlockReasonCode), startedAt);
        }
    }

    /// <summary>
    /// Raises <see cref="JourneyRuntimeRow.Version"/> on every journey row this save changes, and on the
    /// <see cref="GuardedJourneyId"/> row even when the save changes nothing on it (control-server#357). After
    /// <see cref="ReconcileJourneyWaits"/>, so the columns it adds count as a change. Computed from the original value, so a
    /// save retried after it failed raises it by one, not two.
    /// </summary>
    private void StampJourneyVersions()
    {
        foreach (EntityEntry<JourneyRuntimeRow> entry in ChangeTracker.Entries<JourneyRuntimeRow>())
        {
            bool guarded = entry.State == EntityState.Unchanged &&
                           string.Equals(entry.Entity.JourneyId, GuardedJourneyId, StringComparison.Ordinal);
            if (entry.State == EntityState.Modified || guarded)
            {
                PropertyEntry<JourneyRuntimeRow, long> version = entry.Property(row => row.Version);
                version.CurrentValue = version.OriginalValue + 1;
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ReconcileJourneyWaits();
        StampJourneyVersions();
        AuditImmutabilityGuard.Enforce(ChangeTracker, AuditRetention, AuditClock.GetUtcNow());
        PublishedVersionImmutabilityGuard.Enforce(ChangeTracker);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ReconcileJourneyWaits();
        StampJourneyVersions();
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

public sealed class OrderIntentRow
{
    public required string MovementLegId { get; set; }
    public string? DemandId { get; set; }
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

    /// <summary>
    /// Which kind of RIoT order this intent stands for (<see cref="OrderShapes"/>; batch 9, control-server#399). Every
    /// existing writer leaves it at <see cref="OrderShapes.SingleMove"/>, the value the migration back-fills. No CHECK: the
    /// values are validated in code (control-server#401).
    /// </summary>
    public string OrderShape { get; set; } = OrderShapes.SingleMove;
}

public sealed class RiotDispatchAuditEventRow
{
    public required string AuditEventId { get; set; }
    public required string MovementLegId { get; set; }
    public string? DemandId { get; set; }
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

    /// <summary>
    /// When the starvation escalation for this demand was raised (control-server#214), so that it is raised once per
    /// demand: this row is keyed on the demand and survives the demand leaving the catalogue, which only changes its
    /// reason code. Null until then; batch 7's schema ticket (control-server#206) adds it and writes nothing.
    /// </summary>
    public DateTimeOffset? StarvationEscalatedAt { get; set; }

    /// <summary>The per-zone dispatch parameter version the escalation was decided under, or null.</summary>
    public long? StarvationEscalationParameterVersion { get; set; }
}

public sealed class JourneyRuntimeRow
{
    /// <summary>
    /// The journey's identity and, since batch 7 (control-server#206), the key. A single-demand journey's is
    /// <c>journey:{DemandId}</c>, the same value the migration back-fills, so a back-filled row and a newly accepted
    /// one cannot be told apart.
    /// </summary>
    public required string JourneyId { get; set; }

    /// <summary>
    /// The anchor demand: the first demand the journey accepted. Every existing reader still finds the row by it. Null on an
    /// idle return (batch 8-19, control-server#390), which is a journey without a demand (<see cref="IsIdleReturn"/>); so are
    /// the gate, slot, load, unload, sublot and pre-departure columns only a transport has.
    /// </summary>
    /// <remarks>
    /// A reader that can only ever meet a transport journey reads these through <see cref="TransportColumn"/>, which throws on
    /// null rather than letting an idle return walk into transport code with a blank demand. A reader that may meet either
    /// kind asks <see cref="IsIdleReturn"/> first.
    /// </remarks>
    public string? DemandId { get; set; }

    /// <summary>
    /// Whether this journey is an idle return: a move to a waiting point with no demand (batch 8-19, control-server#390). Its
    /// id is the one its commitment was made under (<c>IdleReturnIdentity.JourneyIdPrefix</c>), which is what tells it apart;
    /// a null <see cref="DemandId"/> is a consequence, not the test.
    /// </summary>
    public bool IsIdleReturn() => JourneyId.StartsWith(IdleReturnIdentity.JourneyIdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// A column only a transport journey has, read where only a transport journey can be: throws when it is null, which on
    /// this row means the caller was handed an idle return (control-server#390) -- a defect to surface, never a blank to use.
    /// </summary>
    public string TransportColumn(
        string? value,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string column = "") =>
        value ?? throw new InvalidDataException(
            $"Journey '{JourneyId}' has no {column}: only a transport journey carries it, and this one " +
            (IsIdleReturn() ? "is an idle return." : "is missing it."));
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
    public string? GateStationId { get; set; }
    public int GateStationRiotId { get; set; }
    public int ExpectedBasketCount { get; set; }
    public string? TargetSlotsJson { get; set; }
    public required string OperationSessionId { get; set; }
    public required string PickupMovementLegId { get; set; }
    public required string PickupUpperId { get; set; }
    public string? GateMovementLegId { get; set; }
    public string? GateUpperId { get; set; }
    public long DispatchGeneration { get; set; }
    public long VehicleBusinessRevision { get; set; }
    public long WorklistRevision { get; set; }
    public long PlanRevision { get; set; }
    public required string VehicleBusinessMessageId { get; set; }
    public required string WorklistMessageId { get; set; }
    public required string PlanMessageId { get; set; }
    public string? SublotRequestMessageId { get; set; }
    public string? LoadCommandMessageId { get; set; }
    public string? LoadSlotOperationAttemptId { get; set; }
    public string? PreDepartureSafetyCheckMessageId { get; set; }
    public string? PreDepartureSafetyCheckId { get; set; }
    public string? GateVehicleBusinessMessageId { get; set; }
    public string? GateWorklistMessageId { get; set; }
    public string? GatePlanMessageId { get; set; }
    public string? UnloadCommandMessageId { get; set; }
    public string? UnloadSlotOperationAttemptId { get; set; }
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

    /// <summary>
    /// When the stop at the AREA machine was first held because the station no longer admits the demand's task type, by
    /// this server's clock (control-server#228). The escalation of control-server#198 is measured from it. Kept apart from
    /// <see cref="BlockReasonSince"/> on purpose: that one restarts whenever another code is written -- a failed order, a
    /// checkpoint wait -- and the wait for the admission has not restarted then. Written with the first hold, in the same
    /// save; cleared only when the admission returns and the stop moves on to its unload. A restart or a reconnect leaves
    /// it as it is.
    /// </summary>
    /// <remarks>
    /// The setter is private for the same reason <see cref="BlockReasonSince"/>'s is: a write that bypasses
    /// <see cref="HoldForAreaEndAdmission"/> or <see cref="ReleaseAreaEndAdmissionHold"/> is a compile error rather than a
    /// wait that silently starts over. Restarting it is exactly the defect control-server#228 exists to fix.
    /// </remarks>
    public DateTimeOffset? AreaEndAdmissionRevokedSince { get; private set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // The loading phase (REQ-0354, REQ-0355, ADR-cross-0057). All nullable and none written by control-server#206, which
    // only lands the columns; control-server#212 and #213 write them. Kept apart from StationDepartureWaitStartedAt on
    // purpose: the cargo holding clock and the station departure wait must never share a field.

    /// <summary>When the cargo holding clock started: the first LoadBatch's safety closure. Never reset by a new stop, a disconnect or a restart.</summary>
    public DateTimeOffset? CargoHoldingStartedAt { get; set; }

    /// <summary><c>LOADING</c>, <c>CARGO_HOLDING_WAIT</c>, <c>VEHICLE_FULL</c> or <c>CLOSED</c>; null before the first load.</summary>
    public string? LoadingPhaseState { get; set; }

    /// <summary>Why loading closed; set only while <see cref="LoadingPhaseState"/> is <c>CLOSED</c>.</summary>
    public string? LoadingClosedReason { get; set; }

    /// <summary>Which slot positions (<c>FRONT</c>, <c>REAR</c>) the loading phase judged full, as a JSON array; null until judged.</summary>
    public string? FullSlotPositionsJson { get; set; }

    /// <summary>When another vehicle's acceptance made this one yield its station (control-server#213).</summary>
    public DateTimeOffset? YieldTriggeredAt { get; set; }

    /// <summary>The vehicle whose acceptance triggered the yield, when <see cref="YieldTriggeredAt"/> is set.</summary>
    public string? YieldTriggeredByVehicleKey { get; set; }

    /// <summary>
    /// When this journey's vehicle began waiting for a person, by the clock of the write that made it so
    /// (control-server#273): the waiting journey watch and the dashboard measure the wait from here. Null while the
    /// journey is not waiting (<see cref="JourneyWaitClassification.IsWaiting"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A wait, not a stage.</b> Moving from one stationary stage to another -- a gate that waited two hours for its unload
    /// and is then blocked -- does not start it over: the vehicle has not moved and its battery has not stopped falling. It
    /// ends when the journey stops waiting -- a departure whose order was confirmed, or the end of the journey -- and a new
    /// one begins on arrival, from the arrival, because the vehicle has moved since.
    /// </para>
    /// <para>
    /// <b>A departure whose order was not confirmed keeps waiting</b> (incremental review of #320, L-a). The engine moves the
    /// stage onto the leg in the same save that writes the dispatch outcome as the reason (<c>ResultUnknown</c> and the like);
    /// a travelling stage with a reason is a wait, so the start stays the station's. That is decided, not incidental: the
    /// vehicle has most likely not left.
    /// </para>
    /// <para>
    /// <b>The session gate is not a reason on a leg.</b> <c>ONBOARD_SESSION_NOT_READY</c> is what a real onboard reports on
    /// nearly every leg while it carries this server's own order, so on a travelling stage it is not a wait (see
    /// <see cref="JourneyWaitClassification"/>).
    /// </para>
    /// <para>
    /// <b>A travelling stage's wait begins with its reason, not with the leg.</b> A twelve-minute drive whose session drops
    /// in its last seconds has waited seconds, not twelve minutes, so the start is <see cref="BlockReasonSince"/>. A reason
    /// that changes during the stop (a checkpoint wait escalated past its budget) does not start it over either.
    /// </para>
    /// <para>
    /// Written by <see cref="ControlServerDbContext"/> on every save that adds or changes the row, never by the runtime:
    /// seven places move a stage and more write a reason, and a column each of them had to remember would be wrong the first
    /// time one forgot. A stationary stage's start is the <see cref="UpdatedAt"/> the same write carries; a writer that
    /// forgot that column leaves an earlier time, so the wait reads longer and is reported sooner -- never later.
    /// </para>
    /// </remarks>
    public DateTimeOffset? WaitingSince { get; private set; }

    /// <summary>
    /// The battery the waiting journey watch last read from RIoT for this journey's vehicle, in percent; null when that
    /// read gave no percentage (control-server#273). Meaningful together with <see cref="WaitingBatteryObservedAt"/>:
    /// a null here with a time there is "unknown at that time", not "never read".
    /// </summary>
    public int? WaitingBatteryPercent { get; set; }

    /// <summary>When the watch made the read <see cref="WaitingBatteryPercent"/> holds, by this server's clock; null before the first.</summary>
    public DateTimeOffset? WaitingBatteryObservedAt { get; set; }

    /// <summary>When the watch last logged this journey's wait; kept so a restart neither repeats nor loses the cadence.</summary>
    public DateTimeOffset? WaitingWarnedAt { get; set; }

    /// <summary>
    /// The <c>ChargingPolicyVersion</c> this journey was dispatched under (<c>REQ-0282</c>: work already under way keeps the
    /// policy snapshot it started with; <c>REQ-0281</c>: a threshold crossed on the way is acted on after the journey, read
    /// against this version). Null on every journey today: batch 9 schema ticket control-server#399 only adds the column, and
    /// its writer is the dispatch of batch 9.
    /// </summary>
    public long? ChargingPolicyVersion { get; set; }

    /// <summary>
    /// The <c>batteryState</c> this server last put into this journey's <c>VehicleBusinessStateSnapshot</c>. That snapshot is
    /// re-sent under one deterministic message id per stage, and a re-send whose payload differs is refused as a semantic
    /// conflict (<c>OnboardJourneyPublisher</c>), so the value a stage published must be readable again rather than taken
    /// from the vehicle's live battery. Null on every journey today (control-server#399); its writer is batch 9's
    /// projection of the real battery state.
    /// </summary>
    public string? PublishedBatteryState { get; set; }

    /// <summary>
    /// How many saves have written this row: the concurrency token that stops a writer from saving over a row it read before
    /// someone else committed to it (control-server#357). Raised by <see cref="ControlServerDbContext"/> on every save that
    /// changes the row, never by a writer, so no writer can forget it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why there is one.</b> The inbound handling writes journeys inside the inbox's <c>BEGIN IMMEDIATE</c> transaction, so what
    /// it reads is current. The runtime reads every open journey at the start of a round and saves each one after deciding --
    /// after RIoT calls and after the other vehicles' advances. An operator's cancellation or a result that does not reconcile,
    /// committed in between, was saved over: the reason it closed with went blank (real-rig run 35896134304), and a journey the
    /// inbound had just blocked for manual recovery was carried on to its departure and its gate order.
    /// </para>
    /// <para>
    /// A save whose original value no longer matches fails with a concurrency exception and writes nothing; the runtime yields
    /// that vehicle for the round and reads it afresh in the next (<c>JourneyRuntimeEngine</c>). While a vehicle is advanced
    /// every save the runtime makes also re-checks this row even if it changes nothing on it
    /// (<see cref="ControlServerDbContext.GuardedJourneyId"/>), so a RIoT order is never created on the strength of a read that
    /// has since been overtaken.
    /// </para>
    /// <para>
    /// Not a business fact, so the zero-change pins leave it out: it counts saves, and a path that saves once more is not a
    /// path whose outcome changed.
    /// </para>
    /// </remarks>
    public long Version { get; private set; }

    /// <summary>
    /// Brings <see cref="WaitingSince"/> in line with the row as it is about to be saved: a wait that began starts at
    /// <paramref name="startedAt"/>, a wait that goes on keeps its start, and a journey no longer waiting has none -- nor a
    /// time it was last warned about, so a wait that begins again is logged from its own threshold. Internal, and the
    /// database context is its one caller, so no runtime writer can set the start beside the stage and drift from that.
    /// </summary>
    internal void ReconcileWait(bool waiting, DateTimeOffset startedAt)
    {
        if (!waiting)
        {
            WaitingSince = null;
            WaitingWarnedAt = null;
            return;
        }
        WaitingSince ??= startedAt;
    }

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
    /// <summary>
    /// The one way to start <see cref="AreaEndAdmissionRevokedSince"/>: the first round that holds the stop records when
    /// the wait began, every later round keeps that time, and the start is returned so the caller writes the block from it
    /// (control-server#228). Idempotent on purpose -- the hold is written again on every round it lasts.
    /// </summary>
    public DateTimeOffset HoldForAreaEndAdmission(DateTimeOffset now)
    {
        AreaEndAdmissionRevokedSince ??= now;
        return AreaEndAdmissionRevokedSince.Value;
    }

    /// <summary>
    /// The one way to clear <see cref="AreaEndAdmissionRevokedSince"/>: the station admits the task type again and the
    /// stop leaves for its unload. Nothing else ends the wait -- not a restart, not a reconnect, not another block code.
    /// </summary>
    public void ReleaseAreaEndAdmissionHold() => AreaEndAdmissionRevokedSince = null;

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
