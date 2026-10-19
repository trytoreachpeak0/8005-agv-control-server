using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Withdrawal;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// FieldOps 的两个动词（control-server#771）：<c>plan-journey-withdrawal</c> 只读地演练，<c>withdraw-journey</c> 在服务端停着时对库撤下。
/// 每条命令一个 JSON 对象，退出码 0／1／2。库是 agv03 那一形（<see cref="JourneyWithdrawalTests"/>），导成文件给子进程开。
/// </summary>
/// <remarks>
/// 直接写库之前的两道闸与 <c>release-station-exclusivity</c> 同一套（control-server#419、#459、#473）：先探服务端，有应答就拒、探不出结论也拒，
/// 再拿服务进程持有的那把库锁，拿不到就拒。演练开只读库，不探、不拿锁，服务端在跑时也能先看一眼。
/// </remarks>
public sealed class JourneyWithdrawalFieldOpsTests : IAsyncDisposable
{
    private const string PlanVerb = "plan-journey-withdrawal";
    private const string WithdrawVerb = "withdraw-journey";

    /// <summary>A server address nothing answers on: the probe before a direct database write finds the server stopped.</summary>
    private const string NobodyListens = "http://127.0.0.1:1/";

    private static readonly string FieldOps = typeof(JourneyWithdrawalFieldOpsTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "ControlServer.FieldOps.Path").Value!;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "w2g-withdraw-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(_directory, "controlserver.db");

    [Fact]
    public async Task ThePlanVerbIsReadOnlyAndGivesTheConfirmationCode()
    {
        string[] arguments = await SeedAsync();

        (int exit, JsonElement output) = await RunAsync([PlanVerb, .. arguments, "--database", DatabasePath]);

        Assert.Equal((0, "PLANNED"), (exit, output.GetProperty("outcome").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(output.GetProperty("confirmationCode").GetString()));
        Assert.Equal(2, output.GetProperty("plan").GetProperty("demands").GetArrayLength());
        await AssertBlockedAndUnauditedAsync();
    }

    [Fact]
    public async Task WithTheServerStoppedTheWithdrawVerbWithdrawsAgainstTheDatabase()
    {
        string[] arguments = await SeedAsync();
        (_, JsonElement planned) = await RunAsync([PlanVerb, .. arguments, "--database", DatabasePath]);
        string code = planned.GetProperty("confirmationCode").GetString()!;

        (int exit, JsonElement output) = await RunAsync(
            [WithdrawVerb, .. arguments, "--confirm", code, "--database", DatabasePath, "--probe-server", NobodyListens]);

        Assert.Equal((0, "WITHDRAWN", "database"), (exit, output.GetProperty("outcome").GetString(), output.GetProperty("via").GetString()));
        await using ControlServerDbContext read = Open();
        Assert.Equal(
            JourneyRuntimeStage.Completed,
            (await read.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.BlockReasonCode == JourneyWithdrawal.WithdrawnReason, Token)).Stage);
        Assert.Equal(
            GovernanceActionOutcome.Succeeded,
            (await read.Set<AdministratorAuditRecordRow>().SingleAsync(row => row.Action == JourneyWithdrawal.AuditAction, Token)).Outcome);
    }

    /// <summary>没给确认码不是用法错误：先演练、再按，是这道入口防误按的一半。<c>REJECTED</c>、退出码 1，一行不动，审计留下。</summary>
    [Fact]
    public async Task AWithdrawalWithoutTheConfirmationCodeIsARefusal()
    {
        string[] arguments = await SeedAsync();

        (int exit, JsonElement output) = await RunAsync(
            [WithdrawVerb, .. arguments, "--database", DatabasePath, "--probe-server", NobodyListens]);

        Assert.Equal((1, "REJECTED"), (exit, output.GetProperty("outcome").GetString()));
        Assert.Equal(
            [JourneyWithdrawal.ConfirmationRequired],
            output.GetProperty("codes").EnumerateArray().Select(item => item.GetString()));
        await using ControlServerDbContext read = Open();
        Assert.Equal(
            JourneyRuntimeStage.Blocked,
            (await read.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.BlockReasonCode == "LOAD_RESULT_REQUIRES_RECOVERY", Token)).Stage);
        Assert.Equal(1, await read.Set<AdministratorAuditRecordRow>().CountAsync(row => row.Action == JourneyWithdrawal.AuditAction, Token));
    }

    [Fact]
    public async Task AWithdrawalWithoutAServerProbeIsAUsageError()
    {
        string[] arguments = await SeedAsync();

        (int exit, _, string error) = await RunWithErrorAsync([WithdrawVerb, .. arguments, "--confirm", "X", "--database", DatabasePath]);

        Assert.Equal(2, exit);
        Assert.Contains("--probe-server", error, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown command", error, StringComparison.Ordinal);
        await AssertBlockedAndUnauditedAsync();
    }

    /// <summary>服务端其实在跑，却给了 <c>--database</c>：探到应答就拒，<c>SERVER_RUNNING</c>、退出码 1，一行不写、不写审计。</summary>
    [Fact]
    public async Task WithTheServerRunningADirectWithdrawalIsRefused()
    {
        string[] arguments = await SeedAsync();
        await using WebApplication app = await StartAnsweringServerAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        (int exit, JsonElement output) = await RunAsync(
            [WithdrawVerb, .. arguments, "--confirm", "X", "--database", DatabasePath, "--probe-server", address]);
        await app.StopAsync(Token);

        Assert.Equal((1, "SERVER_RUNNING"), (exit, output.GetProperty("outcome").GetString()));
        await AssertBlockedAndUnauditedAsync();
    }

    /// <summary>探的那一刻服务端停着，库锁却被别人拿着（服务端刚好起来）：<c>DATABASE_IN_USE</c>，一行不写。</summary>
    [Fact]
    public async Task WithTheDatabaseLockHeldADirectWithdrawalIsRefused()
    {
        string[] arguments = await SeedAsync();
        using ControlServerDatabaseLock held = ControlServerDatabaseLock.TryAcquire(DatabasePath)!;

        (int exit, JsonElement output) = await RunAsync(
            [WithdrawVerb, .. arguments, "--confirm", "X", "--database", DatabasePath, "--probe-server", NobodyListens]);

        Assert.Equal((1, "DATABASE_IN_USE"), (exit, output.GetProperty("outcome").GetString()));
        await AssertBlockedAndUnauditedAsync();
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>agv03 那一形写进文件，返回点名它的参数（不含库与探测）。</summary>
    private async Task<string[]> SeedAsync()
    {
        Directory.CreateDirectory(_directory);
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await JourneyWithdrawalTests.BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        JourneyWithdrawalRequest request = await JourneyWithdrawalTests.RequestAsync(fixture, blocked);
        JourneyWithdrawalDispatchGateTests.Export(fixture, DatabasePath);
        return
        [
            "--journey", request.JourneyId!, "--vehicle-key", request.VehicleKey!, "--operator", request.OperatorId!,
            "--reason", request.Reason!, "--site-verification", request.SiteVerification!,
            "--riot-verification", request.RiotVerification!, "--slot-facts", request.SlotFacts!,
            "--cargo-location", request.CargoLocation!
        ];
    }

    private async Task AssertBlockedAndUnauditedAsync()
    {
        await using ControlServerDbContext read = Open();
        Assert.Empty(await read.JourneyRuntimes.AsNoTracking()
            .Where(row => row.BlockReasonCode == JourneyWithdrawal.WithdrawnReason).ToArrayAsync(Token));
        Assert.Equal(0, await read.Set<AdministratorAuditRecordRow>().CountAsync(row => row.Action == JourneyWithdrawal.AuditAction, Token));
    }

    private ControlServerDbContext Open() => new(
        new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(ControlServerSqlite.ForDatabaseFile(DatabasePath, readOnly: false))
            .Options);

    /// <summary>A loopback HTTP server that answers anything with 404: to the probe, a running server.</summary>
    private static async Task<WebApplication> StartAnsweringServerAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        await app.StartAsync(Token);
        return app;
    }

    private static async Task<(int ExitCode, JsonElement Output)> RunAsync(string[] arguments)
    {
        (int exit, JsonElement output, _) = await RunWithErrorAsync(arguments);
        return (exit, output);
    }

    private static async Task<(int ExitCode, JsonElement Output, string Error)> RunWithErrorAsync(string[] arguments)
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
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        string output = (await stdout).Trim();
        string error = await stderr;
        if (output.Length == 0)
        {
            return (process.ExitCode, default, error);
        }
        using JsonDocument document = JsonDocument.Parse(output);
        return (process.ExitCode, document.RootElement.Clone(), error);
    }
}
