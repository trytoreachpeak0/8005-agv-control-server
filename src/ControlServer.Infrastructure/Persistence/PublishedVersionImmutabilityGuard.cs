using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>发布过的配置版本被改写或删除时抛出。</summary>
public sealed class PublishedVersionImmutabilityException : InvalidOperationException
{
    public PublishedVersionImmutabilityException(string message)
        : base(message)
    {
    }

    public PublishedVersionImmutabilityException()
        : base("A published configuration version cannot be rewritten.")
    {
    }

    public PublishedVersionImmutabilityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 一个版本一经发布就是不可改写的（REQ-0267）。
/// </summary>
/// <remarks>
/// 和审计的不可改写一样放在 <see cref="ControlServerDbContext"/> 上而不是写它们的那些 store 里：
/// 它是上下文的性质，不是每个未来调用方要记住的约定。草稿仍可改 —— 拦的是「把普通模板资料修改
/// 变成对已批准硬件事实的静默改写」这一件事。
/// </remarks>
internal static class PublishedVersionImmutabilityGuard
{
    internal const string PublishedStatus = "PUBLISHED";

    internal static void Enforce(ChangeTracker changeTracker)
    {
        foreach (EntityEntry entry in changeTracker.Entries())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }
            if (!IsPublished(entry))
            {
                continue;
            }
            throw new PublishedVersionImmutabilityException(
                $"{entry.Entity.GetType().Name} is published and cannot be "
                + $"{(entry.State == EntityState.Modified ? "updated" : "deleted")}. "
                + "Publish a new version instead.");
        }
    }

    private static bool IsPublished(EntityEntry entry) => entry.Entity switch
    {
        // A row already published is judged on the value it had when it was loaded, so flipping the
        // status back to DRAFT in the same change does not unlock it.
        SlotTemplateRow => WasPublished(entry, nameof(SlotTemplateRow.Status)),
        SlotModelVersionRow => WasPublished(entry, nameof(SlotModelVersionRow.Status)),
        SlotIoBindingRow => WasPublished(entry, nameof(SlotIoBindingRow.Status)),
        SlotModelSlotRow => IsSlotOfPublishedModel(entry),
        _ => false,
    };

    private static bool WasPublished(EntityEntry entry, string statusProperty) =>
        string.Equals(
            entry.State == EntityState.Modified
                ? entry.Property(statusProperty).OriginalValue as string
                : entry.Property(statusProperty).CurrentValue as string,
            PublishedStatus,
            StringComparison.Ordinal);

    private static bool IsSlotOfPublishedModel(EntityEntry entry)
    {
        SlotModelSlotRow slot = (SlotModelSlotRow)entry.Entity;
        SlotModelVersionRow? model = entry.Context.Set<SlotModelVersionRow>()
            .Local.FirstOrDefault(row => row.SlotModelVersionId == slot.SlotModelVersionId)
            ?? entry.Context.Set<SlotModelVersionRow>()
                .FirstOrDefault(row => row.SlotModelVersionId == slot.SlotModelVersionId);
        return string.Equals(model?.Status, PublishedStatus, StringComparison.Ordinal);
    }
}
