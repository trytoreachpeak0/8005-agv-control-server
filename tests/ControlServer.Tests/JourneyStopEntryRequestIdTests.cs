using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 「当前停靠此刻要结算哪一条录入请求」这个问题的两个取值器，以及它们各自在缺 id 时的答案
/// （批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JourneyStopCursor.CurrentSublotRequestMessageId"/> 缺 id 就抛——那是本票新立的一道红线，
/// 而在这个文件之前<b>没有任何用例断过它</b>：把那个 throw 换成返回空串，全量一条都不会红。
/// </para>
/// <para>
/// <see cref="JourneyStopCursor.CurrentSublotRequestMessageIdOrNone"/> 是后加的，给那些车停在哪个停靠上
/// 都可能发生的终结路径用。它<b>按停靠角色</b>判该不该有 id，而不是按那一列空不空——这个选择是这里的主角：
/// 按空不空判，它会把上面那道红线一起吞掉，而全量仍然全绿，因为那道红线本来就没人断。
/// </para>
/// </remarks>
public sealed class JourneyStopEntryRequestIdTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";
    private const string SecondDemandId = "10000000-0000-4000-8000-000000000002";
    private const string EntryRequestId = "d0000000-0000-4000-8000-000000000004";
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 6, 0, 0, TimeSpan.Zero);

    /// <summary>车停在取货停靠上：两个取值器给的都是那一条录入请求。</summary>
    [Fact]
    public async Task AtAPickupStopBothGettersGiveTheEntryRequest()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        JourneyRuntimeRow runtime = await SeedAsync(context, pickupCompleted: false);
        JourneyStopCursor cursor = await JourneyStopCursor.LoadAsync(context, runtime, token);

        Assert.Equal(JourneyStopRoles.Pickup, cursor.Current.StopRole);
        Assert.Equal(EntryRequestId, cursor.CurrentSublotRequestMessageId(runtime.WorklistRevision));
        Assert.Equal(EntryRequestId, cursor.CurrentSublotRequestMessageIdOrNone(runtime.WorklistRevision));
    }

    /// <summary>
    /// 车停在卸货停靠上：严格的那个抛，宽的那个给 null。
    /// </summary>
    /// <remarks>
    /// 卸货停靠没有录入请求，所以也没有要结算的那一条，null 在这里是正确答案而不是降级。抛的那一个
    /// 留给只会在取货停靠上走到的调用方——恢复路径里的取消就是一个，它的阶段护栏保证了这一点。
    /// </remarks>
    [Fact]
    public async Task AtAnUnloadStopTheStrictGetterThrowsAndTheOtherGivesNull()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        JourneyRuntimeRow runtime = await SeedAsync(context, pickupCompleted: true);
        JourneyStopCursor cursor = await JourneyStopCursor.LoadAsync(context, runtime, token);

        Assert.Equal(JourneyStopRoles.Unload, cursor.Current.StopRole);
        Assert.Throws<InvalidDataException>(
            () => cursor.CurrentSublotRequestMessageId(runtime.WorklistRevision));
        Assert.Null(cursor.CurrentSublotRequestMessageIdOrNone(runtime.WorklistRevision));
    }

    /// <summary>
    /// 一个<b>取货</b>停靠缺 id：两个取值器都抛，宽的那个也不例外。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这条是这个文件存在的理由。受理必定给取货停靠写下那一列，所以缺了就是库坏了或者受理漏写——
    /// 一件要响亮停下的事，不是一件要兜住的事。
    /// </para>
    /// <para>
    /// <b>把 <c>OrNone</c> 的判据从「角色是卸货」改成「那一列是 null」，这条会红，而别的一条都不会。</b>
    /// 那个改法看起来更直接，甚至更「防御」，但它把上面那道红线换成了一次静默的降级：终结时不结算任何
    /// 录入请求，那条请求于是被补发进下一个会话，车载端把它当成内容已变的业务 id 而断会话——
    /// 也就是那道红线当初要防的事，只不过现在没有东西会响。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APickupStopMissingItsEntryRequestThrowsFromEitherGetter()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        JourneyRuntimeRow runtime = await SeedAsync(context, pickupCompleted: false);
        JourneyStopRow pickup = await context.Set<JourneyStopRow>()
            .SingleAsync(row => row.StopRole == JourneyStopRoles.Pickup, token);
        pickup.SublotRequestMessageId = null;
        await context.SaveChangesAsync(token);
        JourneyStopCursor cursor = await JourneyStopCursor.LoadAsync(context, runtime, token);

        Assert.Throws<InvalidDataException>(
            () => cursor.CurrentSublotRequestMessageId(runtime.WorklistRevision));
        Assert.Throws<InvalidDataException>(
            () => cursor.CurrentSublotRequestMessageIdOrNone(runtime.WorklistRevision));
    }

    /// <summary>
    /// 终结时结算的是<b>停靠行此刻那一版</b>录入请求，不是旅程行上受理时写下的那一版。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>「同名不同源」：</b>旅程行与停靠行上都有一列 <c>SublotRequestMessageId</c>，受理时从前者搬到后者，
    /// 此后一直恒等——直到清单升版：升版换一个新 id，而旅程行那一列还停在受理时那个。读旅程行的那一版会结算不到
    /// 当前这条录入请求，于是它被补发进下一个会话，车载端把它当成内容已变的业务 id 而断会话。
    /// </para>
    /// <para>
    /// <b>这条用例的关键在夹具，不在断言。</b>两列只有在<c>本停靠已经有需求做完本停靠作业</c>时才分岔
    /// （<c>WorklistRevisionAt = First + DoneAt</c>）。既有那条多需求取消用例发生在第一条还在 <c>LOADING</c> 时，
    /// <c>DoneAt</c> 为 0，两列仍然恒等——在那个状态下写的断言分不出谁读了谁，等于没写。所以这里把锚需求推到
    /// <c>LOADED</c>，并且<b>先断言两列真的不相等</b>：没有那一句，这条用例哪天夹具退回恒等状态也照样绿。
    /// </para>
    /// <para>
    /// <b>能通过它的错误实现</b>（在这条用例之前是全绿的）：把新重载里的 <c>currentSublotRequestMessageId</c>
    /// 参数忽略掉，仍旧查 <c>runtime.SublotRequestMessageId</c>。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ClosingTheJourneySettlesTheStopsCurrentEntryRequestNotTheJourneyRowsOriginal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        JourneyRuntimeRow runtime = await SeedAsync(context, pickupCompleted: false);

        // 一站两条需求，第一条已经装完——两列分岔的唯一入口。
        await JourneyMembershipSeed.AddFurtherDemandAsync(context, runtime, SecondDemandId);
        JourneyDemandRow anchorMembership = await context.Set<JourneyDemandRow>()
            .SingleAsync(row => row.DemandId == DemandId, token);
        anchorMembership.Status = JourneyDemandStatuses.Loaded;
        await context.SaveChangesAsync(token);

        JourneyStopCursor cursor = await JourneyStopCursor.LoadAsync(context, runtime, token);
        string current = cursor.CurrentSublotRequestMessageId(runtime.WorklistRevision);

        // 前提先断住：这一句为假时整条用例就什么也证明不了。
        Assert.NotEqual(runtime.SublotRequestMessageId, current);

        context.ProtocolOutbox.AddRange(
            new ProtocolOutboxRow
            {
                MessageId = runtime.SublotRequestMessageId,
                MessageType = "SublotEntryRequest",
                PayloadJson = "{}",
                CreatedAt = Now
            },
            new ProtocolOutboxRow
            {
                MessageId = current,
                MessageType = "SublotEntryRequest",
                PayloadJson = "{}",
                CreatedAt = Now.AddMinutes(1)
            });
        await context.SaveChangesAsync(token);

        await new PickupStopTermination(context).StageJourneyClosureAsync(
            runtime,
            cursor.CurrentSublotRequestMessageIdOrNone(runtime.WorklistRevision),
            "CANCELLED_BY_OPERATOR",
            Now.AddMinutes(2),
            token);
        await context.SaveChangesAsync(token);

        Assert.NotNull((await context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageId == current, token)).AcknowledgedAt);
        // 而受理时那一版没有被碰过——「读错了一版」的表现正是这两条调个个儿。
        Assert.Null((await context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageId == runtime.SublotRequestMessageId, token)).AcknowledgedAt);
    }

    /// <summary>
    /// 恢复路径那道阶段护栏点名的两个阶段，确实都意味着「车此刻在一个取货停靠上」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OnboardRecoveryCoordinator</c> 里在途取消那一段的护栏是
    /// <c>stop.Stage is not (AwaitingSublot or AwaitingLoadResult)</c>。它之所以够，全靠「这两个阶段只在取货停靠上
    /// 出现」——而这件事全仓只有 <see cref="JourneyStopCursor.RoleOf"/> 那张表表达，<b>那张表没有任何调用点</b>，
    /// 在这条用例之前也没有任何东西读过它。改掉它，那道护栏静默失效，同一票里另一段就是因为类似的失效才抛的。
    /// </para>
    /// <para>
    /// 两个方向都断：该是取货的五个阶段一个不少，该是卸货的两个也没有混进来。只断前者的话，
    /// 一张把所有阶段都映成取货的表照样通过。
    /// </para>
    /// </remarks>
    [Fact]
    public void TheStagesThatMeanAPickupStopAreTheOnesTheRecoveryGuardNames()
    {
        Assert.Equal(JourneyStopRoles.Pickup, JourneyStopCursor.RoleOf(JourneyRuntimeStage.AwaitingSublot));
        Assert.Equal(JourneyStopRoles.Pickup, JourneyStopCursor.RoleOf(JourneyRuntimeStage.AwaitingLoadResult));
        Assert.Equal(JourneyStopRoles.Pickup, JourneyStopCursor.RoleOf(JourneyRuntimeStage.AwaitingPickupArrival));
        Assert.Equal(JourneyStopRoles.Pickup, JourneyStopCursor.RoleOf(JourneyRuntimeStage.AwaitingStationDeparture));
        Assert.Equal(JourneyStopRoles.Pickup, JourneyStopCursor.RoleOf(JourneyRuntimeStage.AwaitingDepartureSafety));

        Assert.Equal(JourneyStopRoles.Unload, JourneyStopCursor.RoleOf(JourneyRuntimeStage.AwaitingGateArrival));
        Assert.Equal(JourneyStopRoles.Unload, JourneyStopCursor.RoleOf(JourneyRuntimeStage.AwaitingUnloadResult));
    }

    private static async Task<ControlServerDbContext> CreateContextAsync(SqliteConnection connection)
    {
        ControlServerDbContext context = new(new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return context;
    }

    /// <summary>受理写下的那一份：旅程行加两个停靠。<paramref name="pickupCompleted"/> 把车挪到卸货停靠上。</summary>
    private static async Task<JourneyRuntimeRow> SeedAsync(ControlServerDbContext context, bool pickupCompleted)
    {
        JourneyRuntimeRow runtime = new()
        {
            JourneyId = JourneyIdentity.ForAnchorDemand(DemandId),
            DemandId = DemandId,
            Stage = JourneyRuntimeStage.AwaitingSublot,
            AgvId = "AGV-01",
            VehicleKey = "VEHICLE-001",
            AgvLifecycleGeneration = 1,
            MapId = 29,
            MapIdentity = "MAP-29",
            DispatchZone = "ZONE-01",
            RouteEvidenceId = "ROUTE-01",
            PickupStationId = "PICKUP",
            PickupStationRiotId = 11,
            GateStationId = "GATE",
            GateStationRiotId = 22,
            ExpectedBasketCount = 2,
            TargetSlotsJson = "[1,2]",
            OperationSessionId = "c0000000-0000-4000-8000-000000000001",
            PickupMovementLegId = "pickup-leg",
            PickupUpperId = "UPPER-PICKUP",
            GateMovementLegId = "gate-leg",
            GateUpperId = "UPPER-GATE",
            DispatchGeneration = 1,
            VehicleBusinessRevision = 1,
            WorklistRevision = 1,
            PlanRevision = 1,
            VehicleBusinessMessageId = "d0000000-0000-4000-8000-000000000001",
            WorklistMessageId = "d0000000-0000-4000-8000-000000000002",
            PlanMessageId = "d0000000-0000-4000-8000-000000000003",
            SublotRequestMessageId = EntryRequestId,
            LoadCommandMessageId = "d0000000-0000-4000-8000-000000000005",
            LoadSlotOperationAttemptId = "d0000000-0000-4000-8000-000000000013",
            UnloadCommandMessageId = "d0000000-0000-4000-8000-000000000011",
            UnloadSlotOperationAttemptId = "d0000000-0000-4000-8000-000000000012",
            PreDepartureSafetyCheckMessageId = "d0000000-0000-4000-8000-000000000006",
            PreDepartureSafetyCheckId = "d0000000-0000-4000-8000-000000000007",
            GateVehicleBusinessMessageId = "d0000000-0000-4000-8000-000000000008",
            GateWorklistMessageId = "d0000000-0000-4000-8000-000000000009",
            GatePlanMessageId = "d0000000-0000-4000-8000-000000000010",
            CreatedAt = Now.AddMinutes(-8),
            UpdatedAt = Now
        };
        context.JourneyRuntimes.Add(runtime);
        context.AcceptedDemands.Add(new AcceptedDemandRow
        {
            DemandId = DemandId,
            SeriesId = "SERIES-001",
            TransportDemandKey = "SUBLOT-001|WIRE_TO_GATE",
            WorkType = "WIRE_TO_GATE",
            Sublot = "SUBLOT-001",
            Generation = 1,
            DemandRevision = 1,
            HistoryEpoch = "history-1",
            CatalogRevision = 1,
            CreatedAt = Now.AddMinutes(-10),
            ValueObservedAt = Now.AddMinutes(-9),
            ValuePollTraceId = "TRACE-001",
            ValueProjectionCommitId = "COMMIT-001",
            LiveMesFieldsJson = "{}",
            AcceptedAt = Now.AddMinutes(-8),
            Status = DemandExecutionStatus.Accepted
        });
        context.OrderIntents.Add(new OrderIntentRow
        {
            MovementLegId = "pickup-leg",
            DemandId = DemandId,
            UpperId = "UPPER-PICKUP",
            Purpose = "TO_PICKUP",
            TargetStationId = "11",
            VehicleKey = "VEHICLE-001",
            MapId = 29,
            DestinationStationId = 11,
            AgvLifecycleGeneration = 1,
            DispatchGeneration = 1,
            CreatedAt = Now.AddMinutes(-8),
            Status = "CONFIRMED",
            OrderId = "ORDER-TO_PICKUP"
        });
        context.VehicleDispatchLeases.Add(new VehicleDispatchLeaseRow
        {
            JourneyId = runtime.JourneyId,
            DemandId = DemandId,
            VehicleKey = "VEHICLE-001",
            AcquiredAt = Now.AddMinutes(-8)
        });
        JourneyMembershipSeed.Seed(context, runtime);
        if (pickupCompleted)
        {
            context.Set<JourneyStopRow>()
                .Local.Single(row => row.StopRole == JourneyStopRoles.Pickup)
                .Status = JourneyStopStatuses.Completed;
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return runtime;
    }
}
