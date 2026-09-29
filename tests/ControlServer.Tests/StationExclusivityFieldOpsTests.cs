using System.Diagnostics;
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

        (int exit, JsonElement output) = await RunAsync(null, [.. Arguments(), "--database", DatabasePath]);

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
            null, [.. arguments[..index], .. arguments[(index + 2)..], "--database", DatabasePath]);

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
    public async Task AMissingStationIsAUsageError(string option)
    {
        await SeedAsync();
        string[] arguments = [.. Arguments(), "--database", DatabasePath];
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
        await context.Database.MigrateAsync(Token);
        await new StationExclusivityStore(context).TryAcquireAsync(
            new StationExclusivityRequest(25, 202, StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Occupied, null),
            KeyA, "journey:a", At, Token);
    }

    private ControlServerDbContext Open() => new(
        new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(ControlServerSqlite.ForDatabaseFile(DatabasePath, readOnly: false))
            .Options);

    private async Task<WebApplication> StartServerAsync(string credentialVariable)
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
        app.MapStationExclusivityRelease();
        await app.StartAsync(Token);
        return app;
    }

    private static async Task<(int ExitCode, JsonElement Output)> RunAsync(
        (string Name, string? Value)? environment, string[] arguments)
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
        if (environment is { } variable)
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
