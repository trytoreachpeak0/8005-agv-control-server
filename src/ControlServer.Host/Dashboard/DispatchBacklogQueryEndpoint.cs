using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Release;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 派车积压的数据面：受理前的正常积压（REQ-0210「展示等待状态」）与未清除的结构性派车阻断，两组分开给。
/// </summary>
/// <remarks>
/// <para>
/// 两组不混成一张列表，因为对现场是两种处置：正常积压等着就行，结构性阻断永远没有车能接，要人去处理。
/// </para>
/// <para>
/// 原因码与「静默」判定都取 <see cref="DispatchReasonCodes"/>，看板工程不另抄一份码表——它不引用服务端任何程序集，
/// 所以中文说明也在这里按常量配好，随行下发；没登记说明的码 <c>reasonDescription</c> 为 <c>null</c>，由卡片原样显示码值。
/// 静默的原因（<see cref="DispatchReasonCodes.Silent"/>，即未映射 AREA，REQ-0191）不进积压列表，只给一个计数；
/// 结构性阻断里也不列，control-server#74 本就不会为它们写行。
/// </para>
/// <para>
/// 已不在 MesIngest 目录里的未受理需求（<see cref="DispatchReasonCodes.DemandLeftCatalog"/>）没有在等车，
/// 既不进积压列表，也不计入静默计数——它不是 REQ-0191 的静默。
/// </para>
/// <para>
/// 已受理旅程上的阻断原因不在这里，那是 control-server#80 的卡片。积压行没有 AREA 列（AREA 只在需求目录的实时字段里，
/// 不落 <c>JourneyBacklog</c>），本票零 migration，所以不给。
/// </para>
/// <para>
/// 积压行的 <c>reasonCode</c> 是这一轮最后判它的那辆车写的（<c>DispatchRoundRunner.UpsertBacklog</c>，空闲车先判、在途车后判），说不出别的车为什么
/// 没接（2026-10-17 生产：78 条都显示在途车的 <c>VEHICLE_TASK_TYPE_NOT_ADMITTED</c>，把空闲 agv02 的电量原因盖住了）。所以每行另给
/// <c>vehicleVerdicts</c>：上一轮每辆跑完的车对这条需求的原因码与说明（control-server#744），取自派车轮整张替换的那块单例
/// （<see cref="SlotGroupFullnessBoard.VerdictsByDemand"/>），<c>reasonCode</c> 的写法与含义不变。
/// </para>
/// </remarks>
internal sealed class DispatchBacklogQueryEndpoint : IDashboardQueryEndpoint
{
    /// <summary>
    /// 分侧派车带来的原因码的中文说明（批次 4）。键取常量，码改名时编译期就断在这里。
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Descriptions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable] =
                "所需一侧的仓位分组暂时没有足够的空仓，有仓位空出或重新启用后即可派车",
            [DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup] =
                "花篮数超过本车该侧分组的物理仓位数，一条需求不跨分组，本车装不下",
            [DispatchReasonCodes.VehicleSlotModelUnresolved] =
                "车辆的生效仓位配置与已发布 IO 绑定都没有指明仓位模型，无法判断哪个仓位在哪一侧",
            [DispatchReasonCodes.AreaSlotGroupNotAssigned] =
                "分区归属表没有为该需求的 AREA 指派仓位分组（开门侧）",
            // 按任务类型准入（control-server#160）：都是配置造成的不投运，只挡该任务类型，不是故障。
            [DispatchReasonCodes.OutOfScopeWorkType] =
                "本服务不执行这个任务类型：部署的 AllowedWorkTypes 里没有它，或规则表不认识它",
            [DispatchReasonCodes.TaskTypeBindingMissing] =
                "本图没有为这个任务类型绑定固定站点（不在本图需求集里，或在需求集里却没绑定），只有这个任务类型不投运",
            [TaskTypeStationReasonCodes.BindingStationNotInCatalog] =
                "这个任务类型绑定的固定站点不在当前站点目录里（被删、改名或不是本图），只有这个任务类型不投运",
            // REQ-0302：新鲜度按最近一次完整确认算，是整图的状态；不新鲜时依赖站点目录的业务全部不启用，不是只停这一类。
            [TaskTypeStationReasonCodes.BindingCatalogNotFresh] =
                "本图站点目录超过允许时长没有完整确认，整图所有任务类型暂停受理（不是只停这一类），目录重新确认后恢复",
            // control-server#198：正常构建走不到，出现即服务端缺陷。
            ["FINAL_JOURNEY_PLAN_INCOMPLETE"] =
                "受理时计划带了任务类型规则与绑定版本、却没带站点目录修订，整笔受理被拒、什么也没写；这是服务端缺陷，请联系开发",
            [DispatchReasonCodes.TaskTypeHeld] =
                "本图这个任务类型处于暂停（人工暂停、站点目录变化或绑定激活结果未知），解除后才会派车",
            [DispatchReasonCodes.TaskTypeNotYetExecutable] =
                "这个任务类型已有绑定，但当前版本的服务端还不能执行它",
            // 按业务键抑制（control-server#210；故障货物交接自 control-server#395 起也写）：有意不执行，不是故障。
            [DispatchReasonCodes.TransportDemandKeySuppressed] =
                "这个子批次的这类任务已在本地取消过、或已以故障货物交接终止，按业务键永久不再执行；MES 换了新的需求号也一样",
            [DispatchReasonCodes.TransportDemandKeyAlreadyAccepted] =
                "这个子批次的这类任务已由另一个需求号受理过（在办、已完成或已取消），同一件活不再重复受理",
            // 目录时间窗（REQ-0366，control-server#827）：用户定的有意不派，不是故障；需求仍在目录里，交现场按既有方式处理。
            [DispatchReasonCodes.DemandOlderThanCatalogWindow] =
                "MES 接入首次读到这条需求已超过目录时间窗（默认 30 分钟），不再派车；需求仍在目录里，MES 字段更新也不会放回",
            [DispatchReasonCodes.DemandCatalogCreatedAtMissing] =
                "MES 接入没有给出首次读到这条需求的时刻，无法判断是否在目录时间窗内，不派车",
            // 试验机批号（REQ-0368，control-server#828）：用户定的有意不派，不是故障；需求仍在目录里，交现场按既有方式处理。
            [DispatchReasonCodes.DemandTrialSublot] =
                "子批次号表明这是试验机上生产的批次，按规定不派车；需求仍在目录里，由现场按既有方式处理",
            // 批次8-18（control-server#389）：空闲返回的承诺不被搬运抢，正常调度结论，不是故障。
            [DispatchReasonCodes.VehicleCommittedToIdleReturn] =
                "这辆车已承诺返回等待点，返回不被搬运取消或抢走；由别的车接，或等它到点后下一轮再派",
            // control-server#385（REQ-0364）：仓已确认无货而门锁没能证明锁闭，整车扣到维修放行。
            [DispatchReasonCodes.VehicleSlotDoorHold] =
                "这辆车有仓门锁闭没能证明，已扣车等维修放行（维修记录、重新读到锁闭复位为空、再过放行检查）；由别的车接，或放行后再派",
            // 批次9-06（control-server#404）：充电的承诺不被搬运抢；人工充电等待的出口只有「充电后返回服务」。都是车辆侧的正常结论。
            [DispatchReasonCodes.VehicleCommittedToCharging] =
                "这辆车已承诺去充电（占着充电用途并预占了充电桩），充电不被搬运取消或抢走；由别的车接，或等它充完电后再派",
            [DispatchReasonCodes.VehicleInManualChargingHold] =
                "这辆车在人工充电等待中（充电桩名册里没有它可用的桩，或它的充电单反复被取消），不接任何任务、也不会自己移动；"
                + "请人工给它充电，再由管理员在车上发起「充电后返回服务」。电量回升、名册重新启用都不会自动解除",
            // control-server#686（REQ-0259、REQ-0263）：生产段的逐车门禁，档位由部署按切换阶段写，不靠人开。
            [DispatchReasonCodes.SlotConfigurationNotConfirmed] =
                "这辆车的仓位配置在当前库里还没确认（IO 绑定不齐、还没逐仓核对、核对有仓没通过，或维修换过硬件后还没重新核对），不接搬运；"
                + "别的车照常。现场逐仓核对开、关、到位三个信号并录入通过后，下一轮即可派",
            // 批次9-02（control-server#400）：逐车硬阻断，看板展示归批次9-10，这里只有派车原因的一句说明。
            [DispatchReasonCodes.ChargingPolicyNotApproved] =
                "这辆车没有已批准并激活、适用范围覆盖它的充电策略版本，不承接新任务；别的车照常，导入、批准并激活一版覆盖它的策略即解除",
            // 批次9-05（control-server#403）：强制充电优先，车辆侧的正常结论。去桩由批次9-06 做，在那之前车原地不动。
            [DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine] =
                "这辆车生效的充电策略版本，强制充电线不高于服务端的救命告警线，这一版不能用，车不接任何新任务（在途的照常做完）；" +
                "用 FieldOps 激活一版强制充电线高于救命线的策略即恢复，不需要改库，也不需要重启服务",
            // control-server#744：逐车裁决一列让这几个车辆侧的码第一次直接出现在卡片上。
            [DispatchAdmissionChain.Eligible] =
                "这辆车对这条需求合格；这一轮它已接了别的单，或这条需求排在后面还没轮到，下一轮再派",
            [Runtime.Dispatch.Criteria.VehicleDynamicFactsCriterion.BatteryPolicyNotSatisfiedReason] =
                "这辆车电量不满足：预计做完这一趟后低于最低余量，或车正在充电还没充满（在 v2 充电安排之外被人充上桩的车，充到充满线才接活）",
            [Runtime.Dispatch.Criteria.VehicleTaskTypeAdmissionCriterion.TaskTypeNotAdmittedReason] =
                "这辆车的配置不接这个任务类型（车队配置里它的 allowedTaskTypes 没有这一类），由别的车接",
            [DispatchReasonCodes.MandatoryChargeRequired] =
                "这辆车电量低于它所用充电策略的强制充电线，只等去充电，不接新任务也不接途中追加；别的车照常。"
                + "它若一直没去充电，是在排队等桩：看服务端日志事件 2246（写明分不到桩的原因）——充电桩被占或被预占、"
                + "RIoT 读不到某辆车的位置或订单清单、路网引擎没开或数据不新、桩被暂停分配或在地图上改了名；"
                + "或者车本身此刻不能出发（原因码 CHARGING_DEPARTURE_NOT_PROVEN_AT_ALLOCATION，事件里写明缺哪一项）："
                + "车上某个仓位没锁好或开锁输出没复位（到车前把仓门关好锁上）、车载端安全摘要带着阻断原因（看车载端屏上的原因）、"
                + "RIoT 读不到车停稳（看 RIoT 里这辆车是否急停、在动或抱闸）。这几项恢复后下一轮就会分桩",
            // 批次 7（control-server#211～#215）：途中追加、装货阶段、释放改派。都是正常调度的结论，不是故障（规格 8.8 第 4 条）。
            [DispatchReasonCodes.SlotGroupOccupiedByOwnCargo] =
                "本车货物占侧：所需一侧的空仓已被这辆车自己已装或已预留的货占满，其余条件都满足，等别的车或本车卸货后再派",
            [DispatchReasonCodes.EnRouteAppendNotConfigured] =
                "所在分区没有批准途中追加（未配置或配成 0），这条需求不会追加到在途车上，等空车来接",
            [DispatchReasonCodes.EnRouteAppendDelayGateExceeded] =
                "追加到在途车上会让车上某条需求到终点的路程增加超过所在分区的上限，这一趟不追加",
            [DispatchReasonCodes.EnRouteAppendDelayUncomputable] =
                "路网算不出追加之后的路程增量，按规则不追加（算不出不当成零）",
            [DispatchReasonCodes.EnRouteAppendBreaksZoneContiguity] =
                "追加进去会让在途车的停靠在分区之间来回穿插，没有能保持分区连续的插入位，这一趟不追加",
            [DispatchReasonCodes.EnRouteAppendPlanLimitReached] =
                "追加进去会让在途车剩下要走的停靠超过 9 段或某一站清单超过 8 项，这一趟不追加",
            [DispatchReasonCodes.EnRouteAppendJourneyStopLimitReached] =
                "追加进去会让在途车这一趟的停靠总数（含已走过的）超过服务端配置的上限（默认 20），这一趟不追加，可以由别的车或这辆车的下一趟接",
            [DispatchReasonCodes.EnRouteAppendNoInsertionPoint] =
                "在途车正驶向的那一站之后已经没有可插入的位置，这一趟不追加",
            [DispatchReasonCodes.EnRouteAppendDemandLeftThisJourney] =
                "这条需求刚从这趟旅程上释放出去，不再追加回同一趟，可以由别的车接",
            [DispatchReasonCodes.LoadingPhaseClosed] =
                "在途车的装货阶段已经结束（持货超时、让站、装满后离开或计划装货完成），不再接新的待装需求",
            [DispatchReasonCodes.SublotTaskTypeConflict] =
                "同一份 MES 快照里这个批次同时命中了不止一种任务类型，这个批次的需求都先不派，请到 MES 核对数据",
            [DemandReleaseReasons.Released] =
                "这条需求已从原来的车上释放，正在等改派给别的车",
            // control-server#703：站点超时不再静默丢单。下面三条是人工搬运告警，挂在结构性阻断里，MES 关单后自动清除。
            // v5 起站点期限第一次到点就终结并告警、不放回（REQ-0365，control-server#751）；放回那一句只在放回上限配成非 0 时出现，
            // 而那样的配置不符合 REQ-0365、部署检查拒绝，留着是为了库里还有 v4 时期的行时看板说得出它是什么。
            [DemandJourneyLookup.ReleasedAtStationDeadlineReason] =
                "车到了取货站，但离站期限内没人录入条码，这条需求已放回待派，会重新派车（只在放回次数配置为非 0 时出现）",
            [ManualCarryAlarms.StationTimeoutRedispatchLimitReached] =
                "车到了取货站，离站期限内没人录入，系统已终结这条需求、不再派车；货还在机台上，请人工搬运或在 MES 处理，MES 关单后告警自动消失",
            [ManualCarryAlarms.StationTimeoutAnchorNotReleasable] =
                "这条需求在取货站超时没人录入，但它与同车其他需求绑在同一趟里无法单独放回，系统不再派车；货还在机台上，请人工搬运或在 MES 处理，MES 关单后告警自动消失",
            [ManualCarryAlarms.DeterminateLoadFailureNeedsManual] =
                "这条需求装货失败（期限到时仓位已锁好），部分花篮可能已在车上，系统不再派车；请到车上和机台核对货物去向并人工处理，MES 关单后告警自动消失",
            // 批次8-20（control-server#391，REQ-0204）：公共站点同时只由一台车占用或预占。都是正常调度的结论，那辆车离开后再派。
            [DispatchReasonCodes.FixedTaskStationReservedByOtherVehicle] =
                "这条需求的公共站点（如派工待送站）已被另一台车预占、正在前往，等它到站并离开后再派",
            [DispatchReasonCodes.FixedTaskStationOccupiedByOtherVehicle] =
                "这条需求的公共站点（如派工待送站）上正停着另一台车，等它离开后再派",
            [DispatchReasonCodes.FixedTaskStationApproachedByOtherVehicle] =
                "这条需求的公共站点正是另一台车的下一站、它还在等这个站空出来，等它到站并离开后再派",
        };

    private readonly TimeProvider _clock;
    private readonly SlotGroupFullnessBoard _verdicts;

    public DispatchBacklogQueryEndpoint()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// 挂在宿主上时用这一个：每车裁决读派车轮写的那块单例。必填：宿主漏注册时看板查询在启动时就构造失败，而不是静默成「每行都没有逐车裁决」。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public DispatchBacklogQueryEndpoint(SlotGroupFullnessBoard verdicts)
        : this(TimeProvider.System, verdicts)
    {
    }

    internal DispatchBacklogQueryEndpoint(TimeProvider clock, SlotGroupFullnessBoard? verdicts = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _verdicts = verdicts ?? new SlotGroupFullnessBoard();
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "dispatch-backlog";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        DateTimeOffset now = _clock.GetUtcNow();
        JourneyBacklogRow[] pending = await dbContext.JourneyBacklog.AsNoTracking()
            .Where(row => row.AcceptedAt == null && row.ReasonCode != DispatchReasonCodes.DemandLeftCatalog)
            .ToArrayAsync(cancellationToken);
        IReadOnlyList<StructuralDispatchBlock> blocks =
            await new StructuralDispatchBlockStore(dbContext).ListUnclearedAsync(cancellationToken);
        StarvationThresholds thresholds = await StarvationThresholds.ReadAsync(dbContext, cancellationToken);

        // The order is the dispatch ranking's own (DispatchCandidateOrdering.Ranker), run over the backlog rows restored as the
        // tasks the dispatch round ranked (BacklogStanding.TaskOf); it compares in memory, as SQLite cannot ORDER BY a DateTimeOffset.
        Dictionary<DispatchTask, JourneyBacklogRow> rowOf = new(ReferenceEqualityComparer.Instance);
        foreach (JourneyBacklogRow row in pending.Where(row => !DispatchReasonCodes.IsSilent(row.ReasonCode)))
        {
            rowOf.Add(BacklogStanding.TaskOf(row, now), row);
        }
        IReadOnlyList<DispatchTask> ordered = DispatchCandidateOrdering.Ranker().Order([.. rowOf.Keys]);
        (DateTimeOffset? verdictRoundAt, IReadOnlyDictionary<string, IReadOnlyList<(string AgvId, string ReasonCode)>> verdictsByDemand) =
            _verdicts.VerdictsByDemand();
        return new
        {
            vehicleVerdictsRoundAt = verdictRoundAt,
            starvationThresholdsApproved = thresholds.AnyApproved,
            starvationThresholds = thresholds.Zones
                .Select(zone => new { dispatchZone = zone.Key, thresholdSeconds = zone.Value })
                .ToArray(),
            backlog = ordered
                .Select(task => (Task: task, Row: rowOf[task]))
                .Select(item => new
                {
                    demandId = item.Row.DemandId,
                    transportDemandKey = item.Row.TransportDemandKey,
                    reasonCode = item.Row.ReasonCode,
                    reasonDescription = Describe(item.Row.ReasonCode),
                    firstSeenAt = item.Row.FirstSeenAt,
                    lastEvaluatedAt = item.Row.LastSeenAt,
                    waitingSeconds = (long)TaskStarvation.WaitingAge(item.Task.Snapshot, now).TotalSeconds,
                    demandCreatedAt = TaskStarvation.HasLocalCreation(item.Task.Snapshot) ? item.Row.DemandCreatedAt : (DateTimeOffset?)null,
                    waitingAgeKnown = TaskStarvation.HasLocalCreation(item.Task.Snapshot),
                    tier = BacklogStanding.TierOf(item.Task) switch
                    {
                        BacklogTier.StarvationTimeout => "STARVATION_TIMEOUT",
                        BacklogTier.TopBand => "TOP_BAND",
                        _ => "NORMAL_BAND"
                    },
                    starvationEscalatedAt = item.Row.StarvationEscalatedAt,
                    starvationEscalationParameterVersion = item.Row.StarvationEscalationParameterVersion,
                    vehicleVerdicts = (verdictsByDemand.TryGetValue(item.Row.DemandId, out IReadOnlyList<(string AgvId, string ReasonCode)>? verdicts)
                            ? verdicts
                            : [])
                        .Select(verdict => new
                        {
                            agvId = verdict.AgvId,
                            reasonCode = verdict.ReasonCode,
                            reasonDescription = Describe(verdict.ReasonCode)
                        })
                        .ToArray()
                })
                .ToArray(),
            silentBacklogCount = pending.Count(row => DispatchReasonCodes.IsSilent(row.ReasonCode)),
            structuralBlocks = blocks
                .Where(block => !DispatchReasonCodes.IsSilent(block.ReasonCode))
                .Select(block => new
                {
                    demandId = block.DemandId,
                    transportDemandKey = block.TransportDemandKey,
                    reasonCode = block.ReasonCode,
                    reasonDescription = Describe(block.ReasonCode),
                    firstRaisedAt = block.FirstRaisedAt,
                    lastSeenAt = block.LastSeenAt
                })
                .ToArray()
        };
    }

    private static string? Describe(string reasonCode) =>
        Descriptions.TryGetValue(reasonCode, out string? description) ? description : null;
}
