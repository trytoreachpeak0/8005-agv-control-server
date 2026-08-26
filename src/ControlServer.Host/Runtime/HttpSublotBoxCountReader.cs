using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Application;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

public sealed class HttpSublotBoxCountReader(
    HttpClient httpClient,
    IOptions<JourneyRuntimeOptions> options,
    TimeProvider timeProvider) : ISublotBoxCountReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly JourneyRuntimeOptions runtimeOptions = options.Value;

    public async Task<int?> ReadMaxBoxCountAsync(string sublot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sublot);
        string separator = runtimeOptions.SublotBoxCountPath.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        string requestPath = runtimeOptions.SublotBoxCountPath + separator +
                             "sublot=" + Uri.EscapeDataString(sublot);
        using HttpRequestMessage request = new(HttpMethod.Get, requestPath);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        SublotBoxCountResponse body = await response.Content.ReadFromJsonAsync<SublotBoxCountResponse>(
            SerializerOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("SUBLOT_BOX_COUNT returned an empty body.");
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!string.Equals(body.QueryId, "SUBLOT_BOX_COUNT", StringComparison.Ordinal) ||
            !string.Equals(body.Sublot, sublot, StringComparison.Ordinal) ||
            body.MaxBoxCount <= 0 ||
            body.ObservedAt == default ||
            body.ObservedAt > now ||
            now - body.ObservedAt > runtimeOptions.MaximumEvidenceAge)
        {
            throw new InvalidDataException("SUBLOT_BOX_COUNT response identity or value is invalid.");
        }
        return body.MaxBoxCount;
    }

    private sealed record SublotBoxCountResponse(
        string? QueryId,
        string? Sublot,
        int MaxBoxCount,
        DateTimeOffset ObservedAt);
}
