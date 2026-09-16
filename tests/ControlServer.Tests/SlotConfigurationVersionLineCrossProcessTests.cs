using System.Data.Common;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ControlServer.Tests;

/// <summary>
/// 两个进程写同一个 SQLite 库时，版本线上的撞车长什么样、要怎么收场。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SlotConfigurationVersionLineTests"/> 里的交错是<b>表达</b>出来的：两边跑在同一个
/// <c>DbContext</c> 上，冲突异常是注入的，后发起的那一方因此继承了先发起那一方的事务。这个类是
/// <b>复现</b>：两条真连接、两个 <c>DbContext</c>、一个磁盘上的库文件，撞车由 SQLite 自己报出来。
/// </para>
/// <para>
/// <b>第一现场是 <c>SQLITE_BUSY</c>（错误码 5），不是版本冲突。</b>Microsoft.Data.Sqlite 的事务是
/// <c>BEGIN IMMEDIATE</c>，开事务那一刻就要写锁，所以第二个写入方连号都取不到就被挡在门外——它看到的
/// 是 <c>database is locked</c>。<c>SlotConfigurationVersionLine</c> 因此必须把它与版本冲突一样当成
/// 「重新来一次」，否则现场一次正常的并发操作会以一条与版本毫无关系的错误结束。
/// </para>
/// <para>
/// <b>这些测试不靠时序运气。</b>另一个写入方的锁是显式握住、显式放开的，没有 sleep，也没有「希望两边
/// 恰好撞上」的编排：要撞车的那一段，锁在整段时间里都被握着。
/// </para>
/// </remarks>
public sealed class SlotConfigurationVersionLineCrossProcessTests : IAsyncLifetime
{
    private const string Agv = "AGV-02";

    /// <summary>
    /// 测试里第二个写入方等锁的上限。产品默认是
    /// <see cref="ControlServerSqlite.BusyTimeoutSeconds"/> 秒；这里调短只为让套件快，不影响结论——
    /// 锁在整个窗口里都被握着，等多久都等不到。
    /// </summary>
    private const int TestBusyTimeoutSeconds = 1;

    private static readonly DateTimeOffset Now = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);

    private string _directory = string.Empty;

    private string _databasePath = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "w2g-version-line-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "controlserver.db");

        await using ControlServerDbContext seed = OpenContext();
        await seed.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        // 连接池会替进程留着文件句柄，不清掉就删不动目录。
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 删不掉就留在临时目录里，不值得让一条测试因此变红。
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 另一个进程握着写锁的时候取号：等它放手，然后取一个它没占过的号。
    /// </summary>
    /// <remarks>
    /// 放手的时机是确定的——第二次开事务之前放，由拦截器触发，不是等出来的。
    /// </remarks>
    [Fact]
    public async Task AnAllocationBlockedByAnotherProcessRetriesAndTakesAVersionThatProcessDidNotUse()
    {
        string model = await SeedApprovedModelWithBindingsAsync();

        await using ControlServerDbContext other = OpenContext();
        // 另一个进程发布到一半：第 2 版的绑定行已经落地，事务还没提交。
        await using OtherProcessWriteLock held = await OtherProcessWriteLock.TakeAsync(
            other, model, version: 2, Now);

        CountingTransactionInterceptor interceptor = new(onAttempt: 2, held.ReleaseAsync);
        await using ControlServerDbContext mine = OpenContext(TestBusyTimeoutSeconds, interceptor);

        SlotConfigurationActivationRow activation = await CoordinatorFor(mine).IssueActivationAsync(
            Agv, model, Now.AddMinutes(1), TestContext.Current.CancellationToken);

        // 第一次开事务被挡，第二次成功：撞车确实发生过，而且是被重试收拾掉的。
        Assert.Equal(2, interceptor.Attempts);
        // 对方占掉的 2 号被跳过——它的绑定行此刻已经提交，取号看得见。
        Assert.Equal(3, activation.ConfigurationVersion);
    }

    /// <summary>
    /// 另一个进程一直不放手：重试到上限之后，报出来的是 <c>SQLITE_BUSY</c> 本身。
    /// </summary>
    /// <remarks>
    /// 重试不是无限的。撞满 <see cref="SlotConfigurationVersionLine.MaxAllocationAttempts"/> 次意味着
    /// 有东西不对，那时候把库报的原话交出去，比继续转要好。
    /// </remarks>
    [Fact]
    public async Task AnAllocationGivesUpWithTheDatabaseLockedErrorWhenTheOtherProcessNeverLetsGo()
    {
        string model = await SeedApprovedModelWithBindingsAsync();

        await using ControlServerDbContext other = OpenContext();
        await using OtherProcessWriteLock held = await OtherProcessWriteLock.TakeAsync(
            other, model, version: 2, Now);

        CountingTransactionInterceptor interceptor = new();
        await using ControlServerDbContext mine = OpenContext(TestBusyTimeoutSeconds, interceptor);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await CoordinatorFor(mine).IssueActivationAsync(
                Agv, model, Now.AddMinutes(1), TestContext.Current.CancellationToken));

        Assert.True(
            SlotConfigurationVersionLine.IsDatabaseLocked(failure),
            "A cross-process collision must surface as the lock error SQLite reported, not as "
            + $"something else: {failure}");
        Assert.Equal(SlotConfigurationVersionLine.MaxAllocationAttempts, interceptor.Attempts);

        // 放弃的那一次一行不留。
        Assert.Empty(await mine.Set<SlotConfigurationActivationRow>()
            .AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 两条真连接上的绑定发布与激活：绝不共用一个版本号，也绝不挂到对方的快照上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是 #99 那条缺陷的真复现。修复前的取号只数快照，于是挤在「绑定行已写」与「快照已冻」之间的激活
    /// 会拿到同一个号；这里把那一刻做出来，用的是另一条连接上另一个 <c>DbContext</c>，冲突由 SQLite
    /// 报出来而不是由测试塞进去。
    /// </para>
    /// <para>
    /// 保证有两层：绑定发布的事务开着的时候，激活连号都取不到（被写锁挡住）；绑定发布提交之后，激活取到
    /// 的是下一个号，内容是它自己的。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATwoConnectionInterleavingNeverLetsTheActivationTakeTheBindingPublicationsVersion()
    {
        string model = await SeedApprovedModelWithBindingsAsync();

        await using ControlServerDbContext publishing = OpenContext();
        CountingTransactionInterceptor interceptor = new();
        await using ControlServerDbContext activating = OpenContext(TestBusyTimeoutSeconds, interceptor);

        // 绑定发布冻结快照的那一刻，另一条连接上发起一次激活——修复前正是这一刻会撞号。
        InterposingSnapshotStore interposing = new(GovernanceFor(publishing));
        Exception? blocked = null;
        interposing.Once(async _ =>
            blocked = await Record.ExceptionAsync(async () =>
                await CoordinatorFor(activating).IssueActivationAsync(
                    Agv, model, Now.AddMinutes(1), TestContext.Current.CancellationToken)));

        SlotConfigurationAuthorityStore authority = new(
            publishing,
            new GovernedConfigurationPublisher(interposing, GovernanceFor(publishing)));
        IReadOnlyList<SlotIoBindingRow> published = await authority.PublishIoBindingsAsync(
            Agv,
            model,
            [.. ApprovedSlotHardwareFacts.IoBindings.Select(b => b with { PulseResetMilliseconds = 800 })],
            Now.AddMinutes(2),
            TestContext.Current.CancellationToken);

        // 挤进来的那一次没有悄悄成功：它被写锁挡在门外，而不是拿到了同一个号。
        Assert.NotNull(blocked);
        Assert.True(
            SlotConfigurationVersionLine.IsDatabaseLocked(blocked!),
            $"The interleaved activation should have been refused the write lock: {blocked}");
        Assert.Equal(SlotConfigurationVersionLine.MaxAllocationAttempts, interceptor.Attempts);

        long bindingVersion = Assert.Single(published.Select(row => row.Version).Distinct());

        // 绑定发布提交之后再发起同一次激活：号是下一个，内容是它自己的。
        SlotConfigurationActivationRow activation = await CoordinatorFor(activating).IssueActivationAsync(
            Agv, model, Now.AddMinutes(3), TestContext.Current.CancellationToken);

        Assert.NotEqual(bindingVersion, activation.ConfigurationVersion);
        Assert.NotEqual(
            Assert.Single(published.Select(row => row.SnapshotId).Distinct()),
            activation.SnapshotId);

        // 绑定那一版冻的是 800 ms，激活那一版冻的也是 800 ms——因为激活发的就是最新那批已发布绑定。
        // 两版各有自己的快照行，这才是「没有共用一个号」的证据。
        GovernedConfigurationSnapshotRow[] frozen = await activating
            .Set<GovernedConfigurationSnapshotRow>().AsNoTracking()
            .Where(row => row.ObjectKind == GovernedObjectKind.ActiveSlotConfiguration
                && row.ObjectId == $"{Agv}:{model}")
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(frozen.Length, frozen.Select(row => row.Version).Distinct().Count());
        Assert.Contains(frozen, row => row.Version == bindingVersion);
        Assert.Contains(frozen, row => row.Version == activation.ConfigurationVersion);
    }

    /// <summary>
    /// 放弃之后，变更跟踪器里不留任何「已经存进去了」的谎话。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 重试那条路早就清了跟踪器，第五次失败那条路没有。事务已经回滚，跟踪器里那些实体在库里并不存在：
    /// <c>Added</c> 的会被下一次 <c>SaveChanges</c> 真的写进去——写在一个谁都没有分配过的版本号上；
    /// <c>Unchanged</c> 的则是对库的一句假话，改它会得到一次针对不存在的行的 UPDATE。
    /// </para>
    /// <para>
    /// 这条测试直接问版本线本身，因为受影响的是<b>调用方的</b>那一次工作单元，而调用方的 lambda 可以是
    /// 任意形状。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnAllocationThatGivesUpLeavesNothingBehindForALaterSaveToWriteBack()
    {
        string model = await SeedApprovedModelWithBindingsAsync();
        await using ControlServerDbContext context = OpenContext();
        SlotConfigurationVersionLine line = SlotConfigurationVersionLine.For(Agv, model);

        int attempts = 0;
        await Assert.ThrowsAsync<GovernedSnapshotVersionConflictException>(async () =>
            await line.PublishNextVersionAsync<SlotIoBindingRow>(
                context,
                (version, _) =>
                {
                    attempts++;
                    context.Set<SlotIoBindingRow>().Add(BindingRow(model, version, 900));
                    throw Conflict(model, version);
                },
                TestContext.Current.CancellationToken));

        Assert.Equal(SlotConfigurationVersionLine.MaxAllocationAttempts, attempts);
        Assert.Empty(context.ChangeTracker.Entries());

        // 真正要紧的一句：这之后再存一次，库里不能多出任何东西。
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            ApprovedSlotHardwareFacts.SlotCount,
            await context.Set<SlotIoBindingRow>().CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 调用方自己开着事务的时候，版本线不动它的变更跟踪器。
    /// </summary>
    /// <remarks>
    /// 这不是上一条的遗漏，是同一条规则的另一半。那时候回滚<b>还没有发生</b>——调用方的写入仍然活在它
    /// 自己的事务里，清跟踪器等于替它把工作扔掉，而这里无权替它决定。冲突原样抛出去，由那个更大的工作
    /// 单元决定怎么办。
    /// </remarks>
    [Fact]
    public async Task AnAllocationInsideACallersTransactionLeavesTheCallersUnitOfWorkAlone()
    {
        string model = await SeedApprovedModelWithBindingsAsync();
        await using ControlServerDbContext context = OpenContext();
        SlotConfigurationVersionLine line = SlotConfigurationVersionLine.For(Agv, model);

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction outer =
            await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        SlotIoBindingRow callersOwnWrite = BindingRow(model, version: 99, pulseResetMilliseconds: 111);
        context.Set<SlotIoBindingRow>().Add(callersOwnWrite);

        int attempts = 0;
        await Assert.ThrowsAsync<GovernedSnapshotVersionConflictException>(async () =>
            await line.PublishNextVersionAsync<SlotIoBindingRow>(
                context,
                (version, _) =>
                {
                    attempts++;
                    throw Conflict(model, version);
                },
                TestContext.Current.CancellationToken));

        // 一次就抛：这里不重试，因为「回滚」会连调用方的写入一起扔掉。
        Assert.Equal(1, attempts);
        Assert.Contains(
            context.ChangeTracker.Entries(),
            entry => ReferenceEquals(entry.Entity, callersOwnWrite));

        await outer.RollbackAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> SeedApprovedModelWithBindingsAsync()
    {
        await using ControlServerDbContext context = OpenContext();
        SlotConfigurationAuthorityStore authority = AuthorityFor(context);
        string model = (await authority.EnsureApprovedHardwareFactsAsync(
            Now, TestContext.Current.CancellationToken)).SlotModelVersionId;
        await authority.PublishIoBindingsAsync(
            Agv,
            model,
            [.. ApprovedSlotHardwareFacts.IoBindings],
            Now,
            TestContext.Current.CancellationToken);
        return model;
    }

    private ControlServerDbContext OpenContext(
        int busyTimeoutSeconds = ControlServerSqlite.BusyTimeoutSeconds,
        IInterceptor? interceptor = null)
    {
        DbContextOptionsBuilder<ControlServerDbContext> options = new();
        options.UseSqlite(ControlServerSqlite.ForDatabaseFile(
            _databasePath, busyTimeoutSeconds: busyTimeoutSeconds));
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }
        return new ControlServerDbContext(options.Options);
    }

    private static GovernanceStore GovernanceFor(ControlServerDbContext context) => new(
        context,
        new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
        AuditRetentionPolicy.Default);

    private static SlotConfigurationAuthorityStore AuthorityFor(ControlServerDbContext context)
    {
        GovernanceStore governance = GovernanceFor(context);
        return new SlotConfigurationAuthorityStore(
            context, new GovernedConfigurationPublisher(governance, governance));
    }

    private static SlotConfigurationActivationCoordinator CoordinatorFor(ControlServerDbContext context)
    {
        GovernanceStore governance = GovernanceFor(context);
        return new SlotConfigurationActivationCoordinator(
            context, new GovernedConfigurationPublisher(governance, governance), governance);
    }

    private static SlotIoBindingRow BindingRow(string model, long version, int pulseResetMilliseconds) =>
        new()
        {
            SlotIoBindingId = Guid.NewGuid().ToString("N"),
            AgvId = Agv,
            SlotModelVersionId = model,
            PhysicalSlotNumber = 1,
            UnlockOutputPoint = "DO1",
            LockFeedbackInputPoint = "DI1",
            LightCurtainInputPoint = "DI9",
            SignalPolarity = ApprovedSlotHardwareFacts.SignalPolarity,
            PulseResetMilliseconds = pulseResetMilliseconds,
            Version = version,
            Status = SlotConfigurationVersionLine.DraftStatus,
            CreatedAt = Now
        };

    private static GovernedSnapshotVersionConflictException Conflict(string model, long version) =>
        new(
            GovernedObjectKind.ActiveSlotConfiguration,
            $"{Agv}:{model}",
            version,
            "another-writers-snapshot",
            new string('a', 64),
            new string('b', 64));

    /// <summary>
    /// 另一个进程发布到一半：绑定行已经写下、事务还开着，库的写锁因此在它手里。
    /// </summary>
    private sealed class OtherProcessWriteLock : IAsyncDisposable
    {
        private Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? _transaction;

        private OtherProcessWriteLock(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction) =>
            _transaction = transaction;

        public static async Task<OtherProcessWriteLock> TakeAsync(
            ControlServerDbContext context, string model, long version, DateTimeOffset at)
        {
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            foreach (SlotIoBindingSpecification binding in ApprovedSlotHardwareFacts.IoBindings)
            {
                context.Set<SlotIoBindingRow>().Add(new SlotIoBindingRow
                {
                    SlotIoBindingId = Guid.NewGuid().ToString("N"),
                    AgvId = Agv,
                    SlotModelVersionId = model,
                    PhysicalSlotNumber = binding.PhysicalSlotNumber,
                    UnlockOutputPoint = binding.UnlockOutputPoint,
                    LockFeedbackInputPoint = binding.LockFeedbackInputPoint,
                    LightCurtainInputPoint = binding.LightCurtainInputPoint,
                    SignalPolarity = binding.SignalPolarity,
                    PulseResetMilliseconds = 700,
                    Version = version,
                    Status = SlotConfigurationVersionLine.DraftStatus,
                    CreatedAt = at
                });
            }
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return new OtherProcessWriteLock(transaction);
        }

        /// <summary>那个进程发布完了，锁放开。</summary>
        public async Task ReleaseAsync()
        {
            if (_transaction is { } transaction)
            {
                _transaction = null;
                await transaction.CommitAsync(TestContext.Current.CancellationToken);
                await transaction.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_transaction is { } transaction)
            {
                _transaction = null;
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// 数第几次开事务，并在指定那一次开之前做一件事。
    /// </summary>
    /// <remarks>
    /// 「另一个进程什么时候放手」因此是确定的：第 N 次开事务之前放，不是等某个毫秒数等来的。
    /// </remarks>
    private sealed class CountingTransactionInterceptor(int onAttempt = 0, Func<Task>? before = null)
        : DbTransactionInterceptor
    {
        public int Attempts { get; private set; }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts == onAttempt && before is not null)
            {
                await before();
            }
            return await base.TransactionStartingAsync(connection, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// 冻结时插一脚：把「另一个写入方挤进来」表达成一次可复现的调用。
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
}
