using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.StagingDepartureRequestTests;

namespace ControlServer.Tests;

/// <summary>
/// 出发请求带的清单号不是当前号、但新旧两版没有实质差别时照样受理（control-server#742 扩项，用户 10-17 定，CP-0015）。
/// </summary>
/// <remarks>
/// <para>
/// 现场 agv03 10-17 140 站（<c>T2-5_T3-5</c>）：本站装上一篮之后取消了另外两条，取消那一轮先发了取消后的空清单（30），引擎紧接着补发一版带期限的空清单（31，
/// 只补期限）。操作员按着 30 点出发，请求晚 62 ms 到，答 <c>WORKLIST_REVISION_STALE</c>，车多停了 3 分多钟才第二次按成。原件
/// <c>F:/8005-scratch/cutover-ev/production-20261014/d5/60-agv03-140-load-departure-rejected-then-accepted.txt</c>。
/// </para>
/// <para>
/// 「没有实质差别」的口径（调度 10-17 核定）：请求号落在当前停靠自己的清单号区间里、那一版是当前站的；请求带的作业会话若有则是当前停靠的；请求号那一版
/// 到当前号每一版都没有待做项；而且请求号那一版落库之后本站的已装集合没变。最后一条在构造上由前一条推出（装上一篮必先有一条待做项出现在某一版清单里，
/// 途中追加不并入当前停靠），但判定照样直接比，用例 <see cref="AStaleRequestIsRefusedWhenTheLoadsAtTheStopChangedAfterItsWorklist"/> 钉住。
/// </para>
/// </remarks>
public sealed class StaleWorklistDepartureTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 140 站形状：第二个取货站乙、丙两条，丙装上一篮，乙扫码前取消。按着取消后那一版空清单（已被只补期限的下一版顶掉）点出发：受理，同一轮发离站核验。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ADepartureFromTheWorklistADeadlineRefreshReplacedIsAccepted()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        Shape shape = await LoadOneThenCancelTheOtherAsync(fixture);

        string departureRequestId = await RequestDepartureAsync(fixture, worklistRevision: shape.CancelledRevision, operationSessionId: null);
        await RunAsync(fixture);

        JsonElement result = await ResultAsync(fixture, departureRequestId);
        Assert.True(
            result.GetProperty("outcome").GetString() == "ACCEPTED",
            $"Revision {shape.CancelledRevision} differs from {shape.CurrentRevision} only by the deadline, yet: {result}");
        Assert.Equal(shape.CurrentRevision, result.GetProperty("currentWorklistRevision").GetInt64());
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
    }

    internal sealed record Shape(JourneyStopRow Stop, long CancelledRevision, long CurrentRevision);

    /// <summary>
    /// 第二个取货站乙、丙两条：丙录入并装上，乙扫码前取消（车报 ALL_EMPTY）。本站有一篮 Loaded，车停在离站等待里等原期限；取消发的那一版空清单
    /// 已经被引擎补期限的下一版顶掉。返回停靠、取消那一版的号与此刻的号。
    /// </summary>
    internal static async Task<Shape> LoadOneThenCancelTheOtherAsync(RuntimeFixture fixture)
    {
        JourneyStopRow stop = await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromMinutes(5);
        await EnterSublotAsync(fixture, StopEndedJourneyContinuesTests.ThirdDemandId, StopEndedJourneyContinuesTests.ThirdSublot, ThirdSubmissionId);
        await SettleLoadAsync(fixture, StopEndedJourneyContinuesTests.ThirdDemandId);
        Assert.Equal(
            JourneyDemandStatuses.Loaded,
            (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, StopEndedJourneyContinuesTests.ThirdDemandId)).Status);

        await CancelBeforeScanAsync(fixture, SecondDemandId);
        long cancelled = await CurrentStopDepartures.CurrentWorklistRevisionAsync(fixture.Context, fixture.Options.AgvId, Token);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        long current = await CurrentStopDepartures.CurrentWorklistRevisionAsync(fixture.Context, fixture.Options.AgvId, Token);
        Assert.True(current > cancelled, $"The deadline refresh did not replace revision {cancelled}.");
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        await fixture.ProveSlotDoorsClosedAsync();
        return new Shape(stop, cancelled, current);
    }

    private const string ThirdSubmissionId = "20000000-0000-4000-8000-000000007421";

    /// <summary>操作员对一条需求扫码前取消，走服务端真实的授权与结果受理；车报 ALL_EMPTY。</summary>
    private static async Task CancelBeforeScanAsync(RuntimeFixture fixture, string demandId)
    {
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
        OnboardConnectionState state = StopEndedJourneyContinuesTests.Connection(fixture);
        string cancellationId = Guid.NewGuid().ToString("D");
        var who = new { operatorId = "OP-001", verificationMethod = "BADGE", verifiedAt = fixture.Clock.GetUtcNow() };
        await processor.ProcessAsync(
            StopEndedJourneyContinuesTests.Envelope(fixture, Guid.NewGuid().ToString("D"), "LoadCancellationStartRequested", new
            {
                cancellationId,
                demandId,
                slotOperationAttemptId = (string?)null,
                @operator = who,
                reason = "Nothing to load at this stop."
            }),
            state,
            Token);
        await processor.ProcessAsync(
            StopEndedJourneyContinuesTests.Envelope(fixture, Guid.NewGuid().ToString("D"), "LoadCancellationResult", new
            {
                cancellationId,
                demandId,
                slotOperationAttemptId = (string?)null,
                overallOutcome = "ALL_EMPTY",
                slotResults = Array.Empty<object>(),
                observedAt = fixture.Clock.GetUtcNow()
            }),
            state,
            Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(JourneyDemandStatuses.Terminated, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, demandId)).Status);
    }
}
