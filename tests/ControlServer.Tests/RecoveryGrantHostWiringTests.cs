using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ControlServer.Tests;

/// <summary>
/// control-server#797 review M3, M4: the grant is wired into the real Host, not only into the handlers the other tests call
/// directly. A real ControlServer.Host process, with the recovery entries on and grants required, answers a fault recovery
/// request without a grant 403 RECOVERY_GRANT_MISSING -- it could not, were the gate not registered -- and logs that the
/// sweep that ends sessions outside their grant is running. A grant then given through the same process lets the request
/// through the gate.
/// </summary>
public sealed class RecoveryGrantHostWiringTests : IAsyncDisposable
{
    private const string Credential = "host-wiring-fault-recovery-credential";
    private const string AgvId = "AGV-797-HOST";
    private const string VehicleKey = "KEY-797-HOST";

    /// <summary>What <c>RecoveryGrantSweeper</c> logs once it runs (event 9795).</summary>
    private const string SweepRunning = "the recovery grant sweep is running";

    private static readonly string[] ClearFault = ["CLEAR_FAULT"];

    private static readonly string HostExecutable = typeof(RecoveryGrantHostWiringTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "ControlServer.Host.Path").Value!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "w2g-recovery-grant-host-" + Guid.NewGuid().ToString("N"));

    private readonly StringBuilder _output = new();

    private Process? _host;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheRealHostRefusesAFaultRecoveryWithoutAGrantAndRunsTheSweep()
    {
        int healthPort = FreePort();
        Uri health = new($"http://127.0.0.1:{healthPort.ToString(CultureInfo.InvariantCulture)}/");
        Start(healthPort);
        using HttpClient client = new(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = health, Timeout = TimeSpan.FromSeconds(30) };
        await WaitUntilLiveAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Credential);

        using HttpResponseMessage refused = await client.PostAsJsonAsync(
            "api/safety/v1/vehicle-fault-recoveries",
            new { agvId = AgvId, operatorId = "L1-797", action = "CLEAR_FAULT", faultRemedied = true, note = "L1" },
            Token);
        string refusedBody = await refused.Content.ReadAsStringAsync(Token);
        Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{(int)refused.StatusCode}: {refusedBody}");
        Assert.Equal(
            ["RECOVERY_GRANT_MISSING"],
            JsonDocument.Parse(refusedBody).RootElement.GetProperty("reasons").EnumerateArray().Select(item => item.GetString()));
        await WaitForOutputAsync(SweepRunning);

        // Review L5 (ADR-cross-0066 decision 3): the charging station recovery confirmation acts on the station only and is not
        // granted per vehicle -- the same process, grants required and none given, does not refuse it on a grant.
        using HttpResponseMessage station = await client.PostAsJsonAsync(
            "api/charging/v1/station-recoveries",
            new { mapId = 1, stationId = 1, operatorId = "op-r11", basis = "L1", claimedRole = (string?)null },
            Token);
        string stationBody = await station.Content.ReadAsStringAsync(Token);
        Assert.True(station.StatusCode == HttpStatusCode.Conflict, $"{(int)station.StatusCode}: {stationBody}");
        Assert.DoesNotContain("RECOVERY_GRANT_", stationBody, StringComparison.Ordinal);

        using HttpResponseMessage planned = await client.PostAsJsonAsync(
            "api/field-ops/v1/recovery-grants",
            new
            {
                vehicleKey = VehicleKey,
                actions = ClearFault,
                operatorId = "maintenance-797",
                reason = "L1",
                siteVerification = "L1-SITE"
            },
            Token);
        string code = JsonDocument.Parse(await planned.Content.ReadAsStringAsync(Token)).RootElement.GetProperty("confirmationCode").GetString()!;
        using HttpResponseMessage granted = await client.PostAsJsonAsync(
            "api/field-ops/v1/recovery-grants",
            new
            {
                vehicleKey = VehicleKey,
                actions = ClearFault,
                operatorId = "maintenance-797",
                reason = "L1",
                siteVerification = "L1-SITE",
                confirmationCode = code
            },
            Token);
        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);

        using HttpResponseMessage through = await client.PostAsJsonAsync(
            "api/safety/v1/vehicle-fault-recoveries",
            new { agvId = AgvId, operatorId = "L1-797", action = "CLEAR_FAULT", faultRemedied = true, note = "L1" },
            Token);
        string throughBody = await through.Content.ReadAsStringAsync(Token);
        Assert.NotEqual(HttpStatusCode.Forbidden, through.StatusCode);
        Assert.DoesNotContain("RECOVERY_GRANT_", throughBody, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            try
            {
                if (!_host.HasExited)
                {
                    _host.Kill(entireProcessTree: true);
                }
                await _host.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Never started, or already reaped.
            }
            _host.Dispose();
        }
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file the killed host still had open; the temp directory is cleaned up by the machine.
        }
    }

    private void Start(int healthPort)
    {
        Assert.True(File.Exists(HostExecutable), $"The host was not built: {HostExecutable}");
        Directory.CreateDirectory(_root);
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        ProcessStartInfo start = new(HostExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(HostExecutable)!
        };
        string invariant(int port) => port.ToString(CultureInfo.InvariantCulture);
        start.Environment["ConnectionStrings__ControlServer"] = $"Data Source={Path.Combine(_root, "controlserver.db")}";
        start.Environment["Health__url"] = $"http://127.0.0.1:{invariant(healthPort)}";
        start.Environment["OnboardTransport__listenAddress"] = "127.0.0.1";
        start.Environment["OnboardTransport__port"] = invariant(FreePort());
        start.Environment["RIoT__baseUrl"] = $"http://127.0.0.1:{invariant(FreePort())}";
        start.Environment["MesIngest__baseUrl"] = $"http://127.0.0.1:{invariant(FreePort())}";
        start.Environment["JourneyRuntime__enabled"] = "false";
        start.Environment["JourneyRuntime__agvId"] = AgvId;
        start.Environment["JourneyRuntime__vehicleKey"] = VehicleKey;
        start.Environment["VehicleFaultRecovery__enabled"] = "true";
        start.Environment["VehicleFaultRecovery__credentialEnvironmentVariable"] = variable;
        start.Environment[variable] = Credential;
        start.Environment["RecoveryGrant__required"] = "true";
        start.Environment.Remove("CONTROL_SERVER_RIOT_CALL_API_KEY");
        _host = Process.Start(start)!;
        _host.OutputDataReceived += (_, line) => { if (line.Data is not null) lock (_output) _output.AppendLine(line.Data); };
        _host.ErrorDataReceived += (_, line) => { if (line.Data is not null) lock (_output) _output.AppendLine(line.Data); };
        _host.BeginOutputReadLine();
        _host.BeginErrorReadLine();
    }

    private async Task WaitUntilLiveAsync(HttpClient client)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (true)
        {
            if (_host!.HasExited) Assert.Fail($"The host exited ({_host.ExitCode}) before it answered. Output: {Output()}");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(90), $"The host never answered /health/live. Output: {Output()}");
            try
            {
                using HttpResponseMessage live = await client.GetAsync("health/live", Token);
                if (live.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            await Task.Delay(200, Token);
        }
    }

    private async Task WaitForOutputAsync(string text)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!Output().Contains(text, StringComparison.Ordinal))
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"The host never logged '{text}'. Output: {Output()}");
            await Task.Delay(100, Token);
        }
    }

    private string Output()
    {
        lock (_output) return _output.ToString();
    }

    private static int FreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
