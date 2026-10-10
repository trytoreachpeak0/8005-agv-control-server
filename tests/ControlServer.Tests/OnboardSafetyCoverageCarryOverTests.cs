using System.Net;
using System.Text;
using ControlServer.Application;
using ControlServer.Infrastructure.Adapters;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace ControlServer.Tests;

/// <summary>
/// control-server#573：给车载端的 vehicle-safety 投影在全厂非终态单分页读不全时怎么回答。
/// </summary>
/// <remarks>
/// <para>
/// 现场 cs#566（2026-10-10，agv02）：全厂约 250 张非终态单、3 页，订单不停在变，每一两分钟就有一次读到一半总数变了，
/// cs#525 判读不全、投影回一次 <c>RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN</c>，车载端闪一次未就绪，两端都把 5 分钟离站等待重新计满，
/// 车在站 15 走不了。修法（调度批准的「甲′ + 丁」）：只对这条投影，读不全先当场重读，最多共 3 次；仍读不全时，
/// 「本车有没有单」沿用不超过 3 秒的上一次读全的清单，<c>observedAt</c> 填那次读全的开始时刻，车辆状态照常现读。
/// </para>
/// <para>
/// 沿用之前还要过两道，各有用例：读不全的那几页里出现本车的单，就是有单；本服务端自己的库里有一张 T₀ − 1 秒之后建的（或正在建的）
/// 本车的单，也是有单——本服务端的单不靠 RIoT 清单来排除。读本库失败或超时，回今天的答案。
/// </para>
/// </remarks>
public sealed class OnboardSafetyCoverageCarryOverTests
{
    private const string Vehicle = "VEHICLE-KEY-01";
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 11, 20, 0, TimeSpan.Zero);
    private static readonly TimeSpan CarryOver = TimeSpan.FromSeconds(3);

    /// <summary>取红用例：上一次读全、本车无单；这一次三遍都读不全。修复前回 <c>COVERAGE_UNKNOWN</c>，车载端闪一次未就绪。</summary>
    [Fact]
    public async Task AnIncompleteReadWithinTheCarryOverAnswersFromTheLastCompleteListing()
    {
        await using Rig rig = new();
        Assert.Equal(RiotVehicleMotionState.Stopped, (await rig.OnboardAsync()).MotionState);

        rig.Clock.Set(T0.AddSeconds(1));
        rig.Orders.FaultEveryReadFrom(rig.Orders.Reads + 1);
        RiotVehicleSafetyObservation carried = await rig.OnboardAsync();

        Assert.Equal(RiotVehicleMotionState.Stopped, carried.MotionState);
        Assert.Empty(carried.ReasonCodes);
        Assert.Equal(T0, carried.ObservedAt);
    }

    [Theory]
    [InlineData(3_000, true)]
    [InlineData(3_001, false)]
    public async Task TheCarryOverEndsAtItsLimit(int ageMs, bool carried)
    {
        await using Rig rig = new();
        await rig.OnboardAsync();

        rig.Clock.Set(T0.AddMilliseconds(ageMs));
        rig.Orders.FaultEveryReadFrom(rig.Orders.Reads + 1);
        RiotVehicleSafetyObservation result = await rig.OnboardAsync();

        if (carried)
        {
            Assert.Equal(RiotVehicleMotionState.Stopped, result.MotionState);
        }
        else
        {
            AssertCoverageUnknown(result);
        }
    }

    /// <summary>0 是关闭，不是「0 毫秒以内可沿用」：同一时刻再读也不沿用（时钟不走，免得年龄判断替它挡住）。</summary>
    [Fact]
    public async Task ACarryOverOfZeroIsTodaysAnswer()
    {
        await using Rig rig = new();
        await rig.OnboardAsync();

        rig.Orders.FaultEveryReadFrom(rig.Orders.Reads + 1);

        AssertCoverageUnknown(await rig.OnboardAsync(TimeSpan.Zero));
        Assert.Equal(0, rig.Ledger.Calls);
    }

    [Fact]
    public async Task WithoutACompleteListingYetTheAnswerIsTodays()
    {
        await using Rig rig = new();
        rig.Orders.FaultEveryReadFrom(1);

        AssertCoverageUnknown(await rig.OnboardAsync());
        Assert.Equal(0, rig.Ledger.Calls);
    }

    /// <summary>读不全的那几页里出现了本车的单：读到的每一条都是 RIoT 那一刻真有的非终态单，读不全不妨碍它说「有单」。</summary>
    [Fact]
    public async Task AnOrderOnTheVehicleSeenOnAPageThatDidNotAddUpIsAnOrder()
    {
        await using Rig rig = new();
        await rig.OnboardAsync();

        rig.Clock.Set(T0.AddSeconds(1));
        rig.Orders.Records.Insert(10, OrderRecordJson(9001, "ORDER-NEW-ON-VEHICLE", 1, Vehicle, "--"));
        rig.Orders.Records.RemoveAt(rig.Orders.Records.Count - 1);
        rig.Orders.FaultEveryReadFrom(rig.Orders.Reads + 1);
        RiotVehicleSafetyObservation result = await rig.OnboardAsync();

        Assert.Equal(RiotVehicleMotionState.Unknown, result.MotionState);
        Assert.Equal(["RIOT_NONFINAL_ORDER_PRESENT"], result.ReasonCodes);
    }

    /// <summary>上一次读全时本车有单；这一次读不全、没读到它——沿用的清单仍说有单，绝不能回 STOPPED。</summary>
    [Fact]
    public async Task AnOrderOnTheVehicleInTheLastCompleteListingIsNeverAnsweredStopped()
    {
        await using Rig rig = new();
        rig.Orders.Records.Insert(230, OrderRecordJson(9001, "ORDER-ON-VEHICLE", 3, Vehicle, Vehicle));
        rig.Orders.Records.RemoveAt(rig.Orders.Records.Count - 1);
        Assert.Equal(RiotVehicleMotionState.Unknown, (await rig.OnboardAsync()).MotionState);

        rig.Clock.Set(T0.AddSeconds(1));
        rig.Orders.FaultEveryReadFrom(rig.Orders.Reads + 1);
        RiotVehicleSafetyObservation result = await rig.OnboardAsync();

        Assert.NotEqual(RiotVehicleMotionState.Stopped, result.MotionState);
        Assert.Equal(["RIOT_NONFINAL_ORDER_PRESENT"], result.ReasonCodes);
    }

    /// <summary>
    /// 本服务端在 T₀ 之后给本车建了单，这一次读不全、没读到它：本库说有，就是有单。问本库的时刻是 T₀ − 1 秒。
    /// </summary>
    [Fact]
    public async Task AnOrderThisServerCreatedSinceTheListingIsAnOrder()
    {
        await using Rig rig = new();
        await rig.OnboardAsync();

        rig.Clock.Set(T0.AddSeconds(2));
        rig.Orders.FaultEveryReadFrom(rig.Orders.Reads + 1);
        rig.Ledger.Answer = true;
        RiotVehicleSafetyObservation result = await rig.OnboardAsync();

        Assert.Equal(RiotVehicleMotionState.Unknown, result.MotionState);
        Assert.Equal(["RIOT_NONFINAL_ORDER_PRESENT"], result.ReasonCodes);
        Assert.Equal((Vehicle, T0.AddSeconds(-1)), (rig.Ledger.LastVehicleKey, rig.Ledger.LastSince));
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("hangs")]
    public async Task ALedgerThatCannotAnswerLeavesTodaysAnswer(string failure)
    {
        await using Rig rig = new();
        await rig.OnboardAsync();

        rig.Clock.Set(T0.AddSeconds(1));
        rig.Orders.FaultEveryReadFrom(rig.Orders.Reads + 1);
        rig.Ledger.Failure = failure;

        AssertCoverageUnknown(await rig.OnboardAsync());
        Assert.Equal(1, rig.Ledger.Calls);
    }

    /// <summary>沿用的只是「有没有单」那一项；车辆自身的状态每次现读，在动就是在动，有原因码就带上。</summary>
    [Theory]
    [InlineData("moving")]
    [InlineData("emergency")]
    public async Task TheVehicleItselfIsAlwaysReadFresh(string state)
    {
        await using Rig rig = new();
        await rig.OnboardAsync();

        rig.Clock.Set(T0.AddSeconds(1));
        rig.Orders.FaultEveryReadFrom(rig.Orders.Reads + 1);
        rig.VehicleJson = state == "moving"
            ? VehicleJson("MT_RUNNING", 0.4, "OK")
            : VehicleJson("MT_FINISHED", 0, "EMERGENCY_STOP");
        RiotVehicleSafetyObservation result = await rig.OnboardAsync();

        if (state == "moving")
        {
            Assert.Equal(RiotVehicleMotionState.Moving, result.MotionState);
            Assert.Equal(["RIOT_MOTION_ACTIVE"], result.ReasonCodes);
        }
        else
        {
            Assert.Equal(RiotVehicleMotionState.Unknown, result.MotionState);
            Assert.Equal(["RIOT_EMERGENCY_NOT_OK"], result.ReasonCodes);
        }
    }

    /// <summary>丁：第一次读不全、第二次读全——同一次请求就回读全的那份，时刻是现在，不需要任何记忆。</summary>
    [Fact]
    public async Task AReadThatAddsUpOnASecondAttemptIsAnsweredAsOfNow()
    {
        await using Rig rig = new();
        rig.Orders.FaultOnlyRead(1);

        RiotVehicleSafetyObservation result = await rig.OnboardAsync();

        Assert.Equal(RiotVehicleMotionState.Stopped, result.MotionState);
        Assert.Equal(T0, result.ObservedAt);
        Assert.Equal(2, rig.Orders.Reads);
    }

    /// <summary>
    /// 丁的上限：一次请求最多读 3 遍清单。这里每遍都在第 2 页发现总数变了，所以页序是 1、2 重复三次；RIoT 是与 MVP 共用的，多一遍都要算账。
    /// </summary>
    [Fact]
    public async Task OneOnboardRequestReadsTheListingAtMostThreeTimes()
    {
        await using Rig rig = new();
        rig.Orders.FaultEveryReadFrom(1);

        await rig.OnboardAsync();

        Assert.Equal([1, 2, 1, 2, 1, 2], rig.Orders.PagesAsked);
        Assert.Equal(1, rig.VehicleReads);
    }

    /// <summary>
    /// 引擎那几处走的是 <see cref="IRiotVehicleSafetyFacts.ReadVehicleSafetyAsync"/>：它们不按 <c>observedAt</c> 判时效，所以既不沿用也不重读，
    /// 读不全照旧回 <c>COVERAGE_UNKNOWN</c>——哪怕记忆里有一份刚读全的清单。
    /// </summary>
    [Fact]
    public async Task TheJourneyRuntimesSafetyReadNeitherCarriesOverNorRetries()
    {
        await using Rig rig = new();
        Assert.Equal(RiotVehicleMotionState.Stopped, (await rig.StrictAsync()).MotionState);

        rig.Clock.Set(T0.AddSeconds(1));
        rig.Orders.PagesAsked.Clear();
        rig.Orders.FaultEveryReadFrom(rig.Orders.Reads + 1);

        AssertCoverageUnknown(await rig.StrictAsync());
        Assert.Equal([1, 2], rig.Orders.PagesAsked);
        Assert.Equal(0, rig.Ledger.Calls);
    }

    /// <summary>任何调用方读全的清单都记下来，投影可以沿用——记忆在单例里，不在 scoped 的网关上。</summary>
    [Theory]
    [InlineData("safety")]
    [InlineData("by-vehicle")]
    [InlineData("listing")]
    public async Task ACompleteListingAnyCallerReadsIsRemembered(string read)
    {
        await using Rig rig = new();
        HttpRiotMovementGateway another = new(rig.Session, rig.Clock, rig.Memory);
        switch (read)
        {
            case "safety": await another.ReadVehicleSafetyAsync(Vehicle, TestContext.Current.CancellationToken); break;
            case "by-vehicle": await another.ReadUnfinishedOrdersAsync(Vehicle, TestContext.Current.CancellationToken); break;
            default: await another.ListUnfinishedOrdersAsync(TestContext.Current.CancellationToken); break;
        }

        rig.Clock.Set(T0.AddSeconds(1));
        rig.Orders.FaultEveryReadFrom(rig.Orders.Reads + 1);
        RiotVehicleSafetyObservation result = await rig.OnboardAsync();

        Assert.Equal(RiotVehicleMotionState.Stopped, result.MotionState);
        Assert.Equal(T0, result.ObservedAt);
    }

    /// <summary>记忆只往前走：一次开始得早、结束得晚的读全，不能把更新的那份换回去。</summary>
    [Fact]
    public void TheMemoryNeverMovesBackInTime()
    {
        NonFinalOrderCoverageMemory memory = new();
        OrderStateRecord[] newer = [new OrderStateRecord(2, null, null, null, null, null)];
        OrderStateRecord[] older = [new OrderStateRecord(1, null, null, null, null, null)];

        memory.Remember(T0.AddSeconds(2), newer);
        memory.Remember(T0.AddSeconds(1), older);

        RememberedNonFinalOrders? recalled = memory.Recall();
        Assert.NotNull(recalled);
        Assert.Equal(T0.AddSeconds(2), recalled.ReadStartedAt);
        Assert.Same(newer, recalled.Records);
    }

    /// <summary>沿用的时刻是那次读全的<b>开始</b>时刻：读的过程中时钟走了，也按开始算，偏保守。</summary>
    [Fact]
    public async Task TheCarriedObservationIsStampedWithTheStartOfTheCompleteRead()
    {
        await using Rig rig = new();
        rig.Orders.OnPage = page => { if (page == 3) rig.Clock.Set(T0.AddMilliseconds(400)); };
        await rig.OnboardAsync();
        rig.Orders.OnPage = null;

        rig.Clock.Set(T0.AddSeconds(1));
        rig.Orders.FaultEveryReadFrom(rig.Orders.Reads + 1);
        RiotVehicleSafetyObservation result = await rig.OnboardAsync();

        Assert.Equal(T0, result.ObservedAt);
        Assert.Equal(T0.AddSeconds(-1), rig.Ledger.LastSince);
    }

    // ---- 本库那一道（OwnOrderCreationLedger） ----

    [Fact]
    public async Task AnIntentCreatedAtOrAfterSinceMayHaveCreated()
    {
        await using LedgerRig rig = await LedgerRig.CreateAsync();
        await rig.AddAsync("LEG-1", Vehicle, createdAt: T0);

        Assert.True(await rig.AskAsync(T0));
        Assert.False(await rig.AskAsync(T0.AddTicks(1)));
    }

    [Theory]
    [InlineData("armed")]
    [InlineData("outcome")]
    public async Task ACreateArmedOrAnsweredAfterSinceMayHaveCreated(string field)
    {
        await using LedgerRig rig = await LedgerRig.CreateAsync();
        await rig.AddAsync(
            "LEG-1",
            Vehicle,
            createdAt: T0.AddHours(-1),
            armedAt: field == "armed" ? T0.AddSeconds(1) : T0.AddHours(-1),
            outcomeAt: field == "outcome" ? T0.AddSeconds(1) : T0.AddHours(-1),
            outcome: "Accepted");

        Assert.True(await rig.AskAsync(T0));
    }

    [Fact]
    public async Task OnlyOlderIntentsOrOtherVehiclesIntentsHaveNotCreated()
    {
        await using LedgerRig rig = await LedgerRig.CreateAsync();
        await rig.AddAsync("LEG-1", Vehicle, createdAt: T0.AddMinutes(-10), armedAt: T0.AddMinutes(-10),
            outcomeAt: T0.AddMinutes(-9), outcome: "Accepted");
        await rig.AddAsync("LEG-2", "OTHER-VEHICLE", createdAt: T0.AddSeconds(1), armedAt: T0.AddSeconds(1),
            outcomeAt: T0.AddSeconds(1), outcome: "CreateRequestStarted");

        Assert.False(await rig.AskAsync(T0));
    }

    /// <summary>三个时刻都没登记的行（建单还没盖时刻）当「刚建」：偏保守。</summary>
    [Fact]
    public async Task AnIntentWithNoTimeAtAllCountsAsJustCreated()
    {
        await using LedgerRig rig = await LedgerRig.CreateAsync();
        await rig.AddAsync("LEG-1", Vehicle, createdAt: default);

        Assert.True(await rig.AskAsync(T0));
    }

    /// <summary>
    /// 建单请求在 since 之前很久就发出、还没回（<c>CreateRequestStarted</c>）：它可能在 T₀ 之后才落到 RIoT，没在那份读全的清单里。
    /// 只认 5 分钟内的——更老的是崩溃留下的残迹，否则这辆车会永远沿用不了。
    /// </summary>
    [Theory]
    [InlineData(-10, true)]
    [InlineData(-299, true)]
    [InlineData(-301, false)]
    public async Task ACreateRequestStillInFlightMayHaveCreated(int startedSecondsBeforeNow, bool expected)
    {
        await using LedgerRig rig = await LedgerRig.CreateAsync();
        DateTimeOffset now = T0.AddSeconds(1);
        rig.Clock.Set(now);
        DateTimeOffset started = now.AddSeconds(startedSecondsBeforeNow);
        await rig.AddAsync("LEG-1", Vehicle, createdAt: started.AddSeconds(-1), armedAt: started,
            outcomeAt: started, outcome: "CreateRequestStarted");

        Assert.Equal(expected, await rig.AskAsync(T0));
    }

    /// <summary>
    /// 时间比较在客户端做（记忆 sqlite-no-datetimeoffset-order-by）：SQLite 里 DateTimeOffset 是带偏移的文本，
    /// 按字符串比 <c>11:40+00:00</c> 小于 <c>19:30+08:00</c>，而实际上前者晚 10 分钟。
    /// </summary>
    [Fact]
    public async Task TimesAreComparedAsInstantsNotAsStoredText()
    {
        await using LedgerRig rig = await LedgerRig.CreateAsync();
        await rig.AddAsync("LEG-1", Vehicle, createdAt: new DateTimeOffset(2026, 10, 10, 11, 40, 0, TimeSpan.Zero));

        Assert.True(await rig.AskAsync(new DateTimeOffset(2026, 10, 10, 19, 30, 0, TimeSpan.FromHours(8))));
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

    private static string VehicleJson(string movementState, double speed, string emergencyState) => $$$"""
        {
          "vehicle":{"movementState":"{{{movementState}}}","controlState":"CONTROL_STATE_OK",
            "emergencyState":"{{{emergencyState}}}","breakSwitchState":"MOVABLE",
            "locationState":"LOCATION_STATE_RUNNING","speed":{{{speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}},
          "vehicleTaskInfo":{"key":"VEHICLE-KEY-01","procState":"IDLE",
            "processingOrder":false,"enable":true,"integrationLevel":"ON_LINE"}
        }
        """;

    /// <summary>
    /// 全厂 252 张别的产线的非终态单（与 cs#525 的用例同形），按 pageNum 分页；从第 N 次读起，每次读到第 2 页时总数多一条——
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
        public Action<int>? OnPage { get; set; }

        public void FaultEveryReadFrom(int read) => faulted = current => current >= read;

        public void FaultOnlyRead(int read) => faulted = current => current == read;

        public string Page(int pageNum, int pageSize)
        {
            if (pageNum == 1) Reads++;
            PagesAsked.Add(pageNum);
            OnPage?.Invoke(pageNum);
            int total = Records.Count + (pageNum >= 2 && faulted(Reads) ? 1 : 0);
            string page = string.Join(",", Records.Skip((pageNum - 1) * pageSize).Take(pageSize));
            return $$$"""
                {"code":"0","result":{"current":{{{pageNum}}},"size":{{{pageSize}}},"total":{{{total}}},"records":[{{{page}}}]}}
                """;
        }
    }

    private sealed class FakeLedger : IOwnOrderCreationLedger
    {
        public bool Answer { get; set; }
        public string? Failure { get; set; }
        public int Calls { get; private set; }
        public string? LastVehicleKey { get; private set; }
        public DateTimeOffset? LastSince { get; private set; }

        public async Task<bool> MayHaveCreatedSinceAsync(
            string vehicleKey, DateTimeOffset since, CancellationToken cancellationToken)
        {
            Calls++;
            LastVehicleKey = vehicleKey;
            LastSince = since;
            switch (Failure)
            {
                case "throws": throw new InvalidOperationException("database is locked");
                case "hangs": await Task.Delay(Timeout.Infinite, cancellationToken); break;
            }
            return Answer;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public void Set(DateTimeOffset value) => current = value;

        public override DateTimeOffset GetUtcNow() => current;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public Rig()
        {
            Session = new RiotSession(
                new RiotOptions { BaseUrl = "http://riot.test", CallApiKey = "test-call-api-key" },
                new Handler(this));
            Gateway = new HttpRiotMovementGateway(Session, Clock, Memory);
        }

        public MutableTimeProvider Clock { get; } = new(T0);
        public NonFinalOrderCoverageMemory Memory { get; } = new();
        public PlantOrders Orders { get; } = new();
        public FakeLedger Ledger { get; } = new();
        public RiotSession Session { get; }
        public HttpRiotMovementGateway Gateway { get; }
        public string VehicleJson { get; set; } = OnboardSafetyCoverageCarryOverTests.VehicleJson("MT_FINISHED", 0, "OK");
        public int VehicleReads { get; private set; }

        public Task<RiotVehicleSafetyObservation> OnboardAsync(TimeSpan? carryOver = null) =>
            Gateway.ReadForOnboardAsync(Vehicle, carryOver ?? CarryOver, Ledger, TestContext.Current.CancellationToken);

        public Task<RiotVehicleSafetyObservation> StrictAsync() =>
            Gateway.ReadVehicleSafetyAsync(Vehicle, TestContext.Current.CancellationToken);

        public ValueTask DisposeAsync() => Session.DisposeAsync();

        private sealed class Handler(Rig rig) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
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
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            }
        }
    }

    private sealed class LedgerRig : IAsyncDisposable
    {
        private LedgerRig(SqliteConnection connection, ControlServerDbContext context)
        {
            Connection = connection;
            Context = context;
        }

        private SqliteConnection Connection { get; }
        private ControlServerDbContext Context { get; }
        public MutableTimeProvider Clock { get; } = new(T0.AddSeconds(1));

        public static async Task<LedgerRig> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new LedgerRig(connection, context);
        }

        public async Task AddAsync(
            string legId,
            string vehicleKey,
            DateTimeOffset createdAt,
            DateTimeOffset? armedAt = null,
            DateTimeOffset? outcomeAt = null,
            string? outcome = null)
        {
            Context.OrderIntents.Add(new OrderIntentRow
            {
                MovementLegId = legId,
                UpperId = "UPPER-" + legId,
                Purpose = "TO_PICKUP",
                TargetStationId = "ST-15",
                VehicleKey = vehicleKey,
                CreatedAt = createdAt,
                CreateDispatchArmedAt = armedAt,
                LastCreateOutcomeAt = outcomeAt,
                LastCreateOutcome = outcome,
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            Context.ChangeTracker.Clear();
        }

        public Task<bool> AskAsync(DateTimeOffset since) =>
            new OwnOrderCreationLedger(Context, Clock).MayHaveCreatedSinceAsync(
                Vehicle, since, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
