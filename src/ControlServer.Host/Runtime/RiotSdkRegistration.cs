using ControlServer.Application;
using ControlServer.Infrastructure.Adapters;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace ControlServer.Host.Runtime;

internal static class RiotSdkRegistration
{
    internal const string HttpClientName = "ControlServer.RIoT.NoRetry";

    internal static IServiceCollection AddRiotSdkIntegration(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<HttpMessageHandler>? primaryHandlerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string baseUrl = (configuration["RIoT:baseUrl"] ?? "http://127.0.0.1:58888").TrimEnd('/');
        double timeoutSeconds = configuration.GetValue<double?>("RIoT:timeoutSeconds") ?? 30;
        if (timeoutSeconds <= 0)
        {
            throw new InvalidDataException("RIoT:timeoutSeconds must be positive.");
        }
        TimeSpan timeout = TimeSpan.FromSeconds(timeoutSeconds);

        services.AddHttpClient(HttpClientName, client =>
            {
                client.BaseAddress = new Uri(baseUrl + "/", UriKind.Absolute);
                client.Timeout = timeout;
            })
            .ConfigurePrimaryHttpMessageHandler(() => primaryHandlerFactory?.Invoke() ?? new HttpClientHandler
            {
                AllowAutoRedirect = false
            });

        services.AddScoped(serviceProvider =>
        {
            string? secretVariable = configuration["RIoT:callApiKeyEnvironmentVariable"];
            string? secret = string.IsNullOrWhiteSpace(secretVariable)
                ? null
                : Environment.GetEnvironmentVariable(secretVariable);
            RiotOptions options = new()
            {
                BaseUrl = baseUrl,
                CallApiKey = secret,
                Timeout = timeout
            };
            HttpClient client = serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            return new RiotSession(options, client);
        });
        services.AddScoped<HttpRiotMovementGateway>();
        services.AddScoped<IRiotMovementGateway>(services =>
            services.GetRequiredService<HttpRiotMovementGateway>());
        services.AddScoped<IRiotVehicleFacts>(services =>
            services.GetRequiredService<HttpRiotMovementGateway>());
        services.AddScoped<IRiotMapStationCatalog>(services =>
            services.GetRequiredService<HttpRiotMovementGateway>());
        services.AddScoped<IRiotVehicleSafetyFacts>(services =>
            services.GetRequiredService<HttpRiotMovementGateway>());
        services.AddScoped<HttpRiotOrderCommandGateway>();
        services.AddScoped<IRiotOrderCommandGateway>(services =>
            services.GetRequiredService<HttpRiotOrderCommandGateway>());
        services.AddScoped<IRiotVehicleEmergencyFacts>(services =>
            services.GetRequiredService<HttpRiotOrderCommandGateway>());
        return services;
    }
}
