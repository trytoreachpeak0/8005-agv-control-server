using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Recovery;

/// <summary>管理员关闭等结果的异常恢复会话被拒时的理由码（control-server#483）。Host 入口的码，不上线路。</summary>
public static class RecoverySessionAdministratorCloseCodes
{
    public const string OperatorRequired = "RECOVERY_CLOSE_OPERATOR_REQUIRED";
    public const string ReasonRequired = "RECOVERY_CLOSE_REASON_REQUIRED";
    public const string SiteVerificationRequired = "RECOVERY_CLOSE_SITE_VERIFICATION_REQUIRED";
    public const string FieldTooLong = "RECOVERY_CLOSE_FIELD_TOO_LONG";
    public const string SessionNotFound = "RECOVERY_CLOSE_SESSION_NOT_FOUND";
    public const string SessionNotExecuting = "RECOVERY_CLOSE_SESSION_NOT_EXECUTING";
    public const string ActionNotClosable = "RECOVERY_CLOSE_ACTION_NOT_CLOSABLE";
    public const string ResultNotAwaited = "RECOVERY_CLOSE_RESULT_NOT_AWAITED";

    /// <summary>每一个拒绝码给现场人员看的中文说明：Host 入口的拒绝响应里逐条带上（<c>descriptions</c>）。</summary>
    public static IReadOnlyDictionary<string, string> Descriptions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [OperatorRequired] = "没有写办理人（operatorId）",
        [ReasonRequired] = "没有写理由（reason）",
        [SiteVerificationRequired] = "没有写现场核实记录（siteVerification）：例如确认车已离线、换机或清库，或车上续作没有在执行",
        [FieldTooLong] = "有一项写得太长：理由与核实记录各不超过 500 字，办理人、角色与会话号各不超过 128 字",
        [SessionNotFound] = "这辆车没有这个异常恢复会话：请照看板上这辆车的会话号填写",
        [SessionNotExecuting] = "这个会话不在执行中：开着的会话在车上选动作即可，已关闭的不用再办",
        [ActionNotClosable] = "这个会话选的动作不能从这里关：目前只有修好后续作（RESUME_AFTER_REPAIR）可以",
        [ResultNotAwaited] = "这个会话的动作已经不在等结果：服务端会自己收尾，不用再办",
    };
}

/// <summary>一次关闭请求。会话与车都由人点名，不推断；文本字段可空——缺了是一条拒绝理由，不是格式错误。</summary>
/// <param name="SiteVerification">现场核实的记录引用：车离线、换机或清库，或车上续作没有在执行。</param>
/// <param name="ClaimedRole">办理人自述的角色，照录进审计，不认证任何东西。</param>
public sealed record RecoverySessionAdministratorCloseRequest(
    string AgvId,
    string ExceptionRecoverySessionId,
    string? OperatorId,
    string? Reason,
    string? SiteVerification,
    string? ClaimedRole);

/// <summary>一次判定的结果。</summary>
public sealed record RecoverySessionAdministratorCloseResult(
    bool Closed,
    IReadOnlyList<string> Codes,
    string AuditRecordId);

/// <summary>
/// 异常恢复会话结果永远来不了时的受治理出口（control-server#483）：会话选了修好后续作、在等续作结果，而这份结果被服务端整行拒收后车载端放弃了
/// 它（8005-agv-onboard-hmi#254），或者车永久离线、换机、清库，于是会话一直停在 EXECUTING——没有可选动作，同一会话再选被拒，新会话也开不了。
/// 由具名的人说明情况，服务端核过会话此刻确实在等结果，就把它关掉。
/// </summary>
/// <remarks>
/// <para>
/// <b>关掉之后一切照 control-server#187 处理</b>：续作判为 <c>RecoveryRequired</c>（结局 <c>ADMINISTRATOR_CLOSED</c>），续作命令结清、不再补发，
/// 会话 CLOSED、修订号加一、下发 CLOSED 快照；需求、旅程、站点操作、租约与车都不动——需求仍待恢复、旅程仍阻塞、车仍被占，由现场管理员在车载端
/// 新开会话，按那时的事实重选动作。之后再来的替换结果找不到在等它的续作，整行拒收（<c>BUSINESS_ID_CONTENT_CONFLICT</c>），服务端什么也不收。
/// </para>
/// <para>
/// <b>放宽类入口</b>：与 <see cref="StationExclusivityReleaseEndpoints"/> 同一把 Bearer 凭据、同一个开关（<c>VehicleFaultRecovery:enabled</c>）。
/// 通过认证的每一次请求——拒绝也算——写一条管理员审计（<see cref="AuditAction"/>）。
/// </para>
/// <para>
/// <b>在写锁里判定</b>：整件事在一个事务里读、判、写（本库的事务是 BEGIN IMMEDIATE），与入站处理串行。先提交的结果会被这里看到、于是拒绝；
/// 后到的结果看到会话已关。
/// </para>
/// <para>
/// <b>只放开续作</b>（调度 2026-10-05 定）：哪些动作能这样关由 <see cref="OnboardRecoveryCoordinator.AdministratorClosableActions"/> 一张表决定，
/// 另外三种动作是否加入另开票决定。
/// </para>
/// </remarks>
public sealed class RecoverySessionAdministratorClose(
    ControlServerDbContext dbContext,
    OnboardRecoveryCoordinator coordinator,
    IGovernanceAuditWriter audit,
    TimeProvider timeProvider,
    ILogger<RecoverySessionAdministratorClose> logger)
{
    /// <summary>每一次请求写的管理员审计动作。</summary>
    public const string AuditAction = "EXCEPTION_RECOVERY_SESSION_ADMINISTRATOR_CLOSE";

    /// <summary>理由与现场核实记录的最长长度（去掉首尾空白后的 UTF-16 码元）。</summary>
    public const int MaxTextLength = 500;

    /// <summary>办理人、角色与会话号的最长长度。</summary>
    public const int MaxIdentifierLength = 128;

    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly Action<ILogger, string, string, string, Exception?> LogClosed =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2132, nameof(LogClosed)),
            "Exception recovery session {SessionId} of vehicle {AgvId} closed by administrator {OperatorId} while its result " +
            "was awaited (control-server#483). The demand stays where it was; a new session is needed to recover it.");

    public async Task<RecoverySessionAdministratorCloseResult> CloseAsync(
        RecoverySessionAdministratorCloseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? operatorId = Trimmed(request.OperatorId);
        string? reason = Trimmed(request.Reason);
        string? siteVerification = Trimmed(request.SiteVerification);
        string? claimedRole = Trimmed(request.ClaimedRole);
        string sessionId = request.ExceptionRecoverySessionId.Trim();

        List<string> codes = [];
        if (operatorId is null) codes.Add(RecoverySessionAdministratorCloseCodes.OperatorRequired);
        if (reason is null) codes.Add(RecoverySessionAdministratorCloseCodes.ReasonRequired);
        if (siteVerification is null) codes.Add(RecoverySessionAdministratorCloseCodes.SiteVerificationRequired);
        bool fits = (reason?.Length ?? 0) <= MaxTextLength && (siteVerification?.Length ?? 0) <= MaxTextLength &&
                    (operatorId?.Length ?? 0) <= MaxIdentifierLength && (claimedRole?.Length ?? 0) <= MaxIdentifierLength &&
                    sessionId.Length <= MaxIdentifierLength && request.AgvId.Length <= MaxIdentifierLength;
        if (!fits) codes.Add(RecoverySessionAdministratorCloseCodes.FieldTooLong);

        object? read = null;
        dbContext.ChangeTracker.Clear();
        if (codes.Count > 0)
        {
            return await RefuseAsync().ConfigureAwait(false);
        }

        string auditRecordId;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            IReadOnlyList<string> refused = await coordinator.CloseSessionAwaitingResultAsync(
                request.AgvId, sessionId, facts => read = facts, cancellationToken).ConfigureAwait(false);
            if (refused.Count > 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                codes.AddRange(refused);
                return await RefuseAsync().ConfigureAwait(false);
            }
            auditRecordId = await WriteAuditAsync(GovernanceActionOutcome.Succeeded, "CLOSED").ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        LogClosed(logger, sessionId, request.AgvId, operatorId!, null);
        await coordinator.TrySendPendingSessionSnapshotsAsync(request.AgvId, cancellationToken).ConfigureAwait(false);
        return new RecoverySessionAdministratorCloseResult(true, [], auditRecordId);

        async Task<RecoverySessionAdministratorCloseResult> RefuseAsync()
        {
            string id = await WriteAuditAsync(GovernanceActionOutcome.Failed, "REJECTED").ConfigureAwait(false);
            return new RecoverySessionAdministratorCloseResult(false, [.. codes], id);
        }

        Task<string> WriteAuditAsync(GovernanceActionOutcome outcome, string result) =>
            audit.WriteAdministratorAsync(
                new GovernanceAuditEntry(
                    AuditAction,
                    GovernedObjectKind.ExceptionRecoverySession,
                    fits ? sessionId : $"length:{sessionId.Length}",
                    null,
                    outcome,
                    JsonSerializer.Serialize(
                        new
                        {
                            agvId = fits ? request.AgvId : null,
                            said = fits
                                ? (object)new { operatorId, reason, siteVerification, claimedRole }
                                : new
                                {
                                    operatorIdLength = operatorId?.Length,
                                    reasonLength = reason?.Length,
                                    siteVerificationLength = siteVerification?.Length,
                                    claimedRoleLength = claimedRole?.Length,
                                    agvIdLength = request.AgvId.Length,
                                },
                            read,
                            codes,
                            result,
                        },
                        DetailOptions),
                    ClaimedAdministratorRole: fits ? claimedRole : null),
                timeProvider.GetUtcNow(),
                cancellationToken);
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
