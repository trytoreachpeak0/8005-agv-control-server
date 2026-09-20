using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 把一条需求追加进一趟已经在跑的旅程，落库是一次全有或全无（批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// 一次追加要写四样：这条需求的受理行、它带来的新停靠、它在这趟旅程里的归属、以及被它挤动的那些停靠的新序位。
/// <b>四样必须同生共死。</b>只落下一半的后果不是「少了点东西」，而是一张自相矛盾的计划——序位重排落了、停靠没落，
/// 车上那张计划就会跳号；归属落了、受理行没落，旅程会带着一条不存在的需求去装货。
/// </para>
/// <para>
/// 所以这里注入的是提交那一刻的失败。判据不是「抛了异常」——那个太容易满足；判据是<b>库里一个字都没变</b>：
/// 受理行数、停靠行数、归属行数、以及原有停靠的序位，四样与追加之前逐一相同。
/// </para>
/// </remarks>
public sealed class Batch7JourneyAppendPersistenceTests
{
    private const string AgvId = "AGV-L1-001";
    private const string VehicleKey = "BROKERX-L1-0001";
    private const string FirstDemandId = "10000000-0000-4000-8000-000000000001";
    private const string SecondDemandId = "10000000-0000-4000-8000-000000000002";

    /// <summary>提交那一刻崩掉：四样一个都没落，原有停靠的序位也没被改。</summary>
    [Fact]
    public async Task AnAppendThatFailsAtTheCommitLeavesNothingBehind()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyExecutionPlan first = await Batch7JourneyFixture.AcceptAsync(
            fixture.Context, FirstDemandId, AgvId, VehicleKey, Batch7JourneyFixture.Now);
        string journeyId = JourneyIdentity.ForAnchorDemand(FirstDemandId);
        Snapshot before = await ReadAsync(fixture, journeyId);

        ControlServerDbContext context = fixture.NewContext();
        context.SavingChanges += (_, _) => throw new HttpRequestException("The append could not be committed.");

        await Assert.ThrowsAsync<HttpRequestException>(() => new WireToGateStore(context).AppendToJourneyAsync(
            Batch7JourneyFixture.Snapshot(SecondDemandId, Batch7JourneyFixture.Now.AddMinutes(1)),
            AppendPlan(first, journeyId),
            TestContext.Current.CancellationToken));

        Assert.Equal(before, await ReadAsync(fixture, journeyId));
    }

    /// <summary>
    /// 同一次追加再来一遍是幂等的：第二次不新增任何行，序位也不再动一次。
    /// </summary>
    /// <remarks>
    /// 派车轮次会重试，而重试到这里时前一次可能已经提交过了。不幂等的表现是序位被重排两次——第一次把某个停靠
    /// 从 2 挪到 3，第二次再挪到 4，而那个停靠本该待在 3。
    /// </remarks>
    [Fact]
    public async Task AppendingTheSameDemandTwiceChangesNothingTheSecondTime()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyExecutionPlan first = await Batch7JourneyFixture.AcceptAsync(
            fixture.Context, FirstDemandId, AgvId, VehicleKey, Batch7JourneyFixture.Now);
        string journeyId = JourneyIdentity.ForAnchorDemand(FirstDemandId);
        AcceptedDemandSnapshot second =
            Batch7JourneyFixture.Snapshot(SecondDemandId, Batch7JourneyFixture.Now.AddMinutes(1));

        await new WireToGateStore(fixture.NewContext()).AppendToJourneyAsync(
            second, AppendPlan(first, journeyId), TestContext.Current.CancellationToken);
        Snapshot afterFirstAppend = await ReadAsync(fixture, journeyId);

        await new WireToGateStore(fixture.NewContext()).AppendToJourneyAsync(
            second, AppendPlan(first, journeyId), TestContext.Current.CancellationToken);

        Assert.Equal(afterFirstAppend, await ReadAsync(fixture, journeyId));
    }

    /// <summary>一次成功的追加确实写下了那四样——否则上面两条用例在一个什么都不做的实现上也会绿。</summary>
    [Fact]
    public async Task ASuccessfulAppendWritesTheDemandItsStopsAndItsMembership()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyExecutionPlan first = await Batch7JourneyFixture.AcceptAsync(
            fixture.Context, FirstDemandId, AgvId, VehicleKey, Batch7JourneyFixture.Now);
        string journeyId = JourneyIdentity.ForAnchorDemand(FirstDemandId);
        Snapshot before = await ReadAsync(fixture, journeyId);

        await new WireToGateStore(fixture.NewContext()).AppendToJourneyAsync(
            Batch7JourneyFixture.Snapshot(SecondDemandId, Batch7JourneyFixture.Now.AddMinutes(1)),
            AppendPlan(first, journeyId),
            TestContext.Current.CancellationToken);

        Snapshot after = await ReadAsync(fixture, journeyId);
        Assert.Equal(before.AcceptedDemands + 1, after.AcceptedDemands);
        Assert.Equal(before.Memberships + 1, after.Memberships);
        // 取货新开一个停靠，卸货并进既有的那一个：计划从两个停靠变成三个。
        Assert.Equal(before.Stops + 1, after.Stops);
        Assert.Equal("1,2,3", after.Sequences);
    }

    /// <summary>
    /// 追加的插入位：新取货停靠排在当前下一站之后（序位 2），既有的卸货停靠被挤到 3；新需求的卸货并进它。
    /// </summary>
    private static JourneyAppendPlan AppendPlan(JourneyExecutionPlan first, string journeyId) =>
        new(
            journeyId,
            SecondDemandId,
            Batch7JourneyFixture.Plan(
                SecondDemandId, AgvId, VehicleKey, Batch7JourneyFixture.Now.AddMinutes(1)),
            JourneyIdentity.AppendedPickupStopId(SecondDemandId),
            JourneyIdentity.UnloadStopId(journeyId),
            first.DispatchZone,
            DispatchZoneParameterVersion: 7,
            [
                new JourneyStopSequenceChange(JourneyIdentity.PickupStopId(journeyId), 1),
                new JourneyStopSequenceChange(JourneyIdentity.AppendedPickupStopId(SecondDemandId), 2),
                new JourneyStopSequenceChange(JourneyIdentity.UnloadStopId(journeyId), 3),
            ],
            Batch7JourneyFixture.Now.AddMinutes(1));

    private static async Task<Snapshot> ReadAsync(Batch7JourneyFixture fixture, string journeyId)
    {
        ControlServerDbContext context = fixture.NewContext();
        CancellationToken token = TestContext.Current.CancellationToken;
        int[] sequences = (await context.Set<JourneyStopRow>().AsNoTracking()
                .Where(row => row.JourneyId == journeyId)
                .Select(row => row.Sequence)
                .ToArrayAsync(token))
            .Order()
            .ToArray();
        return new Snapshot(
            await context.AcceptedDemands.CountAsync(token),
            await context.Set<JourneyStopRow>().CountAsync(row => row.JourneyId == journeyId, token),
            await context.Set<JourneyDemandRow>().CountAsync(row => row.JourneyId == journeyId, token),
            string.Join(',', sequences));
    }

    private sealed record Snapshot(int AcceptedDemands, int Stops, int Memberships, string Sequences);
}
