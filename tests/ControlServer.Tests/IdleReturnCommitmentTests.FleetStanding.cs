using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Tests;

/// <summary>
/// 选点看全车队谁停在点上（control-server#798）：#796 只看这一轮交来评估的车。外来订单占着的车、有旅程的车不交来评估，停在没有占用行的等待点上时，
/// 别的车照样被派过去，开到点前停下等放行、一直堵在路上。
/// </summary>
/// <remarks>
/// 路网同 <see cref="IdleReturnCommitmentTests"/>：210 → 214 → 215 单向链，216 到不了。被评估的车默认停在 210，最近的点是 214，其次 215。
/// <see cref="Harness.Others"/> 里的车不交来评估，读取器另问 RIoT 它在哪。
/// </remarks>
public sealed partial class IdleReturnCommitmentTests
{
    private const string VehicleC = "VK-C";

    private const string VehicleD = "VK-D";

    /// <summary>
    /// B 不交来评估（外来订单占着它、或它在跑一趟旅程），停在唯一登记的 214 上，点上没有占用行：A 不被派去 214，逐点原因写
    /// <see cref="IdleReturnReasons.PointStoodOn"/> 与挡点的车、位置从哪来。B 离线时 RIoT 仍报它最后停的站，同样算，来源写 <c>offline</c>。
    /// </summary>
    [Theory]
    [InlineData(true, StandingSources.Read)]
    [InlineData(false, StandingSources.Offline)]
    public async Task AVehicleNotHandedToTheEvaluationStandingOnTheOnlyPointKeepsOthersOffIt(bool connected, string source)
    {
        await using Harness harness = await Harness.CreateAsync();
        await harness.RegisterAsync(Point(214));
        harness.Others[VehicleB] = () => harness.Observed(VehicleB, 214, connected);

        IdleReturnVerdict verdict = Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA)));

        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, verdict.Reason);
        Assert.Equal(
            $"214={IdleReturnReasons.PointStoodOn}(AGV-{VehicleB},{source})",
            harness.VerdictBoard.LatestCompletedPass!.Verdicts["AGV-" + VehicleA].Detail);
        Assert.Null(await new StationExclusivityStore(harness.Db.NewContext()).ReadAsync(Map, 214, Token));
    }

    /// <summary>B 停在 212：不是等待点，与 214 同在路网节点 2、代价为 0。B 就站在 214 那个位置上，A 去下一个最近的 215。</summary>
    [Fact]
    public async Task AVehicleNotHandedToTheEvaluationOnAStationAtThePointsNodeKeepsOthersOffThePoint()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.ExtraPlacedStations.Add((212, "等待点1旁", 2));
        await harness.SeedRouteGraphAsync();
        harness.Others[VehicleB] = () => harness.Observed(VehicleB, 212);

        IdleReturnVerdict verdict = Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA)));

        Assert.Equal((IdleReturnReasons.Committed, (int?)215), (verdict.Reason, verdict.StationId));
    }

    /// <summary>
    /// 不误伤：车队里另一辆车停在不是等待点的 210、在两站之间（RIoT 报 0）、或在别的地图上，A 照常去最近的 214。
    /// </summary>
    [Theory]
    [InlineData(210, null)]
    [InlineData(0, null)]
    [InlineData(214, "MAP-OTHER")]
    public async Task AVehicleStandingOnNoPointOfThisMapKeepsNoOneOff(int station, string? map)
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.Others[VehicleB] = () => harness.Observed(VehicleB, station, map: map);

        IdleReturnVerdict verdict = Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA)));

        Assert.Equal((IdleReturnReasons.Committed, (int?)214), (verdict.Reason, verdict.StationId));
        Assert.Empty(harness.StandingLog.Entries);
    }

    /// <summary>
    /// 读不到位置的车按它上一次读到的站算（偏保守，票面第 2 条）：第一次评估读到 B 停在 214，A 去了 215；之后 RIoT 读不到 B，C 仍不被派去 214，
    /// 216 又到不了，C 没有点可去。逐点原因写 <c>last-known</c>，告警一次（事件 2339），再读不到不重复。
    /// </summary>
    /// <remarks>
    /// 两种读不到（审查 M1）：读取抛出；生产网关的形状——<c>HttpRiotMovementGateway</c> 对 SDK 失败不抛，答一辆离线、地图为空、没有当前站的车。
    /// </remarks>
    [Theory]
    [InlineData("throws")]
    [InlineData("gateway-unknown-vehicle")]
    public async Task AVehicleThatCannotBeReadCountsWhereItWasLastSeen(string unreadable)
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.Others[VehicleB] = () => harness.Observed(VehicleB, 214);
        Assert.Equal(
            (IdleReturnReasons.Committed, (int?)215),
            Of(Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA)))));

        harness.Others[VehicleB] = unreadable == "throws"
            ? () => throw new HttpRequestException("RIoT did not answer for B.")
            : () => harness.Observed(VehicleB, null, connected: false, map: "");
        IdleReturnVerdict verdict = Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleC)));
        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleD))).Reason);

        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, verdict.Reason);
        Assert.Contains(
            $"214={IdleReturnReasons.PointStoodOn}(AGV-{VehicleB},{StandingSources.LastKnown})",
            harness.VerdictBoard.LatestCompletedPass!.Verdicts["AGV-" + VehicleD].Detail,
            StringComparison.Ordinal);
        EventRecordingLogger<FleetStandingReader>.Entry warning = Assert.Single(harness.StandingLog.Entries);
        Assert.Equal((2339, Microsoft.Extensions.Logging.LogLevel.Warning), (warning.EventId.Id, warning.Level));
        Assert.Contains("station 214", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 一辆车的读取挂住（审查 M2）：整次读取有总预算（<see cref="FleetStandingReader.ReadBudget"/>，5 秒），不按单车预算（默认 30 秒）等它；
    /// 预算内没读到的车按上一次读到的站算，照样挡住 214，并告警。
    /// </summary>
    [Fact]
    public async Task AVehicleWhoseReadHangsIsCutOffByTheFleetBudgetAndCountsWhereItWasLastSeen()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.Others[VehicleB] = () => harness.Observed(VehicleB, 214);
        Assert.Equal(
            (IdleReturnReasons.Committed, (int?)215),
            Of(Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA)))));

        harness.Hanging.Add(VehicleB);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        IdleReturnVerdict verdict = Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleC)));
        watch.Stop();

        Assert.InRange(watch.Elapsed, FleetStandingReader.ReadBudget, TimeSpan.FromSeconds(20));
        Assert.Equal(IdleReturnReasons.NoWaitingPointAvailable, verdict.Reason);
        Assert.Contains(
            $"214={IdleReturnReasons.PointStoodOn}(AGV-{VehicleB},{StandingSources.LastKnown})",
            harness.VerdictBoard.LatestCompletedPass!.Verdicts["AGV-" + VehicleC].Detail,
            StringComparison.Ordinal);
        Assert.Contains("fleet read budget", Assert.Single(harness.StandingLog.Entries).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 从没读到过位置的车（服务端启动后 RIoT 一直读不到它）：选点不知道它在哪，不挡任何点，但不静默——告警一次（事件 2338），连着读不到不重复；
    /// 读到过一次之后再读不到，重新告警。没有合格点时逐点原因末尾写着它（<c>unlocated=</c>）。
    /// </summary>
    [Fact]
    public async Task AVehicleNeverLocatedIsWarnedAboutOnceAndBlocksNoPoint()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.Others[VehicleB] = () => throw new HttpRequestException("RIoT did not answer for B.");

        Assert.Equal(
            (IdleReturnReasons.Committed, (int?)214),
            Of(Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleA)))));
        Assert.Equal(
            (IdleReturnReasons.Committed, (int?)215),
            Of(Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleC)))));

        EventRecordingLogger<FleetStandingReader>.Entry warning = Assert.Single(harness.StandingLog.Entries);
        Assert.Equal((2338, Microsoft.Extensions.Logging.LogLevel.Warning), (warning.EventId.Id, warning.Level));
        Assert.Contains(VehicleB, warning.Message, StringComparison.Ordinal);

        // Read once standing nowhere (between stations), then lost again: a new streak, a new warning.
        harness.Others[VehicleB] = () => harness.Observed(VehicleB, 0);
        Assert.Equal(
            IdleReturnReasons.NoWaitingPointAvailable,
            Assert.Single(await harness.EvaluateAsync(harness.Candidate(VehicleD))).Reason);
        harness.Others[VehicleB] = () => throw new HttpRequestException("RIoT did not answer for B.");
        await harness.EvaluateAsync(harness.Candidate(VehicleD));

        Assert.Equal(2, harness.StandingLog.Entries.Count(entry => entry.EventId.Id == 2338));
        Assert.EndsWith(
            $"; unlocated=AGV-{VehicleB}",
            harness.VerdictBoard.LatestCompletedPass!.Verdicts["AGV-" + VehicleD].Detail,
            StringComparison.Ordinal);
    }

    private static (string Reason, int? StationId) Of(IdleReturnVerdict verdict) => (verdict.Reason, verdict.StationId);

    /// <summary>不交来评估的车的 RIoT 读数；不在 <see cref="Harness.Others"/> 里的车读不到，在 <see cref="Harness.Hanging"/> 里的挂住到被取消。</summary>
    private sealed class OtherVehicleFacts(
        IReadOnlyDictionary<string, Func<RiotVehicleObservation>> others, IReadOnlySet<string> hanging) : IRiotVehicleFacts
    {
        public async Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken)
        {
            if (hanging.Contains(vehicleKey))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return others.TryGetValue(vehicleKey, out Func<RiotVehicleObservation>? read)
                ? read()
                : throw new HttpRequestException($"The harness has no reading for {vehicleKey}.");
        }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Waiting point selection reads no order.");

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Waiting point selection creates no order.");
    }
}
