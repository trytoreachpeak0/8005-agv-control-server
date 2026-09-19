using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 看板写操作的通用路由：每个动作一张确认页（<c>GET /actions/{id}</c>）和一个提交（<c>POST /actions/{id}</c>）。看板主程序只调
/// 这里两次（注册与挂载），不认识任何一个具体动作。
/// </summary>
/// <remarks>
/// <para>
/// <b>确认页不自动刷新。</b>看板主页每 2 秒刷新一次，表单放在主页上会把正在填的理由刷掉；所以链接指到这一页，提交之后回主页。
/// </para>
/// <para>
/// <b>只收看板自己的提交。</b>控制端上随便一个网页都能向 <c>127.0.0.1:58009</c> 提交表单，没有人员认证时那就等于任何网页都能停掉
/// 派车。浏览器提交表单时带 <c>Origin</c>，这里要求它正好是看板自身的来源；请求的 <c>Host</c> 还必须是回环名字，免得一个把域名
/// 改解析到 127.0.0.1 的网页让 <c>Origin</c> 与 <c>Host</c> 恰好相等。缺 <c>Origin</c> 同样拒绝。
/// </para>
/// </remarks>
public static class DashboardActionRoutes
{
    /// <summary>确认页与提交的路径前缀。</summary>
    public const string Prefix = "/actions/";

    private const string ControlServerClient = "dashboard-actions";

    private static readonly string[] LoopbackHostNames = ["127.0.0.1", "localhost", "[::1]", "::1"];

    public static IServiceCollection AddDashboardActions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(DashboardActionCatalog.Discovered);
        services.AddHttpClient(ControlServerClient, client =>
        {
            client.BaseAddress = new Uri(configuration["Dashboard:controlServerBaseUrl"] ?? "http://127.0.0.1:58007");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        return services;
    }

    public static WebApplication MapDashboardActions(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(Prefix + "{actionId}", (string actionId, HttpContext context, DashboardActionCatalog catalog) =>
            catalog.Find(actionId) is IDashboardAction action
                ? Results.Content(RenderConfirmation(action, context.Request.Query), "text/html; charset=utf-8")
                : Results.NotFound());
        app.MapPost(Prefix + "{actionId}", SubmitAsync);
        return app;
    }

    private static async Task<IResult> SubmitAsync(
        string actionId,
        HttpContext context,
        DashboardActionCatalog catalog,
        IHttpClientFactory clients,
        CancellationToken cancellationToken)
    {
        if (catalog.Find(actionId) is not IDashboardAction action)
        {
            return Results.NotFound();
        }
        if (!IsOwnSubmission(context.Request))
        {
            return Page(
                StatusCodes.Status403Forbidden,
                action.Title,
                "这次提交不是从本看板的确认页发出的，已拒绝。请在控制端的看板上操作。");
        }
        if (!context.Request.HasFormContentType)
        {
            return Page(StatusCodes.Status400BadRequest, action.Title, "提交的不是表单。");
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellationToken);
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (DashboardActionField field in action.Fields)
        {
            fields[field.Name] = form[field.Name].ToString();
        }

        HttpClient client = clients.CreateClient(ControlServerClient);
        try
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                action.TargetPath, action.BuildRequest(fields), cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                context.Response.Headers.Location = "/";
                return Results.StatusCode(StatusCodes.Status303SeeOther);
            }
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return Page((int)response.StatusCode, action.Title, $"ControlServer 拒绝了这次提交：{Explain(body)}");
        }
        catch (HttpRequestException exception)
        {
            return Page(StatusCodes.Status502BadGateway, action.Title, $"设备连接失败：{exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Page(StatusCodes.Status504GatewayTimeout, action.Title, "ControlServer 未在 10 秒内应答，提交结果未知，请回主页看这一行的状态。");
        }
    }

    /// <summary>The submission came from a page this dashboard served, reached under a loopback name.</summary>
    private static bool IsOwnSubmission(HttpRequest request)
    {
        string host = request.Host.Host;
        if (!LoopbackHostNames.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        string? origin = request.Headers.Origin;
        return !string.IsNullOrEmpty(origin)
            && string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    private static string RenderConfirmation(IDashboardAction action, IQueryCollection query)
    {
        StringBuilder html = new();
        // No refresh declaration on this page, on purpose: see the remarks above.
        html.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">")
            .Append(CultureInfo.InvariantCulture, $"<title>{WebUtility.HtmlEncode(action.Title)}</title></head><body>")
            .Append(CultureInfo.InvariantCulture, $"<h1>{WebUtility.HtmlEncode(action.Title)}</h1>")
            .Append(CultureInfo.InvariantCulture,
                $"<form method=\"post\" action=\"{WebUtility.HtmlEncode(Prefix + action.ActionId)}\">");
        foreach (DashboardActionField field in action.Fields)
        {
            string name = WebUtility.HtmlEncode(field.Name);
            string label = WebUtility.HtmlEncode(field.Label);
            string value = WebUtility.HtmlEncode(query[field.Name].ToString());
            string required = field.Required ? " required" : string.Empty;
            switch (field.Kind)
            {
                case DashboardActionFieldKind.Hidden:
                    html.Append(CultureInfo.InvariantCulture, $"<p>{label}：{value}</p>")
                        .Append(CultureInfo.InvariantCulture, $"<input type=\"hidden\" name=\"{name}\" value=\"{value}\">");
                    break;
                case DashboardActionFieldKind.TextArea:
                    html.Append(CultureInfo.InvariantCulture,
                        $"<p><label>{label}<br><textarea name=\"{name}\" rows=\"3\" cols=\"60\"{required}></textarea></label></p>");
                    break;
                default:
                    html.Append(CultureInfo.InvariantCulture,
                        $"<p><label>{label}<br><input type=\"text\" name=\"{name}\"{required}></label></p>");
                    break;
            }
        }
        html.Append(CultureInfo.InvariantCulture, $"<p><button type=\"submit\">{WebUtility.HtmlEncode(action.Title)}</button> ")
            .Append("<a href=\"/\">不提交，回看板</a></p></form></body></html>");
        return html.ToString();
    }

    private static IResult Page(int statusCode, string title, string message) =>
        Results.Content(
            "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">"
            + $"<title>{WebUtility.HtmlEncode(title)}</title></head><body>"
            + $"<h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(message)}</p>"
            + "<p><a href=\"/\">回看板</a></p></body></html>",
            "text/html; charset=utf-8",
            Encoding.UTF8,
            statusCode);

    /// <summary>A problem response's detail (or title); anything else as it came, cut short.</summary>
    private static string Explain(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (string property in new[] { "detail", "title" })
                {
                    if (document.RootElement.TryGetProperty(property, out JsonElement value)
                        && value.ValueKind == JsonValueKind.String)
                    {
                        return value.GetString()!;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: shown as it came.
        }
        return body.Length > 500 ? body[..500] : body;
    }
}
