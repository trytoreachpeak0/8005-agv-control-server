using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using static ControlServer.Tests.WaitingPointImportHarness;

namespace ControlServer.Tests;

/// <summary>
/// FieldOps 的 <c>session</c> 入口（control-server#804）：一个进程从 stdin 逐行执行命令，供 L2 搭环境省掉每条命令新起进程的那约 2 秒。
/// 真起一个 <c>ControlServer.FieldOps.exe session</c> 进程，对同一个真 SQLite 文件；判的是它与「一条命令一个进程」在输出、
/// 退出码与库上看到的东西一样，一条失败的命令报出自己的退出码而不拖垮后面的命令，命令之间不留库文件的句柄。
/// </summary>
public sealed class FieldOpsSessionTests
{
    private const string Import = "import-waiting-points";

    private const string Read = "read-waiting-points";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string FieldOps = typeof(FieldOpsSessionTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "ControlServer.FieldOps.Path").Value!;

    private static readonly string Site = Csv("26,214,等待点1,true,", "26,215,等待点2,true,", "26,216,等待点3,true,");

    [Fact]
    public async Task EachCommandAnswersAsItsOwnProcessWouldAndAFailureDoesNotStopTheNext()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        string table = harness.WriteCsv("site.csv", Site);
        string bad = harness.WriteCsv("bad.csv", Csv("26,212,充电准备点1,true,", "26,214,等待点1,true,"));
        await using Session session = Session.Start();

        SessionReply preview = await session.RunAsync(ImportArguments(harness, table, "--dry-run"));
        SessionReply rejected = await session.RunAsync(ImportArguments(harness, bad));
        SessionReply unknown = await session.RunAsync("no-such-verb", "--database", harness.DatabasePath);
        SessionReply imported = await session.RunAsync(ImportArguments(harness, table));
        SessionReply again = await session.RunAsync(ImportArguments(harness, table));
        SessionReply read = await session.RunAsync(Read, "--database", harness.DatabasePath, "--map", "26", "--fleet", "VK-A;VK-B");

        Assert.Equal((0, "OK", true), (preview.ExitCode, preview.Json.GetProperty("outcome").GetString(), preview.Json.GetProperty("dryRun").GetBoolean()));
        Assert.Equal((1, "REJECTED"), (rejected.ExitCode, rejected.Json.GetProperty("outcome").GetString()));
        Assert.Equal(2, unknown.ExitCode);
        Assert.Contains("unknown command 'no-such-verb'", unknown.Text, StringComparison.Ordinal);
        Assert.Equal((0, "OK", 1L), (imported.ExitCode, imported.Json.GetProperty("outcome").GetString(), imported.Json.GetProperty("version").GetInt64()));
        Assert.Equal((0, "UNCHANGED", 1L), (again.ExitCode, again.Json.GetProperty("outcome").GetString(), again.Json.GetProperty("version").GetInt64()));
        Assert.Equal(
            (0, 1L, "214 等待点1;215 等待点2;216 等待点3"),
            (read.ExitCode, read.Json.GetProperty("version").GetInt64(), string.Join(";", read.Json.GetProperty("points").EnumerateArray()
                .Select(point => $"{point.GetProperty("stationId").GetInt32()} {point.GetProperty("stationName").GetString()}"))));
        Assert.Equal((1L, 3L, 0L, 1L, 1L), await harness.FootprintAsync());
        Assert.Equal(0, await session.EndAsync());
    }

    /// <summary>
    /// 单独起进程时，进程一退出它的 SQLite 连接就没了。会话活着，命令之间也不能留着库文件：池里的空闲连接在 Windows 上
    /// 会挡住改名与删除，L2 收尾删 stage 目录、换库的场景都会撞上。
    /// </summary>
    [Fact]
    public async Task BetweenCommandsTheSessionHoldsNoHandleOnTheDatabaseFile()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        string table = harness.WriteCsv("site.csv", Site);
        await using Session session = Session.Start();

        SessionReply imported = await session.RunAsync(ImportArguments(harness, table));
        SessionReply read = await session.RunAsync(Read, "--database", harness.DatabasePath);
        Assert.Equal((0, 0), (imported.ExitCode, read.ExitCode));

        // This process opened the file too, to migrate it, and its pool keeps that connection: only the session may be
        // what stops the move below.
        using (SqliteConnection own = new(ControlServerSqlite.ForDatabaseFile(harness.DatabasePath)))
        {
            SqliteConnection.ClearPool(own);
        }
        string moved = harness.DatabasePath + ".moved";
        File.Move(harness.DatabasePath, moved);
        File.Move(moved, harness.DatabasePath);
        Assert.Equal(0, await session.EndAsync());
    }

    [Fact]
    public async Task AMalformedLineOrANestedSessionAnswersTwoAndTheSessionGoesOn()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await using Session session = Session.Start();

        SessionReply notJson = await session.RunLineAsync("import-waiting-points --database x");
        SessionReply notStrings = await session.RunLineAsync("[\"read-waiting-points\", 1]");
        SessionReply nested = await session.RunAsync("session");
        SessionReply read = await session.RunAsync(Read, "--database", harness.DatabasePath);

        Assert.Equal((2, 2, 2), (notJson.ExitCode, notStrings.ExitCode, nested.ExitCode));
        Assert.Contains("one JSON array of strings", notJson.Text, StringComparison.Ordinal);
        Assert.Contains("cannot start another session", nested.Text, StringComparison.Ordinal);
        Assert.Equal((0, JsonValueKind.Null), (read.ExitCode, read.Json.GetProperty("version").ValueKind));
        Assert.Equal(0, await session.EndAsync());
    }

    [Fact]
    public async Task ASessionGivenOptionsOfItsOwnRefusesToStart()
    {
        await using Session session = Session.Start("--database", "x.db");

        Assert.Equal(2, await session.EndAsync());
    }

    private static string[] ImportArguments(WaitingPointImportHarness harness, string table, params string[] extra) =>
    [
        Import, "--database", harness.DatabasePath, "--input", table, "--catalog", harness.CatalogPath,
        "--map", "26", "--fleet", "VK-A;VK-B", .. extra
    ];

    private sealed record SessionReply(int ExitCode, string Text, JsonElement Json);

    private sealed class Session : IAsyncDisposable
    {
        /// <summary>How long one command may take to answer. A session that never answers fails the test instead of hanging it.</summary>
        private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(60);

        private readonly Process _process;

        // Drained from the start: a process that fills its stderr pipe (a usage text) blocks before it ever closes stdout.
        private readonly Task<string> _errors;

        private Session(Process process)
        {
            _process = process;
            _errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        }

        public static Session Start(params string[] options)
        {
            Assert.True(File.Exists(FieldOps), $"FieldOps was not built next to the tests: {FieldOps}");
            ProcessStartInfo start = new(FieldOps)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add("session");
            foreach (string option in options)
            {
                start.ArgumentList.Add(option);
            }
            return new Session(Process.Start(start)!);
        }

        public Task<SessionReply> RunAsync(params string[] arguments) => RunLineAsync(JsonSerializer.Serialize(arguments));

        public async Task<SessionReply> RunLineAsync(string line)
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), Token);
            await _process.StandardInput.FlushAsync(Token);
            List<string> lines = [];
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(AnswerTimeout);
            while (true)
            {
                string? read = await _process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.True(read is not null, $"The session ended before answering '{line}': {string.Join('\n', lines)}");
                if (read.StartsWith("##fieldops-session-exit ", StringComparison.Ordinal))
                {
                    int exitCode = int.Parse(read["##fieldops-session-exit ".Length..], System.Globalization.CultureInfo.InvariantCulture);
                    string? json = lines.LastOrDefault(text => text.TrimStart().StartsWith('{'));
                    JsonElement element = default;
                    if (json is not null)
                    {
                        using JsonDocument document = JsonDocument.Parse(json);
                        element = document.RootElement.Clone();
                    }
                    return new SessionReply(exitCode, string.Join('\n', lines), element);
                }
                lines.Add(read);
            }
        }

        /// <summary>关 stdin，等进程退出，返回退出码。</summary>
        public async Task<int> EndAsync()
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
                // A session that refused to start has already exited: its end of the pipe is gone.
            }
            Task<string> rest = _process.StandardOutput.ReadToEndAsync(Token);
            await _process.WaitForExitAsync(Token);
            _ = await rest;
            _ = await _errors;
            return _process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None);
            }
            _process.Dispose();
        }
    }
}
