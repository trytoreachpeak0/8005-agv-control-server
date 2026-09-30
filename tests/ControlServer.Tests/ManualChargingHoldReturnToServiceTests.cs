using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.ChargingAllocationTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 「充电后返回服务」对服务端持有的人工充电等待重评资格（批次9-06，control-server#404；<c>CV-MANUAL-CHARGING-RETURN</c> 的
/// <c>REEVALUATE_ELIGIBILITY_AFTER_RETURN</c>、<c>REQUIRE_VERIFIED_ADMINISTRATOR</c>）：受理时同一次保存清掉这辆车的等待；拒绝时等待保持；
/// 同一个 <c>requestId</c> 重放返回同一决定、不重复清。没有等待的车，决定与本票之前逐字相同（那一面由
/// <c>OnboardMessageProcessorTests</c> 的三条既有用例守着）。
/// </summary>
public sealed class ManualChargingHoldReturnToServiceTests
{
    private const string RequestId = "00000000-0000-4000-8000-0000000000e1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string AgvA => FleetFixture.AgvIds[0];
    private static string KeyA => FleetFixture.VehicleKeys[0];

    /// <summary>受理（角色合规、会话不是 RecoveryRequired）：等待的当前行没了，它的经过写上解除时刻与这个请求 id。</summary>
    [Fact]
    public async Task AnAcceptedReturnLiftsTheServersHoldAndRecordsWhichRequestDidIt()
    {
        await using FleetFixture fleet = await FleetAsync(roster: false);
        await PlaceAsync(fleet, "hold-1");

        ManualChargingReturnToServiceDecision decision = await ReturnToServiceAsync(fleet, AgvA, KeyA, RequestId);

        Assert.Equal(ManualChargingReturnToServiceDecision.ReturnedToEligibilityEvaluation, decision.Outcome);
        Assert.Empty(await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().ToArrayAsync(Token));
        ManualChargingHoldRecordRow record = await fleet.Context.Set<ManualChargingHoldRecordRow>().AsNoTracking().SingleAsync(Token);
        Assert.Equal(("hold-1", RequestId), (record.HoldId, record.ReleaseRequestId));
        Assert.NotNull(record.ReleasedAt);
    }

    /// <summary>
    /// 同一个 <c>requestId</c> 重放：返回同一决定，不重复清——重放之前这辆车又被置了一次等待，那一次原样留着。
    /// </summary>
    [Fact]
    public async Task TheSameRequestReplayedReturnsTheSameDecisionAndLiftsNothingAgain()
    {
        await using FleetFixture fleet = await FleetAsync(roster: false);
        await PlaceAsync(fleet, "hold-1");
        ManualChargingReturnToServiceDecision first = await ReturnToServiceAsync(fleet, AgvA, KeyA, RequestId);
        await PlaceAsync(fleet, "hold-2");

        ManualChargingReturnToServiceDecision replay = await ReturnToServiceAsync(fleet, AgvA, KeyA, RequestId);

        Assert.Equal(first, replay);
        Assert.Equal("hold-2", (await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().SingleAsync(Token)).HoldId);
        ManualChargingHoldRecordRow[] records = await fleet.Context.Set<ManualChargingHoldRecordRow>().AsNoTracking().ToArrayAsync(Token);
        Assert.Equal(RequestId, records.Single(record => record.HoldId == "hold-1").ReleaseRequestId);
        Assert.Null(records.Single(record => record.HoldId == "hold-2").ReleasedAt);
        Assert.Single(await fleet.Context.ManualChargingReturnToServiceRequests.AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>拒绝的两种情形照旧，等待保持：角色不在允许的两值之内；会话处于 RecoveryRequired。</summary>
    [Theory]
    [InlineData("role-not-allowed", "PROTOCOL_SCHEMA_INVALID")]
    [InlineData("session-recovery-required", "SESSION_RECOVERY_REQUIRED")]
    public async Task ARejectedReturnLeavesTheHoldInPlace(string why, string expectedProblem)
    {
        await using FleetFixture fleet = await FleetAsync(roster: false);
        await PlaceAsync(fleet, "hold-1");
        if (why == "session-recovery-required")
        {
            await fleet.DropSessionAsync(AgvA);
        }

        ManualChargingReturnToServiceDecision decision = await ReturnToServiceAsync(
            fleet, AgvA, KeyA, RequestId, why == "role-not-allowed" ? "OPERATOR" : "SYSTEM_ADMINISTRATOR");

        Assert.Equal((ManualChargingReturnToServiceDecision.Rejected, expectedProblem), (decision.Outcome, decision.ProblemReasonCode));
        Assert.Equal("hold-1", (await fleet.Context.Set<ManualChargingHoldRow>().AsNoTracking().SingleAsync(Token)).HoldId);
        Assert.Null((await fleet.Context.Set<ManualChargingHoldRecordRow>().AsNoTracking().SingleAsync(Token)).ReleasedAt);
    }

    /// <summary>没有等待的车：照常受理，什么等待也不凭空产生或被记下解除。</summary>
    [Fact]
    public async Task AVehicleWithoutAHoldIsDecidedAsBeforeAndNothingIsTouched()
    {
        await using FleetFixture fleet = await FleetAsync(roster: false);

        ManualChargingReturnToServiceDecision decision = await ReturnToServiceAsync(fleet, AgvA, KeyA, RequestId);

        Assert.Equal(
            (ManualChargingReturnToServiceDecision.ReturnedToEligibilityEvaluation, (string?)null),
            (decision.Outcome, decision.ProblemReasonCode));
        Assert.Empty(await fleet.Context.Set<ManualChargingHoldRecordRow>().AsNoTracking().ToArrayAsync(Token));
    }

    private static async Task PlaceAsync(FleetFixture fleet, string holdId)
    {
        Assert.Equal(
            ManualChargingHoldPlacement.Placed,
            await new ManualChargingHoldStore(fleet.Context).PlaceAsync(
                holdId, KeyA, ManualChargingHoldReasons.RosterEmpty, fleet.Clock.GetUtcNowWithoutTick(), Token));
        fleet.Context.ChangeTracker.Clear();
    }
}
