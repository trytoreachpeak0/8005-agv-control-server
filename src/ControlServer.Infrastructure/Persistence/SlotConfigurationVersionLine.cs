using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 一台车在一版车型下的仓位配置，作为一条版本线：绑定发布、激活与回滚都在它上面取号。
/// </summary>
/// <remarks>
/// <para>
/// 三个写入方冻结的是同一个治理对象（<see cref="GovernedObjectKind.ActiveSlotConfiguration"/>，objectId
/// 为 <c>{agvId}:{slotModelVersionId}</c>），所以「下一版是几」只能有一个答案。这个类型就是那个答案的
/// 唯一定义处：objectId 怎么拼在这里，取号查什么在这里，取到号之后怎么落地也在这里。此前这三件事各有
/// 三份拷贝，而三份拷贝数的东西并不一样——激活与回滚只数快照，绑定发布还数绑定行——这正是两个写入方
/// 会拿到同一个号的原因。
/// </para>
/// <para>
/// <b>取号要把 DRAFT 绑定行也数进去。</b>绑定发布是「先写行、再冻快照」两步，中间那一刻版本号已经被
/// 占用而快照还不存在。只数快照的取号会把它当成空位，于是两个写入方拿到同一个号，而先冻结的那个赢——
/// 后冻结的那个会拿回别人的快照（见 <see cref="IConfigurationSnapshotStore.FreezeAsync"/> 的冲突约定）。
/// </para>
/// <para>
/// <b>取号到冻结之间仍然有窗口，所以还有事务与重试。</b><c>ControlServer.FieldOps</c> 是另一个进程，写的
/// 是同一个 SQLite 库；两个进程可以在任何一方落地之前各自读到同一个最大值。
/// <see cref="PublishNextVersionAsync{T}"/> 把「取号 + 落地」包进一个事务，撞车时整次尝试回滚、重新取号
/// 再来一遍，失败的那次因此不留半行。
/// </para>
/// <para>
/// <b>跨进程撞车的第一现场是 <c>SQLITE_BUSY</c>，不是版本冲突。</b>Microsoft.Data.Sqlite 开的是
/// <c>BEGIN IMMEDIATE</c>，写锁在开事务那一刻就要拿到手，所以第二个写入方连号都取不到就被挡住了，看到的
/// 是 <c>database is locked</c>（错误码 5）。等多久由连接串上的 <c>Default Timeout</c> 决定
/// （<see cref="ControlServerSqlite.BusyTimeoutSeconds"/>）；等满了还拿不到，这里与版本冲突一视同仁——
/// 整次取号重来。两者是同一件事的两种报法：号还没轮到你。
/// </para>
/// </remarks>
public readonly record struct SlotConfigurationVersionLine(string AgvId, string SlotModelVersionId)
{
    /// <summary>绑定行写下、快照尚未冻结时的状态。</summary>
    public const string DraftStatus = "DRAFT";

    /// <summary>绑定行已经挂上自己那一版快照之后的状态。</summary>
    /// <remarks>
    /// 取自 <see cref="PublishedVersionImmutabilityGuard.PublishedStatus"/>：不可改写守卫认的就是这个
    /// 字面量，两处各写一份迟早会有一处改了另一处没改。
    /// </remarks>
    public const string PublishedStatus = PublishedVersionImmutabilityGuard.PublishedStatus;

    /// <summary>
    /// 撞车之后最多重新取号几次。
    /// </summary>
    /// <remarks>
    /// 这不是一个乐观并发的调优参数：同一台车的仓位配置发布是人在现场一次一次做的，撞车本身就极少，
    /// 连撞五次意味着有东西不对，那时候报出来比继续转要好。
    /// </remarks>
    public const int MaxAllocationAttempts = 5;

    /// <summary>这条版本线在治理快照与审计里的 objectId。唯一定义处。</summary>
    public string ObjectId => FormattableString.Invariant($"{AgvId}:{SlotModelVersionId}");

    public static SlotConfigurationVersionLine For(string agvId, string slotModelVersionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotModelVersionId);
        return new SlotConfigurationVersionLine(agvId, slotModelVersionId);
    }

    /// <summary>
    /// 这条线上的下一版是几：既有快照与既有绑定行（DRAFT 与 PUBLISHED 都算）两者的最大值加一。
    /// </summary>
    public async Task<long> NextVersionAsync(ControlServerDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string objectId = ObjectId;
        string agvId = AgvId;
        string slotModelVersionId = SlotModelVersionId;
        long[] taken = await context.Set<GovernedConfigurationSnapshotRow>()
            .Where(row => row.ObjectKind == GovernedObjectKind.ActiveSlotConfiguration && row.ObjectId == objectId)
            .Select(row => row.Version)
            .Concat(context.Set<SlotIoBindingRow>()
                .Where(row => row.AgvId == agvId && row.SlotModelVersionId == slotModelVersionId)
                .Select(row => row.Version))
            .ToArrayAsync(cancellationToken);
        return taken.Length == 0 ? 1 : taken.Max() + 1;
    }

    /// <summary>
    /// 取这条线上的下一个号，把一次发布整个落地；号被别人占走就回滚重来。
    /// </summary>
    /// <param name="publishAtVersion">
    /// 在这一版上要做的全部写入——绑定行、快照、激活行、审计。它要么整个成立，要么一行都不留。
    /// </param>
    /// <remarks>
    /// <para>
    /// 调用方自己已经开着事务时，这里不再开第二个，也<b>不重试</b>：那时候「回滚」会连调用方的写入一起
    /// 扔掉，而这里无权替它决定。冲突照常抛出去，由那个更大的工作单元决定怎么办。同理它也<b>不清</b>
    /// 调用方的变更跟踪器——那里面装着调用方自己的工作，而且回滚根本还没有发生。
    /// </para>
    /// <para>
    /// <b>自己开的那个事务一旦失败退出，跟踪器一定清空</b>，三条路都清：重试、撞满
    /// <see cref="MaxAllocationAttempts"/> 次放弃、以及任何别的异常穿出去。事务已经回滚，跟踪器里那些实体
    /// 在库里并不存在，而留着它们不是无害的：<c>Added</c> 的会被下一次 <c>SaveChanges</c> 真的写进去——
    /// 写在一个谁都没有分配过的版本号上；<c>Unchanged</c> 的则是对库的一句假话，改它会得到一次针对不存在
    /// 的行的 UPDATE。这在服务端一次请求一个上下文时看不出来，在 <c>ControlServer.FieldOps</c> 那种一个
    /// 上下文活满整个进程的地方看得出来。
    /// </para>
    /// <para>
    /// 开事务本身也在重试范围内：写锁被别的进程握着时，失败的正是 <c>BEGIN IMMEDIATE</c> 那一步。
    /// </para>
    /// </remarks>
    public async Task<T> PublishNextVersionAsync<T>(
        ControlServerDbContext context,
        Func<long, CancellationToken, Task<T>> publishAtVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(publishAtVersion);

        // 调用方开着事务的话，这一整次都在它的工作单元里：不开第二个、不重试、不清它的跟踪器。
        bool ownsTransaction = context.Database.CurrentTransaction is null;

        for (int attempt = 1; ; attempt++)
        {
            IDbContextTransaction? transaction = null;
            try
            {
                if (ownsTransaction)
                {
                    transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                }
                long version = await NextVersionAsync(context, cancellationToken);
                T published = await publishAtVersion(version, cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                return published;
            }
            catch (Exception failure) when (ownsTransaction)
            {
                // 先清跟踪器：下面的 dispose 会回滚，而回滚之后这些实体就是库里没有的东西了。清在前面，
                // 回滚那一步万一自己出问题，跟踪器也已经是干净的。
                context.ChangeTracker.Clear();
                if (!IsRetryable(failure) || attempt >= MaxAllocationAttempts)
                {
                    throw;
                }
            }
            finally
            {
                if (transaction is not null)
                {
                    // 没提交的事务在这里回滚。不显式调 RollbackAsync：它自己抛出来会盖掉真正的原因。
                    await transaction.DisposeAsync();
                }
            }
        }
    }

    /// <summary>
    /// 这次失败是不是「号还没轮到你」，因而重新取号再来一次就有意义。
    /// </summary>
    /// <remarks>
    /// 两种报法：本进程内两个写入方取到同一个号，是
    /// <see cref="GovernedSnapshotVersionConflictException"/>；另一个进程握着库的写锁，是
    /// <c>SQLITE_BUSY</c>。别的失败（磁盘满、schema 不对）重试多少次都还是那个结果，原样抛出去。
    /// </remarks>
    private static bool IsRetryable(Exception failure) =>
        failure is GovernedSnapshotVersionConflictException || IsDatabaseLocked(failure);

    /// <summary>SQLite 的 <c>SQLITE_BUSY</c>。</summary>
    private const int SqliteBusyErrorCode = 5;

    /// <summary>SQLite 的 <c>SQLITE_LOCKED</c>。</summary>
    private const int SqliteLockedErrorCode = 6;

    /// <summary>
    /// 这个异常（或者它裹着的某一层）是不是「库被锁着」。
    /// </summary>
    /// <remarks>
    /// 要顺着 <see cref="Exception.InnerException"/> 找下去：<c>SaveChanges</c> 失败时 EF 把它裹进
    /// <c>DbUpdateException</c>，而开事务失败时它是光的。<c>SQLITE_LOCKED</c>（6）一并算上——同一个进程里
    /// 两个连接共享缓存时，同样这件事报的是 6 而不是 5。
    /// </remarks>
    public static bool IsDatabaseLocked(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        for (Exception? candidate = failure; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is SqliteException { SqliteErrorCode: SqliteBusyErrorCode or SqliteLockedErrorCode })
            {
                return true;
            }
        }
        return false;
    }
}
