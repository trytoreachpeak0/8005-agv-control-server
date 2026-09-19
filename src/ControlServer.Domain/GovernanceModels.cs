namespace ControlServer.Domain;

/// <summary>
/// The kinds of object that are governed by versioned snapshots and immutable audit. One mechanism
/// serves FP-C7 slot configuration, FP-C5 archive/restore and (from batch 4) FP-C9b public station
/// binding, so the kind is data rather than a separate table per cluster.
/// </summary>
public enum GovernedObjectKind
{
    SlotTemplate,
    SlotModelVersion,
    ActiveSlotConfiguration,
    AgvLifecycle,
    PublicStationBinding,

    /// <summary>审计保留期本身（REQ-0271 后半句：它的变更是一次管理员操作）。</summary>
    AuditRetention,

    /// <summary>分区归属表（含开门侧列），整张表一个版本（REQ-0350，program#68 决议 2）。</summary>
    DispatchZoneAreaAssignment,

    /// <summary>任务类型规则表，整张表一个版本（REQ-0343）。按图的绑定集沿用 <see cref="PublicStationBinding"/>。</summary>
    TaskTypeStationRule,

    /// <summary>每区派车参数表，整张表一个版本（REQ-0198、REQ-0203；批次 7 建表票 control-server#206）。</summary>
    DispatchZoneParameters
}

/// <summary>
/// What a governance action did. The four outcomes are the ones REQ-0320 requires an archive
/// restoration attempt to distinguish, and they are the same four everywhere else -- a timeout and
/// an unknown result are not failures, and collapsing them into one loses the distinction the
/// reconciliation path needs.
/// </summary>
public enum GovernanceActionOutcome
{
    Succeeded,
    Failed,
    TimedOut,
    ResultUnknown
}

/// <summary>
/// A frozen, version-level, complete snapshot of one governed object. Field-level differences are
/// computed from two of these on demand (see <see cref="ConfigurationFieldDifference"/>); storing a
/// difference as well would be a second truth about the same change (REQ-0346).
/// </summary>
public sealed record GovernedConfigurationSnapshot(
    string SnapshotId,
    GovernedObjectKind ObjectKind,
    string ObjectId,
    long Version,
    string ContentJson,
    string ContentSha256,
    DateTimeOffset FrozenAt);

/// <summary>
/// One field that differs between two frozen snapshots. Computed, never stored.
/// </summary>
public sealed record ConfigurationFieldDifference(
    string FieldPath,
    string? LeftValue,
    string? RightValue);

/// <summary>
/// The person field every audit record carries, and what fills it in this release.
/// </summary>
/// <remarks>
/// There is no personnel authentication in the full product: FP-C10 and FP-C6 are deferred in
/// full, the only administrator identity on the wire is an <c>administratorRole</c> string, and the
/// only authentication is a constant-time comparison against one site-wide environment variable --
/// whoever holds that key can call themselves any role. So the field is filled with the deployment
/// that acted, and carries an explicit marker saying it is not attributable to a natural person. An
/// audit field that looks like a person's name while being nobody is more dangerous than a field
/// that says so out loud.
/// </remarks>
public static class AuditActorAttribution
{
    /// <summary>The literal stored in every audit record's attribution column.</summary>
    public const string NotAttributableToNaturalPerson = "NOT_ATTRIBUTABLE_TO_NATURAL_PERSON";

    /// <summary>The prefix every deployment identity carries, so a person's name cannot pass for one.</summary>
    public const string DeploymentIdentityPrefix = "deployment:";
}
