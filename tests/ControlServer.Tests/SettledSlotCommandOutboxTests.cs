using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 已有结论的 SlotOperationCommand 退出补发，仍在等结果的照常补发（control-server#623）。
/// </summary>
/// <remarks>
/// 补偿放回之后的永久补发（生产 3f01992d）在 <see cref="RecoveryEndingReleasesBlockedJourneyTests"/> 里，经真实入站处理器与引擎走完；
/// 这里是另外几种情况：结果在恢复期间到（cs#205 真装置证据的形状）、仍在等结果、需求终结、旅程收尾、启动修复与只读残留扫描。
/// </remarks>
public sealed class SettledSlotCommandOutboxTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";
    private const string V3Manifest = "4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// cs#205 的形状：装货结果在会话不就绪期间到、装货 Committed；会话回到就绪的第一轮不再补发这条命令——修之前这一轮先补发、后结算。
    /// </summary>
    [Fact]
    public async Task ALoadCommittedWhileTheSessionWasNotReadyIsNotReplayedOnTheFirstReadyRound()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await AdvanceToLoadResultAsync(fixture);
        string command = await LoadCommandIdAsync(fixture);
        await fixture.DropOnboardSessionAsync();
        await fixture.ApplySafeResultAsync(await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.RestoreSessionReadyAsync();
        int sentBefore = fixture.Peer.Lines.Count;

        await RunRoundAsync(fixture);

        Assert.Equal(0, RecoveryEndingReleasesBlockedJourneyTests.SentSince(fixture, sentBefore, command));
        Assert.NotNull((await OutboxRowAsync(fixture, command)).AcknowledgedAt);
    }

    /// <summary>
    /// 吸收路径（票面第 1 步）：服务端重发了命令，车载端按 attempt 去重、原样重放它的 OperationResult（同一 messageId、整行一致）。
    /// 服务端回 DurableAck，只记一条结果，站点操作的状态不变，命令行仍是已确认。
    /// </summary>
    [Fact]
    public async Task TheVehicleReplayingItsOriginalOperationResultIsAbsorbedAsADuplicate()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await AdvanceToLoadResultAsync(fixture);
        string command = await LoadCommandIdAsync(fixture);
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
        OnboardConnectionState state = StopEndedJourneyContinuesTests.Connection(fixture);
        string line = LoadedResultLine(fixture, load);

        string first = await processor.ProcessAsync(line, state, Token);
        await RunRoundAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        StationOperationStatus afterFirst = (await fixture.OperationAsync(SlotOperationType.Load)).Status;
        string replayed = await processor.ProcessAsync(line, state, Token);

        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(("DurableAck", "DurableAck"), (FirstLineType(first), FirstLineType(replayed)));
        Assert.Equal(1, await fixture.Context.OperationResults.AsNoTracking()
            .CountAsync(row => row.SlotOperationAttemptId == load.SlotOperationAttemptId, Token));
        Assert.Equal((StationOperationStatus.Committed, StationOperationStatus.Committed),
            (afterFirst, (await fixture.OperationAsync(SlotOperationType.Load)).Status));
        Assert.NotNull((await OutboxRowAsync(fixture, command)).AcknowledgedAt);
    }

    /// <summary>护栏：结果还没到、需求与旅程都还活着，命令照常补发，行仍待补发。</summary>
    [Fact]
    public async Task ALoadStillAwaitingItsResultIsReplayedAsBefore()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await AdvanceToLoadResultAsync(fixture);
        string command = await LoadCommandIdAsync(fixture);
        await fixture.DropOnboardSessionAsync();
        await fixture.RestoreSessionReadyAsync();
        int sentBefore = fixture.Peer.Lines.Count;

        await RunRoundAsync(fixture);

        Assert.Equal(1, RecoveryEndingReleasesBlockedJourneyTests.SentSince(fixture, sentBefore, command));
        Assert.Contains(command, await fixture.PendingOutboxMessageIdsAsync());
    }

    /// <summary>
    /// 会话因本服务端自己的在途单而未就绪（真车载端行驶全程的状态）：结果到了，引擎在就绪闸门前返回、不补发；回到就绪那一轮先结算，照样不补发。
    /// </summary>
    [Fact]
    public async Task ALoadAnsweredWhileTheSessionIsNotReadyOnTheServersOwnOrderIsNotReplayedOnceReady()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await AdvanceToLoadResultAsync(fixture);
        string command = await LoadCommandIdAsync(fixture);
        await DropSessionOnOwnOrderAsync(fixture);
        await fixture.ApplySafeResultAsync(await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load, SlotBusinessState.Occupied);
        int sentBefore = fixture.Peer.Lines.Count;

        await RunRoundAsync(fixture);
        int whileNotReady = RecoveryEndingReleasesBlockedJourneyTests.SentSince(fixture, sentBefore, command);
        await fixture.RestoreSessionReadyAsync();
        await RunRoundAsync(fixture);

        Assert.Equal((0, 0), (whileNotReady, RecoveryEndingReleasesBlockedJourneyTests.SentSince(fixture, sentBefore, command)));
        Assert.DoesNotContain(command, await fixture.PendingOutboxMessageIdsAsync());
    }

    /// <summary>结果没到、站点操作仍是 Prepared，而所属需求已终结：退役（不是确认——车没有作答）。</summary>
    [Fact]
    public async Task AnUnansweredCommandOfATerminatedDemandIsFenced()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await AdvanceToLoadResultAsync(fixture);
        string command = await LoadCommandIdAsync(fixture);
        JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>()
            .SingleAsync(row => row.LoadCommandMessageId == command, Token);
        membership.Status = JourneyDemandStatuses.Terminated;

        (int answered, int withdrawn) = await SlotCommandOutbox.ConcludeAsync(fixture.Context, fixture.Options.AgvId, fixture.Clock.GetUtcNow(), Token);
        await fixture.Context.SaveChangesAsync(Token);

        ProtocolOutboxRow row = await OutboxRowAsync(fixture, command);
        Assert.Equal((0, 1), (answered, withdrawn));
        Assert.Equal((false, true), (row.AcknowledgedAt is not null, row.FencedAt is not null));
    }

    /// <summary>旅程收尾时还挂着一条没结果的装货命令（例如人工撤下旅程）：经唯一收尾出口 <see cref="JourneyClosure"/> 退役。</summary>
    [Fact]
    public async Task ClosingAJourneyFencesItsCommandStillAwaitingAResult()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await AdvanceToLoadResultAsync(fixture);
        string command = await LoadCommandIdAsync(fixture);
        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.SingleAsync(Token);

        await JourneyClosure.StageAsync(fixture.Context, runtime, ControlServer.Host.Runtime.Withdrawal.JourneyWithdrawal.WithdrawnReason, fixture.Clock.GetUtcNow(), Token);
        await fixture.Context.SaveChangesAsync(Token);

        Assert.NotNull((await OutboxRowAsync(fixture, command)).FencedAt);
        Assert.DoesNotContain(command, await fixture.PendingOutboxMessageIdsAsync());
    }

    /// <summary>
    /// 已有库：修之前留下的已作答命令，信封是上一版协议身份。只读扫描列出它；启动修复确认它，换身份的启动检查随后放行。幂等。
    /// 还在等结果的命令不在扫描的残留里，启动修复也不动它。
    /// </summary>
    [Fact]
    public async Task TheStartupRepairSettlesAForeignIdentityAnsweredCommandSoTheIdentityCheckPasses()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await AdvanceToLoadResultAsync(fixture);
        string command = await LoadCommandIdAsync(fixture);
        await fixture.ApplySafeResultAsync(await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load, SlotBusinessState.Occupied);
        await RewriteToV3IdentityAsync(fixture, command);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProtocolOutboxIdentityStartupCheck.EnsureAsync(fixture.Context, NullLogger.Instance, null, Token));
        OutboxResidueLine scanned = Assert.Single(
            await OutboxResidue.ScanAsync(fixture.Context, Token), line => line.MessageId == command);
        Assert.Equal((OutboxResidue.SlotCommandAnswered, "ANSWERED"), (Assert.Single(scanned.Reasons), scanned.SlotCommandConclusion));

        await using ServiceProvider services = HostServices(fixture);
        await SlotCommandOutbox.ConcludeAtStartupAsync(services, Token);

        fixture.Context.ChangeTracker.Clear();
        Assert.NotNull((await OutboxRowAsync(fixture, command)).AcknowledgedAt);
        await ProtocolOutboxIdentityStartupCheck.EnsureAsync(services, Token);
        await SlotCommandOutbox.ConcludeAtStartupAsync(services, Token);
        await ProtocolOutboxIdentityStartupCheck.EnsureAsync(services, Token);
    }

    /// <summary>护栏：仍在等结果的命令，扫描不算残留，启动修复不动它，身份检查照旧拦（身份检查本身不放宽）。</summary>
    [Fact]
    public async Task TheStartupRepairLeavesACommandStillAwaitingItsResultAlone()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await AdvanceToLoadResultAsync(fixture);
        string command = await LoadCommandIdAsync(fixture);
        await RewriteToV3IdentityAsync(fixture, command);

        OutboxResidueLine scanned = Assert.Single(
            await OutboxResidue.ScanAsync(fixture.Context, Token), line => line.MessageId == command);
        await using ServiceProvider services = HostServices(fixture);
        await SlotCommandOutbox.ConcludeAtStartupAsync(services, Token);

        Assert.Equal(("OPEN", 0), (scanned.SlotCommandConclusion, scanned.Reasons.Length));
        Assert.Contains(command, await fixture.PendingOutboxMessageIdsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProtocolOutboxIdentityStartupCheck.EnsureAsync(services, Token));
    }

    /// <summary>
    /// FieldOps <c>outbox-residue</c>：真起一个进程，对一份落到文件里的库。列出已作答而未确认的命令（退出码 1，<c>FINDINGS</c>），
    /// 以 SQLite 只读模式开库，库文件一个字节都不变。
    /// </summary>
    [Fact]
    public async Task TheFieldOpsResidueVerbListsAnAnsweredCommandWithoutWritingTheDatabase()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await AdvanceToLoadResultAsync(fixture);
        string command = await LoadCommandIdAsync(fixture);
        await fixture.ApplySafeResultAsync(await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load, SlotBusinessState.Occupied);
        string directory = Path.Combine(Path.GetTempPath(), "cs623-residue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string database = Path.Combine(directory, "control-server.db");
        try
        {
            await using (SqliteConnection file = new($"Data Source={database};Pooling=False"))
            {
                await file.OpenAsync(Token);
                ((SqliteConnection)fixture.Context.Database.GetDbConnection()).BackupDatabase(file);
            }
            byte[] before = await File.ReadAllBytesAsync(database, Token);

            (int exit, JsonElement output) = await RunFieldOpsAsync("outbox-residue", "--database", database);

            Assert.True(ControlServer.FieldOps.Program.OpensReadOnly("outbox-residue"));
            Assert.Equal((1, "FINDINGS"), (exit, output.GetProperty("outcome").GetString()));
            JsonElement finding = Assert.Single(output.GetProperty("findings").EnumerateArray().ToArray(),
                item => item.GetProperty("messageId").GetString() == command);
            Assert.Equal(
                ("SlotOperationCommand", "ANSWERED", OutboxResidue.SlotCommandAnswered),
                (finding.GetProperty("messageType").GetString(), finding.GetProperty("slotCommandConclusion").GetString(),
                 Assert.Single(finding.GetProperty("reasons").EnumerateArray().ToArray()).GetString()));
            Assert.Equal(before, await File.ReadAllBytesAsync(database, Token));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>主机启动顺序：命令的启动修复在库迁移之后、发件箱身份检查之前。</summary>
    [Fact]
    public void TheHostConcludesSlotCommandsAfterMigratingAndBeforeTheOutboxIdentityCheck()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "src", "ControlServer.Host", "Program.cs"));
        int Line(string call) => Assert.Single([.. lines.Select((line, index) => (line.Trim(), index))
            .Where(item => item.Item1.StartsWith("await " + call + "(app.Services", StringComparison.Ordinal))
            .Select(item => item.index)]);

        int migrate = Line("EnsureDatabaseAsync");
        int repair = Line("SlotCommandOutbox.ConcludeAtStartupAsync");
        int identity = Line("ProtocolOutboxIdentityStartupCheck.EnsureAsync");

        Assert.True(migrate < repair && repair < identity, $"migrate={migrate + 1} repair={repair + 1} identity={identity + 1}");
    }

    /// <summary>一条需求、一个取货站：录入已受理、装货命令已发，等结果。</summary>
    private static async Task AdvanceToLoadResultAsync(RuntimeFixture fixture)
    {
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.AdvanceToLoadResultAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, runtime.Stage);
    }

    private static async Task RunRoundAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(Token);
    }

    private static Task DropSessionOnOwnOrderAsync(RuntimeFixture fixture) =>
        PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);

    private static async Task<string> LoadCommandIdAsync(RuntimeFixture fixture) =>
        (await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageType == "SlotOperationCommand", Token)).MessageId;

    private static Task<ProtocolOutboxRow> OutboxRowAsync(RuntimeFixture fixture, string messageId) =>
        fixture.Context.ProtocolOutbox.AsNoTracking().SingleAsync(row => row.MessageId == messageId, Token);

    /// <summary>修之前、上一版协议写下的样子：信封身份改成 v3。</summary>
    private static async Task RewriteToV3IdentityAsync(RuntimeFixture fixture, string messageId)
    {
        ProtocolOutboxRow row = await fixture.Context.ProtocolOutbox.SingleAsync(item => item.MessageId == messageId, Token);
        JsonObject envelope = JsonNode.Parse(row.PayloadJson)!.AsObject();
        envelope["protocolVersion"] = 3;
        envelope["protocolReleaseVersion"] = "2.0.0";
        envelope["protocolReleaseManifestSha256"] = V3Manifest;
        row.PayloadJson = envelope.ToJsonString();
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>车对这次装货报的 OperationResult 整行（每个目标仓位装上、锁好）。同一行发两次就是车载端的原样重放。</summary>
    private static string LoadedResultLine(RuntimeFixture fixture, StationOperationRow load)
    {
        int[] slots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson) ?? [];
        var withoutHash = new
        {
            demandId = load.DemandId,
            slotOperationAttemptId = load.SlotOperationAttemptId,
            operationType = "LOAD",
            overallOutcome = "COMPLETED",
            slotResults = slots.Select(slot => new
            {
                slotNo = slot,
                outcome = "COMPLETED",
                finalPhysicalState = "OCCUPIED",
                lockState = "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = Array.Empty<string>()
            }).ToArray(),
            observedAt = fixture.Clock.GetUtcNow(),
            journalCheckpoint = "RESULT_RECORDED"
        };
        string hash = Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(withoutHash, SerializerOptions))).ToLowerInvariant();
        return StopEndedJourneyContinuesTests.Envelope(fixture, Guid.NewGuid().ToString("D"), "OperationResult", new
        {
            withoutHash.demandId,
            withoutHash.slotOperationAttemptId,
            withoutHash.operationType,
            withoutHash.overallOutcome,
            withoutHash.slotResults,
            withoutHash.observedAt,
            withoutHash.journalCheckpoint,
            resultContentSha256 = hash
        });
    }

    private static string FirstLineType(string wire)
    {
        using JsonDocument document = JsonDocument.Parse(wire.Split('\n', StringSplitOptions.RemoveEmptyEntries).First());
        return document.RootElement.GetProperty("messageType").GetString()!;
    }

    private static async Task<(int ExitCode, JsonElement Output)> RunFieldOpsAsync(params string[] arguments)
    {
        string fieldOps = typeof(SettledSlotCommandOutboxTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "ControlServer.FieldOps.Path").Value!;
        ProcessStartInfo start = new(fieldOps)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        string output = (await stdout).Trim();
        Assert.True(output.Length > 0, $"FieldOps printed nothing (exit {process.ExitCode}): {await stderr}");
        using JsonDocument document = JsonDocument.Parse(output);
        return (process.ExitCode, document.RootElement.Clone());
    }

    private static ServiceProvider HostServices(RuntimeFixture fixture)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(fixture.Clock);
        services.AddScoped(_ => new ControlServerDbContext(fixture.DbOptionsForTests));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("ControlServer.sln not found above the test output.");
    }
}
