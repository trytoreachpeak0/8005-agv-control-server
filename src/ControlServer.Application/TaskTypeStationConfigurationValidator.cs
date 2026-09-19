namespace ControlServer.Application;

/// <summary>
/// 任务类型规则与按图绑定的校验，纯函数（control-server#159）。
/// </summary>
public static class TaskTypeStationConfigurationValidator
{
    public static IReadOnlyList<TaskTypeStationViolation> ValidateStatic(
        TaskTypeStationConfiguration configuration,
        int runtimeMapId,
        TransitionalGateStation? gateScalar) =>
        throw new NotImplementedException();

    public static IReadOnlyList<TaskTypeStationViolation> EvaluateCatalog(
        int mapId,
        IReadOnlyList<TaskTypeStationBinding> bindings,
        RiotMapStationCatalogSnapshot? catalog,
        bool catalogFresh) =>
        throw new NotImplementedException();
}
