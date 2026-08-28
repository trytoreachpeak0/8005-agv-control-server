using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

public sealed class RiotDispatchAuditTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PreCreateReconciliationUnknownAppendsUnknownEventWithoutArmingCreate()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        ScriptedGateway gateway = new(
            [Observation(intent, RiotOrderObservationKind.Unknown, receipt: Receipt("RECONCILE", "AbsentAtObservation"))],
            Observation(intent, RiotOrderObservationKind.Active, "MUST-NOT-CREATE"));

        MovementDispatchResult result = await new MovementDispatchService(store, gateway, new AdvancingTimeProvider(Now))
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.ResultUnknown, result.Outcome);
        Assert.Equal(0, gateway.CreateCount);
        OrderIntentRow row = await context.OrderIntents.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, row.DispatchAuditVersion);
        Assert.Equal(0, row.CreateAttemptCount);
        Assert.Null(row.CreateAttemptId);
        Assert.Null(row.CreateDispatchArmedAt);
        Assert.Equal("PreCreateReconciliationUnknown", row.LastReconciliationOutcome);
        RiotDispatchAuditEventRow audit = await context.RiotDispatchAuditEvents
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("PRE_CREATE_RECONCILIATION", audit.Phase);
        Assert.Equal("UNKNOWN", audit.Outcome);
        Assert.Equal("AbsentAtObservation", audit.ReceiptClassification);
        Assert.Null(audit.AttemptId);
    }

    [Fact]
    public async Task StoreDropsUntrustedBusinessCodeBeforePersistingReceipt()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);

        await store.RecordReconciliationAsync(
            intent.UpperId,
            new DispatchAuditWrite(
                RiotDispatchAuditPhase.PreCreateReconciliation,
                RiotDispatchAuditOutcome.Unknown,
                Now,
                Receipt: Receipt(
                    "RECONCILE",
                    "SdkFailure",
                    500,
                    "TOP-SECRET-MARKER",
                    failureCategory: "RIOT_API_FAILURE")),
            markResultUnknown: true,
            TestContext.Current.CancellationToken);

        OrderIntentRow row = await context.OrderIntents.SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(row.LastReconciliationReceiptJson);
        Assert.DoesNotContain("TOP-SECRET-MARKER", row.LastReconciliationReceiptJson, StringComparison.Ordinal);
        RiotDispatchAuditEventRow audit = await context.RiotDispatchAuditEvents
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Null(audit.BusinessCode);
    }

    [Fact]
    public async Task CreateStartIsDurableBeforeGatewayMutationAndUnknownResponseIsSanitized()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        bool durableBeforeCall = false;
        ScriptedGateway gateway = new(
            [Observation(intent, RiotOrderObservationKind.NotFound, receipt: Receipt("RECONCILE", "NotFound", 404))],
            Observation(
                intent,
                RiotOrderObservationKind.Unknown,
                receipt: Receipt("CREATE", "SdkFailure", 502, "order-ref-missing", false, "PROTOCOL_FAILURE")),
            async () =>
            {
                OrderIntentRow armed = await context.OrderIntents.SingleAsync(TestContext.Current.CancellationToken);
                RiotDispatchAuditEventRow[] events = await context.RiotDispatchAuditEvents
                    .OrderBy(item => item.Sequence)
                    .ToArrayAsync(TestContext.Current.CancellationToken);
                durableBeforeCall = armed.Status == "CREATE_ATTEMPTED" &&
                                    armed.CreateAttemptCount == 1 &&
                                    !string.IsNullOrWhiteSpace(armed.CreateAttemptId) &&
                                    events.Any(item => item.Phase == "CREATE_DISPATCH" && item.Outcome == "ARMED") &&
                                    events.Any(item => item.Phase == "CREATE_REQUEST" && item.Outcome == "STARTED");
            });

        MovementDispatchResult result = await new MovementDispatchService(store, gateway, new AdvancingTimeProvider(Now))
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.ResultUnknown, result.Outcome);
        Assert.True(durableBeforeCall);
        Assert.Equal(1, gateway.CreateCount);
        OrderIntentRow row = await context.OrderIntents.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("RESULT_UNKNOWN", row.Status);
        Assert.Equal(1, row.CreateAttemptCount);
        Assert.Equal("CreateResponseUnknown", row.LastCreateOutcome);
        Assert.NotNull(row.LastCreateReceiptJson);
        Assert.Contains("order-ref-missing", row.LastCreateReceiptJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ResponseBody", row.LastCreateReceiptJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", row.LastCreateReceiptJson, StringComparison.OrdinalIgnoreCase);
        RiotDispatchAuditEventRow attempt = await context.RiotDispatchAuditEvents
            .SingleAsync(item => item.Phase == "CREATE_DISPATCH", TestContext.Current.CancellationToken);
        RiotDispatchAuditEventRow started = await context.RiotDispatchAuditEvents
            .SingleAsync(item => item.Phase == "CREATE_REQUEST", TestContext.Current.CancellationToken);
        Assert.True(started.OccurredAt > attempt.OccurredAt);
        Assert.Equal(attempt.RequestSemanticSha256, started.RequestSemanticSha256);
        Assert.Matches("^[0-9a-f]{64}$", attempt.RequestSemanticSha256);
        Assert.Equal(
            "0b494b2bdbbd3dae5d8f4d4c1de6d508f18205b44bedaf874c99420c69483cfc",
            attempt.RequestSemanticSha256);
        RiotDispatchAuditEventRow response = await context.RiotDispatchAuditEvents
            .SingleAsync(item => item.Phase == "CREATE_RESPONSE", TestContext.Current.CancellationToken);
        Assert.Equal("UNKNOWN", response.Outcome);
        Assert.Equal(502, response.HttpStatusCode);
        Assert.Equal("order-ref-missing", response.BusinessCode);
        Assert.False(response.ResultPresent);
        Assert.Equal("PROTOCOL_FAILURE", response.FailureCategory);
    }

    [Fact]
    public async Task AcceptedCreateWithUnknownIndependentReadRecordsPostCreateUnknown()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        ScriptedGateway gateway = new(
            [
                Observation(intent, RiotOrderObservationKind.NotFound, receipt: Receipt("RECONCILE", "NotFound", 404)),
                Observation(intent, RiotOrderObservationKind.Unknown, receipt: Receipt("RECONCILE", "AbsentAtObservation", 200, "0", false))
            ],
            Observation(intent, RiotOrderObservationKind.Active, "UNTRUSTED-CREATE", Receipt("CREATE", "SdkAccepted", 200, "0", true)));

        MovementDispatchResult result = await new MovementDispatchService(store, gateway, new AdvancingTimeProvider(Now))
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.ResultUnknown, result.Outcome);
        Assert.Equal(1, gateway.CreateCount);
        OrderIntentRow row = await context.OrderIntents.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("CreateResponseAccepted", row.LastCreateOutcome);
        Assert.Equal("PostCreateReconciliationUnknown", row.LastReconciliationOutcome);
        RiotDispatchAuditEventRow createResponse = await context.RiotDispatchAuditEvents
            .SingleAsync(item => item.Phase == "CREATE_RESPONSE", TestContext.Current.CancellationToken);
        RiotDispatchAuditEventRow reconciliation = await context.RiotDispatchAuditEvents
            .SingleAsync(item => item.Phase == "POST_CREATE_RECONCILIATION", TestContext.Current.CancellationToken);
        Assert.Equal("ACCEPTED", createResponse.Outcome);
        Assert.Equal("UNKNOWN", reconciliation.Outcome);
        Assert.Equal(createResponse.AttemptId, reconciliation.AttemptId);
    }

    [Fact]
    public async Task MatchingPostCreateObservationConfirmsOrderAndPreservesBothAuditPhases()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        ScriptedGateway gateway = new(
            [
                Observation(intent, RiotOrderObservationKind.NotFound, receipt: Receipt("RECONCILE", "NotFound", 404)),
                Observation(intent, RiotOrderObservationKind.Active, "ORDER-001", Receipt("RECONCILE", "Found", 200, "0", true))
            ],
            Observation(intent, RiotOrderObservationKind.Active, "ORDER-001", Receipt("CREATE", "SdkAccepted", 200, "0", true)));

        MovementDispatchResult result = await new MovementDispatchService(store, gateway, new AdvancingTimeProvider(Now))
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.Confirmed, result.Outcome);
        Assert.Equal("ORDER-001", result.OrderId);
        OrderIntentRow row = await context.OrderIntents.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("CONFIRMED", row.Status);
        Assert.Equal("CreateResponseAccepted", row.LastCreateOutcome);
        Assert.Equal("PostCreateReconciliationConfirmed", row.LastReconciliationOutcome);
        RiotDispatchAuditEventRow[] events = await context.RiotDispatchAuditEvents
            .OrderBy(item => item.Sequence)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Collection(
            events,
            item => Assert.Equal(("PRE_CREATE_RECONCILIATION", "NOT_FOUND"), (item.Phase, item.Outcome)),
            item => Assert.Equal(("CREATE_DISPATCH", "ARMED"), (item.Phase, item.Outcome)),
            item => Assert.Equal(("CREATE_REQUEST", "STARTED"), (item.Phase, item.Outcome)),
            item => Assert.Equal(("CREATE_RESPONSE", "ACCEPTED"), (item.Phase, item.Outcome)),
            item => Assert.Equal(("POST_CREATE_RECONCILIATION", "CONFIRMED"), (item.Phase, item.Outcome)));
        Assert.True(events.Zip(events.Skip(1), (left, right) => left.OccurredAt <= right.OccurredAt).All(value => value));
    }

    [Fact]
    public async Task CallerCancellationAfterCreateStartPersistsUnknownReceiptBeforePropagation()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        using CancellationTokenSource source = new();
        CancellingGateway gateway = new(intent, source);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MovementDispatchService(store, gateway, new AdvancingTimeProvider(Now))
                .ReconcileOrCreateAsync(intent.UpperId, source.Token));

        Assert.Equal(1, gateway.CreateCount);
        OrderIntentRow row = await context.OrderIntents.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("RESULT_UNKNOWN", row.Status);
        Assert.Equal(1, row.CreateAttemptCount);
        Assert.Equal("CreateResponseUnknown", row.LastCreateOutcome);
        RiotDispatchAuditEventRow response = await context.RiotDispatchAuditEvents
            .SingleAsync(item => item.Phase == "CREATE_RESPONSE", TestContext.Current.CancellationToken);
        Assert.Equal("UNKNOWN", response.Outcome);
        Assert.Equal("CALLER_CANCELLATION", response.FailureCategory);
    }

    [Fact]
    public async Task CallerCancellationDuringPostCreateReadPersistsUnknownReconciliationBeforePropagation()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        using CancellationTokenSource source = new();
        PostCreateReadCancellingGateway gateway = new(intent, source);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MovementDispatchService(store, gateway, new AdvancingTimeProvider(Now))
                .ReconcileOrCreateAsync(intent.UpperId, source.Token));

        Assert.Equal(1, gateway.CreateCount);
        Assert.Equal(2, gateway.ReconcileCount);
        OrderIntentRow row = await context.OrderIntents.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("RESULT_UNKNOWN", row.Status);
        Assert.Equal("CreateResponseAccepted", row.LastCreateOutcome);
        Assert.Equal("PostCreateReconciliationUnknown", row.LastReconciliationOutcome);
        RiotDispatchAuditEventRow reconciliation = await context.RiotDispatchAuditEvents
            .SingleAsync(
                item => item.Phase == "POST_CREATE_RECONCILIATION",
                TestContext.Current.CancellationToken);
        Assert.Equal("UNKNOWN", reconciliation.Outcome);
        Assert.Equal("CallerCancellation", reconciliation.ReceiptClassification);
        Assert.Equal("CALLER_CANCELLATION", reconciliation.FailureCategory);
    }

    [Fact]
    public async Task SemanticHashIncludesDestinationFromExactSdkRequestBody()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store, destinationStationId: 13);
        ScriptedGateway gateway = new(
            [Observation(intent, RiotOrderObservationKind.NotFound, receipt: Receipt("RECONCILE", "NotFound", 404))],
            Observation(intent, RiotOrderObservationKind.Unknown, receipt: Receipt("CREATE", "Unknown")));

        await new MovementDispatchService(store, gateway, new AdvancingTimeProvider(Now))
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        RiotDispatchAuditEventRow attempt = await context.RiotDispatchAuditEvents
            .SingleAsync(item => item.Phase == "CREATE_DISPATCH", TestContext.Current.CancellationToken);
        Assert.Equal(
            "ec1e4432e7bb232cd66c6a58e4fa48d81ab8aede1e44990b5f78c31e614d5787",
            attempt.RequestSemanticSha256);
        Assert.NotEqual(
            "0b494b2bdbbd3dae5d8f4d4c1de6d508f18205b44bedaf874c99420c69483cfc",
            attempt.RequestSemanticSha256);
    }

    [Fact]
    public async Task SecondArmForSameIntentIsRejectedWithoutChangingOriginalAttempt()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        CreateDispatchAttempt first = await store.ArmCreateDispatchAsync(
            intent.UpperId,
            new string('a', 64),
            Now,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
            store.ArmCreateDispatchAsync(
                intent.UpperId,
                new string('b', 64),
                Now.AddSeconds(1),
                TestContext.Current.CancellationToken));

        OrderIntentRow row = await context.OrderIntents.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(first.AttemptId, row.CreateAttemptId);
        Assert.Equal(1, row.CreateAttemptCount);
        Assert.Equal(Now, row.CreateDispatchArmedAt);
        RiotDispatchAuditEventRow audit = await context.RiotDispatchAuditEvents
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(first.AttemptId, audit.AttemptId);
        Assert.Equal(new string('a', 64), audit.RequestSemanticSha256);
    }

    [Fact]
    public async Task StaleConcurrentArmCannotOverwriteCommittedAttempt()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using ControlServerDbContext firstContext = new(options);
        await firstContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        WireToGateStore firstStore = new(firstContext);
        OrderIntent intent = await AcceptIntentAsync(firstStore);
        await using ControlServerDbContext staleContext = new(options);
        await staleContext.OrderIntents.SingleAsync(TestContext.Current.CancellationToken);
        WireToGateStore staleStore = new(staleContext);

        CreateDispatchAttempt committed = await firstStore.ArmCreateDispatchAsync(
            intent.UpperId,
            new string('a', 64),
            Now,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            staleStore.ArmCreateDispatchAsync(
                intent.UpperId,
                new string('b', 64),
                Now.AddMilliseconds(1),
                TestContext.Current.CancellationToken));

        await using ControlServerDbContext verificationContext = new(options);
        OrderIntentRow row = await verificationContext.OrderIntents
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(committed.AttemptId, row.CreateAttemptId);
        Assert.Equal(1, row.CreateAttemptCount);
        RiotDispatchAuditEventRow audit = await verificationContext.RiotDispatchAuditEvents
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(committed.AttemptId, audit.AttemptId);
    }

    [Fact]
    public async Task LegacyMigrationLeavesAuditUnknownAndConfirmedAbsenceNeverCreates()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using ControlServerDbContext context = new(options);
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(
            "20260827062706_PackageCapacityAndSafetyProjection",
            TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO OrderIntents
                (MovementLegId, DemandId, UpperId, Purpose, TargetStationId, VehicleKey,
                 MapId, DestinationStationId, AgvLifecycleGeneration, DispatchGeneration,
                 CreatedAt, Status, OrderId)
            VALUES
                ('LEG-LEGACY', 'D-LEGACY', 'UPPER-LEGACY', 'TO_PICKUP', 'ST-12', 'AGV-8005-01',
                 25, 12, 1, 1, '2026-08-28 08:00:00+00:00', 'PENDING_RECONCILIATION', NULL)
            """,
            TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        OrderIntentRow migrated = await context.OrderIntents.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Null(migrated.DispatchAuditVersion);
        Assert.Null(migrated.CreateAttemptCount);
        Assert.Null(migrated.CreateAttemptId);
        Assert.Null(migrated.LastCreateOutcome);
        Assert.Null(migrated.LastReconciliationOutcome);
        Assert.Empty(await context.RiotDispatchAuditEvents.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
            new WireToGateStore(context).ArmCreateDispatchAsync(
                "UPPER-LEGACY",
                new string('a', 64),
                Now,
                TestContext.Current.CancellationToken));
        ScriptedGateway gateway = new(
            [new RiotOrderObservation("UPPER-LEGACY", RiotOrderObservationKind.NotFound, null,
                Receipt: Receipt("RECONCILE", "NotFound", 404))],
            new RiotOrderObservation("UPPER-LEGACY", RiotOrderObservationKind.Active, "MUST-NOT-CREATE"));

        MovementDispatchResult result = await new MovementDispatchService(
                new WireToGateStore(context), gateway, new AdvancingTimeProvider(Now))
            .ReconcileOrCreateAsync("UPPER-LEGACY", TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.ResultUnknown, result.Outcome);
        Assert.Equal(0, gateway.CreateCount);
        OrderIntentRow after = await context.OrderIntents.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Null(after.DispatchAuditVersion);
        Assert.Null(after.CreateAttemptCount);
        Assert.Equal("RESULT_UNKNOWN", after.Status);
        Assert.Equal("LegacyAuditUnavailable", after.LastReconciliationOutcome);
    }

    [Fact]
    public async Task MigratedAuditSchemaRejectsDuplicateSequenceForMovementLeg()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using ControlServerDbContext context = new(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var entityType = context.Model.FindEntityType(typeof(RiotDispatchAuditEventRow));
        var sequenceIndex = entityType!.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(RiotDispatchAuditEventRow.MovementLegId), nameof(RiotDispatchAuditEventRow.Sequence)]));
        Assert.True(sequenceIndex.IsUnique);

        context.RiotDispatchAuditEvents.AddRange(
            AuditEvent("AUDIT-001", sequence: 1),
            AuditEvent("AUDIT-002", sequence: 1));

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            context.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AuditSequenceStartsAtOneIndependentlyForEachMovementLeg()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        OrderIntent first = await AcceptIntentAsync(store, suffix: "001");
        OrderIntent second = await AcceptIntentAsync(store, suffix: "002", vehicleKey: "AGV-8005-02");

        foreach (OrderIntent intent in new[] { first, second })
        {
            await store.RecordReconciliationAsync(
                intent.UpperId,
                new DispatchAuditWrite(
                    RiotDispatchAuditPhase.PreCreateReconciliation,
                    RiotDispatchAuditOutcome.NotFound,
                    Now,
                    Receipt: Receipt("RECONCILE", "NotFound", 404)),
                markResultUnknown: false,
                TestContext.Current.CancellationToken);
            await store.RecordReconciliationAsync(
                intent.UpperId,
                new DispatchAuditWrite(
                    RiotDispatchAuditPhase.PreCreateReconciliation,
                    RiotDispatchAuditOutcome.Unknown,
                    Now.AddMilliseconds(1),
                    Receipt: Receipt("RECONCILE", "Unknown")),
                markResultUnknown: true,
                TestContext.Current.CancellationToken);
        }

        Dictionary<string, long[]> sequences = await context.RiotDispatchAuditEvents
            .AsNoTracking()
            .GroupBy(item => item.MovementLegId)
            .ToDictionaryAsync(
                group => group.Key,
                group => group.OrderBy(item => item.Sequence).Select(item => item.Sequence).ToArray(),
                TestContext.Current.CancellationToken);
        Assert.Equal([1L, 2L], sequences[first.MovementLegId]);
        Assert.Equal([1L, 2L], sequences[second.MovementLegId]);
    }

    private static async Task<ControlServerDbContext> CreateContextAsync(SqliteConnection connection)
    {
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        ControlServerDbContext context = new(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return context;
    }

    private static async Task<OrderIntent> AcceptIntentAsync(
        WireToGateStore store,
        int destinationStationId = 12,
        string suffix = "001",
        string vehicleKey = "AGV-8005-01")
    {
        OrderIntent intent = new(
            $"LEG-{suffix}", $"D-{suffix}", $"UPPER-{suffix}", "TO_PICKUP", $"ST-{destinationStationId}", Now,
            vehicleKey, 25, destinationStationId, 1, 1);
        await store.AcceptWithOrderIntentAsync(
            new AcceptedDemandSnapshot(
                $"D-{suffix}",
                $"SUBLOT-{suffix}|WIRE_TO_GATE",
                1,
                $"history-{suffix}",
                1,
                Now),
            intent,
            TestContext.Current.CancellationToken);
        return intent;
    }

    private static RiotDispatchAuditEventRow AuditEvent(string auditEventId, long sequence) =>
        new()
        {
            AuditEventId = auditEventId,
            MovementLegId = "LEG-DUPLICATE-SEQUENCE",
            DemandId = "D-DUPLICATE-SEQUENCE",
            UpperId = "UPPER-DUPLICATE-SEQUENCE",
            DispatchGeneration = 1,
            Sequence = sequence,
            Phase = "CREATE_REQUEST",
            Outcome = "STARTED",
            OccurredAt = Now
        };

    private static RiotOrderObservation Observation(
        OrderIntent intent,
        RiotOrderObservationKind kind,
        string? orderId = null,
        RiotOrderCallReceipt? receipt = null) =>
        new(
            intent.UpperId,
            kind,
            orderId,
            kind == RiotOrderObservationKind.NotFound ? null : 3,
            intent.VehicleKey,
            intent.MapId,
            intent.DestinationStationId,
            receipt);

    private static RiotOrderCallReceipt Receipt(
        string operation,
        string classification,
        int? httpStatusCode = null,
        string? businessCode = null,
        bool? resultPresent = null,
        string? failureCategory = null) =>
        new(operation, classification, Now, httpStatusCode, businessCode, resultPresent, failureCategory);

    private sealed class ScriptedGateway(
        IEnumerable<RiotOrderObservation> reconciliations,
        RiotOrderObservation createResult,
        Func<Task>? beforeCreate = null) : IRiotMovementGateway
    {
        private readonly Queue<RiotOrderObservation> _reconciliations = new(reconciliations);

        public int CreateCount { get; private set; }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(
            string upperId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_reconciliations.Dequeue());
        }

        public async Task<RiotOrderObservation> CreateAsync(
            OrderIntent intent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            if (beforeCreate is not null)
            {
                await beforeCreate();
            }
            return createResult;
        }
    }

    private sealed class CancellingGateway(OrderIntent intent, CancellationTokenSource source) : IRiotMovementGateway
    {
        public int CreateCount { get; private set; }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(
            string upperId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Observation(
                intent,
                RiotOrderObservationKind.NotFound,
                receipt: Receipt("RECONCILE", "NotFound", 404)));

        public Task<RiotOrderObservation> CreateAsync(
            OrderIntent created,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            source.Cancel();
            return Task.FromCanceled<RiotOrderObservation>(cancellationToken);
        }
    }

    private sealed class PostCreateReadCancellingGateway(
        OrderIntent intent,
        CancellationTokenSource source) : IRiotMovementGateway
    {
        public int CreateCount { get; private set; }

        public int ReconcileCount { get; private set; }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(
            string upperId,
            CancellationToken cancellationToken)
        {
            ReconcileCount++;
            if (ReconcileCount == 1)
            {
                return Task.FromResult(Observation(
                    intent,
                    RiotOrderObservationKind.NotFound,
                    receipt: Receipt("RECONCILE", "NotFound", 404)));
            }

            source.Cancel();
            return Task.FromCanceled<RiotOrderObservation>(cancellationToken);
        }

        public Task<RiotOrderObservation> CreateAsync(
            OrderIntent created,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            return Task.FromResult(Observation(
                intent,
                RiotOrderObservationKind.Active,
                "UNTRUSTED-CREATE",
                Receipt("CREATE", "SdkAccepted", 200, "0", true)));
        }
    }

    private sealed class AdvancingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow()
        {
            DateTimeOffset current = _now;
            _now = _now.AddMilliseconds(1);
            return current;
        }
    }
}
