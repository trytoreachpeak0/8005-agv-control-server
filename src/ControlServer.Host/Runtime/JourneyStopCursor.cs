using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 一趟旅程此刻停在哪一个停靠，那个停靠带着哪些 id，旅程又还带着哪些需求（批次7-03，control-server#208）。
/// </summary>
/// <remarks>
/// <para>
/// 在这之前推进段是一台两段写死的状态机：每一个消息 id 都是「需求 id ＋ 用途」算出来的，取货那段读
/// <c>runtime.WorklistMessageId</c>、关卡那段读 <c>runtime.GateWorklistMessageId</c>，而「关卡」这两个字直接写在方法名里。
/// 多一个停靠这套算术就不成立。改成从这里取之后，发布方法只问「当前停靠的清单消息 id 是什么」，停靠是第几个、是取货还是
/// 卸货，都由 <c>JourneyStops</c> 的行回答。
/// </para>
/// <para>
/// <b>当前停靠由阶段推出，没有新的一列。</b>今天七个阶段各属于哪个停靠是固定的（见 <see cref="RoleOf"/>），
/// 而单需求旅程恰好两个停靠——包括本票要支持的「一个停靠上挂两条需求」，那也还是这两个停靠。停靠指针要自己落库，
/// 是多停靠才有的需求（批次7-06，control-server#211：途中追加会在序列中间插进新停靠，那时同一个阶段会在不同停靠上重复出现，
/// 阶段就不够用了）。本票不提前落它，换来的是 <c>ZeroChangePin</c> 那批基线一列都不用动——那是「行为不变」最硬的证据，
/// 不值得为一个此刻还推不出第二种答案的指针作废掉。<c>JourneyStopRow.Status</c> 因此仍停在受理时写下的
/// <see cref="JourneyStopStatuses.Pending"/>，由 批次7-06 接管。
/// </para>
/// <para>
/// 读出来的行都是 <c>AsNoTracking</c> 的：本票只读停靠与归属，不写。
/// </para>
/// </remarks>
internal sealed class JourneyStopCursor
{
    private readonly JourneyRuntimeStage _stage;

    private JourneyStopCursor(
        JourneyRuntimeStage stage,
        IReadOnlyList<JourneyStopRow> stops,
        IReadOnlyList<JourneyDemandRow> memberships,
        IReadOnlyList<JourneyStopDemand> demands)
    {
        _stage = stage;
        Stops = stops;
        Memberships = memberships;
        Demands = demands;
    }

    /// <summary>旅程的全部停靠，按 <see cref="JourneyStopRow.Sequence"/>。</summary>
    public IReadOnlyList<JourneyStopRow> Stops { get; }

    /// <summary>
    /// 旅程未被移除的全部归属，<b>含已终结的需求</b>。重放白名单用它：白名单是「这趟旅程有权发的消息」，
    /// 一条需求终结不会把它的装卸命令变成别人的消息。用 <see cref="Demands"/> 去筛，终结的那一刻白名单就会缩水，
    /// 而缩水之后那条命令的补发就断了。
    /// </summary>
    public IReadOnlyList<JourneyDemandRow> Memberships { get; }

    /// <summary>
    /// 旅程此刻还带着的需求：归属未被移除，且需求本身还没终结（<see cref="DemandJourneyLookup.OpenDemands"/> 的那个定义）。
    /// </summary>
    public IReadOnlyList<JourneyStopDemand> Demands { get; }

    /// <summary>
    /// 此刻推进的那个停靠。<see cref="JourneyRuntimeStage.Blocked"/> 与 <see cref="JourneyRuntimeStage.Completed"/> 没有
    /// 「当前停靠」可言，取它会抛——这两个阶段在 <c>AdvanceAsync</c> 里直接返回，只有重放会走到 <see cref="Stops"/>。
    /// </summary>
    public JourneyStopRow Current => Stops.FirstOrDefault(stop => stop.StopRole == RoleOf(_stage))
        ?? throw new InvalidDataException($"Journey stage '{_stage}' has no current stop.");

    /// <summary>当前停靠上挂着的、还没终结的需求，按加入旅程的先后。</summary>
    public IReadOnlyList<JourneyStopDemand> CurrentStopDemands => AtStop(Current);

    public IReadOnlyList<JourneyStopDemand> AtStop(JourneyStopRow stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        return [.. Demands.Where(demand => StopIdOf(demand.Membership, stop.StopRole) == stop.StopId)];
    }

    /// <summary>锚需求在这趟旅程里的归属行。装卸命令、离站核验的 <c>demandId</c> 都取它。</summary>
    public JourneyStopDemand Anchor(string demandId) =>
        Demands.SingleOrDefault(demand => demand.Membership.DemandId == demandId)
        ?? throw new InvalidDataException($"Demand '{demandId}' is not carried by this journey any more.");

    public static async Task<JourneyStopCursor> LoadAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(runtime);
        JourneyStopRow[] stops = await dbContext.Set<JourneyStopRow>().AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId)
            .OrderBy(row => row.Sequence)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (stops.Length == 0)
        {
            // 受理事务在写旅程行的同一次保存里写两个停靠（WireToGateStore.AcceptAsync），迁移也把在途旅程回填了
            // （Batch7MultiDemandJourneyPersistence），所以一趟没有停靠的旅程是库坏了，不是一种要兜的情况。
            throw new InvalidDataException($"Journey '{runtime.JourneyId}' has no stops.");
        }

        // 按加入旅程的先后排，同一刻加入的再按需求 id 定序。排序在客户端做：SQLite 不接受 DateTimeOffset 的 ORDER BY，
        // 而一趟旅程的归属至多几条，取回来再排没有代价。
        JourneyDemandRow[] memberships = await dbContext.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId && row.RemovedAt == null)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        JourneyStopDemand[] demands = await dbContext.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId && row.RemovedAt == null)
            .Join(
                DemandJourneyLookup.OpenDemands(dbContext).AsNoTracking(),
                membership => membership.DemandId,
                demand => demand.DemandId,
                (membership, demand) => new JourneyStopDemand(membership, demand))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new JourneyStopCursor(
            runtime.Stage,
            stops,
            [.. memberships.OrderBy(row => row.AddedAt).ThenBy(row => row.DemandId, StringComparer.Ordinal)],
            [.. demands
                .OrderBy(row => row.Membership.AddedAt)
                .ThenBy(row => row.Membership.DemandId, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// 哪个阶段属于哪个停靠。今天的七个阶段里，前五个在取货停靠上（到站、等录入、等装货结果、等离站、等离站核验），
    /// 后两个在卸货停靠上（到站、等卸货结果）。
    /// </summary>
    private static string RoleOf(JourneyRuntimeStage stage) => stage switch
    {
        JourneyRuntimeStage.AwaitingPickupArrival or
        JourneyRuntimeStage.AwaitingSublot or
        JourneyRuntimeStage.AwaitingLoadResult or
        JourneyRuntimeStage.AwaitingStationDeparture or
        JourneyRuntimeStage.AwaitingDepartureSafety => JourneyStopRoles.Pickup,
        JourneyRuntimeStage.AwaitingGateArrival or
        JourneyRuntimeStage.AwaitingUnloadResult => JourneyStopRoles.Unload,
        _ => string.Empty
    };

    private static string StopIdOf(JourneyDemandRow membership, string stopRole) => stopRole == JourneyStopRoles.Pickup
        ? membership.PickupStopId
        : membership.UnloadStopId;
}

/// <summary>一条需求在这趟旅程里的归属，连同需求本身。</summary>
internal sealed record JourneyStopDemand(JourneyDemandRow Membership, AcceptedDemandRow Demand);
