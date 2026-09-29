using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// control-server#401 (coordinator comment of 09-29): every path that builds an order intent carries its order shape.
/// <c>WireToGateStore.Matches</c> compares the shape since control-server#399, so a charging leg the authorisation path
/// rebuilt as a single move would throw <see cref="BusinessIdentityConflictException"/> every round, and every vehicle
/// after this one would stop advancing.
/// </summary>
/// <remarks>
/// No path creates a charging stop yet (that is control-server#404), so the stop is turned into one here the way #404
/// will write it: <see cref="JourneyStopRoles.Charger"/> on the stop, a <see cref="OrderShapes.Charge"/> intent stored for
/// its leg.
/// </remarks>
public sealed class ChargingOrderShapePathTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(JourneyStopRoles.Charger, OrderShapes.Charge)]
    [InlineData(JourneyStopRoles.Pickup, OrderShapes.SingleMove)]
    [InlineData(JourneyStopRoles.Unload, OrderShapes.SingleMove)]
    [InlineData("charger", OrderShapes.SingleMove)]
    [InlineData("", OrderShapes.SingleMove)]
    // A role this function does not know is never a charging order: #390's waiting point, and anything newer.
    [InlineData("WAITING_POINT", OrderShapes.SingleMove)]
    [InlineData("SOME_FUTURE_ROLE", OrderShapes.SingleMove)]
    public void OnlyAChargerStopIsAChargingOrder(string stopRole, string expected)
    {
        Assert.Equal(expected, JourneyStopRoles.OrderShapeOf(stopRole));
    }

    [Fact]
    public async Task AChargingLegAuthorisedAgainEveryRoundMatchesTheStoredChargingIntent()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        DateTimeOffset at = Batch7JourneyFixture.Now.AddMinutes(5);
        (JourneyRuntimeRow runtime, JourneyStopRow charger) = await ChargerLegAsync(fixture);
        WireToGateStore store = new(fixture.Context);
        // What #404 stores when it commits the charge, written out field by field rather than through LegIntent, so this
        // test does not compare LegIntent with itself.
        OrderIntent stored = new(
            charger.MovementLegId, runtime.DemandId, charger.UpperId, "TO_GATE", charger.StationId, at,
            runtime.VehicleKey, runtime.MapId, charger.StationRiotId, runtime.AgvLifecycleGeneration,
            runtime.DispatchGeneration, OrderShapes.Charge);
        await store.AuthorizeMovementAsync(stored, Safe(runtime, at), at, Token);

        OrderIntent replayed = JourneyPlanBuilder.LegIntent(runtime, charger, at);
        await store.AuthorizeMovementAsync(replayed, Safe(runtime, at), at, Token);
        await store.AuthorizeMovementAsync(replayed, Safe(runtime, at.AddSeconds(1)), at.AddSeconds(1), Token);

        Assert.Equal(OrderShapes.Charge, replayed.OrderShape);
        OrderIntentRow row = await fixture.Context.OrderIntents.AsNoTracking()
            .SingleAsync(item => item.UpperId == charger.UpperId, Token);
        Assert.Equal(OrderShapes.Charge, row.OrderShape);
    }

    [Fact]
    public async Task TheSameLegBuiltAsASingleMoveConflictsWithTheStoredChargingIntent()
    {
        // The failure the shape on LegIntent prevents, shown directly: without it this is what every round would throw.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        DateTimeOffset at = Batch7JourneyFixture.Now.AddMinutes(5);
        (JourneyRuntimeRow runtime, JourneyStopRow charger) = await ChargerLegAsync(fixture);
        WireToGateStore store = new(fixture.Context);
        OrderIntent charging = JourneyPlanBuilder.LegIntent(runtime, charger, at);
        await store.AuthorizeMovementAsync(charging, Safe(runtime, at), at, Token);

        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() => store.AuthorizeMovementAsync(
            charging with { OrderShape = OrderShapes.SingleMove }, Safe(runtime, at), at, Token));
    }

    private static async Task<(JourneyRuntimeRow Runtime, JourneyStopRow Charger)> ChargerLegAsync(
        Batch7JourneyFixture fixture)
    {
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-C", "agv-02", "VK-02", Batch7JourneyFixture.Now);
        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.SingleAsync(row => row.DemandId == "D-C", Token);
        JourneyStopRow stop = await fixture.Context.Set<JourneyStopRow>()
            .SingleAsync(row => row.StopId == JourneyIdentity.UnloadStopId(runtime.JourneyId), Token);
        stop.StopRole = JourneyStopRoles.Charger;
        await fixture.Context.SaveChangesAsync(Token);
        return (runtime, stop);
    }

    private static SafetyCheckObservation Safe(JourneyRuntimeRow runtime, DateTimeOffset at) =>
        new(runtime.PreDepartureSafetyCheckId, 1, true, at, at.AddMinutes(1));
}
