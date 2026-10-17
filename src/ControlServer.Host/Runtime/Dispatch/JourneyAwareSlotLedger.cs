using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 一辆车这一侧还空着的物理仓位：会话基线 <b>减去这辆车自己已预留、已装的货</b>（票面第 4 条，批次7-06，
/// control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// <b>一个实现覆盖两种车，不分支。</b>空闲车没有未完成的旅程，减数是空集，答案就等于会话基线——与
/// <see cref="SessionBaselineSlotLedger"/> 逐字相同。在途车有旅程，它计划里每一条还没终结的需求都占着这一侧的位置。
/// 写成两个实现再按车的状态挑一个，等于把「这辆车在途没在途」这个判断复制到第二个地方，而那正是两处会先后走岔的地方。
/// </para>
/// <para>
/// <b>已分到仓位的按仓位减，还没分到的按数量减（REQ-0352、REQ-0208，control-server#754）。</b>受理只按花篮数预留，
/// 不定仓号；扫码通过、下装货命令之前才按同侧当时编号最小的空仓分配并写进 <c>TargetSlotsJson</c>，此后不变。所以
/// <c>TargetSlotsJson</c> 为空的需求占的是它那一侧的 <c>ExpectedBasketCount</c> 个位置、不占哪一个具体的号；不为空的占的就是
/// 那几个号。装完之后装的正是分到的那几个，「已分配」与「已装」在仓位这件事上是同一个集合的两个时刻。
/// <c>JourneyDemands.LoadedSlots</c> 记的是实际装进哪几个，用于审计与差异排查，不参与这里的减法。
/// </para>
/// <para>
/// 还没分到仓位的需求占哪一侧，按它受理时冻结的那一版分区归属定
/// （<see cref="DemandAreaAssignmentFreezeStore.ReadFrozenSlotPositionsAsync"/>）。定不出来的——没有冻结，或那一版里没有它的
/// AREA——每一侧都按它的数量减：少算一侧会把别的需求派进它将来要用的那几格，多算只是让这辆车少接一单。
/// </para>
/// <para>
/// 已终结、已释放的需求不减：它的货已经不在车上（或从未上车），仓位与预留数量随之回到基线。终结由
/// <see cref="JourneyDemandStatuses.Terminated"/> 与需求自己的 <see cref="DemandExecutionStatus.Cancelled"/> 一起判，
/// 理由同 <c>JourneyStopCursor.IsDoneAt</c>：迁移回填的在途旅程可能只有后者。释放看 <c>RemovedAt</c>。
/// </para>
/// </remarks>
public sealed class JourneyAwareSlotLedger(ControlServerDbContext dbContext) : IVehicleSlotLedger
{
    public Task<int> ReadAvailableSlotCountAsync(
        DispatchVehicleFacts vehicle,
        string slotPosition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return ReadAvailableSlotCountAsync(
            vehicle.AgvId, vehicle.Onboard, vehicle.SlotPositions, slotPosition, cancellationToken);
    }

    /// <summary>
    /// 同一个答案，不要 RIoT 观测也能问（批次7-07，control-server#212）：装货阶段判「这一侧已无空仓」时，推进段手上
    /// 只有车载端事实与仓位模型，没有也不该为此去读一次 RIoT。
    /// </summary>
    /// <remarks>
    /// <b>是同一段减法，不是第二份</b>：派车问的与装货阶段问的必须是同一个账本，否则会出现「派车说这一侧还有空、
    /// 装货阶段说这一侧满了」——车为一侧的空位等单，而那一侧的空位派车永远不会用。
    /// </remarks>
    public async Task<int> ReadAvailableSlotCountAsync(
        string agvId,
        OnboardDispatchFacts? onboardFacts,
        VehicleSlotPositions? slotPositions,
        string slotPosition,
        CancellationToken cancellationToken)
    {
        if (onboardFacts is null || slotPositions is null)
        {
            return 0;
        }

        Holdings holdings = await HoldingsOfTheVehiclesJourneyAsync(agvId, exceptDemandId: null, cancellationToken)
            .ConfigureAwait(false);
        int unassigned = Unassigned(onboardFacts, slotPositions, slotPosition, holdings).Count;
        return Math.Max(0, unassigned - holdings.ReservedCountOn(slotPosition));
    }

    /// <summary>
    /// 扫码通过之后给 <paramref name="demandId"/> 分仓位时可以挑的号：这一侧会话基线里的空仓，去掉已分给本车别的需求的，
    /// 编号从小到大（REQ-0352）。
    /// </summary>
    /// <remarks>
    /// 别的需求还没分到的那一部分只是数量，不占具体的号，所以不从这里减：受理时已经保证这一侧的数量够所有预留，挑最小的
    /// 几个给这一条，剩下的仍够其余的。禁用的仓不在会话基线里，所以也不在这里。
    /// </remarks>
    public async Task<IReadOnlyList<int>> ReadAssignableSlotsAsync(
        string agvId,
        string demandId,
        OnboardDispatchFacts? onboardFacts,
        VehicleSlotPositions? slotPositions,
        string slotPosition,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        if (onboardFacts is null || slotPositions is null)
        {
            return [];
        }

        Holdings holdings = await HoldingsOfTheVehiclesJourneyAsync(agvId, demandId, cancellationToken)
            .ConfigureAwait(false);
        return Unassigned(onboardFacts, slotPositions, slotPosition, holdings);
    }

    private static IReadOnlyList<int> Unassigned(
        OnboardDispatchFacts onboard,
        VehicleSlotPositions positions,
        string slotPosition,
        Holdings holdings) =>
    [
        .. SessionBaselineSlotLedger.BaselineSlots(onboard, positions, slotPosition)
            .Where(slot => !holdings.AssignedSlots.Contains(slot))
    ];

    /// <summary>
    /// 这辆车未完成的旅程里，还没终结、没释放的需求各自占着的：已分到的具体仓位，与还没分到的每侧数量。没有这样的旅程
    /// 就都是空的。<paramref name="exceptDemandId"/> 那一条不算——给它分仓时，它自己的预留不是别人的。
    /// </summary>
    private async Task<Holdings> HoldingsOfTheVehiclesJourneyAsync(
        string agvId,
        string? exceptDemandId,
        CancellationToken cancellationToken)
    {
        string[] journeyIds = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == agvId && row.Stage != JourneyRuntimeStage.Completed)
            .Select(row => row.JourneyId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (journeyIds.Length == 0)
        {
            return Holdings.None;
        }

        var held = (await dbContext.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => journeyIds.Contains(row.JourneyId) &&
                          row.RemovedAt == null &&
                          row.Status != JourneyDemandStatuses.Terminated)
            .Join(
                dbContext.AcceptedDemands.AsNoTracking(),
                membership => membership.DemandId,
                demand => demand.DemandId,
                (membership, demand) => new
                {
                    membership.DemandId,
                    membership.TargetSlotsJson,
                    membership.ExpectedBasketCount,
                    demand.Status
                })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .Where(row => row.Status != DemandExecutionStatus.Cancelled && row.DemandId != exceptDemandId)
            .ToArray();
        HashSet<int> assigned = [];
        List<(string DemandId, int Count)> reserved = [];
        foreach (var row in held)
        {
            int[] slots = JsonSerializer.Deserialize<int[]>(row.TargetSlotsJson) ?? [];
            if (slots.Length == 0)
            {
                reserved.Add((row.DemandId, row.ExpectedBasketCount));
            }
            foreach (int slot in slots)
            {
                assigned.Add(slot);
            }
        }

        IReadOnlyDictionary<string, string> sides = await new DemandAreaAssignmentFreezeStore(dbContext)
            .ReadFrozenSlotPositionsAsync([.. reserved.Select(item => item.DemandId)], cancellationToken)
            .ConfigureAwait(false);
        return new Holdings(
            assigned,
            [.. reserved.Select(item => (sides.GetValueOrDefault(item.DemandId), item.Count))]);
    }

    /// <summary>已分到的仓位，与还没分到的预留（所在侧，空即定不出来；花篮数）。</summary>
    private sealed record Holdings(
        IReadOnlySet<int> AssignedSlots,
        IReadOnlyList<(string? SlotPosition, int Count)> Reserved)
    {
        public static readonly Holdings None = new(new HashSet<int>(), []);

        /// <summary>这一侧还没分到仓位的预留数量；定不出侧的那几条每一侧都算。</summary>
        public int ReservedCountOn(string slotPosition) => Reserved
            .Where(item => item.SlotPosition is null ||
                           string.Equals(item.SlotPosition, slotPosition, StringComparison.Ordinal))
            .Sum(item => item.Count);
    }
}
