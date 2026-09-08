using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.ClockSkewProxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ControlServer.Tests;

/// <summary>
/// Black-box tests over a real Kestrel, with a real upstream behind it. The proxy exists so an L2
/// scenario can exercise the onboard's clock-skew tolerance on a machine where both ends share one
/// clock; a proxy that quietly stopped shifting, or quietly repaired an upstream refusal, would
/// leave that scenario green for the wrong reason. That is what these pin.
/// </summary>
public sealed class ClockSkewProxyTests
{
    private const string SafetyPath = "/api/onboard/v1/vehicle-safety";
    private static readonly DateTimeOffset UpstreamObservedAt =
        new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task ObservedAtIsShiftedAndEveryOtherFieldSurvivesUntouched()
    {
        await using UpstreamStub upstream = await UpstreamStub.StartAsync();
        await using ProxyFixture proxy = await ProxyFixture.StartAsync(upstream, skewMs: 100);

        JsonElement body = await proxy.ReadSafetyAsync();

        Assert.Equal(
            UpstreamObservedAt.AddMilliseconds(100),
            DateTimeOffset.Parse(
                body.GetProperty("observedAt").GetString()!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
        Assert.Equal("BROKERX-TEST-0001", body.GetProperty("vehicleKey").GetString());
        Assert.Equal("STOPPED", body.GetProperty("motionState").GetString());
        Assert.Equal("RIOT_BEHAVIOR_LAB_R41", body.GetProperty("source").GetString());
        // A field this double has never heard of must still reach the onboard: the server is free
        // to add one, and a proxy that dropped it would silently change what is under test.
        Assert.Equal("keep-me", body.GetProperty("somethingAddedLater").GetString());
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task ANegativeSkewMovesTheEvidenceIntoThePast()
    {
        // The plan's scenario list has both directions: a fast onboard clock must still be normal.
        await using UpstreamStub upstream = await UpstreamStub.StartAsync();
        await using ProxyFixture proxy = await ProxyFixture.StartAsync(upstream, skewMs: -250);

        JsonElement body = await proxy.ReadSafetyAsync();

        Assert.Equal(
            UpstreamObservedAt.AddMilliseconds(-250),
            DateTimeOffset.Parse(
                body.GetProperty("observedAt").GetString()!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task TheControlPlaneChangesTheOffsetWhileTheProxyIsRunning()
    {
        // The scenario pushes the skew past the onboard's tolerance mid-run and pulls it back, so
        // this has to work without a restart.
        await using UpstreamStub upstream = await UpstreamStub.StartAsync();
        await using ProxyFixture proxy = await ProxyFixture.StartAsync(upstream, skewMs: 0);

        await proxy.SetSkewAsync(3000);
        JsonElement shifted = await proxy.ReadSafetyAsync();
        await proxy.SetSkewAsync(100);
        JsonElement restored = await proxy.ReadSafetyAsync();

        Assert.Equal(UpstreamObservedAt.AddMilliseconds(3000), ObservedAt(shifted));
        Assert.Equal(UpstreamObservedAt.AddMilliseconds(100), ObservedAt(restored));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task TheForwardLogRecordsWhatWentInAndWhatCameOut()
    {
        // Without this the scenario cannot tell "the onboard failed closed" from "the proxy was
        // never in the path" -- the two look identical from the server's side.
        await using UpstreamStub upstream = await UpstreamStub.StartAsync();
        await using ProxyFixture proxy = await ProxyFixture.StartAsync(upstream, skewMs: 100);

        _ = await proxy.ReadSafetyAsync();
        JsonElement forward = (await proxy.SnapshotAsync()).GetProperty("body").GetProperty("forward");

        Assert.Equal(1, forward.GetProperty("forwardedRequests").GetInt64());
        Assert.Equal(200, forward.GetProperty("lastUpstreamStatus").GetInt32());
        Assert.Equal(
            TimeSpan.FromMilliseconds(100),
            DateTimeOffset.Parse(forward.GetProperty("lastForwardedObservedAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) -
            DateTimeOffset.Parse(forward.GetProperty("lastUpstreamObservedAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task AnUpstreamRefusalIsForwardedExactlyAsItArrived()
    {
        // Repairing a 401 into something friendlier would hide the fail-closed behaviour the
        // onboard is supposed to show, which is most of what the scenario is about.
        await using UpstreamStub upstream = await UpstreamStub.StartAsync(status: StatusCodes.Status401Unauthorized);
        await using ProxyFixture proxy = await ProxyFixture.StartAsync(upstream, skewMs: 100);

        HttpResponseMessage response = await proxy.Client.GetAsync(SafetyPath, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task TheCallersCredentialReachesTheUpstreamUnchanged()
    {
        // The proxy must not be able to read a projection the onboard itself could not.
        await using UpstreamStub upstream = await UpstreamStub.StartAsync();
        await using ProxyFixture proxy = await ProxyFixture.StartAsync(upstream, skewMs: 0);

        using HttpRequestMessage request = new(HttpMethod.Get, SafetyPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "the-onboard-credential");
        using HttpResponseMessage response = await proxy.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("Bearer the-onboard-credential", upstream.LastAuthorization);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task AnOffsetOutsideTheAllowedRangeIsRefused()
    {
        await using UpstreamStub upstream = await UpstreamStub.StartAsync();
        await using ProxyFixture proxy = await ProxyFixture.StartAsync(upstream, skewMs: 0);

        HttpResponseMessage response = await proxy.SendSkewAsync(600_000);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, ObservedAt(await proxy.ReadSafetyAsync()).Subtract(UpstreamObservedAt).TotalMilliseconds);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public void WithoutATargetItRefusesToStart()
    {
        // Every other double answers for a system that does not exist here; this one forwards to a
        // real server, so a default would be a guess about which server.
        Assert.Null(ClockSkewProxyHost.TryCreate(["--ClockSkewProxy:port=0"]));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public void ANonLoopbackListenerIsRefused()
    {
        Assert.Null(ClockSkewProxyHost.TryCreate(
        [
            "--ClockSkewProxy:port=0",
            "--ClockSkewProxy:listenAddress=0.0.0.0",
            "--ClockSkewProxy:target=http://127.0.0.1:59999"
        ]));
    }

    private static DateTimeOffset ObservedAt(JsonElement body) =>
        DateTimeOffset.Parse(
            body.GetProperty("observedAt").GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    /// <summary>
    /// Stands in for ControlServer's vehicle-safety projection, in the shape
    /// OnboardVehicleSafetyEndpoints returns.
    /// </summary>
    private sealed class UpstreamStub : IAsyncDisposable
    {
        private WebApplication app = null!;

        public string Origin { get; private set; } = null!;

        public string? LastAuthorization { get; private set; }

        public static async Task<UpstreamStub> StartAsync(int status = StatusCodes.Status200OK)
        {
            UpstreamStub stub = new();
            // Port 0 so parallel test classes never collide. Passed as an argument rather than
            // through Kestrel options: this test project is not a Web SDK project, so the Kestrel
            // configuration extensions are not on its builder.
            WebApplicationBuilder builder = WebApplication.CreateBuilder(
                new WebApplicationOptions { Args = ["--urls=http://127.0.0.1:0"] });
            WebApplication app = builder.Build();
            app.MapGet(SafetyPath, (HttpContext context) =>
            {
                stub.LastAuthorization = context.Request.Headers.Authorization.ToString();
                return status == StatusCodes.Status200OK
                    ? Results.Text(
                        $$"""
                        {"vehicleKey":"BROKERX-TEST-0001","motionState":"STOPPED",
                         "observedAt":"{{UpstreamObservedAt:O}}","source":"RIOT_BEHAVIOR_LAB_R41",
                         "reasonCodes":[],"somethingAddedLater":"keep-me"}
                        """,
                        "application/json")
                    : Results.StatusCode(status);
            });
            stub.app = app;
            await app.StartAsync(TestContext.Current.CancellationToken);
            stub.Origin = app.Urls.First();
            return stub;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    private sealed class ProxyFixture : IAsyncDisposable
    {
        private WebApplication app = null!;

        public HttpClient Client { get; private set; } = null!;

        public static async Task<ProxyFixture> StartAsync(UpstreamStub upstream, int skewMs)
        {
            ProxyFixture fixture = new();
            // Port 0 lets the OS pick, so parallel test classes never collide on 58090.
            WebApplication? app = ClockSkewProxyHost.TryCreate(
            [
                "--ClockSkewProxy:port=0",
                "--ClockSkewProxy:instanceId=clock-skew-proxy-test",
                "--ClockSkewProxy:target=" + upstream.Origin,
                "--ClockSkewProxy:Seed:skewMs=" + skewMs.ToString(CultureInfo.InvariantCulture)
            ]);
            Assert.NotNull(app);
            fixture.app = app;
            await app.StartAsync(TestContext.Current.CancellationToken);
            fixture.Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            return fixture;
        }

        public async Task<JsonElement> ReadSafetyAsync()
        {
            HttpResponseMessage response = await Client.GetAsync(SafetyPath, TestContext.Current.CancellationToken);
            string json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, json);
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        public async Task<JsonElement> SnapshotAsync()
        {
            using JsonDocument document = JsonDocument.Parse(
                await Client.GetStringAsync("/control/v1/snapshot", TestContext.Current.CancellationToken));
            return document.RootElement.Clone();
        }

        public async Task SetSkewAsync(int skewMs)
        {
            HttpResponseMessage response = await SendSkewAsync(skewMs);
            Assert.True(
                response.IsSuccessStatusCode,
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        public async Task<HttpResponseMessage> SendSkewAsync(int skewMs)
        {
            JsonElement snapshot = await SnapshotAsync();
            return await Client.PutAsJsonAsync(
                "/control/v1/skew",
                new
                {
                    runId = snapshot.GetProperty("runId").GetString(),
                    commandId = Guid.NewGuid().ToString("N"),
                    expectedRevision = snapshot.GetProperty("revision").GetInt64(),
                    skewMs
                },
                TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }
}
