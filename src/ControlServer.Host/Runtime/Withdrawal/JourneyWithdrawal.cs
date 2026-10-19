using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Release;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Withdrawal;

/// <summary>
/// 一次人工撤下旅程的请求（control-server#771）。文本字段可空：缺了是一条拒绝理由，不是格式错误。
/// </summary>
/// <param name="JourneyId">要撤的那一趟。点名，不推断「这辆车当前的旅程」。</param>
/// <param name="VehicleKey">那一趟的车（RIoT <c>VehicleKey</c>），必须与旅程行对得上。</param>
/// <param name="SiteVerification">现场核实的记录引用（照片、会话记录的位置）。</param>
/// <param name="RiotVerification">现场看过 RIoT、本车没有未结束订单的记录引用。</param>
/// <param name="SlotFacts">
/// 现场看过的仓位事实，<c>&lt;仓号&gt;=&lt;事实&gt;</c>，逗号分隔，例如 <c>5=EMPTY_LOCKED</c>。只在旅程挂着一次没结的装货时要。
/// </param>
/// <param name="CargoLocation">那一篮此刻在哪里（例如「在机台上」）。只在旅程挂着一次没结的装货时要。</param>
/// <param name="ClaimedRole">操作员自述的角色，照录，不认证任何东西。</param>
/// <param name="ConfirmationCode">演练给出的确认码。撤下时必填；演练时不看。</param>
public sealed record JourneyWithdrawalRequest(
    string? JourneyId,
    string? VehicleKey,
    string? OperatorId,
    string? Reason,
    string? SiteVerification,
    string? RiotVerification,
    string? SlotFacts = null,
    string? CargoLocation = null,
    string? ClaimedRole = null,
    string? ConfirmationCode = null);

/// <summary>撤下会动到的一条需求：撤下之前的样子，和它的去向。</summary>
public sealed record JourneyWithdrawalDemand(
    string DemandId,
    string TransportDemandKey,
    string MembershipStatus,
    string DemandStatus,
    string PickupStopId,
    string Disposition);

/// <summary>旅程挂着的那一次没结的装货：撤下时结成 <c>Cancelled</c>。</summary>
/// <param name="ReportedSlots">车报来的那次结果里，每个目标仓位的读数（<c>5:EMPTY:LOCKED:RESET</c>）。</param>
public sealed record JourneyWithdrawalOperation(
    string SlotOperationAttemptId,
    string DemandId,
    string Status,
    IReadOnlyList<int> TargetSlots,
    IReadOnlyList<string> ReportedSlots);

/// <summary>这一趟名下的一条订单意图。撤下不改它，只列出来、绑进确认码。</summary>
public sealed record JourneyWithdrawalIntent(string UpperId, string Status, string? OrderId);

/// <summary>这一趟名下一条还没建新单的重建记录（待发或停住）。撤下把它转成 <c>ENDED</c>、记下操作人（control-server#771，#770 之后）。</summary>
public sealed record JourneyWithdrawalRebuild(string RebuildId, string State, string? StoppedReason, string EndedUpperId);

/// <summary>
/// 撤下会做的每一件事。演练与撤下各算一次，两次算出的确认码相同才执行：中间任何一项变了，执行就拒。
/// </summary>
public sealed record JourneyWithdrawalPlan(
    string JourneyId,
    string AgvId,
    string VehicleKey,
    string Stage,
    string? BlockReasonCode,
    DateTimeOffset JourneyUpdatedAt,
    IReadOnlyList<JourneyWithdrawalDemand> Demands,
    JourneyWithdrawalOperation? UnresolvedLoad,
    IReadOnlyList<JourneyWithdrawalIntent> OrderIntents,
    string? SessionReadiness,
    string? SessionReasonCode,
    string? SessionUnsettledAttempt = null,
    IReadOnlyList<int>? SessionActiveUnlockSlots = null,
    long? SessionGeneration = null,
    long? SessionSafetyRevision = null,
    IReadOnlyList<string>? SessionSafetyReasonCodes = null,
    DateTimeOffset? SessionSafetySummaryObservedAt = null,
    DateTimeOffset? SessionUpdatedAt = null,
    IReadOnlyList<JourneyWithdrawalRebuild>? Rebuilds = null);

/// <summary>演练或撤下的结果。</summary>
/// <param name="Outcome"><c>PLANNED</c>、<c>WITHDRAWN</c>、<c>ALREADY_DONE</c> 或 <c>REJECTED</c>。</param>
/// <param name="AuditRecordId">撤下（含被拒）写的管理员审计；演练不写，为空。</param>
public sealed record JourneyWithdrawalResult(
    string Outcome,
    IReadOnlyList<string> Codes,
    JourneyWithdrawalPlan? Plan,
    string? ConfirmationCode,
    string? AuditRecordId);

/// <summary>
/// 人工撤下一趟旅程（control-server#771）：车上没货时，旅程收尾，需求退回待派、不抑制；挂着一次结果未知的装货时，那一次结成取消。
/// </summary>
public static class JourneyWithdrawal
{
    /// <summary>旅程收尾码。</summary>
    public const string WithdrawnReason = "WITHDRAWN_BY_OPERATOR";

    /// <summary>每一次撤下请求（含被拒）写的管理员审计动作。</summary>
    public const string AuditAction = "JOURNEY_MANUAL_WITHDRAWAL";

    /// <summary>需求的去向：退回待派。</summary>
    public const string ReleaseToBacklog = "RELEASE_TO_BACKLOG";

    /// <summary>现场看过、仓是空的、门锁着。今天唯一接受的仓位事实（D2：真有货就拒绝撤下）。</summary>
    public const string SlotEmptyLocked = "EMPTY_LOCKED";

    /// <summary>现场看见仓里有货。</summary>
    public const string SlotOccupied = "OCCUPIED";

    public const string OperatorRequired = "OPERATOR_REQUIRED";
    public const string ReasonRequired = "REASON_REQUIRED";
    public const string SiteVerificationRequired = "SITE_VERIFICATION_REQUIRED";
    public const string RiotVerificationRequired = "RIOT_VERIFICATION_REQUIRED";
    public const string JourneyRequired = "JOURNEY_REQUIRED";
    public const string VehicleRequired = "VEHICLE_REQUIRED";
    public const string FieldTooLong = "FIELD_TOO_LONG";
    public const string JourneyNotFound = "JOURNEY_NOT_FOUND";
    public const string JourneyAlreadyCompleted = "JOURNEY_ALREADY_COMPLETED";
    public const string VehicleMismatch = "VEHICLE_MISMATCH";
    public const string JourneyKindNotWithdrawable = "JOURNEY_KIND_NOT_WITHDRAWABLE";
    public const string CargoOnBoard = "CARGO_ON_BOARD";
    public const string OtherOperationUnresolved = "OTHER_OPERATION_UNRESOLVED";
    public const string SlotFactsRequired = "SLOT_FACTS_REQUIRED";
    public const string SlotFactsMalformed = "SLOT_FACTS_MALFORMED";
    public const string SlotFactsMismatch = "SLOT_FACTS_DO_NOT_COVER_THE_TARGET_SLOTS";
    public const string SiteReportsCargo = "SITE_REPORTS_CARGO";
    public const string CargoLocationRequired = "CARGO_LOCATION_REQUIRED";
    public const string SlotEvidenceMissing = "SLOT_EVIDENCE_MISSING";
    public const string SlotEvidenceNotEmpty = "SLOT_EVIDENCE_NOT_EMPTY";
    public const string DoorNotProvenClosed = "DOOR_NOT_PROVEN_CLOSED";
    public const string RecoverySessionOpen = "RECOVERY_SESSION_OPEN";
    public const string SlotFaultDeclarationOpen = "SLOT_FAULT_DECLARATION_OPEN";
    public const string RebuildRecordOpen = "REBUILD_RECORD_OPEN";
    public const string OrderOutcomeUnknown = "ORDER_OUTCOME_UNKNOWN";
    public const string FaultInEffect = "FAULT_IN_EFFECT";
    public const string EmergencyStopOpen = "EMERGENCY_STOP_OPEN";
    public const string FaultedCargoBound = "FAULTED_CARGO_BOUND";
    public const string UnsettledAttemptElsewhere = "VEHICLE_REPORTS_ANOTHER_UNSETTLED_ATTEMPT";
    public const string JourneyStateNotWithdrawable = "JOURNEY_STATE_NOT_WITHDRAWABLE";
    public const string OtherDemandRecoveryRequired = "OTHER_DEMAND_RECOVERY_REQUIRED";
    public const string ConfirmationRequired = "CONFIRMATION_REQUIRED";
    public const string ConfirmationStale = "CONFIRMATION_STALE";

    /// <summary>理由、两条核实记录与那一篮去向的最长长度（去掉首尾空白后的 UTF-16 码元）。</summary>
    public const int MaxTextLength = 500;

    /// <summary>车号、旅程号、操作员与自述角色的最长长度。</summary>
    public const int MaxIdentifierLength = 128;

    /// <summary>演练：只读，一行不写，不写审计。给出计划与确认码，或者拒绝的理由。</summary>
    public static async Task<JourneyWithdrawalResult> PlanAsync(
        ControlServerDbContext dbContext,
        JourneyWithdrawalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(request);
        Assessment assessment = await AssessAsync(dbContext, request, cancellationToken).ConfigureAwait(false);
        if (assessment.AlreadyWithdrawn)
        {
            return new JourneyWithdrawalResult(Rejected, [JourneyAlreadyCompleted], null, null, null);
        }
        return assessment.Codes.Count > 0
            ? new JourneyWithdrawalResult(Rejected, [.. assessment.Codes], assessment.Plan, null, null)
            : new JourneyWithdrawalResult(Planned, [], assessment.Plan, assessment.ConfirmationCode, null);
    }

    /// <summary>
    /// 撤下：重算计划，确认码对得上才写；每一次请求（含被拒）写一条管理员审计。<paramref name="publisher"/> 为空表示服务端停着
    /// （FieldOps 直接写库），收尾快照留在发件箱里，车重连后补发。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>判与写在同一个写事务里</b>（这台服务端的写事务是 <c>BEGIN IMMEDIATE</c>）：判完之后、写之前，不会有别的写者插进来改了计划。
    /// FieldOps 直接写库时服务端停着、库锁在手，本来就没有别的写者；将来服务端在线的入口经同一个方法，靠的是这一条。
    /// </para>
    /// <para>
    /// <b>写什么</b>，全在一次保存里：那一次没结的装货结成 <see cref="StationOperationStatus.Cancelled"/>（与对上的补偿、取消同一个终态，
    /// 之后车再报那一次的结果，服务端只记历史、不改任何东西）；它的需求若因那次结果被标成 <see cref="DemandExecutionStatus.RecoveryRequired"/>，
    /// 改回 <see cref="DemandExecutionStatus.Accepted"/>；旅程上每一条还开着的需求按释放改派退回待派（归属移除、积压行清掉受理标记，
    /// 不写抑制、等待年龄不重置）；旅程经收尾尾巴关闭（三套占用一起放，三张收尾快照进发件箱）。与站点期限放回最后一条（control-server#703）
    /// 同一个做法：车就停在这一站，不修订计划；这一站放回几条就给它补几版清单，较早几版因此照常退役。
    /// </para>
    /// <para>
    /// <b>保存之后</b>：服务端在线时把收尾快照发出去；不论在线与否，把车报来的、已经结掉的那一次从会话的待核实事实里拿掉，重判一次就绪
    /// （与握手时做的同一件事），所以撤下之后会话不再因那一次而 RecoveryRequired。
    /// </para>
    /// </remarks>
    public static async Task<JourneyWithdrawalResult> WithdrawAsync(
        ControlServerDbContext dbContext,
        IGovernanceAuditWriter audit,
        JourneyWithdrawalRequest request,
        DateTimeOffset now,
        OnboardJourneyPublisher? publisher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(request);

        IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        JourneyWithdrawalResult result;
        string? withdrawnVehicle = null;
        await using (transaction)
        {
            Assessment assessment = await AssessAsync(dbContext, request, cancellationToken).ConfigureAwait(false);
            if (assessment.AlreadyWithdrawn)
            {
                string id = await WriteAuditAsync(
                    audit, request, assessment, GovernanceActionOutcome.Succeeded, AlreadyDone, now, cancellationToken)
                    .ConfigureAwait(false);
                result = new JourneyWithdrawalResult(AlreadyDone, [], null, null, id);
            }
            else
            {
                List<string> codes = [.. assessment.Codes];
                if (codes.Count == 0 && Trimmed(request.ConfirmationCode) is null)
                {
                    codes.Add(ConfirmationRequired);
                }
                else if (codes.Count == 0 &&
                         !string.Equals(Trimmed(request.ConfirmationCode), assessment.ConfirmationCode, StringComparison.Ordinal))
                {
                    codes.Add(ConfirmationStale);
                }

                if (codes.Count > 0)
                {
                    string id = await WriteAuditAsync(
                        audit, request, assessment with { Codes = codes }, GovernanceActionOutcome.Failed, Rejected, now,
                        cancellationToken).ConfigureAwait(false);
                    // A stale code is answered with the plan as it now stands and its code, so the person reads it again first.
                    result = new JourneyWithdrawalResult(
                        Rejected, [.. codes], assessment.Plan,
                        codes is [ConfirmationStale] ? assessment.ConfirmationCode : null, id);
                }
                else
                {
                    await StageWithdrawalAsync(dbContext, assessment.Plan!, now, cancellationToken).ConfigureAwait(false);
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    string id = await WriteAuditAsync(
                        audit, request, assessment, GovernanceActionOutcome.Succeeded, Withdrawn, now, cancellationToken)
                        .ConfigureAwait(false);
                    withdrawnVehicle = assessment.Plan!.AgvId;
                    result = new JourneyWithdrawalResult(Withdrawn, [], assessment.Plan, assessment.ConfirmationCode, id);
                }
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (withdrawnVehicle is not null)
        {
            if (publisher is not null)
            {
                await JourneyClosure.SendAsync(publisher, dbContext, withdrawnVehicle, cancellationToken).ConfigureAwait(false);
            }
            await SettleSessionAsync(dbContext, withdrawnVehicle, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private const string Planned = "PLANNED";
    private const string Withdrawn = "WITHDRAWN";
    private const string AlreadyDone = "ALREADY_DONE";
    private const string Rejected = "REJECTED";

    /// <summary>建单结果未知的三种状态（与空闲返回的「结果未知」检查同一组，<c>IdleReturnEvaluator</c>）。</summary>
    private static readonly string[] UnknownOutcomeIntentStatuses =
        ["CREATE_ATTEMPTED", "RESULT_UNKNOWN", "TERMINAL_RECONCILIATION_REQUIRED"];

    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>判一次的结果：拒绝的理由（为空即可以撤）、计划与确认码。</summary>
    private sealed record Assessment(
        IReadOnlyList<string> Codes,
        JourneyWithdrawalPlan? Plan,
        string? ConfirmationCode,
        bool AlreadyWithdrawn);

    private sealed record SlotFact(int Slot, string Fact);

    /// <summary>判一次，只读。</summary>
    private static async Task<Assessment> AssessAsync(
        ControlServerDbContext dbContext,
        JourneyWithdrawalRequest request,
        CancellationToken cancellationToken)
    {
        List<string> codes = [];
        string? journeyId = Trimmed(request.JourneyId);
        string? vehicleKey = Trimmed(request.VehicleKey);
        if (Trimmed(request.OperatorId) is null) codes.Add(OperatorRequired);
        if (Trimmed(request.Reason) is null) codes.Add(ReasonRequired);
        if (Trimmed(request.SiteVerification) is null) codes.Add(SiteVerificationRequired);
        if (Trimmed(request.RiotVerification) is null) codes.Add(RiotVerificationRequired);
        if (journeyId is null) codes.Add(JourneyRequired);
        if (vehicleKey is null) codes.Add(VehicleRequired);
        if (!Fits(request)) codes.Add(FieldTooLong);
        if (journeyId is null || vehicleKey is null)
        {
            return Refused(codes);
        }

        JourneyRuntimeRow? runtime = await dbContext.JourneyRuntimes.AsNoTracking()
            .SingleOrDefaultAsync(row => row.JourneyId == journeyId, cancellationToken).ConfigureAwait(false);
        if (runtime is null)
        {
            codes.Add(JourneyNotFound);
            return Refused(codes);
        }
        if (!string.Equals(runtime.VehicleKey, vehicleKey, StringComparison.Ordinal))
        {
            codes.Add(VehicleMismatch);
            return Refused(codes);
        }
        if (runtime.Stage == JourneyRuntimeStage.Completed)
        {
            if (codes.Count == 0 && string.Equals(runtime.BlockReasonCode, WithdrawnReason, StringComparison.Ordinal))
            {
                return new Assessment([], null, null, AlreadyWithdrawn: true);
            }
            codes.Add(JourneyAlreadyCompleted);
            return Refused(codes);
        }
        if (runtime.CarriesNoDemand())
        {
            codes.Add(JourneyKindNotWithdrawable);
            return Refused(codes);
        }
        if (!InAWithdrawableState(runtime))
        {
            codes.Add(JourneyStateNotWithdrawable);
            return Refused(codes);
        }

        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
        JourneyStopDemand[] open = [.. stops.AllDemands.Where(item =>
            item.Demand.Status is not (DemandExecutionStatus.Succeeded or DemandExecutionStatus.Cancelled) &&
            item.Membership.Status is not (JourneyDemandStatuses.Unloaded or JourneyDemandStatuses.Terminated))];
        string[] attempts = [.. stops.AllDemands
            .SelectMany(item => new[] { item.Membership.LoadSlotOperationAttemptId, item.Membership.UnloadSlotOperationAttemptId })];
        StationOperationRow[] unresolved = await dbContext.StationOperations.AsNoTracking()
            .Where(row => attempts.Contains(row.SlotOperationAttemptId) &&
                          (row.Status == StationOperationStatus.Prepared || row.Status == StationOperationStatus.RecoveryRequired))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        StationOperationRow? unresolvedLoad = unresolved is [{ OperationType: SlotOperationType.Load } only] ? only : null;
        if (unresolved.Length > 0 && unresolvedLoad is null)
        {
            codes.Add(OtherOperationUnresolved);
        }

        // Cargo: a demand loaded, or loading on anything but the one load this exit settles, may be on board.
        if (open.Any(item => item.Membership.Status == JourneyDemandStatuses.Loaded ||
                             (item.Membership.Status == JourneyDemandStatuses.Loading &&
                              !string.Equals(item.Membership.LoadSlotOperationAttemptId, unresolvedLoad?.SlotOperationAttemptId,
                                  StringComparison.Ordinal))))
        {
            codes.Add(CargoOnBoard);
        }

        // Only the demand of the one load this exit settles may be marked for recovery (control-server#771 review S-2): another
        // such mark is a recovery result that did not reconcile (BlockedJourneyRelease, criterion 4), a fact nobody has
        // concluded, and releasing its demand would bury it.
        if (open.Any(item => item.Demand.Status == DemandExecutionStatus.RecoveryRequired &&
                             !string.Equals(item.Demand.DemandId, unresolvedLoad?.DemandId, StringComparison.Ordinal)))
        {
            codes.Add(OtherDemandRecoveryRequired);
        }

        SessionRecoveryRow? session = await dbContext.SessionRecoveries.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AgvId == runtime.AgvId, cancellationToken).ConfigureAwait(false);
        List<SlotFact> facts = [];
        JourneyWithdrawalOperation? operation = null;
        if (unresolvedLoad is not null)
        {
            int[] targets = JsonSerializer.Deserialize<int[]>(unresolvedLoad.TargetSlotsJson) ?? [];
            facts = JudgeSlotFacts(request, targets, codes);
            string[] reported = await JudgeReportedSlotsAsync(dbContext, unresolvedLoad, targets, codes, cancellationToken)
                .ConfigureAwait(false);
            if (!DoorsProvenClosed(session))
            {
                codes.Add(DoorNotProvenClosed);
            }
            operation = new JourneyWithdrawalOperation(
                unresolvedLoad.SlotOperationAttemptId, unresolvedLoad.DemandId, unresolvedLoad.Status.ToString(), targets, reported);
        }

        // onboard-hmi#372 / PR #376, item a: the attempt the vehicle last reported unsettled is the one this exit settles, or none.
        // Another one means the vehicle holds an operation this server is not settling here, and after the withdrawal it would
        // still hold it -- the next command overwrites only once the server has settled what the vehicle reports.
        if (session?.UnsettledSlotOperationAttemptId is { } reportedUnsettled &&
            !string.Equals(reportedUnsettled, unresolvedLoad?.SlotOperationAttemptId, StringComparison.Ordinal))
        {
            codes.Add(UnsettledAttemptElsewhere);
        }

        if (await dbContext.ExceptionRecoverySessions.AsNoTracking()
                .AnyAsync(row => row.AgvId == runtime.AgvId && row.State != "CLOSED", cancellationToken).ConfigureAwait(false) ||
            await dbContext.RecoveryWorkflows.AsNoTracking()
                .AnyAsync(row => row.AgvId == runtime.AgvId &&
                                 (row.State == RecoveryWorkflowState.AwaitingAuthorization ||
                                  row.State == RecoveryWorkflowState.CommandPending ||
                                  row.State == RecoveryWorkflowState.AwaitingResult),
                    cancellationToken).ConfigureAwait(false))
        {
            codes.Add(RecoverySessionOpen);
        }
        if (await dbContext.Set<SlotFaultDeclarationRow>().AsNoTracking()
                .AnyAsync(row => row.AgvId == runtime.AgvId && attempts.Contains(row.SlotOperationAttemptId) &&
                                 (row.State == SlotFaultDeclarationStates.Pending || row.State == SlotFaultDeclarationStates.Unreconciled),
                    cancellationToken).ConfigureAwait(false))
        {
            codes.Add(SlotFaultDeclarationOpen);
        }
        // control-server#771 first part: a rebuild record still waited on is refused; ending it lands after control-server#770.
        if (await dbContext.OwnOrderRebuilds.AsNoTracking()
                .AnyAsync(row => row.JourneyId == runtime.JourneyId &&
                                 row.State != OwnOrderRebuildStates.Rebuilt && row.State != OwnOrderRebuildStates.Ended,
                    cancellationToken).ConfigureAwait(false))
        {
            codes.Add(RebuildRecordOpen);
        }

        JourneyWithdrawalIntent[] intents = await JudgeIntentsAsync(dbContext, runtime, stops, codes, cancellationToken)
            .ConfigureAwait(false);

        VehicleFaultFact? fault = await new VehicleFaultStore(dbContext).ReadAsync(runtime.AgvId, cancellationToken)
            .ConfigureAwait(false);
        if (fault is not null && fault.Level != VehicleFaultLevel.None)
        {
            codes.Add(FaultInEffect);
        }
        if (await EmergencyStopSupervisor.HasOpenEpisodeAsync(
                new RiotOrderCommandAuditStore(dbContext), runtime.VehicleKey, cancellationToken).ConfigureAwait(false))
        {
            codes.Add(EmergencyStopOpen);
        }
        if (await dbContext.FaultedVehicleCargo.AsNoTracking()
                .AnyAsync(row => row.AgvId == runtime.AgvId && row.ReleasedAt == null, cancellationToken).ConfigureAwait(false))
        {
            codes.Add(FaultedCargoBound);
        }

        JourneyWithdrawalPlan plan = new(
            runtime.JourneyId,
            runtime.AgvId,
            runtime.VehicleKey,
            runtime.Stage.ToString(),
            runtime.BlockReasonCode,
            runtime.UpdatedAt,
            [.. open
                .OrderBy(item => item.Demand.DemandId, StringComparer.Ordinal)
                .Select(item => new JourneyWithdrawalDemand(
                    item.Demand.DemandId,
                    item.Demand.TransportDemandKey,
                    item.Membership.Status,
                    item.Demand.Status.ToString(),
                    item.Membership.PickupStopId,
                    ReleaseToBacklog))],
            operation,
            intents,
            session?.Readiness.ToString(),
            session?.ReasonCode,
            session?.UnsettledSlotOperationAttemptId,
            session is null ? null : JsonSerializer.Deserialize<int[]>(session.ActiveUnlockSlotsJson) ?? [],
            session?.SessionGeneration,
            session?.SafetyRevision,
            session?.SafetyReasonCodesJson is { } reasonCodes ? JsonSerializer.Deserialize<string[]>(reasonCodes) : null,
            session is { SafetyRevision: long revision }
                ? await new OnboardDispatchFactsReader(dbContext, Options.Create(new JourneyRuntimeOptions()), TimeProvider.System)
                    .SafetySummaryObservedAtAsync(session.AgvId, session.SessionGeneration, revision, cancellationToken)
                    .ConfigureAwait(false)
                : null,
            session?.UpdatedAt);
        string[] distinct = [.. codes.Distinct(StringComparer.Ordinal)];
        return new Assessment(
            distinct, plan, distinct.Length == 0 ? CodeOf(plan, facts, Trimmed(request.CargoLocation)) : null, false);
    }

    /// <summary>
    /// The states this first part withdraws from (control-server#771 review S-2), and no other: a journey blocked on the unknown load
    /// result (agv03, 10-19), one waiting on a lost or not-ready onboard session, or one on its way to its pickup (agv01, 10-18).
    /// </summary>
    /// <remarks>
    /// Every other block is somebody else's to settle first: <c>UNLOAD_RESULT_REQUIRES_RECOVERY</c> (cargo on board), the
    /// <c>*_NOT_RECONCILED</c> family and the unreleasable one (<see cref="BlockedJourneyRelease.IsUnreleasable"/>, whose exit is
    /// control-server#485), a trip handed to the exception recovery session. A journey later than its pickup with no block at all is
    /// the engine's, running.
    /// </remarks>
    private static bool InAWithdrawableState(JourneyRuntimeRow runtime) =>
        runtime.Stage == JourneyRuntimeStage.Blocked
            ? string.Equals(runtime.BlockReasonCode, LoadResultRequiresRecovery, StringComparison.Ordinal)
            : runtime.Stage == JourneyRuntimeStage.AwaitingPickupArrival ||
              runtime.BlockReasonCode is JourneyRuntimeEngine.OnboardSessionLostReason or OnboardSessionNotReady;

    private const string LoadResultRequiresRecovery = "LOAD_RESULT_REQUIRES_RECOVERY";

    private const string OnboardSessionNotReady = "ONBOARD_SESSION_NOT_READY";

    private static Assessment Refused(List<string> codes) => new([.. codes.Distinct(StringComparer.Ordinal)], null, null, false);

    /// <summary>
    /// 车最近一次报的安全摘要证明仓门都关着：原因码里没有仓位读不出、门没锁、开锁输出没复位这三样（车载端
    /// <c>WireToGateSafetyEvaluator</c> 只在这三处说仓门）。车从没报过也算没证明。
    /// </summary>
    /// <remarks>
    /// 不看 <c>unknownPresent</c>：车载端把「车的运动状态读不到」也算进去（<c>slotUnknown || vehicleUnknown</c>），那与门无关；车停没停稳是
    /// 现场顺序里的另一步（开锁前车要停稳），不是撤下要判的。
    /// </remarks>
    private static bool DoorsProvenClosed(SessionRecoveryRow? session) =>
        session is { SafetyReasonCodesJson: { } json } &&
        !(JsonSerializer.Deserialize<string[]>(json) ?? []).Any(DoorReasons.Contains);

    private static readonly HashSet<string> DoorReasons = new(StringComparer.Ordinal)
    {
        "SLOT_STATE_UNKNOWN",
        "LOCK_NOT_CLOSED",
        "UNLOCK_OUTPUT_NOT_RESET",
    };

    /// <summary>
    /// 现场填的仓位事实：缺了、写不成 <c>仓号=事实</c>、仓号不在 1..8、事实不认得、盖不住目标仓位，或者现场说有货，都拒（D2）。
    /// </summary>
    private static List<SlotFact> JudgeSlotFacts(JourneyWithdrawalRequest request, int[] targets, List<string> codes)
    {
        if (Trimmed(request.CargoLocation) is null)
        {
            codes.Add(CargoLocationRequired);
        }
        string? text = Trimmed(request.SlotFacts);
        if (text is null)
        {
            codes.Add(SlotFactsRequired);
            return [];
        }

        List<SlotFact> facts = [];
        foreach (string entry in text.Split(',', StringSplitOptions.TrimEntries))
        {
            string[] parts = entry.Split('=', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int slot) ||
                slot is < 1 or > 8 ||
                parts[1] is not (SlotEmptyLocked or SlotOccupied) ||
                facts.Any(fact => fact.Slot == slot))
            {
                codes.Add(SlotFactsMalformed);
                return [];
            }
            facts.Add(new SlotFact(slot, parts[1]));
        }
        if (facts.Any(fact => fact.Fact == SlotOccupied))
        {
            codes.Add(SiteReportsCargo);
        }
        if (!facts.Select(fact => fact.Slot).Order().SequenceEqual(targets.Order()))
        {
            codes.Add(SlotFactsMismatch);
        }
        return [.. facts.OrderBy(fact => fact.Slot)];
    }

    /// <summary>
    /// 车报来的那一次结果：每个目标仓位都要读到空、门锁着、开锁输出已复位。没有结果（命令发出后车再没回话）、有一个仓位读到有货或未知，
    /// 都拒——现场与车对不上，是要人在车上处理的事（D2）。返回逐仓读数，给计划与确认码。
    /// </summary>
    private static async Task<string[]> JudgeReportedSlotsAsync(
        ControlServerDbContext dbContext,
        StationOperationRow operation,
        int[] targets,
        List<string> codes,
        CancellationToken cancellationToken)
    {
        // SQLite cannot order a DateTimeOffset, so the live results are read and the latest picked here.
        OperationResultRow? latest = (await dbContext.OperationResults.AsNoTracking()
                .Where(row => row.SlotOperationAttemptId == operation.SlotOperationAttemptId &&
                              !row.HistoricalOnly && row.SupersededByResultId == null)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .MaxBy(row => row.ReceivedAt);
        if (latest is null)
        {
            codes.Add(SlotEvidenceMissing);
            return [];
        }
        SlotPhysicalEvidence[] evidence = JsonSerializer.Deserialize<SlotPhysicalEvidence[]>(latest.EvidenceJson) ?? [];
        if (targets.Any(slot => evidence.FirstOrDefault(item => item.SlotNumber == slot) is not
            { State: SlotBusinessState.Empty, DoorLocked: true, UnlockOutputReset: true }))
        {
            codes.Add(SlotEvidenceNotEmpty);
        }
        return [.. evidence
            .OrderBy(item => item.SlotNumber)
            .Select(item => string.Create(
                CultureInfo.InvariantCulture,
                $"{item.SlotNumber}:{item.State.ToString().ToUpperInvariant()}:{(item.DoorLocked ? "LOCKED" : "NOT_LOCKED")}:{(item.UnlockOutputReset ? "RESET" : "NOT_RESET")}"))];
    }

    /// <summary>
    /// 这一趟名下的订单意图：旅程各停靠点名的，加上这辆车在旅程开始之后建的（引擎一辆车只有一趟进行中的旅程，后来的停靠、重建各有自己的意图，
    /// 与 20 号开闸检查同一个认法）。建单结果未知的拒：RIoT 上可能有一张活单，而且它会永远挡住这辆车的空闲返回（D6 第一版）。
    /// </summary>
    private static async Task<JourneyWithdrawalIntent[]> JudgeIntentsAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        List<string> codes,
        CancellationToken cancellationToken)
    {
        HashSet<string> named = new(stops.Stops.Select(stop => stop.UpperId), StringComparer.Ordinal) { runtime.PickupUpperId };
        if (runtime.GateUpperId is { } gate)
        {
            named.Add(gate);
        }
        // SQLite cannot compare a DateTimeOffset, so the vehicle's intents are read and dated here.
        OrderIntentRow[] mine = [.. (await dbContext.OrderIntents.AsNoTracking()
                .Where(row => row.VehicleKey == runtime.VehicleKey || named.Contains(row.UpperId))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .Where(row => named.Contains(row.UpperId) || row.CreatedAt >= runtime.CreatedAt)
            .OrderBy(row => row.UpperId, StringComparer.Ordinal)];
        foreach (OrderIntentRow intent in mine.Where(row => UnknownOutcomeIntentStatuses.Contains(row.Status, StringComparer.Ordinal)))
        {
            // The idle return evaluator's own exemption and no wider (control-server#771 review M-1): an intent it would still count
            // as RESULT_UNKNOWN holds this vehicle's idle return for good once the journey is gone, and nothing settles it then.
            if (!await AbandonedDemandLegOrders.TakenAsNeverCreatedAsync(dbContext, intent, cancellationToken).ConfigureAwait(false))
            {
                codes.Add(OrderOutcomeUnknown);
                break;
            }
        }
        return [.. mine.Select(row => new JourneyWithdrawalIntent(row.UpperId, row.Status, row.OrderId))];
    }

    /// <summary>确认码：计划、现场填的仓位事实与那一篮去向的摘要。屏上给人看的每一项都在里面（记忆「二次确认的键要含比对对象」）。</summary>
    private static string CodeOf(JourneyWithdrawalPlan plan, IReadOnlyList<SlotFact> facts, string? cargoLocation)
    {
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                plan,
                facts = facts.Select(fact => string.Create(CultureInfo.InvariantCulture, $"{fact.Slot}={fact.Fact}")).ToArray(),
                cargoLocation
            },
            DetailOptions);
        return Convert.ToHexString(SHA256.HashData(content))[..12];
    }

    private static async Task StageWithdrawalAsync(
        ControlServerDbContext dbContext,
        JourneyWithdrawalPlan plan,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        JourneyRuntimeRow runtime = await dbContext.JourneyRuntimes
            .SingleAsync(row => row.JourneyId == plan.JourneyId, cancellationToken).ConfigureAwait(false);
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
        string? entryRequest = stops.CurrentSublotRequestMessageIdOrNone(runtime.WorklistRevision);
        string currentStopId = stops.Current.StopId;

        if (plan.UnresolvedLoad is { } load)
        {
            StationOperationRow operation = await dbContext.StationOperations
                .SingleAsync(row => row.SlotOperationAttemptId == load.SlotOperationAttemptId, cancellationToken).ConfigureAwait(false);
            operation.Status = StationOperationStatus.Cancelled;
        }

        int releasedFromCurrentStop = 0;
        foreach (JourneyWithdrawalDemand item in plan.Demands)
        {
            AcceptedDemandRow demand = await dbContext.AcceptedDemands
                .SingleAsync(row => row.DemandId == item.DemandId, cancellationToken).ConfigureAwait(false);
            if (demand.Status == DemandExecutionStatus.RecoveryRequired &&
                string.Equals(demand.DemandId, plan.UnresolvedLoad?.DemandId, StringComparison.Ordinal))
            {
                demand.Status = DemandExecutionStatus.Accepted;
            }
            JourneyDemandRow membership = await dbContext.Set<JourneyDemandRow>()
                .SingleAsync(row => row.JourneyId == runtime.JourneyId && row.DemandId == item.DemandId && row.RemovedAt == null,
                    cancellationToken).ConfigureAwait(false);
            if (string.Equals(membership.PickupStopId, currentStopId, StringComparison.Ordinal))
            {
                releasedFromCurrentStop++;
            }
            await DemandReleaseService.StageReleasedMembershipAsync(dbContext, membership, now, cancellationToken)
                .ConfigureAwait(false);
        }

        // As the station deadline's release (control-server#703): the stop lost these many entries, so it gains as many worklist
        // versions, and its earlier versions are still retired by its range.
        (await dbContext.Set<JourneyStopRow>().SingleAsync(row => row.StopId == currentStopId, cancellationToken).ConfigureAwait(false))
            .WorklistRefills += releasedFromCurrentStop;
        await new PickupStopTermination(dbContext)
            .StageJourneyClosureAsync(runtime, entryRequest, WithdrawnReason, now, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 握手时做的那两件事，撤下之后立刻做一次：车报来的、已经结掉的那一次从待核实事实里拿掉，重判就绪。车从没和这台服务端建过会话时什么都不做。
    /// </summary>
    private static async Task SettleSessionAsync(ControlServerDbContext dbContext, string agvId, CancellationToken cancellationToken)
    {
        long? generation = await dbContext.SessionRecoveries.AsNoTracking()
            .Where(row => row.AgvId == agvId)
            .Select(row => (long?)row.SessionGeneration)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (generation is null)
        {
            return;
        }
        WireToGateStore store = new(dbContext);
        await store.SettleReportedAttemptsAsync(agvId, generation.Value, cancellationToken).ConfigureAwait(false);
        await store.DecideReadinessAsync(agvId, generation.Value, cancellationToken).ConfigureAwait(false);
    }

    private static Task<string> WriteAuditAsync(
        IGovernanceAuditWriter audit,
        JourneyWithdrawalRequest request,
        Assessment assessment,
        GovernanceActionOutcome outcome,
        string result,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        bool fits = Fits(request);
        // Only what fits the limits is kept in the audit; anything longer is recorded by its length.
        object said = fits
            ? new
            {
                journeyId = Trimmed(request.JourneyId),
                vehicleKey = Trimmed(request.VehicleKey),
                operatorId = Trimmed(request.OperatorId),
                reason = Trimmed(request.Reason),
                siteVerification = Trimmed(request.SiteVerification),
                riotVerification = Trimmed(request.RiotVerification),
                slotFacts = Trimmed(request.SlotFacts),
                cargoLocation = Trimmed(request.CargoLocation),
                claimedRole = Trimmed(request.ClaimedRole),
                confirmationCode = Trimmed(request.ConfirmationCode)
            }
            : new
            {
                journeyIdLength = request.JourneyId?.Length,
                operatorIdLength = request.OperatorId?.Length,
                reasonLength = request.Reason?.Length,
                siteVerificationLength = request.SiteVerification?.Length,
                riotVerificationLength = request.RiotVerification?.Length
            };
        return audit.WriteAdministratorAsync(
            new GovernanceAuditEntry(
                AuditAction,
                GovernedObjectKind.Journey,
                (fits ? Trimmed(request.JourneyId) : null) ?? "(none)",
                null,
                outcome,
                JsonSerializer.Serialize(new { said, plan = assessment.Plan, codes = assessment.Codes, result }, DetailOptions),
                ClaimedAdministratorRole: fits ? Trimmed(request.ClaimedRole) : null),
            now,
            cancellationToken);
    }

    private static bool Fits(JourneyWithdrawalRequest request) =>
        Length(request.Reason) <= MaxTextLength && Length(request.SiteVerification) <= MaxTextLength &&
        Length(request.RiotVerification) <= MaxTextLength && Length(request.CargoLocation) <= MaxTextLength &&
        Length(request.SlotFacts) <= MaxTextLength &&
        Length(request.JourneyId) <= MaxIdentifierLength && Length(request.VehicleKey) <= MaxIdentifierLength &&
        Length(request.OperatorId) <= MaxIdentifierLength && Length(request.ClaimedRole) <= MaxIdentifierLength &&
        Length(request.ConfirmationCode) <= MaxIdentifierLength;

    private static int Length(string? text) => Trimmed(text)?.Length ?? 0;

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
