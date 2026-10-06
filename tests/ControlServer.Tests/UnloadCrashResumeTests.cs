using System.Data.Common;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static ControlServer.Tests.Batch7MultiDemandAdvanceTests;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 卸货落定要保存不止一次，崩在两次之间、重启之后，旅程要从落库的状态接着走（control-server#291，U1～U3、U5）。
/// </summary>
/// <remarks>
/// <para>
/// 与装货侧那一族（批次7-07，control-server#212）同一个形状：卸完一条先把归属写成 <c>UNLOADED</c> 单独保存（下面重新加载游标要读到它），
/// 再在下一条卸货命令、离站或旅程收尾那一次保存里推进。崩在两次之间，重启后读到的是「阶段还是 <c>AwaitingUnloadResult</c>、
/// 刚卸的那条已是 <c>UNLOADED</c>」，而推进段原来只认「还有一条在等结果」这一种。
/// </para>
/// <para>
/// 每条用例在票面点名的那一次写上注入一次失败（<see cref="FailOnce"/>），换一个引擎（如同进程重启）再跑，<b>断这辆车自己有没有往前走</b>，
/// 不断这一轮抛没抛：cs#487 起推进段逐车隔离，一辆车每轮抛不再拖停别的车，所以「抛不抛」不再是缺陷的形状——「这辆车永远停在原地」才是。
/// </para>
/// </remarks>
public sealed class UnloadCrashResumeTests
{
    /// <summary>
    /// U1：一站两条要卸，卸完第一条、归属已存为 <c>UNLOADED</c>，下一版清单写盘时崩了。重启后第二条照常下发、卸完，旅程完成。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACrashAfterTheFirstOfTwoUnloadsIsSavedStillCommandsTheSecond()
    {
        FailOnce crash = new(command =>
            command.CommandText.Contains("INSERT INTO \"ProtocolOutbox\"", StringComparison.Ordinal) &&
            HasParameter(command, "CurrentStopWorklistSnapshot"));
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: crash);
        await LoadBothAndArriveAtTheUnloadStopAsync(fixture);
        (string first, string second) = await UnloadOrderAsync(fixture);

        await ApplySafeResultAsync(fixture, first, SlotOperationType.Unload, SlotBusinessState.Empty);
        crash.Armed = true;
        await TickAndRunExpectingCrashAsync(fixture);
        Assert.True(crash.Fired, "The injected failure never fired, so this proved nothing.");

        await fixture.RecreateEngineAsync();
        Assert.Equal(JourneyDemandStatuses.Unloaded, (await MembershipAsync(fixture, first)).Status);
        Assert.False(await UnloadCommandedAsync(fixture, second), "The crash was meant to land before the second unload was commanded.");

        Exception? resumed = await RunRoundAsync(fixture);
        Assert.True(
            await UnloadCommandedAsync(fixture, second),
            $"After the restart the second unload was never commanded. stage={(await JourneyOfAsync(fixture, first)).Stage} round={resumed}");

        await ApplySafeResultAsync(fixture, second, SlotOperationType.Unload, SlotBusinessState.Empty);
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.Completed, (await JourneyOfAsync(fixture, first)).Stage);
    }

    /// <summary>
    /// U5：同上，但崩在下一版清单已写盘之后、第二条的卸货命令（<c>PrepareSlotOperationAsync</c>）提交之前。
    /// 重启后清单复用已存那一行，第二条照常下发。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACrashBetweenTheNextWorklistAndTheSecondUnloadCommandStillCommandsTheSecond()
    {
        FailOnce crash = new(command =>
            command.CommandText.Contains("INSERT INTO \"ProtocolOutbox\"", StringComparison.Ordinal) &&
            HasParameter(command, "SlotOperationCommand"));
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: crash);
        await LoadBothAndArriveAtTheUnloadStopAsync(fixture);
        (string first, string second) = await UnloadOrderAsync(fixture);
        int worklistsBefore = await CountAsync(fixture, "CurrentStopWorklistSnapshot");

        await ApplySafeResultAsync(fixture, first, SlotOperationType.Unload, SlotBusinessState.Empty);
        crash.Armed = true;
        await TickAndRunExpectingCrashAsync(fixture);
        Assert.True(crash.Fired, "The injected failure never fired, so this proved nothing.");

        await fixture.RecreateEngineAsync();
        Assert.Equal(worklistsBefore + 1, await CountAsync(fixture, "CurrentStopWorklistSnapshot"));
        Assert.False(await UnloadCommandedAsync(fixture, second), "The crash was meant to land before the second unload was commanded.");

        Exception? resumed = await RunRoundAsync(fixture);
        Assert.True(
            await UnloadCommandedAsync(fixture, second),
            $"After the restart the second unload was never commanded. stage={(await JourneyOfAsync(fixture, first)).Stage} round={resumed}");
        Assert.Equal(worklistsBefore + 1, await CountAsync(fixture, "CurrentStopWorklistSnapshot"));

        await ApplySafeResultAsync(fixture, second, SlotOperationType.Unload, SlotBusinessState.Empty);
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.Completed, (await JourneyOfAsync(fixture, first)).Stage);
    }

    /// <summary>
    /// U2：本站最后一条卸完、计划里还有下一站，崩在「已卸」保存之后、阶段前移到离站那一次保存上。
    /// 重启后照常发离站核验，答复之后建下一段腿的订单意图。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACrashAfterTheLastUnloadOfAStopWithAStopAfterItStillLeavesForTheNextStop()
    {
        FailOnce crash = new(command =>
            command.CommandText.Contains("UPDATE \"JourneyRuntimes\"", StringComparison.Ordinal) &&
            HasParameter(command, nameof(JourneyRuntimeStage.AwaitingStationDeparture)));
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: crash);
        string extraUpperId = await SingleDemandWithAnExtraUnloadStopAsync(fixture);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Unload, SlotBusinessState.Empty);
        crash.Armed = true;
        await TickAndRunExpectingCrashAsync(fixture);
        Assert.True(crash.Fired, "The injected failure never fired, so this proved nothing.");

        await fixture.RecreateEngineAsync();
        Assert.Equal(JourneyDemandStatuses.Unloaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        Exception? resumed = await RunRoundAsync(fixture);
        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            after.Stage == JourneyRuntimeStage.AwaitingDepartureSafety,
            $"After the restart the vehicle did not move on to its departure check. stage={after.Stage} round={resumed}");

        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, SecondSafetyResultId);
        Assert.True(
            await fixture.Context.OrderIntents.AnyAsync(
                row => row.UpperId == extraUpperId, TestContext.Current.CancellationToken),
            "Leaving the unload stop after the restart authorised no movement order to the next stop.");
    }

    /// <summary>
    /// U3 前半：最后一个停靠卸完，崩在「已卸」保存之后、停靠完成那一次保存上。重启后旅程照常完成，收尾快照落库。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACrashAfterTheLastUnloadBeforeItsStopCompletesStillClosesTheJourney()
    {
        FailOnce crash = new(command =>
            command.CommandText.Contains("UPDATE \"JourneyStops\"", StringComparison.Ordinal) &&
            HasParameter(command, JourneyStopStatuses.Completed));
        await AssertTheLastUnloadResumesAfterACrashAsync(crash);
    }

    /// <summary>
    /// U3 后半：最后一个停靠卸完，崩在旅程写成 Completed 的那一次保存上（停靠完成之后）。重启后旅程照常完成，收尾快照落库。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACrashAfterTheLastStopCompletesBeforeTheJourneyDoesStillClosesTheJourney()
    {
        FailOnce crash = new(command =>
            command.CommandText.Contains("UPDATE \"JourneyRuntimes\"", StringComparison.Ordinal) &&
            HasParameter(command, nameof(JourneyRuntimeStage.Completed)));
        await AssertTheLastUnloadResumesAfterACrashAsync(crash);
    }

    private static async Task AssertTheLastUnloadResumesAfterACrashAsync(FailOnce crash)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: crash);
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Unload, SlotBusinessState.Empty);
        crash.Armed = true;
        await TickAndRunExpectingCrashAsync(fixture);
        Assert.True(crash.Fired, "The injected failure never fired, so this proved nothing.");

        await fixture.RecreateEngineAsync();
        Assert.Equal(JourneyDemandStatuses.Unloaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        Exception? resumed = await RunRoundAsync(fixture);
        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            after.Stage == JourneyRuntimeStage.Completed,
            $"After the restart the journey never closed. stage={after.Stage} round={resumed}");
        Assert.Null(after.BlockReasonCode);
        Assert.All(
            await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
                .Where(row => row.JourneyId == after.JourneyId)
                .ToArrayAsync(TestContext.Current.CancellationToken),
            stop => Assert.Equal(JourneyStopStatuses.Completed, stop.Status));
        string[] closureIds = [.. JourneyClosure.SnapshotMessageIds(after.JourneyId)];
        Assert.True(
            await fixture.Context.ProtocolOutbox.AsNoTracking()
                .AnyAsync(row => closureIds.Contains(row.MessageId), TestContext.Current.CancellationToken),
            "The journey closed without staging the closure snapshots the vehicle needs.");
    }

    /// <summary>跑一轮，把这一轮抛出的东西交回来而不是让用例在这里失败：判据是车有没有往前走，不是这一轮抛没抛。</summary>
    private static async Task<Exception?> RunRoundAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        return await Record.ExceptionAsync(() => fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>两条需求在同一个取货停靠装上车、同一个卸货停靠卸，车开到卸货站。停在等第一条的卸货结果。</summary>
    private static async Task LoadBothAndArriveAtTheUnloadStopAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow runtime = await TwoDemandsAtThePickupAsync(fixture);
        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));
        await TickAndRunAsync(fixture);
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        await EnterSublotAsync(fixture, FirstDemandId, SecondSublot, SecondSubmissionId);
        await ApplySafeResultAsync(fixture, SecondDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
    }

    /// <summary>到站时下了卸货命令的那一条在前，另一条在后。恰好一条已下命令。</summary>
    private static async Task<(string First, string Second)> UnloadOrderAsync(RuntimeFixture fixture)
    {
        bool firstCommanded = await UnloadCommandedAsync(fixture, FirstDemandId);
        Assert.NotEqual(firstCommanded, await UnloadCommandedAsync(fixture, SecondDemandId));
        return firstCommanded ? (FirstDemandId, SecondDemandId) : (SecondDemandId, FirstDemandId);
    }

    /// <summary>
    /// 受理一条需求，并在它的卸货停靠之后再挂一个卸货停靠（同 <c>Batch7ThreeStopJourneyTests.LeavingAnUnloadStopAuthorisesTheLegToTheNextStop</c>
    /// 的形状）。返回那个停靠的上位机单号。
    /// </summary>
    private static async Task<string> SingleDemandWithAnExtraUnloadStopAsync(RuntimeFixture fixture)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);

        string extraId = $"{runtime.JourneyId}|EXTRA-UNLOAD";
        fixture.Context.Set<JourneyStopRow>().Add(new JourneyStopRow
        {
            StopId = extraId,
            JourneyId = runtime.JourneyId,
            Sequence = 3,
            StopRole = JourneyStopRoles.Unload,
            StationId = runtime.GateStationId!,
            StationRiotId = runtime.GateStationRiotId,
            DispatchZone = runtime.DispatchZone,
            OperationSessionId = JourneyPlanBuilder.StableGuid(extraId, "session"),
            MovementLegId = JourneyPlanBuilder.StableGuid(extraId, "leg"),
            UpperId = $"W2G-{extraId}",
            VehicleBusinessMessageId = JourneyPlanBuilder.StableGuid(extraId, "vehicle-state"),
            WorklistMessageId = JourneyPlanBuilder.StableGuid(extraId, "worklist"),
            PlanMessageId = JourneyPlanBuilder.StableGuid(extraId, "plan"),
            Status = JourneyStopStatuses.Pending,
            CreatedAt = runtime.CreatedAt
        });
        await fixture.Context.SaveChangesAsync(token);
        // 夹具第一轮已经派过车，ArriveAtPickupAsync 那一轮只是多跑一轮，不碰这个停靠。
        return $"W2G-{extraId}";
    }

    private static async Task<bool> UnloadCommandedAsync(RuntimeFixture fixture, string demandId)
    {
        JourneyDemandRow membership = await MembershipAsync(fixture, demandId);
        return await fixture.Context.ProtocolOutbox.AsNoTracking()
            .AnyAsync(
                row => row.MessageType == "SlotOperationCommand" && row.MessageId == membership.UnloadCommandMessageId,
                TestContext.Current.CancellationToken);
    }

    private static Task<int> CountAsync(RuntimeFixture fixture, string messageType) =>
        fixture.Context.ProtocolOutbox.AsNoTracking()
            .CountAsync(row => row.MessageType == messageType, TestContext.Current.CancellationToken);

    private static bool HasParameter(DbCommand command, string value) =>
        command.Parameters.Cast<DbParameter>().Any(parameter => Equals(parameter.Value, value));

    /// <summary>与 <paramref name="matches"/> 相符的第一条命令抛一次，如同那一次保存写到那里时进程崩了。</summary>
    private sealed class FailOnce(Func<DbCommand, bool> matches) : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Check(DbCommand command)
        {
            if (!Armed || Fired || !matches(command))
            {
                return;
            }

            Fired = true;
            throw new InvalidOperationException("Injected failure at the crash point under test.");
        }
    }
}
