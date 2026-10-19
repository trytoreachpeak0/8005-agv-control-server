using System.Diagnostics;
using System.Text;
using ControlServer.Host.Runtime.Withdrawal;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// cs#771 的验收条件：人工撤下那趟挂着 UNKNOWN 装货结果的旅程之后，20 号关闸、开闸都不再被它拒绝（10-19 agv03 闸门死结）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么在这里判。</b>20 号（<c>Set-ParallelDispatchGateLocal.ps1</c>）读 v2 实例自己的库：关闸数「不是 Completed 的旅程」，开闸查这些旅程名下
/// 「可能发出过」的订单意图（<c>scripts/parallel/ParallelInstance.psm1</c> 的 <c>Get-ParallelDispatchGateRefusal</c>，读库的是
/// <c>ParallelHost.psm1</c> 的 <c>Get-ParallelJourneyDispatchState</c>）。这里造出与 agv03 同形的库，导成文件，原样调那两个函数：
/// 撤下之前两个方向都拒，撤下之后两个方向都放行。判据本身不改——撤下给的是它认得的那个事实：旅程 <c>Completed</c>。
/// </para>
/// <para>
/// 撤下之前开闸也拒，是因为取货腿的单已经确认建成（<c>CONFIRMED</c>），不是「肯定没发过」。撤下要求现场先核过 RIoT 上本车没有未结束的单
/// （<see cref="JourneyWithdrawalRequest.RiotVerification"/>），所以收尾之后开闸检查不再看这些意图是对的。
/// </para>
/// </remarks>
public sealed class JourneyWithdrawalDispatchGateTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AfterTheWithdrawalTheGateClosesAndOpensAgain()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await JourneyWithdrawalTests.BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        string directory = Path.Combine(Path.GetTempPath(), "cs771-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string before = Export(fixture, Path.Combine(directory, "before.db"));
            (string close, string open) = JudgeGate(before);
            Assert.StartsWith("GATE_CLOSE_REFUSED_IN_FLIGHT", close, StringComparison.Ordinal);
            Assert.StartsWith("GATE_OPEN_REFUSED_ORDER_SENT", open, StringComparison.Ordinal);

            await JourneyWithdrawalTests.PlanAndWithdrawAsync(fixture, await JourneyWithdrawalTests.RequestAsync(fixture, blocked));

            string after = Export(fixture, Path.Combine(directory, "after.db"));
            Assert.Equal(("NULL", "NULL"), JudgeGate(after));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The fixture's in-memory database, copied page for page into a file the gate script can open read-only.</summary>
    internal static string Export(RuntimeFixture fixture, string path)
    {
        SqliteConnection source = (SqliteConnection)fixture.Context.Database.GetDbConnection();
        using SqliteConnection target = new($"Data Source={path};Pooling=False");
        target.Open();
        source.BackupDatabase(target);
        return path;
    }

    /// <summary>What 20 号 would say in each direction: the refusal, or <c>NULL</c> when it may go ahead.</summary>
    private static (string Close, string Open) JudgeGate(string databasePath)
    {
        string parallel = Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "scripts", "parallel");
        string output = RunPwsh(
            $"Import-Module '{Path.Combine(parallel, "ParallelInstance.psm1")}' -Force; " +
            $"Import-Module '{Path.Combine(parallel, "ParallelHost.psm1")}' -Force; " +
            $"$state = Get-ParallelJourneyDispatchState -DatabasePath '{databasePath}' -AssemblyDirectory '{AppContext.BaseDirectory}'; " +
            "foreach ($direction in 'Close', 'Open') { " +
            $"$refusal = Get-ParallelDispatchGateRefusal -Direction $direction -State $state -ServiceName 'cs771-v2' -DatabasePath '{databasePath}'; " +
            "\"$direction|$($refusal ?? 'NULL')\" }");
        Dictionary<string, string> verdicts = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("Close|", StringComparison.Ordinal) || line.StartsWith("Open|", StringComparison.Ordinal))
            .ToDictionary(line => line[..line.IndexOf('|', StringComparison.Ordinal)], line => line[(line.IndexOf('|', StringComparison.Ordinal) + 1)..]);
        Assert.True(verdicts.ContainsKey("Close") && verdicts.ContainsKey("Open"), $"gate judgement not read: {output}");
        return (verdicts["Close"], verdicts["Open"]);
    }

    private static string RunPwsh(string script)
    {
        ProcessStartInfo start = new("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-Command",
                     "$ErrorActionPreference = 'Stop'; [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); " + script,
                 })
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"pwsh failed (exit {process.ExitCode}): {stdout}{stderr}");
        return stdout;
    }
}
