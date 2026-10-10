using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>人对「车在不在那个等待点上」的结论。</summary>
public static class WaitingPointArrivalVerdicts
{
    /// <summary>车就停在这个等待点上：按到点收尾。</summary>
    public const string AtWaitingPoint = "AT_WAITING_POINT";

    /// <summary>车不在这个等待点上：按没到点收尾。</summary>
    public const string NotAtWaitingPoint = "NOT_AT_WAITING_POINT";

    public static IReadOnlyList<string> All { get; } = [AtWaitingPoint, NotAtWaitingPoint];
}

/// <summary>
/// 一次等待点到点人工收尾的请求（control-server#447）。车、旅程、等待点都由人点名，不推断；文本字段可空——缺了是一条拒绝理由，不是格式错误。
/// </summary>
/// <param name="VehicleKey">按车队登记从 <paramref name="AgvId"/> 解出的 RIoT 车号。</param>
/// <param name="StationId">人核实的那个等待点的 RIoT 站号；要等于这趟旅程正开往的那一个。</param>
/// <param name="SiteVerification">现场核实的记录引用（照片、巡检单号等）。</param>
public sealed record WaitingPointArrivalSettlementRequest(
    string AgvId,
    string VehicleKey,
    string? JourneyId,
    int StationId,
    string? Verdict,
    string? OperatorId,
    string? Reason,
    string? SiteVerification);

/// <summary>一次判定的结果。<see cref="Settled"/> 为真时 <see cref="Ending"/> 是旅程（或这次移动）收尾写下的码。</summary>
public sealed record WaitingPointArrivalSettlementResult(
    bool Settled,
    IReadOnlyList<string> Codes,
    string? Ending,
    string AuditRecordId);

/// <summary>
/// 等待点到点人工收尾（control-server#447）：开往等待点的单（空闲返回 control-server#390，清桩开往等待点 control-server#409）在 RIoT 上已精确
/// <c>SUCCESS</c>，到点证据的车辆那一半却一直不满足——地图对不上、车停偏了、读数不新鲜——时，由具名、有角色的人说明车在不在点上，服务端核过
/// 自己读得到的事实之后，按正常路径同一套记账收尾。
/// </summary>
/// <remarks>
/// <para>
/// <b>不放宽到点证据</b>：引擎照旧只凭证据收敛、不按时间放车放点（<c>REQ-0294</c>）。这里补的是那条证据永远来不了时的出口，属于放宽类入口：
/// 同一把 Bearer 凭据、同一个开关（<c>VehicleFaultRecovery:enabled</c>），每一次请求——拒绝也算——写一条管理员审计
/// （<see cref="AuditAction"/>）。
/// </para>
/// <para>
/// <b>谁能办</b>：<see cref="FieldOperatorRoleRoster"/> 里有 R-11 或 R-13 的人（与人工清桩同一份名单、同一组角色，调度 10-03 定）。
/// </para>
/// <para>
/// <b>前提，全部要满足</b>（不满足的每一条都列出来）：
/// </para>
/// <list type="number">
/// <item>这趟旅程是这辆车的、没收尾，正开往这个等待点（空闲返回的那一个停靠；充电旅程里那一次还在进行的清桩移动）。</item>
/// <item>引擎已经点了名：旅程上是到点证明不了的码（<see cref="IdleReturnExecutionReasons.ArrivalNotProven"/>、
/// <see cref="ChargingExecutionReasons.ClearanceArrivalNotProven"/>），并且已持续超过 <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c>。</item>
/// <item>等待点独占此刻仍归这一趟。</item>
/// <item>RIoT 现读：这一趟 <c>upperId</c> 的单是终态 <c>SUCCESS</c>，订单号、车、图、目标站都对（与引擎判到点的订单那一半逐字相同）。不是终态就拒绝：
/// 单还在走，车就可能还在动。</item>
/// <item>RIoT 现读：车证明停稳、没单——在线、空闲、速度为零、没有任务号、读数新鲜、运动安全读数为停止（与引擎判「单终结后证明停稳」同一组）。
/// 离线或读不到一律拒绝（<see cref="VehicleOffline"/>、<see cref="VehicleUnreadable"/>）：车重新上线后再办（调度 10-03 定）。</item>
/// <item>人的结论不与读数冲突：说不在点上而 RIoT 报它就在这个站上，拒绝；说在点上而 RIoT 报它在别的站上，拒绝。</item>
/// <item>清桩那一支说「在点上」时，清桩已经完成（人工清桩确认过）：清桩的完成要的是 R-11／R-13 的那一次确认，这里不替它做
/// （<see cref="ClearanceStillOpen"/>）。</item>
/// </list>
/// <para>
/// <b>RIoT 不在写锁里读</b>（与 control-server#452 同一个问题）：先在任何事务之外读库、读 RIoT，再开事务；事务里重读旅程行并按读到的
/// <see cref="JourneyRuntimeRow.Version"/> 核对，等待点独占与清桩状态也在事务里重判。核不上（引擎这期间推进过这趟旅程、别人释放了点）就拒绝
/// （<see cref="StateChanged"/>），什么也不写，人再提交一次。保存时 EF 再按同一个版本令牌核一次。
/// </para>
/// <para>
/// <b>收尾走正常路径的记账</b>：在点上——空闲返回与引擎收敛同一套（预占转占用、用途释放、收尾快照留 <c>ARRIVED</c> 的等待点腿），清桩与引擎到点完成的
/// 后一半同一套（<see cref="ClearanceAtWaitingPointClosure"/>）；不在点上——空闲返回按已确认失败（<see cref="IdleReturnEnding"/>，点留给离点清扫），
/// 清桩与「单被人结束、车证明停稳」同一套（<see cref="ClearanceMoveEnding"/>，原因 <see cref="ClearanceMoveReleaseReasons.Ended"/>）。
/// </para>
/// </remarks>
public sealed class WaitingPointArrivalSettlement(
    ControlServerDbContext dbContext,
    IRiotVehicleFacts vehicleFacts,
    IRiotVehicleSafetyFacts vehicleSafety,
    FieldOperatorRoleRoster roles,
    IGovernanceAuditWriter audit,
    OnboardJourneyPublisher publisher,
    IOptions<JourneyRuntimeOptions> runtimeOptions,
    TimeProvider timeProvider,
    ILogger<WaitingPointArrivalSettlement> logger)
{
    /// <summary>每一次请求写的管理员审计动作。</summary>
    public const string AuditAction = "WAITING_POINT_ARRIVAL_SETTLEMENT";

    public const string OperatorRequired = "ARRIVAL_SETTLEMENT_OPERATOR_REQUIRED";
    public const string ReasonRequired = "ARRIVAL_SETTLEMENT_REASON_REQUIRED";
    public const string SiteVerificationRequired = "ARRIVAL_SETTLEMENT_SITE_VERIFICATION_REQUIRED";
    public const string VerdictUnknown = "ARRIVAL_SETTLEMENT_VERDICT_UNKNOWN";
    public const string FieldTooLong = "ARRIVAL_SETTLEMENT_FIELD_TOO_LONG";
    public const string NotAuthorized = "ARRIVAL_SETTLEMENT_NOT_AUTHORIZED";
    public const string JourneyNotFound = "ARRIVAL_SETTLEMENT_JOURNEY_NOT_FOUND";
    public const string JourneyCompleted = "ARRIVAL_SETTLEMENT_JOURNEY_COMPLETED";
    public const string NotAWaitingPointMove = "ARRIVAL_SETTLEMENT_NOT_A_WAITING_POINT_MOVE";
    public const string StationMismatch = "ARRIVAL_SETTLEMENT_STATION_MISMATCH";
    public const string ArrivalNotNamed = "ARRIVAL_SETTLEMENT_ARRIVAL_NOT_NAMED";
    public const string TooEarly = "ARRIVAL_SETTLEMENT_TOO_EARLY";
    public const string WaitingPointNotHeld = "ARRIVAL_SETTLEMENT_WAITING_POINT_NOT_HELD";
    public const string ClearanceStillOpen = "ARRIVAL_SETTLEMENT_CLEARANCE_STILL_OPEN";
    public const string OrderUnreadable = "ARRIVAL_SETTLEMENT_ORDER_UNREADABLE";
    public const string OrderNotExactSuccess = "ARRIVAL_SETTLEMENT_ORDER_NOT_EXACT_SUCCESS";
    public const string VehicleUnreadable = "ARRIVAL_SETTLEMENT_VEHICLE_UNREADABLE";
    public const string VehicleOffline = "ARRIVAL_SETTLEMENT_VEHICLE_OFFLINE";
    public const string VehicleNotProvenStopped = "ARRIVAL_SETTLEMENT_VEHICLE_NOT_PROVEN_STOPPED";
    public const string VehicleReadingStale = "ARRIVAL_SETTLEMENT_VEHICLE_READING_STALE";
    public const string VehicleReportedAtWaitingPoint = "ARRIVAL_SETTLEMENT_VEHICLE_REPORTED_AT_WAITING_POINT";
    public const string VehicleReportedElsewhere = "ARRIVAL_SETTLEMENT_VEHICLE_REPORTED_ELSEWHERE";
    public const string StateChanged = "ARRIVAL_SETTLEMENT_STATE_CHANGED";

    /// <summary>理由与现场核实记录的最长长度（去掉首尾空白后的 UTF-16 码元）。</summary>
    public const int MaxTextLength = 500;

    /// <summary>人员与旅程号的最长长度。</summary>
    public const int MaxIdentifierLength = 128;

    /// <summary>每一个拒绝码给现场人员看的中文说明：Host 入口的拒绝响应里逐条带上（<c>descriptions</c>）。</summary>
    public static IReadOnlyDictionary<string, string> Descriptions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [OperatorRequired] = "没有写办理人（operatorId）",
        [ReasonRequired] = "没有写理由（reason）",
        [SiteVerificationRequired] = "没有写现场核实记录（siteVerification：照片编号、巡检单号等）",
        [VerdictUnknown] = "结论（verdict）只能是 AT_WAITING_POINT（车就在等待点上）或 NOT_AT_WAITING_POINT（车不在等待点上）",
        [FieldTooLong] = "有一项写得太长：理由与核实记录各不超过 500 字，办理人与旅程号各不超过 128 字",
        [NotAuthorized] = "办理人不在 R-11／R-13 名单里（或名单没配、读不到）：只有名单里的人能办",
        [JourneyNotFound] = "这辆车没有这个旅程号的旅程：请照看板上这辆车当前旅程的旅程号填写",
        [JourneyCompleted] = "这趟旅程已经收尾了，不用再办",
        [NotAWaitingPointMove] = "这趟旅程此刻不是在开往等待点（不是空闲返回，也没有一次进行中的清桩移动）",
        [StationMismatch] = "填的等待点不是这趟旅程正开往的那一个：请核对站号",
        [ArrivalNotNamed] = "服务端还没把这趟判成「到点证明不了」（看板上不是那个码）：单可能还在走，或另有别的原因，先按看板上的码处理",
        [TooEarly] = "到点证明不了的时长还没超过配置项 JourneyRuntime:OwnOrderRebuildRepeatWindow 规定的时长：车可能还在停稳、读数可能还在更新，过了再办",
        [WaitingPointNotHeld] = "这个等待点已不归这趟旅程（被人工释放或归了别的车）：服务端会按点丢失自己收尾，不用再办",
        [ClearanceStillOpen] = "清桩还没完成：车在等待点上时，先由 R-11／R-13 名单里的人确认清桩，再来办这一项",
        [OrderUnreadable] = "这一刻读不到 RIoT 上这张单：稍后再办",
        [OrderNotExactSuccess] = "RIoT 上这张单不是已完成（或去的不是这个点、不是这辆车）：单还没结束时车可能还在动，不能收尾",
        [VehicleUnreadable] = "这一刻读不到车的状态（RIoT 不应答）：等 RIoT 恢复、能读到车之后再办",
        [VehicleOffline] = "车离线：服务端证明不了它没在动，不能收尾。请等车重新上线、停稳之后再办",
        [VehicleNotProvenStopped] = "读到车不是停稳没单（在执行任务、速度不为零、不在空闲状态，或运动安全读数不是停止）：等车停稳、身上没有单之后再办",
        [VehicleReadingStale] = "车的读数太旧（超过 JourneyRuntime:MaximumEvidenceAge）：等读数更新之后再办",
        [VehicleReportedAtWaitingPoint] = "结论说车不在等待点上，RIoT 却报它就在这个站上：两者冲突，请再到现场核实",
        [VehicleReportedElsewhere] = "结论说车在等待点上，RIoT 却报它在别的站上：两者冲突，请再到现场核实",
        [StateChanged] = "核对期间这趟旅程或这个等待点变了（服务端刚推进过它，或别人刚办过）：什么也没写，请看一眼看板再提交一次",
    };

    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly Action<ILogger, string, string, string, string, string, Exception?> LogSettled =
        LoggerMessage.Define<string, string, string, string, string>(
            LogLevel.Warning,
            new EventId(2231, nameof(LogSettled)),
            "Waiting point arrival settled by hand: journey {JourneyId} of vehicle {VehicleKey}, verdict {Verdict} by {OperatorId}, " +
            "ended with {Ending} (control-server#447).");

    private readonly JourneyRuntimeOptions _options = runtimeOptions.Value;

    public async Task<WaitingPointArrivalSettlementResult> DecideAsync(
        WaitingPointArrivalSettlementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset now = timeProvider.GetUtcNow();
        string? journeyId = Trimmed(request.JourneyId);
        string? verdict = Trimmed(request.Verdict);
        string? operatorId = Trimmed(request.OperatorId);
        string? reason = Trimmed(request.Reason);
        string? siteVerification = Trimmed(request.SiteVerification);

        List<string> codes = [];
        if (operatorId is null) codes.Add(OperatorRequired);
        if (reason is null) codes.Add(ReasonRequired);
        if (siteVerification is null) codes.Add(SiteVerificationRequired);
        if (verdict is null || !WaitingPointArrivalVerdicts.All.Contains(verdict, StringComparer.Ordinal)) codes.Add(VerdictUnknown);
        bool fits = (reason?.Length ?? 0) <= MaxTextLength && (siteVerification?.Length ?? 0) <= MaxTextLength &&
                    (operatorId?.Length ?? 0) <= MaxIdentifierLength && (journeyId?.Length ?? 0) <= MaxIdentifierLength;
        if (!fits) codes.Add(FieldTooLong);
        string? role = fits ? roles.GrantedRole(operatorId, FieldOperatorRoleRoster.StationClearanceRoles) : null;
        if (operatorId is not null && fits && role is null) codes.Add(NotAuthorized);
        bool at = verdict == WaitingPointArrivalVerdicts.AtWaitingPoint;

        // ---- What the database says, read outside any transaction. ----
        dbContext.ChangeTracker.Clear();
        JourneyRuntimeRow? read = journeyId is null || !fits
            ? null
            : await dbContext.JourneyRuntimes.AsNoTracking()
                .SingleOrDefaultAsync(row => row.JourneyId == journeyId && row.AgvId == request.AgvId, cancellationToken)
                .ConfigureAwait(false);
        Move? move = null;
        if (read is null)
        {
            if (fits)
            {
                codes.Add(JourneyNotFound);
            }
        }
        else if (read.Stage == JourneyRuntimeStage.Completed)
        {
            codes.Add(JourneyCompleted);
        }
        else
        {
            move = await MoveOfAsync(read, cancellationToken).ConfigureAwait(false);
            codes.AddRange(DatabasePremises(read, move, request.StationId, now));
            if (move is not null && codes.Count == 0)
            {
                codes.AddRange(await HeldAndClearancePremisesAsync(read, move, at, cancellationToken).ConfigureAwait(false));
            }
        }
        dbContext.ChangeTracker.Clear();
        if (codes.Count > 0)
        {
            return await RefuseAsync(null).ConfigureAwait(false);
        }

        // ---- What RIoT says, read outside any transaction (control-server#452). ----
        RiotFacts facts = await ReadRiotAsync(read!, move!, cancellationToken).ConfigureAwait(false);
        codes.AddRange(RiotPremises(move!, facts, at));
        if (codes.Count > 0)
        {
            return await RefuseAsync(facts).ConfigureAwait(false);
        }

        // ---- Re-judged under the write lock, against the version read; then the normal path's bookkeeping. ----
        string ending;
        string auditRecordId;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            JourneyRuntimeRow? runtime = await dbContext.JourneyRuntimes
                .SingleOrDefaultAsync(row => row.JourneyId == read!.JourneyId, cancellationToken).ConfigureAwait(false);
            Move? current = runtime is null ? null : await MoveOfAsync(runtime, cancellationToken).ConfigureAwait(false);
            bool unchanged = runtime is not null && current is not null && runtime.Version == read!.Version &&
                             DatabasePremises(runtime, current, request.StationId, now).Count == 0 &&
                             (await HeldAndClearancePremisesAsync(runtime, current, at, cancellationToken).ConfigureAwait(false)).Count == 0;
            string? staged = unchanged
                ? await StageAsync(runtime!, current!, at, now, cancellationToken).ConfigureAwait(false)
                : null;
            if (staged is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                codes.Add(StateChanged);
                return await RefuseAsync(facts).ConfigureAwait(false);
            }
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException conflict) when (JourneyRowConflict.Is(conflict))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                codes.Add(StateChanged);
                return await RefuseAsync(facts).ConfigureAwait(false);
            }
            ending = staged;
            auditRecordId = await WriteAuditAsync(GovernanceActionOutcome.Succeeded, "SETTLED", facts, ending).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        LogSettled(logger, read!.JourneyId, read.VehicleKey, verdict!, operatorId!, ending, null);
        await SendAsync(read, move!, at, cancellationToken).ConfigureAwait(false);
        return new WaitingPointArrivalSettlementResult(true, [], ending, auditRecordId);

        async Task<WaitingPointArrivalSettlementResult> RefuseAsync(RiotFacts? riot)
        {
            string id = await WriteAuditAsync(GovernanceActionOutcome.Failed, "REJECTED", riot, null).ConfigureAwait(false);
            return new WaitingPointArrivalSettlementResult(false, [.. codes], null, id);
        }

        Task<string> WriteAuditAsync(GovernanceActionOutcome outcome, string result, RiotFacts? riot, string? end) =>
            audit.WriteAdministratorAsync(
                new GovernanceAuditEntry(
                    AuditAction,
                    GovernedObjectKind.StationExclusivity,
                    StationExclusivityManualRelease.ObjectId(read?.MapId ?? _options.MapId, request.StationId),
                    null,
                    outcome,
                    JsonSerializer.Serialize(
                        new
                        {
                            agvId = request.AgvId,
                            vehicleKey = request.VehicleKey,
                            stationId = request.StationId,
                            said = fits
                                ? (object)new { journeyId, verdict, operatorId, reason, siteVerification }
                                : new
                                {
                                    journeyIdLength = journeyId?.Length,
                                    verdictLength = verdict?.Length,
                                    operatorIdLength = operatorId?.Length,
                                    reasonLength = reason?.Length,
                                    siteVerificationLength = siteVerification?.Length,
                                },
                            grantedRole = role,
                            journey = read is null
                                ? null
                                : new
                                {
                                    read.JourneyId,
                                    stage = read.Stage.ToString(),
                                    read.BlockReasonCode,
                                    read.BlockReasonSince,
                                    read.Version,
                                    upperId = move?.Stop.UpperId,
                                },
                            riot,
                            codes,
                            result,
                            ending = end,
                        },
                        DetailOptions),
                    ClaimedAdministratorRole: role),
                now,
                cancellationToken);
    }

    /// <summary>这趟旅程正开往的等待点：空闲返回的那一个停靠，或充电旅程里还在进行的清桩移动（连同原桩停靠）。都不是答空。</summary>
    private async Task<Move?> MoveOfAsync(JourneyRuntimeRow runtime, CancellationToken cancellationToken)
    {
        if (runtime.IsIdleReturn())
        {
            JourneyStopRow stop = await dbContext.Set<JourneyStopRow>()
                .SingleAsync(row => row.JourneyId == runtime.JourneyId, cancellationToken).ConfigureAwait(false);
            return stop.Status is JourneyStopStatuses.Completed or JourneyStopStatuses.Removed
                ? null
                : new Move(stop, null, IdleReturnExecutionReasons.ArrivalNotProven);
        }
        if (!runtime.IsCharging())
        {
            return null;
        }
        JourneyStopRow[] stops = await dbContext.Set<JourneyStopRow>()
            .Where(row => row.JourneyId == runtime.JourneyId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        JourneyStopRow? clearance = stops
            .Where(row => row.StopRole == JourneyStopRoles.WaitingPoint &&
                          row.Status != JourneyStopStatuses.Completed && row.Status != JourneyStopStatuses.Removed)
            .Where(ClearanceMoveShape.IsClearanceMove)
            .SingleOrDefault();
        JourneyStopRow? charger = stops.SingleOrDefault(row => row.StopRole == JourneyStopRoles.Charger);
        return clearance is null || charger is null
            ? null
            : new Move(clearance, charger, ChargingExecutionReasons.ClearanceArrivalNotProven);
    }

    /// <summary>只看旅程行与停靠就判得了的前提。</summary>
    private List<string> DatabasePremises(JourneyRuntimeRow runtime, Move? move, int stationId, DateTimeOffset now)
    {
        List<string> codes = [];
        if (runtime.Stage == JourneyRuntimeStage.Completed)
        {
            codes.Add(JourneyCompleted);
            return codes;
        }
        if (move is null)
        {
            codes.Add(NotAWaitingPointMove);
            return codes;
        }
        if (move.Stop.StationRiotId != stationId)
        {
            codes.Add(StationMismatch);
        }
        if (!string.Equals(runtime.BlockReasonCode, move.NotProvenCode, StringComparison.Ordinal))
        {
            codes.Add(ArrivalNotNamed);
        }
        else if (runtime.BlockReasonSince is not { } since || now - since <= _options.OwnOrderRebuildRepeatWindow)
        {
            codes.Add(TooEarly);
        }
        return codes;
    }

    /// <summary>等待点仍归这一趟；清桩那一支说「在点上」时清桩已经完成。</summary>
    private async Task<List<string>> HeldAndClearancePremisesAsync(
        JourneyRuntimeRow runtime, Move move, bool at, CancellationToken cancellationToken)
    {
        List<string> codes = [];
        if (await WaitingPointExclusivity.HeldByAsync(dbContext, runtime.MapId, move.Stop.StationRiotId, runtime.JourneyId, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            codes.Add(WaitingPointNotHeld);
        }
        if (at && move.Charger is not null &&
            await dbContext.Set<ChargingCycleRow>().AsNoTracking()
                .AnyAsync(row => row.JourneyId == runtime.JourneyId && row.Phase != ChargingCyclePhases.Ended, cancellationToken)
                .ConfigureAwait(false))
        {
            codes.Add(ClearanceStillOpen);
        }
        return codes;
    }

    private async Task<RiotFacts> ReadRiotAsync(JourneyRuntimeRow runtime, Move move, CancellationToken cancellationToken)
    {
        RiotReadOutsideWriteLock.Ensure(dbContext);
        RiotOrderObservation? order;
        try
        {
            order = await vehicleFacts.ReconcileByUpperIdAsync(move.Stop.UpperId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            order = null;
        }
        OrderIntentRow? intent = await dbContext.OrderIntents.AsNoTracking()
            .SingleOrDefaultAsync(row => row.UpperId == move.Stop.UpperId, cancellationToken).ConfigureAwait(false);

        RiotVehicleObservation? vehicle;
        RiotVehicleSafetyObservation? safety;
        try
        {
            vehicle = await vehicleFacts.ReadVehicleAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false);
            safety = await vehicleSafety.ReadVehicleSafetyAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            vehicle = null;
            safety = null;
        }
        return new RiotFacts(
            order is null ? "UNREADABLE" : order.Kind.ToString(),
            order?.OrderState,
            order?.OrderId,
            intent?.OrderId,
            order is not null && order.Kind == RiotOrderObservationKind.Terminal &&
            order.OrderState == RiotOrderState.Success &&
            !string.IsNullOrWhiteSpace(order.OrderId) &&
            order.OrderId == intent?.OrderId &&
            order.VehicleKey == runtime.VehicleKey &&
            order.MapId == runtime.MapId &&
            order.DestinationStationId == move.Stop.StationRiotId,
            vehicle is not null,
            vehicle?.Connected,
            vehicle?.ProcState,
            vehicle?.CurrentMap,
            vehicle?.CurrentStationId,
            vehicle?.Speed,
            vehicle?.LockStatus,
            vehicle?.OrderTaskId,
            vehicle?.ObservedAt,
            safety?.MotionState.ToString(),
            timeProvider.GetUtcNow());
    }

    /// <summary>RIoT 读数上的前提。</summary>
    private List<string> RiotPremises(Move move, RiotFacts facts, bool at)
    {
        List<string> codes = [];
        if (facts.OrderKind == "UNREADABLE")
        {
            codes.Add(OrderUnreadable);
        }
        else if (!facts.ExactSuccess)
        {
            codes.Add(OrderNotExactSuccess);
        }

        if (!facts.VehicleRead)
        {
            codes.Add(VehicleUnreadable);
            return codes;
        }
        if (facts.Connected != true)
        {
            codes.Add(VehicleOffline);
            return codes;
        }
        if (facts.ProcState != "IDLE" || facts.Speed != 0 || !string.IsNullOrWhiteSpace(facts.OrderTaskId) ||
            facts.MotionState != nameof(RiotVehicleMotionState.Stopped))
        {
            codes.Add(VehicleNotProvenStopped);
        }
        if (facts.ObservedAt is not { } observed || observed > facts.ReadAt || facts.ReadAt - observed > _options.MaximumEvidenceAge)
        {
            codes.Add(VehicleReadingStale);
        }
        if (!at && facts.CurrentStationId == move.Stop.StationRiotId)
        {
            codes.Add(VehicleReportedAtWaitingPoint);
        }
        if (at && facts.CurrentStationId is int elsewhere && elsewhere != move.Stop.StationRiotId)
        {
            codes.Add(VehicleReportedElsewhere);
        }
        return codes;
    }

    /// <summary>按正常路径的记账暂存收尾，不保存；答收尾码。等待点此刻已不归这一趟时什么也不暂存，答空。</summary>
    private async Task<string?> StageAsync(
        JourneyRuntimeRow runtime, Move move, bool at, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (move.Charger is null)
        {
            StationExclusivityRow? held = await WaitingPointExclusivity
                .HeldByAsync(dbContext, runtime.MapId, move.Stop.StationRiotId, runtime.JourneyId, cancellationToken)
                .ConfigureAwait(false);
            if (held is null)
            {
                return null;
            }
            if (at)
            {
                // ConvergeIdleReturnAsync's bookkeeping, under the operator's code.
                await WaitingPointExclusivity.StageOccupyAsync(dbContext, held, now, cancellationToken).ConfigureAwait(false);
                await IdleReturnEnding.StageAsync(
                        dbContext, runtime, move.Stop, IdleReturnExecutionReasons.ArrivalConfirmedByOperator,
                        IdleReturnExecutionReasons.ArrivalConfirmedByOperator, stillAtWaitingPoint: true, releaseStationNow: null, now,
                        cancellationToken)
                    .ConfigureAwait(false);
                return IdleReturnExecutionReasons.ArrivalConfirmedByOperator;
            }
            // A confirmed failure, as JudgeEndedIdleReturnOrderAsync ends one: the reservation is left to the departure sweep.
            await IdleReturnEnding.StageAsync(
                    dbContext, runtime, move.Stop, IdleReturnExecutionReasons.NotAtWaitingPointByOperator,
                    IdleReturnExecutionReasons.NotAtWaitingPointByOperator, stillAtWaitingPoint: false, releaseStationNow: null, now,
                    cancellationToken)
                .ConfigureAwait(false);
            return IdleReturnExecutionReasons.NotAtWaitingPointByOperator;
        }

        if (at)
        {
            // The second half of CompleteClearanceAtWaitingPointAsync, after a person's clearance ended the cycle.
            string ending = await ClearanceAtWaitingPointClosure
                .EndingAfterClearedCycleAsync(dbContext, runtime.JourneyId, cancellationToken).ConfigureAwait(false);
            return await ClearanceAtWaitingPointClosure
                .StageAsync(dbContext, runtime, move.Charger, move.Stop, ending, now, cancellationToken).ConfigureAwait(false)
                ? ending
                : null;
        }

        // A move ended by a person with the vehicle proven stopped, as JudgeClearanceMoveEndedInRiotAsync ends one off the point.
        await ClearanceMoveEnding.StageAsync(dbContext, runtime, move.Stop, ClearanceMoveReleaseReasons.Ended, now, cancellationToken)
            .ConfigureAwait(false);
        bool clearing = await dbContext.Set<ChargingCycleRow>().AsNoTracking()
            .AnyAsync(row => row.JourneyId == runtime.JourneyId && row.Phase == ChargingCyclePhases.Clearing, cancellationToken)
            .ConfigureAwait(false);
        string code = clearing ? ChargingExecutionReasons.ClearanceMoveEnded : ChargingExecutionReasons.UnableToChargeCleared;
        runtime.SetBlockReason(code, now);
        runtime.UpdatedAt = now;
        return code;
    }

    /// <summary>提交之后发出这次收尾暂存的快照；没连着不算失败，留在发件箱里等重连补发。</summary>
    private async Task SendAsync(JourneyRuntimeRow read, Move move, bool at, CancellationToken cancellationToken)
    {
        if (move.Charger is not null && !at)
        {
            int attempt = ClearanceMoveShape.AttemptOf(move.Stop);
            foreach (string messageId in new[]
                     {
                         ClearanceMoveShape.BackPlanMessageId(read.JourneyId, attempt),
                         ClearanceMoveShape.BackStateMessageId(read.JourneyId, attempt),
                     })
            {
                try
                {
                    await publisher.SendPersistedAsync(messageId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or KeyNotFoundException)
                {
                    // Not on the line, or never staged (the vehicle had no session): the reconnect replay carries what was staged.
                }
            }
            return;
        }
        await JourneyClosure.SendAsync(publisher, dbContext, read.AgvId, cancellationToken).ConfigureAwait(false);
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private sealed record Move(JourneyStopRow Stop, JourneyStopRow? Charger, string NotProvenCode);

    /// <summary>这一次从 RIoT 读到的，原样进审计。</summary>
    private sealed record RiotFacts(
        string OrderKind,
        int? OrderState,
        string? OrderId,
        string? IntentOrderId,
        bool ExactSuccess,
        bool VehicleRead,
        bool? Connected,
        string? ProcState,
        string? CurrentMap,
        int? CurrentStationId,
        double? Speed,
        int? LockStatus,
        string? OrderTaskId,
        DateTimeOffset? ObservedAt,
        string? MotionState,
        DateTimeOffset ReadAt);
}
