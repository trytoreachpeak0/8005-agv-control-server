using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 计划快照在线上的腿：按停靠序号排好、<c>sequence</c> 从 1 连续重新编号，最多 9 条（control-server#664）。
/// </summary>
/// <remarks>
/// 投影里的序号是库里停靠的序号，清桩移动停靠是 <c>charger.Sequence + attempt</c>，所以收尾单腿计划曾发出 <c>[2]</c>、第二次清桩途中 <c>[1, 3]</c>、
/// 第九次 <c>[1, 10]</c>。hmi#327 之前的车载端要求排序后等于 1..n，协议要求 1～9，两者都会让车载端拒收、断会话。
/// </remarks>
[Trait("IntegrationSlice", "FP-IS-01")]
public sealed class UpcomingStopPlanWireSequenceTests
{
    private static readonly int[] OneThree = [1, 3];

    private static readonly DateTimeOffset StagedAt = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<int[], bool> GuardCases => new()
    {
        { [2], false },
        { [1, 3], false },
        { [1, 1], false },
        { [2, 1], true },
        { [], true },
    };

    public static TheoryData<int[], int[]> Renumbered => new()
    {
        { [2], [1] },
        { [1, 3], [1, 2] },
        { [1, 10], [1, 2] },
        { [3, 1], [1, 2] },
        { [1, 2, 3], [1, 2, 3] },
    };

    /// <summary>
    /// 发件箱里落着的那一行（重放原样发的就是它）：序号从 1 连续，腿按原序号排；腿 id 跟着腿走，不跟着位置走。
    /// </summary>
    [Theory]
    [MemberData(nameof(Renumbered))]
    public async Task TheStagedPlanCarriesItsLegsNumberedFromOne(int[] stopSequences, int[] expected)
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(Token);
        await using ControlServerDbContext context = await ContextAsync(connection);
        UpcomingStopPlanProjection projection = Projection(stopSequences);

        await OnboardJourneyPublisher.StageUpcomingStopPlanAsync(
            new WireToGateStore(context), "00000000-0000-4000-8000-000000066401", "AGV-001", 1, projection,
            StagedAt, Token);
        await context.SaveChangesAsync(Token);

        ProtocolOutboxRow row = await context.ProtocolOutbox.AsNoTracking().SingleAsync(Token);
        using JsonDocument envelope = JsonDocument.Parse(row.PayloadJson);
        JsonElement[] legs = [.. envelope.RootElement.GetProperty("payload").GetProperty("legs").EnumerateArray()];
        Assert.Equal(expected, legs.Select(leg => leg.GetProperty("sequence").GetInt32()));
        Assert.Equal(
            projection.Legs.OrderBy(leg => leg.Sequence).Select(leg => leg.MovementLegId),
            legs.Select(leg => leg.GetProperty("movementLegId").GetString()));
    }

    /// <summary>
    /// 发布路径（<see cref="OnboardJourneyPublisher.PublishUpcomingStopPlanAsync"/>）同样重新编号：停靠序号 <c>[1, 3]</c> 的搬运计划
    /// （<see cref="JourneyPlanBuilder.Plan"/> 投影出来的），车收到的那一行和发件箱里那一行都是 <c>[1, 2]</c>（审查 S1）。
    /// </summary>
    [Fact]
    public async Task APublishedTransportPlanWithAGapInItsStopsReachesTheVehicleNumberedFromOne()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(Token);
        await using ControlServerDbContext context = await ContextAsync(connection);
        JourneyRuntimeWorkerTestKit.RecordingPeer peer = new();
        OnboardJourneyPublisher publisher = new(new WireToGateStore(context), peer, TimeProvider.System);
        JourneyRuntimeRow runtime = Batch7PlanRevisionTests.PlanRuntime();
        JourneyStopRow pickup = Batch7PlanRevisionTests.StopRow("s-pickup", 1, JourneyStopRoles.Pickup, JourneyStopStatuses.Active);
        JourneyStopRow unload = Batch7PlanRevisionTests.StopRow("s-unload", 3, JourneyStopRoles.Unload, JourneyStopStatuses.Pending);
        // The helpers' readable ids are not UUIDs; this line goes through the outbound schema gate, so give it real ones.
        runtime.DemandId = "00000000-0000-4000-8000-000000066410";
        pickup.MovementLegId = "00000000-0000-4000-8000-000000066411";
        unload.MovementLegId = "00000000-0000-4000-8000-000000066412";
        UpcomingStopPlanProjection plan = JourneyPlanBuilder.Plan(runtime, [pickup, unload], pickup, arrivedAtCurrent: false, revision: 5);
        Assert.Equal(OneThree, plan.Legs.Select(leg => leg.Sequence));

        await publisher.PublishUpcomingStopPlanAsync("00000000-0000-4000-8000-000000066403", runtime.AgvId, 1, plan, Token);

        string sent = System.Text.Encoding.UTF8.GetString(Assert.Single(peer.Lines));
        string stored = (await context.ProtocolOutbox.AsNoTracking().SingleAsync(Token)).PayloadJson;
        foreach (string line in new[] { sent, stored })
        {
            using JsonDocument envelope = JsonDocument.Parse(line);
            Assert.Equal(
                [(pickup.MovementLegId, 1), (unload.MovementLegId, 2)],
                envelope.RootElement.GetProperty("payload").GetProperty("legs").EnumerateArray()
                    .Select(leg => (leg.GetProperty("movementLegId").GetString(), leg.GetProperty("sequence").GetInt32())));
        }
    }

    /// <summary>9 条腿照常编号 1..9；第 10 条在服务端当场失败，不截断、不发出去让车拆会话。</summary>
    [Fact]
    public void NineLegsAreNumberedOneToNineAndATenthIsRefusedHere()
    {
        Assert.Equal(
            Enumerable.Range(1, 9),
            OnboardJourneyPublisher.WireLegs(Projection([.. Enumerable.Range(5, 9)])).Select(leg => leg.Sequence));

        InvalidDataException refused = Assert.Throws<InvalidDataException>(
            () => OnboardJourneyPublisher.WireLegs(Projection([.. Enumerable.Range(1, 10)])));
        Assert.Contains("at most 9 legs", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 追加后的重发判定拿线上那一版与重新编号的结果比：停靠序号有空档的计划发出去之后，同一份投影判为「车上已是这一版」，不白白重发；
    /// 腿的状态变了照样判为不同。
    /// </summary>
    [Fact]
    public async Task TheReSendCheckComparesWithTheRenumberedLegs()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(Token);
        await using ControlServerDbContext context = await ContextAsync(connection);
        UpcomingStopPlanProjection projection = Projection([1, 3, 4]);
        await OnboardJourneyPublisher.StageUpcomingStopPlanAsync(
            new WireToGateStore(context), "00000000-0000-4000-8000-000000066402", "AGV-001", 1, projection,
            StagedAt, Token);
        await context.SaveChangesAsync(Token);
        string payloadJson = (await context.ProtocolOutbox.AsNoTracking().SingleAsync(Token)).PayloadJson;

        Assert.True(JourneyRuntimeEngine.PlanOnTheWireMatches(payloadJson, projection));
        UpcomingStopPlanProjection moved = projection with
        {
            Legs = [.. projection.Legs.Select((leg, index) => index == 0 ? leg with { State = "COMPLETED" } : leg)]
        };
        Assert.False(JourneyRuntimeEngine.PlanOnTheWireMatches(payloadJson, moved));
    }

    /// <summary>
    /// 出站门禁里旧车载端那条规则本身（<see cref="OutboundSchemaConformance.LegacyOnboardRefusesPlan"/>）：现场被拒的 <c>[2]</c> 与 <c>[1, 3]</c> 判拒，
    /// 乱序但连续的 <c>[2, 1]</c> 与空计划判收——旧车载端先排序再比。
    /// </summary>
    [Theory]
    [MemberData(nameof(GuardCases))]
    public void TheGuardAppliesTheOldOnboardRule(int[] sequences, bool accepted)
    {
        string line = JsonSerializer.Serialize(new
        {
            payload = new { legs = sequences.Select(sequence => new { sequence }) }
        });

        Assert.Equal(accepted, OutboundSchemaConformance.LegacyOnboardRefusesPlan(line) is null);
    }

    private static UpcomingStopPlanProjection Projection(int[] stopSequences) => new(
        7,
        [.. stopSequences.Select(sequence => new UpcomingMovementLeg(
            $"00000000-0000-4000-8000-{sequence:D12}",
            null,
            JourneyPlanBuilder.WaitingPointStopPurpose,
            null,
            null,
            sequence,
            "等待点" + sequence,
            "25",
            "PLANNED"))]);

    private static async Task<ControlServerDbContext> ContextAsync(SqliteConnection connection)
    {
        ControlServerDbContext context = new(new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync(Token);
        return context;
    }
}
