using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// The two recovery actions that end a load by taking its cargo out by hand -- the fault cargo handoff
/// (REQ-0240) and the forced mechanical recovery (REQ-0241/0242) -- driven through the real runtime and
/// the real message processor, on two contexts over one database the way the host runs them
/// (control-server#137).
/// </summary>
/// <remarks>
/// <para>
/// <b>What "permanently suppressed" means here, and what it does not.</b> REQ-0240 writes a
/// TransportDemandSuppression keyed by TransportDemandKey. v2 has no such table for any ending --
/// <c>CANCELLED_BY_OPERATOR</c>, <c>CANCELLED_BY_STATION_TIMEOUT</c> and <c>CANCELLED_BY_LOAD_COMPENSATION</c>
/// write none either; key suppression is REQ-0155/0156/0211, profiled to batch 7. What does hold for every
/// ending today is DemandId-level: an accepted demand is never a candidate again (AlreadyAcceptedCriterion),
/// even while MesIngest keeps listing it. These tests prove that half, for the two endings #137 is about.
/// </para>
/// </remarks>
public sealed class JourneyRuntimeWorkerCargoRecoveryTests
{
    private const string EndedDemandId = "10000000-0000-4000-8000-000000000001";
    private const string NextDemandId = "10000000-0000-4000-8000-000000000002";
    private const string RequestId = "41370000-0000-4000-8000-000000000001";
    private const string EventId = "31370000-0000-4000-8000-000000000001";
    private const string ActionId = "51370000-0000-4000-8000-000000000001";
    private const string ProofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_CARGO_RECOVERY_RUNTIME";
    private const string Proof = "cargo-recovery-runtime-proof-not-a-production-secret";
    private static readonly string[] HardwareChecks = ["LIVE_SLOT_SIGNALS_VALID"];
    private static readonly string[] HardwareActions = ["ADMINISTRATOR_CONFIRMED_HARDWARE_REPAIRED"];
    private static readonly string[] HardwareObservations = ["Lock replaced; doors shut and read locked."];

    /// <summary>
    /// REQ-0240: after a fault cargo handoff the demand is terminated and MesIngest still lists it. It is
    /// never dispatched again, and the vehicle -- whose slots the handoff proved empty and locked -- takes
    /// the next demand instead.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AHandedOffDemandStillListedByMesIngestIsNeverDispatchedAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Environment.SetEnvironmentVariable(ProofVariable, Proof);
        try
        {
            (RuntimeFixture fixture, AcceptedDemandSnapshot ended, int[] slots) = await ReachBlockedLoadAsync();
            await using (fixture)
            {
                await using ControlServerDbContext connection = fixture.OpenConnectionContext();
                OnboardMessageProcessor processor = RecoveryProcessor(fixture, connection);
                OnboardConnectionState state = Connection(fixture);
                string sessionId = await OpenSessionAsync(fixture, processor, state, slots);
                await processor.ProcessAsync(Action(fixture, sessionId, "FAULT_CARGO_HANDOFF", slots), state, token);
                string handoffId = (await fixture.Context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).HandoffId!;

                string ack = await processor.ProcessAsync(HandedOff(fixture, sessionId, handoffId, slots), state, token);
                Assert.Equal("DurableAck", FirstLineType(ack));
                Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.DemandRowAsync()).Status);
                Assert.Equal("TERMINATED_BY_FAULT_CARGO_HANDOFF", (await fixture.RuntimeAsync()).BlockReasonCode);
                await VehicleOccupancyAssertions.AssertActiveLeasesAndPurposeClaimsMatchAsync(fixture.Context);
                await ZeroChangePin.AssertMatchesAsync(fixture.Context, "fault-cargo-handoff");

                await fixture.RestoreSessionReadyAsync();
                AcceptedDemandSnapshot next = await ListNextDemandBesideAsync(fixture, ended);
                await fixture.Engine.ExecuteOnceAsync(token);
                await fixture.Engine.ExecuteOnceAsync(token);

                await AssertEndedDemandNeverDispatchedAgainAsync(fixture, ended);
                Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync(next.DemandId)).Stage);
                Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProofVariable, null);
        }
    }

    /// <summary>
    /// REQ-0242 end to end: the forced mechanical recovery terminates the demand, which is never dispatched
    /// again; the vehicle, reconnected on the new forced generation, still takes no work -- its slots are
    /// physically unknown -- until the administrator's HardwareRecoveryRecord for the forced workflow; then it
    /// takes the next demand, and the ended one stays ended.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedRecoveryEndsTheDemandForGoodAndTheVehicleWorksAgainOnlyAfterItsHardwareRecord()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Environment.SetEnvironmentVariable(ProofVariable, Proof);
        try
        {
            (RuntimeFixture fixture, AcceptedDemandSnapshot ended, int[] slots) = await ReachBlockedLoadAsync();
            await using (fixture)
            {
                await using ControlServerDbContext connection = fixture.OpenConnectionContext();
                OnboardMessageProcessor processor = RecoveryProcessor(fixture, connection);
                OnboardConnectionState state = Connection(fixture);
                string sessionId = await OpenSessionAsync(fixture, processor, state, slots);
                await processor.ProcessAsync(Action(fixture, sessionId, "FORCED_MECHANICAL_RECOVERY", slots), state, token);

                string ack = await processor.ProcessAsync(MechanicallyIsolated(fixture, sessionId, slots), state, token);
                Assert.Equal("DurableAck", FirstLineType(ack));
                Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.DemandRowAsync()).Status);
                Assert.Equal("TERMINATED_BY_FAULT_CARGO_HANDOFF", (await fixture.RuntimeAsync()).BlockReasonCode);
                await VehicleOccupancyAssertions.AssertActiveLeasesAndPurposeClaimsMatchAsync(fixture.Context);
                await ZeroChangePin.AssertMatchesAsync(fixture.Context, "forced-mechanical-recovery");

                // The vehicle reconnects having adopted the new generation and reports nothing open.
                WireToGateStore store = new(connection);
                SessionRecoveryRow session = await connection.SessionRecoveries.AsNoTracking().SingleAsync(token);
                await store.ApplyRecoveryReportAsync(
                    fixture.Options.AgvId, session.SessionGeneration, "f1370000-0000-4000-8000-000000000001",
                    forcedRecoveryGeneration: 1, null, "NONE", [], [], [], token);
                Assert.Equal(WireToGateStore.ForcedRecoveryHardwareRecoveryRequired,
                    (await store.DecideReadinessAsync(fixture.Options.AgvId, session.SessionGeneration, token)).ReasonCode);

                AcceptedDemandSnapshot next = await ListNextDemandBesideAsync(fixture, ended);
                await fixture.Engine.ExecuteOnceAsync(token);
                await fixture.Engine.ExecuteOnceAsync(token);
                Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
                Assert.False(await fixture.Context.JourneyRuntimes.AnyAsync(row => row.DemandId == next.DemandId, token));

                string recorded = await processor.ProcessAsync(HardwareRecord(fixture, sessionId, slots), state, token);
                Assert.Equal("RECORDED", FirstLinePayload(recorded).GetProperty("outcome").GetString());
                Assert.Equal(SessionReadiness.Ready,
                    (await connection.SessionRecoveries.AsNoTracking().SingleAsync(token)).Readiness);
                await fixture.Engine.ExecuteOnceAsync(token);
                await fixture.Engine.ExecuteOnceAsync(token);

                await AssertEndedDemandNeverDispatchedAgainAsync(fixture, ended);
                Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync(next.DemandId)).Stage);
                Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProofVariable, null);
        }
    }

    /// <summary>
    /// A load whose result the server could not reconcile: the operation needs recovery and the journey is
    /// Blocked on it, which is where every recovery session starts.
    /// </summary>
    private static async Task<(RuntimeFixture Fixture, AcceptedDemandSnapshot Demand, int[] Slots)> ReachBlockedLoadAsync()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot demand = fixture.Demand(EndedDemandId, "SUBLOT-001", createdAt: Now.AddMinutes(-10));
        fixture.Catalog.Set(demand);
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.AdvanceToLoadResultAsync();
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        await fixture.ApplyTimedOutResultAsync(load, SlotOperationType.Load);
        await fixture.Engine.ExecuteOnceAsync(token);
        JourneyRuntimeRow blocked = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Blocked, blocked.Stage);
        Assert.Equal(StationOperationStatus.RecoveryRequired, (await fixture.OperationAsync(SlotOperationType.Load)).Status);
        return (fixture, demand, JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!);
    }

    private static async Task<AcceptedDemandSnapshot> ListNextDemandBesideAsync(
        RuntimeFixture fixture,
        AcceptedDemandSnapshot ended)
    {
        AcceptedDemandSnapshot next = fixture.Demand(NextDemandId, "SUBLOT-002", createdAt: Now.AddMinutes(-5));
        fixture.Catalog.Set([ended, next]);
        fixture.BoxCounts.Set("SUBLOT-002", 4);
        await Task.CompletedTask;
        return next;
    }

    private static async Task AssertEndedDemandNeverDispatchedAgainAsync(
        RuntimeFixture fixture,
        AcceptedDemandSnapshot ended)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Assert.Equal("DEMAND_ALREADY_ACCEPTED", (await fixture.BacklogAsync(ended.DemandId)).ReasonCode);
        Assert.Equal(1, await fixture.Context.JourneyRuntimes.CountAsync(row => row.DemandId == ended.DemandId, token));
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync(ended.DemandId)).Stage);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.Context.AcceptedDemands.AsNoTracking()
            .SingleAsync(row => row.DemandId == ended.DemandId, token)).Status);
        await VehicleOccupancyAssertions.AssertActiveLeasesAndPurposeClaimsMatchAsync(fixture.Context);
    }

    private static async Task<string> OpenSessionAsync(
        RuntimeFixture fixture,
        OnboardMessageProcessor processor,
        OnboardConnectionState state,
        int[] slots)
    {
        string opened = await processor.ProcessAsync(
            Envelope(fixture, "ExceptionRecoverySessionRequested", new
            {
                requestId = RequestId,
                administrator = BeforeSublotOperator(fixture),
                administratorRole = "MAINTENANCE_ADMINISTRATOR",
                eventId = EventId,
                demandId = EndedDemandId,
                slots,
                reason = "The load cannot be recovered in place.",
                authenticationProof = Proof
            }),
            state,
            TestContext.Current.CancellationToken);
        Assert.Equal("ExceptionRecoverySessionOpened", FirstLineType(opened));
        return FirstLinePayload(opened).GetProperty("exceptionRecoverySessionId").GetString()!;
    }

    private static string Action(RuntimeFixture fixture, string sessionId, string action, int[] slots) =>
        Envelope(fixture, "RecoveryActionSubmitted", new
        {
            recoveryActionId = ActionId,
            exceptionRecoverySessionId = sessionId,
            action,
            eventId = EventId,
            demandId = EndedDemandId,
            slots,
            @operator = BeforeSublotOperator(fixture),
            reason = "Take the cargo out by hand."
        });

    private static string HandedOff(RuntimeFixture fixture, string sessionId, string handoffId, int[] slots) =>
        Envelope(fixture, "FaultCargoRecoveryResult", new
        {
            exceptionRecoverySessionId = sessionId,
            recoveryActionId = ActionId,
            demandId = EndedDemandId,
            handoffId,
            overallOutcome = "HANDED_OFF",
            slotResults = slots.Select(slot => new
            {
                slotNo = slot,
                outcome = "COMPLETED",
                finalPhysicalState = "EMPTY",
                lockState = "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = Array.Empty<string>()
            }).ToArray(),
            @operator = BeforeSublotOperator(fixture),
            observedAt = Now
        });

    private static string MechanicallyIsolated(RuntimeFixture fixture, string sessionId, int[] slots) =>
        Envelope(fixture, "ForcedMechanicalRecoveryResult", new
        {
            exceptionRecoverySessionId = sessionId,
            recoveryActionId = ActionId,
            forcedRecoveryGeneration = 1,
            outcome = "MECHANICALLY_ISOLATED",
            slots,
            @operator = BeforeSublotOperator(fixture),
            observedAt = Now,
            electronicEmptyProven = false,
            vehicleReadyProven = false
        });

    private static string HardwareRecord(RuntimeFixture fixture, string sessionId, int[] slots) =>
        Envelope(fixture, "HardwareRecoveryRecordSubmitted", new
        {
            recordId = "e1370000-0000-4000-8000-000000000001",
            exceptionRecoverySessionId = sessionId,
            recoveryActionId = ActionId,
            @operator = BeforeSublotOperator(fixture),
            administratorRole = "MAINTENANCE_ADMINISTRATOR",
            slots,
            checksPerformed = HardwareChecks,
            actionsPerformed = HardwareActions,
            observations = HardwareObservations,
            observedAt = Now
        });

    private static OnboardMessageProcessor RecoveryProcessor(RuntimeFixture fixture, ControlServerDbContext connection) =>
        TestOnboardProcessorFactory.Create(
            connection,
            new WireToGateStore(connection),
            fixture.Clock,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Recovery:AuthenticationProofEnvironmentVariable"] = ProofVariable
                })
                .Build());

    private static OnboardConnectionState Connection(RuntimeFixture fixture) => new()
    {
        AgvId = fixture.Options.AgvId,
        SessionGeneration = 1,
        CapabilityRevision = 1,
        SafetyRevision = 7,
        Readiness = SessionReadiness.RecoveryRequired
    };

    private static string Envelope(RuntimeFixture fixture, string messageType, object payload) =>
        BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), messageType, generation: 1, payload);

    private static JsonElement FirstLinePayload(string wire)
    {
        string first = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries).First();
        using JsonDocument document = JsonDocument.Parse(first);
        return document.RootElement.GetProperty("payload").Clone();
    }

    private static string FirstLineType(string wire)
    {
        string first = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries).First();
        using JsonDocument document = JsonDocument.Parse(first);
        return document.RootElement.GetProperty("messageType").GetString()!;
    }
}
