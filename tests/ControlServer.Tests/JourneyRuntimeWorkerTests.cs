using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

public sealed class JourneyRuntimeWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 1, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] UnknownReasonCodes = ["IO_FACT_UNKNOWN"];

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    [Trait("IntegrationSlice", "W2G-IS-04")]
    public async Task ProductionRuntimeResumesOneJourneyThroughTrustedArrivalsAndAtomicCompletion()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot demand = fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            createdAt: Now.AddMinutes(-10));
        fixture.Catalog.Set(demand);
        fixture.BoxCounts.Set("SUBLOT-001", 7);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, runtime.Stage);
        Assert.Equal(2, runtime.ExpectedBasketCount);
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_PICKUP"));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));

        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", fixture.Options.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = fixture.Options.PickupStationRiotId + 1 };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        Assert.DoesNotContain("SublotEntryRequested", await fixture.OutboxTypesAsync());

        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = fixture.Options.PickupStationRiotId };
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.Equal(
            ["CurrentStopWorklistSnapshot", "SublotEntryRequested", "UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"],
            (await fixture.OutboxTypesAsync()).Order(StringComparer.Ordinal));

        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "SublotSubmitted",
            new
            {
                demandId = runtime.DemandId,
                operationSessionId = runtime.OperationSessionId,
                stationId = runtime.PickupStationId,
                worklistRevision = runtime.WorklistRevision,
                sublot = "SUBLOT-001",
                entryMethod = "SCANNER",
                @operator = new
                {
                    operatorId = "OP-001",
                    verificationMethod = "BADGE",
                    verifiedAt = Now
                }
            });
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, runtime.Stage);
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        int[]? loadSlots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson);
        Assert.NotNull(loadSlots);
        Assert.Equal([1, 2], loadSlots);
        AdmissionDecisionSnapshotRow admission = await fixture.Context.AdmissionDecisionSnapshots
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(admission.Allowed);
        Assert.Equal(1, admission.AdmissionPolicyVersion);
        Assert.Equal(fixture.Options.PickupStationId, admission.StationId);
        Assert.Equal("WIRE_TO_GATE", admission.TaskType);

        await fixture.ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = runtime.PreDepartureSafetyCheckId,
                outcome = "SAFE",
                observedAt = Now,
                safetyStateVersion = 7,
                validUntil = Now.AddMinutes(1),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                }
            }, runtime.PreDepartureSafetyCheckMessageId);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, runtime.Stage);
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_GATE"));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));

        fixture.Riot.SetSuccessfulArrival("TO_GATE", fixture.Options.GateStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = fixture.Options.GateStationRiotId };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, runtime.Stage);

        StationOperationRow unload = await fixture.OperationAsync(SlotOperationType.Unload);
        await fixture.ApplySafeResultAsync(unload, SlotOperationType.Unload, SlotBusinessState.Empty);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        Assert.Equal(DemandExecutionStatus.Succeeded, (await fixture.DemandRowAsync()).Status);
        Assert.NotNull((await fixture.LeaseAsync()).ReleasedAt);
        Assert.Equal(1, await fixture.Context.TransportDemandCompletions.CountAsync(TestContext.Current.CancellationToken));

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(2, await fixture.Context.StationOperations.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.Context.TransportDemandCompletions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task HardAdmissionFiltersBeforeStableBacklogOrderingAndRemoteSideEffects()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot wrongWorkType = fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "IGNORED", Now.AddMinutes(-30)) with
        {
            WorkType = "OTHER",
            TransportDemandKey = "IGNORED|OTHER"
        };
        AcceptedDemandSnapshot laterId = fixture.Demand(
            "10000000-0000-4000-8000-000000000003", "SUBLOT-003", Now.AddMinutes(-20));
        AcceptedDemandSnapshot stableWinner = fixture.Demand(
            "10000000-0000-4000-8000-000000000002", "SUBLOT-002", Now.AddMinutes(-20));
        fixture.Catalog.Set(wrongWorkType, laterId, stableWinner);
        fixture.BoxCounts.Set("SUBLOT-002", 4);
        fixture.BoxCounts.Set("SUBLOT-003", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(stableWinner.DemandId, (await fixture.RuntimeAsync()).DemandId);
        Assert.Equal(1, await fixture.Context.AcceptedDemands.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Riot.TotalCreateCount);
        JourneyBacklogRow[] backlog = await fixture.Context.JourneyBacklog
            .OrderBy(row => row.DemandId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, backlog.Length);
        Assert.Equal("OUT_OF_SCOPE_WORK_TYPE", backlog[0].ReasonCode);
        Assert.Equal("ACCEPTED", backlog[1].ReasonCode);
        Assert.Equal("ELIGIBLE", backlog[2].ReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task UnknownOrStaleDynamicFactsFailClosedWithoutAcceptingOrDispatching()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = null };

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.ProtocolOutbox.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("BATTERY_FACT_UNKNOWN", backlog.ReasonCode);
        Assert.Null(backlog.AcceptedAt);
    }

    [Theory]
    [InlineData("route-missing", "STATION_ROUTE_MAPPING_NOT_UNIQUE")]
    [InlineData("zone-not-admitted", "DISPATCH_ZONE_VEHICLE_ADMISSION_MISSING")]
    [InlineData("vehicle-not-idle", "RIOT_VEHICLE_NOT_IDLE")]
    [InlineData("vehicle-map-mismatch", "RIOT_VEHICLE_MAP_MISMATCH")]
    [InlineData("battery-low", "BATTERY_POLICY_NOT_SATISFIED")]
    [InlineData("riot-order-occupied", "RIOT_VEHICLE_ORDER_OCCUPIED")]
    [InlineData("box-count-missing", "SUBLOT_BOX_COUNT_UNAVAILABLE")]
    [InlineData("package-capacity-missing", "PACKAGE_CAPACITY_NOT_UNIQUE")]
    [InlineData("onboard-stale", "ONBOARD_FACTS_NOT_READY")]
    [InlineData("onboard-unsafe", "ONBOARD_DEPARTURE_UNSAFE")]
    [InlineData("station-task-not-admitted", "TASK_TYPE_NOT_ALLOWED_AT_STATION")]
    [InlineData("slot-capacity", "SLOT_CAPACITY_TEMPORARILY_UNAVAILABLE")]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task EveryAdmissionGateFailsClosedBeforeAcceptance(
        string scenario,
        string expectedReason)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot demand = fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10));
        fixture.Catalog.Set(demand);
        fixture.BoxCounts.Set("SUBLOT-001", 8);
        switch (scenario)
        {
            case "route-missing":
                fixture.Options.Routes = [];
                break;
            case "zone-not-admitted":
                fixture.Options.AllowedDispatchZones = [];
                break;
            case "vehicle-not-idle":
                fixture.Riot.Vehicle = fixture.Riot.Vehicle with { ProcState = "EXECUTING" };
                break;
            case "vehicle-map-mismatch":
                fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentMap = "OTHER-MAP" };
                break;
            case "battery-low":
                fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 10 };
                break;
            case "riot-order-occupied":
                fixture.Riot.Vehicle = fixture.Riot.Vehicle with { LockStatus = 1, OrderTaskId = "ORDER-ACTIVE" };
                break;
            case "box-count-missing":
                fixture.BoxCounts.Remove("SUBLOT-001");
                break;
            case "package-capacity-missing":
                fixture.Options.PackageCapacityRules = [];
                break;
            case "onboard-stale":
                await fixture.StaleOnboardFactsAsync();
                break;
            case "onboard-unsafe":
                await fixture.SetOnboardUnknownAsync();
                break;
            case "station-task-not-admitted":
                fixture.Options.StationTaskTypeAdmissions = [];
                break;
            case "slot-capacity":
                await fixture.KeepOnlyOneAvailableSlotAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        Assert.Equal(expectedReason, (await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken)).ReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task RestartKeepsOriginalVehicleLeaseAndStablePickupIdentity()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow after = await fixture.RuntimeAsync();

        Assert.Equal(before.DemandId, after.DemandId);
        Assert.Equal(before.PickupMovementLegId, after.PickupMovementLegId);
        Assert.Equal(before.PickupUpperId, after.PickupUpperId);
        Assert.Equal(1, await fixture.Context.VehicleDispatchLeases.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.Context.OrderIntents.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task RestartReconcilesUnknownPickupCreateWithoutSecondOrderOrIdentityChange()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.Riot.LoseNextCreateResponse = true;

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        Assert.Equal("RESULT_UNKNOWN", (await fixture.Context.OrderIntents.SingleAsync(
            TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));

        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow after = await fixture.RuntimeAsync();
        OrderIntentRow reconciled = await fixture.Context.OrderIntents.SingleAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(before.DemandId, after.DemandId);
        Assert.Equal(before.PickupUpperId, after.PickupUpperId);
        Assert.Equal("CONFIRMED", reconciled.Status);
        Assert.Equal("ORDER-TO_PICKUP", reconciled.OrderId);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Single(await fixture.Context.VehicleDispatchLeases.ToArrayAsync(
            TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task AcceptedDemandWithoutPersistedRuntimeFailsClosedInsteadOfBeingAdopted()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot demand = fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10));
        await new WireToGateStore(fixture.Context).AcceptWithOrderIntentAsync(
            demand,
            new OrderIntent(
                "LEGACY-PICKUP-LEG",
                demand.DemandId,
                "W2G-LEGACY-PICKUP-1",
                "TO_PICKUP",
                fixture.Options.PickupStationId,
                Now,
                fixture.Options.VehicleKey,
                fixture.Options.MapId,
                fixture.Options.PickupStationRiotId,
                fixture.Options.AgvLifecycleGeneration,
                fixture.Options.DispatchGeneration),
            TestContext.Current.CancellationToken);

        BusinessIdentityConflictException error = await Assert.ThrowsAsync<BusinessIdentityConflictException>(
            () => fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken));

        Assert.Contains("has no production journey runtime", error.Message, StringComparison.Ordinal);
        Assert.Empty(await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Single(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Single(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task RestartReenvelopesPendingServerMessagesForTheCurrentSessionWithoutChangingSemantics()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", fixture.Options.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = fixture.Options.PickupStationRiotId };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Dictionary<string, string> originalPayloads = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .ToDictionaryAsync(
                row => row.MessageId,
                row => JsonDocument.Parse(row.PayloadJson).RootElement.GetProperty("payload").GetRawText(),
                TestContext.Current.CancellationToken);
        int originalCount = originalPayloads.Count;

        await fixture.AdvanceSessionAsync(2);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        ProtocolOutboxRow[] replayed = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(originalCount, replayed.Length);
        foreach (ProtocolOutboxRow row in replayed)
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            Assert.Equal(2, document.RootElement.GetProperty("sessionGeneration").GetInt64());
            Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse(originalPayloads[row.MessageId]),
                JsonNode.Parse(document.RootElement.GetProperty("payload").GetRawText())));
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task UnknownPreDepartureSafetyCannotCreateTheGateOrder()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = runtime.PreDepartureSafetyCheckId,
                outcome = "SAFE",
                observedAt = Now,
                safetyStateVersion = 7,
                validUntil = Now.AddMinutes(1),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = true,
                    reasonCodes = UnknownReasonCodes
                }
            }, runtime.PreDepartureSafetyCheckMessageId);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        Assert.Equal("PRE_DEPARTURE_SAFETY_NOT_VALID", runtime.BlockReasonCode);
        Assert.DoesNotContain(
            await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken),
            row => row.Purpose == "TO_GATE");
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private RuntimeFixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            RecordingCatalog catalog,
            RecordingBoxCounts boxCounts,
            RecordingRiot riot,
            RecordingPeer peer,
            JourneyRuntimeOptions options,
            FixedTimeProvider clock)
        {
            Connection = connection;
            Context = context;
            Catalog = catalog;
            BoxCounts = boxCounts;
            Riot = riot;
            Peer = peer;
            Options = options;
            Clock = clock;
            Engine = CreateEngine();
        }

        private SqliteConnection Connection { get; }
        public ControlServerDbContext Context { get; }
        public RecordingCatalog Catalog { get; }
        public RecordingBoxCounts BoxCounts { get; }
        public RecordingRiot Riot { get; }
        public RecordingPeer Peer { get; }
        public JourneyRuntimeOptions Options { get; }
        public FixedTimeProvider Clock { get; }
        public JourneyRuntimeEngine Engine { get; private set; }

        public static async Task<RuntimeFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> dbOptions =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            ControlServerDbContext context = new(dbOptions);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            JourneyRuntimeOptions options = ValidOptions();
            FixedTimeProvider clock = new(Now);
            RecordingCatalog catalog = new();
            RecordingBoxCounts boxCounts = new();
            RecordingRiot riot = new(options, clock);
            RecordingPeer peer = new();
            RuntimeFixture fixture = new(
                connection, context, catalog, boxCounts, riot, peer, options, clock);
            await fixture.SeedRecoveredPeerAsync();
            return fixture;
        }

        public AcceptedDemandSnapshot Demand(string demandId, string sublot, DateTimeOffset createdAt) => new(
            demandId,
            $"{sublot}|WIRE_TO_GATE",
            7,
            "11111111-1111-4111-8111-111111111111",
            21,
            Clock.GetUtcNow(),
            $"SERIES-{demandId}",
            "WIRE_TO_GATE",
            sublot,
            1,
            createdAt,
            createdAt.AddMinutes(1),
            $"TRACE-{demandId}",
            $"COMMIT-{demandId}",
            new LiveMesFieldSet("AREA-01", "EQP-01", "STEP-01", createdAt, "PKG-01"));

        public async Task RecreateEngineAsync()
        {
            Context.ChangeTracker.Clear();
            Engine = CreateEngine();
            await Task.CompletedTask;
        }

        public async Task AdvanceSessionAsync(long generation)
        {
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            session.SessionGeneration = generation;
            session.CapabilityRevision = 1;
            session.SafetyRevision = 7;
            session.Readiness = SessionReadiness.Ready;
            session.ReasonCode = "READY";
            session.UpdatedAt = Clock.GetUtcNow();
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await AddCapabilityAndSafetyAsync(generation);
        }

        public async Task<JourneyRuntimeRow> AdvanceToDepartureSafetyAsync()
        {
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            Riot.SetSuccessfulArrival("TO_PICKUP", Options.PickupStationRiotId);
            Riot.Vehicle = Riot.Vehicle with { CurrentStationId = Options.PickupStationRiotId };
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            JourneyRuntimeRow runtime = await RuntimeAsync();
            await AddInboxAsync(
                Guid.NewGuid().ToString("D"),
                "SublotSubmitted",
                new
                {
                    demandId = runtime.DemandId,
                    operationSessionId = runtime.OperationSessionId,
                    stationId = runtime.PickupStationId,
                    worklistRevision = runtime.WorklistRevision,
                    sublot = "SUBLOT-001",
                    entryMethod = "SCANNER",
                    @operator = new
                    {
                        operatorId = "OP-001",
                        verificationMethod = "BADGE",
                        verifiedAt = Clock.GetUtcNow()
                    }
                });
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            StationOperationRow load = await OperationAsync(SlotOperationType.Load);
            await ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            return await RuntimeAsync();
        }

        public async Task StaleOnboardFactsAsync()
        {
            ProtocolInboxRow[] rows = await Context.ProtocolInbox
                .Where(row => row.MessageType == "CapabilitySnapshot" || row.MessageType == "SafetyStateSnapshot")
                .ToArrayAsync(TestContext.Current.CancellationToken);
            foreach (ProtocolInboxRow row in rows)
            {
                JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
                root["payload"]!["observedAt"] = Clock.GetUtcNow().AddHours(-1);
                row.RequestJson = root.ToJsonString(SerializerOptions);
            }
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task KeepOnlyOneAvailableSlotAsync()
        {
            ProtocolInboxRow[] rows = await Context.ProtocolInbox
                .Where(row => row.MessageType == "CapabilitySnapshot" || row.MessageType == "SafetyStateSnapshot")
                .ToArrayAsync(TestContext.Current.CancellationToken);
            foreach (ProtocolInboxRow row in rows)
            {
                JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
                JsonArray slots = root["payload"]!["slotStates"]!.AsArray();
                for (int index = 1; index < slots.Count; index++)
                {
                    slots[index]!["administrativeAvailability"] = "DISABLED";
                }
                row.RequestJson = root.ToJsonString(SerializerOptions);
            }
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task SetOnboardUnknownAsync()
        {
            ProtocolInboxRow row = await Context.ProtocolInbox.SingleAsync(
                item => item.MessageType == "SafetyStateSnapshot",
                TestContext.Current.CancellationToken);
            JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
            root["payload"]!["safety"]!["departureSafe"] = false;
            root["payload"]!["safety"]!["unknownPresent"] = true;
            row.RequestJson = root.ToJsonString(SerializerOptions);
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public Task<JourneyRuntimeRow> RuntimeAsync() => Context.JourneyRuntimes
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        public Task<AcceptedDemandRow> DemandRowAsync() => Context.AcceptedDemands
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        public Task<VehicleDispatchLeaseRow> LeaseAsync() => Context.VehicleDispatchLeases
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        public async Task<string> IntentStatusAsync(string purpose) => (await Context.OrderIntents
            .AsNoTracking()
            .SingleAsync(row => row.Purpose == purpose, TestContext.Current.CancellationToken)).Status;

        public Task<StationOperationRow> OperationAsync(SlotOperationType type) => Context.StationOperations
            .AsNoTracking()
            .SingleAsync(row => row.OperationType == type, TestContext.Current.CancellationToken);

        public async Task<string[]> OutboxTypesAsync() => await Context.ProtocolOutbox
            .AsNoTracking()
            .OrderBy(row => row.MessageType)
            .Select(row => row.MessageType)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        public async Task AddInboxAsync(
            string messageId,
            string messageType,
            object payload,
            string? correlationId = null)
        {
            JourneyRuntimeRow runtime = await RuntimeAsync();
            string json = JsonSerializer.Serialize(new
            {
                protocolVersion = 1,
                profileId = "WIRE_TO_GATE_MVP",
                protocolReleaseVersion = "0.1.1",
                protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                messageType,
                messageId,
                correlationId,
                agvId = Options.AgvId,
                sessionGeneration = 1,
                sentAt = Now,
                payload
            }, SerializerOptions);
            Context.ProtocolInbox.Add(new ProtocolInboxRow
            {
                MessageId = messageId,
                MessageType = messageType,
                RequestJson = json,
                ContentHash = new string('a', 64),
                FirstResponseJson = "{}",
                ReceivedAt = Now
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task ApplySafeResultAsync(
            StationOperationRow operation,
            SlotOperationType type,
            SlotBusinessState state)
        {
            int[] slots = JsonSerializer.Deserialize<int[]>(operation.TargetSlotsJson) ?? [];
            string resultId = Guid.NewGuid().ToString("D");
            await new WireToGateStore(Context).ApplyOperationResultAsync(
                new StationOperationResult(
                    resultId,
                    operation.SlotOperationAttemptId,
                    operation.DemandId,
                    type,
                    "COMPLETED",
                    slots.Select(slot => new SlotPhysicalEvidence(slot, state, true, true)).ToArray(),
                    true,
                    Now,
                    new string(type == SlotOperationType.Load ? 'b' : 'c', 64),
                    new string(type == SlotOperationType.Load ? 'd' : 'e', 64)),
                Options.AgvId,
                0,
                TestContext.Current.CancellationToken);
        }

        private JourneyRuntimeEngine CreateEngine()
        {
            WireToGateStore store = new(Context);
            JourneyIntakeCoordinator intake = new(
                new DemandIntakeService(Catalog, store),
                new MovementDispatchService(store, Riot));
            OnboardJourneyPublisher publisher = new(store, Peer, Clock);
            return new JourneyRuntimeEngine(
                Context,
                Catalog,
                BoxCounts,
                Riot,
                intake,
                new MovementDispatchService(store, Riot),
                store,
                publisher,
                Microsoft.Extensions.Options.Options.Create(Options),
                Clock,
                NullLogger<JourneyRuntimeEngine>.Instance);
        }

        private async Task SeedRecoveredPeerAsync()
        {
            Context.SessionRecoveries.Add(new SessionRecoveryRow
            {
                AgvId = Options.AgvId,
                SessionGeneration = 1,
                ProtocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
                ManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                ProfileId = ProtocolCandidateIdentity.ProfileId,
                ProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                CapabilityRevision = 1,
                CapabilityHash = new string('1', 64),
                SafetyRevision = 7,
                SafetyHash = new string('2', 64),
                DepartureSafe = true,
                RecoveryReportId = Guid.NewGuid().ToString("D"),
                Readiness = SessionReadiness.Ready,
                ReasonCode = "READY",
                UpdatedAt = Now
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await AddCapabilityAndSafetyAsync(1);
        }

        private async Task AddCapabilityAndSafetyAsync(long generation)
        {
            await AddRawInboxAsync("CapabilitySnapshot", new
            {
                capabilityVersion = 1,
                observedAt = Now,
                slotModelVersion = "SLOT-MODEL-1",
                activeSlotConfigurationVersion = "SLOT-CONFIG-1",
                slotStates = Enumerable.Range(1, 8).Select(slot => new
                {
                    slotNo = slot,
                    operability = "OPERABLE",
                    administrativeAvailability = "ENABLED",
                    physicalState = "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                    reasonCodes = Array.Empty<string>()
                }),
                supportsBatchUnlock = true,
                onboardJournalFormatVersion = 1
            }, generation);
            await AddRawInboxAsync("SafetyStateSnapshot", new
            {
                safetyStateVersion = 7,
                observedAt = Now,
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                },
                slotStates = Enumerable.Range(1, 8).Select(slot => new
                {
                    slotNo = slot,
                    operability = "OPERABLE",
                    administrativeAvailability = "ENABLED",
                    physicalState = "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                    reasonCodes = Array.Empty<string>()
                })
            }, generation);
        }

        private async Task AddRawInboxAsync(string messageType, object payload, long generation)
        {
            string messageId = Guid.NewGuid().ToString("D");
            string json = JsonSerializer.Serialize(new
            {
                messageType,
                messageId,
                agvId = Options.AgvId,
                sessionGeneration = generation,
                sentAt = Now,
                payload
            }, SerializerOptions);
            Context.ProtocolInbox.Add(new ProtocolInboxRow
            {
                MessageId = messageId,
                MessageType = messageType,
                RequestJson = json,
                ContentHash = new string('f', 64),
                FirstResponseJson = "{}",
                ReceivedAt = Now
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        private static JourneyRuntimeOptions ValidOptions() => new()
        {
            Enabled = true,
            PollInterval = TimeSpan.FromSeconds(1),
            AgvId = "AGV-8005-01",
            VehicleKey = "VEHICLE-KEY-01",
            AgvLifecycleGeneration = 4,
            MapId = 29,
            MapIdentity = "MAP-29",
            PickupStationId = "PICKUP-01",
            PickupStationRiotId = 12,
            GateStationId = "GATE-01",
            GateStationRiotId = 20,
            DispatchGeneration = 1,
            MinimumBatteryPercent = 40,
            MaximumEvidenceAge = TimeSpan.FromMinutes(2),
            SublotBoxCountPath = "/api/v2/sublot-box-count",
            AllowedWorkTypes = ["WIRE_TO_GATE"],
            AllowedDispatchZones = ["ZONE-01"],
            AdmissionPolicyVersion = 1,
            AdmissionPolicyDeploymentId = "TEST-DEPLOYMENT-1",
            StationTaskTypeAdmissions =
            [
                new StationTaskTypeAdmissionOptions
                {
                    StationId = "PICKUP-01",
                    TaskType = "WIRE_TO_GATE"
                }
            ],
            Routes =
            [
                new JourneyRouteOptions
                {
                    Area = "AREA-01",
                    Eqp = "EQP-01",
                    DispatchZone = "ZONE-01",
                    PickupStationId = "PICKUP-01",
                    PickupStationRiotId = 12,
                    RouteEvidenceId = "ROUTE-29-12-20"
                }
            ],
            PackageCapacityRules =
            [
                new PackageCapacityRuleOptions
                {
                    Pattern = "PKG-01",
                    MatchType = "exact",
                    MaxBoxesPerBasket = 4
                }
            ]
        };

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class RecordingCatalog : IMesIngestCatalog
    {
        private AcceptedDemandSnapshot[] _items = [];

        public void Set(params AcceptedDemandSnapshot[] items) => _items = items;

        public Task<DemandCatalogSnapshot> ReadCatalogAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            string epoch = _items.FirstOrDefault()?.HistoryEpoch ?? "11111111-1111-4111-8111-111111111111";
            return Task.FromResult(new DemandCatalogSnapshot(epoch, 21, _items));
        }

        public Task<AcceptedDemandSnapshot?> ReadCurrentAsync(string demandId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(_items.SingleOrDefault(item => item.DemandId == demandId));
        }
    }

    private sealed class RecordingBoxCounts : ISublotBoxCountReader
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public void Set(string sublot, int count) => _counts[sublot] = count;
        public void Remove(string sublot) => _counts.Remove(sublot);

        public Task<int?> ReadMaxBoxCountAsync(string sublot, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(_counts.TryGetValue(sublot, out int count) ? (int?)count : null);
        }
    }

    private sealed class RecordingRiot : IRiotMovementGateway, IRiotVehicleFacts
    {
        private readonly JourneyRuntimeOptions _options;
        private readonly FixedTimeProvider _clock;
        private readonly Dictionary<string, int> _creates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RiotOrderObservation> _orders = new(StringComparer.Ordinal);

        public RecordingRiot(JourneyRuntimeOptions options, FixedTimeProvider clock)
        {
            _options = options;
            _clock = clock;
            Vehicle = new RiotVehicleObservation(
                options.VehicleKey,
                Connected: true,
                Enabled: true,
                ProcState: "IDLE",
                CurrentMap: options.MapIdentity,
                CurrentStationId: 1,
                BatteryPercent: 80,
                BatteryState: "NO_CHARGE",
                Speed: 0,
                ObservedAt: clock.GetUtcNow(),
                LockStatus: 0,
                OrderTaskId: null);
        }

        public RiotVehicleObservation Vehicle { get; set; }
        public bool LoseNextCreateResponse { get; set; }
        public int TotalCreateCount => _creates.Values.Sum();

        public int CreateCount(string purpose) => _creates.GetValueOrDefault(purpose);

        public void SetSuccessfulArrival(string purpose, int stationId)
        {
            string upperId = UpperId(purpose);
            _orders[upperId] = new RiotOrderObservation(
                upperId,
                RiotOrderObservationKind.Terminal,
                $"ORDER-{purpose}",
                OrderState: 5,
                VehicleKey: _options.VehicleKey,
                MapId: _options.MapId,
                DestinationStationId: stationId);
        }

        public Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(Vehicle with { VehicleKey = vehicleKey, ObservedAt = _clock.GetUtcNow() });
        }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(_orders.TryGetValue(upperId, out RiotOrderObservation? order)
                ? order
                : new RiotOrderObservation(upperId, RiotOrderObservationKind.NotFound, null));
        }

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _creates[intent.Purpose] = CreateCount(intent.Purpose) + 1;
            RiotOrderObservation active = new(
                intent.UpperId,
                RiotOrderObservationKind.Active,
                $"ORDER-{intent.Purpose}",
                OrderState: 3,
                VehicleKey: intent.VehicleKey,
                MapId: intent.MapId,
                DestinationStationId: intent.DestinationStationId);
            _orders[intent.UpperId] = active;
            if (LoseNextCreateResponse)
            {
                LoseNextCreateResponse = false;
                return Task.FromResult(new RiotOrderObservation(
                    intent.UpperId, RiotOrderObservationKind.Unknown, null));
            }
            return Task.FromResult(active);
        }

        private static string UpperId(string purpose) => purpose switch
        {
            "TO_PICKUP" => "W2G-10000000-0000-4000-8000-000000000001-PICKUP-1",
            "TO_GATE" => "W2G-10000000-0000-4000-8000-000000000001-GATE-1",
            _ => throw new ArgumentOutOfRangeException(nameof(purpose))
        };
    }

    private sealed class RecordingPeer : IOnboardPeer
    {
        public List<byte[]> Lines { get; } = [];

        public Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Lines.Add(ndjsonLine.ToArray());
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
