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
    public DbSet<HardwareRecoveryRecordRow> HardwareRecoveryRecords => Set<HardwareRecoveryRecordRow>();
    public DbSet<RecoveryResultEvidenceRow> RecoveryResultEvidence => Set<RecoveryResultEvidenceRow>();
    public DbSet<JourneyBacklogRow> JourneyBacklog => Set<JourneyBacklogRow>();
    public DbSet<JourneyRuntimeRow> JourneyRuntimes => Set<JourneyRuntimeRow>();
    public DbSet<JourneyStopRow> JourneyStops => Set<JourneyStopRow>();
    public DbSet<JourneyDemandRow> JourneyDemands => Set<JourneyDemandRow>();
    public DbSet<AutoChargingRunRow> AutoChargingRuns => Set<AutoChargingRunRow>();
    public DbSet<TransportDemandSuppressionRow> TransportDemandSuppressions =>
        Set<TransportDemandSuppressionRow>();
    public DbSet<AdmissionPolicyStateRow> AdmissionPolicyState => Set<AdmissionPolicyStateRow>();
    public DbSet<StationTaskTypeAdmissionRow> StationTaskTypeAdmissions => Set<StationTaskTypeAdmissionRow>();
    public DbSet<AdmissionPolicyAuditRow> AdmissionPolicyAudit => Set<AdmissionPolicyAuditRow>();
    public DbSet<AdmissionDecisionSnapshotRow> AdmissionDecisionSnapshots => Set<AdmissionDecisionSnapshotRow>();
    public DbSet<PackageCapacityRuleRow> PackageCapacityRules => Set<PackageCapacityRuleRow>();
    public DbSet<MissingPackageRow> MissingPackages => Set<MissingPackageRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AcceptedDemandRow>().HasKey(row => row.DemandId);
        modelBuilder.Entity<AcceptedDemandRow>().HasIndex(row => row.TransportDemandKey).IsUnique();
        modelBuilder.Entity<AcceptedDemandRow>().Property(row => row.Status).HasConversion<string>();
        modelBuilder.Entity<VehicleDispatchLeaseRow>().HasKey(row => row.JourneyId);
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
        modelBuilder.Entity<HardwareRecoveryRecordRow>().HasKey(row => row.RecordId);
        modelBuilder.Entity<RecoveryResultEvidenceRow>().HasKey(row => row.MessageId);
        modelBuilder.Entity<JourneyBacklogRow>().HasKey(row => row.DemandId);
        modelBuilder.Entity<JourneyBacklogRow>().HasIndex(row => row.TransportDemandKey);
        modelBuilder.Entity<JourneyRuntimeRow>().HasKey(row => row.JourneyId);
        modelBuilder.Entity<JourneyRuntimeRow>().Property(row => row.Stage).HasConversion<string>();
        modelBuilder.Entity<JourneyStopRow>().HasKey(row => new { row.JourneyId, row.Sequence });
        modelBuilder.Entity<JourneyStopRow>().HasIndex(row => row.UpperId).IsUnique();
        modelBuilder.Entity<JourneyStopRow>().HasIndex(row => row.MovementLegId).IsUnique();
        // A demand belongs to at most one journey, ever. The unique index says so rather than
        // leaving it to the callers that look a demand's journey up.
        modelBuilder.Entity<JourneyDemandRow>().HasKey(row => new { row.JourneyId, row.DemandId });
        modelBuilder.Entity<JourneyDemandRow>().HasIndex(row => row.DemandId).IsUnique();
        modelBuilder.Entity<JourneyDemandRow>().HasIndex(row => row.LoadSlotOperationAttemptId).IsUnique();
        modelBuilder.Entity<JourneyDemandRow>().HasIndex(row => row.UnloadSlotOperationAttemptId).IsUnique();
        modelBuilder.Entity<JourneyDemandRow>().Property(row => row.State).HasConversion<string>();
        modelBuilder.Entity<TransportDemandSuppressionRow>().HasKey(row => row.TransportDemandKey);
        modelBuilder.Entity<AutoChargingRunRow>().HasKey(row => row.ChargingRunId);
        modelBuilder.Entity<AutoChargingRunRow>().HasIndex(row => row.UpperId).IsUnique();
        modelBuilder.Entity<AutoChargingRunRow>().Property(row => row.Stage).HasConversion<string>();
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

/// <summary>
/// The exclusive hold one journey has on one vehicle. It is keyed on the journey, not on a demand:
/// a journey carrying several demands holds the vehicle once, and finishing one of those demands
/// does not release it -- the vehicle is still carrying the others.
/// </summary>
/// <remarks>
/// The path that accepts a demand without planning a journey has no journey identity to use, so it
/// degenerates to the demand's own id. That is the same degenerate form the migration gives the
/// single-demand journeys that predate ADR-cross-0057.
/// </remarks>
public sealed class VehicleDispatchLeaseRow
{
    public required string JourneyId { get; set; }
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

/// <summary>
/// One journey, keyed on its own identity rather than on a demand's. A journey carries a sequence
/// of <see cref="JourneyStopRow"/> and, at those stops, several <see cref="JourneyDemandRow"/> --
/// which is what ADR-cross-0057 restores after the MVP froze "one demand, two legs" into the
/// schema. Everything that belongs to one stop or one demand now lives on those rows; what stays
/// here is what the whole journey shares.
/// </summary>
public sealed class JourneyRuntimeRow
{
    public required string JourneyId { get; set; }
    public JourneyRuntimeStage Stage { get; set; }
    public required string AgvId { get; set; }
    public required string VehicleKey { get; set; }
    public long AgvLifecycleGeneration { get; set; }
    public int MapId { get; set; }
    public required string MapIdentity { get; set; }
    public required string DispatchZone { get; set; }
    public required string GateStationId { get; set; }
    public int GateStationRiotId { get; set; }
    public required string OperationSessionId { get; set; }
    public long DispatchGeneration { get; set; }
    /// <summary>Which stop in the sequence the journey is at, or driving to.</summary>
    public int CurrentStopSequence { get; set; }
    /// <summary>
    /// The stop the journey is leaving for, set while it waits for the pre-departure safety check
    /// that authorizes the move. It stays separate from <see cref="CurrentStopSequence"/> because
    /// the check belongs to the stop being left -- its check id is that stop's -- and moving the
    /// cursor first would look the wrong id up.
    /// </summary>
    public int? NextStopSequence { get; set; }
    /// <summary>
    /// The highest snapshot revision this journey has published, per snapshot type. Onboard journals
    /// the adopted revision keyed on the message type alone and refuses one that does not advance,
    /// so these are cursors carried on from the vehicle's previous journey rather than counters that
    /// restart. Every stop takes its own revision from here and advances it.
    /// </summary>
    public long VehicleBusinessRevision { get; set; }
    public long WorklistRevision { get; set; }
    public long PlanRevision { get; set; }
    /// <summary>
    /// When the first LoadBatch on this journey closed safely, which is when the holding clock
    /// starts (ADR-cross-0057). Not acceptance and not first arrival: before cargo is physically on
    /// the vehicle there is no holding risk, and a stop with nothing to load is
    /// ADR-cross-0055's business, not this one's. Null while the vehicle is empty.
    /// </summary>
    public DateTimeOffset? HoldingStartedAt { get; set; }
    /// <summary>
    /// Set once the loading phase has ended -- full, or held too long -- so that no further demand
    /// joins the journey even if the vehicle passes another eligible stop on the way to the gate.
    /// </summary>
    public string? LoadingClosedReason { get; set; }
    public string? BlockReasonCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One stop in a journey's sequence: where the vehicle goes, the movement leg that takes it there,
/// and the snapshots published on arrival. Each stop publishes its own worklist, plan and vehicle
/// state at its own revision -- the peer keys a snapshot's identity on type and revision, so two
/// stops sharing one revision would be refused as a revision whose content changed.
/// </summary>
public sealed class JourneyStopRow
{
    public required string JourneyId { get; set; }
    public int Sequence { get; set; }
    public required string Role { get; set; }
    public required string StationId { get; set; }
    public int StationRiotId { get; set; }
    public required string RouteEvidenceId { get; set; }
    public required string MovementLegId { get; set; }
    public required string UpperId { get; set; }
    public required string LegType { get; set; }
    public required string State { get; set; }
    public long VehicleBusinessRevision { get; set; }
    public long WorklistRevision { get; set; }
    public long PlanRevision { get; set; }
    public required string VehicleBusinessMessageId { get; set; }
    public required string PlanMessageId { get; set; }
    public required string PreDepartureSafetyCheckMessageId { get; set; }
    public required string PreDepartureSafetyCheckId { get; set; }
    /// <summary>
    /// How many times this stop has published a worklist and asked the operator to enter a sublot.
    /// One stop can serve several demands, and each load ends the request that produced it -- the
    /// peer expires an entry request when the worklist revision changes -- so the next demand needs
    /// a fresh worklist and a fresh request, each with its own messageId. The round is what makes
    /// those ids deterministic, which is what lets an unacknowledged one be replayed.
    /// </summary>
    public int LoadRound { get; set; }
    public string? ConsumedSafetyResultMessageId { get; set; }
    /// <summary>
    /// When this stop began waiting for an operator to enter a sublot. <see cref="UpdatedAt"/>
    /// cannot serve: a later poll rewriting the same block reason moves it, which would restart the
    /// wait clock every iteration and make the timeout unreachable. It is per stop because
    /// ADR-cross-0055 recomputes the station wait after every LoadBatch closes, unlike the journey's
    /// holding clock, which runs down once.
    /// </summary>
    public DateTimeOffset? SublotWaitStartedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One demand carried by a journey, bound to the stop it loads at. The slot reservation, the load
/// and unload commands and the operator's entry all belong here rather than to the journey, which
/// is the whole point of ADR-cross-0057: a journey carries several of these at once.
/// </summary>
public sealed class JourneyDemandRow
{
    public required string JourneyId { get; set; }
    public required string DemandId { get; set; }
    public int StopSequence { get; set; }
    public int ExpectedBasketCount { get; set; }
    public required string TargetSlotsJson { get; set; }
    public required string LoadCommandMessageId { get; set; }
    public required string LoadSlotOperationAttemptId { get; set; }
    public required string UnloadCommandMessageId { get; set; }
    public required string UnloadSlotOperationAttemptId { get; set; }
    /// <summary>
    /// The submission this demand has already judged. A refused entry stays in the inbox, so without
    /// this every poll would re-run the remote recount and re-send the same refusal.
    /// </summary>
    public string? ConsumedSublotMessageId { get; set; }
    /// <summary>
    /// When each slot operation was commanded for this demand. They say which of a stop's demands
    /// the runtime is currently waiting on, which <see cref="State"/> cannot: the operator decides
    /// which pending demand loads next by what they scan, so it is not the first one; and the unload
    /// result moves the state to <see cref="JourneyDemandState.Unloaded"/> from the message handler,
    /// so by the time the runtime looks, the demand it was waiting on no longer stands out.
    /// </summary>
    public DateTimeOffset? LoadCommandedAt { get; set; }
    public DateTimeOffset? UnloadCommandedAt { get; set; }
    public JourneyDemandState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LoadedAt { get; set; }
}

/// <summary>
/// A permanent ban on executing a transport demand, keyed on the business identity rather than on
/// the instance: MesIngest allocates a fresh DemandId when a demand disappears from its catalog and
/// comes back, so cancelling one instance stops that instance and nothing else. Nobody writes back
/// to MES, so the same SUBLOT keeps reappearing as a new candidate until somebody physically moves
/// the product and scans it through -- and the vehicle would keep being dispatched to a stop that
/// an operator already refused. ADR-cross-0047 and FR-004.
/// </summary>
/// <remarks>
/// It never expires: not on time, polling, GONE, restart, or a new DemandId. There is deliberately
/// no lifting entry point in this version. The cancelled instance keeps its own terminal state
/// under its DemandId; this row is a separate, coarser fact.
/// </remarks>
public sealed class TransportDemandSuppressionRow
{
    public required string TransportDemandKey { get; set; }
    /// <summary>The instance whose cancellation raised the ban, kept for audit.</summary>
    public required string DemandId { get; set; }
    public required string ReasonCode { get; set; }
    public DateTimeOffset SuppressedAt { get; set; }
}

/// <summary>
/// One trip to the charger. The vehicle is left standing on the pad when the run completes rather
/// than being driven off to an idle spot: the charger doubles as the parking place, and the next
/// demand moves it. That is why <see cref="ReleasedAtBatteryPercent"/> records the level the run
/// stopped holding the vehicle at, which is not the level it eventually reaches.
/// </summary>
public sealed class AutoChargingRunRow
{
    public required string ChargingRunId { get; set; }
    public required string VehicleKey { get; set; }
    public required string AgvId { get; set; }
    public AutoChargingStage Stage { get; set; }
    public required string ChargerStationId { get; set; }
    public int ChargerStationRiotId { get; set; }
    public required string MovementLegId { get; set; }
    public required string UpperId { get; set; }
    public int TriggeredAtBatteryPercent { get; set; }
    public int? ReleasedAtBatteryPercent { get; set; }
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
