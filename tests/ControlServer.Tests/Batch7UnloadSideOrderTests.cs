using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.Batch7MultiDemandAdvanceTests;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 一个卸货停靠上挂着几条需求时，卸货命令先前侧、后后侧（control-server#303）。
/// </summary>
/// <remarks>
/// <para>
/// 规格第 20 节（REQ-0357、ADR-cross-0061）与第 20.2 节表中「第 5.1 节第 4 条」一行：为各需求分别开各自那一组仍成立，
/// 但两组仓门同样一次一扇、先前侧（<c>FRONT</c>）后后侧（<c>REAR</c>）。同一条指令内的先后由车载端执行器保证；一次停靠里
/// <b>跨需求</b>的先后是服务端发命令的顺序，这里守的就是它。在这之前服务端按归属加入的先后发，后侧需求先加入时先开后侧。
/// </para>
/// <para>
/// <b>装货不在这里。</b>装货跨需求的先后由操作员扫码的顺序决定（扫了哪条就装哪条），服务端不排。
/// </para>
/// <para>
/// 侧从需求的目标仓位读，经车的仓位分组（<see cref="VehicleSlotPositionReader"/>，与派车、按侧判满同一个取法），
/// 不按仓号区间推。这里的夹具绑的是已批准的八仓模型：1～4 前侧，5～8 后侧。
/// </para>
/// </remarks>
public sealed class Batch7UnloadSideOrderTests
{
    private const string Front = "FRONT";
    private const string Rear = "REAR";

    /// <summary>测试自己的记号，不是分组名：一个前侧仓位加其余后侧仓位。</summary>
    private const string BothSides = "FRONT+REAR";

    /// <summary>
    /// 后侧需求先加入、前侧需求后加入：到卸货站后第一条卸货命令发给前侧那条，它卸完才轮到后侧那条。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheFrontDemandIsUnloadedFirstEvenWhenTheRearOneJoinedEarlier()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await LoadBothAndArriveAtTheUnloadStopAsync(fixture, firstSide: Rear, secondSide: Front);

        // 先加入的是后侧那条（锚需求），前侧那条后加入。
        Assert.True(await UnloadCommandedAsync(fixture, SecondDemandId), "到站后第一条卸货命令应发给前侧需求。");
        Assert.False(await UnloadCommandedAsync(fixture, FirstDemandId), "前侧那条卸完之前，后侧那条不该发卸货命令。");

        await ApplySafeResultAsync(fixture, SecondDemandId, SlotOperationType.Unload, SlotBusinessState.Empty);
        await TickAndRunAsync(fixture);

        Assert.Equal(JourneyDemandStatuses.Unloaded, (await MembershipAsync(fixture, SecondDemandId)).Status);
        Assert.True(await UnloadCommandedAsync(fixture, FirstDemandId), "前侧卸完之后该轮到后侧。");
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Unload, SlotBusinessState.Empty);
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyDemandStatuses.Unloaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
    }

    /// <summary>
    /// 两条都在同一侧：保持加入的先后，先加入的先卸。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TwoDemandsOnTheSameSideKeepTheOrderTheyJoinedIn()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await LoadBothAndArriveAtTheUnloadStopAsync(fixture, firstSide: Rear, secondSide: Rear);

        Assert.True(await UnloadCommandedAsync(fixture, FirstDemandId), "同侧时先加入的先卸。");
        Assert.False(await UnloadCommandedAsync(fixture, SecondDemandId), "同侧时后加入的要等先加入的卸完。");
    }

    /// <summary>
    /// 已经发出去的那条卸货命令不会因为排序变了而被换掉：它闭环之前不发别的，闭环之后才轮到前侧那条。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「此刻在卸的」没有落库的状态，是每轮从归属重新算的；排序依据又是车的仓位分组，这份配置可以在一次停靠的中途变
    /// （重新激活配置），而升级那一刻在卸的那条是旧版本按加入先后挑的——两种情形下，按侧重算出来的「第一条」都可能不是
    /// 已经发出去的那一条。只按排序取，推进段会去等一条从没发过的命令的结果，旅程静默停住；已发出那条的结果到了也没人结算。
    /// 所以已经备好仓位操作的那一条优先，排序只决定<b>下一条</b>发给谁。
    /// </para>
    /// <para>
    /// 这里用改写还没发命令那条需求的目标仓位来造「排序在中途变了」：两条同在后侧时先加入的先发，然后把后加入那条挪到前侧。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnUnloadAlreadyCommandedIsSeenThroughBeforeTheSideOrderPicksTheNextOne()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await LoadBothAndArriveAtTheUnloadStopAsync(fixture, firstSide: Rear, secondSide: Rear);
        Assert.True(await UnloadCommandedAsync(fixture, FirstDemandId));

        await SetTargetSlotsAsync(fixture, SecondDemandId, Front, taken: []);
        await TickAndRunAsync(fixture);
        Assert.False(await UnloadCommandedAsync(fixture, SecondDemandId), "在卸的那条闭环之前不该发下一条。");

        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Unload, SlotBusinessState.Empty);
        await TickAndRunAsync(fixture);

        Assert.Equal(JourneyDemandStatuses.Unloaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
        Assert.True(await UnloadCommandedAsync(fixture, SecondDemandId), "已发出那条闭环之后，下一条照常发出。");
    }

    /// <summary>
    /// 归属已被移除的需求不参与排序：它即使在前侧、状态还停在已装，也不会被挑去卸，也不会挡住别的需求。
    /// </summary>
    /// <remarks>
    /// 被移除的那条放在前侧、状态留在已装，是为了让这条用例有判别力：排序若读了已移除的归属，它就是「第一条」。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ARemovedMembershipTakesNoPartInTheOrder()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await LoadBothAndArriveAtTheUnloadStopAsync(
            fixture,
            firstSide: Rear,
            secondSide: Front,
            beforeArrival: async () =>
            {
                JourneyDemandRow second = await fixture.Context.Set<JourneyDemandRow>()
                    .SingleAsync(row => row.DemandId == SecondDemandId, TestContext.Current.CancellationToken);
                second.RemovedAt = fixture.Clock.GetUtcNow();
                // 移除原因取释放改派那一个：只有它让一条没有生效归属的 open 需求不算孤儿（DemandJourneyLookup）。
                second.RemovalReason = DemandJourneyLookup.ReleasedForRedispatchReason;
                await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
                fixture.Context.ChangeTracker.Clear();
            });

        Assert.True(await UnloadCommandedAsync(fixture, FirstDemandId), "移除的前侧归属不该挡住后侧那条。");
        Assert.False(await UnloadCommandedAsync(fixture, SecondDemandId), "移除的归属不该收到卸货命令。");
    }

    /// <summary>
    /// 车的仓位模型解析不出来：次序退回加入先后，而且记一条 Warning，说明是哪辆车、哪个停靠、为什么（cs#303 审查）。
    /// </summary>
    /// <remarks>
    /// 前侧那条后加入，所以「退回加入先后」在这里看得出来：能排出侧时先卸的是它。模型在装完、到站之前撤掉——派车在模型
    /// 解析不出来时根本不派，在途旅程遇到这种情况只能是中途撤了配置。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnUnresolvedSlotModelFallsBackToTheJoinOrderAndSaysSo()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await LoadBothAndArriveAtTheUnloadStopAsync(
            fixture,
            firstSide: Rear,
            secondSide: Front,
            beforeArrival: async () =>
            {
                await fixture.Context.Set<SlotIoBindingRow>()
                    .Where(row => row.AgvId == fixture.Options.AgvId)
                    .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
                Assert.Null(await new VehicleSlotPositionReader(fixture.Context)
                    .ReadAsync(fixture.Options.AgvId, TestContext.Current.CancellationToken));
            });

        Assert.True(await UnloadCommandedAsync(fixture, FirstDemandId), "排不出侧时退回加入先后，先加入的先卸。");
        Assert.False(await UnloadCommandedAsync(fixture, SecondDemandId));
        (LogLevel Level, string Message) warning = Assert.Single(
            fixture.EngineLog.Entries, entry => entry.Message.Contains("SLOT_MODEL_UNRESOLVED", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(fixture.Options.AgvId, warning.Message, StringComparison.Ordinal);
        Assert.Contains(FirstDemandId, warning.Message, StringComparison.Ordinal);
        Assert.Contains(SecondDemandId, warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 跨两侧的需求排在只有后侧的需求前面，哪怕后者先加入：它最先开的是前侧那一扇。
    /// </summary>
    /// <remarks>
    /// 反过来排，整站开门的次序是后、前、后。与下一条合起来钉住「先按最先开的那一扇、再按最后开的那一扇」。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ADemandSpanningBothSidesGoesBeforeARearOnlyDemand()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await LoadBothAndArriveAtTheUnloadStopAsync(fixture, firstSide: Rear, secondSide: BothSides);

        Assert.True(await UnloadCommandedAsync(fixture, SecondDemandId), "跨两侧的那条最先开前侧，应先于只有后侧的那条。");
        Assert.False(await UnloadCommandedAsync(fixture, FirstDemandId));
    }

    /// <summary>
    /// 只有前侧的需求排在跨两侧的需求前面，哪怕后者先加入：跨两侧的那条最后开的是后侧那一扇。
    /// </summary>
    /// <remarks>
    /// 反过来排，整站开门的次序是前、后、前。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AFrontOnlyDemandGoesBeforeADemandSpanningBothSides()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await LoadBothAndArriveAtTheUnloadStopAsync(fixture, firstSide: BothSides, secondSide: Front);

        Assert.True(await UnloadCommandedAsync(fixture, SecondDemandId), "只有前侧的那条应先于跨两侧的那条。");
        Assert.False(await UnloadCommandedAsync(fixture, FirstDemandId));
    }

    /// <summary>
    /// 两条需求在同一个取货停靠装上车、同一个卸货停靠卸：先按给定的侧改写两条的目标仓位，装完两条，答复离站安全，
    /// 车开到卸货站。停在等卸货结果。
    /// </summary>
    private static async Task LoadBothAndArriveAtTheUnloadStopAsync(
        RuntimeFixture fixture,
        string firstSide,
        string secondSide,
        Func<Task>? beforeArrival = null)
    {
        // 跨两侧要两个仓位，4 箱只占一个。
        JourneyRuntimeRow runtime = await TwoDemandsAtThePickupAsync(
            fixture, firstSide == BothSides || secondSide == BothSides ? 7 : 4);
        int[] firstSlots = await SetTargetSlotsAsync(fixture, FirstDemandId, firstSide, taken: []);
        await SetTargetSlotsAsync(fixture, SecondDemandId, secondSide, taken: firstSlots);

        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));
        await TickAndRunAsync(fixture);
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        await EnterSublotAsync(fixture, FirstDemandId, SecondSublot, SecondSubmissionId);
        await ApplySafeResultAsync(fixture, SecondDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, SecondDemandId)).Status);

        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        if (beforeArrival is not null)
        {
            await beforeArrival();
        }
        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
    }

    /// <summary>
    /// 把一条需求的目标仓位改写到给定那一侧、避开 <paramref name="taken"/>，条数不变。仓位的侧从车的仓位分组读。
    /// </summary>
    private static async Task<int[]> SetTargetSlotsAsync(
        RuntimeFixture fixture, string demandId, string side, int[] taken)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        VehicleSlotPositions positions = await new VehicleSlotPositionReader(fixture.Context)
            .ReadAsync(fixture.Options.AgvId, token)
            ?? throw new InvalidOperationException("The fixture binds the approved slot model.");
        JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>()
            .SingleAsync(row => row.DemandId == demandId, token);
        int count = (JsonSerializer.Deserialize<int[]>(membership.TargetSlotsJson) ?? []).Length;
        int[] Free(string group) =>
        [
            .. positions.SlotPositionByPhysicalSlot
                .Where(pair => pair.Value == group && !taken.Contains(pair.Key))
                .Select(pair => pair.Key)
                .Order()
        ];
        // 跨两侧：一个前侧仓位，其余后侧。
        int[] slots = side == BothSides
            ? [.. Free(Front).Take(1), .. Free(Rear).Take(count - 1)]
            : [.. Free(side).Take(count)];
        Assert.Equal(count, slots.Length);
        Assert.True(side != BothSides || count >= 2, $"跨两侧要至少两个仓位，这条需求只有 {count} 个。");
        membership.TargetSlotsJson = JsonSerializer.Serialize(slots);
        await fixture.Context.SaveChangesAsync(token);
        fixture.Context.ChangeTracker.Clear();
        return slots;
    }

    /// <summary>这条需求的卸货命令发出去了没有。</summary>
    private static async Task<bool> UnloadCommandedAsync(RuntimeFixture fixture, string demandId)
    {
        JourneyDemandRow membership = await MembershipAsync(fixture, demandId);
        return await fixture.Context.ProtocolOutbox.AsNoTracking()
            .AnyAsync(
                row => row.MessageType == "SlotOperationCommand" && row.MessageId == membership.UnloadCommandMessageId,
                TestContext.Current.CancellationToken);
    }
}
