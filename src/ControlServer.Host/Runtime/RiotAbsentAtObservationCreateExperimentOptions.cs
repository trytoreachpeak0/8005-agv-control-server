using ControlServer.Application;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

public sealed class RiotAbsentAtObservationCreateExperimentOptions
{
    public const string SectionName = "RiotAbsentAtObservationCreateExperiment";

    public bool Enabled { get; set; }
    public string AuthorizationId { get; set; } = string.Empty;
    public int AuthorizationVersion { get; set; }
    public string UpperId { get; set; } = string.Empty;
    public string DemandId { get; set; } = string.Empty;
    public string MovementLegId { get; set; } = string.Empty;
    public long AgvLifecycleGeneration { get; set; }
    public long DispatchGeneration { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class RiotAbsentAtObservationCreateExperimentOptionsValidator(
    TimeProvider timeProvider) : IValidateOptions<RiotAbsentAtObservationCreateExperimentOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        RiotAbsentAtObservationCreateExperimentOptions options)
    {
        _ = name;
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];
        RequireText(options.AuthorizationId, nameof(options.AuthorizationId), failures);
        RequireText(options.UpperId, nameof(options.UpperId), failures);
        RequireText(options.DemandId, nameof(options.DemandId), failures);
        RequireText(options.MovementLegId, nameof(options.MovementLegId), failures);
        if (options.AuthorizationVersion != 1)
        {
            failures.Add("AuthorizationVersion must be 1.");
        }
        if (options.AgvLifecycleGeneration <= 0)
        {
            failures.Add("AgvLifecycleGeneration must be positive.");
        }
        if (options.DispatchGeneration <= 0)
        {
            failures.Add("DispatchGeneration must be positive.");
        }
        if (options.ExpiresAt <= timeProvider.GetUtcNow())
        {
            failures.Add("ExpiresAt must be in the future.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void RequireText(string value, string name, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{name} is required.");
        }
    }
}

public sealed class ConfigurationExperimentalRiotCreateAuthorizationSource :
    IExperimentalRiotCreateAuthorizationSource
{
    private readonly ExperimentalRiotCreateAuthorization? authorization;

    public ConfigurationExperimentalRiotCreateAuthorizationSource(
        IOptions<RiotAbsentAtObservationCreateExperimentOptions> options)
    {
        RiotAbsentAtObservationCreateExperimentOptions value = options.Value;
        authorization = value.Enabled
            ? new ExperimentalRiotCreateAuthorization(
                value.AuthorizationId,
                value.AuthorizationVersion,
                value.UpperId,
                value.DemandId,
                value.MovementLegId,
                value.AgvLifecycleGeneration,
                value.DispatchGeneration,
                value.ExpiresAt)
            : null;
    }

    public Task<ExperimentalRiotCreateAuthorization?> GetAuthorizationAsync(
        string upperId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExperimentalRiotCreateAuthorization? result = authorization is not null &&
            string.Equals(authorization.UpperId, upperId, StringComparison.Ordinal)
                ? authorization
                : null;
        return Task.FromResult(result);
    }
}

public static class RiotAbsentAtObservationCreateExperimentServiceCollectionExtensions
{
    public static IServiceCollection AddRiotAbsentAtObservationCreateExperiment(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RiotAbsentAtObservationCreateExperimentOptions>()
            .Bind(configuration.GetSection(RiotAbsentAtObservationCreateExperimentOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<RiotAbsentAtObservationCreateExperimentOptions>,
            RiotAbsentAtObservationCreateExperimentOptionsValidator>();
        services.AddSingleton<
            IExperimentalRiotCreateAuthorizationSource,
            ConfigurationExperimentalRiotCreateAuthorizationSource>();
        return services;
    }
}
