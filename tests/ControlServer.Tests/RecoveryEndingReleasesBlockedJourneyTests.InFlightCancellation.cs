using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.StopEndedJourneyContinuesTests;
using static ControlServer.Tests.UnloadCrashResumeTests;

namespace ControlServer.Tests;

public sealed partial class RecoveryEndingReleasesBlockedJourneyTests
{
    private static readonly string[] UnknownPhysicalState = ["PHYSICAL_STATE_UNKNOWN"];

    /// <summary>
    /// control-server#697, review L4: the multi-demand shape of an unreconciled in-flight cancellation. One pickup, two
    /// demands, the first loaded; the operator cancels the second's load mid-way and the clearing comes back FAILED with no
    /// OperationResult. The load is judged RecoveryRequired with that result, so the compensation is taken, and ending the
    /// second demand puts the journey back to await the load result at the stop -- the engine then sends the vehicle on with
    /// the first, as <see cref="ACompensationOfOneOfTwoDemandsAtOnePickupSendsTheVehicleOnWithTheOther"/> does.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationAfterAnUnreconciledInFlightCancellationOfOneOfTwoDemandsReleasesTheJourney()
    {
        await WithProofAsync(async () =>
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            JourneyRuntimeRow runtime = await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
            await AddInboxAsync(
                fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));
            await TickAndRunAsync(fixture);
            await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
            await TickAndRunAsync(fixture);
            await EnterSublotAsync(fixture, FirstDemandId, SecondSublot, SecondSubmissionId);
            StationOperationRow load = await OperationOfAsync(fixture, SecondDemandId, SlotOperationType.Load);
            Assert.Equal(StationOperationStatus.Prepared, load.Status);
            int[] slots = System.Text.Json.JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!;
            const string cancellationId = "d6970000-0000-4000-8000-000000000001";
            // Nothing loaded and every door shut since the second load began (control-server#837).
            await TestSlotReadings.SeedProvenEmptyAsync(fixture.Context, fixture.Options.AgvId, fixture.Clock.GetUtcNow());
            await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
            {
                OnboardMessageProcessor processor = RecoveryProcessor(fixture, connection);
                string authorization = await processor.ProcessAsync(
                    Envelope(fixture, "LoadCancellationStartRequested", new
                    {
                        cancellationId,
                        demandId = SecondDemandId,
                        slotOperationAttemptId = load.SlotOperationAttemptId,
                        @operator = BeforeSublotOperator(fixture),
                        reason = "Operator cancelled the load while it waited."
                    }),
                    Connection(fixture),
                    token);
                Assert.Equal("AUTHORIZED", FirstLinePayload(authorization).GetProperty("decision").GetString());
                Assert.Equal("DurableAck", FirstLineType(await processor.ProcessAsync(
                    Envelope(fixture, "LoadCancellationResult", new
                    {
                        cancellationId,
                        demandId = SecondDemandId,
                        slotOperationAttemptId = load.SlotOperationAttemptId,
                        overallOutcome = "FAILED",
                        slotResults = slots.Select(slot => new
                        {
                            slotNo = slot,
                            outcome = "FAILED",
                            finalPhysicalState = "UNKNOWN",
                            lockState = "UNKNOWN",
                            unlockOutputState = "UNKNOWN",
                            reasonCodes = UnknownPhysicalState
                        }).ToArray(),
                        observedAt = fixture.Clock.GetUtcNow()
                    }),
                    Connection(fixture),
                    token)));
            }
            fixture.Context.ChangeTracker.Clear();
            JourneyRuntimeRow blocked = await JourneyOfAsync(fixture, SecondDemandId);
            Assert.Equal(
                (JourneyRuntimeStage.Blocked, "LoadCancellationResult_NOT_RECONCILED"), (blocked.Stage, blocked.BlockReasonCode));
            Assert.Equal(StationOperationStatus.RecoveryRequired,
                (await OperationOfAsync(fixture, SecondDemandId, SlotOperationType.Load)).Status);

            int checksBefore = await CountAsync(fixture, "PreDepartureSafetyCheck");

            await CompensateAsync(fixture, SecondDemandId);

            await AssertReleasedAsync(fixture, JourneyRuntimeStage.AwaitingLoadResult);
            Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
            // Nothing holds the vehicle any more: the compensation's own answer put the session back to READY, with no
            // readiness restored by hand, and the engine sends it on with the first demand.
            Assert.False(await new WireToGateStore(fixture.Context).OperationNeedsRecoveryAsync(fixture.Options.AgvId, token));
            Assert.Equal(SessionReadiness.Ready,
                (await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(token)).Readiness);
            Exception? first = await RunRoundAsync(fixture);
            Exception? second = await RunRoundAsync(fixture);
            await AssertAskedToLeaveAsync(fixture, checksBefore, first, second);
        });
    }
}
