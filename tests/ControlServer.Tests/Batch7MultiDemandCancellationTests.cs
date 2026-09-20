using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.Batch7MultiDemandAdvanceTests;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 扫码前取消，针对操作员在清单里指名的<b>那一条</b>需求（票面第 9 条，批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// 在这之前这个判定是旅程级的：旅程停在等录入、旅程行上没有已消费的录入、旅程行上那条装货命令还没发。一站几条需求
/// 逐条串行之后，这三条都指不准了——第一条正在装的时候，旅程不在等录入、旅程行上有已消费的录入、也发过装货命令，
/// 而第二条明明还是「扫码之前」的。
/// </para>
/// <para>
/// 改法是把三条都换成对被指名那条需求的提问：它挂在车此刻所在的那个停靠上、那是它的取货停靠、它自己的归属行还是待装。
/// 单需求下这与原来挡下的是同一批请求——停靠上只有这一条需求，两组条件一一对应。
/// </para>
/// </remarks>
public sealed class Batch7MultiDemandCancellationTests
{
    private const string CancellationId = "c7060000-0000-4000-8000-000000000001";

    /// <summary>
    /// 第一条正在装，操作员取消<b>第二条</b>：授权；<c>ALL_EMPTY</c> 回来之后只终结第二条，第一条照旧在清单上、
    /// 旅程照旧开着。
    /// </summary>
    [Fact]
    public async Task CancellingTheOtherDemandWhileOneIsLoadingEndsOnlyTheNamedOne()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow runtime = await TwoDemandsAtThePickupAsync(fixture);
        // 第一条（锚需求）录入、开始装。旅程于是停在等装货结果，而不是等录入。
        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync(FirstDemandId)).Stage);

        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = Processor(fixture, connection);
        OnboardConnectionState state = Connection(fixture);

        JsonElement authorized = await AuthorizeAsync(fixture, processor, state, SecondDemandId, token);
        Assert.Equal("AUTHORIZED", authorized.GetProperty("decision").GetString());

        await processor.ProcessAsync(AllEmptyResult(fixture, SecondDemandId), state, token);

        // 只有第二条终结了。
        Assert.Equal(
            DemandExecutionStatus.Cancelled,
            (await fixture.Context.AcceptedDemands.AsNoTracking()
                .SingleAsync(row => row.DemandId == SecondDemandId, token)).Status);
        Assert.Equal(JourneyDemandStatuses.Terminated, (await MembershipAsync(fixture, SecondDemandId)).Status);
        Assert.Equal(
            DemandExecutionStatus.Accepted,
            (await fixture.Context.AcceptedDemands.AsNoTracking()
                .SingleAsync(row => row.DemandId == FirstDemandId, token)).Status);
        // 旅程没关，锚需求照常往下走。
        JourneyRuntimeRow after = await fixture.RuntimeAsync(FirstDemandId);
        Assert.NotEqual(JourneyRuntimeStage.Completed, after.Stage);
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
    }

    /// <summary>已经录入、开始装的那一条，扫码前取消拒绝——那时要走的是在途取消（带 attempt 的那一路）。</summary>
    [Fact]
    public async Task CancellingADemandThatIsAlreadyLoadingIsRejected()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow runtime = await TwoDemandsAtThePickupAsync(fixture);
        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));
        await TickAndRunAsync(fixture);

        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        JsonElement answer = await AuthorizeAsync(
            fixture, Processor(fixture, connection), Connection(fixture), FirstDemandId, token);

        Assert.Equal("REJECTED", answer.GetProperty("decision").GetString());
        Assert.Equal(
            ServerReasonCodes.ActionNotAllowedInState,
            answer.GetProperty("problem").GetProperty("reasonCode").GetString());
    }

    /// <summary>车还没到取货站时拒绝：那时车不在这个停靠上，「扫码之前」无从谈起（与改动前一致）。</summary>
    [Fact]
    public async Task CancellingBeforeTheVehicleReachesThePickupIsRejected()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync(FirstDemandId)).Stage);

        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        JsonElement answer = await AuthorizeAsync(
            fixture, Processor(fixture, connection), Connection(fixture), FirstDemandId, token);

        Assert.Equal("REJECTED", answer.GetProperty("decision").GetString());
    }

    private static async Task<JsonElement> AuthorizeAsync(
        RuntimeFixture fixture,
        OnboardMessageProcessor processor,
        OnboardConnectionState state,
        string demandId,
        CancellationToken cancellationToken)
    {
        string response = await processor.ProcessAsync(
            BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), "LoadCancellationStartRequested", 1, new
            {
                cancellationId = CancellationId,
                demandId,
                slotOperationAttemptId = (string?)null,
                @operator = BeforeSublotOperator(fixture),
                reason = "Nothing to load for this demand at this stop."
            }),
            state,
            cancellationToken);
        using JsonDocument document = JsonDocument.Parse(response.Split('\n')[0]);
        return document.RootElement.GetProperty("payload").Clone();
    }

    private static string AllEmptyResult(RuntimeFixture fixture, string demandId) =>
        BeforeSublotEnvelope(fixture, "c7060000-0000-4000-8000-000000000101", "LoadCancellationResult", 1, new
        {
            cancellationId = CancellationId,
            demandId,
            slotOperationAttemptId = (string?)null,
            overallOutcome = "ALL_EMPTY",
            slotResults = Array.Empty<object>(),
            observedAt = Now,
            evidenceSha256 = new string('b', 64),
            commandContentSha256 = new string('c', 64)
        });

    private static OnboardMessageProcessor Processor(RuntimeFixture fixture, ControlServerDbContext connection) =>
        TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build());

    private static OnboardConnectionState Connection(RuntimeFixture fixture) => new()
    {
        AgvId = fixture.Options.AgvId,
        SessionGeneration = 1,
        CapabilityRevision = 1,
        SafetyRevision = 7,
        Readiness = SessionReadiness.Ready
    };
}
