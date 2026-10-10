using ControlServer.Application;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 需求离开一趟旅程之后，计划该变成什么样：哪些停靠因为没有剩余作业而删掉，剩下的停靠按什么顺序走
/// （REQ-0197；批次7-10，control-server#215）。
/// </summary>
/// <remarks>
/// <para>
/// <b>删与换是两步，而且先删后换。</b>删掉的停靠不参与重排；重排比较的「原序」是删完之后的原序，
/// 因为延迟保护要问的是「换了之后比不换差多少」，不是「比删之前差多少」——删本身只会让路变短。
/// </para>
/// <para>
/// <b>一个停靠有没有剩余作业，只由挂在它上面的需求回答</b>：取货停靠上还有没装的、没终结的、没在离开的需求；
/// 卸货停靠上还有已经装了没卸的，或者还没装、但会被装上的需求。<b>已经装车的需求永远有剩余作业</b>，它的目的站
/// 因此不会被删——这就是 REQ-0197「已装车的不丢，目的站保留到卸货完成」在这里的实现方式，而不是另一条规则。
/// 一条需求只有一个状态，已装车就不是 <see cref="PlanRevisionDemandState.Leaving"/>——把已装车的需求标成离开，
/// 是构造输入的调用方要拒绝的事（REQ-0328 只释放未取货的需求）。
/// </para>
/// <para>
/// <b>当前下一站不删不换</b>（REQ-0197）。唯一的例外由调用方显式放行
/// （<see cref="PlanRevisionInput.CurrentStopMayGo"/>）：REQ-0328 的释放在确认车开往那一站的订单已经取消之后才发生，
/// 那时车已经不再驶向它。放行之后如果它没有剩余作业就删掉，下一个停靠成为新的当前下一站，同样不参与重排。
/// </para>
/// <para>
/// <b>重排的目标是全程路径代价最小，平局保持原序</b>；约束是三条，任一条不满足的排列直接放弃：
/// 每条需求先取后卸；分区连续（REQ-0195，A→A→B→B）；延迟保护（REQ-0197、REQ-0198）——每条还要走的需求，
/// 从当前下一站到它卸货站的路径代价，增量不超过它卸货站所在分区的上限。上限没配置或配成 0 时，那条需求不接受任何增量，
/// 与途中追加的延迟门禁同一口径（<see cref="EnRouteAppendPlanner"/>）。<b>任何一段代价算不出，就不换</b>：
/// 算不出的增量不是零。
/// </para>
/// <para>
/// 代价从当前下一站起算，不从车的位置起算：当前下一站是固定的，车到它那一段对每一种排列都相同，算进去只会让
/// 「车的位置读不到」变成一个不换的理由。
/// </para>
/// </remarks>
public static class JourneyPlanRevision
{
    /// <summary>
    /// 参与重排的停靠数上限。协议最多九条腿（<see cref="EnRouteAppendPlanner.MaximumLegs"/>），当前下一站固定，
    /// 所以真实计划里能换序的至多八个，八的全排列是四万多种，每一轮都算得完。超过这个数说明计划已经不合协议，
    /// 这里不去穷举，保持原序。
    /// </summary>
    public const int MaximumReorderableStops = 8;

    /// <summary>按剩余作业删停靠，再在当前下一站之后重排。</summary>
    /// <param name="input">这趟旅程此刻的全部停靠与全部未移除的需求。</param>
    /// <param name="zoneParameters">每区参数的当前版本；为空即一个分区都没配置，任何需求都不接受增量。</param>
    /// <param name="cost">两站之间的计划路径代价（毫米），算不出返回 null。</param>
    public static PlanRevisionResult Revise(
        PlanRevisionInput input,
        DispatchZoneParameterTableVersion? zoneParameters,
        Func<int, int, long?> cost)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(cost);

        List<PlanRevisionStop> ordered = [.. input.Stops.OrderBy(stop => stop.Sequence)];
        List<PlanRevisionStop> done = [.. ordered.Where(stop => stop.Done)];
        List<PlanRevisionStop> open = [.. ordered.Where(stop => !stop.Done)];

        List<string> removed = [];
        List<PlanRevisionStop> ahead = [];
        for (int index = 0; index < open.Count; index++)
        {
            PlanRevisionStop stop = open[index];
            bool isCurrent = index == 0;
            if (HasRemainingWork(stop, input.Demands) || (isCurrent && !input.CurrentStopMayGo))
            {
                ahead.Add(stop);
            }
            else
            {
                removed.Add(stop.StopId);
            }
        }

        (IReadOnlyList<PlanRevisionStop> order, string? keptBecause) = ahead.Count <= 2
            ? (ahead, null)
            : Reorder(ahead, input.Demands, zoneParameters, cost);

        // 编号（审查 M1）：当前下一站及它之前的停靠一律保留原序位；当前之后还开着的停靠从当前下一站的序位起按新顺序
        // 连续编号；这一次删掉的与之前删掉、位于当前之后的停靠接在它们后面。
        //
        // 要守的是「车停在当前下一站时，序位比它小的停靠集合不变」：它这一站的清单号、录入地址的区间、第一版消息 id
        // 都由那个集合算（JourneyStopCursor.FirstWorklistRevisionAt）。旧的编号从「已完成或已删的最大序位 + 1」数起，
        // 之前删掉、仍留着较大旧序位的停靠会把当前下一站推到它后面去，车在站上时这一站的号就跳了。
        // 删掉的停靠排到所有开放的之后，所以它们永远不会落到某个将来的当前下一站前面；序位始终是一组连续不重号的数，
        // 途中追加的规划器按下标重排前缀时因此与库里的一致。
        List<EnRouteStopSequence> sequences = [];
        if (open.Count == 0)
        {
            sequences.AddRange(done.Select(stop => new EnRouteStopSequence(stop.StopId, stop.Sequence)));
        }
        else
        {
            int currentSequence = open[0].Sequence;
            HashSet<string> removedNow = new(removed, StringComparer.Ordinal);
            sequences.AddRange(done.Where(stop => stop.Sequence < currentSequence)
                .Select(stop => new EnRouteStopSequence(stop.StopId, stop.Sequence)));
            int next = currentSequence;
            foreach (PlanRevisionStop stop in order)
            {
                sequences.Add(new EnRouteStopSequence(stop.StopId, next++));
            }

            foreach (PlanRevisionStop stop in ordered.Where(stop =>
                         removedNow.Contains(stop.StopId) || (stop.Done && stop.Sequence >= currentSequence)))
            {
                sequences.Add(new EnRouteStopSequence(stop.StopId, next++));
            }
        }

        bool reordered = !order.Select(stop => stop.StopId).SequenceEqual(ahead.Select(stop => stop.StopId), StringComparer.Ordinal);
        return new PlanRevisionResult(removed, sequences, reordered, keptBecause);
    }

    /// <summary>这个停靠上还有没有要做的事。</summary>
    private static bool HasRemainingWork(PlanRevisionStop stop, IReadOnlyList<PlanRevisionDemand> demands)
    {
        foreach (PlanRevisionDemand demand in demands)
        {
            if (demand.State is PlanRevisionDemandState.Ended or PlanRevisionDemandState.Leaving)
            {
                continue;
            }

            if (stop.Role == JourneyStopRoles.Pickup &&
                string.Equals(demand.PickupStopId, stop.StopId, StringComparison.Ordinal) &&
                demand.State != PlanRevisionDemandState.Loaded)
            {
                return true;
            }

            // 卸货停靠：已装的要卸，没装的将来也要卸——两种都是剩余作业。
            if (stop.Role == JourneyStopRoles.Unload &&
                string.Equals(demand.UnloadStopId, stop.StopId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>当前下一站固定，其后的停靠找一个更省且守约束的顺序；找不到就是原序。</summary>
    private static (IReadOnlyList<PlanRevisionStop> Order, string? KeptBecause) Reorder(
        IReadOnlyList<PlanRevisionStop> ahead,
        IReadOnlyList<PlanRevisionDemand> demands,
        DispatchZoneParameterTableVersion? zoneParameters,
        Func<int, int, long?> cost)
    {
        PlanRevisionStop current = ahead[0];
        List<PlanRevisionStop> tail = [.. ahead.Skip(1)];
        if (tail.Count > MaximumReorderableStops)
        {
            return (ahead, PlanRevisionKeptReasons.TooManyStops);
        }

        long originalTotal = PathCost(ahead, cost);
        if (originalTotal < 0)
        {
            return (ahead, PlanRevisionKeptReasons.DelayUncomputable);
        }

        Dictionary<string, long> originalPrefix = PrefixCosts(ahead, cost);
        if (originalPrefix.Values.Any(value => value < 0))
        {
            return (ahead, PlanRevisionKeptReasons.DelayUncomputable);
        }

        List<PlanRevisionDemand> active = [.. demands.Where(demand =>
            demand.State is PlanRevisionDemandState.PendingLoad or PlanRevisionDemandState.Loaded)];

        IReadOnlyList<PlanRevisionStop>? best = null;
        long bestTotal = originalTotal;
        bool anyUncomputable = false;
        foreach (IReadOnlyList<PlanRevisionStop> permutation in Permutations(tail))
        {
            List<PlanRevisionStop> candidate = [current, .. permutation];
            if (!PickupsPrecedeUnloads(candidate, active) || !ZonesAreContiguous(candidate))
            {
                continue;
            }

            long total = PathCost(candidate, cost);
            if (total < 0)
            {
                anyUncomputable = true;
                continue;
            }

            if (total >= bestTotal)
            {
                continue;
            }

            Dictionary<string, long> prefix = PrefixCosts(candidate, cost);
            if (prefix.Values.Any(value => value < 0))
            {
                anyUncomputable = true;
                continue;
            }

            if (!DelayProtected(candidate, originalPrefix, prefix, zoneParameters))
            {
                continue;
            }

            best = candidate;
            bestTotal = total;
        }

        if (best is not null)
        {
            return (best, null);
        }

        // 没有更好的排列时给一个理由，只是为了让「为什么没换」可查：「原序已经最省」不需要理由。
        return (ahead, anyUncomputable ? PlanRevisionKeptReasons.DelayUncomputable : null);
    }

    /// <summary>每条还要走的需求，先到取货站，后到卸货站；取货站已经走过的只看卸货站在不在。</summary>
    private static bool PickupsPrecedeUnloads(IReadOnlyList<PlanRevisionStop> order, IReadOnlyList<PlanRevisionDemand> active)
    {
        Dictionary<string, int> position = new(StringComparer.Ordinal);
        for (int index = 0; index < order.Count; index++)
        {
            position[order[index].StopId] = index;
        }

        foreach (PlanRevisionDemand demand in active)
        {
            if (demand.State != PlanRevisionDemandState.Loaded &&
                position.TryGetValue(demand.PickupStopId, out int pickup) &&
                position.TryGetValue(demand.UnloadStopId, out int unload) &&
                pickup > unload)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 每个卸货停靠的前缀代价都没有涨过它分区的上限；分区没配置时一点都不许涨。
    /// </summary>
    private static bool DelayProtected(
        IReadOnlyList<PlanRevisionStop> candidate,
        IReadOnlyDictionary<string, long> before,
        IReadOnlyDictionary<string, long> after,
        DispatchZoneParameterTableVersion? zoneParameters)
    {
        foreach (PlanRevisionStop stop in candidate.Where(stop => stop.Role == JourneyStopRoles.Unload))
        {
            long increase = after[stop.StopId] - before[stop.StopId];
            if (increase <= 0)
            {
                continue;
            }

            if (MaxAllowedIncrease(zoneParameters, stop.DispatchZone) is not { } allowance || increase > allowance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>与 <see cref="EnRouteAppendPlanner"/> 同一口径：未配置或配成 0 即不接受增量。</summary>
    private static long? MaxAllowedIncrease(DispatchZoneParameterTableVersion? zoneParameters, string dispatchZone)
    {
        if (zoneParameters is null ||
            !zoneParameters.Zones.TryGetValue(dispatchZone, out DispatchZoneParameters? zone) ||
            zone.EnRouteAdditionMaxPathCostIncrease is not { } allowance ||
            allowance <= 0)
        {
            return null;
        }

        return allowance;
    }

    /// <summary>从第一个停靠走完全程的代价；任一段算不出返回 -1。</summary>
    private static long PathCost(IReadOnlyList<PlanRevisionStop> stops, Func<int, int, long?> cost)
    {
        long total = 0;
        for (int index = 1; index < stops.Count; index++)
        {
            if (cost(stops[index - 1].StationRiotId, stops[index].StationRiotId) is not { } leg)
            {
                return -1;
            }

            total += leg;
        }

        return total;
    }

    /// <summary>从第一个停靠走到每个停靠为止的代价；算不出的记 -1。</summary>
    private static Dictionary<string, long> PrefixCosts(IReadOnlyList<PlanRevisionStop> stops, Func<int, int, long?> cost)
    {
        Dictionary<string, long> prefix = new(StringComparer.Ordinal) { [stops[0].StopId] = 0 };
        long total = 0;
        bool broken = false;
        for (int index = 1; index < stops.Count; index++)
        {
            if (!broken && cost(stops[index - 1].StationRiotId, stops[index].StationRiotId) is { } leg)
            {
                total += leg;
                prefix[stops[index].StopId] = total;
            }
            else
            {
                broken = true;
                prefix[stops[index].StopId] = -1;
            }
        }

        return prefix;
    }

    /// <summary>各分区在停靠序列上是否形成连续区段（REQ-0195）。</summary>
    private static bool ZonesAreContiguous(IReadOnlyList<PlanRevisionStop> stops)
    {
        HashSet<string> closed = new(StringComparer.Ordinal);
        string? current = null;
        foreach (PlanRevisionStop stop in stops)
        {
            if (string.Equals(stop.DispatchZone, current, StringComparison.Ordinal))
            {
                continue;
            }

            if (!closed.Add(stop.DispatchZone))
            {
                return false;
            }

            current = stop.DispatchZone;
        }

        return true;
    }

    /// <summary>
    /// 全排列，原序排在第一个。顺序本身不影响结果（只接受严格更省的），排第一只是让「原序」在调试时一眼可见。
    /// </summary>
    private static IEnumerable<IReadOnlyList<PlanRevisionStop>> Permutations(IReadOnlyList<PlanRevisionStop> items)
    {
        if (items.Count <= 1)
        {
            yield return items;
            yield break;
        }

        for (int first = 0; first < items.Count; first++)
        {
            List<PlanRevisionStop> rest = [.. items.Where((_, index) => index != first)];
            foreach (IReadOnlyList<PlanRevisionStop> tail in Permutations(rest))
            {
                yield return [items[first], .. tail];
            }
        }
    }
}

/// <summary>计划里的一个停靠，修订看得见的那几样。</summary>
/// <param name="Done">已经完成或早已移除：不参与删，也不参与换，序位原样保留。</param>
public sealed record PlanRevisionStop(
    string StopId,
    string Role,
    int StationRiotId,
    string DispatchZone,
    int Sequence,
    bool Done);

/// <summary>一条还挂在旅程上的需求，修订看得见的那几样。</summary>
public sealed record PlanRevisionDemand(
    string DemandId,
    string PickupStopId,
    string UnloadStopId,
    PlanRevisionDemandState State);

/// <summary>一条需求在这一次修订里的处境。</summary>
public enum PlanRevisionDemandState
{
    /// <summary>还没装。</summary>
    PendingLoad,

    /// <summary>装了还没卸。它的卸货停靠因此永远有剩余作业：已装车的需求不从计划丢弃（REQ-0197）。</summary>
    Loaded,

    /// <summary>已经终结或卸完：在它的两个停靠上都没有剩余作业。</summary>
    Ended,

    /// <summary>这一次修订要把它移出计划：尚未取货且已取消，或被释放改派（REQ-0197、REQ-0328）。</summary>
    Leaving,
}

/// <summary>一次修订的输入。</summary>
/// <param name="Stops">旅程的全部停靠。</param>
/// <param name="Demands">旅程上全部未移除的需求。</param>
/// <param name="CurrentStopMayGo">
/// 当前下一站在没有剩余作业时可以删——只有 REQ-0328 的释放放行它。<b>放行的调用方要承担一件修订这里看不见的事</b>：
/// 删掉当前下一站之后，下一个停靠接过它的序位（开放停靠从已完成的最大序位之后连续编号——协议腿序位上限是 9，
/// 留空号会把后面的停靠推出上限，所以不能留）。清单修订号按「序位更小的停靠发过几版」累加
/// （<c>JourneyStopCursor.FirstWorklistRevisionAt</c>），被删的那个停靠如果已经给车发过清单，新的当前停靠就会算出同一个号、
/// 发不同的内容，车载端以 <c>SNAPSHOT_REVISION_CONTENT_CONFLICT</c> 拆会话。所以要么只在车到站之前放行，要么像计划重发
/// 那样同时抬高清单流的基准。
/// </param>
public sealed record PlanRevisionInput(
    IReadOnlyList<PlanRevisionStop> Stops,
    IReadOnlyList<PlanRevisionDemand> Demands,
    bool CurrentStopMayGo);

/// <summary>一次修订的结果。</summary>
/// <param name="RemovedStopIds">这一次因为没有剩余作业而删掉的停靠。</param>
/// <param name="Sequences">没删的停靠的新序位：已完成的原样，其余接着已完成的最大序位连续编号。</param>
/// <param name="Reordered">当前下一站之后的顺序是否变了。</param>
/// <param name="OrderKeptBecause">本可以考虑换序、却保持原序时的理由；原序本来最省时为空。</param>
public sealed record PlanRevisionResult(
    IReadOnlyList<string> RemovedStopIds,
    IReadOnlyList<EnRouteStopSequence> Sequences,
    bool Reordered,
    string? OrderKeptBecause);

/// <summary>保持原序的理由。</summary>
public static class PlanRevisionKeptReasons
{
    /// <summary>有一段计划路径代价算不出：算不出的增量不是零，所以不换。</summary>
    public const string DelayUncomputable = "PLAN_REORDER_DELAY_UNCOMPUTABLE";

    /// <summary>可换序的停靠多于 <see cref="JourneyPlanRevision.MaximumReorderableStops"/>，不穷举。</summary>
    public const string TooManyStops = "PLAN_REORDER_TOO_MANY_STOPS";
}
