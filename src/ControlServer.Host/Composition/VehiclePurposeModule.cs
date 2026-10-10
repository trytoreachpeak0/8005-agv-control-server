using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Composition;

/// <summary>
/// 批次 8 建表票 control-server#386 的三个持久化端口：用途占有、站点独占、等待点登记，一次注册齐。
/// </summary>
/// <remarks>
/// 自己一个文件，理由与 <see cref="MultiDemandJourneyModule"/> 相同：批次8-16～8-21 只取用这些端口，不改已有的几行。
/// <see cref="IWaitingPointRegistry"/> 依赖 <see cref="GovernanceModule"/> 注册的 <see cref="GovernedConfigurationPublisher"/>。
/// </remarks>
internal static class VehiclePurposeModule
{
    internal static IServiceCollection AddVehiclePurposes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IVehiclePurposeLedger, VehiclePurposeLedgerStore>();
        services.AddScoped<IStationExclusivityStore, StationExclusivityStore>();
        services.AddScoped<IWaitingPointRegistry, WaitingPointRegistry>();
        return services;
    }
}
