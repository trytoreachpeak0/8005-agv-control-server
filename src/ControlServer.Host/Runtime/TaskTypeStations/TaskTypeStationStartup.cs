using ControlServer.Application;

namespace ControlServer.Host.Runtime.TaskTypeStations;

/// <summary>启动装载的结果；没有装载（旅程运行时关闭、或配置里没有这一节）时整体为 <c>null</c>。</summary>
public sealed record TaskTypeStationStartupResult(
    TaskTypeStationVersionWrite<TaskTypeStationRuleVersion> Rules,
    TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion> Bindings);

/// <summary>
/// 启动时装载预置配置（control-server#159）。
/// </summary>
public static class TaskTypeStationStartup
{
    public static Task<TaskTypeStationStartupResult?> EnsureAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
