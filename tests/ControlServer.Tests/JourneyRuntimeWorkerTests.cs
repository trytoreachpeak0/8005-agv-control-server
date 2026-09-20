using System.Text.Json.Nodes;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Adapters;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

public sealed class JourneyRuntimeWorkerTests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task ProductionRuntimeResumesOneJourneyThroughTrustedArrivalsAndAtomicCompletion()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot demand = fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            createdAt: Now.AddMinutes(-10));
        fixture.Catalog.Set(demand);
        fixture.BoxCounts.Set("SUBLOT-001", 7);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, runtime.Stage);
        Assert.Equal(2, runtime.ExpectedBasketCount);
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_PICKUP"));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));

        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId + 1 };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        Assert.DoesNotContain("SublotEntryRequested", await fixture.OutboxTypesAsync());

        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        // Two plans: the one sent once the pickup order was confirmed and the one sent at the
        // pickup. Their order is ThePlanGoesOutBeforeArrivalAndAgainAfterTheWorklistAtThePickup's.
        Assert.Equal(
            ["CurrentStopWorklistSnapshot", "SublotEntryRequested", "UpcomingStopPlanSnapshot", "UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"],
            (await fixture.OutboxTypesAsync()).Order(StringComparer.Ordinal));

        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "SublotSubmitted",
            new
            {
                operationSessionId = runtime.OperationSessionId,
                stationId = runtime.PickupStationId,
                worklistRevision = runtime.WorklistRevision,
                sublot = "SUBLOT-001",
                entryMethod = "SCANNER",
                @operator = new
                {
                    operatorId = "OP-001",
                    verificationMethod = "BADGE",
                    verifiedAt = Now
                }
            });
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, runtime.Stage);
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        int[]? loadSlots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson);
        Assert.NotNull(loadSlots);
        Assert.Equal([1, 2], loadSlots);
        AdmissionDecisionSnapshotRow admission = await fixture.Context.AdmissionDecisionSnapshots
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(admission.Allowed);
        Assert.Equal(1, admission.AdmissionPolicyVersion);
        Assert.Equal(runtime.PickupStationId, admission.StationId);
        Assert.Equal("WIRE_TO_GATE", admission.TaskType);

        await fixture.ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = runtime.PreDepartureSafetyCheckId,
                outcome = "SAFE",
                observedAt = Now,
                safetyStateVersion = 7,
                validUntil = Now.AddMinutes(1),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                }
            }, runtime.PreDepartureSafetyCheckMessageId);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, runtime.Stage);
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_GATE"));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));

        fixture.Riot.SetSuccessfulArrival("TO_GATE", TaskTypeStationRuntimeSeed.GateStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = TaskTypeStationRuntimeSeed.GateStationRiotId };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, runtime.Stage);

        StationOperationRow unload = await fixture.OperationAsync(SlotOperationType.Unload);
        await fixture.ApplySafeResultAsync(unload, SlotOperationType.Unload, SlotBusinessState.Empty);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        Assert.Equal(DemandExecutionStatus.Succeeded, (await fixture.DemandRowAsync()).Status);
        Assert.NotNull((await fixture.LeaseAsync()).ReleasedAt);
        Assert.Equal(1, await fixture.Context.TransportDemandCompletions.CountAsync(TestContext.Current.CancellationToken));

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(2, await fixture.Context.StationOperations.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.Context.TransportDemandCompletions.CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// REQ-0237 keeps ordinary mis-placement correction to the time before the vehicle leaves the
    /// pickup, and ADR-cross-0054/0055 make that time a server-owned wait after the load commits.
    /// Until 2026-09-13 this server asked for departure safety in the very iteration the load
    /// committed and created the gate order a moment later, so the window did not exist: the G3
    /// FP-IS-02 run pressed 「修正装货」 two seconds after the doors closed and the vehicle had
    /// already been sent away (docs/defects/20260913-no-pre-departure-correction-window.md).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task AfterTheLoadCommitsTheVehicleWaitsOutTheStationDepartureWindowBeforeDeparting()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.AdvanceToLoadResultAsync();
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        await fixture.ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, runtime.Stage);
        Assert.Equal(fixture.Clock.GetUtcNow(), runtime.StationDepartureWaitStartedAt);
        Assert.DoesNotContain("PreDepartureSafetyCheck", await fixture.OutboxTypesAsync());

        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await fixture.RuntimeAsync()).Stage);
        Assert.DoesNotContain("PreDepartureSafetyCheck", await fixture.OutboxTypesAsync());
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        Assert.Null(runtime.StationDepartureWaitStartedAt);
        Assert.Contains("PreDepartureSafetyCheck", await fixture.OutboxTypesAsync());
    }

    /// <summary>
    /// 8005-agv-control-server#28, fixed on the MVP line as #40 and carried to v2 here. One DbContext
    /// serves a whole TCP connection while the runtime worker writes the same journeys and operations
    /// from a context of its own, so every row an earlier message on that connection read was handed
    /// back to every later message as it stood then. The window that matters most is the narrowest one
    /// the server has: REQ-0237 lets an ordinary mis-placement be corrected only while the vehicle is
    /// still waiting at the pickup with its load committed. A connection that had asked once while the
    /// load was still running held the journey at AwaitingLoadResult and the operation at Prepared, so
    /// when the operator pressed 「修正装货」 inside the real window the server refused it against a
    /// picture minutes out of date -- and the vehicle drove to the gate with the wrong slots filled,
    /// with nothing left that could put it right.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task TheNextMessageOnAConnectionJudgesACorrectionOnTheJourneyAsStored()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        JourneyRuntimeRow runtime = await fixture.AdvanceToLoadResultAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, runtime.Stage);
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        int[] slots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!;

        // The operator presses it once while the load is still running. Refused, correctly -- and the
        // connection has now read both the operation and the journey.
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        string early = await fixture.RequestLoadCorrectionOnConnectionAsync(
            connection,
            "70000000-0000-4000-8000-000000000011",
            runtime.DemandId,
            load.SlotOperationAttemptId,
            slots);
        Assert.Equal("LoadCorrectionRejected", CorrectionOutcome(early));

        // The load commits and the runtime, from its own context, puts the vehicle into the departure
        // wait -- the one window in which a correction is allowed.
        await fixture.ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await fixture.RuntimeAsync()).Stage);

        string inWindow = await fixture.RequestLoadCorrectionOnConnectionAsync(
            connection,
            "70000000-0000-4000-8000-000000000012",
            runtime.DemandId,
            load.SlotOperationAttemptId,
            slots);

        Assert.Equal("AUTHORIZED", CorrectionOutcome(inWindow));
        Assert.Contains("LoadCorrectionCommand", await fixture.OutboxTypesAsync());
        fixture.Context.ChangeTracker.Clear();
        Assert.Single(await fixture.Context.RecoveryWorkflows
            .Where(row => row.WorkflowType == "LOAD_CORRECTION")
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// An authorized correction is answered by the LoadCorrectionCommand that goes out, not by a line
    /// back on this connection, so an empty answer is the authorization. A refusal names itself.
    /// </summary>
    private static string CorrectionOutcome(string response)
    {
        if (string.IsNullOrEmpty(response))
        {
            return "AUTHORIZED";
        }
        using JsonDocument document = JsonDocument.Parse(
            response.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        return document.RootElement.GetProperty("messageType").GetString()!;
    }

    /// <summary>
    /// ADR-cross-0054: a correction takes the vehicle out of the departure wait and stops the clock,
    /// and once every slot it touched is safely closed again the wait starts over from its full
    /// length. An open correction past the original deadline must therefore neither depart the
    /// vehicle nor leave it only the remainder of the old wait.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task AnOpenLoadCorrectionHoldsTheVehicleAndTheWaitStartsOverWhenItCloses()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.AdvanceToLoadResultAsync();
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        await fixture.ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, runtime.Stage);

        RecoveryWorkflowRow correction = new()
        {
            WorkflowId = "70000000-0000-4000-8000-000000000001",
            WorkflowType = "LOAD_CORRECTION",
            AgvId = runtime.AgvId,
            DemandId = runtime.DemandId,
            SlotOperationAttemptId = load.SlotOperationAttemptId,
            SlotsJson = load.TargetSlotsJson,
            State = RecoveryWorkflowState.AwaitingResult,
            RequestMessageId = "70000000-0000-4000-8000-000000000002",
            RequestContentHash = new string('c', 64),
            CreatedAt = fixture.Clock.GetUtcNow(),
            UpdatedAt = fixture.Clock.GetUtcNow()
        };
        fixture.Context.RecoveryWorkflows.Add(correction);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, runtime.Stage);
        Assert.Equal("LOAD_CORRECTION_IN_PROGRESS", runtime.BlockReasonCode);
        Assert.DoesNotContain("PreDepartureSafetyCheck", await fixture.OutboxTypesAsync());

        correction.State = RecoveryWorkflowState.Reconciled;
        correction.UpdatedAt = fixture.Clock.GetUtcNow();
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, runtime.Stage);
        Assert.Null(runtime.BlockReasonCode);
        Assert.DoesNotContain("PreDepartureSafetyCheck", await fixture.OutboxTypesAsync());

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await fixture.RuntimeAsync()).Stage);
        Assert.Contains("PreDepartureSafetyCheck", await fixture.OutboxTypesAsync());
    }

    /// <summary>
    /// ADR-cross-0055 starts the station departure wait when the vehicle arrives at the pickup, not
    /// when a load commits. Until batch 5 the v2 server had no deadline at all in AwaitingSublot: an
    /// unscanned stop was asked again every poll and held the pickup until someone edited the
    /// database. The start is seeded before the worklist goes out, because the worklist is where the
    /// vehicle will be told the deadline (control-server#84) -- a first snapshot sent ahead of the
    /// seed would carry none.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task TheStationDepartureWaitStartsAtThePickupArrivalBeforeTheWorklistGoesOut()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromMinutes(5);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        // 这三十秒里车一直在说话（ADR-cross-0027 的两秒一次）。只推时钟不补心跳，在 control-server#234
        // 之后等于「车失联了」，那一轮就不再推进到站。
        await fixture.HearFromPeerAsync();
        DateTimeOffset arrivedAt = fixture.Clock.GetUtcNow();
        bool worklistSent = false;
        DateTimeOffset? startedWhenTheWorklistWasSent = null;
        fixture.Peer.OnMessageSent = async line =>
        {
            using JsonDocument sent = JsonDocument.Parse(line);
            if (sent.RootElement.GetProperty("messageType").GetString() != "CurrentStopWorklistSnapshot")
            {
                return;
            }
            worklistSent = true;
            startedWhenTheWorklistWasSent = (await fixture.RuntimeAsync()).StationDepartureWaitStartedAt;
        };

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(worklistSent);
        Assert.Equal(arrivedAt, startedWhenTheWorklistWasSent);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.Equal(arrivedAt, runtime.StationDepartureWaitStartedAt);
    }

    /// <summary>
    /// ADR-cross-0058 decision 7, redone for the v2 one-demand journey: nobody scanned before the
    /// deadline, so the server ends the stop itself. The demand is terminated as
    /// CANCELLED_BY_STATION_TIMEOUT, the vehicle is released, and the entry request nobody answered is
    /// settled -- left open it would be replayed into every later session, where the peer refuses it
    /// as a business id whose content changed. It may end the stop alone precisely because no slot
    /// operation was ever commanded: there is no physical state only the peer could settle. All of it
    /// commits together, so a crash cannot leave a cancelled demand still holding its vehicle.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AnUnscannedPickupEndsAtTheStationDeadlineInOneSaveAndReleasesTheVehicle()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        JourneyRuntimeRow runtime = await fixture.AdvanceToSublotWaitAsync();
        await fixture.ProveSlotDoorsClosedAsync();

        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        DateTimeOffset deadline = fixture.Clock.GetUtcNow();
        fixture.SaveChanges.Reset();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", runtime.BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.DemandRowAsync()).Status);
        Assert.Equal(deadline, (await fixture.LeaseAsync()).ReleasedAt);
        OrderIntentRow pickup = await fixture.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.Purpose == "TO_PICKUP", TestContext.Current.CancellationToken);
        Assert.Equal(deadline, pickup.VehicleOccupancyReleasedAt);
        ProtocolOutboxRow entryRequest = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageId == runtime.SublotRequestMessageId, TestContext.Current.CancellationToken);
        Assert.Equal(deadline, entryRequest.AcknowledgedAt);
        Assert.Equal(0, await fixture.Context.StationOperations.CountAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("SlotOperationCommand", await fixture.OutboxTypesAsync());

        string[] settlement = Assert.Single(
            fixture.SaveChanges.Saves, save => save.Contains("JourneyRuntimeRow.Stage"));
        Assert.Contains("AcceptedDemandRow.Status", settlement);
        Assert.Contains("VehicleDispatchLeaseRow.ReleasedAt", settlement);
        Assert.Contains("OrderIntentRow.VehicleOccupancyReleasedAt", settlement);
        Assert.Contains("ProtocolOutboxRow.AcknowledgedAt", settlement);
    }

    /// <summary>
    /// ADR-cross-0058 decision 4: a stop whose deadline passes while a slot door is not shut does not
    /// end, because ending it means the vehicle may then be sent away, and ADR-cross-0011/0012 forbid
    /// moving with a door open. Evidence the server cannot read counts as not shut. In AwaitingSublot the
    /// session normally leaves Ready first (ADR-cross-0058 Verification), which this does not change;
    /// it only guarantees the timeout itself never closes the stop against an open or unknown door.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    // A null reasonCode is the empty set: InlineData cannot carry an array, and the two cases that
    // turn on unknownPresent report no reason code at all.
    [InlineData(null, null)]
    [InlineData(true, null)]
    [InlineData(false, "LOCK_NOT_CLOSED")]
    public async Task TheStationDeadlineDoesNotEndTheStopWhileADoorIsOpenOrUnknown(
        bool? unknownPresent,
        string? reasonCode)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.AdvanceToSublotWaitAsync();
        await fixture.SetSafetyEvidenceAsync(unknownPresent, reasonCode is null ? [] : [reasonCode]);

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        Assert.Null((await fixture.LeaseAsync()).ReleasedAt);

        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", runtime.BlockReasonCode);
    }

    /// <summary>
    /// ADR-cross-0055 makes the timeout and the start of the load exclusive, the first to be persisted
    /// winning. An entry already durable when the deadline's iteration reads the inbox was persisted
    /// first: the load starts, and once a load is commanded the pickup timeout never looks at the stop
    /// again, however late it runs.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AnEntryDurableBeforeTheTimeoutStartsTheLoadAndTheDeadlineNoLongerEndsTheStop()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.AdvanceToSublotWaitAsync();
        await fixture.ProveSlotDoorsClosedAsync();

        fixture.Clock.Advance(TimeSpan.FromSeconds(15));
        await fixture.SubmitSublotAsync("SUBLOT-001");
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, runtime.Stage);
        Assert.Contains("SlotOperationCommand", await fixture.OutboxTypesAsync());
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        Assert.Null((await fixture.LeaseAsync()).ReleasedAt);
    }

    /// <summary>
    /// The other half of the same exclusion: once the timeout has committed, an entry that arrives
    /// afterwards was persisted second and starts nothing -- no slot operation, no load command, and
    /// the demand stays cancelled.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AnEntryPersistedAfterTheStationTimeoutCommittedStartsNoLoad()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.AdvanceToSublotWaitAsync();
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync()).Stage);

        await fixture.SubmitSublotAsync("SUBLOT-001");
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", runtime.BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.DemandRowAsync()).Status);
        Assert.Equal(0, await fixture.Context.StationOperations.CountAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("SlotOperationCommand", await fixture.OutboxTypesAsync());
    }

    /// <summary>
    /// What ending the stop is for: the vehicle is free again. The released lease and occupancy let
    /// the next round give it the next demand, while the cancelled one is not dispatched a second time
    /// because its DemandId is already accepted. Suppressing its business key, so a new DemandId for
    /// the same sublot stays out too, is batch 7 (REQ-0155/0156/0211).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AfterAStationTimeoutTheVehicleTakesTheNextDemandAndTheCancelledOneIsNotDispatchedAgain()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        AcceptedDemandSnapshot timedOut = fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10));
        fixture.Catalog.Set(timedOut);
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.AdvanceToSublotWaitAsync();
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync()).Stage);
        await VehicleOccupancyAssertions.AssertActiveLeasesAndPurposeClaimsMatchAsync(fixture.Context);
        await ZeroChangePin.AssertMatchesAsync(fixture.Context, "station-deadline");

        AcceptedDemandSnapshot next = fixture.Demand(
            "10000000-0000-4000-8000-000000000002", "SUBLOT-002", createdAt: Now.AddMinutes(-5));
        fixture.Catalog.Set([timedOut, next]);
        fixture.BoxCounts.Set("SUBLOT-002", 7);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync(next.DemandId)).Stage);
        await VehicleOccupancyAssertions.AssertActiveLeasesAndPurposeClaimsMatchAsync(fixture.Context);
        Assert.Equal(2, await fixture.Context.JourneyRuntimes.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    /// <summary>
    /// FR-031 AC-9 (TC-108): while the vehicle is off air its stop is neither ended nor departed. A
    /// dropped link does not move the session off Ready by itself -- nothing calls
    /// RecordConnectionLossAsync -- so without this the deadline would run out against whatever the
    /// vehicle last said before it vanished, and the demand would be cancelled while nobody could have
    /// scanned it. Liveness is measured the way the dispatch facts measure it: the last inbound message
    /// of this session generation, within MaximumEvidenceAge.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task TheStationDeadlineDoesNotEndTheStopWhileTheVehicleIsOffAir()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Options.MaximumEvidenceAge = TimeSpan.FromSeconds(5);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.AdvanceToSublotWaitAsync();
        await fixture.ProveSlotDoorsClosedAsync();

        // Off air: nothing more arrives from the peer, and the clock runs past both the deadline and
        // the age at which this server stops believing what the peer last said.
        fixture.Clock.Advance(TimeSpan.FromSeconds(11));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        Assert.Null((await fixture.LeaseAsync()).ReleasedAt);

        // Heard from again on the same generation: nothing reconnected, so the deadline that has
        // already passed still stands and this iteration ends the stop. A hold, not an exemption.
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow ended = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, ended.Stage);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", ended.BlockReasonCode);
    }

    /// <summary>
    /// A session leaves Ready without the vehicle going anywhere too: a forced recovery reconciliation
    /// (<c>FORCED_RECOVERY_RECONCILIATION_REQUIRED</c>) or a safety report that makes departure unsafe.
    /// The runtime advances no stop while that gate is closed, so nobody can be asked to scan and no
    /// entry would be acted on -- a clock left running there would end the stop on the first iteration
    /// after readiness returns. Voided while the gate is closed and refilled behind it, the same way a
    /// disconnect is.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task ASessionThatLeavesReadyVoidsTheSublotWaitUntilReadinessReturns()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.AdvanceToSublotWaitAsync();
        await fixture.ProveSlotDoorsClosedAsync();

        await fixture.DropOnboardSessionAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow held = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, held.Stage);
        Assert.Null(held.StationDepartureWaitStartedAt);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);

        await fixture.RestoreSessionReadyAsync();
        DateTimeOffset readyAt = fixture.Clock.GetUtcNow();
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow refilled = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, refilled.Stage);
        Assert.Equal(readyAt, refilled.StationDepartureWaitStartedAt);

        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>
    /// ADR-cross-0055: "倒计时期间断联使本轮截止时间失效，恢复握手和投影对账完成后重新计满". A wall clock that
    /// kept running while the vehicle was gone would end the stop on the first iteration after it came
    /// back, and the operator would find the demand cancelled without ever having had the chance to scan
    /// it. The disconnect is the real reconnect path -- BeginSessionRecoveryAsync is what the peer's
    /// SessionHello reaches -- and the refill happens behind the readiness gate, which is where the
    /// handshake and the projection reconciliation are already done. It is a reset, not an exemption.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task ADisconnectVoidsTheSublotWaitAndItRefillsInFullOnceTheSessionIsReadyAgain()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.AdvanceToSublotWaitAsync();
        await fixture.ProveSlotDoorsClosedAsync();

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.ReconnectAsync(2);
        Assert.Null((await fixture.RuntimeAsync()).StationDepartureWaitStartedAt);

        // Offline far past the original deadline. No iteration runs in between on purpose: one behind
        // the closed readiness gate would leave ONBOARD_SESSION_NOT_READY on the row, and the save that
        // reason triggers would hide whether the refill below is saved for its own sake.
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.AdvanceSessionAsync(2);
        await fixture.ProveSlotDoorsClosedAsync();
        DateTimeOffset readyAt = fixture.Clock.GetUtcNow();
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.Equal(readyAt, runtime.StationDepartureWaitStartedAt);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);

        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", runtime.BlockReasonCode);
    }

    /// <summary>
    /// The same ADR-cross-0055 rule for the wait after the load commits. That wait already restarted
    /// from the server's clock when it was entered, but a disconnect did not void it
    /// (program#61 Q2 on FR-031 AC-9): a vehicle offline past the window came back and was asked for
    /// departure safety at once, with the correction window it was owed gone.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task ADisconnectVoidsTheDepartureWaitAfterTheLoadAndItRefillsInFullOnceTheSessionIsReadyAgain()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.AdvanceToLoadResultAsync();
        await fixture.ApplySafeResultAsync(
            await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await fixture.RuntimeAsync()).Stage);

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.ReconnectAsync(2);
        Assert.Null((await fixture.RuntimeAsync()).StationDepartureWaitStartedAt);

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await fixture.RuntimeAsync()).Stage);

        await fixture.AdvanceSessionAsync(2);
        DateTimeOffset readyAt = fixture.Clock.GetUtcNow();
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, runtime.Stage);
        Assert.Equal(readyAt, runtime.StationDepartureWaitStartedAt);
        Assert.DoesNotContain("PreDepartureSafetyCheck", await fixture.OutboxTypesAsync());

        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("PreDepartureSafetyCheck", await fixture.OutboxTypesAsync());

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>
    /// The one place the station departure deadline is computed: the runtime judges the timeout from
    /// it, and control-server#84 sends it to the vehicle as <c>stationDepartureDeadlineAt</c>. A
    /// disabled timeout and a wait that has not started are both "no deadline" -- a countdown the
    /// runtime would never act on is one that expires into silence.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task TheStationDepartureDeadlineIsTheWaitStartPlusTheTimeoutAndAbsentWithoutAWait()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        JourneyRuntimeRow runtime = await fixture.AdvanceToSublotWaitAsync();

        runtime.StationDepartureWaitStartedAt = new DateTimeOffset(2026, 8, 26, 1, 0, 30, TimeSpan.Zero);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 26, 1, 5, 30, TimeSpan.Zero),
            JourneyRuntimeEngine.StationDepartureDeadline(runtime, TimeSpan.FromMinutes(5)));
        Assert.Null(JourneyRuntimeEngine.StationDepartureDeadline(runtime, TimeSpan.Zero));

        runtime.StationDepartureWaitStartedAt = null;
        Assert.Null(JourneyRuntimeEngine.StationDepartureDeadline(runtime, TimeSpan.FromMinutes(5)));
    }

    /// <summary>
    /// ADR-cross-0055 restarts the wait from its full length once a LoadBatch closes (6e8dea5a). With
    /// the wait now starting at the arrival, the commit has to replace that start rather than inherit
    /// it: an operator who scans eight seconds into a ten-second wait still gets the whole correction
    /// window after the load.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task TheLoadCommitRestartsTheStationDepartureWaitFromItsFullLength()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        JourneyRuntimeRow runtime = await fixture.AdvanceToSublotWaitAsync();
        DateTimeOffset arrivedAt = fixture.Clock.GetUtcNow();
        Assert.Equal(arrivedAt, runtime.StationDepartureWaitStartedAt);

        fixture.Clock.Advance(TimeSpan.FromSeconds(8));
        await fixture.SubmitSublotAsync("SUBLOT-001");
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.ApplySafeResultAsync(
            await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load, SlotBusinessState.Occupied);
        DateTimeOffset committedAt = fixture.Clock.GetUtcNow();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, runtime.Stage);
        Assert.Equal(committedAt, runtime.StationDepartureWaitStartedAt);

        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await fixture.RuntimeAsync()).Stage);
        Assert.DoesNotContain("PreDepartureSafetyCheck", await fixture.OutboxTypesAsync());

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await fixture.RuntimeAsync()).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    public async Task ACompletedDemandStillListedByMesIngestIsNeverOfferedForIntakeAgain()
    {
        // MesIngest publishes MES's own list of open transport demands; our journey completing does
        // not remove the demand from it. Discovery runs again as soon as no unresolved journey
        // remains, so the finished demand was scored as a fresh candidate, and intake met the
        // AcceptedDemands row it had written itself. The store refused that replay -- rightly: the
        // pickup intent discovery rebuilds stamps CreatedAt from the current clock, which no longer
        // matches the persisted intent. Every poll then failed closed.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot finished = fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10));
        fixture.Catalog.Set(finished);
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.RunToCompletionAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync()).Stage);

        // The field clock moves between polls and the fixture's does not. Without this the replayed
        // intent looks identical to the persisted one and the store lets the replay through, which
        // is what hid the defect from the suite.
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "DEMAND_ALREADY_ACCEPTED",
            (await fixture.BacklogAsync(finished.DemandId)).ReasonCode);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(1, await fixture.Context.JourneyRuntimes.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.Context.AcceptedDemands.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.Context.TransportDemandCompletions.CountAsync(TestContext.Current.CancellationToken));
        Assert.NotNull((await fixture.LeaseAsync()).ReleasedAt);
        Assert.Equal(2, fixture.Riot.TotalCreateCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    public async Task ACompletedDemandStillListedByMesIngestDoesNotBlockTheNextEligibleDemand()
    {
        // The operational cost of the replay refusal: it aborted the whole runtime iteration, so no
        // other demand could be accepted while the finished one remained in the catalog. Backlog
        // ordering is by FirstSeenAt, so the finished demand is always the one selected.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot finished = fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10));
        fixture.Catalog.Set(finished);
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.RunToCompletionAsync();
        Assert.NotNull((await fixture.LeaseAsync()).ReleasedAt);

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        AcceptedDemandSnapshot next = fixture.Demand(
            "10000000-0000-4000-8000-000000000002", "SUBLOT-002", Now.AddMinutes(-5));
        fixture.Catalog.Set(finished, next);
        fixture.BoxCounts.Set("SUBLOT-002", 4);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow accepted = await fixture.RuntimeAsync(next.DemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, accepted.Stage);
        Assert.Equal("ACCEPTED", (await fixture.BacklogAsync(next.DemandId)).ReasonCode);
        Assert.Equal(
            "DEMAND_ALREADY_ACCEPTED",
            (await fixture.BacklogAsync(finished.DemandId)).ReasonCode);

        // One pickup and one gate order for the finished journey, one pickup for the new one: the
        // finished demand is never dispatched a second time.
        Assert.Equal(3, fixture.Riot.TotalCreateCount);
        Assert.Equal(1, await fixture.Context.TransportDemandCompletions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            JourneyRuntimeStage.Completed,
            (await fixture.RuntimeAsync(finished.DemandId)).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-REPLACE-AND-ACK")]
    public async Task EachStopPublishesItsWorklistUnderItsOwnRevision()
    {
        // The peer keys a snapshot's identity on its type and revision, so two worklists published
        // at the same revision are a claim that their content is identical. The pickup and gate
        // worklists carry a different station, a different role and a different stop: publishing
        // both at revision 1 made the peer reject the second, correctly, and the connection died on
        // that rejection with the unload command still queued behind it.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.RunToGateUnloadAsync();

        (string StationId, long Revision)[] worklists = fixture.Peer.Lines
            .Select(line => JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(line)))
            .Where(document => document.RootElement.GetProperty("messageType").GetString()
                == "CurrentStopWorklistSnapshot")
            .Select(document => document.RootElement.GetProperty("payload"))
            .Select(payload => (
                payload.GetProperty("stationId").GetString()!,
                payload.GetProperty("worklistRevision").GetInt64()))
            .Distinct()
            .ToArray();

        Assert.Equal(2, worklists.Length);
        Assert.Equal(2, worklists.Select(item => item.StationId).Distinct().Count());
        Assert.Equal(2, worklists.Select(item => item.Revision).Distinct().Count());
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-SAME-REVISION-CONFLICT")]
    public async Task ASecondJourneyOnTheSameVehicleNeverRepublishesAnAdoptedRevision()
    {
        // The peer journals the adopted revision of each snapshot type in SQLite keyed on the type
        // alone -- no demand, no session -- and never deletes the row: on reconnect it clears its
        // in-memory copy and immediately restores it from that journal. A revision below the one it
        // holds is refused as SNAPSHOT_REVISION_REGRESSION, an equal one whose payload differs as
        // SNAPSHOT_REVISION_CONTENT_CONFLICT, and both refusals raise a protocol problem and tear
        // the session down. The runtime row is created per demand, so a second journey on the same
        // vehicle restarted every revision at 1 while the peer already held 2.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.RunToCompletionAsync();

        // The vehicle lease is released by an atomic completion, so the same vehicle taking a second
        // demand is the designed path, not a recovery case.
        Assert.NotNull((await fixture.LeaseAsync()).ReleasedAt);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000002", "SUBLOT-002", Now.AddMinutes(-5)));
        fixture.BoxCounts.Set("SUBLOT-002", 4);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow second = await fixture.RuntimeAsync("10000000-0000-4000-8000-000000000002");
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", second.PickupUpperId, second.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = second.PickupStationRiotId };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            JourneyRuntimeStage.AwaitingSublot,
            (await fixture.RuntimeAsync("10000000-0000-4000-8000-000000000002")).Stage);

        // One entry per snapshot the server allocated a revision to, in the order it first sent
        // them. The fixture peer never acknowledges, so every pending outbox row is re-sent on each
        // iteration; a real peer acknowledges what it has adopted and only the first send counts.
        (string Type, long Revision)[] published = fixture.Peer.Lines
            .Select(line => JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(line)).RootElement)
            .Select(root => (Type: root.GetProperty("messageType").GetString()!, Root: root))
            .Where(item => SnapshotRevisionProperty(item.Type) is not null)
            .DistinctBy(item => item.Root.GetProperty("messageId").GetString()!)
            .Select(item => (
                item.Type,
                item.Root.GetProperty("payload").GetProperty(SnapshotRevisionProperty(item.Type)!).GetInt64()))
            .ToArray();

        Assert.Equal(
            ["CurrentStopWorklistSnapshot", "UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"],
            published.Select(item => item.Type).Distinct().Order(StringComparer.Ordinal));
        foreach (IGrouping<string, long> stream in published.GroupBy(item => item.Type, item => item.Revision))
        {
            long[] revisions = stream.ToArray();
            // Three stops have been published: pickup and gate on the first journey, pickup on the
            // second. That is three distinct revisions of the worklist and the vehicle state. The plan
            // also went out before each pickup arrival, so it has five: before, at the pickup and at
            // the gate on the first journey, before and at the pickup on the second. The sequence may
            // never step back.
            Assert.Equal(revisions.Order(), revisions);
            Assert.Equal(stream.Key == "UpcomingStopPlanSnapshot" ? 5 : 3, revisions.Distinct().Count());
        }
    }
}
