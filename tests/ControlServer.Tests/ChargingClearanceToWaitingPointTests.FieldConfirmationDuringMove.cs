using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.ChargingAllocationTests;
using static ControlServer.Tests.ChargingUnableToChargeTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

// control-server#452 (review note 3): a clearance move gives the charging journey a second stop, the waiting point. The
// unable-to-charge field confirmation read "the" stop of the journey and threw on two, which ended the vehicle's connection on
// every resend.
public sealed partial class ChargingClearanceToWaitingPointTests
{
    /// <summary>
    /// 清桩开往等待点途中（周期仍在清桩中，旅程多了等待点那一个停靠），车载端来一个新确认号的现场确认充不上：照常判定、答一次——系统早已确认过这一次充不上，
    /// 答 <c>CONFIRMED</c>，不写第二条暂停事件。判定取的是充电桩那一个停靠，不因为旅程有两个停靠而抛错、断开连接。
    /// </summary>
    [Fact]
    public async Task ANewUnableToChargeNumberWhileTheClearanceMovesIsAnswered()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Single(await ClearanceStopsAsync(fleet, journey));
        Assert.Equal(ChargingCyclePhases.Clearing, (await OpenCycleAsync(fleet)).Phase);

        OnboardMessageProcessor processor = FieldConfirmationWriteLockTests.Processor(
            fleet, fleet.Context, fieldConfirmations: FieldConfirmationWriteLockTests.FieldConfirmations(fleet, fleet.Context, fleet.Riot));
        OnboardConnectionState state = await FieldConfirmationWriteLockTests.SessionAsync(fleet, FleetFixture.AgvIds[0]);

        JsonElement result = FieldConfirmationWriteLockTests.Payload(
            await processor.ProcessAsync(
                FieldConfirmationWriteLockTests.UnableToChargeLine(state, "00000000-0000-4000-8000-000000045260"), state, Token),
            "UnableToChargeFieldConfirmationResult");

        Assert.Equal("CONFIRMED", result.GetProperty("outcome").GetString());
        Assert.Single(await HoldsAsync(fleet));
    }
}
