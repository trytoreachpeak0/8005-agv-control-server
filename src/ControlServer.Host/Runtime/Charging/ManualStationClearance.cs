using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>人工清桩确认从哪来。</summary>
public static class ManualStationClearanceSources
{
    /// <summary>车载端 <c>ManualStationClearanceConfirmationRequested</c>（hmi#221 的入口）。</summary>
    public const string Onboard = "ONBOARD";

    /// <summary>服务端 Host 接口（车载端入口没合入、或车载端连不上时）。</summary>
    public const string Host = "HOST";
}

/// <summary>一次人工清桩确认的请求，两个来源同一个形状。</summary>
/// <param name="VehicleKey">按车队登记从 <paramref name="AgvId"/> 解出的 RIoT 车号；解不出为空（判拒绝）。</param>
/// <param name="RequestContentHash">
/// 只对载荷取的摘要（调度 09-30 对齐第 2 条）：车载端重提时沿用同一个确认号、载荷逐字节相同、<c>messageId</c> 换新，整行摘要必然不同。
/// </param>
public sealed record ManualStationClearanceRequest(
    string Source,
    string AgvId,
    string? VehicleKey,
    string ConfirmationRequestId,
    long SessionGeneration,
    string RequestMessageId,
    string RequestContentHash,
    string StationId,
    string? PublicStationFunction,
    string ClearedCondition,
    string? OperatorId,
    string VerificationMethod,
    DateTimeOffset VerifiedAt,
    DateTimeOffset ObservedAt);

/// <summary>
/// 人工清桩确认的判定（<c>REQ-0179</c>、<c>REQ-0178</c>、<c>REQ-0180</c>；批次9-08，control-server#406）。车载端的线上请求与 Host 接口走这同一个函数。
/// </summary>
/// <remarks>
/// <para>
/// <b>至多判一次，按确认号</b>（调度 09-30 对齐第 1、3 条）：同一个 <c>confirmationRequestId</c> 再来——重放、或换了 <c>messageId</c> 的重提——拿回存下的
/// 那一份判定，<c>stationReleased</c> 不重算（车载端按确认号记载荷哈希，前后不同就断会话）；内容不同按冲突拒收
/// （<see cref="FieldConfirmationContentConflictException"/>）。
/// </para>
/// <para>
/// <b>谁能确认</b>：<see cref="FieldOperatorRoleRoster"/> 里有 R-11 或 R-13 的人（角色不上线路，按 <c>operatorId</c> 查）；普通操作员、名单为空、
/// 名单读不到，一律 <c>REJECTED</c>（<see cref="NotAuthorized"/>）。
/// </para>
/// <para>
/// <b>确认的是哪一个桩</b>：这辆车此刻正被留在那里的那个桩——充不上之后的清桩中，或者下面几种只有人能收尾的保持状态之一：到桩不充电、到桩时桩已不是
/// 这一趟的、取消后证明不了停稳、失联、充满后离桩确认不了（旅程已收尾、周期还挂着桩）、失败周期留下的预占放不掉（cs#404 审查留给本票的两格）。
/// 请求里的 <c>stationId</c> 要等于计划里那条 <c>CHARGER</c> 腿的站点（站名，或 RIoT 站号），<c>publicStationFunction</c> 要为空——本批只清充电桩
/// （<see cref="StationMismatch"/>）。车不在任何一种这样的状态里答 <see cref="NotAllowedInState"/>。
/// </para>
/// <para>
/// <b>与新鲜的系统事实冲突就拒绝、不释放</b>：RIoT 此刻读到车在线、停在这个桩上，或读到它在充电（<see cref="NotAllowedInState"/>）。读不到、离线都不挡——
/// 断电移车、拖车正是这个出口要接的情形；车辆最终位置照实记「未知」，车按 <c>REQ-0180</c> 由常规派车检查挡住，直到对账完成。
/// </para>
/// <para>
/// <b>确认之后</b>（同一个事务）：清桩记录写上确认人、角色、时刻、车辆最终位置（确认那一刻读到的 RIoT 位置，读不到记 <c>UNKNOWN</c>）、旧单处置、
/// <c>clearedCondition</c>。<b>只有旧单已终结</b>（取消、删除、成功、失败，或 RIoT 答查无此单——真实形态的 HTTP 200 不带 result 与 404 都算）才
/// <b>完成</b>清桩（写 <c>CompletedAt</c>）并在同一个事务里释放（<see cref="ChargerClearanceRelease"/>），<c>stationReleased=true</c>。充不上之后的清桩中，
/// 旧单还没收敛（仍 <c>HANG</c>、读不到）时只记下确认、<c>CompletedAt</c> 留空（<c>REQ-0178</c>：取消结果未知时不完成清桩；control-server#406 独立审查），
/// 答 <c>stationReleased=false</c>，由引擎在对账到终态的那一轮完成并释放；别的保持状态没有这一轮，旧单没终结就拒绝。桩的分配暂停不解除，车的派单资格
/// 不在这里恢复（调度 09-30 对齐第 8 条）。
/// </para>
/// <para>
/// <b>同一次清桩的第二个确认号</b>（车载端重启后再按会换新号；调度 09-30 对齐第 7 条）：确认已经记下或清桩已经完成的，不再记第二次、不再释放第二次，答
/// <c>CONFIRMED</c>；确认已记下而旧单此刻已终结的，由这一次完成并释放，<c>stationReleased</c> 照此刻桩是否已放开。
/// </para>
/// <para>
/// <b>并发</b>：车载端与 Host 几乎同时确认、或确认与引擎那一轮的释放撞在一起时，清桩记录的完成带着并发令牌、释放带着读到的版本与持有者，只有一方写成；
/// 输的一方整个事务回滚，从头再判一次（这时读到的已经是赢的那一方留下的），所以只有一条清桩记录、一次释放。RIoT 在事务之外读。
/// </para>
/// </remarks>
public sealed class ManualStationClearance(
    ControlServerDbContext dbContext,
    IFieldConfirmationRequestStore requests,
    IStationClearanceStore clearances,
    IRiotVehicleFacts vehicleFacts,
    FieldOperatorRoleRoster roles,
    IGovernanceAuditWriter audit,
    IOptions<JourneyRuntimeOptions> runtimeOptions,
    TimeProvider timeProvider,
    ILogger<ManualStationClearance> logger)
{
    /// <summary>不在 R-11／R-13 名单里。</summary>
    public const string NotAuthorized = "RECOVERY_AUTHENTICATION_FAILED";

    /// <summary>站点不是这辆车正被留着的那个充电桩，或请求说的是公共站点。</summary>
    public const string StationMismatch = "STATION_MISMATCH";

    /// <summary>车不在任何一种要人工清桩的状态里，或 RIoT 此刻的事实与确认冲突。</summary>
    public const string NotAllowedInState = "ACTION_NOT_ALLOWED_IN_STATE";

    /// <summary>每一次判定写的管理员审计动作。</summary>
    public const string AuditAction = "MANUAL_STATION_CLEARANCE_CONFIRMATION";

    /// <summary>车辆最终位置读不到时记的值。</summary>
    public const string PositionUnknown = "UNKNOWN";

    /// <summary>
    /// 这些保持码之一挂在一趟未收尾的充电旅程上，表示它只有人能收尾：人工清桩可以作它的出口（cs#404、cs#405 留给本票）。
    /// </summary>
    public static IReadOnlySet<string> HeldCodes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        JourneyRuntimeEngine.OrderEndedWithoutArrivalReason,
        ChargingExecutionReasons.ChargerNotEngaged,
        ChargingExecutionReasons.ReservationLostAtArrival,
        ChargingExecutionReasons.VehicleObservationLost,
        ChargingExecutionReasons.BatteryTelemetryLost,
        ChargingExecutionReasons.ArrivalNotProven,
        ChargingExecutionReasons.OrderNotFound,
    };

    private static readonly Action<ILogger, string, string, string, string, bool, string, Exception?> LogDecided =
        LoggerMessage.Define<string, string, string, string, bool, string>(
            LogLevel.Warning,
            new EventId(2266, nameof(LogDecided)),
            "Manual station clearance {ConfirmationRequestId} for vehicle {AgvId} at charger {StationId}: {Outcome}, station " +
            "released {Released} ({Detail}). The charger's allocation hold, if any, stays until a ChargingStationRecoveryConfirmation.");

    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private readonly JourneyRuntimeOptions _runtime = runtimeOptions.Value;

    /// <summary>判定并答它；同一个确认号判过的，答存下的那一份。</summary>
    public async Task<ManualStationClearanceConfirmation> DecideAsync(
        ManualStationClearanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        FieldConfirmationRequestIdentity identity = new(
            request.ConfirmationRequestId, request.AgvId, request.SessionGeneration, request.RequestMessageId,
            request.RequestContentHash, request.OperatorId ?? "", request.VerificationMethod, request.VerifiedAt,
            request.ObservedAt);

        // Twice at most: the loser of a race with the engine or with the other entry reads what the winner left, once.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ForgetWhatThisWrote();
            if (await requests.ReadManualStationClearanceAsync(request.ConfirmationRequestId, cancellationToken)
                    .ConfigureAwait(false) is { } decided)
            {
                if (decided.Request.RequestContentHash != request.RequestContentHash || decided.Request.AgvId != request.AgvId)
                {
                    throw new FieldConfirmationContentConflictException(
                        $"Confirmation request {request.ConfirmationRequestId} was replayed with different content.");
                }
                return decided;
            }

            Target? target = request.VehicleKey is null
                ? null
                : await FindTargetAsync(request.VehicleKey, cancellationToken).ConfigureAwait(false);
            RiotVehicleObservation? vehicle = await ReadVehicleAsync(request.VehicleKey, cancellationToken).ConfigureAwait(false);
            RiotOrderObservation? oldOrder = target is null
                ? null
                : await ReadOldOrderAsync(target.UpperId, cancellationToken).ConfigureAwait(false);
            string disposition = oldOrder is null ? PositionUnknown : Disposition(oldOrder, target!.UpperId);

            string? role = roles.GrantedRole(request.OperatorId, FieldOperatorRoleRoster.StationClearanceRoles);
            (string? code, string? field, string? message) = Judge(request, role, target, vehicle, disposition, oldOrder);
            DateTimeOffset now = timeProvider.GetUtcNow();
            bool released = false;

            DecisionTransaction transaction = await DecisionTransaction.BeginAsync(dbContext, cancellationToken)
                .ConfigureAwait(false);
            await using (transaction)
            {
                if (code is null && target is not null)
                {
                    if (target.CompletedClearance is not null)
                    {
                        // The same clearance confirmed again under another number: nothing is recorded twice. If the old order
                        // has ended since and the engine has not yet released, this does -- the same release, so only one of the
                        // two takes effect and the loser reads it released.
                        released = !target.ChargerHeld || target.Cycle.Phase == ChargingCyclePhases.Ended;
                        if (!released && Settled(disposition))
                        {
                            if (!await ChargerClearanceRelease.ReleaseAsync(
                                    dbContext, target.Cycle.CycleId, target.Cycle.Version, EndingOf(target.Cycle), now,
                                    cancellationToken)
                                    .ConfigureAwait(false))
                            {
                                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                                ForgetWhatThisWrote();
                                continue;
                            }
                            released = true;
                        }
                    }
                    else if (target.Cycle.Phase == ChargingCyclePhases.Clearing)
                    {
                        // After an unable-to-charge (review of control-server#406): the confirmation is recorded now, and the clearance
                        // completes -- CompletedAt, and the release -- only once the old order has ended too (REQ-0178, REQ-0179). A
                        // second number for a confirmation already recorded records nothing again.
                        string clearanceId = target.ClearanceId ?? JourneyPlanBuilder.StableGuid(target.Cycle.CycleId, "station-clearance");
                        if (!target.ConfirmationRecorded &&
                            !await RecordConfirmationAsync(clearanceId, request, role!, vehicle, disposition, now, cancellationToken)
                                .ConfigureAwait(false))
                        {
                            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                            ForgetWhatThisWrote();
                            continue;
                        }
                        // Review N1: the old order has ended, but the charger is completed vacant only if RIoT does not read the
                        // vehicle back on it (or charging) right now -- a recorded confirmation can be older than that.
                        if (Settled(disposition) && !StillOnTheCharger(vehicle, target.Cycle.StationId, _runtime.MapIdentity))
                        {
                            if (!await ChargerClearanceRelease.CompleteAndReleaseAsync(
                                    dbContext, clearanceId, target.Cycle.CycleId, target.Cycle.Version, EndingOf(target.Cycle),
                                    disposition, now, cancellationToken)
                                    .ConfigureAwait(false))
                            {
                                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                                ForgetWhatThisWrote();
                                continue;
                            }
                            released = true;
                        }
                    }
                    else
                    {
                        string clearanceId = target.ClearanceId ?? JourneyPlanBuilder.StableGuid(target.Cycle.CycleId, "station-clearance");
                        StationClearance started = await clearances.StartAsync(
                                clearanceId, target.Cycle.CycleId, target.Cycle.VehicleKey, target.Cycle.MapId,
                                target.Cycle.StationId, now, cancellationToken)
                            .ConfigureAwait(false);
                        bool completed = await clearances.CompleteAsync(
                                started.ClearanceId,
                                new StationClearanceCompletion(
                                    now,
                                    StationClearanceProofs.ManualConfirmation,
                                    null,
                                    null,
                                    request.OperatorId!.Trim(),
                                    role,
                                    now,
                                    FinalPosition(vehicle),
                                    disposition,
                                    [],
                                    request.ClearedCondition,
                                    request.ConfirmationRequestId),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!completed)
                        {
                            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                            ForgetWhatThisWrote();
                            continue;
                        }

                        if (Settled(disposition))
                        {
                            if (!await ChargerClearanceRelease.ReleaseAsync(
                                    dbContext, target.Cycle.CycleId, target.Cycle.Version, EndingOf(target.Cycle), now,
                                    cancellationToken)
                                    .ConfigureAwait(false))
                            {
                                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                                ForgetWhatThisWrote();
                                continue;
                            }
                            released = true;
                        }
                    }
                }

                ManualStationClearanceConfirmation decision = new(
                    identity,
                    request.StationId,
                    request.PublicStationFunction,
                    request.ClearedCondition,
                    new FieldConfirmationDecision(
                        code is null ? FieldConfirmationDecision.Confirmed : FieldConfirmationDecision.Rejected,
                        code, field, message, now),
                    released);
                ManualStationClearanceConfirmation stored = await requests
                    .DecideManualStationClearanceAsync(decision, cancellationToken).ConfigureAwait(false);
                if (stored.Request.RequestMessageId != request.RequestMessageId || stored.Decision.DecidedAt != now)
                {
                    // The same number was decided through another context in between: that decision is the one, and nothing
                    // this attempt did stands.
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    ForgetWhatThisWrote();
                    return stored;
                }

                await WriteAuditAsync(request, role, target, vehicle, disposition, stored, now, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                LogDecided(
                    logger, request.ConfirmationRequestId, request.AgvId, request.StationId, stored.Decision.Outcome,
                    stored.StationReleased, code ?? disposition, null);
                return stored;
            }
        }

        throw new InvalidOperationException(
            $"Manual station clearance {request.ConfirmationRequestId} lost every race it entered; nothing was written.");
    }

    /// <summary>
    /// 清桩中：只记下人工确认（确认人、角色、时刻、车辆最终位置、此刻的旧单处置、<c>clearedCondition</c>、确认号），<b>不写</b>
    /// <see cref="StationClearanceRow.CompletedAt"/>。按「还没记过确认、还没完成」更新，另一方先记下了答假（调用方回滚、重判）。
    /// </summary>
    private async Task<bool> RecordConfirmationAsync(
        string clearanceId,
        ManualStationClearanceRequest request,
        string role,
        RiotVehicleObservation? vehicle,
        string disposition,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string confirmedBy = request.OperatorId!.Trim();
        string position = FinalPosition(vehicle);
        int recorded = await dbContext.Set<StationClearanceRow>()
            .Where(row => row.ClearanceId == clearanceId && row.ConfirmedAt == null && row.CompletedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.ConfirmedBy, confirmedBy)
                    .SetProperty(row => row.ConfirmedByRole, role)
                    .SetProperty(row => row.ConfirmedAt, now)
                    .SetProperty(row => row.VehicleFinalPosition, position)
                    .SetProperty(row => row.OldOrderDisposition, disposition)
                    .SetProperty(row => row.ClearedCondition, request.ClearedCondition)
                    .SetProperty(row => row.ConfirmationRequestId, request.ConfirmationRequestId),
                cancellationToken)
            .ConfigureAwait(false);
        return recorded == 1;
    }

    /// <summary>
    /// 一次尝试回滚之后，丢掉它留在变更跟踪里的行（它们说的是没有提交的东西）。只丢这个判定会写的那几类：入站处理器的收件箱行等调用方的行原样留着。
    /// </summary>
    private void ForgetWhatThisWrote()
    {
        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry in dbContext.ChangeTracker.Entries()
                     .Where(entry => entry.Entity is StationClearanceRow or ManualStationClearanceConfirmationRow
                         or ChargingCycleRow or StationExclusivityRow or StationExclusivityRecordRow
                         or VehiclePurposeClaimRow or VehiclePurposeClaimRecordRow)
                     .ToArray())
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// 判定的那一个事务：自己开一个；调用方已经开着时（入站处理器的收件箱事务）在它里面立一个保存点，回滚只回到保存点，调用方的事务照旧由它提交。
    /// </summary>
    private sealed class DecisionTransaction : IAsyncDisposable
    {
        private const string Savepoint = "manual_station_clearance";
        private readonly IDbContextTransaction? _own;
        private readonly IDbContextTransaction? _outer;

        private DecisionTransaction(IDbContextTransaction? own, IDbContextTransaction? outer)
        {
            _own = own;
            _outer = outer;
        }

        public static async Task<DecisionTransaction> BeginAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
        {
            if (dbContext.Database.CurrentTransaction is { } outer)
            {
                await outer.CreateSavepointAsync(Savepoint, cancellationToken).ConfigureAwait(false);
                return new DecisionTransaction(null, outer);
            }
            return new DecisionTransaction(
                await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false), null);
        }

        public Task RollbackAsync(CancellationToken cancellationToken) =>
            _own?.RollbackAsync(cancellationToken) ?? _outer!.RollbackToSavepointAsync(Savepoint, cancellationToken);

        public Task CommitAsync(CancellationToken cancellationToken) =>
            _own?.CommitAsync(cancellationToken) ?? _outer!.ReleaseSavepointAsync(Savepoint, cancellationToken);

        public ValueTask DisposeAsync() => _own?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    /// <summary>旧单此刻在 RIoT 里的样子；读不到记 <see cref="RiotOrderObservationKind.Unknown"/>（处置随之是 <see cref="PositionUnknown"/>）。</summary>
    private async Task<RiotOrderObservation> ReadOldOrderAsync(string upperId, CancellationToken cancellationToken)
    {
        try
        {
            return await vehicleFacts.ReconcileByUpperIdAsync(upperId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            return new RiotOrderObservation(upperId, RiotOrderObservationKind.Unknown, null);
        }
    }

    /// <summary>
    /// 旧单此刻的处置：<c>CANCELLED</c>、<c>DELETED</c>、<c>SUCCESS</c>、<c>FAILED</c>、<c>ABSENT</c>（查无此单）为已终结；<c>HANG</c>、<c>ACTIVE</c>、
    /// <c>SUSPENDED</c>、<c>UNKNOWN</c> 不是。
    /// </summary>
    public static string Disposition(RiotOrderObservation order, string upperId)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.Kind == RiotOrderObservationKind.NotFound || order.IsExactAbsentAtObservation(upperId))
        {
            return "ABSENT";
        }
        return (order.Kind, order.OrderState) switch
        {
            (RiotOrderObservationKind.Terminal, RiotOrderState.Cancelled) => "CANCELLED",
            (RiotOrderObservationKind.Terminal, RiotOrderState.Deleted) => "DELETED",
            (RiotOrderObservationKind.Terminal, RiotOrderState.Success) => "SUCCESS",
            (RiotOrderObservationKind.Terminal, RiotOrderState.Failed) => "FAILED",
            (RiotOrderObservationKind.Terminal, RiotOrderState.Suspended) => "SUSPENDED",
            (RiotOrderObservationKind.Active, RiotOrderState.Hang) => "HANG",
            (RiotOrderObservationKind.Active, _) => "ACTIVE",
            _ => PositionUnknown,
        };
    }

    /// <summary>清桩收尾时周期的结束原因：充不上之后的清桩中是 <see cref="ChargingExecutionReasons.UnableToChargeCleared"/>，别的保持状态是
    /// <see cref="ChargingExecutionReasons.ClearedByOperator"/>。</summary>
    private static string EndingOf(ChargingCycleRow cycle) =>
        cycle.Phase == ChargingCyclePhases.Clearing
            ? ChargingExecutionReasons.UnableToChargeCleared
            : ChargingExecutionReasons.ClearedByOperator;

    /// <summary>
    /// 旧单离开 <c>HANG</c> 进了会让车动的状态——排队（1）、执行（3）、队列优先（10）：有人在 RIoT 里让它继续了（审查 S5）。暂停（7）、挂起（8）
    /// 不算。引擎据此告警并作废已记下的确认（审查 N1），人工确认据此拒收（审查 W3）。
    /// </summary>
    public static bool CanMoveTheVehicle(RiotOrderObservation? order) =>
        order is
        {
            Kind: RiotOrderObservationKind.Active,
            OrderState: RiotOrderState.Queueing or RiotOrderState.Executing or RiotOrderState.QueuePriority,
        };

    /// <summary>旧单处置是否已终结（<c>REQ-0178</c>：清桩只在旧单确认终态之后完成释放）。</summary>
    public static bool Settled(string disposition) =>
        disposition is "CANCELLED" or "DELETED" or "SUCCESS" or "FAILED" or "ABSENT";

    /// <summary>
    /// RIoT 读到这辆车在线，并且停在这个桩上（地图对得上或没报地图）、或正在充电：桩没有腾空。读不到、离线答假——断电移车、拖车正是人工清桩要接的情形。
    /// 人工确认的冲突判断与完成清桩前的重核（审查 N1）用的是同一个判断。
    /// </summary>
    public static bool StillOnTheCharger(RiotVehicleObservation? vehicle, int stationId, string mapIdentity) =>
        vehicle is { Connected: true } &&
        ((vehicle.CurrentStationId == stationId &&
          (string.IsNullOrEmpty(vehicle.CurrentMap) || string.Equals(vehicle.CurrentMap, mapIdentity, StringComparison.Ordinal))) ||
         string.Equals(vehicle.BatteryState, BatteryEligibility.ChargingBatteryState, StringComparison.Ordinal));

    /// <summary>确认那一刻读到的车辆位置：<c>地图/站</c>；读不到、离线、没有当前站记 <see cref="PositionUnknown"/>。</summary>
    public static string FinalPosition(RiotVehicleObservation? vehicle) =>
        vehicle is { Connected: true, CurrentStationId: int station }
            ? string.Create(CultureInfo.InvariantCulture, $"{vehicle.CurrentMap}/{station}")
            : PositionUnknown;

    private (string? Code, string? Field, string? Message) Judge(
        ManualStationClearanceRequest request,
        string? role,
        Target? target,
        RiotVehicleObservation? vehicle,
        string disposition,
        RiotOrderObservation? oldOrder)
    {
        if (role is null)
        {
            return (NotAuthorized, "payload.operator.operatorId",
                "This operator holds neither R-11 nor R-13 in the server's field operator roster; only they may confirm a charger clear.");
        }
        if (request.PublicStationFunction is not null)
        {
            return (StationMismatch, "payload.publicStationFunction",
                "Only a charger is cleared in this batch; a public station's manual clearance is not taken.");
        }
        if (target is null)
        {
            return (NotAllowedInState, "payload.stationId",
                "The vehicle is not held at a charger waiting for a manual clearance.");
        }
        if (!string.Equals(request.StationId, target.StationName, StringComparison.Ordinal) &&
            !string.Equals(request.StationId, target.Cycle.StationId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return (StationMismatch, "payload.stationId",
                $"The vehicle is held at charger {target.StationName}, not at {request.StationId}.");
        }
        if (target.CompletedClearance is null && !target.ConfirmationRecorded &&
            StillOnTheCharger(vehicle, target.Cycle.StationId, _runtime.MapIdentity))
        {
            return (NotAllowedInState, "payload.stationId",
                "RIoT reads the vehicle still on the charger, or charging: the confirmation conflicts with a fresh system fact.");
        }
        if (target.CompletedClearance is null && target.Cycle.Phase == ChargingCyclePhases.Clearing && CanMoveTheVehicle(oldOrder))
        {
            // Review W3: the engine voids a recorded confirmation once, on the first round it sees the resume; one pressed after
            // that would stay recorded with the vehicle free to drive back, guarded only by the re-read before completion.
            return (NotAllowedInState, "payload.stationId",
                $"The old charge order has been let go on in RIoT (state {oldOrder!.OrderState}) and may drive the vehicle back " +
                "to the charger; end it in RIoT first, then confirm again.");
        }
        if (target.CompletedClearance is null && target.Cycle.Phase != ChargingCyclePhases.Clearing && !Settled(disposition))
        {
            // Only the clearing after an unable-to-charge has a round that finishes a release later (the engine's clearing
            // branch). For the other held states the old order has to have ended now, or the confirmation is refused rather
            // than recorded with nobody to finish it.
            return (NotAllowedInState, "payload.stationId",
                $"The old charge order is {disposition} in RIoT, not ended; end it in RIoT first, then confirm again.");
        }
        return (null, null, null);
    }

    private async Task<RiotVehicleObservation?> ReadVehicleAsync(string? vehicleKey, CancellationToken cancellationToken)
    {
        if (vehicleKey is null)
        {
            return null;
        }
        try
        {
            return await vehicleFacts.ReadVehicleAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// 这辆车此刻被留在哪个桩上等人工清桩：清桩中的周期；挂着 <see cref="HeldCodes"/> 之一的充电旅程的周期；充满后还挂着桩的周期；
    /// 失败结束、预占还放不掉的周期。都没有时，看它最近一个周期是不是刚由人工清桩收尾（同一次清桩的第二个确认号）。
    /// </summary>
    private async Task<Target?> FindTargetAsync(string vehicleKey, CancellationToken cancellationToken)
    {
        ChargingCycleRow[] cycles = await dbContext.Set<ChargingCycleRow>().AsNoTracking()
            .Where(row => row.VehicleKey == vehicleKey)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        ChargingCycleRow? open = cycles.SingleOrDefault(row => row.Phase != ChargingCyclePhases.Ended);
        if (open is not null)
        {
            bool held = open.Phase == ChargingCyclePhases.Clearing || open.WireState == ChargingCycleWireStates.Complete;
            if (!held)
            {
                JourneyRuntimeRow? journey = await dbContext.JourneyRuntimes.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.JourneyId == open.JourneyId, cancellationToken).ConfigureAwait(false);
                held = journey is { Stage: not JourneyRuntimeStage.Completed, BlockReasonCode: { } code } && HeldCodes.Contains(code);
            }
            return held ? await DescribeAsync(open, cancellationToken).ConfigureAwait(false) : null;
        }

        string[] heldJourneys = await dbContext.Set<StationExclusivityRow>().AsNoTracking()
            .Where(row => row.VehicleKey == vehicleKey && row.StationKind == StationExclusivityKinds.Charger)
            .Select(row => row.JourneyId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        ChargingCycleRow? stuck = cycles.FirstOrDefault(row =>
            heldJourneys.Contains(row.JourneyId, StringComparer.Ordinal) && row.EndReason is { } reason &&
            ChargingExecutionReasons.ConfirmedFailures.Contains(reason));
        if (stuck is not null)
        {
            return await DescribeAsync(stuck, cancellationToken).ConfigureAwait(false);
        }

        // Latest by allocation, compared in memory (SQLite cannot order by a DateTimeOffset).
        ChargingCycleRow? latest = cycles.OrderByDescending(row => row.AllocatedAt).FirstOrDefault();
        if (latest is { EndReason: { } ending } && ChargingExecutionReasons.ClearedEndings.Contains(ending))
        {
            Target described = await DescribeAsync(latest, cancellationToken).ConfigureAwait(false);
            return described.CompletedClearance is null ? null : described;
        }
        return null;
    }

    private async Task<Target> DescribeAsync(ChargingCycleRow cycle, CancellationToken cancellationToken)
    {
        JourneyStopRow stop = await dbContext.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == cycle.JourneyId, cancellationToken).ConfigureAwait(false);
        StationClearance? clearance = await clearances.ReadByCycleAsync(cycle.CycleId, cancellationToken).ConfigureAwait(false);
        bool chargerHeld = await dbContext.Set<StationExclusivityRow>().AsNoTracking()
            .AnyAsync(
                row => row.MapId == cycle.MapId && row.StationId == cycle.StationId &&
                       row.StationKind == StationExclusivityKinds.Charger && row.JourneyId == cycle.JourneyId,
                cancellationToken)
            .ConfigureAwait(false);
        return new Target(
            cycle, stop.StationId, stop.UpperId, clearance?.ClearanceId,
            clearance is { CompletedAt: not null } ? clearance : null, chargerHeld,
            clearance is { ConfirmedAt: not null, CompletedAt: null });
    }

    private Task<string> WriteAuditAsync(
        ManualStationClearanceRequest request,
        string? role,
        Target? target,
        RiotVehicleObservation? vehicle,
        string disposition,
        ManualStationClearanceConfirmation decision,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        audit.WriteAdministratorAsync(
            new GovernanceAuditEntry(
                AuditAction,
                GovernedObjectKind.StationExclusivity,
                target is null
                    ? request.StationId
                    : StationExclusivityManualRelease.ObjectId(target.Cycle.MapId, target.Cycle.StationId),
                null,
                decision.Decision.Outcome == FieldConfirmationDecision.Confirmed
                    ? GovernanceActionOutcome.Succeeded
                    : GovernanceActionOutcome.Failed,
                JsonSerializer.Serialize(
                    new
                    {
                        source = request.Source,
                        confirmationRequestId = request.ConfirmationRequestId,
                        agvId = request.AgvId,
                        vehicleKey = request.VehicleKey,
                        operatorId = request.OperatorId,
                        verificationMethod = request.VerificationMethod,
                        verifiedAt = request.VerifiedAt,
                        grantedRole = role,
                        stationId = request.StationId,
                        publicStationFunction = request.PublicStationFunction,
                        clearedCondition = request.ClearedCondition,
                        cycleId = target?.Cycle.CycleId,
                        cyclePhase = target?.Cycle.Phase,
                        vehicleFinalPosition = FinalPosition(vehicle),
                        oldOrderDisposition = disposition,
                        outcome = decision.Decision.Outcome,
                        reasonCode = decision.Decision.ProblemReasonCode,
                        stationReleased = decision.StationReleased
                    },
                    DetailOptions),
                ClaimedAdministratorRole: role),
            now,
            cancellationToken);

    /// <param name="ClearanceId">这个周期已有的清桩记录；没有为空（失败周期、保持状态的周期由确认这一刻开始并完成）。</param>
    /// <param name="CompletedClearance">已经完成的那一次清桩：同一次清桩的第二个确认号不再写、不再放。</param>
    /// <param name="ChargerHeld">这一趟此刻还持有这个桩的独占。</param>
    /// <param name="ConfirmationRecorded">清桩中：人工确认已经记下、清桩还没完成（旧单那时还没终结）。</param>
    private sealed record Target(
        ChargingCycleRow Cycle,
        string StationName,
        string UpperId,
        string? ClearanceId,
        StationClearance? CompletedClearance,
        bool ChargerHeld,
        bool ConfirmationRecorded);
}
