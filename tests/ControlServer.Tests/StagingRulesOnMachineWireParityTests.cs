using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 待送点规则打开（control-server#709）之后，机台取货发给车的每一条报文与改动前逐字相同：用的就是集成分支上录的那几份既有 <see cref="WirePin"/>
/// 基线，一份没有重录。
/// </summary>
/// <remarks>
/// 与 <c>Batch7StopDrivenAdvanceWireParityTests</c> 里的同名路径逐步相同，只多一行把开关打开。规则只认 STAGING_TO_WIRE 在派工待送站的取货，
/// 这几条都是 WIRE_TO_GATE 在机台取货——判别答「否」，这里每一条报文都不该变。判别改成恒真，这几条红。
/// </remarks>
public sealed class StagingRulesOnMachineWireParityTests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ANormalMachineJourneySendsTheSameLinesWithTheStagingRulesOn()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StagingDepartureRules.Enabled = true;
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);

        JourneyRuntimeRow runtime = await RunJourneyToCompletionAsync(
            fixture, FirstDemandId, FirstSublot, FirstSubmissionId, FirstSafetyResultId);

        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        await WirePin.AssertMatchesAsync(fixture.Context, "normal-journey", fixture.Peer.Lines);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AMachineStopEndedByItsStationDeadlineSendsTheSameLinesWithTheStagingRulesOn()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StagingDepartureRules.Enabled = true;
        fixture.Options.StationTimeoutRedispatchLimit = 0;
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);

        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", runtime.BlockReasonCode);
        await WirePin.AssertMatchesAsync(fixture.Context, "station-deadline", fixture.Peer.Lines);
    }
}
