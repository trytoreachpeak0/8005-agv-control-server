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
        // 分区归属表没有草稿态：一个版本写入的那一刻就已发布，回滚是把旧内容再导入成新版本（REQ-0350）。
        DispatchZoneAreaAssignmentVersionRow or DispatchZoneAreaAssignmentRow => true,
        // 任务类型规则与按图绑定集同样没有草稿态（REQ-0337、REQ-0343），版本行与其子行写入即发布；换内容是写一个新版本。
        TaskTypeStationRuleVersionRow or TaskTypeStationRuleRow
            or TaskTypeStationBindingSetVersionRow or TaskTypeStationBindingRow or TaskTypeStationRequirementRow => true,
        // 每区派车参数同样没有草稿态（REQ-0198、REQ-0203；control-server#206）：版本行与分区行写入即发布，换参数是写一个新版本。
        DispatchZoneParameterVersionRow or DispatchZoneParameterRow => true,
        // 等待点登记同样没有草稿态（REQ-0289、REQ-0297；control-server#386）：版本行、等待点行与白名单行写入即发布。
        WaitingPointVersionRow or WaitingPointRow or WaitingPointVehicleScopeRow => true,
        // 充电桩名册与充电策略同样没有草稿态（REQ-0171、REQ-0282；control-server#399）：版本行与子行写入即发布；策略的批准与激活
        // 只追加，换生效版本是再激活一次。
        ChargerRosterVersionRow or ChargerRosterEntryRow or ChargerRosterVehicleScopeRow
            or ChargingPolicyVersionRow or ChargingPolicyVehicleScopeRow
            or ChargingPolicyApprovalRow or ChargingPolicyActivationRow => true,
        // 桩与车的充电暂停是不可变事件（REQ-0177）：暂停行与恢复行都只追加，恢复是另起一行，不是改暂停行。
        ChargingStationAllocationHoldRow or ChargingStationRecoveryRow
            or VehicleChargingEligibilityHoldRow or VehicleChargingEligibilityRecoveryRow => true,
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
