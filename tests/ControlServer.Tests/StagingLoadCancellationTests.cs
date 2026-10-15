using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.StagingPickupNoCountdownTests;

namespace ControlServer.Tests;

/// <summary>
/// 待送点取货停靠上的两种取消（control-server#713 票正文第 5、6 条，CP-0012）：装货中取消改为放回待派、不抑制；扫码前取消一律拒绝。
/// 机台取货与开关关着时两样都照旧（验收 7 的回归）。
/// </summary>
public sealed class StagingLoadCancellationTests
{
    private const string CancellationId = "71300000-0000-4000-8000-000000000001";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 验收 6：待送点上装货中取消、车证明仓位全空：需求放回积压（仍 Accepted、不抑制、不计站点超时次数），一篮没装，旅程原地收尾、
    /// 收尾清单说 <c>LOAD_CANCELLED</c>。另两种照旧终结为 <c>CANCELLED_BY_OPERATOR</c> 并抑制。把放回改回终结，待送点那一种红。
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds), MemberType = typeof(StagingPickupNoCountdownTests))]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task ACancellationDuringTheLoadReleasesTheDemandOnlyAtTheStagingStation(StopKind kind)
    {
        await using RuntimeFixture fixture = await ArrangeAsync(kind);
        await fixture.AdvanceToLoadResultAsync();
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);

        await CancelDuringTheLoadAsync(fixture, Demand, load);

        fixture.Context.ChangeTracker.Clear();
        AcceptedDemandRow demand = await fixture.DemandRowAsync();
        JourneyRuntimeRow journey = await fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.Equal(JourneyRuntimeStage.Completed, journey.Stage);
        if (kind == StopKind.StagingRulesOn)
        {
            Assert.Equal(DemandExecutionStatus.Accepted, demand.Status);
            JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
                .SingleAsync(row => row.DemandId == Demand, Token);
            Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, membership.RemovalReason);
            Assert.True(
                await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AnyAsync(row => row.DemandId == Demand, Token));
            await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
            Assert.Equal(CurrentStopDepartures.LoadCancelledReleasedReason, journey.BlockReasonCode);
            Assert.Contains("LOAD_CANCELLED", await StagingDepartureRequestTests.StopEndedReasonsSentAsync(fixture));
        }
        else
        {
            Assert.Equal(DemandExecutionStatus.Cancelled, demand.Status);
            await SuppressionAssertions.AssertSuppressedAsync(fixture.Context, Demand, "CANCELLED_BY_OPERATOR");
        }
        Assert.Equal(StationOperationStatus.Cancelled, (await fixture.OperationAsync(SlotOperationType.Load)).Status);
    }

    /// <summary>
    /// 验收 6 的另一格：同站追加之后第 1 个停靠已装上 A，车在第 2 个停靠装 B 时取消，车证明 B 的仓位全空。B 放回待派、不抑制，A 还在车上，
    /// 旅程不收尾；引擎接着推进不抛（「装货途中被结束之后的续跑」认得被放回的那一条），本站结束后车停在离站等待里。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancellationDuringASecondStagingLoadKeepsTheFirstOnBoardAndTheJourneyGoing()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        fixture.Clock.Advance(fixture.Options.SameStationAppendCorrectionGrace + TimeSpan.FromSeconds(1));
        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        await Batch7StopDrivenAdvanceDriver.AnswerDepartureSafetyAsync(
            fixture, Demand, Batch7StopDrivenAdvanceDriver.FirstSafetyResultId);
        await Batch7StopDrivenAdvanceDriver.ArriveAtCurrentStopAsync(fixture, SecondDemand, "TO_GATE");
        // 夹具追加时预留两篮；录入时按 BR-013 由盒数重算篮数（每篮四盒），所以子批要读出 8 盒，否则录入被拒。
        fixture.BoxCounts.Set(SecondSublot, 8);
        await SubmitAtCurrentStopAsync(fixture, SecondSublot);
        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
        StationOperationRow load = await fixture.Context.StationOperations.AsNoTracking()
            .SingleAsync(row => row.DemandId == SecondDemand && row.OperationType == SlotOperationType.Load, Token);

        await CancelDuringTheLoadAsync(fixture, SecondDemand, load);
        fixture.Context.ChangeTracker.Clear();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyDemandRow released = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == SecondDemand, Token);
        Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, released.RemovalReason);
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
        JourneyDemandRow first = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == Demand, Token);
        Assert.Equal((JourneyDemandStatuses.Loaded, (DateTimeOffset?)null), (first.Status, first.RemovedAt));
        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, journey.Stage);
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.Contains("LOAD_CANCELLED", await StagingDepartureRequestTests.StopEndedReasonsSentAsync(fixture));
    }

    /// <summary>
    /// C11 的裁定（control-server#713 第 4 条）：待送点上锚需求装货中取消、旅程后面还有一条没开始装的，按一篮没装处理——旅程原地收尾，
    /// 锚与那一条一起放回待派、不抑制。只放回锚不行：旅程开着时锚会被改派到别的车（<c>FaultedCargoBindings</c> 的前提）；裁定之前这一支
    /// 照旧终结锚（取消并抑制），删掉收尾里对另一条的放回，这条红。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task CancellingTheAnchorDuringItsLoadClosesTheJourneyAndReleasesEverything()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await fixture.AdvanceToLoadResultAsync();
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        fixture.Context.ChangeTracker.Clear();
        string journeyId = (await fixture.RuntimeAsync()).JourneyId;
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);

        await CancelDuringTheLoadAsync(fixture, Demand, load);
        fixture.Context.ChangeTracker.Clear();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        // 放回之后那一轮派车可能把两条重新受理进新旅程，所以只看原来那一趟。
        JourneyRuntimeRow journey = await fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journeyId, Token);
        Assert.Equal(JourneyRuntimeStage.Completed, journey.Stage);
        Assert.Equal(CurrentStopDepartures.LoadCancelledReleasedReason, journey.BlockReasonCode);
        JourneyDemandRow[] memberships = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => row.JourneyId == journeyId)
            .ToArrayAsync(Token);
        Assert.Equal(2, memberships.Length);
        Assert.All(memberships, row => Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, row.RemovalReason));
        Assert.Equal(
            [DemandExecutionStatus.Accepted, DemandExecutionStatus.Accepted],
            await fixture.Context.AcceptedDemands.AsNoTracking()
                .Where(row => row.DemandId == Demand || row.DemandId == SecondDemand)
                .Select(row => row.Status)
                .ToArrayAsync(Token));
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
    }

    /// <summary>
    /// 第 6 条（纵深防御，防旧版车载端）：待送点上扫码前取消一律拒绝，<c>ACTION_NOT_ALLOWED_IN_STATE</c>，什么都不变。机台与开关关着时照旧授权。
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds), MemberType = typeof(StagingPickupNoCountdownTests))]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task ACancellationBeforeAnyEntryIsRefusedOnlyAtTheStagingStation(StopKind kind)
    {
        await using RuntimeFixture fixture = await ArrangeAsync(kind);
        await fixture.AdvanceToSublotWaitAsync();

        string answer;
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            answer = await ProcessAsync(fixture, connection, "LoadCancellationStartRequested", new
            {
                cancellationId = CancellationId,
                demandId = Demand,
                slotOperationAttemptId = (string?)null,
                @operator = Operator(fixture),
                reason = "Nothing to load here."
            });
        }

        JsonElement payload = FirstPayload(answer);
        if (kind == StopKind.StagingRulesOn)
        {
            Assert.Equal("REJECTED", payload.GetProperty("decision").GetString());
            Assert.Equal(ServerReasonCodes.ActionNotAllowedInState,
                payload.GetProperty("problem").GetProperty("reasonCode").GetString());
            Assert.False(await fixture.Context.RecoveryWorkflows.AnyAsync(Token));
        }
        else
        {
            Assert.Equal("AUTHORIZED", payload.GetProperty("decision").GetString());
        }
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static async Task CancelDuringTheLoadAsync(RuntimeFixture fixture, string demandId, StationOperationRow load)
    {
        int[] slots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!;
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        string authorization = await ProcessAsync(fixture, connection, "LoadCancellationStartRequested", new
        {
            cancellationId = CancellationId,
            demandId,
            slotOperationAttemptId = load.SlotOperationAttemptId,
            @operator = Operator(fixture),
            reason = "Scanned the wrong door."
        });
        Assert.Equal("AUTHORIZED", FirstPayload(authorization).GetProperty("decision").GetString());
        string ack = await ProcessAsync(fixture, connection, "LoadCancellationResult", new
        {
            cancellationId = CancellationId,
            demandId,
            slotOperationAttemptId = load.SlotOperationAttemptId,
            overallOutcome = "ALL_EMPTY",
            slotResults = slots.Select(slot => new
            {
                slotNo = slot,
                outcome = "COMPLETED",
                finalPhysicalState = "EMPTY",
                lockState = "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = Array.Empty<string>()
            }).ToArray(),
            observedAt = fixture.Clock.GetUtcNow()
        });
        Assert.Contains("\"DurableAck\"", ack, StringComparison.Ordinal);
    }

    private static async Task SubmitAtCurrentStopAsync(RuntimeFixture fixture, string sublot)
    {
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        StopEntryAddress address = (await JourneyStopCursor.LoadAsync(fixture.Context, runtime, Token))
            .EntryAddressOfCurrentStop(runtime.WorklistRevision);
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"), "SublotSubmitted", Batch7StopDrivenAdvanceDriver.SublotSubmission(fixture, address, sublot));
    }

    private static object Operator(RuntimeFixture fixture) =>
        new { operatorId = "OP-713", verificationMethod = "BADGE", verifiedAt = fixture.Clock.GetUtcNow() };

    private static Task<string> ProcessAsync(
        RuntimeFixture fixture,
        ControlServerDbContext connection,
        string messageType,
        object payload)
    {
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(),
            runtimeOptions: fixture.Options);
        OnboardConnectionState state = new()
        {
            AgvId = fixture.Options.AgvId,
            SessionGeneration = 1,
            CapabilityRevision = 1,
            SafetyRevision = 7,
            Readiness = SessionReadiness.Ready
        };
        string line = JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType,
            messageId = Guid.NewGuid().ToString("D"),
            correlationId = (string?)null,
            agvId = fixture.Options.AgvId,
            sessionGeneration = 1L,
            sentAt = fixture.Clock.GetUtcNow(),
            payload
        }, SerializerOptions);
        return processor.ProcessAsync(line, state, Token);
    }

    private static JsonElement FirstPayload(string answer)
    {
        using JsonDocument document = JsonDocument.Parse(answer.Split('\n')[0]);
        return document.RootElement.GetProperty("payload").Clone();
    }
}
