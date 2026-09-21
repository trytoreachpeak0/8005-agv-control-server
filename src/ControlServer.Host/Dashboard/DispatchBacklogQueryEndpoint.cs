using ControlServer.Application;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
        };

    private readonly TimeProvider _clock;

    public DispatchBacklogQueryEndpoint()
        : this(TimeProvider.System)
    {
    }

    /// <summary>桩（批次7-12 测试先行）：只够测试注入时钟编译。</summary>
    internal DispatchBacklogQueryEndpoint(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "dispatch-backlog";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        _ = _clock;
        JourneyBacklogRow[] pending = await dbContext.JourneyBacklog.AsNoTracking()
            .Where(row => row.AcceptedAt == null && row.ReasonCode != DispatchReasonCodes.DemandLeftCatalog)
            .ToArrayAsync(cancellationToken);
        IReadOnlyList<StructuralDispatchBlock> blocks =
            await new StructuralDispatchBlockStore(dbContext).ListUnclearedAsync(cancellationToken);

        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset.
        return new
        {
            backlog = pending
                .Where(row => !DispatchReasonCodes.IsSilent(row.ReasonCode))
                .OrderBy(row => row.FirstSeenAt)
                .ThenBy(row => row.DemandId, StringComparer.Ordinal)
                .Select(row => new
                {
                    demandId = row.DemandId,
                    transportDemandKey = row.TransportDemandKey,
                    reasonCode = row.ReasonCode,
                    reasonDescription = Describe(row.ReasonCode),
                    firstSeenAt = row.FirstSeenAt,
                    lastEvaluatedAt = row.LastSeenAt,
                    waitingSeconds = (long)Math.Max(0, (now - row.FirstSeenAt).TotalSeconds)
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
