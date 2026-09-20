using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 一条需求能不能插进一辆在途车的计划里，插在哪，代价多少（票面第 3、6 条；批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// <b>四道门在一个地方决策，因为它们本来就是一道题。</b>「当前下一站不可改」限定了插入位的最早位置，「分区连续」限定了
/// 哪些位置不破坏区段，「上限」限定了插完之后的腿数与清单项数，「延迟门禁」则要先有一个插入位才算得出增量。分成四个
/// 判据各查一遍，等于把同一次插位规划算四遍，而且四个判据对「插在哪」会各有各的答案。
/// </para>
/// <para>
/// <b>拒绝的每一种有自己的原因码</b>（<see cref="DispatchReasonCodes"/>），归普通积压：参数批准、车开过那一站、
/// 别的需求卸完，都会让同一条需求下一轮通过。
/// </para>
/// <para>
/// <b>代价是计划路径代价，不是时间</b>（规格第 22 节补记，票 03 Q13）：路网只给遍历代价（毫米），全仓没有车速、
/// ETA 或站点作业时长的载体，也不打算造一个。基线文字写的是「预计到达终点时间」，这是实施口径的出入，PR 与出口报告如实写。
/// </para>
/// </remarks>
public sealed class EnRouteAppendPlanner
{
    /// <summary>协议允许的最大腿数（<c>UpcomingStopPlanSnapshot.legs</c>）。</summary>
    public const int MaximumLegs = 9;

    /// <summary>协议允许的一张清单最多几项（<c>CurrentStopWorklistSnapshot.items</c>）。</summary>
    public const int MaximumWorklistItems = 8;

    /// <summary>
    /// 把 <paramref name="candidate"/> 插进 <paramref name="plan"/>，或者说出为什么不能。
    /// </summary>
    /// <param name="plan">这辆车此刻的计划：全部未完成的停靠，按序位。</param>
    /// <param name="candidate">要追加的需求：它的取货站、卸货站与分区。</param>
    /// <param name="zoneParameters">每区参数的当前版本；为空即一个分区都没配置，全部禁止追加。</param>
    /// <param name="cost">两站之间的计划路径代价（毫米），算不出返回 null。</param>
    public static EnRouteAppendDecision Plan(
        EnRouteVehiclePlan plan,
        EnRouteAppendCandidate candidate,
        DispatchZoneParameterTableVersion? zoneParameters,
        Func<int, int, long?> cost)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(cost);

        // 本区没配置、或配成 0，就是本区禁止途中追加（REQ-0198）。参数批准之前 v2 的行为与今天完全相同，
        // 这一条是那个保证本身，所以它排在最前面：后面每一步都比它贵。
        if (MaxAllowedIncrease(zoneParameters, candidate.DispatchZone) is not { } allowance)
        {
            return EnRouteAppendDecision.Refused(DispatchReasonCodes.EnRouteAppendNotConfigured);
        }

        // 已经完成的停靠在计划里、也在编号里，但不参与任何一步计算（批次7-06，control-server#211）。
        //
        // <b>序位是整条旅程的属性，而这里要算的东西全都只关乎剩下的路。</b>两件事都成立，所以这里把计划切成
        // 「已完成的前缀」与「还要走的那一段」：下面每一步都只看后者——车已经走过的路不该算进代价，已经走过的
        // 分区也不该约束后面能不能追加——而最后给出的序位覆盖前缀加尾部的全部停靠。
        //
        // 这之前传进来的就只有未完成的停靠，于是编号从 1 重新数，与已完成的那些撞号。撞号不是内部编号的小事：
        // 发给车的计划按序位排腿，腿的状态也按它与当前停靠的先后判，所以已经装完离站的停靠会被当成还没走到、
        // 重新发给车。
        IReadOnlyList<EnRouteStop> completed = [.. plan.Stops.Take(plan.CurrentNextStopIndex)];
        EnRouteVehiclePlan ahead = plan with
        {
            Stops = [.. plan.Stops.Skip(plan.CurrentNextStopIndex)],
            CurrentNextStopIndex = 0
        };

        // 当前下一站不可改（REQ-0196）：车正驶向的那一站，或者车此刻停着的那一站，都不能被插到前面去。
        // 插入位因此从「当前下一站之后」开始数。
        int earliest = ahead.CurrentNextStopIndex + 1;
        if (earliest > ahead.Stops.Count)
        {
            return EnRouteAppendDecision.Refused(DispatchReasonCodes.EnRouteAppendNoInsertionPoint);
        }

        long originalCost = PathCost(ahead.Stops, ahead.VehicleStationRiotId, cost);
        if (originalCost < 0)
        {
            return EnRouteAppendDecision.Refused(DispatchReasonCodes.EnRouteAppendDelayUncomputable);
        }

        EnRouteAppendPlacement? best = null;
        // 每一个插入位被拒的理由都收着，最后挑一个报出去——不是碰上的第一个（批次7-06，control-server#211）。
        // 为什么不能是第一个，见 MostActionableRefusal。
        HashSet<string> refusals = new(StringComparer.Ordinal);
        // 插入位有两种：并进一个「同站同角色」的既有停靠（车本来就要去那一站，清单多一项），或者在两个停靠之间
        // 新开一个。两种都只在「当前下一站之后」，REQ-0196 因此对两种一样成立。
        //
        // 「并入还是新开」是本票开工时要定的那件事（票面第 3 条第一道门）：同站就并入，唯独当前下一站不并——
        // 并进当前下一站等于改它，那正是 REQ-0196 禁止的。车停在最后装货站等单时同站冒出的新需求，因此排在它后面
        // 成为一个新停靠：物理上车不动，逻辑上是第二次到站、第二个作业会话，「每次到站一个作业会话」的口径不变。
        for (int pickupAt = earliest; pickupAt <= ahead.Stops.Count; pickupAt++)
        {
            // 下标是「插进原序列的哪一格」，两个停靠各插一次，卸货那格先插。所以 unloadAt 与 pickupAt 相等
            // 时，卸货正好落在取货后面一位——那是「这条需求的两站紧挨着」，也往往是最省的一种插法。
            // 从 pickupAt + 1 起数会把这一族整个漏掉，于是新需求的两站之间永远夹着一个既有停靠，代价被高估。
            for (int unloadAt = pickupAt; unloadAt <= ahead.Stops.Count + 1; unloadAt++)
            {
                EnRouteStop? mergePickup = MergeTargetAt(ahead.Stops, pickupAt, candidate.PickupStop, earliest);
                EnRouteStop? mergeUnload = MergeTargetAt(ahead.Stops, unloadAt, candidate.UnloadStop, earliest);
                // 并入一个既有取货停靠时，卸货必须排在它之后才有意义：那一格本身没有被推后。
                if (mergePickup is not null && unloadAt <= pickupAt)
                {
                    continue;
                }

                List<EnRouteStop> inserted = [.. ahead.Stops];
                Dictionary<string, int> itemsByStop = ahead.WorklistItemsByStopId.ToDictionary(
                    entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
                if (mergeUnload is null)
                {
                    inserted.Insert(Math.Min(unloadAt, inserted.Count), candidate.UnloadStop);
                }
                else
                {
                    itemsByStop[mergeUnload.StopId] = itemsByStop.GetValueOrDefault(mergeUnload.StopId) + 1;
                }

                if (mergePickup is null)
                {
                    inserted.Insert(pickupAt, candidate.PickupStop);
                }
                else
                {
                    itemsByStop[mergePickup.StopId] = itemsByStop.GetValueOrDefault(mergePickup.StopId) + 1;
                }

                // 腿数按<b>整条旅程</b>数，已完成的那些也在内（批次7-06，control-server#211）。
                //
                // 这个门禁守的是协议上限 <c>UpcomingStopPlanSnapshot.legs</c> 的 <c>maxItems: 9</c>，而
                // <see cref="JourneyPlanBuilder.Plan"/> 把传给它的停靠<b>全部</b>投影成腿——调用方传的是
                // <c>stops.Stops</c>，含已完成的。只数还要走的那些，差额正好是已完成的停靠数：已完成一个、
                // 开放的到了 9 个，落库共 10 个停靠，发出去就是 10 条腿，出站 schema 门禁与车载端各拒一次。
                if (completed.Count + inserted.Count > MaximumLegs)
                {
                    refusals.Add(DispatchReasonCodes.EnRouteAppendPlanLimitReached);
                    continue;
                }

                // 分区连续（REQ-0195）：同一计划里各分区的需求形成连续区段。A→A→B→B 可以，A→B→A 不行。
                if (!ZonesAreContiguous(inserted))
                {
                    refusals.Add(DispatchReasonCodes.EnRouteAppendBreaksZoneContiguity);
                    continue;
                }

                // 一站最多 8 项：只有并入才让某个停靠的清单变长，新开的停靠恒为一项。
                if (itemsByStop.Values.Any(items => items > MaximumWorklistItems))
                {
                    refusals.Add(DispatchReasonCodes.EnRouteAppendPlanLimitReached);
                    continue;
                }

                long newCost = PathCost(inserted, ahead.VehicleStationRiotId, cost);
                if (newCost < 0)
                {
                    refusals.Add(DispatchReasonCodes.EnRouteAppendDelayUncomputable);
                    continue;
                }

                // 延迟门禁（REQ-0198）：对「任一既有需求」到达终点的计划路径代价增量都不得超过该需求所在分区的上限。
                // 既有需求包括已装车的与仅被当前计划接受的——两者都已经被这辆车承诺过，都受保护。
                string? gate = DelayGate(ahead, inserted, zoneParameters, cost);
                if (gate is not null)
                {
                    refusals.Add(gate);
                    continue;
                }

                long marginal = newCost - originalCost;
                // 新需求自己也受本区上限约束：它的增量就是整条计划的增量（计划锚，自插入位的前一站起算）。
                if (marginal > allowance)
                {
                    refusals.Add(DispatchReasonCodes.EnRouteAppendDelayGateExceeded);
                    continue;
                }

                if (best is null || marginal < best.MarginalCostMm)
                {
                    // 对外的序位一律是整条旅程的序位：已完成的前缀原样占住 1..n，插入之后的那一段接着数。
                    // 插入位下标同样加上前缀长度，它说的是「这条需求落在整条计划的第几格」。
                    best = new EnRouteAppendPlacement(
                        mergePickup?.StopId,
                        completed.Count + pickupAt,
                        mergeUnload?.StopId,
                        completed.Count + unloadAt,
                        marginal,
                        [
                            .. completed.Select((stop, index) => new EnRouteStopSequence(stop.StopId, index + 1)),
                            .. inserted.Select((stop, index) =>
                                new EnRouteStopSequence(stop.StopId, completed.Count + index + 1))
                        ]);
                }
            }
        }

        return best is not null
            ? EnRouteAppendDecision.Placed(best)
            : EnRouteAppendDecision.Refused(MostActionableRefusal(refusals));
    }

    /// <summary>
    /// 这一轮挡住全部插入位的那些理由里，报给操作员最有用的那一个。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>报第一个碰上的理由会指错方向。</b>一次追加要把几十个插入位各判一遍，每个位置可能栽在不同的门上；
    /// 循环里分区连续判在延迟门禁之前，所以「有一个位置分区不连续」几乎总是第一个出现的理由——而真正让这条
    /// 需求进不来的，可能是每个位置都超了那一区的延迟上限。现场据此去查分区编排，要改的却是一个参数。
    /// </para>
    /// <para>
    /// <b>排序的依据是「看到这个码的人能据此做什么」，从能做最多的排到最少的。</b>这是一个产品判断，不是
    /// 技术上的必然，所以依据写在这里，好让它能被反驳：
    /// </para>
    /// <list type="number">
    /// <item><c>NOT_CONFIGURED</c>：这一区根本没配上限。去配一个，下一轮就放行——最可操作的一条。
    /// （它在循环之前就返回了，不会走到这里；列在这里是为了让这张表本身是完整的。）</item>
    /// <item><c>DELAY_GATE_EXCEEDED</c>：超了那一区的上限。调大那个参数，或者等这趟旅程短一些。
    /// <b>有一个明确的旋钮</b>。</item>
    /// <item><c>DELAY_UNCOMPUTABLE</c>：路网算不出代价。去看路网快照新不新、那两站通不通——
    /// 有地方可查，只是不在这个子系统里。</item>
    /// <item><c>PLAN_LIMIT_REACHED</c>：协议的九腿八项满了。没有旋钮，等这趟旅程卸掉几条需求。</item>
    /// <item><c>BREAKS_ZONE_CONTIGUITY</c>：每个合法位置都会让某个分区断开。同样没有旋钮，而且它多半
    /// 只是排除了一部分位置——单独看到它，说明剩下的位置是被别的理由挡的。</item>
    /// <item><c>NO_INSERTION_POINT</c>：兜底，一条信息都没有。</item>
    /// </list>
    /// </remarks>
    private static string MostActionableRefusal(HashSet<string> refusals)
    {
        foreach (string code in RefusalsByHowMuchTheyTellTheOperator)
        {
            if (refusals.Contains(code))
            {
                return code;
            }
        }

        return DispatchReasonCodes.EnRouteAppendNoInsertionPoint;
    }

    /// <summary>见 <see cref="MostActionableRefusal"/>：从「能据此做最多」排到「一条信息都没有」。</summary>
    private static readonly string[] RefusalsByHowMuchTheyTellTheOperator =
    [
        DispatchReasonCodes.EnRouteAppendNotConfigured,
        DispatchReasonCodes.EnRouteAppendDelayGateExceeded,
        DispatchReasonCodes.EnRouteAppendDelayUncomputable,
        DispatchReasonCodes.EnRouteAppendPlanLimitReached,
        DispatchReasonCodes.EnRouteAppendBreaksZoneContiguity,
        DispatchReasonCodes.EnRouteAppendNoInsertionPoint,
    ];

    /// <summary>
    /// <paramref name="at"/> 这个位置上的既有停靠，如果它与 <paramref name="candidate"/> 同站同角色而且不是当前下一站，
    /// 就是可以并入的那一个；否则为空，表示在这里新开一个停靠。
    /// </summary>
    private static EnRouteStop? MergeTargetAt(
        IReadOnlyList<EnRouteStop> stops,
        int at,
        EnRouteStop candidate,
        int earliest)
    {
        if (at < earliest || at >= stops.Count)
        {
            return null;
        }

        EnRouteStop existing = stops[at];
        return existing.StationRiotId == candidate.StationRiotId &&
               string.Equals(existing.Role, candidate.Role, StringComparison.Ordinal)
            ? existing
            : null;
    }

    /// <summary>本区允许的最大路径代价增量；未配置或配成 0 即本区禁止追加，返回 null。</summary>
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

    /// <summary>
    /// 插入之后，每一条既有需求到它终点的代价增量是否都在它自己分区的上限内；越限或算不出就返回那个原因码。
    /// </summary>
    private static string? DelayGate(
        EnRouteVehiclePlan plan,
        IReadOnlyList<EnRouteStop> inserted,
        DispatchZoneParameterTableVersion? zoneParameters,
        Func<int, int, long?> cost)
    {
        foreach (EnRouteStop stop in plan.Stops.Where(stop => stop.Role == JourneyStopRoles.Unload))
        {
            long before = PrefixCost(plan.Stops, plan.VehicleStationRiotId, stop, cost);
            long after = PrefixCost(inserted, plan.VehicleStationRiotId, stop, cost);
            if (before < 0 || after < 0)
            {
                return DispatchReasonCodes.EnRouteAppendDelayUncomputable;
            }

            // 这条既有需求所在分区没有配置上限时，它「不接受任何延迟」：禁止追加与「随便延」是两回事，
            // 而「未配置」在 REQ-0198 里的意思是前者。没被延到就放过这一条，继续查下一条——每条既有需求都要
            // 自己过一遍，先遇到一条没被延到不代表后面的也没有。
            if (MaxAllowedIncrease(zoneParameters, stop.DispatchZone) is not { } allowance)
            {
                if (after > before)
                {
                    return DispatchReasonCodes.EnRouteAppendNotConfigured;
                }

                continue;
            }

            if (after - before > allowance)
            {
                return DispatchReasonCodes.EnRouteAppendDelayGateExceeded;
            }
        }

        return null;
    }

    /// <summary>从车当前位置沿计划走完全程的代价；任一段算不出返回 -1。</summary>
    private static long PathCost(
        IReadOnlyList<EnRouteStop> stops,
        int vehicleStationRiotId,
        Func<int, int, long?> cost)
    {
        long total = 0;
        int from = vehicleStationRiotId;
        foreach (EnRouteStop stop in stops)
        {
            if (cost(from, stop.StationRiotId) is not { } leg)
            {
                return -1;
            }

            total += leg;
            from = stop.StationRiotId;
        }

        return total;
    }

    /// <summary>从车当前位置走到 <paramref name="target"/> 为止的代价；算不出返回 -1。</summary>
    private static long PrefixCost(
        IReadOnlyList<EnRouteStop> stops,
        int vehicleStationRiotId,
        EnRouteStop target,
        Func<int, int, long?> cost)
    {
        long total = 0;
        int from = vehicleStationRiotId;
        foreach (EnRouteStop stop in stops)
        {
            if (cost(from, stop.StationRiotId) is not { } leg)
            {
                return -1;
            }

            total += leg;
            from = stop.StationRiotId;
            if (ReferenceEquals(stop, target))
            {
                return total;
            }
        }

        return -1;
    }

    /// <summary>各分区在停靠序列上是否形成连续区段：一个分区出现过、断开过、又出现，就不是。</summary>
    private static bool ZonesAreContiguous(IReadOnlyList<EnRouteStop> stops)
    {
        HashSet<string> closed = new(StringComparer.Ordinal);
        string? current = null;
        foreach (EnRouteStop stop in stops)
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
}

/// <summary>计划里的一个停靠，规划器看得见的那几样。</summary>
public sealed record EnRouteStop(
    string StopId,
    string StationId,
    int StationRiotId,
    string DispatchZone,
    string Role);

/// <summary>一辆在途车此刻的计划。</summary>
/// <param name="Stops">未完成的停靠，按序位。</param>
/// <param name="VehicleStationRiotId">车此刻所在（或正驶离）的站。</param>
/// <param name="CurrentNextStopIndex">
/// 当前下一站在 <paramref name="Stops"/> 里的下标，恒为 0——列表里第一个未完成的停靠就是它。写成参数而不是写死 0，
/// 是因为「当前下一站」是 REQ-0196 的概念，值得在调用处被显式地答一次。
/// </param>
/// <param name="WorklistItemsByStopId">每个停靠此刻的清单项数——并入会让其中一个加一，8 项上限按它判。</param>
public sealed record EnRouteVehiclePlan(
    IReadOnlyList<EnRouteStop> Stops,
    int VehicleStationRiotId,
    int CurrentNextStopIndex,
    IReadOnlyDictionary<string, int> WorklistItemsByStopId);

/// <summary>要追加的那条需求：它会带来的两个停靠。</summary>
public sealed record EnRouteAppendCandidate(EnRouteStop PickupStop, EnRouteStop UnloadStop, string DispatchZone);

/// <summary>插在哪，代价多少。</summary>
/// <param name="MergeIntoPickupStopId">并进这个既有停靠；为空表示在 <paramref name="PickupSequence"/> 处新开一个。</param>
/// <param name="Resequenced">插入之后全部停靠的新序位，包括没动的那些。</param>
public sealed record EnRouteAppendPlacement(
    string? MergeIntoPickupStopId,
    int PickupSequence,
    string? MergeIntoUnloadStopId,
    int UnloadSequence,
    long MarginalCostMm,
    IReadOnlyList<EnRouteStopSequence> Resequenced);

/// <summary>一个停靠插入之后的新序位。</summary>
public sealed record EnRouteStopSequence(string StopId, int Sequence);

/// <summary>插得进去（带插入位），或者插不进去（带原因码）。</summary>
public sealed record EnRouteAppendDecision
{
    private EnRouteAppendDecision(EnRouteAppendPlacement? placement, string? refusalReasonCode)
    {
        Placement = placement;
        RefusalReasonCode = refusalReasonCode;
    }

    public EnRouteAppendPlacement? Placement { get; }

    public string? RefusalReasonCode { get; }

    public static EnRouteAppendDecision Placed(EnRouteAppendPlacement placement) =>
        new(placement ?? throw new ArgumentNullException(nameof(placement)), null);

    public static EnRouteAppendDecision Refused(string refusalReasonCode) => new(null, refusalReasonCode);
}
