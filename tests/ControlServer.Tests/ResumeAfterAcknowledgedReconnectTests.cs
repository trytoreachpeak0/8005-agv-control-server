using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 续跑重发的那一版清单车已经确认、随后车以新的会话代次重连：续跑沿用已确认那一行，不再撞重放校验（control-server#291 独立审查）。
/// </summary>
/// <remarks>
/// <para>
/// 三处续跑（卸货侧 U1/U5、装货侧 #289 的 <c>resumingAfterCommit</c>、本站在装那一条被终结之后）都在「上一轮已经把这一版清单写盘、
/// 推进那一次保存没落」时重发同一个 messageId。同一代、或那一行还没确认时，发件箱按同一个 id 复用它；而车确认过、又换了一代时，
/// 候选报文只是信封的代次不同，重放校验（<c>WireToGateStore.RefreshOutboundEnvelopeAsync</c>）按「已确认、代次不同」拒：
/// <c>ProtocolContentConflictException: Outbound MessageId was replayed with different semantics or a non-advancing session generation.</c>
/// 阶段停住、看板 <c>JOURNEY_ADVANCE_FAILED</c>，这辆车每轮抛。审查对照过：只确认不换代、只换代不确认都是绿，两者都发生才红。
/// </para>
/// <para>
/// 修法与到站重跑（control-server#331）相同：续跑发清单与录入请求时带 <c>keepAcknowledgedIgnoring</c>，车确认过的那一版除信封外一字不差就沿用。
/// </para>
/// </remarks>
public sealed class ResumeAfterAcknowledgedReconnectTests
{
    /// <summary>卸货侧 U5 的形状：下一版清单已写盘、第二条卸货命令没提交；车确认了那一版清单，又以第 2 代重连。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheNextUnloadIsStillCommandedWhenTheVehicleAcknowledgedTheWorklistAndReconnected()
    {
        UnloadCrashResumeTests.FailOnce crash = new(command =>
            command.CommandText.Contains("INSERT INTO \"ProtocolOutbox\"", StringComparison.Ordinal) &&
            UnloadCrashResumeTests.HasParameter(command, "SlotOperationCommand"));
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: crash);
        await UnloadCrashResumeTests.LoadBothAndArriveAtTheUnloadStopAsync(fixture);
        (string first, string second) = await UnloadCrashResumeTests.UnloadOrderAsync(fixture);
        await ApplySafeResultAsync(fixture, first, SlotOperationType.Unload, SlotBusinessState.Empty);
        crash.Armed = true;
        await TickAndRunExpectingCrashAsync(fixture);
        Assert.True(crash.Fired, "The injected failure never fired, so this proved nothing.");

        await AcknowledgeAndReconnectAsync(fixture, "CurrentStopWorklistSnapshot");

        Exception? resumed = await UnloadCrashResumeTests.RunRoundAsync(fixture);
        JourneyRuntimeRow after = await JourneyOfAsync(fixture, first);
        Assert.True(
            await UnloadCrashResumeTests.UnloadCommandedAsync(fixture, second),
            $"The second unload was never commanded. stage={after.Stage} block={after.BlockReasonCode} round={resumed?.Message}");
    }

    /// <summary>
    /// 装货侧 #289 的续跑：一站两条，第一条装上、已存为 LOADED，下一版清单已写盘、录入请求没写盘；车确认了那一版清单，又以第 2 代重连。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheNextEntryIsStillRequestedAfterALoadWhenTheVehicleAcknowledgedTheWorklistAndReconnected()
    {
        UnloadCrashResumeTests.FailOnce crash = new(command =>
            command.CommandText.Contains("INSERT INTO \"ProtocolOutbox\"", StringComparison.Ordinal) &&
            UnloadCrashResumeTests.HasParameter(command, "SublotEntryRequested"));
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: crash);
        JourneyRuntimeRow runtime = await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));
        await TickAndRunAsync(fixture);
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        crash.Armed = true;
        await TickAndRunExpectingCrashAsync(fixture);
        Assert.True(crash.Fired, "The injected failure never fired, so this proved nothing.");
        Assert.Equal(JourneyDemandStatuses.Loaded, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, FirstDemandId)).Status);

        await AcknowledgeAndReconnectAsync(fixture, "CurrentStopWorklistSnapshot");

        Exception? resumed = await UnloadCrashResumeTests.RunRoundAsync(fixture);
        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            after.Stage == JourneyRuntimeStage.AwaitingSublot,
            $"The load was settled and the next entry never requested. stage={after.Stage} block={after.BlockReasonCode} round={resumed?.Message}");
        await AssertTheEntryRequestedInTheNewGenerationAsync(fixture, SecondSublot);
    }

    /// <summary>
    /// 同上，开着离站期限（生产默认 5 分钟，这里 10 秒）：重连作废了离站等待（ADR-cross-0055），续跑那一轮按此刻补填，期限与车已确认那一版
    /// 不同，于是升一版清单（control-server#339）而不是同号重发；录入请求跟着新的一版在第 2 代发出。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task WithTheStationDeadlineOnTheNextEntryIsRequestedUnderANewerWorklistAfterTheReconnect()
    {
        UnloadCrashResumeTests.FailOnce crash = new(command =>
            command.CommandText.Contains("INSERT INTO \"ProtocolOutbox\"", StringComparison.Ordinal) &&
            UnloadCrashResumeTests.HasParameter(command, "SublotEntryRequested"));
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: crash);
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        JourneyRuntimeRow runtime = await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));
        await TickAndRunAsync(fixture);
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        crash.Armed = true;
        await TickAndRunExpectingCrashAsync(fixture);
        Assert.True(crash.Fired, "The injected failure never fired, so this proved nothing.");
        string[] worklistsBefore = await WorklistIdsAsync(fixture);

        await AcknowledgeAndReconnectAsync(fixture, "CurrentStopWorklistSnapshot");

        Exception? resumed = await UnloadCrashResumeTests.RunRoundAsync(fixture);
        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            after.Stage == JourneyRuntimeStage.AwaitingSublot,
            $"The load was settled and the next entry never requested. stage={after.Stage} block={after.BlockReasonCode} round={resumed?.Message}");
        await AssertTheEntryRequestedInTheNewGenerationAsync(fixture, SecondSublot);
        string[] newer = [.. (await WorklistIdsAsync(fixture)).Except(worklistsBefore)];
        Assert.NotEmpty(newer);
        // 新的那一版带着补填之后的期限：车上显示的就是服务端此刻判定用的那一个，而不是先收到一版没有期限的清单。
        string[] payloads = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => newer.Contains(row.MessageId))
            .Select(row => row.PayloadJson)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.All(payloads, payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            Assert.NotEqual(
                JsonValueKind.Null,
                document.RootElement.GetProperty("payload").GetProperty("stationDepartureDeadlineAt").ValueKind);
        });
    }

    /// <summary>
    /// 不是续跑的那一条路保持原样：断联作废了离站等待，结果在就绪后第一轮之前已落库，这一轮刚落定、本站还有待装——发出的那一版清单
    /// 照旧不带期限，补填留给下一轮等录入那一处（续跑那一支的补填不碰正常路径，cs#291 审查追问）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AFreshLoadAfterAVoidedWaitStillSendsItsWorklistWithoutADeadlineAsBefore()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        JourneyRuntimeRow runtime = await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        // 断开：新的一代握手还没完成，会话不就绪，这一轮作废离站等待。
        await fixture.ReconnectAsync(2);
        await TickAndRunAsync(fixture);
        Assert.Null((await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt);
        string[] worklistsBefore = await WorklistIdsAsync(fixture);

        // 结果先落库，然后握手完成、车听得到；就绪后第一轮直接走到刚落定那一段。
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.AdvanceSessionAsync(2);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        string[] newer = [.. (await WorklistIdsAsync(fixture)).Except(worklistsBefore)];
        string payload = Assert.Single(await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => newer.Contains(row.MessageId))
            .Select(row => row.PayloadJson)
            .ToArrayAsync(TestContext.Current.CancellationToken));
        using JsonDocument document = JsonDocument.Parse(payload);
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("payload").GetProperty("stationDepartureDeadlineAt").ValueKind);
    }

    private static async Task<string[]> WorklistIdsAsync(RuntimeFixture fixture) =>
        await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "CurrentStopWorklistSnapshot")
            .Select(row => row.MessageId)
            .ToArrayAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// 本站在装那一条被终结之后的续跑：第二个取货站装货途中取消了一条，同站还有一条；续跑那一轮清单已写盘、录入请求没写盘。
    /// 车确认了那一版清单，又以第 2 代重连。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheEntryForTheOtherDemandIsStillRequestedAfterAnEndingWhenTheVehicleAcknowledgedTheWorklistAndReconnected()
    {
        UnloadCrashResumeTests.FailOnce crash = new(command =>
            command.CommandText.Contains("INSERT INTO \"ProtocolOutbox\"", StringComparison.Ordinal) &&
            UnloadCrashResumeTests.HasParameter(command, "SublotEntryRequested"));
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: crash);
        await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        await LoadEndedAtASecondPickupTests.CancelTheSecondDemandWhileLoadingAsync(fixture);
        crash.Armed = true;
        await TickAndRunExpectingCrashAsync(fixture);
        Assert.True(crash.Fired, "The injected failure never fired, so this proved nothing.");

        await AcknowledgeAndReconnectAsync(fixture, "CurrentStopWorklistSnapshot");

        Exception? resumed = await UnloadCrashResumeTests.RunRoundAsync(fixture);
        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            after.Stage == JourneyRuntimeStage.AwaitingSublot,
            $"The stop still has a demand to load and its entry was never requested. stage={after.Stage} block={after.BlockReasonCode} round={resumed?.Message}");
        await AssertTheEntryRequestedInTheNewGenerationAsync(fixture, StopEndedJourneyContinuesTests.ThirdSublot);
    }

    /// <summary>
    /// <summary>
    /// 第 2 代下发出了只点名 <paramref name="sublot"/> 的那一版录入请求，没被退役。不再接着录入：夹具的入站辅助方法把会话代次写死为 1，
    /// 换代之后它录的那一条按旧会话处理——那是夹具的局限，不是这里要证的事。
    /// </summary>
    private static async Task AssertTheEntryRequestedInTheNewGenerationAsync(RuntimeFixture fixture, string sublot)
    {
        ProtocolOutboxRow[] requests = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "SublotEntryRequested" && row.FencedAt == null)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Contains(requests, row =>
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            JsonElement root = document.RootElement;
            return root.GetProperty("sessionGeneration").GetInt64() == 2 &&
                   root.GetProperty("payload").GetProperty("expectedSublots").EnumerateArray()
                       .Select(item => item.GetString()).SequenceEqual([sublot]);
        });
    }

    /// 车确认了发件箱里这一类还在等确认的每一行，然后断开、以第 2 代重连并握手完成；服务端换一个引擎（进程重启）。
    /// </summary>
    private static async Task AcknowledgeAndReconnectAsync(RuntimeFixture fixture, string messageType)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using (ControlServerDbContext context = fixture.OpenConnectionContext())
        {
            ProtocolOutboxRow[] pending = await context.ProtocolOutbox
                .Where(row => row.MessageType == messageType && row.AcknowledgedAt == null && row.FencedAt == null)
                .ToArrayAsync(token);
            Assert.NotEmpty(pending);
            foreach (ProtocolOutboxRow row in pending)
            {
                row.AcknowledgedAt = fixture.Clock.GetUtcNow();
            }
            await context.SaveChangesAsync(token);
        }
        fixture.Context.ChangeTracker.Clear();
        await ArrivalPublishInterruptedThenReconnectedTests.ReconnectAtGenerationAsync(fixture, generation: 2);
        await fixture.RecreateEngineAsync();
    }
}
