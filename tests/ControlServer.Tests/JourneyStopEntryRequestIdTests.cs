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
