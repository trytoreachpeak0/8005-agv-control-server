using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// What another scope can read of a journey at the instant its LOAD command reaches the vehicle
/// (control-server#193).
/// </summary>
/// <remarks>
/// <para>
/// The iteration that loads does not commit in one piece. The outbox row and the Prepared station operation
/// are saved and the line is sent (<c>OnboardJourneyPublisher.PublishSlotOperationEnvelopeAsync</c>), the entry
/// request is settled in a save of its own, and only the save at the end of the iteration writes
/// <see cref="JourneyRuntimeStage.AwaitingLoadResult"/>. So a vehicle can hold the command while every other
/// scope still reads <see cref="JourneyRuntimeStage.AwaitingSublot"/>. The L2 criterion L2-LN-01 read in exactly
/// that window and went red (CI run 35432232407).
/// </para>
/// <para>
/// The window has no business consequence, and the second test is why: what could act on the old stage from
/// another scope is the cancellation before a sublot, and it is refused there, because the load command it
/// checks for is already durable when the line goes out. The stage is the last thing to move, not the fact
/// anything decides on.
/// </para>
/// </remarks>
public sealed class JourneyRuntimeWorkerLoadCommandCommitOrderTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task TheLoadCommandReachesTheVehicleBeforeItsIterationCommitsAwaitingLoadResult()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotEntryAsync();
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();

        JourneyRuntimeStage? stageAtSend = null;
        bool commandDurableAtSend = false;
        StationOperationStatus? loadAtSend = null;
        fixture.Peer.OnMessageSent = async line =>
        {
            if (!IsSlotOperationCommand(line)) return;
            await using ControlServerDbContext reader = fixture.OpenConnectionContext();
            stageAtSend = (await reader.JourneyRuntimes.AsNoTracking().SingleAsync(token)).Stage;
            commandDurableAtSend = await reader.ProtocolOutbox.AsNoTracking()
                .AnyAsync(row => row.MessageId == waiting.LoadCommandMessageId, token);
            loadAtSend = (await reader.StationOperations.AsNoTracking().SingleOrDefaultAsync(token))?.Status;
        };

        await fixture.Engine.ExecuteOnceAsync(token);

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, stageAtSend);
        Assert.True(commandDurableAtSend);
        Assert.Equal(StationOperationStatus.Prepared, loadAtSend);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ACancellationBeforeSublotArrivingWhileTheStageStillReadsAwaitingSublotIsRefused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotEntryAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build());
        OnboardConnectionState state = new()
        {
            AgvId = fixture.Options.AgvId,
            SessionGeneration = 1,
            CapabilityRevision = 1,
            SafetyRevision = 7,
            Readiness = SessionReadiness.Ready
        };

        JourneyRuntimeStage? stageAtRequest = null;
        string? response = null;
        fixture.Peer.OnMessageSent = async line =>
        {
            if (!IsSlotOperationCommand(line)) return;
            stageAtRequest = (await connection.JourneyRuntimes.AsNoTracking().SingleAsync(token)).Stage;
            response = await processor.ProcessAsync(
                BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), "LoadCancellationStartRequested", 1, new
                {
                    cancellationId = "c1930000-0000-4000-8000-000000000001",
                    demandId = DemandId,
                    slotOperationAttemptId = (string?)null,
                    @operator = BeforeSublotOperator(fixture),
                    reason = "Nothing to load at this stop."
                }),
                state,
                token);
        };

        await fixture.Engine.ExecuteOnceAsync(token);

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, stageAtRequest);
        Assert.NotNull(response);
        using JsonDocument answer = JsonDocument.Parse(response.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        Assert.Equal("LoadCancellationAuthorization", answer.RootElement.GetProperty("messageType").GetString());
        Assert.Equal("REJECTED", answer.RootElement.GetProperty("payload").GetProperty("decision").GetString());
        Assert.Equal(0, await fixture.Context.RecoveryWorkflows.CountAsync(token));
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
    }

    private static bool IsSlotOperationCommand(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("messageType").GetString() == "SlotOperationCommand";
    }

    /// <summary>The journey at its pickup with the operator's entry durable, one iteration before the load.</summary>
    private static async Task<RuntimeFixture> ReachSublotEntryAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.AdvanceToSublotWaitAsync()).Stage);
        await fixture.SubmitSublotAsync("SUBLOT-001");
        return fixture;
    }
}
