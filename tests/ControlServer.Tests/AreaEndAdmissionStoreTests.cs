using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.TaskTypeStationTestData;

namespace ControlServer.Tests;

/// <summary>
/// I6 overturned (scope specification 21.2 item 2): the station task type admission is carried and frozen on
/// the leg whose station is the AREA machine. That is the load for WIRE_TO_GATE, as it always was, and the
/// unload for STAGING_TO_WIRE. The store decides which leg that is from the demand's task type under the rule
/// version the demand froze, not from what the caller claims.
/// </summary>
public sealed class AreaEndAdmissionStoreTests
{
    private const string ReverseDemand = "D-STAGING-TO-WIRE";

    private const string ForwardDemand = "D-WIRE-TO-GATE";

    // A WIRE_TO_GATE demand that froze the factory rules, so STAGING_TO_WIRE's rule can be read under its version.
    private const string FrozenForwardDemand = "D-WIRE-TO-GATE-FROZEN";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AStagingToWireUnloadCarriesAndFreezesTheAreaMachineAdmission()
    {
        await using TaskTypeStationPersistenceFixture fixture = await WithFrozenReverseDemandAsync();
        await AcceptAsync(fixture, ReverseDemand, TransportTaskTypes.StagingToWire);
        await SeedReverseJourneyAsync(fixture);
        WireToGateStore store = new(fixture.Context);
        StationOperationPlan unload = Plan(
            "ATTEMPT-UNLOAD", ReverseDemand, SlotOperationType.Unload, "N1-1", TransportTaskTypes.StagingToWire);

        await store.PrepareSlotOperationAsync(unload, "MESSAGE-UNLOAD", Wire("MESSAGE-UNLOAD", "ATTEMPT-UNLOAD"), Token);
        fixture.Context.ChangeTracker.Clear();
        await store.PrepareSlotOperationAsync(unload, "MESSAGE-UNLOAD", Wire("MESSAGE-UNLOAD", "ATTEMPT-UNLOAD"), Token);

        AdmissionDecisionSnapshotRow decision = await fixture.Context.AdmissionDecisionSnapshots.AsNoTracking()
            .SingleAsync(Token);
        Assert.Equal(
            ("ATTEMPT-UNLOAD", "N1-1", TransportTaskTypes.StagingToWire, 1L, true),
            (decision.SlotOperationAttemptId, decision.StationId, decision.TaskType,
                decision.AdmissionPolicyVersion, decision.Allowed));
    }

    /// <summary>
    /// The staging station is not where STAGING_TO_WIRE is admitted, so its load may not carry the identity;
    /// and WIRE_TO_GATE's unload at the gate may not either. Both are refused before anything is written.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnAdmissionIdentityOnTheLegAwayFromTheAreaMachineIsRefused()
    {
        await using TaskTypeStationPersistenceFixture fixture = await WithFrozenReverseDemandAsync();
        // Each demand accepted as its own task type, so what refuses below is the AREA-end check and not
        // control-server#198's task type check.
        await AcceptAsync(fixture, ReverseDemand, TransportTaskTypes.StagingToWire);
        await AcceptAsync(fixture, ForwardDemand, TransportTaskTypes.WireToGate);
        WireToGateStore store = new(fixture.Context);

        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() => store.PrepareSlotOperationAsync(
            Plan("ATTEMPT-LOAD", ReverseDemand, SlotOperationType.Load, "N1-1", TransportTaskTypes.StagingToWire),
            "MESSAGE-LOAD",
            Wire("MESSAGE-LOAD", "ATTEMPT-LOAD"),
            Token));
        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() => store.PrepareSlotOperationAsync(
            Plan("ATTEMPT-GATE", ForwardDemand, SlotOperationType.Unload, "N1-1", TransportTaskTypes.WireToGate),
            "MESSAGE-GATE",
            Wire("MESSAGE-GATE", "ATTEMPT-GATE"),
            Token));

        fixture.Context.ChangeTracker.Clear();
        Assert.Empty(await fixture.Context.StationOperations.ToArrayAsync(Token));
        Assert.Empty(await fixture.Context.AdmissionDecisionSnapshots.ToArrayAsync(Token));
    }

    /// <summary>
    /// control-server#198 c-1: the admission identity names the demand's own task type. A WIRE_TO_GATE demand whose
    /// unload carries STAGING_TO_WIRE passes the AREA-end check -- that task type's rule puts its AREA end at the
    /// unload -- and the machine admits STAGING_TO_WIRE, so until now it was frozen as admitted. Refused before
    /// anything is written: no operation, no admission snapshot, no outbox row.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnAdmissionIdentityNamingAnotherTaskTypeThanTheDemandsIsRefused()
    {
        await using TaskTypeStationPersistenceFixture fixture = await WithFrozenReverseDemandAsync();
        await fixture.Freezes.FreezeAsync(FrozenForwardDemand, 1, 25, 1, Now, Token);
        await AcceptAsync(fixture, FrozenForwardDemand, TransportTaskTypes.WireToGate);
        WireToGateStore store = new(fixture.Context);

        Exception? refused = await Record.ExceptionAsync(() => store.PrepareSlotOperationAsync(
            Plan("ATTEMPT-MISMATCH", FrozenForwardDemand, SlotOperationType.Unload, "N1-1", TransportTaskTypes.StagingToWire),
            "MESSAGE-MISMATCH",
            Wire("MESSAGE-MISMATCH", "ATTEMPT-MISMATCH"),
            Token));

        fixture.Context.ChangeTracker.Clear();
        int operations = await fixture.Context.StationOperations.CountAsync(Token);
        int snapshots = await fixture.Context.AdmissionDecisionSnapshots.CountAsync(Token);
        int outbox = await fixture.Context.ProtocolOutbox.CountAsync(Token);
        Assert.True(
            refused is BusinessIdentityConflictException,
            $"exception: {refused?.GetType().Name ?? "none"}; operations: {operations}; admission snapshots: {snapshots}; outbox rows: {outbox}");
        Assert.Equal((0, 0, 0), (operations, snapshots, outbox));
    }

    /// <summary>
    /// control-server#198 c-1, the replay branch: an operation already prepared under an admission identity that is not
    /// the demand's task type -- written before this check existed -- is not replayed either. The replay returns the
    /// stored outbox row after refreshing its envelope, which would send the command again; it is refused instead, and
    /// the rows stay as they were.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AReplayOfAnOperationWhoseAdmissionIdentityIsNotTheDemandsTaskTypeIsRefused()
    {
        await using TaskTypeStationPersistenceFixture fixture = await WithFrozenReverseDemandAsync();
        await AcceptAsync(fixture, ReverseDemand, TransportTaskTypes.StagingToWire);
        await SeedReverseJourneyAsync(fixture);
        WireToGateStore store = new(fixture.Context);
        StationOperationPlan unload = Plan(
            "ATTEMPT-UNLOAD", ReverseDemand, SlotOperationType.Unload, "N1-1", TransportTaskTypes.StagingToWire);
        await store.PrepareSlotOperationAsync(unload, "MESSAGE-UNLOAD", Wire("MESSAGE-UNLOAD", "ATTEMPT-UNLOAD"), Token);
        // The stored operation now names another task type than its demand: the state a write before this check could leave.
        AcceptedDemandRow demand = await fixture.Context.AcceptedDemands.SingleAsync(row => row.DemandId == ReverseDemand, Token);
        demand.WorkType = TransportTaskTypes.WireToGate;
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        Exception? refused = await Record.ExceptionAsync(() => store.PrepareSlotOperationAsync(
            unload, "MESSAGE-UNLOAD", Wire("MESSAGE-UNLOAD", "ATTEMPT-UNLOAD"), Token));

        fixture.Context.ChangeTracker.Clear();
        Assert.True(refused is BusinessIdentityConflictException, $"exception: {refused?.GetType().Name ?? "none"}");
        Assert.Equal(
            (1, 1, 1),
            (await fixture.Context.StationOperations.CountAsync(Token),
                await fixture.Context.AdmissionDecisionSnapshots.CountAsync(Token),
                await fixture.Context.ProtocolOutbox.CountAsync(Token)));
    }

    /// <summary>
    /// control-server#251: the admission station is the demand's AREA-end station at the stop of this operation. N2-1 admits
    /// WIRE_TO_GATE too, so naming it on a demand whose pickup is N1-1 passes every earlier check -- the task type is the
    /// demand's, the load is the AREA-end operation, the station admits the task type -- and until now was frozen as the
    /// admission. Refused before anything is written: no operation, no admission snapshot, no outbox row.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnAdmissionStationThatIsNotTheDemandsAreaEndStationIsRefused()
    {
        await using TaskTypeStationPersistenceFixture fixture = await WithFrozenReverseDemandAsync();
        await AcceptAsync(fixture, ForwardDemand, TransportTaskTypes.WireToGate);
        await SeedStopAsync(fixture, "STOP-PICKUP", 1, JourneyStopRoles.Pickup, "N1-1");
        await SeedStopAsync(fixture, "STOP-GATE", 2, JourneyStopRoles.Unload, "GATE-1");
        await SeedMembershipAsync(fixture, ForwardDemand, "STOP-PICKUP", "STOP-GATE", "ATTEMPT-LOAD", "ATTEMPT-UNLOAD");
        WireToGateStore store = new(fixture.Context);

        Exception? refused = await Record.ExceptionAsync(() => store.PrepareSlotOperationAsync(
            Plan("ATTEMPT-LOAD", ForwardDemand, SlotOperationType.Load, "N2-1", TransportTaskTypes.WireToGate),
            "MESSAGE-LOAD",
            Wire("MESSAGE-LOAD", "ATTEMPT-LOAD"),
            Token));

        await AssertRefusedWithNothingWrittenAsync(fixture, refused);
    }

    /// <summary>
    /// control-server#251, no regression: what the runtime's two call sites send -- the load at a WIRE_TO_GATE pickup, the
    /// unload at a STAGING_TO_WIRE drop-off, each naming the station of the stop it is at -- is prepared and frozen as before.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task TheAreaEndStationOfEitherDirectionIsPreparedAndFrozen()
    {
        await using TaskTypeStationPersistenceFixture fixture = await WithFrozenReverseDemandAsync();
        await AcceptAsync(fixture, ForwardDemand, TransportTaskTypes.WireToGate);
        await AcceptAsync(fixture, ReverseDemand, TransportTaskTypes.StagingToWire);
        await SeedStopAsync(fixture, "STOP-FORWARD-PICKUP", 1, JourneyStopRoles.Pickup, "N1-1", "J-FORWARD");
        await SeedStopAsync(fixture, "STOP-FORWARD-GATE", 2, JourneyStopRoles.Unload, "GATE-1", "J-FORWARD");
        await SeedStopAsync(fixture, "STOP-REVERSE-STAGING", 1, JourneyStopRoles.Pickup, "STAGING-1", "J-REVERSE");
        await SeedStopAsync(fixture, "STOP-REVERSE-MACHINE", 2, JourneyStopRoles.Unload, "N2-1", "J-REVERSE");
        await SeedMembershipAsync(
            fixture, ForwardDemand, "STOP-FORWARD-PICKUP", "STOP-FORWARD-GATE", "ATTEMPT-F-LOAD", "ATTEMPT-F-UNLOAD", "J-FORWARD");
        await SeedMembershipAsync(
            fixture, ReverseDemand, "STOP-REVERSE-STAGING", "STOP-REVERSE-MACHINE", "ATTEMPT-R-LOAD", "ATTEMPT-R-UNLOAD", "J-REVERSE");
        WireToGateStore store = new(fixture.Context);

        await store.PrepareSlotOperationAsync(
            Plan("ATTEMPT-F-LOAD", ForwardDemand, SlotOperationType.Load, "N1-1", TransportTaskTypes.WireToGate),
            "MESSAGE-F-LOAD", Wire("MESSAGE-F-LOAD", "ATTEMPT-F-LOAD"), Token);
        await store.PrepareSlotOperationAsync(
            Plan("ATTEMPT-R-UNLOAD", ReverseDemand, SlotOperationType.Unload, "N2-1", TransportTaskTypes.StagingToWire),
            "MESSAGE-R-UNLOAD", Wire("MESSAGE-R-UNLOAD", "ATTEMPT-R-UNLOAD"), Token);

        fixture.Context.ChangeTracker.Clear();
        (string, string)[] frozen = await fixture.Context.AdmissionDecisionSnapshots.AsNoTracking()
            .OrderBy(row => row.SlotOperationAttemptId)
            .Select(row => ValueTuple.Create(row.SlotOperationAttemptId, row.StationId))
            .ToArrayAsync(Token);
        Assert.Equal([("ATTEMPT-F-LOAD", "N1-1"), ("ATTEMPT-R-UNLOAD", "N2-1")], frozen);
    }

    /// <summary>
    /// control-server#251, the replay branch: an operation already prepared under a station that is not the demand's AREA-end
    /// station -- written before this check existed -- is not replayed either. The replay branch compares the plan with the
    /// frozen snapshot only, which agree with each other here, so until now it refreshed the envelope and sent the command
    /// again. Refused instead, and the rows stay as they were.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AReplayOfAnOperationFrozenAtAnotherStationThanTheAreaEndIsRefused()
    {
        await using TaskTypeStationPersistenceFixture fixture = await WithFrozenReverseDemandAsync();
        await AcceptAsync(fixture, ForwardDemand, TransportTaskTypes.WireToGate);
        await SeedStopAsync(fixture, "STOP-PICKUP", 1, JourneyStopRoles.Pickup, "N1-1");
        await SeedStopAsync(fixture, "STOP-GATE", 2, JourneyStopRoles.Unload, "GATE-1");
        await SeedMembershipAsync(fixture, ForwardDemand, "STOP-PICKUP", "STOP-GATE", "ATTEMPT-LOAD", "ATTEMPT-UNLOAD");
        WireToGateStore store = new(fixture.Context);
        await store.PrepareSlotOperationAsync(
            Plan("ATTEMPT-LOAD", ForwardDemand, SlotOperationType.Load, "N1-1", TransportTaskTypes.WireToGate),
            "MESSAGE-LOAD", Wire("MESSAGE-LOAD", "ATTEMPT-LOAD"), Token);
        // The frozen station is now another one than the pickup's: the state a write before this check could leave.
        AdmissionDecisionSnapshotRow decision = await fixture.Context.AdmissionDecisionSnapshots.SingleAsync(Token);
        decision.StationId = "N2-1";
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        Exception? refused = await Record.ExceptionAsync(() => store.PrepareSlotOperationAsync(
            Plan("ATTEMPT-LOAD", ForwardDemand, SlotOperationType.Load, "N2-1", TransportTaskTypes.WireToGate),
            "MESSAGE-LOAD", Wire("MESSAGE-LOAD", "ATTEMPT-LOAD"), Token));

        fixture.Context.ChangeTracker.Clear();
        Assert.True(refused is BusinessIdentityConflictException, $"exception: {refused?.GetType().Name ?? "none"}");
        Assert.Equal(
            (1, 1, 1),
            (await fixture.Context.StationOperations.CountAsync(Token),
                await fixture.Context.AdmissionDecisionSnapshots.CountAsync(Token),
                await fixture.Context.ProtocolOutbox.CountAsync(Token)));
    }

    /// <summary>
    /// control-server#251, the case the ticket waited for batch 7-06 over: one journey, two pickup stops at two stations, one
    /// demand loaded at each. Each demand's load naming its own stop's station passes; demand B's naming stop A's station is
    /// refused. The journey row's anchor columns cannot tell these apart -- they hold one pickup station for the whole journey
    /// -- so the station has to come from the stop the demand's membership points at.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task InAMultiStopJourneyEachDemandIsAdmittedAtItsOwnStopsStationOnly()
    {
        const string DemandA = "D-MULTI-A";
        const string DemandB = "D-MULTI-B";
        await using TaskTypeStationPersistenceFixture fixture = await WithFrozenReverseDemandAsync();
        await AcceptAsync(fixture, DemandA, TransportTaskTypes.WireToGate);
        await AcceptAsync(fixture, DemandB, TransportTaskTypes.WireToGate);
        await SeedStopAsync(fixture, "STOP-PICKUP-A", 1, JourneyStopRoles.Pickup, "N1-1");
        await SeedStopAsync(fixture, "STOP-PICKUP-B", 2, JourneyStopRoles.Pickup, "N2-1");
        await SeedStopAsync(fixture, "STOP-GATE", 3, JourneyStopRoles.Unload, "GATE-1");
        await SeedMembershipAsync(fixture, DemandA, "STOP-PICKUP-A", "STOP-GATE", "ATTEMPT-A-LOAD", "ATTEMPT-A-UNLOAD");
        await SeedMembershipAsync(fixture, DemandB, "STOP-PICKUP-B", "STOP-GATE", "ATTEMPT-B-LOAD", "ATTEMPT-B-UNLOAD");
        WireToGateStore store = new(fixture.Context);

        Exception? crossed = await Record.ExceptionAsync(() => store.PrepareSlotOperationAsync(
            Plan("ATTEMPT-B-LOAD", DemandB, SlotOperationType.Load, "N1-1", TransportTaskTypes.WireToGate),
            "MESSAGE-B-LOAD", Wire("MESSAGE-B-LOAD", "ATTEMPT-B-LOAD"), Token));
        await AssertRefusedWithNothingWrittenAsync(fixture, crossed);

        await store.PrepareSlotOperationAsync(
            Plan("ATTEMPT-A-LOAD", DemandA, SlotOperationType.Load, "N1-1", TransportTaskTypes.WireToGate),
            "MESSAGE-A-LOAD", Wire("MESSAGE-A-LOAD", "ATTEMPT-A-LOAD"), Token);
        await store.PrepareSlotOperationAsync(
            Plan("ATTEMPT-B-LOAD", DemandB, SlotOperationType.Load, "N2-1", TransportTaskTypes.WireToGate),
            "MESSAGE-B-LOAD", Wire("MESSAGE-B-LOAD", "ATTEMPT-B-LOAD"), Token);
        fixture.Context.ChangeTracker.Clear();
        (string, string)[] frozen = await fixture.Context.AdmissionDecisionSnapshots.AsNoTracking()
            .OrderBy(row => row.SlotOperationAttemptId)
            .Select(row => ValueTuple.Create(row.SlotOperationAttemptId, row.StationId))
            .ToArrayAsync(Token);
        Assert.Equal([("ATTEMPT-A-LOAD", "N1-1"), ("ATTEMPT-B-LOAD", "N2-1")], frozen);
    }

    /// <summary>
    /// control-server#251 (ticket item 4, one derivation of "which station is the AREA end"): the runtime's admission
    /// question for a demand that is not the journey's anchor is asked at that demand's own stop. Demand A anchors the
    /// journey and loads at N1-1; demand B loads at N2-1. After dispatch the policy moves: WIRE_TO_GATE is admitted at N2-1
    /// only, so B's entry should go on; or at N1-1 only, so it should be held. Asked at the anchor's pickup, both come out
    /// the other way round.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-11")]
    [InlineData("N2-1", true)]
    [InlineData("N1-1", false)]
    public async Task AFurtherDemandIsAskedAboutAtItsOwnStopsStationNotTheAnchors(string admittingStation, bool expected)
    {
        const string DemandA = "D-MULTI-A";
        const string DemandB = "D-MULTI-B";
        await using TaskTypeStationPersistenceFixture fixture = await WithFrozenReverseDemandAsync();
        await AcceptAsync(fixture, DemandA, TransportTaskTypes.WireToGate);
        await AcceptAsync(fixture, DemandB, TransportTaskTypes.WireToGate);
        await SeedStopAsync(fixture, "STOP-PICKUP-A", 1, JourneyStopRoles.Pickup, "N1-1");
        await SeedStopAsync(fixture, "STOP-PICKUP-B", 2, JourneyStopRoles.Pickup, "N2-1");
        await SeedStopAsync(fixture, "STOP-GATE", 3, JourneyStopRoles.Unload, "GATE-1");
        await SeedMembershipAsync(fixture, DemandA, "STOP-PICKUP-A", "STOP-GATE", "ATTEMPT-A-LOAD", "ATTEMPT-A-UNLOAD");
        await SeedMembershipAsync(fixture, DemandB, "STOP-PICKUP-B", "STOP-GATE", "ATTEMPT-B-LOAD", "ATTEMPT-B-UNLOAD");
        WireToGateStore store = new(fixture.Context);
        // The policy after dispatch: WIRE_TO_GATE at one of the two pickups only.
        await store.ApplyAdmissionPolicyAsync(
            new AdmissionPolicyDefinition(
                2, "DEPLOY-1", [new StationTaskTypeAdmission(admittingStation, TransportTaskTypes.WireToGate)], Now),
            Token);
        fixture.Context.ChangeTracker.Clear();

        // RED-ONLY: the old signature takes the journey row, whose anchor is A; it is the only way it can be asked about B.
        bool admitted = await store.IsTaskTypeAllowedAtAreaEndAsync(
            new JourneyRuntimeRow
            {
                JourneyId = "J-1", DemandId = DemandA, AgvId = "AGV-1", VehicleKey = "VEHICLE-1", MapIdentity = "MAP-25",
                DispatchZone = "ZONE-1", RouteEvidenceId = "ROUTE-1", PickupStationId = "N1-1", GateStationId = "GATE-1",
                TargetSlotsJson = "[1]", OperationSessionId = "SESSION-J-1", PickupMovementLegId = "L1", PickupUpperId = "U1",
                GateMovementLegId = "L2", GateUpperId = "U2", VehicleBusinessMessageId = "M1", WorklistMessageId = "M2",
                PlanMessageId = "M3", SublotRequestMessageId = "M4", LoadCommandMessageId = "M5",
                LoadSlotOperationAttemptId = "ATTEMPT-A-LOAD", PreDepartureSafetyCheckMessageId = "M6",
                PreDepartureSafetyCheckId = "S1", GateVehicleBusinessMessageId = "M7", GateWorklistMessageId = "M8",
                GatePlanMessageId = "M9", UnloadCommandMessageId = "M10", UnloadSlotOperationAttemptId = "ATTEMPT-A-UNLOAD"
            },
            TransportTaskTypes.WireToGate,
            Token);

        Assert.Equal(expected, admitted);
    }

    /// <summary>The refusal the station check makes: the conflict exception, and no operation, admission snapshot or outbox row.</summary>
    private static async Task AssertRefusedWithNothingWrittenAsync(TaskTypeStationPersistenceFixture fixture, Exception? refused)
    {
        fixture.Context.ChangeTracker.Clear();
        int operations = await fixture.Context.StationOperations.CountAsync(Token);
        int snapshots = await fixture.Context.AdmissionDecisionSnapshots.CountAsync(Token);
        int outbox = await fixture.Context.ProtocolOutbox.CountAsync(Token);
        Assert.True(
            refused is BusinessIdentityConflictException,
            $"exception: {refused?.GetType().Name ?? "none"}; operations: {operations}; admission snapshots: {snapshots}; outbox rows: {outbox}");
        Assert.Equal((0, 0, 0), (operations, snapshots, outbox));
    }

    /// <summary>The reverse demand's journey: loaded at a staging station, unloaded at the AREA machine N1-1.</summary>
    private static async Task SeedReverseJourneyAsync(TaskTypeStationPersistenceFixture fixture)
    {
        await SeedStopAsync(fixture, "STOP-STAGING", 1, JourneyStopRoles.Pickup, "STAGING-1");
        await SeedStopAsync(fixture, "STOP-MACHINE", 2, JourneyStopRoles.Unload, "N1-1");
        await SeedMembershipAsync(fixture, ReverseDemand, "STOP-STAGING", "STOP-MACHINE", "ATTEMPT-LOAD", "ATTEMPT-UNLOAD");
    }

    private static async Task SeedStopAsync(
        TaskTypeStationPersistenceFixture fixture,
        string stopId,
        int sequence,
        string role,
        string stationId,
        string journeyId = "J-1")
    {
        fixture.Context.Set<JourneyStopRow>().Add(JourneyMembershipSeed.Stop(journeyId, stopId, sequence, role, stationId));
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static async Task SeedMembershipAsync(
        TaskTypeStationPersistenceFixture fixture,
        string demandId,
        string pickupStopId,
        string unloadStopId,
        string loadAttemptId,
        string unloadAttemptId,
        string journeyId = "J-1")
    {
        fixture.Context.Set<JourneyDemandRow>().Add(JourneyMembershipSeed.Membership(
            journeyId, demandId, pickupStopId, unloadStopId, loadAttemptId, unloadAttemptId));
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>
    /// The factory rules, map 25 bound for WIRE_TO_GATE and STAGING_TO_WIRE, the reverse demand frozen against
    /// them, and an admission policy that admits both task types at the AREA machine stations N1-1 and N2-1. The forward
    /// demand has no freeze, like every demand accepted before control-server#160 wrote one.
    /// </summary>
    private static async Task<TaskTypeStationPersistenceFixture> WithFrozenReverseDemandAsync()
    {
        TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await fixture.Rules.WriteVersionAsync(SixRules, Source, Now, Token);
        await fixture.Bindings.WriteVersionAsync(
            25, 1, [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire], [GateBinding, StagingBinding],
            null, Source, Now, Token);
        await fixture.Freezes.FreezeAsync(ReverseDemand, 1, 25, 1, Now, Token);
        await new WireToGateStore(fixture.Context).ApplyAdmissionPolicyAsync(
            new AdmissionPolicyDefinition(
                1,
                "DEPLOY-1",
                [
                    new StationTaskTypeAdmission("N1-1", TransportTaskTypes.WireToGate),
                    new StationTaskTypeAdmission("N1-1", TransportTaskTypes.StagingToWire),
                    // A second machine admitting both, so a wrong station is not already caught by the admission
                    // relation itself (control-server#251).
                    new StationTaskTypeAdmission("N2-1", TransportTaskTypes.WireToGate),
                    new StationTaskTypeAdmission("N2-1", TransportTaskTypes.StagingToWire),
                ],
                Now),
            Token);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    /// <summary>An accepted demand row of <paramref name="workType"/>, the one fact the admission identity is checked against.</summary>
    private static async Task AcceptAsync(TaskTypeStationPersistenceFixture fixture, string demandId, string workType)
    {
        fixture.Context.AcceptedDemands.Add(AcceptedDemand(demandId, workType));
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static StationOperationPlan Plan(
        string attemptId,
        string demandId,
        SlotOperationType operationType,
        string admissionStationId,
        string admissionTaskType) => new(
            attemptId,
            demandId,
            "SUBLOT-1",
            [1],
            operationType,
            0,
            new string('a', 64),
            Now,
            admissionStationId,
            admissionTaskType);

    private static string Wire(string messageId, string attemptId) => JsonSerializer.Serialize(new
    {
        messageType = "SlotOperationCommand",
        messageId,
        agvId = "AGV-1",
        sessionGeneration = 1,
        sentAt = Now,
        payload = new { slotOperationAttemptId = attemptId }
    });
}
