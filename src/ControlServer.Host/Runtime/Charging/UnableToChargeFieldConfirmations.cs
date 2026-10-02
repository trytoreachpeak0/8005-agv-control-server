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

/// <summary>一次 <c>UnableToChargeFieldConfirmationRequested</c>，照线上载荷（批次9-12，control-server#410）。</summary>
/// <param name="VehicleKey">按车队登记从 <paramref name="AgvId"/> 解出的 RIoT 车号；解不出为空（判拒绝）。</param>
/// <param name="RequestContentHash">
/// 只对载荷取的摘要（调度对齐第 1 条）：车载端重提时沿用同一个确认号、载荷逐字节相同，<c>messageId</c>、<c>sentAt</c>、会话代次都会换。
/// </param>
public sealed record UnableToChargeFieldConfirmationRequest(
    string AgvId,
    string? VehicleKey,
    string ConfirmationRequestId,
    long SessionGeneration,
    string RequestMessageId,
    string RequestContentHash,
    string ChargerStationId,
    string ObservedCondition,
    string? OperatorId,
    string VerificationMethod,
    DateTimeOffset VerifiedAt,
    DateTimeOffset ObservedAt);

/// <summary>协议 <c>chargingPolicyDecision</c> 的三个取值。</summary>
public static class ChargingPolicyDecisions
{
    /// <summary>清桩之后回统一电量队列，由候选链（control-server#404）重新分配；不是立刻改派。</summary>
    public const string ReassignCharger = "REASSIGN_CHARGER";

    /// <summary>名册里没有别的桩给这辆车：清桩之后排队等桩恢复，分配器照旧告警「无合格桩」。</summary>
    public const string RetryLater = "RETRY_LATER";

    /// <summary>没有别的桩、电量也等不起：服务端置人工充电等待，只由「充电后返回服务」解除。</summary>
    public const string ManualChargingHold = "MANUAL_CHARGING_HOLD";
}

/// <summary>
/// 现场确认充不上的判定（<c>REQ-0176</c>、<c>REQ-0177</c>；批次9-12，control-server#410）：系统事实不完整时，由 R-11 以个人身份确认这辆车已在这个桩上
/// 实际尝试开始充电、但充电没有建立。
/// </summary>
/// <remarks>
/// <para>
/// <b>至多判一次，按确认号</b>：同一个 <c>confirmationRequestId</c> 再来——重放、或换了 <c>messageId</c> 的重提——拿回存下的那一份判定，不重判；内容不同按冲突拒收
/// （<see cref="FieldConfirmationContentConflictException"/>）。拒绝也存：一次报告就是一次现场观察（<c>RECORD_FIELD_OBSERVATION</c>），连同管理员审计。
/// </para>
/// <para>
/// <b>谁能确认</b>：<see cref="FieldOperatorRoleRoster"/> 里持有 R-11 的人（角色不上线路，按 <c>operatorId</c> 查，与 control-server#406 同一份名单）。
/// R-13 是调度管理员，不是维护权限，不算。普通操作员的请求只作报告：记下、告警、<c>REJECTED</c>，不暂停桩、不改周期。
/// </para>
/// <para>
/// <b>只有「试过、没建立」才可确认</b>：<c>CONNECTION_FAILED</c>、<c>CHARGER_FAULT</c>。<c>CHARGER_UNREACHABLE</c>、<c>CHARGER_OCCUPIED</c> 是没到桩、或桩被占着，
/// <c>REQ-0175</c> 把它们列为不足以确认的一般异常：只记观察并告警。
/// </para>
/// <para>
/// <b>身份在判定那一刻重核，人工不能覆盖</b>：这辆车有一个未结束的周期、周期的目标桩就是 <c>chargerStationId</c>、周期没到 <c>COMPLETE</c>、本周期没读到过充电、
/// RIoT 此刻新鲜地读到车在线、停在这个桩上、没在充电，车的用途是这一趟的 <c>CHARGING</c>，人工清桩的出口可用（与引擎形成确认时同一个检查）。任何一项不成立都拒绝，
/// 读不到 RIoT 也拒绝——不以人工覆盖未知。
/// </para>
/// <para>
/// <b>确认之后与系统确认同一条路</b>：写下的行与 <c>JourneyRuntimeEngine.ConfirmUnableToChargeAsync</c> 逐项相同，主键与幂等键也相同（暂停事件
/// <c>StableGuid(周期, "unable-to-charge-hold")</c>、清桩记录 <c>StableGuid(周期, "station-clearance")</c>），所以系统与现场谁先谁后都只有一条暂停事件。
/// 暂停事件另记确认人、角色、认证时刻、现场处置（<c>observedCondition</c>），证据引用以 <see cref="EvidencePrefix"/> 开头标明来源。周期进清桩中、用途转
/// <c>CLEARING_MAINTENANCE</c>、旅程写 <see cref="ChargingExecutionReasons.UnableToChargeClearing"/>；之后的一切（清桩中的两张快照、旧单、人工清桩、收尾）
/// 由引擎下一轮的清桩分支照系统确认那样做——<c>VehicleBusinessStateSnapshot</c> 也是那一轮发出，与「充电后返回服务」之后由下一轮发快照同一个规则。
/// </para>
/// <para>
/// <b>系统已经确认过这一次充不上</b>（周期已因它进清桩中）：不写第二条事件，答 <c>CONFIRMED</c>；<c>chargingPolicyDecision</c> 与本周期之前那次现场确认给的相同，
/// 之前没有就按此刻的事实定。
/// </para>
/// <para>
/// <b><c>chargingPolicyDecision</c> 只由服务端的事实定</b>（<c>DECIDE_CHARGING_POLICY_CENTRALLY</c>）：见 <see cref="DecidePolicyAsync"/>。拒绝时为空——拒绝即
/// 服务端什么都没改，不宣称一个并没做的决定。
/// </para>
/// <para>
/// <b>并发</b>：周期按并发令牌改、暂停事件按主键插，与引擎同一刻形成时只有一方写成；输的一方回滚到保存点，从头再判一次（读到的已是赢的那一方留下的）。RIoT 在事务之外读。
/// </para>
/// </remarks>
public sealed class UnableToChargeFieldConfirmations(
    ControlServerDbContext dbContext,
    IFieldConfirmationRequestStore requests,
    IRiotVehicleFacts vehicleFacts,
    FieldOperatorRoleRoster roles,
    IChargerRoster chargerRoster,
    IChargingHoldStore chargingHolds,
    IManualChargingHoldStore manualHolds,
    IChargingPolicyResolver chargingPolicy,
    IGovernanceAuditWriter audit,
    IOptions<JourneyRuntimeOptions> runtimeOptions,
    TimeProvider timeProvider,
    ILogger<UnableToChargeFieldConfirmations> logger,
    StationClearanceExit? clearanceExit = null)
{
    /// <summary>不持有 R-11。</summary>
    public const string NotAuthorized = "RECOVERY_AUTHENTICATION_FAILED";

    /// <summary>这辆车周期的目标桩不是请求里的桩。</summary>
    public const string StationMismatch = "STATION_MISMATCH";

    /// <summary>观察到的情形不是「试过、没建立」，或车此刻不在可以确认的状态（含 RIoT 读不到）。</summary>
    public const string NotAllowedInState = "ACTION_NOT_ALLOWED_IN_STATE";

    /// <summary>每一次判定写的管理员审计动作。</summary>
    public const string AuditAction = "UNABLE_TO_CHARGE_FIELD_CONFIRMATION";

    /// <summary>现场确认写下的暂停事件，证据引用以它开头。</summary>
    public const string EvidencePrefix = "FIELD_CONFIRMATION:";

    /// <summary>「现场确认充不上」的权限（<c>REQ-0176</c>）：只有 R-11。</summary>
    public static IReadOnlyList<string> ConfirmingRoles { get; } = [FieldOperatorRoleRoster.EquipmentMaintenance];

    /// <summary>可以确认的观察：桩在、试过、没建立。</summary>
    public static IReadOnlySet<string> ConfirmableConditions { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "CONNECTION_FAILED", "CHARGER_FAULT" };

    private static readonly Action<ILogger, string, string, string, string, string, string, Exception?> LogConfirmed =
        LoggerMessage.Define<string, string, string, string, string, string>(
            LogLevel.Warning,
            new EventId(2290, nameof(LogConfirmed)),
            "UNABLE_TO_CHARGE_FIELD_CONFIRMED: {OperatorId} ({Role}) confirmed on site that vehicle {AgvId} could not charge at " +
            "charger {StationId} ({Condition}); charging policy decision {Decision}. The charger is paused for all 8005 allocation " +
            "(root cause UNKNOWN) and the vehicle stays where it is until a person moves it off and confirms the charger clear.");

    private static readonly Action<ILogger, string, string, string, string, string, string, Exception?> LogReported =
        LoggerMessage.Define<string, string, string, string, string, string>(
            LogLevel.Warning,
            new EventId(2291, nameof(LogReported)),
            "UNABLE_TO_CHARGE_FIELD_REPORT: {OperatorId} reported vehicle {AgvId} at charger {StationId} as {Condition}; not " +
            "confirmed ({ReasonCode}: {Detail}). Recorded as a field observation; the charger is not paused and the cycle is " +
            "unchanged. Only a person holding R-11 can confirm an unable-to-charge, and only for a vehicle standing on its charger.");

    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private readonly JourneyRuntimeOptions _runtime = runtimeOptions.Value;

    /// <summary>判定并答它；同一个确认号判过的，答存下的那一份。</summary>
    public async Task<UnableToChargeFieldConfirmation> DecideAsync(
        UnableToChargeFieldConfirmationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        FieldConfirmationRequestIdentity identity = new(
            request.ConfirmationRequestId, request.AgvId, request.SessionGeneration, request.RequestMessageId,
            request.RequestContentHash, request.OperatorId ?? "", request.VerificationMethod, request.VerifiedAt,
            request.ObservedAt);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            ForgetWhatThisWrote();
            if (await requests.ReadUnableToChargeAsync(request.ConfirmationRequestId, cancellationToken)
                    .ConfigureAwait(false) is { } decided)
            {
                if (decided.Request.RequestContentHash != request.RequestContentHash || decided.Request.AgvId != request.AgvId)
                {
                    throw new FieldConfirmationContentConflictException(
                        $"Confirmation request {request.ConfirmationRequestId} was replayed with different content.");
                }
                return decided;
            }

            Facts facts = await ReadFactsAsync(request, cancellationToken).ConfigureAwait(false);
            string? role = roles.GrantedRole(request.OperatorId, ConfirmingRoles);
            (string? code, string? field, string? message) = Judge(request, role, facts);
            DateTimeOffset now = timeProvider.GetUtcNow();
            (string? policy, bool newlyDecided) = code is null
                ? await PolicyForAsync(request, facts, cancellationToken).ConfigureAwait(false)
                : (null, false);

            DecisionTransaction transaction = await DecisionTransaction.BeginAsync(dbContext, cancellationToken)
                .ConfigureAwait(false);
            await using (transaction)
            {
                if (code is null && !facts.AlreadyConfirmed &&
                    !await FormAsync(request, role!, facts, now, cancellationToken).ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (code is not null && facts.Cycle is { } judged &&
                    await dbContext.Set<ChargingCycleRow>().AsNoTracking()
                        .Where(row => row.CycleId == judged.CycleId).Select(row => (long?)row.Version)
                        .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) != judged.Version)
                {
                    // Refused on facts that moved under the reads (the engine formed or ended the cycle meanwhile): judged again.
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (policy == ChargingPolicyDecisions.ManualChargingHold && newlyDecided)
                {
                    _ = await manualHolds.PlaceAsync(
                            JourneyPlanBuilder.StableGuid(facts.Cycle!.CycleId, "unable-to-charge-field-manual-hold"),
                            facts.Cycle.VehicleKey, ManualChargingHoldReasons.UnableToChargeLowBattery, now, cancellationToken)
                        .ConfigureAwait(false);
                }

                UnableToChargeFieldConfirmation decision = new(
                    identity,
                    request.ChargerStationId,
                    request.ObservedCondition,
                    new FieldConfirmationDecision(
                        code is null ? FieldConfirmationDecision.Confirmed : FieldConfirmationDecision.Rejected,
                        code, field, message, now),
                    policy);
                UnableToChargeFieldConfirmation stored = await requests
                    .DecideUnableToChargeAsync(decision, cancellationToken).ConfigureAwait(false);
                if (stored.Request.RequestMessageId != request.RequestMessageId || stored.Decision.DecidedAt != now)
                {
                    // The same number was decided through another context in between: that decision is the one.
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    ForgetWhatThisWrote();
                    return stored;
                }

                await WriteAuditAsync(request, role, facts, stored, now, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                if (code is null)
                {
                    LogConfirmed(
                        logger, request.OperatorId!.Trim(), role!, request.AgvId, request.ChargerStationId, request.ObservedCondition,
                        policy!, null);
                }
                else
                {
                    LogReported(
                        logger, request.OperatorId ?? "(none)", request.AgvId, request.ChargerStationId, request.ObservedCondition,
                        code, message!, null);
                }
                return stored;
            }
        }

        throw new InvalidOperationException(
            $"Unable-to-charge field confirmation {request.ConfirmationRequestId} lost every race it entered; nothing was written.");
    }

    /// <summary>
    /// 形成「已确认充不上」，与引擎那一段写同样的行、同样的键：周期按读到的版本改为 <c>UNABLE_TO_CHARGE</c>／清桩中，用途转 <c>CLEARING_MAINTENANCE</c>，
    /// 暂停事件、清桩记录开始、旅程写码，一次保存。被别人先改过（周期版本、用途、暂停事件的主键）答假，调用方回滚重判。
    /// </summary>
    private async Task<bool> FormAsync(
        UnableToChargeFieldConfirmationRequest request,
        string role,
        Facts facts,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ChargingCycleRow cycle = facts.Cycle!;
        int changed = await dbContext.Set<ChargingCycleRow>()
            .Where(row => row.CycleId == cycle.CycleId && row.Version == cycle.Version && row.Phase == ChargingCyclePhases.Active)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.WireState, ChargingCycleWireStates.UnableToCharge)
                    .SetProperty(row => row.Phase, ChargingCyclePhases.Clearing)
                    .SetProperty(row => row.Version, row => row.Version + 1),
                cancellationToken)
            .ConfigureAwait(false);
        if (changed != 1 ||
            !await VehiclePurposeClaimTransition.StageAsync(
                    dbContext, cycle.VehicleKey, cycle.JourneyId, VehiclePurposes.Charging, VehiclePurposes.ClearingMaintenance,
                    now, ChargingStationHoldTriggers.UnableToChargeConfirmed, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        dbContext.Add(new ChargingStationAllocationHoldRow
        {
            HoldId = JourneyPlanBuilder.StableGuid(cycle.CycleId, "unable-to-charge-hold"),
            IdempotencyKey = JourneyPlanBuilder.StableGuid(
                ChargingStationHoldTriggers.UnableToChargeConfirmed + "|" + cycle.CycleId, "charging-station-hold"),
            Trigger = ChargingStationHoldTriggers.UnableToChargeConfirmed,
            RootCause = ChargingHoldRootCauses.Unknown,
            MapId = cycle.MapId,
            StationId = cycle.StationId,
            ChargerRosterVersion = cycle.ChargerRosterVersion,
            VehicleKey = cycle.VehicleKey,
            ReservationRecordId = facts.ReservationRecordId,
            CycleId = cycle.CycleId,
            UpperId = facts.UpperId,
            OrderId = facts.Order?.OrderId,
            ArrivedAt = cycle.ArrivedAt,
            ChargingStartedAt = null,
            // What the person saw: the moment they observed the failed start, by their own report.
            FailedAt = request.ObservedAt,
            FinalHangAt = facts.Order is { Kind: RiotOrderObservationKind.Active, OrderState: RiotOrderState.Hang } ? now : null,
            ConfirmedAt = now,
            HeldAt = now,
            RawPositionJson = JsonSerializer.Serialize(facts.Vehicle, DetailOptions),
            RawOrderJson = facts.Order is null ? null : JsonSerializer.Serialize(facts.Order, DetailOptions),
            RawActionResultJson = null,
            RawBatteryJson = JsonSerializer.Serialize(
                new { facts.Vehicle!.BatteryPercent, facts.Vehicle.BatteryState, facts.Vehicle.ObservedAt }, DetailOptions),
            EvidenceReference = string.Create(
                CultureInfo.InvariantCulture,
                $"{EvidencePrefix}{request.ConfirmationRequestId}; agv {request.AgvId}; observed {request.ObservedCondition} at {request.ObservedAt:O}"),
            ConfirmedByPersonId = request.OperatorId!.Trim(),
            ConfirmedByRole = role,
            ConfirmedAuthenticatedAt = request.VerifiedAt,
            SiteDisposition = request.ObservedCondition,
        });
        if (facts.ClearanceId is null)
        {
            dbContext.Add(new StationClearanceRow
            {
                ClearanceId = JourneyPlanBuilder.StableGuid(cycle.CycleId, "station-clearance"),
                CycleId = cycle.CycleId,
                VehicleKey = cycle.VehicleKey,
                MapId = cycle.MapId,
                StationId = cycle.StationId,
                StartedAt = now,
                AssistantsJson = "[]",
            });
        }
        JourneyRuntimeRow journey = await dbContext.JourneyRuntimes
            .SingleAsync(row => row.JourneyId == cycle.JourneyId, cancellationToken).ConfigureAwait(false);
        journey.SetBlockReason(ChargingExecutionReasons.UnableToChargeClearing, now);
        journey.UpdatedAt = now;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // The engine formed the same confirmation in between (the same hold id): the rerun reads it. Anything else is not
            // a lost race and goes on up.
            string holdId = JourneyPlanBuilder.StableGuid(cycle.CycleId, "unable-to-charge-hold");
            ForgetWhatThisWrote();
            if (await dbContext.Set<ChargingStationAllocationHoldRow>().AsNoTracking()
                    .AnyAsync(row => row.HoldId == holdId, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
            throw;
        }
    }

    private (string? Code, string? Field, string? Message) Judge(
        UnableToChargeFieldConfirmationRequest request, string? role, Facts facts)
    {
        if (role is null)
        {
            return (NotAuthorized, "payload.operator.operatorId",
                "This operator does not hold R-11 in the server's field operator roster: the report is recorded, but only an " +
                "equipment maintenance person (R-11) can confirm that the vehicle could not charge.");
        }
        if (!ConfirmableConditions.Contains(request.ObservedCondition))
        {
            return (NotAllowedInState, "payload.observedCondition",
                $"{request.ObservedCondition} is not a charge that was tried and not established (REQ-0175, REQ-0176): it is " +
                "recorded as a field observation, and the charging cycle goes on as before.");
        }
        if (facts.Cycle is not { } cycle)
        {
            return (NotAllowedInState, "payload.chargerStationId", "The vehicle has no charging cycle in progress.");
        }
        if (!string.Equals(request.ChargerStationId, facts.StationName, StringComparison.Ordinal) &&
            !string.Equals(request.ChargerStationId, cycle.StationId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return (StationMismatch, "payload.chargerStationId",
                $"The vehicle's charging cycle is for charger {facts.StationName}, not {request.ChargerStationId}.");
        }
        if (cycle.Phase == ChargingCyclePhases.Clearing)
        {
            return facts.AlreadyConfirmed
                ? (null, null, null)
                : (NotAllowedInState, "payload.chargerStationId",
                    "The charging cycle is already clearing after an interruption or no progress, not after an unable-to-charge.");
        }
        if (cycle.WireState == ChargingCycleWireStates.Complete)
        {
            return (NotAllowedInState, "payload.chargerStationId", "The charging cycle is already complete.");
        }
        if (cycle.FirstChargingSeenAt is not null)
        {
            return (NotAllowedInState, "payload.chargerStationId",
                "RIoT has read this vehicle charging in this cycle: charging was established, so this is not an unable-to-charge.");
        }
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (facts.Vehicle is not { Connected: true } vehicle ||
            vehicle.ObservedAt > now || now - vehicle.ObservedAt > _runtime.MaximumEvidenceAge)
        {
            return (NotAllowedInState, "payload.chargerStationId",
                "RIoT gives no fresh reading of the vehicle now; an unknown position is not confirmed by hand.");
        }
        if (vehicle.CurrentStationId != cycle.StationId ||
            !string.Equals(vehicle.CurrentMap, _runtime.MapIdentity, StringComparison.Ordinal) ||
            vehicle.Speed is > 0d)
        {
            return (NotAllowedInState, "payload.chargerStationId",
                $"RIoT does not read the vehicle standing on charger {facts.StationName} now.");
        }
        if (string.Equals(vehicle.BatteryState, BatteryEligibility.ChargingBatteryState, StringComparison.Ordinal))
        {
            return (NotAllowedInState, "payload.chargerStationId", "RIoT reads the vehicle charging now.");
        }
        if (!facts.ChargingClaimIsThisJourneys)
        {
            return (NotAllowedInState, "payload.chargerStationId",
                "The vehicle is not held for this charging journey any more.");
        }
        if ((clearanceExit is null ? StationClearanceExit.NoEntry : clearanceExit.Unavailable()) is { } unavailable)
        {
            return (NotAllowedInState, "payload.chargerStationId",
                $"Nobody could confirm the charger clear afterwards ({unavailable}), so the vehicle is not paused for a clearance.");
        }
        return (null, null, null);
    }

    /// <summary>
    /// 确认时的 <c>chargingPolicyDecision</c>：本周期之前已有一次现场确认的，照它（不重新置人工充电等待）；否则按此刻的事实定（<see cref="DecidePolicyAsync"/>）。
    /// </summary>
    private async Task<(string Policy, bool NewlyDecided)> PolicyForAsync(
        UnableToChargeFieldConfirmationRequest request, Facts facts, CancellationToken cancellationToken)
    {
        ChargingCycleRow cycle = facts.Cycle!;
        if (facts.AlreadyConfirmed)
        {
            // Compared in memory: SQLite cannot compare a DateTimeOffset.
            UnableToChargeFieldConfirmationRow[] earlier = await dbContext.Set<UnableToChargeFieldConfirmationRow>().AsNoTracking()
                .Where(row => row.AgvId == request.AgvId && row.Outcome == FieldConfirmationDecision.Confirmed &&
                              row.ChargingPolicyDecision != null)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            if (earlier.Where(row => row.DecidedAt >= cycle.AllocatedAt).OrderBy(row => row.DecidedAt).FirstOrDefault() is { } first)
            {
                return (first.ChargingPolicyDecision!, false);
            }
        }
        return (await DecidePolicyAsync(cycle, facts.Vehicle?.BatteryPercent, cancellationToken).ConfigureAwait(false), true);
    }

    /// <summary>
    /// 服务端定的充电策略（<c>DECIDE_CHARGING_POLICY_CENTRALLY</c>）。只看服务端的事实，请求里的什么都不看：
    /// <list type="bullet">
    /// <item>此刻生效的名册里，同一张地图上还有别的桩可给这辆车（车辆范围含它）、且那个桩没有未恢复的分配暂停：<see cref="ChargingPolicyDecisions.ReassignCharger"/>。</item>
    /// <item>没有，而车此刻的电量低于这个周期冻结的策略的最低任务后电量余量（等不起桩恢复）：<see cref="ChargingPolicyDecisions.ManualChargingHold"/>。</item>
    /// <item>其余（没有别的桩、电量够等，或读不到电量、读不回策略）：<see cref="ChargingPolicyDecisions.RetryLater"/>。</item>
    /// </list>
    /// </summary>
    private async Task<string> DecidePolicyAsync(ChargingCycleRow cycle, int? batteryPercent, CancellationToken cancellationToken)
    {
        ChargerRosterVersion? roster = await chargerRoster.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        foreach (ChargerRosterEntry charger in roster?.Chargers ?? [])
        {
            if (charger.MapId == cycle.MapId && charger.StationId != cycle.StationId &&
                (charger.VehicleScope.Count == 0 || charger.VehicleScope.Contains(cycle.VehicleKey, StringComparer.Ordinal)) &&
                (await chargingHolds.ListActiveStationHoldsAsync(charger.MapId, charger.StationId, cancellationToken)
                    .ConfigureAwait(false)).Count == 0)
            {
                return ChargingPolicyDecisions.ReassignCharger;
            }
        }

        int? margin;
        try
        {
            margin = (await chargingPolicy.ReadFrozenAsync(cycle.ChargingPolicyVersion, cancellationToken).ConfigureAwait(false))
                .Content.MinimumPostTaskBatteryMarginPercent;
        }
        catch (InvalidOperationException)
        {
            margin = null;
        }
        return batteryPercent is { } battery && margin is { } line && battery < line
            ? ChargingPolicyDecisions.ManualChargingHold
            : ChargingPolicyDecisions.RetryLater;
    }

    private async Task<Facts> ReadFactsAsync(UnableToChargeFieldConfirmationRequest request, CancellationToken cancellationToken)
    {
        if (request.VehicleKey is not { } vehicleKey)
        {
            return new Facts(null, "", "", false, null, null, false, null, null);
        }
        // RIoT first, then the database in one go: an engine round that forms the same confirmation in between is then either
        // wholly before these reads or caught by the cycle's version when writing (DecideAsync re-checks it for a refusal too).
        RiotVehicleObservation? vehicle = await ReadVehicleAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
        ChargingCycleRow? cycle = await dbContext.Set<ChargingCycleRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.VehicleKey == vehicleKey && row.Phase != ChargingCyclePhases.Ended, cancellationToken)
            .ConfigureAwait(false);
        if (cycle is null)
        {
            return new Facts(null, "", "", false, null, null, false, vehicle, null);
        }

        JourneyStopRow stop = await dbContext.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == cycle.JourneyId, cancellationToken).ConfigureAwait(false);
        bool alreadyConfirmed = cycle.Phase == ChargingCyclePhases.Clearing &&
                                await dbContext.Set<ChargingStationAllocationHoldRow>().AsNoTracking()
                                    .AnyAsync(
                                        row => row.CycleId == cycle.CycleId &&
                                               row.Trigger == ChargingStationHoldTriggers.UnableToChargeConfirmed,
                                        cancellationToken)
                                    .ConfigureAwait(false);
        string? clearanceId = await dbContext.Set<StationClearanceRow>().AsNoTracking()
            .Where(row => row.CycleId == cycle.CycleId).Select(row => row.ClearanceId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        bool claimed = await dbContext.Set<VehiclePurposeClaimRow>().AsNoTracking()
            .AnyAsync(
                row => row.VehicleKey == vehicleKey && row.JourneyId == cycle.JourneyId && row.Purpose == VehiclePurposes.Charging,
                cancellationToken)
            .ConfigureAwait(false);
        string? reservation = await dbContext.Set<StationExclusivityRow>().AsNoTracking()
            .Where(row => row.MapId == cycle.MapId && row.StationId == cycle.StationId &&
                          row.StationKind == StationExclusivityKinds.Charger && row.JourneyId == cycle.JourneyId)
            .Select(row => row.RecordId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        RiotOrderObservation? order = await ReadOrderAsync(stop.UpperId, cancellationToken).ConfigureAwait(false);
        return new Facts(cycle, stop.StationId, stop.UpperId, alreadyConfirmed, clearanceId, reservation, claimed, vehicle, order);
    }

    private async Task<RiotVehicleObservation?> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken)
    {
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

    /// <summary>旧单此刻的样子，只作证据记进暂停事件；读不到为空，不影响判定。</summary>
    private async Task<RiotOrderObservation?> ReadOrderAsync(string upperId, CancellationToken cancellationToken)
    {
        try
        {
            return await vehicleFacts.ReconcileByUpperIdAsync(upperId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private Task<string> WriteAuditAsync(
        UnableToChargeFieldConfirmationRequest request,
        string? role,
        Facts facts,
        UnableToChargeFieldConfirmation decision,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        audit.WriteAdministratorAsync(
            new GovernanceAuditEntry(
                AuditAction,
                GovernedObjectKind.StationExclusivity,
                facts.Cycle is null
                    ? request.ChargerStationId
                    : StationExclusivityManualRelease.ObjectId(facts.Cycle.MapId, facts.Cycle.StationId),
                null,
                decision.Decision.Outcome == FieldConfirmationDecision.Confirmed
                    ? GovernanceActionOutcome.Succeeded
                    : GovernanceActionOutcome.Failed,
                JsonSerializer.Serialize(
                    new
                    {
                        confirmationRequestId = request.ConfirmationRequestId,
                        agvId = request.AgvId,
                        vehicleKey = request.VehicleKey,
                        operatorId = request.OperatorId,
                        verificationMethod = request.VerificationMethod,
                        verifiedAt = request.VerifiedAt,
                        grantedRole = role,
                        chargerStationId = request.ChargerStationId,
                        observedCondition = request.ObservedCondition,
                        observedAt = request.ObservedAt,
                        cycleId = facts.Cycle?.CycleId,
                        cyclePhase = facts.Cycle?.Phase,
                        cycleWireState = facts.Cycle?.WireState,
                        alreadyConfirmedBySystem = facts.AlreadyConfirmed,
                        vehicleStationId = facts.Vehicle?.CurrentStationId,
                        vehicleObservedAt = facts.Vehicle?.ObservedAt,
                        outcome = decision.Decision.Outcome,
                        reasonCode = decision.Decision.ProblemReasonCode,
                        chargingPolicyDecision = decision.ChargingPolicyDecision
                    },
                    DetailOptions),
                ClaimedAdministratorRole: role),
            now,
            cancellationToken);

    /// <summary>一次尝试回滚之后，丢掉它留在变更跟踪里的行。只丢这个判定会写的那几类：入站处理器的收件箱行等调用方的行原样留着。</summary>
    private void ForgetWhatThisWrote()
    {
        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry in dbContext.ChangeTracker.Entries()
                     .Where(entry => entry.Entity is ChargingStationAllocationHoldRow or StationClearanceRow
                         or UnableToChargeFieldConfirmationRow or ChargingCycleRow or JourneyRuntimeRow
                         or VehiclePurposeClaimRow or VehiclePurposeClaimRecordRow or ManualChargingHoldRow
                         or ManualChargingHoldRecordRow)
                     .ToArray())
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// 判定的那一个事务：自己开一个；调用方已经开着时（入站处理器的收件箱事务）在它里面立一个保存点，回滚只回到保存点。
    /// </summary>
    private sealed class DecisionTransaction : IAsyncDisposable
    {
        private const string Savepoint = "unable_to_charge_field_confirmation";
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

    /// <param name="Cycle">这辆车未结束的周期；没有为空。</param>
    /// <param name="StationName">周期那条 <c>CHARGER</c> 腿的站名。</param>
    /// <param name="AlreadyConfirmed">周期已因这一次充不上进了清桩中（系统先确认了，或另一个确认号先确认了）。</param>
    /// <param name="ClearanceId">这个周期已有的清桩记录；没有为空。</param>
    /// <param name="ReservationRecordId">这一趟此刻持有这个桩的独占时，那一行的经过 id。</param>
    /// <param name="ChargingClaimIsThisJourneys">车的用途此刻是这一趟的 <c>CHARGING</c>。</param>
    private sealed record Facts(
        ChargingCycleRow? Cycle,
        string StationName,
        string UpperId,
        bool AlreadyConfirmed,
        string? ClearanceId,
        string? ReservationRecordId,
        bool ChargingClaimIsThisJourneys,
        RiotVehicleObservation? Vehicle,
        RiotOrderObservation? Order);
}
