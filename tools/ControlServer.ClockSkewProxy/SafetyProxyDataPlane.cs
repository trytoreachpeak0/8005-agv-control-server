using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.TestDoubles;

namespace ControlServer.ClockSkewProxy;

/// <summary>
/// The one route the onboard actually talks to. Everything about the exchange is passed through
/// untouched except the single field this double exists to move.
/// </summary>
public static class SafetyProxyDataPlane
{
    private const string SafetyPath = "/api/onboard/v1/vehicle-safety";

    public static void MapSafetyProxyDataPlane(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<ClockSkewProxyState> engine =
            app.Services.GetRequiredService<CommandEngine<ClockSkewProxyState>>();
        ClockSkewProxyHost.ForwardTarget target =
            app.Services.GetRequiredService<ClockSkewProxyHost.ForwardTarget>();
        ForwardLog log = app.Services.GetRequiredService<ForwardLog>();
        IHttpClientFactory clients = app.Services.GetRequiredService<IHttpClientFactory>();

        app.MapGet(SafetyPath, async (HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            using HttpRequestMessage upstream = new(HttpMethod.Get, new Uri(target.BaseUri, SafetyPath));
            // The credential is the caller's, forwarded verbatim: the proxy must not be able to
            // read a projection the onboard itself could not.
            if (AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? auth))
            {
                upstream.Headers.Authorization = auth;
            }
            upstream.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpClient client = clients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using HttpResponseMessage response = await client
                .SendAsync(upstream, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // A refusal is forwarded exactly as it arrived. Rewriting one into something friendlier
            // would hide the fail-closed behaviour the onboard is supposed to show.
            if (!response.IsSuccessStatusCode)
            {
                log.Record((int)response.StatusCode, null, null);
                return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
            }

            int skewMs = engine.Snapshot().State.SkewMs;
            string? upstreamObservedAt = null;
            string? forwardedObservedAt = null;
            JsonNode? payload;
            try
            {
                payload = JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                // Malformed upstream JSON is the server's answer, not this double's business to
                // repair; the onboard's own INVALID_JSON path should see it.
                log.Record((int)response.StatusCode, null, null);
                return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
            }

            // JsonNode rather than a typed record on purpose: an unknown field the server adds
            // later must reach the onboard unchanged, not be silently dropped by this double.
            if (payload is JsonObject json &&
                json.TryGetPropertyValue("observedAt", out JsonNode? observedNode) &&
                observedNode is not null &&
                DateTimeOffset.TryParse(
                    observedNode.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTimeOffset observedAt))
            {
                upstreamObservedAt = observedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                DateTimeOffset shifted = observedAt.AddMilliseconds(skewMs);
                forwardedObservedAt = shifted.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                json["observedAt"] = forwardedObservedAt;
                body = json.ToJsonString();
            }

            log.Record((int)response.StatusCode, upstreamObservedAt, forwardedObservedAt);
            return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
        });
    }
}
