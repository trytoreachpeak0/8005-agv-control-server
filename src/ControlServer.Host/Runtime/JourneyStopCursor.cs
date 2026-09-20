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
/// <b>当前停靠是落库的状态，不再由阶段推出</b>（批次7-06，control-server#211）。批次7-03 用「阶段属于哪个角色的停靠」
/// 定位当前停靠，那只在「一趟恰好一个取货、一个卸货」时成立；途中追加会在序列中间插进新停靠，同一个阶段于是会在不同
/// 停靠上重复出现，阶段就不够用了。现在当前停靠是<b>序位最小的、还没完成的那一个</b>
/// （<see cref="JourneyStopRow.Status"/>），阶段只说「在这个停靠上走到哪一步」。
/// </para>
/// <para>
/// <b>「这个停靠此刻在装哪一条需求」也是落库的状态</b>：从属需求行的 <see cref="JourneyDemandStatuses.Loading"/>。
/// 装货那一条需要状态，是因为装哪一条由操作员扫了什么决定，服务端事后推不出来；卸货那一条不需要，因为卸货是服务端
/// 自己按顺序发的，「此刻在卸的」就是本停靠上第一条还没卸的已装需求（<see cref="NextToUnloadAtCurrentStop"/>）。
/// 这个不对称是有理由的，不是漏了一半。
/// </para>
/// <para>
/// 读出来的行都是 <c>AsNoTracking</c> 的。要写停靠状态或需求状态，用 <see cref="ControlServerDbContext"/> 上被跟踪的
/// 那一份（<see cref="JourneyRuntimeEngine"/> 里的 <c>TrackedStopAsync</c> 与 <c>TrackedMembershipAsync</c>）。
/// </para>
/// </remarks>
internal sealed class JourneyStopCursor
{
    private readonly JourneyRuntimeRow _runtime;

    private JourneyStopCursor(
        JourneyRuntimeRow runtime,
        IReadOnlyList<JourneyStopRow> stops,
        IReadOnlyList<JourneyStopDemand> allDemands)
    {
        _runtime = runtime;
        Stops = stops;
        AllDemands = allDemands;
        Demands = [.. allDemands.Where(item =>
            item.Demand.Status is not (DemandExecutionStatus.Succeeded or DemandExecutionStatus.Cancelled))];
    }

    /// <summary>旅程的全部停靠，按 <see cref="JourneyStopRow.Sequence"/>。</summary>
    public IReadOnlyList<JourneyStopRow> Stops { get; }

    /// <summary>
    /// 未移除的全部归属，连同各自的需求行，<b>含已终结的</b>。重放白名单用它：白名单是「这趟旅程有权发的消息」，
    /// 一条需求终结不会把它的装卸命令变成别人的消息。用 <see cref="Demands"/> 去筛，终结的那一刻白名单就会缩水，
    /// 而缩水之后那条命令的补发就断了。
    /// </summary>
    public IReadOnlyList<JourneyStopDemand> AllDemands { get; }

    /// <summary>
    /// 旅程此刻还带着的需求：归属未被移除，且需求本身还没终结（<see cref="DemandJourneyLookup.OpenDemands"/> 的那个定义）。
    /// 清单项与录入请求的期待子批取它。
    /// </summary>
    public IReadOnlyList<JourneyStopDemand> Demands { get; }

    /// <summary>
    /// 此刻推进的那个停靠：序位最小的、还没完成也没被移除的那一个。一趟旅程的每个停靠都完成之后就没有当前停靠了，
    /// 取它会抛——那一刻旅程已经 <see cref="JourneyRuntimeStage.Completed"/>，<c>AdvanceAsync</c> 在那之前就返回了。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是本轮加载那一刻的答案，而推进段在一轮里会把停靠推完。</b><c>AdvanceAsync</c> 用 <c>goto case</c> 把几个
    /// 阶段串在一轮里推，其中一条（装货落定后回到等录入）现在还会跨停靠。所以完成一个停靠之后要重新加载游标，
    /// 而不是接着用手上这一份——接着用会拿上一个停靠的 id 发报文，测试照绿，只有车上收到的东西不对。
    /// </para>
    /// <para>
    /// 角色不再参与定位，但它仍是一道自检：<see cref="RoleOf"/> 把阶段映到角色，当前停靠的角色对不上就是推进段把
    /// 旅程推到了一个与它所在停靠不相称的阶段。
    /// </para>
    /// </remarks>
    public JourneyStopRow Current => Stops.FirstOrDefault(IsOpen)
        ?? throw new InvalidDataException($"Journey '{_runtime.JourneyId}' has no open stop left.");

    /// <summary>还没完成也没被移除的停靠，按序位。</summary>
    public IReadOnlyList<JourneyStopRow> OpenStops => [.. Stops.Where(IsOpen)];

    /// <summary>当前停靠上挂着的、还没终结的需求，按加入旅程的先后。</summary>
    public IReadOnlyList<JourneyStopDemand> CurrentStopDemands => AtStop(Current);

    /// <summary>
    /// 当前停靠上<b>这个停靠的作业还没做完</b>的需求，按加入旅程的先后：取货停靠是还没装完的，卸货停靠是装了还没卸的。
    /// </summary>
    /// <remarks>
    /// 清单项与录入请求的期待子批取它，不取 <see cref="CurrentStopDemands"/>。批次7-03 取的是「还没终结的」，因为那时
    /// 装货闭环还没落到从属需求行上，「未装」与「未终结」分不开；一个停靠一条需求时两者恒等。现在分得开了，而分不开
    /// 的那一版会让操作员在清单上看见一条刚装完的需求，再扫一次。
    /// </remarks>
    public IReadOnlyList<JourneyStopDemand> OutstandingAtCurrentStop =>
        [.. ProgressAtStop(Current).Where(IsOutstandingAt(Current))];

    /// <summary>
    /// 当前停靠此刻正在装的那一条：录入已受理、装货命令已发、结果没到。没有正在装的就是空。
    /// </summary>
    /// <remarks>
    /// 至多一条——同一站多条需求逐条串行（规格第 22 节补记）。多于一条是这台服务器自己的不变量被破坏了，所以抛而不是
    /// 挑一条：两条同时在装意味着两条开仓命令同时在飞，而操作员面前只有一排仓门。
    /// </remarks>
    public JourneyStopDemand? LoadingAtCurrentStop => ProgressAtStop(Current)
        .SingleOrDefault(item => item.Membership.Status == JourneyDemandStatuses.Loading);

    /// <summary>
    /// 当前停靠此刻该卸的那一条：本停靠上第一条装了还没卸的。卸货逐条串行，顺序由归属先后定，所以不需要一个
    /// 「正在卸」的状态——见类注释里那段不对称的理由。
    /// </summary>
    public JourneyStopDemand? NextToUnloadAtCurrentStop => ProgressAtStop(Current)
        .FirstOrDefault(item => item.Membership.Status == JourneyDemandStatuses.Loaded);

    /// <summary>这个停靠上挂着的全部归属（含已终结的），按加入旅程的先后。清单的版数按它数。</summary>
    public IReadOnlyList<JourneyStopDemand> AllAtStop(JourneyStopRow stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        return [.. AllDemands.Where(demand => StopIdOf(demand.Membership, stop.StopRole) == stop.StopId)];
    }

    /// <summary>一条需求在某个停靠上的作业做完了没有：取货停靠看装，卸货停靠看卸。</summary>
    /// <remarks>
    /// <para>
    /// <b>已取消的需求在哪个停靠上都没有作业可做</b>，不管它的归属行写成什么。这一条不是为了对称，是为了兜住
    /// 「需求终结了、归属行没跟着写」的库：批次 7 的迁移回填在途旅程时按受理时的形状写归属，而一趟在途旅程的某条需求
    /// 可能早就取消了。没有这一条，升级之后那趟旅程会停在一个「还有东西要卸」的停靠上等一条永远不会来的结果。
    /// </para>
    /// <para>
    /// <b><see cref="DemandExecutionStatus.Succeeded"/> 故意不在里面。</b>卸货结果落定的那一刻，需求已经是
    /// <c>Succeeded</c> 而归属行还是 <c>LOADED</c>——正是要拿它去结算那条卸货命令的时刻。把它也算成「做完了」，
    /// 推进段会在那一轮找不到该结算的那一条。两者的不对称是这个时间差本身，不是漏了一半。
    /// </para>
    /// </remarks>
    public static bool IsDoneAt(JourneyStopRow stop, JourneyStopDemand item)
    {
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(item);
        if (item.Demand.Status == DemandExecutionStatus.Cancelled ||
            item.Membership.Status == JourneyDemandStatuses.Terminated)
        {
            return true;
        }
        return stop.StopRole == JourneyStopRoles.Pickup
            ? item.Membership.Status is JourneyDemandStatuses.Loaded or JourneyDemandStatuses.Unloaded
            : item.Membership.Status == JourneyDemandStatuses.Unloaded;
    }

    private static bool IsOpen(JourneyStopRow stop) =>
        stop.Status is not (JourneyStopStatuses.Completed or JourneyStopStatuses.Removed);

    private static Func<JourneyStopDemand, bool> IsOutstandingAt(JourneyStopRow stop) =>
        item => !IsDoneAt(stop, item);

    public IReadOnlyList<JourneyStopDemand> AtStop(JourneyStopRow stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        return [.. Demands.Where(demand => StopIdOf(demand.Membership, stop.StopRole) == stop.StopId)];
    }

    /// <summary>
    /// 这个停靠上还没做完本停靠作业的归属，按加入旅程的先后——<b>按归属行的状态判，不按需求的执行状态</b>。
    /// </summary>
    /// <remarks>
    /// 两者是两件事，而它们在一个时刻必然分岔：卸货结果刚落定的那一轮，需求已经是
    /// <see cref="DemandExecutionStatus.Succeeded"/>，而归属行还停在 <c>LOADED</c>——正是要拿它去结算那条卸货命令的
    /// 时刻。按需求状态筛会在这里筛空，于是推进段对着一个「没有东西可卸」的停靠抛。所以本停靠的进度只由
    /// <see cref="JourneyDemandRow.Status"/> 回答，需求终结时由 <see cref="PickupStopTermination"/> 把它写成
    /// <see cref="JourneyDemandStatuses.Terminated"/>。
    /// </remarks>
    private IReadOnlyList<JourneyStopDemand> ProgressAtStop(JourneyStopRow stop) =>
        [.. AllDemands.Where(demand => StopIdOf(demand.Membership, stop.StopRole) == stop.StopId)];

    /// <summary>
    /// 锚需求在这趟旅程里的归属行。装卸命令与离站核验的 <c>demandId</c> 都取它。
    /// </summary>
    /// <remarks>
    /// 从<b>未移除的全部</b>归属里找，不是从 <see cref="Demands"/>——一条需求终结之后它的归属行仍然是它那几个 id 的载体，
    /// 而推进段可能正要拿它去结算刚发出去的那条命令。用未终结的那一份去找，终结的那一刻这里就开始抛。
    /// 归属被移除（批次7-10 的改派）才是真的找不到了。
    /// </remarks>
    public JourneyStopDemand Anchor(string demandId) =>
        AllDemands.SingleOrDefault(demand => demand.Membership.DemandId == demandId)
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

        // 归属连着需求行一起取：受理事务同一次保存写下归属与需求，所以这个内连接丢不了行。按加入旅程的先后排，
        // 同一刻加入的再按需求 id 定序；排序在客户端做，因为 SQLite 不接受 DateTimeOffset 的 ORDER BY，而一趟旅程的
        // 归属至多几条，取回来再排没有代价。
        JourneyStopDemand[] demands = await dbContext.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId && row.RemovedAt == null)
            .Join(
                dbContext.AcceptedDemands.AsNoTracking(),
                membership => membership.DemandId,
                demand => demand.DemandId,
                (membership, demand) => new JourneyStopDemand(membership, demand))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new JourneyStopCursor(
            runtime,
            stops,
            [.. demands
                .OrderBy(row => row.Membership.AddedAt)
                .ThenBy(row => row.Membership.DemandId, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// 哪个阶段属于哪个角色的停靠。七个阶段里，前五个在取货停靠上（到站、等录入、等装货结果、等离站、等离站核验），
    /// 后两个在卸货停靠上（到站、等卸货结果）。批次7-06 起它只用于自检，不再用于定位。
    /// </summary>
    public static string RoleOf(JourneyRuntimeStage stage) => stage switch
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
