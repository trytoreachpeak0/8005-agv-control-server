using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.StopEndedJourneyContinuesTests;
using static ControlServer.Tests.UnloadCrashResumeTests;

namespace ControlServer.Tests;

/// <summary>
/// 放回之后车已不在站上（control-server#783）：异常处置会话把旅程从 <c>Blocked</c> 放回装卸阶段，而车在阻塞期间被人开离了站点。
/// 放回本身不读 RIoT（<c>BlockedJourneyRelease.StageAsync</c>），所以开门之前那一次位置复核是唯一挡住站外开门的东西。
/// </summary>
public sealed partial class RecoveryEndingReleasesBlockedJourneyTests
{
    private const string OffStationThirdSubmissionId = "24990000-0000-4000-8000-000000000031";

    /// <summary>
    /// 票面的场景，原样：第二个取货站上在装的一条要恢复、旅程阻塞；车在这期间被开到别处停稳，会话恢复 <c>Ready</c>；补偿之后旅程回到
    /// 等录入、重发录入请求；操作员扫本站还没装的第三条。不发装货命令，录入按 <c>STATION_MISMATCH</c> 拒收。车回到站上，再扫一次照常装。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AfterACompensationTheNextScanIsRefusedWhileTheVehicleIsAwayAndLoadsOnceItIsBack()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
            await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
            await BlockOnLoadAsync(fixture, SecondDemandId);
            RiotVehicleObservation atStop = fixture.Riot.Vehicle;
            fixture.Riot.Vehicle = atStop with { CurrentStationId = 999 };

            await CompensateAsync(fixture, SecondDemandId);
            await fixture.RestoreSessionReadyAsync();
            await RunRoundAsync(fixture);
            Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

            await SubmitAtTheCurrentStopAsync(fixture, ThirdSublot, OffStationThirdSubmissionId);
            Exception? refusedRound = await RunRoundAsync(fixture);

            JourneyRuntimeRow refused = await JourneyOfAsync(fixture, FirstDemandId);
            Assert.True(
                (await MembershipAsync(fixture, ThirdDemandId)).Status != JourneyDemandStatuses.Loading &&
                refused.BlockReasonCode == "SLOT_COMMAND_VEHICLE_NOT_AT_STOP",
                $"A load was commanded, or the stop was not named as waiting for the vehicle, while it stood away from the stop. " +
                $"third={(await MembershipAsync(fixture, ThirdDemandId)).Status} stage={refused.Stage} " +
                $"block={refused.BlockReasonCode} round={refusedRound?.Message}");
            Assert.Equal("STATION_MISMATCH", await RefusalReasonAsync(fixture, OffStationThirdSubmissionId));

            fixture.Riot.Vehicle = atStop;
            await EnterSublotAsync(fixture, FirstDemandId, ThirdSublot, ThirdSubmissionId);
            Assert.Equal(JourneyDemandStatuses.Loading, (await MembershipAsync(fixture, ThirdDemandId)).Status);
        });
    }

    /// <summary>
    /// 卸货停靠上的故障货物交接：一站两条，先卸的那一条要恢复、旅程阻塞，车在这期间被开走；交接掉它之后旅程放回等卸货结果。
    /// 另一条的卸货命令在车回到站上之前不发，回来的那一轮照常发。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AfterAHandoffTheOtherUnloadWaitsWhileTheVehicleIsAwayAndGoesOutOnceItIsBack()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await LoadBothAndArriveAtTheUnloadStopAsync(fixture);
            (string first, string second) = await UnloadOrderAsync(fixture);
            await BlockOnUnloadAsync(fixture, first);
            RiotVehicleObservation atStop = fixture.Riot.Vehicle;
            fixture.Riot.Vehicle = atStop with { CurrentStationId = 999 };

            await HandOffAsync(fixture, first, SlotOperationType.Unload);
            await fixture.RestoreSessionReadyAsync();
            Exception? awayRound = await RunRoundAsync(fixture);

            JourneyRuntimeRow away = await JourneyOfAsync(fixture, second);
            Assert.True(
                !await UnloadCommandedAsync(fixture, second) && away.BlockReasonCode == "SLOT_COMMAND_VEHICLE_NOT_AT_STOP",
                $"The other unload went out, or the stop was not named as waiting for the vehicle, while it stood away from the stop. " +
                $"stage={away.Stage} block={away.BlockReasonCode} round={awayRound?.Message}");

            fixture.Riot.Vehicle = atStop;
            await RunRoundAsync(fixture);
            Assert.True(await UnloadCommandedAsync(fixture, second), "The other unload was never commanded once the vehicle was back.");
        });
    }

    /// <summary>车载端在当前停靠此刻这一版清单上提交一条录入，与 <c>EnterSublotAsync</c> 同一个形状，但不跑引擎、不断言结果。</summary>
    private static async Task SubmitAtTheCurrentStopAsync(RuntimeFixture fixture, string sublot, string submissionId)
    {
        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        await AddInboxAsync(fixture, submissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, sublot));
    }

    /// <summary>对这一条录入的拒收所带的原因码；没有拒收时为 null。</summary>
    private static async Task<string?> RefusalReasonAsync(RuntimeFixture fixture, string submissionId)
    {
        ProtocolOutboxRow? refusal = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.MessageType == "SublotRejected" && row.PayloadJson.Contains(submissionId),
                TestContext.Current.CancellationToken);
        if (refusal is null)
        {
            return null;
        }
        using JsonDocument payload = JsonDocument.Parse(refusal.PayloadJson);
        return payload.RootElement.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString();
    }
}
