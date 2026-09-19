using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.TaskTypeStationTestData;

namespace ControlServer.Tests;

/// <summary>
/// 规则表整张一个版本（REQ-0343）、绑定集每张图一条版本线（REQ-0337）：每个版本不可改写、自带快照与业务审计，
/// 内容不变不出新版本；唯一索引挡住同一 Station 被两个任务类型绑定（REQ-0334）。
/// </summary>
public sealed class TaskTypeStationStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RulesWrittenTwiceWithTheSameContentStayOneVersionAndAChangedFieldMakesASecondOne()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        Assert.Null(await fixture.Rules.ReadCurrentAsync(Token));

        TaskTypeStationVersionWrite<TaskTypeStationRuleVersion> first =
            await fixture.Rules.WriteVersionAsync(SixRules, Source, Now, Token);
        // The same table in another order is the same content.
        TaskTypeStationVersionWrite<TaskTypeStationRuleVersion> again =
            await fixture.Rules.WriteVersionAsync([.. SixRules.Reverse()], Source, Now.AddHours(1), Token);
        TaskTypeStationRule[] changed =
        [
            .. SixRules.Select(rule => rule.TaskType == TransportTaskTypes.DieToOven
                ? rule with { FixedEnd = TaskTypeFixedEnd.Origin }
                : rule)
        ];
        TaskTypeStationVersionWrite<TaskTypeStationRuleVersion> second =
            await fixture.Rules.WriteVersionAsync(changed, Source, Now.AddHours(2), Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.True(first.Created);
        Assert.Equal(1, first.Version.Version);
        Assert.False(again.Created);
        Assert.Equal(first.Version, again.Version with { Rules = first.Version.Rules });
        Assert.True(second.Created);
        Assert.Equal(2, second.Version.Version);

        TaskTypeStationRuleVersion current = Assert.IsType<TaskTypeStationRuleVersion>(
            await fixture.Rules.ReadCurrentAsync(Token));
        Assert.Equal(2, current.Version);
        Assert.Equal(TaskTypeFixedEnd.Origin, current.Rules.Single(rule => rule.TaskType == TransportTaskTypes.DieToOven).FixedEnd);

        TaskTypeStationRuleVersion readFirst = Assert.IsType<TaskTypeStationRuleVersion>(
            await fixture.Rules.ReadVersionAsync(1, Token));
        Assert.Equal(first.Version.SnapshotId, readFirst.SnapshotId);
        Assert.Equal(first.Version.ContentSha256, readFirst.ContentSha256);
        Assert.Equal(Now, readFirst.LoadedAt);
        Assert.Equal(Source, readFirst.Source);
        Assert.Equal(
            SixRules.OrderBy(rule => rule.TaskType, StringComparer.Ordinal),
            readFirst.Rules);
        Assert.Null(await fixture.Rules.ReadVersionAsync(3, Token));
    }

    [Fact]
    public async Task EachMapHasItsOwnBindingSetVersionLineAndSameContentMakesNoNewVersion()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        long rules = (await fixture.Rules.WriteVersionAsync(SixRules, Source, Now, Token)).Version.Version;

        TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion> map25 = await fixture.Bindings.WriteVersionAsync(
            25, rules, [TransportTaskTypes.WireToGate], [GateBinding], null, Source, Now, Token);
        TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion> map26 = await fixture.Bindings.WriteVersionAsync(
            26, rules, [TransportTaskTypes.WireToGate], [GateBinding], 7, Source, Now, Token);
        // A different catalog revision, source and time are not content.
        TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion> map25Again = await fixture.Bindings.WriteVersionAsync(
            25, rules, [TransportTaskTypes.WireToGate], [GateBinding], 9, "preset:other", Now.AddHours(1), Token);
        TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion> map25Second = await fixture.Bindings.WriteVersionAsync(
            25, rules, [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire], [GateBinding, StagingBinding],
            null, Source, Now.AddHours(2), Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal((1, true), (map25.Version.Version, map25.Created));
        Assert.Equal((1, true), (map26.Version.Version, map26.Created));
        Assert.Equal((1, false), (map25Again.Version.Version, map25Again.Created));
        Assert.Equal((2, true), (map25Second.Version.Version, map25Second.Created));

        TaskTypeStationBindingSetVersion latest = Assert.IsType<TaskTypeStationBindingSetVersion>(
            await fixture.Bindings.ReadLatestAsync(25, Token));
        Assert.Equal(2, latest.Version);
        Assert.Equal(rules, latest.RuleVersion);
        Assert.Equal([TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate], latest.RequiredTaskTypes);
        Assert.Equal([StagingBinding, GateBinding], latest.Bindings);

        TaskTypeStationBindingSetVersion first = Assert.IsType<TaskTypeStationBindingSetVersion>(
            await fixture.Bindings.ReadVersionAsync(25, 1, Token));
        Assert.Equal(map25.Version.SnapshotId, first.SnapshotId);
        Assert.Null(first.CatalogRevision);
        Assert.Equal([TransportTaskTypes.WireToGate], first.RequiredTaskTypes);
        Assert.Equal([GateBinding], first.Bindings);
        Assert.Equal(7, (await fixture.Bindings.ReadVersionAsync(26, 1, Token))!.CatalogRevision);
        Assert.Null(await fixture.Bindings.ReadVersionAsync(25, 3, Token));
    }

    [Fact]
    public async Task ANewRuleVersionUnderTheSameBindingsIsANewBindingSetVersion()
    {
        // The binding set names the rule version it was validated against; pointing old bindings at new rules is a
        // different version, not the same one.
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await fixture.Rules.WriteVersionAsync(SixRules, Source, Now, Token);
        await fixture.Bindings.WriteVersionAsync(25, 1, [TransportTaskTypes.WireToGate], [GateBinding], null, Source, Now, Token);

        TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion> next = await fixture.Bindings.WriteVersionAsync(
            25, 2, [TransportTaskTypes.WireToGate], [GateBinding], null, Source, Now, Token);

        Assert.True(next.Created);
        Assert.Equal(2, next.Version.Version);
        Assert.Equal(2, next.Version.RuleVersion);
    }

    [Fact]
    public async Task EveryRuleAndBindingSetVersionHasASnapshotAndExactlyOneBusinessAuditRecord()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        TaskTypeStationRuleVersion rules = (await fixture.Rules.WriteVersionAsync(SixRules, Source, Now, Token)).Version;
        await fixture.Rules.WriteVersionAsync(SixRules, Source, Now, Token);
        TaskTypeStationBindingSetVersion bindings = (await fixture.Bindings.WriteVersionAsync(
            25, rules.Version, [TransportTaskTypes.WireToGate], [GateBinding], null, Source, Now, Token)).Version;
        await fixture.Bindings.WriteVersionAsync(
            25, rules.Version, [TransportTaskTypes.WireToGate], [GateBinding], null, Source, Now, Token);

        GovernedConfigurationSnapshotRow ruleSnapshot = await fixture.Context.Set<GovernedConfigurationSnapshotRow>()
            .SingleAsync(row => row.ObjectKind == GovernedObjectKind.TaskTypeStationRule, Token);
        Assert.Equal(TaskTypeStationGovernance.RuleObjectId, ruleSnapshot.ObjectId);
        Assert.Equal(1, ruleSnapshot.Version);
        Assert.Equal(rules.SnapshotId, ruleSnapshot.SnapshotId);
        Assert.Equal(rules.ContentSha256, ruleSnapshot.ContentSha256);
        Assert.Contains(TransportTaskTypes.StagingToWire, ruleSnapshot.ContentJson, StringComparison.Ordinal);

        GovernedConfigurationSnapshotRow bindingSnapshot = await fixture.Context.Set<GovernedConfigurationSnapshotRow>()
            .SingleAsync(row => row.ObjectKind == GovernedObjectKind.PublicStationBinding, Token);
        Assert.Equal("map-25", bindingSnapshot.ObjectId);
        Assert.Equal(1, bindingSnapshot.Version);
        Assert.Equal(bindings.SnapshotId, bindingSnapshot.SnapshotId);
        Assert.Contains("关卡", bindingSnapshot.ContentJson, StringComparison.Ordinal);

        BusinessAuditRecordRow[] audit = await fixture.Context.Set<BusinessAuditRecordRow>().ToArrayAsync(Token);
        BusinessAuditRecordRow ruleAudit = Assert.Single(
            audit, row => row.Action == TaskTypeStationGovernance.RuleVersionLoadedAction);
        Assert.Equal(rules.SnapshotId, ruleAudit.SnapshotId);
        BusinessAuditRecordRow bindingAudit = Assert.Single(
            audit, row => row.Action == TaskTypeStationGovernance.BindingSetVersionLoadedAction);
        Assert.Equal(bindings.SnapshotId, bindingAudit.SnapshotId);
        Assert.Equal("map-25", bindingAudit.ObjectId);
    }

    [Fact]
    public async Task VersionRowsAndTheirChildRowsRefuseToBeRewrittenOrDeleted()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await fixture.Rules.WriteVersionAsync(SixRules, Source, Now, Token);
        await fixture.Bindings.WriteVersionAsync(25, 1, [TransportTaskTypes.WireToGate], [GateBinding], null, Source, Now, Token);
        fixture.Context.ChangeTracker.Clear();

        async Task AssertRefused(Action<ControlServerDbContext> change)
        {
            change(fixture.Context);
            await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(
                () => fixture.Context.SaveChangesAsync(Token));
            fixture.Context.ChangeTracker.Clear();
        }

        await AssertRefused(context => context.Set<TaskTypeStationRuleRow>()
            .Single(row => row.TaskType == TransportTaskTypes.WireToGate).FixedEnd = TaskTypeFixedEnd.Origin);
        await AssertRefused(context => context.Set<TaskTypeStationRuleVersionRow>().Single().Source = "rewritten");
        await AssertRefused(context => context.Set<TaskTypeStationBindingRow>().Single().StationRiotId = 211);
        await AssertRefused(context => context.Set<TaskTypeStationBindingSetVersionRow>().Single().CatalogRevision = 3);
        await AssertRefused(context =>
            context.Set<TaskTypeStationRequirementRow>().Remove(context.Set<TaskTypeStationRequirementRow>().Single()));
        await AssertRefused(context =>
            context.Set<TaskTypeStationBindingRow>().Remove(context.Set<TaskTypeStationBindingRow>().Single()));

        Assert.Equal(
            TaskTypeFixedEnd.Destination,
            (await fixture.Rules.ReadCurrentAsync(Token))!.Rules.Single(rule => rule.TaskType == TransportTaskTypes.WireToGate).FixedEnd);
        Assert.Equal([GateBinding], (await fixture.Bindings.ReadLatestAsync(25, Token))!.Bindings);
    }

    [Fact]
    public async Task TheDatabaseRefusesOneStationBoundToTwoTaskTypesAndNothingOfThatVersionIsLeftBehind()
    {
        // The store does no business validation of its own (that is the validator's job); this is the database's
        // own line, underneath every caller.
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await fixture.Rules.WriteVersionAsync(SixRules, Source, Now, Token);

        DbUpdateException refused = await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Bindings.WriteVersionAsync(
            25, 1, [TransportTaskTypes.WireToGate],
            [GateBinding, StagingBinding with { StationRiotId = GateBinding.StationRiotId }],
            null, Source, Now, Token));
        Assert.Contains("UNIQUE", refused.InnerException!.Message, StringComparison.Ordinal);
        fixture.Context.ChangeTracker.Clear();

        Assert.Null(await fixture.Bindings.ReadLatestAsync(25, Token));
        Assert.Empty(await fixture.Context.Set<GovernedConfigurationSnapshotRow>()
            .Where(row => row.ObjectKind == GovernedObjectKind.PublicStationBinding)
            .ToArrayAsync(Token));
    }

    [Fact]
    public async Task TheActivePointerNamesOneExistingVersionPerMap()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await fixture.Rules.WriteVersionAsync(SixRules, Source, Now, Token);
        await fixture.Bindings.WriteVersionAsync(25, 1, [TransportTaskTypes.WireToGate], [GateBinding], null, Source, Now, Token);
        await fixture.Bindings.WriteVersionAsync(
            25, 1, [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire], [GateBinding, StagingBinding],
            null, Source, Now, Token);
        Assert.Null(await fixture.Bindings.ReadActivePointerAsync(25, Token));
        Assert.Null(await fixture.Bindings.ReadActiveAsync(25, Token));

        await fixture.Bindings.SetActiveAsync(25, 2, Now, Token);
        TaskTypeStationActivePointer pointer = await fixture.Bindings.SetActiveAsync(25, 1, Now.AddMinutes(5), Token);

        Assert.Equal(new TaskTypeStationActivePointer(25, 1, TaskTypeStationActivationState.Active, null, Now.AddMinutes(5)), pointer);
        Assert.Equal(pointer, await fixture.Bindings.ReadActivePointerAsync(25, Token));
        Assert.Equal(1, (await fixture.Bindings.ReadActiveAsync(25, Token))!.Version);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Bindings.SetActiveAsync(25, 3, Now, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Bindings.SetActiveAsync(26, 1, Now, Token));
        Assert.Equal(1, (await fixture.Bindings.ReadActivePointerAsync(25, Token))!.ActiveVersion);
    }
}
