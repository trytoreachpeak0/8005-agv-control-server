using System.Globalization;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.FieldOps;

/// <summary>
/// 站点独占的人工释放（control-server#419）：公共站点（<c>FIXED_TASK_STATION</c>）与等待点（<c>WAITING_POINT</c>）的持有车离线、被拖走或
/// 退役后，离点清扫永远不放那一行；这个动词是受治理的出口。
/// </summary>
/// <remarks>
/// <para>
/// <b>两种走法，同一段判定</b>（<see cref="StationExclusivityManualRelease"/>）。服务端在线时给 <c>--server &lt;基址&gt;</c>，本工具把请求交给
/// Host 的 <c>/api/field-ops/v1/station-exclusivity-releases</c>，用故障人工恢复入口那把凭据（环境变量，默认
/// <c>CONTROL_SERVER_FAULT_RECOVERY_CREDENTIAL</c>，<c>--credential-env</c> 可改）；Host 另读一次 RIoT 做交叉核对。服务端停着时给
/// <c>--database</c>，直接对库执行，没有 RIoT 交叉核对，结果里如实写 <c>NOT_AVAILABLE</c>。两种走法与离点清扫用同一个删除条件，
/// 同刻只有一方生效。
/// </para>
/// <para>
/// <b>直接写库之前先探一次服务端</b>（#422 审查）：<c>--database</c> 必须同时给 <c>--probe-server &lt;基址&gt;</c>，本工具对它的
/// <c>/health/live</c> 发一次请求；得到任何 HTTP 应答（服务端在跑）就拒绝（<c>SERVER_RUNNING</c>），一行不写，提示改用
/// <c>--server</c>——在线时要走 Host 那条有 RIoT 交叉核对的路。只有连接被主动拒绝（<c>ConnectionRefused</c>：那个地址上没人监听）
/// 才对库执行。超时或任何别的错误都不是「停着」（#459）：在跑却答得慢的服务端、卡死却仍占着端口的进程（内核照样完成握手）、
/// 填错成不回 RST 的地址，都只会超时；那是 <c>SERVER_STATE_UNKNOWN</c>，同样一行不写。
/// </para>
/// <para>
/// 必填：<c>--map</c>、<c>--station</c>、<c>--vehicle-key</c>（持有车的 RIoT <c>VehicleKey</c>）、<c>--operator</c>、<c>--reason</c>、
/// <c>--site-verification</c>（现场核实「车不在站上」的记录引用）；<c>--role</c> 可选、照录。缺操作员、理由或核实记录不是用法错误，
/// 是拒绝理由，照样写审计。
/// </para>
/// </remarks>
internal static partial class Program
{
    private const string ReleaseStationExclusivityCommand = "release-station-exclusivity";

    private const string DefaultCredentialVariable = "CONTROL_SERVER_FAULT_RECOVERY_CREDENTIAL";

    private const string ReleaseStationExclusivityUsage =
        ReleaseStationExclusivityCommand + " needs --map <id> --station <id> and either --server <base url> (server running) or "
        + "--database <file> --probe-server <base url> (server stopped), with --vehicle-key --operator --reason --site-verification";

    /// <summary>
    /// 探测服务端是否在跑的超时。超时只说明「没得到结论」，不说明服务端停着（#459）：负载下在跑的服务端可能答得比这慢。
    /// </summary>
    private static readonly TimeSpan ServerProbeTimeout = TimeSpan.FromSeconds(5);

    private static async Task<int> ReleaseStationExclusivityAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        if (!TryReadStation(options, out int mapId, out int stationId) ||
            !options.TryGetValue("probe-server", out string? probeText) ||
            !Uri.TryCreate(probeText, UriKind.Absolute, out Uri? probe))
        {
            return Usage(ReleaseStationExclusivityUsage);
        }
        ServerProbe answer = await ProbeServerAsync(probe);
        if (answer.State == ServerProbeState.Inconclusive)
        {
            return Emit(
                new
                {
                    command = ReleaseStationExclusivityCommand,
                    outcome = "SERVER_STATE_UNKNOWN",
                    via = "database",
                    mapId,
                    stationId,
                    probeServer = probe.ToString(),
                    detail = $"The probe of {probe} reached no conclusion (timeout or another error: {answer.Cause}): nothing "
                        + "was written. Confirm the server is stopped and try again, or run the verb with --server instead."
                },
                1);
        }
        if (answer.State == ServerProbeState.Answered)
        {
            return Emit(
                new
                {
                    command = ReleaseStationExclusivityCommand,
                    outcome = "SERVER_RUNNING",
                    via = "database",
                    mapId,
                    stationId,
                    probeServer = probe.ToString(),
                    probeHttpStatus = answer.HttpStatus,
                    detail = "The server answered, so it is running: nothing was written. Run the verb with --server instead, "
                        + "which goes through the server and checks RIoT."
                },
                1);
        }

        StationExclusivityManualReleaseResult result = await StationExclusivityManualRelease.ReleaseAsync(
            context,
            governance,
            vehicleFacts: null,
            StationReleaseRequest(options, mapId, stationId),
            now,
            CancellationToken.None);
        return Emit(
            new
            {
                command = ReleaseStationExclusivityCommand,
                outcome = result.Released ? "OK" : "REJECTED",
                via = "database",
                mapId,
                stationId,
                codes = result.Codes,
                stationKind = result.Holder?.StationKind,
                holderVehicleKey = result.Holder?.VehicleKey,
                holderJourneyId = result.Holder?.JourneyId,
                riotCrossCheck = result.RiotCrossCheck,
                auditRecordId = result.AuditRecordId
            },
            result.Released ? 0 : 1);
    }

    /// <summary>服务端在线：交给 Host。连不上、凭据不对、入口没开，都是 <c>UNAVAILABLE</c>，退出码 1，库一行没动。</summary>
    private static async Task<int> ReleaseStationExclusivityViaServerAsync(Dictionary<string, string> options)
    {
        if (!TryReadStation(options, out int mapId, out int stationId) ||
            !Uri.TryCreate(options["server"], UriKind.Absolute, out Uri? server))
        {
            return Usage(ReleaseStationExclusivityUsage);
        }
        string variable = options.GetValueOrDefault("credential-env", DefaultCredentialVariable);
        string? credential = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(credential))
        {
            return Usage($"{ReleaseStationExclusivityCommand} --server needs the credential in environment variable {variable}");
        }

        StationExclusivityManualReleaseRequest request = StationReleaseRequest(options, mapId, stationId);
        using HttpClient client = new() { BaseAddress = server, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(
                "api/field-ops/v1/station-exclusivity-releases",
                new
                {
                    mapId,
                    stationId,
                    vehicleKey = request.VehicleKey,
                    operatorId = request.OperatorId,
                    reason = request.Reason,
                    siteVerification = request.SiteVerification,
                    claimedRole = request.ClaimedRole
                });
        }
        catch (Exception unreachable) when (unreachable is HttpRequestException or TaskCanceledException)
        {
            return Emit(
                new
                {
                    command = ReleaseStationExclusivityCommand,
                    outcome = "UNAVAILABLE",
                    via = "server",
                    server = server.ToString(),
                    detail = "The server did not answer. If it is stopped, run the verb with --database instead.",
                    cause = unreachable.Message
                },
                1);
        }

        using (response)
        {
            JsonElement? body = null;
            string text = await response.Content.ReadAsStringAsync();
            try
            {
                body = text.Length == 0 ? null : JsonSerializer.Deserialize<JsonElement>(text);
            }
            catch (JsonException)
            {
                // Not a body this verb understands; the status says what happened.
            }
            int status = (int)response.StatusCode;
            string outcome = status switch
            {
                200 => "OK",
                409 => "REJECTED",
                _ => "UNAVAILABLE"
            };
            return Emit(
                new
                {
                    command = ReleaseStationExclusivityCommand,
                    outcome,
                    via = "server",
                    server = server.ToString(),
                    mapId,
                    stationId,
                    httpStatus = status,
                    codes = Read(body, "codes"),
                    stationKind = Read(body, "stationKind"),
                    holderVehicleKey = Read(body, "holderVehicleKey"),
                    holderJourneyId = Read(body, "holderJourneyId"),
                    riotCrossCheck = Read(body, "riotCrossCheck"),
                    auditRecordId = Read(body, "auditRecordId"),
                    detail = status is 200 or 409 ? null : Read(body, "title") ?? (object)text
                },
                outcome == "OK" ? 0 : 1);
        }
    }

    /// <summary>
    /// 探一次 <c>/health/live</c>。应答了（任何状态码）是 <see cref="ServerProbeState.Answered"/>；连接被主动拒绝（那个地址上没人监听）
    /// 是 <see cref="ServerProbeState.Refused"/>，唯一允许直接写库的结论；超时与其他任何错误是 <see cref="ServerProbeState.Inconclusive"/>。
    /// </summary>
    private static async Task<ServerProbe> ProbeServerAsync(Uri server)
    {
        using HttpClient client = new() { BaseAddress = server, Timeout = ServerProbeTimeout };
        try
        {
            using HttpResponseMessage response = await client.GetAsync("health/live");
            return new ServerProbe(ServerProbeState.Answered, (int)response.StatusCode, null);
        }
        catch (HttpRequestException refused) when (refused.InnerException is SocketException
                                                   {
                                                       SocketErrorCode: SocketError.ConnectionRefused
                                                   })
        {
            return new ServerProbe(ServerProbeState.Refused, null, refused.Message);
        }
        catch (Exception other)
        {
            return new ServerProbe(ServerProbeState.Inconclusive, null, $"{other.GetType().Name}: {other.Message}");
        }
    }

    private enum ServerProbeState
    {
        Answered,
        Refused,
        Inconclusive
    }

    private sealed record ServerProbe(ServerProbeState State, int? HttpStatus, string? Cause);

    private static StationExclusivityManualReleaseRequest StationReleaseRequest(
        Dictionary<string, string> options, int mapId, int stationId) =>
        new(
            mapId,
            stationId,
            options.GetValueOrDefault("vehicle-key"),
            options.GetValueOrDefault("operator"),
            options.GetValueOrDefault("reason"),
            options.GetValueOrDefault("site-verification"),
            options.GetValueOrDefault("role"));

    private static bool TryReadStation(Dictionary<string, string> options, out int mapId, out int stationId)
    {
        stationId = 0;
        return TryReadMap(options, out mapId)
               && options.TryGetValue("station", out string? text)
               && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out stationId)
               && stationId > 0;
    }

    private static object? Read(JsonElement? body, string property) =>
        body is { ValueKind: JsonValueKind.Object } found && found.TryGetProperty(property, out JsonElement value)
            ? value.ValueKind == JsonValueKind.Null ? null : value.Clone()
            : null;
}
