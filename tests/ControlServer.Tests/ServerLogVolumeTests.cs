using System.Net;
using System.Text;
using System.Text.Json;
using ControlServer.Host.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Settings.Configuration;

namespace ControlServer.Tests;

/// <summary>
/// control-server#587. On factory01 the v2 Host's log reached Serilog's default 1 GiB file size limit at 22:36 on
/// 2026-10-10 and wrote nothing more that day: the File sink the installer renders sets rollingInterval=Day and no
/// rollOnFileSizeLimit, so the limit is a cap. 68 % of those bytes were EF Core's per-statement log (event 20101) and
/// about 30 % the HttpClient handlers' four lines per request, nearly all of them successful RIoT reads.
/// </summary>
public sealed class ServerLogVolumeTests
{
    private const string RiotTrafficCategory = "ControlServer.Host.Runtime.RiotHttpTrafficLogger";

    [Fact]
    public void FileSinkWithASizeLimitButNoRollingRollsAtTheLimitInsteadOfStopping()
    {
        string directory = NewDirectory();
        try
        {
            // The factory01 shape: a size limit (there the implicit 1 GiB) and nothing that rolls at it.
            using (Serilog.Core.Logger logger = CreateInstalledFileLogger(directory, fileSizeLimitBytes: 4096, rollOnFileSizeLimit: null))
            {
                for (int index = 0; index < 200; index++)
                {
                    logger.Information("noise {Index} {Padding}", index, new string('x', 100));
                }
            }

            FileInfo[] files = LogFilesNewestFirst(directory);
            Assert.True(files.Length > 1, $"the sink wrote {files.Length} file(s) of {string.Join(", ", files.Select(file => file.Length))} bytes");
            Assert.Contains("\"Index\":199", ReadShared(files[0]), StringComparison.Ordinal);
            Assert.All(files, file => Assert.True(file.Length <= 4096 + 512, $"{file.Name} is {file.Length} bytes"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingSettingsTurnOnSizeRollingAndFillTheLimitsAnInstalledFileLeftOut()
    {
        // What Install-ControlServerLocal.ps1 rendered before #587, and what an upgrade keeps.
        IReadOnlyDictionary<string, string?> before = ServerLogFileLimits.MissingSettings(Configuration(
            """{ "Serilog": { "WriteTo": [ { "Name": "File", "Args": { "path": "C:\\logs\\controlserver-.ndjson", "rollingInterval": "Day", "retainedFileCountLimit": 14, "shared": true } } ] } }"""));
        Assert.Equal("true", before["Serilog:WriteTo:0:Args:rollOnFileSizeLimit"]);
        Assert.Equal("67108864", before["Serilog:WriteTo:0:Args:fileSizeLimitBytes"]);
        // An explicit retention is the installer's (or an operator's) choice and stays.
        Assert.False(before.ContainsKey("Serilog:WriteTo:0:Args:retainedFileCountLimit"));

        IReadOnlyDictionary<string, string?> bare = ServerLogFileLimits.MissingSettings(Configuration(
            """{ "Serilog": { "WriteTo": [ { "Name": "Console" }, { "Name": "File", "Args": { "path": "C:\\logs\\x-.ndjson", "rollOnFileSizeLimit": false, "fileSizeLimitBytes": null, "retainedFileCountLimit": null } } ] } }"""));
        Assert.Equal(
            new Dictionary<string, string?>
            {
                ["Serilog:WriteTo:1:Args:rollOnFileSizeLimit"] = "true",
                ["Serilog:WriteTo:1:Args:fileSizeLimitBytes"] = "67108864",
                ["Serilog:WriteTo:1:Args:retainedFileCountLimit"] = "48",
            },
            bare.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value));

        // The disk the v2 log can take, written down where a change to either number shows: 48 x 64 MiB = 3 GiB.
        Assert.Equal(3L * 1024 * 1024 * 1024, ServerLogFileLimits.FileSizeLimitBytes * ServerLogFileLimits.RetainedFileCountLimit);
    }

    [Fact]
    public void ExplicitPositiveSizeLimitIsKeptAndRolledAt()
    {
        IReadOnlyDictionary<string, string?> settings = ServerLogFileLimits.MissingSettings(Configuration(
            """{ "Serilog": { "WriteTo": [ { "Name": "File", "Args": { "path": "C:\\logs\\x-.ndjson", "fileSizeLimitBytes": 1048576, "rollOnFileSizeLimit": true, "retainedFileCountLimit": 10 } } ] } }"""));

        Assert.Empty(settings);
    }

    [Fact]
    public void PackageLogLevelsDropPerStatementSqlAndDuplicateMesIngestLinesButKeepWhatTheReadBackAndSafetyNeed()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindRepositoryRoot(), "src", "ControlServer.Host", "appsettings.json"))
            .Build();
        using Serilog.Core.Logger serilog = new LoggerConfiguration().ReadFrom.Configuration(configuration).CreateLogger();
        using Serilog.Extensions.Logging.SerilogLoggerFactory factory = new(serilog);
        bool Enabled(string category, LogLevel level) => factory.CreateLogger(category).IsEnabled(level);

        // Dropped: one line per SQL statement (20101) and the inner handler's copy of each MesIngest request.
        Assert.False(Enabled("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Information));
        Assert.False(Enabled("System.Net.Http.HttpClient.IMesIngestCatalog.ClientHandler", LogLevel.Information));
        // Kept: a failing statement (20102 is Error), and the outer MesIngest line -- the held read-back's evidence that
        // the dispatch loop ran ($script:HeldRuntimeActivitySources, control-server#578).
        Assert.True(Enabled("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Error));
        Assert.True(Enabled("System.Net.Http.HttpClient.IMesIngestCatalog.LogicalHandler", LogLevel.Information));
        // Kept: the Host's own events, the journey worker (2001), and the vehicle-safety request lines.
        Assert.True(Enabled("ControlServer.Host", LogLevel.Information));
        Assert.True(Enabled("ControlServer.Host.Runtime.JourneyRuntimeWorker", LogLevel.Information));
        Assert.True(Enabled("ControlServer.Host.Runtime.Faults.VehicleFaultCoordinator", LogLevel.Information));
        Assert.True(Enabled("Serilog.AspNetCore.RequestLoggingMiddleware", LogLevel.Information));
        // RIoT: commands and failures at Information and above, successful reads at Debug, off unless turned on.
        Assert.True(Enabled(RiotTrafficCategory, LogLevel.Information));
        Assert.False(Enabled(RiotTrafficCategory, LogLevel.Debug));
    }

    [Fact]
    public async Task RiotClientLogsEveryCommandAndEveryFailureButSuccessfulReadsOnlyAtDebug()
    {
        RecordingLoggerProvider records = new();
        ServiceCollection services = new();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(records));
        services.AddRiotSdkIntegration(new ConfigurationBuilder().Build(), () => new StubHandler());
        using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(RiotSdkRegistration.HttpClientName);

        CancellationToken token = TestContext.Current.CancellationToken;
        (await client.GetAsync("api/order/v1/orderRecord?pageNum=1", token)).Dispose();
        (await client.PostAsync("api/device/v1/command/sync/service/K2/triggerEmergency", new StringContent("{}"), token)).Dispose();
        (await client.GetAsync("api/fail", token)).Dispose();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("api/throw", token));

        Assert.DoesNotContain(records.Entries, entry => entry.Category.StartsWith("System.Net.Http.HttpClient.", StringComparison.Ordinal));
        List<LogRecord> riot = [.. records.Entries.Where(entry => entry.Category == RiotTrafficCategory)];
        // Every command, request and response, at Information.
        Assert.Equal(2, riot.Count(entry => entry.Level == LogLevel.Information && entry.Message.Contains("POST", StringComparison.Ordinal)
            && entry.Message.Contains("/api/device/v1/command/sync/service/K2/triggerEmergency", StringComparison.Ordinal)));
        // Every failure, a status that is not success and an exception, at Information: the level the default loggers
        // used, so no Warning record appears that L2's "silent" criteria would read (the fake RIoT answers 404 often).
        Assert.Contains(riot, entry => entry.Level == LogLevel.Information && entry.Message.Contains("/api/fail", StringComparison.Ordinal)
            && entry.Message.Contains("500", StringComparison.Ordinal));
        Assert.Contains(riot, entry => entry.Level == LogLevel.Information && entry.Message.Contains("/api/throw", StringComparison.Ordinal)
            && entry.Exception is HttpRequestException);
        Assert.DoesNotContain(riot, entry => entry.Level >= LogLevel.Warning);
        // A successful read: Debug only, so it can be turned back on for a diagnosis such as control-server#573.
        Assert.DoesNotContain(riot, entry => entry.Level >= LogLevel.Information && entry.Message.Contains("orderRecord", StringComparison.Ordinal));
        Assert.Contains(riot, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("orderRecord?pageNum=1", StringComparison.Ordinal));
    }

    /// <summary>
    /// The File sink as Install-ControlServerLocal.ps1 renders it (CompactJsonFormatter, daily, shared), bound the way
    /// the Host binds it: ReadFrom.Configuration over the installed file, plus <see cref="ServerLogFileLimits"/>.
    /// </summary>
    internal static Serilog.Core.Logger CreateInstalledFileLogger(string directory, long fileSizeLimitBytes, bool? rollOnFileSizeLimit)
    {
        Dictionary<string, object> args = new()
        {
            ["path"] = Path.Combine(directory, "controlserver-.ndjson"),
            ["formatter"] = "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact",
            ["rollingInterval"] = "Day",
            ["shared"] = true,
            ["fileSizeLimitBytes"] = fileSizeLimitBytes,
        };
        if (rollOnFileSizeLimit is bool roll)
        {
            args["rollOnFileSizeLimit"] = roll;
        }
        string json = JsonSerializer.Serialize(new { Serilog = new { WriteTo = new[] { new { Name = "File", Args = args } } } });
        ConfigurationManager configuration = new();
        configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        configuration.AddInMemoryCollection(ServerLogFileLimits.MissingSettings(configuration));
        return new LoggerConfiguration()
            .ReadFrom.Configuration(configuration, new ConfigurationReaderOptions(
                typeof(Serilog.FileLoggerConfigurationExtensions).Assembly,
                typeof(Serilog.Formatting.Compact.CompactJsonFormatter).Assembly))
            .CreateLogger();
    }

    /// <summary>Newest first, by creation: the order Serilog wrote them in.</summary>
    internal static FileInfo[] LogFilesNewestFirst(string directory) =>
    [
        .. new DirectoryInfo(directory).GetFiles("controlserver-*.ndjson")
            .OrderByDescending(file => file.CreationTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal),
    ];

    internal static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cs587-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ReadShared(FileInfo file)
    {
        using FileStream stream = new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static IConfiguration Configuration(string json) =>
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from the test binary.");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/throw", StringComparison.Ordinal))
            {
                throw new HttpRequestException("connection refused");
            }
            HttpStatusCode status = path.EndsWith("/fail", StringComparison.Ordinal) ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }

    internal sealed record LogRecord(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogRecord> entries = [];

        public IReadOnlyList<LogRecord> Entries
        {
            get
            {
                lock (entries)
                {
                    return [.. entries];
                }
            }
        }

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, this);

        public void Dispose()
        {
        }

        private void Add(LogRecord record)
        {
            lock (entries)
            {
                entries.Add(record);
            }
        }

        private sealed class RecordingLogger(string category, RecordingLoggerProvider owner) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Add(new LogRecord(category, logLevel, eventId, formatter(state, exception), exception));
        }
    }
}
