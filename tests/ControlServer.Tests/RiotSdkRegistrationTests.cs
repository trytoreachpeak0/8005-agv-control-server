using System.Net;
using System.Text;
using ControlServer.Application;
using ControlServer.Host.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Tests;

public sealed class RiotSdkRegistrationTests
{
    [Fact]
    public void RiotSdkBackedAdapterResolvesAllFourPortsFromOneScopedInstance()
    {
        RecordingHandler handler = new(_ => JsonResponse("{\"code\":\"0\",\"result\":null}"));
        using ServiceProvider provider = CreateProvider(handler);

        using IServiceScope firstScope = provider.CreateScope();
        IRiotMovementGateway movement = firstScope.ServiceProvider.GetRequiredService<IRiotMovementGateway>();
        IRiotVehicleFacts vehicle = firstScope.ServiceProvider.GetRequiredService<IRiotVehicleFacts>();
        IRiotMapStationCatalog map = firstScope.ServiceProvider.GetRequiredService<IRiotMapStationCatalog>();
        IRiotVehicleSafetyFacts safety = firstScope.ServiceProvider.GetRequiredService<IRiotVehicleSafetyFacts>();

        Assert.Same(movement, vehicle);
        Assert.Same(movement, map);
        Assert.Same(movement, safety);

        using IServiceScope secondScope = provider.CreateScope();
        Assert.NotSame(
            movement,
            secondScope.ServiceProvider.GetRequiredService<IRiotMovementGateway>());
    }

    [Fact]
    public async Task RiotSdkSessionUsesConfiguredEnvironmentCredentialAsBearerWithoutLogin()
    {
        string variable = "CONTROL_SERVER_TEST_RIOT_SDK_" + Guid.NewGuid().ToString("N");
        const string credential = "test-call-api-key";
        Environment.SetEnvironmentVariable(variable, credential);
        try
        {
            RecordingHandler handler = new(request =>
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal(credential, request.Headers.Authorization?.Parameter);
                return JsonResponse("{\"code\":\"0\",\"result\":null}");
            });
            using ServiceProvider provider = CreateProvider(handler, variable);
            using IServiceScope scope = provider.CreateScope();
            IRiotMovementGateway gateway = scope.ServiceProvider.GetRequiredService<IRiotMovementGateway>();

            RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
                "UPPER-001", TestContext.Current.CancellationToken);

            Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task NamedRiotSdkHttpClientDoesNotRetryTransientFailure()
    {
        string variable = "CONTROL_SERVER_TEST_RIOT_SDK_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "test-call-api-key");
        try
        {
            RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
            using ServiceProvider provider = CreateProvider(handler, variable);
            using IServiceScope scope = provider.CreateScope();
            IRiotMovementGateway gateway = scope.ServiceProvider.GetRequiredService<IRiotMovementGateway>();

            RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
                "UPPER-001", TestContext.Current.CancellationToken);

            Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static ServiceProvider CreateProvider(
        RecordingHandler handler,
        string? credentialVariable = null)
    {
        Dictionary<string, string?> values = new()
        {
            ["RIoT:baseUrl"] = "http://riot.test",
            ["RIoT:callApiKeyEnvironmentVariable"] = credentialVariable
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddRiotSdkIntegration(configuration, () => handler);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responseFactory(request));
        }
    }
}
