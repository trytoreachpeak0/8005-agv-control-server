using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 有货车解除 8005 三类急停的一次请求（REQ-0356 按 CP-0011 修订，需求基线 v1.10.0，control-server#539）。车由人点名，不推断；文本字段可空——
/// 缺了是一条拒绝理由，不是格式错误。
/// </summary>
/// <param name="DoorLockRepaired">人确认门锁已检修。只有这辆车是因为行驶中仓门未证明锁闭被停下时才必填。</param>
public sealed record LoadedEmergencyStopReleaseRequest(
    string AgvId,
    string? OperatorId,
    string? Reason,
    bool CauseCleared,
    bool AllDoorsClosed,
    bool DoorLockRepaired);

/// <summary>一次判定的结果：急停监管的决定、拒绝码（含本入口自己的）与这次请求的管理员审计记录。</summary>
public sealed record LoadedEmergencyStopReleaseResult(
    EmergencyStopDecision? Decision,
    IReadOnlyList<string> Codes,
    string AuditRecordId);

/// <summary>
/// 有货车的受治理人工解除（REQ-0356 按 CP-0011 修订）：车上有货或货物状态未知时，持异常处置权限的管理员以本人身份解除 8005 自己的那一层急停
/// （三类急停），不要求车上无货。
/// </summary>
/// <remarks>
/// <para>
/// <b>规则都在 <see cref="EmergencyStopSupervisor.ReleaseLoadedOnGovernedConfirmationAsync"/></b>：急停状态、8005 的三类是否在生效、RIoT 上的未结束订单、
/// 各项确认。这里只做三件那边做不了的事：查角色、判断这辆车是不是因为门锁被停下、列出 8005 自己建过的单（让唯一一张已暂停的自己的单可以不取消），
/// 再把每一次请求——拒绝也算——写一条不可改写的管理员审计（<see cref="AuditAction"/>）。
/// </para>
/// <para>
/// <b>谁能办，与需求的差距如实写在这里。</b>REQ-0253 说异常处置权限归维护管理员和系统管理员。服务端没有按人登录，也没有这两个角色：现成的只有现场人员
/// 角色名单（<see cref="FieldOperatorRoleRoster"/>），其中只有 R-11 设备/电气维护人员与 R-13 AGV 运维/调度管理员。这里取最窄的 R-13，并用一把单独的
/// Bearer 凭据（<see cref="EmergencyStopReleaseOptions.LoadedCredentialEnvironmentVariable"/>）——不是本人凭据，是「能到端口的人里，持这把凭据、且自报的
/// operatorId 在名单里有 R-13 的人」。这一点在 control-server#539 的 PR 里报调度，不在代码里自行放宽。
/// </para>
/// </remarks>
public sealed class LoadedEmergencyStopRelease(
    ControlServerDbContext dbContext,
    EmergencyStopSupervisor supervisor,
    IVehicleFaultStore faults,
    FieldOperatorRoleRoster roles,
    IGovernanceAuditWriter audit,
    TimeProvider timeProvider)
{
    /// <summary>每一次请求写的管理员审计动作。</summary>
    public const string AuditAction = "EMERGENCY_STOP_GOVERNED_LOADED_RELEASE";

    public const string NotAuthorized = "EMERGENCY_CONFIRMER_NOT_AUTHORIZED";
    public const string FieldTooLong = "EMERGENCY_RELEASE_FIELD_TOO_LONG";

    /// <summary>能办的角色：AGV 运维/调度管理员（见类注释里的差距）。</summary>
    public static IReadOnlyList<string> AllowedRoles { get; } = [FieldOperatorRoleRoster.AgvOperationsAdministrator];

    /// <summary>原因的最长长度（去掉首尾空白后的 UTF-16 码元）。</summary>
    public const int MaxTextLength = 500;

    /// <summary>办理人的最长长度。</summary>
    public const int MaxIdentifierLength = 128;

    /// <summary>每一个拒绝码给现场人员看的中文说明：入口的拒绝响应里逐条带上（<c>descriptions</c>）。</summary>
    public static IReadOnlyDictionary<string, string> Descriptions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["EMERGENCY_CONFIRMER_UNIDENTIFIED"] = "没有写办理人（operatorId）",
        [NotAuthorized] = "办理人不在名单里，或没有 AGV 运维/调度管理员（R-13）角色：车上有货时只有这个角色能解除",
        [FieldTooLong] = "有一项写得太长：原因不超过 500 字，办理人不超过 128 字",
        ["EMERGENCY_RELEASE_REASON_REQUIRED"] = "没有写解除原因（reason）",
        ["EMERGENCY_CAUSE_CLEARED_NOT_CONFIRMED"] = "没有确认急停原因已经消除",
        ["EMERGENCY_DOORS_CLOSED_NOT_CONFIRMED"] = "没有确认全部仓门已经关好",
        ["EMERGENCY_DOOR_LOCK_REPAIR_NOT_CONFIRMED"] = "这台车是因为行驶中仓门没证明锁好被停下的：要先检修门锁，并确认门锁已检修，才能解除",
        ["EMERGENCY_NOT_CAN_RECOVER"] = "急停此刻不是可恢复状态：如果车上急停按钮还按着，请先松开；如果急停已经解开，不用再办",
        ["EMERGENCY_NOT_RAISED_BY_8005"] = "这台车上没有本系统下的急停：急停按钮、触边、RIoT 或 Matrix 下的急停，请在原来的地方解除",
        ["EMERGENCY_VEHICLE_ORDERS_UNKNOWN"] = "这一刻没读到 RIoT 上这台车的订单：证明不了车不会被别的单开走，稍后再办",
        ["EMERGENCY_VEHICLE_ORDER_NOT_FINISHED"] = "RIoT 上这台车还挂着别的未结束订单：解除后车可能被开走，请联系系统维护人员处理那张单",
        [EmergencyStopSupervisor.HeldOrderNotPausedReason] = "这台车上本系统的订单此刻不是暂停状态：解除后车可能直接开走，不能解除，请联系系统维护人员",
        ["EMERGENCY_RELEASE_NOT_CONFIRMED"] = "上一次解除已经发出、还没看到急停解开：车是停着的，稍等几秒再看",
    };

    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public async Task<LoadedEmergencyStopReleaseResult> DecideAsync(
        EmergencyStopSubject subject,
        LoadedEmergencyStopReleaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(request);
        string? operatorId = Trimmed(request.OperatorId);
        string? reason = Trimmed(request.Reason);

        List<string> codes = [];
        bool fits = (reason?.Length ?? 0) <= MaxTextLength && (operatorId?.Length ?? 0) <= MaxIdentifierLength;
        if (!fits) codes.Add(FieldTooLong);
        string? role = fits ? roles.GrantedRole(operatorId, AllowedRoles) : null;
        if (operatorId is null) codes.Add("EMERGENCY_CONFIRMER_UNIDENTIFIED");
        else if (fits && role is null) codes.Add(NotAuthorized);
        if (codes.Count > 0)
        {
            string refused = await WriteAuditAsync(GovernanceActionOutcome.Failed, "REJECTED", null, null).ConfigureAwait(false);
            return new LoadedEmergencyStopReleaseResult(null, [.. codes], refused);
        }

        bool doorCause = await EmergencyStopDoorCause.ReadAsync(dbContext, faults, supervisor, subject, cancellationToken)
            .ConfigureAwait(false);
        HashSet<string> ownOrderIds = (await dbContext.OrderIntents.AsNoTracking()
                .Where(row => row.VehicleKey == subject.DeviceKey && row.OrderId != null)
                .Select(row => row.OrderId!)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        EmergencyStopDecision decision = await supervisor.ReleaseLoadedOnGovernedConfirmationAsync(
            new LoadedEmergencyStopReleaseConfirmation(
                subject, operatorId, reason, request.CauseCleared, request.AllDoorsClosed, request.DoorLockRepaired),
            doorCause,
            ownOrderIds,
            cancellationToken).ConfigureAwait(false);

        bool issued = decision.Action is EmergencyStopAction.Recovered or EmergencyStopAction.RecoveryUnconfirmed;
        string id = await WriteAuditAsync(
            issued ? GovernanceActionOutcome.Succeeded : GovernanceActionOutcome.Failed,
            issued ? "RELEASE_ISSUED" : decision.Action == EmergencyStopAction.AwaitingRetry ? "AWAITING_RETRY" : "REJECTED",
            decision,
            doorCause).ConfigureAwait(false);
        return new LoadedEmergencyStopReleaseResult(decision, issued ? [] : decision.Reasons, id);

        Task<string> WriteAuditAsync(
            GovernanceActionOutcome outcome,
            string result,
            EmergencyStopDecision? taken,
            bool? stoppedForDoors) =>
            audit.WriteAdministratorAsync(
                new GovernanceAuditEntry(
                    AuditAction,
                    GovernedObjectKind.EmergencyStop,
                    subject.AgvId,
                    null,
                    outcome,
                    JsonSerializer.Serialize(
                        new
                        {
                            agvId = subject.AgvId,
                            vehicleKey = subject.DeviceKey,
                            said = fits
                                ? (object)new
                                {
                                    operatorId,
                                    reason,
                                    request.CauseCleared,
                                    request.AllDoorsClosed,
                                    request.DoorLockRepaired,
                                }
                                : new { operatorIdLength = operatorId?.Length, reasonLength = reason?.Length },
                            grantedRole = role,
                            stoppedForDoors,
                            emergencyState = taken?.Emergency.EmergencyState,
                            action = taken?.Action.ToString(),
                            commandAuditId = taken?.Attempt?.CommandAuditId,
                            codes = taken is null ? codes : [.. taken.Reasons],
                            result,
                        },
                        DetailOptions),
                    ClaimedAdministratorRole: role),
                timeProvider.GetUtcNow(),
                cancellationToken);
    }

    /// <summary>
    /// Writes the administrator audit for a request the entry turned away before deciding it -- no credential configured, the
    /// wrong credential, an incomplete request, a vehicle this server does not drive -- or that failed while being decided
    /// (control-server#539 review L-4). Every request leaves a record, not only the ones that reached the decision.
    /// </summary>
    /// <remarks>
    /// Nothing the caller sent is written but the vehicle it named, cut to <see cref="MaxIdentifierLength"/>: a request that was
    /// not authenticated says nothing that may be stored as a person's statement.
    /// </remarks>
    public Task<string> RecordTurnedAwayAsync(string? agvId, string result, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        string? named = Trimmed(agvId);
        if (named is { Length: > MaxIdentifierLength })
        {
            named = named[..MaxIdentifierLength];
        }

        return audit.WriteAdministratorAsync(
            new GovernanceAuditEntry(
                AuditAction,
                GovernedObjectKind.EmergencyStop,
                named ?? "-",
                null,
                GovernanceActionOutcome.Failed,
                JsonSerializer.Serialize(new { agvId = named, result }, DetailOptions),
                ClaimedAdministratorRole: null),
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Whether a vehicle was stopped for the doors of a driving vehicle (REQ-0246), which makes both class-three entries ask the
/// person to confirm the lock was repaired (CP-0011, requirements baseline v1.10.0: "锁可能坏了").
/// </summary>
/// <remarks>
/// The stop's own record says so first: every stop of the open episode carries the symptom it was raised for
/// (<see cref="EmergencyStopSupervisor.OpenEpisodeCausesAsync"/>). That is what the person is releasing, and nothing later
/// overwrites it (control-server#539 review M-2: the fault's evidence code becomes <c>VEHICLE_ORDER_FAILED</c> when the held
/// order fails afterwards, and <see cref="VehicleFaultEvidence.EmergencyCanNotRecover"/> while a button is held). The standing
/// fault and the journey's block code are asked as well, for a stop raised before causes were recorded; any one of the three
/// makes the confirmation required, never fewer.
/// </remarks>
public static class EmergencyStopDoorCause
{
    public static async Task<bool> ReadAsync(
        ControlServerDbContext dbContext,
        IVehicleFaultStore faults,
        EmergencyStopSupervisor supervisor,
        EmergencyStopSubject subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(faults);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(subject);
        if ((await supervisor.OpenEpisodeCausesAsync(subject, cancellationToken).ConfigureAwait(false))
            .Contains(VehicleFaultEvidence.DoorNotProvenLocked))
        {
            return true;
        }

        string agvId = subject.AgvId;
        VehicleFaultFact? fault = await faults.ReadAsync(agvId, cancellationToken).ConfigureAwait(false);
        if (fault is { Level: not VehicleFaultLevel.None } &&
            string.Equals(fault.EvidenceCode, VehicleFaultEvidence.DoorNotProvenLocked, StringComparison.Ordinal))
        {
            return true;
        }

        return await dbContext.JourneyRuntimes.AsNoTracking()
            .AnyAsync(
                row => row.AgvId == agvId &&
                       (row.BlockReasonCode == VehicleFaultEvidence.DoorNotProvenLocked ||
                        row.BlockReasonCode == JourneyRuntimeEngine.HeldOrderResumedWithoutContinueReason),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
