using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 批次7-03（control-server#208）那几个测试类共用的驱动：一趟单需求旅程，一步一步，每一条入站报文的 messageId 都写死。
/// </summary>
/// <remarks>
/// 不走 <c>RuntimeFixture</c> 的 <c>AdvanceTo*</c> 快捷方法，因为那些方法用 <c>Guid.NewGuid()</c> 造入站 messageId，
/// 而装货命令的 <c>correlationId</c> 就是录入提交的那个 id——随机值一进载荷，<see cref="WirePin"/> 每跑一次摘要都不同。
/// 每轮推进前把钟往前拨一秒，好让发件箱里每条报文落在不同的秒上，「哪条先发」这个事实才有地方记。
/// </remarks>
internal static class Batch7StopDrivenAdvanceDriver
{
    internal const string FirstDemandId = "10000000-0000-4000-8000-000000000001";
    internal const string SecondDemandId = "10000000-0000-4000-8000-000000000002";
    internal const string FirstSublot = "SUBLOT-001";
    internal const string SecondSublot = "SUBLOT-002";
    internal const string FirstSubmissionId = "20000000-0000-4000-8000-000000000001";
    internal const string FirstSafetyResultId = "20000000-0000-4000-8000-000000000002";
    internal const string SecondSubmissionId = "20000000-0000-4000-8000-000000000011";
    internal const string SecondSafetyResultId = "20000000-0000-4000-8000-000000000012";

    /// <summary>
    /// 这条需求所属的那趟旅程。<b>不是按 <c>DemandId</c> 查旅程行</b>——被追加进来的需求没有自己的旅程行
    /// （批次7-06，control-server#211）。
    /// </summary>
    internal static async Task<JourneyRuntimeRow> JourneyOfAsync(RuntimeFixture fixture, string demandId)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journeyId = (await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId, token)).JourneyId;
        return await fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journeyId, token);
    }

    /// <summary>
    /// 这条需求所属旅程此刻的当前停靠。
    /// </summary>
    /// <remarks>
    /// <b>这个驱动取停靠行，不取旅程行，这是它与批次7-03 那一版唯一的口径差别。</b>旅程行上的
    /// <c>PickupUpperId</c>、<c>PickupStationId</c>、<c>PreDepartureSafetyCheckId</c> 这些都只描述<b>锚需求
    /// 那一个停靠</b>；产品代码早就改读停靠行了（<c>CheckArrivalAsync</c> 按 <c>stops.Current</c> 判到站，
    /// <c>DepartureCheckId</c> 读 <c>stop.DepartureSafetyCheckId</c>）。第一个停靠上两者是同源副本，所以
    /// 单需求旅程看不出差别——多一个停靠就全错，而且错得安静：报文寄到一个没人在等的地址上。
    /// </remarks>
    internal static async Task<JourneyStopRow> CurrentStopAsync(RuntimeFixture fixture, string demandId) =>
        (await JourneyStopCursor.LoadAsync(
            fixture.Context,
            await JourneyOfAsync(fixture, demandId),
            TestContext.Current.CancellationToken)).Current;

    /// <summary>把钟往前拨一秒再跑一轮。</summary>
    internal static async Task TickAndRunAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>把钟往前拨一秒再跑一轮，并要求这一轮抛出来——用来模拟推进到一半崩掉。</summary>
    internal static async Task<Exception> TickAndRunExpectingCrashAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        return await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>受理、派车，然后车开到取货站。停在等录入。</summary>
    internal static async Task<JourneyRuntimeRow> ArriveAtPickupAsync(RuntimeFixture fixture, string demandId)
    {
        await TickAndRunAsync(fixture);
        return await ArriveAtCurrentStopAsync(fixture, demandId, "TO_PICKUP");
    }

    /// <summary>
    /// 车开到当前停靠所在的那一站，用那个停靠自己的上位机单号与站号。
    /// </summary>
    /// <remarks>
    /// 多停靠旅程里「下一站是哪一站」只有停靠行说得出来，旅程行上那两个字段只描述锚需求的取货与关卡。
    /// </remarks>
    internal static async Task<JourneyRuntimeRow> ArriveAtCurrentStopAsync(
        RuntimeFixture fixture, string demandId, string purpose)
    {
        JourneyStopRow stop = await CurrentStopAsync(fixture, demandId);
        fixture.Riot.SetSuccessfulArrival(purpose, stop.UpperId, stop.StationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = stop.StationRiotId };
        await TickAndRunAsync(fixture);
        return await JourneyOfAsync(fixture, demandId);
    }

    /// <summary>操作员扫码录入，服务端下装货命令。停在等装货结果。</summary>
    internal static async Task<JourneyRuntimeRow> EnterSublotAsync(
        RuntimeFixture fixture,
        string demandId,
        string sublot,
        string submissionId)
    {
        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, demandId);
        // 录入寄到服务端自己算出来的那个地址：会话、站点、清单修订号三样都取当前停靠的。清单修订号尤其不能
        // 用旅程行上那一个——一个停靠可以发不止一版清单，地址是一个区间，而旅程行上只存着这趟旅程的起点。
        StopEntryAddress address = (await JourneyStopCursor.LoadAsync(
                fixture.Context, runtime, TestContext.Current.CancellationToken))
            .EntryAddressOfCurrentStop(runtime.WorklistRevision);
        await AddInboxAsync(fixture, submissionId, "SublotSubmitted", SublotSubmission(fixture, address, sublot));
        await TickAndRunAsync(fixture);
        return await JourneyOfAsync(fixture, demandId);
    }

    /// <summary>装货落定，服务端发离站核验。停在等核验答复。</summary>
    internal static async Task<JourneyRuntimeRow> SettleLoadAsync(RuntimeFixture fixture, string demandId)
    {
        await ApplySafeResultAsync(fixture, demandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        return await JourneyOfAsync(fixture, demandId);
    }

    /// <summary>车载端答复离站安全，服务端建关卡订单。停在等关卡到站。</summary>
    internal static async Task<JourneyRuntimeRow> AnswerDepartureSafetyAsync(
        RuntimeFixture fixture,
        string demandId,
        string safetyResultId,
        long safetyStateVersion = 7)
    {
        JourneyStopRow stop = await CurrentStopAsync(fixture, demandId);
        await AddInboxAsync(
            fixture,
            safetyResultId,
            "PreDepartureSafetyCheckResult",
            SafeDepartureAnswer(fixture, stop.DepartureSafetyCheckId!, safetyStateVersion),
            stop.DepartureSafetyCheckMessageId);
        await TickAndRunAsync(fixture);
        return await JourneyOfAsync(fixture, demandId);
    }

    /// <summary>车开到关卡，服务端下卸货命令；卸货落定，旅程完成。</summary>
    internal static async Task<JourneyRuntimeRow> ArriveAtGateAndUnloadAsync(RuntimeFixture fixture, string demandId)
    {
        await ArriveAtCurrentStopAsync(fixture, demandId, "TO_GATE");
        await ApplySafeResultAsync(fixture, demandId, SlotOperationType.Unload, SlotBusinessState.Empty);
        await TickAndRunAsync(fixture);
        return await JourneyOfAsync(fixture, demandId);
    }

    /// <summary>一整趟，从受理到完成。</summary>
    internal static async Task<JourneyRuntimeRow> RunJourneyToCompletionAsync(
        RuntimeFixture fixture,
        string demandId,
        string sublot,
        string submissionId,
        string safetyResultId)
    {
        await ArriveAtPickupAsync(fixture, demandId);
        await EnterSublotAsync(fixture, demandId, sublot, submissionId);
        await SettleLoadAsync(fixture, demandId);
        await AnswerDepartureSafetyAsync(fixture, demandId, safetyResultId);
        return await ArriveAtGateAndUnloadAsync(fixture, demandId);
    }

    /// <summary>
    /// 一条入站报文，id 由调用方写死。<c>RuntimeFixture.AddInboxAsync</c> 做不了这件事：它开头读一次唯一的旅程行
    /// （读出来并不用），同车跑第二趟时库里有两行，直接抛 <c>Sequence contains more than one element</c>。
    /// </summary>
    internal static async Task AddInboxAsync(
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
    /// 同上，但地址由这趟旅程的当前停靠算出来——调用方手里只有旅程行时用它。
    /// </summary>
    /// <remarks>
    /// 旅程行上的 <c>PickupStationId</c> 与 <c>WorklistRevision</c> 只描述锚需求那一个停靠、那一趟的起点；
    /// 服务端认的是当前停靠的会话、站点与那个停靠自己的清单修订号区间（<c>EntryAddressOfCurrentStop</c>）。
    /// </remarks>
    internal static async Task<object> SublotSubmissionAsync(
        RuntimeFixture fixture, JourneyRuntimeRow runtime, string sublot) =>
        SublotSubmission(
            fixture,
            (await JourneyStopCursor.LoadAsync(
                fixture.Context, runtime, TestContext.Current.CancellationToken))
                .EntryAddressOfCurrentStop(runtime.WorklistRevision),
            sublot);

    internal static object SublotSubmission(RuntimeFixture fixture, StopEntryAddress address, string sublot) => new
    {
        operationSessionId = address.OperationSessionId,
        stationId = address.StationId,
        worklistRevision = address.CurrentWorklistRevision,
        sublot,
        entryMethod = "SCANNER",
        @operator = new
        {
            operatorId = "OP-001",
            verificationMethod = "BADGE",
            verifiedAt = fixture.Clock.GetUtcNow()
        }
    };

    internal static object SafeDepartureAnswer(RuntimeFixture fixture, string checkId, long safetyStateVersion) => new
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
    /// <c>RuntimeFixture.OperationAsync</c> 按操作类型取唯一一条，同车第二趟就有两条装货记录了——所以这里按需求取。
    /// </summary>
    internal static async Task ApplySafeResultAsync(
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

    /// <summary>这辆车的按车修订号计数器。</summary>
    internal static Task<VehicleSnapshotRevisionRow> CounterAsync(RuntimeFixture fixture) =>
        fixture.Context.Set<VehicleSnapshotRevisionRow>().AsNoTracking()
            .SingleAsync(row => row.AgvId == fixture.Options.AgvId, TestContext.Current.CancellationToken);

    /// <summary>一条出站报文的载荷。</summary>
    internal static async Task<JsonElement> OutboundPayloadAsync(RuntimeFixture fixture, string messageId)
    {
        ProtocolOutboxRow row = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(item => item.MessageId == messageId, TestContext.Current.CancellationToken);
        using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
        return document.RootElement.GetProperty("payload").Clone();
    }
}
