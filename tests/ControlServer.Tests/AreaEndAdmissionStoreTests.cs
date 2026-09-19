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
    /// The factory rules, map 25 bound for WIRE_TO_GATE and STAGING_TO_WIRE, the reverse demand frozen against
    /// them, and an admission policy that admits both task types at the AREA machine station N1-1. The forward
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
                ],
                Now),
            Token);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    /// <summary>An accepted demand row of <paramref name="workType"/>, the one fact the admission identity is checked against.</summary>
    private static async Task AcceptAsync(TaskTypeStationPersistenceFixture fixture, string demandId, string workType)
    {
        fixture.Context.AcceptedDemands.Add(new AcceptedDemandRow
        {
            DemandId = demandId,
            SeriesId = "SERIES-" + demandId,
            TransportDemandKey = $"SUBLOT-1|{workType}|{demandId}",
            WorkType = workType,
            Sublot = "SUBLOT-1",
            Generation = 1,
            DemandRevision = 1,
            HistoryEpoch = "11111111-1111-4111-8111-111111111111",
            CatalogRevision = 1,
            CreatedAt = Now,
            ValueObservedAt = Now,
            ValuePollTraceId = "TRACE-" + demandId,
            ValueProjectionCommitId = "COMMIT-" + demandId,
            LiveMesFieldsJson = "{}",
            AcceptedAt = Now,
            Status = DemandExecutionStatus.Accepted,
        });
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
