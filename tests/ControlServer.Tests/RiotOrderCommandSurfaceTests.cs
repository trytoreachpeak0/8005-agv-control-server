using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// The four RIoT order commands: issued through named Facades, armed before the call, and never
/// counted as successful until the order's state has been read back.
/// </summary>
/// <remarks>
/// The audit store here is the real one over SQLite rather than a stand-in, because half of what
/// is being asserted is what the table ends up holding — attempt numbers that increase, an armed
/// row that exists before any answer arrives, and an outcome that is not <c>Confirmed</c> merely
/// because RIoT accepted the call.
/// </remarks>
public sealed class RiotOrderCommandSurfaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    private static readonly RiotOrderCommandTarget Target =
        new("agv01", "W2G-20260908-0001", "RIOT-ORDER-77");

    [Theory]
    [InlineData(RiotOrderCommandKind.Cancel, RiotOrderState.Cancelled)]
    [InlineData(RiotOrderCommandKind.Hold, RiotOrderState.Paused)]
    [InlineData(RiotOrderCommandKind.ContinueFromHeld, RiotOrderState.Executing)]
    [InlineData(RiotOrderCommandKind.ContinueFromHang, RiotOrderState.Executing)]
    public async Task EachCommandIsConfirmedOnlyWhenTheOrderReachesTheStateItWasIssuedFor(
        RiotOrderCommandKind kind,
        int reachedState)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Movement.Next = Observation(reachedState);

        RiotOrderCommandRecord record = await fixture.Service.IssueAsync(
            kind, Target, "test", faultGeneration: null, TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderCommandOutcome.Confirmed, record.Outcome);
        Assert.True(record.Succeeded);
        Assert.Equal(RiotCommandTypeNames.For(kind), fixture.Gateway.OrderCalls.Single().CommandType);
        Assert.Equal(Target.OrderId, fixture.Gateway.OrderCalls.Single().OrderId);
    }

    /// <summary>
    /// The load-bearing negative: RIoT accepted the call and the order has not moved, so the
    /// command is not successful.
    /// </summary>
    [Fact]
    public async Task AnAcceptedCallWhoseOrderHasNotMovedIsPendingRatherThanSuccessful()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Movement.Next = Observation(RiotOrderState.Executing);

        RiotOrderCommandRecord record = await fixture.Service.IssueAsync(
            RiotOrderCommandKind.Hold, Target, "test", null, TestContext.Current.CancellationToken);

        Assert.Equal(RiotCommandCallDisposition.Accepted, record.Call.Disposition);
        Assert.Equal(RiotOrderCommandOutcome.Pending, record.Outcome);
        Assert.False(record.Succeeded);
    }

    [Fact]
    public async Task ADefinitiveCallFailureOnAnUnmovedOrderIsRecordedAsFailed()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.OrderDisposition = RiotCommandCallDisposition.Failed;
        fixture.Movement.Next = Observation(RiotOrderState.Executing);

        RiotOrderCommandRecord record = await fixture.Service.IssueAsync(
            RiotOrderCommandKind.Hold, Target, "test", null, TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderCommandOutcome.Failed, record.Outcome);
    }

    /// <summary>
    /// A lost answer is not a failed command. The order is HELD, so the hold worked whatever
    /// happened to the response.
    /// </summary>
    [Fact]
    public async Task ATimedOutCallWhoseOrderReachedTheIntendedStateIsConfirmed()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.OrderDisposition = RiotCommandCallDisposition.Unknown;
        fixture.Movement.Next = Observation(RiotOrderState.Paused);

        RiotOrderCommandRecord record = await fixture.Service.IssueAsync(
            RiotOrderCommandKind.Hold, Target, "test", null, TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderCommandOutcome.Confirmed, record.Outcome);
    }

    [Fact]
    public async Task AnOrderThatCannotBeReadBackLeavesTheCommandUnknown()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Movement.Next = new RiotOrderObservation(
            Target.UpperId, RiotOrderObservationKind.Unknown, null);

        RiotOrderCommandRecord record = await fixture.Service.IssueAsync(
            RiotOrderCommandKind.Hold, Target, "test", null, TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderCommandOutcome.Unknown, record.Outcome);
    }

    [Fact]
    public async Task TheAttemptIsArmedBeforeTheCallGoesOut()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Movement.Next = Observation(RiotOrderState.Paused);
        fixture.Gateway.BeforeCall = async () =>
        {
            IReadOnlyList<RiotOrderCommandAttempt> armed = await fixture.Audit.ReadAttemptsAsync(
                RiotCommandTypeNames.OrderHold, Target.UpperId, TestContext.Current.CancellationToken);
            Assert.Equal(RiotOrderCommandOutcome.Pending, Assert.Single(armed).Outcome);
        };

        await fixture.Service.IssueAsync(
            RiotOrderCommandKind.Hold, Target, "test", null, TestContext.Current.CancellationToken);

        Assert.True(fixture.Gateway.BeforeCallRan);
    }

    /// <summary>
    /// Three tries are three rows. "It was called once" has to be decidable from the table.
    /// </summary>
    [Fact]
    public async Task EachRetryIsANewAttemptRatherThanAnOverwrite()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Gateway.OrderDisposition = RiotCommandCallDisposition.Unknown;
        fixture.Movement.Next = Observation(RiotOrderState.Executing);

        for (int i = 0; i < 3; i++)
        {
            await fixture.Service.IssueAsync(
                RiotOrderCommandKind.Hold, Target, "test", null, TestContext.Current.CancellationToken);
        }

        IReadOnlyList<RiotOrderCommandAttempt> attempts = await fixture.Audit.ReadAttemptsAsync(
            RiotCommandTypeNames.OrderHold, Target.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], attempts.Select(attempt => attempt.AttemptNumber));
        Assert.Equal(3, fixture.Gateway.OrderCalls.Count);
    }

    /// <summary>
    /// A repeat issued for a different reason is a different request, and the semantic hash says so.
    /// </summary>
    [Fact]
    public async Task ARepeatWithADifferentReasonHasADifferentSemanticHash()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Movement.Next = Observation(RiotOrderState.Executing);

        await fixture.Service.IssueAsync(
            RiotOrderCommandKind.Hold, Target, "gate closed", null, TestContext.Current.CancellationToken);
        await fixture.Service.IssueAsync(
            RiotOrderCommandKind.Hold, Target, "vehicle faulted", null, TestContext.Current.CancellationToken);

        IReadOnlyList<RiotOrderCommandAttempt> attempts = await fixture.Audit.ReadAttemptsAsync(
            RiotCommandTypeNames.OrderHold, Target.UpperId, TestContext.Current.CancellationToken);

        Assert.NotEqual(attempts[0].RequestSemanticSha256, attempts[1].RequestSemanticSha256);
    }

    /// <summary>
    /// A pending attempt settles when the order later reaches the state the command was for.
    /// </summary>
    [Fact]
    public async Task ReconcileSettlesAPendingAttemptOnceTheOrderReachesTheIntendedState()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Movement.Next = Observation(RiotOrderState.Executing);
        RiotOrderCommandRecord record = await fixture.Service.IssueAsync(
            RiotOrderCommandKind.Hold, Target, "test", null, TestContext.Current.CancellationToken);
        Assert.Equal(RiotOrderCommandOutcome.Pending, record.Outcome);

        fixture.Movement.Next = Observation(RiotOrderState.Paused);
        RiotOrderCommandOutcome settled = await fixture.Service.ReconcileAsync(
            record.Attempt, TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderCommandOutcome.Confirmed, settled);
        IReadOnlyList<RiotOrderCommandAttempt> attempts = await fixture.Audit.ReadAttemptsAsync(
            RiotCommandTypeNames.OrderHold, Target.UpperId, TestContext.Current.CancellationToken);
        Assert.Equal(RiotOrderCommandOutcome.Confirmed, Assert.Single(attempts).Outcome);
    }

    [Fact]
    public async Task ReconcileLeavesASettledAttemptAlone()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Movement.Next = Observation(RiotOrderState.Paused);
        RiotOrderCommandRecord record = await fixture.Service.IssueAsync(
            RiotOrderCommandKind.Hold, Target, "test", null, TestContext.Current.CancellationToken);

        // The order moved on afterwards; that is not this command's business any more.
        fixture.Movement.Next = Observation(RiotOrderState.Success);
        RiotOrderCommandOutcome settled = await fixture.Service.ReconcileAsync(
            record.Attempt, TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderCommandOutcome.Confirmed, settled);
    }

    /// <summary>
    /// The reconciliation table itself, exhausted per command against the states that matter.
    /// </summary>
    [Theory]
    // A cancel is confirmed by CANCELLED and defeated by any other terminal state.
    [InlineData(RiotOrderCommandKind.Cancel, RiotOrderState.Cancelled, RiotOrderCommandOutcome.Confirmed)]
    [InlineData(RiotOrderCommandKind.Cancel, RiotOrderState.Success, RiotOrderCommandOutcome.Failed)]
    [InlineData(RiotOrderCommandKind.Cancel, RiotOrderState.Executing, RiotOrderCommandOutcome.Pending)]
    // A hold is confirmed by PAUSED only.
    [InlineData(RiotOrderCommandKind.Hold, RiotOrderState.Paused, RiotOrderCommandOutcome.Confirmed)]
    [InlineData(RiotOrderCommandKind.Hold, RiotOrderState.Cancelled, RiotOrderCommandOutcome.Failed)]
    [InlineData(RiotOrderCommandKind.Hold, RiotOrderState.Queueing, RiotOrderCommandOutcome.Pending)]
    // Resuming accepts SUCCESS: an order that already finished did resume.
    [InlineData(RiotOrderCommandKind.ContinueFromHeld, RiotOrderState.Executing, RiotOrderCommandOutcome.Confirmed)]
    [InlineData(RiotOrderCommandKind.ContinueFromHeld, RiotOrderState.Success, RiotOrderCommandOutcome.Confirmed)]
    [InlineData(RiotOrderCommandKind.ContinueFromHeld, RiotOrderState.Paused, RiotOrderCommandOutcome.Pending)]
    [InlineData(RiotOrderCommandKind.ContinueFromHeld, RiotOrderState.Failed, RiotOrderCommandOutcome.Failed)]
    // A HANG that is still a HANG is not resumed. The SDK says as much about this command.
    [InlineData(RiotOrderCommandKind.ContinueFromHang, RiotOrderState.Hang, RiotOrderCommandOutcome.Pending)]
    [InlineData(RiotOrderCommandKind.ContinueFromHang, RiotOrderState.Executing, RiotOrderCommandOutcome.Confirmed)]
    public void ReconciliationReadsTheOrderStateAgainstWhatTheCommandWasFor(
        RiotOrderCommandKind kind,
        int orderState,
        RiotOrderCommandOutcome expected)
    {
        RiotOrderCommandOutcome outcome = RiotOrderCommandService.Reconcile(
            kind, RiotCommandCallDisposition.Accepted, Observation(orderState));

        Assert.Equal(expected, outcome);
    }

    [Fact]
    public void TheCommandTypeNamesRoundTrip()
    {
        foreach (RiotOrderCommandKind kind in Enum.GetValues<RiotOrderCommandKind>())
        {
            Assert.Equal(kind, RiotCommandTypeNames.ParseOrderCommand(RiotCommandTypeNames.For(kind)));
        }
    }

    private static RiotOrderObservation Observation(int orderState) => new(
        Target.UpperId,
        orderState is RiotOrderState.Queueing or RiotOrderState.Executing or RiotOrderState.Paused
            or RiotOrderState.Hang or RiotOrderState.QueuePriority
            ? RiotOrderObservationKind.Active
            : RiotOrderObservationKind.Terminal,
        Target.OrderId,
        orderState);

    private sealed class FakeOrderCommandGateway : IRiotOrderCommandGateway
    {
        public RiotCommandCallDisposition OrderDisposition { get; set; } = RiotCommandCallDisposition.Accepted;

        public RiotCommandCallDisposition EmergencyDisposition { get; set; } = RiotCommandCallDisposition.Accepted;

        public List<(string CommandType, string OrderId, string? Reason)> OrderCalls { get; } = [];

        public List<(string CommandType, string DeviceKey)> EmergencyCalls { get; } = [];

        public Func<Task>? BeforeCall { get; set; }

        public bool BeforeCallRan { get; private set; }

        public async Task<RiotCommandCallResult> IssueOrderCommandAsync(
            RiotOrderCommandKind kind,
            string orderId,
            string? reason,
            CancellationToken cancellationToken)
        {
            if (BeforeCall is not null)
            {
                BeforeCallRan = true;
                await BeforeCall().ConfigureAwait(false);
            }

            string commandType = RiotCommandTypeNames.For(kind);
            OrderCalls.Add((commandType, orderId, reason));
            return Result(commandType, OrderDisposition);
        }

        public Task<RiotCommandCallResult> IssueEmergencyCommandAsync(
            RiotEmergencyCommandKind kind,
            string deviceKey,
            CancellationToken cancellationToken)
        {
            string commandType = RiotCommandTypeNames.For(kind);
            EmergencyCalls.Add((commandType, deviceKey));
            return Task.FromResult(Result(commandType, EmergencyDisposition));
        }

        private static RiotCommandCallResult Result(string operation, RiotCommandCallDisposition disposition) =>
            new(disposition, new RiotOrderCallReceipt(
                operation,
                disposition == RiotCommandCallDisposition.Accepted ? "SdkAccepted" : "SdkFailure",
                Now));
    }

    private sealed class FakeMovementGateway : IRiotMovementGateway
    {
        public RiotOrderObservation Next { get; set; } =
            new("unset", RiotOrderObservationKind.Unknown, null);

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(
            string upperId,
            CancellationToken cancellationToken) => Task.FromResult(Next);

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The command surface never creates orders.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ControlServerDbContext _context;

        private Fixture(SqliteConnection connection, ControlServerDbContext context)
        {
            _connection = connection;
            _context = context;
            Audit = new RiotOrderCommandAuditStore(context);
            Service = new RiotOrderCommandService(Gateway, Audit, Movement, new FixedClock(Now));
        }

        public FakeOrderCommandGateway Gateway { get; } = new();

        public FakeMovementGateway Movement { get; } = new();

        public RiotOrderCommandAuditStore Audit { get; }

        public RiotOrderCommandService Service { get; }

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

        public async ValueTask DisposeAsync()
        {
            await _context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
