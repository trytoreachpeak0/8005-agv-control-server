using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 绑定集整图版本化（control-server#161，批次6-05）：候选预览、激活、回滚、结果未知对账、解除暂停与审计。
/// 对真 SQLite 文件跑，入口是 <see cref="TaskTypeStationActivationService"/>。
/// </summary>
public sealed class TaskTypeStationActivationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DryRunPreviewsTheDifferenceAndTheInFlightImpactAndWritesNothingButOneAuditRecord()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        await harness.AddInFlightDemandAsync("D-GATE", TransportTaskTypes.WireToGate, 1, 210);
        IReadOnlyDictionary<string, long> before = await harness.CountRowsAsync();
        TaskTypeStationBinding movedGate = new(TransportTaskTypes.WireToGate, 330, "氮气柜", "SITE-CHECK-GATE-MOVED");

        TaskTypeStationActivationResult result = await harness.Default().ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(movedGate, TaskTypeStationActivationHarness.Staging), dryRun: true);

        Assert.Equal(TaskTypeStationActivationOutcome.Previewed, result.Outcome);
        Assert.Empty(result.Violations);
        Assert.Equal(1, result.PreviousVersion);
        Assert.Null(result.TargetVersion);
        Assert.Equal(
            [
                (TransportTaskTypes.StagingToWire, (int?)null, (int?)305),
                (TransportTaskTypes.WireToGate, 210, 330)
            ],
            result.Changes.Select(change => (change.TaskType, change.Before?.StationRiotId, change.After?.StationRiotId)));
        Assert.Equal(1, result.Impact.InFlightDemandCount);
        TaskTypeStationInFlightImpact affected = Assert.Single(result.Impact.Affected);
        Assert.Equal(("D-GATE", 1L, (int?)210, (int?)330),
            (affected.DemandId, affected.FrozenBindingSetVersion, affected.FrozenStationRiotId, affected.CandidateStationRiotId));

        IReadOnlyDictionary<string, long> after = await harness.CountRowsAsync();
        Assert.Equal(before["BusinessAuditRecords"] + 1, after["BusinessAuditRecords"]);
        Assert.Equal(
            before.Where(pair => pair.Key != "BusinessAuditRecords"),
            after.Where(pair => pair.Key != "BusinessAuditRecords"));
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
        BusinessAuditRecordRow audit = (await harness.AuditAsync())[^1];
        Assert.Equal(TaskTypeStationActivationAuditActions.Previewed, audit.Action);
        Assert.Equal(GovernanceActionOutcome.Succeeded, audit.Outcome);
    }

    [Fact]
    public async Task ActivationSwitchesTheWholeMapAtOnceAfterTheFirstStepHeldEveryRequiredTaskType()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();

        TaskTypeStationActivationResult result = await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        Assert.Equal(TaskTypeStationActivationOutcome.Activated, result.Outcome);
        Assert.Equal((1L, 2L), (result.PreviousVersion, result.TargetVersion));
        Assert.Equal("25|2|ACTIVE|<null>", await harness.PointerRowAsync());
        TaskTypeStationBindingSetVersion active = (await harness.Default().Bindings.ReadActiveAsync(25, Token))!;
        Assert.Equal([305, 210], active.Bindings.Select(binding => binding.StationRiotId));
        Assert.Equal(TaskTypeStationCatalogEvidence.RevisionOf(TaskTypeStationActivationHarness.Catalog.ContentSha256), active.CatalogRevision);

        IReadOnlyList<TaskTypeStationHoldRow> holds = await harness.HoldsAsync();
        Assert.Equal(
            [TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate],
            holds.Select(hold => hold.TaskType).Order(StringComparer.Ordinal));
        Assert.All(holds, hold =>
        {
            Assert.Equal(TaskTypeStationHoldSource.ActivationResultUnknown, hold.Source);
            Assert.NotNull(hold.ReleasedAt);
        });
        Assert.Equal(
            [
                (TaskTypeStationActivationAuditActions.Started, GovernanceActionOutcome.ResultUnknown),
                (TaskTypeStationActivationAuditActions.Activated, GovernanceActionOutcome.Succeeded)
            ],
            (await harness.AuditAsync())
                .Where(row => row.Action.StartsWith("TASK_TYPE_STATION_BINDING_SET_ACTIVAT", StringComparison.Ordinal))
                .Select(row => (row.Action, row.Outcome)));
    }

    [Fact]
    public async Task SecondStepFailingBeforeCommitLeavesTheOldVersionWholeAndEveryRequiredTaskTypeHeld()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        CommitFault fault = new(new TimeoutException("SQLite commit did not return within the lock timeout."));
        TaskTypeStationActivationHarness.Stack stack = TaskTypeStationActivationHarness.StackOver(
            harness.NewContext(fault), inner => new FaultOnComplete(inner, fault));

        TaskTypeStationActivationResult result = await stack.ActivateAsync(TaskTypeStationActivationHarness.Candidate(
            new TaskTypeStationBinding(TransportTaskTypes.WireToGate, 330, "氮气柜", "SITE-CHECK-GATE-MOVED"),
            TaskTypeStationActivationHarness.Staging));

        Assert.Equal(TaskTypeStationActivationOutcome.ResultUnknown, result.Outcome);
        Assert.Equal(1, fault.Thrown);
        // Whole old version: the pointer never moved, and version 1 still reads back exactly as it was.
        Assert.Equal("25|1|ACTIVATION_UNKNOWN|2", await harness.PointerRowAsync());
        TaskTypeStationBindingSetVersion active = (await harness.Default().Bindings.ReadActiveAsync(25, Token))!;
        Assert.Equal((1L, 210), (active.Version, Assert.Single(active.Bindings).StationRiotId));

        IReadOnlyList<TaskTypeStationHoldRow> holds = await harness.HoldsAsync();
        Assert.Equal(
            [TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate],
            holds.Where(hold => hold.ReleasedAt is null).Select(hold => hold.TaskType).Order(StringComparer.Ordinal));
        Assert.All(holds, hold => Assert.Equal(TaskTypeStationHoldSource.ActivationResultUnknown, hold.Source));

        IReadOnlyList<BusinessAuditRecordRow> audit = await harness.AuditAsync();
        Assert.DoesNotContain(audit, row => row.Action == TaskTypeStationActivationAuditActions.Activated);
        BusinessAuditRecordRow last = audit[^1];
        Assert.Equal(
            (TaskTypeStationActivationAuditActions.ResultUnknown, GovernanceActionOutcome.TimedOut),
            (last.Action, last.Outcome));
    }

    [Fact]
    public async Task ReadBackThatContradictsTheTargetPutsTheMapBackOnHoldAndNeverSaysActivated()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = TaskTypeStationActivationHarness.StackOver(
            harness.NewContext(),
            inner => new AfterComplete(inner, () => harness.ExecuteAsync(
                "UPDATE TaskTypeStationActiveBindingSets SET ActiveVersion = 1 WHERE MapId = 25")));

        TaskTypeStationActivationResult result = await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        Assert.Equal(TaskTypeStationActivationOutcome.ResultUnknown, result.Outcome);
        Assert.Contains("reads back as version 1", result.Detail, StringComparison.Ordinal);
        Assert.Equal("25|1|ACTIVATION_UNKNOWN|2", await harness.PointerRowAsync());
        Assert.Equal(
            [TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate],
            (await harness.HoldsAsync()).Where(hold => hold.ReleasedAt is null)
                .Select(hold => hold.TaskType).Order(StringComparer.Ordinal));
        IReadOnlyList<BusinessAuditRecordRow> audit = await harness.AuditAsync();
        Assert.DoesNotContain(audit, row => row.Action == TaskTypeStationActivationAuditActions.Activated);
        Assert.Equal(
            (TaskTypeStationActivationAuditActions.ResultUnknown, GovernanceActionOutcome.ResultUnknown),
            (audit[^1].Action, audit[^1].Outcome));
    }

    [Fact]
    public async Task ProcessDyingBetweenTheStepsLeavesTheMapHeldAcrossARestartUntilReconciliationFindsThePreviousVersion()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        ControlServer.Infrastructure.Persistence.ControlServerDbContext dying = harness.NewContext();
        TaskTypeStationActivationHarness.Stack stack = TaskTypeStationActivationHarness.StackOver(
            dying, inner => new DieBeforeComplete(inner, dying));

        await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        // After the restart: nothing after the first step reached the database.
        TaskTypeStationActivationHarness.Stack restarted = harness.Default();
        Assert.Equal("25|1|ACTIVATION_UNKNOWN|2", await harness.PointerRowAsync());
        Assert.Equal(
            [TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate],
            (await harness.HoldsAsync()).Where(hold => hold.ReleasedAt is null)
                .Select(hold => hold.TaskType).Order(StringComparer.Ordinal));
        Assert.True(await restarted.Holds.IsHeldAsync(25, TransportTaskTypes.WireToGate, Token));
        Assert.Equal(
            [TaskTypeStationActivationAuditActions.Started],
            (await harness.AuditAsync()).Where(row => row.Action.StartsWith("TASK_TYPE_STATION_BINDING_SET_ACTIVAT", StringComparison.Ordinal))
                .Select(row => row.Action));
        TaskTypeStationActivationAttempt open = (await restarted.Activations.ReadOpenAttemptAsync(25, Token))!;
        Assert.Equal((1L, 2L), (open.PreviousVersion, open.TargetVersion));

        TaskTypeStationReconciliationResult first = await restarted.Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(1), Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.PreviousActive, first.Conclusion);
        Assert.Equal((1L, 2L, 1L), (first.PreviousVersion, first.TargetVersion, first.ActiveVersion));
        Assert.Equal(2, first.ReleasedHoldIds.Count);
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
        Assert.All(await harness.HoldsAsync(), hold => Assert.NotNull(hold.ReleasedAt));

        // Reconciling again finds nothing open, and writes its own record next to the first one, which stays as it was.
        BusinessAuditRecordRow firstRecord = (await harness.AuditAsync()).Single(row => row.AuditRecordId == first.AuditRecordId);
        TaskTypeStationReconciliationResult second = await restarted.Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(2), Token);
        Assert.Equal(TaskTypeStationReconciliationConclusion.NothingToReconcile, second.Conclusion);
        IReadOnlyList<BusinessAuditRecordRow> reconciled = [.. (await harness.AuditAsync())
            .Where(row => row.Action == TaskTypeStationActivationAuditActions.Reconciled)];
        Assert.Equal([first.AuditRecordId, second.AuditRecordId], reconciled.Select(row => row.AuditRecordId));
        Assert.Equal(firstRecord.DetailJson, reconciled[0].DetailJson);
        Assert.Contains("\"conclusion\":\"PREVIOUS_ACTIVE\"", reconciled[0].DetailJson, StringComparison.Ordinal);
        Assert.Contains("\"conclusion\":\"NOTHING_TO_RECONCILE\"", reconciled[1].DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecondStepThatCommitsButThrowsIsStillUnknownUntilReconciliationReadsTheTargetBack()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        CommitFault fault = new(new InvalidOperationException("The connection dropped after COMMIT was sent.")) { AfterCommit = true };
        TaskTypeStationActivationHarness.Stack stack = TaskTypeStationActivationHarness.StackOver(
            harness.NewContext(fault), inner => new FaultOnComplete(inner, fault));

        TaskTypeStationActivationResult result = await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        // The switch did land, but nothing here saw it land: the map is held and no record says activated.
        Assert.Equal(TaskTypeStationActivationOutcome.ResultUnknown, result.Outcome);
        Assert.Equal("25|2|ACTIVATION_UNKNOWN|2", await harness.PointerRowAsync());
        Assert.Equal(2, (await harness.HoldsAsync()).Count(hold => hold.ReleasedAt is null));
        Assert.DoesNotContain(await harness.AuditAsync(), row => row.Action == TaskTypeStationActivationAuditActions.Activated);

        TaskTypeStationReconciliationResult reconciled = await harness.Default().Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(1), Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.TargetActive, reconciled.Conclusion);
        Assert.Equal(2, reconciled.ActiveVersion);
        Assert.Equal("25|2|ACTIVE|<null>", await harness.PointerRowAsync());
        Assert.All(await harness.HoldsAsync(), hold => Assert.NotNull(hold.ReleasedAt));
    }

    [Fact]
    public async Task ReconciliationThatReadsNeitherVersionWholeKeepsTheHoldsAndEachRunKeepsItsOwnRecord()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying)).ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));
        // Version 1's rows no longer hash to what version 1 recorded.
        await harness.ExecuteAsync(
            "UPDATE TaskTypeStationBindings SET StationName = '关卡-改' WHERE MapId = 25 AND Version = 1");
        TaskTypeStationActivationHarness.Stack restarted = harness.Default();

        TaskTypeStationReconciliationResult first = await restarted.Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(1), Token);
        TaskTypeStationReconciliationResult second = await restarted.Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(2), Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.Contradictory, first.Conclusion);
        Assert.Equal(TaskTypeStationReconciliationConclusion.Contradictory, second.Conclusion);
        Assert.Empty(first.ReleasedHoldIds);
        Assert.Contains("but its header says", first.Detail, StringComparison.Ordinal);
        Assert.Equal("25|1|ACTIVATION_UNKNOWN|2", await harness.PointerRowAsync());
        Assert.Equal(2, (await harness.HoldsAsync()).Count(hold => hold.ReleasedAt is null));
        BusinessAuditRecordRow[] reconciled = [.. (await harness.AuditAsync())
            .Where(row => row.Action == TaskTypeStationActivationAuditActions.Reconciled)];
        Assert.Equal([first.AuditRecordId, second.AuditRecordId], reconciled.Select(row => row.AuditRecordId));
        Assert.All(reconciled, row =>
        {
            Assert.Equal(GovernanceActionOutcome.ResultUnknown, row.Outcome);
            Assert.Contains("\"conclusion\":\"CONTRADICTORY\"", row.DetailJson, StringComparison.Ordinal);
        });
    }

    public static TheoryData<string, string, string> Rejections => new()
    {
        { "catalog-stale", TaskTypeStationReasonCodes.BindingCatalogNotFresh, "more than the approved 300 s" },
        { "station-missing", TaskTypeStationReasonCodes.BindingStationNotInCatalog, "has no station 999" },
        { "other-map", TaskTypeStationReasonCodes.BindingStationNotInCatalog, "The catalog is for Map 26, not Map 25" },
        { "task-type-twice", TaskTypeStationReasonCodes.BindingDuplicate, "WIRE_TO_GATE is bound 2 times" },
        { "station-reused", TaskTypeStationReasonCodes.StationReused, "is bound to 2 task types" },
        { "required-unbound", TaskTypeStationReasonCodes.BindingRequiredMissing, "STAGING_TO_WIRE is in this map's requirement set" },
        { "rule-not-current", TaskTypeStationActivationReasonCodes.RuleVersionNotCurrent, "the current rule version is 2" },
        { "no-site-verification", TaskTypeStationReasonCodes.BindingSiteVerificationMissing, "without a site verification reference" },
    };

    /// <summary>
    /// 激活前重验的八种拒绝（REQ-0337、REQ-0338、REQ-0343）。被拒时什么都不写：没有新版本、没有暂停、指针不动，只有一条被拒审计——
    /// 这也是「未经校验的非法值写不进去」：绑定集只经这一条校验过的路写入。
    /// </summary>
    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task ActivationRevalidatesAndRefusesWithEveryReasonListed(string scenario, string reasonCode, string detail)
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationBinding gate = TaskTypeStationActivationHarness.Gate;
        TaskTypeStationBinding staging = TaskTypeStationActivationHarness.Staging;
        RiotMapStationCatalogSnapshot catalog = TaskTypeStationActivationHarness.Catalog;
        TaskTypeStationCandidate candidate = TaskTypeStationActivationHarness.Candidate(gate, staging);
        switch (scenario)
        {
            case "catalog-stale":
                await harness.ConfirmCatalogAsync(catalog, TaskTypeStationActivationHarness.Now.AddMinutes(-10));
                break;
            case "station-missing":
                candidate = TaskTypeStationActivationHarness.Candidate(gate, staging with { StationRiotId = 999 });
                break;
            case "other-map":
                catalog = TaskTypeStationCatalogEvidence.Supplied(26, [new(210, "关卡"), new(305, "派工待送取货")], TaskTypeStationActivationHarness.Now);
                await harness.ConfirmCatalogAsync(catalog, TaskTypeStationActivationHarness.Now.AddSeconds(-30));
                break;
            case "task-type-twice":
                candidate = TaskTypeStationActivationHarness.Candidate(gate, gate with { StationRiotId = 330, StationName = "氮气柜" });
                break;
            case "station-reused":
                candidate = TaskTypeStationActivationHarness.Candidate(gate, staging with { StationRiotId = 210, StationName = "关卡" });
                break;
            case "required-unbound":
                candidate = new TaskTypeStationCandidate(
                    25, 1, [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire], [gate]);
                break;
            case "rule-not-current":
                await harness.Default().Rules.WriteVersionAsync(
                    [.. TaskTypeStationTestData.SixRules.Where(rule => rule.TaskType != TransportTaskTypes.WireToNitrogen)],
                    "preset:newer", TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);
                break;
            case "no-site-verification":
                candidate = TaskTypeStationActivationHarness.Candidate(gate, staging with { SiteVerificationRef = " " });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
        IReadOnlyDictionary<string, long> before = await harness.CountRowsAsync();

        TaskTypeStationActivationResult result = await harness.Default().ActivateAsync(candidate, catalog: catalog);

        Assert.Equal(TaskTypeStationActivationOutcome.Rejected, result.Outcome);
        Assert.Contains(result.Violations, violation => violation.ReasonCode == reasonCode
            && violation.Detail.Contains(detail, StringComparison.Ordinal));
        IReadOnlyDictionary<string, long> after = await harness.CountRowsAsync();
        Assert.Equal(before["BusinessAuditRecords"] + 1, after["BusinessAuditRecords"]);
        Assert.Equal(
            before.Where(pair => pair.Key != "BusinessAuditRecords"),
            after.Where(pair => pair.Key != "BusinessAuditRecords"));
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
        BusinessAuditRecordRow rejected = (await harness.AuditAsync())[^1];
        Assert.Equal(
            (TaskTypeStationActivationAuditActions.Rejected, GovernanceActionOutcome.Failed),
            (rejected.Action, rejected.Outcome));
        Assert.Contains(reasonCode, rejected.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// 回滚是把历史版本的内容当作一次新的激活再走一遍全部校验，不是把指针拨回去：它产生新版本号。激活、回滚交错，同一内容激活两次，
    /// 每一次都有自己的审计，后一次不覆盖前一次。
    /// </summary>
    [Fact]
    public async Task RollbackIsAFreshlyValidatedActivationOfOldContentAndEveryActivationKeepsItsOwnRecord()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        TaskTypeStationCandidate both = TaskTypeStationActivationHarness.Candidate(
            TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging);

        TaskTypeStationActivationResult activated = await stack.ActivateAsync(both);
        TaskTypeStationActivationResult again = await stack.ActivateAsync(both);
        TaskTypeStationActivationResult rolledBack = await stack.Service.RollbackAsync(
            25, 1, TaskTypeStationActivationHarness.Catalog, TaskTypeStationActivationHarness.Request, dryRun: false,
            TaskTypeStationActivationHarness.Now, Token);
        TaskTypeStationActivationResult forward = await stack.ActivateAsync(both);

        Assert.Equal(
            [
                (TaskTypeStationActivationOutcome.Activated, 1L, 2L),
                (TaskTypeStationActivationOutcome.Activated, 2L, 2L),
                (TaskTypeStationActivationOutcome.Activated, 2L, 3L),
                (TaskTypeStationActivationOutcome.Activated, 3L, 4L)
            ],
            new[] { activated, again, rolledBack, forward }.Select(result =>
                (result.Outcome, result.PreviousVersion ?? 0, result.TargetVersion ?? 0)));
        Assert.Equal(TaskTypeStationRequestCategory.Rollback, rolledBack.RequestCategory);
        TaskTypeStationBindingSetVersion three = (await stack.Bindings.ReadVersionAsync(25, 3, Token))!;
        Assert.Equal(210, Assert.Single(three.Bindings).StationRiotId);
        Assert.StartsWith("fieldops:rollback:v1:", three.Source, StringComparison.Ordinal);
        Assert.Equal("25|4|ACTIVE|<null>", await harness.PointerRowAsync());

        BusinessAuditRecordRow[] records = [.. (await harness.AuditAsync())
            .Where(row => row.Action == TaskTypeStationActivationAuditActions.Activated)];
        Assert.Equal(
            [.. new[] { activated, again, rolledBack, forward }.Select(result => result.AuditRecordIds.Single())],
            records.Select(row => row.AuditRecordId));
        Assert.Equal([2L, 2L, 3L, 4L], records.Select(row => row.Version ?? 0));
        Assert.Contains("\"requestCategory\":\"ROLLBACK\"", records[2].DetailJson, StringComparison.Ordinal);
        Assert.Contains("\"rollbackOfVersion\":1", records[2].DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RollbackRevalidatesTheOldContentAndRefusesWhatNoLongerHolds()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        await stack.ActivateAsync(TaskTypeStationActivationHarness.Candidate(
            TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));
        // Station 210 has since left the catalog the server confirms.
        RiotMapStationCatalogSnapshot without210 = TaskTypeStationCatalogEvidence.Supplied(
            25, [.. TaskTypeStationActivationHarness.CatalogStations.Where(station => station.StationId != 210)],
            TaskTypeStationActivationHarness.Now);
        await harness.ConfirmCatalogAsync(without210, TaskTypeStationActivationHarness.Now.AddSeconds(-10));

        TaskTypeStationActivationResult refused = await stack.Service.RollbackAsync(
            25, 1, without210, TaskTypeStationActivationHarness.Request, dryRun: false, TaskTypeStationActivationHarness.Now, Token);
        TaskTypeStationActivationResult missing = await stack.Service.RollbackAsync(
            25, 9, without210, TaskTypeStationActivationHarness.Request, dryRun: false, TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(TaskTypeStationActivationOutcome.Rejected, refused.Outcome);
        Assert.Contains(refused.Violations, violation =>
            violation.ReasonCode == TaskTypeStationReasonCodes.BindingStationNotInCatalog && violation.StationRiotId == 210);
        Assert.Equal(TaskTypeStationActivationOutcome.Rejected, missing.Outcome);
        Assert.Equal(TaskTypeStationActivationReasonCodes.VersionNotFound, Assert.Single(missing.Violations).ReasonCode);
        Assert.Equal("25|2|ACTIVE|<null>", await harness.PointerRowAsync());
        Assert.Null(await stack.Bindings.ReadVersionAsync(25, 3, Token));
    }

    /// <summary>
    /// REQ-0345 配置半边：激活、回滚、结果未知与对账前后，需求冻结行、冻结站点行、旅程行、订单行与需求行逐行相同。
    /// </summary>
    [Fact]
    public async Task ActivationRollbackAndReconciliationLeaveEveryInFlightRowExactlyAsItWas()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        await harness.AddInFlightDemandAsync("D-GATE-1", TransportTaskTypes.WireToGate, 1, 210);
        await harness.AddInFlightDemandAsync("D-GATE-2", TransportTaskTypes.WireToGate, 1, 210);
        IReadOnlyDictionary<string, IReadOnlyList<string>> before = await harness.SnapshotInFlightRowsAsync();
        Assert.All(TaskTypeStationActivationHarness.InFlightTables, table => Assert.NotEmpty(before[table]));
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        TaskTypeStationBinding movedGate = new(TransportTaskTypes.WireToGate, 330, "氮气柜", "SITE-CHECK-GATE-MOVED");

        TaskTypeStationActivationResult moved = await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(movedGate, TaskTypeStationActivationHarness.Staging));
        TaskTypeStationActivationResult back = await stack.Service.RollbackAsync(
            25, 1, TaskTypeStationActivationHarness.Catalog, TaskTypeStationActivationHarness.Request, dryRun: false,
            TaskTypeStationActivationHarness.Now, Token);
        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying))
            .ActivateAsync(TaskTypeStationActivationHarness.Candidate(movedGate));
        TaskTypeStationReconciliationResult reconciled = await harness.Default().Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(TaskTypeStationActivationOutcome.Activated, moved.Outcome);
        Assert.Equal(2, moved.Impact.Affected.Count);
        Assert.Equal(TaskTypeStationActivationOutcome.Activated, back.Outcome);
        Assert.Equal(TaskTypeStationReconciliationConclusion.PreviousActive, reconciled.Conclusion);
        IReadOnlyDictionary<string, IReadOnlyList<string>> after = await harness.SnapshotInFlightRowsAsync();
        Assert.All(TaskTypeStationActivationHarness.InFlightTables, table => Assert.Equal(before[table], after[table]));
    }

    /// <summary>
    /// 激活事务直接写的暂停行：重试、重启、重复对账都不会让同一任务类型出现第二条未解除的「激活结果未知」暂停；撤销按本次尝试的
    /// HoldId 做，别的尝试留下的同来源暂停与人工暂停一条不碰。
    /// </summary>
    [Fact]
    public async Task ActivationHoldsAreNeverDuplicatedAndOnlyThisAttemptsHoldsAreReleased()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationCandidate both = TaskTypeStationActivationHarness.Candidate(
            TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging);
        // A hold of the same source left by some other attempt, and a manual hold: neither belongs to what follows.
        await harness.ExecuteAsync(
            "INSERT INTO TaskTypeStationHolds (HoldId, MapId, TaskType, Source, ReasonCode, DetailJson, RaisedAt, RaisedBy) VALUES "
            + "('foreign-hold', 25, 'WIRE_TO_OPTICAL', 'ACTIVATION_RESULT_UNKNOWN', 'TASK_TYPE_ACTIVATION_RESULT_UNKNOWN', "
            + "'{\"attemptId\":\"another-attempt\",\"targetVersion\":9,\"previousVersion\":8}', '2026-09-19 07:00:00+00:00', 'fieldops:activation:another-attempt')");
        TaskTypeStationHold manual = await harness.Default().Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "MANUAL_TIGHTEN", "{}", "operator",
            TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);

        async Task AssertOneOpenActivationHoldPerTaskTypeAsync()
        {
            IReadOnlyList<TaskTypeStationHoldRow> open = [.. (await harness.HoldsAsync())
                .Where(hold => hold.ReleasedAt is null && hold.Source == TaskTypeStationHoldSource.ActivationResultUnknown
                    && hold.HoldId != "foreign-hold")];
            Assert.Equal(
                [TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate],
                open.Select(hold => hold.TaskType).Order(StringComparer.Ordinal));
        }

        // The second step commits and then throws: step 2 released the holds, the unknown path raises them again -- once.
        CommitFault fault = new(new InvalidOperationException("dropped after COMMIT")) { AfterCommit = true };
        TaskTypeStationActivationResult unknown = await TaskTypeStationActivationHarness.StackOver(
                harness.NewContext(fault), inner => new FaultOnComplete(inner, fault))
            .ActivateAsync(both);
        Assert.Equal(TaskTypeStationActivationOutcome.ResultUnknown, unknown.Outcome);
        await AssertOneOpenActivationHoldPerTaskTypeAsync();

        // Retrying while the result is unknown is refused and raises nothing.
        TaskTypeStationActivationResult retried = await harness.Default().ActivateAsync(both);
        Assert.Equal(TaskTypeStationActivationOutcome.Rejected, retried.Outcome);
        Assert.Contains(retried.Violations, violation =>
            violation.ReasonCode == TaskTypeStationActivationReasonCodes.ActivationPendingReconciliation);
        await AssertOneOpenActivationHoldPerTaskTypeAsync();

        // Re-marking the same attempt again, as a restarted process retrying the unknown path would, adds nothing.
        TaskTypeStationActivationHarness.Stack restarted = harness.Default();
        TaskTypeStationActivationAttempt open = (await restarted.Activations.ReadOpenAttemptAsync(25, Token))!;
        await restarted.Activations.MarkUnknownAsync(open, TaskTypeStationActivationHarness.Now, Token);
        await AssertOneOpenActivationHoldPerTaskTypeAsync();

        // Reconciling twice releases exactly this attempt's two holds, the second run nothing more.
        TaskTypeStationReconciliationResult first = await restarted.Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);
        TaskTypeStationReconciliationResult second = await harness.Default().Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);
        Assert.Equal(TaskTypeStationReconciliationConclusion.TargetActive, first.Conclusion);
        Assert.Equal(2, first.ReleasedHoldIds.Count);
        Assert.Equal(TaskTypeStationReconciliationConclusion.NothingToReconcile, second.Conclusion);
        Assert.Empty(second.ReleasedHoldIds);

        IReadOnlyList<TaskTypeStationHoldRow> holds = await harness.HoldsAsync();
        Assert.Null(holds.Single(hold => hold.HoldId == "foreign-hold").ReleasedAt);
        Assert.Null(holds.Single(hold => hold.HoldId == manual.HoldId).ReleasedAt);
        Assert.DoesNotContain(holds, hold => hold.ReleasedAt is null
            && hold.Source == TaskTypeStationHoldSource.ActivationResultUnknown && hold.HoldId != "foreign-hold");
        // Every activation hold this attempt ever raised names this attempt, and each release names it too.
        Assert.All(
            holds.Where(hold => hold.Source == TaskTypeStationHoldSource.ActivationResultUnknown && hold.HoldId != "foreign-hold"),
            hold =>
            {
                Assert.Contains(unknown.AttemptId, hold.DetailJson, StringComparison.Ordinal);
                Assert.Equal("fieldops:activation:" + unknown.AttemptId, hold.ReleasedBy);
            });
    }

    [Fact]
    public async Task ActivationNeverReleasesAManualOrCatalogChangeHold()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        TaskTypeStationHold manual = await stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "MANUAL_TIGHTEN", "{}", "operator",
            TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);
        TaskTypeStationHold catalog = await stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.CatalogChange, "CATALOG_RENAMED", "{}", "server",
            TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);

        TaskTypeStationActivationResult result = await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        Assert.Equal(TaskTypeStationActivationOutcome.Activated, result.Outcome);
        IReadOnlyList<TaskTypeStationHoldRow> holds = await harness.HoldsAsync();
        Assert.Null(holds.Single(hold => hold.HoldId == manual.HoldId).ReleasedAt);
        Assert.Null(holds.Single(hold => hold.HoldId == catalog.HoldId).ReleasedAt);
        Assert.All(
            holds.Where(hold => hold.Source == TaskTypeStationHoldSource.ActivationResultUnknown),
            hold => Assert.NotNull(hold.ReleasedAt));
    }

    [Fact]
    public async Task ReleasingAHoldRecordsTheReleaseAndKeepsTheHoldRowAndLeavesActivationHoldsAlone()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        TaskTypeStationHold manual = await stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "MANUAL_TIGHTEN", "{}", "operator",
            TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);
        TaskTypeStationHold catalog = await stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.CatalogChange, "CATALOG_RENAMED", "{}", "server",
            TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);

        TaskTypeStationHoldReleaseResult result = await stack.Service.ReleaseHoldAsync(
            25, TransportTaskTypes.WireToGate, "SITE-RECHECK-0919", TaskTypeStationActivationHarness.Catalog,
            TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(TaskTypeStationHoldReleaseOutcome.Released, result.Outcome);
        Assert.Empty(result.Violations);
        Assert.Equal(
            new[] { catalog.HoldId, manual.HoldId }.Order(StringComparer.Ordinal),
            result.Released.Select(hold => hold.HoldId).Order(StringComparer.Ordinal));
        IReadOnlyList<TaskTypeStationHoldRow> holds = await harness.HoldsAsync();
        Assert.Equal(2, holds.Count);
        Assert.All(holds, hold => Assert.NotNull(hold.ReleasedAt));
        Assert.False(await stack.Holds.IsHeldAsync(25, TransportTaskTypes.WireToGate, Token));
        BusinessAuditRecordRow audit = (await harness.AuditAsync())[^1];
        Assert.Equal(
            (TaskTypeStationActivationAuditActions.HoldReleased, GovernanceActionOutcome.Succeeded),
            (audit.Action, audit.Outcome));
        Assert.Contains("SITE-RECHECK-0919", audit.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// REQ-0348：预览、被拒、激活、回滚、结果未知、对账、解除暂停每个动作都写不可改写审计，字段齐全；操作者是部署标识并标不可归属；
    /// 改写与删除被拒；<c>export-audit</c> 用的那条导出路径能全部取回。
    /// </summary>
    [Fact]
    public async Task EveryActionLeavesAnImmutableExportableAuditRecordWithTheRequiredFields()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        await harness.AddInFlightDemandAsync("D-GATE", TransportTaskTypes.WireToGate, 1, 210);
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        TaskTypeStationBinding movedGate = new(TransportTaskTypes.WireToGate, 330, "氮气柜", "SITE-CHECK-GATE-MOVED");
        TaskTypeStationCandidate candidate = TaskTypeStationActivationHarness.Candidate(movedGate, TaskTypeStationActivationHarness.Staging);

        List<string> ids = [];
        ids.AddRange((await stack.ActivateAsync(candidate, dryRun: true)).AuditRecordIds);
        ids.AddRange((await stack.ActivateAsync(TaskTypeStationActivationHarness.Candidate(movedGate, movedGate))).AuditRecordIds);
        TaskTypeStationActivationResult activated = await stack.ActivateAsync(candidate);
        ids.AddRange(activated.AuditRecordIds);
        ids.AddRange((await stack.Service.RollbackAsync(
            25, 1, TaskTypeStationActivationHarness.Catalog, TaskTypeStationActivationHarness.Request, false,
            TaskTypeStationActivationHarness.Now, Token)).AuditRecordIds);
        CommitFault fault = new(new TimeoutException("lock timeout"));
        ids.AddRange((await TaskTypeStationActivationHarness.StackOver(
                harness.NewContext(fault), inner => new FaultOnComplete(inner, fault))
            .ActivateAsync(candidate)).AuditRecordIds);
        ids.Add((await stack.Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token)).AuditRecordId);
        await stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "MANUAL_TIGHTEN", "{}", "operator",
            TaskTypeStationActivationHarness.Now, Token);
        TaskTypeStationHoldReleaseResult releasedHold = await stack.Service.ReleaseHoldAsync(
            25, TransportTaskTypes.WireToGate, "SITE-RECHECK", TaskTypeStationActivationHarness.Catalog,
            TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);
        Assert.True(releasedHold.Outcome == TaskTypeStationHoldReleaseOutcome.Released, string.Join("; ", releasedHold.Violations.Select(v => v.ReasonCode + " " + v.Detail)));
        ids.Add(releasedHold.AuditRecordId);

        IReadOnlyList<BusinessAuditRecordRow> audit = await harness.AuditAsync();
        Assert.Equal(
            [
                TaskTypeStationActivationAuditActions.Previewed,
                TaskTypeStationActivationAuditActions.Rejected,
                TaskTypeStationActivationAuditActions.Activated,
                TaskTypeStationActivationAuditActions.Activated,
                TaskTypeStationActivationAuditActions.ResultUnknown,
                TaskTypeStationActivationAuditActions.Reconciled,
                TaskTypeStationActivationAuditActions.HoldReleased
            ],
            ids.Select(id => audit.Single(row => row.AuditRecordId == id).Action));
        Assert.All(audit, row =>
        {
            Assert.StartsWith(AuditActorAttribution.DeploymentIdentityPrefix, row.ActorIdentity, StringComparison.Ordinal);
            Assert.Equal(AuditActorAttribution.NotAttributableToNaturalPerson, row.ActorAttribution);
        });

        // REQ-0348's fields, on the activation that moved the gate.
        using (System.Text.Json.JsonDocument detail = System.Text.Json.JsonDocument.Parse(
                   audit.Single(row => row.AuditRecordId == activated.AuditRecordIds.Single()).DetailJson))
        {
            System.Text.Json.JsonElement root = detail.RootElement;
            Assert.Equal(25, root.GetProperty("mapId").GetInt32());
            System.Text.Json.JsonElement gate = root.GetProperty("taskTypes").EnumerateArray()
                .Single(item => item.GetProperty("taskType").GetString() == TransportTaskTypes.WireToGate);
            Assert.Equal((210, "关卡"), (gate.GetProperty("before").GetProperty("stationRiotId").GetInt32(), gate.GetProperty("before").GetProperty("stationName").GetString()));
            Assert.Equal((330, "氮气柜"), (gate.GetProperty("after").GetProperty("stationRiotId").GetInt32(), gate.GetProperty("after").GetProperty("stationName").GetString()));
            Assert.Equal(
                TaskTypeStationCatalogEvidence.RevisionOf(TaskTypeStationActivationHarness.Catalog.ContentSha256),
                root.GetProperty("catalogRevision").GetInt64());
            Assert.Equal((1, 1), (root.GetProperty("ruleVersion").GetProperty("previous").GetInt64(), root.GetProperty("ruleVersion").GetProperty("next").GetInt64()));
            Assert.Equal((1, 2), (root.GetProperty("bindingSetVersion").GetProperty("previous").GetInt64(), root.GetProperty("bindingSetVersion").GetProperty("target").GetInt64()));
            Assert.Equal(1, root.GetProperty("impactPreview").GetProperty("inFlightDemandCount").GetInt32());
            Assert.Equal(1, root.GetProperty("activeTaskImpactCount").GetInt32());
            Assert.Equal(TaskTypeStationActivationHarness.Request.Reason, root.GetProperty("reason").GetString());
            Assert.Equal("FieldEngineer", root.GetProperty("selfReportedRole").GetString());
            Assert.Contains(
                "SITE-CHECK-GATE-MOVED",
                root.GetProperty("siteVerification").EnumerateArray().Select(item => item.GetProperty("reference").GetString()));
            Assert.True(root.GetProperty("validation").GetProperty("passed").GetBoolean());
            Assert.Equal("ACTIVATE", root.GetProperty("requestCategory").GetString());
            Assert.Equal("TARGET_ACTIVE", root.GetProperty("conclusion").GetString());
        }

        // Write-once: neither a rewrite nor a delete inside retention gets through.
        await using (ControlServerDbContext context = harness.NewContext())
        {
            BusinessAuditRecordRow row = await context.Set<BusinessAuditRecordRow>()
                .SingleAsync(candidateRow => candidateRow.AuditRecordId == activated.AuditRecordIds[0], Token);
            row.DetailJson = "{}";
            await Assert.ThrowsAsync<AuditRecordImmutabilityException>(() => context.SaveChangesAsync(Token));
            context.ChangeTracker.Clear();
            context.Remove(await context.Set<BusinessAuditRecordRow>()
                .SingleAsync(candidateRow => candidateRow.AuditRecordId == activated.AuditRecordIds[0], Token));
            await Assert.ThrowsAsync<AuditRecordImmutabilityException>(() => context.SaveChangesAsync(Token));
        }
        Assert.Equal(
            audit.Single(row => row.AuditRecordId == activated.AuditRecordIds[0]).DetailJson,
            (await harness.AuditAsync()).Single(row => row.AuditRecordId == activated.AuditRecordIds[0]).DetailJson);

        await using ControlServerDbContext exporting = harness.NewContext();
        IReadOnlyList<AuditExportRecord> exported = await AuditExport.ReadAsync(
            exporting, AuditTrail.Business, null, null, Token);
        Assert.Superset(ids.ToHashSet(StringComparer.Ordinal), exported.Select(record => record.AuditRecordId).ToHashSet(StringComparer.Ordinal));
    }

    public static TheoryData<string, string, string> ReleaseRejections => new()
    {
        { "catalog-stale", TaskTypeStationReasonCodes.BindingCatalogNotFresh, "more than the approved 300 s" },
        { "station-gone", TaskTypeStationReasonCodes.BindingStationNotInCatalog, "has no station 210" },
        { "name-changed", TaskTypeStationReasonCodes.BindingStationNotInCatalog, "is now named 关卡-北, not 关卡" },
        { "no-site-verification", TaskTypeStationReasonCodes.BindingSiteVerificationMissing, "without a site verification" },
    };

    /// <summary>解除暂停对当前新鲜目录做完整重验（REQ-0340 恢复半边）；不通过就拒绝，暂停原样保留。</summary>
    [Theory]
    [MemberData(nameof(ReleaseRejections))]
    public async Task ReleasingAHoldRevalidatesTheBindingAndRefusesWithTheReason(string scenario, string reasonCode, string detail)
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        TaskTypeStationHold manual = await stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "MANUAL_TIGHTEN", "{}", "operator",
            TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);
        RiotMapStationCatalogSnapshot catalog = TaskTypeStationActivationHarness.Catalog;
        string? siteVerification = "SITE-RECHECK-0919";
        switch (scenario)
        {
            case "catalog-stale":
                await harness.ConfirmCatalogAsync(catalog, TaskTypeStationActivationHarness.Now.AddMinutes(-10));
                break;
            case "station-gone":
                catalog = TaskTypeStationCatalogEvidence.Supplied(
                    25, [.. TaskTypeStationActivationHarness.CatalogStations.Where(station => station.StationId != 210)],
                    TaskTypeStationActivationHarness.Now);
                await harness.ConfirmCatalogAsync(catalog, TaskTypeStationActivationHarness.Now.AddSeconds(-10));
                break;
            case "name-changed":
                catalog = TaskTypeStationCatalogEvidence.Supplied(
                    25,
                    [.. TaskTypeStationActivationHarness.CatalogStations.Select(station =>
                        station.StationId == 210 ? station with { StationName = "关卡-北" } : station)],
                    TaskTypeStationActivationHarness.Now);
                await harness.ConfirmCatalogAsync(catalog, TaskTypeStationActivationHarness.Now.AddSeconds(-10));
                break;
            case "no-site-verification":
                siteVerification = "  ";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        TaskTypeStationHoldReleaseResult result = await stack.Service.ReleaseHoldAsync(
            25, TransportTaskTypes.WireToGate, siteVerification, catalog, TaskTypeStationActivationHarness.Request,
            TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(TaskTypeStationHoldReleaseOutcome.Rejected, result.Outcome);
        Assert.Contains(result.Violations, violation => violation.ReasonCode == reasonCode
            && violation.Detail.Contains(detail, StringComparison.Ordinal));
        Assert.Empty(result.Released);
        Assert.Null((await harness.HoldsAsync()).Single(hold => hold.HoldId == manual.HoldId).ReleasedAt);
        BusinessAuditRecordRow audit = (await harness.AuditAsync())[^1];
        Assert.Equal(
            (TaskTypeStationActivationAuditActions.HoldReleaseRejected, GovernanceActionOutcome.Failed),
            (audit.Action, audit.Outcome));
    }
}

/// <summary>
/// The process dies right after the first step commits: the connection goes away, and nothing the service tries afterwards
/// (re-marking, the result-unknown audit) reaches the database.
/// </summary>
internal sealed class DieBeforeComplete(ITaskTypeStationActivationStore inner, ControlServerDbContext context)
    : DelegatingActivationStore(inner)
{
    public override async Task CompleteAsync(
        TaskTypeStationActivationAttempt attempt, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await context.DisposeAsync();
        throw new OperationCanceledException("The process was terminated between the two steps.");
    }
}

/// <summary>Runs <paramref name="afterComplete"/> right after the real second step commits, as a racing writer would.</summary>
internal sealed class AfterComplete(ITaskTypeStationActivationStore inner, Func<Task> afterComplete)
    : DelegatingActivationStore(inner)
{
    public override async Task CompleteAsync(
        TaskTypeStationActivationAttempt attempt, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await base.CompleteAsync(attempt, at, cancellationToken);
        await afterComplete();
    }
}

/// <summary>Arms <see cref="CommitFault"/> for exactly the second step, so the first step commits for real.</summary>
internal sealed class FaultOnComplete(ITaskTypeStationActivationStore inner, CommitFault fault) : DelegatingActivationStore(inner)
{
    public override async Task CompleteAsync(
        TaskTypeStationActivationAttempt attempt, DateTimeOffset at, CancellationToken cancellationToken)
    {
        fault.Armed = true;
        try
        {
            await base.CompleteAsync(attempt, at, cancellationToken);
        }
        finally
        {
            fault.Armed = false;
        }
    }
}

/// <summary>Throws from the database's own commit, armed only while the second step runs.</summary>
internal sealed class CommitFault(Exception toThrow) : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
{
    public bool Armed { get; set; }

    public int Thrown { get; private set; }

    /// <summary>Throw once the commit has gone through, instead of before it.</summary>
    public bool AfterCommit { get; init; }

    public override Task TransactionCommittedAsync(
        System.Data.Common.DbTransaction transaction,
        Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (Armed && AfterCommit)
        {
            Thrown++;
            throw toThrow;
        }
        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> TransactionCommittingAsync(
        System.Data.Common.DbTransaction transaction,
        Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (Armed && !AfterCommit)
        {
            Thrown++;
            throw toThrow;
        }
        return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
    }
}
