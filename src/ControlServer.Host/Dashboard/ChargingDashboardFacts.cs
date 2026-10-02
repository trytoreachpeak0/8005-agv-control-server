using System.Globalization;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 充电看板（批次9-10，control-server#408）四张卡片共用的中文说明：周期状态与阶段、桩暂停来源、车辆充电资格暂停原因、人工充电等待原因、
/// 清桩证明、充电旅程上的码，以及桩阶段与告警码。
/// </summary>
/// <remarks>
/// <para>
/// <b>充电自己的码不抄第二遍。</b>以 <c>CHARGING_</c>、<c>CHARGER_</c> 开头的码（充电旅程的阻断码与收尾码、桩独占的释放原因）在
/// <see cref="BlockedJourneysQueryEndpoint.Descriptions"/> 里本来就是按充电写的（control-server#404～#406），这里直接用那一份。
/// </para>
/// <para>
/// <b>共用码另写一份充电口径</b>（与 cs#392 空闲返回同一个理由）：充电旅程的途中监看与搬运同一套（<c>JourneyRuntimeEngine.Charging.cs</c> 的
/// <c>NameStalledOrderAsync</c>、<c>NameCheckpointWaitAsync</c>，推进循环的 <c>NameFailedAdvanceAsync</c>），会写 <c>ORDER_HANG</c> 等码，而那份说明
/// 写的是「为同一条需求重建运单」——充电单没有需求、被取消后不重建。所以这些码在 <see cref="SharedCodesOnChargingJourneys"/> 里按充电写。
/// </para>
/// <para>
/// 守卫不是手写清单：充电执行类测试的夹具挂着保存拦截器，收集实际写进库的每一个码（旅程阻断码、周期状态与结束原因、桩暂停来源、
/// 人工充电等待原因、桩独占释放原因、清桩证明、用途），用例结束时断言每一个都有说明（<c>ChargingDashboardCodeRecorder</c>）。
/// </para>
/// </remarks>
internal static class ChargingDashboardDescriptions
{
    /// <summary><c>chargingCycleState</c> 的七个取值（<see cref="ChargingCycleWireStates.All"/>）。</summary>
    internal static IReadOnlyDictionary<string, string> CycleStates { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ChargingCycleWireStates.NotCharging] = "不在充电周期里",
            [ChargingCycleWireStates.Allocated] = "已分配：桩已为它预占，开往桩的单还没确认",
            [ChargingCycleWireStates.EnRoute] = "去桩途中：开往桩的单已确认，车到桩、停稳并证明之后转充电",
            [ChargingCycleWireStates.Charging] = "充电中：车在桩上，RIoT 报它在充电",
            [ChargingCycleWireStates.Complete] = "已充满：用途已放开，车还停在桩上、桩仍归它，等它离桩三项确认之后才放",
            [ChargingCycleWireStates.UnableToCharge] = "已确认充不上：桩已暂停分配，服务端不为它建单、不动车，等人工清桩",
            [ChargingCycleWireStates.Unknown] = "未知：服务端说不准这一次充电走到了哪一步",
        };

    /// <summary>周期在服务端内部的阶段（<see cref="ChargingCyclePhases.All"/>）。</summary>
    internal static IReadOnlyDictionary<string, string> CyclePhases { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ChargingCyclePhases.Active] = "进行中：从分配到充满离桩",
            [ChargingCyclePhases.Clearing] = "清桩中：服务端不为它建单、不动车，等有权限的人现场确认清桩、旧单终结",
            [ChargingCyclePhases.Ended] = "已结束",
        };

    /// <summary>桩分配暂停的四种来源（<see cref="ChargingStationHoldTriggers.All"/>，<c>REQ-0177</c>、<c>REQ-0285</c>、<c>REQ-0288</c>）。</summary>
    internal static IReadOnlyDictionary<string, string> HoldTriggers { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ChargingStationHoldTriggers.UnableToChargeConfirmed] =
                "充不上：车到桩执行了开始充电，RIoT 返回 407802 且订单停在 HANG，全程没充上，已确认充不上，这个桩不再分给任何车",
            [ChargingStationHoldTriggers.InterruptionConfirmed] = "充电中断：已确认充电中途断开，这个桩与这辆车的充电资格同时暂停",
            [ChargingStationHoldTriggers.NoProgressConfirmed] = "充电无进展：已确认充了一段时间电量不涨，这个桩与这辆车的充电资格同时暂停",
            [ChargingStationHoldTriggers.Maintenance] = "维修：维护管理员或系统管理员让待修的桩退出分配，桩在名册里的身份保留",
        };

    /// <summary>车辆充电资格暂停的原因（<see cref="VehicleChargingEligibilityHoldReasons.All"/>）。</summary>
    internal static IReadOnlyDictionary<string, string> EligibilityHoldReasons { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VehicleChargingEligibilityHoldReasons.InterruptionConfirmed] = "充电中断：这辆车充电中途断开，恢复之前不再给它分桩",
            [VehicleChargingEligibilityHoldReasons.NoProgressConfirmed] =
                "充电无进展：这辆车充了一段时间电量不涨，或在同一个没离开过的桩上第二次掉回强充线以下（原桩反复重充），恢复之前不再给它分桩",
        };

    /// <summary>服务端置人工充电等待的原因（<see cref="ManualChargingHoldReasons"/>）。</summary>
    internal static IReadOnlyDictionary<string, string> ManualHoldReasons { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ManualChargingHoldReasons.RosterEmpty] =
                "名册为空：充电桩名册是空的（或没有这辆车能用的桩），自动充电停了，这辆车等人工充电",
            [ManualChargingHoldReasons.ChargingRepeatedlyFailed] =
                "反复失败：这辆车的充电单短时间内第二次被取消、删除或失败，说明有人要它别动，不再自动给它安排充电，改为人工充电等待",
            [ManualChargingHoldReasons.UnableToChargeLowBattery] =
                "充不上且电量等不起：维护人员现场确认这辆车在桩上充不上，名册里没有别的桩给它，电量又已低于最低余量，改为人工充电等待",
        };

    /// <summary>人工充电等待的解除提示：只有这一个出口。</summary>
    internal const string ManualHoldReleaseHint =
        "由管理员在车上点「充电后返回服务」解除；电量回升、名册重新启用都不会自动解除";

    /// <summary>清桩的两种完成证明（<see cref="StationClearanceProofs.All"/>，<c>REQ-0179</c>）。</summary>
    internal static IReadOnlyDictionary<string, string> ClearanceProofs { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StationClearanceProofs.ArrivedAtWaitingPoint] = "系统确认车已到等待点",
            [StationClearanceProofs.ManualConfirmation] = "人工确认：车已挪到安全位置、原桩已腾空",
        };

    /// <summary>
    /// 充电旅程上可能出现的共用码（不以 <c>CHARGING_</c>、<c>CHARGER_</c> 开头的那些），按充电的实际行为写。来源见类说明。
    /// </summary>
    internal static IReadOnlyDictionary<string, string> SharedCodesOnChargingJourneys { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JourneyRuntimeEngine.OrderHangReason] =
                "开往充电桩（或在桩上）的充电单在 RIoT 上挂起（HANG），例如急停或切了手动：这不算「充不上」，服务端不暂停桩、不暂停车的充电资格、"
                + "不释放，也不会另建一张充电单，车、桩预占与充电用途都保持。请到现场确认原因，在 RIoT 里继续（continue）；继续后车会接着开往充电桩，原因码自动消失",
            [JourneyRuntimeEngine.OrderStateUnrecognizedReason] =
                "充电单在 RIoT 上处于未识别的状态（SUSPENDED 8）：服务端按仍在执行处理，车、桩预占与充电用途都保持，不做任何自动动作，"
                + "请人工到 RIoT 核实",
            [JourneyRuntimeEngine.OrderEndedWithoutArrivalReason] =
                "充电单在 RIoT 被取消或删除，服务端还没证明车已停稳、名下没有单：车、桩预占与充电用途都保持，也不会另建一张充电单。车证明停稳之后这次充电按失败结束，"
                + "桩要等充电已停、车不在桩上、桩位空闲三项确认之后才放。持续不消失请到现场看车是否还在动",
            [VehicleFaultEvidence.OrderFailed] =
                "充电单在 RIoT 上失败（FAILED），服务端已把车判为疑似故障：不派新单，车、桩预占与充电用途都保持。请到现场排除原因；若急停已锁住，"
                + "先按急停人工解除；然后由现场人员经故障清除入口确认。故障清除后这次充电按失败结束，不会另建一张充电单；30 秒内不给这辆车安排任何充电桩，"
                + "10 分钟内它的充电再失败一次，就改为人工充电等待",
            [VehicleFaultEvidence.DoorNotProvenLocked] =
                "车在开往充电桩的途中，车载端报门锁未锁闭或仓位状态读不到：服务端已按住这张单，证不出停稳即急停，并把车判为疑似故障，"
                + "车、桩预占与充电用途都保持。请到现场确认车已停、门已锁好；门锁锁好后急停会自动解除，但单仍停着，要由现场人员经故障处置入口"
                + "「继续原单」车才会再走",
            [JourneyRuntimeEngine.HeldOrderResumedWithoutContinueReason] =
                "开往充电桩的车因门锁被按住、急停，门锁锁好后急停已自动解除；这张单本应停着等人继续，RIoT 却报它在执行，而没有人按过继续。"
                + "车若在动，服务端会再次急停。请到现场确认车的状态，并在 RIoT 上核实是谁让这张单继续的",
            [JourneyRuntimeEngine.CheckpointWaitReason] =
                "开往充电桩的车停在 RIoT 的检查点前等放行：这是 RIoT 的交通管制，服务端不干预，放行后自动继续",
            [JourneyRuntimeEngine.CheckpointWaitExceededReason] =
                "开往充电桩的车在 RIoT 检查点前等放行，已超过服务端的等待时限：服务端只告警，不急停、不换桩。请到 RIoT 查看交通管制为什么一直不放行",
            [JourneyRuntimeEngine.AdvanceFailedReason] =
                "服务端推进这趟充电旅程时出错，它停在原处、一步没动（不发命令，桩预占与充电用途保持）。每一轮都会重试，走通后这个码自动消失；"
                + "一直不消失请找值班工程师看服务端日志里的事件 2002，那里有具体的异常",
        };

    /// <summary>
    /// 充电自己的码里，阻断卡片那一份没有写说明的（那份只管写在旅程上的码）。由说明守卫在真实跑出来的码里找到：
    /// <c>CHARGER_RELEASED_AFTER_ENDED_CYCLE</c> 只写在桩独占的经过上。
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ChargingCodesNotOnTheBlockedCard { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ChargingExecutionReasons.ReservationReleasedAfterEndedCycle] =
                "充电桩已释放：这一次充电已按失败结束，充电已停、原车不在桩上、桩位可确认空闲三项都确认了，桩可以分给下一辆车",
        };

    /// <summary>
    /// 充电旅程上（以及周期结束原因、桩独占释放原因里）一个码的中文说明：共用码取充电口径，充电自己的码取阻断卡片那一份。没有说明时答空。
    /// </summary>
    internal static string? DescribeChargingCode(string? code) =>
        code is null ? null
        : SharedCodesOnChargingJourneys.TryGetValue(code, out string? shared) ? shared
        : ChargingCodesNotOnTheBlockedCard.TryGetValue(code, out string? own) ? own
        : code.StartsWith("CHARGING_", StringComparison.Ordinal) || code.StartsWith("CHARGER_", StringComparison.Ordinal)
            ? BlockedJourneysQueryEndpoint.Descriptions.GetValueOrDefault(code)
            : null;

    /// <summary>
    /// 充电分配的结论码（<see cref="ChargingAllocationReasons"/> 的每个常量，看板测试反射扫）：逐车卡片「排队原因」那一格。
    /// 故障阻断、投运策略与车辆动态事实那几格用的是派车链共用的码，说明取派车积压卡片那一份（<see cref="DescribeAllocationReason"/>）。
    /// </summary>
    internal static IReadOnlyDictionary<string, string> AllocationReasons { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ChargingAllocationReasons.Committed] = "已安排充电：桩已为它预占，充电用途与开往桩的单在同一次保存里形成",
            [ChargingAllocationReasons.NotRequired] = "不需要充电：电量不低于它所用策略版本的强制充电线",
            [ChargingAllocationReasons.ManualChargingHoldActive] = "在人工充电等待中：不分桩，要先由管理员在车上点「充电后返回服务」",
            [ChargingAllocationReasons.VehicleHasPurpose] = "车已有用途（搬运、空闲返回或一次没结束的充电）：已开始的用途保持，不改去充电",
            [ChargingAllocationReasons.VehicleEligibilityHeld] = "这辆车的充电资格暂停着：恢复之前不分桩",
            [ChargingAllocationReasons.VehicleStillHoldsCharger] =
                "这辆车自己还占着一个充电桩（上一次充电结束了、桩还没按三项确认放掉）：不再给它分桩，免得向原桩再发一次充电动作",
            [ChargingAllocationReasons.VehiclePositionUnknown] = "不知道车停在哪个站：算不出到各个桩的路程，不分桩",
            [ChargingAllocationReasons.RouteGraphUnavailable] = "路网没开或这张图的路网不可用：最近优先无从计算，不猜",
            [ChargingAllocationReasons.RosterEmptyManualHold] = "名册里没有这辆车能用的桩：转为人工充电等待并告警",
            [ChargingAllocationReasons.RepeatedlyFailedManualHold] = "这辆车的充电单短时间内第二次被取消、删除或失败：转为人工充电等待并告警",
            [ChargingAllocationReasons.CooldownAfterFailedCycle] = "刚有一次充电以失败结束，还在冷却时间里：这段时间不给它安排任何充电桩",
            [ChargingAllocationReasons.NoChargerAvailable] = "无合格桩：名册里这辆车能用的桩逐个核过之后一个都不剩（逐桩原因见细节），车留在队里",
            [ChargingAllocationReasons.DepartureNotProven] =
                "有桩可分，但车此刻过不了出发前安全检查（仓门没锁好、安全摘要有阻断原因、RIoT 读不到它停稳）：不安排，门一好下一轮就分",
            [ChargingAllocationReasons.CommitmentRefused] = "算出了桩，保存时被数据库拒掉（车或桩被别人先拿到）：整笔回滚，下一轮重评",
            [ChargingAllocationReasons.EvaluationFailed] = "评估这辆车时出了异常：它这一轮什么也没留下，下一轮重评；详情见服务端日志事件 2243",
            [ChargingAllocationReasons.ChargerNotInRoster] = "桩不在当前生效的名册里（或名册里它的车辆范围不含这辆车）",
            [ChargingAllocationReasons.ChargerAllocationHeld] = "桩的分配暂停着",
            [ChargingAllocationReasons.ChargerNotOnCurrentMap] = "桩不在 RIoT 当前地图的站点目录里，或站名与名册登记的不一致",
            [ChargingAllocationReasons.ChargerReservedOrOccupied] = "桩已被别的车预占或占用（预占成功后不可抢占）",
            [ChargingAllocationReasons.ChargerOccupancyUnknown] = "桩占用未知：车辆位置、未完成订单清单或订单目的站有一样读不全，未知即不分配",
            [ChargingAllocationReasons.ChargerOccupiedByVehicle] = "本项目另一辆车停在这个桩上",
            [ChargingAllocationReasons.ChargerTargetedByRunningOrder] = "本项目车辆有一张在跑的单以这个桩为目的站",
            [ChargingAllocationReasons.ChargerUnreachable] = "路网上从车的位置到不了这个桩，或它不在路网上",
        };

    /// <summary>
    /// 充电分配借用的派车链判定码（<c>VehicleNewPurposeReadiness.JudgeForChargingAsync</c>：故障阻断、<c>VehicleDynamicFactsCriterion.Evaluate</c>；
    /// <c>BatteryEligibility.Judge</c> 的策略与电量几支），按「为什么不给它分桩」写。由说明守卫在真实跑出来的结论里找到，再按那两处源码补全同族的码。
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ReadinessReasons { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VehicleFaultBlockCriterion.SuspectedReason] = "车被判为疑似故障：故障清除之前不给它安排充电（也不派别的活）",
            [VehicleFaultBlockCriterion.IsolatedReason] = "车已被故障隔离：解除隔离之前不给它安排充电",
            [VehicleFaultBlockCriterion.IdentityUnresolvedReason] = "车的故障记录对不上身份：说不准它有没有故障，不给它安排充电",
            ["ONBOARD_FACTS_NOT_READY"] = "车载端会话没有就绪：拿不到车载端的事实，不给它安排充电",
            ["ONBOARD_DEPARTURE_UNSAFE"] = "车载端此刻报不能出发（仓门没锁好、安全摘要带阻断原因或有未知）：不给它安排充电",
            ["RIOT_VEHICLE_NOT_AVAILABLE"] = "RIoT 报这辆车离线或被禁用：不给它安排充电",
            ["RIOT_VEHICLE_BINDING_MISMATCH"] = "RIoT 读回来的车与这辆车的绑定对不上：不给它安排充电",
            ["RIOT_VEHICLE_NOT_IDLE"] = "RIoT 报这辆车不空闲：不给它安排充电",
            ["RIOT_VEHICLE_MAP_MISMATCH"] = "RIoT 报这辆车此刻在别的地图上：不给它安排充电",
            ["RIOT_VEHICLE_FACT_STALE"] = "这辆车的 RIoT 读数太旧或时刻在未来：说不准它此刻的状态，不给它安排充电",
            [VehicleDynamicFactsCriterion.BatteryFactUnknownReason] = "电量未知：RIoT 没报这辆车的电量或电池状态，不知道要不要充电，不分桩",
            [VehicleDynamicFactsCriterion.BatteryPolicyNotSatisfiedReason] = "车正在充电（例如有人在给它充）：不另外给它分桩",
            ["RIOT_VEHICLE_NOT_STOPPED"] = "RIoT 读到这辆车在动（或读不到速度）：不给它安排充电",
            ["RIOT_VEHICLE_ORDER_OCCUPIED"] = "RIoT 上这辆车身上有单（被锁定或有任务号）：不给它安排充电",
            [Runtime.Dispatch.DispatchReasonCodes.ChargingPolicyNotApproved] = "这辆车没有已批准、已生效的充电策略：不知道强制充电线在哪，不分桩",
            [Runtime.Dispatch.DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine] =
                "这辆车的充电策略里强制充电线不高于服务端的救命告警线：这一版策略不能用，不分桩",
        };

    /// <summary>
    /// 按电量下的结论（<c>QualifyAsync</c> 的电量一段：不需要充电、正在充电、强制充电）。这一轮的电量观测不新鲜时它们不作数（增量审查 S2'）。
    /// </summary>
    internal static IReadOnlySet<string> BatteryDerivedReasons { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ChargingAllocationReasons.NotRequired,
        VehicleDynamicFactsCriterion.BatteryPolicyNotSatisfiedReason,
        Runtime.Dispatch.DispatchReasonCodes.MandatoryChargeRequired,
    };

    /// <summary>观测不新鲜时，按电量下的结论那一格写的话；原码另放括号里。</summary>
    internal const string BatteryVerdictVoid = "电量拿不到，这一轮按电量得出的结论不作数";

    /// <summary>一个排队原因码的中文说明：充电分配自己的码、借用的派车链判定码，或派车积压卡片那一份。没有说明时答空。</summary>
    internal static string? DescribeAllocationReason(string? code) =>
        code is null ? null
        : AllocationReasons.GetValueOrDefault(code)
          ?? ReadinessReasons.GetValueOrDefault(code)
          ?? DispatchBacklogQueryEndpoint.Descriptions.GetValueOrDefault(code);

    /// <summary><c>VehicleBusinessStateSnapshot.batteryState</c> 的四个值。</summary>
    internal static IReadOnlyDictionary<string, string> BatteryStateProjections { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Runtime.Dispatch.BatteryStates.Sufficient] = "够用：跑完下一趟还保得住最低任务后电量余量",
            [Runtime.Dispatch.BatteryStates.Low] = "偏低：不低于强制充电线，但跑完下一趟保不住最低任务后电量余量",
            [Runtime.Dispatch.BatteryStates.MandatoryCharge] = "强制充电：低于它所用策略版本的强制充电线",
            [Runtime.Dispatch.BatteryStates.Unknown] = "未知：没有已批准策略，或电量、电池状态读不到",
        };

    // ---- 桩的阶段（充电桩卡片）----

    internal const string StageFree = "FREE";
    internal const string StageReserved = "RESERVED";
    internal const string StageOccupied = "OCCUPIED";
    internal const string StageCompleteAwaitingDeparture = "COMPLETE_AWAITING_DEPARTURE";
    internal const string StageClearing = "CLEARING";
    internal const string StageFailedCycleAwaitingRelease = "FAILED_CYCLE_AWAITING_RELEASE";
    internal const string StageHeldWithoutOpenCycle = "HELD_WITHOUT_OPEN_CYCLE";

    /// <summary>桩的阶段与它此刻为什么还没放（cs#405 审查 S1 的桩独占视图：谁占着、哪个阶段、为什么不放）。</summary>
    internal static IReadOnlyDictionary<string, string> ChargerStages { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StageFree] = "空闲：没有车预占或占用",
            [StageReserved] = "预占：已为这辆车留下，车还在去桩的路上；车到桩、停稳并证明之后转占用",
            [StageOccupied] = "占用：车在桩上充电；充满之后也不放",
            [StageCompleteAwaitingDeparture] =
                "充满待离桩：车已充满、用途已放开，还停在桩上。要等它接到下一项任务离开，并且充电已停、车不在桩上、桩位可确认空闲三项都确认了才放；"
                + "服务端不会为离桩单独建单",
            [StageClearing] =
                "清桩中：服务端不为它建单、不动车。要等有权限的人现场确认清桩（车已挪开、桩已腾空），并且旧充电单在 RIoT 里终结，"
                + "两样都齐了的那一轮才放",
            [StageFailedCycleAwaitingRelease] =
                "失败待放桩：这一次充电已按失败结束，桩还没放。要等充电已停、原车不在桩上、桩位可确认空闲三项都确认了才放，没有按时间放的分支",
            [StageHeldWithoutOpenCycle] =
                "桩仍被持有，但它的充电周期已结束（或找不到）而结束原因不在自动放桩的几种里：服务端不会自动放。请到现场确认后经人工释放入口处理，并报开发",
        };

    /// <summary>
    /// 清桩中、旧充电单却被人在 RIoT 里恢复了（<see cref="ChargingExecutionReasons.OldOrderResumedWhileClearing"/>）时，阶段与「现场要做的」那一格写的话。
    /// 这时车可能自己开回桩上，任何「车不动」一类的话都不能出现在同一行（独立审查 S1）。
    /// </summary>
    internal const string ClearingVehicleMayMove =
        "车可能移动，先联系现场：这辆车的旧充电单在 RIoT 里被人恢复了，车可能自己开回充电桩，而现场可能有人正在清桩。"
        + "请立刻通知现场人员避让，并在 RIoT 里结束这张旧单；旧单结束后须重新确认清桩";

    /// <summary>清桩中、旧单没被恢复时「现场要做的」那一格：只说服务端自己做什么、不做什么。</summary>
    internal const string ClearingGuidance =
        "服务端不为它建单、不动车；等 R-11／R-13 名单里的人到现场确认清桩，并在 RIoT 里结束旧充电单";

    /// <summary>清桩中的车此刻的说明：旧单被恢复时是 <see cref="ClearingVehicleMayMove"/>，否则是 <paramref name="otherwise"/>。</summary>
    internal static string WhileClearing(string? journeyCode, string otherwise) =>
        journeyCode == ChargingExecutionReasons.OldOrderResumedWhileClearing ? ClearingVehicleMayMove : otherwise;

    // ---- 告警（充电告警卡片）----

    internal const string SeverityCritical = "CRITICAL";
    internal const string SeverityHigh = "HIGH";
    internal const string SeverityMedium = "MEDIUM";

    internal static IReadOnlyDictionary<string, string> Severities { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SeverityCritical] = "紧急：现场可能有车在动或有人身风险",
            [SeverityHigh] = "高：自动充电停了或车被留在原地，需要人去处理",
            [SeverityMedium] = "中：充电在等某个条件，持续不消失要有人看",
        };

    internal static int SeverityRank(string severity) => severity switch
    {
        SeverityCritical => 0,
        SeverityHigh => 1,
        _ => 2,
    };

    /// <summary>告警卡片上不来自旅程码的那几种告警。</summary>
    internal const string AlarmRosterEmpty = "CHARGER_ROSTER_EMPTY";
    internal const string AlarmRosterNeverImported = "CHARGER_ROSTER_NEVER_IMPORTED";
    internal const string AlarmManualChargingHold = "MANUAL_CHARGING_HOLD";

    internal static IReadOnlyDictionary<string, string> OwnAlarmCodes { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AlarmRosterEmpty] = "充电桩名册为空：当前生效的名册一个桩都没有，自动充电已停，需要充电的车转为人工充电等待（规格 5.5，不静默）",
            [AlarmRosterNeverImported] = "充电桩名册从没导入过：没有任何一版名册，自动充电从没开始，需要充电的车转为人工充电等待",
            [AlarmManualChargingHold] = "这辆车在服务端持有的人工充电等待中：不分桩、不接搬运、不做空闲返回、不动车",
            [JourneyRuntimeEngine.OnboardSessionLostReason] =
                "车载端会话失联：这辆车正处在充电周期、清桩或人工充电等待中，车载端听不到了。服务端只阻断它的新业务，继续经 RIoT 观察，"
                + "不结束、不释放、不改派（REQ-0287）",
        };

    /// <summary>每种告警「现场该做什么」。告警卡片上出现的每个码都要在这里有一句（看板测试核）。</summary>
    internal static IReadOnlyDictionary<string, string> FieldActions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AlarmRosterEmpty] = "给需要充电的车人工充电，充完由管理员在车上点「充电后返回服务」；要恢复自动充电，导入一版登记了桩的名册",
            [AlarmRosterNeverImported] = "给需要充电的车人工充电；要开自动充电，先按治理流程导入充电桩名册",
            [AlarmManualChargingHold] = "到车前人工充电，充完由管理员在车上点「充电后返回服务」",
            [JourneyRuntimeEngine.OnboardSessionLostReason] = "检查车载端电脑与网络；车可能仍在桩上，不要据此认为桩已空",
            [StageCompleteAwaitingDeparture] =
                "看车是否在桩上关了机，或一直没有活派给它；缺哪一项确认见服务端日志事件 2249。确认车已离开而桩没放时，经人工释放入口处理",
            [StageFailedCycleAwaitingRelease] =
                "到现场看车与桩：车被关机或拖走时三项确认永远不来；缺哪一项见服务端日志事件 2247，需要时经人工释放入口处理",
            [ChargingStationHoldTriggers.UnableToChargeConfirmed] =
                "R-11／R-13 名单里的人到现场把车挪开、确认桩已腾空，并在 RIoT 里结束旧充电单；桩修好后做恢复确认",
            [ChargingStationHoldTriggers.InterruptionConfirmed] = "检查桩与车的充电接口；修好后分别做桩的恢复确认与车的充电资格恢复",
            [ChargingStationHoldTriggers.NoProgressConfirmed] = "检查桩的输出与车的电池；修好后分别做桩的恢复确认与车的充电资格恢复",
            [ChargingStationHoldTriggers.Maintenance] = "维修完成后由维护管理员或系统管理员做恢复确认",
            [ChargingExecutionReasons.OldOrderResumedWhileClearing] =
                "立刻联系现场人员注意车辆可能移动，并在 RIoT 里结束这张旧单；旧单结束后重新确认清桩",
            [ChargingExecutionReasons.UnableToChargeClearing] =
                "R-11／R-13 名单里的人到现场把车挪开、确认桩已腾空，并在 RIoT 里结束旧充电单",
            [ChargingExecutionReasons.ClearedOldOrderUnsettled] = "在 RIoT 里把这张旧充电单结束",
            [ChargingExecutionReasons.ClearanceChargerNotVacant] =
                "到现场把车挪离原桩；车其实已不在桩上时，在 RIoT 里给车重定位或给车断电",
            [ChargingExecutionReasons.ChargerNotEngaged] = "到现场看车是否插好、充电桩是否通电",
            // control-server#407：充电中断与充电无进展。
            [ChargingExecutionReasons.InterruptionClearing] =
                "R-11／R-13 名单里的人到现场把车挪开、确认桩已腾空；之后桩与车各自检查，分别做桩的恢复确认与车的充电资格恢复",
            [ChargingExecutionReasons.NoProgressClearing] =
                "R-11／R-13 名单里的人到现场把车挪开、确认桩已腾空；之后检查桩的输出与车的电池，分别做桩的恢复确认与车的充电资格恢复",
            [ChargingExecutionReasons.ClearingVehicleStillCharging] = "现场先结束这辆车的充电，再把车挪开，再确认清桩",
            [ChargingExecutionReasons.InterruptionNotIsolated] =
                "到现场查看车与桩；车和桩都没被暂停，车一直占着这个桩。要自动隔离，配置清桩名单与 VehicleFaultRecovery 入口",
            [ChargingExecutionReasons.NoProgressNotIsolated] =
                "到现场查看车的电池与桩的输出；车和桩都没被暂停，车一直占着这个桩。要自动隔离，配置清桩名单与 VehicleFaultRecovery 入口",
            [ChargingExecutionReasons.StalledUnstableReadings] =
                "到现场查看车的充电接触与桩的输出；车和桩都没被暂停，车一直占着这个桩。需要让车离开时，R-11／R-13 名单里的人做人工清桩",
            [ChargingExecutionReasons.VehicleObservationLost] = "检查车与 RIoT 的连接；车可能仍在桩上，不要据此认为桩已空",
            [ChargingExecutionReasons.BatteryTelemetryLost] = "检查车的电量上报与 RIoT",
            [ChargingExecutionReasons.ReservationLostAtArrival] = "到现场确认车停的位置与桩的归属",
            [ChargingExecutionReasons.ArrivalNotProven] = "到现场看车停在哪里",
            [ChargingExecutionReasons.OrderNotFound] = "在 RIoT 上查这张充电单去了哪里、车在哪里",
            [ChargingExecutionReasons.DepartureNotProven] = "检查车载端连接与仓门",
            [ChargingExecutionReasons.CycleMissing] = "联系开发：这是不该出现的状态",
            [ChargingExecutionReasons.LegOutcomeCode(MovementDispatchOutcome.ResultUnknown)] = "到 RIoT 核对这张充电单与车的状态",
            [ChargingExecutionReasons.LegOutcomeCode(MovementDispatchOutcome.CreateDispatchDisabled)] = "确认建单开关是否应该打开",
            [ChargingExecutionReasons.LegOutcomeCode(MovementDispatchOutcome.UnsupportedOrderShape)] = "查服务端日志并报开发",
            [JourneyRuntimeEngine.OrderHangReason] = "到现场确认原因，在 RIoT 里继续这张单",
            [JourneyRuntimeEngine.OrderStateUnrecognizedReason] = "到 RIoT 核实这张单的状态",
            [JourneyRuntimeEngine.OrderEndedWithoutArrivalReason] = "到现场看车是否还在动",
            [VehicleFaultEvidence.OrderFailed] = "到现场排除原因，必要时先人工解除急停，再经故障清除入口确认",
            [VehicleFaultEvidence.DoorNotProvenLocked] = "到现场确认车已停、门已锁好，再经故障处置入口「继续原单」",
            [JourneyRuntimeEngine.HeldOrderResumedWithoutContinueReason] = "到现场确认车的状态，并在 RIoT 上核实是谁让这张单继续的",
            [JourneyRuntimeEngine.CheckpointWaitReason] = "不用处理：放行后自动继续",
            [JourneyRuntimeEngine.CheckpointWaitExceededReason] = "到 RIoT 查看交通管制为什么一直不放行",
            [JourneyRuntimeEngine.AdvanceFailedReason] = "持续不消失找值班工程师看服务端日志事件 2002",
        };

    /// <summary>一个告警码有多紧急：可能让车动的最高，自动充电停了、车被留在原地的次之，其余为中。</summary>
    internal static string SeverityOf(string code) => code switch
    {
        ChargingExecutionReasons.OldOrderResumedWhileClearing
            or VehicleFaultEvidence.DoorNotProvenLocked
            or JourneyRuntimeEngine.HeldOrderResumedWithoutContinueReason => SeverityCritical,
        AlarmRosterEmpty
            or AlarmRosterNeverImported
            or AlarmManualChargingHold
            or JourneyRuntimeEngine.OnboardSessionLostReason
            or ChargingStationHoldTriggers.UnableToChargeConfirmed
            or ChargingStationHoldTriggers.InterruptionConfirmed
            or ChargingStationHoldTriggers.NoProgressConfirmed
            or ChargingExecutionReasons.UnableToChargeClearing
            or ChargingExecutionReasons.InterruptionClearing
            or ChargingExecutionReasons.NoProgressClearing
            or ChargingExecutionReasons.ClearingVehicleStillCharging
            or ChargingExecutionReasons.InterruptionNotIsolated
            or ChargingExecutionReasons.NoProgressNotIsolated
            or ChargingExecutionReasons.StalledUnstableReadings
            or ChargingExecutionReasons.ClearedOldOrderUnsettled
            or ChargingExecutionReasons.ClearanceChargerNotVacant
            or ChargingExecutionReasons.VehicleObservationLost
            or ChargingExecutionReasons.ReservationLostAtArrival
            or ChargingExecutionReasons.CycleMissing
            or VehicleFaultEvidence.OrderFailed => SeverityHigh,
        _ => SeverityMedium,
    };

    /// <summary>告警码的中文说明：卡片自己的码、桩阶段、暂停来源、充电旅程上的码，依次找。</summary>
    internal static string? DescribeAlarm(string code) =>
        OwnAlarmCodes.GetValueOrDefault(code)
        ?? ChargerStages.GetValueOrDefault(code)
        ?? HoldTriggers.GetValueOrDefault(code)
        ?? DescribeChargingCode(code);

    // ---- 本版本读不到、或还没实施的几格 ----

    /// <summary>车不在最近一轮已完成的充电分配里时，电量与排队原因那几格写的话。不显示它更早的值（REQ-0269）。</summary>
    internal const string NotEvaluatedThisPass =
        "本轮没有评估这辆车：最近一轮充电分配没有交到它（车不空闲、在途或在干别的活时派车轮不把它交给充电分配），这里不显示它更早的电量与结论";

    /// <summary>服务启动以来还没有完成过一轮充电分配时写的话。</summary>
    internal const string NoPassCompletedYet = "本轮没有评估这辆车：服务启动以来还没有完成过一轮充电分配";

    /// <summary>超出时效窗口时写的话（窗口按实际配置的轮询间隔算），原因列全。</summary>
    internal static string PassNotRunning(TimeSpan window) =>
        string.Create(CultureInfo.InvariantCulture, $"本轮没有评估这辆车：最近 {window.TotalSeconds:0.###} 秒内没有完成过一轮充电分配。")
        + "可能的原因：全车队没有空闲车（派车轮不跑）、MesIngest 读不到（这一轮不派车）、引擎这一轮出错、一轮跑得太慢（例如 RIoT 应答慢）。"
        + "这里不显示更早的电量与结论";

    /// <summary>无合格桩为什么不在告警卡片的行里：它是分配结论，在逐车卡片的排队原因那一格。</summary>
    internal const string NoChargerAvailableNote =
        "无合格桩（CHARGING_NO_CHARGER_AVAILABLE）不单列在这里：它是充电分配的结论，看「逐车充电状态」卡片的排队原因（取自最近一轮已完成的分配）；服务端日志事件 2246 每车每种结论告警一次";

    /// <summary>中断与无进展在告警卡片上的样子（批次9-09，control-server#407）。</summary>
    internal const string InterruptionAlarmsNote =
        "充电中断与充电无进展：隔离了的，以桩的分配暂停（INTERRUPTION_CONFIRMED／NO_PROGRESS_CONFIRMED）与清桩中的旅程码列在这里；"
        + "只告警、未隔离的，以旅程码 CHARGING_INTERRUPTION_NOT_ISOLATED／CHARGING_NO_PROGRESS_NOT_ISOLATED 列在这里。车的充电资格暂停看「充电暂停与等待」";

    /// <summary>车辆充电资格暂停从哪来、怎么恢复（批次9-09，control-server#407）。</summary>
    internal const string EligibilityHoldsNote =
        "车辆充电资格暂停来自充电中断、充电无进展或原桩反复重充（原桩反复重充只暂停车、不暂停桩）。暂停着的车不分任何桩，需要充电时排队并告警。"
        + "恢复走 Host 的车辆充电资格恢复入口，与桩的恢复确认各自独立";

    /// <summary>Host 上的三个充电桩入口（control-server#406）：看板只读，只写出路径作为指引。</summary>
    internal static object HostEntries { get; } = new
    {
        maintenanceHold = "POST " + ChargingStationEndpoints.HoldRoute,
        recovery = "POST " + ChargingStationEndpoints.RecoveryRoute,
        vehicleEligibilityRecovery = "POST " + ChargingStationEndpoints.VehicleRecoveryRoute,
        manualClearance = "POST " + ChargingStationEndpoints.ClearanceRoute,
        stationExclusivityRelease = "POST " + StationExclusivityReleaseEndpoints.Route,
    };

    internal static string Minutes(TimeSpan span) =>
        ((long)Math.Round(span.TotalMinutes)).ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 充电看板一次读到的库里事实（批次9-10，control-server#408）：名册、周期、桩独占、暂停、清桩、人工充电等待、充电旅程与用途占有。
/// 只读库，不调 RIoT、不在这里另算运行时的判定。
/// </summary>
internal sealed class ChargingDashboardFacts
{
    private ChargingDashboardFacts()
    {
    }

    public required DashboardFleetContact Contact { get; init; }

    /// <summary>当前（版本号最大）的名册版本；从没导入过为空。</summary>
    public ChargerRosterVersionRow? Roster { get; init; }

    public required ChargerRosterEntryRow[] RosterEntries { get; init; }

    public required ChargerRosterVehicleScopeRow[] RosterScopes { get; init; }

    /// <summary>名册的全部版本，新的在前（REQ-0171 的批准与变更记录）。</summary>
    public required ChargerRosterVersionRow[] RosterHistory { get; init; }

    /// <summary>各版本名册里每个桩的站名，按 (版本, 地图, 站号)：周期按它冻结的那一版找桩名。</summary>
    public required Dictionary<(long Version, int MapId, int StationId), string> StationNames { get; init; }

    public required StationExclusivityRow[] Exclusivities { get; init; }

    /// <summary>未结束的周期（一车至多一个）。</summary>
    public required ChargingCycleRow[] OpenCycles { get; init; }

    /// <summary>桩独占的持有旅程对应的周期（含已结束的），按旅程 id。</summary>
    public required Dictionary<string, ChargingCycleRow> CyclesOfHeldChargers { get; init; }

    /// <summary>还没恢复的桩分配暂停。</summary>
    public required ChargingStationAllocationHoldRow[] OpenStationHolds { get; init; }

    /// <summary>还没恢复的车辆充电资格暂停。</summary>
    public required VehicleChargingEligibilityHoldRow[] OpenEligibilityHolds { get; init; }

    public required ManualChargingHoldRow[] ManualHolds { get; init; }

    /// <summary>未完成的清桩，按周期 id。</summary>
    public required Dictionary<string, StationClearanceRow> OpenClearances { get; init; }

    /// <summary>最近完成的清桩记录（新的在前，至多 <see cref="RecentClearanceCount"/> 条）。</summary>
    public required StationClearanceRow[] RecentClearances { get; init; }

    /// <summary>未完成的充电旅程，按旅程 id。</summary>
    public required Dictionary<string, JourneyRuntimeRow> OpenChargingJourneys { get; init; }

    public required Dictionary<string, VehiclePurposeClaimRow> Claims { get; init; }

    public const int RecentClearanceCount = 10;

    public bool RosterEmpty => Roster is null || RosterEntries.Length == 0;

    public static async Task<ChargingDashboardFacts> ReadAsync(
        ControlServerDbContext dbContext,
        VehicleRoster roster,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        DashboardFleetContact contact = await DashboardFleetContact.ReadAsync(dbContext, roster, now, cancellationToken);

        ChargerRosterVersionRow[] history = await dbContext.Set<ChargerRosterVersionRow>().AsNoTracking()
            .ToArrayAsync(cancellationToken);
        history = [.. history.OrderByDescending(row => row.Version)];
        ChargerRosterVersionRow? current = history.FirstOrDefault();
        ChargerRosterEntryRow[] allEntries = await dbContext.Set<ChargerRosterEntryRow>().AsNoTracking()
            .ToArrayAsync(cancellationToken);
        ChargerRosterVehicleScopeRow[] scopes = current is null
            ? []
            : await dbContext.Set<ChargerRosterVehicleScopeRow>().AsNoTracking()
                .Where(row => row.Version == current.Version)
                .ToArrayAsync(cancellationToken);

        StationExclusivityRow[] exclusivities =
            await StationHoldings.ReadAsync(dbContext, StationExclusivityKinds.Charger, cancellationToken);
        string[] heldJourneys = [.. exclusivities.Select(row => row.JourneyId)];
        ChargingCycleRow[] cycles = await dbContext.Set<ChargingCycleRow>().AsNoTracking()
            .Where(row => row.Phase != ChargingCyclePhases.Ended || heldJourneys.Contains(row.JourneyId))
            .ToArrayAsync(cancellationToken);

        string[] recovered = await dbContext.Set<ChargingStationRecoveryRow>().AsNoTracking()
            .Select(row => row.HoldId)
            .ToArrayAsync(cancellationToken);
        ChargingStationAllocationHoldRow[] stationHolds = await dbContext.Set<ChargingStationAllocationHoldRow>().AsNoTracking()
            .Where(row => !recovered.Contains(row.HoldId))
            .ToArrayAsync(cancellationToken);
        string[] vehicleRecovered = await dbContext.Set<VehicleChargingEligibilityRecoveryRow>().AsNoTracking()
            .Select(row => row.HoldId)
            .ToArrayAsync(cancellationToken);
        VehicleChargingEligibilityHoldRow[] eligibilityHolds = await dbContext.Set<VehicleChargingEligibilityHoldRow>().AsNoTracking()
            .Where(row => !vehicleRecovered.Contains(row.HoldId))
            .ToArrayAsync(cancellationToken);

        ManualChargingHoldRow[] manualHolds = await dbContext.Set<ManualChargingHoldRow>().AsNoTracking()
            .ToArrayAsync(cancellationToken);
        StationClearanceRow[] clearances = await dbContext.Set<StationClearanceRow>().AsNoTracking()
            .ToArrayAsync(cancellationToken);

        JourneyRuntimeRow[] journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.JourneyId.StartsWith(ChargingIdentity.JourneyIdPrefix) && row.Stage != JourneyRuntimeStage.Completed)
            .ToArrayAsync(cancellationToken);

        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset.
        return new ChargingDashboardFacts
        {
            Contact = contact,
            Roster = current,
            RosterEntries = [.. allEntries.Where(row => row.Version == current?.Version)
                .OrderBy(row => row.MapId).ThenBy(row => row.StationId)],
            RosterScopes = scopes,
            RosterHistory = history,
            StationNames = allEntries.ToDictionary(row => (row.Version, row.MapId, row.StationId), row => row.StationName),
            Exclusivities = exclusivities,
            OpenCycles = [.. cycles.Where(row => row.Phase != ChargingCyclePhases.Ended)],
            CyclesOfHeldChargers = cycles.Where(row => heldJourneys.Contains(row.JourneyId))
                .ToDictionary(row => row.JourneyId, StringComparer.Ordinal),
            OpenStationHolds = [.. stationHolds.OrderBy(row => row.HeldAt)],
            OpenEligibilityHolds = [.. eligibilityHolds.OrderBy(row => row.HeldAt)],
            ManualHolds = [.. manualHolds.OrderBy(row => row.Since)],
            OpenClearances = clearances.Where(row => row.CompletedAt is null)
                .ToDictionary(row => row.CycleId, StringComparer.Ordinal),
            RecentClearances = [.. clearances.Where(row => row.CompletedAt is not null)
                .OrderByDescending(row => row.CompletedAt)
                .Take(RecentClearanceCount)],
            OpenChargingJourneys = journeys.ToDictionary(row => row.JourneyId, StringComparer.Ordinal),
            Claims = await VehiclePurposeFacts.ReadAsync(dbContext, cancellationToken),
        };
    }

    /// <summary>一个桩的阶段与它的起点：只读独占行、周期行，不重算放桩的三项确认（那三项每轮从 RIoT 现读，看板读不到）。</summary>
    public static (string Stage, DateTimeOffset? Since) Stage(StationExclusivityRow? holding, ChargingCycleRow? cycle)
    {
        if (holding is null)
        {
            return (ChargingDashboardDescriptions.StageFree, null);
        }

        return cycle switch
        {
            { Phase: ChargingCyclePhases.Clearing } => (ChargingDashboardDescriptions.StageClearing, holding.StateSince),
            { Phase: ChargingCyclePhases.Active, WireState: ChargingCycleWireStates.Complete, CompletedAt: { } completedAt } =>
                (ChargingDashboardDescriptions.StageCompleteAwaitingDeparture, completedAt),
            { Phase: ChargingCyclePhases.Active } => holding.State == StationExclusivityStates.Occupied
                ? (ChargingDashboardDescriptions.StageOccupied, holding.StateSince)
                : (ChargingDashboardDescriptions.StageReserved, holding.StateSince),
            { Phase: ChargingCyclePhases.Ended, EndReason: { } reason, EndedAt: { } endedAt }
                when ChargingExecutionReasons.ConfirmedFailures.Contains(reason) =>
                (ChargingDashboardDescriptions.StageFailedCycleAwaitingRelease, endedAt),
            _ => (ChargingDashboardDescriptions.StageHeldWithoutOpenCycle, holding.StateSince),
        };
    }

    /// <summary>
    /// 充满待离桩、失败待放桩两种阶段超过告警窗口（<c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c>，从充满或失败结束的时刻算）多久；
    /// 没超过或不是这两种答空。与引擎告警（事件 2249、2247）同一个窗口、同一个起点；这是拿库里的时刻与配置比，不是重算放桩条件。
    /// </summary>
    public static TimeSpan? OverdueBy(string stage, DateTimeOffset? since, DateTimeOffset now, TimeSpan window) =>
        since is { } start &&
        stage is ChargingDashboardDescriptions.StageCompleteAwaitingDeparture or ChargingDashboardDescriptions.StageFailedCycleAwaitingRelease &&
        now - start > window
            ? now - start
            : null;

    /// <summary>超过窗口时引擎写的那条日志的事件号。</summary>
    public static string OverdueEventId(string stage) =>
        stage == ChargingDashboardDescriptions.StageCompleteAwaitingDeparture ? "2249" : "2247";

    /// <summary>车号：名册里有就用名册的，没有（车已不在投运名册里）就照写 <c>VehicleKey</c>。</summary>
    public string AgvIdOf(string vehicleKey) => Contact.ByVehicleKey(vehicleKey)?.AgvId ?? vehicleKey;

    /// <summary>这辆车听不听得到；不在投运名册里的车按听不到算。</summary>
    public bool InContact(string vehicleKey) => Contact.ByVehicleKey(vehicleKey) is { } vehicle && Contact.InContact(vehicle);

    public string? StationNameOf(long rosterVersion, int mapId, int stationId) =>
        StationNames.GetValueOrDefault((rosterVersion, mapId, stationId));

    /// <summary>一次清桩的全部记录字段（REQ-0179）。</summary>
    public object Clearance(StationClearanceRow row) => new
    {
        clearanceId = row.ClearanceId,
        cycleId = row.CycleId,
        agvId = AgvIdOf(row.VehicleKey),
        vehicleKey = row.VehicleKey,
        mapId = row.MapId,
        stationId = row.StationId,
        startedAt = row.StartedAt,
        // ConfirmedAt 是人工确认被记下的时刻；CompletedAt 是清桩完成（REQ-0178：旧单也已终结、桩放掉的那一轮）。两者不同。
        confirmedAt = row.ConfirmedAt,
        confirmedBy = row.ConfirmedBy,
        confirmedByRole = row.ConfirmedByRole,
        completedAt = row.CompletedAt,
        proof = row.Proof,
        proofDescription = row.Proof is null ? null : ChargingDashboardDescriptions.ClearanceProofs.GetValueOrDefault(row.Proof),
        waitingPointMapId = row.WaitingPointMapId,
        waitingPointStationId = row.WaitingPointStationId,
        vehicleFinalPosition = row.VehicleFinalPosition,
        oldOrderDisposition = row.OldOrderDisposition,
        assistantsJson = row.AssistantsJson,
        clearedCondition = row.ClearedCondition,
        confirmationRequestId = row.ConfirmationRequestId,
    };

    /// <summary>
    /// 一次桩分配暂停：卡片上显示得下的几项（REQ-0177），全部字段在 <c>full</c> 里（数据面 JSON 就是全文）。
    /// </summary>
    public object StationHold(ChargingStationAllocationHoldRow row) => new
    {
        holdId = row.HoldId,
        trigger = row.Trigger,
        triggerDescription = ChargingDashboardDescriptions.HoldTriggers.GetValueOrDefault(row.Trigger),
        rootCause = row.RootCause,
        heldAt = row.HeldAt,
        agvId = row.VehicleKey is null ? null : AgvIdOf(row.VehicleKey),
        cycleId = row.CycleId,
        confirmedByPersonId = row.ConfirmedByPersonId,
        confirmedByRole = row.ConfirmedByRole,
        siteDisposition = row.SiteDisposition,
        full = new
        {
            holdId = row.HoldId,
            idempotencyKey = row.IdempotencyKey,
            trigger = row.Trigger,
            rootCause = row.RootCause,
            mapId = row.MapId,
            stationId = row.StationId,
            chargerRosterVersion = row.ChargerRosterVersion,
            vehicleKey = row.VehicleKey,
            reservationRecordId = row.ReservationRecordId,
            cycleId = row.CycleId,
            upperId = row.UpperId,
            orderId = row.OrderId,
            arrivedAt = row.ArrivedAt,
            chargingStartedAt = row.ChargingStartedAt,
            failedAt = row.FailedAt,
            finalHangAt = row.FinalHangAt,
            confirmedAt = row.ConfirmedAt,
            heldAt = row.HeldAt,
            rawPositionJson = row.RawPositionJson,
            rawOrderJson = row.RawOrderJson,
            rawActionResultJson = row.RawActionResultJson,
            rawBatteryJson = row.RawBatteryJson,
            evidenceReference = row.EvidenceReference,
            riotBuild = row.RiotBuild,
            riotContractVersion = row.RiotContractVersion,
            confirmedByPersonId = row.ConfirmedByPersonId,
            confirmedByRole = row.ConfirmedByRole,
            confirmedAuthenticatedAt = row.ConfirmedAuthenticatedAt,
            siteDisposition = row.SiteDisposition,
        },
    };
}
