using System.Net.Http.Headers;
using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Adapters;

namespace ControlServer.Host.Composition;

/// <summary>
/// The HTTP clients that read MES facts. Both carry the same shared-secret bearer, from the same
/// configuration keys -- they are one integration with two endpoints, not two integrations.
/// </summary>
internal static class MesIngestModule
{
    internal static IServiceCollection AddMesIngestIntegration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient<IMesIngestCatalog, HttpMesIngestCatalog>(ConfigureMesIngestClient);
        services.AddHttpClient<ISublotBoxCountReader, HttpSublotBoxCountReader>(ConfigureMesIngestClient);
        return services;
    }

    private static void ConfigureMesIngestClient(IServiceProvider services, HttpClient client)
    {
        IConfiguration configuration = services.GetRequiredService<IConfiguration>();
        client.BaseAddress = new Uri(configuration["MesIngest:baseUrl"] ?? "http://127.0.0.1:5088");
        string? secretVariable = configuration["MesIngest:sharedSecretEnvironmentVariable"];
        string? secret = string.IsNullOrWhiteSpace(secretVariable)
            ? null
            : Environment.GetEnvironmentVariable(secretVariable);
        if (!string.IsNullOrWhiteSpace(secret))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
    }
}
