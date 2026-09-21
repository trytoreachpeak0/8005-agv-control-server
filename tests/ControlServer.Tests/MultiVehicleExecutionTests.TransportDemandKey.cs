using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 批次7-05（control-server#210）的隐患回归：同一份目录里两个 <c>DemandId</c> 同一个业务键，车队在同一轮里判它们。
/// </summary>
public sealed partial class MultiVehicleExecutionTests
{
    /// <summary>
    /// 第一辆车受理第一条；同键的第二条得 <c>TRANSPORT_DEMAND_KEY_ALREADY_ACCEPTED</c>，本轮跑完、不抛；目录里一条无关需求在同一轮
    /// 被另一辆车受理。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>缺陷版本上红在哪里</b>（本票改动之前的 <c>fp/v2-impl</c>）：同键的第二条过完全部判据、每一辆还能接活的车都出价；
    /// 被选中的车在受理存储层撞上业务键唯一索引、抛 <see cref="BusinessIdentityConflictException"/>，记 2124、退出本轮，
    /// 下一个出价者重复一遍——于是排在后面的无关需求一辆车都不剩。票面写的是在 <c>e74c0058</c> 上「本轮抛」，那是
    /// control-server#231 的每车隔离之前的形状；隔离之后抛错被每一辆车各吞一次，症状变成「后面的需求没车接」加上一串 2124。
    /// </para>
    /// <para>
    /// 判据的两半各守一个方向：「无关需求被受理」抓整轮被卡住，「没有 2124」抓每车隔离把缺陷藏成天气。只断言前一半的话，
    /// 车多于出价者的车队上缺陷版本也会绿。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TwoDemandIdsOfOneKeyInOneRoundTakeOneAndLeaveTheRestOfTheRoundServed()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        AcceptedDemandSnapshot first = FleetFixture.Demand(0, "N1-1", 0);
        AcceptedDemandSnapshot sameKey = first with
        {
            DemandId = "10000000-0000-4000-8000-000000000099",
            SeriesId = "SERIES-99",
            CreatedAt = first.CreatedAt.AddMinutes(1),
            ValueObservedAt = first.ValueObservedAt.AddMinutes(1),
            ValuePollTraceId = "TRACE-99",
            ValueProjectionCommitId = "COMMIT-99",
        };
        AcceptedDemandSnapshot unrelated = FleetFixture.Demand(1, "N1-2", 2);
        Assert.Equal(first.TransportDemandKey, sameKey.TransportDemandKey);
        fixture.Catalog.Set([first, sameKey, unrelated]);

        await fixture.RunRoundAsync();

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        fixture.Context.ChangeTracker.Clear();
        AcceptedDemandRow[] accepted = await fixture.Context.AcceptedDemands.AsNoTracking().ToArrayAsync(cancellationToken);
        Assert.Equal(
            [first.DemandId, unrelated.DemandId],
            accepted.Select(row => row.DemandId).Order(StringComparer.Ordinal).ToArray());
        JourneyRuntimeRow[] journeys = await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(cancellationToken);
        Assert.Equal(2, journeys.Select(row => row.AgvId).Distinct(StringComparer.Ordinal).Count());
        JourneyBacklogRow refused = await fixture.Context.JourneyBacklog.AsNoTracking()
            .SingleAsync(row => row.DemandId == sameKey.DemandId, cancellationToken);
        Assert.Equal(DispatchReasonCodes.TransportDemandKeyAlreadyAccepted, refused.ReasonCode);
        Assert.Null(refused.AcceptedAt);
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.EventId.Id is 2123 or 2124);
        // The round reached its end with every vehicle: nobody was withdrawn over the second DemandId.
        Assert.Equal(3, Assert.Single(fixture.RoundOutcomes.Outcomes).CompletedVehicles.Count);
    }
}
