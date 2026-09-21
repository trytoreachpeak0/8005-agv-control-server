using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 同一条需求释放之后改派进第二趟旅程，身份不撞、第一趟的行不被覆盖（批次7-10，control-server#215，调度决策 2、3）。
/// </summary>
/// <remarks>
/// <para>
/// 改派出现之前，一趟旅程带的每一个 id 都只由需求 id 派生：旅程 id、停靠 id、腿、作业会话、报文与 attempt。同一条需求
/// 第二次受理，这些 id 与第一次逐字相同，主键直接撞上——或者更糟，撞在没有唯一约束的列上，第二趟悄悄认了第一趟的行。
/// </para>
/// <para>
/// 派生键（<see cref="JourneyIdentity.DerivationKey"/>）在第一次受理时就是需求 id，所以这里<b>同时</b>钉两件事：
/// 不带改派代次时 id 与今天逐字相同（零变化那一半），带上之后每一个都换了（不撞那一半）。只钉后一半，一个「总是带代次」
/// 的实现也能通过，而它会让每一个已有的单需求 id 都变掉。
/// </para>
/// <para>
/// 车载端对 movementLegId、operationSessionId、messageId 只认标准 UUID（<c>Guid.TryParseExact(value, "D")</c>，
/// 不合格整条消息以 <c>PROTOCOL_SCHEMA_INVALID</c> 拒收；读的是 <c>8005-agv-onboard-hmi</c> 的 <c>w2g/fp-v2-impl</c>
/// 分支），所以改派之后的 id 也断言是 UUID：把代次拼在 id 字符串后面会在这里红，而不是到车上才红。
/// </para>
/// </remarks>
public sealed class Batch7RedispatchIdentityTests
{
    private const string Anchor = "D-7301";
    private const string Released = "D-7302";
    private static readonly DateTimeOffset Now = Batch7JourneyFixture.Now;

    private static readonly JourneyRuntimeOptions Options = new()
    {
        MapId = 25,
        MapIdentity = "MAP-25",
        DispatchGeneration = 1,
        AllowedDispatchZones = ["MAP-25-WIRE_TO_GATE"],
    };

    [Fact]
    public void AFirstAcceptanceDerivesEveryIdExactlyAsBeforeRedispatchExisted()
    {
        JourneyExecutionPlan plan = CreatePlan(redispatchGeneration: null);

        Assert.Null(plan.IdentityKey);
        Assert.Equal(Released, plan.DerivationKeyFor(Released));
        Assert.Equal(JourneyPlanBuilder.StableGuid(Released, "operation-session"), plan.OperationSessionId);
        Assert.Equal(JourneyPlanBuilder.StableGuid(Released, "pickup-leg"), plan.PickupMovementLegId);
        Assert.Equal(JourneyPlanBuilder.StableGuid(Released, "gate-leg"), plan.GateMovementLegId);
        Assert.Equal($"W2G-{Released}-PICKUP-1", plan.PickupUpperId);
        Assert.Equal($"W2G-{Released}-GATE-1", plan.GateUpperId);
        Assert.Equal(1, plan.DispatchGeneration);
    }

    [Fact]
    public void ARedispatchDerivesEveryIdAfreshAndKeepsThemUuids()
    {
        JourneyExecutionPlan first = CreatePlan(redispatchGeneration: null);
        JourneyExecutionPlan again = CreatePlan(redispatchGeneration: 2);

        Assert.Equal($"{Released}|g2", again.IdentityKey);
        Assert.Equal(2, again.DispatchGeneration);
        Assert.Equal($"W2G-{Released}-PICKUP-2", again.PickupUpperId);
        Assert.Equal($"W2G-{Released}-GATE-2", again.GateUpperId);
        foreach ((string before, string after) in new[]
        {
            (first.OperationSessionId, again.OperationSessionId),
            (first.PickupMovementLegId, again.PickupMovementLegId),
            (first.GateMovementLegId, again.GateMovementLegId),
        })
        {
            Assert.NotEqual(before, after);
            Assert.True(Guid.TryParseExact(after, "D", out _), $"'{after}' is not a standard UUID.");
        }
    }

    /// <summary>
    /// 释放之后作为新旅程的锚再受理：受理行复用（受理时刻不变）、新旅程的每一个 id 都不与第一趟的撞，
    /// 第一趟的停靠与归属一行都没被改写；同一次改派重放是判同，不是冲突。
    /// </summary>
    [Fact]
    public async Task ARedispatchedDemandGetsASecondJourneyBesideTheFirstOne()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyRuntimeRow first = await AcceptTwoDemandJourneyAsync(fixture);
        await new JourneyMembershipStore(fixture.Context).RemoveDemandAsync(
            first.JourneyId, Released, DemandJourneyLookup.ReleasedForRedispatchReason, Now.AddMinutes(1), token);
        string[] firstJourneyBefore = await FirstJourneyRowsAsync(fixture, first.JourneyId);
        DateTimeOffset acceptedAt = (await fixture.NewContext().AcceptedDemands.AsNoTracking()
            .SingleAsync(row => row.DemandId == Released, token)).AcceptedAt;

        JourneyExecutionPlan plan = RedispatchPlan(generation: 2);
        await AcceptRedispatchAsync(fixture, plan);

        await using ControlServerDbContext reading = fixture.NewContext();
        AcceptedDemandRow accepted = await reading.AcceptedDemands.AsNoTracking()
            .SingleAsync(row => row.DemandId == Released, token);
        Assert.Equal(acceptedAt, accepted.AcceptedAt);
        Assert.Equal(DemandExecutionStatus.Accepted, accepted.Status);

        JourneyRuntimeRow second = await reading.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.DemandId == Released, token);
        Assert.Equal(JourneyIdentity.ForAnchorDemand($"{Released}|g2"), second.JourneyId);
        Assert.NotEqual(first.JourneyId, second.JourneyId);

        JourneyDemandRow[] memberships = await reading.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => row.DemandId == Released).ToArrayAsync(token);
        JourneyDemandRow firstMembership = memberships.Single(row => row.JourneyId == first.JourneyId);
        JourneyDemandRow secondMembership = memberships.Single(row => row.JourneyId == second.JourneyId);
        Assert.NotNull(firstMembership.RemovedAt);
        Assert.Null(secondMembership.RemovedAt);
        Assert.Equal(2, secondMembership.DispatchGeneration);
        Assert.Empty(new[]
        {
            firstMembership.LoadSlotOperationAttemptId, firstMembership.LoadCommandMessageId,
            firstMembership.UnloadSlotOperationAttemptId, firstMembership.UnloadCommandMessageId,
        }.Intersect(new[]
        {
            secondMembership.LoadSlotOperationAttemptId, secondMembership.LoadCommandMessageId,
            secondMembership.UnloadSlotOperationAttemptId, secondMembership.UnloadCommandMessageId,
        }, StringComparer.Ordinal));

        JourneyStopRow[] stops = await reading.Set<JourneyStopRow>().AsNoTracking().ToArrayAsync(token);
        string[] Ids(string journeyId) =>
        [
            .. stops.Where(row => row.JourneyId == journeyId).SelectMany(row => new[]
            {
                row.StopId, row.OperationSessionId, row.MovementLegId, row.UpperId, row.VehicleBusinessMessageId,
                row.WorklistMessageId, row.PlanMessageId,
            })
        ];
        Assert.NotEmpty(Ids(second.JourneyId));
        Assert.Empty(Ids(first.JourneyId).Intersect(Ids(second.JourneyId), StringComparer.Ordinal));

        Assert.Equal(firstJourneyBefore, await FirstJourneyRowsAsync(fixture, first.JourneyId));

        // 同一次改派重放：需求已不再是待改派（它有生效的归属了），于是走重放判同，照旧不抛、不多写一行。
        await AcceptRedispatchAsync(fixture, plan);
        await using ControlServerDbContext afterReplay = fixture.NewContext();
        Assert.Equal(2, await afterReplay.JourneyRuntimes.CountAsync(token));
        Assert.Equal(1, await afterReplay.AcceptedDemands.CountAsync(row => row.DemandId == Released, token));
    }

    /// <summary>
    /// 没被释放的已受理需求，带着改派的计划来受理，仍然是冲突：复用受理行只给「已释放待改派」那一批。
    /// 判据写宽了，这条会在一趟还带着它的旅程之外，再给它建一趟。
    /// </summary>
    [Fact]
    public async Task ADemandThatWasNotReleasedIsNotAcceptedASecondTime()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await AcceptTwoDemandJourneyAsync(fixture);

        await Assert.ThrowsAsync<BusinessIdentityConflictException>(
            () => AcceptRedispatchAsync(fixture, RedispatchPlan(generation: 2)));
        await using ControlServerDbContext reading = fixture.NewContext();
        Assert.Equal(1, await reading.JourneyRuntimes.CountAsync(TestContext.Current.CancellationToken));
    }

    private static JourneyExecutionPlan CreatePlan(long? redispatchGeneration) =>
        new JourneyPlanBuilder(Options).CreatePlan(
            new FleetVehicle("AGV-2", "BROKERX-0002", 1),
            new EligibleDispatchCandidate(
                Batch7JourneyFixture.Snapshot(Released, Now),
                new ResolvedJourneyRoute(
                    "MAP-25-WIRE_TO_GATE",
                    $"route-{Released}",
                    "ST-PICKUP",
                    101,
                    "ST-GATE",
                    202,
                    FixedTaskStationResolution.Resolved(
                        TransportTaskTypes.WireToGate, FixedStationEnd.Destination, new RiotMapStation(202, "ST-GATE"))),
                2,
                [1, 2],
                Now),
            Now,
            redispatchGeneration);

    /// <summary>
    /// 改派的计划，派生方式与 <see cref="JourneyPlanBuilder.CreatePlan"/> 一致（上面两条用例钉着它），
    /// 端点与夹具的第一次受理相同，好让冻结的判同不因端点不同而拒。
    /// </summary>
    private static JourneyExecutionPlan RedispatchPlan(long generation)
    {
        string key = JourneyIdentity.DerivationKey(Released, generation);
        return Batch7JourneyFixture.Plan(Released, "agv-02", "VK-02", Now.AddMinutes(2), generation) with
        {
            OperationSessionId = JourneyPlanBuilder.StableGuid(key, "operation-session"),
            PickupMovementLegId = JourneyPlanBuilder.StableGuid(key, "pickup-leg"),
            GateMovementLegId = JourneyPlanBuilder.StableGuid(key, "gate-leg"),
            IdentityKey = key,
        };
    }

    private static Task AcceptRedispatchAsync(Batch7JourneyFixture fixture, JourneyExecutionPlan plan) =>
        new WireToGateStore(fixture.NewContext()).AcceptWithOrderIntentAsync(
            Batch7JourneyFixture.Snapshot(Released, Now),
            JourneyPlanBuilder.PickupIntent(plan, Released, Now.AddMinutes(2)),
            plan,
            TestContext.Current.CancellationToken);

    private static async Task<JourneyRuntimeRow> AcceptTwoDemandJourneyAsync(Batch7JourneyFixture fixture)
    {
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, Anchor, "agv-01", "VK-01", Now);
        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        await JourneyMembershipSeed.AddFurtherDemandAsync(fixture.Context, runtime, Released);
        return runtime;
    }

    /// <summary>第一趟旅程的旅程行、停靠与归属，逐列逐行，排好序——改派前后必须逐字相同。</summary>
    private static async Task<string[]> FirstJourneyRowsAsync(Batch7JourneyFixture fixture, string journeyId)
    {
        List<string> rows = [];
        foreach (string table in new[] { "JourneyRuntimes", "JourneyStops", "JourneyDemands" })
        {
            rows.AddRange((await Batch7JourneyFixture.DumpAsync(fixture.Connection, table))
                .Where(row => row.Contains($"JourneyId='{journeyId}'", StringComparison.Ordinal))
                .Select(row => $"{table}: {row}"));
        }

        Assert.NotEmpty(rows);
        return [.. rows];
    }
}
