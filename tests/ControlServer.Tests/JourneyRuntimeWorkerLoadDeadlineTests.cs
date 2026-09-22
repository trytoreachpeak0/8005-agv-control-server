using System.Security.Cryptography;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// The load stop past its station departure deadline (ADR-cross-0058 decisions 4 and 5; control-server#81):
/// a door left open raises an alarm and ends nothing, and a determinate load failure ends the demand.
/// </summary>
public sealed class JourneyRuntimeWorkerLoadDeadlineTests
{
    /// <summary>
    /// ADR-cross-0058 decision 5, redone for the v2 one-demand journey. The vehicle ran out the stop and said
    /// so completely: slot FAILED under OPERATOR_TIMEOUT, the rest NOT_STARTED, all empty, locked and reset.
    /// The server ends the demand itself as CANCELLED_BY_STATION_TIMEOUT, settles the load command nobody
    /// else will answer, releases the vehicle and completes the journey, and nobody is asked to recover
    /// anything. Defensive: the v2 onboard never reports this; the protocol lets another onboard do so.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ADeterminateLoadFailureAfterTheDeadlineEndsTheDemandAndReleasesTheVehicle()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await AdvanceToLoadWithStationDeadlineAsync(fixture);

        fixture.Clock.Advance(TimeSpan.FromSeconds(11));
        string response = await ReportLoadResultAsync(fixture, DeterminateFailureSlots(fixture, "OPERATOR_TIMEOUT"));
        Assert.Equal("DurableAck", Assert.Single(MessageTypes(response)));
        Assert.Equal(StationOperationStatus.Failed, (await fixture.OperationAsync(SlotOperationType.Load)).Status);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);

        DateTimeOffset settledAt = fixture.Clock.GetUtcNow();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", runtime.BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.DemandRowAsync()).Status);
        Assert.Equal(StationOperationStatus.Failed, (await fixture.OperationAsync(SlotOperationType.Load)).Status);
        Assert.Equal(settledAt, (await fixture.LeaseAsync()).ReleasedAt);
        OrderIntentRow pickup = await fixture.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.Purpose == "TO_PICKUP", TestContext.Current.CancellationToken);
        Assert.Equal(settledAt, pickup.VehicleOccupancyReleasedAt);
        await VehicleOccupancyAssertions.AssertActiveLeasesAndPurposeClaimsMatchAsync(fixture.Context);
        ProtocolOutboxRow loadCommand = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageId == runtime.LoadCommandMessageId, TestContext.Current.CancellationToken);
        Assert.Equal(settledAt, loadCommand.AcknowledgedAt);
        Assert.Empty(await fixture.Context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.ExceptionRecoverySessions.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Contains(fixture.EngineLog.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("CANCELLED_BY_STATION_TIMEOUT"));
        await ZeroChangePin.AssertMatchesAsync(fixture.Context, "determinate-load-failure");
        await SuppressionAssertions.AssertTheDemandSuppressedAsync(fixture.Context, "CANCELLED_BY_STATION_TIMEOUT");
        // control-server#208：发出去的报文与修订号。录入提交的 messageId 是随机的，而它原样进了装货命令的
        // correlationId，所以按值遮掉。
        string submissionId = await fixture.Context.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "SublotSubmitted")
            .Select(row => row.MessageId)
            .SingleAsync(TestContext.Current.CancellationToken);
        await WirePin.AssertMatchesAsync(
            fixture.Context, "determinate-load-failure", fixture.Peer.Lines, [submissionId]);
    }

    /// <summary>
    /// A determinate-looking FAILED that arrives before the station deadline cannot be what decision 5 settles:
    /// that failure only ever follows the deadline. It is not accepted as one; the load goes to recovery and the
    /// journey blocks on it the ordinary way. The reason code it went to recovery under is logged by the
    /// processor (RecoveryStateMachineG2Tests).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AFailureReportedBeforeTheDeadlineIsNotSettledAsDeterminate()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await AdvanceToLoadWithStationDeadlineAsync(fixture);

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        string response = await ReportLoadResultAsync(fixture, DeterminateFailureSlots(fixture, "OPERATOR_TIMEOUT"));
        Assert.Equal(["DurableAck", "SessionReadiness"], MessageTypes(response));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Blocked, runtime.Stage);
        Assert.Equal("LOAD_RESULT_REQUIRES_RECOVERY", runtime.BlockReasonCode);
        Assert.Equal(StationOperationStatus.RecoveryRequired, (await fixture.OperationAsync(SlotOperationType.Load)).Status);
        Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await fixture.DemandRowAsync()).Status);
        Assert.Null((await fixture.LeaseAsync()).ReleasedAt);
    }

    /// <summary>
    /// ADR-cross-0058 decision 4. The stop runs out its deadline with no result and a slot door open: the
    /// journey is not ended and not Blocked -- it stays in AwaitingLoadResult under
    /// STATION_TIMEOUT_DOOR_NOT_CLOSED, which moves the duty to someone shutting the door. The alarm's start
    /// time is the block reason's own and does not move while the alarm holds (control-server#80); a shut
    /// door withdraws the alarm and clears it; an open door raises it again; and the result that then arrives
    /// settles the stop as the vehicle reports it, clearing the alarm.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ADoorLeftOpenPastTheDeadlineRaisesTheAlarmWithoutEndingTheStop()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await AdvanceToLoadWithStationDeadlineAsync(fixture);
        await fixture.SetSafetyEvidenceAsync(unknownPresent: false, reasonCodes: ["LOCK_NOT_CLOSED"]);

        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        DateTimeOffset raisedAt = fixture.Clock.GetUtcNow();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow alarmed = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, alarmed.Stage);
        Assert.Equal("STATION_TIMEOUT_DOOR_NOT_CLOSED", alarmed.BlockReasonCode);
        Assert.Equal(raisedAt, alarmed.BlockReasonSince);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        Assert.Null((await fixture.LeaseAsync()).ReleasedAt);
        Assert.Single(fixture.EngineLog.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("STATION_TIMEOUT_DOOR_NOT_CLOSED"));

        fixture.Clock.Advance(TimeSpan.FromMinutes(20));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow stillAlarmed = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, stillAlarmed.Stage);
        Assert.Equal("STATION_TIMEOUT_DOOR_NOT_CLOSED", stillAlarmed.BlockReasonCode);
        Assert.Equal(raisedAt, stillAlarmed.BlockReasonSince);
        // The door alarm itself is logged once, on the edge. Since control-server#273 the waiting journey watch also logs
        // this wait -- twenty minutes is past its threshold -- and its line names the reason code the stage carries; that
        // is a different line (event 2163, "has waited for a person"), so it is counted apart rather than filtered out.
        Assert.Single(fixture.EngineLog.Entries, entry =>
            entry.Message.Contains("with a slot door not closed", StringComparison.Ordinal));
        Assert.Single(fixture.EngineLog.Entries, entry =>
            entry.Message.Contains("has waited for a person", StringComparison.Ordinal) &&
            entry.Message.Contains("(reason STATION_TIMEOUT_DOOR_NOT_CLOSED)", StringComparison.Ordinal));

        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow withdrawn = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, withdrawn.Stage);
        Assert.Null(withdrawn.BlockReasonCode);
        Assert.Null(withdrawn.BlockReasonSince);

        await fixture.SetSafetyEvidenceAsync(unknownPresent: false, reasonCodes: ["LOCK_NOT_CLOSED"]);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal("STATION_TIMEOUT_DOOR_NOT_CLOSED", (await fixture.RuntimeAsync()).BlockReasonCode);

        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.ApplySafeResultAsync(
            await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow settled = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, settled.Stage);
        Assert.Null(settled.BlockReasonCode);
        Assert.Null(settled.BlockReasonSince);
    }

    /// <summary>
    /// A disconnect voids the station departure wait (ADR-cross-0055), and AwaitingLoadResult is not re-entered
    /// on the reconnect. Refilled behind the readiness gate the way AwaitingSublot's is, the alarm counts the
    /// full wait from there, rather than never arriving for a stop whose wait nothing refills.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task AReconnectDuringTheLoadRestartsTheWaitTheAlarmCountsFrom()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await AdvanceToLoadWithStationDeadlineAsync(fixture);

        fixture.Clock.Advance(TimeSpan.FromSeconds(8));
        await fixture.ReconnectAsync(2);
        await fixture.AdvanceSessionAsync(2);
        await fixture.SetSafetyEvidenceAsync(unknownPresent: false, reasonCodes: ["LOCK_NOT_CLOSED"]);
        DateTimeOffset refilledAt = fixture.Clock.GetUtcNow();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(refilledAt, (await fixture.RuntimeAsync()).StationDepartureWaitStartedAt);

        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal("STATION_TIMEOUT_DOOR_NOT_CLOSED", (await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// The settlement is decided again under the write lock, and a load cancellation the processor authorized
    /// for the same Failed load wins if it is already open: the cancellation decides the demand, and the runtime
    /// neither ends it nor records a terminal reason of its own.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnOpenLoadCancellationKeepsTheRuntimeFromEndingTheFailedLoadsDemand()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await AdvanceToLoadWithStationDeadlineAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromSeconds(11));
        await ReportLoadResultAsync(fixture, DeterminateFailureSlots(fixture, "OPERATOR_TIMEOUT"));
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            connection.RecoveryWorkflows.Add(new RecoveryWorkflowRow
            {
                WorkflowId = Guid.NewGuid().ToString("D"),
                WorkflowType = "LOAD_CANCELLATION",
                AgvId = runtime.AgvId,
                DemandId = runtime.DemandId,
                SlotOperationAttemptId = runtime.LoadSlotOperationAttemptId,
                SlotsJson = runtime.TargetSlotsJson,
                State = RecoveryWorkflowState.AwaitingResult,
                RequestMessageId = Guid.NewGuid().ToString("D"),
                RequestContentHash = new string('c', 64),
                CreatedAt = fixture.Clock.GetUtcNow(),
                UpdatedAt = fixture.Clock.GetUtcNow()
            });
            await connection.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        fixture.Context.ChangeTracker.Clear();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow unchanged = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, unchanged.Stage);
        Assert.Null(unchanged.BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        Assert.Null((await fixture.LeaseAsync()).ReleasedAt);
    }

    /// <summary>
    /// FR-031 AC-9, the station deadline's own measure: nothing is ended against evidence from a vehicle that
    /// has since gone off air. The settlement waits, and the next thing heard on the same session lets it
    /// through.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task ADeterminateLoadFailureIsNotSettledWhileTheVehicleIsOffAir()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.MaximumEvidenceAge = TimeSpan.FromSeconds(5);
        await AdvanceToLoadWithStationDeadlineAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromSeconds(11));
        await ReportLoadResultAsync(fixture, DeterminateFailureSlots(fixture, "OPERATOR_TIMEOUT"));

        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);

        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.DemandRowAsync()).Status);
    }

    /// <summary>A ten-second station departure wait, and the journey carried to its outstanding load command.</summary>
    private static async Task AdvanceToLoadWithStationDeadlineAsync(RuntimeFixture fixture)
    {
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.AdvanceToLoadResultAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, runtime.Stage);
        Assert.Equal(Now, runtime.StationDepartureWaitStartedAt);
    }

    /// <summary>
    /// ADR-cross-0058 decision 5's slots for this journey's load: the first target FAILED under
    /// <paramref name="failedSlotReasonCode"/>, the rest NOT_STARTED, every one empty, locked and reset.
    /// </summary>
    private static object[] DeterminateFailureSlots(RuntimeFixture fixture, string failedSlotReasonCode)
    {
        int[] slots = JsonSerializer.Deserialize<int[]>(
            fixture.Context.StationOperations.AsNoTracking().Single().TargetSlotsJson) ?? [];
        return
        [
            .. slots.Select((slot, index) => (object)new
            {
                slotNo = slot,
                outcome = index == 0 ? "FAILED" : "NOT_STARTED",
                finalPhysicalState = "EMPTY",
                lockState = "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = index == 0 ? new[] { failedSlotReasonCode } : Array.Empty<string>()
            })
        ];
    }

    /// <summary>
    /// The vehicle's OperationResult for this journey's load, through OnboardMessageProcessor on a connection
    /// context of its own, the way every result on a TCP connection arrives. Returns the server's answer.
    /// </summary>
    private static async Task<string> ReportLoadResultAsync(RuntimeFixture fixture, object[] slotResults)
    {
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection,
            new WireToGateStore(connection),
            fixture.Clock,
            new ConfigurationBuilder().Build(),
            runtimeOptions: fixture.Options);
        var withoutHash = new
        {
            demandId = load.DemandId,
            slotOperationAttemptId = load.SlotOperationAttemptId,
            operationType = "LOAD",
            overallOutcome = "FAILED",
            slotResults,
            observedAt = fixture.Clock.GetUtcNow(),
            journalCheckpoint = "OPERATOR_TIMEOUT_RECORDED"
        };
        string resultContentSha256 = Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(withoutHash, SerializerOptions))).ToLowerInvariant();
        string line = JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType = "OperationResult",
            messageId = Guid.NewGuid().ToString("D"),
            correlationId = (string?)null,
            agvId = fixture.Options.AgvId,
            sessionGeneration = 1L,
            sentAt = fixture.Clock.GetUtcNow(),
            payload = new
            {
                withoutHash.demandId,
                withoutHash.slotOperationAttemptId,
                withoutHash.operationType,
                withoutHash.overallOutcome,
                withoutHash.slotResults,
                withoutHash.observedAt,
                withoutHash.journalCheckpoint,
                resultContentSha256
            }
        }, SerializerOptions);
        string response = await processor.ProcessAsync(
            line,
            new OnboardConnectionState
            {
                AgvId = fixture.Options.AgvId,
                SessionGeneration = 1,
                CapabilityRevision = 1,
                SafetyRevision = 7,
                Readiness = SessionReadiness.Ready
            },
            TestContext.Current.CancellationToken);
        // The runtime reads each iteration in a scope of its own; the fixture's engine keeps one context, so
        // what it tracked before this connection wrote is dropped the way a new scope would drop it.
        fixture.Context.ChangeTracker.Clear();
        return response;
    }

    private static string[] MessageTypes(string response) =>
    [
        .. response.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("messageType").GetString() ?? string.Empty)
    ];
}
