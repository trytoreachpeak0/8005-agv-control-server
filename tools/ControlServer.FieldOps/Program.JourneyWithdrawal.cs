using System.Text.Json;
using ControlServer.Host.Runtime.Withdrawal;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.FieldOps;

/// <summary>
/// 人工撤下一趟旅程（control-server#771）：车上没货、旅程卡住（车载端离线、重建待发，或挂着一次结果未知的装货而车上没有出口）时，
/// 受治理的现场出口。两个动词，先演练、后撤下。
/// </summary>
/// <remarks>
/// <para>
/// <b><c>plan-journey-withdrawal</c> 只读</b>：以 SQLite 只读模式开库，不探服务端、不拿库锁，所以服务端还跑着时也能先看一眼。输出要撤的那一趟、
/// 每条需求的去向（退回待派）、那一次没结的装货、这一趟名下的订单意图，以及确认码；不能撤时输出全部拒绝理由。
/// </para>
/// <para>
/// <b><c>withdraw-journey</c> 只在服务端停着时直接写库</b>，与 <c>release-station-exclusivity</c> 的直接写库同两道闸（control-server#419、
/// #459、#473）：先探 <c>--probe-server</c> 的 <c>/health/live</c>，有应答就拒（<c>SERVER_RUNNING</c>），探不出结论也拒
/// （<c>SERVER_STATE_UNKNOWN</c>）；再拿服务进程持有的那把库锁，拿不到就拒（<c>DATABASE_IN_USE</c>）。服务端在线的入口是本票第二部分。
/// 必须带演练给出的 <c>--confirm</c>：重算的计划与演练时不同（中间有任何一项变了），拒绝并给出新计划与新码。
/// </para>
/// <para>
/// 必填：<c>--journey</c>、<c>--vehicle-key</c>、<c>--operator</c>、<c>--reason</c>、<c>--site-verification</c>、<c>--riot-verification</c>；
/// 旅程挂着一次没结的装货时还要 <c>--slot-facts</c>（例如 <c>5=EMPTY_LOCKED</c>）与 <c>--cargo-location</c>；<c>--role</c> 可选、照录。
/// 缺了不是用法错误，是拒绝理由（撤下时照样写审计）。
/// </para>
/// </remarks>
internal static partial class Program
{
    private const string PlanJourneyWithdrawalCommand = "plan-journey-withdrawal";

    private const string WithdrawJourneyCommand = "withdraw-journey";

    private const string WithdrawJourneyUsage =
        WithdrawJourneyCommand + " needs --database <file> --probe-server <base url> (server stopped) and --confirm <code from "
        + PlanJourneyWithdrawalCommand + ">, with --journey --vehicle-key --operator --reason --site-verification "
        + "--riot-verification [--slot-facts --cargo-location]";

    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

    private static async Task<int> PlanJourneyWithdrawalAsync(ControlServerDbContext context, Dictionary<string, string> options)
    {
        JourneyWithdrawalResult result = await JourneyWithdrawal.PlanAsync(context, WithdrawalRequest(options), CancellationToken.None);
        return Emit(
            new
            {
                command = PlanJourneyWithdrawalCommand,
                outcome = result.Outcome,
                codes = result.Codes,
                confirmationCode = result.ConfirmationCode,
                plan = JsonSerializer.SerializeToElement(result.Plan, CamelCase)
            },
            result.Outcome == "PLANNED" ? 0 : 1);
    }

    private static async Task<int> WithdrawJourneyAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        string databasePath,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        if (!options.TryGetValue("probe-server", out string? probeText) ||
            !Uri.TryCreate(probeText, UriKind.Absolute, out Uri? probe))
        {
            return Usage(WithdrawJourneyUsage);
        }
        ServerProbe answer = await ProbeServerAsync(probe);
        if (answer.State == ServerProbeState.Inconclusive)
        {
            return Emit(
                new
                {
                    command = WithdrawJourneyCommand,
                    outcome = "SERVER_STATE_UNKNOWN",
                    via = "database",
                    probeServer = probe.ToString(),
                    detail = $"The probe of {probe} reached no conclusion (timeout or another error: {answer.Cause}): nothing "
                        + "was written. Confirm the server is stopped and try again."
                },
                1);
        }
        if (answer.State == ServerProbeState.Answered)
        {
            return Emit(
                new
                {
                    command = WithdrawJourneyCommand,
                    outcome = "SERVER_RUNNING",
                    via = "database",
                    probeServer = probe.ToString(),
                    probeHttpStatus = answer.HttpStatus,
                    detail = "The server answered, so it is running: nothing was written. Stop it first; a journey is withdrawn "
                        + "against the database only while nothing else writes it."
                },
                1);
        }

        ControlServerDatabaseLock? held;
        try
        {
            held = ControlServerDatabaseLock.TryAcquire(databasePath);
        }
        catch (ControlServerDatabaseLockException unusable)
        {
            return Emit(
                new
                {
                    command = WithdrawJourneyCommand,
                    outcome = "DATABASE_LOCK_FILE_UNUSABLE",
                    via = "database",
                    lockFile = unusable.LockFile,
                    detail = "没有写库：" + unusable.Message
                },
                1);
        }
        using ControlServerDatabaseLock? releasedOnExit = held;
        if (held is null)
        {
            return Emit(
                new
                {
                    command = WithdrawJourneyCommand,
                    outcome = "DATABASE_IN_USE",
                    via = "database",
                    lockFile = ControlServerDatabaseLock.LockFileFor(databasePath),
                    detail = ControlServerDatabaseLock.FieldOpsRefusal(databasePath)
                },
                1);
        }

        JourneyWithdrawalResult result = await JourneyWithdrawal.WithdrawAsync(
            context, governance, WithdrawalRequest(options), now, publisher: null, CancellationToken.None);
        return Emit(
            new
            {
                command = WithdrawJourneyCommand,
                outcome = result.Outcome,
                via = "database",
                codes = result.Codes,
                confirmationCode = result.ConfirmationCode,
                plan = JsonSerializer.SerializeToElement(result.Plan, CamelCase),
                auditRecordId = result.AuditRecordId
            },
            result.Outcome is "WITHDRAWN" or "ALREADY_DONE" ? 0 : 1);
    }

    private static JourneyWithdrawalRequest WithdrawalRequest(Dictionary<string, string> options) =>
        new(
            options.GetValueOrDefault("journey"),
            options.GetValueOrDefault("vehicle-key"),
            options.GetValueOrDefault("operator"),
            options.GetValueOrDefault("reason"),
            options.GetValueOrDefault("site-verification"),
            options.GetValueOrDefault("riot-verification"),
            options.GetValueOrDefault("slot-facts"),
            options.GetValueOrDefault("cargo-location"),
            options.GetValueOrDefault("role"),
            options.GetValueOrDefault("confirm"));
}
