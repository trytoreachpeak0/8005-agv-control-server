namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// Dispatch reason codes that more than one batch 4 ticket names, registered in one place.
/// </summary>
/// <remarks>
/// <para>
/// The codes that already exist stay on the criterion that returns them. These are here because the
/// ticket that returns one is not the only one that reads it: the slot group criterion (#73) returns
/// them, the structural classification (#74) sorts them, and the dashboard (#70) explains them to an
/// operator. Three spellings of one reason would make each of those silently miss the others.
/// </para>
/// <para>
/// Registered by control-server#69 before anything returns them. Nothing in the chain produces one of
/// these yet; a code that appears here with no caller is expected until #73 merges.
/// </para>
/// </remarks>
public static class DispatchReasonCodes
{
    /// <summary>
    /// The demand's slot group has fewer usable empty slots than it needs right now. Ordinary backlog: a
    /// slot freeing up or being re-enabled clears it.
    /// </summary>
    public const string SlotGroupCapacityTemporarilyUnavailable = "SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE";

    /// <summary>
    /// The demand needs more baskets than this vehicle's slot group has physical slots, whatever their
    /// state. One demand never spans groups (REQ-0352), so this vehicle can never carry it.
    /// </summary>
    public const string ExpectedBasketCountExceedsSlotGroup = "EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP";

    /// <summary>
    /// Neither the vehicle's active slot configuration nor its latest published IO binding names a slot
    /// model, so which slot is in which group is unknown and the vehicle is not guessed at.
    /// </summary>
    public const string VehicleSlotModelUnresolved = "VEHICLE_SLOT_MODEL_UNRESOLVED";

    /// <summary>
    /// The area assignment table the round read gives this demand's AREA no slot group.
    /// </summary>
    public const string AreaSlotGroupNotAssigned = "AREA_SLOT_GROUP_NOT_ASSIGNED";

    /// <summary>
    /// <b>Silent.</b> The area assignment table the round read does not name this demand's AREA, so this
    /// server does not execute it (REQ-0191). The table is the only execution whitelist; there is no prefix
    /// rule behind it.
    /// </summary>
    /// <remarks>
    /// Not a fault and not an alarm: eutectic and low-temperature eutectic AREAs, among others, are kept out of
    /// execution precisely by leaving them unmapped, and they stay in the plant-wide projection. So the demand
    /// is only written to <c>JourneyBacklog</c>, never raised as a structural dispatch block, and never logged
    /// at Warning or above. Listed in <see cref="Silent"/>, which is what the structural classification
    /// (control-server#74) excludes.
    /// </remarks>
    public const string OutOfScopeArea = "OUT_OF_SCOPE_AREA";

    /// <summary>
    /// <b>Not waiting.</b> The demand was in the backlog unaccepted and the latest catalog the round read no
    /// longer lists it: it is no longer in the MesIngest catalog, and this server never took it. Nothing is
    /// waiting for a vehicle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written by the dispatch round, not by an admission criterion, and only from a catalog that was read
    /// successfully -- the MesIngest catalog is the complete list of open demands, so absence from it is a
    /// fact, whereas a failed read says nothing. The row is kept rather than deleted, and its
    /// <c>LastSeenAt</c> keeps the last time the demand was actually in the catalog. A demand that comes back
    /// is judged again like any other and keeps its <c>FirstSeenAt</c>.
    /// </para>
    /// <para>
    /// Anything that lists waiting backlog (the dashboard, control-server#70) leaves these rows out; REQ-0210's
    /// "展示等待状态" is about demands still waiting. A structural dispatch block on such a demand is cleared by
    /// its own rule (control-server#74), not by this code.
    /// </para>
    /// </remarks>
    public const string DemandLeftCatalog = "DEMAND_LEFT_CATALOG";

    /// <summary>
    /// The rule table does not know the demand's task type, or the deployment's <c>AllowedWorkTypes</c> leaves it
    /// out. Configuration, not a fault.
    /// </summary>
    public const string OutOfScopeWorkType = "OUT_OF_SCOPE_WORK_TYPE";

    /// <summary>
    /// This Map has no effective binding for the demand's task type: it is not in the Map's requirement set, or it
    /// is and has no binding (REQ-0335). No default station is ever put in its place. Only this task type waits;
    /// every other task type of the round is judged as usual.
    /// </summary>
    public const string TaskTypeBindingMissing = "TASK_TYPE_BINDING_MISSING";

    /// <summary>
    /// The task type is held on this Map (REQ-0340, REQ-0342, REQ-0347): by an operator, by a catalog change, or
    /// because an activation's outcome is unknown. Only this task type waits.
    /// </summary>
    public const string TaskTypeHeld = "TASK_TYPE_HELD";

    /// <summary>
    /// The task type cleared its binding, but this build cannot execute it yet (scope specification 8.3: the four
    /// same-direction task types wait for batch 10).
    /// </summary>
    public const string TaskTypeNotYetExecutable = "TASK_TYPE_NOT_YET_EXECUTABLE";

    /// <summary>
    /// 这一侧的空仓位被<b>本车自己已预留或已装的货</b>占着，装不下（票面第 4 条，批次7-06，control-server#211）。
    /// 与 <see cref="SlotGroupCapacityTemporarilyUnavailable"/> 分开登记：那一个是仓位被禁用，这一个是车自己满了。
    /// </summary>
    /// <remarks>
    /// 批次7-07（control-server#212）判「这一侧装满」时唯一的依据就是它，所以它必须只在「其余准入全过、只卡在这一条」
    /// 时出现——一个把两种情形合在一起的原因码，会让那一票分不出「车满了」和「仓位坏了」。
    /// </remarks>
    public const string SlotGroupOccupiedByOwnCargo = "SLOT_GROUP_OCCUPIED_BY_OWN_CARGO";

    /// <summary>
    /// 这条需求所在分区没有配置途中追加的上限，或配成了 0：本区禁止途中追加（REQ-0198）。
    /// </summary>
    /// <remarks>
    /// 参数默认未配置，所以本票合入本身不会让线上产生多需求旅程——v2 的行为与今天完全相同，直到有人批准参数。
    /// 它也是既有需求那一侧的答案：一条既有需求所在分区没有上限时，它不接受任何延迟。
    /// </remarks>
    public const string EnRouteAppendNotConfigured = "EN_ROUTE_APPEND_NOT_CONFIGURED";

    /// <summary>追加会让某条需求到终点的计划路径代价增量超过它所在分区的上限（REQ-0198）。</summary>
    public const string EnRouteAppendDelayGateExceeded = "EN_ROUTE_APPEND_DELAY_GATE_EXCEEDED";

    /// <summary>
    /// 任一增量算不出（路网给不出某一段的代价）。<b>算不出即拒</b>，不按零处理——一个算不出的增量与一个为零的增量
    /// 是两回事，而把前者当后者用，正是「门禁形同虚设」的样子。
    /// </summary>
    public const string EnRouteAppendDelayUncomputable = "EN_ROUTE_APPEND_DELAY_UNCOMPUTABLE";

    /// <summary>没有一个插入位能让各分区的需求保持连续区段（REQ-0195）：允许 A→A→B→B，禁止 A→B→A。</summary>
    public const string EnRouteAppendBreaksZoneContiguity = "EN_ROUTE_APPEND_BREAKS_ZONE_CONTIGUITY";

    /// <summary>追加会让计划超过 9 条腿，或让某一站的清单超过 8 项（protocol 2.0.0）。</summary>
    public const string EnRouteAppendPlanLimitReached = "EN_ROUTE_APPEND_PLAN_LIMIT_REACHED";

    /// <summary>
    /// 当前下一站之后没有可用的插入位（REQ-0196）：车正驶向的那一站不能被插到前面去，而它之后已经没有位置了。
    /// </summary>
    public const string EnRouteAppendNoInsertionPoint = "EN_ROUTE_APPEND_NO_INSERTION_POINT";

    /// <summary>
    /// 这条需求曾经挂在这趟旅程上、被释放出去了（批次7-10，control-server#215，审查 M5）：不再追加回这一趟。
    /// 归属表的主键是 (JourneyId, DemandId)，被释放的那一行只标移除、不删，追加回去会撞主键，每一轮都撞。
    /// 它可以被别的车、别的旅程接走。
    /// </summary>
    public const string EnRouteAppendDemandLeftThisJourney = "EN_ROUTE_APPEND_DEMAND_LEFT_THIS_JOURNEY";

    /// <summary>
    /// 这辆在途车的装货阶段已经结束（批次7-07，control-server#212）：持货超时了，或者装满之后已经离开最后一个装货停靠，
    /// 或者本来就不适用持货、当前计划已经装完。REQ-0354 末句「持货超时或让站之后不再接受新的待装 Demand」。
    /// </summary>
    public const string LoadingPhaseClosed = "LOADING_PHASE_CLOSED";

    /// <summary>
    /// 同一份完整 MES 快照里，这个 Sublot 命中了多于一种任务类型（<c>REQ-0189</c>）。该 Sublot 的<b>全部</b>候选都挡，
    /// 别的 Sublot 不受影响。
    /// </summary>
    /// <remarks>
    /// 归普通积压而不是结构性告警：这是 MES 那一侧的数据自相矛盾，下一份快照就能改掉，而结构性告警说的是
    /// 「整个车队都接不了」——换一辆车、等一等都没用。两者的处置也不同：这一条要人去看 MES，不是去看车队。
    /// </remarks>
    public const string SublotTaskTypeConflict = "SUBLOT_TASK_TYPE_CONFLICT";

    /// <summary>
    /// 这个业务键（<c>sublot|workType</c>）已被本地取消永久抑制（<c>REQ-0155</c>、<c>REQ-0156</c>、<c>REQ-0211</c>；
    /// 批次7-05，control-server#210）：MES 换了新 <c>DemandId</c> 也不再执行。
    /// </summary>
    /// <remarks>
    /// 归普通积压、不报结构性告警：这是有意不执行，不是故障，也没有人需要去处理它。抑制没有「解除」操作。
    /// 只在服务端与看板，不经 <c>blockingFacts</c> 下发。
    /// </remarks>
    public const string TransportDemandKeySuppressed = "TRANSPORT_DEMAND_KEY_SUPPRESSED";

    /// <summary>
    /// 这个业务键已有别的 <c>DemandId</c> 被本服务端受理过（进行中、成功或取消；批次7-05，control-server#210）。
    /// </summary>
    /// <remarks>
    /// 归普通积压，理由同上：同一件活已经办过或正在办，不是故障。挡在判据链里，受理存储层的业务键唯一索引就不会被撞到。
    /// </remarks>
    public const string TransportDemandKeyAlreadyAccepted = "TRANSPORT_DEMAND_KEY_ALREADY_ACCEPTED";

    /// <summary>
    /// The reasons that are a configured outcome rather than a problem: they reach the backlog and nothing
    /// else — no structural dispatch block, no alarm, no log at Warning or above.
    /// </summary>
    public static IReadOnlySet<string> Silent { get; } =
        new HashSet<string>(StringComparer.Ordinal) { OutOfScopeArea };

    /// <summary>Whether <paramref name="reasonCode"/> is one of <see cref="Silent"/>.</summary>
    public static bool IsSilent(string reasonCode) => Silent.Contains(reasonCode);
}
