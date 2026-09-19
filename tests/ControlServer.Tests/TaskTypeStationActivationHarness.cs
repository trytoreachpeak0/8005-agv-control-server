using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ControlServer.Tests;

/// <summary>
/// 绑定集激活的测试布置（control-server#161）：一个真 SQLite 文件，迁移建表，并布置成现场刚起过一次服务端的样子——
/// 规则第 1 版（六条）、Map 25 绑定集第 1 版（<c>WIRE_TO_GATE → 210／关卡</c>）已生效，服务端 30 秒前完整确认过一次
/// <see cref="Catalog"/> 那份目录。每条测试只改它要证的那一处。
/// </summary>
/// <remarks>
/// 用文件而不是内存库：激活要证「进程中断后重启看到什么」，那就要能关掉一个上下文、再开一个新的读同一个库；
/// FieldOps 的进程入口测试也要一个文件路径交给子进程。
/// </remarks>
internal sealed class TaskTypeStationActivationHarness : IAsyncDisposable
{
    public const int MapId = 25;

    public static readonly DateTimeOffset Now = TaskTypeStationTestData.Now;

    public static readonly TaskTypeStationChangeRequest Request = new("现场换绑定：测试", "FieldEngineer");

    public static readonly RiotMapStation[] CatalogStations =
    [
        new(11, "C15-13"),
        new(210, "关卡"),
        new(305, "派工待送取货"),
        new(320, "光学检测"),
        new(330, "氮气柜"),
    ];

    private readonly string _directory;
    private readonly List<ControlServerDbContext> _contexts = [];

    private TaskTypeStationActivationHarness(string directory)
    {
        _directory = directory;
        DatabasePath = Path.Combine(directory, "controlserver.db");
    }

    public string DatabasePath { get; }

    /// <summary>服务端最近一次完整确认过的那份目录，也是测试默认带给激活的那份。</summary>
    public static RiotMapStationCatalogSnapshot Catalog { get; } =
        TaskTypeStationCatalogEvidence.Supplied(MapId, CatalogStations, Now);

    public static TaskTypeStationBinding Gate => TaskTypeStationTestData.GateBinding;

    public static TaskTypeStationBinding Staging => TaskTypeStationTestData.StagingBinding;

    public static TaskTypeStationBinding Optical { get; } =
        new(TransportTaskTypes.WireToOptical, 320, "光学检测", "SITE-CHECK-OPTICAL");

    public static async Task<TaskTypeStationActivationHarness> CreateAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cs161-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TaskTypeStationActivationHarness harness = new(directory);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        ControlServerDbContext context = harness.NewContext();
        await context.Database.MigrateAsync(cancellationToken);
        Stack stack = StackOver(context);
        TaskTypeStationVersionWrite<TaskTypeStationRuleVersion> rules = await stack.Rules.WriteVersionAsync(
            TaskTypeStationTestData.SixRules, TaskTypeStationTestData.Source, Now.AddHours(-1), cancellationToken);
        TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion> first = await stack.Bindings.WriteVersionAsync(
            MapId, rules.Version.Version, [TransportTaskTypes.WireToGate], [Gate], null,
            TaskTypeStationTestData.Source, Now.AddHours(-1), cancellationToken);
        await stack.Bindings.SetActiveAsync(MapId, first.Version.Version, Now.AddHours(-1), cancellationToken);
        await harness.ConfirmCatalogAsync(Catalog, Now.AddSeconds(-30));
        return harness;
    }

    /// <summary>一个新的上下文，就像一个新进程开同一个库。</summary>
    public ControlServerDbContext NewContext(params IInterceptor[] interceptors)
    {
        ControlServerDbContext context = new(new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(ControlServerSqlite.ForDatabaseFile(DatabasePath, readOnly: false))
            .AddInterceptors(interceptors)
            .Options);
        _contexts.Add(context);
        return context;
    }

    /// <summary>一个上下文上的全套：存储、审计与激活服务。</summary>
    public static Stack StackOver(
        ControlServerDbContext context,
        Func<ITaskTypeStationActivationStore, ITaskTypeStationActivationStore>? wrap = null,
        Func<IGovernanceAuditWriter, IGovernanceAuditWriter>? wrapAudit = null)
    {
        GovernanceStore governance = new(
            context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);
        IGovernanceAuditWriter audit = wrapAudit?.Invoke(governance) ?? governance;
        GovernedConfigurationPublisher publisher = new(governance, governance);
        TaskTypeStationRuleStore rules = new(context, publisher);
        TaskTypeStationBindingStore bindings = new(context, publisher);
        ITaskTypeStationActivationStore activations = new TaskTypeStationActivationStore(context, bindings, audit);
        if (wrap is not null)
        {
            activations = wrap(activations);
        }
        TaskTypeStationActivationService service = new(
            rules, bindings, activations, new CatalogAvailabilityStore(context), audit);
        return new Stack(context, governance, rules, bindings, new TaskTypeStationHoldStore(context), activations, service);
    }

    public Stack Default() => StackOver(NewContext());

    /// <summary>服务端在 <paramref name="at"/> 完整确认了这份目录（批准参数：60 秒同步、300 秒上限）。</summary>
    public async Task ConfirmCatalogAsync(RiotMapStationCatalogSnapshot catalog, DateTimeOffset at)
    {
        await using ControlServerDbContext context = NewContext();
        await new CatalogAvailabilityStore(context).RecordCompleteConfirmationAsync(
            catalog.MapId, TaskTypeStationCatalogEvidence.RevisionOf(catalog.ContentSha256), 60, 300, at,
            TestContext.Current.CancellationToken);
    }

    public static TaskTypeStationCandidate Candidate(params TaskTypeStationBinding[] bindings) =>
        new(MapId, 1, [.. bindings.Select(binding => binding.TaskType)], bindings);

    /// <summary>
    /// 一条已受理、冻结在 Map 25 某一版绑定集上的在途需求，连同它的冻结站点、旅程行与一条已建单的订单行。
    /// </summary>
    public async Task AddInFlightDemandAsync(string demandId, string taskType, long frozenBindingSetVersion, int stationRiotId)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using ControlServerDbContext context = NewContext();
        context.Set<AcceptedDemandRow>().Add(new AcceptedDemandRow
        {
            DemandId = demandId,
            SeriesId = demandId,
            TransportDemandKey = $"key:{demandId}",
            WorkType = taskType,
            Sublot = demandId,
            Generation = 1,
            DemandRevision = 1,
            HistoryEpoch = "epoch",
            CatalogRevision = 1,
            CreatedAt = Now.AddHours(-1),
            ValueObservedAt = Now.AddHours(-1),
            ValuePollTraceId = "trace",
            ValueProjectionCommitId = "commit",
            LiveMesFieldsJson = JsonSerializer.Serialize(new LiveMesFieldSet("C15-13", "EQP-1", "STEP-1", null, "PKG-1")),
            AcceptedAt = Now.AddHours(-1),
            Status = DemandExecutionStatus.Accepted
        });
        context.FrozenDemandStations.Add(new FrozenDemandStationRow
        {
            DemandId = demandId,
            Role = FrozenStationRole.Dropoff,
            TransportDemandKey = $"key:{demandId}",
            MapId = MapId,
            StationId = stationRiotId,
            StationName = "frozen",
            CatalogRevision = 1,
            FrozenAt = Now.AddHours(-1)
        });
        context.JourneyRuntimes.Add(Journey(demandId, JourneyRuntimeStage.AwaitingGateArrival));
        context.OrderIntents.Add(new OrderIntentRow
        {
            MovementLegId = $"gate-leg-{demandId}",
            DemandId = demandId,
            UpperId = $"UPPER-GATE-{demandId}",
            Purpose = "GATE",
            TargetStationId = "GATE",
            VehicleKey = "VEHICLE-TEST",
            MapId = MapId,
            DestinationStationId = stationRiotId,
            AgvLifecycleGeneration = 1,
            DispatchGeneration = 1,
            CreatedAt = Now.AddHours(-1),
            Status = "CREATED",
            OrderId = $"ORDER-{demandId}"
        });
        await context.SaveChangesAsync(cancellationToken);
        await new DemandTaskTypeStationFreezeStore(context).FreezeAsync(
            demandId, 1, MapId, frozenBindingSetVersion, Now.AddHours(-1), cancellationToken);
    }

    /// <summary>需求冻结、冻结站点、旅程、订单与需求本身各表的全部行，逐列转成文本，用来比对「逐行相同」。</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> SnapshotInFlightRowsAsync()
    {
        Dictionary<string, IReadOnlyList<string>> tables = new(StringComparer.Ordinal);
        await using SqliteConnection connection = new(ControlServerSqlite.ForDatabaseFile(DatabasePath, readOnly: true));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        foreach (string table in InFlightTables)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY rowid";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            List<string> rows = [];
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount)
                    .Select(index => $"{reader.GetName(index)}={(reader.IsDBNull(index) ? "<null>" : reader.GetValue(index))}")));
            }
            tables[table] = rows;
        }
        return tables;
    }

    public static readonly string[] InFlightTables =
        ["AcceptedDemands", "ConfigurationConsumerBindings", "FrozenDemandStations", "JourneyRuntimes", "OrderIntents"];

    /// <summary>除审计之外全部业务表的行数，用来证「只多了审计」。</summary>
    public async Task<IReadOnlyDictionary<string, long>> CountRowsAsync()
    {
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        await using SqliteConnection connection = new(ControlServerSqlite.ForDatabaseFile(DatabasePath, readOnly: true));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        List<string> tables = [];
        await using (SqliteCommand list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            await using SqliteDataReader reader = await list.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }
        foreach (string table in tables)
        {
            await using SqliteCommand count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
            counts[table] = (long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }
        return counts;
    }

    /// <summary>生效指针表的全部行，整行转成文本。</summary>
    public async Task<string> PointerRowAsync()
    {
        await using SqliteConnection connection = new(ControlServerSqlite.ForDatabaseFile(DatabasePath, readOnly: true));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT MapId, ActiveVersion, State, PendingVersion FROM TaskTypeStationActiveBindingSets ORDER BY MapId";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        List<string> rows = [];
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add($"{reader.GetValue(0)}|{(reader.IsDBNull(1) ? "<null>" : reader.GetValue(1))}|{reader.GetValue(2)}|{(reader.IsDBNull(3) ? "<null>" : reader.GetValue(3))}");
        }
        return string.Join('\n', rows);
    }

    /// <summary>该图全部业务审计（不论成败），按记录时间排序。</summary>
    public async Task<IReadOnlyList<BusinessAuditRecordRow>> AuditAsync()
    {
        await using ControlServerDbContext context = NewContext();
        // In write order: one command stamps all its records with the same instant.
        BusinessAuditRecordRow[] rows = await context.Set<BusinessAuditRecordRow>()
            .FromSqlRaw("SELECT * FROM BusinessAuditRecords ORDER BY rowid")
            .AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken);
        string objectId = TaskTypeStationGovernance.BindingSetObjectId(MapId);
        return
        [
            .. rows.Where(row => row.ObjectKind == GovernedObjectKind.PublicStationBinding
                && string.Equals(row.ObjectId, objectId, StringComparison.Ordinal))
        ];
    }

    /// <summary>绕过上下文直接改库：扮演另一个写者，或扮演坏掉的数据。</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using SqliteConnection connection = new(ControlServerSqlite.ForDatabaseFile(DatabasePath, readOnly: false));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>该图全部暂停（含已解除）。</summary>
    public async Task<IReadOnlyList<TaskTypeStationHoldRow>> HoldsAsync()
    {
        await using ControlServerDbContext context = NewContext();
        TaskTypeStationHoldRow[] rows = await context.Set<TaskTypeStationHoldRow>().AsNoTracking()
            .Where(row => row.MapId == MapId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        return [.. rows.OrderBy(row => row.RaisedAt).ThenBy(row => row.TaskType, StringComparer.Ordinal)];
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ControlServerDbContext context in _contexts)
        {
            await context.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A child process may still hold the file for a moment; the temp directory is not evidence.
        }
    }

    private static JourneyRuntimeRow Journey(string demandId, JourneyRuntimeStage stage) => new()
    {
        JourneyId = JourneyIdentity.ForAnchorDemand(demandId),
        DemandId = demandId,
        Stage = stage,
        AgvId = "AGV-TEST",
        VehicleKey = "VEHICLE-TEST",
        AgvLifecycleGeneration = 1,
        MapId = MapId,
        MapIdentity = "MAP-25",
        DispatchZone = "MAP-25-WIRE_TO_GATE",
        RouteEvidenceId = "ROUTE-01",
        PickupStationId = "PICKUP",
        PickupStationRiotId = 11,
        GateStationId = "GATE",
        GateStationRiotId = 210,
        ExpectedBasketCount = 1,
        TargetSlotsJson = "[1]",
        OperationSessionId = $"session-{demandId}",
        PickupMovementLegId = $"pickup-leg-{demandId}",
        PickupUpperId = $"UPPER-PICKUP-{demandId}",
        GateMovementLegId = $"gate-leg-{demandId}",
        GateUpperId = $"UPPER-GATE-{demandId}",
        DispatchGeneration = 1,
        VehicleBusinessRevision = 1,
        WorklistRevision = 1,
        PlanRevision = 1,
        VehicleBusinessMessageId = $"vb-{demandId}",
        WorklistMessageId = $"wl-{demandId}",
        PlanMessageId = $"plan-{demandId}",
        SublotRequestMessageId = $"sublot-{demandId}",
        LoadCommandMessageId = $"load-{demandId}",
        LoadSlotOperationAttemptId = $"load-attempt-{demandId}",
        PreDepartureSafetyCheckMessageId = $"safety-msg-{demandId}",
        PreDepartureSafetyCheckId = $"safety-{demandId}",
        GateVehicleBusinessMessageId = $"gate-vb-{demandId}",
        GateWorklistMessageId = $"gate-wl-{demandId}",
        GatePlanMessageId = $"gate-plan-{demandId}",
        UnloadCommandMessageId = $"unload-{demandId}",
        UnloadSlotOperationAttemptId = $"unload-attempt-{demandId}",
        CreatedAt = Now.AddHours(-1),
        UpdatedAt = Now.AddHours(-1)
    };

    internal sealed record Stack(
        ControlServerDbContext Context,
        GovernanceStore Governance,
        TaskTypeStationRuleStore Rules,
        TaskTypeStationBindingStore Bindings,
        TaskTypeStationHoldStore Holds,
        ITaskTypeStationActivationStore Activations,
        TaskTypeStationActivationService Service)
    {
        public Task<TaskTypeStationActivationResult> ActivateAsync(
            TaskTypeStationCandidate candidate,
            bool dryRun = false,
            RiotMapStationCatalogSnapshot? catalog = null,
            DateTimeOffset? at = null) =>
            Service.ActivateAsync(
                candidate, catalog ?? TaskTypeStationActivationHarness.Catalog, Request, dryRun, at ?? Now,
                TestContext.Current.CancellationToken);
    }
}

/// <summary>Forwards everything to a real store; a test double overrides the one step it breaks.</summary>
internal abstract class DelegatingActivationStore(ITaskTypeStationActivationStore inner) : ITaskTypeStationActivationStore
{
    public virtual Task<TaskTypeStationActivationAttempt> BeginAsync(
        TaskTypeStationActivationStart start,
        Func<TaskTypeStationActivationAttempt, GovernanceAuditEntry> startedAudit,
        CancellationToken cancellationToken) => inner.BeginAsync(start, startedAudit, cancellationToken);

    public virtual Task CompleteAsync(
        TaskTypeStationActivationAttempt attempt, DateTimeOffset at, CancellationToken cancellationToken) =>
        inner.CompleteAsync(attempt, at, cancellationToken);

    public virtual Task<TaskTypeStationActiveReadBack> ReadBackAsync(int mapId, CancellationToken cancellationToken) =>
        inner.ReadBackAsync(mapId, cancellationToken);

    public virtual Task<TaskTypeStationActivationAttempt> MarkUnknownAsync(
        TaskTypeStationActivationAttempt attempt, DateTimeOffset at, CancellationToken cancellationToken) =>
        inner.MarkUnknownAsync(attempt, at, cancellationToken);

    public virtual Task<TaskTypeStationActivationAttempt?> ReadOpenAttemptAsync(int mapId, CancellationToken cancellationToken) =>
        inner.ReadOpenAttemptAsync(mapId, cancellationToken);

    public virtual Task<TaskTypeStationReconciliation> ReconcileAsync(
        int mapId,
        Func<TaskTypeStationActivationAttempt?, TaskTypeStationActiveReadBack, TaskTypeStationReconciliationConclusion> decide,
        Func<TaskTypeStationActivationAttempt?, TaskTypeStationActiveReadBack, TaskTypeStationReconciliationConclusion, IReadOnlyList<string>, TaskTypeStationPointerAfterWrite, GovernanceAuditEntry> audit,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        inner.ReconcileAsync(mapId, decide, audit, at, cancellationToken);

    public virtual Task<TaskTypeStationManualClose> CloseManuallyAsync(
        int mapId,
        Func<TaskTypeStationActivationAttempt?, TaskTypeStationActiveReadBack, bool> isContradictory,
        Func<TaskTypeStationActivationAttempt?, TaskTypeStationActiveReadBack, bool, IReadOnlyList<string>, GovernanceAuditEntry> audit,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        inner.CloseManuallyAsync(mapId, isContradictory, audit, at, cancellationToken);

    public virtual Task<(IReadOnlyList<TaskTypeStationHold> Released, string AuditRecordId)> ReleaseManualAndCatalogHoldsAsync(
        int mapId,
        string taskType,
        string releasedByPrefix,
        Func<IReadOnlyList<TaskTypeStationHold>, GovernanceAuditEntry> audit,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        inner.ReleaseManualAndCatalogHoldsAsync(mapId, taskType, releasedByPrefix, audit, at, cancellationToken);

    public virtual Task<IReadOnlyList<TaskTypeStationInFlightDemand>> ListInFlightDemandsAsync(
        int mapId, CancellationToken cancellationToken) =>
        inner.ListInFlightDemandsAsync(mapId, cancellationToken);
}
