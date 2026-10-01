using System.Text.Json;
using ControlServer.Dashboard;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 看板读的空闲返回结论（批次8-21，control-server#392）：评估器每一轮是一轮评估（<see cref="IdleReturnVerdictBoard.BeginPass"/>／
/// <see cref="IdleReturnVerdictBoard.EndPass"/>），看板只读最近一轮已完成的那一份。这一轮没交到评估器的车——读 RIoT 抛了异常、这一轮
/// 预算用尽、已经在途——在看板上是「本轮没有评估」，不是它上一轮的码；开关关着、候选为空提前返回的那一轮也算一轮。
/// </summary>
/// <remarks>
/// 每条用例都先断言结论板的「最近一次」（<see cref="IdleReturnVerdictBoard.Reasons"/>）里还留着那辆车上一轮的码：旧值确实在，
/// 看板没显示它是因为轮次标记，不是因为根本没有旧值。
/// </remarks>
public sealed partial class IdleReturnExecutionTests
{
    /// <summary>
    /// 调度 09-30 的要求：被停止自动空闲返回的车，在看板上能看到车号与停止原因（码与中文说明，说明里写现场怎么处理）。走真实的两次取消。
    /// </summary>
    [Fact]
    public async Task AVehicleStoppedAfterRepeatedEndedOrdersShowsOnTheDashboardWithItsVehicleAndReason()
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        JourneyRuntimeRow first = await CommittedAndSentAsync(fleet);
        fleet.Riot.CancelOrder(first.PickupUpperId);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);
        await RoundAsync(fleet);
        JourneyRuntimeRow second = (await IdleJourneyAsync(fleet, AgvA))!;
        fleet.Riot.CancelOrder(second.PickupUpperId);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Equal(IdleReturnReasons.StoppedAfterRepeatedEndedOrders, fleet.IdleReturnBoard.Reasons[AgvA]);

        (JsonElement vehicle, string row) = await DashboardRowAsync(fleet, AgvA);

        JsonElement verdict = vehicle.GetProperty("verdict");
        Assert.True(verdict.GetProperty("evaluated").GetBoolean());
        Assert.Equal(IdleReturnReasons.StoppedAfterRepeatedEndedOrders, verdict.GetProperty("reasonCode").GetString());
        Assert.Contains(AgvA, row, StringComparison.Ordinal);
        Assert.Contains(
            $"{IdleReturnReasons.StoppedAfterRepeatedEndedOrders}：{IdleReturnCodeDescriptions.VerdictCodes[IdleReturnReasons.StoppedAfterRepeatedEndedOrders]}",
            row,
            StringComparison.Ordinal);
        Assert.Contains("请到现场与 RIoT 查明为什么反复取消", row, StringComparison.Ordinal);
    }

    /// <summary>
    /// 两辆车，这一轮只有 A 读 RIoT 抛异常：A 不是参与者，评估照常走完（B 被评估），A 在看板上是「本轮没有评估」，不显示它上一轮的码。
    /// 下一轮 RIoT 好了，A 又被评估。
    /// </summary>
    [Fact]
    public async Task AVehicleWhoseRiotReadFailsThisRoundReadsAsNotEvaluatedRatherThanItsPreviousVerdict()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2, points: []);
        await RoundAsync(fleet);
        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, PassVerdict(fleet, AgvA));

        fleet.Riot.FailOn = KeyA;
        await RoundAsync(fleet);

        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, fleet.IdleReturnBoard.Reasons[AgvA]);
        Assert.Null(PassVerdict(fleet, AgvA));
        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, PassVerdict(fleet, AgvB));
        await AssertNotEvaluatedOnDashboardAsync(fleet, AgvA, IdleReturnsQueryEndpoint.NotEvaluatedThisPass);

        fleet.Riot.FailOn = null;
        await RoundAsync(fleet);
        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, PassVerdict(fleet, AgvA));
    }

    /// <summary>这一轮读 RIoT 时用尽了自己的预算（挂住、1 秒后被截断）：同样不是参与者，看板写「本轮没有评估」。</summary>
    [Fact]
    public async Task AVehicleCutOffByItsRoundBudgetReadsAsNotEvaluatedRatherThanItsPreviousVerdict()
    {
        await using FleetFixture fleet = await FleetFixture.CreateAsync(
            configure: options =>
            {
                options.Fleet = options.Fleet[..2];
                options.Fleet[0].RoundTimeoutMilliseconds = 1000;
            },
            withRouteGraph: true);
        await fleet.ReplaceRouteGraphAsync(Edges(), Stations());
        await fleet.EnableIdleReturnAsync();
        fleet.Catalog.Set([]);
        await RoundAsync(fleet);
        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, PassVerdict(fleet, AgvA));

        fleet.Riot.HangOn = KeyA;
        await RoundAsync(fleet);

        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, fleet.IdleReturnBoard.Reasons[AgvA]);
        Assert.Null(PassVerdict(fleet, AgvA));
        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, PassVerdict(fleet, AgvB));
        await AssertNotEvaluatedOnDashboardAsync(fleet, AgvA, IdleReturnsQueryEndpoint.NotEvaluatedThisPass);
    }

    /// <summary>
    /// 单车在途：车一出发就不空闲了，全车队没有空闲车，引擎在派车之前退出，评估器不再被调用，最近一轮已完成的评估停在承诺那一轮
    /// （结论「已承诺」）。窗口内照写那一轮；过了 3 个轮询间隔，结论一格写评估没在跑，不留着「已承诺」。步骤（在途）与用途、等待点照常给。
    /// </summary>
    [Fact]
    public async Task ASingleVehicleUnderWayReadsAsNotEvaluatedOnceTheLastPassIsOutsideTheWindow()
    {
        await using FleetFixture fleet = await FleetAsync();
        await CommittedAndSentAsync(fleet);
        Assert.Equal(IdleReturnReasons.Committed, PassVerdict(fleet, AgvA));
        (JsonElement inWindow, _) = await DashboardRowAsync(fleet, AgvA);
        Assert.Equal(IdleReturnReasons.Committed, inWindow.GetProperty("verdict").GetProperty("reasonCode").GetString());

        await RoundAsync(fleet);
        TimeSpan window = fleet.Options.PollInterval * IdleReturnsQueryEndpoint.PassLivenessPollIntervals;
        fleet.Clock.Advance(window);

        Assert.Equal(IdleReturnReasons.Committed, PassVerdict(fleet, AgvA));
        (JsonElement vehicle, _) = await AssertNotEvaluatedOnDashboardAsync(fleet, AgvA, IdleReturnsQueryEndpoint.PassNotRunning(window));
        Assert.Equal(IdleReturnsQueryEndpoint.StepEnRoute, vehicle.GetProperty("step").GetString());
        Assert.Equal("IDLE_RETURN", vehicle.GetProperty("purpose").GetString());
        Assert.Equal(Near.StationId, vehicle.GetProperty("waitingPoint").GetProperty("stationId").GetInt32());
    }

    /// <summary>
    /// 两辆车都在空闲返回途中：全车队没有空闲车，派车轮不跑。过了窗口，两辆车的结论一格都写评估没在跑。
    /// </summary>
    [Fact]
    public async Task WhenNoVehicleInTheFleetIsIdleEveryVehicleReadsAsNotEvaluatedOnceTheWindowPasses()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2, points: [Near, Far]);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.NotNull(await IdleJourneyAsync(fleet, AgvA));
        Assert.NotNull(await IdleJourneyAsync(fleet, AgvB));
        long last = fleet.IdleReturnBoard.LatestCompletedPass!.Number;

        await RoundAsync(fleet);
        Assert.Equal(last, fleet.IdleReturnBoard.LatestCompletedPass!.Number);
        TimeSpan window = fleet.Options.PollInterval * IdleReturnsQueryEndpoint.PassLivenessPollIntervals;
        fleet.Clock.Advance(window);

        foreach (string agvId in new[] { AgvA, AgvB })
        {
            await AssertNotEvaluatedOnDashboardAsync(fleet, agvId, IdleReturnsQueryEndpoint.PassNotRunning(window));
        }
    }

    /// <summary>
    /// 候选为空提前返回的那一轮也算一轮：只有一辆车、它读 RIoT 抛异常，评估器收到空的候选，照样完成一轮，那辆车不在其中。
    /// </summary>
    [Fact]
    public async Task APassWithNoCandidatesStillCompletesAndLeavesTheVehicleNotEvaluated()
    {
        await using FleetFixture fleet = await FleetAsync(points: []);
        await RoundAsync(fleet);
        long before = fleet.IdleReturnBoard.LatestCompletedPass!.Number;
        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, PassVerdict(fleet, AgvA));

        fleet.Riot.FailOn = KeyA;
        await RoundAsync(fleet);

        IdleReturnBoardPass pass = fleet.IdleReturnBoard.LatestCompletedPass!;
        Assert.True(pass.Number > before);
        Assert.Empty(pass.Verdicts);
        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, fleet.IdleReturnBoard.Reasons[AgvA]);
        await AssertNotEvaluatedOnDashboardAsync(fleet, AgvA, IdleReturnsQueryEndpoint.NotEvaluatedThisPass);
    }

    /// <summary>
    /// 开关关着提前返回的那一轮也算一轮：每辆空闲车的结论是开关关着，记在这一轮里；之后它读 RIoT 抛异常，下一轮它就不在其中。
    /// </summary>
    [Fact]
    public async Task APassWithIdleReturnDisabledStillCompletesWithEveryCandidateInIt()
    {
        await using FleetFixture fleet = await FleetFixture.CreateAsync(configure: options => options.Fleet = options.Fleet[..2]);
        fleet.Catalog.Set([]);
        await RoundAsync(fleet);

        IdleReturnBoardPass pass = fleet.IdleReturnBoard.LatestCompletedPass!;
        Assert.Equal(IdleReturnReasons.Disabled, pass.Verdicts[AgvA].Reason);
        Assert.Equal(IdleReturnReasons.Disabled, pass.Verdicts[AgvB].Reason);
        (JsonElement vehicle, _) = await DashboardRowAsync(fleet, AgvA);
        Assert.Equal(IdleReturnReasons.Disabled, vehicle.GetProperty("verdict").GetProperty("reasonCode").GetString());

        fleet.Riot.FailOn = KeyA;
        await RoundAsync(fleet);

        Assert.True(fleet.IdleReturnBoard.LatestCompletedPass!.Number > pass.Number);
        Assert.Null(PassVerdict(fleet, AgvA));
        Assert.Equal(IdleReturnReasons.Disabled, PassVerdict(fleet, AgvB));
        await AssertNotEvaluatedOnDashboardAsync(fleet, AgvA, IdleReturnsQueryEndpoint.NotEvaluatedThisPass);
    }

    private static string? PassVerdict(FleetFixture fleet, string agvId) =>
        fleet.IdleReturnBoard.LatestCompletedPass?.Verdicts.GetValueOrDefault(agvId)?.Reason;

    private static async Task<(JsonElement Vehicle, string Row)> AssertNotEvaluatedOnDashboardAsync(
        FleetFixture fleet, string agvId, string note)
    {
        (JsonElement vehicle, string row) = await DashboardRowAsync(fleet, agvId);
        JsonElement verdict = vehicle.GetProperty("verdict");
        Assert.False(verdict.GetProperty("evaluated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, verdict.GetProperty("reasonCode").ValueKind);
        Assert.Equal(note, verdict.GetProperty("note").GetString());
        Assert.Contains(note, row, StringComparison.Ordinal);
        // The previous verdict is still on the board (asserted by each caller); it must not reach the card.
        Assert.DoesNotContain(fleet.IdleReturnBoard.Reasons[agvId], row, StringComparison.Ordinal);
        return (vehicle, row);
    }

    /// <summary>按宿主的样子读空闲返回数据面：车队夹具的名册与结论板、它的时钟（会话存活按它判）。</summary>
    private static async Task<(JsonElement Vehicle, string Row)> DashboardRowAsync(FleetFixture fleet, string agvId)
    {
        await fleet.HearFromEveryVehicleAsync();
        fleet.Context.ChangeTracker.Clear();
        IdleReturnsQueryEndpoint endpoint = new(Options.Create(fleet.Options), fleet.IdleReturnBoard, fleet.Clock);
        object rows = await endpoint.ReadAsync(fleet.Context, Token);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(rows));
        JsonElement vehicle = document.RootElement.GetProperty("vehicles").EnumerateArray()
            .Single(item => item.GetProperty("agvId").GetString() == agvId).Clone();
        string html = new IdleReturnCard().RenderFact(document.RootElement);
        int start = html.IndexOf("<tr><td>" + agvId + "</td>", StringComparison.Ordinal);
        Assert.True(start >= 0, "No dashboard row for " + agvId + ".");
        return (vehicle, html[start..html.IndexOf("</tr>", start, StringComparison.Ordinal)]);
    }
}
