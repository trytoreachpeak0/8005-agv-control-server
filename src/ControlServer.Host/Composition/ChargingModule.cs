using ControlServer.Application;
using ControlServer.Host.Runtime.Charging;
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
        // 批次9-06（control-server#404）：充电分配。分配器与占用读取是作用域的（与派车轮共用同一个 DbContext）；每辆车上一次的结论要跨轮次
        // 记着（结论变了才记日志），所以那块板是单例，不放静态字段、也不放作用域实例上。
        services.AddSingleton<ChargingAllocationBoard>();
        services.AddScoped<ChargerOccupancyReader>();
        services.AddScoped<ChargingAllocator>();
        // 批次9-08（control-server#406）：人工清桩确认（车载端与 Host 共用一个判定）与它查的 R-11／R-13 名单（只读文件，每次判定现读）。
        services.AddOptions<FieldOperatorRoleOptions>().BindConfiguration(FieldOperatorRoleOptions.SectionName);
        services.AddSingleton<FieldOperatorRoleRoster>();
        services.AddSingleton<StationClearanceExit>();
        services.AddScoped<ManualStationClearance>();
        return services;
    }
}
