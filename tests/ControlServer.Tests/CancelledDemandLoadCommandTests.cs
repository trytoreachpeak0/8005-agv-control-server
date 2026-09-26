using System.Security.Cryptography;
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
using static ControlServer.Tests.StopEndedJourneyContinuesTests;

namespace ControlServer.Tests;

/// <summary>
/// 扫码前取消落定与引擎装货交错（control-server#362）：已取消的需求不再被下装货命令。
/// </summary>
/// <remarks>
/// <para>
/// <b>形状。</b>多需求旅程的第二个取货站（只挂乙），操作员对乙按了扫码前取消、服务端已授权；一条扫乙自己子批的录入落库。引擎这一轮
/// 锁外读完收件箱、认定这条录入「没拒收、没消费」之后，车报 <c>ALL_EMPTY</c>，入站把乙结成 <c>Cancelled</c>、归属 <c>TERMINATED</c>、
/// 本站结束并答了这条录入 <c>WORKLIST_REVISION_STALE</c>（control-server#324）。引擎接着查取消（已不开着），按这一轮开头的旧游标认乙待装。
/// </para>
/// <para>
/// <b>修之前</b>引擎给乙下了 <c>SlotOperationCommand</c>、归属写回 <c>LOADING</c>。车载端不认「这条需求本车已取消」，照命令开仓
/// （车载端 <c>b789c3d</c>，第一步结果表）。之后车怎么回，下面四格各是一种，都跑到落定：
/// </para>
/// <list type="bullet">
/// <item><b>装上了</b>：服务端接受，关卡只给甲下卸货命令，旅程收尾，乙的货留在车上；乙记着 <c>Cancelled</c>、业务键已抑制。</item>
/// <item><b>期限前报空</b>：乙从 <c>Cancelled</c> 被改写成 <c>RecoveryRequired</c>，旅程 <c>Blocked</c>。</item>
/// <item><b>期限后报空</b>：确定失败要求需求仍是 <c>Accepted</c>，旅程永远停在 <c>AwaitingLoadResult</c>。</item>
/// <item><b>装货中按取消</b>：乙已终结，在途取消被拒 <c>WORKLIST_REVISION_STALE</c>，命令一直挂着。</item>
/// </list>
/// <para>
/// 所以每一格断的都是<b>跑到底之后</b>的整组记账（需求、归属、装货操作、完成记录、旅程），先于「没下命令」断：去掉修复时，每一格红在它自己
/// 的后果上，而不是都红在同一句「发了命令」上。
/// </para>
/// </remarks>
public sealed class CancelledDemandLoadCommandTests
{
    public static TheoryData<string> WhatTheVehicleDoesWithACommand => new()
    {
        "LOADED",
        "REPORTED_EMPTY_BEFORE_THE_DEADLINE",
        "REPORTED_EMPTY_AFTER_THE_DEADLINE",
        "CANCELLED_WHILE_LOADING"
    };

    private const string CancellationId = "d6000000-0000-4000-8000-000000000001";
    private const string EntryId = "d6000000-0000-4000-8000-000000000004";
    private const string InFlightCancellationId = "d6000000-0000-4000-8000-000000000011";
    private const string SecondStopSafetyResultId = "d6000000-0000-4000-8000-00000000005a";

    /// <summary>
    /// 取消在引擎读完录入之后落定：乙根本不被下装货命令，整趟跑到底，只有甲被运走，乙的账与它被取消时一致。
    /// </summary>
    /// <remarks>
    /// <paramref name="vehicle"/> 是「万一命令发出去了，车怎么回」。修好之后没有命令、车无从回，四格结果相同；修之前每一格走到第一步结果表
    /// 里它那一行的结局。
    /// </remarks>
    [Theory]
    [MemberData(nameof(WhatTheVehicleDoesWithACommand))]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancellationThatSettlesAfterTheRuntimeReadTheEntryWinsTheStop(string vehicle)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        EntryInterleaver interleaver = new(EntryInterleaver.AfterTheEntryWasJudgedUnanswered);
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: interleaver);
        JourneyStopRow secondPickup = await ArriveAtTheSecondPickupAsync(fixture);
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromMinutes(5);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
        OnboardConnectionState state = Connection(fixture);

        await RaceTheCancellationAgainstTheEntryAsync(fixture, interleaver, processor, state, secondPickup, SecondSublot);

        // 前提：交错确实发生——拦截器触发了一次，而且触发时这条录入还没人答（答它的是入站那次终结）。
        Assert.Equal(1, interleaver.Fired);
        StationOperationRow? load = await fixture.Context.StationOperations.AsNoTracking()
            .SingleOrDefaultAsync(row => row.DemandId == SecondDemandId && row.OperationType == SlotOperationType.Load, token);
        if (load is not null)
        {
            await AnswerTheCommandAsync(fixture, processor, state, load, vehicle);
        }
        await DriveTheJourneyToItsEndAsync(fixture);

        // 跑到底之后的整组记账。去掉修复时，「装上了」红在归属 LOADED + 装货 Committed（货留在车上），期限前报空红在需求
        // RecoveryRequired + 旅程 Blocked，另外两格红在旅程停在 AwaitingLoadResult。
        Assert.Equal(
            new Ledger(
                DemandExecutionStatus.Succeeded,
                DemandExecutionStatus.Cancelled,
                JourneyDemandStatuses.Terminated,
                LoadOperationForTheCancelled: null,
                Completions: FirstDemandId,
                JourneyRuntimeStage.Completed),
            await LedgerAsync(fixture));
        Assert.False(await AnsweredByALoadCommandAsync(fixture, EntryId), "已取消的乙被下了装货命令。");
        TransportDemandSuppressionRow suppression = await fixture.Context.Set<TransportDemandSuppressionRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == SecondDemandId, token);
        Assert.Equal("CANCELLED_BY_OPERATOR", suppression.ReasonCode);
        // 这条录入恰好被答了一次，是入站终结本站时答的 STALE：引擎让开的那一轮不答，下一轮按「已拒收」跳过。
        JsonElement[] rejections = await RejectionsOfEntryAsync(fixture);
        Assert.Equal(
            ["WORKLIST_REVISION_STALE"],
            rejections.Select(item => item.GetProperty("problem").GetProperty("reasonCode").GetString()!).ToArray());
    }

    /// <summary>
    /// 同一个交错，但这一站还挂着丙：乙的终结不结束本站，入站也就不答这条录入。引擎这一轮让开，下一轮按新游标判乙不在派车范围，
    /// 答 <c>SUBLOT_NOT_IN_DISPATCH_SCOPE</c>——与没有竞态时扫一条已取消的子批得到的答复相同；丙照旧等录入。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task WhenTheStopStillHasWorkTheLosingEntryIsRefusedAsOutOfScopeOnTheNextIteration()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        EntryInterleaver interleaver = new(EntryInterleaver.AfterTheEntryWasJudgedUnanswered);
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: interleaver);
        JourneyStopRow secondPickup = await ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
        OnboardConnectionState state = Connection(fixture);

        await RaceTheCancellationAgainstTheEntryAsync(fixture, interleaver, processor, state, secondPickup, SecondSublot);
        Assert.Equal(1, interleaver.Fired);
        // 前提：乙的终结没有结束本站（丙还开着），所以入站没有答这条录入——答它的只能是引擎的下一轮。
        Assert.Empty(await RejectionsOfEntryAsync(fixture));
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        Assert.False(await AnsweredByALoadCommandAsync(fixture, EntryId), "已取消的乙被下了装货命令。");
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingSublot, JourneyDemandStatuses.Terminated, DemandExecutionStatus.Cancelled),
            ((await JourneyOfAsync(fixture, FirstDemandId)).Stage,
                (await MembershipAsync(fixture, SecondDemandId)).Status,
                (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(row => row.DemandId == SecondDemandId, token))
                    .Status));
        Assert.NotEqual(JourneyDemandStatuses.Terminated, (await MembershipAsync(fixture, ThirdDemandId)).Status);
        JsonElement[] rejections = await RejectionsOfEntryAsync(fixture);
        Assert.Equal(
            ["SUBLOT_NOT_IN_DISPATCH_SCOPE"],
            rejections.Select(item => item.GetProperty("problem").GetProperty("reasonCode").GetString()!).ToArray());
    }

    /// <summary>
    /// 同一个窗口，但车报的取消结果证明不了空（<c>UNKNOWN</c>）：入站把乙转 <c>RecoveryRequired</c>、旅程转 <c>Blocked</c>，归属不动
    /// （<c>OnboardRecoveryCoordinator.KeepDemandAndJourneyBlockedAsync</c>）。引擎不下命令，也不把 <c>Blocked</c> 改写回
    /// <c>AwaitingLoadResult</c>。
    /// </summary>
    /// <remarks>
    /// 这是只写需求、不写归属的那条路：复核只看归属就放它过去。修之前引擎照旧读下命令，<c>SetStage</c> 把入站刚写的 <c>Blocked</c>
    /// 覆盖掉，要人处理的那个状态就这样消失了。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnUnreconciledCancellationLandingInTheWindowKeepsTheJourneyBlockedAndNothingIsLoaded()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        EntryInterleaver interleaver = new(EntryInterleaver.AfterTheEntryWasJudgedUnanswered);
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: interleaver);
        JourneyStopRow secondPickup = await ArriveAtTheSecondPickupAsync(fixture);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
        OnboardConnectionState state = Connection(fixture);

        await RaceTheCancellationAgainstTheEntryAsync(
            fixture, interleaver, processor, state, secondPickup, SecondSublot, overallOutcome: "UNKNOWN");
        Assert.Equal(1, interleaver.Fired);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        Assert.False(await AnsweredByALoadCommandAsync(fixture, EntryId), "证明不了空的取消之后，乙仍被下了装货命令。");
        JourneyRuntimeRow journey = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(
            (JourneyRuntimeStage.Blocked, "LoadCancellationResult_NOT_RECONCILED", DemandExecutionStatus.RecoveryRequired),
            (journey.Stage, journey.BlockReasonCode,
                (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(row => row.DemandId == SecondDemandId, token))
                    .Status));
        Assert.Null(await fixture.Context.StationOperations.AsNoTracking()
            .SingleOrDefaultAsync(row => row.DemandId == SecondDemandId, token));
    }

    /// <summary>
    /// 授权扫码前取消、落一条扫 <paramref name="sublot"/> 的录入，然后跑引擎一轮：在它认定这条录入没人答之后，让车的取消结果落定。
    /// </summary>
    private static async Task RaceTheCancellationAgainstTheEntryAsync(
        RuntimeFixture fixture,
        EntryInterleaver interleaver,
        OnboardMessageProcessor processor,
        OnboardConnectionState state,
        JourneyStopRow secondPickup,
        string sublot,
        string overallOutcome = "ALL_EMPTY")
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        string authorization = await processor.ProcessAsync(CancellationRequest(fixture, CancellationId), state, token);
        Assert.Equal("AUTHORIZED", FirstLinePayload(authorization).GetProperty("decision").GetString());
        await processor.ProcessAsync(
            Envelope(fixture, EntryId, "SublotSubmitted", SecondStopEntry(fixture, secondPickup, runtime, sublot)),
            state,
            token);
        string result = overallOutcome == "ALL_EMPTY"
            ? CancellationResult(fixture, CancellationId)
            : Envelope(fixture, JourneyPlanBuilder.StableGuid(CancellationId, "result"), "LoadCancellationResult", new
            {
                cancellationId = CancellationId,
                demandId = SecondDemandId,
                slotOperationAttemptId = (string?)null,
                overallOutcome,
                slotResults = Array.Empty<object>(),
                observedAt = Now
            });
        interleaver.Arm(() => processor.ProcessAsync(result, state, token));
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>车收到了给乙的装货命令（只在修之前发生），照 <paramref name="vehicle"/> 那一格回。结果都经入站处理，与真车同路。</summary>
    private static async Task AnswerTheCommandAsync(
        RuntimeFixture fixture,
        OnboardMessageProcessor processor,
        OnboardConnectionState state,
        StationOperationRow load,
        string vehicle)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        switch (vehicle)
        {
            case "LOADED":
                await ReportLoadAsync(fixture, processor, state, load, loaded: true);
                break;
            case "REPORTED_EMPTY_BEFORE_THE_DEADLINE":
                await ReportLoadAsync(fixture, processor, state, load, loaded: false);
                break;
            case "REPORTED_EMPTY_AFTER_THE_DEADLINE":
                fixture.Clock.Advance(TimeSpan.FromMinutes(6));
                await ReportLoadAsync(fixture, processor, state, load, loaded: false);
                break;
            case "CANCELLED_WHILE_LOADING":
                await processor.ProcessAsync(
                    Envelope(fixture, JourneyPlanBuilder.StableGuid(InFlightCancellationId, "request"), "LoadCancellationStartRequested", new
                    {
                        cancellationId = InFlightCancellationId,
                        demandId = SecondDemandId,
                        slotOperationAttemptId = load.SlotOperationAttemptId,
                        @operator = new { operatorId = "OP-001", verificationMethod = "BADGE", verifiedAt = fixture.Clock.GetUtcNow() },
                        reason = "Nothing to load."
                    }),
                    state,
                    token);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(vehicle), vehicle, null);
        }
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>
    /// 车对乙的装货报 <c>OperationResult</c>：装上（每个目标仓位 OCCUPIED）或者超时报空（首仓 FAILED/<c>OPERATOR_TIMEOUT</c>，其余 NOT_STARTED）。
    /// </summary>
    private static async Task ReportLoadAsync(
        RuntimeFixture fixture, OnboardMessageProcessor processor, OnboardConnectionState state, StationOperationRow load, bool loaded)
    {
        int[] slots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson) ?? [];
        object[] slotResults =
        [
            .. slots.Select((slot, index) => (object)new
            {
                slotNo = slot,
                outcome = loaded ? "COMPLETED" : index == 0 ? "FAILED" : "NOT_STARTED",
                finalPhysicalState = loaded ? "OCCUPIED" : "EMPTY",
                lockState = "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = loaded || index != 0 ? [] : OperatorTimeout
            })
        ];
        var withoutHash = new
        {
            demandId = load.DemandId,
            slotOperationAttemptId = load.SlotOperationAttemptId,
            operationType = "LOAD",
            overallOutcome = loaded ? "COMPLETED" : "FAILED",
            slotResults,
            observedAt = fixture.Clock.GetUtcNow(),
            journalCheckpoint = loaded ? "RESULT_RECORDED" : "OPERATOR_TIMEOUT_RECORDED"
        };
        string hash = Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(withoutHash, SerializerOptions))).ToLowerInvariant();
        await processor.ProcessAsync(
            Envelope(fixture, Guid.NewGuid().ToString("D"), "OperationResult", new
            {
                withoutHash.demandId,
                withoutHash.slotOperationAttemptId,
                withoutHash.operationType,
                withoutHash.overallOutcome,
                withoutHash.slotResults,
                withoutHash.observedAt,
                withoutHash.journalCheckpoint,
                resultContentSha256 = hash
            }),
            state,
            TestContext.Current.CancellationToken);
    }

    private static readonly string[] OperatorTimeout = ["OPERATOR_TIMEOUT"];

    /// <summary>
    /// 把这趟旅程往下推：离站等待到期、离站核验、开到关卡、卸完关卡上等卸的每一条。哪一步推不动就停在那里，由调用方的断言说清停在哪。
    /// </summary>
    private static async Task DriveTheJourneyToItsEndAsync(RuntimeFixture fixture)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        // 取消结束本站之后阶段仍是 AwaitingSublot、一个待做项都没有，只有离站期限推它走（control-server#324 剩余风险）；
        // 期限要车证明仓门都关着。
        await fixture.ProveSlotDoorsClosedAsync();
        for (int round = 0; round < 4 && (await JourneyOfAsync(fixture, FirstDemandId)).Stage != JourneyRuntimeStage.AwaitingDepartureSafety; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(6));
            await fixture.HearFromPeerAsync();
            await TickAndRunAsync(fixture);
            fixture.Context.ChangeTracker.Clear();
        }
        if ((await JourneyOfAsync(fixture, FirstDemandId)).Stage != JourneyRuntimeStage.AwaitingDepartureSafety)
        {
            return;
        }
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, SecondStopSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");
        // 卸货逐条串行：卸完一条、跑一轮，引擎才对下一条下命令。
        for (int round = 0; round < 3; round++)
        {
            StationOperationRow[] pending = await fixture.Context.StationOperations.AsNoTracking()
                .Where(row => row.OperationType == SlotOperationType.Unload && row.Status == StationOperationStatus.Prepared)
                .ToArrayAsync(token);
            if (pending.Length == 0)
            {
                break;
            }
            foreach (StationOperationRow unload in pending)
            {
                await fixture.ApplySafeResultAsync(unload, SlotOperationType.Unload, SlotBusinessState.Empty);
            }
            await TickAndRunAsync(fixture);
            fixture.Context.ChangeTracker.Clear();
        }
    }

    /// <summary>跑到底之后的记账：甲、乙的需求终态，乙的归属，乙有没有装货操作（有就是它的状态），完成记录的需求，旅程阶段。</summary>
    private sealed record Ledger(
        DemandExecutionStatus First,
        DemandExecutionStatus Second,
        string SecondMembership,
        StationOperationStatus? LoadOperationForTheCancelled,
        string Completions,
        JourneyRuntimeStage Stage);

    private static async Task<Ledger> LedgerAsync(RuntimeFixture fixture)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ControlServerDbContext db = fixture.Context;
        Dictionary<string, DemandExecutionStatus> demands = await db.AcceptedDemands.AsNoTracking()
            .ToDictionaryAsync(row => row.DemandId, row => row.Status, token);
        StationOperationStatus? load = await db.StationOperations.AsNoTracking()
            .Where(row => row.DemandId == SecondDemandId && row.OperationType == SlotOperationType.Load)
            .Select(row => (StationOperationStatus?)row.Status)
            .SingleOrDefaultAsync(token);
        string[] completions = await db.TransportDemandCompletions.AsNoTracking()
            .Select(row => row.DemandId).OrderBy(id => id).ToArrayAsync(token);
        return new Ledger(
            demands[FirstDemandId],
            demands[SecondDemandId],
            (await MembershipAsync(fixture, SecondDemandId)).Status,
            load,
            string.Join(",", completions),
            (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
    }

    /// <summary>发件箱里答这条录入的全部 <c>SublotRejected</c> 的载荷。</summary>
    private static async Task<JsonElement[]> RejectionsOfEntryAsync(RuntimeFixture fixture)
    {
        string[] rows = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "SublotRejected")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        List<JsonElement> matching = [];
        foreach (string json in rows)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.GetProperty("correlationId").GetString() == EntryId)
            {
                matching.Add(document.RootElement.GetProperty("payload").Clone());
            }
        }
        return [.. matching];
    }
}
