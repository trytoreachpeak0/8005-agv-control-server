using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 仓位配置激活的发起入口：协议 v2 消息 7 的唯一进程外来源。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须是进程内的 HTTP 而不是 <c>ControlServer.FieldOps</c> 的一条动词。</b>下发一次激活
/// 不只是写库：<see cref="SlotConfigurationActivationDispatcher.IssueAsync" /> 落库之后要把命令排进
/// 发件箱并**在活着的那条会话上发出去**。FieldOps 是进程外直连数据库的工具，它够不到运行中服务端的
/// 那条连接——它能造出一行「待发」，却发不出去，而一条永远发不出去的命令比没有这个入口更坏。
/// </para>
/// <para>
/// <b>会话代由服务端自己读，不接受调用方传。</b>调用方手上的那个数字必然是它上一次查询时看到的，
/// 车中间重连过一次它就过期了；拿一个过期的会话代发出去，命令会落在一个已经不存在的会话里。
/// </para>
/// <para>
/// <b>成功的回应是 202，不是 200。</b>命令按 <c>RELIABLE</c> 上线之后，这次激活处在
/// <c>PENDING_RESULT</c>：车还没有报结果。REQ-0264 的「不能猜测成功」就是这个意思——200 会让调用方
/// 以为配置已经换好了。要知道结果，读那条激活记录，或者等消息 8。
/// </para>
/// </remarks>
public static class SlotConfigurationActivationEndpoints
{
    public static void MapSlotConfigurationActivation(this WebApplication app)
    {
        app.MapPost("/api/governance/v1/slot-configuration-activations", HandleAsync)
            .WithName("IssueSlotConfigurationActivation")
            .WithSummary("Issue a slot configuration activation command to a vehicle")
            .WithDescription(
                "Persists the activation, queues protocol v2 message 7 and sends it on the vehicle's "
                + "live session. Returns 202: the vehicle has not reported a result yet.")
            .Produces<SlotConfigurationActivationAcceptedResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<Results<
        Accepted<SlotConfigurationActivationAcceptedResponse>,
        UnauthorizedHttpResult,
        ProblemHttpResult>> HandleAsync(
        HttpContext context,
        SlotConfigurationActivationRequest request,
        SlotConfigurationActivationDispatcher dispatcher,
        ControlServerDbContext dbContext,
        TimeProvider timeProvider,
        IOptions<SlotConfigurationActivationOptions> activationOptions,
        CancellationToken cancellationToken)
    {
        SlotConfigurationActivationOptions options = activationOptions.Value;
        context.Response.Headers.CacheControl = "no-store";

        string? expected = Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expected))
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Slot configuration activation unavailable");

        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            !FixedTimeEquals(expected, header.Parameter))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return TypedResults.Unauthorized();
        }

        if (request is null ||
            string.IsNullOrWhiteSpace(request.AgvId) ||
            string.IsNullOrWhiteSpace(request.SlotModelVersionId) ||
            request.Administrator is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Incomplete activation request",
                detail: "agvId, slotModelVersionId and administrator are all required.");
        }

        ProtocolOperatorContext administrator = new(
            request.Administrator.OperatorId,
            request.Administrator.VerificationMethod,
            request.Administrator.VerifiedAt);
        try
        {
            administrator.Validated();
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        {
            // 协议把 verificationMethod 冻结在 BADGE／SESSION 两个值上，administrator 又是
            // additionalProperties:false。让一条注定被车拒收的命令出去，等于在审计里留下一次
            // 从未生效的激活。
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Invalid operator context",
                detail: error.Message);
        }

        SessionRecoveryRow? session = await dbContext.SessionRecoveries.AsNoTracking()
            .FirstOrDefaultAsync(row => row.AgvId == request.AgvId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "No session for this vehicle",
                detail: $"No session has ever been established for '{request.AgvId}'.");
        }

        try
        {
            SlotConfigurationActivationRow activation = await dispatcher.IssueAsync(
                request.AgvId,
                request.SlotModelVersionId,
                session.SessionGeneration,
                administrator,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);

            return TypedResults.Accepted(
                $"/api/governance/v1/slot-configuration-activations/{activation.ActivationId}",
                new SlotConfigurationActivationAcceptedResponse(
                    activation.ActivationId,
                    activation.AgvId,
                    activation.SlotModelVersionId,
                    activation.ConfigurationVersion,
                    activation.Fingerprint,
                    activation.State.ToString(),
                    activation.RecoveryRole,
                    activation.CommandMessageId,
                    session.SessionGeneration));
        }
        catch (IOException error)
        {
            // 车此刻不在线。命令已经落库并进了发件箱，车回来时按 SLOT_CONFIGURATION 补发——那正是
            // PENDING_RESULT_REPLAY 准备好要处理的情况，所以这是一次被受理的下发，不是一次失败。
            //
            // 这里原来让异常冒泡成 500，并且留下一行看起来没人管的 PENDING_RESULT。2026-09-10 的 G3
            // 撞上过：evidence/g3/20260910-fp-is-14-fingerprint-mismatch-corrected。落库那一半从来
            // 就是对的，错的只是把它报成服务器错误。
            // Ordered in memory, not in SQL: the SQLite provider refuses DateTimeOffset in ORDER BY,
            // and this is the one path that only ever runs against a real vehicle being offline.
            List<SlotConfigurationActivationRow> pending = await dbContext
                .Set<SlotConfigurationActivationRow>().AsNoTracking()
                .Where(row => row.AgvId == request.AgvId &&
                              row.State == SlotConfigurationActivationState.PendingResult)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            SlotConfigurationActivationRow queued = pending.MaxBy(row => row.IssuedAt)
                ?? throw new InvalidOperationException(
                    "The activation was not persisted before the send failed.", error);
            return TypedResults.Accepted(
                $"/api/governance/v1/slot-configuration-activations/{queued.ActivationId}",
                new SlotConfigurationActivationAcceptedResponse(
                    queued.ActivationId,
                    queued.AgvId,
                    queued.SlotModelVersionId,
                    queued.ConfigurationVersion,
                    queued.Fingerprint,
                    queued.State.ToString(),
                    queued.RecoveryRole,
                    queued.CommandMessageId,
                    session.SessionGeneration));
        }
        catch (ActivationTargetIncompleteException error)
        {
            // 目标那一版没发布，或者它的 IO 绑定不齐。这是治理状态的问题，不是请求写错了。
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Activation target is not publishable",
                detail: error.Message);
        }
    }

    private static bool FixedTimeEquals(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(presented));
}

/// <summary>发起一次激活要交上来的东西。会话代不在其中——服务端自己读。</summary>
public sealed record SlotConfigurationActivationRequest(
    string AgvId,
    string SlotModelVersionId,
    SlotConfigurationActivationOperator Administrator);

/// <summary>协议 <c>OperatorContext</c> 的线上形状，由调用方交上来。</summary>
public sealed record SlotConfigurationActivationOperator(
    string OperatorId,
    string VerificationMethod,
    DateTimeOffset VerifiedAt);

/// <summary>命令已经上线，结果还没回来。</summary>
public sealed record SlotConfigurationActivationAcceptedResponse(
    string ActivationId,
    string AgvId,
    string SlotModelVersionId,
    long ConfigurationVersion,
    string Fingerprint,
    string State,
    string RecoveryRole,
    string? CommandMessageId,
    long SessionGeneration);
