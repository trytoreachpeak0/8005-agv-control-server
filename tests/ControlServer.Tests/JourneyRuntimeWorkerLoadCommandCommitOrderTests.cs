using System.Text.Json;
using ControlServer.Application;
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
/// Until control-server#362 the iteration that loads did not commit in one piece: the outbox row and the Prepared
/// station operation were saved and the line was sent, and only the save at the end of the iteration wrote
/// <see cref="JourneyRuntimeStage.AwaitingLoadResult"/>. A vehicle could hold the command while every other scope
/// still read <see cref="JourneyRuntimeStage.AwaitingSublot"/>; the L2 criterion L2-LN-01 read in exactly that
/// window and went red (CI run 35432232407). These tests pinned that window (control-server#193).
/// </para>
/// <para>
/// Since #362 the command, the Prepared operation, the demand's LOADING membership and the stage are written in one
/// write transaction, after the demand has been re-checked under that lock, and the line goes out only after it
/// commits (<c>OnboardJourneyPublisher.StageSlotOperationCommandAsync</c>, then <c>SendPersistedAsync</c>). The
/// window is closed: whatever reaches the vehicle, every other scope already reads. What #193 established still
/// holds and the second test keeps it: a cancellation before a sublot that arrives while the line goes out is
/// refused, because the load it checks for is durable.
/// </para>
/// </remarks>
public sealed class JourneyRuntimeWorkerLoadCommandCommitOrderTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task TheLoadCommandReachesTheVehicleOnlyAfterItsIterationCommitsAwaitingLoadResult()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotEntryAsync();
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();

        JourneyRuntimeStage? stageAtSend = null;
        bool commandDurableAtSend = false;
        StationOperationStatus? loadAtSend = null;
        string? membershipAtSend = null;
        fixture.Peer.OnMessageSent = async line =>
        {
            if (!IsSlotOperationCommand(line)) return;
            await using ControlServerDbContext reader = fixture.OpenConnectionContext();
            stageAtSend = (await reader.JourneyRuntimes.AsNoTracking().SingleAsync(token)).Stage;
            commandDurableAtSend = await reader.ProtocolOutbox.AsNoTracking()
                .AnyAsync(row => row.MessageId == waiting.LoadCommandMessageId, token);
            loadAtSend = (await reader.StationOperations.AsNoTracking().SingleOrDefaultAsync(token))?.Status;
            membershipAtSend = (await reader.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(token)).Status;
        };

        await fixture.Engine.ExecuteOnceAsync(token);

        // Everything the command stands for is committed before the line leaves (control-server#362).
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, stageAtSend);
        Assert.True(commandDurableAtSend);
        Assert.Equal(StationOperationStatus.Prepared, loadAtSend);
        Assert.Equal(JourneyDemandStatuses.Loading, membershipAtSend);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ACancellationBeforeSublotArrivingWhileTheLoadCommandGoesOutIsRefused()
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

        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, stageAtRequest);
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
