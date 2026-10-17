using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using ControlServer.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 以站为生命周期的出站行（<c>SublotEntryRequested</c>、<c>SublotRejected</c>）在站结束、旅程终态时退役（control-server#740）。
/// </summary>
/// <remarks>
/// <para>
/// <b>修之前。</b>站结束之后车载端不收这一站的录入请求与拒收、也不回 ack（「录入请求所属的站已结束，不收下」）；服务端只退上一号或当前号，
/// 更早的版留在发件箱里未确认、未退役，每次重连补发、车每次拒收。换协议身份时 <c>ProtocolOutboxIdentityStartupCheck</c> 拒绝启动：
/// 10-17 v4 换装因此只能换新库（库 C 有 9 行，全是终态旅程的）。
/// </para>
/// <para>
/// <b>不认来路。</b>遗留行是哪条路径漏下的不重要，这里直接在发件箱里放一行「本停靠较早一版」的录入请求与一行拒收，断言站结束（旅程终态）之后
/// 它们都不再待补发。判据按载荷里的 <c>operationSessionId</c>，它就是停靠行的 <c>OperationSessionId</c>。
/// </para>
/// </remarks>
public sealed class StopScopedOutboxFenceTests
{
    private const string V3Manifest = "4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7";
    private const string StrayEntryId = "c7400000-0000-4000-8000-000000000001";
    private const string StrayRejectionId = "c7400000-0000-4000-8000-000000000002";
    private const string StrayStagedRejectionId = "c7400000-0000-4000-8000-000000000003";
    private const string OtherSessionEntryId = "c7400000-0000-4000-8000-000000000004";
    private const string OtherSessionId = "c7400000-0000-4000-8000-0000000000ff";

    private static readonly string[] StopScopedTypes = ["SublotEntryRequested", "SublotRejected"];
    private static readonly string[] EntryMethods = ["SCAN"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>车开离取货站：本停靠遗留的录入请求与拒收都退役。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task LeavingThePickupStopFencesItsStrayEntryRequestAndRejection()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        JourneyStopRow pickup = await CurrentStopAsync(fixture, FirstDemandId);
        await AddStraysAsync(fixture, pickup, current: true);

        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        // 遗留行在站还开着时不动：车可能还在等它们。
        await AssertPendingAsync(fixture, StrayEntryId, StrayRejectionId);

        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);

        await AssertFencedAsync(fixture, StrayEntryId, StrayRejectionId);
    }

    /// <summary>旅程走完：发件箱里不再有这两类未确认、未退役的行。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACompletedJourneyLeavesNoStopScopedLinePendingReplay()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await AddStraysAsync(fixture, await CurrentStopAsync(fixture, FirstDemandId), current: true);

        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        Assert.Equal(JourneyRuntimeStage.Completed, (await ArriveAtGateAndUnloadAsync(fixture, FirstDemandId)).Stage);

        Assert.Empty(await PendingStopScopedAsync(fixture));
        await AssertOtherStopScopedTypesSettledAsync(fixture);
    }

    /// <summary>
    /// 一站两条，甲已装完，扫码前取消乙：本站因此结束、旅程继续（<c>StopEndWorklist</c>）。车还停在站上等离站，本站遗留的两行在取消那一次就退役。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancelThatEndsTheStopFencesItsStrayLinesAtOnce()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        JourneyStopRow pickup = await CurrentStopAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await SettleLoadAsync(fixture, FirstDemandId)).Stage);
        await AddStraysAsync(fixture, pickup, current: true);
        // 别的作业会话的一行（审查 F2）：按停靠退，不是这一类全退。
        fixture.Context.ProtocolOutbox.Add(Stray(
            fixture, OtherSessionEntryId, "SublotEntryRequested", current: true,
            new
            {
                operationSessionId = OtherSessionId,
                stationId = pickup.StationId,
                worklistRevision = 0,
                expectedSublots = new[] { FirstSublot },
                entryMethods = EntryMethods,
                expiresOnRevisionChange = true
            }));
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        await CancelBeforeScanAsync(fixture, SecondDemandId, "c7400000-0000-4000-8000-000000000011");

        Assert.NotEqual(JourneyRuntimeStage.Completed, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        await AssertFencedAsync(fixture, StrayEntryId, StrayRejectionId);
        await AssertPendingAsync(fixture, OtherSessionEntryId);
    }

    /// <summary>一站两条都没装，扫码前先后取消：旅程被终结（<c>PickupStopTermination</c> → <c>JourneyClosure</c>），遗留的两行退役。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ATerminatedJourneyLeavesNoStopScopedLinePendingReplay()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        await AddStraysAsync(fixture, await CurrentStopAsync(fixture, FirstDemandId), current: true);

        await CancelBeforeScanAsync(fixture, SecondDemandId, "c7400000-0000-4000-8000-000000000021");
        await AssertPendingAsync(fixture, StrayEntryId, StrayRejectionId);
        await CancelBeforeScanAsync(fixture, FirstDemandId, "c7400000-0000-4000-8000-000000000022");

        Assert.Equal(JourneyRuntimeStage.Completed, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        await AssertFencedAsync(fixture, StrayEntryId, StrayRejectionId);
        Assert.Empty(await PendingStopScopedAsync(fixture));
        await AssertOtherStopScopedTypesSettledAsync(fixture);
    }

    /// <summary>
    /// 只退已落库的行：收尾与站结束时答迟到扫码新暂存（还没保存）的那一条 <c>SublotRejected</c> 是车要收的，同一次退役不碰它。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnAnswerStagedInTheSameChangeIsNotFenced()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        JourneyStopRow pickup = await CurrentStopAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        ProtocolOutboxRow staged = Stray(fixture, StrayStagedRejectionId, "SublotRejected", current: true, Rejection(pickup));
        fixture.Context.ProtocolOutbox.Add(staged);

        Assert.Equal(0, await StopScopedOutbox.FenceEndedAsync(fixture.Context, agvId: null, fixture.Clock.GetUtcNow(), Token));
        Assert.Null(staged.FencedAt);
    }

    /// <summary>
    /// 已有库：所属旅程已终态、信封是另一协议身份的遗留行，启动修复把它们退役，换身份的启动检查随后放行。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheStartupRepairFencesForeignIdentityStraysOfEndedStopsSoTheIdentityCheckPasses()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        JourneyStopRow pickup = await CurrentStopAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtGateAndUnloadAsync(fixture, FirstDemandId);
        // 修之前写下的样子：旅程已终态，遗留行仍未确认、未退役，且是上一版协议身份。
        await AddStraysAsync(fixture, pickup, current: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProtocolOutboxIdentityStartupCheck.EnsureAsync(fixture.Context, NullLogger.Instance, null, Token));

        await using ServiceProvider services = HostServices(fixture);

        await StopScopedOutbox.FenceEndedAtStartupAsync(services, Token);

        await AssertFencedAsync(fixture, StrayEntryId, StrayRejectionId);
        await ProtocolOutboxIdentityStartupCheck.EnsureAsync(services, Token);
        // 幂等：再跑一次不动任何行，身份检查照旧放行。
        await StopScopedOutbox.FenceEndedAtStartupAsync(services, Token);
        await ProtocolOutboxIdentityStartupCheck.EnsureAsync(services, Token);
    }

    /// <summary>启动修复不碰还开着的停靠：那一站的车可能还在等它们。启动检查不因此放宽，照旧拒绝。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheStartupRepairLeavesLinesOfAnOpenStopAlone()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await AddStraysAsync(fixture, await CurrentStopAsync(fixture, FirstDemandId), current: false);

        await using ServiceProvider services = HostServices(fixture);

        await StopScopedOutbox.FenceEndedAtStartupAsync(services, Token);

        await AssertPendingAsync(fixture, StrayEntryId, StrayRejectionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProtocolOutboxIdentityStartupCheck.EnsureAsync(services, Token));
    }

    /// <summary>
    /// 主机的启动顺序（审查 F1）：库迁移之后、发件箱身份检查之前跑启动修复。这一行回退，修之前留下遗留行的库换协议身份时照旧起不来，
    /// 而上面两条测试直接调入口、看不见 <c>Program.cs</c>。同 <c>SlotFaultDeclarationTests.TheHostSettlesAnsweredDeclarationsAfterMigratingAndBeforeTheOutboxIdentityCheck</c>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public void TheHostFencesEndedStopLinesAfterMigratingAndBeforeTheOutboxIdentityCheck()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "src", "ControlServer.Host", "Program.cs"));
        int Line(string call)
        {
            int[] found = [.. lines.Select((line, index) => (line.Trim(), index))
                .Where(item => !item.Item1.StartsWith("//", StringComparison.Ordinal)
                               && item.Item1.StartsWith("await " + call + "(app.Services", StringComparison.Ordinal))
                .Select(item => item.index)];
            return Assert.Single(found);
        }

        int migrate = Line("EnsureDatabaseAsync");
        int repair = Line("StopScopedOutbox.FenceEndedAtStartupAsync");
        int identity = Line("ProtocolOutboxIdentityStartupCheck.EnsureAsync");

        Assert.True(migrate < repair, $"the repair at line {repair + 1} runs before the migration at line {migrate + 1}");
        Assert.True(repair < identity, $"the repair at line {repair + 1} runs after the identity check at line {identity + 1}");
    }

    /// <summary>本停靠较早一版的录入请求与一条拒收，未确认、未退役。</summary>
    private static async Task AddStraysAsync(RuntimeFixture fixture, JourneyStopRow stop, bool current)
    {
        fixture.Context.ProtocolOutbox.Add(Stray(
            fixture, StrayEntryId, "SublotEntryRequested", current,
            new
            {
                operationSessionId = stop.OperationSessionId,
                stationId = stop.StationId,
                worklistRevision = 0,
                expectedSublots = new[] { FirstSublot },
                entryMethods = EntryMethods,
                expiresOnRevisionChange = true
            }));
        fixture.Context.ProtocolOutbox.Add(Stray(fixture, StrayRejectionId, "SublotRejected", current, Rejection(stop)));
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static object Rejection(JourneyStopRow stop) => new
    {
        demandId = (string?)null,
        operationSessionId = stop.OperationSessionId,
        problem = new { reasonCode = "WORKLIST_REVISION_STALE" },
        currentWorklistRevision = 0,
        rejectedSublot = FirstSublot
    };

    /// <summary>车载端扫码前取消 <paramref name="demandId"/>：授权、全空结果，走 <c>OnboardRecoveryCoordinator</c> → <c>PickupStopTermination</c>。</summary>
    private static async Task CancelBeforeScanAsync(RuntimeFixture fixture, string demandId, string cancellationId)
    {
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
        OnboardConnectionState state = StopEndedJourneyContinuesTests.Connection(fixture);
        object @operator = new { operatorId = "OP-001", verificationMethod = "BADGE", verifiedAt = fixture.Clock.GetUtcNow() };
        string authorization = await processor.ProcessAsync(
            StopEndedJourneyContinuesTests.Envelope(
                fixture, JourneyPlanBuilder.StableGuid(cancellationId, "request"), "LoadCancellationStartRequested",
                new { cancellationId, demandId, slotOperationAttemptId = (string?)null, @operator, reason = "Nothing to load at this stop." }),
            state, Token);
        Assert.Equal(
            "AUTHORIZED",
            StopEndedJourneyContinuesTests.FirstLinePayload(authorization).GetProperty("decision").GetString());
        await processor.ProcessAsync(
            StopEndedJourneyContinuesTests.Envelope(
                fixture, JourneyPlanBuilder.StableGuid(cancellationId, "result"), "LoadCancellationResult",
                new
                {
                    cancellationId,
                    demandId,
                    slotOperationAttemptId = (string?)null,
                    overallOutcome = "ALL_EMPTY",
                    slotResults = Array.Empty<object>(),
                    observedAt = Now
                }),
            state, Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(
            JourneyDemandStatuses.Terminated, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, demandId)).Status);
    }

    private static ProtocolOutboxRow Stray(RuntimeFixture fixture, string messageId, string messageType, bool current, object payload) => new()
    {
        MessageId = messageId,
        MessageType = messageType,
        PayloadJson = JsonSerializer.Serialize(new
        {
            protocolVersion = current ? ProtocolCandidateIdentity.ProtocolVersion : 3,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = current ? ProtocolCandidateIdentity.ReleaseVersion : "2.0.0",
            protocolReleaseManifestSha256 = current ? ProtocolCandidateIdentity.ManifestSha256 : V3Manifest,
            messageType,
            messageId,
            correlationId = (string?)null,
            agvId = fixture.Options.AgvId,
            sessionGeneration = 1,
            sentAt = "2026-10-15T07:13:00Z",
            payload
        }),
        CreatedAt = fixture.Clock.GetUtcNow()
    };

    /// <summary>主机启动时那几步拿到的服务：同一个库、夹具的钟。</summary>
    private static ServiceProvider HostServices(RuntimeFixture fixture)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(fixture.Clock);
        services.AddScoped(_ => new ControlServerDbContext(fixture.DbOptionsForTests));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("ControlServer.sln not found above the test output.");
    }

    /// <summary>
    /// 另外以站为生命周期的出站命令（审查说明 2）：旅程终态后，仓位命令、离站核验、离站结果都没有待补发的。
    /// </summary>
    /// <remarks>
    /// 清单快照（<c>CurrentStopWorklistSnapshot</c>）不在这里断言：它是按修订号递增的快照，车收到就确认、只认更高一版，不因「站已结束」拒收；
    /// 夹具里没有车来确认，本站发过的各版都留着未确认，这是夹具的样子，不是遗留。
    /// </remarks>
    private static async Task AssertOtherStopScopedTypesSettledAsync(RuntimeFixture fixture)
    {
        string[] commands = ["SlotOperationCommand", "PreDepartureSafetyCheck", "CurrentStopDepartureResult"];
        ProtocolOutboxRow[] pending = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => commands.Contains(row.MessageType) && row.AcknowledgedAt == null && row.FencedAt == null)
            .ToArrayAsync(Token);
        Assert.All(pending, row => Assert.Fail($"{row.MessageType} {row.MessageId} is still pending replay after the journey ended."));
    }

    private static async Task<ProtocolOutboxRow[]> PendingStopScopedAsync(RuntimeFixture fixture) =>
        await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => StopScopedTypes.Contains(row.MessageType) && row.AcknowledgedAt == null && row.FencedAt == null)
            .ToArrayAsync(Token);

    private static async Task AssertFencedAsync(RuntimeFixture fixture, params string[] messageIds)
    {
        foreach (string messageId in messageIds)
        {
            ProtocolOutboxRow row = await fixture.Context.ProtocolOutbox.AsNoTracking()
                .SingleAsync(item => item.MessageId == messageId, Token);
            Assert.True(
                row.FencedAt is not null || row.AcknowledgedAt is not null,
                $"{row.MessageType} {messageId} is still pending replay after its stop ended.");
        }
    }

    private static async Task AssertPendingAsync(RuntimeFixture fixture, params string[] messageIds)
    {
        foreach (string messageId in messageIds)
        {
            ProtocolOutboxRow row = await fixture.Context.ProtocolOutbox.AsNoTracking()
                .SingleAsync(item => item.MessageId == messageId, Token);
            Assert.Null(row.FencedAt);
            Assert.Null(row.AcknowledgedAt);
        }
    }
}
