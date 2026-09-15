using ControlServer.Application;

namespace ControlServer.Tests;

/// <summary>
/// 结构性派车阻断按任务与原因去重，同一原因持续成立只更新既有告警，不按每轮调度新建（REQ-0210）。
/// </summary>
public sealed class StructuralDispatchBlockStoreTests
{
    private const string BasketsExceedGroup = "EXPECTED_BASKETS_EXCEED_SLOT_POSITION_GROUP";
    private const string NoRoute = "NO_REACHABLE_ROUTE";

    private static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RaisingTheSameReasonAgainKeepsOneBlockWithItsFirstRaisedTimeAndOnlyMovesLastSeen()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();

        StructuralDispatchBlock first = await fixture.Blocks.RaiseOrRefreshAsync(
            "D-1", BasketsExceedGroup, "TDK-1", """{"expected":6,"largest":4}""", Now,
            TestContext.Current.CancellationToken);
        StructuralDispatchBlock again = await fixture.Blocks.RaiseOrRefreshAsync(
            "D-1", BasketsExceedGroup, "TDK-1", """{"expected":6,"largest":4,"round":2}""", Now.AddMinutes(5),
            TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(Now, first.FirstRaisedAt);
        Assert.Equal(Now, again.FirstRaisedAt);
        Assert.Equal(Now.AddMinutes(5), again.LastSeenAt);
        StructuralDispatchBlock only = Assert.Single(
            await fixture.Blocks.ListUnclearedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            new StructuralDispatchBlock(
                "D-1", BasketsExceedGroup, "TDK-1", Now, Now.AddMinutes(5), null, """{"expected":6,"largest":4}"""),
            only);
    }

    [Fact]
    public async Task ADifferentReasonForTheSameDemandIsADifferentBlock()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();

        await fixture.Blocks.RaiseOrRefreshAsync(
            "D-1", NoRoute, "TDK-1", "{}", Now.AddMinutes(1), TestContext.Current.CancellationToken);
        await fixture.Blocks.RaiseOrRefreshAsync(
            "D-1", BasketsExceedGroup, "TDK-1", "{}", Now, TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        IReadOnlyList<StructuralDispatchBlock> uncleared =
            await fixture.Blocks.ListUnclearedAsync(TestContext.Current.CancellationToken);
        // Ordered by when each block first held.
        Assert.Equal([BasketsExceedGroup, NoRoute], uncleared.Select(block => block.ReasonCode));
    }

    [Fact]
    public async Task AClearedBlockLeavesTheUnclearedListAndARecurrenceStartsAFreshEpisode()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        Assert.False(await fixture.Blocks.ClearAsync(
            "D-1", BasketsExceedGroup, Now, TestContext.Current.CancellationToken));

        await fixture.Blocks.RaiseOrRefreshAsync(
            "D-1", BasketsExceedGroup, "TDK-1", """{"episode":1}""", Now, TestContext.Current.CancellationToken);
        Assert.True(await fixture.Blocks.ClearAsync(
            "D-1", BasketsExceedGroup, Now.AddMinutes(1), TestContext.Current.CancellationToken));
        Assert.False(await fixture.Blocks.ClearAsync(
            "D-1", BasketsExceedGroup, Now.AddMinutes(2), TestContext.Current.CancellationToken));
        fixture.Context.ChangeTracker.Clear();
        Assert.Empty(await fixture.Blocks.ListUnclearedAsync(TestContext.Current.CancellationToken));

        StructuralDispatchBlock recurred = await fixture.Blocks.RaiseOrRefreshAsync(
            "D-1", BasketsExceedGroup, "TDK-1", """{"episode":2}""", Now.AddMinutes(10),
            TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(
            new StructuralDispatchBlock(
                "D-1", BasketsExceedGroup, "TDK-1", Now.AddMinutes(10), Now.AddMinutes(10), null, """{"episode":2}"""),
            recurred);
        Assert.Equal(
            recurred,
            Assert.Single(await fixture.Blocks.ListUnclearedAsync(TestContext.Current.CancellationToken)));
    }
}
