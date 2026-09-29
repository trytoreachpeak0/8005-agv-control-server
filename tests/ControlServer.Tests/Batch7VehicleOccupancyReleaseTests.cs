using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 批次 7 建表票（control-server#206）：用途占有在每一个旅程结束的地方、同一次保存里释放，所以一趟旅程不论怎样结束，
/// 同一辆车都能受理下一趟。批次8-16（control-server#387）退役租约之后，成立的是「开着的占有记录 ⇔ 用途占有」，
/// 并且每一趟结束都在历史里留下释放时刻与原因。
/// </summary>
/// <remarks>
/// <para>
/// 释放用途占有的地方只有三处：卸货结果（<c>WireToGateStore.ApplyOperationResultAsync</c>）、直接完成入口
/// （<c>WireToGateStore.CompleteDemandAfterUnloadAsync</c>）、取货停靠的各种结束（<see cref="PickupStopTermination"/>）。
/// 最后那一处是站点期限、确定性装货失败、扫码前取消、在途取消、补偿、故障货物交接、强制机械取出七条路径共用的尾巴，
/// 这里按它们用到的四种终态原因各跑一遍。经真实运行时与报文处理器走完这些路径、再让同车接下一单的端到端测试在各自的类里
/// （PR 正文逐条列名），它们在结束之后同样断言 <see cref="VehicleOccupancyAssertions"/>。
/// </para>
/// </remarks>
public sealed class Batch7VehicleOccupancyReleaseTests
{
    [Fact]
    public async Task AfterASuccessfulUnloadResultTheSameVehicleTakesItsNextJourney()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.RunToCompletionAsync();
        Assert.Equal(DemandExecutionStatus.Succeeded, (await fixture.DemandRowAsync()).Status);
        await VehicleOccupancyAssertions.AssertOpenClaimRecordsAndPurposeClaimsMatchAsync(fixture.Context);
        await ZeroChangePin.AssertMatchesAsync(fixture.Context, "unload");
        Assert.Equal(
            VehiclePurposeReleaseReasons.LastDemandUnloaded,
            (await fixture.ClaimRecordAsync()).ReleaseReason);
        Assert.Empty(await fixture.Context.Set<VehiclePurposeClaimRow>().AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));

        fixture.Catalog.Set(fixture.Demand("10000000-0000-4000-8000-000000000002", "SUBLOT-002", Now.AddMinutes(-5)));
        fixture.BoxCounts.Set("SUBLOT-002", 4);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            JourneyRuntimeStage.AwaitingPickupArrival,
            (await fixture.RuntimeAsync("10000000-0000-4000-8000-000000000002")).Stage);
        await VehicleOccupancyAssertions.AssertOpenClaimRecordsAndPurposeClaimsMatchAsync(fixture.Context);
        Assert.Single(await fixture.Context.Set<VehiclePurposeClaimRow>().AsNoTracking()
            .Where(claim => claim.JourneyId == "journey:10000000-0000-4000-8000-000000000002")
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AfterASuccessfulUnloadThroughTheDirectCompletionEntryTheSameVehicleTakesItsNextJourney()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-721", "agv-01", "VK-01", Batch7JourneyFixture.Now);

        await Batch7JourneyFixture.CompleteByUnloadAsync(fixture.Context, "D-721", Batch7JourneyFixture.Now.AddMinutes(10));
        await VehicleOccupancyAssertions.AssertOpenClaimRecordsAndPurposeClaimsMatchAsync(fixture.NewContext());
        await VehicleOccupancyAssertions.AssertReleasedWithHistoryAsync(
            fixture.NewContext(), JourneyIdentity.ForAnchorDemand("D-721"), Batch7JourneyFixture.Now.AddMinutes(10),
            VehiclePurposeReleaseReasons.LastDemandUnloaded);

        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-722", "agv-01", "VK-01", Batch7JourneyFixture.Now.AddMinutes(11));
        await VehicleOccupancyAssertions.AssertOpenClaimRecordsAndPurposeClaimsMatchAsync(fixture.NewContext());
    }

    /// <summary>
    /// Every ending of a pickup stop, by the terminal reason its caller hands <see cref="PickupStopTermination"/>:
    /// the station deadline and the determinate load failure end under <c>CANCELLED_BY_STATION_TIMEOUT</c>, the
    /// cancellation before a sublot and the one in flight under <c>CANCELLED_BY_OPERATOR</c>, the compensation under
    /// <c>CANCELLED_BY_LOAD_COMPENSATION</c>, and the fault cargo handoff and the forced mechanical recovery under
    /// <c>TERMINATED_BY_FAULT_CARGO_HANDOFF</c>.
    /// </summary>
    [Theory]
    [InlineData(JourneyRuntimeStage.AwaitingSublot, "CANCELLED_BY_STATION_TIMEOUT")]
    [InlineData(JourneyRuntimeStage.AwaitingSublot, "CANCELLED_BY_OPERATOR")]
    [InlineData(JourneyRuntimeStage.Blocked, "CANCELLED_BY_OPERATOR")]
    [InlineData(JourneyRuntimeStage.Blocked, "CANCELLED_BY_LOAD_COMPENSATION")]
    [InlineData(JourneyRuntimeStage.Blocked, "TERMINATED_BY_FAULT_CARGO_HANDOFF")]
    [InlineData(JourneyRuntimeStage.AwaitingGateArrival, "TERMINATED_BY_FAULT_CARGO_HANDOFF")]
    public async Task AfterAPickupStopEndsTheSameVehicleTakesItsNextJourney(JourneyRuntimeStage endedIn, string reasonCode)
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTimeOffset now = Batch7JourneyFixture.Now;
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-723", "agv-01", "VK-01", now);
        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.SingleAsync(cancellationToken);
        runtime.Stage = endedIn;
        await fixture.Context.SaveChangesAsync(cancellationToken);

        await new PickupStopTermination(fixture.Context).StageAsync(runtime, reasonCode, now.AddMinutes(5), cancellationToken);
        await fixture.Context.SaveChangesAsync(cancellationToken);

        Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.NewContext().AcceptedDemands.SingleAsync(cancellationToken)).Status);
        await VehicleOccupancyAssertions.AssertOpenClaimRecordsAndPurposeClaimsMatchAsync(fixture.NewContext());
        Assert.Empty(await fixture.NewContext().Set<VehiclePurposeClaimRow>().ToArrayAsync(cancellationToken));
        await VehicleOccupancyAssertions.AssertReleasedWithHistoryAsync(
            fixture.NewContext(), runtime.JourneyId, now.AddMinutes(5), reasonCode);

        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-724", "agv-01", "VK-01", now.AddMinutes(6));
        await VehicleOccupancyAssertions.AssertOpenClaimRecordsAndPurposeClaimsMatchAsync(fixture.NewContext());
    }

    [Fact]
    public async Task EndingAPickupStopReleasesOnlyTheClaimItsOwnJourneyHolds()
    {
        // A claim some other journey holds on the vehicle -- which the key allows to exist only after this journey's own
        // was released -- is not this ending's to remove.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTimeOffset now = Batch7JourneyFixture.Now;
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-725", "agv-01", "VK-01", now);
        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.SingleAsync(cancellationToken);
        await new PickupStopTermination(fixture.Context).StageAsync(runtime, "CANCELLED_BY_OPERATOR", now.AddMinutes(1), cancellationToken);
        await fixture.Context.SaveChangesAsync(cancellationToken);
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-726", "agv-01", "VK-01", now.AddMinutes(2));

        // The first journey's ending replayed: its lease is already released, and the vehicle's claim is the second's.
        await new PickupStopTermination(fixture.Context).StageAsync(runtime, "CANCELLED_BY_OPERATOR", now.AddMinutes(3), cancellationToken);
        await fixture.Context.SaveChangesAsync(cancellationToken);

        VehiclePurposeClaimRow claim = await fixture.NewContext().Set<VehiclePurposeClaimRow>().SingleAsync(cancellationToken);
        Assert.Equal(JourneyIdentity.ForAnchorDemand("D-726"), claim.JourneyId);
        await VehicleOccupancyAssertions.AssertOpenClaimRecordsAndPurposeClaimsMatchAsync(fixture.NewContext());
    }
}
