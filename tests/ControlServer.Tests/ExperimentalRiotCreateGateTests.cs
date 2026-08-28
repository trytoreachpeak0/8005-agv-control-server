using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ControlServer.Tests;

public sealed class ExperimentalRiotCreateGateTests
{
    private const string EligibilityBasis = "EXPERIMENTAL_ABSENT_AT_OBSERVATION";
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExistingConstructorDefaultsExperimentalAbsentGateOffEvenForPersistedPermit()
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
        DelegateGateway gateway = GatewayFor(
            _ => Absent(intent),
            _ => CreateUnknown(intent));

        MovementDispatchResult result = await new MovementDispatchService(
                store,
                gateway,
                new FixedTimeProvider(Now))
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.ResultUnknown, result.Outcome);
        Assert.Equal(0, gateway.CreateCount);
        ExperimentalRiotCreateAuthorizationRow permit = await context
            .Set<ExperimentalRiotCreateAuthorizationRow>()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Null(permit.ConsumedAt);
        Assert.Null(permit.ConsumedByAttemptId);
        RiotDispatchAuditEventRow audit = await context.RiotDispatchAuditEvents
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("PRE_CREATE_RECONCILIATION", audit.Phase);
        Assert.Equal("UNKNOWN", audit.Outcome);
        Assert.Equal("AbsentAtObservation", audit.ReceiptClassification);
        Assert.Null(audit.ExperimentalAuthorizationId);
        Assert.Null(audit.EligibilityBasis);
        Assert.DoesNotContain(
            context.RiotDispatchAuditEvents,
            item => item.Phase is "CREATE_DISPATCH" or "CREATE_REQUEST");
    }

    [Fact]
    public async Task ExactPermitIsPersistedBeforeReadThenUnknownArmAndStartAreDurableBeforeSingleCreate()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        await using ControlServerDbContext context = database.CreateContext();
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        ExperimentalRiotCreateAuthorization authorization = Authorization(intent);
        bool permitWasDurableBeforeRead = false;
        bool dispatchWasDurableBeforeCreate = false;
        DelegateGateway gateway = new(
            async (_, _, cancellationToken) =>
            {
                ExperimentalRiotCreateAuthorizationRow permit = await context
                    .Set<ExperimentalRiotCreateAuthorizationRow>()
                    .AsNoTracking()
                    .SingleAsync(cancellationToken);
                permitWasDurableBeforeRead = permit.AuthorizationId == authorization.AuthorizationId &&
                                             permit.AuthorizationVersion == 1 &&
                                             permit.UpperId == intent.UpperId &&
                                             permit.DemandId == intent.DemandId &&
                                             permit.MovementLegId == intent.MovementLegId &&
                                             permit.AgvLifecycleGeneration == intent.AgvLifecycleGeneration &&
                                             permit.DispatchGeneration == intent.DispatchGeneration &&
                                             permit.ExpiresAt == authorization.ExpiresAt &&
                                             permit.ConsumedAt is null &&
                                             permit.ConsumedByAttemptId is null;
                return Absent(intent);
            },
            async (_, _, cancellationToken) =>
            {
                RiotDispatchAuditEventRow[] beforeCreate = await context.RiotDispatchAuditEvents
                    .AsNoTracking()
                    .OrderBy(item => item.Sequence)
                    .ToArrayAsync(cancellationToken);
                ExperimentalRiotCreateAuthorizationRow permit = await context
                    .Set<ExperimentalRiotCreateAuthorizationRow>()
                    .AsNoTracking()
                    .SingleAsync(cancellationToken);
                dispatchWasDurableBeforeCreate = beforeCreate.Length == 3 &&
                                                  beforeCreate[0].Phase == "PRE_CREATE_RECONCILIATION" &&
                                                  beforeCreate[0].Outcome == "UNKNOWN" &&
                                                  beforeCreate[1].Phase == "CREATE_DISPATCH" &&
                                                  beforeCreate[1].Outcome == "ARMED" &&
                                                  beforeCreate[2].Phase == "CREATE_REQUEST" &&
                                                  beforeCreate[2].Outcome == "STARTED" &&
                                                  beforeCreate.All(item =>
                                                      item.ExperimentalAuthorizationId == authorization.AuthorizationId &&
                                                      item.EligibilityBasis == EligibilityBasis) &&
                                                  permit.ConsumedAt is not null &&
                                                  permit.ConsumedByAttemptId == beforeCreate[1].AttemptId &&
                                                  beforeCreate[1].AttemptId == beforeCreate[2].AttemptId;
                return CreateUnknown(intent);
            });
        StaticAuthorizationSource source = new(authorization);

        MovementDispatchResult result = await Service(store, gateway, source)
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.ResultUnknown, result.Outcome);
        Assert.True(permitWasDurableBeforeRead);
        Assert.True(dispatchWasDurableBeforeCreate);
        Assert.Equal([intent.UpperId], source.RequestedUpperIds);
        Assert.Equal(1, gateway.ReconcileCount);
        Assert.Equal(1, gateway.CreateCount);
        RiotDispatchAuditEventRow[] events = await context.RiotDispatchAuditEvents
            .AsNoTracking()
            .OrderBy(item => item.Sequence)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Collection(
            events,
            item =>
            {
                Assert.Equal("PRE_CREATE_RECONCILIATION", item.Phase);
                Assert.Equal("UNKNOWN", item.Outcome);
                Assert.Equal("RECONCILE", item.ReceiptOperation);
                Assert.Equal("AbsentAtObservation", item.ReceiptClassification);
                Assert.False(item.ResultPresent);
                Assert.Null(item.ReturnedOrderId);
                Assert.Null(item.FailureCategory);
                Assert.Equal(authorization.AuthorizationId, item.ExperimentalAuthorizationId);
                Assert.Equal(EligibilityBasis, item.EligibilityBasis);
            },
            item => Assert.Equal(("CREATE_DISPATCH", "ARMED"), (item.Phase, item.Outcome)),
            item => Assert.Equal(("CREATE_REQUEST", "STARTED"), (item.Phase, item.Outcome)),
            item => Assert.Equal(("CREATE_RESPONSE", "UNKNOWN"), (item.Phase, item.Outcome)));
        Assert.DoesNotContain(events, item => item.Outcome == "NOT_FOUND");
        Assert.Single(events.Select(item => item.AttemptId).Where(id => id is not null).Distinct());
    }

    [Fact]
    public async Task PermitExpiringAfterArmBeforeStartStaysConsumedAndNeverInvokesCreate()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        await using ControlServerDbContext context = database.CreateContext();
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        DateTimeOffset expiresAt = Now.AddMilliseconds(4);
        ExperimentalRiotCreateAuthorization authorization = Authorization(intent) with
        {
            ExpiresAt = expiresAt
        };
        SequenceTimeProvider clock = new(
            Now,
            Now.AddMilliseconds(1),
            Now.AddMilliseconds(3),
            expiresAt);
        DelegateGateway gateway = GatewayFor(
            _ => Absent(intent),
            _ => CreateUnknown(intent));

        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() =>
            new MovementDispatchService(
                    store,
                    gateway,
                    clock,
                    new StaticAuthorizationSource(authorization))
                .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken));

        Assert.Equal(0, gateway.CreateCount);
        Assert.Equal(4, clock.ReadCount);
        OrderIntentRow row = await context.OrderIntents
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("CREATE_ATTEMPTED", row.Status);
        Assert.Equal(1, row.CreateAttemptCount);
        Assert.NotNull(row.CreateAttemptId);
        ExperimentalRiotCreateAuthorizationRow permit = await context
            .Set<ExperimentalRiotCreateAuthorizationRow>()
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(row.CreateAttemptId, permit.ConsumedByAttemptId);
        Assert.Equal(Now.AddMilliseconds(3), permit.ConsumedAt);
        RiotDispatchAuditEventRow[] events = await context.RiotDispatchAuditEvents
            .AsNoTracking()
            .OrderBy(item => item.Sequence)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Collection(
            events,
            item => Assert.Equal(("PRE_CREATE_RECONCILIATION", "UNKNOWN"), (item.Phase, item.Outcome)),
            item => Assert.Equal(("CREATE_DISPATCH", "ARMED"), (item.Phase, item.Outcome)));
        Assert.DoesNotContain(events, item => item.Phase == "CREATE_REQUEST");
        Assert.All(events, item =>
        {
            Assert.Equal(authorization.AuthorizationId, item.ExperimentalAuthorizationId);
            Assert.Equal(EligibilityBasis, item.EligibilityBasis);
        });
    }

    [Fact]
    public async Task ProcessLossAfterExperimentalPreBeforeArmMarksUnknownAndDisabledRestartNeverCreates()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        OrderIntent intent;
        ExperimentalRiotCreateAuthorization authorization;
        DelegateGateway firstGateway;
        ThrowOnReadTimeProvider interruptedClock = new(Now, throwOnRead: 3);
        await using (ControlServerDbContext firstContext = database.CreateContext())
        {
            WireToGateStore firstStore = new(firstContext);
            intent = await AcceptIntentAsync(firstStore);
            authorization = Authorization(intent);
            firstGateway = GatewayFor(
                _ => Absent(intent),
                _ => CreateUnknown(intent));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new MovementDispatchService(
                        firstStore,
                        firstGateway,
                        interruptedClock,
                        new StaticAuthorizationSource(authorization))
                    .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken));

            Assert.Equal(0, firstGateway.CreateCount);
            Assert.Equal(3, interruptedClock.ReadCount);
            OrderIntentRow interrupted = await firstContext.OrderIntents
                .AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal("RESULT_UNKNOWN", interrupted.Status);
            Assert.Equal(0, interrupted.CreateAttemptCount);
            Assert.Null(interrupted.CreateAttemptId);
            Assert.Equal(authorization.AuthorizationId, interrupted.ExperimentalCreateAuthorizationId);
            ExperimentalRiotCreateAuthorizationRow permit = await firstContext
                .Set<ExperimentalRiotCreateAuthorizationRow>()
                .AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Null(permit.ConsumedAt);
            Assert.Null(permit.ConsumedByAttemptId);
            RiotDispatchAuditEventRow pre = await firstContext.RiotDispatchAuditEvents
                .AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal("PRE_CREATE_RECONCILIATION", pre.Phase);
            Assert.Equal("UNKNOWN", pre.Outcome);
            Assert.Equal(authorization.AuthorizationId, pre.ExperimentalAuthorizationId);
            Assert.Equal(EligibilityBasis, pre.EligibilityBasis);
        }

        RiotOrderObservation ordinaryNotFound = new(
            intent.UpperId,
            RiotOrderObservationKind.NotFound,
            null,
            Receipt: Receipt("RECONCILE", "NotFound", httpStatusCode: 404, resultPresent: false));
        DelegateGateway restartGateway = GatewayFor(
            _ => ordinaryNotFound,
            _ => CreateUnknown(intent));
        await using ControlServerDbContext restartContext = database.CreateContext();

        MovementDispatchResult restart = await new MovementDispatchService(
                new WireToGateStore(restartContext),
                restartGateway,
                new FixedTimeProvider(Now.AddMinutes(1)))
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.ResultUnknown, restart.Outcome);
        Assert.Equal(0, restartGateway.CreateCount);
        OrderIntentRow final = await restartContext.OrderIntents
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("RESULT_UNKNOWN", final.Status);
        Assert.Equal(0, final.CreateAttemptCount);
        Assert.Null(final.CreateAttemptId);
        Assert.Equal(authorization.AuthorizationId, final.ExperimentalCreateAuthorizationId);
        RiotDispatchAuditEventRow[] finalEvents = await restartContext.RiotDispatchAuditEvents
            .AsNoTracking()
            .OrderBy(item => item.Sequence)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Single(finalEvents, item =>
            item.Phase == "PRE_CREATE_RECONCILIATION" && item.Outcome == "UNKNOWN");
        Assert.DoesNotContain(finalEvents, item => item.Phase is "CREATE_DISPATCH" or "CREATE_REQUEST");
        ExperimentalRiotCreateAuthorizationRow finalPermit = await restartContext
            .Set<ExperimentalRiotCreateAuthorizationRow>()
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Null(finalPermit.ConsumedAt);
        Assert.Null(finalPermit.ConsumedByAttemptId);
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

    [Fact]
    public async Task UnauthorizedFreshIntentCannotUseExperimentalAbsentGate()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        await using ControlServerDbContext context = database.CreateContext();
        WireToGateStore store = new(context);
        OrderIntent intent = await AcceptIntentAsync(store);
        DelegateGateway gateway = GatewayFor(
            _ => Absent(intent),
            _ => CreateUnknown(intent));

        MovementDispatchResult result = await Service(
                store,
                gateway,
                new StaticAuthorizationSource(null))
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.ResultUnknown, result.Outcome);
        Assert.Equal(0, gateway.CreateCount);
        Assert.Empty(await context.Set<ExperimentalRiotCreateAuthorizationRow>()
            .AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        RiotDispatchAuditEventRow audit = await context.RiotDispatchAuditEvents
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("UNKNOWN", audit.Outcome);
        Assert.Null(audit.ExperimentalAuthorizationId);
        Assert.Null(audit.EligibilityBasis);
    }

    [Theory]
    [InlineData("UpperIdMismatch")]
    [InlineData("DemandIdMismatch")]
    [InlineData("MovementLegIdMismatch")]
    [InlineData("LifecycleGenerationMismatch")]
    [InlineData("DispatchGenerationMismatch")]
    [InlineData("AuthorizationVersionMismatch")]
    [InlineData("ExpiresAtDecisionTime")]
    [InlineData("ExpiredBeforeDecisionTime")]
    [InlineData("LegacyNullableAudit")]
    [InlineData("HistoricalDemandGeneration65")]
    [InlineData("PriorAuditHistory")]
    [InlineData("PriorAttempt")]
    public async Task PermitIdentityExpiryOrFreshnessMismatchCannotUseExperimentalAbsentGate(string scenario)
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        await using ControlServerDbContext context = database.CreateContext();
        WireToGateStore store = new(context);
        int demandGeneration = scenario == "HistoricalDemandGeneration65" ? 65 : 66;
        OrderIntent intent = await AcceptIntentAsync(store, demandGeneration: demandGeneration);
        ExperimentalRiotCreateAuthorization authorization = scenario switch
        {
            "UpperIdMismatch" => Authorization(intent) with { UpperId = "UPPER-OTHER" },
            "DemandIdMismatch" => Authorization(intent) with { DemandId = "D-OTHER" },
            "MovementLegIdMismatch" => Authorization(intent) with { MovementLegId = "LEG-OTHER" },
            "LifecycleGenerationMismatch" => Authorization(intent) with
            {
                AgvLifecycleGeneration = intent.AgvLifecycleGeneration + 1
            },
            "DispatchGenerationMismatch" => Authorization(intent) with
            {
                DispatchGeneration = intent.DispatchGeneration + 1
            },
            "AuthorizationVersionMismatch" => Authorization(intent) with { AuthorizationVersion = 2 },
            "ExpiresAtDecisionTime" => Authorization(intent) with { ExpiresAt = Now },
            "ExpiredBeforeDecisionTime" => Authorization(intent) with { ExpiresAt = Now.AddTicks(-1) },
            _ => Authorization(intent)
        };
        if (scenario == "LegacyNullableAudit")
        {
            OrderIntentRow row = await context.OrderIntents.SingleAsync(TestContext.Current.CancellationToken);
            row.DispatchAuditVersion = null;
            row.CreateAttemptCount = null;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        else if (scenario == "PriorAuditHistory")
        {
            await store.RecordReconciliationAsync(
                intent.UpperId,
                new DispatchAuditWrite(
                    RiotDispatchAuditPhase.PreCreateReconciliation,
                    RiotDispatchAuditOutcome.Unknown,
                    Now.AddMinutes(-1),
                    Receipt: Receipt("RECONCILE", "Indeterminate", resultPresent: true)),
                markResultUnknown: false,
                TestContext.Current.CancellationToken);
        }
        else if (scenario == "PriorAttempt")
        {
            OrderIntentRow row = await context.OrderIntents.SingleAsync(TestContext.Current.CancellationToken);
            row.Status = "CREATE_ATTEMPTED";
            row.CreateAttemptCount = 1;
            row.CreateAttemptId = "ATTEMPT-HISTORICAL";
            row.CreateDispatchArmedAt = Now.AddMinutes(-1);
            context.RiotDispatchAuditEvents.Add(AuditEvent(
                intent,
                sequence: 1,
                phase: "CREATE_DISPATCH",
                outcome: "ARMED",
                attemptId: row.CreateAttemptId));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        int armEventsBefore = await context.RiotDispatchAuditEvents.CountAsync(
            item => item.Phase == "CREATE_DISPATCH",
            TestContext.Current.CancellationToken);
        int startEventsBefore = await context.RiotDispatchAuditEvents.CountAsync(
            item => item.Phase == "CREATE_REQUEST",
            TestContext.Current.CancellationToken);
        DelegateGateway gateway = GatewayFor(
            _ => Absent(intent),
            _ => CreateUnknown(intent));

        _ = await Record.ExceptionAsync(() => Service(
                store,
                gateway,
                new StaticAuthorizationSource(authorization))
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken));

        Assert.Equal(0, gateway.CreateCount);
        Assert.Equal(
            armEventsBefore,
            await context.RiotDispatchAuditEvents.CountAsync(
                item => item.Phase == "CREATE_DISPATCH",
                TestContext.Current.CancellationToken));
        Assert.Equal(
            startEventsBefore,
            await context.RiotDispatchAuditEvents.CountAsync(
                item => item.Phase == "CREATE_REQUEST",
                TestContext.Current.CancellationToken));
        Assert.Empty(await context.Set<ExperimentalRiotCreateAuthorizationRow>()
            .AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
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
    public async Task OnlyExactTypedAbsentReceiptCanUseExperimentalGate(string scenario)
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
    public async Task FoundAndTerminalObservationsNeverUseExperimentalCreatePath(
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
    public async Task ConcurrentCallersConsumeOnePermitAndInvokeCreateAtMostOnce()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        OrderIntent intent;
        await using (ControlServerDbContext seedContext = database.CreateContext())
        {
            intent = await AcceptIntentAsync(new WireToGateStore(seedContext));
        }
        ExperimentalRiotCreateAuthorization authorization = Authorization(intent);
        CoordinatedAuthorizationSource source = new(authorization, expectedCallers: 2);
        DelegateGateway gateway = GatewayFor(
            _ => Absent(intent),
            _ => CreateUnknown(intent));
        await using ControlServerDbContext firstContext = database.CreateContext();
        await using ControlServerDbContext secondContext = database.CreateContext();
        Task<MovementDispatchResult> first = Service(new WireToGateStore(firstContext), gateway, source)
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);
        Task<MovementDispatchResult> second = Service(new WireToGateStore(secondContext), gateway, source)
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        _ = await Record.ExceptionAsync(() => Task.WhenAll(first, second));

        Assert.Equal(1, gateway.CreateCount);
        await using ControlServerDbContext verification = database.CreateContext();
        OrderIntentRow row = await verification.OrderIntents
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, row.CreateAttemptCount);
        Assert.NotNull(row.CreateAttemptId);
        ExperimentalRiotCreateAuthorizationRow permit = await verification
            .Set<ExperimentalRiotCreateAuthorizationRow>()
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(row.CreateAttemptId, permit.ConsumedByAttemptId);
        Assert.NotNull(permit.ConsumedAt);
        Assert.Single(await verification.RiotDispatchAuditEvents
            .AsNoTracking()
            .Where(item => item.Phase == "CREATE_REQUEST")
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LosingDuplicatePermitCallerCannotAppendLatePreAfterWinnerCompletesCreateResponse()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        OrderIntent intent;
        await using (ControlServerDbContext seedContext = database.CreateContext())
        {
            intent = await AcceptIntentAsync(new WireToGateStore(seedContext));
        }
        ExperimentalRiotCreateAuthorization authorization = Authorization(intent);
        CoordinatedAuthorizationSource source = new(authorization, expectedCallers: 2);
        AuthorizationPersistRace race = new();
        await using ControlServerDbContext winningContext = database.CreateContext(
            new AuthorizationPersistRaceInterceptor(race, isWinner: true));
        await using ControlServerDbContext losingContext = database.CreateContext(
            new AuthorizationPersistRaceInterceptor(race, isWinner: false));
        TaskCompletionSource loserReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseLoserRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DelegateGateway winnerGateway = new(
            async (_, _, cancellationToken) =>
            {
                await loserReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                return Absent(intent);
            },
            (_, _, _) => Task.FromResult(CreateUnknown(intent)));
        DelegateGateway loserGateway = new(
            async (_, _, cancellationToken) =>
            {
                loserReadStarted.TrySetResult();
                await releaseLoserRead.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                return Absent(intent);
            },
            (_, _, _) => Task.FromResult(CreateUnknown(intent)));

        Task<MovementDispatchResult> winner = Service(
                new WireToGateStore(winningContext),
                winnerGateway,
                source)
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);
        Task<MovementDispatchResult> loser = Service(
                new WireToGateStore(losingContext),
                loserGateway,
                source)
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        await loserReadStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        MovementDispatchResult winningResult = await winner;
        releaseLoserRead.TrySetResult();
        Exception losingFailure = await Assert.ThrowsAnyAsync<Exception>(() => loser);

        Assert.Equal(MovementDispatchOutcome.ResultUnknown, winningResult.Outcome);
        Assert.True(
            losingFailure is DbUpdateConcurrencyException or BusinessIdentityConflictException,
            $"Unexpected loser failure: {losingFailure.GetType().FullName}");
        Assert.Equal(1, winnerGateway.CreateCount);
        Assert.Equal(0, loserGateway.CreateCount);
        Assert.Equal(1, winnerGateway.ReconcileCount);
        Assert.Equal(1, loserGateway.ReconcileCount);
        await using ControlServerDbContext verification = database.CreateContext();
        OrderIntentRow row = await verification.OrderIntents
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("RESULT_UNKNOWN", row.Status);
        Assert.Equal(1, row.CreateAttemptCount);
        Assert.NotNull(row.CreateAttemptId);
        Assert.Equal(4L, row.DispatchAuditSequence);
        Assert.Equal("CreateResponseUnknown", row.LastCreateOutcome);
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
            item => Assert.Equal((3L, "CREATE_REQUEST", "STARTED"),
                (item.Sequence, item.Phase, item.Outcome)),
            item => Assert.Equal((4L, "CREATE_RESPONSE", "UNKNOWN"),
                (item.Sequence, item.Phase, item.Outcome)));
        Assert.Single(events, item => item.Phase == "PRE_CREATE_RECONCILIATION");
        Assert.Single(events, item => item.Phase == "CREATE_RESPONSE");
        ExperimentalRiotCreateAuthorizationRow permit = await verification
            .Set<ExperimentalRiotCreateAuthorizationRow>()
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(row.CreateAttemptId, permit.ConsumedByAttemptId);
        Assert.NotNull(permit.ConsumedAt);
    }

    [Fact]
    public async Task ArmedPermitWithoutStartedEventSurvivesRestartAndNeverCreates()
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
    public async Task CallerCancellationAfterStartConsumesPermitAndRestartNeverRetries()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        OrderIntent intent;
        ExperimentalRiotCreateAuthorization authorization;
        using CancellationTokenSource source = new();
        DelegateGateway firstGateway;
        await using (ControlServerDbContext firstContext = database.CreateContext())
        {
            WireToGateStore firstStore = new(firstContext);
            intent = await AcceptIntentAsync(firstStore);
            authorization = Authorization(intent);
            firstGateway = new DelegateGateway(
                (_, _, _) => Task.FromResult(Absent(intent)),
                (_, _, cancellationToken) =>
                {
                    source.Cancel();
                    return Task.FromCanceled<RiotOrderObservation>(cancellationToken);
                });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(
                    firstStore,
                    firstGateway,
                    new StaticAuthorizationSource(authorization))
                .ReconcileOrCreateAsync(intent.UpperId, source.Token));
        }

        Assert.Equal(1, firstGateway.CreateCount);
        await AssertConsumedUnknownAndRestartDoesNotCreateAsync(
            database,
            intent,
            authorization,
            expectedUnknownPhase: "CREATE_RESPONSE");
    }

    [Fact]
    public async Task UnexpectedCreateFailureAfterStartConsumesPermitAndRestartNeverRetries()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        OrderIntent intent;
        ExperimentalRiotCreateAuthorization authorization;
        DelegateGateway firstGateway;
        await using (ControlServerDbContext firstContext = database.CreateContext())
        {
            WireToGateStore firstStore = new(firstContext);
            intent = await AcceptIntentAsync(firstStore);
            authorization = Authorization(intent);
            firstGateway = new DelegateGateway(
                (_, _, _) => Task.FromResult(Absent(intent)),
                (_, _, _) => Task.FromException<RiotOrderObservation>(
                    new InvalidOperationException("simulated gateway crash")));

            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(
                    firstStore,
                    firstGateway,
                    new StaticAuthorizationSource(authorization))
                .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, firstGateway.CreateCount);
        await AssertConsumedUnknownAndRestartDoesNotCreateAsync(
            database,
            intent,
            authorization,
            expectedUnknownPhase: "CREATE_RESPONSE");
    }

    [Fact]
    public async Task PostCreateAbsenceConsumesPermitAndRestartNeverRetries()
    {
        await using SharedDatabase database = await SharedDatabase.CreateAsync();
        OrderIntent intent;
        ExperimentalRiotCreateAuthorization authorization;
        DelegateGateway firstGateway;
        await using (ControlServerDbContext firstContext = database.CreateContext())
        {
            WireToGateStore firstStore = new(firstContext);
            intent = await AcceptIntentAsync(firstStore);
            authorization = Authorization(intent);
            firstGateway = new DelegateGateway(
                (call, _, _) => Task.FromResult(call == 1
                    ? Absent(intent)
                    : Absent(intent)),
                (_, _, _) => Task.FromResult(Observation(
                    intent,
                    RiotOrderObservationKind.Active,
                    "ORDER-UNCONFIRMED",
                    Receipt("CREATE", "SdkAccepted", resultPresent: true))));

            MovementDispatchResult first = await Service(
                    firstStore,
                    firstGateway,
                    new StaticAuthorizationSource(authorization))
                .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);
            Assert.Equal(MovementDispatchOutcome.ResultUnknown, first.Outcome);
        }

        Assert.Equal(1, firstGateway.CreateCount);
        Assert.Equal(2, firstGateway.ReconcileCount);
        await AssertConsumedUnknownAndRestartDoesNotCreateAsync(
            database,
            intent,
            authorization,
            expectedUnknownPhase: "POST_CREATE_RECONCILIATION");
    }

    private static async Task AssertConsumedUnknownAndRestartDoesNotCreateAsync(
        SharedDatabase database,
        OrderIntent intent,
        ExperimentalRiotCreateAuthorization authorization,
        string expectedUnknownPhase)
    {
        await using (ControlServerDbContext verification = database.CreateContext())
        {
            OrderIntentRow row = await verification.OrderIntents
                .AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, row.CreateAttemptCount);
            Assert.Equal("RESULT_UNKNOWN", row.Status);
            ExperimentalRiotCreateAuthorizationRow permit = await verification
                .Set<ExperimentalRiotCreateAuthorizationRow>()
                .AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(permit.ConsumedAt);
            Assert.Equal(row.CreateAttemptId, permit.ConsumedByAttemptId);
            RiotDispatchAuditEventRow unknown = await verification.RiotDispatchAuditEvents
                .AsNoTracking()
                .SingleAsync(
                    item => item.Phase == expectedUnknownPhase && item.Outcome == "UNKNOWN",
                    TestContext.Current.CancellationToken);
            Assert.Equal(row.CreateAttemptId, unknown.AttemptId);
        }

        DelegateGateway restartGateway = GatewayFor(
            _ => Absent(intent),
            _ => CreateUnknown(intent));
        await using ControlServerDbContext restartContext = database.CreateContext();
        _ = await Record.ExceptionAsync(() => Service(
                new WireToGateStore(restartContext),
                restartGateway,
                new StaticAuthorizationSource(authorization))
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken));

        Assert.Equal(0, restartGateway.CreateCount);
        await using ControlServerDbContext final = database.CreateContext();
        OrderIntentRow finalRow = await final.OrderIntents
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, finalRow.CreateAttemptCount);
        Assert.Single(await final.RiotDispatchAuditEvents
            .AsNoTracking()
            .Where(item => item.Phase == "CREATE_REQUEST")
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

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

    private static RiotDispatchAuditEventRow AuditEvent(
        OrderIntent intent,
        long sequence,
        string phase,
        string outcome,
        string? attemptId = null) =>
        new()
        {
            AuditEventId = Guid.NewGuid().ToString("D"),
            MovementLegId = intent.MovementLegId,
            DemandId = intent.DemandId,
            UpperId = intent.UpperId,
            DispatchGeneration = intent.DispatchGeneration,
            Sequence = sequence,
            Phase = phase,
            Outcome = outcome,
            AttemptId = attemptId,
            OccurredAt = Now.AddMinutes(-1)
        };

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

    private sealed class CoordinatedAuthorizationSource(
        ExperimentalRiotCreateAuthorization authorization,
        int expectedCallers) : IExperimentalRiotCreateAuthorizationSource
    {
        private readonly TaskCompletionSource _allCallers = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public async Task<ExperimentalRiotCreateAuthorization?> GetAuthorizationAsync(
            string upperId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(authorization.UpperId, upperId);
            if (Interlocked.Increment(ref _callCount) == expectedCallers)
            {
                _allCallers.TrySetResult();
            }
            await _allCallers.Task.WaitAsync(cancellationToken);
            return authorization;
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

    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private int _index;

        public int ReadCount => Volatile.Read(ref _index);

        public override DateTimeOffset GetUtcNow()
        {
            int index = Interlocked.Increment(ref _index) - 1;
            return index < values.Length
                ? values[index]
                : throw new InvalidOperationException("The experimental decision read the clock unexpectedly.");
        }
    }

    private sealed class ThrowOnReadTimeProvider(DateTimeOffset value, int throwOnRead) : TimeProvider
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public override DateTimeOffset GetUtcNow()
        {
            int read = Interlocked.Increment(ref _readCount);
            return read == throwOnRead
                ? throw new InvalidOperationException("Simulated process loss before experimental ARM.")
                : value.AddMilliseconds(read - 1);
        }
    }

    private sealed class AuthorizationPersistRace
    {
        public TaskCompletionSource LoserReachedSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WinnerCommitted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AuthorizationPersistRaceInterceptor(
        AuthorizationPersistRace race,
        bool isWinner) : SaveChangesInterceptor
    {
        private bool _authorizationSave;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _authorizationSave = eventData.Context?.ChangeTracker
                .Entries<ExperimentalRiotCreateAuthorizationRow>()
                .Any(entry => entry.State == EntityState.Added) == true;
            if (!_authorizationSave)
            {
                return result;
            }

            if (isWinner)
            {
                await race.LoserReachedSave.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            else
            {
                race.LoserReachedSave.TrySetResult();
                await race.WinnerCommitted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            _ = eventData;
            _ = cancellationToken;
            if (_authorizationSave && isWinner)
            {
                race.WinnerCommitted.TrySetResult();
            }
            _authorizationSave = false;
            return ValueTask.FromResult(result);
        }
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

        public ControlServerDbContext CreateContext(params IInterceptor[] interceptors)
        {
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>()
                    .UseSqlite(_anchor.ConnectionString)
                    .AddInterceptors(interceptors)
                    .Options;
            return new ControlServerDbContext(options);
        }

        public async ValueTask DisposeAsync() => await _anchor.DisposeAsync();
    }
}
