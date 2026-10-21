using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ControlServer.FieldOps;

/// <summary>
/// 恢复授权（control-server#797，ADR-cross-0066）：维护人员给一辆车、一趟旅程下限时的恢复授权，车上开异常处置会话、会话里每个动作、控制端故障恢复
/// 入口与控制端的人工清桩确认、解除车辆充电资格暂停都逐请求查它。授权只在服务端内存里，所以三个动词都只走 <c>--server</c>，不开库。
/// </summary>
/// <remarks>
/// <para>
/// <b><c>grant-recovery</c> 先演练、再带确认码执行</b>：不带 <c>--confirm</c> 是演练，输出要下的授权（车、点名的旅程与车眼下那趟旅程的阶段、动作、时长、
/// 替换掉的那条）与确认码，服务端什么都不写；带演练给出的 <c>--confirm</c> 才下发，写治理审计。中间有任何一项变了（例如旅程阶段），拒绝并给出新计划
/// 与新码。授权到期前再下一次就是续期，时长从那一刻重算。
/// </para>
/// <para>
/// 必填：<c>--server</c>、<c>--vehicle-key</c>（RIoT <c>VehicleKey</c>）、<c>--operator</c>、<c>--reason</c>、<c>--site-verification</c>。
/// <c>--journey</c>：涉及旅程的动作（车上的会话、控制端对旅程的故障恢复）要点名这一趟；不给即只对车、不对旅程的授权。<c>--actions</c>：逗号分隔，
/// 不给即只放 <c>RESUME_AFTER_REPAIR</c>（用户 10-21 定）。<c>--minutes</c>：默认 30。<c>--role</c> 可选、照录。缺了不是用法错误，是拒绝理由。
/// 凭据同故障人工恢复入口（环境变量，默认 <c>CONTROL_SERVER_FAULT_RECOVERY_CREDENTIAL</c>，<c>--credential-env</c> 可改）。
/// </para>
/// <para>
/// <c>revoke-recovery</c> 收回一辆车的授权（<c>--server --vehicle-key --operator --reason</c>），车上开着且没有动作在执行的会话随即结束；
/// <c>recovery-grants</c> 列出眼下有效的授权（<c>--server</c>）。下授权之前先问用户（ADR-cross-0066 决策 6）。
/// </para>
/// </remarks>
internal static partial class Program
{
    private const string GrantRecoveryCommand = "grant-recovery";
    private const string RevokeRecoveryCommand = "revoke-recovery";
    private const string RecoveryGrantsCommand = "recovery-grants";

    private const string GrantRecoveryUsage =
        GrantRecoveryCommand + " needs --server <base url> --vehicle-key <key> --operator --reason --site-verification "
        + "[--journey <id>] [--actions <a,b>] [--minutes <n>] [--role <role>] [--confirm <code from the rehearsal>]";

    private static bool IsRecoveryGrantCommand(string command) =>
        command is GrantRecoveryCommand or RevokeRecoveryCommand or RecoveryGrantsCommand;

    private static async Task<int> RecoveryGrantViaServerAsync(string command, Dictionary<string, string> options)
    {
        if (!options.TryGetValue("server", out string? serverText) ||
            !Uri.TryCreate(serverText, UriKind.Absolute, out Uri? server))
        {
            return Usage(command == GrantRecoveryCommand ? GrantRecoveryUsage : $"{command} needs --server <base url>");
        }
        int? minutes = null;
        if (options.TryGetValue("minutes", out string? minutesText))
        {
            if (!int.TryParse(minutesText, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
            {
                return Usage(GrantRecoveryUsage);
            }
            minutes = parsed;
        }
        string variable = options.GetValueOrDefault("credential-env", DefaultCredentialVariable);
        string? credential = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(credential))
        {
            return Usage($"{command} needs the credential in environment variable {variable}");
        }

        // Same as the station release (#459 review): the server is on this machine or the plant LAN, never behind a proxy.
        using HttpClient client = new(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = server,
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        HttpResponseMessage response;
        try
        {
            response = command switch
            {
                GrantRecoveryCommand => await client.PostAsJsonAsync(
                    "api/field-ops/v1/recovery-grants",
                    new
                    {
                        vehicleKey = options.GetValueOrDefault("vehicle-key"),
                        journeyId = options.GetValueOrDefault("journey"),
                        actions = options.TryGetValue("actions", out string? actions)
                            ? actions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            : null,
                        minutes,
                        operatorId = options.GetValueOrDefault("operator"),
                        reason = options.GetValueOrDefault("reason"),
                        siteVerification = options.GetValueOrDefault("site-verification"),
                        claimedRole = options.GetValueOrDefault("role"),
                        confirmationCode = options.GetValueOrDefault("confirm")
                    }),
                RevokeRecoveryCommand => await client.PostAsJsonAsync(
                    "api/field-ops/v1/recovery-grant-revocations",
                    new
                    {
                        vehicleKey = options.GetValueOrDefault("vehicle-key"),
                        operatorId = options.GetValueOrDefault("operator"),
                        reason = options.GetValueOrDefault("reason"),
                        claimedRole = options.GetValueOrDefault("role")
                    }),
                _ => await client.GetAsync("api/field-ops/v1/recovery-grants")
            };
        }
        catch (Exception unreachable) when (unreachable is HttpRequestException or TaskCanceledException)
        {
            return Emit(
                new
                {
                    command,
                    outcome = "UNAVAILABLE",
                    server = server.ToString(),
                    detail = "The server did not answer. A recovery grant lives in the running server only: start it first.",
                    cause = unreachable.Message
                },
                1);
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync();
            JsonElement? body = null;
            try
            {
                body = text.Length == 0 ? null : JsonSerializer.Deserialize<JsonElement>(text);
            }
            catch (JsonException)
            {
                // Not a body this verb understands; the status says what happened.
            }
            int status = (int)response.StatusCode;
            if (command == RecoveryGrantsCommand)
            {
                return Emit(
                    new
                    {
                        command,
                        outcome = status == 200 ? "OK" : "UNAVAILABLE",
                        server = server.ToString(),
                        httpStatus = status,
                        grants = status == 200 ? body : null,
                        detail = status == 200 ? null : Read(body, "title") ?? (object)text
                    },
                    status == 200 ? 0 : 1);
            }
            string outcome = status switch
            {
                200 => Read(body, "outcome")?.ToString() ?? "OK",
                404 or 409 => "REJECTED",
                _ => "UNAVAILABLE"
            };
            return Emit(
                new
                {
                    command,
                    outcome,
                    server = server.ToString(),
                    httpStatus = status,
                    codes = Read(body, "codes"),
                    confirmationCode = Read(body, "confirmationCode"),
                    plan = Read(body, "plan"),
                    grant = Read(body, "grant"),
                    auditRecordId = Read(body, "auditRecordId"),
                    detail = status is 200 or 404 or 409 ? null : Read(body, "title") ?? (object)text
                },
                outcome is "GRANTED" or "REVOKED" or "PLANNED" ? 0 : 1);
        }
    }
}
