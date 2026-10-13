using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 安全状态消息按收件时间从新到旧读、遇到即停（control-server#597）：挑中的那一行必须与改动前「整类读回、内存里稳定排序」挑中的一样。
/// </summary>
/// <remarks>
/// <para>
/// 这里的用例先在改动前的代码上跑绿，钉住今天的挑选结果，改完仍要绿。所以没有「先红」，红证据靠变异取。
/// </para>
/// <para>
/// <b>同一收件时间（同一个 tick）多行时今天挑哪一行。</b>派车事实（<c>LatestSafetySummaryForSessionAsync</c>）把按类型读回的行按收件时间
/// 稳定地倒排，同 tick 的行保持库返回的次序；库走 <c>IX_ProtocolInbox_MessageType</c>，所以同 tick 时 <c>SafetyStateChanged</c> 在
/// <c>SafetyStateSnapshot</c> 前、同类先写入的在前。在途门锁（<see cref="InTransitDoorFacts"/>）不按收件时间排，取库返回次序里第一条匹配的。
/// 生产写入路径存不下两行同车同代同版本号（版本相同而整行哈希不同按 <c>SNAPSHOT_REVISION_CONTENT_CONFLICT</c> 拒收，拒收的消息不入库），
/// 所以下面那几条重复版本的行是直接插库造的：只有这样，挑中哪一行才看得出来。
/// </para>
/// </remarks>
public sealed class SafetyStateLatestFirstReadTests
{
    private const string AgvId = "AGV-01";
    private const string OtherAgvId = "AGV-02";
    private const long Generation = 7;
    private const long Revision = 5;
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(8));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 派车事实：当前安全摘要 ---------------------------------------------------------------------------------

    /// <summary>
    /// 同一收件时间两行都带会话行的代次与版本号：今天挑 <c>SafetyStateChanged</c>，不论谁先写入。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnTheSameTickTheDispatchSummaryTakesTheChangeOverTheSnapshot(bool snapshotWrittenFirst)
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedReadyVehicleAsync(database.Context, AgvId);
        DateTimeOffset at = Now - TimeSpan.FromMinutes(1);
        ProtocolInboxRow snapshot = Safety("SafetyStateSnapshot", AgvId, Generation, Revision, at, stopped: false, locked: false);
        ProtocolInboxRow change = Safety("SafetyStateChanged", AgvId, Generation, Revision, at, stopped: true, locked: true);
        await database.AddInOrderAsync(snapshotWrittenFirst ? [snapshot, change] : [change, snapshot]);

        OnboardDispatchFacts? facts = await Reader(database.Context).ReadOnboardFactsAsync(AgvId, Token);

        Assert.Equal((true, true), (facts?.VehicleStopped, facts?.AllTargetSlotsLocked));
    }

    /// <summary>同一收件时间、同一类的两行都匹配：今天挑先写入的那一行。</summary>
    [Theory]
    [InlineData("SafetyStateChanged")]
    [InlineData("SafetyStateSnapshot")]
    public async Task OnTheSameTickTheDispatchSummaryTakesTheRowOfTheSameTypeWrittenFirst(string messageType)
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedReadyVehicleAsync(database.Context, AgvId);
        DateTimeOffset at = Now - TimeSpan.FromMinutes(1);
        await database.AddInOrderAsync(
            Safety(messageType, AgvId, Generation, Revision, at, stopped: true, locked: true),
            Safety(messageType, AgvId, Generation, Revision, at, stopped: false, locked: false));

        OnboardDispatchFacts? facts = await Reader(database.Context).ReadOnboardFactsAsync(AgvId, Token);

        Assert.Equal((true, true), (facts?.VehicleStopped, facts?.AllTargetSlotsLocked));
    }

    /// <summary>两行都匹配、收件时间不同：今天挑收得晚的那一行，即便它先写入（rowid 小）。</summary>
    [Fact]
    public async Task TheDispatchSummaryTakesTheLaterReceivedOfTwoMatchingRowsWhicheverWasWrittenFirst()
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedReadyVehicleAsync(database.Context, AgvId);
        await database.AddInOrderAsync(
            Safety("SafetyStateChanged", AgvId, Generation, Revision, Now - TimeSpan.FromMinutes(1), stopped: true, locked: true),
            Safety("SafetyStateChanged", AgvId, Generation, Revision, Now - TimeSpan.FromMinutes(2), stopped: false, locked: false));

        OnboardDispatchFacts? facts = await Reader(database.Context).ReadOnboardFactsAsync(AgvId, Token);

        Assert.Equal((true, true), (facts?.VehicleStopped, facts?.AllTargetSlotsLocked));
    }

    /// <summary>
    /// 跨代与不同车交错：比这一行收得更晚的，有上一代同版本号的、有另一台车同代同版本号的、有本车本代别的版本号的，都不算；
    /// 挑中的是本车、会话行那一代、会话行那一版。
    /// </summary>
    [Fact]
    public async Task TheDispatchSummaryIgnoresNewerRowsOfAnotherGenerationAnotherVehicleOrAnotherVersion()
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedReadyVehicleAsync(database.Context, AgvId);
        await database.AddInOrderAsync(
            Safety("SafetyStateChanged", AgvId, Generation, Revision, Now - TimeSpan.FromMinutes(10), stopped: true, locked: true),
            Safety("SafetyStateChanged", AgvId, Generation - 1, Revision, Now - TimeSpan.FromMinutes(9), stopped: false, locked: false),
            Safety("SafetyStateChanged", OtherAgvId, Generation, Revision, Now - TimeSpan.FromMinutes(8), stopped: false, locked: false),
            Safety("SafetyStateChanged", AgvId, Generation, Revision - 1, Now - TimeSpan.FromMinutes(7), stopped: false, locked: false),
            Safety("SafetyStateSnapshot", AgvId, Generation + 1, Revision, Now - TimeSpan.FromMinutes(6), stopped: false, locked: false),
            Safety("SafetyStateChanged", OtherAgvId, Generation, Revision + 1, Now - TimeSpan.FromMinutes(5), stopped: false, locked: false));

        OnboardDispatchFacts? facts = await Reader(database.Context).ReadOnboardFactsAsync(AgvId, Token);

        Assert.Equal((Generation, true, true), (facts?.SessionGeneration, facts?.VehicleStopped, facts?.AllTargetSlotsLocked));
    }

    /// <summary>
    /// 窗口外仍是最新：车停了三天没变过安全状态，它唯一那一条当前摘要收于三天前，此后另一台车发了几百条；照样读得到，
    /// 不因为任何窗口把它漏掉。
    /// </summary>
    [Fact]
    public async Task TheDispatchSummaryIsFoundHoweverOldItIs()
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedReadyVehicleAsync(database.Context, AgvId);
        List<ProtocolInboxRow> rows =
        [
            Safety("SafetyStateChanged", AgvId, Generation, Revision, Now - TimeSpan.FromDays(3), stopped: true, locked: true),
        ];
        for (int change = 0; change < 300; change++)
        {
            rows.Add(Safety("SafetyStateChanged", OtherAgvId, Generation, 10 + change,
                Now - TimeSpan.FromDays(3) + TimeSpan.FromMinutes(10 * (change + 1)), stopped: false, locked: false));
        }
        await database.AddInOrderAsync([.. rows]);

        OnboardDispatchFacts? facts = await Reader(database.Context).ReadOnboardFactsAsync(AgvId, Token);

        Assert.Equal((true, true), (facts?.VehicleStopped, facts?.AllTargetSlotsLocked));
    }

    /// <summary>没有哪一行带会话行的版本号：会话行与收件箱对不上，失败关闭，与今天一样。</summary>
    [Fact]
    public async Task TheDispatchFactsFailClosedWhenNoRowCarriesTheSessionRevision()
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedReadyVehicleAsync(database.Context, AgvId);
        await database.AddInOrderAsync(
            Safety("SafetyStateChanged", AgvId, Generation, Revision - 1, Now - TimeSpan.FromMinutes(1), stopped: true, locked: true));

        Assert.Null(await Reader(database.Context).ReadOnboardFactsAsync(AgvId, Token));
    }

    // ---- 在途门锁 ---------------------------------------------------------------------------------------------

    /// <summary>同一收件时间两行都带会话行的代次与版本号：今天挑 <c>SafetyStateChanged</c>，不论谁先写入。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnTheSameTickTheDoorFactTakesTheChangeOverTheSnapshot(bool snapshotWrittenFirst)
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedSessionAsync(database.Context, AgvId, Revision);
        DateTimeOffset at = Now - TimeSpan.FromMinutes(1);
        ProtocolInboxRow snapshot = Safety("SafetyStateSnapshot", AgvId, Generation, Revision, at, stopped: true, locked: false);
        ProtocolInboxRow change = Safety("SafetyStateChanged", AgvId, Generation, Revision, at, stopped: true, locked: true);
        await database.AddInOrderAsync(snapshotWrittenFirst ? [snapshot, change] : [change, snapshot]);

        InTransitDoorVerdict verdict = await InTransitDoorFacts.ReadAsync(database.Context, AgvId, Now, Token);

        Assert.Equal(new InTransitDoorVerdict(InTransitDoorState.ProvenLocked, Generation, Revision), verdict);
    }

    /// <summary>同一收件时间、同一类的两行都匹配：今天挑先写入的那一行。</summary>
    [Theory]
    [InlineData("SafetyStateChanged")]
    [InlineData("SafetyStateSnapshot")]
    public async Task OnTheSameTickTheDoorFactTakesTheRowOfTheSameTypeWrittenFirst(string messageType)
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedSessionAsync(database.Context, AgvId, Revision);
        DateTimeOffset at = Now - TimeSpan.FromMinutes(1);
        await database.AddInOrderAsync(
            Safety(messageType, AgvId, Generation, Revision, at, stopped: true, locked: true),
            Safety(messageType, AgvId, Generation, Revision, at, stopped: true, locked: false));

        InTransitDoorVerdict verdict = await InTransitDoorFacts.ReadAsync(database.Context, AgvId, Now, Token);

        Assert.Equal(InTransitDoorState.ProvenLocked, verdict.State);
    }

    /// <summary>跨代与不同车交错、唯一那一条当前摘要收于三天前：挑中的仍是本车、会话行那一代、那一版。</summary>
    [Fact]
    public async Task TheDoorFactIgnoresNewerRowsOfAnotherGenerationOrVehicleAndIsFoundHoweverOldItIs()
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedSessionAsync(database.Context, AgvId, Revision);
        List<ProtocolInboxRow> rows =
        [
            Safety("SafetyStateChanged", AgvId, Generation, Revision, Now - TimeSpan.FromDays(3), stopped: true, locked: true),
            Safety("SafetyStateChanged", AgvId, Generation - 1, Revision, Now - TimeSpan.FromDays(2), stopped: true, locked: false),
            Safety("SafetyStateChanged", OtherAgvId, Generation, Revision, Now - TimeSpan.FromDays(1), stopped: true, locked: false),
        ];
        for (int change = 0; change < 300; change++)
        {
            rows.Add(Safety("SafetyStateChanged", OtherAgvId, Generation, 10 + change,
                Now - TimeSpan.FromHours(20) + TimeSpan.FromMinutes(change), stopped: true, locked: false));
        }
        await database.AddInOrderAsync([.. rows]);

        InTransitDoorVerdict verdict = await InTransitDoorFacts.ReadAsync(database.Context, AgvId, Now, Token);

        Assert.Equal(new InTransitDoorVerdict(InTransitDoorState.ProvenLocked, Generation, Revision), verdict);
    }

    /// <summary>
    /// 重连后新快照还没到（会话行版本号为空）：取上一代按代次、再按版本号最新的那一条，不看收件时间——服务端时钟回拨过，版本号大的
    /// 那条收件时间反而早。本票不改这一段的挑法。
    /// </summary>
    [Fact]
    public async Task BetweenAReconnectAndItsSnapshotTheDoorFactIsThePreviousGenerationsHighestVersionNotItsLatestReceived()
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedSessionAsync(database.Context, AgvId, revision: null);
        await database.AddInOrderAsync(
            Safety("SafetyStateChanged", AgvId, Generation - 1, 9, Now - TimeSpan.FromMinutes(3), stopped: true, locked: true),
            Safety("SafetyStateChanged", AgvId, Generation - 1, 8, Now - TimeSpan.FromMinutes(1), stopped: true, locked: false),
            Safety("SafetyStateChanged", AgvId, Generation - 2, 20, Now - TimeSpan.FromSeconds(30), stopped: true, locked: false),
            Safety("SafetyStateChanged", OtherAgvId, Generation - 1, 30, Now - TimeSpan.FromSeconds(20), stopped: true, locked: false));
        database.Context.ProtocolInbox.Add(HeardFrom(AgvId, Generation - 1, Now - TimeSpan.FromSeconds(1)));
        await database.Context.SaveChangesAsync(Token);

        InTransitDoorVerdict verdict = await InTransitDoorFacts.ReadAsync(database.Context, AgvId, Now, Token);

        Assert.Equal(new InTransitDoorVerdict(InTransitDoorState.ProvenLocked, Generation - 1, 9), verdict);
    }

    // ---- 造数 -------------------------------------------------------------------------------------------------

    private static OnboardDispatchFactsReader Reader(ControlServerDbContext context) =>
        new(context, Options.Create(new JourneyRuntimeOptions()), new FixedClock(Now));

    /// <summary>
    /// 一台就绪的车：会话行（代次 <see cref="Generation"/>、安全版本 <see cref="Revision"/>），握手时的能力快照与基线安全快照（版本 1），
    /// 一小时前收到；以及一秒前的一条心跳，让它在证据窗口里算活。当前摘要由各用例自己加。
    /// </summary>
    private static async Task SeedReadyVehicleAsync(ControlServerDbContext context, string agvId)
    {
        await SeedSessionAsync(context, agvId, Revision);
        DateTimeOffset handshake = Now - TimeSpan.FromHours(1);
        context.ProtocolInbox.Add(Envelope("CapabilitySnapshot", agvId, Generation, handshake, new
        {
            capabilityVersion = 1,
            activeSlotConfigurationFingerprint = new string('0', 64),
            observedAt = handshake,
            slotStates = Enumerable.Range(1, 8).Select(slot => new
            {
                slotNo = slot,
                operability = "OPERABLE",
                administrativeAvailability = "ENABLED",
                physicalState = "EMPTY",
                lockState = "LOCKED",
                unlockOutputState = "RESET",
            }).ToArray(),
        }));
        await context.SaveChangesAsync(Token);
        context.ProtocolInbox.Add(Safety("SafetyStateSnapshot", agvId, Generation, 1, handshake, stopped: false, locked: false));
        await context.SaveChangesAsync(Token);
        context.ProtocolInbox.Add(HeardFrom(agvId, Generation, Now - TimeSpan.FromSeconds(1)));
        await context.SaveChangesAsync(Token);
    }

    /// <summary>会话行，与一秒前的一条心跳（在途门锁要这一代在存活窗口里听得到）。</summary>
    private static async Task SeedSessionAsync(ControlServerDbContext context, string agvId, long? revision)
    {
        SessionRecoveryRow session = ProtocolInboxReadWindowTests.Session(agvId, Generation);
        session.SafetyRevision = revision;
        context.SessionRecoveries.Add(session);
        context.ProtocolInbox.Add(HeardFrom(agvId, Generation, Now - TimeSpan.FromSeconds(1)));
        await context.SaveChangesAsync(Token);
    }

    private static ProtocolInboxRow HeardFrom(string agvId, long generation, DateTimeOffset at) =>
        ProtocolInboxReadWindowTests.HeardFrom(agvId, generation, at);

    /// <summary>一条安全状态消息。快照带八个仓位都锁着的 <c>slotStates</c>（基线要），变化带 <c>affectedSlots</c>。</summary>
    internal static ProtocolInboxRow Safety(
        string messageType, string agvId, long generation, long version, DateTimeOffset receivedAt, bool stopped, bool locked)
    {
        object safety = new
        {
            departureSafe = stopped && locked,
            vehicleStopped = stopped,
            allTargetSlotsLocked = locked,
            allUnlockOutputsReset = true,
            unknownPresent = false,
            reasonCodes = locked ? Array.Empty<string>() : ["LOCK_NOT_CLOSED"],
        };
        object payload = messageType == "SafetyStateSnapshot"
            ? new
            {
                safetyStateVersion = version,
                observedAt = receivedAt,
                safety,
                slotStates = Enumerable.Range(1, 8).Select(slot => new
                {
                    slotNo = slot,
                    physicalState = "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                }).ToArray(),
            }
            : new { safetyStateVersion = version, observedAt = receivedAt, safety, affectedSlots = new[] { 1 } };
        return Envelope(messageType, agvId, generation, receivedAt, payload);
    }

    private static ProtocolInboxRow Envelope(
        string messageType, string agvId, long generation, DateTimeOffset receivedAt, object payload) => new()
    {
        MessageId = Guid.NewGuid().ToString("D"),
        MessageType = messageType,
        RequestJson = JsonSerializer.Serialize(new
        {
            messageType,
            messageId = Guid.NewGuid().ToString("D"),
            agvId,
            sessionGeneration = generation,
            sentAt = receivedAt,
            payload,
        }),
        ContentHash = new string('0', 64),
        FirstResponseJson = "{}",
        ReceivedAt = receivedAt
    };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    /// <summary>内存 SQLite，建库与生产同一个模型（含 #563 的两个索引）。</summary>
    internal sealed class InboxDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private InboxDatabase(SqliteConnection connection, ControlServerDbContext context)
        {
            this.connection = connection;
            Context = context;
        }

        public ControlServerDbContext Context { get; }

        public static async Task<InboxDatabase> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(Token);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync(Token);
            return new InboxDatabase(connection, context);
        }

        /// <summary>一行一次保存，写入次序（rowid）就是参数次序。</summary>
        public async Task AddInOrderAsync(params ProtocolInboxRow[] rows)
        {
            foreach (ProtocolInboxRow row in rows)
            {
                Context.ProtocolInbox.Add(row);
                await Context.SaveChangesAsync(Token);
            }
            Context.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
