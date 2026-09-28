using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Adapters;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// One vehicle's trouble stays its own inside the round that holds <c>JourneyMutationGate</c> (control-server#334's review):
/// a vehicle whose Onboard connection is gone yields its turn instead of failing the round, and a MesIngest that never
/// answers is a failed read, not a failed round.
/// </summary>
public sealed partial class MultiVehicleExecutionTests
{
    /// <summary>
    /// 车 A 的车载端连接不在了（断线不写库，会话行还是 Ready）：它这一轮让开，同一轮里排在它后面的车照常推进——车 C 的在途单被报
    /// FAILED，故障照样记下、Hold 照样发出。A 自己在看板上说出「推进失败」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>修之前</b>（审查必修 1，读到的）：A 的推进抛出传输类异常，整轮随之抛掉。active 按受理时刻排，被跳过的每一轮都是排在 A 后面的
    /// 同一批车，派车也不跑；断线不写库，所以要一直等到 A 重连。写超时按构造一定会留下一条未确认的发件箱行，这条路径因此一定会走到。
    /// </para>
    /// <para>
    /// <b>「本车在途单导致会话未就绪」那一格</b>（<c>not-ready-on-own-order</c>）：车 C 的会话因为挂着本服务端的在途单而不是 Ready——
    /// 真车载端整段路都这样。它的故障监看走在就绪闸门前面（control-server#358），这里要证明 A 让开之后它照样走得到。
    /// </para>
    /// <para>
    /// <b>让 A 排在最前面</b>是刻意的：A 若排在最后，整轮抛与不抛在 C 身上看不出区别。前提断言 A 这一轮确实被拒过发送，否则 A 没有失败，
    /// 什么都没证明。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("ready")]
    [InlineData("not-ready-on-own-order")]
    public async Task AVehicleWhoseOnboardConnectionIsGoneYieldsItsTurnAndTheVehiclesAfterItAreStillAdvanced(string lastVehicleSession)
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        fixture.Riot.MovementState = "MT_FINISHED";
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        string gone = FleetFixture.AgvIds[0];
        string failing = FleetFixture.AgvIds[2];
        await MakeFirstInLineAsync(fixture, gone);
        JourneyRuntimeRow failed = await fixture.JourneyOfAsync(failing);
        fixture.Riot.FailOrder(failed.PickupUpperId);
        if (lastVehicleSession == "not-ready-on-own-order")
        {
            await fixture.DropSessionAsync(failing);
        }
        fixture.Peer.Unavailable = gone;

        await fixture.RunRoundAsync();

        Assert.True(fixture.Peer.Refused > 0, $"nothing was sent to {gone} this round, so its connection being gone was never met");
        RiotOrderCommandAuditRow hold = Assert.Single(await fixture.HoldAttemptsAsync());
        Assert.Equal((failing, failed.PickupUpperId), (hold.AgvId, hold.TargetUpperId));
        VehicleFaultStateRow fault = Assert.Single(
            await fixture.Context.VehicleFaultStates.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal((failing, VehicleFaultEvidence.OrderFailed), (fault.AgvId, fault.EvidenceCode));
        Assert.Equal(JourneyRuntimeEngine.AdvanceFailedReason, (await fixture.JourneyOfAsync(gone)).BlockReasonCode);
    }

    /// <summary>
    /// 派车时读需求目录，MesIngest 接了连接却一直不回话：与目录读不到（<see cref="AFailedCatalogReadDecidesAsBeforeTheMove"/>）一模一样——
    /// 这一轮不受理任何需求，而不是整轮失败（control-server#334 审查必修 2）。
    /// </summary>
    /// <remarks>
    /// 期望不是从这次运行里抄的：它就是「读不到」那条用例的期望，一个字没改。超时与读不到要是走两条路，这里就会看出来。生产的
    /// <see cref="HttpMesIngestCatalog"/> 经真实的 <see cref="HttpClient"/>（500 ms 超时）连一个从不回话的回环监听。
    /// </remarks>
    [Fact]
    public async Task ACatalogReadFromAMesIngestThatNeverAnswersDecidesLikeAnUnreachableOne()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        await using GateHeldOnboardSendTests.HungMesIngest mes = GateHeldOnboardSendTests.HungMesIngest.Start();
        using HttpClient client = mes.Client(TimeSpan.FromMilliseconds(500));
        HttpMesIngestCatalog catalog = new(client, fixture.Clock);
        fixture.Catalog.Through = catalog.ReadCatalogAsync;

        fixture.Clock.Tick = TimeSpan.FromMilliseconds(1);
        await fixture.RunRoundAsync();

        Assert.True(mes.Accepted > 0, "the catalog read never reached MesIngest, so nothing here timed out");
        Assert.Empty(fixture.RoundOutcomes.Outcomes);
        await AssertTranscriptAsync(fixture, """
            log 2101 LogCatalogPollFailed Warning: MesIngest catalog polling failed closed; no journey was accepted.
            catalog reads 1
            """);
    }

    /// <summary>
    /// 派车判据读箱数，MesIngest 一直不回话：每一台车对每一条候选都判「箱数不可用」，这一轮照常走完，不受理任何需求
    /// （control-server#334 审查必修 2，与引擎那一处同一机理）。
    /// </summary>
    /// <remarks>
    /// 箱数读取经真实的 <see cref="HttpClient"/>，超时 500 ms，远短于派车给每台车的预算（默认 30 s），所以这里抛的是 HTTP 超时，
    /// 不是预算到期的取消——预算到期仍是预算到期，由 <c>AVehicleThatExhaustsItsBudgetDoesNotCostTheOthersTheirRound</c> 那一族管。
    /// </remarks>
    [Fact]
    public async Task ABoxCountReadFromAMesIngestThatNeverAnswersRefusesTheCandidatesNotTheRound()
    {
        await using FleetFixture fixture = await FleetFixture.CreateAsync();
        await using GateHeldOnboardSendTests.HungMesIngest mes = GateHeldOnboardSendTests.HungMesIngest.Start();
        using HttpClient client = mes.Client(TimeSpan.FromMilliseconds(500));
        HttpSublotBoxCountReader reader = new(client, Microsoft.Extensions.Options.Options.Create(fixture.Options), fixture.Clock);
        fixture.BoxCounts.Through = reader.ReadMaxBoxCountAsync;

        await fixture.RunRoundAsync();

        Assert.True(mes.Accepted > 0, "no box count read reached MesIngest, so nothing here timed out");
        Assert.Empty(await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        DispatchVehicleOutcome[] vehicles = [.. Assert.Single(fixture.RoundOutcomes.Outcomes).CompletedVehicles];
        Assert.NotEmpty(vehicles);
        Assert.All(
            vehicles.SelectMany(vehicle => vehicle.Verdicts),
            verdict => Assert.Equal("SUBLOT_BOX_COUNT_UNAVAILABLE", verdict.ReasonCode));
    }

    /// <summary>Makes <paramref name="agvId"/>'s journey the first the round advances (the round orders by acceptance).</summary>
    private static async Task MakeFirstInLineAsync(FleetFixture fixture, string agvId)
    {
        JourneyRuntimeRow[] journeys = await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken);
        DateTimeOffset earliest = journeys.Min(row => row.CreatedAt);
        journeys.Single(row => row.AgvId == agvId).CreatedAt = earliest.AddMinutes(-1);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();
    }
}
