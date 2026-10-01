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
/// 等待点占着没有、最近一趟空闲返回怎么收的尾，以及评估器最近一轮已完成的评估对它的结论（不能返回的原因、冷却、停止）。
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
/// <b>评估结论读结论板</b>（<see cref="IdleReturnVerdictBoard.LatestCompletedPass"/>，宿主单例）：冷却与停止两种结论只在那里，不在库里。
/// 只取最近一轮已完成的评估；车不在其中（在途、读 RIoT 失败、预算用尽）或服务启动以来还没完成过一轮时，写没评估，不显示更早的结论。
/// 那一轮走完已超过 <see cref="PassLivenessPollIntervals"/> 个轮询间隔时，评估没在跑（全车队没有空闲车时派车轮不跑），每辆车都写没评估。
/// 这只管结论这一格；步骤、等待点与上一趟收尾读的是库里的落库事实，照常给。
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
            [StepAtPoint] = "在点：服务端记录这个等待点被它占用（等待点占用直到离点证据满足才放）",
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

    /// <summary>车不在最近一轮已完成的评估里时，结论那一格写的话。不显示它更早的结论（REQ-0269）。</summary>
    internal const string NotEvaluatedThisPass =
        "本轮没有评估这辆车：最近一轮空闲返回评估没有交到它（车在途、读 RIoT 失败或这一轮时间用尽时都是这样），这里不显示它更早的结论";

    /// <summary>服务启动以来还没有完成过一轮评估时，结论那一格写的话。</summary>
    internal const string NoPassCompletedYet =
        "本轮没有评估这辆车：服务启动以来还没有完成过一轮空闲返回评估";

    /// <summary>
    /// 评估在不在跑的时效窗口是几个引擎轮询间隔（<c>JourneyRuntime:PollInterval</c>）。最近一轮已完成的评估走完超过这么久，
    /// 就是评估没在跑：全车队没有空闲车时派车轮不跑（引擎在派车之前退出），MesIngest 读不到时派车轮只做充电分配就返回
    /// （<c>DispatchRoundRunner</c> 读目录失败那一支），引擎这一轮出错，或一轮本身跑得比窗口还慢（例如 RIoT 应答慢）。
    /// </summary>
    /// <remarks>
    /// 判的是「评估还在不在跑」，不是重算结论：窗口内照写评估器的结论，窗口外一律不写结论。与会话存活（<c>SessionLiveness.Timeout</c>，
    /// 心跳两秒、六秒超时）同一个形状。
    /// </remarks>
    internal const int PassLivenessPollIntervals = 3;

    private readonly VehicleRoster _roster;
    private readonly IdleReturnVerdictBoard _board;
    private readonly TimeSpan _passLiveness;
    private readonly TimeProvider _clock;

    public IdleReturnsQueryEndpoint()
        : this(Options.Create(new JourneyRuntimeOptions()), new IdleReturnVerdictBoard(), TimeProvider.System)
    {
    }

    /// <summary>
    /// 挂在宿主上时用这一个：名册从宿主的同一份配置建，结论板是评估器写的那一块单例。结论板是必填的：宿主漏注册时看板查询
    /// 在启动时就构造失败，而不是静默成「从没评估过」。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public IdleReturnsQueryEndpoint(IOptions<JourneyRuntimeOptions> options, IdleReturnVerdictBoard board)
        : this(options, board, TimeProvider.System)
    {
    }

    internal IdleReturnsQueryEndpoint(IOptions<JourneyRuntimeOptions> options, IdleReturnVerdictBoard board, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _roster = new VehicleRoster(options);
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _passLiveness = options.Value.PollInterval * PassLivenessPollIntervals;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>超出时效窗口时，结论那一格写的话（窗口按实际配置的轮询间隔算）。</summary>
    internal static string PassNotRunning(TimeSpan window) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"本轮没有评估这辆车：最近 {window.TotalSeconds:0.###} 秒内没有完成过一轮空闲返回评估。")
        + "可能的原因：全车队没有空闲车（派车轮不跑）、MesIngest 读不到（这一轮不派车）、引擎这一轮出错、一轮跑得太慢（例如 RIoT 应答慢）。"
        + "这里不显示更早的结论";

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "idle-returns";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        DateTimeOffset now = _clock.GetUtcNow();
        DashboardFleetContact contact = await DashboardFleetContact.ReadAsync(dbContext, _roster, now, cancellationToken);
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

        IdleReturnBoardPass? pass = _board.LatestCompletedPass;
        // 窗口外的一轮不当作这一轮：它说的是评估停下之前的事。
        string? notRunning = pass is not null && now - pass.CompletedAt > _passLiveness ? PassNotRunning(_passLiveness) : null;
        return new
        {
            vehicles = inContact.Select(vehicle => Fact(
                    vehicle,
                    claims.GetValueOrDefault(vehicle.VehicleKey),
                    [.. journeys.Where(row => row.AgvId == vehicle.AgvId)],
                    intents,
                    releasedAt,
                    waitingPoints.FirstOrDefault(row => row.VehicleKey == vehicle.VehicleKey),
                    notRunning is null ? pass : null,
                    notRunning,
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
        IdleReturnBoardPass? pass,
        string? notRunning,
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
            verdict = Verdict(vehicle, pass, notRunning),
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
    /// 评估器对这辆车的结论（资格、冷却、停止……），只取最近一轮<b>已完成</b>的评估：车不在其中时说没评估，不拿更早一轮的结论顶上。
    /// </summary>
    private static object Verdict(FleetVehicle vehicle, IdleReturnBoardPass? pass, string? notRunning)
    {
        IdleReturnBoardVerdict? verdict = pass?.Verdicts.GetValueOrDefault(vehicle.AgvId);
        return new
        {
            evaluated = verdict is not null,
            reasonCode = verdict?.Reason,
            reasonDescription = IdleReturnCodeDescriptions.DescribeVerdict(verdict?.Reason),
            detail = string.IsNullOrEmpty(verdict?.Detail) ? null : verdict.Detail,
            passStartedAt = verdict is null ? null : pass?.StartedAt,
            note = verdict is not null ? null : notRunning ?? (pass is null ? NoPassCompletedYet : NotEvaluatedThisPass),
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
