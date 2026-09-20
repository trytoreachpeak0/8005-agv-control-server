using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 批次7-03（control-server#208）的主证据：推进改成由当前停靠驱动之后，单需求旅程两个停靠发出去的每一条报文与每一张 RIoT 订单，
/// 与改动前逐字相同。
/// </summary>
/// <remarks>
/// <para>
/// 本票把清单、录入请求、计划、装卸命令改从停靠表与从属需求表生成，修订号改取按车计数器。这些都是车载端看得见的东西：
/// 消息 id 变了，重连后的补发就认不回同一条；修订号变了，车载端按消息类型记住的已采纳修订号会回退，直接
/// <c>SNAPSHOT_REVISION_REGRESSION</c> 断会话。所以「行为不变」在这张票上不是一句结论，是一份逐字对照。
/// </para>
/// <para>
/// 对照的粒度见 <see cref="WirePin"/>：每条出站报文的 messageId、类型、关联 id、会话代次、修订号、确认与作废状态，
/// 加上规范化后的载荷全文与它的 SHA-256；RIoT 订单则是 <c>UpperId</c> 与它冻结的那几列。
/// </para>
/// <para>
/// <b>每条路径都自己驱动，不走 <c>RuntimeFixture</c> 的 <c>AdvanceTo*</c> 快捷方法。</b>那些方法用
/// <c>Guid.NewGuid()</c> 造入站 messageId，而装货命令的 <c>correlationId</c> 就是录入提交的 messageId——随机值一进载荷，
/// 每跑一次摘要都不同，对照就没了意义。这里每一条入站报文的 id 都写死在常量里。
/// </para>
/// </remarks>
public sealed class Batch7StopDrivenAdvanceWireParityTests
{
    private const string FirstDemandId = "10000000-0000-4000-8000-000000000001";
    private const string SecondDemandId = "10000000-0000-4000-8000-000000000002";
    private const string FirstSublot = "SUBLOT-001";
    private const string SecondSublot = "SUBLOT-002";

    /// <summary>第一趟的两条入站 id；第二趟用 <c>...11</c>／<c>...12</c>，好在 pin 里一眼看出是哪一趟。</summary>
    private const string FirstSubmissionId = "20000000-0000-4000-8000-000000000001";

    private const string FirstSafetyResultId = "20000000-0000-4000-8000-000000000002";
    private const string SecondSubmissionId = "20000000-0000-4000-8000-000000000011";
    private const string SecondSafetyResultId = "20000000-0000-4000-8000-000000000012";

    [Fact]
    public async Task ANormalJourneySendsTheSameElevenLinesAndTheSameTwoOrders()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);

        JourneyRuntimeRow runtime = await RunJourneyToCompletionAsync(
            fixture, FirstDemandId, FirstSublot, FirstSubmissionId, FirstSafetyResultId);

        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        await WirePin.AssertMatchesAsync(fixture.Context, "normal-journey", fixture.Peer.Lines);
    }

    [Fact]
    public async Task TwoJourneysOnOneVehicleKeepTheirRevisionStreamsMonotonic()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await RunJourneyToCompletionAsync(
            fixture, FirstDemandId, FirstSublot, FirstSubmissionId, FirstSafetyResultId);

        fixture.Catalog.Set(fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9)));
        fixture.BoxCounts.Set(SecondSublot, 4);
        JourneyRuntimeRow second = await RunJourneyToCompletionAsync(
            fixture, SecondDemandId, SecondSublot, SecondSubmissionId, SecondSafetyResultId);

        Assert.Equal(JourneyRuntimeStage.Completed, second.Stage);
        await WirePin.AssertMatchesAsync(fixture.Context, "two-journeys-one-vehicle", fixture.Peer.Lines);
    }


    /// <summary>
    /// 到了取货站却没人扫码，站点期限走完，服务端自己结束这个停靠。推进段独力走完的一条终结路径——本票把这段挪到「当前停靠」上，
    /// 所以它发过什么、没发过什么都要一字不差：特别是一条装卸命令都不该有。
    /// </summary>
    [Fact]
    public async Task AStopEndedByItsStationDeadlineSendsTheSameLines()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);

        await TickAndRunAsync(fixture);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupUpperId, runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await TickAndRunAsync(fixture);
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await TickAndRunAsync(fixture);

        runtime = await fixture.RuntimeAsync(FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", runtime.BlockReasonCode);
        await WirePin.AssertMatchesAsync(fixture.Context, "station-deadline", fixture.Peer.Lines);
    }

    /// <summary>
    /// 车已经装好、等着离站核验的答复，这时车上的安全状态版本变了，原来那次核验就过期了：服务端作废它，换一个新身份再问一次，
    /// 拿到新答复才放行。
    /// </summary>
    /// <remarks>
    /// 新身份是 <c>StableGuid(旧 id, "reissued-after-expiry")</c> 派生的，而本票动的正是「id 从哪里来」这件事——
    /// 离站核验的消息 id 与核验 id 现在挂在取货停靠行上（<c>JourneyStopRow.DepartureSafetyCheckMessageId</c> 与
    /// <c>DepartureSafetyCheckId</c>），重发时两处都要跟着换。这条路径专门盯它。
    /// </remarks>
    [Fact]
    public async Task AnExpiredDepartureCheckIsRetiredAndAskedAgainUnderTheSameDerivedIdentity()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);

        JourneyRuntimeRow runtime = await AdvanceToDepartureSafetyAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        string expiredCheckId = runtime.PreDepartureSafetyCheckId;
        string expiredMessageId = runtime.PreDepartureSafetyCheckMessageId;
        await AddInboxAsync(
            fixture, FirstSafetyResultId, "PreDepartureSafetyCheckResult",
            SafeDepartureAnswer(fixture, expiredCheckId, safetyStateVersion: 7), expiredCheckId);
        await fixture.AddSafetyStateChangedAsync(8, departureSafe: true, vehicleStopped: true);
        await TickAndRunAsync(fixture);

        runtime = await fixture.RuntimeAsync(FirstDemandId);
        Assert.Equal("PREDEPARTURE_CHECK_EXPIRED", runtime.BlockReasonCode);
        Assert.NotEqual(expiredCheckId, runtime.PreDepartureSafetyCheckId);
        Assert.NotEqual(expiredMessageId, runtime.PreDepartureSafetyCheckMessageId);
        await WirePin.AssertMatchesAsync(fixture.Context, "departure-check-expired", fixture.Peer.Lines);
    }

    /// <summary>
    /// 车载端断线、换一个会话代次重连，服务端把还没被确认的报文按原 id 重发一遍。
    /// </summary>
    /// <remarks>
    /// 这是本票风险最集中的一条：重放集合今天是写死的 11 个 id（<c>RuntimeMessageIds</c>），本票改成按停靠枚举。
    /// 少枚举一个，那条报文就永远不再补发，车载端等一个再也不来的东西；多枚举一个或者换了 id，车载端会当成没见过的新消息。
    /// 所以这里既钉发件箱里每行的会话代次，也钉真正写到线上的那一串——后者是唯一能看见「补发了哪几条、按什么顺序」的地方。
    /// </remarks>
    [Fact]
    public async Task AReconnectReplaysExactlyTheUnacknowledgedLinesOfThisJourney()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);

        await TickAndRunAsync(fixture);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupUpperId, runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await TickAndRunAsync(fixture);

        await fixture.ReconnectAsync(2);
        await fixture.AdvanceSessionAsync(2);
        await TickAndRunAsync(fixture);

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync(FirstDemandId)).Stage);
        await WirePin.AssertMatchesAsync(fixture.Context, "reconnect-replay", fixture.Peer.Lines);
    }

    /// <summary>驱动到「已装好、等离站核验答复」为止。</summary>
    private static async Task<JourneyRuntimeRow> AdvanceToDepartureSafetyAsync(
        RuntimeFixture fixture,
        string demandId,
        string sublot,
        string submissionId)
    {
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(demandId);
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupUpperId, runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await TickAndRunAsync(fixture);
        await AddInboxAsync(fixture, submissionId, "SublotSubmitted", SublotSubmission(fixture, runtime, sublot));
        await TickAndRunAsync(fixture);
        await ApplySafeResultAsync(fixture, demandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        return await fixture.RuntimeAsync(demandId);
    }

    private static object SublotSubmission(RuntimeFixture fixture, JourneyRuntimeRow runtime, string sublot) => new
    {
        operationSessionId = runtime.OperationSessionId,
        stationId = runtime.PickupStationId,
        worklistRevision = runtime.WorklistRevision,
        sublot,
        entryMethod = "SCANNER",
        @operator = new
        {
            operatorId = "OP-001",
            verificationMethod = "BADGE",
            verifiedAt = fixture.Clock.GetUtcNow()
        }
    };

    private static object SafeDepartureAnswer(RuntimeFixture fixture, string checkId, long safetyStateVersion) => new
    {
        preDepartureSafetyCheckId = checkId,
        outcome = "SAFE",
        observedAt = fixture.Clock.GetUtcNow(),
        safetyStateVersion,
        validUntil = fixture.Clock.GetUtcNow().AddMinutes(1),
        safety = new
        {
            departureSafe = true,
            vehicleStopped = true,
            allTargetSlotsLocked = true,
            allUnlockOutputsReset = true,
            unknownPresent = false,
            reasonCodes = Array.Empty<string>()
        }
    };

    /// <summary>
    /// 一条入站报文，id 由调用方写死。<c>RuntimeFixture.AddInboxAsync</c> 做不了这件事：它开头读一次唯一的旅程行
    /// （读出来并不用），同车跑第二趟时库里有两行，直接抛 <c>Sequence contains more than one element</c>。
    /// </summary>
    private static async Task AddInboxAsync(
        RuntimeFixture fixture,
        string messageId,
        string messageType,
        object payload,
        string? correlationId = null)
    {
        fixture.Context.ProtocolInbox.Add(new ProtocolInboxRow
        {
            MessageId = messageId,
            MessageType = messageType,
            RequestJson = JsonSerializer.Serialize(
                new
                {
                    protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                    profileId = ProtocolCandidateIdentity.ProfileId,
                    protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
                    protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                    messageType,
                    messageId,
                    correlationId,
                    agvId = fixture.Options.AgvId,
                    sessionGeneration = 1,
                    sentAt = Now,
                    payload
                },
                SerializerOptions),
            ContentHash = new string('a', 64),
            FirstResponseJson = "{}",
            ReceivedAt = fixture.Clock.GetUtcNow()
        });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 把钟往前拨一秒再跑一轮。拨钟是为了让发件箱里每条报文的 <c>CreatedAt</c> 落在不同的秒上——固定时钟下所有报文时刻相同，
    /// 「哪条先发」这个事实就没地方记，而重连补发的顺序正是本票要钉住的东西之一。
    /// </summary>
    private static async Task TickAndRunAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 一趟顺利的旅程，一步一步：到取货站、操作员录入、装货落定、离站核验、到关卡、卸货落定。
    /// </summary>
    private static async Task<JourneyRuntimeRow> RunJourneyToCompletionAsync(
        RuntimeFixture fixture,
        string demandId,
        string sublot,
        string submissionId,
        string safetyResultId)
    {
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(demandId);

        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupUpperId, runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await TickAndRunAsync(fixture);

        await AddInboxAsync(fixture,
            submissionId,
            "SublotSubmitted",
            new
            {
                operationSessionId = runtime.OperationSessionId,
                stationId = runtime.PickupStationId,
                worklistRevision = runtime.WorklistRevision,
                sublot,
                entryMethod = "SCANNER",
                @operator = new
                {
                    operatorId = "OP-001",
                    verificationMethod = "BADGE",
                    verifiedAt = fixture.Clock.GetUtcNow()
                }
            });
        await TickAndRunAsync(fixture);

        await ApplySafeResultAsync(fixture, demandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);

        await AddInboxAsync(fixture,
            safetyResultId,
            "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = runtime.PreDepartureSafetyCheckId,
                outcome = "SAFE",
                observedAt = fixture.Clock.GetUtcNow(),
                safetyStateVersion = 7,
                validUntil = fixture.Clock.GetUtcNow().AddMinutes(1),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                }
            },
            runtime.PreDepartureSafetyCheckMessageId);
        await TickAndRunAsync(fixture);

        fixture.Riot.SetSuccessfulArrival("TO_GATE", runtime.GateUpperId, TaskTypeStationRuntimeSeed.GateStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with
        {
            CurrentStationId = TaskTypeStationRuntimeSeed.GateStationRiotId
        };
        await TickAndRunAsync(fixture);

        await ApplySafeResultAsync(fixture, demandId, SlotOperationType.Unload, SlotBusinessState.Empty);
        await TickAndRunAsync(fixture);
        return await fixture.RuntimeAsync(demandId);
    }

    /// <summary>
    /// <c>RuntimeFixture.OperationAsync</c> 按操作类型取唯一一条，同车第二趟就有两条装货记录了——所以这里按需求取。
    /// </summary>
    private static async Task ApplySafeResultAsync(
        RuntimeFixture fixture,
        string demandId,
        SlotOperationType type,
        SlotBusinessState state)
    {
        StationOperationRow operation = await fixture.Context.StationOperations.AsNoTracking()
            .SingleAsync(
                row => row.DemandId == demandId && row.OperationType == type,
                TestContext.Current.CancellationToken);
        await fixture.ApplySafeResultAsync(operation, type, state);
    }
}
