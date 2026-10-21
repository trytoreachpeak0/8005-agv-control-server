using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 车上有货时建立的车载会话：那几个仓被本服务端在这一代会话里安全卸空之后，同一会话里就回到可用，不必等重连
/// （control-server#802）。
/// </summary>
/// <remarks>
/// <para>
/// 仓位可用性是会话基线（握手的 <c>CapabilitySnapshot</c> 与这一代最小版本的 <c>SafetyStateSnapshot</c>），基线只认
/// <c>EMPTY</c> 的仓。这套设计默认建会话时车是空的；车上有货时建的会话，有货的仓整场都不进可用集合，卸空了也回不来。
/// 10-21 下午 agv02 第 36 代会话建于 13:54:04，那时 1、2、5、6、7 号仓有货，之后待送点只挂一条 2 篮、1 篮的需求就判一侧满。
/// </para>
/// <para>
/// 回到可用只认本服务端自己的卸货结果：<c>StationOperation</c> 已 <c>Committed</c> 的 UNLOAD，结果在这一代基线之后
/// （<c>OperationResult.ObservedAt</c> 晚于基线快照的 <c>observedAt</c>，两者都是车载端的钟），证据里该仓空、锁住、开锁输出复位。
/// 会话中途催读的快照（control-server#142）照旧不参与：作业中读到的空不算数。
/// </para>
/// </remarks>
public sealed class UnloadedSlotAvailabilityTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 现场回放（10-21 agv02 第 36 代会话）：基线 1、2、5、6、7 有货，3、4、8 空；13:54:59 卸 5、6，13:56:05 卸 7，
    /// 13:59:02 卸 1、2。卸之前可用的只有 3、4、8；三次卸货都落定之后八个仓都可用，两侧各 4。
    /// 14:08:32 只挂一条 2 篮的需求时 FRONT 本该还剩 2 个空仓，14:09:07 只挂 1 篮时 REAR 本该还剩 3 个——现状是 0 与 0，判满。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task TheAgv02SessionOfOctober21GetsBackEverySlotItUnloaded()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await OccupyInTheBaselineAsync(fixture, generation: 1, 1, 2, 5, 6, 7);

        Assert.Equal("3,4,8", Slots(await ReadAsync(fixture)));

        await SeedUnloadAsync(fixture, [5, 6], Now.AddSeconds(55));
        await SeedUnloadAsync(fixture, [7], Now.AddSeconds(121));
        await SeedUnloadAsync(fixture, [1, 2], Now.AddSeconds(298));
        await StayLiveAsync(fixture, TimeSpan.FromMinutes(14));

        OnboardDispatchFacts? onboard = await ReadAsync(fixture);
        Assert.Equal("1,2,3,4,5,6,7,8", Slots(onboard));
        (int front, int rear) = await SidesAsync(fixture, onboard!);
        Assert.Equal((4, 4), (front, rear));
    }

    /// <summary>
    /// 派车那一侧（票面第 1 条的红证据）：前侧 1～4 握手时都有货，需要前侧 2 篮的需求以容量拒绝；同一会话里本服务端把这四个仓卸空之后，
    /// 同一条需求就被受理，不等重连。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task ADemandRefusedForCapacityIsAcceptedOnceTheSameSessionUnloadsThoseSlots()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await OccupyInTheBaselineAsync(fixture, generation: 1, 1, 2, 3, 4);
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 8);

        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal("SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE", (await fixture.BacklogAsync(DemandId)).ReasonCode);

        await SeedUnloadAsync(fixture, [1, 2, 3, 4], Now.AddSeconds(30));
        await StayLiveAsync(fixture, TimeSpan.FromMinutes(1));
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal("ACCEPTED", (await fixture.BacklogAsync(DemandId)).ReasonCode);
    }

    /// <summary>
    /// 作业中的仓不误判（control-server#142 的那种快照）：会话中途催读的快照把 1、2 号读成空、锁住，但本服务端还没有它们落定的卸货结果
    /// ——卸货命令发了、结果没回——它们仍不可用。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task AMidSessionSnapshotReadingASlotEmptyDuringItsUnloadDoesNotMakeItAvailable()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await OccupyInTheBaselineAsync(fixture, generation: 1, 1, 2);
        await SeedUnloadAsync(fixture, [1, 2], observedAt: null, status: StationOperationStatus.Prepared);
        await AddMidSessionSnapshotReadingEverySlotEmptyAsync(fixture, safetyStateVersion: 8);

        Assert.Equal("3,4,5,6,7,8", Slots(await ReadAsync(fixture)));
    }

    /// <summary>
    /// 只认本服务端在这一代基线之后安全落定的卸货：没落定（要人工恢复）、只作记录（不是本服务端的命令或已被取消的作业）、
    /// 已被替代的那一条、落定在基线之前（上一代会话卸的，之后车上又有了货）、装货结果，都不让有货的仓回到可用。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [InlineData("recovery-required")]
    [InlineData("failed")]
    [InlineData("historical-only")]
    [InlineData("superseded")]
    [InlineData("before-the-baseline")]
    [InlineData("at-the-baseline")]
    [InlineData("a-load")]
    [InlineData("evidence-not-locked")]
    [InlineData("another-vehicle")]
    public async Task OnlyTheServersOwnSafelyCommittedUnloadAfterTheBaselineCounts(string shape)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await OccupyInTheBaselineAsync(fixture, generation: 1, 1, 2);
        DateTimeOffset after = Now.AddSeconds(30);
        await (shape switch
        {
            "recovery-required" => SeedUnloadAsync(fixture, [1, 2], after, status: StationOperationStatus.RecoveryRequired),
            "failed" => SeedUnloadAsync(fixture, [1, 2], after, status: StationOperationStatus.Failed),
            "historical-only" => SeedUnloadAsync(fixture, [1, 2], after, historicalOnly: true),
            "superseded" => SeedUnloadAsync(fixture, [1, 2], after, supersededBy: Guid.NewGuid().ToString("D")),
            "before-the-baseline" => SeedUnloadAsync(fixture, [1, 2], Now.AddSeconds(-30)),
            "at-the-baseline" => SeedUnloadAsync(fixture, [1, 2], Now),
            // 证据读空也不算：认的是卸货这件事，不是证据里的一个空字。
            "a-load" => SeedUnloadAsync(fixture, [1, 2], after, type: SlotOperationType.Load, evidenceState: SlotBusinessState.Empty),
            "evidence-not-locked" => SeedUnloadAsync(fixture, [1, 2], after, doorLocked: false),
            "another-vehicle" => SeedUnloadAsync(fixture, [1, 2], after, agvId: "老厂前线新多仓位2"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        });
        await StayLiveAsync(fixture, TimeSpan.FromMinutes(1));

        Assert.Equal("3,4,5,6,7,8", Slots(await ReadAsync(fixture)));
    }

    /// <summary>
    /// 卸空回到可用的只是「有货」这一条：握手时被禁用、或开锁输出没复位的仓，卸过也不回来。5 号是对照：同样有货、同一次卸空，
    /// 它回来了，所以卸货记录确实读过（基线里没有一个只因有货而不可用的仓时，根本不读）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task ASlotTheBaselineRefusesForAnythingButItsCargoStaysOutAfterAnUnload()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await OccupyInTheBaselineAsync(fixture, generation: 1, 1, 2, 5);
        await RewriteBaselineSlotAsync(fixture, generation: 1, slot: 1, "administrativeAvailability", "DISABLED");
        await RewriteBaselineSlotAsync(fixture, generation: 1, slot: 2, "unlockOutputState", "ACTIVE");
        await SeedUnloadAsync(fixture, [1, 2, 5], Now.AddSeconds(30));
        await StayLiveAsync(fixture, TimeSpan.FromMinutes(1));

        Assert.Equal("3,4,5,6,7,8", Slots(await ReadAsync(fixture)));
    }

    /// <summary>
    /// 不重复扣（票面第 3 条）：握手时 1、2 号有货，卸空后回到可用；新需求扫码时分到的正是 1、2，装上之后前侧还剩 3、4 两个——
    /// 已装的货只由账本按分到的仓位扣一次，可用集合不因再装上而缩回去。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task ASlotGotBackAndLoadedAgainIsTakenOnceByTheLedger()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await OccupyInTheBaselineAsync(fixture, generation: 1, 1, 2);
        await SeedUnloadAsync(fixture, [1, 2], Now.AddMilliseconds(500));
        fixture.Catalog.Set(fixture.Demand(DemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 8);

        await ArriveAtPickupAsync(fixture, DemandId);
        await EnterSublotAsync(fixture, DemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, DemandId);

        Assert.Equal("[1,2]", (await MembershipOfAsync(fixture, DemandId)).TargetSlotsJson);
        OnboardDispatchFacts? onboard = await ReadAsync(fixture);
        Assert.Equal("1,2,3,4,5,6,7,8", Slots(onboard));
        Assert.Equal((2, 4), await SidesAsync(fixture, onboard!));
    }

    /// <summary>
    /// 握手时空、会话中装上货的仓仍只由账本扣（票面第 3 条的另一个方向）：基线全空，装上 1、2 之后前侧剩 2，不是 0。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task ASlotEmptyAtTheHandshakeAndLoadedInTheSessionIsTakenOnce()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(DemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 8);

        await ArriveAtPickupAsync(fixture, DemandId);
        await EnterSublotAsync(fixture, DemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, DemandId);

        OnboardDispatchFacts? onboard = await ReadAsync(fixture);
        Assert.Equal("1,2,3,4,5,6,7,8", Slots(onboard));
        Assert.Equal((2, 4), await SidesAsync(fixture, onboard!));
    }

    /// <summary>
    /// 会话因本服务端自己的在途单而未就绪（真车载端行驶全程都是这个状态）：这期间读不到车载端事实，派车不拿它判；
    /// 车停下、同一代会话回到 Ready 之后，卸空回来的仓照样可用，不因中间未就绪过而丢掉。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task SlotsGotBackSurviveTheSessionBeingNotReadyOnTheServersOwnOrder()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await OccupyInTheBaselineAsync(fixture, generation: 1, 1, 2, 3, 4);
        await SeedUnloadAsync(fixture, [1, 2, 3, 4], Now.AddSeconds(30));
        await StayLiveAsync(fixture, TimeSpan.FromMinutes(1));

        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        Assert.Null(await ReadAsync(fixture));
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 8);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.NotEqual("ACCEPTED", (await fixture.BacklogAsync(DemandId)).ReasonCode);

        await BackToReadyAsync(fixture);
        await StayLiveAsync(fixture, TimeSpan.FromSeconds(90));
        Assert.Equal("1,2,3,4,5,6,7,8", Slots(await ReadAsync(fixture)));
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal("ACCEPTED", (await fixture.BacklogAsync(DemandId)).ReasonCode);
    }

    /// <summary>
    /// 走真实写库路径（审查要求）：一趟装上 1、2 号，开往关卡途中车重连、带着货握手；到关卡后卸货结果经
    /// <c>WireToGateStore.ApplyOperationResultAsync</c> 落定。结果时间晚于新基线，1、2 并回；与基线同一时刻，基线优先、不并回。
    /// 上面的用例都是照它写库的样子手工塞行，这一条证明真实写下的行形状认得出。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUnloadAppliedByTheStoreBringsBackSlotsLoadedBeforeAReconnect(bool observedAfterTheBaseline)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(DemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 8);
        await ArriveAtPickupAsync(fixture, DemandId);
        await EnterSublotAsync(fixture, DemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, DemandId);
        await AnswerDepartureSafetyAsync(fixture, DemandId, FirstSafetyResultId);
        Assert.Equal("[1,2]", (await MembershipOfAsync(fixture, DemandId)).TargetSlotsJson);

        DateTimeOffset baselineAt = fixture.Clock.GetUtcNow();
        await ReconnectAsync(fixture, generation: 2, baselineAt);
        await OccupyInTheBaselineAsync(fixture, generation: 2, 1, 2);
        await ArriveAtCurrentStopAsync(fixture, DemandId, "TO_GATE");
        StationOperationRow unload = await fixture.Context.StationOperations.AsNoTracking()
            .SingleAsync(row => row.DemandId == DemandId && row.OperationType == SlotOperationType.Unload, Token);
        await fixture.ApplySafeResultAsync(
            unload,
            SlotOperationType.Unload,
            SlotBusinessState.Empty,
            observedAfterTheBaseline ? fixture.Clock.GetUtcNow() : baselineAt);
        await fixture.HearFromPeerAsync();

        Assert.Equal(StationOperationStatus.Committed, (await fixture.Context.StationOperations.AsNoTracking()
            .SingleAsync(row => row.SlotOperationAttemptId == unload.SlotOperationAttemptId, Token)).Status);
        Assert.Equal(
            observedAfterTheBaseline ? "1,2,3,4,5,6,7,8" : "3,4,5,6,7,8",
            Slots(await ReadAsync(fixture)));
    }

    /// <summary>
    /// 重连后行为不变：新一代会话的基线说了算。车空着重连，八个仓都可用；车带着别的货重连（上一代卸过 1、2，之后又有了货），
    /// 上一代的卸货结果早于新基线，不让它们回来。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReconnectTakesTheNewBaselineAsItIs(bool cargoAgainAtTheReconnect)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await OccupyInTheBaselineAsync(fixture, generation: 1, 1, 2);
        await SeedUnloadAsync(fixture, [1, 2], Now.AddSeconds(30));

        await ReconnectAsync(fixture, generation: 2, baselineAt: Now.AddMinutes(5));
        if (cargoAgainAtTheReconnect)
        {
            await OccupyInTheBaselineAsync(fixture, generation: 2, 1, 2);
        }
        await StayLiveAsync(fixture, TimeSpan.FromMinutes(6));

        Assert.Equal(
            cargoAgainAtTheReconnect ? "3,4,5,6,7,8" : "1,2,3,4,5,6,7,8",
            Slots(await ReadAsync(fixture)));
    }

    private static string Slots(OnboardDispatchFacts? onboard) =>
        onboard is null ? "(no facts)" : string.Join(',', onboard.AvailableSlots);

    private static Task<OnboardDispatchFacts?> ReadAsync(RuntimeFixture fixture) =>
        new OnboardDispatchFactsReader(
                fixture.Context, Microsoft.Extensions.Options.Options.Create(fixture.Options), fixture.Clock)
            .ReadOnboardFactsAsync(fixture.Options.AgvId, Token);

    private static async Task<(int Front, int Rear)> SidesAsync(RuntimeFixture fixture, OnboardDispatchFacts onboard)
    {
        string agvId = fixture.Options.AgvId;
        VehicleSlotPositions? positions = await new VehicleSlotPositionReader(fixture.Context).ReadAsync(agvId, Token);
        JourneyAwareSlotLedger ledger = new(fixture.Context);
        return (
            await ledger.ReadAvailableSlotCountAsync(agvId, onboard, positions, "FRONT", Token),
            await ledger.ReadAvailableSlotCountAsync(agvId, onboard, positions, "REAR", Token));
    }

    private static Task<JourneyDemandRow> MembershipOfAsync(RuntimeFixture fixture, string demandId) =>
        fixture.Context.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(row => row.DemandId == demandId, Token);

    /// <summary>把时钟拨到基线之后 <paramref name="since"/>（只往前拨），并让这一代会话刚说过话，好让会话存活检查不挡。</summary>
    private static async Task StayLiveAsync(RuntimeFixture fixture, TimeSpan since)
    {
        TimeSpan step = Now + since - fixture.Clock.GetUtcNow();
        Assert.True(step >= TimeSpan.Zero, "The fixture clock only moves forward.");
        fixture.Clock.Advance(step);
        await fixture.HearFromPeerAsync();
    }

    /// <summary>这一代会话握手时这几个仓有货：两张基线快照里都写成 <c>OCCUPIED</c>，其余字段不动。</summary>
    private static async Task OccupyInTheBaselineAsync(RuntimeFixture fixture, long generation, params int[] slots)
    {
        foreach (int slot in slots)
        {
            await RewriteBaselineSlotAsync(fixture, generation, slot, "physicalState", "OCCUPIED");
        }
    }

    private static async Task RewriteBaselineSlotAsync(
        RuntimeFixture fixture, long generation, int slot, string field, string value)
    {
        ProtocolInboxRow[] rows = await fixture.Context.ProtocolInbox
            .Where(row => row.MessageType == "CapabilitySnapshot" || row.MessageType == "SafetyStateSnapshot")
            .ToArrayAsync(Token);
        foreach (ProtocolInboxRow row in rows)
        {
            JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
            if (root["sessionGeneration"]!.GetValue<long>() != generation)
            {
                continue;
            }
            foreach (JsonNode? state in root["payload"]!["slotStates"]!.AsArray())
            {
                if (state!["slotNo"]!.GetValue<int>() == slot)
                {
                    state[field] = value;
                }
            }
            row.RequestJson = root.ToJsonString(SerializerOptions);
        }
        await fixture.Context.SaveChangesAsync(Token);
    }

    /// <summary>
    /// 本服务端的一次卸货作业与车回的结果，按 <c>WireToGateStore.ApplyOperationResultAsync</c> 写下的样子：作业行的状态，
    /// 与一条结果行（证据是 <see cref="SlotPhysicalEvidence"/> 的数组）。<paramref name="observedAt"/> 为空即结果还没回。
    /// </summary>
    private static async Task SeedUnloadAsync(
        RuntimeFixture fixture,
        int[] slots,
        DateTimeOffset? observedAt,
        StationOperationStatus status = StationOperationStatus.Committed,
        bool historicalOnly = false,
        string? supersededBy = null,
        SlotOperationType type = SlotOperationType.Unload,
        bool doorLocked = true,
        string? agvId = null,
        SlotBusinessState? evidenceState = null)
    {
        string attemptId = Guid.NewGuid().ToString("D");
        string evidence = JsonSerializer.Serialize(slots.Select(slot => new SlotPhysicalEvidence(
            slot,
            evidenceState ?? (type == SlotOperationType.Unload ? SlotBusinessState.Empty : SlotBusinessState.Occupied),
            doorLocked,
            true)));
        fixture.Context.StationOperations.Add(new StationOperationRow
        {
            SlotOperationAttemptId = attemptId,
            DemandId = Guid.NewGuid().ToString("D"),
            SublotId = "SUBLOT-EARLIER",
            TargetSlotsJson = JsonSerializer.Serialize(slots),
            OperationType = type,
            ForcedRecoveryGeneration = 0,
            ContentHash = new string('a', 64),
            Status = status,
            EvidenceJson = observedAt is null ? null : evidence,
            CreatedAt = Now,
            CommittedAt = status == StationOperationStatus.Committed ? observedAt : null
        });
        if (observedAt is DateTimeOffset at)
        {
            fixture.Context.OperationResults.Add(new OperationResultRow
            {
                ResultId = Guid.NewGuid().ToString("D"),
                SlotOperationAttemptId = attemptId,
                AgvId = agvId ?? fixture.Options.AgvId,
                ForcedRecoveryGeneration = 0,
                ContentHash = new string('b', 64),
                ResultContentSha256 = new string('c', 64),
                OverallOutcome = status == StationOperationStatus.Committed ? "COMPLETED" : "FAILED",
                EvidenceJson = evidence,
                ObservedAt = at,
                HistoricalOnly = historicalOnly,
                ReceivedAt = at,
                SupersededByResultId = supersededBy
            });
        }
        await fixture.Context.SaveChangesAsync(Token);
    }

    /// <summary>看板催读时车回的快照（control-server#142）：下一个版本号，每个仓都按此刻 IO 读成空、锁住、复位。</summary>
    private static async Task AddMidSessionSnapshotReadingEverySlotEmptyAsync(RuntimeFixture fixture, long safetyStateVersion)
    {
        ProtocolInboxRow baseline = await fixture.Context.ProtocolInbox.AsNoTracking()
            .SingleAsync(row => row.MessageType == "SafetyStateSnapshot", Token);
        JsonObject root = JsonNode.Parse(baseline.RequestJson)!.AsObject();
        string messageId = Guid.NewGuid().ToString("D");
        root["messageId"] = messageId;
        JsonObject payload = root["payload"]!.AsObject();
        payload["safetyStateVersion"] = safetyStateVersion;
        payload["observedAt"] = fixture.Clock.GetUtcNow();
        foreach (JsonNode? state in payload["slotStates"]!.AsArray())
        {
            state!["physicalState"] = "EMPTY";
            state["lockState"] = "LOCKED";
            state["unlockOutputState"] = "RESET";
        }
        fixture.Context.ProtocolInbox.Add(new ProtocolInboxRow
        {
            MessageId = messageId,
            MessageType = "SafetyStateSnapshot",
            RequestJson = root.ToJsonString(SerializerOptions),
            ContentHash = new string('f', 64),
            FirstResponseJson = "{}",
            ReceivedAt = fixture.Clock.GetUtcNow()
        });
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(Token);
        session.SafetyRevision = safetyStateVersion;
        await fixture.Context.SaveChangesAsync(Token);
    }

    private static async Task BackToReadyAsync(RuntimeFixture fixture)
    {
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(Token);
        session.Readiness = SessionReadiness.Ready;
        session.ReasonCode = "READY";
        session.DepartureSafe = true;
        session.SafetyReasonCodesJson = "[]";
        session.SafetyUnknownPresent = false;
        session.UpdatedAt = fixture.Clock.GetUtcNow();
        await fixture.Context.SaveChangesAsync(Token);
    }

    /// <summary>
    /// 车重连，建起新一代会话：两张基线快照照第一代的抄一份，换代次、换 messageId、观测时刻改成 <paramref name="baselineAt"/>，
    /// 八个仓都按空写（要有货的用例之后再改）。
    /// </summary>
    private static async Task ReconnectAsync(RuntimeFixture fixture, long generation, DateTimeOffset baselineAt)
    {
        ProtocolInboxRow[] first = await fixture.Context.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "CapabilitySnapshot" || row.MessageType == "SafetyStateSnapshot")
            .ToArrayAsync(Token);
        foreach (ProtocolInboxRow row in first)
        {
            JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
            string messageId = Guid.NewGuid().ToString("D");
            root["messageId"] = messageId;
            root["sessionGeneration"] = generation;
            root["payload"]!["observedAt"] = baselineAt;
            foreach (JsonNode? state in root["payload"]!["slotStates"]!.AsArray())
            {
                state!["physicalState"] = "EMPTY";
            }
            fixture.Context.ProtocolInbox.Add(new ProtocolInboxRow
            {
                MessageId = messageId,
                MessageType = row.MessageType,
                RequestJson = root.ToJsonString(SerializerOptions),
                ContentHash = new string('f', 64),
                FirstResponseJson = "{}",
                ReceivedAt = baselineAt
            });
        }
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(Token);
        session.SessionGeneration = generation;
        session.UpdatedAt = baselineAt;
        await fixture.Context.SaveChangesAsync(Token);
    }
}
