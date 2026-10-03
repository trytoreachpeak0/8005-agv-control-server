using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.WaitingPointArrivalSettlementTestKit;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

// control-server#447: the idle return's waiting-point move ends SUCCESS in RIoT, and the vehicle half of the arrival proof is
// never satisfied.
public sealed partial class IdleReturnExecutionTests
{
    public static TheoryData<string> ArrivalNeverProven => ["other-map", "other-station"];

    /// <summary>
    /// control-server#447 第 1 步的复现，修复之后的样子：RIoT 报单 <c>SUCCESS</c>，车停稳、没单，但到点证据的车辆那一半始终不满足——当前图与旅程的图
    /// 对不上（数据本身不一致），或车停在了别的站。旅程写上 <c>IDLE_RETURN_ARRIVAL_NOT_PROVEN</c>（开始时刻不随轮次变），过了重复窗口告警一次
    /// （事件 2230），两个小时也不按时间放车放点；#419 的人工释放与故障恢复入口仍然拒绝（它们没有被放宽）。出口只有到点人工收尾，见下面几条。
    /// </summary>
    [Theory]
    [MemberData(nameof(ArrivalNeverProven))]
    public async Task AnIdleReturnWhoseArrivalIsNeverProvenIsNamedAlertedOnceAndNeverReleasedByTime(string why)
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await SucceededWithoutArrivalAsync(fleet, why);
        DateTimeOffset since = (await IdleJourneyAsync(fleet, AgvA))!.BlockReasonSince!.Value;

        fleet.Clock.Advance(TimeSpan.FromHours(2));
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        JourneyRuntimeRow stuck = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingPickupArrival, IdleReturnExecutionReasons.ArrivalNotProven, since),
            (stuck.Stage, stuck.BlockReasonCode, stuck.BlockReasonSince!.Value));
        Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2230);
        Assert.Equal((VehiclePurposes.IdleReturn, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));

        fleet.Context.ChangeTracker.Clear();
        StationExclusivityManualReleaseResult release = await StationExclusivityManualRelease.ReleaseAsync(
            fleet.Context,
            new GovernanceStore(fleet.Context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default),
            fleet.Riot,
            new StationExclusivityManualReleaseRequest(Map, Near.StationId, KeyA, "OP-7", "到点证明不了", "SITE-447", "班长"),
            fleet.Clock.GetUtcNowWithoutTick(),
            Token);
        fleet.Context.ChangeTracker.Clear();
        Assert.Contains(StationExclusivityManualRelease.HolderJourneyStillBound, release.Codes);
        VehicleFaultRecoveryDecision recovery = await fleet.CreateFaultRecovery().RecoverAsync(
            new VehicleFaultRecoveryRequest(
                new EmergencyStopSubject(AgvA, KeyA), VehicleFaultRecoveryAction.ClearFault, "operator-1", true, null),
            Token);
        fleet.Context.ChangeTracker.Clear();
        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, recovery.Outcome);
    }

    /// <summary>
    /// 人说车就在等待点上（地图对不上的那种）：与引擎收敛同一套记账——预占转占用、用途占有释放（原因是人工确认的码）、旅程收尾，收尾快照撤下
    /// <c>IDLE_RETURN</c>、计划留那条 <c>ARRIVED</c> 的等待点腿；一条成功的管理员审计记下谁、依据、读到的 RIoT。车回到可派：下一条需求派给它。
    /// </summary>
    [Fact]
    public async Task AtTheWaitingPointClosesTheIdleReturnWithTheConvergingBookkeeping()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await SucceededWithoutArrivalAsync(fleet, "other-map");
        fleet.Clock.Advance(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));

        WaitingPointArrivalSettlementResult result = await SettleAsync(
            fleet, SettlementRequest(AgvA, KeyA, journey.JourneyId, Near.StationId, WaitingPointArrivalVerdicts.AtWaitingPoint));

        Assert.Equal((true, IdleReturnExecutionReasons.ArrivalConfirmedByOperator), (result.Settled, result.Ending));
        Assert.Empty(result.Codes);
        JourneyRuntimeRow closed = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.Equal(
            (JourneyRuntimeStage.Completed, IdleReturnExecutionReasons.ArrivalConfirmedByOperator), (closed.Stage, closed.BlockReasonCode));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        VehiclePurposeClaimRecordRow record = await fleet.Context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(IdleReturnExecutionReasons.ArrivalConfirmedByOperator, record.ReleaseReason);
        Assert.Equal((KeyA, StationExclusivityStates.Occupied), await HolderAsync(fleet, Near.StationId));
        Assert.Equal(
            JourneyStopStatuses.Completed,
            (await fleet.Context.Set<JourneyStopRow>().AsNoTracking().SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).Status);

        JsonElement lastState = (await PayloadsAsync(fleet, AgvA, "VehicleBusinessStateSnapshot")).Last();
        Assert.Equal(JsonValueKind.Null, lastState.GetProperty("activePurpose").ValueKind);
        JsonElement leg = Assert.Single((await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot")).Last().GetProperty("legs").EnumerateArray());
        Assert.Equal(("WAITING_POINT", "ARRIVED"), (leg.GetProperty("stopPurposeCategory").GetString(), leg.GetProperty("state").GetString()));

        AdministratorAuditRecordRow audit = Assert.Single(await AuditsAsync(fleet));
        Assert.Equal(GovernanceActionOutcome.Succeeded, audit.Outcome);
        Assert.Equal("R-11", audit.ClaimedAdministratorRole);
        Assert.Equal($"{Map}/{Near.StationId}", audit.ObjectId);
        using (JsonDocument detail = JsonDocument.Parse(audit.DetailJson!))
        {
            Assert.Equal("SETTLED", detail.RootElement.GetProperty("result").GetString());
            Assert.Equal(Operator, detail.RootElement.GetProperty("said").GetProperty("operatorId").GetString());
            Assert.Equal("map:not-the-journeys", detail.RootElement.GetProperty("riot").GetProperty("CurrentMap").GetString());
        }

        fleet.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        MoveTo(fleet, KeyA, Near.StationId);
        await RoundAsync(fleet);
        Assert.Contains(fleet.Riot.Creates, create => create.VehicleKey == KeyA && create.DestinationStationId == 12);
    }

    /// <summary>
    /// 人说车不在等待点上（停在别的站的那种）：与「单终结后证明停稳」同一套——按已确认失败收尾，用途释放，计划为空；预占不当场放，车被读到停在别的站
    /// 之后由离点清扫放。下一次承诺排除这个点，冷却照算。
    /// </summary>
    [Fact]
    public async Task NotAtTheWaitingPointEndsTheIdleReturnAsAConfirmedFailure()
    {
        await using FleetFixture fleet = await FleetAsync(points: [Near, Far]);
        JourneyRuntimeRow journey = await SucceededWithoutArrivalAsync(fleet, "other-station");
        fleet.Clock.Advance(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));

        WaitingPointArrivalSettlementResult result = await SettleAsync(
            fleet, SettlementRequest(AgvA, KeyA, journey.JourneyId, Near.StationId, WaitingPointArrivalVerdicts.NotAtWaitingPoint));

        Assert.Equal((true, IdleReturnExecutionReasons.NotAtWaitingPointByOperator), (result.Settled, result.Ending));
        JourneyRuntimeRow closed = await fleet.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(
            (JourneyRuntimeStage.Completed, IdleReturnExecutionReasons.NotAtWaitingPointByOperator), (closed.Stage, closed.BlockReasonCode));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Empty((await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot")).Last().GetProperty("legs").EnumerateArray());
        Assert.Equal(GovernanceActionOutcome.Succeeded, Assert.Single(await AuditsAsync(fleet)).Outcome);

        fleet.Clock.Advance(fleet.Options.OwnOrderRebuildDelay + TimeSpan.FromSeconds(1));
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Null(await StationAsync(fleet, Near.StationId));
        JourneyRuntimeRow next = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.NotEqual(journey.JourneyId, next.JourneyId);
        Assert.Equal(Far.StationId, next.PickupStationRiotId);
    }

    /// <summary>
    /// 每一条前提各缺一样（其余都满足）：拒绝，答那一条的码；旅程、用途占有、等待点预占一样不动；写一条失败的管理员审计，带着这些码。
    /// 离线与读不到的码在说明里叫人等车上线（调度 10-03）。
    /// </summary>
    [Theory]
    [MemberData(nameof(SettlementPremises))]
    public async Task EachUnmetPremiseRefusesTheIdleReturnSettlementChangesNothingAndIsAudited(string premise, string code)
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2);
        MoveTo(fleet, KeyB, 12);
        JourneyRuntimeRow journey = await SucceededWithoutArrivalAsync(fleet, "other-map");
        fleet.Clock.Advance(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));
        WaitingPointArrivalSettlementRequest request =
            SettlementRequest(AgvA, KeyA, journey.JourneyId, Near.StationId, WaitingPointArrivalVerdicts.AtWaitingPoint);
        RiotOrderObservation order = fleet.Riot.OrderOf(journey.PickupUpperId)!;
        switch (premise)
        {
            case "operator-missing": request = request with { OperatorId = " " }; break;
            case "not-authorized": request = request with { OperatorId = "somebody-else" }; break;
            case "reason-missing": request = request with { Reason = null }; break;
            case "site-verification-missing": request = request with { SiteVerification = "" }; break;
            case "verdict-unknown": request = request with { Verdict = "PROBABLY" }; break;
            case "field-too-long": request = request with { Reason = new string('x', WaitingPointArrivalSettlement.MaxTextLength + 1) }; break;
            case "other-vehicles-journey": request = request with { AgvId = AgvB, VehicleKey = KeyB }; break;
            case "station-mismatch": request = request with { StationId = Far.StationId }; break;
            case "too-early":
                await SetJourneyAsync(fleet, journey.JourneyId, row =>
                {
                    row.SetBlockReason(null, fleet.Clock.GetUtcNowWithoutTick());
                    row.SetBlockReason(IdleReturnExecutionReasons.ArrivalNotProven, fleet.Clock.GetUtcNowWithoutTick());
                });
                break;
            case "not-named":
                await SetJourneyAsync(fleet, journey.JourneyId, row => row.SetBlockReason(JourneyRuntimeEngine.CheckpointWaitReason, fleet.Clock.GetUtcNowWithoutTick()));
                break;
            case "journey-completed":
                await SetJourneyAsync(fleet, journey.JourneyId, row => row.Stage = JourneyRuntimeStage.Completed);
                break;
            case "waiting-point-not-held": await ReleaseByHandAsync(fleet, Near.StationId); break;
            case "order-not-terminal":
                fleet.Riot.PutOrder(order with { Kind = RiotOrderObservationKind.Active, OrderState = RiotOrderState.Executing });
                break;
            case "order-elsewhere":
                fleet.Riot.PutOrder(order with { DestinationStationId = Far.StationId });
                break;
            case "order-unreadable":
                fleet.Riot.BeforeReconcile = _ => throw new HttpRequestException("RIoT did not answer.");
                break;
            case "vehicle-unreadable": fleet.Riot.FailOn = KeyA; break;
            case "vehicle-offline": Override(fleet, seen => seen with { Connected = false }); break;
            case "vehicle-moving": Override(fleet, seen => seen with { Speed = 0.3 }); break;
            case "vehicle-busy": Override(fleet, seen => seen with { ProcState = "EXECUTING" }); break;
            case "vehicle-has-task": Override(fleet, seen => seen with { OrderTaskId = "T-9" }); break;
            case "motion-not-stopped": fleet.Riot.SafetyReasons = ["MOTION_UNKNOWN"]; break;
            case "reading-stale":
                DateTimeOffset longAgo = fleet.Clock.GetUtcNowWithoutTick().AddMinutes(-10);
                Override(fleet, seen => seen with { ObservedAt = longAgo });
                break;
            case "not-at-but-reported-at":
                request = request with { Verdict = WaitingPointArrivalVerdicts.NotAtWaitingPoint };
                break;
            case "at-but-reported-elsewhere": Override(fleet, seen => seen with { CurrentStationId = 12 }); break;
            default: throw new ArgumentOutOfRangeException(nameof(premise), premise, null);
        }

        WaitingPointArrivalSettlementResult result = await SettleAsync(fleet, request);
        fleet.Riot.BeforeReconcile = null;
        fleet.Riot.FailOn = null;

        Assert.False(result.Settled);
        Assert.Contains(code, result.Codes);
        Assert.True(WaitingPointArrivalSettlement.Descriptions.ContainsKey(code));
        JourneyRuntimeRow after = await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == journey.JourneyId, Token);
        Assert.Equal(premise == "journey-completed" ? JourneyRuntimeStage.Completed : JourneyRuntimeStage.AwaitingPickupArrival, after.Stage);
        Assert.Equal((VehiclePurposes.IdleReturn, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
        if (premise != "waiting-point-not-held")
        {
            Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        }
        AdministratorAuditRecordRow audit = Assert.Single(await AuditsAsync(fleet));
        Assert.Equal(GovernanceActionOutcome.Failed, audit.Outcome);
        using JsonDocument detail = JsonDocument.Parse(audit.DetailJson!);
        Assert.Equal("REJECTED", detail.RootElement.GetProperty("result").GetString());
        Assert.Contains(code, detail.RootElement.GetProperty("codes").EnumerateArray().Select(item => item.GetString()));
    }

    public static TheoryData<string, string> SettlementPremises => new()
    {
        { "operator-missing", WaitingPointArrivalSettlement.OperatorRequired },
        { "not-authorized", WaitingPointArrivalSettlement.NotAuthorized },
        { "reason-missing", WaitingPointArrivalSettlement.ReasonRequired },
        { "site-verification-missing", WaitingPointArrivalSettlement.SiteVerificationRequired },
        { "verdict-unknown", WaitingPointArrivalSettlement.VerdictUnknown },
        { "field-too-long", WaitingPointArrivalSettlement.FieldTooLong },
        { "other-vehicles-journey", WaitingPointArrivalSettlement.JourneyNotFound },
        { "station-mismatch", WaitingPointArrivalSettlement.StationMismatch },
        { "too-early", WaitingPointArrivalSettlement.TooEarly },
        { "not-named", WaitingPointArrivalSettlement.ArrivalNotNamed },
        { "journey-completed", WaitingPointArrivalSettlement.JourneyCompleted },
        { "waiting-point-not-held", WaitingPointArrivalSettlement.WaitingPointNotHeld },
        { "order-not-terminal", WaitingPointArrivalSettlement.OrderNotExactSuccess },
        { "order-elsewhere", WaitingPointArrivalSettlement.OrderNotExactSuccess },
        { "order-unreadable", WaitingPointArrivalSettlement.OrderUnreadable },
        { "vehicle-unreadable", WaitingPointArrivalSettlement.VehicleUnreadable },
        { "vehicle-offline", WaitingPointArrivalSettlement.VehicleOffline },
        { "vehicle-moving", WaitingPointArrivalSettlement.VehicleNotProvenStopped },
        { "vehicle-busy", WaitingPointArrivalSettlement.VehicleNotProvenStopped },
        { "vehicle-has-task", WaitingPointArrivalSettlement.VehicleNotProvenStopped },
        { "motion-not-stopped", WaitingPointArrivalSettlement.VehicleNotProvenStopped },
        { "reading-stale", WaitingPointArrivalSettlement.VehicleReadingStale },
        { "not-at-but-reported-at", WaitingPointArrivalSettlement.VehicleReportedAtWaitingPoint },
        { "at-but-reported-elsewhere", WaitingPointArrivalSettlement.VehicleReportedElsewhere },
    };

    /// <summary>
    /// RIoT 在任何事务之外读（control-server#452 的教训）；读的那一刻引擎推进了这趟旅程（版本变了）：事务里按读到的版本核对，核不上就拒绝，
    /// 什么也不写，答 <c>ARRIVAL_SETTLEMENT_STATE_CHANGED</c>，并照样留一条失败审计。
    /// </summary>
    [Fact]
    public async Task RiotIsReadOutsideTheWriteLockAndAJourneyAdvancedMeanwhileIsRefused()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await SucceededWithoutArrivalAsync(fleet, "other-map");
        fleet.Clock.Advance(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));
        List<bool> inTransaction = [];
        Override(fleet, seen =>
        {
            inTransaction.Add(fleet.Context.Database.CurrentTransaction is not null);
            using ControlServerDbContext other = fleet.NewContext();
            JourneyRuntimeRow row = other.JourneyRuntimes.Single(item => item.JourneyId == journey.JourneyId);
            row.UpdatedAt = row.UpdatedAt.AddMilliseconds(1);
            other.SaveChanges();
            return seen;
        });

        WaitingPointArrivalSettlementResult result = await SettleAsync(
            fleet, SettlementRequest(AgvA, KeyA, journey.JourneyId, Near.StationId, WaitingPointArrivalVerdicts.AtWaitingPoint));

        Assert.Equal([false], inTransaction);
        Assert.False(result.Settled);
        Assert.Equal([WaitingPointArrivalSettlement.StateChanged], result.Codes);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await IdleJourneyAsync(fleet, AgvA))!.Stage);
        Assert.Equal((VehiclePurposes.IdleReturn, journey.JourneyId), await ClaimOfAsync(fleet, KeyA));
        Assert.Equal((KeyA, StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
        Assert.Equal(GovernanceActionOutcome.Failed, Assert.Single(await AuditsAsync(fleet)).Outcome);
    }

    /// <summary>
    /// 第二次办同一趟：第一次已收尾，第二次答 <c>ARRIVAL_SETTLEMENT_JOURNEY_COMPLETED</c>，不再写第二次收尾，审计两条（一成一败）。
    /// </summary>
    [Fact]
    public async Task ASecondSettlementOfTheSameJourneyChangesNothing()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await SucceededWithoutArrivalAsync(fleet, "other-map");
        fleet.Clock.Advance(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));
        WaitingPointArrivalSettlementRequest request =
            SettlementRequest(AgvA, KeyA, journey.JourneyId, Near.StationId, WaitingPointArrivalVerdicts.AtWaitingPoint);
        Assert.True((await SettleAsync(fleet, request)).Settled);

        WaitingPointArrivalSettlementResult again = await SettleAsync(fleet, request);

        Assert.Equal([WaitingPointArrivalSettlement.JourneyCompleted], again.Codes);
        Assert.Single(await fleet.Context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
            .Where(row => row.JourneyId == journey.JourneyId).ToArrayAsync(Token));
        Assert.Equal(
            [GovernanceActionOutcome.Succeeded, GovernanceActionOutcome.Failed],
            (await AuditsAsync(fleet)).Select(row => row.Outcome));
    }

    /// <summary>
    /// 入口的每一个拒绝码都有给现场人员看的中文说明（按反射核，新加一个码而忘了说明就红）；离线与读不到的说明叫人等车上线（调度 10-03）。
    /// </summary>
    [Fact]
    public void EverySettlementRefusalCodeHasAChineseDescriptionAndTheOfflineOnesSayWaitForTheVehicle()
    {
        string[] codes =
        [
            .. typeof(WaitingPointArrivalSettlement)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
                .Where(value => value.StartsWith("ARRIVAL_SETTLEMENT_", StringComparison.Ordinal)),
        ];
        Assert.Equal(23, codes.Length);
        Assert.Equal(codes.Order(StringComparer.Ordinal), WaitingPointArrivalSettlement.Descriptions.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("重新上线", WaitingPointArrivalSettlement.Descriptions[WaitingPointArrivalSettlement.VehicleOffline], StringComparison.Ordinal);
        Assert.Contains("读到车", WaitingPointArrivalSettlement.Descriptions[WaitingPointArrivalSettlement.VehicleUnreadable], StringComparison.Ordinal);
    }

    /// <summary>
    /// Host 入口的边界（判定是上面几条的事）：故障恢复那把凭据，不对答 401、什么都不写；没点名车、旅程或站答 422、不写审计；不是本服务端的车答 404；
    /// 拒绝答 409，列出全部码与每个码的中文说明，并写失败审计；办成答 200，带收尾码与审计号。
    /// </summary>
    [Fact]
    public async Task TheHostEntryAuthenticatesNamesTheVehicleAndMapsTheDecisionOntoHttp()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await SucceededWithoutArrivalAsync(fleet, "other-map");
        fleet.Clock.Advance(fleet.Options.OwnOrderRebuildRepeatWindow + TimeSpan.FromSeconds(1));
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "settlement-credential");
        try
        {
            WaitingPointArrivalSettlementHttpRequest request = new(
                AgvA, journey.JourneyId, Near.StationId, WaitingPointArrivalVerdicts.AtWaitingPoint, Operator, "地图对不上", "SITE-447-02");

            Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>(
                (await PostSettlementAsync(fleet, variable, "Bearer wrong", request)).Result);
            Assert.Equal(422, ProblemOf(await PostSettlementAsync(fleet, variable, "Bearer settlement-credential", request with { JourneyId = " " })).StatusCode);
            Assert.Equal(404, ProblemOf(await PostSettlementAsync(fleet, variable, "Bearer settlement-credential", request with { AgvId = "agv-nobody" })).StatusCode);
            Assert.Empty(await AuditsAsync(fleet));

            var refused = ProblemOf(await PostSettlementAsync(
                fleet, variable, "Bearer settlement-credential", request with { OperatorId = "somebody-else", SiteVerification = null }));
            Assert.Equal(409, refused.StatusCode);
            Assert.Equal(
                [WaitingPointArrivalSettlement.SiteVerificationRequired, WaitingPointArrivalSettlement.NotAuthorized],
                Assert.IsAssignableFrom<IReadOnlyList<string>>(refused.ProblemDetails.Extensions["codes"]));
            IReadOnlyDictionary<string, string> descriptions =
                Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(refused.ProblemDetails.Extensions["descriptions"]);
            Assert.Equal(
                WaitingPointArrivalSettlement.Descriptions[WaitingPointArrivalSettlement.NotAuthorized],
                descriptions[WaitingPointArrivalSettlement.NotAuthorized]);
            Assert.Equal(GovernanceActionOutcome.Failed, Assert.Single(await AuditsAsync(fleet)).Outcome);

            var settled = await PostSettlementAsync(fleet, variable, "Bearer settlement-credential", request);
            WaitingPointArrivalSettlementResponse body =
                Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<WaitingPointArrivalSettlementResponse>>(settled.Result).Value!;
            Assert.Equal(("SETTLED", IdleReturnExecutionReasons.ArrivalConfirmedByOperator), (body.Outcome, body.Ending));
            Assert.Equal(body.AuditRecordId, (await AuditsAsync(fleet))[^1].AuditRecordId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult ProblemOf(
        Microsoft.AspNetCore.Http.HttpResults.Results<
            Microsoft.AspNetCore.Http.HttpResults.Ok<WaitingPointArrivalSettlementResponse>,
            Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult,
            Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult> result) =>
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(result.Result);

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.Results<
        Microsoft.AspNetCore.Http.HttpResults.Ok<WaitingPointArrivalSettlementResponse>,
        Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult,
        Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>> PostSettlementAsync(
        FleetFixture fleet, string credentialVariable, string authorization, WaitingPointArrivalSettlementHttpRequest request)
    {
        Microsoft.AspNetCore.Http.DefaultHttpContext http = new();
        http.Request.Headers.Authorization = authorization;
        fleet.Context.ChangeTracker.Clear();
        var result = await WaitingPointArrivalSettlementEndpoints.HandleAsync(
            http,
            request,
            Settlement(fleet),
            new ControlServer.Host.Runtime.Fleet.VehicleRoster(Microsoft.Extensions.Options.Options.Create(fleet.Options)),
            Microsoft.Extensions.Options.Options.Create(new VehicleFaultRecoveryOptions { Enabled = true, CredentialEnvironmentVariable = credentialVariable }),
            Token);
        fleet.Context.ChangeTracker.Clear();
        return result;
    }

    /// <summary>建单、确认，RIoT 报单 SUCCESS，车停稳没单但到点证据的车辆那一半不满足；跑一轮，引擎写上到点证明不了的码。</summary>
    private static async Task<JourneyRuntimeRow> SucceededWithoutArrivalAsync(FleetFixture fleet, string why)
    {
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.CompleteOrder(journey.PickupUpperId);
        Override(fleet, seen => seen);
        if (why == "other-station")
        {
            MoveTo(fleet, KeyA, 12);
        }
        await RoundAsync(fleet);
        Assert.Equal(IdleReturnExecutionReasons.ArrivalNotProven, (await IdleJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        return journey;
    }

    /// <summary>车 A 的读数：停在近的等待点上，但当前图与旅程的图对不上；再按 <paramref name="change"/> 改。</summary>
    private static void Override(FleetFixture fleet, Func<RiotVehicleObservation, RiotVehicleObservation> change) =>
        fleet.Riot.VehicleOverrides[KeyA] = seen => change(seen with { CurrentStationId = Near.StationId, CurrentMap = "map:not-the-journeys" });

    private static async Task SetJourneyAsync(FleetFixture fleet, string journeyId, Action<JourneyRuntimeRow> change)
    {
        await using ControlServerDbContext other = fleet.NewContext();
        JourneyRuntimeRow row = await other.JourneyRuntimes.SingleAsync(item => item.JourneyId == journeyId, Token);
        change(row);
        await other.SaveChangesAsync(Token);
    }
}
