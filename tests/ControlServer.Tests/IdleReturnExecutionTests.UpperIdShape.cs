using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>control-server#616: the upperId an idle return is created under, before and after it was shortened.</summary>
public sealed partial class IdleReturnExecutionTests
{
    /// <summary>
    /// An idle return materialized and created before the upgrade carries the old upperId (<c>W2G-IDLE-{deviceKey}-{instant}</c>),
    /// which RIoT took because the rig's device key was short. After the upgrade the engine still knows that order by the upperId
    /// stored on its intent: it reconciles it rather than creating a second order under the new shape, and the journey finishes.
    /// </summary>
    [Fact]
    public async Task AnIdleReturnCreatedUnderTheOldUpperIdShapeIsStillReconciledAndFinishedAfterTheUpgrade()
    {
        await using FleetFixture fleet = await FleetAsync();
        await RoundAsync(fleet);
        (string Purpose, string JourneyId) claim = (await ClaimOfAsync(fleet, KeyA))!.Value;
        string oldUpperId = "W2G-IDLE-" + claim.JourneyId[IdleReturnIdentity.JourneyIdPrefix.Length..].Replace(':', '-');
        Assert.NotEqual(oldUpperId, IdleReturnJourneyShape.UpperIdFor(claim.JourneyId));
        (JourneyRuntimeRow runtime, JourneyStopRow stop, OrderIntent intent) = IdleReturnJourneyShape.Build(
            claim.JourneyId, new FleetVehicle(AgvA, KeyA, 1), Near.StationId, Near.StationName, fleet.Options, fleet.Clock.GetUtcNow());
        runtime.PickupUpperId = oldUpperId;
        stop.UpperId = oldUpperId;
        intent = intent with { UpperId = oldUpperId };
        Assert.True(await new WireToGateStore(fleet.Context).MaterializeIdleReturnAsync(runtime, stop, intent, Token));
        // Created before the upgrade; the answer was never recorded, so the intent is still pending.
        await fleet.Riot.CreateAsync(intent, Token);

        await RoundAsync(fleet);

        Assert.Equal([oldUpperId], fleet.Riot.Creates.Select(create => create.UpperId).ToArray());
        OrderIntentRow stored = await fleet.Context.OrderIntents.AsNoTracking().SingleAsync(Token);
        Assert.Equal((oldUpperId, "CONFIRMED"), (stored.UpperId, stored.Status));
        JourneyRuntimeRow journey = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.Equal(oldUpperId, journey.PickupUpperId);

        await ArriveAsync(fleet, journey, KeyA, Near.StationId);

        JourneyRuntimeRow closed = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.Equal((JourneyRuntimeStage.Completed, (string?)null), (closed.Stage, closed.BlockReasonCode));
        Assert.Equal([oldUpperId], fleet.Riot.Creates.Select(create => create.UpperId).ToArray());
    }

    /// <summary>
    /// A vehicle with a real device key (<c>BROKERX-</c> and 32 hex digits) gets its idle-return order created and confirmed. The
    /// fleet's RIoT double answers an upperId longer than RIoT is known to take with <c>00002</c>, as RIoT did on 2026-10-11.
    /// </summary>
    [Fact]
    public async Task AnIdleReturnForAVehicleWithAProductionShapedDeviceKeyIsCreatedAndConfirmed()
    {
        const string ProductionKey = "BROKERX-f38975561adf46ccb1d2f23833c7d0e4";
        await using FleetFixture fleet = await FleetAsync();
        string journeyId = IdleReturnIdentity.JourneyIdFor(ProductionKey, fleet.Clock.GetUtcNow());
        (JourneyRuntimeRow runtime, JourneyStopRow stop, OrderIntent intent) = IdleReturnJourneyShape.Build(
            journeyId, new FleetVehicle(AgvA, ProductionKey, 1), Near.StationId, Near.StationName, fleet.Options, fleet.Clock.GetUtcNow());
        WireToGateStore store = new(fleet.Context);
        Assert.True(await store.MaterializeIdleReturnAsync(runtime, stop, intent, Token));

        MovementDispatchResult result = await new MovementDispatchService(store, fleet.Riot).ReconcileOrCreateAsync(intent.UpperId, Token);

        Assert.Equal(MovementDispatchOutcome.Confirmed, result.Outcome);
        Assert.Equal([intent.UpperId], fleet.Riot.Creates.Select(create => create.UpperId).ToArray());
    }
}
