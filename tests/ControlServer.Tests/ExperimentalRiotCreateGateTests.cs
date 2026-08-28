using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// Covers the RIoT create-dispatch gate.
/// BC-ORDER-004 establishes that RIoT enforces upperId idempotency server-side: resubmitting a
/// held upperId returns business code 0610008 and never creates a second order. An exact
/// absent-at-observation read is therefore sufficient to dispatch a create, and no one-shot
/// permit is required to prove absence first. The experimental permit machinery is retained only
/// for the persistence invariants it still guarantees, and is no longer on the create path.
/// </summary>
public sealed class ExperimentalRiotCreateGateTests
{
    private const string EligibilityBasis = "EXPERIMENTAL_ABSENT_AT_OBSERVATION";
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExactAbsentAtObservationCreatesExactlyOnceWithoutAnyPermit()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        await using ControlServerDbContext context = database.CreateContext();
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        RiotOrderObservation confirmed = Observation(
            intent,
            RiotOrderObservationKind.Active,
            "ORDER-NEW",
            Receipt("RECONCILE", "Found", resultPresent: true));
        DelegateGateway gateway = new(
            (call, _, _) => Task.FromResult(call == 1 ? Absent(intent) : confirmed),
            (_, _, _) => Task.FromResult(Observation(
                intent,
                RiotOrderObservationKind.Active,
                "ORDER-NEW",
                Receipt("CREATE", "SdkAccepted", resultPresent: true))));

        MovementDispatchResult result = await new MovementDispatchService(
                store,
                gateway,
                new FixedTimeProvider(Now))
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.Confirmed, result.Outcome);
        Assert.Equal("ORDER-NEW", result.OrderId);
        Assert.Equal(1, gateway.CreateCount);
        OrderIntentRow row = await context.OrderIntents
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("CONFIRMED", row.Status);
        Assert.Equal(1, row.CreateAttemptCount);
        Assert.Null(row.ExperimentalCreateAuthorizationId);
        Assert.Empty(await context.Set<ExperimentalRiotCreateAuthorizationRow>()
            .AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        RiotDispatchAuditEventRow[] events = await context.RiotDispatchAuditEvents
            .AsNoTracking()
            .OrderBy(item => item.Sequence)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal("PRE_CREATE_RECONCILIATION", events[0].Phase);
        Assert.Equal("UNKNOWN", events[0].Outcome);
        Assert.Contains(events, item => item.Phase == "CREATE_DISPATCH" && item.Outcome == "ARMED");
        Assert.Equal("CONFIRMED", events[^1].Outcome);
    }

    [Fact]
    public async Task CreateRefusedAsAlreadyExistingReconcilesToTheOrderHoldingTheUpperId()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        await using ControlServerDbContext context = database.CreateContext();
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        RiotOrderObservation existing = Observation(
            intent,
            RiotOrderObservationKind.Active,
            "ORDER-EXISTING",
            Receipt("RECONCILE", "Found", resultPresent: true));
        DelegateGateway gateway = new(
            (call, _, _) => Task.FromResult(call == 1 ? Absent(intent) : existing),
            (_, _, _) => Task.FromResult(AlreadyExists(intent)));

        MovementDispatchResult result = await new MovementDispatchService(
                store,
                gateway,
                new FixedTimeProvider(Now))
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.Confirmed, result.Outcome);
        Assert.Equal("ORDER-EXISTING", result.OrderId);
        Assert.Equal(1, gateway.CreateCount);
        OrderIntentRow row = await context.OrderIntents
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("CONFIRMED", row.Status);
        Assert.Equal("ORDER-EXISTING", row.OrderId);
        RiotDispatchAuditEventRow[] events = await context.RiotDispatchAuditEvents
            .AsNoTracking()
            .OrderBy(item => item.Sequence)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Contains(events, item => item.BusinessCode == "0610008");
        Assert.Contains(events, item => item.Outcome == "ACCEPTED");
        Assert.Equal("CONFIRMED", events[^1].Outcome);
    }

    [Fact]
    public async Task AbsentAtObservationAfterAnAttemptNeverDispatchesASecondCreate()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        await using ControlServerDbContext context = database.CreateContext();
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        DelegateGateway gateway = new(
            (_, _, _) => Task.FromResult(Absent(intent)),
            (_, _, _) => Task.FromResult(CreateUnknown(intent)));
        MovementDispatchService service = new(store, gateway, new FixedTimeProvider(Now));

        MovementDispatchResult first = await service.ReconcileOrCreateAsync(
            intent.UpperId,
            TestContext.Current.CancellationToken);
        MovementDispatchResult second = await service.ReconcileOrCreateAsync(
            intent.UpperId,
            TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.ResultUnknown, first.Outcome);
        Assert.Equal(MovementDispatchOutcome.ResultUnknown, second.Outcome);
        Assert.Equal(1, gateway.CreateCount);
        OrderIntentRow row = await context.OrderIntents
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("RESULT_UNKNOWN", row.Status);
        Assert.Equal(1, row.CreateAttemptCount);
    }

    [Fact]
    public async Task ExactPermitPersistenceIsIdempotentButIdentityRebindingIsRejected()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        await using ControlServerDbContext context = database.CreateContext();
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        ExperimentalRiotCreateAuthorization exact = Authorization(intent);

        await store.PersistExperimentalCreateAuthorizationAsync(
            exact,
            Now,
            TestContext.Current.CancellationToken);
        await store.PersistExperimentalCreateAuthorizationAsync(
            exact,
            Now.AddSeconds(1),
            TestContext.Current.CancellationToken);

        ExperimentalRiotCreateAuthorizationRow persisted = Assert.Single(await context
            .Set<ExperimentalRiotCreateAuthorizationRow>()
            .AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(exact.AuthorizationId, persisted.AuthorizationId);
        Assert.Equal(1, persisted.AuthorizationVersion);
        Assert.Equal(exact.UpperId, persisted.UpperId);
        Assert.Equal(exact.DemandId, persisted.DemandId);
        Assert.Equal(exact.MovementLegId, persisted.MovementLegId);
        Assert.Equal(exact.AgvLifecycleGeneration, persisted.AgvLifecycleGeneration);
        Assert.Equal(exact.DispatchGeneration, persisted.DispatchGeneration);
        Assert.Equal(exact.ExpiresAt, persisted.ExpiresAt);
        Assert.Equal(Now, persisted.PersistedAt);
        OrderIntentRow reservedIntent = await context.OrderIntents
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(exact.AuthorizationId, reservedIntent.ExperimentalCreateAuthorizationId);

        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
            store.PersistExperimentalCreateAuthorizationAsync(
                exact with { DemandId = "D-REBIND" },
                Now.AddSeconds(2),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
            store.PersistExperimentalCreateAuthorizationAsync(
                exact with { AuthorizationId = "AUTH-REBIND" },
                Now.AddSeconds(2),
                TestContext.Current.CancellationToken));

        Assert.Single(await context.Set<ExperimentalRiotCreateAuthorizationRow>()
            .AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("HttpStatusCode")]
    [InlineData("BusinessCode")]
    public async Task ExperimentalArmRejectsNoncanonicalPersistedPreReceipt(string scenario)
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        await using ControlServerDbContext context = database.CreateContext();
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        ExperimentalRiotCreateAuthorization authorization = Authorization(intent);
        await store.PersistExperimentalCreateAuthorizationAsync(
            authorization,
            Now,
            TestContext.Current.CancellationToken);
        RiotOrderCallReceipt receipt = scenario switch
        {
            "HttpStatusCode" => Receipt(
                "RECONCILE",
                "AbsentAtObservation",
                httpStatusCode: 200,
                resultPresent: false),
            "BusinessCode" => Receipt(
                "RECONCILE",
                "AbsentAtObservation",
                businessCode: "0",
                resultPresent: false),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
        await store.RecordReconciliationAsync(
            intent.UpperId,
            new DispatchAuditWrite(
                RiotDispatchAuditPhase.PreCreateReconciliation,
                RiotDispatchAuditOutcome.Unknown,
                Now.AddMilliseconds(1),
                Receipt: receipt,
                ExperimentalAuthorizationId: authorization.AuthorizationId,
                EligibilityBasis: EligibilityBasis),
            markResultUnknown: true,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
            store.ArmExperimentalCreateDispatchAsync(
                intent.UpperId,
                new string('a', 64),
                authorization,
                EligibilityBasis,
                Now.AddMilliseconds(2),
                TestContext.Current.CancellationToken));

        OrderIntentRow row = await context.OrderIntents
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("RESULT_UNKNOWN", row.Status);
        Assert.Equal(0, row.CreateAttemptCount);
        Assert.Null(row.CreateAttemptId);
        Assert.Equal(authorization.AuthorizationId, row.ExperimentalCreateAuthorizationId);
        ExperimentalRiotCreateAuthorizationRow permit = await context
            .Set<ExperimentalRiotCreateAuthorizationRow>()
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Null(permit.ConsumedAt);
        Assert.Null(permit.ConsumedByAttemptId);
        RiotDispatchAuditEventRow audit = await context.RiotDispatchAuditEvents
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("PRE_CREATE_RECONCILIATION", audit.Phase);
        Assert.Equal("UNKNOWN", audit.Outcome);
        Assert.DoesNotContain(
            context.RiotDispatchAuditEvents,
            item => item.Phase == "CREATE_DISPATCH");
    }

    [Theory]
    [InlineData("MissingReceipt")]
    [InlineData("WrongOperation")]
    [InlineData("Indeterminate")]
    [InlineData("ResultPresentMissing")]
    [InlineData("ResultPresentTrue")]
    [InlineData("OrderIdPresent")]
    [InlineData("FailureCategoryPresent")]
    [InlineData("SdkFailure")]
    [InlineData("HttpStatusCodePresent")]
    [InlineData("BusinessCodePresent")]
    public async Task OnlyExactTypedAbsentReceiptCanDispatchACreate(string scenario)
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        await using ControlServerDbContext context = database.CreateContext();
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        RiotOrderObservation observation = scenario switch
        {
            "MissingReceipt" => new RiotOrderObservation(intent.UpperId, RiotOrderObservationKind.Unknown, null),
            "WrongOperation" => Unknown(
                intent,
                Receipt("CREATE", "AbsentAtObservation", resultPresent: false)),
            "Indeterminate" => Unknown(
                intent,
                Receipt("RECONCILE", "Indeterminate", resultPresent: true)),
            "ResultPresentMissing" => Unknown(
                intent,
                Receipt("RECONCILE", "AbsentAtObservation")),
            "ResultPresentTrue" => Unknown(
                intent,
                Receipt("RECONCILE", "AbsentAtObservation", resultPresent: true)),
            "OrderIdPresent" => new RiotOrderObservation(
                intent.UpperId,
                RiotOrderObservationKind.Unknown,
                "ORDER-MALFORMED",
                Receipt: Receipt("RECONCILE", "AbsentAtObservation", resultPresent: false)),
            "FailureCategoryPresent" => Unknown(
                intent,
                Receipt(
                    "RECONCILE",
                    "AbsentAtObservation",
                    resultPresent: false,
                    failureCategory: "TIMEOUT")),
            "SdkFailure" => Unknown(
                intent,
                Receipt("RECONCILE", "SdkFailure", failureCategory: "TRANSPORT_FAILURE")),
            "HttpStatusCodePresent" => Unknown(
                intent,
                Receipt(
                    "RECONCILE",
                    "AbsentAtObservation",
                    httpStatusCode: 200,
                    resultPresent: false)),
            "BusinessCodePresent" => Unknown(
                intent,
                Receipt(
                    "RECONCILE",
                    "AbsentAtObservation",
                    businessCode: "0",
                    resultPresent: false)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
        DelegateGateway gateway = GatewayFor(
            _ => observation,
            _ => CreateUnknown(intent));

        MovementDispatchResult result = await Service(
                store,
                gateway,
                new StaticAuthorizationSource(Authorization(intent)))
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.ResultUnknown, result.Outcome);
        Assert.Equal(0, gateway.CreateCount);
        ExperimentalRiotCreateAuthorizationRow permit = await context
            .Set<ExperimentalRiotCreateAuthorizationRow>()
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Null(permit.ConsumedAt);
        Assert.Null(permit.ConsumedByAttemptId);
        Assert.DoesNotContain(
            await context.RiotDispatchAuditEvents.AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken),
            item => item.Phase is "CREATE_DISPATCH" or "CREATE_REQUEST");
    }

    [Theory]
    [InlineData(RiotOrderObservationKind.Active, MovementDispatchOutcome.Confirmed)]
    [InlineData(RiotOrderObservationKind.Terminal, MovementDispatchOutcome.TerminalReconciliationRequired)]
    public async Task FoundAndTerminalObservationsNeverDispatchACreate(
        RiotOrderObservationKind kind,
        MovementDispatchOutcome expectedOutcome)
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        await using ControlServerDbContext context = database.CreateContext();
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        string classification = kind == RiotOrderObservationKind.Active ? "Found" : "Terminal";
        RiotOrderObservation found = Observation(
            intent,
            kind,
            "ORDER-EXISTING",
            Receipt("RECONCILE", classification, resultPresent: true));
        DelegateGateway gateway = GatewayFor(
            _ => found,
            _ => CreateUnknown(intent));

        MovementDispatchResult result = await Service(
                store,
                gateway,
                new StaticAuthorizationSource(Authorization(intent)))
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(0, gateway.CreateCount);
        ExperimentalRiotCreateAuthorizationRow permit = await context
            .Set<ExperimentalRiotCreateAuthorizationRow>()
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Null(permit.ConsumedAt);
        Assert.Null(permit.ConsumedByAttemptId);
        Assert.DoesNotContain(
            await context.RiotDispatchAuditEvents.AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken),
            item => item.Phase is "CREATE_DISPATCH" or "CREATE_REQUEST");
    }

    [Fact]
    public async Task ArmedAttemptWithoutStartedEventSurvivesRestartAndNeverCreates()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        OrderIntent intent;
        ExperimentalRiotCreateAuthorization authorization;
        CreateDispatchAttempt interruptedAttempt;
        await using (ControlServerDbContext interruptedContext = database.CreateContext())
        {
            WireToGateStore store = new(interruptedContext);
            intent = await AcceptIntentAsync(store);
            authorization = Authorization(intent);
            await store.PersistExperimentalCreateAuthorizationAsync(
                authorization,
                Now,
                TestContext.Current.CancellationToken);
            await store.RecordReconciliationAsync(
                intent.UpperId,
                new DispatchAuditWrite(
                    RiotDispatchAuditPhase.PreCreateReconciliation,
                    RiotDispatchAuditOutcome.Unknown,
                    Now,
                    Receipt: Receipt("RECONCILE", "AbsentAtObservation", resultPresent: false),
                    ExperimentalAuthorizationId: authorization.AuthorizationId,
                    EligibilityBasis: EligibilityBasis),
                markResultUnknown: true,
                TestContext.Current.CancellationToken);
            interruptedAttempt = await store.ArmExperimentalCreateDispatchAsync(
                intent.UpperId,
                new string('a', 64),
                authorization,
                EligibilityBasis,
                Now.AddMilliseconds(1),
                TestContext.Current.CancellationToken);
        }

        DelegateGateway restartGateway = GatewayFor(
            _ => Absent(intent),
            _ => CreateUnknown(intent));
        await using ControlServerDbContext restartContext = database.CreateContext();
        MovementDispatchResult restart = await new MovementDispatchService(
                new WireToGateStore(restartContext),
                restartGateway,
                new FixedTimeProvider(Now.AddMinutes(1)))
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.ResultUnknown, restart.Outcome);
        Assert.Equal(0, restartGateway.CreateCount);
        Assert.Equal(1, restartGateway.ReconcileCount);
        await using ControlServerDbContext verification = database.CreateContext();
        OrderIntentRow final = await verification.OrderIntents
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("RESULT_UNKNOWN", final.Status);
        Assert.Equal(1, final.CreateAttemptCount);
        Assert.Equal(interruptedAttempt.AttemptId, final.CreateAttemptId);
        RiotDispatchAuditEventRow[] events = await verification.RiotDispatchAuditEvents
            .AsNoTracking()
            .OrderBy(item => item.Sequence)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Collection(
            events,
            item => Assert.Equal((1L, "PRE_CREATE_RECONCILIATION", "UNKNOWN"),
                (item.Sequence, item.Phase, item.Outcome)),
            item => Assert.Equal((2L, "CREATE_DISPATCH", "ARMED"),
                (item.Sequence, item.Phase, item.Outcome)),
            item => Assert.Equal((3L, "POST_CREATE_RECONCILIATION", "UNKNOWN"),
                (item.Sequence, item.Phase, item.Outcome)));
        Assert.Null(events[0].AttemptId);
        Assert.Equal(interruptedAttempt.AttemptId, events[1].AttemptId);
        Assert.Equal(interruptedAttempt.AttemptId, events[2].AttemptId);
        Assert.DoesNotContain(events, item => item.Phase == "CREATE_REQUEST");
        ExperimentalRiotCreateAuthorizationRow permit = await verification
            .Set<ExperimentalRiotCreateAuthorizationRow>()
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(interruptedAttempt.AttemptId, permit.ConsumedByAttemptId);
        Assert.NotNull(permit.ConsumedAt);
    }

    [Fact]
    public async Task ClosedCreateDispatchGateRefusesAnExactAbsentCreateAndWritesNothing()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        await using ControlServerDbContext context = database.CreateContext();
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        DelegateGateway gateway = GatewayFor(_ => Absent(intent), _ => CreateUnknown(intent));
        OrderIntentRow before = await SnapshotAsync(context);
        int auditEventsBefore = await context.RiotDispatchAuditEvents
            .CountAsync(TestContext.Current.CancellationToken);

        MovementDispatchResult result = await GatedService(store, gateway, RiotCreateDispatchPolicy.Denied)
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.CreateDispatchDisabled, result.Outcome);
        Assert.Null(result.OrderId);
        Assert.Equal(0, gateway.CreateCount);
        Assert.Equal(1, gateway.ReconcileCount);
        OrderIntentRow after = await SnapshotAsync(context);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.CreateAttemptCount, after.CreateAttemptCount);
        Assert.Equal(before.CreateAttemptId, after.CreateAttemptId);
        Assert.Equal(before.DispatchAuditVersion, after.DispatchAuditVersion);
        Assert.Equal(before.DispatchAuditSequence, after.DispatchAuditSequence);
        Assert.Equal(
            auditEventsBefore,
            await context.RiotDispatchAuditEvents.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClosedCreateDispatchGateRefusesANotFoundCreate()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        await using ControlServerDbContext context = database.CreateContext();
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        RiotOrderObservation notFound = new(
            intent.UpperId,
            RiotOrderObservationKind.NotFound,
            null,
            Receipt: Receipt("RECONCILE", "NotFound", httpStatusCode: 404));
        DelegateGateway gateway = GatewayFor(_ => notFound, _ => CreateUnknown(intent));

        MovementDispatchResult result = await GatedService(store, gateway, RiotCreateDispatchPolicy.Denied)
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.CreateDispatchDisabled, result.Outcome);
        Assert.Equal(0, gateway.CreateCount);
        Assert.Equal("PENDING_RECONCILIATION", (await SnapshotAsync(context)).Status);
    }

    [Fact]
    public async Task ClosedCreateDispatchGateRefusesTheExperimentalPermitCreate()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        await using ControlServerDbContext context = database.CreateContext();
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        DelegateGateway gateway = GatewayFor(_ => Absent(intent), _ => CreateUnknown(intent));

        MovementDispatchResult result = await new MovementDispatchService(
                store,
                gateway,
                new FixedTimeProvider(Now),
                new StaticAuthorizationSource(Authorization(intent)),
                RiotCreateDispatchPolicy.Denied)
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.CreateDispatchDisabled, result.Outcome);
        Assert.Equal(0, gateway.CreateCount);
    }

    [Fact]
    public async Task RehearsalsUnderAClosedGateLeaveTheIntentEligibleForTheAuthorizedCreate()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        await using ControlServerDbContext context = database.CreateContext();
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        RiotOrderObservation confirmed = Observation(
            intent,
            RiotOrderObservationKind.Active,
            "ORDER-NEW",
            Receipt("RECONCILE", "Found", resultPresent: true));
        DelegateGateway gateway = new(
            (call, _, _) => Task.FromResult(call <= 3 ? Absent(intent) : confirmed),
            (_, _, _) => Task.FromResult(Observation(
                intent,
                RiotOrderObservationKind.Active,
                "ORDER-NEW",
                Receipt("CREATE", "SdkAccepted", resultPresent: true))));

        for (int rehearsal = 0; rehearsal < 2; rehearsal++)
        {
            MovementDispatchResult refused = await GatedService(store, gateway, RiotCreateDispatchPolicy.Denied)
                .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);
            Assert.Equal(MovementDispatchOutcome.CreateDispatchDisabled, refused.Outcome);
        }

        MovementDispatchResult authorized = await GatedService(store, gateway, RiotCreateDispatchPolicy.Allowed)
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.Confirmed, authorized.Outcome);
        Assert.Equal("ORDER-NEW", authorized.OrderId);
        Assert.Equal(1, gateway.CreateCount);
        OrderIntentRow row = await SnapshotAsync(context);
        Assert.Equal("CONFIRMED", row.Status);
        Assert.Equal(1, row.CreateAttemptCount);
    }

    private static MovementDispatchService GatedService(
        WireToGateStore store,
        IRiotMovementGateway gateway,
        RiotCreateDispatchPolicy policy) =>
        new(
            store,
            gateway,
            new FixedTimeProvider(Now),
            new StaticAuthorizationSource(null),
            policy);

    private static Task<OrderIntentRow> SnapshotAsync(ControlServerDbContext context) =>
        context.OrderIntents.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

    private static MovementDispatchService Service(
        WireToGateStore store,
        IRiotMovementGateway gateway,
        IExperimentalRiotCreateAuthorizationSource source) =>
        new(store, gateway, new FixedTimeProvider(Now), source);

    private static DelegateGateway GatewayFor(
        Func<int, RiotOrderObservation> reconcile,
        Func<int, RiotOrderObservation> create) =>
        new(
            (call, _, _) => Task.FromResult(reconcile(call)),
            (call, _, _) => Task.FromResult(create(call)));

    private static async Task<OrderIntent> AcceptIntentAsync(
        WireToGateStore store,
        string suffix = "001",
        int demandGeneration = 66)
    {
        OrderIntent intent = new(
            $"LEG-{suffix}",
            $"D-{suffix}",
            $"UPPER-{suffix}",
            "TO_PICKUP",
            "ST-12",
            Now,
            "AGV-8005-01",
            25,
            12,
            4,
            7);
        await store.AcceptWithOrderIntentAsync(
            new AcceptedDemandSnapshot(
                intent.DemandId,
                $"SUBLOT-{suffix}|WIRE_TO_GATE",
                1,
                $"history-{suffix}",
                1,
                Now,
                SeriesId: $"SERIES-{suffix}",
                WorkType: "WIRE_TO_GATE",
                Sublot: $"SUBLOT-{suffix}",
                Generation: demandGeneration,
                CreatedAt: Now.AddMinutes(-1),
                ValueObservedAt: Now.AddSeconds(-1),
                ValuePollTraceId: $"TRACE-{suffix}",
                ValueProjectionCommitId: $"COMMIT-{suffix}"),
            intent,
            TestContext.Current.CancellationToken);
        return intent;
    }

    private static ExperimentalRiotCreateAuthorization Authorization(OrderIntent intent) => new(
        AuthorizationId: $"AUTH-{intent.DemandId}",
        AuthorizationVersion: 1,
        UpperId: intent.UpperId,
        DemandId: intent.DemandId,
        MovementLegId: intent.MovementLegId,
        AgvLifecycleGeneration: intent.AgvLifecycleGeneration,
        DispatchGeneration: intent.DispatchGeneration,
        ExpiresAt: Now.AddMinutes(5));

    private static RiotOrderObservation Absent(OrderIntent intent) => Unknown(
        intent,
        Receipt("RECONCILE", "AbsentAtObservation", resultPresent: false));

    private static RiotOrderObservation AlreadyExists(OrderIntent intent) =>
        new(
            intent.UpperId,
            RiotOrderObservationKind.AlreadyExists,
            null,
            Receipt: Receipt(
                "CREATE",
                "OrderAlreadyExists",
                httpStatusCode: 200,
                businessCode: "0610008",
                resultPresent: false));

    private static RiotOrderObservation CreateUnknown(OrderIntent intent) => Unknown(
        intent,
        Receipt("CREATE", "SdkFailure", failureCategory: "TIMEOUT"));

    private static RiotOrderObservation Unknown(
        OrderIntent intent,
        RiotOrderCallReceipt? receipt) =>
        new(intent.UpperId, RiotOrderObservationKind.Unknown, null, Receipt: receipt);

    private static RiotOrderObservation Observation(
        OrderIntent intent,
        RiotOrderObservationKind kind,
        string orderId,
        RiotOrderCallReceipt receipt) =>
        new(
            intent.UpperId,
            kind,
            orderId,
            kind == RiotOrderObservationKind.Terminal ? 5 : 3,
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

    private sealed class StaticAuthorizationSource(
        ExperimentalRiotCreateAuthorization? authorization) : IExperimentalRiotCreateAuthorizationSource
    {
        public List<string> RequestedUpperIds { get; } = [];

        public Task<ExperimentalRiotCreateAuthorization?> GetAuthorizationAsync(
            string upperId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedUpperIds.Add(upperId);
            return Task.FromResult(authorization);
        }
    }

    private sealed class DelegateGateway(
        Func<int, string, CancellationToken, Task<RiotOrderObservation>> reconcile,
        Func<int, OrderIntent, CancellationToken, Task<RiotOrderObservation>> create) : IRiotMovementGateway
    {
        private int _createCount;
        private int _reconcileCount;

        public int CreateCount => Volatile.Read(ref _createCount);

        public int ReconcileCount => Volatile.Read(ref _reconcileCount);

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(
            string upperId,
            CancellationToken cancellationToken) =>
            reconcile(Interlocked.Increment(ref _reconcileCount), upperId, cancellationToken);

        public Task<RiotOrderObservation> CreateAsync(
            OrderIntent intent,
            CancellationToken cancellationToken) =>
            create(Interlocked.Increment(ref _createCount), intent, cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SharedDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _anchor;
        private readonly DbContextOptions<ControlServerDbContext> _options;

        private SharedDatabase(SqliteConnection anchor, DbContextOptions<ControlServerDbContext> options)
        {
            _anchor = anchor;
            _options = options;
        }

        public static async Task<SharedDatabase> CreateAsync()
        {
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = $"experimental-riot-create-{Guid.NewGuid():N}",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 30
            }.ToString();
            SqliteConnection anchor = new(connectionString);
            await anchor.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>()
                    .UseSqlite(connectionString)
                    .Options;
            await using ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new SharedDatabase(anchor, options);
        }

        public ControlServerDbContext CreateContext() => new(_options);

        public async ValueTask DisposeAsync() => await _anchor.DisposeAsync();
    }
}
