using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>一次「这台车绑的是哪个车型版本」的解析结果。</summary>
/// <param name="SlotModelVersionId">解析出的车型版本。</param>
/// <param name="Source">从哪份服务端记录解析出来的。</param>
/// <param name="ActiveSnapshotId">
/// 生效配置冻结的那一版快照；<see cref="VehicleSlotPositionSource.LatestPublishedIoBinding"/> 时为
/// <c>null</c>。车上此刻跑着的内容是这一份，不是绑定表里版本号最大的那一版。
/// </param>
internal sealed record VehicleSlotModelResolution(
    string SlotModelVersionId,
    VehicleSlotPositionSource Source,
    string? ActiveSnapshotId);

/// <summary>
/// 「这台车绑的是哪个车型版本、此刻跑着哪一份配置」的唯一解析处（control-server#66 定的顺序）。
/// </summary>
/// <remarks>
/// <para>
/// 解析顺序固定：生效配置 <see cref="ActiveSlotConfigurationRow"/> 引用的车型；没有生效配置时退到该车
/// 最新一版已发布 IO 绑定引用的车型；都没有就是未解析，由调用方 fail-closed。**不回退到已批准八仓事实的
/// 默认车型，也不按仓号区间推断**——那会把「这台车是什么车型」变成隐含默认，将来出现不同车型时悄悄出错。
/// </para>
/// <para>
/// 「最新一版绑定」只按绑定时间 <c>CreatedAt</c> 取，**不再以版本号做并列时的次级判据**。版本号按
/// （车，车型）各自从 1 编号（<see cref="SlotConfigurationAuthorityStore.PublishIoBindingsAsync"/>），
/// 两个车型的版本号数的不是同一条线，比它们大小没有意义；而 <c>CreatedAt</c> 是调用方传入的
/// <c>occurredAt</c>，也不是写入顺序，所以并列时同样不能靠写入先后兜底。**并列跨了车型就是未解析**：
/// 这种情况没有正确答案，只有一个安全答案。并列落在同一个车型下时不是歧义，照常解析到那个车型。
/// </para>
/// <para>
/// 两个调用方共用这一份实现：派车读仓位分组的 <see cref="VehicleSlotPositionReader"/>，与核验车载端
/// 配置声明的 <see cref="SlotConfigurationAuthorityStore.VerifyVehicleDeclarationAsync"/>。它们此前是
/// 两份近乎逐行相同的副本。
/// </para>
/// </remarks>
internal static class VehicleSlotModelResolver
{
    /// <summary>该车绑的车型版本；两份记录都没有、或最新一版绑定跨车型并列时为 <c>null</c>。</summary>
    public static async Task<VehicleSlotModelResolution?> ResolveAsync(
        ControlServerDbContext context,
        string agvId,
        CancellationToken cancellationToken)
    {
        var active = await context.Set<ActiveSlotConfigurationRow>()
            .AsNoTracking()
            .Where(row => row.AgvId == agvId)
            .Select(row => new { row.SlotModelVersionId, row.SnapshotId })
            .SingleOrDefaultAsync(cancellationToken);
        if (active is not null)
        {
            return new VehicleSlotModelResolution(
                active.SlotModelVersionId,
                VehicleSlotPositionSource.ActiveSlotConfiguration,
                active.SnapshotId);
        }

        var bindings = await context.Set<SlotIoBindingRow>()
            .AsNoTracking()
            .Where(row => row.AgvId == agvId
                && row.Status == PublishedVersionImmutabilityGuard.PublishedStatus)
            .Select(row => new { row.SlotModelVersionId, row.CreatedAt })
            .ToArrayAsync(cancellationToken);
        if (bindings.Length == 0)
        {
            return null;
        }

        // 在内存里比时间：SQLite 不能对 DateTimeOffset 做 ORDER BY。
        DateTimeOffset newest = bindings.Max(row => row.CreatedAt);
        string[] models = [.. bindings
            .Where(row => row.CreatedAt == newest)
            .Select(row => row.SlotModelVersionId)
            .Distinct(StringComparer.Ordinal)];
        return models.Length == 1
            ? new VehicleSlotModelResolution(
                models[0], VehicleSlotPositionSource.LatestPublishedIoBinding, null)
            : null;
    }

    /// <summary>
    /// 这台车此刻跑着的那一份 IO 绑定内容，即核验车载端声明时该比对的权威值。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 有生效配置时取它冻结的那一版快照的内容，**不是该车型下版本号最大的那一版绑定行**。两者在回滚之后
    /// 就分道扬镳：回滚是「取旧版内容在当下发起一次新的激活」，生效的内容是旧的，而绑定表里版本号最大的
    /// 那一版仍是被回滚掉的那一份。激活之后又发布一版绑定（还没再激活）也一样——发布绑定让这台车重新待
    /// 核验（REQ-0263），但没有改变车上跑着的东西。这两种情形下按绑定行比对，都会把一台照生效内容接好线
    /// 的车判成不一致。
    /// </para>
    /// <para>
    /// 没有生效配置时才退到该车型下最新一版已发布绑定：还没激活过的车没有别的权威可比。
    /// </para>
    /// <para>
    /// 生效配置在，而它冻结的那一版快照读不出来时返回 <c>null</c>（未解析），**不退回绑定行**：那正是本
    /// 方法要修掉的那个错误答案。
    /// </para>
    /// </remarks>
    public static async Task<SlotIoBindingSpecification[]?> ReadActiveContentAsync(
        ControlServerDbContext context,
        string agvId,
        CancellationToken cancellationToken)
    {
        VehicleSlotModelResolution? resolution = await ResolveAsync(context, agvId, cancellationToken);
        if (resolution is null)
        {
            return null;
        }

        if (resolution.Source == VehicleSlotPositionSource.ActiveSlotConfiguration)
        {
            string? contentJson = await context.Set<GovernedConfigurationSnapshotRow>()
                .AsNoTracking()
                .Where(row => row.SnapshotId == resolution.ActiveSnapshotId)
                .Select(row => row.ContentJson)
                .FirstOrDefaultAsync(cancellationToken);
            // 反序列化不做兜底：内容是我们自己写进这张不可改写的表的，读不成绑定就是治理库被动过，
            // 那要炸出来，不能降级成一句「声明不一致」。
            return contentJson is null
                ? null
                : JsonSerializer.Deserialize<SlotIoBindingSpecification[]>(contentJson, SnapshotJson);
        }

        SlotIoBindingRow[] latest = await ReadLatestPublishedBindingsAsync(
            context, agvId, resolution.SlotModelVersionId, cancellationToken);
        return latest.Length == 0
            ? null
            : [.. latest.Select(row => new SlotIoBindingSpecification(
                row.PhysicalSlotNumber,
                row.UnlockOutputPoint,
                row.LockFeedbackInputPoint,
                row.LightCurtainInputPoint,
                row.SignalPolarity,
                row.PulseResetMilliseconds))];
    }

    /// <summary>
    /// 这台车在某一版车型下最新一版已发布的 IO 绑定；一版都没有时返回空数组。
    /// </summary>
    /// <remarks>
    /// 车型版本已经定死，所以这里比版本号是有意义的：同一条（车，车型）版本线上的号码本来就按发布顺序
    /// 递增。跨车型比版本号才是无意义的那件事，见 <see cref="ResolveAsync"/>。
    /// </remarks>
    public static async Task<SlotIoBindingRow[]> ReadLatestPublishedBindingsAsync(
        ControlServerDbContext context,
        string agvId,
        string slotModelVersionId,
        CancellationToken cancellationToken)
    {
        SlotIoBindingRow[] bindings = await context.Set<SlotIoBindingRow>()
            .AsNoTracking()
            .Where(row => row.AgvId == agvId
                && row.SlotModelVersionId == slotModelVersionId
                && row.Status == PublishedVersionImmutabilityGuard.PublishedStatus)
            .ToArrayAsync(cancellationToken);
        if (bindings.Length == 0)
        {
            return bindings;
        }
        long latest = bindings.Max(row => row.Version);
        return [.. bindings.Where(row => row.Version == latest)];
    }

    /// <summary>
    /// 冻结快照里的绑定内容用 web 默认（大小写不敏感）读回来：写它的两条路一条序列化
    /// <see cref="SlotIoBindingSpecification"/>、一条序列化同名字段的匿名对象。
    /// </summary>
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
}
