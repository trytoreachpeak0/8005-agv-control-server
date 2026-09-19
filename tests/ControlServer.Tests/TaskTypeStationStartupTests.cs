using System.Text.Json;
using ControlServer.Application;
using ControlServer.Host.Composition;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.TaskTypeStationTestData;

namespace ControlServer.Tests;

/// <summary>
/// 启动装载（control-server#159）：旅程运行时开着时读预置配置 → 静态校验 → 同一个事务写规则版本与绑定集版本并指向生效版本。
/// </summary>
public sealed class TaskTypeStationStartupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static JourneyRuntimeOptions Runtime(bool enabled = true) => new()
    {
        Enabled = enabled,
        MapId = 25,
        GateStationId = "关卡",
        GateStationRiotId = 210,
    };

    private static object Preset(
        IReadOnlyList<TaskTypeStationRule>? rules = null,
        int mapId = 25,
        IReadOnlyList<string>? required = null,
        IReadOnlyList<TaskTypeStationBinding>? bindings = null) =>
        new
        {
            TaskTypeStations = new
            {
                rules = (rules ?? SixRules).Select(rule => new { taskType = rule.TaskType, fixedEnd = rule.FixedEnd }),
                mapId,
                requiredTaskTypes = required ?? [TransportTaskTypes.WireToGate],
                bindings = (bindings ?? [GateBinding]).Select(binding => new
                {
                    taskType = binding.TaskType,
                    stationRiotId = binding.StationRiotId,
                    stationName = binding.StationName,
                    siteVerificationRef = binding.SiteVerificationRef
                })
            }
        };

    [Fact]
    public async Task TheFactoryPresetShippedNextToTheHostPassesAgainstTheShippedJourneyRuntimeAndSaysExactlyTheGate()
    {
        // The two files the build copies next to the host: appsettings.json and the preset. What ships must start.
        JourneyRuntimeOptions shipped = new();
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build()
            .GetSection(JourneyRuntimeOptions.SectionName)
            .Bind(shipped);
        shipped.Enabled = true;

        TaskTypeStationPresetFile preset = Assert.IsType<TaskTypeStationPresetFile>(
            TaskTypeStationPreset.Load(AppContext.BaseDirectory, settingsFile: null));
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, TaskTypeStationPreset.FileName), preset.Path);
        TaskTypeStationConfiguration configuration = preset.Configuration;
        Assert.Equal(25, shipped.MapId);
        Assert.Equal(shipped.MapId, configuration.Map.MapId);
        Assert.Equal(
            SixRules.OrderBy(rule => rule.TaskType, StringComparer.Ordinal),
            configuration.Rules.OrderBy(rule => rule.TaskType, StringComparer.Ordinal));
        Assert.Equal([TransportTaskTypes.WireToGate], configuration.Map.RequiredTaskTypes);
        Assert.Equal([GateBinding], configuration.Map.Bindings);

        await using Harness harness = await Harness.CreateAsync(shipped, settingsFile: null);
        TaskTypeStationStartupResult result = Assert.IsType<TaskTypeStationStartupResult>(
            await TaskTypeStationStartup.EnsureAsync(harness.Services, Token));
        Assert.Equal(1, result.Rules.Version.Version);
        Assert.Equal(1, result.Bindings.Version.Version);
    }

    [Fact]
    public async Task AFirstStartWritesRuleAndBindingSetVersionOneAndPointsTheMapAtIt()
    {
        await using Harness harness = await Harness.CreateAsync(Runtime());
        harness.WritePreset(Preset());

        TaskTypeStationStartupResult result = Assert.IsType<TaskTypeStationStartupResult>(
            await TaskTypeStationStartup.EnsureAsync(harness.Services, Token));

        Assert.True(result.Rules.Created);
        Assert.True(result.Bindings.Created);
        Assert.Equal(1, result.Bindings.Version.RuleVersion);
        Assert.StartsWith(TaskTypeStationGovernance.PresetSourcePrefix, result.Rules.Version.Source, StringComparison.Ordinal);
        Assert.Null(result.Bindings.Version.CatalogRevision);
        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();
        ITaskTypeStationBindingStore bindings = scope.ServiceProvider.GetRequiredService<ITaskTypeStationBindingStore>();
        TaskTypeStationBindingSetVersion active = Assert.IsType<TaskTypeStationBindingSetVersion>(
            await bindings.ReadActiveAsync(25, Token));
        Assert.Equal(1, active.Version);
        Assert.Equal([GateBinding], active.Bindings);
        Assert.Equal(TaskTypeStationActivationState.Active, (await bindings.ReadActivePointerAsync(25, Token))!.State);
    }

    [Fact]
    public async Task RestartingWithTheSameContentMakesNoNewVersion()
    {
        await using Harness harness = await Harness.CreateAsync(Runtime());
        harness.WritePreset(Preset());
        await TaskTypeStationStartup.EnsureAsync(harness.Services, Token);

        // The same content written again, in another order: a restart, not a change.
        harness.WritePreset(Preset(rules: [.. SixRules.Reverse()]));
        TaskTypeStationStartupResult again = Assert.IsType<TaskTypeStationStartupResult>(
            await TaskTypeStationStartup.EnsureAsync(harness.Services, Token));

        Assert.False(again.Rules.Created);
        Assert.False(again.Bindings.Created);
        Assert.Equal(1, again.Rules.Version.Version);
        Assert.Equal(1, again.Bindings.Version.Version);
        Assert.Equal(1, await harness.CountAsync<TaskTypeStationRuleVersionRow>());
        Assert.Equal(1, await harness.CountAsync<TaskTypeStationBindingSetVersionRow>());
    }

    [Fact]
    public async Task ChangingOneFieldMakesANewVersionMovesThePointerAndLeavesTheOldVersionReadableAsItWas()
    {
        await using Harness harness = await Harness.CreateAsync(Runtime());
        harness.WritePreset(Preset());
        TaskTypeStationStartupResult first = (await TaskTypeStationStartup.EnsureAsync(harness.Services, Token))!;

        TaskTypeStationBinding reverified = GateBinding with { SiteVerificationRef = "SITE-CHECK-2026-09-19" };
        harness.WritePreset(Preset(bindings: [reverified]));
        TaskTypeStationStartupResult second = Assert.IsType<TaskTypeStationStartupResult>(
            await TaskTypeStationStartup.EnsureAsync(harness.Services, Token));

        Assert.False(second.Rules.Created);
        Assert.True(second.Bindings.Created);
        Assert.Equal(2, second.Bindings.Version.Version);
        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();
        ITaskTypeStationBindingStore bindings = scope.ServiceProvider.GetRequiredService<ITaskTypeStationBindingStore>();
        Assert.Equal(2, (await bindings.ReadActiveAsync(25, Token))!.Version);
        Assert.Equal([reverified], (await bindings.ReadActiveAsync(25, Token))!.Bindings);
        TaskTypeStationBindingSetVersion old = Assert.IsType<TaskTypeStationBindingSetVersion>(
            await bindings.ReadVersionAsync(25, 1, Token));
        Assert.Equal(first.Bindings.Version.SnapshotId, old.SnapshotId);
        Assert.Equal(first.Bindings.Version.ContentSha256, old.ContentSha256);
        Assert.Equal([GateBinding], old.Bindings);
    }

    [Fact]
    public async Task AMisconfiguredPresetRefusesStartNamingEveryViolationAndWritesNothing()
    {
        await using Harness harness = await Harness.CreateAsync(Runtime());
        harness.WritePreset(Preset(
            required: [TransportTaskTypes.WireToGate, TransportTaskTypes.WireToOptical],
            bindings: [GateBinding, StagingBinding with { StationRiotId = 210, StationName = "关卡" }]));

        TaskTypeStationConfigurationException refused = await Assert.ThrowsAsync<TaskTypeStationConfigurationException>(
            () => TaskTypeStationStartup.EnsureAsync(harness.Services, Token));

        Assert.Equal(
            [TaskTypeStationReasonCodes.BindingRequiredMissing, TaskTypeStationReasonCodes.StationReused],
            refused.Violations.Select(violation => violation.ReasonCode).Order(StringComparer.Ordinal));
        Assert.Contains(TaskTypeStationReasonCodes.StationReused, refused.Message, StringComparison.Ordinal);
        Assert.Contains(TaskTypeStationReasonCodes.BindingRequiredMissing, refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await harness.CountAsync<TaskTypeStationRuleVersionRow>());
        Assert.Equal(0, await harness.CountAsync<TaskTypeStationBindingSetVersionRow>());
    }

    [Fact]
    public async Task RulesAndBindingsLandTogetherOrNotAtAll()
    {
        await using Harness harness = await Harness.CreateAsync(
            Runtime(),
            tweak: services => services.AddScoped<ITaskTypeStationBindingStore, FailingBindingStore>());
        harness.WritePreset(Preset());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TaskTypeStationStartup.EnsureAsync(harness.Services, Token));

        // The rule version was written before the binding set failed; it must not survive on its own.
        Assert.Equal(0, await harness.CountAsync<TaskTypeStationRuleVersionRow>());
        Assert.Equal(0, await harness.CountAsync<TaskTypeStationRuleRow>());
        Assert.Equal(0, await harness.CountAsync<GovernedConfigurationSnapshotRow>());
        Assert.Equal(0, await harness.CountAsync<BusinessAuditRecordRow>());
    }

    [Fact]
    public async Task WithTheJourneyRuntimeOffNothingIsLoadedOrCheckedEvenWhenThePresetIsWrong()
    {
        await using Harness harness = await Harness.CreateAsync(Runtime(enabled: false));
        harness.WritePreset(Preset(bindings: [GateBinding, StagingBinding with { StationRiotId = 210 }]));

        Assert.Null(await TaskTypeStationStartup.EnsureAsync(harness.Services, Token));
        Assert.Equal(0, await harness.CountAsync<TaskTypeStationRuleVersionRow>());
    }

    [Fact]
    public async Task APresetFileWithoutTheSectionIsNotLoadedAndDoesNotRefuseStart()
    {
        // Then control-server#160 treats every task type as not enabled.
        await using Harness harness = await Harness.CreateAsync(Runtime());
        harness.WritePreset(new { SomethingElse = new { value = 1 } });

        Assert.Null(await TaskTypeStationStartup.EnsureAsync(harness.Services, Token));
        Assert.Equal(0, await harness.CountAsync<TaskTypeStationRuleVersionRow>());
    }

    [Fact]
    public async Task ANamedPresetFileThatDoesNotExistRefusesStartRatherThanSilentlyLoadingNothing()
    {
        await using Harness harness = await Harness.CreateAsync(Runtime());

        FileNotFoundException missing = await Assert.ThrowsAsync<FileNotFoundException>(
            () => TaskTypeStationStartup.EnsureAsync(harness.Services, Token));
        Assert.Contains(TaskTypeStationPreset.FileName, missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFieldOfTheWrongTypeRefusesStartNamingTheField()
    {
        await using Harness harness = await Harness.CreateAsync(Runtime());
        harness.WriteRaw("""
            { "TaskTypeStations": { "rules": [], "mapId": 25, "requiredTaskTypes": [],
              "bindings": [ { "taskType": "WIRE_TO_GATE", "stationRiotId": "two-ten", "stationName": "关卡", "siteVerificationRef": "x" } ] } }
            """);

        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(
            () => TaskTypeStationStartup.EnsureAsync(harness.Services, Token));
        Assert.Contains("stationRiotId", refused.Message, StringComparison.Ordinal);
    }

    private sealed class FailingBindingStore : ITaskTypeStationBindingStore
    {
        public Task<TaskTypeStationBindingSetVersion?> ReadActiveAsync(int mapId, CancellationToken cancellationToken) =>
            Task.FromResult<TaskTypeStationBindingSetVersion?>(null);

        public Task<TaskTypeStationBindingSetVersion?> ReadLatestAsync(int mapId, CancellationToken cancellationToken) =>
            Task.FromResult<TaskTypeStationBindingSetVersion?>(null);

        public Task<TaskTypeStationBindingSetVersion?> ReadVersionAsync(
            int mapId, long version, CancellationToken cancellationToken) =>
            Task.FromResult<TaskTypeStationBindingSetVersion?>(null);

        public Task<TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion>> WriteVersionAsync(
            int mapId, long ruleVersion, IReadOnlyList<string> requiredTaskTypes,
            IReadOnlyList<TaskTypeStationBinding> bindings, long? catalogRevision, string source,
            DateTimeOffset loadedAt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Injected failure writing the binding set.");

        public Task<TaskTypeStationActivePointer?> ReadActivePointerAsync(int mapId, CancellationToken cancellationToken) =>
            Task.FromResult<TaskTypeStationActivePointer?>(null);

        public Task<TaskTypeStationActivePointer> SetActiveAsync(
            int mapId, long version, DateTimeOffset at, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Injected failure setting the pointer.");
    }

    /// <summary>
    /// A migrated in-memory database, the governance and batch 6 modules as the host registers them, and a preset file
    /// in a directory of its own named by <c>TaskTypeStations:settingsFile</c>.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _directory;

        private Harness(SqliteConnection connection, ServiceProvider services, string directory, string presetPath)
        {
            _connection = connection;
            Services = services;
            _directory = directory;
            PresetPath = presetPath;
        }

        public ServiceProvider Services { get; }
        public string PresetPath { get; }

        public static async Task<Harness> CreateAsync(
            JourneyRuntimeOptions runtime,
            Action<IServiceCollection>? tweak = null,
            string? settingsFile = "")
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            string directory = Directory.CreateTempSubdirectory("cs159-preset-").FullName;
            string presetPath = Path.Combine(directory, TaskTypeStationPreset.FileName);
            Dictionary<string, string?> settings = [];
            if (settingsFile is not null)
            {
                settings[TaskTypeStationPreset.SettingsFileKey] = presetPath;
            }
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

            ServiceCollection services = new();
            services.AddLogging();
            services.AddSingleton(configuration);
            services.AddSingleton(Options.Create(runtime));
            services.AddDbContext<ControlServerDbContext>(options => options.UseSqlite(connection));
            services.AddGovernance(configuration);
            services.AddTaskTypeStations();
            tweak?.Invoke(services);
            ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ControlServerDbContext>().Database
                    .MigrateAsync(TestContext.Current.CancellationToken);
            }
            return new Harness(connection, provider, directory, presetPath);
        }

        public void WritePreset(object preset) => WriteRaw(JsonSerializer.Serialize(preset));

        public void WriteRaw(string json) => File.WriteAllText(PresetPath, json);

        public async Task<int> CountAsync<TRow>()
            where TRow : class
        {
            await using AsyncServiceScope scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ControlServerDbContext>()
                .Set<TRow>()
                .CountAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await _connection.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
