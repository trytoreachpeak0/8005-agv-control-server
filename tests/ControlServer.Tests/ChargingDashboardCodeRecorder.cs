using System.Collections.Concurrent;
using ControlServer.Application;
using ControlServer.Host.Dashboard;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ControlServer.Tests;

/// <summary>
/// 充电看板的说明守卫（批次9-10，control-server#408；cs#392 审查 S1 的做法）：充电执行类测试实际写进库的每一个码，看板上都要有中文说明。
/// </summary>
/// <remarks>
/// <para>
/// 不是拿手写清单核手写清单：<see cref="ChargingAllocationTests.FleetAsync"/> 搭的每个夹具都挂这个保存拦截器（分配、执行、到桩、充不上、
/// 人工清桩几个测试类共用那一个夹具），看每次保存里新增或改了的这些字段，没有说明的记下来，夹具释放时断言一个都没有：
/// </para>
/// <list type="bullet">
/// <item>充电旅程的阻断码（<see cref="ChargingDashboardDescriptions.DescribeChargingCode"/>）；旅程还没完成时它会出现在告警卡片上，
/// 所以还要有「现场该做什么」（<see cref="ChargingDashboardDescriptions.FieldActions"/>）；</item>
/// <item>充电周期的线上状态、阶段与结束原因；桩独占经过上的释放原因；</item>
/// <item>桩分配暂停的来源、车辆充电资格暂停的原因、人工充电等待的原因、清桩证明、用途。</item>
/// </list>
/// <para>覆盖面就是这些测试走到的路径：新码出现在一条没有用例走到的路径上时，这里看不见它——那条路径本来就该补用例。</para>
/// </remarks>
internal sealed class ChargingDashboardCodeRecorder : ISaveChangesInterceptor
{
    public ConcurrentBag<string> Seen { get; } = [];

    public ConcurrentBag<string> Undescribed { get; } = [];

    /// <summary>夹具释放时调：这个用例写过的码里，有没有说明的就红，并列出它们。</summary>
    public void AssertEveryCodeIsDescribed() =>
        Assert.True(
            Undescribed.IsEmpty,
            "Charging codes written in this test with no description on the charging dashboard: "
            + string.Join(", ", Undescribed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));

    /// <summary>
    /// 排队原因与 batteryState 不落库，所以从这个用例真实跑出来的地方收：充电分配每次结论变了写的日志（事件 2241，原因码在消息里）、
    /// 板上每辆车最近一次的结论，以及最近一轮已完成的分配里的结论与 batteryState。每一个都要有逐车卡片上的中文说明。
    /// </summary>
    public static void AssertEveryAllocationCodeIsDescribed(
        Host.Runtime.Charging.ChargingAllocationBoard board, IEnumerable<EventRecordingLogger<Host.Runtime.Charging.ChargingAllocator>.Entry> log)
    {
        HashSet<string> reasons = new(StringComparer.Ordinal);
        foreach (EventRecordingLogger<Host.Runtime.Charging.ChargingAllocator>.Entry entry in log.Where(entry => entry.EventId.Id == 2241))
        {
            System.Text.RegularExpressions.Match match =
                System.Text.RegularExpressions.Regex.Match(entry.Message, @"^Charging not allocated for vehicle .+?: ([A-Za-z0-9_]+)\.");
            Assert.True(match.Success, "Unexpected 2241 message shape: " + entry.Message);
            reasons.Add(match.Groups[1].Value);
        }
        reasons.UnionWith(board.Verdicts.Values.Select(verdict => verdict.Reason));
        Host.Runtime.Charging.ChargingBoardPass? pass = board.LatestCompletedPass;
        reasons.UnionWith(pass?.Verdicts.Values.Select(verdict => verdict.Reason) ?? []);
        string[] undescribed =
        [
            .. reasons.Where(reason => ChargingDashboardDescriptions.DescribeAllocationReason(reason) is null)
                .Select(reason => reason + " (queue reason)"),
            .. (pass?.Observations.Values.Select(observed => observed.BatteryState) ?? [])
                .Where(state => !ChargingDashboardDescriptions.BatteryStateProjections.ContainsKey(state))
                .Select(state => state + " (batteryState)"),
        ];
        Assert.True(
            undescribed.Length == 0,
            "Charging allocation codes from this test with no description on the charging dashboard: "
            + string.Join(", ", undescribed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
    }

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Inspect(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Inspect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Inspect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (EntityEntry<JourneyRuntimeRow> entry in context.ChangeTracker.Entries<JourneyRuntimeRow>())
        {
            if (!entry.Entity.IsCharging() || !Written(entry, nameof(JourneyRuntimeRow.BlockReasonCode)) ||
                entry.Entity.BlockReasonCode is not { } code)
            {
                continue;
            }
            Check("journey code", code, ChargingDashboardDescriptions.DescribeChargingCode(code));
            if (entry.Entity.Stage != Domain.JourneyRuntimeStage.Completed)
            {
                Check("field action for open journey code", code, ChargingDashboardDescriptions.FieldActions.GetValueOrDefault(code));
            }
        }

        foreach (EntityEntry<ChargingCycleRow> entry in context.ChangeTracker.Entries<ChargingCycleRow>())
        {
            if (Written(entry, nameof(ChargingCycleRow.WireState)))
            {
                Check("cycle state", entry.Entity.WireState, ChargingDashboardDescriptions.CycleStates.GetValueOrDefault(entry.Entity.WireState));
            }
            if (Written(entry, nameof(ChargingCycleRow.Phase)))
            {
                Check("cycle phase", entry.Entity.Phase, ChargingDashboardDescriptions.CyclePhases.GetValueOrDefault(entry.Entity.Phase));
            }
            if (Written(entry, nameof(ChargingCycleRow.EndReason)) && entry.Entity.EndReason is { } reason)
            {
                Check("cycle end reason", reason, ChargingDashboardDescriptions.DescribeChargingCode(reason));
            }
        }

        foreach (EntityEntry<StationExclusivityRecordRow> entry in context.ChangeTracker.Entries<StationExclusivityRecordRow>())
        {
            if (entry.Entity.StationKind == StationExclusivityKinds.Charger &&
                Written(entry, nameof(StationExclusivityRecordRow.ReleaseReason)) && entry.Entity.ReleaseReason is { } reason)
            {
                Check("charger release reason", reason, ChargingDashboardDescriptions.DescribeChargingCode(reason));
            }
        }

        foreach (EntityEntry<ChargingStationAllocationHoldRow> entry in context.ChangeTracker.Entries<ChargingStationAllocationHoldRow>()
                     .Where(entry => entry.State == EntityState.Added))
        {
            Check("charger hold trigger", entry.Entity.Trigger,
                ChargingDashboardDescriptions.HoldTriggers.GetValueOrDefault(entry.Entity.Trigger));
        }

        foreach (EntityEntry<VehicleChargingEligibilityHoldRow> entry in context.ChangeTracker.Entries<VehicleChargingEligibilityHoldRow>()
                     .Where(entry => entry.State == EntityState.Added))
        {
            Check("vehicle eligibility hold reason", entry.Entity.Reason,
                ChargingDashboardDescriptions.EligibilityHoldReasons.GetValueOrDefault(entry.Entity.Reason));
        }

        foreach (EntityEntry<ManualChargingHoldRow> entry in context.ChangeTracker.Entries<ManualChargingHoldRow>()
                     .Where(entry => Written(entry, nameof(ManualChargingHoldRow.Reason))))
        {
            Check("manual charging hold reason", entry.Entity.Reason,
                ChargingDashboardDescriptions.ManualHoldReasons.GetValueOrDefault(entry.Entity.Reason));
        }

        foreach (EntityEntry<StationClearanceRow> entry in context.ChangeTracker.Entries<StationClearanceRow>())
        {
            if (Written(entry, nameof(StationClearanceRow.Proof)) && entry.Entity.Proof is { } proof)
            {
                Check("clearance proof", proof, ChargingDashboardDescriptions.ClearanceProofs.GetValueOrDefault(proof));
            }
        }

        foreach (EntityEntry<VehiclePurposeClaimRow> entry in context.ChangeTracker.Entries<VehiclePurposeClaimRow>()
                     .Where(entry => Written(entry, nameof(VehiclePurposeClaimRow.Purpose))))
        {
            Check("vehicle purpose", entry.Entity.Purpose, DashboardDescriptions.Purposes.GetValueOrDefault(entry.Entity.Purpose));
        }
    }

    private static bool Written<T>(EntityEntry<T> entry, string property)
        where T : class =>
        entry.State == EntityState.Added ||
        (entry.State == EntityState.Modified && entry.Property(property).IsModified);

    private void Check(string what, string code, string? description)
    {
        Seen.Add(code);
        if (string.IsNullOrWhiteSpace(description))
        {
            Undescribed.Add($"{code} ({what})");
        }
    }
}
