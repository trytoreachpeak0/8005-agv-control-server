using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// Whatever ends a stop without its cargo leaving it, the vehicle is free for the next demand afterwards
/// (control-server#131). Driven like <c>AfterAStationTimeoutTheVehicleTakesTheNextDemand...</c>: the stop is
/// ended, a second demand is put in the catalog, and two runtime rounds must dispatch it to the same vehicle
/// rather than block it on VEHICLE_OCCUPANCY_CONFLICT.
/// </summary>
public sealed partial class JourneyRuntimeWorkerTests
{
    private const string InFlightCancellationId = "c1310000-0000-4000-8000-000000000001";
    private const string NextDemandId = "10000000-0000-4000-8000-000000000002";

    /// <summary>
    /// The real-rig reproduction of control-server#131 (real-onboard-load-door-closed-empty-reopens, L2-DC-10
    /// and L2-DC-12): the load was commanded, the operator cancels it, the vehicle proves every slot empty.
    /// The stop ends as CANCELLED_BY_OPERATOR with the load operation cancelled, and the pickup order no
    /// longer holds the vehicle -- until #131 it did, and every later claim was refused by the index.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AfterAnInFlightLoadIsCancelledTheVehicleTakesTheNextDemand()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        AcceptedDemandSnapshot cancelled = fixture.Demand(
            BeforeSublotDemandId, "SUBLOT-001", createdAt: Now.AddMinutes(-10));
        fixture.Catalog.Set(cancelled);
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.AdvanceToLoadResultAsync();
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        int[] slots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!;

        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = BeforeSublotConnection(fixture, generation: 1);
        string authorization = await processor.ProcessAsync(
            InFlightCancellationRequest(fixture, load.SlotOperationAttemptId), state, token);
        Assert.Equal("AUTHORIZED", FirstLinePayload(authorization, out _).GetProperty("decision").GetString());
        DateTimeOffset receivedAt = fixture.Clock.GetUtcNow();
        string ack = await processor.ProcessAsync(
            InFlightCancellationResult(fixture, load.SlotOperationAttemptId, slots), state, token);
        Assert.Equal("DurableAck", FirstLineType(ack));

        JourneyRuntimeRow ended = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, ended.Stage);
        Assert.Equal("CANCELLED_BY_OPERATOR", ended.BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.DemandRowAsync()).Status);
        Assert.Equal(StationOperationStatus.Cancelled,
            (await fixture.Context.StationOperations.AsNoTracking().SingleAsync(token)).Status);
        Assert.Equal(receivedAt, (await fixture.LeaseAsync()).ReleasedAt);
        OrderIntentRow pickup = await fixture.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.Purpose == "TO_PICKUP", token);
        Assert.Equal(receivedAt, pickup.VehicleOccupancyReleasedAt);

        await AssertTheVehicleTakesTheNextDemandAsync(fixture, cancelled);
    }

    /// <summary>
    /// Regression guard for load-cancelled-before-sublot: converging the commanded settlements onto the same
    /// tail must leave the uncommanded one as it was, down to the vehicle taking the next demand.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task AfterACancellationBeforeAnySublotTheVehicleTakesTheNextDemand()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        AcceptedDemandSnapshot cancelled = fixture.Demand(
            BeforeSublotDemandId, "SUBLOT-001", createdAt: Now.AddMinutes(-10));
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = BeforeSublotConnection(fixture, generation: 1);
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();
        await processor.ProcessAsync(
            CancellationBeforeSublotRequest(fixture, BeforeSublotCancellationId, generation: 1), state, token);
        DateTimeOffset receivedAt = fixture.Clock.GetUtcNow();
        await processor.ProcessAsync(
            CancellationBeforeSublotResult(fixture, "c1310000-0000-4000-8000-000000000101", generation: 1),
            state,
            token);
        await AssertStopEndedByOperatorAsync(fixture, waiting, receivedAt);

        await AssertTheVehicleTakesTheNextDemandAsync(fixture, cancelled);
    }

    private static async Task AssertTheVehicleTakesTheNextDemandAsync(
        RuntimeFixture fixture,
        AcceptedDemandSnapshot cancelled)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        AcceptedDemandSnapshot next = fixture.Demand(NextDemandId, "SUBLOT-002", createdAt: Now.AddMinutes(-5));
        fixture.Catalog.Set([cancelled, next]);
        fixture.BoxCounts.Set("SUBLOT-002", 7);
        await fixture.Engine.ExecuteOnceAsync(token);
        await fixture.Engine.ExecuteOnceAsync(token);

        JourneyRuntimeRow nextRuntime = await fixture.RuntimeAsync(next.DemandId);
        Assert.Null(nextRuntime.BlockReasonCode);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, nextRuntime.Stage);
        Assert.Equal((await fixture.RuntimeAsync(cancelled.DemandId)).AgvId, nextRuntime.AgvId);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    private static string InFlightCancellationRequest(RuntimeFixture fixture, string attemptId) =>
        BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), "LoadCancellationStartRequested", 1, new
        {
            cancellationId = InFlightCancellationId,
            demandId = BeforeSublotDemandId,
            slotOperationAttemptId = attemptId,
            @operator = BeforeSublotOperator(fixture),
            reason = "The operator gave up the load after the station deadline."
        });

    private static string InFlightCancellationResult(RuntimeFixture fixture, string attemptId, int[] slots) =>
        BeforeSublotEnvelope(fixture, "c1310000-0000-4000-8000-000000000102", "LoadCancellationResult", 1, new
        {
            cancellationId = InFlightCancellationId,
            demandId = BeforeSublotDemandId,
            slotOperationAttemptId = attemptId,
            overallOutcome = "ALL_EMPTY",
            slotResults = slots.Select(slot => new
            {
                slotNo = slot,
                outcome = "COMPLETED",
                finalPhysicalState = "EMPTY",
                lockState = "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = Array.Empty<string>()
            }).ToArray(),
            observedAt = Now
        });
}
