using ControlServer.Host.Composition;
using ControlServer.Host.Runtime;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.ConfigureControlServerHost();

builder.Services.AddControlServerCore();
builder.Services.AddControlServerPersistence(builder.Configuration);
builder.Services.AddJourneyRuntime(builder.Configuration);
builder.Services.AddOnboardTransport(builder.Configuration);
builder.Services.AddMesIngestIntegration();
builder.Services.AddRiotIntegration(builder.Configuration);
builder.Services.AddOnboardSafetyProjection(builder.Configuration);
builder.Services.AddGovernance(builder.Configuration);

WebApplication app = builder.Build();
app.UseControlServerHost();

await app.Services.EnsureDatabaseAsync();

if (PackageCapacityImportCommand.IsRequested(args))
{
    Environment.ExitCode = await PackageCapacityImportCommand.RunAsync(
        args, app.Services, CancellationToken.None);
    return;
}

app.MapControlServerDiagnostics();
app.MapRuntimeQueries();
app.MapOnboardSafetyProjection();

await app.RunAsync();

public partial class Program;
