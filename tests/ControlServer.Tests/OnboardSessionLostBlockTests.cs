using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 车载端静默失联期间，停在「等车载端事实」那些阶段的在途旅程要在看板上看得见
/// （<c>ONBOARD_SESSION_LOST</c>，control-server#234）。
/// </summary>
/// <remarks>
/// <para>
/// <b>此前它是看不见的。</b>车在到站前后静默时，<c>CheckArrivalAsync</c> 每轮都判「到站不可信」然后返回；
/// <c>ObserveOrderFailureAsync</c> 只在 RIoT 报单 FAILED 时写码，<c>NameCheckpointWaitAsync</c> 在车不在
/// 检查点时把码清成空。阻断码为空，看板阻断端点就不列这一行（它只列 <c>BlockReasonCode</c> 非空的）。
/// 车静默卡住，看板上没有卡片。
/// </para>
/// <para>
/// <b>会话行救不了这一格。</b>断线与静默都不改 <c>SessionRecoveries.Readiness</c>——那一行是给恢复握手用的，
/// 断线不动它是对的——所以 <see cref="JourneyRuntimeEngine"/> 里那条写 <c>ONBOARD_SESSION_NOT_READY</c> 的
/// 分支在失联期间根本走不到。这是本票要另立一个码的原因，不是重复既有能力。
/// </para>
/// <para>
/// <b>静默取十秒，不取一小时。</b>存活窗是六秒，而派车证据新鲜度 <c>MaximumEvidenceAge</c> 是三十秒；静默
/// 落在两者之间，这些判据才说得清红是六秒那条线挣来的。拿一小时去静默，实现里把窗口错写成三十秒也照样绿。
/// </para>
/// </remarks>
public sealed class OnboardSessionLostBlockTests
{
    /// <summary>落在六秒存活窗之外、三十秒证据新鲜度之内。</summary>
    private static readonly TimeSpan SilenceBetweenTheTwoWindows = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 车在去关卡的路上静默：旅程挂上失联码，开始时间记下来，看板据此列得出这一行。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task ASilentSessionBlocksTheJourneyWaitingForAnArrival()
    {
        await using RuntimeFixture fixture = await ArrivalWaitAsync();

        await GoSilentAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow blocked = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeEngine.OnboardSessionLostReason, blocked.BlockReasonCode);
        Assert.Equal(fixture.Clock.GetUtcNow(), blocked.BlockReasonSince);
    }

    /// <summary>
    /// 判别力对照：只静默到存活窗之内，什么都不该发生。
    /// </summary>
    /// <remarks>
    /// 没有这一条，上面那条在「一进这个阶段就挂失联码」这种改坏法下照样绿。两条只差静默了多久。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task SilenceInsideTheLivenessWindowBlocksNothing()
    {
        await using RuntimeFixture fixture = await ArrivalWaitAsync();

        await fixture.HearFromPeerAsync();
        fixture.Clock.Advance(SessionLiveness.Timeout - TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow quiet = await fixture.RuntimeAsync();
        Assert.Null(quiet.BlockReasonCode);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, quiet.Stage);
    }

    /// <summary>
    /// 车再开口就把码清掉，回到这一阶段本来的判断。
    /// </summary>
    /// <remarks>
    /// 清除必须是本票自己做的：<c>NameCheckpointWaitAsync</c> 只清它自己那两个检查点码，
    /// <c>SetStage</c> 只在换段时清，而失联多半在同一个阶段里来了又走。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task TheBlockIsClearedOnceThePeerIsHeardFromAgain()
    {
        await using RuntimeFixture fixture = await ArrivalWaitAsync();
        await GoSilentAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            JourneyRuntimeEngine.OnboardSessionLostReason, (await fixture.RuntimeAsync()).BlockReasonCode);

        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow cleared = await fixture.RuntimeAsync();
        Assert.Null(cleared.BlockReasonCode);
        Assert.Null(cleared.BlockReasonSince);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, cleared.Stage);
    }

    /// <summary>
    /// 车既失联、单又被 RIoT 报 FAILED 时，现场看到的仍然是「单失败」，不是「车不说话了」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两个码都在最高档，所以升级不受影响——**变的是告诉人什么**。<c>ORDER_FAILED</c> 是 REQ-0232 的症状，
    /// 已经登记成车辆故障事实，而在途单被报 FAILED 触发的急停没有自动解除路径
    /// （<c>docs/emergency-stop-field-fallback.md</c>），正是走到车前的人最需要知道的那一条。
    /// 拿「车停止说话」盖掉它，等于把原因换成了它的一个症状，还顺手把 <c>BlockReasonSince</c> 重置了。
    /// </para>
    /// <para>
    /// 而这两件事通常同时发生：单失败与会话失联多半是同一个事件的两面。所以这不是一个罕见的边角。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task ASilentSessionDoesNotOverwriteAFailedOrderAsTheReason()
    {
        await using RuntimeFixture fixture = await ArrivalWaitAsync();

        // RIoT 报这一段的移动单 FAILED：引擎登记故障事实并写下 ORDER_FAILED。
        JourneyRuntimeRow beforeFailure = await fixture.RuntimeAsync();
        fixture.Riot.FailOrder(beforeFailure.GateUpperId);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow failed = await fixture.RuntimeAsync();
        Assert.Equal(VehicleFaultEvidence.OrderFailed, failed.BlockReasonCode);
        DateTimeOffset? failedSince = failed.BlockReasonSince;

        // 车随后也不说话了，再跑几轮。
        await GoSilentAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow held = await fixture.RuntimeAsync();
        Assert.Equal(VehicleFaultEvidence.OrderFailed, held.BlockReasonCode);
        Assert.Equal(failedSince, held.BlockReasonSince);
    }

    /// <summary>
    /// 门没关的站点超时告警与纠错进行中，失联时也不被「车停止说话」盖掉（control-server#331 第三轮审查建议 2）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这两个码加进了失联写码与推进失败写码共用的那个判定（<c>JourneyRuntimeEngine.CarriesACodeThatNamesAWaitOnAPerson</c>）。
    /// 它们都指名了在等谁——一个在等人去关门，一个在等纠错走完——被覆盖后下一轮按「码不同」重写，开始时间归零，
    /// program#55 的升级档位清零。
    /// </para>
    /// <para>
    /// <b>这里的阶段与码的组合是造出来的。</b>失联写码只在两个到站阶段起作用，而这两个码只在停站阶段写入（等装货结果、等离站），
    /// 阶段一变 <c>SetStage</c> 就把码清掉——产品代码里走不到「到站阶段带着这两个码」。这条钉的是「这份清单是两处共用的」：
    /// 哪天有人从判定里拿掉其中一个，推进失败那一侧的用例会红，这一侧也会红。
    /// </para>
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [InlineData("STATION_TIMEOUT_DOOR_NOT_CLOSED")]
    [InlineData("LOAD_CORRECTION_IN_PROGRESS")]
    public async Task ASilentSessionDoesNotOverwriteACodeThatNamesAWaitOnAPerson(string code)
    {
        await using RuntimeFixture fixture = await ArrivalWaitAsync();
        JourneyRuntimeRow waiting = await fixture.Context.JourneyRuntimes.SingleAsync(TestContext.Current.CancellationToken);
        waiting.SetBlockReason(code, fixture.Clock.GetUtcNow());
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        DateTimeOffset? since = waiting.BlockReasonSince;

        await GoSilentAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow held = await fixture.RuntimeAsync();
        Assert.Equal(code, held.BlockReasonCode);
        Assert.Equal(since, held.BlockReasonSince);
    }

    /// <summary>
    /// 失联期间只亮卡、只升级告警：不换阶段、不结束需求、不释放租约、不碰订单命令面（REQ-0287）。
    /// </summary>
    /// <remarks>
    /// 命令面那一格读的是审计表而不是一个调用计数：<c>RiotOrderCommandAuditStore</c> 在每次发出命令时落一行，
    /// 空表是落库的事实，比「测试替身没被调用」硬。<c>OrderHold</c> 本批不做（用户 2026-09-20 定，
    /// ADR-cross-0026 与 REQ-0287 的冲突留到批次 9），这一条是钉住它确实没被顺手做进来。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task TheBlockNeitherMovesTheJourneyNorTouchesTheOrderCommandSurface()
    {
        await using RuntimeFixture fixture = await ArrivalWaitAsync();

        await GoSilentAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        // 挂着不动地多跑几轮：否定判据要有界。
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow held = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, held.Stage);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        Assert.Null((await fixture.LeaseAsync()).ReleasedAt);
        Assert.Empty(await fixture.Context.RiotOrderCommandAudit.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 阻断码持续挂着时开始时间不动：升级阶梯从失联那一刻算，不是从最近一轮算。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task TheStartOfTheBlockDoesNotMoveWhileTheSessionStaysSilent()
    {
        await using RuntimeFixture fixture = await ArrivalWaitAsync();
        await GoSilentAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        DateTimeOffset? since = (await fixture.RuntimeAsync()).BlockReasonSince;

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow stillBlocked = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeEngine.OnboardSessionLostReason, stillBlocked.BlockReasonCode);
        Assert.Equal(since, stillBlocked.BlockReasonSince);
    }

    /// <summary>
    /// 重连之后计时从新代次重新开始：上一代静默了多久都不算这一代的账。
    /// </summary>
    /// <remarks>
    /// 这是「重连后第一轮不被旧的失联判定再次关闭」在旅程这一侧的样子。新代次的第一条入站落在窗口里，
    /// 所以这一轮不该再判失联——如果实现读的是「这台车最近一条入站」而不是「这一代最近一条入站」，
    /// 旧代次那些陈旧的心跳会把它救活，这一条就变成绿得没道理。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task AReconnectStartsTheLivenessWindowFromTheNewGeneration()
    {
        await using RuntimeFixture fixture = await ArrivalWaitAsync();
        await GoSilentAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            JourneyRuntimeEngine.OnboardSessionLostReason, (await fixture.RuntimeAsync()).BlockReasonCode);

        // 车重连、握手走完、新一代会话回到 Ready，新代次的第一条入站就在此刻。
        await fixture.AdvanceSessionAsync(generation: 2);
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow reconnected = await fixture.RuntimeAsync();
        Assert.Null(reconnected.BlockReasonCode);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, reconnected.Stage);
    }

    // --- 辅助 ----------------------------------------------------------------------------------------

    /// <summary>
    /// 把旅程带到「等关卡到站」：这是最典型的等车载端事实的阶段，到站可不可信要读车载端的安全事实。
    /// </summary>
    private static async Task<RuntimeFixture> ArrivalWaitAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.AdvanceToGateArrivalAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync()).Stage);
        return fixture;
    }

    /// <summary>
    /// 车不说话了，但连接没断：最后一条入站停在此刻，然后时钟走过存活窗、没走过证据新鲜度。
    /// </summary>
    private static async Task GoSilentAsync(RuntimeFixture fixture)
    {
        await fixture.HearFromPeerAsync();
        fixture.Clock.Advance(SilenceBetweenTheTwoWindows);
        Assert.True(SilenceBetweenTheTwoWindows > SessionLiveness.Timeout);
        Assert.True(SilenceBetweenTheTwoWindows < fixture.Options.MaximumEvidenceAge);
    }
}
