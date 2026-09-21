using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

public sealed class WireToGateStoreTests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-SESSION-RECOVERY-HAPPY")]
    public async Task FiveStepRecoveryRequiresCurrentGenerationAndUniqueConsistentFacts()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        SessionIdentity identity = new(
            "AGV-001", 1, ProtocolCandidateIdentity.RepositoryCommit,
            ProtocolCandidateIdentity.ManifestSha256, ProtocolCandidateIdentity.ProfileId,
            ProtocolCandidateIdentity.ProtocolVersion);

        await fixture.Store.BeginSessionRecoveryAsync(identity, fixture.CancellationToken);
        await fixture.Store.ApplyCapabilitySnapshotAsync("AGV-001", 1, 4, "cap-hash", null, fixture.CancellationToken);
        await fixture.Store.ApplySafetySnapshotAsync("AGV-001", 1, 9, true, "safe-hash", fixture.CancellationToken);
        await fixture.Store.ApplyRecoveryReportAsync(
            "AGV-001", 1, "REPORT-001", 0, null, null, [], [], [], fixture.CancellationToken);
        SessionReadinessDecision decision = await fixture.Store.DecideReadinessAsync(
            "AGV-001", 1, fixture.CancellationToken);

        Assert.Equal(SessionReadiness.Ready, decision.Readiness);
        await Assert.ThrowsAsync<StaleSessionGenerationException>(() =>
            fixture.Store.ApplyCapabilitySnapshotAsync("AGV-001", 0, 5, "late", null, fixture.CancellationToken));

        await fixture.Store.BeginSessionRecoveryAsync(identity with { SessionGeneration = 2 }, fixture.CancellationToken);
        SessionReadinessDecision reconnectDecision = await fixture.Store.GetReadinessAsync(
            "AGV-001", fixture.CancellationToken);
        Assert.Equal(SessionReadiness.RecoveryRequired, reconnectDecision.Readiness);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task SessionStaysUsableWhileOurOwnSlotOperationHoldsASlotUnlocked()
    {
        // Carrying out a SlotOperationCommand means unlocking a slot, and an unlocked slot is
        // correctly reported as unsafe to depart. Failing the session on that made the command
        // destroy its own precondition: the peer refused to continue the operation this server
        // had just asked for, and the journey could never leave the load stage.
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await ReachReadyAsync(fixture);
        await fixture.AddJourneyAsync("D-401", "AGV-001");
        await fixture.Store.PrepareSlotOperationAsync(
            new StationOperationPlan(
                "ATTEMPT-401", "D-401", "SUBLOT-401", [1], SlotOperationType.Load, 0, "plan-hash", fixture.Now),
            "MSG-CMD-401",
            "command-json",
            fixture.CancellationToken);

        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 10, false, "unlocked-hash", fixture.CancellationToken,
            ["LOCK_NOT_CLOSED", "UNLOCK_OUTPUT_NOT_RESET"], unknownPresent: false);
        SessionReadinessDecision duringOperation = await fixture.Store.DecideReadinessAsync(
            "AGV-001", 1, fixture.CancellationToken);

        Assert.Equal(SessionReadiness.Ready, duringOperation.Readiness);
        Assert.Equal("READY", duringOperation.ReasonCode);
    }

    /// <summary>
    /// The exemption is earned by this vehicle's own operation. It used to be granted whenever any operation
    /// anywhere was Prepared, so in a fleet one vehicle mid-load gave every other vehicle standing with a
    /// door ajar a Ready session -- and a vehicle judged Ready never shows its recovery entry
    /// (8005-agv-program#61, MVP <c>770447f5</c>).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AnotherVehiclesLoadDoesNotExplainThisVehiclesOpenDoor()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await ReachReadyAsync(fixture);
        await fixture.AddJourneyAsync("D-402", "AGV-002");
        await fixture.Store.PrepareSlotOperationAsync(
            new StationOperationPlan(
                "ATTEMPT-402", "D-402", "SUBLOT-402", [1], SlotOperationType.Load, 0, "plan-hash", fixture.Now),
            "MSG-CMD-402",
            "command-json",
            fixture.CancellationToken);

        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 10, false, "ajar-hash", fixture.CancellationToken,
            ["LOCK_NOT_CLOSED"], unknownPresent: false);
        SessionReadinessDecision idleVehicle = await fixture.Store.DecideReadinessAsync(
            "AGV-001", 1, fixture.CancellationToken);

        Assert.Equal(SessionReadiness.RecoveryRequired, idleVehicle.Readiness);
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY", idleVehicle.ReasonCode);
    }

    /// <summary>
    /// ADR-cross-0058 decision 5 in the store: a load that missed its target is Failed, not RecoveryRequired,
    /// only when every part of the account is determinate -- overall FAILED; every commanded slot's occupancy
    /// known, door locked and unlock output reset; each slot COMPLETED, FAILED or NOT_STARTED (an unstarted
    /// slot says so rather than UNKNOWN); received at or after the station deadline; and the failed slot's
    /// reason one with a terminal state. Each case below takes exactly one of those away from the settleable
    /// result. A Failed load leaves its demand Accepted for the runtime to end; recovery marks it
    /// RecoveryRequired.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("settleable", OperationResultDisposition.DeterminateFailure)]
    [InlineData("received-at-the-deadline", OperationResultDisposition.DeterminateFailure)]
    [InlineData("occupancy-unknown", OperationResultDisposition.RecoveryRequired)]
    [InlineData("door-unlocked", OperationResultDisposition.RecoveryRequired)]
    [InlineData("unlock-output-active", OperationResultDisposition.RecoveryRequired)]
    [InlineData("unstarted-slot-unknown", OperationResultDisposition.RecoveryRequired)]
    [InlineData("no-slot-outcomes", OperationResultDisposition.RecoveryRequired)]
    [InlineData("no-failed-slot", OperationResultDisposition.RecoveryRequired)]
    [InlineData("slot-missing", OperationResultDisposition.RecoveryRequired)]
    [InlineData("overall-unknown", OperationResultDisposition.RecoveryRequired)]
    [InlineData("before-the-deadline", OperationResultDisposition.FailedBeforeStationDeadline)]
    [InlineData("no-deadline", OperationResultDisposition.FailedBeforeStationDeadline)]
    [InlineData("reason-without-terminal-state", OperationResultDisposition.FailureReasonWithoutTerminalState)]
    [InlineData("failed-slot-without-reason", OperationResultDisposition.FailureReasonWithoutTerminalState)]
    public async Task OnlyACompletelyDeterminateLoadFailureAfterTheDeadlineIsFailed(
        string variant,
        OperationResultDisposition expected)
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await fixture.PrepareOperationAsync(SlotOperationType.Load, [1, 2]);
        DateTimeOffset deadline = fixture.Now.AddMinutes(5);

        SlotPhysicalEvidence failedSlot = new(1, SlotBusinessState.Empty, true, true);
        SlotPhysicalEvidence unstartedSlot = new(2, SlotBusinessState.Empty, true, true);
        SlotOutcomeReport failedOutcome = new(1, "FAILED", ["OPERATOR_TIMEOUT"]);
        SlotOutcomeReport unstartedOutcome = new(2, "NOT_STARTED", []);
        string overall = "FAILED";
        OperationResultReceipt? receipt = new(deadline.AddSeconds(1), deadline);
        switch (variant)
        {
            case "received-at-the-deadline": receipt = new(deadline, deadline); break;
            case "occupancy-unknown": failedSlot = failedSlot with { State = SlotBusinessState.Unknown }; break;
            case "door-unlocked": unstartedSlot = unstartedSlot with { DoorLocked = false }; break;
            case "unlock-output-active": failedSlot = failedSlot with { UnlockOutputReset = false }; break;
            case "unstarted-slot-unknown": unstartedOutcome = unstartedOutcome with { Outcome = "UNKNOWN" }; break;
            case "no-failed-slot": failedOutcome = failedOutcome with { Outcome = "COMPLETED" }; break;
            case "overall-unknown": overall = "UNKNOWN"; break;
            case "before-the-deadline": receipt = new(deadline.AddSeconds(-1), deadline); break;
            case "no-deadline": receipt = new(deadline.AddSeconds(1), null); break;
            case "reason-without-terminal-state":
                failedOutcome = failedOutcome with { ReasonCodes = ["ACTION_NOT_ALLOWED_IN_STATE"] };
                break;
            case "failed-slot-without-reason": failedOutcome = failedOutcome with { ReasonCodes = [] }; break;
        }
        SlotPhysicalEvidence[] evidence = variant == "slot-missing" ? [failedSlot] : [failedSlot, unstartedSlot];
        SlotOutcomeReport[]? outcomes = variant switch
        {
            "no-slot-outcomes" => null,
            "slot-missing" => [failedOutcome],
            _ => [failedOutcome, unstartedOutcome]
        };

        OperationResultDisposition disposition = await fixture.Store.ApplyOperationResultAsync(
            new StationOperationResult(
                "RESULT-001", "ATTEMPT-001", "D-001", SlotOperationType.Load, overall, evidence, false,
                fixture.Now, "result-hash", "wire-hash", outcomes),
            "AGV-001",
            0,
            fixture.CancellationToken,
            receipt);

        Assert.Equal(expected, disposition);
        StationOperationStatus status =
            (await fixture.Context.StationOperations.AsNoTracking().SingleAsync(fixture.CancellationToken)).Status;
        DemandExecutionStatus demand =
            (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(fixture.CancellationToken)).Status;
        if (expected == OperationResultDisposition.DeterminateFailure)
        {
            Assert.Equal(StationOperationStatus.Failed, status);
            Assert.Equal(DemandExecutionStatus.Accepted, demand);
        }
        else
        {
            Assert.Equal(StationOperationStatus.RecoveryRequired, status);
            Assert.Equal(DemandExecutionStatus.RecoveryRequired, demand);
        }
    }

    /// <summary>
    /// ADR-cross-0015 gives unload no cancellation branch: an unload that misses its target keeps closing the
    /// loop until the slots are empty. So the determinate shape that settles a load, after its deadline and
    /// under OPERATOR_TIMEOUT, still goes to recovery for an unload.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnUnloadOfTheSameDeterminateShapeStillNeedsRecovery()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await fixture.PrepareOperationAsync(SlotOperationType.Unload, [1, 2]);
        DateTimeOffset deadline = fixture.Now.AddMinutes(5);

        OperationResultDisposition disposition = await fixture.Store.ApplyOperationResultAsync(
            new StationOperationResult(
                "RESULT-001", "ATTEMPT-001", "D-001", SlotOperationType.Unload, "FAILED",
                [
                    new SlotPhysicalEvidence(1, SlotBusinessState.Occupied, true, true),
                    new SlotPhysicalEvidence(2, SlotBusinessState.Occupied, true, true)
                ],
                false,
                fixture.Now,
                "result-hash",
                "wire-hash",
                [new SlotOutcomeReport(1, "FAILED", ["OPERATOR_TIMEOUT"]), new SlotOutcomeReport(2, "NOT_STARTED", [])]),
            "AGV-001",
            0,
            fixture.CancellationToken,
            new OperationResultReceipt(deadline.AddMinutes(1), deadline));

        Assert.Equal(OperationResultDisposition.RecoveryRequired, disposition);
        Assert.Equal(
            StationOperationStatus.RecoveryRequired,
            (await fixture.Context.StationOperations.AsNoTracking().SingleAsync(fixture.CancellationToken)).Status);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task UnsafetyOurOwnOperationDoesNotExplainStillFailsTheSessionClosed()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await ReachReadyAsync(fixture);
        await fixture.Store.PrepareSlotOperationAsync(
            new StationOperationPlan(
                "ATTEMPT-402", "D-402", "SUBLOT-402", [1], SlotOperationType.Load, 0, "plan-hash", fixture.Now),
            "MSG-CMD-402",
            "command-json",
            fixture.CancellationToken);

        // One reason outside the operation-induced set is enough to fail closed.
        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 11, false, "moving-hash", fixture.CancellationToken,
            ["LOCK_NOT_CLOSED", "VEHICLE_STATE_UNKNOWN"], unknownPresent: false);
        Assert.Equal(
            SessionReadiness.RecoveryRequired,
            (await fixture.Store.DecideReadinessAsync("AGV-001", 1, fixture.CancellationToken)).Readiness);

        // Incomplete evidence is never treated as explained.
        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 12, false, "unknown-hash", fixture.CancellationToken,
            ["LOCK_NOT_CLOSED"], unknownPresent: true);
        Assert.Equal(
            SessionReadiness.RecoveryRequired,
            (await fixture.Store.DecideReadinessAsync("AGV-001", 1, fixture.CancellationToken)).Readiness);

        // Neither is a peer that reports no reason at all, nor a legacy row with no reasons stored.
        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 13, false, "silent-hash", fixture.CancellationToken, [], unknownPresent: false);
        Assert.Equal(
            SessionReadiness.RecoveryRequired,
            (await fixture.Store.DecideReadinessAsync("AGV-001", 1, fixture.CancellationToken)).Readiness);
        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 14, false, "legacy-hash", fixture.CancellationToken);
        SessionReadinessDecision legacy = await fixture.Store.DecideReadinessAsync(
            "AGV-001", 1, fixture.CancellationToken);
        Assert.Equal(SessionReadiness.RecoveryRequired, legacy.Readiness);
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY", legacy.ReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task AnUnlockedSlotWithNoOperationOfOursInFlightFailsTheSessionClosed()
    {
        // An idle vehicle standing with a locker open is exactly what this gate is for. Nothing
        // this server commanded explains it, so it must still fail closed.
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await ReachReadyAsync(fixture);

        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 15, false, "idle-open-hash", fixture.CancellationToken,
            ["LOCK_NOT_CLOSED"], unknownPresent: false);
        SessionReadinessDecision decision = await fixture.Store.DecideReadinessAsync(
            "AGV-001", 1, fixture.CancellationToken);

        Assert.Equal(SessionReadiness.RecoveryRequired, decision.Readiness);
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY", decision.ReasonCode);
    }

    /// <summary>
    /// Pins behaviour that is BY DESIGN, not a defect: do not "fix" this test by exempting the vehicle's own
    /// journey. A vehicle carrying this server's gate order cannot be shown to stand still -- the vehicle-safety
    /// projection reads it UNKNOWN (RIOT_NONFINAL_ORDER_PRESENT) or MOVING -- so its onboard reports
    /// departureSafe=false with VEHICLE_NOT_READY or ACTION_NOT_ALLOWED_IN_STATE, and the session is held at
    /// RecoveryRequired / DEPARTURE_SAFETY_NOT_READY in the same generation until it reports safe again.
    /// docs/RELEASE-CANDIDATE.md section 8 names this the expected behaviour of the safety gate, and
    /// evidence/g3/20260830-issue14-field-closed-loop saw it on the real vehicle on both legs. Unlike the
    /// slot-operation exemption above, nothing the vehicle may do next depends on this session while it moves,
    /// and the vehicle-motion reasons are exactly the ones this gate exists to hold. control-server#138 found it
    /// as a flaky L2 criterion that read the session a moment after departure.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-03")]
    public async Task ByDesignAVehicleOnItsOwnGateLegHoldsTheSessionNotReadyUntilItStandsStill()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await ReachReadyAsync(fixture);
        await fixture.AddJourneyAsync("D-138", "AGV-001");
        JourneyRuntimeRow journey = await fixture.Context.JourneyRuntimes.SingleAsync(
            row => row.DemandId == "D-138", fixture.CancellationToken);
        journey.Stage = JourneyRuntimeStage.AwaitingGateArrival;
        await fixture.Context.SaveChangesAsync(fixture.CancellationToken);

        // Given the gate order but not yet moving: RIoT holds a non-final order, so standing still is unknown.
        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 10, false, "order-held-hash", fixture.CancellationToken,
            ["VEHICLE_NOT_READY"], unknownPresent: true);
        SessionReadinessDecision orderHeld = await fixture.Store.DecideReadinessAsync(
            "AGV-001", 1, fixture.CancellationToken);
        Assert.Equal(SessionReadiness.RecoveryRequired, orderHeld.Readiness);
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY", orderHeld.ReasonCode);

        // Moving.
        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 11, false, "moving-hash", fixture.CancellationToken,
            ["ACTION_NOT_ALLOWED_IN_STATE"], unknownPresent: false);
        SessionReadinessDecision moving = await fixture.Store.DecideReadinessAsync(
            "AGV-001", 1, fixture.CancellationToken);
        Assert.Equal(SessionReadiness.RecoveryRequired, moving.Readiness);
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY", moving.ReasonCode);

        // Standing still at the gate: Ready again, in the same session.
        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 12, true, "arrived-hash", fixture.CancellationToken, [], unknownPresent: false);
        SessionReadinessDecision arrived = await fixture.Store.DecideReadinessAsync(
            "AGV-001", 1, fixture.CancellationToken);
        Assert.Equal(SessionReadiness.Ready, arrived.Readiness);
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(
            row => row.AgvId == "AGV-001", fixture.CancellationToken);
        Assert.Equal(1, session.SessionGeneration);
    }

    private static async Task ReachReadyAsync(StoreFixture fixture)
    {
        SessionIdentity identity = new(
            "AGV-001", 1, ProtocolCandidateIdentity.RepositoryCommit,
            ProtocolCandidateIdentity.ManifestSha256, ProtocolCandidateIdentity.ProfileId,
            ProtocolCandidateIdentity.ProtocolVersion);
        await fixture.Store.BeginSessionRecoveryAsync(identity, fixture.CancellationToken);
        await fixture.Store.ApplyCapabilitySnapshotAsync("AGV-001", 1, 4, "cap-hash", null, fixture.CancellationToken);
        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 9, true, "safe-hash", fixture.CancellationToken, [], unknownPresent: false);
        await fixture.Store.ApplyRecoveryReportAsync(
            "AGV-001", 1, "REPORT-001", 0, null, null, [], [], [], fixture.CancellationToken);
        Assert.Equal(
            SessionReadiness.Ready,
            (await fixture.Store.DecideReadinessAsync("AGV-001", 1, fixture.CancellationToken)).Readiness);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task SlotPlanAndReliableCommandAreAtomicAndBatchNeedsCompleteSafeEvidence()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        StationOperationPlan plan = new(
            "ATTEMPT-001", "D-001", "SUBLOT-001", [1, 2], SlotOperationType.Load, 0, "plan-hash", fixture.Now);

        await fixture.Store.PrepareSlotOperationAsync(plan, "MSG-CMD-001", "command-json", fixture.CancellationToken);

        Assert.Equal(1, await fixture.Context.StationOperations.CountAsync(fixture.CancellationToken));
        Assert.Equal(1, await fixture.Context.ProtocolOutbox.CountAsync(fixture.CancellationToken));
        await Assert.ThrowsAsync<UnsafePhysicalEvidenceException>(() => fixture.Store.CommitSlotBatchAsync(
            "ATTEMPT-001",
            [new SlotPhysicalEvidence(1, SlotBusinessState.Occupied, true, true)],
            fixture.Now.AddSeconds(1), fixture.CancellationToken));

        await fixture.Store.CommitSlotBatchAsync(
            "ATTEMPT-001",
            [
                new SlotPhysicalEvidence(1, SlotBusinessState.Occupied, true, true),
                new SlotPhysicalEvidence(2, SlotBusinessState.Occupied, true, true)
            ],
            fixture.Now.AddSeconds(2), fixture.CancellationToken);

        StationOperationRow row = await fixture.Context.StationOperations.SingleAsync(fixture.CancellationToken);
        Assert.Equal(StationOperationStatus.Committed, row.Status);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task UnsafeCompletedOperationResultIsDurableButMovesDemandToRecovery()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await fixture.Store.AcceptWithOrderIntentAsync(
            new AcceptedDemandSnapshot(
                "D-001",
                "SUBLOT-001|WIRE_TO_GATE",
                7,
                "history-1",
                21,
                fixture.Now),
            new OrderIntent(
                "LEG-001",
                "D-001",
                "W2G-D-001-PICKUP-1",
                "TO_PICKUP",
                "ST-PICKUP",
                fixture.Now),
            fixture.CancellationToken);
        await fixture.Store.PrepareSlotOperationAsync(
            new StationOperationPlan(
                "ATTEMPT-001",
                "D-001",
                "SUBLOT-001",
                [1],
                SlotOperationType.Load,
                0,
                "plan-hash",
                fixture.Now),
            "MSG-CMD-001",
            "command-json",
            fixture.CancellationToken);

        OperationResultDisposition disposition = await fixture.Store.ApplyOperationResultAsync(
            new StationOperationResult(
                "RESULT-001",
                "ATTEMPT-001",
                "D-001",
                SlotOperationType.Load,
                "COMPLETED",
                [new SlotPhysicalEvidence(1, SlotBusinessState.Occupied, false, true)],
                true,
                fixture.Now.AddSeconds(1),
                "result-hash",
                "wire-hash"),
            "AGV-001",
            0,
            fixture.CancellationToken);

        Assert.Equal(OperationResultDisposition.RecoveryRequired, disposition);
        Assert.Equal(
            StationOperationStatus.RecoveryRequired,
            (await fixture.Context.StationOperations.SingleAsync(fixture.CancellationToken)).Status);
        Assert.Equal(
            DemandExecutionStatus.RecoveryRequired,
            (await fixture.Context.AcceptedDemands.SingleAsync(fixture.CancellationToken)).Status);
        Assert.False((await fixture.Context.OperationResults.SingleAsync(fixture.CancellationToken)).HistoricalOnly);
    }

    /// <summary>
    /// 装载进行中被操作员取消：取消授权、全部仓位证空、取消收敛——需求 Cancelled、装载 Cancelled。原装载的
    /// 执行器此时仍在等放货，等满车载端的操作超时才交出一份 UNKNOWN 结果。这份结果晚于取消的收敛，它说的
    /// 那次装载已经被一个证过全空的取消终结了。服务端此前照常把它判为不安全，把装载和需求都改回
    /// RecoveryRequired，会话随之 RECOVERY_REQUIRED，车被一次早已结清的装载扣住
    /// （G3 FP-IS-02 调试运行 cancel-002 的 G3-02-27；docs/defects/20260913-late-load-result-reopens-cancelled-load.md）。
    /// 结果照样落库、照样确认，只是作为历史记录，不再改动已经终结的事实。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ALateResultForALoadAlreadyCancelledIsKeptButReopensNothing()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await fixture.Store.AcceptWithOrderIntentAsync(
            new AcceptedDemandSnapshot(
                "D-001",
                "SUBLOT-001|WIRE_TO_GATE",
                7,
                "history-1",
                21,
                fixture.Now),
            new OrderIntent(
                "LEG-001",
                "D-001",
                "W2G-D-001-PICKUP-1",
                "TO_PICKUP",
                "ST-PICKUP",
                fixture.Now),
            fixture.CancellationToken);
        await fixture.Store.PrepareSlotOperationAsync(
            new StationOperationPlan(
                "ATTEMPT-001",
                "D-001",
                "SUBLOT-001",
                [1, 2],
                SlotOperationType.Load,
                0,
                "plan-hash",
                fixture.Now),
            "MSG-CMD-001",
            "command-json",
            fixture.CancellationToken);
        (await fixture.Context.StationOperations.SingleAsync(fixture.CancellationToken)).Status =
            StationOperationStatus.Cancelled;
        (await fixture.Context.AcceptedDemands.SingleAsync(fixture.CancellationToken)).Status =
            DemandExecutionStatus.Cancelled;
        await fixture.Context.SaveChangesAsync(fixture.CancellationToken);

        OperationResultDisposition disposition = await fixture.Store.ApplyOperationResultAsync(
            new StationOperationResult(
                "RESULT-LATE",
                "ATTEMPT-001",
                "D-001",
                SlotOperationType.Load,
                "UNKNOWN",
                [
                    new SlotPhysicalEvidence(1, SlotBusinessState.Empty, true, true),
                    new SlotPhysicalEvidence(2, SlotBusinessState.Empty, true, true)
                ],
                false,
                fixture.Now.AddSeconds(120),
                "result-hash",
                "wire-hash"),
            "AGV-001",
            0,
            fixture.CancellationToken);

        Assert.Equal(OperationResultDisposition.HistoricalOnly, disposition);
        Assert.Equal(
            StationOperationStatus.Cancelled,
            (await fixture.Context.StationOperations.SingleAsync(fixture.CancellationToken)).Status);
        Assert.Equal(
            DemandExecutionStatus.Cancelled,
            (await fixture.Context.AcceptedDemands.SingleAsync(fixture.CancellationToken)).Status);
        OperationResultRow kept = await fixture.Context.OperationResults.SingleAsync(fixture.CancellationToken);
        Assert.Equal("UNKNOWN", kept.OverallOutcome);
        Assert.True(kept.HistoricalOnly);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task GateMovementRequiresFreshSafeCheckAndStableIntentIdentity()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        SafetyCheckObservation fresh = new("CHECK-001", 12, true, fixture.Now, fixture.Now.AddSeconds(10));
        OrderIntent gateIntent = new("LEG-GATE-001", "D-001", "W2G-D-001-GATE-1", "TO_GATE", "ST-GATE", fixture.Now);

        await fixture.Store.AuthorizeMovementAsync(gateIntent, fresh, fixture.Now.AddSeconds(5), fixture.CancellationToken);
        Assert.Equal(1, await fixture.Context.OrderIntents.CountAsync(fixture.CancellationToken));

        SafetyCheckObservation expired = fresh with { CheckId = "CHECK-002", ValidUntil = fixture.Now.AddSeconds(3) };
        await Assert.ThrowsAsync<UnsafeMovementAuthorizationException>(() => fixture.Store.AuthorizeMovementAsync(
            gateIntent with { MovementLegId = "LEG-GATE-002", UpperId = "W2G-D-001-GATE-2" },
            expired, fixture.Now.AddSeconds(4), fixture.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-04")]
    [Trait("ProtocolVector", "CV-DESTINATION-UNLOAD-ALL-EMPTY")]
    public async Task UnloadCompletionCommitsAllFourFactsExactlyOnce()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await fixture.Store.AcceptWithOrderIntentAsync(
            new AcceptedDemandSnapshot("D-001", "SUBLOT-001|WIRE_TO_GATE", 7, "history-1", 21, fixture.Now),
            new OrderIntent("LEG-001", "D-001", "W2G-D-001-PICKUP-1", "TO_PICKUP", "ST-PICKUP", fixture.Now),
            fixture.CancellationToken);

        SlotPhysicalEvidence[] allEmpty =
        [
            new(1, SlotBusinessState.Empty, true, true),
            new(2, SlotBusinessState.Empty, true, true)
        ];
        await fixture.Store.CompleteDemandAfterUnloadAsync(
            "UNLOAD-001", "D-001", "SUBLOT-001|WIRE_TO_GATE", 7, allEmpty,
            "all-empty-locked-output-reset", fixture.Now.AddMinutes(1), fixture.CancellationToken);
        await fixture.Store.CompleteDemandAfterUnloadAsync(
            "UNLOAD-001", "D-001", "SUBLOT-001|WIRE_TO_GATE", 7, allEmpty,
            "all-empty-locked-output-reset", fixture.Now.AddMinutes(1), fixture.CancellationToken);

        Assert.Equal(1, await fixture.Context.UnloadBatches.CountAsync(fixture.CancellationToken));
        Assert.Equal(1, await fixture.Context.StopClosures.CountAsync(fixture.CancellationToken));
        Assert.Equal(1, await fixture.Context.TransportDemandCompletions.CountAsync(fixture.CancellationToken));
        Assert.Equal(DemandExecutionStatus.Succeeded,
            (await fixture.Context.AcceptedDemands.SingleAsync(fixture.CancellationToken)).Status);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-CONNECTION-LOSS-SAFE-FINISH")]
    public async Task ConnectionLossCanFinishButNeverExpandActiveUnlockSet()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await fixture.Store.RecordConnectionLossAsync("AGV-001", 4, [2], fixture.Now, fixture.CancellationToken);

        await Assert.ThrowsAsync<ActiveUnlockSetExpansionException>(() => fixture.Store.RecordSafeFinishAsync(
            "AGV-001", 4, [2, 3], fixture.Now.AddSeconds(1), fixture.CancellationToken));
        await fixture.Store.RecordSafeFinishAsync(
            "AGV-001", 4, [2], fixture.Now.AddSeconds(2), fixture.CancellationToken);

        ConnectionRecoveryRow row = await fixture.Context.ConnectionRecoveries.SingleAsync(fixture.CancellationToken);
        Assert.Equal(ConnectionRecoveryStatus.AwaitingHandshake, row.Status);
        Assert.False(row.ResumeAuthorized);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-SAME-CONTENT")]
    [Trait("ProtocolVector", "CV-REQUEST-FIRST-RESULT-REPLAY")]
    public async Task InboxReplaysFirstResponseAndRejectsDifferentContent()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        int sideEffects = 0;
        string first = await fixture.Store.CaptureFirstResponseAsync(
            "MSG-001", "same-hash", () => Task.FromResult($"result-{++sideEffects}"),
            fixture.Now, fixture.CancellationToken);
        string replay = await fixture.Store.CaptureFirstResponseAsync(
            "MSG-001", "same-hash", () => Task.FromResult($"result-{++sideEffects}"),
            fixture.Now.AddSeconds(1), fixture.CancellationToken);

        Assert.Equal("result-1", first);
        Assert.Equal(first, replay);
        Assert.Equal(1, sideEffects);
        await Assert.ThrowsAsync<ProtocolContentConflictException>(() => fixture.Store.CaptureFirstResponseAsync(
            "MSG-001", "different-hash", () => Task.FromResult("must-not-run"),
            fixture.Now.AddSeconds(2), fixture.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task ForcedRecoveryGenerationFencesLateResultsAndPersistsDecision()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await fixture.Store.AdvanceForcedRecoveryGenerationAsync("AGV-001", 2, fixture.Now, fixture.CancellationToken);

        OperationResultDisposition late = await fixture.Store.RecordOperationResultAsync(
            "RESULT-LATE", "ATTEMPT-001", "AGV-001", 1, "late-hash", fixture.Now.AddSeconds(1), fixture.CancellationToken);
        OperationResultDisposition current = await fixture.Store.RecordOperationResultAsync(
            "RESULT-CURRENT", "ATTEMPT-001", "AGV-001", 2, "current-hash", fixture.Now.AddSeconds(2), fixture.CancellationToken);
        string first = await fixture.Store.RecordRecoveryDecisionAsync(
            "RECOVERY-SESSION-001", "ACTION-001", 2, "decision-hash", "RESUME",
            fixture.Now.AddSeconds(3), fixture.CancellationToken);
        string replay = await fixture.Store.RecordRecoveryDecisionAsync(
            "RECOVERY-SESSION-001", "ACTION-001", 2, "decision-hash", "COMPENSATE",
            fixture.Now.AddSeconds(4), fixture.CancellationToken);

        Assert.Equal(OperationResultDisposition.HistoricalOnly, late);
        Assert.Equal(OperationResultDisposition.Accepted, current);
        Assert.Equal("RESUME", first);
        Assert.Equal(first, replay);
        Assert.Equal(1, await fixture.Context.RecoveryDecisions.CountAsync(fixture.CancellationToken));
    }

    private sealed class StoreFixture : IAsyncDisposable
    {
        private StoreFixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            CancellationToken cancellationToken)
        {
            Connection = connection;
            Context = context;
            Store = new WireToGateStore(context);
            CancellationToken = cancellationToken;
        }

        public SqliteConnection Connection { get; }
        public ControlServerDbContext Context { get; }
        public WireToGateStore Store { get; }
        public DateTimeOffset Now { get; } = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
        public CancellationToken CancellationToken { get; }

        public static async Task<StoreFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(connection)
                .Options;
            ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new StoreFixture(connection, context, TestContext.Current.CancellationToken);
        }

        /// <summary>An accepted demand D-001 whose slot operation ATTEMPT-001 has been commanded.</summary>
        public async Task PrepareOperationAsync(SlotOperationType type, int[] slots)
        {
            await Store.AcceptWithOrderIntentAsync(
                new AcceptedDemandSnapshot("D-001", "SUBLOT-001|WIRE_TO_GATE", 7, "history-1", 21, Now),
                new OrderIntent("LEG-001", "D-001", "W2G-D-001-PICKUP-1", "TO_PICKUP", "ST-PICKUP", Now),
                CancellationToken);
            await Store.PrepareSlotOperationAsync(
                new StationOperationPlan("ATTEMPT-001", "D-001", "SUBLOT-001", slots, type, 0, "plan-hash", Now),
                "MSG-CMD-001",
                "command-json",
                CancellationToken);
        }

        /// <summary>The journey that carries <paramref name="demandId"/> on <paramref name="agvId"/>.</summary>
        public async Task AddJourneyAsync(string demandId, string agvId)
        {
            JourneyRuntimeRow journey = new()
            {
                JourneyId = JourneyIdentity.ForAnchorDemand(demandId),
                DemandId = demandId,
                Stage = JourneyRuntimeStage.AwaitingLoadResult,
                AgvId = agvId,
                VehicleKey = "VEHICLE-" + agvId,
                AgvLifecycleGeneration = 1,
                MapId = 25,
                MapIdentity = "MAP-25",
                DispatchZone = "ZONE-01",
                RouteEvidenceId = "ROUTE-01",
                PickupStationId = "PICKUP",
                PickupStationRiotId = 11,
                GateStationId = "GATE",
                GateStationRiotId = 22,
                ExpectedBasketCount = 1,
                TargetSlotsJson = "[1]",
                OperationSessionId = $"session-{demandId}",
                PickupMovementLegId = $"pickup-leg-{demandId}",
                PickupUpperId = $"UPPER-PICKUP-{demandId}",
                GateMovementLegId = $"gate-leg-{demandId}",
                GateUpperId = $"UPPER-GATE-{demandId}",
                DispatchGeneration = 1,
                VehicleBusinessRevision = 1,
                WorklistRevision = 1,
                PlanRevision = 1,
                VehicleBusinessMessageId = $"vb-{demandId}",
                WorklistMessageId = $"wl-{demandId}",
                PlanMessageId = $"plan-{demandId}",
                SublotRequestMessageId = $"sublot-{demandId}",
                LoadCommandMessageId = $"load-{demandId}",
                LoadSlotOperationAttemptId = $"load-attempt-{demandId}",
                PreDepartureSafetyCheckMessageId = $"safety-msg-{demandId}",
                PreDepartureSafetyCheckId = $"safety-{demandId}",
                GateVehicleBusinessMessageId = $"gate-vb-{demandId}",
                GateWorklistMessageId = $"gate-wl-{demandId}",
                GatePlanMessageId = $"gate-plan-{demandId}",
                UnloadCommandMessageId = $"unload-{demandId}",
                UnloadSlotOperationAttemptId = $"unload-attempt-{demandId}",
                CreatedAt = Now,
                UpdatedAt = Now
            };
            Context.JourneyRuntimes.Add(journey);
            // control-server#207: acceptance writes the demand's membership beside the journey row.
            JourneyMembershipSeed.Seed(Context, journey);
            await Context.SaveChangesAsync(CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
