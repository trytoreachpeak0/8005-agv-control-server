using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 待送点规则关着时（出厂，control-server#709），STAGING_TO_WIRE 在派工待送站的取货与改动前逐字节相同：发给车的每一条报文、每一张 RIoT 订单。
/// </summary>
/// <remarks>
/// <para>
/// C11 派工时的要求：02、03、09、04 四张都挂在 <c>JourneyRuntime:StagingDepartureRules:Enabled</c> 下，开关关着时行为与现在逐字节一致，
/// 要有用例证明。机台那一半由既有的 <see cref="WirePin"/> 基线在开关打开时照样对得上来证（<c>Batch7StopDrivenAdvanceWireParityTests</c> 末尾两条）；
/// 待送点这一半在改动之前没有基线，所以这里录了两份。
/// </para>
/// <para>
/// <b>两份基线取自集成分支，不在本票分支上录</b>（<see cref="WirePin"/> 类注释的做法）：<c>fp/v2-impl@1f9e1b0a</c> 的一个临时 worktree 里
/// 只放进这个文件，产品代码一行没动，跑出 <c>.actual</c> 改名而来。所以这个文件<b>不碰任何 control-server#709 新加的东西</b>——它在集成分支上
/// 也要编得过。开关在这里就是出厂的关。
/// </para>
/// <para>
/// 两条路径正是规则开着时会变的那两处：离站期限到期放回需求（control-server#703），与持货期限到期关闭装货阶段、发离站核验（ADR-cross-0057）。
/// </para>
/// </remarks>
public sealed class StagingSwitchOffWireParityTests
{
    private const int StagingStationRiotId = 305;

    private const string StagingStationName = "派工待送取货";

    private static readonly TaskTypeStationBinding StagingBinding =
        new(TransportTaskTypes.StagingToWire, StagingStationRiotId, StagingStationName, "SITE-CHECK-STAGING");

    /// <summary>没人扫码，离站期限到：本站结束、需求放回待派。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnUnscannedStagingPickupSendsTheSameLinesWithTheSwitchOff()
    {
        await using RuntimeFixture fixture = await StagingFixtureAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);

        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        Assert.Equal(DemandJourneyLookup.ReleasedAtStationDeadlineReason, runtime.BlockReasonCode);
        await WirePin.AssertMatchesAsync(fixture.Context, "staging-switch-off-station-deadline", fixture.Peer.Lines);
    }

    /// <summary>装了一篮、持货等单，持货期限到：装货阶段以持货超时关闭，发离站核验。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AHoldingStagingPickupSendsTheSameLinesWithTheSwitchOff()
    {
        await using RuntimeFixture fixture = await StagingFixtureAsync();
        await new DispatchZoneParameterStore(fixture.Context, CreateGovernedPublisher(fixture.Context))
            .WriteVersionAsync(
                [new DispatchZoneParameters(fixture.Options.DispatchZone, 10_000_000, null)],
                fixture.Clock.GetUtcNow(),
                TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);

        fixture.Clock.Advance(fixture.Options.CargoHoldingTimeout);
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(LoadingClosedReasons.CargoHoldingTimeout, runtime.LoadingClosedReason);
        await WirePin.AssertMatchesAsync(fixture.Context, "staging-switch-off-cargo-holding", fixture.Peer.Lines);
    }

    /// <summary>与 <c>ReversedDirectionJourneyRuntimeTests</c> 同一张图、同一份绑定；需求是一条 STAGING_TO_WIRE。</summary>
    private static async Task<RuntimeFixture> StagingFixtureAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.AllowedWorkTypes = [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire];
        fixture.Riot.SetMapStations(
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(TaskTypeStationRuntimeSeed.GateStationRiotId, TaskTypeStationRuntimeSeed.GateStationName),
            new RiotMapStation(300, "等待点"),
            new RiotMapStation(StagingStationRiotId, StagingStationName));
        await TaskTypeStationRuntimeSeed.ActivateAsync(
            fixture.DbOptionsForTests,
            Now,
            requiredTaskTypes: [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire],
            bindings: [TaskTypeStationRuntimeSeed.GateBinding, StagingBinding]);
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)) with
        {
            WorkType = TransportTaskTypes.StagingToWire,
            TransportDemandKey = $"{FirstSublot}|{TransportTaskTypes.StagingToWire}",
        });
        fixture.BoxCounts.Set(FirstSublot, 4);
        return fixture;
    }
}
