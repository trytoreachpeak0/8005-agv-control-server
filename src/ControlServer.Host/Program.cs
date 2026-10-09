using System.Net.Http.Headers;
using System.Globalization;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Adapters;
using ControlServer.Infrastructure.Persistence;
using ControlServer.Host.Transport;
using ControlServer.Host.Composition;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.ForeignOrders;
using ControlServer.Host.Runtime.TaskTypeStations;

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
// 这个库有两个进程在写（另一个是 ControlServer.FieldOps），连接串因此只在一处拼：路径展开与等写锁的
// 上限都由 ControlServerSqlite 说了算。
string connectionString = ControlServerSqlite.FromConfigured(configuredConnection);
ControlServerSqlite.EnsureDataSourceDirectory(connectionString);
builder.Services.AddDbContext<ControlServerDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<WireToGateStore>();
builder.Services.AddScoped<IDemandAcceptanceStore>(services => services.GetRequiredService<WireToGateStore>());
builder.Services.AddScoped<IMovementIntentStore>(services => services.GetRequiredService<WireToGateStore>());
builder.Services.AddScoped<DemandIntakeService>();
builder.Services.AddScoped<MovementDispatchService>();
builder.Services.AddScoped<JourneyIntakeCoordinator>();
builder.Services.AddScoped<JourneyRuntimeEngine>();
// control-server#391: the fixed task station sweep's warnings are raised once and cleared once across rounds.
builder.Services.AddSingleton<FixedStationSweepWarnings>();
builder.Services.AddScoped<ForeignRunningOrderSupervisor>();
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
// control-server#299: a person's way out of a vehicle fault. The gate is a singleton because it is the one lock the
// runtime loop and the HTTP request share; see JourneyMutationGate.
builder.Services.AddSingleton<JourneyMutationGate>();
builder.Services.AddSingleton<ControlServer.Host.Runtime.Faults.VehicleFaultResumeFlights>();
builder.Services.AddScoped<VehicleFaultRecoveryService>();
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
builder.Services.AddScoped<ControlServer.Host.Runtime.Recovery.RecoverySessionAdministratorClose>();
builder.Services.AddScoped<SlotConfigurationActivationDispatcher>();
builder.Services.AddSingleton<OnboardPeer>();
builder.Services.AddSingleton<IOnboardPeer>(services => services.GetRequiredService<OnboardPeer>());
builder.Services.AddSingleton<IOnboardConnectionPresence>(services => services.GetRequiredService<OnboardPeer>());
builder.Services.AddHostedService<OnboardTcpServer>();
builder.Services.AddJourneyRuntimeOptions(builder.Configuration)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<JourneyRuntimeOptions>, JourneyRuntimeOptionsValidator>();
builder.Services.AddHostedService<JourneyRuntimeWorker>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddRiotCreateDispatchGate(builder.Configuration);
builder.Services.AddRiotForeignOrderCancelGate(builder.Configuration);
builder.Services.AddRiotAbsentAtObservationCreateExperiment(builder.Configuration);
// Read and checked here, at startup, rather than on the first request that creates a client.
TimeSpan mesIngestTimeout = MesIngestReads.Timeout(builder.Configuration);
builder.Services.AddHttpClient<IMesIngestCatalog, HttpMesIngestCatalog>((services, client) =>
{
    IConfiguration configuration = services.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration["MesIngest:baseUrl"] ?? "http://127.0.0.1:5088");
    // Read under JourneyMutationGate: bounded, and a timeout is a failed read (control-server#334, MesIngestReads).
    client.Timeout = mesIngestTimeout;
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
builder.Services.AddOptions<SlotConfigurationActivationOptions>()
    .Bind(builder.Configuration.GetSection(SlotConfigurationActivationOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<SlotConfigurationActivationOptions>, SlotConfigurationActivationOptionsValidator>();
builder.Services.AddOptions<EmergencyStopReleaseOptions>()
    .Bind(builder.Configuration.GetSection(EmergencyStopReleaseOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<EmergencyStopReleaseOptions>, EmergencyStopReleaseOptionsValidator>();
builder.Services.AddOptions<VehicleFaultRecoveryOptions>()
    .Bind(builder.Configuration.GetSection(VehicleFaultRecoveryOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<VehicleFaultRecoveryOptions>, VehicleFaultRecoveryOptionsValidator>();
builder.Services.AddSingleton<MapStationResolver>();
// 固定站按任务类型取得：规则表加本图生效绑定集（control-server#160）。
builder.Services.AddScoped<IFixedTaskStationResolver, BoundFixedTaskStationResolver>();
builder.Services.AddScoped<TaskTypeStationAccess>();
builder.Services.AddHttpClient<ISublotBoxCountReader, HttpSublotBoxCountReader>((services, client) =>
{
    IConfiguration configuration = services.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration["MesIngest:baseUrl"] ?? "http://127.0.0.1:5088");
    client.Timeout = mesIngestTimeout;
    string? secretVariable = configuration["MesIngest:sharedSecretEnvironmentVariable"];
    string? secret = string.IsNullOrWhiteSpace(secretVariable) ? null : Environment.GetEnvironmentVariable(secretVariable);
    if (!string.IsNullOrWhiteSpace(secret))
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }
});
// 批次 3 的治理机制：版本化快照、两条不可改写审计流，以及吃它们的那几个 store。
builder.Services.AddGovernance(builder.Configuration);
// 批次 6 建表票 control-server#159：任务类型规则、按图绑定集、暂停、目录变化与需求冻结的端口。
builder.Services.AddTaskTypeStations();
builder.Services.AddMultiDemandJourneys();
builder.Services.AddVehiclePurposes();
builder.Services.AddIdleReturn(builder.Configuration);
builder.Services.AddCharging();
builder.Services.AddPlanRevision();

WebApplication app = builder.Build();
app.UseSerilogRequestLogging();

// control-server#535 review M2: what this process really bound, once it has started (options validated).
// The v2 parallel installer reads this event back from the log and compares it with the instance
// definition; reading the definition instead is how M1 (six work types bound where one was written) passed.
Action<Microsoft.Extensions.Logging.ILogger, string[], string[], string, Exception?> logEffectiveConfiguration = LoggerMessage.Define<string[], string[], string>(
    LogLevel.Information,
    new EventId(5350, "EffectiveConfiguration"),
    "EFFECTIVE_CONFIGURATION allowedWorkTypes={AllowedWorkTypes} allowedDispatchZones={AllowedDispatchZones} mesIngestBaseUrl={MesIngestBaseUrl}");
app.Lifetime.ApplicationStarted.Register(() =>
{
    JourneyRuntimeOptions effective = app.Services.GetRequiredService<IOptions<JourneyRuntimeOptions>>().Value;
    logEffectiveConfiguration(app.Logger, effective.AllowedWorkTypes, effective.AllowedDispatchZones,
        app.Configuration["MesIngest:baseUrl"] ?? "http://127.0.0.1:5088", null);
});

// control-server#473：数据库一碰之前先拿与库文件绑定的锁，进程活着就一直不放；另一个进程（另一个服务端实例、正在直接写库的
// FieldOps）占着时等一小会儿，仍拿不到就拒绝启动。包容量导入按设计与运行中的服务端并行（control-server#87），不拿。
using ControlServerDatabaseLock? databaseLock = PackageCapacityImportCommand.IsRequested(args)
    ? null
    : await DatabaseLockStartup.AcquireAsync(
        app.Services,
        ControlServerSqlite.DataSourceOf(connectionString),
        args.Contains("--migrate-only", StringComparer.Ordinal),
        CancellationToken.None);

await EnsureDatabaseAsync(app.Services);

// control-server#388：只迁移建库就退出。多车部署在导入等待点之前起不来，首次部署要先有一个库给 FieldOps 离线导入。
if (args.Contains("--migrate-only", StringComparer.Ordinal))
{
    return;
}

if (PackageCapacityImportCommand.IsRequested(args))
{
    Environment.ExitCode = await PackageCapacityImportCommand.RunAsync(
        args, app.Services, CancellationToken.None);
    return;
}

// control-server#72：当前分区归属版本把 AREA 归进了未允许的调度区时拒绝启动，并列出是哪几条。
await AreaAssignmentDispatchZoneStartupCheck.EnsureAsync(app.Services, CancellationToken.None);
// control-server#159：旅程运行时开着时装载任务类型规则与按图绑定的预置配置，配错拒绝启动并列出全部违规。
await TaskTypeStationStartup.EnsureAsync(app.Services, CancellationToken.None);
// control-server#388：投运车辆数大于 1 而等待点不够每辆车各分一个时拒绝启动（规格 5.4）。在绑定装载之后，固定站不算等待点。
await WaitingPointStartupCheck.EnsureAsync(app.Services, CancellationToken.None);
// control-server#403：生效的充电策略版本（含在途旅程与充电周期冻结的版本）不满足 REQ-0281 的阈值关系、或救命线不低于它的强制充电线时拒绝启动。
// 关系只有 ChargingPolicyRules.ThresholdRelationViolations 一份定义，导入也调它。一版都没有照常启动（逐车不投运，control-server#400）。
await ChargingPolicyStartupCheck.EnsureAsync(app.Services, CancellationToken.None);
// control-server#406 审查 M1：人工清桩的出口（名单加至少一个入口）不可用时告警一次；那时充不上照旧写 ORDER_HANG，不进清桩中。
ControlServer.Host.Runtime.Charging.StationClearanceExit.LogAtStartup(app.Services);

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
// 默认不挂。这个入口发出去的是让车换掉自己仓位 IO 绑定的那条命令，装好就开着等于把它挂在网上。
if (app.Configuration.GetValue<bool>("SlotConfigurationActivation:enabled"))
{
    app.MapSlotConfigurationActivation();
}
app.MapTaskTypeHolds();
// 默认不挂。REQ-0356 的人工确认解除：这个入口会把一台车的急停解开，要现场明确打开才提供。
if (app.Configuration.GetValue<bool>("EmergencyStopRelease:enabled"))
{
    app.MapEmergencyStopRelease();
}
// 默认不挂。control-server#299 的故障人工清除：这个入口会清掉一台车的故障、把它的需求交回改派，要现场明确打开才提供；
// control-server#419 的站点独占人工释放同一把凭据、同一个开关。
app.MapVehicleFaultRecoveryEntriesWhenEnabled();
// 批次9-08（control-server#406）：充电桩的维修暂停（总是挂）、恢复确认与人工清桩（与上面同一个开关、同一把凭据）。
app.MapChargingStationEntries();
app.MapDashboardQueries();
// 防饥饿阈值的标定证据（批次7-09，control-server#214）：只读，JSON 与 CSV。
app.MapStarvationCalibrationReport();

await app.RunAsync();

static async Task EnsureDatabaseAsync(IServiceProvider services)
{
    await using AsyncServiceScope scope = services.CreateAsyncScope();
    ControlServerDbContext dbContext = scope.ServiceProvider.GetRequiredService<ControlServerDbContext>();
    await dbContext.Database.MigrateAsync();
    // control-server#505：停在不可放行阻塞码上的旅程不会自动放行，只有旅程收尾结束它；升级迁移会改出这样的行，有就在启动时告诉现场有几趟。
    await ControlServer.Host.Runtime.UnreleasableBlockReport.LogAsync(
        dbContext,
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("ControlServer.Host.Startup"),
        CancellationToken.None);

    // REQ-0271：保留期是管理员配置，变更本身要留管理员审计。新值在服务起来的这一刻生效，所以在这一刻记。
    GovernanceStore governance = scope.ServiceProvider.GetRequiredService<GovernanceStore>();
    TimeProvider clock = scope.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
    await governance.RecordRetentionPolicyAsync(clock.GetUtcNow(), CancellationToken.None);
}

public partial class Program;
