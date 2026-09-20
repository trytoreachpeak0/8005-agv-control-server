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
    /// 旅程在轮次判定与落库之间变成 Blocked：这一次追加被拒，库里一个字都没变
    /// （批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这不是一道多余的保险，这是那条路唯一的守卫。</b>派车轮次在开头读到旅程不是 Blocked，而从那一读到
    /// 这一写之间，车载端的一条入站消息可以把它置成 Blocked（<c>OnboardRecoveryCoordinator</c>，另一个连接、
    /// 另一个线程）。轮次那一侧在它自己的时刻是对的，所以挡不住这个竞态；判据放在事务外也挡不住，
    /// <c>BEGIN IMMEDIATE</c> 之前读到的仍是旧快照。
    /// </para>
    /// <para>
    /// 放行的代价不是少接一条活：需求写进 <c>AcceptedDemands</c> 之后就不再是候选，绑死在一辆等人介入的车上，
    /// 而车上那张计划不会更新——<c>RefreshUpcomingStopPlanAsync</c> 对 Blocked 直接返回。操作员看到的是一条
    /// 派出去了、却永远不动的需求。
    /// </para>
    /// <para>
    /// 判据不是「抛了异常」：那太容易满足。判据与本类其余两条相同——<b>库里一个字都没变</b>。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnAppendOntoAJourneyBlockedSinceTheRoundReadItIsRefusedAndChangesNothing()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyExecutionPlan first = await Batch7JourneyFixture.AcceptAsync(
            fixture.Context, FirstDemandId, AgvId, VehicleKey, Batch7JourneyFixture.Now);
        string journeyId = JourneyIdentity.ForAnchorDemand(FirstDemandId);
        await BlockAsync(fixture, journeyId);
        Snapshot before = await ReadAsync(fixture, journeyId);

        await Assert.ThrowsAsync<BusinessIdentityConflictException>(
            () => new WireToGateStore(fixture.NewContext()).AppendToJourneyAsync(
                Batch7JourneyFixture.Snapshot(SecondDemandId, Batch7JourneyFixture.Now.AddMinutes(1)),
                AppendPlan(first, journeyId),
                TestContext.Current.CancellationToken));

        Assert.Equal(before, await ReadAsync(fixture, journeyId));
    }

    /// <summary>把旅程置成 Blocked，像一条报了需要恢复的装货结果那样。</summary>
    private static async Task BlockAsync(Batch7JourneyFixture fixture, string journeyId)
    {
        ControlServerDbContext context = fixture.NewContext();
        JourneyRuntimeRow runtime = await context.JourneyRuntimes.SingleAsync(
            row => row.JourneyId == journeyId, TestContext.Current.CancellationToken);
        runtime.Stage = JourneyRuntimeStage.Blocked;
        runtime.SetBlockReason("LOAD_RESULT_REQUIRES_RECOVERY", Batch7JourneyFixture.Now);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 追加的插入位：新取货停靠排在当前下一站之后（序位 2），既有的卸货停靠被挤到 3；新需求的卸货并进它。
    /// </summary>
    /// <summary>
    /// 重放算出来的插入位与已经落下的不一致：判冲突，而不是照着新的再排一次。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 票面「重放与重连」写的是「同一追加重放判同、<b>序列不同判冲突</b>」。上面那条幂等用例两次传的是
    /// <b>同一个</b> <c>JourneyAppendPlan</c>，走的只是「判同」那一半；这一条走另一半。
    /// </para>
    /// <para>
    /// 它要防的是什么：派车轮次重试时会重新规划，而这中间计划可能已经被另一次追加改过。若此时照新算出的
    /// 序位再排一次，车手上那张计划与库里的就分岔了，而两边都认为自己是对的。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AReplayedAppendWhoseSequenceDisagreesWithWhatIsStoredIsRefused()
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

        // 同一条需求、同一趟旅程，但新来的这一版把追加的取货放在序位 3、卸货放在 2——落下的是反过来的。
        JourneyAppendPlan disagreeing = AppendPlan(first, journeyId) with
        {
            Resequenced =
            [
                new JourneyStopSequenceChange(JourneyIdentity.PickupStopId(journeyId), 1),
                new JourneyStopSequenceChange(JourneyIdentity.UnloadStopId(journeyId), 2),
                new JourneyStopSequenceChange(JourneyIdentity.AppendedPickupStopId(SecondDemandId), 3),
            ],
        };

        await Assert.ThrowsAsync<BusinessIdentityConflictException>(
            () => new WireToGateStore(fixture.NewContext()).AppendToJourneyAsync(
                second, disagreeing, TestContext.Current.CancellationToken));
        // 拒绝要干净：库里一个字都没动，否则「判冲突」就成了「先改一半再抛」。
        Assert.Equal(afterFirstAppend, await ReadAsync(fixture, journeyId));
    }

    /// <summary>
    /// 同一个需求 id 第二次带着不同的内容回来：判冲突。
    /// </summary>
    /// <remarks>
    /// 这一条与上面那条走的是同一个方法里的不同分支：那条比的是停靠序列，这条比的是需求本身的身份四项
    /// （<c>DemandId</c>、<c>TransportDemandKey</c>、<c>Sublot</c>、<c>WorkType</c>）。重放判同的前提是
    /// 「同一条需求」，而那四项就是「同一条」的定义。
    /// </remarks>
    [Fact]
    public async Task AReplayedAppendCarryingDifferentContentUnderTheSameDemandIdIsRefused()
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

        AcceptedDemandSnapshot sameIdOtherContent = second with { Sublot = "SUBLOT-SOMETHING-ELSE" };

        await Assert.ThrowsAsync<BusinessIdentityConflictException>(
            () => new WireToGateStore(fixture.NewContext()).AppendToJourneyAsync(
                sameIdOtherContent, AppendPlan(first, journeyId), TestContext.Current.CancellationToken));
        Assert.Equal(afterFirstAppend, await ReadAsync(fixture, journeyId));
    }

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
