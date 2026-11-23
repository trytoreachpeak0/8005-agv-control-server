using ControlServer.Application;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// A vehicle whose Onboard connection has ended takes no new purpose, before its last message ages out (control-server#912).
/// </summary>
/// <remarks>
/// <para>
/// The timeline is agv03's of 11-23 (UTC): its Onboard was killed and the server read the reset at 13:19:05; the session row
/// stayed <c>Ready</c>, the last message was seconds old, and the round of 13:19:10 committed it to an idle return to 214,
/// where it drove with no Onboard. Here the vehicle speaks (a heartbeat), its connection ends in the same instant, and rounds
/// run 5, 15 and 25 seconds later -- all inside the thirty seconds of <c>MaximumEvidenceAge</c> that used to be the only
/// bound -- with nothing heard from it in between. Then it reconnects and is dispatchable again.
/// </para>
/// <para>
/// The fixture's presence double stands for the transport's <c>OnboardPeer</c>: the connection is up for the session row's
/// generation unless a test names the vehicle in <c>Disconnected</c>. Built on <see cref="ChargingAllocationTests.FleetAsync"/>'s
/// fleet (route graph with charger 211 and waiting point 214, one vehicle at 300).
/// </para>
/// </remarks>
public sealed partial class MultiVehicleExecutionTests
{
    private static readonly WaitingPointEntry DisconnectWaitingPoint = new(25, 214, "等待点214", true, []);

    private static readonly TimeSpan[] SecondsAfterTheDisconnect =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)];

    /// <summary>
    /// agv03 on 11-23: idle at a station that is not a waiting point, with idle return on. Disconnected, it is not committed to
    /// an idle return at 5, 15 or 25 seconds; reconnected, it is, in the next round.
    /// </summary>
    [Fact]
    public async Task AVehicleWhoseOnboardDisconnectedIsNotSentOnAnIdleReturnUntilItReconnects()
    {
        await using FleetFixture fleet = await ChargingAllocationTests.FleetAsync(roster: false);
        await fleet.EnableIdleReturnAsync(DisconnectWaitingPoint);
        string agv = FleetFixture.AgvIds[0];
        await fleet.HearFromEveryVehicleAsync();
        fleet.Presence.Disconnected.Add(agv);

        foreach (TimeSpan step in SecondsAfterTheDisconnect)
        {
            await fleet.RunRoundAsync(step);
            await AssertNoNewPurposeAsync(fleet);
        }

        await ReconnectAsync(fleet, agv);
        Assert.Equal(
            VehiclePurposes.IdleReturn, (await ChargingAllocationTests.ClaimOfAsync(fleet, FleetFixture.VehicleKeys[0]))?.Purpose);
    }

    /// <summary>
    /// Below its mandatory charge line, disconnected: no charger is allocated at 5, 15 or 25 seconds, and the allocation says
    /// why (<c>ONBOARD_FACTS_NOT_READY</c>, which agv03 only got at 13:24:59). Reconnected, it is sent to charge.
    /// </summary>
    [Fact]
    public async Task AVehicleWhoseOnboardDisconnectedIsAllocatedNoChargerUntilItReconnects()
    {
        await using FleetFixture fleet = await ChargingAllocationTests.FleetAsync();
        fleet.Riot.BatteryByVehicle[FleetFixture.VehicleKeys[0]] = 20;
        string agv = FleetFixture.AgvIds[0];
        await fleet.HearFromEveryVehicleAsync();
        fleet.Presence.Disconnected.Add(agv);

        foreach (TimeSpan step in SecondsAfterTheDisconnect)
        {
            await fleet.RunRoundAsync(step);
            await AssertNoNewPurposeAsync(fleet);
            Assert.Equal("ONBOARD_FACTS_NOT_READY", fleet.ChargingBoard.Verdicts[agv].Reason);
        }

        await ReconnectAsync(fleet, agv);
        Assert.Equal(
            VehiclePurposes.Charging, (await ChargingAllocationTests.ClaimOfAsync(fleet, FleetFixture.VehicleKeys[0]))?.Purpose);
    }

    /// <summary>
    /// A demand that fits it, disconnected: it is not accepted at 5, 15 or 25 seconds and waits with
    /// <c>ONBOARD_FACTS_NOT_READY</c>; reconnected, the vehicle takes it.
    /// </summary>
    [Fact]
    public async Task AVehicleWhoseOnboardDisconnectedTakesNoTransportUntilItReconnects()
    {
        await using FleetFixture fleet = await ChargingAllocationTests.FleetAsync(roster: false);
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        string agv = FleetFixture.AgvIds[0];
        await fleet.HearFromEveryVehicleAsync();
        fleet.Presence.Disconnected.Add(agv);

        foreach (TimeSpan step in SecondsAfterTheDisconnect)
        {
            await fleet.RunRoundAsync(step);
            await AssertNoNewPurposeAsync(fleet);
            JourneyBacklogRow backlog = Assert.Single(
                await fleet.Context.JourneyBacklog.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
            Assert.Equal("ONBOARD_FACTS_NOT_READY", backlog.ReasonCode);
        }

        await ReconnectAsync(fleet, agv);
        Assert.Equal(
            VehiclePurposes.Transport, (await ChargingAllocationTests.ClaimOfAsync(fleet, FleetFixture.VehicleKeys[0]))?.Purpose);
        Assert.Equal(agv, (await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken)).AgvId);
    }

    /// <summary>
    /// Connected when the round read it, disconnected the moment intake re-reads the catalog: the final admission re-check
    /// reads the connection again with the facts, so the demand is not accepted and nothing is created.
    /// </summary>
    /// <remarks><c>OnReread</c> clears itself when it runs; asserting it is null shows the round did reach intake.</remarks>
    [Fact]
    public async Task AConnectionThatEndsWhileTheDemandIsBeingAcceptedStopsTheAcceptance()
    {
        await using FleetFixture fleet = await ChargingAllocationTests.FleetAsync(roster: false);
        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        string agv = FleetFixture.AgvIds[0];
        await fleet.HearFromEveryVehicleAsync();
        fleet.Catalog.OnReread = () => fleet.Presence.Disconnected.Add(agv);

        await fleet.RunRoundAsync(TimeSpan.FromSeconds(1));

        Assert.Null(fleet.Catalog.OnReread);
        await AssertNoNewPurposeAsync(fleet);
    }

    /// <summary>
    /// Committed to an idle return while connected, then disconnected before the order went out: the departure gate holds it
    /// (<c>ONBOARD_NOT_CONNECTED</c>) and RIoT gets no order; reconnected, the order is created.
    /// </summary>
    /// <remarks>The round that commits sends nothing; the next one materializes the order behind the departure gate.</remarks>
    [Fact]
    public async Task AnIdleReturnCommittedBeforeTheDisconnectDoesNotSetOffUntilTheVehicleReconnects()
    {
        await using FleetFixture fleet = await ChargingAllocationTests.FleetAsync(roster: false);
        await fleet.EnableIdleReturnAsync(DisconnectWaitingPoint);
        string agv = FleetFixture.AgvIds[0];
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(
            VehiclePurposes.IdleReturn, (await ChargingAllocationTests.ClaimOfAsync(fleet, FleetFixture.VehicleKeys[0]))?.Purpose);
        Assert.Empty(fleet.Riot.Creates);

        fleet.Presence.Disconnected.Add(agv);
        foreach (TimeSpan step in SecondsAfterTheDisconnect)
        {
            await fleet.RunRoundAsync(step);
            Assert.Empty(fleet.Riot.Creates);
        }

        await ReconnectAsync(fleet, agv);
        Assert.Contains(fleet.Riot.Creates, create => create.VehicleKey == FleetFixture.VehicleKeys[0]);
    }

    /// <summary>No purpose claim, no journey, no RIoT order for anyone.</summary>
    private static async Task AssertNoNewPurposeAsync(FleetFixture fleet)
    {
        Assert.Empty(await fleet.Context.Set<VehiclePurposeClaimRow>().AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fleet.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(fleet.Riot.Creates);
    }

    /// <summary>The vehicle's connection is back and it speaks; one round, a second later.</summary>
    private static async Task ReconnectAsync(FleetFixture fleet, string agvId)
    {
        fleet.Presence.Disconnected.Remove(agvId);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(1));
    }
}
