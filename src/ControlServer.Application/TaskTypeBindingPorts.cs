namespace ControlServer.Application;

/// <summary>
/// 六类 MES 运输任务的原始字面值（协议 <c>TransportTaskType</c>），规则表里的 <c>TASK_TYPE</c> 只能取这几个。
/// </summary>
public static class TransportTaskTypes
{
    public const string DieToWireStaging = "DIE_TO_WIRE_STAGING";
    public const string DieToOven = "DIE_TO_OVEN";
    public const string WireToGate = "WIRE_TO_GATE";
    public const string WireToOptical = "WIRE_TO_OPTICAL";
    public const string StagingToWire = "STAGING_TO_WIRE";
    public const string WireToNitrogen = "WIRE_TO_NITROGEN";

    public static IReadOnlyList<string> All { get; } =
        [DieToWireStaging, DieToOven, WireToGate, WireToOptical, StagingToWire, WireToNitrogen];
}

/// <summary>
/// 任务类型规则只记固定端是起点还是终点（规格 5.3：产品不再维护 <c>PublicStationFunction</c>）。
/// </summary>
public static class TaskTypeFixedEnd
{
    /// <summary>固定站是取货端，例如 <c>STAGING_TO_WIRE</c> 的派工待送取货点。</summary>
    public const string Origin = "ORIGIN";

    /// <summary>固定站是卸货端，例如 <c>WIRE_TO_GATE</c> 的关卡。</summary>
    public const string Destination = "DESTINATION";
}

/// <summary>
/// 规则表与绑定集作为受治理对象时的固定名字、审计动作与冻结用的消费者类别。
/// </summary>
/// <remarks>
/// 规则整张表一个版本（REQ-0343），所以只有一个 <see cref="RuleObjectId"/>；绑定集每张 Map 一条版本线（REQ-0337），
/// 对象名按图取 <see cref="BindingSetObjectId"/>，种类沿用已有的 <c>PublicStationBinding</c>。
/// </remarks>
public static class TaskTypeStationGovernance
{
    public const string RuleObjectId = "task-type-station-rules";

    public const string RuleVersionLoadedAction = "TASK_TYPE_STATION_RULE_VERSION_LOADED";

    public const string BindingSetVersionLoadedAction = "TASK_TYPE_STATION_BINDING_SET_VERSION_LOADED";

    /// <summary>需求冻结规则与绑定集版本时的消费者类别，与批次 4 冻结分区归属版本是同一个常量。</summary>
    public const string DemandConsumerKind = DispatchZoneAreaAssignmentGovernance.DemandConsumerKind;

    /// <summary>预置配置文件装载时写在版本行 <c>Source</c> 里的来源前缀。</summary>
    public const string PresetSourcePrefix = "preset:";

    public static string BindingSetObjectId(int mapId) =>
        FormattableString.Invariant($"map-{mapId}");
}

/// <summary>规则表的一行：一个任务类型的固定端（REQ-0343）。</summary>
public sealed record TaskTypeStationRule(string TaskType, string FixedEnd);

/// <summary>
/// 一张图上一个任务类型绑定的固定站（REQ-0334、REQ-0338）。站点按 RIoT id 与名字认，
/// <see cref="SiteVerificationRef"/> 指向现场用途核对记录。
/// </summary>
public sealed record TaskTypeStationBinding(
    string TaskType,
    int StationRiotId,
    string StationName,
    string SiteVerificationRef);

/// <summary>
/// 一张图的需求集与绑定。需求集是本图获准启用、需要固定站的任务类型（<c>MapPublicStationRequirementSet</c>，REQ-0335）。
/// </summary>
public sealed record TaskTypeStationMapConfiguration(
    int MapId,
    IReadOnlyList<string> RequiredTaskTypes,
    IReadOnlyList<TaskTypeStationBinding> Bindings);

/// <summary>受控预置配置的内容：一张规则表，加一张图的需求集与绑定。</summary>
public sealed record TaskTypeStationConfiguration(
    IReadOnlyList<TaskTypeStationRule> Rules,
    TaskTypeStationMapConfiguration Map);

/// <summary>
/// 过渡期的旧配置标量 <c>JourneyRuntime:gateStationId</c>／<c>gateStationRiotId</c>。批次6-04 删掉标量时连同这条校验删除。
/// </summary>
public sealed record TransitionalGateStation(int StationRiotId, string StationName);

/// <summary>一条违规：原因码、涉及的任务类型与站点（能说出时），以及给人看的明细。</summary>
public sealed record TaskTypeStationViolation(
    string ReasonCode,
    string? TaskType,
    int? StationRiotId,
    string Detail);

/// <summary>
/// 校验器的原因码。前十一个在启动期拒绝，最后两个（目录相关）只让该任务类型不投运。
/// </summary>
public static class TaskTypeStationReasonCodes
{
    public const string RuleUnknownTaskType = "TASK_TYPE_RULE_UNKNOWN_TASK_TYPE";
    public const string RuleDuplicate = "TASK_TYPE_RULE_DUPLICATE";
    public const string RuleFixedEndInvalid = "TASK_TYPE_RULE_FIXED_END_INVALID";
    public const string RuleMissing = "TASK_TYPE_RULE_MISSING";
    public const string BindingDuplicate = "TASK_TYPE_BINDING_DUPLICATE";
    public const string StationReused = "TASK_TYPE_STATION_REUSED";
    public const string BindingRequiredMissing = "TASK_TYPE_BINDING_REQUIRED_MISSING";
    public const string BindingIdentityInvalid = "TASK_TYPE_BINDING_IDENTITY_INVALID";
    public const string BindingAreaNamedStation = "TASK_TYPE_BINDING_AREA_NAMED_STATION";
    public const string BindingSiteVerificationMissing = "TASK_TYPE_BINDING_SITE_VERIFICATION_MISSING";
    public const string BindingGateScalarMismatch = "TASK_TYPE_BINDING_GATE_SCALAR_MISMATCH";
    public const string BindingMapMismatch = "TASK_TYPE_BINDING_MAP_MISMATCH";

    public const string BindingStationNotInCatalog = "TASK_TYPE_BINDING_STATION_NOT_IN_CATALOG";
    public const string BindingCatalogNotFresh = "TASK_TYPE_BINDING_CATALOG_NOT_FRESH";
}

/// <summary>预置配置违反启动期校验：服务拒绝启动，<see cref="Violations"/> 是全部违规项，不只是第一条。</summary>
public sealed class TaskTypeStationConfigurationException : InvalidOperationException
{
    public TaskTypeStationConfigurationException(IReadOnlyList<TaskTypeStationViolation> violations)
        : base(Describe(violations))
    {
        Violations = violations;
    }

    public TaskTypeStationConfigurationException()
        : this([])
    {
    }

    public TaskTypeStationConfigurationException(string message)
        : base(message)
    {
        Violations = [];
    }

    public TaskTypeStationConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Violations = [];
    }

    public IReadOnlyList<TaskTypeStationViolation> Violations { get; }

    private static string Describe(IReadOnlyList<TaskTypeStationViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        return FormattableString.Invariant(
                $"The task type station preset configuration has {violations.Count} violation(s); refusing to start: ")
            + string.Join("; ", violations.Select(violation => $"{violation.ReasonCode}: {violation.Detail}"));
    }
}

// ======== Persistence ports (control-server#159). Later batch 6 tickets call or implement these. ========

/// <summary>规则表的一个版本（REQ-0343）：整张表一个版本号，写入即不可改。</summary>
public sealed record TaskTypeStationRuleVersion(
    long Version,
    string ContentSha256,
    string SnapshotId,
    DateTimeOffset LoadedAt,
    string Source,
    IReadOnlyList<TaskTypeStationRule> Rules);

/// <summary>
/// 一张图的绑定集版本（REQ-0337）：绑定、需求集、它依赖的规则版本，以及写入时据以核验的目录修订（没有核验时为空）。
/// </summary>
public sealed record TaskTypeStationBindingSetVersion(
    int MapId,
    long Version,
    long RuleVersion,
    string ContentSha256,
    string SnapshotId,
    long? CatalogRevision,
    DateTimeOffset LoadedAt,
    string Source,
    IReadOnlyList<string> RequiredTaskTypes,
    IReadOnlyList<TaskTypeStationBinding> Bindings);

/// <summary>写一个版本的结果：<see cref="Created"/> 为假表示内容与该线最新一版相同，没有产生新版本。</summary>
public sealed record TaskTypeStationVersionWrite<TVersion>(TVersion Version, bool Created);

/// <summary>生效指针的状态。本票只写 <see cref="Active"/>；<see cref="ActivationUnknown"/> 给批次6-05 的结果未知对账用（REQ-0347）。</summary>
public static class TaskTypeStationActivationState
{
    public const string Active = "ACTIVE";
    public const string ActivationUnknown = "ACTIVATION_UNKNOWN";
}

/// <summary>一张图当前生效的绑定集版本指针。</summary>
public sealed record TaskTypeStationActivePointer(
    int MapId,
    long? ActiveVersion,
    string State,
    long? PendingVersion,
    DateTimeOffset UpdatedAt);

public interface ITaskTypeStationRuleStore
{
    /// <summary>版本号最大的一版；从未写过时为 <c>null</c>。</summary>
    Task<TaskTypeStationRuleVersion?> ReadCurrentAsync(CancellationToken cancellationToken);

    Task<TaskTypeStationRuleVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken);

    /// <summary>
    /// 写入新版本：内容指纹与当前版本相同时返回当前版本、不产生新版本；否则经 <see cref="GovernedConfigurationPublisher"/>
    /// 冻结快照、写业务审计，再落版本行与各条规则。已在事务里时加入调用方的事务。
    /// </summary>
    Task<TaskTypeStationVersionWrite<TaskTypeStationRuleVersion>> WriteVersionAsync(
        IReadOnlyList<TaskTypeStationRule> rules,
        string source,
        DateTimeOffset loadedAt,
        CancellationToken cancellationToken);
}

public interface ITaskTypeStationBindingStore
{
    /// <summary>生效指针指向的版本；该图没有生效版本时为 <c>null</c>。</summary>
    Task<TaskTypeStationBindingSetVersion?> ReadActiveAsync(int mapId, CancellationToken cancellationToken);

    /// <summary>该图版本号最大的一版，不论是否生效；从未写过时为 <c>null</c>。</summary>
    Task<TaskTypeStationBindingSetVersion?> ReadLatestAsync(int mapId, CancellationToken cancellationToken);

    Task<TaskTypeStationBindingSetVersion?> ReadVersionAsync(
        int mapId,
        long version,
        CancellationToken cancellationToken);

    /// <summary>
    /// 写入该图的新版本。内容（需求集、绑定与所依赖的规则版本）与该图最新一版相同时不产生新版本；目录修订、来源与时间
    /// 不算内容。不动生效指针。
    /// </summary>
    Task<TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion>> WriteVersionAsync(
        int mapId,
        long ruleVersion,
        IReadOnlyList<string> requiredTaskTypes,
        IReadOnlyList<TaskTypeStationBinding> bindings,
        long? catalogRevision,
        string source,
        DateTimeOffset loadedAt,
        CancellationToken cancellationToken);

    Task<TaskTypeStationActivePointer?> ReadActivePointerAsync(int mapId, CancellationToken cancellationToken);

    /// <summary>把该图的生效指针指向一个已存在的版本，状态 <c>ACTIVE</c>、清掉待定版本。版本不存在时抛。</summary>
    Task<TaskTypeStationActivePointer> SetActiveAsync(
        int mapId,
        long version,
        DateTimeOffset at,
        CancellationToken cancellationToken);
}

/// <summary>暂停的来源：人工收紧，或目录变化自动收敛（REQ-0340、REQ-0342）。</summary>
public static class TaskTypeStationHoldSource
{
    public const string Manual = "MANUAL";
    public const string CatalogChange = "CATALOG_CHANGE";
}

/// <summary>一条暂停。<see cref="ReleasedAt"/> 为空表示仍成立。</summary>
public sealed record TaskTypeStationHold(
    string HoldId,
    int MapId,
    string TaskType,
    string Source,
    string ReasonCode,
    string DetailJson,
    DateTimeOffset RaisedAt,
    string RaisedBy,
    DateTimeOffset? ReleasedAt,
    string? ReleasedBy);

/// <summary>
/// 按 <c>Map + TASK_TYPE</c> 的暂停（REQ-0340）。同一 <c>(MapId, TaskType)</c> 可同时有多条未解除暂停，不自动到期，解除不删行。
/// </summary>
public interface ITaskTypeStationHoldStore
{
    Task<TaskTypeStationHold> RaiseAsync(
        int mapId,
        string taskType,
        string source,
        string reasonCode,
        string detailJson,
        string raisedBy,
        DateTimeOffset raisedAt,
        CancellationToken cancellationToken);

    /// <summary>解除一条仍成立的暂停；没有这条或已解除时返回 <c>false</c>。</summary>
    Task<bool> ReleaseAsync(
        string holdId,
        string releasedBy,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken);

    /// <summary>该图全部未解除的暂停，按置位时间排序。</summary>
    Task<IReadOnlyList<TaskTypeStationHold>> ListUnreleasedAsync(int mapId, CancellationToken cancellationToken);

    Task<bool> IsHeldAsync(int mapId, string taskType, CancellationToken cancellationToken);
}

/// <summary>一条目录变化记录（REQ-0341、REQ-0342），分类由批次6-06 做，这里只存。</summary>
public sealed record TaskTypeStationCatalogChange(
    string ChangeId,
    int MapId,
    int StationRiotId,
    string PreviousStationName,
    string? CurrentStationName,
    string ChangeKind,
    string RiskClass,
    long CatalogRevision,
    DateTimeOffset ObservedAt,
    IReadOnlyList<string> AffectedTaskTypes,
    string? HoldId);

/// <summary>目录变化记录，按 <c>(MapId, StationRiotId, CatalogRevision)</c> 去重。</summary>
public interface ITaskTypeStationCatalogChangeStore
{
    /// <summary>记一条。同一 <c>(MapId, StationRiotId, CatalogRevision)</c> 已记过时不再写，返回已有的那条。</summary>
    Task<TaskTypeStationCatalogChange> RecordAsync(
        TaskTypeStationCatalogChange change,
        CancellationToken cancellationToken);

    /// <summary>该图全部记录，按观察时间排序。</summary>
    Task<IReadOnlyList<TaskTypeStationCatalogChange>> ListAsync(int mapId, CancellationToken cancellationToken);
}

/// <summary>一条需求冻结的规则版本与绑定集版本（REQ-0344）。站点本身由 <c>FrozenDemandStations</c> 冻结，不重复存。</summary>
public sealed record DemandTaskTypeStationFreeze(
    string DemandId,
    long RuleVersion,
    int MapId,
    long BindingSetVersion,
    DateTimeOffset FrozenAt);

/// <summary>对已冻结的需求再冻结另一对版本时抛出：冻结之后的版本变化不改变这条需求（REQ-0344）。</summary>
public sealed class DemandTaskTypeStationFreezeConflictException : InvalidOperationException
{
    public DemandTaskTypeStationFreezeConflictException(string message)
        : base(message)
    {
    }

    public DemandTaskTypeStationFreezeConflictException()
        : base("The demand has already frozen a different task type station rule or binding set version.")
    {
    }

    public DemandTaskTypeStationFreezeConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 需求冻结规则与绑定集版本，写在批次 3 的通用冻结表 <c>ConfigurationConsumerBindings</c> 里，一需求两行，同一个事务。
/// 写入调用在批次6-04。
/// </summary>
public interface IDemandTaskTypeStationFreeze
{
    /// <summary>
    /// 冻结。同一需求再冻结同一对版本是幂等的，返回第一次冻结的记录；任一版本不同抛
    /// <see cref="DemandTaskTypeStationFreezeConflictException"/>。版本不存在时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    Task<DemandTaskTypeStationFreeze> FreezeAsync(
        string demandId,
        long ruleVersion,
        int mapId,
        long bindingSetVersion,
        DateTimeOffset frozenAt,
        CancellationToken cancellationToken);

    /// <summary>该需求冻结的版本；尚未冻结时为 <c>null</c>。</summary>
    Task<DemandTaskTypeStationFreeze?> ReadAsync(string demandId, CancellationToken cancellationToken);
}
