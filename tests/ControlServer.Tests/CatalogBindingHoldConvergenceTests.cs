using System.Globalization;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.TaskTypeStationTestData;

namespace ControlServer.Tests;

/// <summary>
/// 目录完整确认之后的处理入口（REQ-0341、REQ-0342、REQ-0345，control-server#162）：影响只收敛到绑定在变化 Station 上的那个任务类型，
/// 不改写任何业务配置，不碰已建单的订单。
/// </summary>
public sealed class CatalogBindingHoldConvergenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARenamedStationHoldsOnlyTheTaskTypeBoundToItAndNothingOnAnotherMap()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 26, GateBinding);

        IReadOnlyList<CatalogBindingHoldOutcome> outcomes = await TaskTypeHoldTestKit.Convergence(fixture)
            .ApplyAsync(TaskTypeHoldTestKit.Catalog25((210, "关卡"), (305, "派工待送取货-临时堆放")), Token);
        fixture.Context.ChangeTracker.Clear();

        CatalogBindingHoldOutcome outcome = Assert.Single(outcomes);
        Assert.True(outcome.HoldCreated);
        TaskTypeStationHold hold = Assert.Single(await fixture.Holds.ListUnreleasedAsync(25, Token));
        Assert.Equal(TransportTaskTypes.StagingToWire, hold.TaskType);
        Assert.Equal(TaskTypeStationHoldSource.CatalogChange, hold.Source);
        Assert.Equal(CatalogBindingHoldReasons.StationRenamed, hold.ReasonCode);
        Assert.False(await fixture.Holds.IsHeldAsync(25, TransportTaskTypes.WireToGate, Token));
        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(26, Token));

        TaskTypeStationCatalogChange change = Assert.Single(await fixture.CatalogChanges.ListAsync(25, Token));
        Assert.Equal(305, change.StationRiotId);
        Assert.Equal("派工待送取货", change.PreviousStationName);
        Assert.Equal("派工待送取货-临时堆放", change.CurrentStationName);
        Assert.Equal("RENAMED", change.ChangeKind);
        Assert.Equal([TransportTaskTypes.StagingToWire], change.AffectedTaskTypes);
        Assert.Equal(hold.HoldId, change.HoldId);
        Assert.Equal(TaskTypeHoldTestKit.Revision25, change.CatalogRevision);
    }

    [Fact]
    public async Task EveryAutomaticHoldWritesOneImmutableAuditRecordNamingTheChangeOnBothSides()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        long version = await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);

        await TaskTypeHoldTestKit.Convergence(fixture)
            .ApplyAsync(TaskTypeHoldTestKit.Catalog25((210, "关卡-新"), (305, "派工待送取货")), Token);
        fixture.Context.ChangeTracker.Clear();

        BusinessAuditRecordRow audit = Assert.Single(await fixture.Context.Set<BusinessAuditRecordRow>()
            .Where(row => row.Action == CatalogBindingHoldConvergence.HoldRaisedAction)
            .ToArrayAsync(Token));
        Assert.Equal(GovernedObjectKind.PublicStationBinding, audit.ObjectKind);
        Assert.Equal("map-25", audit.ObjectId);
        Assert.Equal(version, audit.Version);
        Assert.Equal(GovernanceActionOutcome.Succeeded, audit.Outcome);
        Assert.Equal(AuditActorAttribution.NotAttributableToNaturalPerson, audit.ActorAttribution);
        using JsonDocument detail = JsonDocument.Parse(audit.DetailJson);
        JsonElement root = detail.RootElement;
        Assert.Equal(25, root.GetProperty("mapId").GetInt32());
        Assert.Equal(TransportTaskTypes.WireToGate, root.GetProperty("taskType").GetString());
        Assert.Equal(TaskTypeStationHoldSource.CatalogChange, root.GetProperty("source").GetString());
        Assert.Equal("RENAMED", root.GetProperty("classification").GetString());
        Assert.Equal(210, root.GetProperty("before").GetProperty("stationRiotId").GetInt32());
        Assert.Equal("关卡", root.GetProperty("before").GetProperty("stationName").GetString());
        Assert.Equal(210, root.GetProperty("after").GetProperty("stationRiotId").GetInt32());
        Assert.Equal("关卡-新", root.GetProperty("after").GetProperty("stationName").GetString());
        Assert.Equal(TaskTypeHoldTestKit.Revision25, root.GetProperty("catalogRevision").GetInt64());
        Assert.Equal(0, root.GetProperty("inFlightDemands").GetInt32());
    }

    [Fact]
    public async Task ARemovedStationAndAnIdReplacedStationBothHoldAndNeitherIsReboundByName()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        long version = await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);
        TaskTypeStationBindingSetVersion before = (await fixture.Bindings.ReadActiveAsync(25, Token))!;

        // 210 is gone and 230 now carries its name; 305 is simply gone.
        await TaskTypeHoldTestKit.Convergence(fixture)
            .ApplyAsync(TaskTypeHoldTestKit.Catalog25((230, "关卡"), (211, "B-WB-01")), Token);
        fixture.Context.ChangeTracker.Clear();

        IReadOnlyList<TaskTypeStationHold> holds = await fixture.Holds.ListUnreleasedAsync(25, Token);
        Assert.Equal(
            [TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate],
            holds.Select(hold => hold.TaskType).Order(StringComparer.Ordinal));
        Assert.All(holds, hold => Assert.Equal(CatalogBindingHoldReasons.StationNotInCatalog, hold.ReasonCode));
        Assert.Equal(
            ["ID_REPLACED", "REMOVED"],
            (await fixture.CatalogChanges.ListAsync(25, Token)).Select(change => change.ChangeKind).Order(StringComparer.Ordinal));

        // The binding set is exactly what it was: same active version, same stations, same requirement set.
        TaskTypeStationBindingSetVersion after = (await fixture.Bindings.ReadActiveAsync(25, Token))!;
        Assert.Equal(version, after.Version);
        Assert.Equal(before.ContentSha256, after.ContentSha256);
        Assert.Equal(before.Bindings, after.Bindings);
        Assert.Equal(before.RequiredTaskTypes, after.RequiredTaskTypes);
        Assert.Equal(version, (await fixture.Bindings.ReadLatestAsync(25, Token))!.Version);
    }

    [Fact]
    public async Task TheSameChangeSeenOnTwoRoundsIsOneHoldOneChangeRecordAndOneAudit()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);
        RiotMapStationCatalogSnapshot renamed = TaskTypeHoldTestKit.Catalog25((210, "关卡"), (305, "派工待送取货-改"));

        await TaskTypeHoldTestKit.Convergence(fixture).ApplyAsync(renamed, Token);
        IReadOnlyList<CatalogBindingHoldOutcome> second = await TaskTypeHoldTestKit.Convergence(fixture)
            .ApplyAsync(renamed with { ObservedAt = Now.AddSeconds(30) }, Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.False(Assert.Single(second).HoldCreated);
        Assert.Single(await fixture.Holds.ListUnreleasedAsync(25, Token));
        Assert.Single(await fixture.CatalogChanges.ListAsync(25, Token));
        Assert.Single(await fixture.Context.Set<BusinessAuditRecordRow>()
            .Where(row => row.Action == CatalogBindingHoldConvergence.HoldRaisedAction)
            .ToArrayAsync(Token));
    }

    [Fact]
    public async Task TheSameChangeUnderANewCatalogRevisionFromAnUnrelatedEditIsStillOneChangeRecord()
    {
        // control-server#201 (review C of #162): the revision is the whole Map's content hash, so an edit to a station no
        // binding names changes it too. 305 is still renamed to the same name, under the same hold -- one change, one row.
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);

        await TaskTypeHoldTestKit.Convergence(fixture).ApplyAsync(
            TaskTypeHoldTestKit.Catalog25((210, "关卡"), (305, "派工待送取货-改"), (211, "B-WB-01")), Token);
        await TaskTypeHoldTestKit.Convergence(fixture).ApplyAsync(
            TaskTypeHoldTestKit.Catalog25At(2000, (210, "关卡"), (305, "派工待送取货-改"), (211, "B-WB-99")), Token);
        fixture.Context.ChangeTracker.Clear();

        TaskTypeStationCatalogChange change = Assert.Single(await fixture.CatalogChanges.ListAsync(25, Token));
        Assert.Equal(TaskTypeHoldTestKit.Revision25, change.CatalogRevision);
        Assert.Single(await fixture.Holds.ListUnreleasedAsync(25, Token));
        Assert.Single(await fixture.Context.Set<BusinessAuditRecordRow>()
            .Where(row => row.Action == CatalogBindingHoldConvergence.HoldRaisedAction)
            .ToArrayAsync(Token));
    }

    [Fact]
    public async Task AChangeThatItselfMovedOnIsRecordedAgainUnderTheSameHold()
    {
        // Renamed, renamed again, then removed: three different changes. The rename-to-rename stays under the one hold;
        // the removal raises its own (another reason code), and each gets its own row.
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);

        await TaskTypeHoldTestKit.Convergence(fixture).ApplyAsync(
            TaskTypeHoldTestKit.Catalog25((210, "关卡"), (305, "派工待送取货-改")), Token);
        await TaskTypeHoldTestKit.Convergence(fixture).ApplyAsync(
            TaskTypeHoldTestKit.Catalog25At(2000, (210, "关卡"), (305, "派工待送取货-再改")), Token);
        await TaskTypeHoldTestKit.Convergence(fixture).ApplyAsync(
            TaskTypeHoldTestKit.Catalog25At(3000, (210, "关卡"), (305, "派工待送取货-再改"), (211, "B-WB-99")), Token);
        await TaskTypeHoldTestKit.Convergence(fixture).ApplyAsync(
            TaskTypeHoldTestKit.Catalog25At(4000, (210, "关卡")), Token);
        fixture.Context.ChangeTracker.Clear();

        IReadOnlyList<TaskTypeStationCatalogChange> changes = await fixture.CatalogChanges.ListAsync(25, Token);
        Assert.Equal(
            [("RENAMED", "派工待送取货-改", 1000L), ("RENAMED", "派工待送取货-再改", 2000L), ("REMOVED", (string?)null, 4000L)],
            changes.Select(change => (change.ChangeKind, change.CurrentStationName, change.CatalogRevision))
                .OrderBy(change => change.CatalogRevision));
        Assert.Equal(changes[0].HoldId, changes.Single(change => change.CatalogRevision == 2000).HoldId);
        Assert.NotEqual(changes[0].HoldId, changes.Single(change => change.CatalogRevision == 4000).HoldId);
    }

    [Fact]
    public async Task TheSameChangeUnderANewHoldAfterFieldOpsReleasedTheOldOneIsRecordedAgain()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);
        RiotMapStationCatalogSnapshot renamed = TaskTypeHoldTestKit.Catalog25((210, "关卡"), (305, "派工待送取货-改"));
        CatalogBindingHoldOutcome first = Assert.Single(
            await TaskTypeHoldTestKit.Convergence(fixture).ApplyAsync(renamed, Token));
        Assert.True(await fixture.Holds.ReleaseAsync(first.Hold.HoldId, "fieldops:test", Now.AddMinutes(1), Token));

        CatalogBindingHoldOutcome second = Assert.Single(await TaskTypeHoldTestKit.Convergence(fixture).ApplyAsync(
            TaskTypeHoldTestKit.Catalog25At(2000, (210, "关卡"), (305, "派工待送取货-改")), Token));
        fixture.Context.ChangeTracker.Clear();

        Assert.True(second.HoldCreated);
        IReadOnlyList<TaskTypeStationCatalogChange> changes = await fixture.CatalogChanges.ListAsync(25, Token);
        Assert.Equal(
            [first.Hold.HoldId, second.Hold.HoldId],
            changes.OrderBy(change => change.CatalogRevision).Select(change => change.HoldId));
    }

    [Fact]
    public async Task AChangeMadeWhileTheServiceWasDownIsJudgedOnTheFirstRoundAfterTheRestart()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);
        await TaskTypeHoldTestKit.Convergence(fixture)
            .ApplyAsync(TaskTypeHoldTestKit.Catalog25((210, "关卡"), (305, "派工待送取货")), Token);
        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(25, Token));

        // The service stops; 210 is renamed while it is down; a new process opens the same database.
        await using ControlServerDbContext restarted = new(
            new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(fixture.Connection).Options);
        await TaskTypeHoldTestKit.Convergence(restarted)
            .ApplyAsync(TaskTypeHoldTestKit.Catalog25((210, "关卡-重启期间改名"), (305, "派工待送取货")), Token);

        TaskTypeStationHold hold = Assert.Single(await new TaskTypeStationHoldStore(restarted).ListUnreleasedAsync(25, Token));
        Assert.Equal(TransportTaskTypes.WireToGate, hold.TaskType);
        Assert.Equal(CatalogBindingHoldReasons.StationRenamed, hold.ReasonCode);
    }

    [Fact]
    public async Task AnUnboundAreaNamedStationChangingItsNameRaisesNoHoldAndLeavesTheAreaAssignmentTableAlone()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);
        AreaAssignmentStore areas = new(fixture.Context, new GovernedConfigurationPublisher(fixture.Governance, fixture.Governance));
        AreaAssignmentTableVersion areaVersion = await areas.WriteVersionAsync(
            [new("N01", "MAP-25-WIRE_TO_GATE", "FRONT")], Now.AddDays(-1), Token);

        IReadOnlyList<CatalogBindingHoldOutcome> outcomes = await TaskTypeHoldTestKit.Convergence(fixture)
            .ApplyAsync(TaskTypeHoldTestKit.Catalog25((210, "关卡"), (305, "派工待送取货"), (211, "B-WB-99")), Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Empty(outcomes);
        Assert.Empty(await fixture.Holds.ListUnreleasedAsync(25, Token));
        Assert.Empty(await fixture.CatalogChanges.ListAsync(25, Token));
        AreaAssignmentTableVersion current = (await areas.ReadCurrentAsync(Token))!;
        Assert.Equal(areaVersion.Version, current.Version);
        Assert.Equal(areaVersion.SnapshotId, current.SnapshotId);
    }

    [Fact]
    public async Task AHoldDoesNotTouchAJourneyAlreadyUnderWayAndCountsItInTheAudit()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await TaskTypeHoldTestKit.ActivateAsync(fixture, 25, GateBinding, StagingBinding);
        await TaskTypeHoldTestKit.SeedJourneyUnderWayAsync(fixture.Context, "D-1", TransportTaskTypes.WireToGate);
        await TaskTypeHoldTestKit.SeedJourneyUnderWayAsync(fixture.Context, "D-2", TransportTaskTypes.StagingToWire);
        string before = await TaskTypeHoldTestKit.DescribeOrdersAsync(fixture.Context);

        await TaskTypeHoldTestKit.Convergence(fixture)
            .ApplyAsync(TaskTypeHoldTestKit.Catalog25((305, "派工待送取货")), Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.True(await fixture.Holds.IsHeldAsync(25, TransportTaskTypes.WireToGate, Token));
        Assert.Equal(before, await TaskTypeHoldTestKit.DescribeOrdersAsync(fixture.Context));
        BusinessAuditRecordRow audit = await fixture.Context.Set<BusinessAuditRecordRow>()
            .SingleAsync(row => row.Action == CatalogBindingHoldConvergence.HoldRaisedAction, Token);
        using JsonDocument detail = JsonDocument.Parse(audit.DetailJson);
        Assert.Equal(1, detail.RootElement.GetProperty("inFlightDemands").GetInt32());
    }
}

/// <summary>Shared setup for the hold tests: an active binding set, a catalog, and a journey under way.</summary>
internal static class TaskTypeHoldTestKit
{
    /// <summary>A catalog content hash; its revision is <see cref="Revision25"/>.</summary>
    public const string Sha25 = "00000000000003e8aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public const long Revision25 = 1000;

    public static RiotMapStationCatalogSnapshot Catalog25(params (int Id, string Name)[] stations) =>
        new(25, Now, Sha25, [.. stations.Select(station => new RiotMapStation(station.Id, station.Name))]);

    /// <summary>The same Map under another content hash, whose revision is <paramref name="revision"/>.</summary>
    public static RiotMapStationCatalogSnapshot Catalog25At(long revision, params (int Id, string Name)[] stations) =>
        Catalog25(stations) with { ContentSha256 = revision.ToString("x16", CultureInfo.InvariantCulture) + Sha25[16..] };

    public static async Task<long> ActivateAsync(
        TaskTypeStationPersistenceFixture fixture,
        int mapId,
        params TaskTypeStationBinding[] bindings)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskTypeStationRuleVersion rules = (await fixture.Rules.WriteVersionAsync(SixRules, Source, Now.AddDays(-1), token)).Version;
        TaskTypeStationBindingSetVersion written = (await fixture.Bindings.WriteVersionAsync(
            mapId,
            rules.Version,
            [.. bindings.Select(binding => binding.TaskType)],
            bindings,
            catalogRevision: null,
            Source,
            Now.AddDays(-1),
            token)).Version;
        await fixture.Bindings.SetActiveAsync(mapId, written.Version, Now.AddDays(-1), token);
        fixture.Context.ChangeTracker.Clear();
        return written.Version;
    }

    public static CatalogBindingHoldConvergence Convergence(TaskTypeStationPersistenceFixture fixture) =>
        Convergence(fixture.Context);

    public static CatalogBindingHoldConvergence Convergence(ControlServerDbContext context)
    {
        GovernanceStore governance = new(
            context,
            new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
            AuditRetentionPolicy.Default);
        return new CatalogBindingHoldConvergence(
            context,
            new TaskTypeStationBindingStore(context, new GovernedConfigurationPublisher(governance, governance)),
            new TaskTypeStationHoldStore(context),
            new TaskTypeStationCatalogChangeStore(context),
            governance,
            new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
            new FixedClock());
    }

    /// <summary>A demand of this task type accepted, its journey heading for the gate, and its RIoT order created.</summary>
    public static async Task SeedJourneyUnderWayAsync(ControlServerDbContext context, string demandId, string taskType)
    {
        context.AcceptedDemands.Add(new AcceptedDemandRow
        {
            DemandId = demandId,
            SeriesId = "S-" + demandId,
            TransportDemandKey = "K-" + demandId,
            WorkType = taskType,
            Sublot = "SUBLOT-" + demandId,
            HistoryEpoch = "epoch",
            ValuePollTraceId = "trace",
            ValueProjectionCommitId = "commit",
            LiveMesFieldsJson = "{}",
            CreatedAt = Now.AddMinutes(-10),
            AcceptedAt = Now.AddMinutes(-9),
            Status = DemandExecutionStatus.Accepted
        });
        context.JourneyRuntimes.Add(new JourneyRuntimeRow
        {
            DemandId = demandId,
            Stage = JourneyRuntimeStage.AwaitingGateArrival,
            AgvId = "AGV-" + demandId,
            VehicleKey = "VEHICLE-" + demandId,
            AgvLifecycleGeneration = 1,
            MapId = 25,
            MapIdentity = "MAP-25",
            DispatchZone = "MAP-25-WIRE_TO_GATE",
            RouteEvidenceId = "ROUTE-01",
            PickupStationId = "PICKUP",
            PickupStationRiotId = 11,
            GateStationId = "关卡",
            GateStationRiotId = 210,
            ExpectedBasketCount = 2,
            TargetSlotsJson = "[1,2]",
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
            CreatedAt = Now.AddMinutes(-8),
            UpdatedAt = Now.AddMinutes(-1)
        });
        context.OrderIntents.Add(new OrderIntentRow
        {
            MovementLegId = $"gate-leg-{demandId}",
            DemandId = demandId,
            UpperId = $"UPPER-GATE-{demandId}",
            Purpose = "GATE",
            TargetStationId = "关卡",
            VehicleKey = "VEHICLE-" + demandId,
            MapId = 25,
            DestinationStationId = 210,
            AgvLifecycleGeneration = 1,
            DispatchGeneration = 1,
            CreatedAt = Now.AddMinutes(-2),
            Status = "CREATED",
            OrderId = "RIOT-ORDER-" + demandId
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();
    }

    /// <summary>Every field of every order intent and journey, so that "untouched" is checked, not assumed.</summary>
    public static async Task<string> DescribeOrdersAsync(ControlServerDbContext context)
    {
        OrderIntentRow[] orders = await context.OrderIntents.AsNoTracking()
            .OrderBy(row => row.MovementLegId).ToArrayAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow[] journeys = await context.JourneyRuntimes.AsNoTracking()
            .OrderBy(row => row.DemandId).ToArrayAsync(TestContext.Current.CancellationToken);
        return JsonSerializer.Serialize(new { orders, journeys });
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
