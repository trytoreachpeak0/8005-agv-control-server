using System.Text.Json;
using ControlServer.Application;
using ControlServer.Host.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Tests;

/// <summary>
/// The create-dispatch gate is an operational interlock that is deliberately separate from
/// <c>JourneyRuntime</c>: enabling the runtime lets the journey state machine run and reconcile
/// read-only, but it is not by itself an authorization to place a real RIoT order.
/// </summary>
public sealed class RiotCreateDispatchGateOptionsTests
{
    [Fact]
    public void DefaultAppSettingsKeepCreateDispatchDisabled()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(path), $"Expected Host appsettings at '{path}'.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement section = document.RootElement.GetProperty(RiotCreateDispatchOptions.SectionName);

        Assert.True(section.GetProperty("enabled").GetBoolean() == false);
    }

    [Fact]
    public void AMissingSectionResolvesToADeniedPolicy()
    {
        RiotCreateDispatchPolicy policy = Resolve(new Dictionary<string, string?>());

        Assert.False(policy.CreateEnabled);
        Assert.Equal(RiotCreateDispatchPolicy.Denied, policy);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    public void AnExplicitlyClosedSectionResolvesToADeniedPolicy(string configured)
    {
        Assert.False(Resolve(new Dictionary<string, string?>
        {
            [$"{RiotCreateDispatchOptions.SectionName}:enabled"] = configured
        }).CreateEnabled);
    }

    [Fact]
    public void OnlyAnExplicitlyOpenedSectionResolvesToAnAllowedPolicy()
    {
        Assert.True(Resolve(new Dictionary<string, string?>
        {
            [$"{RiotCreateDispatchOptions.SectionName}:enabled"] = "true"
        }).CreateEnabled);
    }

    private static RiotCreateDispatchPolicy Resolve(Dictionary<string, string?> configured)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configured)
            .Build();
        ServiceCollection services = new();
        services.AddRiotCreateDispatchGate(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<RiotCreateDispatchPolicy>();
    }
}
