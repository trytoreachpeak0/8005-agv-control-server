using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Composition;

/// <summary>
/// Versioned configuration snapshots and immutable audit -- the mechanism FP-C7, FP-C5 and FP-C9b
/// share.
/// </summary>
internal static class GovernanceModule
{
    internal const string RetentionDaysKey = "Governance:auditRetentionDays";
    internal const string DeploymentIdentityKey = "Governance:deploymentIdentity";

    /// <summary>
    /// 仓位配置就绪门禁的档位开关。缺省是 <see cref="SlotConfigurationGateMode.Off"/>。
    /// </summary>
    /// <remarks>
    /// W1 现场窗口先核对后启用，所以缺省必须是关的——一个默认打开的门禁会在第一台车核对完之前就把
    /// 三台车一起挡住，那是 3.5 明确排除的排法。切档由现场在第一台车通过之后做，并留下审计。
    /// </remarks>
    internal const string GateModeKey = "Governance:slotConfigurationReadinessGate";

    internal static IServiceCollection AddGovernance(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(ResolveRetentionPolicy(configuration));
        services.AddSingleton(ResolveDeploymentIdentity(configuration));
        services.AddSingleton(new SlotConfigurationGateSwitch(ResolveGateMode(configuration)));
        services.AddScoped<GovernanceStore>();
        services.AddScoped<IConfigurationSnapshotStore>(sp => sp.GetRequiredService<GovernanceStore>());
        services.AddScoped<IGovernanceAuditWriter>(sp => sp.GetRequiredService<GovernanceStore>());
        services.AddScoped<IGovernedConfigurationReader>(sp => sp.GetRequiredService<GovernanceStore>());
        services.AddScoped<GovernedConfigurationPublisher>();
        services.AddScoped<SlotConfigurationAuthorityStore>();
        services.AddScoped<AgvRestorationStore>();
        services.AddScoped<SlotConfigurationReadinessGate>();
        services.AddScoped<GovernedActivationStore>();
        services.AddScoped<SlotConfigurationActivationCoordinator>();
        services.AddScoped<OnboardAlarmProjectionStore>();
        return services;
    }

    /// <summary>
    /// Applies the configured retention policy to the request's context. REQ-0271 sets the floor at
    /// 180 days, so a configured value below it is rejected at startup rather than silently honoured.
    /// </summary>
    internal static AuditRetentionPolicy ResolveRetentionPolicy(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        double? configured = configuration.GetValue<double?>(RetentionDaysKey);
        if (configured is null)
        {
            return AuditRetentionPolicy.Default;
        }
        if (configured.Value < AuditRetentionPolicy.Default.RetainFor.TotalDays)
        {
            throw new InvalidDataException(
                $"{RetentionDaysKey} is {configured.Value} days; REQ-0271 requires at least "
                + $"{AuditRetentionPolicy.Default.RetainFor.TotalDays} days online.");
        }
        return new AuditRetentionPolicy(TimeSpan.FromDays(configured.Value));
    }

    /// <summary>
    /// 读门禁档位。配置里写了别的值就在启动时抛，而不是悄悄当成关的——一个被拼错的档位名如果被
    /// 静默降级成 <see cref="SlotConfigurationGateMode.Off"/>，现场会以为门禁开着。
    /// </summary>
    internal static SlotConfigurationGateMode ResolveGateMode(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? configured = configuration[GateModeKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return SlotConfigurationGateMode.Off;
        }
        return Enum.TryParse(configured, ignoreCase: true, out SlotConfigurationGateMode mode)
            ? mode
            : throw new InvalidDataException(
                $"{GateModeKey} is '{configured}'; it must be "
                + $"{nameof(SlotConfigurationGateMode.Off)} or {nameof(SlotConfigurationGateMode.Enforcing)}.");
    }

    internal static GovernanceDeploymentIdentity ResolveDeploymentIdentity(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? configured = configuration[DeploymentIdentityKey];
        return string.IsNullOrWhiteSpace(configured)
            ? GovernanceDeploymentIdentity.ForCurrentHost()
            : new GovernanceDeploymentIdentity(configured);
    }
}
