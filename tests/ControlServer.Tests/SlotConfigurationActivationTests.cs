using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 整车仓位配置的原子激活下发与结果补报，服务端半边（REQ-0264、REQ-0265、REQ-0316）。
/// </summary>
/// <remarks>
/// 这里固化的是业务语义，本文件不碰传输也不碰序列化。#15 落地时协议 v2 消息 7／8 还没做，所以它
/// 一条切片 trait 都没有；线上那一半接好之后，传输层调的就是这几个方法，它们站在
/// <c>SlotConfigurationActivationCommand</c>／<c>Result</c> 背后，归 <c>FP-IS-14</c>。线上那一半
/// 自己的证据在 <c>SlotConfigurationActivationWireTests</c>。
/// </remarks>
public sealed class SlotConfigurationActivationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    public async Task AnActivationGoesOutReliableUnderTheSlotConfigurationRecoveryRoleAndWaitsForItsResult()
    {
        await using ActivationFixture fixture = await ActivationFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.BindAllSlotsAsync("AGV-01", model);

        SlotConfigurationActivationRow activation = await fixture.Coordinator.IssueActivationAsync(
            "AGV-01", model, Now, TestContext.Current.CancellationToken);

        Assert.Equal("RELIABLE", SlotConfigurationActivationDelivery.DeliveryClass);
        Assert.Equal("SLOT_CONFIGURATION", activation.RecoveryRole);
        Assert.Equal(SlotConfigurationActivationState.PendingResult, activation.State);
        Assert.Null(activation.ResultReceivedAt);
        Assert.Null(activation.ResultJson);

        // 每次激活都带 #9 的完整快照与审计。
        Assert.NotNull(activation.SnapshotId);
        GovernedConfigurationSnapshot snapshot = await fixture.RequireSnapshotAsync(
            $"AGV-01:{model}", activation.ConfigurationVersion);
        // 指纹是跨端契约，不是这份治理快照对自己内容的摘要：消息 7 不带配置内容，那次激活是一次核验，
        // 车算自己手上那份的指纹与它比，所以它由两端共用的规范化规则算出来。快照的 ContentSha256 仍然
        // 是 #9 的审计事实，只是不再兼任这个角色——两者回答的是不同的问题。
        Assert.Equal(
            SlotConfigurationFingerprint.Compute(ApprovedSlotHardwareFacts.IoBindings),
            activation.Fingerprint);
        Assert.NotEqual(snapshot.ContentSha256, activation.Fingerprint);
        BusinessAuditRecordRow issued = await fixture.Context.Set<BusinessAuditRecordRow>().AsNoTracking()
            .SingleAsync(row => row.Action == "SLOT_CONFIGURATION_ACTIVATION_ISSUED",
                TestContext.Current.CancellationToken);
        Assert.Equal(activation.SnapshotId, issued.SnapshotId);
    }

    /// <summary>
    /// 规范化摘要钉在一个固定值上，两个仓各钉一份同样的。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 消息 7 不带配置内容，激活是一次核验：服务端发它批准的那一版的指纹，车算自己手上那份的指纹，
    /// 相等才切换。两端的实现互相看不见——控制服务端在
    /// <see cref="SlotConfigurationFingerprint"/>，车载端在
    /// <c>SQCD.Agv.Core.ActiveSlotConfiguration.ComputeFingerprint</c>——所以「两边算法一致」这句话
    /// 在任何一个仓里都不可能靠对比来证。
    /// </para>
    /// <para>
    /// 固定值是唯一能证的形式：同一批输入，同一个字面量，两个仓各断言一次。哪一边改了规范化形式，
    /// 那一边当场变红，而不是等到现场那台车拒收激活的时候才发现。车载端那一份在
    /// <c>SlotConfigurationActivationTests.TheFingerprintMatchesTheValueTheControlServerComputes</c>。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    public void TheCanonicalFingerprintOfTheSharedExampleIsTheValueTheOnboardSideAlsoComputes()
    {
        SlotIoBindingSpecification[] bindings =
        [
            .. Enumerable.Range(1, 8).Select(number => new SlotIoBindingSpecification(
                number,
                FormattableString.Invariant($"DO{number}"),
                FormattableString.Invariant($"DI{number}"),
                FormattableString.Invariant($"DI{number + 8}"),
                "ACTIVE_HIGH",
                500))
        ];

        Assert.Equal(
            "de93ca3d9eda7b619dd3ea2e8824f8592a3471b11ff723eba3dbc12ea6f69da9",
            SlotConfigurationFingerprint.Compute(bindings));

        // 顺序无关：摘要按仓号升序规范化，输入顺序不该影响结果。
        Assert.Equal(
            SlotConfigurationFingerprint.Compute(bindings),
            SlotConfigurationFingerprint.Compute([.. bindings.Reverse()]));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    public async Task WhileTheResultIsOutstandingTheServerGuessesNeitherSuccessNorFailure()
    {
        await using ActivationFixture fixture = await ActivationFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.BindAllSlotsAsync("AGV-01", model);

        SlotConfigurationActivationRow activation = await fixture.Coordinator.IssueActivationAsync(
            "AGV-01", model, Now, TestContext.Current.CancellationToken);

        // 断线：结果没回来。服务端处在待补报态——不是「大概成功了」也不是「超时算失败」。
        Assert.Equal(SlotConfigurationActivationState.PendingResult, activation.State);
        Assert.Empty(await fixture.Context.Set<ActiveSlotConfigurationRow>()
            .ToArrayAsync(TestContext.Current.CancellationToken));

        // 重连：按恢复角色找回未结的激活，一条。
        IReadOnlyList<SlotConfigurationActivationRow> pending =
            await fixture.Coordinator.ListPendingResultReplayAsync("AGV-01", TestContext.Current.CancellationToken);
        Assert.Equal([activation.ActivationId], pending.Select(row => row.ActivationId));

        // 补报到达，服务端据此收敛。这时才有生效配置，也才有第一份结果。
        await fixture.Coordinator.RecordResultAsync(
            new ActivationResultReport(activation.ActivationId, Succeeded: true, null, Now.AddMinutes(5)),
            TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();
        SlotConfigurationActivationRow settled = await fixture.Context.Set<SlotConfigurationActivationRow>()
            .AsNoTracking()
            .SingleAsync(row => row.ActivationId == activation.ActivationId, TestContext.Current.CancellationToken);
        Assert.Equal(SlotConfigurationActivationState.Activated, settled.State);
        Assert.Equal(Now.AddMinutes(5), settled.ResultReceivedAt);
        ActiveSlotConfigurationRow active = await fixture.Context.Set<ActiveSlotConfigurationRow>().AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(activation.Fingerprint, active.Fingerprint);
        Assert.Empty(await fixture.Coordinator.ListPendingResultReplayAsync(
            "AGV-01", TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    public async Task AReplayedResultConvergesTheSameActivationInsteadOfProducingASecondOne()
    {
        await using ActivationFixture fixture = await ActivationFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.BindAllSlotsAsync("AGV-01", model);
        SlotConfigurationActivationRow activation = await fixture.Coordinator.IssueActivationAsync(
            "AGV-01", model, Now, TestContext.Current.CancellationToken);

        ActivationResultReport report = new(activation.ActivationId, Succeeded: true, null, Now.AddMinutes(5));
        await fixture.Coordinator.RecordResultAsync(report, TestContext.Current.CancellationToken);

        // 同一份结果又到了一次——断线重连本来就会让它重复到达，那是补报机制在正常工作。
        SlotConfigurationActivationRow again = await fixture.Coordinator.RecordResultAsync(
            report with { ReportedAt = Now.AddMinutes(9) }, TestContext.Current.CancellationToken);
        Assert.Equal(Now.AddMinutes(5), again.ResultReceivedAt);

        Assert.Single(await fixture.Context.Set<SlotConfigurationActivationRow>()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Single(await fixture.Context.Set<ActiveSlotConfigurationRow>()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Single(await fixture.Context.Set<BusinessAuditRecordRow>().AsNoTracking()
            .Where(row => row.Action == "SLOT_CONFIGURATION_ACTIVATION_RESULT_RECORDED")
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    public async Task AConfigurationWithAnUnboundSlotCannotBeIssuedAtAll()
    {
        await using ActivationFixture fixture = await ActivationFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();

        // 八仓只绑了七个：原子激活没有「半份生效」这个状态，所以拦在下发之前。
        await fixture.BindSlotsAsync("AGV-01", model, Enumerable.Range(1, 7));
        ActivationTargetIncompleteException failure =
            await Assert.ThrowsAsync<ActivationTargetIncompleteException>(() =>
                fixture.Coordinator.IssueActivationAsync(
                    "AGV-01", model, Now, TestContext.Current.CancellationToken));
        Assert.Contains("REQ-0264", failure.Message, StringComparison.Ordinal);

        // 被拒的下发不留半条记录：没有激活行，也没有多出来的快照。
        Assert.Empty(await fixture.Context.Set<SlotConfigurationActivationRow>()
            .ToArrayAsync(TestContext.Current.CancellationToken));

        // 补齐第八仓之后同一次调用成立。
        await fixture.BindAllSlotsAsync("AGV-01", model);
        SlotConfigurationActivationRow activation = await fixture.Coordinator.IssueActivationAsync(
            "AGV-01", model, Now, TestContext.Current.CancellationToken);
        Assert.Equal(SlotConfigurationActivationState.PendingResult, activation.State);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    public async Task TheFingerprintDecidesWhetherTheArchivedConfigurationIsStillARestorationCandidate()
    {
        await using ActivationFixture fixture = await ActivationFixture.CreateAsync();

        // 还没有档案：没有候选可谈，这与「候选不匹配」是两件事，原因码也不同。
        RecoveryCandidateVerdict absent = await fixture.Coordinator.EvaluateRecoveryCandidateAsync(
            "AGV-01", "sha-1", TestContext.Current.CancellationToken);
        Assert.False(absent.Accepted);
        Assert.Equal(RecoveryCandidateVerdict.NoCandidateCode, absent.ReasonCode);

        fixture.Context.Set<AgvArchiveRow>().Add(new AgvArchiveRow
        {
            AgvId = "AGV-01",
            ArchivedAt = Now,
            ArchiveReason = "报废前置检修",
            ArchivedLifecycleGeneration = 1,
            ArchivedActiveSlotConfigurationFingerprint = "sha-archived"
        });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // 指纹一致：这份候选被采纳。
        RecoveryCandidateVerdict accepted = await fixture.Coordinator.EvaluateRecoveryCandidateAsync(
            "AGV-01", "sha-archived", TestContext.Current.CancellationToken);
        Assert.True(accepted.Accepted);
        Assert.Equal(RecoveryCandidateVerdict.AcceptedCode, accepted.ReasonCode);

        // 指纹不一致：按不匹配处置——不是谁覆盖谁，是这份候选不成立。
        RecoveryCandidateVerdict mismatched = await fixture.Coordinator.EvaluateRecoveryCandidateAsync(
            "AGV-01", "sha-something-else", TestContext.Current.CancellationToken);
        Assert.False(mismatched.Accepted);
        Assert.Equal(RecoveryCandidateVerdict.FingerprintMismatchCode, mismatched.ReasonCode);
        Assert.Equal("sha-archived", mismatched.CandidateFingerprint);

        // 车上什么都没报（字段缺失）同样不匹配，不是「就当一致」。
        Assert.False((await fixture.Coordinator.EvaluateRecoveryCandidateAsync(
            "AGV-01", null, TestContext.Current.CancellationToken)).Accepted);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-14")]
    public void NoSecondHumanApprovalStandsBetweenTheDecisionAndTheActivation()
    {
        // REQ-0265：一次激活动作已经包含重新投运意图。发起激活就是那次决定本身，不再回头要一次确认
        // ——所以这条测的不是「审批被跳过了」，是**根本没有那道关卡**：没有审批入参，也没有一个
        // 「已确认」字段等着谁去填。
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src", "ControlServer.Infrastructure", "Persistence", "SlotConfigurationActivationCoordinator.cs"));
        foreach (string forbidden in new[]
        {
            "Approval", "Approver", "Approved", "RequiresConfirmation", "SecondConfirmation", "AwaitingConfirmation"
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }

        string[] parameters = [.. typeof(SlotConfigurationActivationCoordinator)
            .GetMethod(nameof(SlotConfigurationActivationCoordinator.IssueActivationAsync))!
            .GetParameters()
            .Select(parameter => parameter.Name!)];
        Assert.Equal(["agvId", "slotModelVersionId", "occurredAt", "cancellationToken"], parameters);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from the test binary.");
    }

    private sealed class ActivationFixture : IAsyncDisposable
    {
        private ActivationFixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            GovernanceStore governance,
            SlotConfigurationAuthorityStore authority,
            SlotConfigurationActivationCoordinator coordinator)
        {
            Connection = connection;
            Context = context;
            Governance = governance;
            Authority = authority;
            Coordinator = coordinator;
        }

        private SqliteConnection Connection { get; }

        public ControlServerDbContext Context { get; }

        public GovernanceStore Governance { get; }

        public SlotConfigurationAuthorityStore Authority { get; }

        public SlotConfigurationActivationCoordinator Coordinator { get; }

        public static async Task<ActivationFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            GovernanceStore governance = new(
                context,
                new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
                AuditRetentionPolicy.Default);
            GovernedConfigurationPublisher publisher = new(governance, governance);
            return new ActivationFixture(
                connection,
                context,
                governance,
                new SlotConfigurationAuthorityStore(context, publisher),
                new SlotConfigurationActivationCoordinator(context, publisher, governance));
        }

        public async Task<string> PublishApprovedModelAsync() =>
            (await Authority.EnsureApprovedHardwareFactsAsync(Now, TestContext.Current.CancellationToken))
            .SlotModelVersionId;

        public Task BindAllSlotsAsync(string agvId, string model) =>
            BindSlotsAsync(agvId, model, Enumerable.Range(1, ApprovedSlotHardwareFacts.SlotCount));

        public async Task BindSlotsAsync(string agvId, string model, IEnumerable<int> slots) =>
            await Authority.PublishIoBindingsAsync(
                agvId,
                model,
                [.. ApprovedSlotHardwareFacts.IoBindings.Where(binding => slots.Contains(binding.PhysicalSlotNumber))],
                Now,
                TestContext.Current.CancellationToken);

        public async Task<GovernedConfigurationSnapshot> RequireSnapshotAsync(string objectId, long version) =>
            await ((IConfigurationSnapshotStore)Governance).ReadAsync(
                GovernedObjectKind.ActiveSlotConfiguration,
                objectId,
                version,
                TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"No frozen version {version} of {objectId}.");

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
