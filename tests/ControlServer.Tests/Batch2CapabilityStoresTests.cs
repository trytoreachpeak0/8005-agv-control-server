using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// The batch-2 track-B persistence foundation (ticket 06): every new table gets a write and a
/// read-back, and the two constraints that carry real weight — vehicle-occupancy uniqueness and
/// the frozen-endpoint rule — get their negative cases.
/// </summary>
/// <remarks>
/// Structure only, so these are storage tests. No staleness verdict, no admission rule, no
/// escalation is exercised here — those belong to tickets 09／10／11／12／13.
/// </remarks>
public sealed class Batch2CapabilityStoresTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    // ---- B2 multi-vehicle execution (ticket 09) ----------------------------------------

    [Fact]
    public async Task DispatchPolicyRoundTripsAdmissionsZonesAndBudgets()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        VehicleDispatchPolicyStore store = new(fixture.Context);

        VehicleDispatchPolicy written = new(
            [
                new VehicleDispatchProfile("agv01", Set("WIRE_TO_GATE", "CHARGE"), 30_000),
                new VehicleDispatchProfile("agv02", Set("WIRE_TO_GATE"), 45_000),
            ],
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["MAP-25-WIRE_TO_GATE"] = Set("agv01", "agv02"),
            },
            "CONFIG-1");

        await store.ReplacePolicyAsync(written, Now, TestContext.Current.CancellationToken);
        VehicleDispatchPolicy read = await store.ReadPolicyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["agv01", "agv02"], read.Vehicles.Select(vehicle => vehicle.AgvId));
        Assert.Equal(Set("WIRE_TO_GATE", "CHARGE"), read.Vehicles[0].AllowedTaskTypes);
        Assert.Equal(45_000, read.Vehicles[1].RoundTimeoutMilliseconds);
        Assert.Equal(Set("agv01", "agv02"), read.ZoneVehicles["MAP-25-WIRE_TO_GATE"]);
        Assert.Equal("CONFIG-1", read.ConfigurationVersion);
    }

    [Fact]
    public async Task DispatchPolicyReplacementIsWholesaleSoARemovedVehicleDisappears()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        VehicleDispatchPolicyStore store = new(fixture.Context);

        await store.ReplacePolicyAsync(
            new VehicleDispatchPolicy(
                [new VehicleDispatchProfile("agv01", Set("WIRE_TO_GATE"), 30_000)],
                new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal),
                "CONFIG-1"),
            Now,
            TestContext.Current.CancellationToken);
        await store.ReplacePolicyAsync(
            new VehicleDispatchPolicy(
                [new VehicleDispatchProfile("agv02", Set("WIRE_TO_GATE"), 30_000)],
                new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal),
                "CONFIG-2"),
            Now,
            TestContext.Current.CancellationToken);

        VehicleDispatchPolicy read = await store.ReadPolicyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["agv02"], read.Vehicles.Select(vehicle => vehicle.AgvId));
    }

    [Fact]
    public async Task ASecondClaimOnAnOccupiedVehicleIsRefusedByTheUniqueIndex()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.AddOrderIntentAsync("UPPER-1", "agv01");
        await fixture.AddOrderIntentAsync("UPPER-2", "agv01");
        VehicleDispatchPolicyStore store = new(fixture.Context);

        Assert.True(await store.TryClaimVehicleOccupancyAsync(
            "UPPER-1", Now, TestContext.Current.CancellationToken));
        Assert.False(await store.TryClaimVehicleOccupancyAsync(
            "UPPER-2", Now, TestContext.Current.CancellationToken));

        // Releasing the first frees the vehicle for the second — the index is filtered on
        // ReleasedAt, so a finished occupancy steps out of the uniqueness scope.
        await store.ReleaseVehicleOccupancyAsync("UPPER-1", Now, TestContext.Current.CancellationToken);
        Assert.True(await store.TryClaimVehicleOccupancyAsync(
            "UPPER-2", Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnclaimedOrderIntentsForOneVehicleDoNotCollide()
    {
        // This is the behaviour-unchanged guarantee ticket 06 promised. OrderIntents keeps
        // historical rows, so one vehicle already appears on many of them; the index only bites
        // once something writes ClaimedAt, which nothing does before ticket 09.
        await using Fixture fixture = await Fixture.CreateAsync();

        await fixture.AddOrderIntentAsync("UPPER-1", "agv01");
        await fixture.AddOrderIntentAsync("UPPER-2", "agv01");
        await fixture.AddOrderIntentAsync("UPPER-3", "agv01");

        Assert.Equal(3, await fixture.Context.OrderIntents
            .CountAsync(intent => intent.VehicleKey == "agv01", TestContext.Current.CancellationToken));
    }

    // ---- RouteGraphSnapshot engine (ticket 12) -----------------------------------------

    [Fact]
    public async Task DesignStateRoundTripsAndBumpsTheRevision()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        RouteGraphSnapshotStore store = new(fixture.Context);

        long revision = await store.ReplaceDesignStateAsync(
            25,
            [new RouteGraphEdgeFact(1, 2, 1, 15450.0, -18410, 24890, -18410, 40340, 1, false)],
            [new RouteGraphStationFact(1, "N1-1", 187, 760.0, 27520.0, 76, 0.0)],
            "2026-09-07 08:00:00",
            Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, revision);
        RouteGraphState state = Assert.IsType<RouteGraphState>(
            await store.ReadStateAsync(25, TestContext.Current.CancellationToken));

        RouteGraphEdgeFact edge = Assert.Single(state.Edges);
        Assert.Equal((2, 1), (edge.StartNode, edge.EndNode));
        Assert.Equal(15450.0, edge.CostMm);
        RouteGraphStationFact station = Assert.Single(state.Stations);
        Assert.Equal((187, 76), (station.EdgeId, station.ResolvedNode));
        Assert.Equal(1, state.Header.DesignEdgeCount);
        Assert.Equal("2026-09-07 08:00:00", state.Header.DesignSourceGmtUpdate);

        long second = await store.ReplaceDesignStateAsync(
            25, [], [], null, Now, TestContext.Current.CancellationToken);
        Assert.Equal(2, second);
        // Wholesale replacement: the previous revision's edges are gone, not merged.
        Assert.Empty((await store.ReadStateAsync(25, TestContext.Current.CancellationToken))!.Edges);
    }

    [Fact]
    public async Task RuntimeRemovalsRoundTripOnTheirOwnCycle()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        RouteGraphSnapshotStore store = new(fixture.Context);

        await store.ReplaceRuntimeStateAsync(25, [187, 188], [170], Now, TestContext.Current.CancellationToken);
        RouteGraphState state = (await store.ReadStateAsync(25, TestContext.Current.CancellationToken))!;

        Assert.Equal([187, 188], state.RemovedEdgeIds.Order());
        Assert.Equal([170], state.RemovedStationIds);
        Assert.Equal(2, state.Header.RuntimeRemovedEdgeCount);

        // Empty is the normal case — map25 returned exactly that in Round 43 — and it must clear
        // the previous cycle's removals rather than leaving them behind.
        await store.ReplaceRuntimeStateAsync(25, [], [], Now, TestContext.Current.CancellationToken);
        Assert.Empty((await store.ReadStateAsync(25, TestContext.Current.CancellationToken))!.RemovedEdgeIds);
    }

    [Fact]
    public async Task EdgeGroupsRoundTripAndAnEmptyGroupListFingerprintsAsEmpty()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        RouteGraphSnapshotStore store = new(fixture.Context);

        // map25 has no edge groups at all — every group on the production RIoT belongs to another
        // Map — so "no groups" is the state the engine will actually see first.
        string empty = RouteGraphEdgeGroupFingerprint.Compute([]);
        Assert.Equal("", empty);
        await store.ReplaceEdgeGroupsAsync(25, [], empty, Now, TestContext.Current.CancellationToken);
        Assert.Equal("", (await store.ReadHeaderAsync(25, TestContext.Current.CancellationToken))!
            .EdgeGroupFingerprint);

        RouteGraphEdgeGroupFact[] groups =
            [new RouteGraphEdgeGroupFact("老厂电梯", 8, "SINGLE_VEHICLE_ONLY")];
        string fingerprint = RouteGraphEdgeGroupFingerprint.Compute(groups);
        Assert.NotEqual("", fingerprint);
        await store.ReplaceEdgeGroupsAsync(25, groups, fingerprint, Now, TestContext.Current.CancellationToken);

        RouteGraphSnapshotHeader header =
            (await store.ReadHeaderAsync(25, TestContext.Current.CancellationToken))!;
        Assert.Equal(fingerprint, header.EdgeGroupFingerprint);
        RouteGraphEdgeGroupRow row = await fixture.Context.RouteGraphEdgeGroups
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(("老厂电梯", 8, "SINGLE_VEHICLE_ONLY"), (row.GroupName, row.EdgeId, row.GroupType));
    }

    [Fact]
    public void EdgeGroupFingerprintIsOrderIndependentAndSeparatesFields()
    {
        RouteGraphEdgeGroupFact[] one =
        [
            new RouteGraphEdgeGroupFact("A", 1, "SINGLE_VEHICLE_ONLY"),
            new RouteGraphEdgeGroupFact("B", 2, "SAME_DIRECTION_ONLY"),
        ];
        RouteGraphEdgeGroupFact[] reordered = [one[1], one[0]];
        Assert.Equal(RouteGraphEdgeGroupFingerprint.Compute(one), RouteGraphEdgeGroupFingerprint.Compute(reordered));

        // Field boundaries must be unambiguous: these two memberships differ only in where the
        // group name ends, and a naive concatenation would give them the same fingerprint.
        Assert.NotEqual(
            RouteGraphEdgeGroupFingerprint.Compute([new RouteGraphEdgeGroupFact("AB", 1, "T")]),
            RouteGraphEdgeGroupFingerprint.Compute([new RouteGraphEdgeGroupFact("A", 1, "T")]));
    }

    [Fact]
    public async Task ANewSnapshotStartsStaleAndTheFirstStaleReasonKeepsItsClock()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        RouteGraphSnapshotStore store = new(fixture.Context);

        // "No snapshot" must never read as "a usable empty graph".
        await store.RecordDynamicRouteCostObservationAsync(25, false, Now, TestContext.Current.CancellationToken);
        RouteGraphSnapshotHeader header =
            (await store.ReadHeaderAsync(25, TestContext.Current.CancellationToken))!;
        Assert.Equal("SNAPSHOT_NEVER_REFRESHED", header.StaleReason);

        await store.MarkStaleAsync(25, "EDGE_GROUP_FINGERPRINT_CHANGED", Now, TestContext.Current.CancellationToken);
        DateTimeOffset? firstSince =
            (await store.ReadHeaderAsync(25, TestContext.Current.CancellationToken))!.StaleSince;

        await store.MarkStaleAsync(
            25, "DYNAMIC_ROUTE_COST_APPEARED", Now.AddMinutes(5), TestContext.Current.CancellationToken);
        RouteGraphSnapshotHeader second =
            (await store.ReadHeaderAsync(25, TestContext.Current.CancellationToken))!;

        // Second trigger updates the reason but must not restart the clock — StaleSince answers
        // "stale since when", and a stale snapshot that keeps re-arming would never look old.
        Assert.Equal("DYNAMIC_ROUTE_COST_APPEARED", second.StaleReason);
        Assert.Equal(firstSince, second.StaleSince);

        await store.ClearStaleAsync(25, Now.AddMinutes(6), TestContext.Current.CancellationToken);
        RouteGraphSnapshotHeader cleared =
            (await store.ReadHeaderAsync(25, TestContext.Current.CancellationToken))!;
        Assert.Null(cleared.StaleReason);
        Assert.Null(cleared.StaleSince);
    }

    // ---- FP-C11 fault isolation and command reconciliation (tickets 10 and 11) -----------

    [Fact]
    public async Task FaultLevelsRoundTripAndOneEpisodeKeepsOneGeneration()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        VehicleFaultStore store = new(fixture.Context);

        VehicleFaultFact suspected = await store.RecordLevelAsync(
            "agv01", VehicleFaultLevel.SuspectedBlocked, "COMMS_LOST", false, Now,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, suspected.FaultGeneration);
        Assert.False(suspected.EvidenceOnAutoConfirmWhitelist);

        // Escalating within a live fault keeps the generation, so the cargo binding and the
        // command audit stay attached to the same episode.
        VehicleFaultFact confirmed = await store.RecordLevelAsync(
            "agv01", VehicleFaultLevel.ConfirmedIsolated, "VEHICLE_REPORTED_FAULT", true,
            Now.AddSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, confirmed.FaultGeneration);
        Assert.Equal(VehicleFaultLevel.ConfirmedIsolated, confirmed.Level);
        Assert.Equal(suspected.EnteredAt, confirmed.EnteredAt);

        await store.ClearAsync("agv01", 1, "CAUSE_REMOVED_AND_SAFETY_PROVEN", Now.AddMinutes(2),
            TestContext.Current.CancellationToken);
        VehicleFaultFact reentered = await store.RecordLevelAsync(
            "agv01", VehicleFaultLevel.SuspectedBlocked, "COMMS_LOST", false, Now.AddMinutes(3),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, reentered.FaultGeneration);
        // A new episode starts with no stop proof carried over from the old one.
        Assert.False(reentered.StopProven);
    }

    [Fact]
    public async Task StopProofAndEscalationAreRecordedAgainstTheirOwnGeneration()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        VehicleFaultStore store = new(fixture.Context);
        await store.RecordLevelAsync("agv01", VehicleFaultLevel.SuspectedBlocked, "NAV_FAILED", false, Now,
            TestContext.Current.CancellationToken);

        await store.RecordStopProofAsync("agv01", 1, true, Now.AddSeconds(5), TestContext.Current.CancellationToken);
        Assert.True((await store.ReadAsync("agv01", TestContext.Current.CancellationToken))!.StopProven);

        await store.RecordEscalationAsync("agv01", 1, Now.AddSeconds(6), TestContext.Current.CancellationToken);
        Assert.NotNull((await store.ReadAsync("agv01", TestContext.Current.CancellationToken))!.EscalatedAt);

        // A write carrying a stale generation belongs to an episode that has already ended;
        // applying it would attach one episode's evidence to another's fault.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordStopProofAsync(
            "agv01", 99, true, Now.AddSeconds(7), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CargoBindingIsUniquePerLiveFaultAndSurvivesRelease()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        VehicleFaultStore store = new(fixture.Context);
        await store.RecordLevelAsync("agv01", VehicleFaultLevel.ConfirmedIsolated, "VEHICLE_REPORTED_FAULT",
            true, Now, TestContext.Current.CancellationToken);

        FaultedCargoBinding binding = await store.BindCargoAsync(
            "agv01", 1, "DEMAND-1", "LEG-1", "TDK-1", loadingWitnessed: true, Now,
            TestContext.Current.CancellationToken);
        Assert.Equal("DEMAND-1", (await store.ReadLiveCargoAsync("agv01", TestContext.Current.CancellationToken))!.DemandId);

        await Assert.ThrowsAsync<DbUpdateException>(() => store.BindCargoAsync(
            "agv01", 1, "DEMAND-2", null, "TDK-2", loadingWitnessed: false, Now,
            TestContext.Current.CancellationToken));
        fixture.Context.ChangeTracker.Clear();

        await store.ReleaseCargoAsync(binding.CargoBindingId, "REPAIR_CONTINUE", Now.AddMinutes(1),
            TestContext.Current.CancellationToken);
        Assert.Null(await store.ReadLiveCargoAsync("agv01", TestContext.Current.CancellationToken));
        // Released, not deleted: the record of what the vehicle was carrying stays.
        Assert.Single(await fixture.Context.FaultedVehicleCargo.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CommandAttemptsAreNumberedSoCalledOnceIsDecidable()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        RiotOrderCommandAuditStore store = new(fixture.Context);

        RiotOrderCommandAttempt first = await store.ArmAttemptAsync(
            "triggerEmergency", "agv01", "UPPER-1", "ORDER-1", "sha-1", 1, Now,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, first.AttemptNumber);
        // Armed, not confirmed: this is exactly the state that says "it may have gone out".
        Assert.Equal(RiotOrderCommandOutcome.Pending, first.Outcome);

        await store.RecordOutcomeAsync(first.CommandAuditId, RiotOrderCommandOutcome.Unknown, null,
            Now.AddSeconds(1), TestContext.Current.CancellationToken);
        RiotOrderCommandAttempt retry = await store.ArmAttemptAsync(
            "triggerEmergency", "agv01", "UPPER-1", "ORDER-1", "sha-1", 1, Now.AddSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, retry.AttemptNumber);

        await store.RecordOutcomeAsync(retry.CommandAuditId, RiotOrderCommandOutcome.Confirmed, "{}",
            Now.AddSeconds(3), TestContext.Current.CancellationToken);

        IReadOnlyList<RiotOrderCommandAttempt> attempts = await store.ReadAttemptsAsync(
            "triggerEmergency", "UPPER-1", TestContext.Current.CancellationToken);
        Assert.Equal([1, 2], attempts.Select(attempt => attempt.AttemptNumber));
        Assert.Equal(RiotOrderCommandOutcome.Confirmed, attempts[1].Outcome);
        // A different command against the same order is a separate series.
        Assert.Empty(await store.ReadAttemptsAsync("OrderHold", "UPPER-1", TestContext.Current.CancellationToken));
    }

    // ---- FP-C13 catalog availability and the pre-create gate (ticket 13) ------------------

    [Fact]
    public async Task AnUnconfirmedCatalogIsNotFreshAndAFailureDoesNotExtendFreshness()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        CatalogAvailabilityStore store = new(fixture.Context);

        await store.RecordFailureAsync(25, MapStationCatalogState.RefreshFailed, "HTTP_TIMEOUT", Now,
            TestContext.Current.CancellationToken);
        MapStationCatalogAvailability initial =
            (await store.ReadStateAsync(25, TestContext.Current.CancellationToken))!;
        Assert.Equal(MapStationCatalogState.RefreshFailed, initial.State);
        Assert.Null(initial.LastCompleteConfirmationAt);

        await store.RecordCompleteConfirmationAsync(25, 7, 60, 300, Now.AddMinutes(1),
            TestContext.Current.CancellationToken);
        MapStationCatalogAvailability confirmed =
            (await store.ReadStateAsync(25, TestContext.Current.CancellationToken))!;
        Assert.Equal(MapStationCatalogState.Fresh, confirmed.State);
        Assert.Equal(Now.AddMinutes(1), confirmed.LastCompleteConfirmationAt);
        Assert.Equal((60, 300), (confirmed.ApprovedSyncPeriodSeconds, confirmed.ApprovedMaxUnconfirmedSeconds));

        await store.RecordFailureAsync(25, MapStationCatalogState.CandidateInvalid, "MISSING_STATIONS",
            Now.AddMinutes(2), TestContext.Current.CancellationToken);
        MapStationCatalogAvailability afterFailure =
            (await store.ReadStateAsync(25, TestContext.Current.CancellationToken))!;

        // REQ-0302 measures freshness from the last complete confirmation, so a failed attempt
        // must move LastAttemptAt and leave the confirmation timestamp exactly where it was.
        Assert.Equal(Now.AddMinutes(1), afterFailure.LastCompleteConfirmationAt);
        Assert.Equal(Now.AddMinutes(2), afterFailure.LastAttemptAt);
        Assert.Equal(MapStationCatalogState.CandidateInvalid, afterFailure.State);
    }

    [Fact]
    public async Task FrozenEndpointsAreIdempotentAndRefuseToBeRewritten()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        CatalogAvailabilityStore store = new(fixture.Context);
        FrozenStationFact[] frozen =
        [
            new FrozenStationFact(FrozenStationRole.Pickup, 25, 10, "N1-1"),
            new FrozenStationFact(FrozenStationRole.Dropoff, 25, 25, "GATE-1"),
        ];

        await store.FreezeDemandStationsAsync("DEMAND-1", "TDK-1", frozen, 7, Now,
            TestContext.Current.CancellationToken);
        await store.FreezeDemandStationsAsync("DEMAND-1", "TDK-1", frozen, 8, Now.AddMinutes(1),
            TestContext.Current.CancellationToken);

        IReadOnlyList<FrozenStationFact> read =
            await store.ReadFrozenStationsAsync("DEMAND-1", TestContext.Current.CancellationToken);
        Assert.Equal(2, read.Count);
        Assert.Equal(10, read.Single(station => station.Role == FrozenStationRole.Pickup).StationId);

        // REQ-0305: a later catalog must not rewrite a task's endpoints. Rewriting one is a
        // caller bug and has to be loud, not a silently changed destination.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FreezeDemandStationsAsync(
            "DEMAND-1",
            "TDK-1",
            [new FrozenStationFact(FrozenStationRole.Pickup, 25, 99, "N9-9")],
            9,
            Now.AddMinutes(2),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GateVerdictsAreAuditedWithBothEvidenceSourcesSeparatelyNamed()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        CatalogAvailabilityStore store = new(fixture.Context);

        await store.RecordGateVerdictAsync(
            new CreateGateEvaluation(
                "GATE-1", "DEMAND-1", "TDK-1", "agv01", 25, 10,
                CreateGateVerdict.BlockedEvidenceConflict,
                RiotRouteCostMm: -1,
                GraphTraversalCostMm: 15450,
                GraphReachable: true,
                ConflictDetail: "graph reachable, RIoT unreachable",
                EvaluatedAt: Now),
            TestContext.Current.CancellationToken);

        CreateGateAuditRow row = await fixture.Context.CreateGateAudit
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CreateGateVerdict.BlockedEvidenceConflict, row.Verdict);
        // Two separately named facts, never merged into one "RouteCost" — REQ-0207, and the
        // closing clause of CP-0001's REQ-0298 revision.
        Assert.Equal(-1, row.RiotRouteCostMm);
        Assert.Equal(15450, row.GraphTraversalCostMm);
        Assert.True(row.GraphReachable);
    }

    // ---- migration -----------------------------------------------------------------------

    [Fact]
    public async Task MigrationAppliesForwardOverExistingDataWithoutLosingIt()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options =
            new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
        await using ControlServerDbContext context = new(options);

        Microsoft.EntityFrameworkCore.Migrations.IMigrator migrator =
            Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
                .GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(context);
        await migrator.MigrateAsync(
            "20260904094629_ManualChargingReturnToService", TestContext.Current.CancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO OrderIntents
                (MovementLegId, DemandId, UpperId, Purpose, TargetStationId, VehicleKey,
                 MapId, DestinationStationId, AgvLifecycleGeneration, DispatchGeneration,
                 CreatedAt, Status)
            VALUES
                ('LEG-1', 'DEMAND-1', 'UPPER-1', 'WIRE_TO_GATE', 'GATE-1', 'agv01',
                 25, 10, 1, 1, '2026-09-06T00:00:00+00:00', 'CONFIRMED'),
                ('LEG-2', 'DEMAND-2', 'UPPER-2', 'WIRE_TO_GATE', 'GATE-1', 'agv01',
                 25, 10, 1, 1, '2026-09-06T01:00:00+00:00', 'CONFIRMED')
            """,
            TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Two rows for one vehicle survive the new unique index, because neither has claimed an
        // occupancy. That is the whole reason the index is filtered on ClaimedAt.
        Assert.Equal(2, await context.OrderIntents.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            ["UPPER-1", "UPPER-2"],
            await context.OrderIntents.OrderBy(intent => intent.UpperId)
                .Select(intent => intent.UpperId)
                .ToListAsync(TestContext.Current.CancellationToken));

        // And the new tables are there, empty and queryable.
        Assert.Empty(await context.RouteGraphSnapshots.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await context.VehicleFaultStates.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await context.CreateGateAudit.ToListAsync(TestContext.Current.CancellationToken));
    }

    private static HashSet<string> Set(params string[] values) =>
        values.ToHashSet(StringComparer.Ordinal);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, ControlServerDbContext context)
        {
            _connection = connection;
            Context = context;
        }

        public ControlServerDbContext Context { get; }

        public static async Task<Fixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            ControlServerDbContext context = new(options);
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            return new Fixture(connection, context);
        }

        public async Task AddOrderIntentAsync(string upperId, string vehicleKey)
        {
            Context.OrderIntents.Add(new OrderIntentRow
            {
                MovementLegId = $"LEG-{upperId}",
                DemandId = $"DEMAND-{upperId}",
                UpperId = upperId,
                Purpose = "WIRE_TO_GATE",
                TargetStationId = "GATE-1",
                VehicleKey = vehicleKey,
                MapId = 25,
                DestinationStationId = 10,
                CreatedAt = Now,
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
