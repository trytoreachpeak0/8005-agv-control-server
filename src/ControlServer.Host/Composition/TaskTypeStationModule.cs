using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Composition;

/// <summary>
/// 批次 6 建表票 control-server#159 的五个持久化端口，一次注册齐。
/// </summary>
/// <remarks>
/// 自己一个文件而不是加进 <see cref="GovernanceModule"/>：那个文件的注释写明批次 4 之后只取用、不再改。批次6-04～6-07 只取用这里的
/// 端口，不再改这个文件。存储依赖 <see cref="GovernanceModule"/> 注册的 <see cref="GovernedConfigurationPublisher"/>。
/// </remarks>
internal static class TaskTypeStationModule
{
    internal static IServiceCollection AddTaskTypeStations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ITaskTypeStationRuleStore, TaskTypeStationRuleStore>();
        services.AddScoped<ITaskTypeStationBindingStore, TaskTypeStationBindingStore>();
        services.AddScoped<ITaskTypeStationHoldStore, TaskTypeStationHoldStore>();
        services.AddScoped<ITaskTypeStationCatalogChangeStore, TaskTypeStationCatalogChangeStore>();
        services.AddScoped<IDemandTaskTypeStationFreeze, DemandTaskTypeStationFreezeStore>();
        return services;
    }
}
