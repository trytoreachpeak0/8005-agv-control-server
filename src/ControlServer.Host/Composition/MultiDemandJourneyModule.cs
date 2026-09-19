using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Composition;

/// <summary>
/// 批次 7 建表票 control-server#206 的五个持久化端口，一次注册齐。
/// </summary>
/// <remarks>
/// 自己一个文件，理由与 <see cref="TaskTypeStationModule"/> 相同：批次7-02～7-12 只取用这些端口，不改已有的几行。
/// <see cref="IDispatchZoneParameterStore"/> 依赖 <see cref="GovernanceModule"/> 注册的 <see cref="GovernedConfigurationPublisher"/>。
/// </remarks>
internal static class MultiDemandJourneyModule
{
    internal static IServiceCollection AddMultiDemandJourneys(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IJourneyMembershipStore, JourneyMembershipStore>();
        services.AddScoped<IVehiclePurposeClaimStore, VehiclePurposeClaimStore>();
        services.AddScoped<ITransportDemandSuppressionStore, TransportDemandSuppressionStore>();
        services.AddScoped<IDispatchZoneParameterStore, DispatchZoneParameterStore>();
        services.AddScoped<IVehicleSnapshotRevisionStore, VehicleSnapshotRevisionStore>();
        return services;
    }
}
