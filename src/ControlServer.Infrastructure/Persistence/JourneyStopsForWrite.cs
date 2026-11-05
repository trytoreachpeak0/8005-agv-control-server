using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 要改一趟旅程停靠行的人从这里读：拿到的是被跟踪的实例，但除了本上下文自己暂存、还没保存的改动之外，每一列都是库里此刻的值
/// （control-server#879）。
/// </summary>
/// <remarks>
/// <para>
/// <b>带跟踪的查询不刷新已经跟踪着的实例。</b>EF 按主键做身份解析：上下文里已经有这一行，查询就把那个实例原样交回，库里的新值丢掉。
/// 派车轮与引擎共用一个上下文，本轮早先读过的停靠（让站、到站、清单号都用带跟踪的读法）一直挂在里面；车载端连接、释放服务各有各的上下文，
/// 随时可能在这中间提交一次计划修订。拿着旧实例去写序位，有两处会错：
/// </para>
/// <list type="number">
/// <item>按旧序位算出来的新编号本身就是错的（修订的输入是一份过时的计划）。</item>
/// <item>EF 只把「当前值与跟踪时的原值不同」的列写回去。新序位恰好等于那个旧原值时它一列都不写，库里留着别人刚写的值，
/// 与这一次写下的其余停靠撞号。11-05 agv02 的两个 5、缺 8 就是这样来的：一删一插抵消，三个停靠的新序位恰好等于旧序位。</item>
/// </list>
/// <para>
/// 所以这里先按库对齐，再交出跟踪实例：没暂存过的列，原值与当前值都改成库里的值；暂存过的列保留暂存值，只把原值改成库里的值，
/// 于是保存时比较的对象是库，写不写由「与库是否不同」决定。暂存的改动（终结刚把停靠标成已删、清单补版数）一个都不丢——
/// <c>JourneyPlanRevisionStage</c> 正是靠读到它们才删得掉空停靠。本上下文新加、还没保存的停靠一并交出。
/// </para>
/// <para>
/// <c>ChangeTracker.Clear()</c> 或 <c>Reload</c> 都不行：前者扔掉调用方同一次改动里暂存的其余东西，后者扔掉这些停靠自己的暂存改动。
/// </para>
/// </remarks>
public static class JourneyStopsForWrite
{
    /// <summary>这趟旅程的全部停靠，被跟踪，与库对齐（见类注释），含本上下文新加未保存的。</summary>
    public static async Task<IReadOnlyList<JourneyStopRow>> LoadAsync(
        ControlServerDbContext dbContext,
        string journeyId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);

        Dictionary<string, JourneyStopRow> stored = await dbContext.Set<JourneyStopRow>().AsNoTracking()
            .Where(row => row.JourneyId == journeyId)
            .ToDictionaryAsync(row => row.StopId, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
        foreach (EntityEntry<JourneyStopRow> entry in dbContext.ChangeTracker.Entries<JourneyStopRow>()
                     .Where(entry => entry.State is EntityState.Unchanged or EntityState.Modified &&
                                     string.Equals(entry.Entity.JourneyId, journeyId, StringComparison.Ordinal))
                     .ToArray())
        {
            if (stored.TryGetValue(entry.Entity.StopId, out JourneyStopRow? row))
            {
                AlignWithStore(entry, row);
            }
        }

        JourneyStopRow[] tracked = await dbContext.Set<JourneyStopRow>()
            .Where(row => row.JourneyId == journeyId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. tracked
                .Concat(dbContext.Set<JourneyStopRow>().Local.Where(row => row.JourneyId == journeyId))
                .DistinctBy(row => row.StopId, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// 一个停靠，被跟踪，与库对齐（见类注释）：要在它上面改一列（清单补版数、状态、离站时刻）再保存的人从这里取（审查 S4）。
    /// 带跟踪的 <c>SingleAsync</c> 会把本上下文早先跟踪的旧实例原样交回，在旧值上做的 <c>WorklistRefills +=</c> 会盖掉别的连接刚加上的那几版。
    /// </summary>
    public static async Task<JourneyStopRow> TrackedAsync(
        ControlServerDbContext dbContext,
        string stopId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(stopId);
        return await TrackedOrDefaultAsync(dbContext, stopId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Stop '{stopId}' does not exist.");
    }

    /// <summary>同 <see cref="TrackedAsync"/>，没有这个停靠时为空。</summary>
    public static async Task<JourneyStopRow?> TrackedOrDefaultAsync(
        ControlServerDbContext dbContext,
        string stopId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(stopId);
        JourneyStopRow? stored = await dbContext.Set<JourneyStopRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.StopId == stopId, cancellationToken).ConfigureAwait(false);
        if (stored is not null &&
            dbContext.ChangeTracker.Entries<JourneyStopRow>()
                .FirstOrDefault(entry => entry.State is EntityState.Unchanged or EntityState.Modified &&
                                         string.Equals(entry.Entity.StopId, stopId, StringComparison.Ordinal)) is { } entry)
        {
            AlignWithStore(entry, stored);
        }

        return await dbContext.Set<JourneyStopRow>()
            .SingleOrDefaultAsync(row => row.StopId == stopId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 一趟旅程的停靠序位是不是「1 到停靠数、连续、不重号」（<c>JourneyPlanRevision</c> 的编号规则）；是就返回空，不是就返回一句
    /// 能直接进日志的描述：哪些号重了、哪些号缺了、每个停靠现在是几号。
    /// </summary>
    /// <remarks>
    /// 已删、已完成的停靠一样占号：删掉的接在所有开放的之后，已完成的保留原序位，清单号与计划修订号都按整条旅程的序位算。
    /// </remarks>
    public static string? SequenceProblem(IEnumerable<JourneyStopRow> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        JourneyStopRow[] all = [.. stops.OrderBy(stop => stop.Sequence).ThenBy(stop => stop.StopId, StringComparer.Ordinal)];
        int[] duplicated = [.. all.GroupBy(stop => stop.Sequence).Where(group => group.Count() > 1).Select(group => group.Key)];
        int[] missing = [.. Enumerable.Range(1, all.Length).Except(all.Select(stop => stop.Sequence))];
        if (duplicated.Length == 0 && missing.Length == 0)
        {
            return null;
        }

        return $"stop sequences are not 1..{all.Length} without duplicates: duplicated [{string.Join(',', duplicated)}], " +
               $"missing [{string.Join(',', missing)}]; " +
               string.Join(", ", all.Select(stop => $"{stop.Sequence}={stop.StopId}({stop.Status})"));
    }

    /// <summary>
    /// <paramref name="stops"/> 的序位不守 <see cref="SequenceProblem"/> 时抛 <see cref="JourneyStopSequenceBrokenException"/>。
    /// </summary>
    public static void EnsureSequencesHold(string journeyId, IEnumerable<JourneyStopRow> stops)
    {
        if (SequenceProblem(stops) is { } problem)
        {
            throw new JourneyStopSequenceBrokenException(journeyId, problem);
        }
    }

    private static void AlignWithStore(EntityEntry<JourneyStopRow> entry, JourneyStopRow stored)
    {
        PropertyValues storeValues = entry.OriginalValues.Clone();
        storeValues.SetValues(stored);
        foreach (PropertyEntry property in entry.Properties)
        {
            if (property.Metadata.IsPrimaryKey())
            {
                continue;
            }

            bool staged = property.IsModified;
            object? storeValue = storeValues[property.Metadata];
            if (staged && property.Metadata.Name == nameof(JourneyStopRow.WorklistRefills))
            {
                // 计数列（审查 S4）：暂存的是「在跟踪时的原值上加了几」，别的连接可能在这期间也加过。按库重放这一次的增量，
                // 而不是拿旧原值加出来的结果盖掉别人加的那几版——那会让清单号回退、与车上已收到的号撞上。
                long increment = (long)property.CurrentValue! - (long)property.OriginalValue!;
                property.OriginalValue = storeValue;
                property.CurrentValue = (long)storeValue! + increment;
                continue;
            }

            property.OriginalValue = storeValue;
            if (!staged)
            {
                property.CurrentValue = storeValue;
                property.IsModified = false;
            }
        }
    }
}

/// <summary>
/// 一次停靠序位的写入会让这趟旅程的序位不再是「1 到停靠数、连续、不重号」（control-server#879）。写入整笔不存。
/// </summary>
public sealed class JourneyStopSequenceBrokenException(string journeyId, string problem)
    : InvalidOperationException($"Journey '{journeyId}': {problem}")
{
    public string JourneyId { get; } = journeyId;
}
