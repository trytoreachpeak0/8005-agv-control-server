using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Tests;

/// <summary>
/// control-server#812. A redirected .NET console writes in the console's code page: GBK on a zh-CN machine, and on an
/// en-US one (code page 437) every Chinese character became a literal '?' (0x3F) before it reached the file, so the
/// L2 scenario task-type-binding-station-reused-refuses-start could not find 关卡/210 in control-server.out.log. The
/// Host's redirected output is UTF-8 whatever the system locale; here a real Host process writes a path that holds
/// 关卡 to a redirected stdout, and the raw bytes are read back. A GBK writer (zh-CN) and a '?' writer (en-US) both
/// fail, so the test does not depend on the locale of the machine it runs on.
/// </summary>
public sealed class ServerConsoleEncodingTests : IAsyncDisposable
{
    /// <summary>What the host logs while it waits for the lock (<c>DatabaseLockWaiting</c>, event 9404); it names the database.</summary>
    private static readonly byte[] LockWaitingLog = Encoding.ASCII.GetBytes("Another process holds the lock on database");

    private static readonly string HostExecutable = typeof(ServerConsoleEncodingTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "ControlServer.Host.Path").Value!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "w2g-console-encoding-" + Guid.NewGuid().ToString("N"));

    private Process? _host;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RedirectedStandardOutputIsUtf8WithoutAByteOrderMark()
    {
        string database = Path.Combine(_root, "关卡", "controlserver.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        // Holding the database's lock makes the host log the database path, 关卡 included, and then wait: a line with
        // Chinese in it that needs no RIoT, MesIngest or onboard peer.
        using ControlServerDatabaseLock held = ControlServerDatabaseLock.TryAcquire(database)
            ?? throw new InvalidOperationException("The test could not take the lock on its own fresh database.");

        _host = Process.Start(HostStart(database))!;
        MemoryStream stdout = new();
        Task copying = CollectAsync(_host.StandardOutput.BaseStream, stdout);
        _ = _host.StandardError.BaseStream.CopyToAsync(Stream.Null, Token);

        byte[] bytes = await WaitForAsync(stdout, copying, LockWaitingLog, TimeSpan.FromSeconds(60));

        Assert.False(
            bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble),
            "The redirected output starts with a UTF-8 byte order mark; readers that match the first line would see U+FEFF.");
        byte[] utf8 = Encoding.UTF8.GetBytes("关卡");
        Assert.True(
            bytes.AsSpan().IndexOf(utf8) >= 0,
            $"关卡 ({Convert.ToHexString(utf8)}) is not in the redirected output as UTF-8. The line it is in: {Hex(bytes, LockWaitingLog)}");
    }

    /// <summary>
    /// The last line before the process dies is on the redirected stream, UTF-8: an L2 scenario's verdict often rests on
    /// it. The lock is held past <c>DatabaseLockStartup.Wait</c>, so the host logs <c>Startup refused</c> with the database
    /// path and its start throws out of Program.cs, unhandled: no host shutdown, no orderly flush of anything.
    /// </summary>
    [Fact]
    public async Task TheLastLineBeforeAnUnhandledExitIsOnTheRedirectedStreamAsUtf8()
    {
        string database = Path.Combine(_root, "关卡", "controlserver.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using ControlServerDatabaseLock held = ControlServerDatabaseLock.TryAcquire(database)
            ?? throw new InvalidOperationException("The test could not take the lock on its own fresh database.");

        _host = Process.Start(HostStart(database))!;
        MemoryStream stdout = new();
        MemoryStream stderr = new();
        Task copyingOut = CollectAsync(_host.StandardOutput.BaseStream, stdout);
        Task copyingErr = CollectAsync(_host.StandardError.BaseStream, stderr);
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(Token);
        bounded.CancelAfter(TimeSpan.FromSeconds(90));
        await _host.WaitForExitAsync(bounded.Token);
        await Task.WhenAll(copyingOut, copyingErr);

        Assert.NotEqual(0, _host.ExitCode);
        string text = Encoding.UTF8.GetString(stdout.ToArray());
        string last = text.TrimEnd('\r', '\n').Split('\n')[^1];
        Assert.Contains("Startup refused: DATABASE_IN_USE", last, StringComparison.Ordinal);
        Assert.Contains("关卡", last, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the host writes with <c>Console.WriteLine</c> rather than through Serilog (whose sink flushes after every
    /// event) is on the redirected stream when the process ends: the writer that replaced <c>Console.Out</c> is not
    /// flushed at exit by anyone, so it flushes on every write. <c>--import-package-capacity</c> ends on such a line.
    /// </summary>
    [Fact]
    public async Task AConsoleWriteLineJustBeforeTheProcessEndsIsOnTheRedirectedStream()
    {
        string database = Path.Combine(_root, "关卡", "controlserver.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        string csv = Path.Combine(_root, "package-capacity.csv");
        await File.WriteAllLinesAsync(
            csv,
            ["pattern,match_type,max_boxes_per_basket,source,status,note", "PKG-812,exact,4,cs812-test,active,flush at exit"],
            Token);
        ProcessStartInfo import = HostStart(database);
        foreach (string argument in new[] { "--import-package-capacity", "--input", csv, "--version", "1" })
        {
            import.ArgumentList.Add(argument);
        }

        _host = Process.Start(import)!;
        MemoryStream stdout = new();
        Task copyingOut = CollectAsync(_host.StandardOutput.BaseStream, stdout);
        Task copyingErr = _host.StandardError.BaseStream.CopyToAsync(Stream.Null, Token);
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(Token);
        bounded.CancelAfter(TimeSpan.FromSeconds(120));
        await _host.WaitForExitAsync(bounded.Token);
        await Task.WhenAll(copyingOut, copyingErr);

        string text = Encoding.UTF8.GetString(stdout.ToArray());
        Assert.True(_host.ExitCode == 0, text);
        Assert.StartsWith(
            "Package capacity import complete: inserted=1",
            text.TrimEnd('\r', '\n').Split('\n')[^1],
            StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            try
            {
                if (!_host.HasExited)
                {
                    _host.Kill(entireProcessTree: true);
                }
                await _host.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Never started, or already reaped.
            }
            _host.Dispose();
        }
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file the killed host still had open; the temp directory is cleaned up by the machine.
        }
    }

    private static ProcessStartInfo HostStart(string database)
    {
        Assert.True(File.Exists(HostExecutable), $"The host was not built: {HostExecutable}");
        ProcessStartInfo start = new(HostExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // A console of its own (hidden), as the host has under the L2 harness's pwsh. The test host has none, and a
            // .NET process without a console has no console code page to fall back to and writes UTF-8 anyway: without
            // this line the test passes on the defect.
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(HostExecutable)!
        };
        string invariant(int port) => port.ToString(CultureInfo.InvariantCulture);
        start.Environment["ConnectionStrings__ControlServer"] = $"Data Source={database}";
        start.Environment["Health__url"] = $"http://127.0.0.1:{invariant(FreePort())}";
        start.Environment["OnboardTransport__listenAddress"] = "127.0.0.1";
        start.Environment["OnboardTransport__port"] = invariant(FreePort());
        // Nothing here gets past the database lock, but the external addresses still go to loopback ports nothing
        // listens on, as in ControlServerDatabaseLockTests.
        start.Environment["RIoT__baseUrl"] = $"http://127.0.0.1:{invariant(FreePort())}";
        start.Environment["MesIngest__baseUrl"] = $"http://127.0.0.1:{invariant(FreePort())}";
        start.Environment["JourneyRuntime__enabled"] = "false";
        start.Environment.Remove("CONTROL_SERVER_RIOT_CALL_API_KEY");
        return start;
    }

    private async Task<byte[]> WaitForAsync(MemoryStream stdout, Task copying, byte[] marker, TimeSpan limit)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (true)
        {
            byte[] bytes;
            lock (stdout)
            {
                bytes = stdout.ToArray();
            }
            int at = bytes.AsSpan().IndexOf(marker);
            int end = at < 0 ? -1 : Array.IndexOf(bytes, (byte)'\n', at);
            if (end >= 0)
            {
                return bytes;
            }
            Assert.True(
                clock.Elapsed < limit && !copying.IsCompleted,
                $"The host never logged the lock wait (exited: {_host!.HasExited}). Output: {Encoding.UTF8.GetString(bytes)}");
            await Task.Delay(50, Token);
        }
    }

    /// <summary>Copies the process's raw output into <paramref name="into"/>, under its lock, as it arrives.</summary>
    private static async Task CollectAsync(Stream from, MemoryStream into)
    {
        byte[] buffer = new byte[4096];
        int read;
        while ((read = await from.ReadAsync(buffer, Token)) > 0)
        {
            lock (into)
            {
                into.Write(buffer, 0, read);
            }
        }
    }

    /// <summary>The line holding <paramref name="marker"/>, as hex: the evidence when the bytes are not UTF-8.</summary>
    private static string Hex(byte[] bytes, byte[] marker)
    {
        int at = bytes.AsSpan().IndexOf(marker);
        int end = Array.IndexOf(bytes, (byte)'\n', at);
        return Convert.ToHexString(bytes, at, end - at);
    }

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
