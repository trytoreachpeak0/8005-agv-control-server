using ControlServer.Domain;

namespace ControlServer.Application;

/// <summary>
/// 一次提议的变更归到哪条治理路径。
/// </summary>
/// <remarks>
/// 这是 REQ-0260 与 REQ-0261 的分界，也是本票要落的核心区别：只改模板的长宽高或花篮兼容性走模板
/// 资料变更；改仓位集合、物理编号、SlotPosition、IO 点位、信号极性或传感器，走整车维护。
///
/// 两者同时变的，归整车维护 —— 保守的一侧是安全的一侧：整车维护会重新核验，模板资料变更不会。
/// </remarks>
public static class SlotConfigurationChangeClassification
{
    /// <summary>模板规格的变化（只可能是模板资料变更）。</summary>
    public static SlotConfigurationChangeRoute ClassifyTemplateChange(
        SlotTemplateSpecification current,
        SlotTemplateSpecification proposed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);

        bool changed = current.LengthMm != proposed.LengthMm
            || current.WidthMm != proposed.WidthMm
            || current.HeightMm != proposed.HeightMm
            || !current.CompatibleBasketTypes.SequenceEqual(proposed.CompatibleBasketTypes, StringComparer.Ordinal);
        return changed ? SlotConfigurationChangeRoute.TemplateSpecification : SlotConfigurationChangeRoute.NoChange;
    }

    /// <summary>整车层的变化：仓位集合、物理编号、SlotPosition 或模板引用。</summary>
    public static SlotConfigurationChangeRoute ClassifyModelChange(
        IReadOnlyList<SlotModelSlotSpecification> current,
        IReadOnlyList<SlotModelSlotSpecification> proposed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);

        bool changed = !current
            .OrderBy(slot => slot.PhysicalSlotNumber)
            .SequenceEqual(proposed.OrderBy(slot => slot.PhysicalSlotNumber));
        return changed
            ? SlotConfigurationChangeRoute.WholeVehicleMaintenance
            : SlotConfigurationChangeRoute.NoChange;
    }

    /// <summary>IO 层的变化：点位、信号极性、脉冲复位。全部是硬件相关变更。</summary>
    public static SlotConfigurationChangeRoute ClassifyIoBindingChange(
        IReadOnlyList<SlotIoBindingSpecification> current,
        IReadOnlyList<SlotIoBindingSpecification> proposed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);

        bool changed = !current
            .OrderBy(binding => binding.PhysicalSlotNumber)
            .SequenceEqual(proposed.OrderBy(binding => binding.PhysicalSlotNumber));
        return changed
            ? SlotConfigurationChangeRoute.WholeVehicleMaintenance
            : SlotConfigurationChangeRoute.NoChange;
    }

    /// <summary>
    /// 一次同时触及两层的提议归到哪条路。任一层有硬件相关变化就是整车维护。
    /// </summary>
    public static SlotConfigurationChangeRoute Combine(params SlotConfigurationChangeRoute[] routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        if (routes.Contains(SlotConfigurationChangeRoute.WholeVehicleMaintenance))
        {
            return SlotConfigurationChangeRoute.WholeVehicleMaintenance;
        }
        return routes.Contains(SlotConfigurationChangeRoute.TemplateSpecification)
            ? SlotConfigurationChangeRoute.TemplateSpecification
            : SlotConfigurationChangeRoute.NoChange;
    }
}
