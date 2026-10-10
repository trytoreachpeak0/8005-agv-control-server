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
/// <see cref="SessionBaselineSlotLedger"/> 逐字相同。在途车有旅程，它计划里每一条还没卸掉的需求都占着自己的目标仓位。
/// 写成两个实现再按车的状态挑一个，等于把「这辆车在途没在途」这个判断复制到第二个地方，而那正是两处会先后走岔的地方。
/// </para>
/// <para>
/// <b>减的是目标仓位，不是「已装的那几个」。</b>一条需求的目标仓位从受理起就为它留着，装完之后装的也正是那几个：
/// 「已预留」与「已装」在仓位这件事上是同一个集合的两个时刻，而未装的那一部分同样不能给别人。
/// <c>JourneyDemands.LoadedSlots</c> 记的是实际装进哪几个，用于审计与差异排查，不参与这里的减法——拿它来减，
/// 一条还没装的需求就会被当成没占仓位，于是第二条需求被派进它预留的那一排。
/// </para>
/// <para>
/// 已终结的需求不减：它的货已经不在车上（或从未上车），仓位随之回到基线。终结由
/// <see cref="JourneyDemandStatuses.Terminated"/> 与需求自己的 <see cref="DemandExecutionStatus.Cancelled"/> 一起判，
/// 理由同 <c>JourneyStopCursor.IsDoneAt</c>：迁移回填的在途旅程可能只有后者。
/// </para>
/// </remarks>
public sealed class JourneyAwareSlotLedger(ControlServerDbContext dbContext) : IVehicleSlotLedger
{
    public Task<IReadOnlyList<int>> ReadAvailableSlotsAsync(
        DispatchVehicleFacts vehicle,
        string slotPosition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return ReadAvailableSlotsAsync(vehicle.AgvId, vehicle.Onboard, vehicle.SlotPositions, slotPosition, cancellationToken);
    }

    /// <summary>
    /// 同一个答案，不要 RIoT 观测也能问（批次7-07，control-server#212）：装货阶段判「这一侧已无空仓」时，推进段手上
    /// 只有车载端事实与仓位模型，没有也不该为此去读一次 RIoT。
    /// </summary>
    /// <remarks>
    /// <b>是同一段减法，不是第二份</b>：派车问的与装货阶段问的必须是同一个账本，否则会出现「派车说这一侧还有空、
    /// 装货阶段说这一侧满了」——车为一侧的空位等单，而那一侧的空位派车永远不会用。
    /// </remarks>
    public async Task<IReadOnlyList<int>> ReadAvailableSlotsAsync(
        string agvId,
        OnboardDispatchFacts? onboardFacts,
        VehicleSlotPositions? slotPositions,
        string slotPosition,
        CancellationToken cancellationToken)
    {
        if (onboardFacts is not OnboardDispatchFacts onboard || slotPositions is not { } positions)
        {
            return [];
        }

        HashSet<int> takenByThisVehicle = await SlotsHeldByTheVehiclesJourneyAsync(agvId, cancellationToken)
            .ConfigureAwait(false);
        return
        [
            .. onboard.AvailableSlots
                .Where(slot => positions.SlotPositionByPhysicalSlot.TryGetValue(slot, out string? position) &&
                    string.Equals(position, slotPosition, StringComparison.Ordinal))
                .Where(slot => !takenByThisVehicle.Contains(slot))
                .Distinct()
                .Order()
        ];
    }

    /// <summary>这辆车未完成的旅程里，还没终结的需求各自占着的目标仓位。没有这样的旅程就是空集。</summary>
    private async Task<HashSet<int>> SlotsHeldByTheVehiclesJourneyAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        string[] journeyIds = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == agvId && row.Stage != JourneyRuntimeStage.Completed)
            .Select(row => row.JourneyId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (journeyIds.Length == 0)
        {
            return [];
        }

        var held = await dbContext.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => journeyIds.Contains(row.JourneyId) &&
                          row.RemovedAt == null &&
                          row.Status != JourneyDemandStatuses.Terminated)
            .Join(
                dbContext.AcceptedDemands.AsNoTracking(),
                membership => membership.DemandId,
                demand => demand.DemandId,
                (membership, demand) => new { membership.TargetSlotsJson, demand.Status })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        HashSet<int> slots = [];
        foreach (var row in held.Where(row => row.Status != DemandExecutionStatus.Cancelled))
        {
            foreach (int slot in JsonSerializer.Deserialize<int[]>(row.TargetSlotsJson) ?? [])
            {
                slots.Add(slot);
            }
        }

        return slots;
    }
}
