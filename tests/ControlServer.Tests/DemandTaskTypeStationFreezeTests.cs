using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static ControlServer.Tests.TaskTypeStationTestData;

namespace ControlServer.Tests;

/// <summary>
/// 需求冻结规则版本与绑定集版本（REQ-0344），存在批次 3 的通用冻结表 <c>ConfigurationConsumerBindings</c> 里，一需求两行。
/// </summary>
public sealed class DemandTaskTypeStationFreezeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<TaskTypeStationPersistenceFixture> WithTwoVersionsOfEachAsync(
        params IInterceptor[] interceptors)
    {
        TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync(interceptors);
        await fixture.Rules.WriteVersionAsync(SixRules, Source, Now, Token);
        await fixture.Rules.WriteVersionAsync(
            [.. SixRules.Where(rule => rule.TaskType != TransportTaskTypes.DieToOven)], Source, Now, Token);
        await fixture.Bindings.WriteVersionAsync(25, 1, [TransportTaskTypes.WireToGate], [GateBinding], null, Source, Now, Token);
        await fixture.Bindings.WriteVersionAsync(25, 2, [TransportTaskTypes.WireToGate], [GateBinding], null, Source, Now, Token);
        // Map 25 version 3 goes back to rule version 1 with a different binding, so "same rules, other binding set" exists.
        await fixture.Bindings.WriteVersionAsync(
            25, 1, [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire], [GateBinding, StagingBinding],
            null, Source, Now, Token);
        await fixture.Bindings.WriteVersionAsync(26, 1, [TransportTaskTypes.WireToGate], [GateBinding], null, Source, Now, Token);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    [Fact]
    public async Task FreezingWritesTwoConsumerBindingRowsAndFreezingTheSamePairAgainIsIdempotent()
    {
        await using TaskTypeStationPersistenceFixture fixture = await WithTwoVersionsOfEachAsync();
        Assert.Null(await fixture.Freezes.ReadAsync("demand-1", Token));

        DemandTaskTypeStationFreeze first = await fixture.Freezes.FreezeAsync("demand-1", 1, 25, 3, Now, Token);
        DemandTaskTypeStationFreeze again = await fixture.Freezes.FreezeAsync("demand-1", 1, 25, 3, Now.AddMinutes(9), Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(new DemandTaskTypeStationFreeze("demand-1", 1, 25, 3, Now), first);
        Assert.Equal(first, again);
        Assert.Equal(first, await fixture.Freezes.ReadAsync("demand-1", Token));

        ConfigurationConsumerBindingRow[] rows = await fixture.Context.Set<ConfigurationConsumerBindingRow>()
            .Where(row => row.ConsumerId == "demand-1")
            .ToArrayAsync(Token);
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.Equal("TransportDemand", row.ConsumerKind));
        ConfigurationConsumerBindingRow rule = Assert.Single(rows, row => row.ObjectKind == GovernedObjectKind.TaskTypeStationRule);
        Assert.Equal(TaskTypeStationGovernance.RuleObjectId, rule.ObjectId);
        Assert.Equal(1, rule.FrozenVersion);
        Assert.Equal((await fixture.Rules.ReadVersionAsync(1, Token))!.SnapshotId, rule.SnapshotId);
        ConfigurationConsumerBindingRow binding = Assert.Single(rows, row => row.ObjectKind == GovernedObjectKind.PublicStationBinding);
        Assert.Equal("map-25", binding.ObjectId);
        Assert.Equal(3, binding.FrozenVersion);
        Assert.Equal((await fixture.Bindings.ReadVersionAsync(25, 3, Token))!.SnapshotId, binding.SnapshotId);
    }

    [Theory]
    [InlineData(2, 25, 2)]
    [InlineData(1, 25, 1)]
    [InlineData(1, 26, 1)]
    public async Task FreezingADifferentRuleVersionBindingSetVersionOrMapIsRefusedAndTheFirstFreezeStands(
        long ruleVersion, int mapId, long bindingSetVersion)
    {
        await using TaskTypeStationPersistenceFixture fixture = await WithTwoVersionsOfEachAsync();
        DemandTaskTypeStationFreeze first = await fixture.Freezes.FreezeAsync("demand-1", 1, 25, 3, Now, Token);

        await Assert.ThrowsAsync<DemandTaskTypeStationFreezeConflictException>(
            () => fixture.Freezes.FreezeAsync("demand-1", ruleVersion, mapId, bindingSetVersion, Now, Token));
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(first, await fixture.Freezes.ReadAsync("demand-1", Token));
    }

    [Theory]
    [InlineData(3, 25, 1)]
    [InlineData(1, 25, 9)]
    [InlineData(1, 27, 1)]
    public async Task FreezingAVersionThatDoesNotExistWritesNothing(long ruleVersion, int mapId, long bindingSetVersion)
    {
        await using TaskTypeStationPersistenceFixture fixture = await WithTwoVersionsOfEachAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Freezes.FreezeAsync("demand-1", ruleVersion, mapId, bindingSetVersion, Now, Token));
        fixture.Context.ChangeTracker.Clear();

        Assert.Null(await fixture.Freezes.ReadAsync("demand-1", Token));
        Assert.Empty(await fixture.Context.Set<ConfigurationConsumerBindingRow>().ToArrayAsync(Token));
    }

    [Theory]
    [InlineData(2, 25, 1)]
    [InlineData(1, 25, 2)]
    [InlineData(2, 26, 1)]
    public async Task ARuleVersionThatIsNotTheOneTheBindingSetWasBuiltOnIsRefusedAndWritesNothing(
        long ruleVersion, int mapId, long bindingSetVersion)
    {
        // Both versions exist, but the binding set was validated against another rule version: freezing the pair would
        // record a demand against rules and bindings that never held together (REQ-0344).
        await using TaskTypeStationPersistenceFixture fixture = await WithTwoVersionsOfEachAsync();

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Freezes.FreezeAsync("demand-1", ruleVersion, mapId, bindingSetVersion, Now, Token));
        Assert.Contains("rule version", refused.Message, StringComparison.Ordinal);
        fixture.Context.ChangeTracker.Clear();

        Assert.Null(await fixture.Freezes.ReadAsync("demand-1", Token));
        Assert.Empty(await fixture.Context.Set<ConfigurationConsumerBindingRow>().ToArrayAsync(Token));
    }

    [Fact]
    public async Task WhenTheSecondRowCannotBeWrittenTheFirstIsNotLeftBehind()
    {
        FailWhenBindingSetFreezeIsSaved failure = new();
        await using TaskTypeStationPersistenceFixture fixture = await WithTwoVersionsOfEachAsync(failure);
        failure.Armed = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Freezes.FreezeAsync("demand-1", 1, 25, 3, Now, Token));
        failure.Armed = false;
        fixture.Context.ChangeTracker.Clear();

        Assert.Empty(await fixture.Context.Set<ConfigurationConsumerBindingRow>().ToArrayAsync(Token));
    }

    /// <summary>Fails any save that carries the binding-set half of a demand freeze.</summary>
    private sealed class FailWhenBindingSetFreezeIsSaved : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<ConfigurationConsumerBindingRow>().Any(entry =>
                    entry.State == EntityState.Added
                    && entry.Entity.ObjectKind == GovernedObjectKind.PublicStationBinding))
            {
                throw new InvalidOperationException("Injected failure writing the binding set freeze.");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
