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
    public const string SessionMismatch = "RECOVERY_CLOSE_SESSION_MISMATCH";
    public const string SessionNotExecuting = "RECOVERY_CLOSE_SESSION_NOT_EXECUTING";
    public const string ActionNotClosable = "RECOVERY_CLOSE_ACTION_NOT_CLOSABLE";
    public const string ResultNotAwaited = "RECOVERY_CLOSE_RESULT_NOT_AWAITED";
    public const string ResultInFlightOnVehicle = "RECOVERY_CLOSE_RESULT_IN_FLIGHT_ON_VEHICLE";

    /// <summary>每一个拒绝码给现场人员看的中文说明：Host 入口的拒绝响应里逐条带上（<c>descriptions</c>）。</summary>
    public static IReadOnlyDictionary<string, string> Descriptions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [OperatorRequired] = "没有写办理人（operatorId）",
        [ReasonRequired] = "没有写理由（reason）",
        [SiteVerificationRequired] = "没有写现场核实记录（siteVerification）：例如确认车已离线、换机或清库，或车上续作没有在执行",
        [FieldTooLong] = "有一项写得太长：理由与核实记录各不超过 500 字，办理人、角色、车号与会话号各不超过 128 字",
        [SessionNotFound] = "这辆车眼下没有未关闭的异常恢复会话，不用再办",
        [SessionMismatch] = "填的会话号不是这辆车眼下未关闭的那个会话：可以不填会话号，服务端按车找到它；填了就必须一致",
        [SessionNotExecuting] = "这辆车的会话不在执行中：开着的会话在车上选动作即可",
        [ActionNotClosable] = "这个会话选的动作不能从这里关：目前只有修好后续作（RESUME_AFTER_REPAIR）可以",
        [ResultNotAwaited] = "这个会话的动作已经不在等结果：服务端会自己收尾，不用再办",
        [ResultInFlightOnVehicle] = "车此刻在线，而且它上报的待处理事项里还有这次续作，结果可能还在路上：现在关会丢掉真实结果。请等车把结果交上来，或等车离线后再办",
    };
}

/// <summary>
/// 一次关闭请求。车由人点名；会话号可不填，服务端按车找它唯一未关闭的那个会话，填了就必须对得上。文本字段可空——缺了是一条拒绝理由，不是格式错误。
/// </summary>
/// <param name="SiteVerification">现场核实的记录引用：车离线、换机或清库，或车上续作没有在执行。</param>
/// <param name="ClaimedRole">办理人自述的角色，照录进审计，不认证任何东西。</param>
public sealed record RecoverySessionAdministratorCloseRequest(
    string AgvId,
    string? ExceptionRecoverySessionId,
    string? OperatorId,
    string? Reason,
    string? SiteVerification,
    string? ClaimedRole);

/// <summary>一次判定的结果。<see cref="ExceptionRecoverySessionId"/> 是这次判定的那个会话（关掉的，或拒绝关的），这辆车没有未关闭会话时为空。</summary>
public sealed record RecoverySessionAdministratorCloseResult(
    bool Closed,
    IReadOnlyList<string> Codes,
    string? ExceptionRecoverySessionId,
    string AuditRecordId);

/// <summary>
/// 异常恢复会话结果永远来不了时的受治理出口（control-server#483）：会话选了修好后续作、在等续作结果，而这份结果被服务端整行拒收后车载端放弃了
/// 它（8005-agv-onboard-hmi#254），或者车永久离线、换机、清库，于是会话一直停在 EXECUTING——没有可选动作，同一会话再选被拒，新会话也开不了。
/// 由具名的人说明情况，服务端核过会话此刻确实在等结果、结果不在车上待交，就把它关掉。
/// </summary>
/// <remarks>
/// <para>
/// <b>关掉之后一切照 control-server#187 处理</b>：续作判为 <c>RecoveryRequired</c>（结局 <c>ADMINISTRATOR_CLOSED</c>），续作命令结清、不再补发，
/// 会话 CLOSED、修订号加一、下发 CLOSED 快照；需求、旅程、站点操作、租约与车都不动——需求仍待恢复、旅程仍阻塞、车仍被占，由现场管理员在车载端
/// 新开会话，按那时的事实重选动作。之后再来的替换结果找不到在等它的续作，整行拒收（<c>BUSINESS_ID_CONTENT_CONFLICT</c>，说明点名
/// <c>ADMINISTRATOR_CLOSED</c>），服务端什么也不收。不能改成应答：车载端收到应答会把那一行删掉，真实发生过的结果就没了。
/// </para>
/// <para>
/// <b>车在线、结果可能在路上时不关</b>（审查 S1）：车此刻连着、最近一份恢复报告还点名这次续作的 attempt（待处理 attempt 或未结清 attempt），或
/// 报着任何待交结果，就拒绝（<see cref="RecoverySessionAdministratorCloseCodes.ResultInFlightOnVehicle"/>）。车不在线照样放行——等一辆永远不回来的车，
/// 正是这个出口要结束的事。是否在线、会话代次与那三项事实，放行拒绝都记进审计。
/// </para>
/// <para>
/// <b>放宽类入口</b>：与 <see cref="StationExclusivityReleaseEndpoints"/> 同一把 Bearer 凭据、同一个开关（<c>VehicleFaultRecovery:enabled</c>）。
/// 通过认证的每一次请求——拒绝也算——写一条管理员审计（<see cref="AuditAction"/>）；判定中途抛了异常，也尽力补写一条失败审计再抛出，只有请求已被取消时
/// 不补，补写本身失败只记日志。
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
    IOnboardConnectionPresence presence,
    IGovernanceAuditWriter audit,
    TimeProvider timeProvider,
    ILogger<RecoverySessionAdministratorClose> logger)
{
    /// <summary>每一次请求写的管理员审计动作。</summary>
    public const string AuditAction = "EXCEPTION_RECOVERY_SESSION_ADMINISTRATOR_CLOSE";

    /// <summary>理由与现场核实记录的最长长度（去掉首尾空白后的 UTF-16 码元）。</summary>
    public const int MaxTextLength = 500;

    /// <summary>办理人、角色、车号与会话号的最长长度。</summary>
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

    private static readonly Action<ILogger, string, Exception?> LogFailureNotAudited =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2134, nameof(LogFailureNotAudited)),
            "Closing an exception recovery session of vehicle {AgvId} failed, and so did writing the failed audit record for " +
            "it (control-server#483). Nothing was closed.");

    public async Task<RecoverySessionAdministratorCloseResult> CloseAsync(
        RecoverySessionAdministratorCloseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string agvId = request.AgvId.Trim();
        string? sessionId = Trimmed(request.ExceptionRecoverySessionId);
        string? operatorId = Trimmed(request.OperatorId);
        string? reason = Trimmed(request.Reason);
        string? siteVerification = Trimmed(request.SiteVerification);
        string? claimedRole = Trimmed(request.ClaimedRole);

        List<string> codes = [];
        if (operatorId is null) codes.Add(RecoverySessionAdministratorCloseCodes.OperatorRequired);
        if (reason is null) codes.Add(RecoverySessionAdministratorCloseCodes.ReasonRequired);
        if (siteVerification is null) codes.Add(RecoverySessionAdministratorCloseCodes.SiteVerificationRequired);
        bool fits = (reason?.Length ?? 0) <= MaxTextLength && (siteVerification?.Length ?? 0) <= MaxTextLength &&
                    (operatorId?.Length ?? 0) <= MaxIdentifierLength && (claimedRole?.Length ?? 0) <= MaxIdentifierLength &&
                    (sessionId?.Length ?? 0) <= MaxIdentifierLength && agvId.Length <= MaxIdentifierLength;
        if (!fits) codes.Add(RecoverySessionAdministratorCloseCodes.FieldTooLong);

        object? read = null;
        string? judged = null;
        bool audited = false;
        dbContext.ChangeTracker.Clear();
        if (codes.Count > 0)
        {
            return await RefuseAsync().ConfigureAwait(false);
        }

        string auditRecordId;
        try
        {
            long? connected = presence.ConnectedSessionGeneration(agvId);
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            AdministratorCloseDecision decision = await coordinator.CloseSessionAwaitingResultAsync(
                agvId, sessionId, connected, facts => read = facts, cancellationToken).ConfigureAwait(false);
            judged = decision.ExceptionRecoverySessionId;
            if (decision.Codes.Count > 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                codes.AddRange(decision.Codes);
                return await RefuseAsync().ConfigureAwait(false);
            }
            auditRecordId = await WriteAuditAsync(GovernanceActionOutcome.Succeeded, "CLOSED", null).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            audited = true;
        }
        catch (Exception error) when (!audited && !cancellationToken.IsCancellationRequested)
        {
            // Best effort, after the transaction has rolled back: an attempt that failed half-way still leaves its trace.
            dbContext.ChangeTracker.Clear();
            try
            {
                await WriteAuditAsync(GovernanceActionOutcome.Failed, "ERROR", error.GetType().FullName).ConfigureAwait(false);
            }
            catch (Exception auditFailure) when (!cancellationToken.IsCancellationRequested)
            {
                LogFailureNotAudited(logger, agvId, auditFailure);
            }
            throw;
        }

        LogClosed(logger, judged!, agvId, operatorId!, null);
        await coordinator.TrySendPendingSessionSnapshotsAsync(agvId, cancellationToken).ConfigureAwait(false);
        return new RecoverySessionAdministratorCloseResult(true, [], judged, auditRecordId);

        async Task<RecoverySessionAdministratorCloseResult> RefuseAsync()
        {
            audited = true;
            string id = await WriteAuditAsync(GovernanceActionOutcome.Failed, "REJECTED", null).ConfigureAwait(false);
            return new RecoverySessionAdministratorCloseResult(false, [.. codes], judged, id);
        }

        Task<string> WriteAuditAsync(GovernanceActionOutcome outcome, string result, string? error) =>
            audit.WriteAdministratorAsync(
                new GovernanceAuditEntry(
                    AuditAction,
                    GovernedObjectKind.ExceptionRecoverySession,
                    judged ?? (fits ? $"vehicle:{agvId}" : $"vehicle-length:{agvId.Length}"),
                    null,
                    outcome,
                    JsonSerializer.Serialize(
                        new
                        {
                            agvId = fits ? agvId : null,
                            exceptionRecoverySessionId = judged,
                            said = fits
                                ? (object)new { exceptionRecoverySessionId = sessionId, operatorId, reason, siteVerification, claimedRole }
                                : new
                                {
                                    exceptionRecoverySessionIdLength = sessionId?.Length,
                                    operatorIdLength = operatorId?.Length,
                                    reasonLength = reason?.Length,
                                    siteVerificationLength = siteVerification?.Length,
                                    claimedRoleLength = claimedRole?.Length,
                                    agvIdLength = agvId.Length,
                                },
                            read,
                            codes,
                            result,
                            error,
                        },
                        DetailOptions),
                    ClaimedAdministratorRole: fits ? claimedRole : null),
                timeProvider.GetUtcNow(),
                cancellationToken);
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
