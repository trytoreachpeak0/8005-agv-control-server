using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 一个停靠上挂着几条需求时，推进段照哪一条走（批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// 批次7-03（control-server#208）把录入的范围放宽成了「这个停靠上还没终结的需求」，但只放宽了录入那一处，下游每一步仍按
/// 锚需求走，所以它在录入处加了一句守卫把范围退回锚需求。本票删掉那句守卫，代价是要先把「<b>这个停靠此刻在装哪一条
/// 需求</b>」立成一个状态——否则就落进 7-03 注释里算过的两个坏答案之一：只放宽录入，车上收到的是第一条的开仓命令；
/// 连下游一起按被录入那条走而没有状态，等装货结果时又回头查锚需求的 attempt，旅程静默卡死。
/// </para>
/// <para>
/// 状态的载体是从属需求行的 <see cref="JourneyDemandRow.Status"/>：待装、<c>LOADING</c>（录入已受理、装货命令已发、
/// 结果未到）、已装、已卸、已终结。「此刻在装哪一条」就是当前停靠上唯一一条 <c>LOADING</c>。
/// </para>
/// <para>
/// 当前停靠不再由阶段推出，而是<b>序位最小的、还没完成的停靠</b>（<see cref="JourneyStopRow.Status"/>）。阶段只说
/// 「在这个停靠上走到哪一步」；多停靠下同一个阶段会在不同停靠上重复出现，阶段就不够定位了。
/// </para>
/// </remarks>
public sealed class Batch7MultiDemandAdvanceTests
{
    /// <summary>
    /// 一站两条需求，操作员先扫了<b>第二条</b>的子批：装货命令必须是第二条的——它的 attempt、它的命令 id、它的仓位、
    /// 它的子批。
    /// </summary>
    /// <remarks>
    /// 这一条直接取代 7-03 的 <c>EnteringTheSecondDemandsSublotIsRefusedRatherThanLoadingTheAnchor</c>：那条钉的是
    /// 守卫在位时的行为（拒收 <c>SUBLOT_NOT_IN_DISPATCH_SCOPE</c>），守卫删掉之后它的期望必须反过来，所以它被改写成
    /// 了本类里的这一条，而不是删掉。
    /// </remarks>
    [Fact]
    public async Task EnteringTheSecondDemandsSublotLoadsTheSecondDemand()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow runtime = await TwoDemandsAtThePickupAsync(fixture);

        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", SublotSubmission(fixture, runtime, SecondSublot));
        await TickAndRunAsync(fixture);

        JourneyDemandRow second = await MembershipAsync(fixture, SecondDemandId);
        ProtocolOutboxRow command = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageType == "SlotOperationCommand", TestContext.Current.CancellationToken);
        Assert.Equal(second.LoadCommandMessageId, command.MessageId);
        using JsonDocument document = JsonDocument.Parse(command.PayloadJson);
        JsonElement payload = document.RootElement.GetProperty("payload");
        Assert.Equal(SecondDemandId, payload.GetProperty("demandId").GetString());
        Assert.Equal(SecondSublot, payload.GetProperty("sublotId").GetString());
        Assert.Equal(
            second.LoadSlotOperationAttemptId,
            payload.GetProperty("slotOperationAttemptId").GetString());

        // 「此刻在装第二条」落了库：第二条 LOADING，第一条还在待装。
        Assert.Equal(JourneyDemandStatuses.Loading, second.Status);
        Assert.Equal(JourneyDemandStatuses.PendingLoad, (await MembershipAsync(fixture, FirstDemandId)).Status);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync(FirstDemandId)).Stage);
    }

    /// <summary>
    /// 等装货结果时查的是<b>被录入那一条</b>的 attempt，不是锚需求的：第二条的结果到了，旅程就往下走。
    /// </summary>
    /// <remarks>
    /// 7-03 注释里算过的第二个坏答案正是这里——命令按第二条发出去、结果按锚需求去查，查不到，旅程停在等结果<b>而且
    /// 不报错</b>。所以这一条断言的是「走了」，而反过来那种坏法的样子是「永远停在 AwaitingLoadResult」。
    /// </remarks>
    [Fact]
    public async Task TheLoadResultIsLookedUpByTheEnteredDemandsAttempt()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow runtime = await TwoDemandsAtThePickupAsync(fixture);
        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", SublotSubmission(fixture, runtime, SecondSublot));
        await TickAndRunAsync(fixture);

        await ApplySafeResultAsync(fixture, SecondDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);

        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, SecondDemandId)).Status);
    }

    /// <summary>
    /// 同一站的几条需求逐条串行（规格第 22 节补记）：第一条装完，旅程<b>回到本停靠的等录入</b>等下一条，两条都装完
    /// 才进离站等待。
    /// </summary>
    [Fact]
    public async Task TheStopReturnsToTheEntryWaitUntilEveryDemandOnItIsLoaded()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow runtime = await TwoDemandsAtThePickupAsync(fixture);

        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", SublotSubmission(fixture, runtime, SecondSublot));
        await TickAndRunAsync(fixture);
        await ApplySafeResultAsync(fixture, SecondDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);

        // 一条装完、另一条还没：回到等录入，不离站。
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync(FirstDemandId)).Stage);

        await AddInboxAsync(
            fixture, SecondSubmissionId, "SublotSubmitted", SublotSubmission(fixture, runtime, FirstSublot));
        await TickAndRunAsync(fixture);
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);

        // 两条都装完了，这才轮到离站核验。
        JourneyRuntimeRow after = await fixture.RuntimeAsync(FirstDemandId);
        Assert.True(
            after.Stage is JourneyRuntimeStage.AwaitingStationDeparture or JourneyRuntimeStage.AwaitingDepartureSafety,
            $"两条都装完之后该离站了，实际停在 {after.Stage}。");
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, SecondDemandId)).Status);
    }

    /// <summary>
    /// 锚需求在取货站被终结，旅程还带着另一条：旅程<b>回到当前停靠继续推进</b>，不停在 <c>Blocked</c>
    /// （原 7-03 第 8 条，MVP <c>8be28b1c</c> 修的同一类问题）。
    /// </summary>
    /// <remarks>
    /// 终结走 <see cref="PickupStopTermination"/> 的第一步（只终结这一条），第二步不跑，因为旅程还有未终结的需求。
    /// 留下的坑是阶段：终结的那条路径把旅程推到哪里，都不能让剩下那条需求没人管。
    /// </remarks>
    [Fact]
    public async Task TerminatingTheAnchorLeavesTheJourneyAdvancingOnItsOtherDemand()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow runtime = await TwoDemandsAtThePickupAsync(fixture);

        JourneyRuntimeRow tracked = await fixture.Context.JourneyRuntimes
            .SingleAsync(row => row.DemandId == FirstDemandId, TestContext.Current.CancellationToken);
        await new PickupStopTermination(fixture.Context).StageAsync(
            tracked, FirstDemandId, "CANCELLED_BY_OPERATOR", fixture.Clock.GetUtcNow(),
            TestContext.Current.CancellationToken);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await TickAndRunAsync(fixture);

        JourneyRuntimeRow after = await fixture.RuntimeAsync(FirstDemandId);
        Assert.NotEqual(JourneyRuntimeStage.Blocked, after.Stage);
        Assert.NotEqual(JourneyRuntimeStage.Completed, after.Stage);
        Assert.Equal(JourneyDemandStatuses.Terminated, (await MembershipAsync(fixture, FirstDemandId)).Status);

        // 剩下那条照样能装：清单与录入请求还在这个停靠上开着。
        await AddInboxAsync(
            fixture, SecondSubmissionId, "SublotSubmitted", SublotSubmission(fixture, runtime, SecondSublot));
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyDemandStatuses.Loading, (await MembershipAsync(fixture, SecondDemandId)).Status);
    }

    /// <summary>
    /// 单需求旅程跑完之后，两个停靠都 <c>COMPLETED</c>、那条需求 <c>UNLOADED</c>——「当前停靠」与「装到哪一步」从此
    /// 是落库的状态，不是从阶段反推的。
    /// </summary>
    /// <remarks>
    /// 这两列在批次7-03 之前一直停在受理时写下的值（<c>PENDING</c> 与 <c>PENDING_LOAD</c>），<c>ZeroChangePin</c> 钉着
    /// 它们。本票让它们动起来，于是那批基线在这两列上会变——那是本票<b>要的</b>变化，也是它唯一该有的变化。
    /// </remarks>
    [Fact]
    public async Task ASingleDemandJourneyEndsWithItsStopsCompletedAndItsDemandUnloaded()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        JourneyRuntimeRow runtime = await RunJourneyToCompletionAsync(
            fixture, FirstDemandId, FirstSublot, FirstSubmissionId, FirstSafetyResultId);

        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        JourneyStopRow[] stops = await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId)
            .OrderBy(row => row.Sequence)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            [JourneyStopStatuses.Completed, JourneyStopStatuses.Completed],
            stops.Select(stop => stop.Status).ToArray());
        Assert.Equal(JourneyDemandStatuses.Unloaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
    }

    /// <summary>
    /// 受理、到取货站，然后往这个停靠上再挂一条需求：一站两条，都还没装。
    /// </summary>
    private static async Task<JourneyRuntimeRow> TwoDemandsAtThePickupAsync(RuntimeFixture fixture)
    {
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        fixture.BoxCounts.Set(SecondSublot, 4);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        await AddSecondDemandToJourneyAsync(fixture, runtime);
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupUpperId, runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await TickAndRunAsync(fixture);
        return await fixture.RuntimeAsync(FirstDemandId);
    }

    internal static Task<JourneyDemandRow> MembershipAsync(RuntimeFixture fixture, string demandId) =>
        fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId, TestContext.Current.CancellationToken);

    /// <summary>
    /// 往这趟旅程的两个停靠上再挂一条需求，直接写库——本票之前没有任何路径会这么做，途中追加是本票的另一半。
    /// </summary>
    internal static async Task AddSecondDemandToJourneyAsync(RuntimeFixture fixture, JourneyRuntimeRow runtime)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        AcceptedDemandRow anchor = await fixture.Context.AcceptedDemands.AsNoTracking()
            .SingleAsync(row => row.DemandId == runtime.DemandId, token);
        JourneyDemandRow anchorMembership = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == runtime.JourneyId && row.DemandId == runtime.DemandId, token);

        // AcceptedDemandRow 不是 record，字段又多，所以整行序列化再读回来当克隆用。
        AcceptedDemandRow second = JsonSerializer.Deserialize<AcceptedDemandRow>(JsonSerializer.Serialize(anchor))!;
        second.DemandId = SecondDemandId;
        second.Sublot = SecondSublot;
        second.TransportDemandKey = $"{SecondSublot}|WIRE_TO_GATE";
        second.SeriesId = $"SERIES-{SecondDemandId}";
        fixture.Context.AcceptedDemands.Add(second);

        // 仓位要与锚需求错开：同一辆车上两条需求不能占同一个仓位，而装货结果按仓位回报。
        int[] anchorSlots = JsonSerializer.Deserialize<int[]>(anchorMembership.TargetSlotsJson) ?? [];
        int[] secondSlots = [.. anchorSlots.Select(slot => slot + anchorSlots.Length)];
        fixture.Context.Set<JourneyDemandRow>().Add(new JourneyDemandRow
        {
            JourneyId = anchorMembership.JourneyId,
            DemandId = SecondDemandId,
            PickupStopId = anchorMembership.PickupStopId,
            UnloadStopId = anchorMembership.UnloadStopId,
            ExpectedBasketCount = anchorMembership.ExpectedBasketCount,
            TargetSlotsJson = JsonSerializer.Serialize(secondSlots),
            LoadSlotOperationAttemptId = JourneyPlanBuilder.StableGuid(SecondDemandId, "load-attempt"),
            LoadCommandMessageId = JourneyPlanBuilder.StableGuid(SecondDemandId, "load-command"),
            UnloadSlotOperationAttemptId = JourneyPlanBuilder.StableGuid(SecondDemandId, "unload-attempt"),
            UnloadCommandMessageId = JourneyPlanBuilder.StableGuid(SecondDemandId, "unload-command"),
            DispatchZone = anchorMembership.DispatchZone,
            DispatchGeneration = anchorMembership.DispatchGeneration,
            Status = JourneyDemandStatuses.PendingLoad,
            AddedAt = anchorMembership.AddedAt.AddSeconds(1)
        });
        await fixture.Context.SaveChangesAsync(token);
    }
}
