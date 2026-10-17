using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static ControlServer.Tests.Batch7MultiDemandAdvanceTests;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerLoadCancellationBeforeSublotTests;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 具体仓位在扫码之后才分配（REQ-0352、REQ-0208，CP-0015，control-server#754）：受理只按每侧花篮数预留数量，扫码通过、
/// 下装货命令之前按同侧当时编号最小的空仓分配并写定，之后不再改变。
/// </summary>
/// <remarks>
/// 现场 10-17 的样子是「1～4 号都空，扫码后开了 3 号」：那条需求受理时 1、2 号还被别的需求占着，<c>TargetSlotsJson</c>
/// 当时就定成了后面的号，后来前面空出来也不改。下面的用例都是这一句话的不同侧面。夹具里每条需求两个花篮（8 盒、每篮 4 盒），
/// 所需分组是前侧 1～4。
/// </remarks>
public sealed class SlotAssignmentAtScanTests
{
    /// <summary>受理只占数量：归属行与旅程行上都没有具体仓号，花篮数照旧冻结。</summary>
    [Fact]
    public async Task AcceptanceReservesTheBasketCountButNoSlotNumbers()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 8);

        await TickAndRunAsync(fixture);

        JourneyDemandRow membership = await MembershipAsync(fixture, FirstDemandId);
        Assert.Equal((2, "[]"), (membership.ExpectedBasketCount, membership.TargetSlotsJson));
        Assert.Equal("[]", (await fixture.RuntimeAsync(FirstDemandId)).TargetSlotsJson);
    }

    /// <summary>
    /// 用户的例子：一站两条需求，前侧 1～4 都空。操作员先扫后加入的那一条，它开的是此刻最小的 1、2 号，不是受理时排在
    /// 后面的那一段；分配结果写回归属行，卸货排序读的就是它。
    /// </summary>
    [Fact]
    public async Task TheDemandScannedFirstGetsTheLowestFreeSlotsOfItsSideWhateverItsAcceptanceOrder()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow runtime = await TwoDemandsAtThePickupAsync(fixture, boxCount: 8);

        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, SecondSublot));
        await TickAndRunAsync(fixture);

        int[] opened = await LoadCommandSlotsAsync(fixture, SecondDemandId);
        Assert.Equal([1, 2], opened);
        Assert.Equal("[1,2]", (await MembershipAsync(fixture, SecondDemandId)).TargetSlotsJson);
    }

    /// <summary>
    /// 同侧两条先后扫码：各自拿到扫码那一刻最小的空仓，第二条不碰第一条已分到的号。
    /// </summary>
    [Fact]
    public async Task TwoDemandsScannedOneAfterTheOtherOnOneSideGetDisjointLowestSlots()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow runtime = await TwoDemandsAtThePickupAsync(fixture, boxCount: 8);

        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, SecondSublot));
        await TickAndRunAsync(fixture);
        await ApplySafeResultAsync(fixture, SecondDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        await AddInboxAsync(
            fixture, SecondSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));
        await TickAndRunAsync(fixture);

        int[] second = await LoadCommandSlotsAsync(fixture, SecondDemandId);
        int[] first = await LoadCommandSlotsAsync(fixture, FirstDemandId);
        Assert.Equal(([1, 2], [3, 4]), (second, first));
    }

    /// <summary>
    /// 两次扫码在同一轮之前都到了：一站一次只装一条，先处理的那一条拿 1、2；另一条等它装完再分，拿到的是 3、4，两组不重叠。
    /// </summary>
    [Fact]
    public async Task TwoEntriesWaitingInTheSameRoundAreAssignedOneAfterTheOtherWithoutOverlap()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow runtime = await TwoDemandsAtThePickupAsync(fixture, boxCount: 8);
        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, SecondSublot));
        await AddInboxAsync(
            fixture, SecondSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));

        await TickAndRunAsync(fixture);
        Assert.Equal("[]", (await MembershipAsync(fixture, FirstDemandId)).TargetSlotsJson);
        await ApplySafeResultAsync(fixture, SecondDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);

        int[] second = await LoadCommandSlotsAsync(fixture, SecondDemandId);
        int[] first = await LoadCommandSlotsAsync(fixture, FirstDemandId);
        Assert.Equal(([1, 2], [3, 4]), (second, first));
    }

    /// <summary>
    /// 写定之后不变：分到 3、4 的那一条，在更小的号空出来、服务端重启之后，归属行、装货记录与重发的命令都还是 3、4。
    /// 重放（<c>PrepareSlotOperationAsync</c>）与恢复都按第一次写下的值判冲突，变了就是冲突。
    /// </summary>
    [Fact]
    public async Task AnAssignmentStaysAsWrittenWhenALowerSlotFreesUpAndTheServerRestarts()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow runtime = await TwoDemandsAtThePickupAsync(fixture, boxCount: 8);
        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, SecondSublot));
        await TickAndRunAsync(fixture);
        await ApplySafeResultAsync(fixture, SecondDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        await AddInboxAsync(
            fixture, SecondSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));
        await TickAndRunAsync(fixture);

        // 先装的那一条结掉，它的 1、2 号空回来。
        JourneyDemandRow ended = await fixture.Context.Set<JourneyDemandRow>()
            .SingleAsync(row => row.DemandId == SecondDemandId, TestContext.Current.CancellationToken);
        ended.Status = JourneyDemandStatuses.Terminated;
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await fixture.RecreateEngineAsync();
        await TickAndRunAsync(fixture);

        StationOperationRow operation = await fixture.Context.StationOperations.AsNoTracking()
            .SingleAsync(row => row.DemandId == FirstDemandId, TestContext.Current.CancellationToken);
        Assert.Equal(
            ("[3,4]", "[3,4]"),
            ((await MembershipAsync(fixture, FirstDemandId)).TargetSlotsJson, operation.TargetSlotsJson));
        int[] first = await LoadCommandSlotsAsync(fixture, FirstDemandId);
        Assert.Equal([3, 4], first);
    }

    /// <summary>禁用的仓位不分：前侧 1 号被禁用，两个花篮分到 2、3。</summary>
    [Fact]
    public async Task ADisabledSlotIsNotAssigned()
    {
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await DisableSlotsAsync(fixture, 1);
        await fixture.RecreateEngineAsync();

        await fixture.SubmitSublotAsync("SUBLOT-001");
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        int[] opened = await LoadCommandSlotsAsync(fixture, BeforeSublotDemandId);
        Assert.Equal([2, 3], opened);
    }

    /// <summary>
    /// 扫码时这一侧能分的空仓比预留的少（受理之后仓位被禁用）：这次录入按 <c>SLOT_INOPERABLE</c> 拒收，不装货、不开门，
    /// 旅程停在等录入，看板挂 <c>LOAD_SLOT_ASSIGNMENT_SHORT</c>；操作员在那一行按「取消」仍能把这条需求结掉。
    /// </summary>
    [Fact]
    public async Task AShortSideRefusesTheEntryAsSlotInoperableAndLeavesTheStopCancellable()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await DisableSlotsAsync(fixture, 2, 3, 4);
        await fixture.RecreateEngineAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = BeforeSublotConnection(fixture, generation: 1);
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();

        await processor.ProcessAsync(SublotEntry(fixture, waiting, "SUBLOT-001"), state, token);
        await fixture.Engine.ExecuteOnceAsync(token);

        using JsonDocument refusal = JsonDocument.Parse(
            (await fixture.Context.ProtocolOutbox.AsNoTracking()
                .SingleAsync(row => row.MessageType == "SublotRejected", token)).PayloadJson);
        JourneyRuntimeRow refused = await fixture.RuntimeAsync();
        Assert.Equal(
            ("SLOT_INOPERABLE", JourneyRuntimeStage.AwaitingSublot, "LOAD_SLOT_ASSIGNMENT_SHORT"),
            (refusal.RootElement.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString(),
                refused.Stage, refused.BlockReasonCode));
        Assert.DoesNotContain("SlotOperationCommand", await fixture.OutboxTypesAsync());
        Assert.Equal("[]", (await MembershipAsync(fixture, BeforeSublotDemandId)).TargetSlotsJson);

        string authorization = await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), state, token);

        using JsonDocument answer = JsonDocument.Parse(authorization.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        Assert.Equal(
            "AUTHORIZED",
            answer.RootElement.GetProperty("payload").GetProperty("decision").GetString());
    }

    /// <summary>
    /// 崩在分配写入与装货命令写盘之间：两者在同一个写事务里，崩了就都不在。下一轮重新分配，只分出一组、只有一条装货记录。
    /// </summary>
    [Fact]
    public async Task ACrashBeforeTheLoadCommandIsWrittenLeavesNothingAssignedAndTheNextRoundAssignsOnce()
    {
        FailOnceOnStationOperationInsert failure = new();
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: failure);
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 8);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));

        failure.Armed = true;
        await TickAndRunExpectingCrashAsync(fixture);
        Assert.True(failure.Fired);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal("[]", (await MembershipAsync(fixture, FirstDemandId)).TargetSlotsJson);
        Assert.Empty(await fixture.Context.StationOperations.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));

        await fixture.RecreateEngineAsync();
        await TickAndRunAsync(fixture);

        StationOperationRow operation = await fixture.Context.StationOperations.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            ("[1,2]", "[1,2]"),
            ((await MembershipAsync(fixture, FirstDemandId)).TargetSlotsJson, operation.TargetSlotsJson));
    }

    /// <summary>这条需求那一条装货命令里的仓位，也就是车会开的仓门。</summary>
    private static async Task<int[]> LoadCommandSlotsAsync(RuntimeFixture fixture, string demandId)
    {
        JourneyDemandRow membership = await MembershipAsync(fixture, demandId);
        ProtocolOutboxRow command = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageId == membership.LoadCommandMessageId, TestContext.Current.CancellationToken);
        using JsonDocument document = JsonDocument.Parse(command.PayloadJson);
        return [.. document.RootElement.GetProperty("payload").GetProperty("slots").EnumerateArray()
            .Select(slot => slot.GetInt32())];
    }

    /// <summary>把会话快照里这几个仓位标成禁用，与车载端报管理性禁用同一个样子。</summary>
    private static async Task DisableSlotsAsync(RuntimeFixture fixture, params int[] slotNumbers)
    {
        ProtocolInboxRow[] rows = await fixture.Context.ProtocolInbox
            .Where(row => row.MessageType == "CapabilitySnapshot" || row.MessageType == "SafetyStateSnapshot")
            .ToArrayAsync(TestContext.Current.CancellationToken);
        foreach (ProtocolInboxRow row in rows)
        {
            JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
            foreach (JsonNode? slot in root["payload"]!["slotStates"]!.AsArray())
            {
                if (slotNumbers.Contains(slot!["slotNo"]!.GetValue<int>()))
                {
                    slot["administrativeAvailability"] = "DISABLED";
                }
            }
            row.RequestJson = root.ToJsonString(SerializerOptions);
        }
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>写 <c>StationOperations</c> 的那一条命令抛一次，如同写装货记录时崩了。</summary>
    private sealed class FailOnceOnStationOperationInsert : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Check(DbCommand command)
        {
            if (!Armed || Fired ||
                !command.CommandText.Contains("INSERT INTO \"StationOperations\"", StringComparison.Ordinal))
            {
                return;
            }

            Fired = true;
            throw new InvalidOperationException("Injected failure while writing the load's station operation.");
        }
    }
}
