using ControlServer.Application;
using ControlServer.Host.Runtime.IdleReturn;

namespace ControlServer.Host.Composition;

/// <summary>
/// 空闲返回（批次8-18，control-server#389）：开关、强制充电线的过渡实现与评估器，一次注册齐。
/// </summary>
/// <remarks>
/// 开关绑配置节 <c>IdleReturn</c>，<c>appsettings.json</c> 里没有这一节，所以不配即关（<see cref="IdleReturnOptions"/>）。
/// 评估器依赖 <see cref="VehiclePurposeModule"/> 的三个端口、<see cref="TaskTypeStationModule"/> 的绑定存储与路网访问器。
/// </remarks>
internal static class IdleReturnModule
{
    internal static IServiceCollection AddIdleReturn(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<IdleReturnOptions>().Bind(configuration.GetSection(IdleReturnOptions.SectionName));
        // 批次 9 的阈值票只换这一行的实现。
        services.AddSingleton<IMandatoryChargeLine, TransitionalMandatoryChargeLine>();
        services.AddScoped<IdleReturnEvaluator>();
        return services;
    }
}
