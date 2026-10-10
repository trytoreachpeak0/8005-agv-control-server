using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ControlServer.Tests;

/// <summary>
/// FieldOps <c>release-station-exclusivity</c> 的进程入口（control-server#419）：服务端停着时对库直接执行，服务端在线时（<c>--server</c>）
/// 交给 Host 接口——这里起一个挂着真接口的主机，证明两边的路由、凭据与字段对得上。每条命令一个 JSON 对象，退出码 0／1／2。
/// </summary>
public sealed class StationExclusivityFieldOpsTests : IAsyncDisposable
{
    private const string Verb = "release-station-exclusivity";
    private const string KeyA = "KEY-A";

    private static readonly DateTimeOffset At = Batch7JourneyFixture.Now;

    /// <summary>A server address nothing answers on: the probe before a direct database write finds the server stopped.</summary>
    private const string NobodyListens = "http://127.0.0.1:1/";

    private static readonly string FieldOps = typeof(StationExclusivityFieldOpsTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "ControlServer.FieldOps.Path").Value!;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "w2g-station-release-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(_directory, "controlserver.db");

    [Fact]
    public async Task WithTheServerStoppedTheVerbReleasesAgainstTheDatabaseWithoutARiotCrossCheck()
    {
        await SeedAsync();

        (int exit, JsonElement output) = await RunAsync(null, [.. Arguments(), "--database", DatabasePath, "--probe-server", NobodyListens]);

        Assert.Equal(
            (0, "OK", "database", KeyA, "journey:a", StationExclusivityManualRelease.CrossCheckNotAvailable),
            (exit, output.GetProperty("outcome").GetString(), output.GetProperty("via").GetString(),
                output.GetProperty("holderVehicleKey").GetString(), output.GetProperty("holderJourneyId").GetString(),
                output.GetProperty("riotCrossCheck").GetString()));
        await using ControlServerDbContext read = Open();
        Assert.Empty(await read.Set<StationExclusivityRow>().ToArrayAsync(Token));
        Assert.Equal(
            StationExclusivityManualRelease.ReleasedByOperator,
            (await read.Set<StationExclusivityRecordRow>().SingleAsync(Token)).ReleaseReason);
    }

    /// <summary>缺现场核实记录不是用法错误：退出码 1，<c>REJECTED</c>，理由码列出，行不动，审计留下。</summary>
    [Fact]
    public async Task AMissingSiteVerificationIsARefusalNotAUsageError()
    {
        await SeedAsync();
        string[] arguments = Arguments();
        int index = Array.IndexOf(arguments, "--site-verification");

        (int exit, JsonElement output) = await RunAsync(
            null, [.. arguments[..index], .. arguments[(index + 2)..], "--database", DatabasePath, "--probe-server", NobodyListens]);

        Assert.Equal((1, "REJECTED"), (exit, output.GetProperty("outcome").GetString()));
        Assert.Equal(
            [StationExclusivityManualRelease.SiteVerificationRequired],
            output.GetProperty("codes").EnumerateArray().Select(code => code.GetString()));
        await using ControlServerDbContext read = Open();
        Assert.Single(await read.Set<StationExclusivityRow>().ToArrayAsync(Token));
        Assert.Equal(
            1, await read.Set<AdministratorAuditRecordRow>().CountAsync(row => row.Action == StationExclusivityManualRelease.AuditAction, Token));
    }

    [Theory]
    [InlineData("--map")]
    [InlineData("--station")]
    [InlineData("--probe-server")]
    public async Task AMissingStationOrServerProbeIsAUsageError(string option)
    {
        await SeedAsync();
        string[] arguments = [.. Arguments(), "--database", DatabasePath, "--probe-server", NobodyListens];
        int index = Array.IndexOf(arguments, option);

        (int exit, _) = await RunAsync(null, [.. arguments[..index], .. arguments[(index + 2)..]]);

        Assert.Equal(2, exit);
        await using ControlServerDbContext read = Open();
        Assert.Single(await read.Set<StationExclusivityRow>().ToArrayAsync(Token));
    }

    /// <summary>
    /// 服务端在线：<c>--server</c> 把请求交给 Host，不要 <c>--database</c>。第一次凭据变量没给到进程——用法错误；给到了——Host 放行，
    /// 输出里是 Host 的结果（经过 Host，所以交叉核对不是 <c>NOT_AVAILABLE</c>）；再来一次，站已不被占，<c>REJECTED</c>。
    /// </summary>
    [Fact]
    public async Task WithTheServerRunningTheVerbGoesThroughTheHostEntry()
    {
        await SeedAsync();
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "fieldops-credential");
        try
        {
            await using WebApplication app = await StartServerAsync(variable);
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            string[] arguments = [.. Arguments(), "--server", address, "--credential-env", variable];

            // The test process holds the variable for the host; the child must not inherit it.
            (int noCredentialExit, _) = await RunAsync((variable, null), arguments);
            (int exit, JsonElement output) = await RunAsync((variable, "fieldops-credential"), arguments);
            (int againExit, JsonElement again) = await RunAsync((variable, "fieldops-credential"), arguments);
            await app.StopAsync(Token);

            Assert.Equal(2, noCredentialExit);
            Assert.Equal(
                (0, "OK", "server", 200, KeyA, StationExclusivityManualRelease.CrossCheckOffline),
                (exit, output.GetProperty("outcome").GetString(), output.GetProperty("via").GetString(),
                    output.GetProperty("httpStatus").GetInt32(), output.GetProperty("holderVehicleKey").GetString(),
                    output.GetProperty("riotCrossCheck").GetString()));
            Assert.Equal((1, "REJECTED", 409), (againExit, again.GetProperty("outcome").GetString(), again.GetProperty("httpStatus").GetInt32()));
            Assert.Equal(
                [StationExclusivityManualRelease.StationNotHeld],
                again.GetProperty("codes").EnumerateArray().Select(code => code.GetString()));
            await using ControlServerDbContext read = Open();
            Assert.Empty(await read.Set<StationExclusivityRow>().ToArrayAsync(Token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// 服务端其实在跑，操作员却给了 <c>--database</c>（#422 审查建议 2）：先探服务端，它应答了（哪怕是 404）就拒绝，<c>SERVER_RUNNING</c>、
    /// 退出码 1，一行不写、不写审计——在线时要走 <c>--server</c>，那条路有 RIoT 交叉核对。
    /// </summary>
    [Fact]
    public async Task WithTheServerRunningADirectDatabaseWriteIsRefused()
    {
        await SeedAsync();
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        await using WebApplication app = await StartServerAsync(variable);
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        (int exit, JsonElement output) = await RunAsync(
            null, [.. Arguments(), "--database", DatabasePath, "--probe-server", address]);
        await app.StopAsync(Token);

        Assert.Equal((1, "SERVER_RUNNING"), (exit, output.GetProperty("outcome").GetString()));
        await using ControlServerDbContext read = Open();
        Assert.Single(await read.Set<StationExclusivityRow>().ToArrayAsync(Token));
        Assert.Equal(
            0, await read.Set<AdministratorAuditRecordRow>().CountAsync(row => row.Action == StationExclusivityManualRelease.AuditAction, Token));
    }

    /// <summary>
    /// 服务端在跑却答得比探测超时慢（#459：负载下一次红在这里放行了写库）：超时不是「停着」，<c>SERVER_STATE_UNKNOWN</c>、退出码 1，
    /// 一行不写、不写审计，提示里带着探测地址。
    /// </summary>
    [Fact]
    public async Task AServerThatAnswersAfterTheProbeTimeoutIsNotTakenForStopped()
    {
        await SeedAsync();
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        await using WebApplication app = await StartServerAsync(variable, answerDelay: TimeSpan.FromSeconds(8));
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        (int exit, JsonElement output) = await RunAsync(
            null, [.. Arguments(), "--database", DatabasePath, "--probe-server", address]);
        await app.StopAsync(Token);

        await AssertStateUnknownAndNothingWrittenAsync(exit, output, address);
    }

    /// <summary>
    /// 卡死的服务端进程仍占着端口，内核照样完成握手，却永远不会有 HTTP 应答（#459）：只 accept、从不应答的监听同样是
    /// <c>SERVER_STATE_UNKNOWN</c>，不写库。
    /// </summary>
    [Fact]
    public async Task APortThatAcceptsButNeverAnswersIsNotTakenForStopped()
    {
        await SeedAsync();
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();
        List<TcpClient> accepted = [];
        Task accepting = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    accepted.Add(await silent.AcceptTcpClientAsync(Token));
                }
            }
            catch (Exception stopped) when (stopped is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }, Token);
        string address = $"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}/";

        (int exit, JsonElement output) = await RunAsync(
            null, [.. Arguments(), "--database", DatabasePath, "--probe-server", address]);
        silent.Stop();
        await accepting;
        accepted.ForEach(client => client.Dispose());

        await AssertStateUnknownAndNothingWrittenAsync(exit, output, address);
    }

    /// <summary>
    /// 失败即关不能修过头（#459）：一个刚释放、确实没人监听的端口，连接被主动拒绝，这是唯一的「停着」，照常对库执行。
    /// </summary>
    [Fact]
    public async Task ARefusedConnectionIsTheOneAnswerThatLetsTheDatabaseWriteThrough()
    {
        await SeedAsync();
        string address = $"http://127.0.0.1:{FreedPort()}/";

        (int exit, JsonElement output) = await RunAsync(
            null, [.. Arguments(), "--database", DatabasePath, "--probe-server", address]);

        Assert.Equal(
            (0, "OK", "database", address),
            (exit, output.GetProperty("outcome").GetString(), output.GetProperty("via").GetString(),
                output.GetProperty("probeServer").GetString()));
        await using ControlServerDbContext read = Open();
        Assert.Empty(await read.Set<StationExclusivityRow>().ToArrayAsync(Token));
    }

    /// <summary>
    /// <c>--server</c> 那条路同样不走代理（#459 审查）：进程环境里挂着一个已经死掉的 <c>HTTP_PROXY</c>，服务端在跑，照常经 Host 放行。
    /// </summary>
    [Fact]
    public async Task ADeadProxyInTheEnvironmentDoesNotStopTheServerRoute()
    {
        await SeedAsync();
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "fieldops-credential");
        try
        {
            await using WebApplication app = await StartServerAsync(variable);
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            string deadProxy = $"http://127.0.0.1:{FreedPort()}/";

            (int exit, JsonElement output) = await RunWithEnvironmentAsync(
                [(variable, "fieldops-credential"), ("HTTP_PROXY", deadProxy), ("http_proxy", deadProxy), ("ALL_PROXY", deadProxy),
                    ("NO_PROXY", null), ("no_proxy", null)],
                [.. Arguments(), "--server", address, "--credential-env", variable]);
            await app.StopAsync(Token);

            Assert.Equal(
                (0, "OK", "server"),
                (exit, output.GetProperty("outcome").GetString(), output.GetProperty("via").GetString()));
            Assert.Equal(200, output.GetProperty("httpStatus").GetInt32());
            await using ControlServerDbContext read = Open();
            Assert.Empty(await read.Set<StationExclusivityRow>().ToArrayAsync(Token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// 探测不走代理（#459 审查 S1）：进程环境里挂着一个已经死掉的 <c>HTTP_PROXY</c>，服务端却在跑。走代理时「代理拒绝连接」被当成
    /// 「服务端停着」放行写库；直连才探到服务端本身，<c>SERVER_RUNNING</c>。
    /// </summary>
    [Fact]
    public async Task ADeadProxyInTheEnvironmentDoesNotHideARunningServer()
    {
        await SeedAsync();
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        await using WebApplication app = await StartServerAsync(variable);
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        string deadProxy = $"http://127.0.0.1:{FreedPort()}/";

        (int exit, JsonElement output) = await RunWithEnvironmentAsync(
            [("HTTP_PROXY", deadProxy), ("http_proxy", deadProxy), ("ALL_PROXY", deadProxy), ("NO_PROXY", null), ("no_proxy", null)],
            [.. Arguments(), "--database", DatabasePath, "--probe-server", address]);
        await app.StopAsync(Token);

        Assert.Equal((1, "SERVER_RUNNING"), (exit, output.GetProperty("outcome").GetString()));
        await using ControlServerDbContext read = Open();
        Assert.Single(await read.Set<StationExclusivityRow>().ToArrayAsync(Token));
    }

    /// <summary>
    /// 解析不出的主机名（#459 审查 S3）也是一种 <c>SocketException</c>，但不是「连接被拒」：<c>SERVER_STATE_UNKNOWN</c>，不写库。
    /// </summary>
    [Fact]
    public async Task AnUnresolvableProbeHostIsNotTakenForStopped()
    {
        await SeedAsync();
        const string address = "http://fieldops-probe.invalid:58007/";

        (int exit, JsonElement output) = await RunAsync(
            null, [.. Arguments(), "--database", DatabasePath, "--probe-server", address]);

        await AssertStateUnknownAndNothingWrittenAsync(exit, output, address);
    }

    /// <summary>服务端没应答：<c>UNAVAILABLE</c>，退出码 1，提示改用 <c>--database</c>。</summary>
    [Fact]
    public async Task AnUnreachableServerIsUnavailable()
    {
        (int exit, JsonElement output) = await RunAsync(
            ("CONTROL_SERVER_FAULT_RECOVERY_CREDENTIAL", "x"), [.. Arguments(), "--server", "http://127.0.0.1:1/"]);

        Assert.Equal((1, "UNAVAILABLE"), (exit, output.GetProperty("outcome").GetString()));
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(_directory))
        {
            return ValueTask.CompletedTask;
        }
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return ValueTask.CompletedTask;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static string[] Arguments() =>
    [
        Verb, "--map", "25", "--station", "202", "--vehicle-key", KeyA, "--operator", "OP-7",
        "--reason", "A 车离线，已拖离关卡", "--site-verification", "SITE-2026-0930-01"
    ];

    private async Task SeedAsync()
    {
        Directory.CreateDirectory(_directory);
        await using ControlServerDbContext context = Open();
        await MigratedDatabaseTemplate.ApplyAsync(context.Database, Token);
        await new StationExclusivityStore(context).TryAcquireAsync(
            new StationExclusivityRequest(25, 202, StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Occupied, null),
            KeyA, "journey:a", At, Token);
    }

    private async Task AssertStateUnknownAndNothingWrittenAsync(int exit, JsonElement output, string address)
    {
        Assert.Equal((1, "SERVER_STATE_UNKNOWN"), (exit, output.GetProperty("outcome").GetString()));
        Assert.Contains(new Uri(address).ToString(), output.GetProperty("detail").GetString(), StringComparison.Ordinal);
        await using ControlServerDbContext read = Open();
        Assert.Single(await read.Set<StationExclusivityRow>().ToArrayAsync(Token));
        Assert.Equal(
            0, await read.Set<AdministratorAuditRecordRow>().CountAsync(row => row.Action == StationExclusivityManualRelease.AuditAction, Token));
    }

    /// <summary>A loopback port that was just bound and released, so nothing listens on it and a connection is refused.</summary>
    private static int FreedPort()
    {
        TcpListener freed = new(IPAddress.Loopback, 0);
        freed.Start();
        int port = ((IPEndPoint)freed.LocalEndpoint).Port;
        freed.Stop();
        return port;
    }

    private ControlServerDbContext Open() => new(
        new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(ControlServerSqlite.ForDatabaseFile(DatabasePath, readOnly: false))
            .Options);

    private async Task<WebApplication> StartServerAsync(string credentialVariable, TimeSpan? answerDelay = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<ControlServerDbContext>(
            options => options.UseSqlite(ControlServerSqlite.ForDatabaseFile(DatabasePath, readOnly: false)));
        builder.Services.AddScoped<IGovernanceAuditWriter>(services => new GovernanceStore(
            services.GetRequiredService<ControlServerDbContext>(),
            new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
            AuditRetentionPolicy.Default));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IRiotVehicleFacts>(new OfflineFacts());
        builder.Services.Configure<VehicleFaultRecoveryOptions>(options =>
        {
            options.Enabled = true;
            options.CredentialEnvironmentVariable = credentialVariable;
        });
        WebApplication app = builder.Build();
        if (answerDelay is { } delay)
        {
            app.Use(async (context, next) => { await Task.Delay(delay); await next(context); });
        }
        app.MapStationExclusivityRelease();
        await app.StartAsync(Token);
        return app;
    }

    private static Task<(int ExitCode, JsonElement Output)> RunAsync(
        (string Name, string? Value)? environment, string[] arguments) =>
        RunWithEnvironmentAsync(environment is { } single ? [single] : [], arguments);

    /// <summary>Runs FieldOps; each variable is set in the child's environment, or removed from it when its value is null.</summary>
    private static async Task<(int ExitCode, JsonElement Output)> RunWithEnvironmentAsync(
        (string Name, string? Value)[] environment, string[] arguments)
    {
        Assert.True(File.Exists(FieldOps), $"FieldOps was not built next to the tests: {FieldOps}");
        ProcessStartInfo start = new(FieldOps)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        foreach ((string Name, string? Value) variable in environment)
        {
            if (variable.Value is null)
            {
                start.Environment.Remove(variable.Name);
            }
            else
            {
                start.Environment[variable.Name] = variable.Value;
            }
        }
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        string output = (await stdout).Trim();
        _ = await stderr;
        if (output.Length == 0)
        {
            return (process.ExitCode, default);
        }
        using JsonDocument document = JsonDocument.Parse(output);
        return (process.ExitCode, document.RootElement.Clone());
    }

    private sealed class OfflineFacts : IRiotVehicleFacts
    {
        public Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken) =>
            Task.FromResult(new RiotVehicleObservation(vehicleKey, false, true, "IDLE", "MAP-25", null, 50, "DISCHARGING", 0, At));

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A station release reads no order.");

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A station release creates no order.");
    }
}
