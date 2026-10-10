using System.Net;
using System.Text;
using ControlServer.Application;
using ControlServer.Infrastructure.Adapters;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace ControlServer.Tests;

/// <summary>
/// control-server#573：给车载端的 vehicle-safety 投影在全厂非终态单分页读不全时，同一次请求里当场重读。
/// </summary>
/// <remarks>
/// <para>
/// 现场 cs#566（2026-10-10，agv02）：全厂约 250 张非终态单、3 页，订单在变，cs#525 偶尔判一次读不全，投影回一次
/// <c>RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN</c>，车载端闪一次未就绪，两端都把 5 分钟离站等待重新计满，车在站 15 走不了。
/// </para>
/// <para>
/// 修法（调度定的 C）：只对这条投影，读不全就在同一次请求里再读，次数可配（默认再读 2 次，0～4），每一遍都单独按 cs#525 判、
/// 不拼页；都读不全就是今天的答案。不交出任何旧证据。第一版的「沿用上一次读全的清单」在慢装置上两头不讨好，已撤掉（PR 里单列）。
/// </para>
/// </remarks>
public sealed class OnboardSafetyListingRereadTests
{
    private const string Vehicle = "VEHICLE-KEY-01";
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 11, 20, 0, TimeSpan.Zero);
    private static readonly int[] PagesOfAReadThatStopsAtPageTwo = [1, 2];

    /// <summary>取红用例：第一遍读到一半总数变了、第二遍读全——修复前回 <c>COVERAGE_UNKNOWN</c>，车载端闪一次未就绪。</summary>
    [Fact]
    public async Task AReadThatDoesNotAddUpIsReadAgainAndTheReadThatAddsUpAnswers()
    {
        await using Rig rig = new();
        rig.Orders.FaultOnlyRead(1);

        RiotVehicleSafetyObservation result = await rig.OnboardAsync(rereads: 2);

        Assert.Equal(RiotVehicleMotionState.Stopped, result.MotionState);
        Assert.Empty(result.ReasonCodes);
        Assert.Equal(T0, result.ObservedAt);
        Assert.Equal(2, rig.Orders.Reads);
    }

    /// <summary>每一遍都不全时，一次请求恰好读 1 + 重读次数遍（RIoT 与 MVP 共用，多一遍都要算账），然后回今天的答案。</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 3)]
    [InlineData(4, 5)]
    public async Task OneOnboardRequestReadsTheListingOncePlusTheConfiguredRereads(int rereads, int reads)
    {
        await using Rig rig = new();
        rig.Orders.FaultEveryReadFrom(1);

        RiotVehicleSafetyObservation result = await rig.OnboardAsync(rereads);

        AssertCoverageUnknown(result);
        Assert.Equal(reads, rig.Orders.Reads);
        Assert.Equal([.. Enumerable.Repeat(PagesOfAReadThatStopsAtPageTwo, reads).SelectMany(pages => pages)], rig.Orders.PagesAsked);
        Assert.Equal(1, rig.VehicleReads);
    }

    /// <summary>重读读全之后照常判：读全的那一遍里有本车的单，就是有单——用的是那一遍的记录，不是前一遍读到的半截。</summary>
    [Fact]
    public async Task TheReadThatAddsUpIsJudgedInFull()
    {
        await using Rig rig = new();
        rig.Orders.Records.Insert(230, OrderRecordJson(9001, "ORDER-ON-VEHICLE", 3, Vehicle, Vehicle));
        rig.Orders.Records.RemoveAt(rig.Orders.Records.Count - 1);
        rig.Orders.FaultOnlyRead(1);

        RiotVehicleSafetyObservation result = await rig.OnboardAsync(rereads: 2);

        Assert.Equal(RiotVehicleMotionState.Unknown, result.MotionState);
        Assert.Equal(["RIOT_NONFINAL_ORDER_PRESENT"], result.ReasonCodes);
        Assert.Equal([1, 2, 1, 2, 3], rig.Orders.PagesAsked);
    }

    /// <summary>车辆自身的状态与读全的那一遍一起判：在动就是在动。</summary>
    [Fact]
    public async Task AMovingVehicleIsMovingWhicheverReadAddsUp()
    {
        await using Rig rig = new();
        rig.VehicleJson = VehicleJson("MT_RUNNING", 0.4);
        rig.Orders.FaultOnlyRead(1);

        RiotVehicleSafetyObservation result = await rig.OnboardAsync(rereads: 2);

        Assert.Equal(RiotVehicleMotionState.Moving, result.MotionState);
        Assert.Equal(["RIOT_MOTION_ACTIVE"], result.ReasonCodes);
    }

    /// <summary>
    /// 引擎那几处走的是 <see cref="IRiotVehicleSafetyFacts.ReadVehicleSafetyAsync"/>，不重读：读不全照旧回 <c>COVERAGE_UNKNOWN</c>，只读一遍。
    /// </summary>
    [Fact]
    public async Task TheJourneyRuntimesSafetyReadDoesNotReread()
    {
        await using Rig rig = new();
        rig.Orders.FaultOnlyRead(1);

        AssertCoverageUnknown(await rig.Gateway.ReadVehicleSafetyAsync(Vehicle, TestContext.Current.CancellationToken));
        Assert.Equal([1, 2], rig.Orders.PagesAsked);
    }

    /// <summary>
    /// 预算内读得完就照常重读：每页 100 ms、预算 2 秒，三遍都不全也读满三遍（真装置 run 38082575561 之后加了时间预算，这条钉住预算没把重读吃掉）。
    /// </summary>
    [Fact]
    public async Task WithinTheBudgetTheRereadsAllHappen()
    {
        await using Rig rig = new() { PageCost = TimeSpan.FromMilliseconds(100) };
        rig.Orders.FaultEveryReadFrom(1);

        AssertCoverageUnknown(await rig.OnboardAsync(rereads: 2, budget: TimeSpan.FromSeconds(2)));
        Assert.Equal(3, rig.Orders.Reads);
    }

    /// <summary>
    /// 剩下的时间不够再读一遍就不读，直接回未知：每页 400 ms，一遍读两页 800 ms；第一遍后 0.8 + 0.8 ≤ 2 秒再读，
    /// 第二遍后 1.6 + 0.8 > 2 秒停下。真装置 run 38082575561 第 3 遍就是重读拖过了车载端的 3 秒。
    /// </summary>
    [Fact]
    public async Task NoRereadStartsThatTheTimeLeftCannotCover()
    {
        await using Rig rig = new() { PageCost = TimeSpan.FromMilliseconds(400) };
        rig.Orders.FaultEveryReadFrom(1);

        AssertCoverageUnknown(await rig.OnboardAsync(rereads: 4, budget: TimeSpan.FromSeconds(2)));
        Assert.Equal([1, 2, 1, 2], rig.Orders.PagesAsked);
    }

    /// <summary>
    /// 预算到点时还卡着的那一遍读被取消，回未知，不抛、不回 500，服务端也不在车载端放弃之后接着读：读过一遍不全的清单就是
    /// <c>COVERAGE_UNKNOWN</c>，一遍清单都没读到就是 <c>RIOT_READ_TIMEOUT</c>。
    /// </summary>
    [Theory]
    [InlineData(true, "RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN")]
    [InlineData(false, "RIOT_READ_TIMEOUT")]
    public async Task AReadStillRunningWhenTheBudgetRunsOutIsCancelledAndAnswersUnknown(bool afterAnIncompleteRead, string reason)
    {
        await using Rig rig = new();
        rig.Orders.FaultOnlyRead(1);
        rig.HangOn = afterAnIncompleteRead ? request => request.Contains("pageNum=1", StringComparison.Ordinal) && rig.Orders.Reads >= 1
            : request => request.Contains("getVehicleInfo", StringComparison.Ordinal);

        RiotVehicleSafetyObservation result = await rig.OnboardAsync(rereads: 2, budget: TimeSpan.FromMilliseconds(300));

        Assert.Equal(RiotVehicleMotionState.Unknown, result.MotionState);
        Assert.Equal([reason], result.ReasonCodes);
        Assert.True(rig.HungCallWasCancelled);
    }

    /// <summary>车载端自己放弃（请求令牌被取消）照旧往外抛取消，由端点记成取消；不是预算到点，不能被当成一次读失败吞掉。</summary>
    [Fact]
    public async Task TheOnboardGivingUpIsACancellationNotAnAnswer()
    {
        await using Rig rig = new();
        using CancellationTokenSource onboard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        rig.HangOn = request => request.Contains("getVehicleInfo", StringComparison.Ordinal);
        onboard.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rig.Gateway.ReadForOnboardAsync(Vehicle, 2, TimeSpan.FromSeconds(30), onboard.Token));
    }

    private static void AssertCoverageUnknown(RiotVehicleSafetyObservation result)
    {
        Assert.Equal(RiotVehicleMotionState.Unknown, result.MotionState);
        Assert.Equal(["RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN"], result.ReasonCodes);
    }

    private static string OrderRecordJson(
        long id, string orderId, int orderState, string? appointVehicleKey, string? executeVehicleKey)
    {
        string appointed = appointVehicleKey is null ? "null" : $"\"{appointVehicleKey}\"";
        string execute = executeVehicleKey is null ? "null" : $"\"{executeVehicleKey}\"";
        return $$$"""
            {"id":{{{id}}},"orderId":"{{{orderId}}}","upperId":"UPPER-{{{id}}}","orderState":{{{orderState}}},"appointVehicleKey":{{{appointed}}},"executeVehicleKey":{{{execute}}}}
            """;
    }

    private static string VehicleJson(string movementState, double speed) => $$$"""
        {
          "vehicle":{"movementState":"{{{movementState}}}","controlState":"CONTROL_STATE_OK",
            "emergencyState":"OK","breakSwitchState":"MOVABLE",
            "locationState":"LOCATION_STATE_RUNNING","speed":{{{speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}},
          "vehicleTaskInfo":{"key":"VEHICLE-KEY-01","procState":"IDLE",
            "processingOrder":false,"enable":true,"integrationLevel":"ON_LINE"}
        }
        """;

    /// <summary>
    /// 全厂 252 张别的产线的非终态单（与 cs#525 的用例同形），按 pageNum 分页；被打中的那几次读，读到第 2 页时总数多一条——
    /// 就是现场那种「读到一半全厂多了一张单」。
    /// </summary>
    private sealed class PlantOrders
    {
        private Func<int, bool> faulted = _ => false;

        public List<string> Records { get; } =
        [
            .. Enumerable.Range(1, 252).Select(index => index % 20 == 0
                ? OrderRecordJson(index, $"ORDER-{index}", 3, "OTHER-VEHICLE", "OTHER-VEHICLE")
                : OrderRecordJson(index, $"ORDER-{index}", 8, null, "--")),
        ];

        public List<int> PagesAsked { get; } = [];
        public int Reads { get; private set; }

        public void FaultEveryReadFrom(int read) => faulted = current => current >= read;

        public void FaultOnlyRead(int read) => faulted = current => current == read;

        public string Page(int pageNum, int pageSize)
        {
            if (pageNum == 1) Reads++;
            PagesAsked.Add(pageNum);
            int total = Records.Count + (pageNum >= 2 && faulted(Reads) ? 1 : 0);
            string page = string.Join(",", Records.Skip((pageNum - 1) * pageSize).Take(pageSize));
            return $$$"""
                {"code":"0","result":{"current":{{{pageNum}}},"size":{{{pageSize}}},"total":{{{total}}},"records":[{{{page}}}]}}
                """;
        }
    }

    /// <summary>墙钟钉在 T0；计时戳由 <see cref="Advance"/> 手动推进（每次 RIoT 调用推进一次）；预算的定时器仍是真定时器。</summary>
    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long timestamp;

        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public void Advance(TimeSpan by) => Interlocked.Add(ref timestamp, by.Ticks);
    }

    private sealed class Rig : IAsyncDisposable
    {
        public Rig()
        {
            Session = new RiotSession(
                new RiotOptions { BaseUrl = "http://riot.test", CallApiKey = "test-call-api-key" },
                new Handler(this));
            Gateway = new HttpRiotMovementGateway(Session, Clock);
        }

        public MutableTimeProvider Clock { get; } = new(T0);
        public TimeSpan PageCost { get; init; }
        public Func<string, bool> HangOn { get; set; } = _ => false;
        public bool HungCallWasCancelled { get; private set; }

        public PlantOrders Orders { get; } = new();
        public RiotSession Session { get; }
        public HttpRiotMovementGateway Gateway { get; }
        public string VehicleJson { get; set; } = OnboardSafetyListingRereadTests.VehicleJson("MT_FINISHED", 0);
        public int VehicleReads { get; private set; }

        public Task<RiotVehicleSafetyObservation> OnboardAsync(int rereads, TimeSpan? budget = null) =>
            Gateway.ReadForOnboardAsync(Vehicle, rereads, budget ?? TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        public ValueTask DisposeAsync() => Session.DisposeAsync();

        private sealed class Handler(Rig rig) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (rig.HangOn(request.RequestUri!.PathAndQuery))
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    finally
                    {
                        rig.HungCallWasCancelled = cancellationToken.IsCancellationRequested;
                    }
                }
                string body;
                if (request.RequestUri?.AbsolutePath == $"/api/task/v1/task/getVehicleInfo/{Vehicle}")
                {
                    rig.VehicleReads++;
                    body = rig.VehicleJson;
                }
                else
                {
                    Assert.Equal("/api/order/v1/orderRecord", request.RequestUri?.AbsolutePath);
                    Dictionary<string, string> query = request.RequestUri!.Query.TrimStart('?').Split('&')
                        .Select(pair => pair.Split('=', 2))
                        .Where(pair => pair[0] is "pageNum" or "pageSize")
                        .ToDictionary(pair => pair[0], pair => pair[1]);
                    body = rig.Orders.Page(
                        int.Parse(query["pageNum"], System.Globalization.CultureInfo.InvariantCulture),
                        int.Parse(query["pageSize"], System.Globalization.CultureInfo.InvariantCulture));
                    rig.Clock.Advance(rig.PageCost);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }
        }
    }
}
