using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图「空闲返回」的数据面（批次8-21，control-server#392；REQ-0290～0297）：每台车的空闲返回走到哪一步、失败按 REQ-0296 的哪一支、
/// 等待点占着没有、最近一趟空闲返回怎么收的尾。
/// </summary>
/// <remarks>
/// <para>
/// <b>步骤只从落库的事实读</b>：用途占有（<c>VehiclePurposeClaims</c>）、空闲返回旅程行与它的阻断码、它的订单意图状态、等待点独占。
/// 引擎写什么这里就读什么，不重判资格、不重跑到点证据。
/// </para>
/// <para>
/// <b>「到点待收敛」并在「在途」里</b>：RIoT 报单成功、车还没证明停稳时，引擎不落任何与在途不同的值（意图仍是 <c>CONFIRMED</c>，
/// 阻断码清空或是检查点等待），看板分不出这两者，也不另算。
/// </para>
/// <para>
/// 「在点」的车写「等待点占用直到离点证据满足」，不写「空闲」二字：车停在等待点上不接空闲返回，但等待点一直归它。
/// </para>
/// </remarks>
internal sealed class IdleReturnsQueryEndpoint : IDashboardQueryEndpoint
{
    internal const string StepNone = "NONE";
    internal const string StepOtherPurpose = "OTHER_PURPOSE";
    internal const string StepCommitted = "COMMITTED";
    internal const string StepCreateResultUnknown = "CREATE_RESULT_UNKNOWN";
    internal const string StepEnRoute = "EN_ROUTE";
    internal const string StepOrderStalled = "ORDER_STALLED";
    internal const string StepHeldAwaitingStop = "HELD_AWAITING_STOP";
    internal const string StepFailedAwaitingManual = "FAILED_AWAITING_MANUAL";
    internal const string StepAdvanceFailed = "ADVANCE_FAILED";
    internal const string StepAtPoint = "AT_POINT";

    /// <summary>REQ-0296 的三支。</summary>
    internal const string BranchReleaseAndReevaluate = "RELEASE_AND_REEVALUATE";
    internal const string BranchHoldAndReconcile = "HOLD_AND_RECONCILE";
    internal const string BranchConfirmedFailure = "CONFIRMED_FAILURE";

    internal static IReadOnlyDictionary<string, string> StepDescriptions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StepNone] = "没有在进行的空闲返回，也不占等待点",
            [StepOtherPurpose] = "车正被别的用途占着（见「车辆用途」卡片），不在空闲返回",
            [StepCommitted] = "已承诺：用途与等待点预占已取得，开往等待点的单还没建出去（等物化、等出发安全检查或等建单开关）",
            [StepCreateResultUnknown] = "建单结果未知：开往等待点的单发出后还没确认，服务端按同一个单号对账，不建第二张，车、等待点与用途都保持",
            [StepEnRoute] = "在途：开往等待点的单已确认。车到点、停稳并证明之后才收敛（已到点、还在等到点证据收敛的车也显示在这一步，服务端没有分开记）",
            [StepOrderStalled] = "单停住了：开往等待点的单在 RIoT 上挂起或处于未识别状态，用途与等待点预占保持，等人处理",
            [StepHeldAwaitingStop] = "保持中：单已在 RIoT 终结或等待点已不归它，服务端在等车证明停稳、身上没有单，才结束这趟；期间用途与等待点都不放",
            [StepFailedAwaitingManual] = "失败待人工：单失败或行驶中门锁出问题，车已判为疑似故障，用途与等待点预占保持，要现场人员经故障清除入口处理",
            [StepAdvanceFailed] = "推进出错：服务端推进这趟空闲返回时出错，它停在原处、一步没动，每一轮重试",
            [StepAtPoint] = "在点：等待点占用直到离点证据满足（车停在等待点上，这个点一直归它，车离开之后才放）",
        };

    internal static IReadOnlyDictionary<string, string> BranchDescriptions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BranchReleaseAndReevaluate] = "REQ-0296 释放重评一支：还没建过单、车没动，用途与等待点已释放，车回到重新评估",
            [BranchHoldAndReconcile] = "REQ-0296 保持对账一支：单可能存在或车可能在动，原承诺与等待点保持，按订单规则对账、告警，必要时转人工，不另选等待点",
            [BranchConfirmedFailure] = "REQ-0296 已确认失败一支：车已证明停稳、没有单，用途已释放；下一次空闲返回排除原失败点，并受冷却与停止两道护栏约束",
        };

    private static readonly HashSet<string> FailedAwaitingManualCodes = new(StringComparer.Ordinal)
    {
        VehicleFaultEvidence.OrderFailed,
        VehicleFaultEvidence.DoorNotProvenLocked,
        JourneyRuntimeEngine.HeldOrderResumedWithoutContinueReason,
    };

    private static readonly HashSet<string> HeldAwaitingStopCodes = new(StringComparer.Ordinal)
    {
        IdleReturnExecutionReasons.OrderEndedStopNotProven,
        IdleReturnExecutionReasons.WaitingPointLostOrderInFlight,
    };

    private static readonly HashSet<string> OrderStalledCodes = new(StringComparer.Ordinal)
    {
        JourneyRuntimeEngine.OrderHangReason,
        JourneyRuntimeEngine.OrderStateUnrecognizedReason,
    };

    private static readonly HashSet<string> ReleasedBeforeCreateCodes = new(StringComparer.Ordinal)
    {
        IdleReturnExecutionReasons.CommitmentOrphaned,
        IdleReturnExecutionReasons.WaitingPointNoLongerEligible,
        IdleReturnExecutionReasons.WaitingPointLost,
    };

    private readonly VehicleRoster _roster;
    private readonly TimeProvider _clock;

    public IdleReturnsQueryEndpoint()
        : this(new VehicleRoster(Options.Create(new JourneyRuntimeOptions())), TimeProvider.System)
    {
    }

    [ActivatorUtilitiesConstructor]
    public IdleReturnsQueryEndpoint(VehicleRoster roster)
        : this(roster, TimeProvider.System)
    {
    }

    internal IdleReturnsQueryEndpoint(VehicleRoster roster, TimeProvider clock)
    {
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "idle-returns";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        DashboardFleetContact contact =
            await DashboardFleetContact.ReadAsync(dbContext, _roster, _clock.GetUtcNow(), cancellationToken);
        FleetVehicle[] inContact = [.. contact.Vehicles.Where(contact.InContact)];
        Dictionary<string, VehiclePurposeClaimRow> claims = await VehiclePurposeFacts.ReadAsync(dbContext, cancellationToken);
        StationExclusivityRow[] waitingPoints =
            await StationHoldings.ReadAsync(dbContext, StationExclusivityKinds.WaitingPoint, cancellationToken);

        string[] agvIds = [.. inContact.Select(vehicle => vehicle.AgvId)];
        // 每辆车的全部旅程（只取几列）：开着的空闲返回在其中，最近一趟收尾也在其中。SQLite 不能按 DateTimeOffset 排序，在内存里排。
        JourneyRuntimeRow[] journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => agvIds.Contains(row.AgvId))
            .ToArrayAsync(cancellationToken);
        string[] upperIds = [.. journeys.Where(row => row.IsIdleReturn()).Select(row => row.PickupUpperId).OfType<string>()];
        Dictionary<string, OrderIntentRow> intents = await dbContext.OrderIntents.AsNoTracking()
            .Where(row => upperIds.Contains(row.UpperId))
            .ToDictionaryAsync(row => row.UpperId, StringComparer.Ordinal, cancellationToken);
        string[] idleJourneyIds = [.. journeys.Where(row => row.IsIdleReturn()).Select(row => row.JourneyId)];
        Dictionary<string, DateTimeOffset?> releasedAt = (await dbContext.Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
                .Where(record => idleJourneyIds.Contains(record.JourneyId))
                .Select(record => new { record.JourneyId, record.ReleasedAt })
                .ToArrayAsync(cancellationToken))
            .GroupBy(record => record.JourneyId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(record => record.ReleasedAt), StringComparer.Ordinal);

        return new
        {
            vehicles = inContact.Select(vehicle => Fact(
                    vehicle,
                    claims.GetValueOrDefault(vehicle.VehicleKey),
                    [.. journeys.Where(row => row.AgvId == vehicle.AgvId)],
                    intents,
                    releasedAt,
                    waitingPoints.FirstOrDefault(row => row.VehicleKey == vehicle.VehicleKey),
                    contact))
                .ToArray(),
            unavailableVehicles = contact.Unavailable(),
        };
    }

    private static object Fact(
        FleetVehicle vehicle,
        VehiclePurposeClaimRow? claim,
        JourneyRuntimeRow[] journeys,
        Dictionary<string, OrderIntentRow> intents,
        Dictionary<string, DateTimeOffset?> releasedAt,
        StationExclusivityRow? waitingPoint,
        DashboardFleetContact contact)
    {
        JourneyRuntimeRow? open = journeys.SingleOrDefault(row => row.IsIdleReturn() && row.Stage != JourneyRuntimeStage.Completed);
        OrderIntentRow? intent = open?.PickupUpperId is { } upperId ? intents.GetValueOrDefault(upperId) : null;
        string step = Classify(claim, open, intent?.Status, intent?.OrderId, waitingPoint);
        string? code = open?.BlockReasonCode;
        JourneyRuntimeRow? latest = journeys
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.JourneyId, StringComparer.Ordinal)
            .FirstOrDefault();
        // 最近一趟旅程正是一趟带收尾码收了尾的空闲返回时才给：之后若做过别的旅程，它就只是历史，不说明此刻。
        JourneyRuntimeRow? lastEnded = latest is not null && latest.IsIdleReturn() &&
                                       latest.Stage == JourneyRuntimeStage.Completed && latest.BlockReasonCode is not null
            ? latest
            : null;

        return new
        {
            agvId = vehicle.AgvId,
            vehicleKey = vehicle.VehicleKey,
            step,
            stepDescription = StepDescriptions[step],
            purpose = claim?.Purpose,
            journeyId = open?.JourneyId ?? (claim?.Purpose == VehiclePurposes.IdleReturn ? claim.JourneyId : null),
            stationId = open?.PickupStationRiotId ?? (claim?.Purpose == VehiclePurposes.IdleReturn ? waitingPoint?.StationId : null),
            stationName = open?.PickupStationId,
            reasonCode = code,
            reasonDescription = IdleReturnCodeDescriptions.DescribeJourneyCode(code),
            reasonSince = code is null ? null : open?.BlockReasonSince,
            intentStatus = intent?.Status,
            failureBranch = BranchOf(step),
            failureBranchDescription = BranchOf(step) is { } branch ? BranchDescriptions[branch] : null,
            waitingPoint = waitingPoint is null
                ? null
                : new
                {
                    mapId = waitingPoint.MapId,
                    stationId = waitingPoint.StationId,
                    holding = StationHoldings.Project(waitingPoint, contact),
                },
            lastEnded = lastEnded is null
                ? null
                : new
                {
                    journeyId = lastEnded.JourneyId,
                    stationId = lastEnded.PickupStationRiotId,
                    reasonCode = lastEnded.BlockReasonCode,
                    reasonDescription = IdleReturnCodeDescriptions.DescribeJourneyCode(lastEnded.BlockReasonCode),
                    endedAt = releasedAt.GetValueOrDefault(lastEnded.JourneyId),
                    failureBranch = EndBranchOf(lastEnded.BlockReasonCode!),
                    failureBranchDescription = EndBranchOf(lastEnded.BlockReasonCode!) is { } ended
                        ? BranchDescriptions[ended]
                        : null,
                },
        };
    }

    /// <summary>
    /// 这辆车的空闲返回此刻在哪一步。只看落库的值：用途占有、开着的空闲返回旅程的阻断码、它的订单意图状态与单号、它占着的等待点。
    /// </summary>
    internal static string Classify(
        VehiclePurposeClaimRow? claim,
        JourneyRuntimeRow? openIdleReturn,
        string? intentStatus,
        string? orderId,
        StationExclusivityRow? waitingPoint)
    {
        if (openIdleReturn is null)
        {
            return claim is null ? (waitingPoint is null ? StepNone : StepAtPoint)
                : claim.Purpose == VehiclePurposes.IdleReturn ? StepCommitted
                : StepOtherPurpose;
        }

        string? code = openIdleReturn.BlockReasonCode;
        if (code is not null)
        {
            if (FailedAwaitingManualCodes.Contains(code))
            {
                return StepFailedAwaitingManual;
            }
            if (HeldAwaitingStopCodes.Contains(code))
            {
                return StepHeldAwaitingStop;
            }
            if (OrderStalledCodes.Contains(code))
            {
                return StepOrderStalled;
            }
            if (code == JourneyRuntimeEngine.AdvanceFailedReason)
            {
                return StepAdvanceFailed;
            }
            if (code == IdleReturnExecutionReasons.LegOutcomeCode(MovementDispatchOutcome.ResultUnknown))
            {
                return StepCreateResultUnknown;
            }
        }

        if (intentStatus is not null && IdleReturnEvaluator.UnknownOutcomeIntentStatuses.Contains(intentStatus))
        {
            return StepCreateResultUnknown;
        }
        return intentStatus == "CONFIRMED" && !string.IsNullOrWhiteSpace(orderId) ? StepEnRoute : StepCommitted;
    }

    private static string? BranchOf(string step) => step switch
    {
        StepCreateResultUnknown or StepOrderStalled or StepHeldAwaitingStop or StepFailedAwaitingManual => BranchHoldAndReconcile,
        _ => null,
    };

    private static string? EndBranchOf(string endCode) =>
        IdleReturnExecutionReasons.ConfirmedFailures.Contains(endCode) ? BranchConfirmedFailure
        : ReleasedBeforeCreateCodes.Contains(endCode) ? BranchReleaseAndReevaluate
        : null;
}
