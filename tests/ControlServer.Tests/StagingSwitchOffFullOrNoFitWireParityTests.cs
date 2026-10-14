using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 待送点规则关着时（出厂），control-server#710 那两种「该走」的事实摆在 STAGING_TO_WIRE 的待送站上，发给车的每一条报文、每一张 RIoT 订单与改动前逐字节相同。
/// </summary>
/// <remarks>
/// <para>
/// C11 派工要求：本票挂在 <c>JourneyRuntime:StagingDepartureRules:Enabled</c> 下，开关关着时行为逐字节不变，用 control-server#709 的
/// <see cref="WirePin"/> 做法证明。<c>StagingSwitchOffWireParityTests</c> 那两份基线的事实里没有一侧满、也没有等着的候选，碰不到本票改的判定，
/// 所以这里另录两份：一侧满而等着的候选全要这一侧（规则开着时即走），与两侧都满（规则开着时不等离站等待即走）。
/// </para>
/// <para>
/// <b>两份基线取自集成分支，不在本票分支上录</b>：<c>fp/v2-impl@b4e68e67</c>（cs#709 合入之后）的一个临时 worktree 里只放进这个文件，产品代码一行没动，
/// 跑出 <c>.actual</c> 改名而来。所以这个文件不碰任何 control-server#710 新加的东西——它在集成分支上也要编得过。开关在这里就是出厂的关。
/// 同一组事实在规则开着时确实会让车走，由 <see cref="StagingDepartureFullOrNoFitTests"/> 的同名用例证。
/// </para>
/// </remarks>
public sealed class StagingSwitchOffFullOrNoFitWireParityTests
{
    private const int StagingStationRiotId = 305;

    private const string StagingStationName = "派工待送取货";

    private static readonly TaskTypeStationBinding StagingBinding =
        new(TransportTaskTypes.StagingToWire, StagingStationRiotId, StagingStationName, "SITE-CHECK-STAGING");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>装了一篮、前仓满，等着的一条待送候选只因本车货占着前仓而装不下；持货期限到才走。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task OneFullSideWithEveryWaitingDemandNeedingItSendsTheSameLinesWithTheSwitchOff()
    {
        await using RuntimeFixture fixture = await LoadedStagingFixtureAsync();
        int[] own = await OwnSlotsAsync(fixture);
        await DisableSlotsAsync(fixture, [.. Enumerable.Range(1, 4).Except(own)]);

        for (int round = 0; round < 3; round++)
        {
            RecordWaitingOwnCargoFront(fixture);
            await TickAndRunAsync(fixture);
        }
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);

        fixture.Clock.Advance(fixture.Options.CargoHoldingTimeout);
        RecordWaitingOwnCargoFront(fixture);
        await TickAndRunAsync(fixture);

        Assert.Equal(LoadingClosedReasons.CargoHoldingTimeout, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingClosedReason);
        await WirePin.AssertMatchesAsync(fixture.Context, "staging-switch-off-one-side-full", fixture.Peer.Lines);
    }

    /// <summary>装了一篮、两侧都满（其余仓位禁用），没有等着的候选；离站等待到期才走。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task BothSidesFullSendsTheSameLinesWithTheSwitchOff()
    {
        await using RuntimeFixture fixture = await LoadedStagingFixtureAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        int[] own = await OwnSlotsAsync(fixture);
        await DisableSlotsAsync(fixture, [.. Enumerable.Range(1, 8).Except(own)]);

        await TickAndRunAsync(fixture);
        Assert.Equal(LoadingPhaseStates.VehicleFull, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await TickAndRunAsync(fixture);

        Assert.Equal(
            JourneyRuntimeStage.AwaitingDepartureSafety, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        await WirePin.AssertMatchesAsync(fixture.Context, "staging-switch-off-both-sides-full", fixture.Peer.Lines);
    }

    /// <summary>与 <c>StagingSwitchOffWireParityTests</c> 同一张图、同一份绑定；本区允许途中追加，需求到站、录入、装货落定。</summary>
    private static async Task<RuntimeFixture> LoadedStagingFixtureAsync()
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
        await new DispatchZoneParameterStore(fixture.Context, CreateGovernedPublisher(fixture.Context))
            .WriteVersionAsync(
                [new DispatchZoneParameters(fixture.Options.DispatchZone, 10_000_000, null)],
                fixture.Clock.GetUtcNow(),
                Token);
        fixture.Context.ChangeTracker.Clear();

        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);
        await fixture.ProveSlotDoorsClosedAsync();
        return fixture;
    }

    private static async Task<int[]> OwnSlotsAsync(RuntimeFixture fixture) =>
        JsonSerializer.Deserialize<int[]>((await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == FirstDemandId, Token)).TargetSlotsJson)!;

    private static async Task DisableSlotsAsync(RuntimeFixture fixture, int[] slots)
    {
        ProtocolInboxRow[] rows = await fixture.Context.ProtocolInbox
            .Where(row => row.MessageType == "CapabilitySnapshot" || row.MessageType == "SafetyStateSnapshot")
            .ToArrayAsync(Token);
        foreach (ProtocolInboxRow row in rows)
        {
            JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
            foreach (JsonNode? slot in root["payload"]!["slotStates"]!.AsArray())
            {
                if (slots.Contains(slot!["slotNo"]!.GetValue<int>()))
                {
                    slot["administrativeAvailability"] = "DISABLED";
                }
            }
            row.RequestJson = root.ToJsonString();
        }
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>上一轮派车：一条在待送站取货的 STAGING_TO_WIRE 候选，只因本车货占着前仓而装不下。</summary>
    private static void RecordWaitingOwnCargoFront(RuntimeFixture fixture) =>
        fixture.SlotGroupFullness.Record(new DispatchRoundOutcome(
            Round: null!,
            [
                new DispatchVehicleOutcome(
                    fixture.Options.AgvId,
                    fixture.Options.VehicleKey,
                    [
                        new DispatchCandidateVerdict(
                            new DispatchCandidateEvaluation(
                                new AcceptedDemandSnapshot(
                                    "W-1", $"W-1|{TransportTaskTypes.StagingToWire}", 1, "history", 1, Now,
                                    WorkType: TransportTaskTypes.StagingToWire, Sublot: "SUBLOT-W-1"),
                                round: null!,
                                vehicle: null!)
                            {
                                AreaAssignment = new AreaAssignment("N1-1", fixture.Options.DispatchZone, "FRONT"),
                                Route = new ResolvedJourneyRoute(
                                    fixture.Options.DispatchZone, "route", StagingStationName, StagingStationRiotId,
                                    TaskTypeStationRuntimeSeed.GateStationName, TaskTypeStationRuntimeSeed.GateStationRiotId,
                                    FixedStation: null!),
                            },
                            DispatchReasonCodes.SlotGroupOccupiedByOwnCargo)
                    ])
            ]));
}
