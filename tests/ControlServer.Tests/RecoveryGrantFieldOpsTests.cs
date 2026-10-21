using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.Recovery;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ControlServer.Tests;

/// <summary>
/// control-server#797 FieldOps 的 <c>grant-recovery</c>、<c>revoke-recovery</c>、<c>recovery-grants</c>：只走 <c>--server</c>，不开库。演练给码、
/// 带码下发、列出、收回，每一步的输出与退出码；服务端没开时 <c>UNAVAILABLE</c>。
/// </summary>
public sealed class RecoveryGrantFieldOpsTests : IAsyncDisposable
{
    private const string AgvId = "AGV-8005-02";
    private const string VehicleKey = "KEY-AGV02";

    private static readonly string FieldOps = typeof(RecoveryGrantFieldOpsTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "ControlServer.FieldOps.Path").Value!;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "w2g-recovery-grant-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(_directory, "controlserver.db");

    [Fact]
    public async Task TheVerbsRehearseGrantListAndRevokeThroughTheHost()
    {
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "fieldops-credential");
        try
        {
            await using WebApplication app = await StartServerAsync(variable);
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            string[] grant =
            [
                "grant-recovery", "--server", address, "--credential-env", variable, "--vehicle-key", VehicleKey,
                "--operator", "maintenance-797", "--reason", "卸货结果待恢复、车上有货", "--site-verification", "SITE-797-02",
                "--minutes", "20"
            ];

            (int planExit, JsonElement planned) = await RunAsync(variable, grant);
            Assert.Equal((0, "PLANNED"), (planExit, planned.GetProperty("outcome").GetString()));
            string code = planned.GetProperty("confirmationCode").GetString()!;
            Assert.Equal(["RESUME_AFTER_REPAIR"], planned.GetProperty("plan").GetProperty("actions").EnumerateArray().Select(item => item.GetString()));
            Assert.Null(app.Services.GetRequiredService<RecoveryGrantRegistry>().Current(AgvId));

            (int grantExit, JsonElement granted) = await RunAsync(variable, [.. grant, "--confirm", code]);
            Assert.Equal((0, "GRANTED"), (grantExit, granted.GetProperty("outcome").GetString()));
            Assert.Equal(20, (int)(app.Services.GetRequiredService<RecoveryGrantRegistry>().Current(AgvId)!.ExpiresAt
                - app.Services.GetRequiredService<RecoveryGrantRegistry>().Current(AgvId)!.GrantedAt).TotalMinutes);

            (int listExit, JsonElement listed) = await RunAsync(variable, ["recovery-grants", "--server", address, "--credential-env", variable]);
            Assert.Equal(0, listExit);
            Assert.Equal(AgvId, Assert.Single(listed.GetProperty("grants").EnumerateArray()).GetProperty("agvId").GetString());

            (int revokeExit, JsonElement revoked) = await RunAsync(variable,
            [
                "revoke-recovery", "--server", address, "--credential-env", variable, "--vehicle-key", VehicleKey,
                "--operator", "maintenance-797", "--reason", "处理完"
            ]);
            Assert.Equal((0, "REVOKED"), (revokeExit, revoked.GetProperty("outcome").GetString()));
            Assert.Null(app.Services.GetRequiredService<RecoveryGrantRegistry>().Current(AgvId));

            (int againExit, JsonElement again) = await RunAsync(variable,
            [
                "revoke-recovery", "--server", address, "--credential-env", variable, "--vehicle-key", VehicleKey,
                "--operator", "maintenance-797", "--reason", "再收一次"
            ]);
            Assert.Equal((1, "REJECTED"), (againExit, again.GetProperty("outcome").GetString()));
            Assert.Equal(["RECOVERY_GRANT_NOT_IN_EFFECT"], again.GetProperty("codes").EnumerateArray().Select(item => item.GetString()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task AWrongCodeIsRejectedWithTheNewCodeAndNothingIsGranted()
    {
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "fieldops-credential");
        try
        {
            await using WebApplication app = await StartServerAsync(variable);
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

            (int exit, JsonElement output) = await RunAsync(variable,
            [
                "grant-recovery", "--server", address, "--credential-env", variable, "--vehicle-key", VehicleKey,
                "--operator", "maintenance-797", "--reason", "r", "--site-verification", "s", "--confirm", "000000000000"
            ]);

            Assert.Equal((1, "REJECTED"), (exit, output.GetProperty("outcome").GetString()));
            Assert.Equal(["RECOVERY_GRANT_CONFIRMATION_STALE"], output.GetProperty("codes").EnumerateArray().Select(item => item.GetString()));
            Assert.Matches("^[0-9A-F]{12}$", output.GetProperty("confirmationCode").GetString());
            Assert.Null(app.Services.GetRequiredService<RecoveryGrantRegistry>().Current(AgvId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task WithoutARunningServerTheVerbIsUnavailableAndOpensNoDatabase()
    {
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");

        (int exit, JsonElement output) = await RunAsync(variable,
        [
            "grant-recovery", "--server", "http://127.0.0.1:1/", "--credential-env", variable, "--vehicle-key", VehicleKey,
            "--operator", "o", "--reason", "r", "--site-verification", "s"
        ]);

        Assert.Equal((1, "UNAVAILABLE"), (exit, output.GetProperty("outcome").GetString()));
        Assert.False(File.Exists(DatabasePath));
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A handle still closing; the temp directory is cleaned later.
        }
        return ValueTask.CompletedTask;
    }

    private async Task<WebApplication> StartServerAsync(string credentialVariable)
    {
        Directory.CreateDirectory(_directory);
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
        builder.Services.Configure<JourneyRuntimeOptions>(options =>
        {
            options.AgvId = AgvId;
            options.VehicleKey = VehicleKey;
        });
        builder.Services.AddSingleton<VehicleRoster>();
        builder.Services.AddSingleton<RecoveryGrantRegistry>();
        builder.Services.AddScoped<RecoveryGrantIssuance>();
        builder.Services.Configure<RecoveryGrantOptions>(options => options.Required = true);
        builder.Services.Configure<VehicleFaultRecoveryOptions>(options =>
        {
            options.Enabled = true;
            options.CredentialEnvironmentVariable = credentialVariable;
        });
        WebApplication app = builder.Build();
        await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ControlServerDbContext>().Database.EnsureCreatedAsync(Token);
        }
        app.MapRecoveryGrants();
        await app.StartAsync(Token);
        return app;
    }

    private static async Task<(int ExitCode, JsonElement Output)> RunAsync(string credentialVariable, string[] arguments)
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
        start.Environment[credentialVariable] = "fieldops-credential";
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        string output = (await stdout).Trim();
        _ = await stderr;
        using JsonDocument document = JsonDocument.Parse(output);
        return (process.ExitCode, document.RootElement.Clone());
    }
}
