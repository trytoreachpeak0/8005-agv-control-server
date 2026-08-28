using System.Text.Json;
using ControlServer.Application;
using ControlServer.Host.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

public sealed class RiotAbsentAtObservationCreateExperimentOptionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DefaultAppSettingsKeepExperimentDisabled()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(path), $"Expected Host appsettings at '{path}'.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement section = document.RootElement
            .GetProperty(RiotAbsentAtObservationCreateExperimentOptions.SectionName);
        Dictionary<string, JsonElement> properties = section
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);

        Assert.True(properties.TryGetValue("Enabled", out JsonElement enabled));
        Assert.False(enabled.GetBoolean());
        Assert.DoesNotContain("AuthorizationId", properties.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("AuthorizationVersion", properties.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("UpperId", properties.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("DemandId", properties.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("MovementLegId", properties.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("AgvLifecycleGeneration", properties.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("DispatchGeneration", properties.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExpiresAt", properties.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DefaultDisabledOptionsValidateAndSourceReturnsNoAuthorization()
    {
        RiotAbsentAtObservationCreateExperimentOptions options = new();
        RiotAbsentAtObservationCreateExperimentOptionsValidator validator = new(
            new FixedTimeProvider(Now));

        ValidateOptionsResult validation = validator.Validate(null, options);
        ConfigurationExperimentalRiotCreateAuthorizationSource source = new(
            Options.Create(options));
        ExperimentalRiotCreateAuthorization? authorization = await source.GetAuthorizationAsync(
            "ANY-UPPER-ID",
            TestContext.Current.CancellationToken);

        Assert.True(validation.Succeeded);
        Assert.False(validation.Failed);
        Assert.Null(authorization);
    }

    [Fact]
    public async Task ExactEnabledOptionsValidateAndSourceReturnsOnlyForExactUpperId()
    {
        RiotAbsentAtObservationCreateExperimentOptions options = ValidOptions();
        RiotAbsentAtObservationCreateExperimentOptionsValidator validator = new(
            new FixedTimeProvider(Now));

        ValidateOptionsResult validation = validator.Validate(null, options);
        ConfigurationExperimentalRiotCreateAuthorizationSource source = new(
            Options.Create(options));
        ExperimentalRiotCreateAuthorization? mismatch = await source.GetAuthorizationAsync(
            "UPPER-OTHER",
            TestContext.Current.CancellationToken);
        ExperimentalRiotCreateAuthorization? exact = await source.GetAuthorizationAsync(
            options.UpperId,
            TestContext.Current.CancellationToken);

        Assert.True(validation.Succeeded);
        Assert.Null(mismatch);
        Assert.NotNull(exact);
        Assert.Equal(options.AuthorizationId, exact.AuthorizationId);
        Assert.Equal(1, exact.AuthorizationVersion);
        Assert.Equal(options.UpperId, exact.UpperId);
        Assert.Equal(options.DemandId, exact.DemandId);
        Assert.Equal(options.MovementLegId, exact.MovementLegId);
        Assert.Equal(options.AgvLifecycleGeneration, exact.AgvLifecycleGeneration);
        Assert.Equal(options.DispatchGeneration, exact.DispatchGeneration);
        Assert.Equal(options.ExpiresAt, exact.ExpiresAt);
    }

    [Fact]
    public async Task DisabledButPopulatedOptionsSourceReturnsNoAuthorization()
    {
        RiotAbsentAtObservationCreateExperimentOptions options = ValidOptions();
        options.Enabled = false;
        ConfigurationExperimentalRiotCreateAuthorizationSource source = new(
            Options.Create(options));

        ExperimentalRiotCreateAuthorization? authorization = await source.GetAuthorizationAsync(
            options.UpperId,
            TestContext.Current.CancellationToken);

        Assert.Null(authorization);
    }

    [Fact]
    public async Task InvalidEnabledConfigurationFailsHostStartupThroughValidateOnStart()
    {
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                DisableDefaults = true,
                ApplicationName = typeof(RiotAbsentAtObservationCreateExperimentOptionsTests).Assembly.GetName().Name!
            });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{RiotAbsentAtObservationCreateExperimentOptions.SectionName}:Enabled"] = "true",
            [$"{RiotAbsentAtObservationCreateExperimentOptions.SectionName}:AuthorizationId"] = "AUTH-STARTUP-001",
            [$"{RiotAbsentAtObservationCreateExperimentOptions.SectionName}:AuthorizationVersion"] = "2",
            [$"{RiotAbsentAtObservationCreateExperimentOptions.SectionName}:UpperId"] = "UPPER-STARTUP-001",
            [$"{RiotAbsentAtObservationCreateExperimentOptions.SectionName}:DemandId"] = "D-STARTUP-001",
            [$"{RiotAbsentAtObservationCreateExperimentOptions.SectionName}:MovementLegId"] = "LEG-STARTUP-001",
            [$"{RiotAbsentAtObservationCreateExperimentOptions.SectionName}:AgvLifecycleGeneration"] = "4",
            [$"{RiotAbsentAtObservationCreateExperimentOptions.SectionName}:DispatchGeneration"] = "7",
            [$"{RiotAbsentAtObservationCreateExperimentOptions.SectionName}:ExpiresAt"] =
                Now.AddMinutes(5).ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        });
        builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        builder.Services.AddRiotAbsentAtObservationCreateExperiment(builder.Configuration);
        using IHost host = builder.Build();

        OptionsValidationException error = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("AuthorizationVersion must be 1.", error.Failures);
    }

    [Theory]
    [InlineData("BlankAuthorizationId", "AuthorizationId is required.")]
    [InlineData("BlankUpperId", "UpperId is required.")]
    [InlineData("BlankDemandId", "DemandId is required.")]
    [InlineData("BlankMovementLegId", "MovementLegId is required.")]
    [InlineData("WrongAuthorizationVersion", "AuthorizationVersion must be 1.")]
    [InlineData("ZeroLifecycleGeneration", "AgvLifecycleGeneration must be positive.")]
    [InlineData("NegativeLifecycleGeneration", "AgvLifecycleGeneration must be positive.")]
    [InlineData("ZeroDispatchGeneration", "DispatchGeneration must be positive.")]
    [InlineData("NegativeDispatchGeneration", "DispatchGeneration must be positive.")]
    [InlineData("ExpiresAtNow", "ExpiresAt must be in the future.")]
    [InlineData("ExpiresBeforeNow", "ExpiresAt must be in the future.")]
    public void EnabledOptionsRejectEveryInvalidIdentityVersionGenerationAndExpiry(
        string scenario,
        string expectedFailure)
    {
        RiotAbsentAtObservationCreateExperimentOptions options = ValidOptions();
        switch (scenario)
        {
            case "BlankAuthorizationId":
                options.AuthorizationId = " ";
                break;
            case "BlankUpperId":
                options.UpperId = "";
                break;
            case "BlankDemandId":
                options.DemandId = "\t";
                break;
            case "BlankMovementLegId":
                options.MovementLegId = " ";
                break;
            case "WrongAuthorizationVersion":
                options.AuthorizationVersion = 2;
                break;
            case "ZeroLifecycleGeneration":
                options.AgvLifecycleGeneration = 0;
                break;
            case "NegativeLifecycleGeneration":
                options.AgvLifecycleGeneration = -1;
                break;
            case "ZeroDispatchGeneration":
                options.DispatchGeneration = 0;
                break;
            case "NegativeDispatchGeneration":
                options.DispatchGeneration = -1;
                break;
            case "ExpiresAtNow":
                options.ExpiresAt = Now;
                break;
            case "ExpiresBeforeNow":
                options.ExpiresAt = Now.AddTicks(-1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
        RiotAbsentAtObservationCreateExperimentOptionsValidator validator = new(
            new FixedTimeProvider(Now));

        ValidateOptionsResult validation = validator.Validate(null, options);

        Assert.True(validation.Failed);
        Assert.False(validation.Succeeded);
        Assert.Contains(expectedFailure, validation.Failures);
    }

    private static RiotAbsentAtObservationCreateExperimentOptions ValidOptions() => new()
    {
        Enabled = true,
        AuthorizationId = "AUTH-OPTIONS-001",
        AuthorizationVersion = 1,
        UpperId = "UPPER-OPTIONS-001",
        DemandId = "D-OPTIONS-001",
        MovementLegId = "LEG-OPTIONS-001",
        AgvLifecycleGeneration = 4,
        DispatchGeneration = 7,
        ExpiresAt = Now.AddMinutes(5)
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
