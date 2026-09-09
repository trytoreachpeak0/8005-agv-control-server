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

    internal static IServiceCollection AddGovernance(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(ResolveRetentionPolicy(configuration));
        services.AddSingleton(ResolveDeploymentIdentity(configuration));
        services.AddScoped<GovernanceStore>();
        services.AddScoped<IConfigurationSnapshotStore>(sp => sp.GetRequiredService<GovernanceStore>());
        services.AddScoped<IGovernanceAuditWriter>(sp => sp.GetRequiredService<GovernanceStore>());
        services.AddScoped<IGovernedConfigurationReader>(sp => sp.GetRequiredService<GovernanceStore>());
        services.AddScoped<GovernedConfigurationPublisher>();
        services.AddScoped<SlotConfigurationAuthorityStore>();
        services.AddScoped<AgvRestorationStore>();
        services.AddScoped<SlotConfigurationReadinessGate>();
        services.AddScoped<GovernedActivationStore>();
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

    internal static GovernanceDeploymentIdentity ResolveDeploymentIdentity(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? configured = configuration[DeploymentIdentityKey];
        return string.IsNullOrWhiteSpace(configured)
            ? GovernanceDeploymentIdentity.ForCurrentHost()
            : new GovernanceDeploymentIdentity(configured);
    }
}
