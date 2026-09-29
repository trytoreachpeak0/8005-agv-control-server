using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Composition;

/// <summary>
/// 批次 9 建表票 control-server#399 的持久化端口：充电桩名册、充电策略版本、充电周期、两类暂停、清桩记录、人工充电等待、
/// 两类现场确认请求，一次注册齐。
/// </summary>
/// <remarks>
/// 自己一个文件，理由与 <see cref="VehiclePurposeModule"/> 相同：批次9-02～9-12 只取用这些端口，不改已有的几行。
/// <see cref="IChargerRoster"/> 与 <see cref="IChargingPolicyStore"/> 依赖 <see cref="GovernanceModule"/> 注册的
/// <see cref="GovernedConfigurationPublisher"/>。
/// </remarks>
internal static class ChargingModule
{
    internal static IServiceCollection AddCharging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IChargerRoster, ChargerRosterStore>();
        services.AddScoped<IChargingPolicyStore, ChargingPolicyStore>();
        services.AddScoped<IChargingCycleStore, ChargingCycleStore>();
        services.AddScoped<IChargingHoldStore, ChargingHoldStore>();
        services.AddScoped<IStationClearanceStore, StationClearanceStore>();
        services.AddScoped<IManualChargingHoldStore, ManualChargingHoldStore>();
        services.AddScoped<IFieldConfirmationRequestStore, FieldConfirmationRequestStore>();
        return services;
    }
}
