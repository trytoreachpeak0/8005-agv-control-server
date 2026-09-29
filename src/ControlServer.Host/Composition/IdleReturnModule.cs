using ControlServer.Application;
using ControlServer.Host.Runtime.IdleReturn;
using Microsoft.Extensions.Options;

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

        // ValidateOnStart：校验在任何托管服务启动之前就拒（IdleReturnCommitmentTests.TheStartupGuardRefusesBeforeAnyHostedServiceStarts）。
        // 没有它，校验要等第一次取选项——那时别的托管服务（车载端监听、派车循环）已经起来了。
        services.AddOptions<IdleReturnOptions>()
            .Bind(configuration.GetSection(IdleReturnOptions.SectionName))
            .ValidateOnStart();
        // 过渡期护栏：批次8-19 合入前打开即拒绝启动（审查 S2），由那张票删掉。
        services.AddSingleton<IValidateOptions<IdleReturnOptions>, IdleReturnOptionsValidator>();
        services.AddHostedService<IdleReturnStartupWarning>();
        // 批次 9 的阈值票只换这一行的实现。
        services.AddSingleton<IMandatoryChargeLine, TransitionalMandatoryChargeLine>();
        // 单例：结论变了才记日志，要跨轮次（每一轮是一个新的作用域）记得上一轮的结论。
        services.AddSingleton<IdleReturnVerdictBoard>();
        services.AddScoped<IdleReturnEvaluator>();
        return services;
    }
}
