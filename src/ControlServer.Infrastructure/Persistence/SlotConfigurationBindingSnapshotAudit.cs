using System.Text.Json;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 一组绑定行与它们指着的那份快照对不上。
/// </summary>
/// <param name="Problem">
/// 四种之一，见 <see cref="SlotConfigurationBindingSnapshotAudit"/> 上的常量。
/// </param>
/// <param name="SlotsOnlyInSnapshot">快照里有、绑定行里没有的物理仓号。</param>
/// <param name="SlotsOnlyInRows">绑定行里有、快照里没有的物理仓号。</param>
/// <param name="DifferingFields">两边都有但值不同的字段，形如 <c>slot3.pulseResetMilliseconds</c>。</param>
public sealed record SlotBindingSnapshotFinding(
    string AgvId,
    string SlotModelVersionId,
    string ObjectId,
    long Version,
    string? SnapshotId,
    string Problem,
    IReadOnlyList<int> SlotsOnlyInSnapshot,
    IReadOnlyList<int> SlotsOnlyInRows,
    IReadOnlyList<string> DifferingFields);

/// <summary>
/// 只读体检：哪些 IO 绑定行指着的快照装的不是它们自己。
/// </summary>
/// <remarks>
/// <para>
/// <b>只读，一行不写。</b>这里没有修复动作，也不该有：一版已冻结的快照不可改写，绑定行也已经发布，
/// 「修」意味着重新发布一版并作废旧的那一版，那是一次有人负责的运维决定，不是体检能替人做的。
/// </para>
/// <para>
/// 它查的是取号撞车留下的痕迹（见 <see cref="SlotConfigurationVersionLine"/>）：绑定发布与激活、回滚
/// 取到同一个号时，后冻结的一方会拿回先冻结那一方的快照，于是绑定行与它的发布审计指向一份别人的内容。
/// 修复之后新写入不再产生这种行，但**修复之前写下的行不会自己变好**，所以要有一条能把它们数出来的命令。
/// </para>
/// </remarks>
public static class SlotConfigurationBindingSnapshotAudit
{
    /// <summary>已发布的绑定行没有挂上任何快照。</summary>
    public const string MissingSnapshotId = "PUBLISHED_BINDING_ROWS_WITHOUT_SNAPSHOT";

    /// <summary>挂着的 SnapshotId 在快照表里查无此人。</summary>
    public const string SnapshotNotFound = "SNAPSHOT_ID_NOT_IN_STORE";

    /// <summary>挂着的快照属于另一个对象或另一版。</summary>
    public const string SnapshotOnAnotherVersion = "SNAPSHOT_BELONGS_TO_ANOTHER_OBJECT_OR_VERSION";

    /// <summary>快照存在、版本也对，但里面装的不是这批绑定。</summary>
    public const string ContentIsNotTheseBindings = "SNAPSHOT_CONTENT_IS_NOT_THESE_BINDINGS";

    /// <summary>快照内容根本不是一批仓位 IO 绑定。</summary>
    public const string ContentIsNotBindingsAtAll = "SNAPSHOT_CONTENT_DOES_NOT_PARSE_AS_BINDINGS";

    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 扫全库的已发布 IO 绑定行，按「车 + 车型版本 + 版本号」成组与各自的快照比对。
    /// </summary>
    /// <remarks>
    /// DRAFT 行不参与：它们按定义还没挂上快照，发布到一半的那一刻本来就长这样。
    /// </remarks>
    public static async Task<IReadOnlyList<SlotBindingSnapshotFinding>> ScanAsync(
        ControlServerDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        SlotIoBindingRow[] published = await context.Set<SlotIoBindingRow>().AsNoTracking()
            .Where(row => row.Status == SlotConfigurationVersionLine.PublishedStatus)
            .ToArrayAsync(cancellationToken);
        GovernedConfigurationSnapshotRow[] snapshots = await context.Set<GovernedConfigurationSnapshotRow>()
            .AsNoTracking()
            .Where(row => row.ObjectKind == GovernedObjectKind.ActiveSlotConfiguration)
            .ToArrayAsync(cancellationToken);
        Dictionary<string, GovernedConfigurationSnapshotRow> bySnapshotId =
            snapshots.ToDictionary(row => row.SnapshotId, StringComparer.Ordinal);

        List<SlotBindingSnapshotFinding> findings = [];
        foreach (IGrouping<(string AgvId, string Model, long Version), SlotIoBindingRow> group in published
            .GroupBy(row => (row.AgvId, Model: row.SlotModelVersionId, row.Version))
            .OrderBy(group => group.Key.AgvId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Model, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Version))
        {
            SlotConfigurationVersionLine line =
                SlotConfigurationVersionLine.For(group.Key.AgvId, group.Key.Model);
            foreach (SlotBindingSnapshotFinding finding in Inspect(line, group.Key.Version, [.. group], bySnapshotId))
            {
                findings.Add(finding);
            }
        }
        return findings;
    }

    private static IEnumerable<SlotBindingSnapshotFinding> Inspect(
        SlotConfigurationVersionLine line,
        long version,
        IReadOnlyList<SlotIoBindingRow> rows,
        Dictionary<string, GovernedConfigurationSnapshotRow> bySnapshotId)
    {
        // 同一次发布的行共用一个 SnapshotId；分了组就说明这一版被写坏过不止一次，每一组各报一条。
        foreach (IGrouping<string?, SlotIoBindingRow> attached in rows
            .GroupBy(row => row.SnapshotId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            SlotIoBindingRow[] group = [.. attached];
            if (string.IsNullOrWhiteSpace(attached.Key))
            {
                yield return Finding(line, version, null, MissingSnapshotId, [], [.. Slots(group)], []);
                continue;
            }
            if (!bySnapshotId.TryGetValue(attached.Key, out GovernedConfigurationSnapshotRow? snapshot))
            {
                yield return Finding(line, version, attached.Key, SnapshotNotFound, [], [.. Slots(group)], []);
                continue;
            }
            if (!string.Equals(snapshot.ObjectId, line.ObjectId, StringComparison.Ordinal)
                || snapshot.Version != version)
            {
                yield return Finding(
                    line, version, attached.Key, SnapshotOnAnotherVersion, [], [.. Slots(group)], []);
                continue;
            }

            SlotIoBindingSpecification[]? frozen;
            try
            {
                frozen = JsonSerializer.Deserialize<SlotIoBindingSpecification[]>(
                    snapshot.ContentJson, SnapshotJson);
            }
            catch (JsonException)
            {
                frozen = null;
            }
            if (frozen is null)
            {
                yield return Finding(
                    line, version, attached.Key, ContentIsNotBindingsAtAll, [], [.. Slots(group)], []);
                continue;
            }

            Dictionary<int, SlotIoBindingSpecification> inSnapshot =
                frozen.GroupBy(binding => binding.PhysicalSlotNumber).ToDictionary(g => g.Key, g => g.First());
            Dictionary<int, SlotIoBindingRow> inRows =
                group.GroupBy(row => row.PhysicalSlotNumber).ToDictionary(g => g.Key, g => g.First());
            int[] onlyInSnapshot = [.. inSnapshot.Keys.Except(inRows.Keys).Order()];
            int[] onlyInRows = [.. inRows.Keys.Except(inSnapshot.Keys).Order()];
            List<string> differing = [];
            foreach (int slot in inSnapshot.Keys.Intersect(inRows.Keys).Order())
            {
                differing.AddRange(Differences(slot, inRows[slot], inSnapshot[slot]));
            }
            if (onlyInSnapshot.Length > 0 || onlyInRows.Length > 0 || differing.Count > 0)
            {
                yield return Finding(
                    line, version, attached.Key, ContentIsNotTheseBindings, onlyInSnapshot, onlyInRows, [.. differing]);
            }
        }
    }

    private static IEnumerable<string> Differences(int slot, SlotIoBindingRow row, SlotIoBindingSpecification frozen)
    {
        if (!string.Equals(row.UnlockOutputPoint, frozen.UnlockOutputPoint, StringComparison.Ordinal))
        {
            yield return Field(slot, "unlockOutputPoint");
        }
        if (!string.Equals(row.LockFeedbackInputPoint, frozen.LockFeedbackInputPoint, StringComparison.Ordinal))
        {
            yield return Field(slot, "lockFeedbackInputPoint");
        }
        if (!string.Equals(row.LightCurtainInputPoint, frozen.LightCurtainInputPoint, StringComparison.Ordinal))
        {
            yield return Field(slot, "lightCurtainInputPoint");
        }
        if (!string.Equals(row.SignalPolarity, frozen.SignalPolarity, StringComparison.Ordinal))
        {
            yield return Field(slot, "signalPolarity");
        }
        if (row.PulseResetMilliseconds != frozen.PulseResetMilliseconds)
        {
            yield return Field(slot, "pulseResetMilliseconds");
        }
    }

    private static SlotBindingSnapshotFinding Finding(
        SlotConfigurationVersionLine line,
        long version,
        string? snapshotId,
        string problem,
        IReadOnlyList<int> onlyInSnapshot,
        IReadOnlyList<int> onlyInRows,
        IReadOnlyList<string> differingFields) =>
        new(
            line.AgvId,
            line.SlotModelVersionId,
            line.ObjectId,
            version,
            snapshotId,
            problem,
            onlyInSnapshot,
            onlyInRows,
            differingFields);

    private static IEnumerable<int> Slots(IEnumerable<SlotIoBindingRow> rows) =>
        rows.Select(row => row.PhysicalSlotNumber).Distinct().Order();

    private static string Field(int slot, string field) =>
        FormattableString.Invariant($"slot{slot}.{field}");
}
