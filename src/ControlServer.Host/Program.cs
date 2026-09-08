using System.Net.Http.Headers;
using System.Globalization;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Adapters;
using ControlServer.Infrastructure.Persistence;
using ControlServer.Host.Transport;
using ControlServer.Host.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;

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
builder.Services.AddScoped<JourneyIntakeCoordinator>();
builder.Services.AddScoped<JourneyRuntimeEngine>();
builder.Services.AddDispatchAdmission();
builder.Services.AddOptions<RouteGraphOptions>()
    .Bind(builder.Configuration.GetSection(RouteGraphOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<RouteGraphOptions>, RouteGraphOptionsValidator>();
builder.Services.AddScoped<IRouteGraphSource, HttpRouteGraphSource>();
builder.Services.AddScoped<IRouteGraphSnapshotStore, RouteGraphSnapshotStore>();
builder.Services.AddScoped<RouteGraphRefresher>();
builder.Services.AddScoped<RouteGraphAccess>();
// FP-C13: the catalog availability state and the pre-create gate. No Enabled switch -- REQ-0302
// is a hard block, and absence of the two approved values is expressed by not configuring them,
// which blocks rather than disabling the check.
builder.Services.AddOptions<MapStationCatalogOptions>()
    .Bind(builder.Configuration.GetSection(MapStationCatalogOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<MapStationCatalogOptions>, MapStationCatalogOptionsValidator>();
builder.Services.AddSingleton<CatalogAlarmLedger>();
builder.Services.AddScoped<ICatalogAvailabilityStore, CatalogAvailabilityStore>();
builder.Services.AddScoped<CatalogAvailabilityAccess>();
builder.Services.AddScoped<IRiotRouteCostProbe, HttpRiotRouteCostProbe>();
builder.Services.AddScoped<PreCreateGate>();
// FP-C11: the RIoT order command surface and the emergency-stop supervisor. Both are driven --
// ticket 11's fault flow is the caller -- so neither takes a hosted service of its own.
builder.Services.AddOptions<RiotCommandOptions>()
    .Bind(builder.Configuration.GetSection(RiotCommandOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<RiotCommandOptions>, RiotCommandOptionsValidator>();
builder.Services.AddScoped<IRiotOrderCommandAuditStore, RiotOrderCommandAuditStore>();
builder.Services.AddScoped<IVehicleFaultStore, VehicleFaultStore>();
builder.Services.AddScoped<RiotOrderCommandService>();
builder.Services.AddScoped<EmergencyStopSupervisor>();
// FP-C11's other half: REQ-0232's two-level fault model. The motion ledger is a singleton because
// REQ-0247's consecutive samples have to survive between evaluations, and it is deliberately not
// persisted -- a stop proof assembled across a restart would be a claim about a vehicle nobody was
// watching across it.
builder.Services.AddOptions<VehicleFaultOptions>()
    .Bind(builder.Configuration.GetSection(VehicleFaultOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<VehicleFaultOptions>, VehicleFaultOptionsValidator>();
builder.Services.AddSingleton<VehicleMotionLedger>();
builder.Services.AddScoped<VehicleFaultCoordinator>();
// B2 multi-vehicle: the roster is the identity register and is fixed for the life of the process;
// the policy access keeps the three configured tables equal to the roster. The checkpoint ledger is
// a singleton for the reason the motion ledger is -- how long a vehicle has been waiting is a
// statement about a stretch this process actually watched.
builder.Services.AddSingleton<VehicleRoster>();
builder.Services.AddSingleton<CheckpointWaitLedger>();
builder.Services.AddScoped<IVehicleDispatchPolicyStore, VehicleDispatchPolicyStore>();
builder.Services.AddScoped<VehicleDispatchPolicyAccess>();
builder.Services.AddScoped<IPackageCapacityStore, PackageCapacityStore>();
builder.Services.AddScoped<PackageCapacityImportService>();
builder.Services.AddOptions<OnboardTransportOptions>()
    .Bind(builder.Configuration.GetSection(OnboardTransportOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<OnboardTransportOptions>, OnboardTransportOptionsValidator>();
builder.Services.AddScoped<OnboardMessageProcessor>();
builder.Services.AddScoped<OnboardJourneyPublisher>();
builder.Services.AddScoped<OnboardRecoveryCoordinator>();
builder.Services.AddSingleton<OnboardPeer>();
builder.Services.AddSingleton<IOnboardPeer>(services => services.GetRequiredService<OnboardPeer>());
builder.Services.AddHostedService<OnboardTcpServer>();
builder.Services.AddOptions<JourneyRuntimeOptions>()
    .Bind(builder.Configuration.GetSection(JourneyRuntimeOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<JourneyRuntimeOptions>, JourneyRuntimeOptionsValidator>();
builder.Services.AddHostedService<JourneyRuntimeWorker>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddRiotCreateDispatchGate(builder.Configuration);
builder.Services.AddRiotAbsentAtObservationCreateExperiment(builder.Configuration);
builder.Services.AddHttpClient<IMesIngestCatalog, HttpMesIngestCatalog>((services, client) =>
{
    IConfiguration configuration = services.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration["MesIngest:baseUrl"] ?? "http://127.0.0.1:5088");
    string? secretVariable = configuration["MesIngest:sharedSecretEnvironmentVariable"];
    string? secret = string.IsNullOrWhiteSpace(secretVariable) ? null : Environment.GetEnvironmentVariable(secretVariable);
    if (!string.IsNullOrWhiteSpace(secret))
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }
});
builder.Services.AddRiotSdkIntegration(builder.Configuration);
builder.Services.AddOptions<OnboardSafetyProjectionOptions>()
    .Bind(builder.Configuration.GetSection(OnboardSafetyProjectionOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<OnboardSafetyProjectionOptions>, OnboardSafetyProjectionOptionsValidator>();
builder.Services.AddSingleton<MapStationResolver>();
builder.Services.AddHttpClient<ISublotBoxCountReader, HttpSublotBoxCountReader>((services, client) =>
{
    IConfiguration configuration = services.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration["MesIngest:baseUrl"] ?? "http://127.0.0.1:5088");
    string? secretVariable = configuration["MesIngest:sharedSecretEnvironmentVariable"];
    string? secret = string.IsNullOrWhiteSpace(secretVariable) ? null : Environment.GetEnvironmentVariable(secretVariable);
    if (!string.IsNullOrWhiteSpace(secret))
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }
});

WebApplication app = builder.Build();
app.UseSerilogRequestLogging();

await EnsureDatabaseAsync(app.Services);

if (PackageCapacityImportCommand.IsRequested(args))
{
    Environment.ExitCode = await PackageCapacityImportCommand.RunAsync(
        args, app.Services, CancellationToken.None);
    return;
}

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
// REQ-0308's operator surface: one row per Map, deduplicated by reason and kept updated -- never
// one alarm per polling round or per waiting task. The approved values it was judged against are
// reported alongside, because "not fresh" is only meaningful next to the maximum it exceeded, and
// a null pair here is the uncommissioned server that REQ-0302 blocks.
app.MapGet("/api/runtime/catalog-availability", async (
        ControlServerDbContext dbContext, CancellationToken cancellationToken) =>
    await dbContext.MapStationCatalogStates.AsNoTracking()
        .OrderBy(row => row.MapId)
        .Select(row => new
        {
            row.MapId,
            state = row.State.ToString(),
            row.LastCompleteConfirmationAt,
            row.LastAttemptAt,
            row.LastFailureReason,
            row.CatalogRevision,
            row.ApprovedSyncPeriodSeconds,
            row.ApprovedMaxUnconfirmedSeconds,
            row.UpdatedAt
        })
        .ToArrayAsync(cancellationToken));
if (app.Configuration.GetValue<bool>("OnboardSafetyProjection:enabled"))
{
    app.MapOnboardVehicleSafety();
}

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
