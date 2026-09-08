using System.Net;
using System.Net.Http.Json;
using ControlServer.Host.Runtime;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

public sealed class HttpSublotBoxCountReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ReadsOnlyTheConfiguredBoundSublotQueryAndValidatesResponseIdentity()
    {
        RecordingHandler handler = new();
        HttpClient client = new(handler) { BaseAddress = new Uri("http://mes-query.test") };
        JourneyRuntimeOptions options = new()
        {
            SublotBoxCountPath = "/api/v2/sublot-box-count",
            MaximumEvidenceAge = TimeSpan.FromMinutes(1)
        };
        HttpSublotBoxCountReader reader = new(client, Options.Create(options), new FixedTimeProvider(Now));

        int? result = await reader.ReadMaxBoxCountAsync(
            "SUBLOT A/1", TestContext.Current.CancellationToken);

        Assert.Equal(7, result);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("/api/v2/sublot-box-count?sublot=SUBLOT%20A%2F1", handler.PathAndQuery);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task RejectsStaleBoxCountEvidence()
    {
        RecordingHandler handler = new(Now.AddMinutes(-2));
        HttpClient client = new(handler) { BaseAddress = new Uri("http://mes-query.test") };
        JourneyRuntimeOptions options = new()
        {
            SublotBoxCountPath = "/api/v2/sublot-box-count",
            MaximumEvidenceAge = TimeSpan.FromMinutes(1)
        };
        HttpSublotBoxCountReader reader = new(client, Options.Create(options), new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadMaxBoxCountAsync(
            "SUBLOT A/1", TestContext.Current.CancellationToken));
    }

    private sealed class RecordingHandler(DateTimeOffset? observedAt = null) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? PathAndQuery { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Method = request.Method;
            PathAndQuery = request.RequestUri?.PathAndQuery;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    queryId = "SUBLOT_BOX_COUNT",
                    sublot = "SUBLOT A/1",
                    maxBoxCount = 7,
                    observedAt = observedAt ?? Now
                })
            });
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
