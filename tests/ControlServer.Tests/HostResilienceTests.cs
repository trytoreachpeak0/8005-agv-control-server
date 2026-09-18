using System.Globalization;
using System.Runtime.Versioning;
using System.ServiceProcess;
using ControlServer.Host.Composition;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ControlServer.Tests;

/// <summary>
/// Issue #148 and #149: a faulted background service must surface as a service failure the recovery
/// policy can act on, and the file log must roll instead of going silent at its size limit.
/// </summary>
[Collection(ProcessExitCodeGroup.Name)]
[SupportedOSPlatform("windows")]
public sealed class HostResilienceTests
{
    [Fact]
    public async Task FaultedBackgroundServiceStopsHostWithNonZeroServiceExitCodeAndErrorLog()
    {
        int originalExitCode = Environment.ExitCode;
        try
        {
            CapturingSink sink = new();
            FakeServiceLifetime lifetime = new();
            await using WebApplication app = BuildHost<ThrowingBackgroundService>(sink, lifetime);

            await app.RunAsync().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(BackgroundServiceFaultReporting.FaultedExitCode, lifetime.ExitCode);
            Assert.Equal(BackgroundServiceFaultReporting.FaultedExitCode, Environment.ExitCode);
            LogEvent reported = Assert.Single(sink.Events, e =>
                e.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? source) &&
                source.ToString().Contains(nameof(BackgroundServiceFaultReporting), StringComparison.Ordinal));
            Assert.Equal(LogEventLevel.Error, reported.Level);
            Assert.IsType<InvalidOperationException>(reported.Exception);
            Assert.Contains(nameof(ThrowingBackgroundService), reported.RenderMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task StopRequestedFromOutsideKeepsServiceExitCodeZero()
    {
        int originalExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            CapturingSink sink = new();
            FakeServiceLifetime lifetime = new();
            await using WebApplication app = BuildHost<IdleBackgroundService>(sink, lifetime);

            await app.StartAsync(TestContext.Current.CancellationToken);
            await app.StopAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, lifetime.ExitCode);
            Assert.Equal(0, Environment.ExitCode);
            Assert.DoesNotContain(sink.Events, e => e.Level >= LogEventLevel.Error);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public void EventLogMessageNamesTheServiceAndStaysWithinTheEventLogLimit()
    {
        InvalidOperationException error = new(new string('x', 40_000));
        string message = BackgroundServiceFaultReporting.DescribeForEventLog(
            [(new ThrowingBackgroundService(), error)]);

        Assert.Contains(typeof(ThrowingBackgroundService).FullName!, message, StringComparison.Ordinal);
        Assert.True(message.Length <= 30_000);
    }

    [Fact]
    public void ShippedLogLevelsSilenceSqlAndHttpClientChatter()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();

        Assert.Equal("Warning", configuration["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"]);
        Assert.Equal("Warning", configuration["Serilog:MinimumLevel:Override:System.Net.Http.HttpClient"]);
        Assert.Equal("Information", configuration["Serilog:MinimumLevel:Default"]);
    }

    [Fact]
    public void FileSinkArgumentsTheInstallerWritesRollAtTheSizeLimitInsteadOfStopping()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cs-log-roll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // The same argument names Install-ControlServerLocal.ps1 writes and
            // Update-ControlServerLocal.ps1 migrates, with a limit small enough to cross in a test.
            IConfigurationRoot configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Serilog:WriteTo:0:Name"] = "File",
                    ["Serilog:WriteTo:0:Args:path"] = Path.Combine(directory, "controlserver-.ndjson"),
                    ["Serilog:WriteTo:0:Args:formatter"] = "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact",
                    ["Serilog:WriteTo:0:Args:rollingInterval"] = "Day",
                    ["Serilog:WriteTo:0:Args:rollOnFileSizeLimit"] = "true",
                    ["Serilog:WriteTo:0:Args:fileSizeLimitBytes"] = "4096",
                    ["Serilog:WriteTo:0:Args:retainedFileCountLimit"] = "3",
                    ["Serilog:WriteTo:0:Args:retainedFileTimeLimit"] = "14.00:00:00",
                    ["Serilog:WriteTo:0:Args:shared"] = "true",
                })
                .Build();

            using (Logger logger = new LoggerConfiguration().ReadFrom.Configuration(configuration).CreateLogger())
            {
                for (int i = 0; i < 400; i++)
                {
                    logger.Information("Filler event {Index} {Padding}", i, new string('p', 64));
                }
                logger.Warning("Last event after the limit");
            }

            string[] files = Directory.GetFiles(directory, "controlserver-*.ndjson");
            Assert.Equal(3, files.Length);
            string newest = files.OrderBy(File.GetLastWriteTimeUtc).ThenBy(f => f, StringComparer.Ordinal).Last();
            Assert.Contains("Last event after the limit", File.ReadAllText(newest), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static WebApplication BuildHost<TService>(CapturingSink sink, FakeServiceLifetime lifetime)
        where TService : BackgroundService
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Test",
        });
        builder.Configuration["Health:url"] = "http://127.0.0.1:0";
        builder.ConfigureControlServerHost();
        builder.Services.AddSingleton<ILogEventSink>(sink);
        builder.Services.AddSingleton<IHostLifetime>(lifetime);
        builder.Services.AddHostedService<TService>();
        WebApplication app = builder.Build();
        app.UseControlServerHost();
        return app;
    }

    private sealed class ThrowingBackgroundService : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("Simulated background service failure.");
        }
    }

    private sealed class IdleBackgroundService : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    /// <summary>
    /// Stands in for <c>WindowsServiceLifetime</c>: the reporter only needs the <see cref="ServiceBase"/>
    /// whose exit code the service control manager will read.
    /// </summary>
    private sealed class FakeServiceLifetime : ServiceBase, IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
            {
                _events.Add(logEvent);
            }
        }
    }
}

/// <summary>
/// <see cref="Environment.ExitCode"/> is process-wide; tests that set it do not run beside each other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessExitCodeGroup
{
    public const string Name = "ProcessExitCode";
}
