using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.IdleReturn;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 空闲返回口径的原因码中文说明（批次8-21，control-server#392）：评估器的结论码，与空闲返回旅程上可能出现的每一个码。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么共用码要另写一份</b>（调度 09-30，cs#390 增量审查 L-d）：空闲返回旅程复用搬运的几个码（例如 <c>ORDER_HANG</c>，经
/// <c>NameStalledOrderAsync</c> 写入）。<see cref="BlockedJourneysQueryEndpoint.Descriptions"/> 里这些码的说明写的是「为同一辆车、同一条需求
/// 重建运单」「需求不改派」，对空闲返回不成立：空闲返回没有需求，单被取消后不重建，走的是冷却与停止两道护栏。所以这里按空闲返回的
/// 实际行为写；空闲返回自己的码（<see cref="IdleReturnExecutionReasons"/>）在那份说明里本来就是按空闲返回写的，这里直接用那一份，不抄第二遍。
/// </para>
/// <para>
/// 哪些共用码会出现在空闲返回旅程上，按 <c>JourneyRuntimeEngine.IdleReturn.cs</c> 的 <c>AdvanceIdleReturnAsync</c> 实读列在
/// <see cref="SharedCodesOnIdleReturnJourneys"/>；<c>IdleReturnDashboardTests</c> 核每一个都有空闲返回口径的说明。
/// </para>
/// </remarks>
internal static class IdleReturnCodeDescriptions
{
    /// <summary>
    /// 空闲返回旅程上可能出现的共用码（不属于 <see cref="IdleReturnExecutionReasons"/> 的那些），各自的来源：
    /// <list type="bullet">
    /// <item><c>NameStalledOrderAsync</c>：<c>ORDER_HANG</c>、<c>ORDER_STATE_UNRECOGNIZED</c>；它先交给故障模型（<c>VEHICLE_ORDER_FAILED</c>）与
    /// 行驶中门锁监看（<c>VEHICLE_DOOR_NOT_PROVEN_LOCKED</c>、<c>HELD_ORDER_RESUMED_WITHOUT_CONTINUE</c>）。<c>ORDER_ENDED_WITHOUT_ARRIVAL</c>
    /// 到不了：取消与删除在空闲返回分支里先被接走（写 <see cref="IdleReturnExecutionReasons.OrderEndedStopNotProven"/>）。</item>
    /// <item><c>NameCheckpointWaitAsync</c>：<c>VEHICLE_WAITING_AT_CHECKPOINT</c>、<c>VEHICLE_CHECKPOINT_WAIT_EXCEEDED</c>。</item>
    /// <item>推进循环（<c>NameFailedAdvanceAsync</c>）：<c>JOURNEY_ADVANCE_FAILED</c>。</item>
    /// </list>
    /// </summary>
    internal static IReadOnlyDictionary<string, string> SharedCodesOnIdleReturnJourneys { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JourneyRuntimeEngine.OrderHangReason] =
                "开往等待点的空闲返回单在 RIoT 上挂起（HANG）：服务端不暂停、不急停、不另选等待点，用途与等待点预占都保持。"
                + "请到现场确认原因，在 RIoT 里继续（continue）；继续后自动往下走。若在 RIoT 里取消它，服务端不重建："
                + "车停稳后这趟空闲返回结束，30 秒内这辆车不再自动空闲返回，10 分钟内再被取消一次就停止自动空闲返回",
            [JourneyRuntimeEngine.OrderStateUnrecognizedReason] =
                "空闲返回单在 RIoT 上处于未识别的状态（SUSPENDED 8）：服务端按仍在执行处理，用途与等待点预占保持，"
                + "不做任何自动动作，请人工到 RIoT 核实",
            [VehicleFaultEvidence.OrderFailed] =
                "空闲返回单在 RIoT 上失败（FAILED），服务端已把车判为疑似故障：不派新单，用途与等待点预占保持，不另选等待点。"
                + "请到现场排除原因；若急停已锁住，先按急停人工解除；然后由现场人员经故障清除入口确认（见现场说明）。"
                + "故障清除后这趟空闲返回按已确认失败结束、不重建，下一次空闲返回不再选这个等待点；"
                + "30 秒内这辆车不再自动空闲返回，10 分钟内再失败或被取消一次就停止自动空闲返回",
            [VehicleFaultEvidence.DoorNotProvenLocked] =
                "车在开往等待点的途中，车载端报门锁未锁闭或仓位状态读不到：服务端已按住这张单，证不出停稳即急停，"
                + "并把车判为疑似故障：不派新单，用途与等待点预占保持。请到现场确认车已停、门已锁好；"
                + "门锁锁好后急停会自动解除，但单仍停着，要由现场人员经故障处置入口「继续原单」车才会再走",
            [JourneyRuntimeEngine.HeldOrderResumedWithoutContinueReason] =
                "开往等待点的车因门锁被按住、急停，门锁锁好后急停已自动解除；这张单本应停着等人继续，RIoT 却报它在执行，"
                + "而没有人按过继续。车若在动，服务端会再次急停。请到现场确认车的状态，并在 RIoT 上核实是谁让这张单继续的",
            [JourneyRuntimeEngine.CheckpointWaitReason] =
                "开往等待点的车停在 RIoT 的检查点前等放行：这是 RIoT 的交通管制，服务端不干预，放行后自动继续",
            [JourneyRuntimeEngine.CheckpointWaitExceededReason] =
                "开往等待点的车在 RIoT 检查点前等放行，已超过服务端的等待时限：服务端只告警，不急停、不另选等待点。"
                + "请到 RIoT 查看交通管制为什么一直不放行",
            [JourneyRuntimeEngine.AdvanceFailedReason] =
                "服务端推进这趟空闲返回时出错，它停在原处、一步没动（不发命令，用途与等待点预占保持）。每一轮都会重试，"
                + "走通后这个码自动消失；一直不消失请找值班工程师看服务端日志里的事件 2002，那里有具体的异常",
        };

    /// <summary>
    /// 评估器的结论码（<see cref="IdleReturnReasons"/> 的每个常量）。新加一个码而忘了说明，<c>IdleReturnDashboardTests</c> 就红。
    /// </summary>
    internal static IReadOnlyDictionary<string, string> VerdictCodes { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [IdleReturnReasons.Committed] = "已承诺：这辆车的空闲返回用途与一个等待点的预占已在同一次保存里取得",
            [IdleReturnReasons.Disabled] = "空闲返回开关关着（出厂默认）：不形成新的空闲返回，已有的照常走完",
            [IdleReturnReasons.NotLeftOverByTransportThisRound] =
                "这一轮派车选中了它去搬运，或它这一轮中途退出了派车：空闲返回只在没有搬运可派时才轮到",
            [IdleReturnReasons.VehicleHasPurpose] = "车已有用途占着（搬运、充电，或一次还没收敛的空闲返回）：不再接空闲返回",
            [IdleReturnReasons.VehicleHoldsStation] =
                "车还占着一个站点（例如停在上一次返回的等待点上、等离点证据，或还在充电桩上）：它已经在一个等待之处",
            [IdleReturnReasons.HasNextBusinessTarget] = "车还有一趟没结束的旅程：有下一业务目标，不空闲返回",
            [IdleReturnReasons.ForeignOrderRunning] = "车上有一张不是本服务端建的订单在跑：不空闲返回。那张单见车队视图「车上的外来订单」",
            [IdleReturnReasons.OwnOrderResultUnknown] = "本服务端给这辆车的某张单建单结果未知，还没对账出结论：不空闲返回，等对账",
            [IdleReturnReasons.BatteryUnknownOrCharging] = "电量读不到，或车正在充电（本周期还没充满）：不空闲返回",
            [IdleReturnReasons.BelowMandatoryChargeLine] = "电量低于这辆车充电策略的强制充电线：先去充电，不空闲返回",
            [IdleReturnReasons.VehiclePositionUnknown] = "读不到车在哪个站：没有起点，算不出去等待点的路线，不空闲返回",
            [IdleReturnReasons.RouteGraphUnavailable] = "路网没开或这张图的路网不可用：到不到得了等待点核不了，不猜，不空闲返回",
            [IdleReturnReasons.NoWaitingPointAvailable] =
                "没有可去的等待点：逐个核过之后一个都不剩（被别的车占着、到不了、不接这辆车、停用等），逐点原因见服务端日志事件 2199",
            [IdleReturnReasons.CommitmentRefused] = "选中的等待点在保存那一刻被别的车先拿到了：这一轮不换点，下一轮重新挑",
            [IdleReturnReasons.EvaluationFailed] = "评估这辆车时服务端出错：这一轮它什么也没留下，下一轮重评。具体异常见服务端日志事件 2200",
            [IdleReturnReasons.CooldownAfterEndedOrder] =
                "冷却中：这辆车上一趟空闲返回的单刚被取消、删除，或失败后故障刚被人清除。收尾后 30 秒内不再自动空闲返回"
                + "（配置项 JourneyRuntime:OwnOrderRebuildDelay），给车旁的人走开或把车停住的时间。冷却过后自动再评估，不需要人处理；"
                + "若是有人故意让它别动，请让车急停或切到手动",
            [IdleReturnReasons.StoppedAfterRepeatedEndedOrders] =
                "已停止自动空闲返回：这辆车的空闲返回在 10 分钟内（配置项 JourneyRuntime:OwnOrderRebuildRepeatWindow）"
                + "两次以单被取消、删除或失败收尾，说明有人要它别动，服务端不再自动让它回等待点，并在服务端日志里告警（事件 2227）。"
                + "窗口过去也不会自己解除：这辆车被派了一趟搬运（或别的旅程）之后才重新开始自动空闲返回。"
                + "请到现场与 RIoT 查明为什么反复取消；车停在原地不影响派搬运",
            [IdleReturnReasons.PointTaken] = "这个等待点此刻被别的承诺预占或占用着",
            [IdleReturnReasons.PointUnreachable] = "路网上从车的位置到不了这个等待点，或它不在路网上",
            [IdleReturnReasons.PointFailedLastAttempt] = "这辆车上一次开往这个等待点已确认失败：这一次排除它",
        };

    /// <summary>
    /// 空闲返回旅程上一个码的空闲返回口径说明：共用码用 <see cref="SharedCodesOnIdleReturnJourneys"/>，空闲返回自己的码用
    /// <see cref="BlockedJourneysQueryEndpoint.Descriptions"/>（那里本来就按空闲返回写）。没有说明时为空，测试守着不让它发生。
    /// </summary>
    internal static string? DescribeJourneyCode(string? code) =>
        code is null ? null
        : SharedCodesOnIdleReturnJourneys.TryGetValue(code, out string? shared) ? shared
        : IsIdleReturnExecutionCode(code) ? BlockedJourneysQueryEndpoint.Descriptions.GetValueOrDefault(code)
        : null;

    /// <summary>评估器结论码的说明；没有说明时为空。</summary>
    internal static string? DescribeVerdict(string? code) =>
        code is null ? null : VerdictCodes.GetValueOrDefault(code);

    /// <summary>空闲返回旅程上可能出现的每一个码：空闲返回自己的（保持、收尾、建单结果）加上共用的。</summary>
    internal static IReadOnlyList<string> AllJourneyCodes { get; } =
    [
        .. IdleReturnExecutionReasons.All,
        .. IdleReturnExecutionReasons.LegOutcomeCodes,
        .. SharedCodesOnIdleReturnJourneys.Keys,
    ];

    private static bool IsIdleReturnExecutionCode(string code) =>
        IdleReturnExecutionReasons.All.Contains(code, StringComparer.Ordinal) ||
        IdleReturnExecutionReasons.LegOutcomeCodes.Contains(code, StringComparer.Ordinal);
}
