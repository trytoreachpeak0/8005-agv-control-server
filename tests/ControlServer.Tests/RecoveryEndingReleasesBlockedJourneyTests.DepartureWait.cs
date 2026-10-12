using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.StopEndedJourneyContinuesTests;
using static ControlServer.Tests.UnloadCrashResumeTests;

namespace ControlServer.Tests;

public sealed partial class RecoveryEndingReleasesBlockedJourneyTests
{
    /// <summary>
    /// 补偿掉本站在装的那一条、本站已有一条装上、还有一条待装（control-server#643 单列的子情形）：回到等录入，离站等待从放行那一轮
    /// 重新计满——与本站已无待装时（转入离站等待前置为此刻）同一个口径。
    /// </summary>
    /// <remarks>
    /// 这一条走的是 <c>resumingAfterCommit</c> 而不是 <c>resumingAfterEnding</c>：本站有一条 <c>LOADED</c>。它不是一次 LoadBatch 闭环，
    /// 但与最后一批同形的那一处（本站已无待装）对同一情形一直是置为此刻，两处取同一个口径。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationWithOneLoadedAndOneStillToLoadRestartsTheDepartureWait()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(60);
            JourneyRuntimeRow runtime = await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
            fixture.BoxCounts.Set(ThirdSublot, 4);
            await Batch7MultiDemandAdvanceTests.AddDemandToJourneyAsync(fixture, runtime, ThirdDemandId, ThirdSublot, slotBlock: 2);
            await AddInboxAsync(
                fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));
            await TickAndRunAsync(fixture);
            await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
            await TickAndRunAsync(fixture);
            await EnterSublotAsync(fixture, FirstDemandId, SecondSublot, SecondSubmissionId);
            await BlockOnLoadAsync(fixture, SecondDemandId);
            DateTimeOffset? whileBlocked = (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt;

            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            await CompensateAsync(fixture, SecondDemandId);
            await fixture.RestoreSessionReadyAsync();
            DateTimeOffset? beforeRelease = (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt;
            Exception? round = await RunRoundAsync(fixture);
            DateTimeOffset releasedAt = fixture.Clock.GetUtcNow();

            JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"start while blocked {whileBlocked:O}; before the release round {beforeRelease:O}; " +
                $"released at {releasedAt:O}; after {after.StationDepartureWaitStartedAt:O}; stage {after.Stage}");
            Assert.True(
                after.Stage == JourneyRuntimeStage.AwaitingSublot,
                $"The stop still has a demand to load. stage={after.Stage} block={after.BlockReasonCode} round={round?.Message}");
            Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
            Assert.Equal(JourneyDemandStatuses.PendingLoad, (await MembershipAsync(fixture, ThirdDemandId)).Status);
            Assert.Equal(releasedAt, after.StationDepartureWaitStartedAt);
        });
    }
}
