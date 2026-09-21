using ControlServer.Host.Runtime;

namespace ControlServer.Host.Composition;

/// <summary>
/// 批次7-10（control-server#215）的计划修订与释放改派，一次注册齐。
/// </summary>
/// <remarks>
/// 自己一个文件，理由与 <see cref="MultiDemandJourneyModule"/> 相同：并行的几张票各注册各的，不在同几行上相撞。
/// </remarks>
internal static class PlanRevisionModule
{
    internal static IServiceCollection AddPlanRevision(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<PlanRevisionRoutingSource>();
        return services;
    }
}
