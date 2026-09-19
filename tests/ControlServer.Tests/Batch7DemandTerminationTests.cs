using System.Data.Common;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 批次7-02（control-server#207）：终结一条需求与关闭它所在的旅程是两步，仍在调用方的同一次保存里；租约与用途占有在
/// 最后一条需求终结时才放；孤儿检查按从属需求表判。
/// </summary>
/// <remarks>
/// <para>
/// 单需求旅程里「终结这条需求」就是「终结最后一条」，所以拆开之后与今天逐字相同——那由 <see cref="ZeroChangePin"/> 在八条
/// 终结路径的端到端测试里钉住。这里是两者不同的时候：在库里直接造一趟两条需求的旅程（批次7-01 的表允许，引擎还造不出来），
/// 以及票面点名的几个时刻——同一动作第二次发生、两条需求的终结同时到达、终结与关闭之间重启、FAILED／UNKNOWN 的结果。
/// </para>
/// </remarks>
public sealed class Batch7DemandTerminationTests
{
    private const string Anchor = "D-7211";
    private const string Further = "D-7212";
    private const string Reason = "CANCELLED_BY_OPERATOR";
    private static readonly DateTimeOffset At = Batch7JourneyFixture.Now;

    // ---- Two steps: terminate the demand, close the journey only when it carries no other open demand -------------

    [Fact]
    public async Task EndingOneDemandOfTwoLeavesTheJourneyOpenItsOccupanciesHeldAndTheOtherDemandUntouched()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyRuntimeRow runtime = await AcceptTwoDemandJourneyAsync(fixture);
        string[] anchorBefore = await DumpDemandAsync(fixture, Anchor);
        string[] journeyBefore = await Batch7JourneyFixture.DumpAsync(fixture.Connection, "JourneyRuntimes");

        await EndAsync(fixture.Context, Further, At.AddMinutes(5));

        Assert.Equal(DemandExecutionStatus.Cancelled, await StatusAsync(fixture, Further));
        Assert.Equal(anchorBefore, await DumpDemandAsync(fixture, Anchor));
        Assert.Equal(journeyBefore, await Batch7JourneyFixture.DumpAsync(fixture.Connection, "JourneyRuntimes"));
        await AssertOccupanciesHeldAsync(fixture, runtime);
    }

    [Fact]
    public async Task EndingTheLastDemandClosesTheJourneyAndReleasesAllThreeOccupanciesTogether()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyRuntimeRow runtime = await AcceptTwoDemandJourneyAsync(fixture);
        await EndAsync(fixture.Context, Further, At.AddMinutes(5));

        await EndAsync(fixture.Context, Anchor, At.AddMinutes(7));

        await AssertClosedAtAsync(fixture, runtime, At.AddMinutes(7), Reason);
    }

    /// <summary>
    /// The same ending a second time -- a replayed cancellation result -- ends nothing again and releases nothing again:
    /// while the other demand is open it is not the last, and once the journey is closed every release keeps its first
    /// moment.
    /// </summary>
    [Fact]
    public async Task ARepeatedEndingNeitherEndsTheJourneyEarlyNorReleasesAnythingASecondTime()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyRuntimeRow runtime = await AcceptTwoDemandJourneyAsync(fixture);
        await EndAsync(fixture.Context, Further, At.AddMinutes(5));

        await EndAsync(fixture.Context, Further, At.AddMinutes(6));
        await AssertOccupanciesHeldAsync(fixture, runtime);

        await EndAsync(fixture.Context, Anchor, At.AddMinutes(7));
        await EndAsync(fixture.Context, Anchor, At.AddMinutes(8));
        await EndAsync(fixture.Context, Further, At.AddMinutes(9));
        await AssertClosedAtAsync(fixture, runtime, At.AddMinutes(7), Reason);
    }

    [Fact]
    public async Task AnEndingOfADemandAlreadyCompletedIsRefusedBeforeAnythingIsStaged()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyRuntimeRow runtime = await AcceptTwoDemandJourneyAsync(fixture);
        await Batch7JourneyFixture.CompleteByUnloadAsync(fixture.Context, Further, At.AddMinutes(4));
        await fixture.RenewContextAsync();
        JourneyRuntimeRow tracked = await DemandJourneyLookup.JourneyOf(fixture.Context, Further)
            .SingleAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() => new PickupStopTermination(fixture.Context)
            .StageAsync(tracked, Further, Reason, At.AddMinutes(5), TestContext.Current.CancellationToken));

        Assert.DoesNotContain(fixture.Context.ChangeTracker.Entries(), entry => entry.State != EntityState.Unchanged);
        await AssertOccupanciesHeldAsync(fixture, runtime);
    }

    // ---- The unload result releases the lease only for the last demand ------------------------------------------

    [Fact]
    public async Task AnUnloadOfADemandThatIsNotTheLastKeepsTheLeaseAndTheClaim()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyRuntimeRow runtime = await AcceptTwoDemandJourneyAsync(fixture);

        await Batch7JourneyFixture.CompleteByUnloadAsync(fixture.Context, Further, At.AddMinutes(5));

        Assert.Equal(DemandExecutionStatus.Succeeded, await StatusAsync(fixture, Further));
        await AssertOccupanciesHeldAsync(fixture, runtime);

        await EndAsync(fixture.Context, Anchor, At.AddMinutes(7));
        await AssertClosedAtAsync(fixture, runtime, At.AddMinutes(7), Reason);
    }

    [Fact]
    public async Task AnUnloadResultOfADemandThatIsNotTheLastKeepsTheLeaseAndTheClaimAndTheLastOneReleasesThem()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyRuntimeRow runtime = await AcceptTwoDemandJourneyAsync(fixture);
        await AddUnloadOperationAsync(fixture, Further);
        await AddUnloadOperationAsync(fixture, Anchor);

        Assert.Equal(OperationResultDisposition.Accepted, await ApplyUnloadResultAsync(fixture, Further, "COMPLETED", 5));
        Assert.Equal(DemandExecutionStatus.Succeeded, await StatusAsync(fixture, Further));
        await AssertOccupanciesHeldAsync(fixture, runtime);

        Assert.Equal(OperationResultDisposition.Accepted, await ApplyUnloadResultAsync(fixture, Anchor, "COMPLETED", 7));
        await using ControlServerDbContext reading = fixture.NewContext();
        Assert.Equal(At.AddMinutes(7), (await reading.VehicleDispatchLeases.SingleAsync(TestContext.Current.CancellationToken)).ReleasedAt);
        Assert.Empty(await reading.Set<VehiclePurposeClaimRow>().ToArrayAsync(TestContext.Current.CancellationToken));
        // The journey and the pickup order are the runtime's to close on its next round, as before (Engine, AwaitingUnloadResult).
        Assert.NotEqual(JourneyRuntimeStage.Completed, (await reading.JourneyRuntimes.SingleAsync(TestContext.Current.CancellationToken)).Stage);
    }

    /// <summary>A FAILED or UNKNOWN unload is not a termination: the demand goes to recovery and nothing is released.</summary>
    [Theory]
    [InlineData("FAILED")]
    [InlineData("UNKNOWN")]
    public async Task AFailedOrUnknownUnloadResultOfTheLastDemandReleasesNothing(string overallOutcome)
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyRuntimeRow runtime = await AcceptTwoDemandJourneyAsync(fixture);
        await EndAsync(fixture.Context, Further, At.AddMinutes(4));
        await AddUnloadOperationAsync(fixture, Anchor);

        Assert.Equal(OperationResultDisposition.RecoveryRequired,
            await ApplyUnloadResultAsync(fixture, Anchor, overallOutcome, 5));

        Assert.Equal(DemandExecutionStatus.RecoveryRequired, await StatusAsync(fixture, Anchor));
        await AssertOccupanciesHeldAsync(fixture, runtime);
    }

    // ---- Two endings at the same moment --------------------------------------------------------------------------

    /// <summary>
    /// The two demands of one journey end at the same moment on two connections, each in its own write transaction: an
    /// unload result on one, a cancellation on the other. Exactly one of them finds itself the last, so the journey is
    /// closed once and the lease and the claim are released once, and nothing is left with every demand ended and the
    /// vehicle still held.
    /// </summary>
    /// <remarks>
    /// The interleaving is forced: the unload reads "is this the last open demand" and then waits, up to a moment, for the
    /// cancellation to read it too. Read inside the unload's write transaction, as it must be, the cancellation cannot
    /// start its own until the unload has committed, so it reads the unload's result and is the last. Read before that
    /// transaction, both read "the other is still open" and neither releases -- which is what this test exists to catch.
    /// </remarks>
    [Fact]
    public async Task TwoDemandsOfOneJourneyEndingAtOnceCloseItExactlyOnce()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cs207-{Guid.NewGuid():N}.db");
        string connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        try
        {
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            JourneyRuntimeRow runtime;
            await using (ControlServerDbContext setup = FileContext(connectionString))
            {
                await setup.Database.MigrateAsync(cancellationToken);
                await Batch7JourneyFixture.AcceptAsync(setup, Anchor, "agv-01", "VK-01", At);
                runtime = await setup.JourneyRuntimes.AsNoTracking().SingleAsync(cancellationToken);
                await JourneyMembershipSeed.AddFurtherDemandAsync(setup, runtime, Further);
            }

            TaskCompletionSource unloadRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource cancellationRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await using ControlServerDbContext unloading = FileContext(connectionString, new LastOpenDemandRead(async () =>
            {
                unloadRead.TrySetResult();
                await Task.WhenAny(cancellationRead.Task, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
            }));
            await using ControlServerDbContext cancelling = FileContext(connectionString, new LastOpenDemandRead(() =>
            {
                cancellationRead.TrySetResult();
                return Task.CompletedTask;
            }));

            Task unload = Batch7JourneyFixture.CompleteByUnloadAsync(unloading, Further, At.AddMinutes(5));
            await unloadRead.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            Task cancellation = Task.Run(async () =>
            {
                await using var transaction = await cancelling.Database.BeginTransactionAsync(cancellationToken);
                JourneyRuntimeRow tracked = await DemandJourneyLookup.JourneyOf(cancelling, Anchor).SingleAsync(cancellationToken);
                await new PickupStopTermination(cancelling).StageAsync(tracked, Anchor, Reason, At.AddMinutes(6), cancellationToken);
                await cancelling.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }, cancellationToken);
            await Task.WhenAll(unload, cancellation);

            await using ControlServerDbContext reading = FileContext(connectionString);
            Assert.Equal(DemandExecutionStatus.Succeeded, (await reading.AcceptedDemands.SingleAsync(row => row.DemandId == Further, cancellationToken)).Status);
            Assert.Equal(DemandExecutionStatus.Cancelled, (await reading.AcceptedDemands.SingleAsync(row => row.DemandId == Anchor, cancellationToken)).Status);
            // The cancellation read after the unload committed, so it was the last: it closed the journey and released
            // the lease at its own moment. The unload released nothing.
            Assert.Equal(At.AddMinutes(6), (await reading.VehicleDispatchLeases.SingleAsync(cancellationToken)).ReleasedAt);
            Assert.Empty(await reading.Set<VehiclePurposeClaimRow>().ToArrayAsync(cancellationToken));
            JourneyRuntimeRow closed = await reading.JourneyRuntimes.SingleAsync(cancellationToken);
            Assert.Equal(JourneyRuntimeStage.Completed, closed.Stage);
            Assert.Equal(Reason, closed.BlockReasonCode);
            await VehicleOccupancyAssertions.AssertActiveLeasesAndPurposeClaimsMatchAsync(reading);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    // ---- Restart between the ending and the journey's closing, and the orphan check --------------------------------

    /// <summary>
    /// The unload result is saved -- demand Succeeded, lease and claim released -- and the server stops before the runtime
    /// closes the journey. That is an ordinary state between two saves, not an orphan: the restarted runtime's next round
    /// closes the journey and releases the pickup order's occupancy.
    /// </summary>
    [Fact]
    public async Task AfterAnUnloadResultIsSavedARestartedRuntimeClosesTheJourneyRatherThanCallingItAnOrphan()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.RunToGateUnloadAsync();
        await fixture.ApplySafeResultAsync(
            await fixture.OperationAsync(SlotOperationType.Unload), SlotOperationType.Unload, SlotBusinessState.Empty);
        Assert.Equal(DemandExecutionStatus.Succeeded, (await fixture.DemandRowAsync()).Status);
        Assert.NotNull((await fixture.LeaseAsync()).ReleasedAt);
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await fixture.RuntimeAsync()).Stage);

        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        await AssertClosedByTheRuntimeAsync(fixture);
    }

    /// <summary>The same with two demands, both ended, and the journey still open when the server stops.</summary>
    [Fact]
    public async Task AJourneyWhoseDemandsHaveAllEndedIsClosedAfterARestartRatherThanCalledAnOrphan()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.RunToGateUnloadAsync();
        await JourneyMembershipSeed.AddFurtherDemandAsync(
            fixture.Context, runtime, "10000000-0000-4000-8000-000000000072", DemandExecutionStatus.Cancelled);
        await fixture.ApplySafeResultAsync(
            await fixture.OperationAsync(SlotOperationType.Unload), SlotOperationType.Unload, SlotBusinessState.Empty);
        Assert.NotNull((await fixture.LeaseAsync()).ReleasedAt);

        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        await AssertClosedByTheRuntimeAsync(fixture);
    }

    /// <summary>
    /// An open demand is an orphan when no membership in force puts it in a journey -- not when no journey row names it
    /// as its anchor. A demand added to a journey has no journey row of its own.
    /// </summary>
    [Fact]
    public async Task AnOpenDemandInAJourneyIsNotAnOrphanThoughNoJourneyRowNamesIt()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow runtime = await CompleteAJourneyAsync(fixture);
        await JourneyMembershipSeed.AddFurtherDemandAsync(fixture.Context, runtime, "10000000-0000-4000-8000-000000000072");

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AnOpenDemandWhoseMembershipWasRemovedIsStillAnOrphan()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow runtime = await CompleteAJourneyAsync(fixture);
        const string removed = "10000000-0000-4000-8000-000000000072";
        await JourneyMembershipSeed.AddFurtherDemandAsync(fixture.Context, runtime, removed);
        await new JourneyMembershipStore(fixture.Context).RemoveDemandAsync(
            runtime.JourneyId, removed, "REDISPATCHED", Now, TestContext.Current.CancellationToken);

        BusinessIdentityConflictException error = await Assert.ThrowsAsync<BusinessIdentityConflictException>(
            () => fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken));

        Assert.Contains(removed, error.Message, StringComparison.Ordinal);
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private static async Task<JourneyRuntimeRow> AcceptTwoDemandJourneyAsync(Batch7JourneyFixture fixture)
    {
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, Anchor, "agv-01", "VK-01", At);
        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        await JourneyMembershipSeed.AddFurtherDemandAsync(fixture.Context, runtime, Further);
        await fixture.RenewContextAsync();
        return runtime;
    }

    /// <summary>Ends a demand at its pickup stop the way every caller does: staged into a write transaction, saved once.</summary>
    private static async Task EndAsync(ControlServerDbContext context, string demandId, DateTimeOffset at)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        JourneyRuntimeRow runtime = await DemandJourneyLookup.JourneyOf(context, demandId).SingleAsync(cancellationToken);
        await new PickupStopTermination(context).StageAsync(runtime, demandId, Reason, at, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        context.ChangeTracker.Clear();
    }

    private static async Task<DemandExecutionStatus> StatusAsync(Batch7JourneyFixture fixture, string demandId)
    {
        await using ControlServerDbContext reading = fixture.NewContext();
        return (await reading.AcceptedDemands.SingleAsync(row => row.DemandId == demandId, TestContext.Current.CancellationToken)).Status;
    }

    private static async Task<string[]> DumpDemandAsync(Batch7JourneyFixture fixture, string demandId) =>
        [.. (await Batch7JourneyFixture.DumpAsync(fixture.Connection, "AcceptedDemands"))
            .Where(row => row.StartsWith($"DemandId='{demandId}'", StringComparison.Ordinal))];

    private static async Task AssertOccupanciesHeldAsync(Batch7JourneyFixture fixture, JourneyRuntimeRow runtime)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using ControlServerDbContext reading = fixture.NewContext();
        Assert.NotEqual(JourneyRuntimeStage.Completed, (await reading.JourneyRuntimes.SingleAsync(cancellationToken)).Stage);
        Assert.Null((await reading.VehicleDispatchLeases.SingleAsync(cancellationToken)).ReleasedAt);
        Assert.Equal(runtime.JourneyId, (await reading.Set<VehiclePurposeClaimRow>().SingleAsync(cancellationToken)).JourneyId);
        Assert.Null((await reading.OrderIntents.SingleAsync(row => row.UpperId == runtime.PickupUpperId, cancellationToken))
            .VehicleOccupancyReleasedAt);
    }

    private static async Task AssertClosedAtAsync(
        Batch7JourneyFixture fixture, JourneyRuntimeRow runtime, DateTimeOffset closedAt, string reasonCode)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using ControlServerDbContext reading = fixture.NewContext();
        JourneyRuntimeRow closed = await reading.JourneyRuntimes.SingleAsync(cancellationToken);
        Assert.Equal(JourneyRuntimeStage.Completed, closed.Stage);
        Assert.Equal(reasonCode, closed.BlockReasonCode);
        Assert.Null(closed.StationDepartureWaitStartedAt);
        Assert.Equal(closedAt, (await reading.VehicleDispatchLeases.SingleAsync(cancellationToken)).ReleasedAt);
        Assert.Empty(await reading.Set<VehiclePurposeClaimRow>().ToArrayAsync(cancellationToken));
        Assert.Equal(closedAt, (await reading.OrderIntents.SingleAsync(row => row.UpperId == runtime.PickupUpperId, cancellationToken))
            .VehicleOccupancyReleasedAt);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await reading.AcceptedDemands.SingleAsync(row => row.DemandId == Anchor, cancellationToken)).Status);
    }

    private static async Task AssertClosedByTheRuntimeAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow closed = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, closed.Stage);
        Assert.NotNull((await fixture.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == closed.PickupUpperId, TestContext.Current.CancellationToken)).VehicleOccupancyReleasedAt);
        await VehicleOccupancyAssertions.AssertActiveLeasesAndPurposeClaimsMatchAsync(fixture.Context);
    }

    private static async Task<JourneyRuntimeRow> CompleteAJourneyAsync(RuntimeFixture fixture)
    {
        fixture.Catalog.Set(fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.RunToCompletionAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        fixture.Catalog.Set();
        return runtime;
    }

    private static async Task AddUnloadOperationAsync(Batch7JourneyFixture fixture, string demandId)
    {
        await using ControlServerDbContext writing = fixture.NewContext();
        writing.StationOperations.Add(new StationOperationRow
        {
            SlotOperationAttemptId = "unload-" + demandId,
            DemandId = demandId,
            SublotId = "SUBLOT-" + demandId,
            TargetSlotsJson = "[1]",
            OperationType = SlotOperationType.Unload,
            ForcedRecoveryGeneration = 0,
            ContentHash = new string('a', 64),
            Status = StationOperationStatus.Prepared,
            CreatedAt = At
        });
        await writing.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>An unload result, applied the way the inbox applies it: inside its write transaction.</summary>
    private static async Task<OperationResultDisposition> ApplyUnloadResultAsync(
        Batch7JourneyFixture fixture, string demandId, string overallOutcome, int minute)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        bool completed = overallOutcome == "COMPLETED";
        await using ControlServerDbContext writing = fixture.NewContext();
        await using var transaction = await writing.Database.BeginTransactionAsync(cancellationToken);
        OperationResultDisposition disposition = await new WireToGateStore(writing).ApplyOperationResultAsync(
            new StationOperationResult(
                $"result-{demandId}-{minute}",
                "unload-" + demandId,
                demandId,
                SlotOperationType.Unload,
                overallOutcome,
                [new SlotPhysicalEvidence(1, completed ? SlotBusinessState.Empty : SlotBusinessState.Unknown, true, true)],
                completed,
                At.AddMinutes(minute),
                new string('c', 64),
                new string('e', 64)),
            "agv-01",
            0,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return disposition;
    }

    private static ControlServerDbContext FileContext(string connectionString, params IInterceptor[] interceptors)
    {
        DbContextOptionsBuilder<ControlServerDbContext> builder =
            new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connectionString);
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }
        return new ControlServerDbContext(builder.Options);
    }

    /// <summary>Runs <paramref name="afterRead"/> once the "is this the last open demand" read has executed.</summary>
    private sealed class LastOpenDemandRead(Func<Task> afterRead) : DbCommandInterceptor
    {
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(DemandJourneyLookup.LastOpenDemandTag, StringComparison.Ordinal))
            {
                await afterRead();
            }
            return result;
        }
    }
}
