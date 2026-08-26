using System.Net.Http.Headers;
using System.Globalization;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Adapters;
using ControlServer.Infrastructure.Persistence;
using ControlServer.Host.Transport;
using Microsoft.EntityFrameworkCore;
using Serilog;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "8005 AGV ControlServer");
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));
builder.WebHost.UseUrls(builder.Configuration["Health:url"] ?? "http://127.0.0.1:58007");

string configuredConnection = builder.Configuration.GetConnectionString("ControlServer")
    ?? "Data Source=%ProgramData%\\8005\\ControlServer\\data\\controlserver.db";
string connectionString = ExpandDataSource(configuredConnection);
builder.Services.AddDbContext<ControlServerDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<WireToGateStore>();
builder.Services.AddScoped<IDemandAcceptanceStore>(services => services.GetRequiredService<WireToGateStore>());
builder.Services.AddScoped<IMovementIntentStore>(services => services.GetRequiredService<WireToGateStore>());
builder.Services.AddScoped<DemandIntakeService>();
builder.Services.AddScoped<MovementDispatchService>();
builder.Services.Configure<OnboardTransportOptions>(builder.Configuration.GetSection(OnboardTransportOptions.SectionName));
builder.Services.AddScoped<OnboardMessageProcessor>();
builder.Services.AddScoped<OnboardJourneyPublisher>();
builder.Services.AddSingleton<OnboardPeer>();
builder.Services.AddSingleton<IOnboardPeer>(services => services.GetRequiredService<OnboardPeer>());
builder.Services.AddHostedService<OnboardTcpServer>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<IMesIngestCatalog, HttpMesIngestCatalog>((services, client) =>
{
    IConfiguration configuration = services.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration["MesIngest:baseUrl"] ?? "http://127.0.0.1:58004");
    string? secretVariable = configuration["MesIngest:sharedSecretEnvironmentVariable"];
    string? secret = string.IsNullOrWhiteSpace(secretVariable) ? null : Environment.GetEnvironmentVariable(secretVariable);
    if (!string.IsNullOrWhiteSpace(secret))
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }
});
builder.Services.AddHttpClient<IRiotMovementGateway, HttpRiotMovementGateway>((services, client) =>
{
    IConfiguration configuration = services.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration["RIoT:baseUrl"] ?? "http://127.0.0.1:58888");
    string? secretVariable = configuration["RIoT:callApiKeyEnvironmentVariable"];
    string? secret = string.IsNullOrWhiteSpace(secretVariable) ? null : Environment.GetEnvironmentVariable(secretVariable);
    if (!string.IsNullOrWhiteSpace(secret))
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }
});

WebApplication app = builder.Build();
app.UseSerilogRequestLogging();

await EnsureDatabaseAsync(app.Services);

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (ControlServerDbContext dbContext, CancellationToken cancellationToken) =>
{
    bool databaseAvailable = await dbContext.Database.CanConnectAsync(cancellationToken);
    bool vehicleReady = databaseAvailable && await dbContext.SessionRecoveries
        .AnyAsync(row => row.Readiness == SessionReadiness.Ready, cancellationToken);
    return vehicleReady
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new
        {
            status = "not-ready",
            reason = databaseAvailable ? "RECOVERY_HANDSHAKE_REQUIRED" : "DATABASE_UNAVAILABLE"
        }, statusCode: 503);
});
app.MapGet("/version", () => Results.Ok(new
{
    protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
    profileId = ProtocolCandidateIdentity.ProfileId,
    protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
    protocolTag = ProtocolCandidateIdentity.Tag,
    protocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
    manifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
    schemaBundleSha256 = ProtocolCandidateIdentity.SchemaBundleSha256,
    vectorsSha256 = ProtocolCandidateIdentity.VectorsSha256,
    approvalStatus = ProtocolCandidateIdentity.ApprovalStatus
}));
app.MapGet("/api/runtime/sessions", async (ControlServerDbContext dbContext, CancellationToken cancellationToken) =>
    await dbContext.SessionRecoveries.AsNoTracking()
        .OrderBy(row => row.AgvId)
        .Select(row => new
        {
            row.AgvId,
            row.SessionGeneration,
            readiness = row.Readiness.ToString(),
            row.ReasonCode,
            row.UpdatedAt
        })
        .ToArrayAsync(cancellationToken));

await app.RunAsync();

static string ExpandDataSource(string connectionString)
{
    const string prefix = "Data Source=";
    if (!connectionString.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return connectionString;
    }
    string path = Environment.ExpandEnvironmentVariables(connectionString[prefix.Length..])
        .Replace('/', Path.DirectorySeparatorChar);
    string? directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }
    return $"{prefix}{path}";
}

static async Task EnsureDatabaseAsync(IServiceProvider services)
{
    await using AsyncServiceScope scope = services.CreateAsyncScope();
    ControlServerDbContext dbContext = scope.ServiceProvider.GetRequiredService<ControlServerDbContext>();
    await dbContext.Database.MigrateAsync();
}

public partial class Program;
