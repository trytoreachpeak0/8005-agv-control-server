using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 一台车在一版车型下的仓位配置只有一条版本线，绑定发布、激活与回滚在它上面取号。
/// </summary>
/// <remarks>
/// <para>
/// 这条线的对象是 <see cref="GovernedObjectKind.ActiveSlotConfiguration"/>，objectId 是
/// <c>{agvId}:{slotModelVersionId}</c>。三个写入方各自取号：绑定发布先写 DRAFT 绑定行、再冻结快照，
/// 而激活与回滚只看快照。两步之间发起的激活因此会拿到同一个号，并且先冻结——冻结一个已存在的版本
/// 原样返回既有快照，唯一索引连碰都没碰到，于是绑定行与它的发布审计指向了一份装着别人内容的快照。
/// </para>
/// <para>
/// 这不是假想的窗口：<c>ControlServer.FieldOps</c> 是另一个进程，写的是同一个 SQLite 库。
/// </para>
/// </remarks>
public sealed class SlotConfigurationVersionLineTests
{
    private const string Agv = "AGV-02";

    private static readonly DateTimeOffset Now = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 绑定行已落地、快照还没冻上的那一刻发起激活：激活不占这一版，绑定冻结的是它自己的内容。
    /// </summary>
    [Fact]
    public async Task AnActivationIssuedBetweenTheBindingRowAndItsFreezeDoesNotTakeTheBindingsVersion()
    {
        await using VersionLineFixture fixture = await VersionLineFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.PublishBindingsAsync(model, pulseResetMilliseconds: 500);

        // 另一个写入方恰好挤在「绑定行已写」与「快照冻结」之间，取号并先冻结。
        SlotConfigurationActivationRow? activation = null;
        fixture.InterposeOnceBeforeFreeze(async () =>
            activation = await fixture.Coordinator.IssueActivationAsync(
                Agv, model, Now.AddMinutes(1), TestContext.Current.CancellationToken));

        IReadOnlyList<SlotIoBindingRow> republished = await fixture.Authority.PublishIoBindingsAsync(
            Agv,
            model,
            [.. ApprovedSlotHardwareFacts.IoBindings.Select(binding => binding with { PulseResetMilliseconds = 800 })],
            Now.AddMinutes(2),
            TestContext.Current.CancellationToken);

        SlotConfigurationActivationRow issued = Assert.IsType<SlotConfigurationActivationRow>(activation);
        long bindingVersion = Assert.Single(republished.Select(row => row.Version).Distinct());
        string bindingSnapshotId = Assert.Single(republished.Select(row => row.SnapshotId).Distinct())!;

        // 两个写入方各占一版，绝不共用一版。
        Assert.NotEqual(issued.ConfigurationVersion, bindingVersion);
        Assert.NotEqual(issued.SnapshotId, bindingSnapshotId);

        // 绑定行指着的那份快照装的是这次发布自己的接线（800 ms），不是激活下发的那一份（500 ms）。
        GovernedConfigurationSnapshot frozen = await fixture.RequireSnapshotAsync(model, bindingVersion);
        Assert.Equal(bindingSnapshotId, frozen.SnapshotId);
        Assert.All(
            JsonSerializer.Deserialize<SlotIoBindingSpecification[]>(frozen.ContentJson)!,
            binding => Assert.Equal(800, binding.PulseResetMilliseconds));

        GovernedConfigurationSnapshot activated = await fixture.RequireSnapshotAsync(
            model, issued.ConfigurationVersion);
        Assert.All(
            JsonSerializer.Deserialize<SlotIoBindingSpecification[]>(activated.ContentJson)!,
            binding => Assert.Equal(500, binding.PulseResetMilliseconds));

        // 发布审计也指着这次发布自己的那一份。
        BusinessAuditRecordRow publishAudit = await fixture.Context.Set<BusinessAuditRecordRow>().AsNoTracking()
            .Where(row => row.Action == "SLOT_IO_BINDING_VERSION_PUBLISHED" && row.Version == bindingVersion)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(bindingSnapshotId, publishAudit.SnapshotId);
    }

    /// <summary>
    /// 有一版绑定行已经落地、快照还没冻上（另一个进程发布到一半）：激活取号跳过这一版。
    /// </summary>
    /// <remarks>
    /// 激活原先只数快照，因此会把这一版当成空的。这条测试不需要任何交错编排，它问的是取号本身。
    /// </remarks>
    [Fact]
    public async Task AnActivationSkipsAVersionThatAHalfWrittenBindingPublicationAlreadyHolds()
    {
        await using VersionLineFixture fixture = await VersionLineFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.PublishBindingsAsync(model, pulseResetMilliseconds: 500);
        await fixture.WriteHalfPublishedBindingRowsAsync(model, version: 2);

        SlotConfigurationActivationRow activation = await fixture.Coordinator.IssueActivationAsync(
            Agv, model, Now.AddMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(3, activation.ConfigurationVersion);

        // 半版绑定占着的 2 号仍然是空的——激活没有把自己的内容冻到别人的版本号上。
        Assert.Null(await fixture.ReadSnapshotAsync(model, 2));
        GovernedConfigurationSnapshot frozen = await fixture.RequireSnapshotAsync(model, 3);
        Assert.Equal(activation.SnapshotId, frozen.SnapshotId);
    }

    /// <summary>
    /// 取到的版本号被别人用别的内容占了：重新取号再发一次，不把绑定挂到别人的快照上。
    /// </summary>
    /// <remarks>
    /// 失败的那一次尝试不留痕迹：没有半批 DRAFT 绑定行，也没有多出来的发布审计。
    /// </remarks>
    [Fact]
    public async Task APublicationWhoseVersionWasTakenRetriesInsteadOfAttachingToTheOtherWritersSnapshot()
    {
        await using VersionLineFixture fixture = await VersionLineFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.PublishBindingsAsync(model, pulseResetMilliseconds: 500);

        // 第一次冻结时，这一版已经被另一个写入方以别的内容占掉了——存储层就是这么报的。
        fixture.FailOnceOnFreezeWithConflict(model);

        IReadOnlyList<SlotIoBindingRow> republished = await fixture.Authority.PublishIoBindingsAsync(
            Agv,
            model,
            [.. ApprovedSlotHardwareFacts.IoBindings.Select(binding => binding with { PulseResetMilliseconds = 900 })],
            Now.AddMinutes(2),
            TestContext.Current.CancellationToken);

        long bindingVersion = Assert.Single(republished.Select(row => row.Version).Distinct());
        GovernedConfigurationSnapshot frozen = await fixture.RequireSnapshotAsync(model, bindingVersion);
        Assert.Equal(Assert.Single(republished.Select(row => row.SnapshotId).Distinct()), frozen.SnapshotId);
        Assert.All(
            JsonSerializer.Deserialize<SlotIoBindingSpecification[]>(frozen.ContentJson)!,
            binding => Assert.Equal(900, binding.PulseResetMilliseconds));

        // 库里这台车只有两版绑定：500 ms 那一版和 900 ms 这一版，没有第三批半途而废的行。
        SlotIoBindingRow[] all = await fixture.Context.Set<SlotIoBindingRow>().AsNoTracking()
            .Where(row => row.AgvId == Agv)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, all.Select(row => row.Version).Distinct().Count());
        Assert.All(all, row => Assert.Equal("PUBLISHED", row.Status));
        Assert.Equal(
            2,
            await fixture.Context.Set<BusinessAuditRecordRow>().AsNoTracking()
                .CountAsync(
                    row => row.Action == "SLOT_IO_BINDING_VERSION_PUBLISHED",
                    TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 体检把「绑定行指着别人的快照」这件事数出来，包括修复之前就已经写坏的那些行。
    /// </summary>
    /// <remarks>
    /// 取号修好之后不会再写出这种行，但已经写下的不会自己变好，所以要有一条能把它们找出来的只读命令。
    /// 这里手写的正是修复前那条路留下的形状：绑定行挂着激活那一版的 SnapshotId。
    /// </remarks>
    [Fact]
    public async Task TheReadOnlyDiagnosticNamesBindingRowsWhoseSnapshotHoldsSomebodyElsesContent()
    {
        await using VersionLineFixture fixture = await VersionLineFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.PublishBindingsAsync(model, pulseResetMilliseconds: 500);
        SlotConfigurationActivationRow activation = await fixture.Coordinator.IssueActivationAsync(
            Agv, model, Now.AddMinutes(1), TestContext.Current.CancellationToken);
        await fixture.WriteBindingRowsAttachedToAsync(
            model, version: activation.ConfigurationVersion, snapshotId: activation.SnapshotId!,
            pulseResetMilliseconds: 800);

        int snapshotsBefore = await fixture.Context.Set<GovernedConfigurationSnapshotRow>()
            .CountAsync(TestContext.Current.CancellationToken);
        int bindingsBefore = await fixture.Context.Set<SlotIoBindingRow>()
            .CountAsync(TestContext.Current.CancellationToken);

        IReadOnlyList<SlotBindingSnapshotFinding> findings =
            await SlotConfigurationBindingSnapshotAudit.ScanAsync(
                fixture.Context, TestContext.Current.CancellationToken);

        SlotBindingSnapshotFinding finding = Assert.Single(findings);
        Assert.Equal(SlotConfigurationBindingSnapshotAudit.ContentIsNotTheseBindings, finding.Problem);
        Assert.Equal(Agv, finding.AgvId);
        Assert.Equal(activation.ConfigurationVersion, finding.Version);
        Assert.Equal(activation.SnapshotId, finding.SnapshotId);
        Assert.Equal(
            [.. Enumerable.Range(1, ApprovedSlotHardwareFacts.SlotCount)
                .Select(slot => $"slot{slot}.pulseResetMilliseconds")],
            finding.DifferingFields);
        Assert.Empty(finding.SlotsOnlyInRows);
        Assert.Empty(finding.SlotsOnlyInSnapshot);

        // 体检一行不写：表里数量不变，也没有任何东西挂在变更跟踪器上等着被保存。
        Assert.Equal(snapshotsBefore, await fixture.Context.Set<GovernedConfigurationSnapshotRow>()
            .CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(bindingsBefore, await fixture.Context.Set<SlotIoBindingRow>()
            .CountAsync(TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Context.ChangeTracker.Entries());
    }

    /// <summary>
    /// 一切都从版本线走出来的库：体检查不出东西。
    /// </summary>
    [Fact]
    public async Task TheReadOnlyDiagnosticFindsNothingInADatabaseWrittenThroughTheVersionLine()
    {
        await using VersionLineFixture fixture = await VersionLineFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.PublishBindingsAsync(model, pulseResetMilliseconds: 500);
        await fixture.Coordinator.IssueActivationAsync(
            Agv, model, Now.AddMinutes(1), TestContext.Current.CancellationToken);
        await fixture.PublishBindingsAsync(model, pulseResetMilliseconds: 800);

        Assert.Empty(await SlotConfigurationBindingSnapshotAudit.ScanAsync(
            fixture.Context, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 冻结时插一脚的测试替身：把「另一个写入方挤进来」表达成一次可复现的调用。
    /// </summary>
    private sealed class InterposingSnapshotStore(IConfigurationSnapshotStore inner) : IConfigurationSnapshotStore
    {
        private Func<long, Task>? _once;

        public void Once(Func<long, Task> interposition) => _once = interposition;

        public async Task<GovernedConfigurationSnapshot> FreezeAsync(
            GovernedObjectKind objectKind,
            string objectId,
            long version,
            string contentJson,
            DateTimeOffset frozenAt,
            CancellationToken cancellationToken)
        {
            if (_once is { } interposition)
            {
                _once = null;
                await interposition(version);
            }
            return await inner.FreezeAsync(objectKind, objectId, version, contentJson, frozenAt, cancellationToken);
        }

        public Task<GovernedConfigurationSnapshot?> ReadAsync(
            GovernedObjectKind objectKind, string objectId, long version, CancellationToken cancellationToken) =>
            inner.ReadAsync(objectKind, objectId, version, cancellationToken);

        public Task<IReadOnlyList<ConfigurationFieldDifference>> DiffAsync(
            GovernedObjectKind objectKind,
            string objectId,
            long leftVersion,
            long rightVersion,
            CancellationToken cancellationToken) =>
            inner.DiffAsync(objectKind, objectId, leftVersion, rightVersion, cancellationToken);
    }

    private sealed class VersionLineFixture : IAsyncDisposable
    {
        private VersionLineFixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            GovernanceStore governance,
            InterposingSnapshotStore interposing,
            SlotConfigurationAuthorityStore authority,
            SlotConfigurationActivationCoordinator coordinator)
        {
            Connection = connection;
            Context = context;
            Governance = governance;
            Interposing = interposing;
            Authority = authority;
            Coordinator = coordinator;
        }

        private SqliteConnection Connection { get; }

        private GovernanceStore Governance { get; }

        private InterposingSnapshotStore Interposing { get; }

        public ControlServerDbContext Context { get; }

        public SlotConfigurationAuthorityStore Authority { get; }

        public SlotConfigurationActivationCoordinator Coordinator { get; }

        public static async Task<VersionLineFixture> CreateAsync()
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
            // 绑定发布走带钩子的那条路，激活走直连的那条：挤进来的那个写入方本身不再被挤。
            InterposingSnapshotStore interposing = new(governance);
            return new VersionLineFixture(
                connection,
                context,
                governance,
                interposing,
                new SlotConfigurationAuthorityStore(
                    context, new GovernedConfigurationPublisher(interposing, governance)),
                new SlotConfigurationActivationCoordinator(
                    context, new GovernedConfigurationPublisher(governance, governance), governance));
        }

        public async Task<string> PublishApprovedModelAsync() =>
            (await Authority.EnsureApprovedHardwareFactsAsync(Now, TestContext.Current.CancellationToken))
            .SlotModelVersionId;

        public async Task PublishBindingsAsync(string model, int pulseResetMilliseconds) =>
            await Authority.PublishIoBindingsAsync(
                Agv,
                model,
                [
                    .. ApprovedSlotHardwareFacts.IoBindings.Select(binding =>
                        binding with { PulseResetMilliseconds = pulseResetMilliseconds })
                ],
                Now,
                TestContext.Current.CancellationToken);

        /// <summary>另一个进程发布到一半：绑定行已经落地，快照还没冻上。</summary>
        public async Task WriteHalfPublishedBindingRowsAsync(string model, long version) =>
            await WriteBindingRowsAsync(model, version, "DRAFT", null, 700);

        /// <summary>修复之前那条路留下的形状：已发布的绑定行挂着别人那一版的快照。</summary>
        public async Task WriteBindingRowsAttachedToAsync(
            string model, long version, string snapshotId, int pulseResetMilliseconds) =>
            await WriteBindingRowsAsync(model, version, "PUBLISHED", snapshotId, pulseResetMilliseconds);

        private async Task WriteBindingRowsAsync(
            string model, long version, string status, string? snapshotId, int pulseResetMilliseconds)
        {
            foreach (SlotIoBindingSpecification binding in ApprovedSlotHardwareFacts.IoBindings)
            {
                Context.Set<SlotIoBindingRow>().Add(new SlotIoBindingRow
                {
                    SlotIoBindingId = Guid.NewGuid().ToString("N"),
                    AgvId = Agv,
                    SlotModelVersionId = model,
                    PhysicalSlotNumber = binding.PhysicalSlotNumber,
                    UnlockOutputPoint = binding.UnlockOutputPoint,
                    LockFeedbackInputPoint = binding.LockFeedbackInputPoint,
                    LightCurtainInputPoint = binding.LightCurtainInputPoint,
                    SignalPolarity = binding.SignalPolarity,
                    PulseResetMilliseconds = pulseResetMilliseconds,
                    Version = version,
                    Status = status,
                    SnapshotId = snapshotId,
                    CreatedAt = Now
                });
            }
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            Context.ChangeTracker.Clear();
        }

        public void InterposeOnceBeforeFreeze(Func<Task> interposition) =>
            Interposing.Once(_ => interposition());

        public void FailOnceOnFreezeWithConflict(string model) =>
            Interposing.Once(version => throw new GovernedSnapshotVersionConflictException(
                GovernedObjectKind.ActiveSlotConfiguration,
                ObjectId(model),
                version,
                "another-writers-snapshot",
                new string('a', 64),
                new string('b', 64)));

        public async Task<GovernedConfigurationSnapshot?> ReadSnapshotAsync(string model, long version) =>
            await ((IConfigurationSnapshotStore)Governance).ReadAsync(
                GovernedObjectKind.ActiveSlotConfiguration,
                ObjectId(model),
                version,
                TestContext.Current.CancellationToken);

        public async Task<GovernedConfigurationSnapshot> RequireSnapshotAsync(string model, long version) =>
            await ReadSnapshotAsync(model, version)
            ?? throw new InvalidOperationException($"No frozen version {version} of {ObjectId(model)}.");

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }

        private static string ObjectId(string model) => $"{Agv}:{model}";
    }
}
