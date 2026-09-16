using ControlServer.Application;
using ControlServer.Domain;
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
    /// 扔掉，而这里无权替它决定。冲突照常抛出去，由那个更大的工作单元决定怎么办。
    /// </para>
    /// <para>
    /// 重试前会清空变更跟踪器。事务已经回滚，跟踪器里那些「已保存」的实体在库里并不存在，留着它们下一次
    /// <c>SaveChanges</c> 就会把幽灵行写回去。
    /// </para>
    /// </remarks>
    public async Task<T> PublishNextVersionAsync<T>(
        ControlServerDbContext context,
        Func<long, CancellationToken, Task<T>> publishAtVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(publishAtVersion);

        for (int attempt = 1; ; attempt++)
        {
            await using IDbContextTransaction? transaction = context.Database.CurrentTransaction is null
                ? await context.Database.BeginTransactionAsync(cancellationToken)
                : null;
            long version = await NextVersionAsync(context, cancellationToken);
            try
            {
                T published = await publishAtVersion(version, cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                return published;
            }
            catch (GovernedSnapshotVersionConflictException)
                when (transaction is not null && attempt < MaxAllocationAttempts)
            {
                await transaction.RollbackAsync(cancellationToken);
                context.ChangeTracker.Clear();
            }
        }
    }
}
