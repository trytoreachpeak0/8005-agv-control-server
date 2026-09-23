using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Fleet;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.VehicleFaultRecoveryTests;

namespace ControlServer.Tests;

/// <summary>
/// 故障人工清除的 HTTP 入口（control-server#299）：照 REQ-0356 的做法——默认不挂、共享 Bearer 凭据、按 <c>agvId</c> 点名、拒绝时列出全部理由。
/// </summary>
/// <remarks>
/// 判据本身是 <see cref="VehicleFaultRecoveryTests"/> 的事。这里证的是边界：凭据没配就不可用、凭据不对什么都不读；缺车号或动作不认识
/// 在读 RIoT 之前就拒；不是本服务端的车 404；三种结果各落到对的状态码上，拒绝理由原样列在 <c>reasons</c> 里。
/// </remarks>
public sealed class VehicleFaultRecoveryEndpointsTests
{
    private const string Credential = "fault-recovery-credential";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUnpopulatedCredentialVariableMakesTheEntryPointUnavailable()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        string variable = "CONTROL_SERVER_TEST_MISSING_" + Guid.NewGuid().ToString("N");

        var result = await PostAsync(fixture, variable, "Bearer anything", Request(fixture));

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await FaultAsync(fixture)).Level);
    }

    [Fact]
    public async Task AWrongBearerCredentialIsRejectedBeforeAnythingIsDone()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        using CredentialScope scope = new();

        var result = await PostAsync(fixture, scope.Variable, "Bearer wrong-credential", Request(fixture));

        Assert.IsType<UnauthorizedHttpResult>(result.Result);
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await FaultAsync(fixture)).Level);
    }

    /// <summary>
    /// 没有车号、或动作不是两个之一：没有东西可判，422，在读任何事实之前就拒。<c>CONFIRM_REBUILD</c> 也在其中：本服务端自己的在途单在
    /// RIoT 里被取消后由 #318 自动重建、不经人确认（用户 2026-09-22 定），这个入口不再为它留位置。
    /// </summary>
    [Theory]
    [InlineData("", "CLEAR_FAULT")]
    [InlineData("agv", null)]
    [InlineData("agv", "clear_fault")]
    [InlineData("agv", "CANCEL_ORDER")]
    [InlineData("agv", "CONFIRM_REBUILD")]
    public async Task ARequestWithoutAVehicleOrAKnownActionIsIncomplete(string agvId, string? action)
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        using CredentialScope scope = new();

        var result = await PostAsync(
            fixture,
            scope.Variable,
            $"Bearer {Credential}",
            Request(fixture) with { AgvId = agvId == "agv" ? fixture.Options.AgvId : agvId, Action = action });

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.StatusCode);
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await FaultAsync(fixture)).Level);
    }

    /// <summary>车是点名的，不是本服务端开的车就不去找一辆同名的。</summary>
    [Fact]
    public async Task AVehicleThisServerDoesNotDriveIsNotFound()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        using CredentialScope scope = new();

        var result = await PostAsync(fixture, scope.Variable, $"Bearer {Credential}", Request(fixture) with { AgvId = "agv09" });

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
    }

    /// <summary>拒绝是 409，理由一条不少地列在 <c>reasons</c> 里——包括没署名，它不是格式错误，是一项没满足的判据。</summary>
    [Fact]
    public async Task ARefusalIsAConflictNamingEveryReason()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        fixture.EmergencyLatched = true;
        using CredentialScope scope = new();

        var result = await PostAsync(
            fixture, scope.Variable, $"Bearer {Credential}", Request(fixture) with { OperatorId = null },
            new SiteRiot(fixture) { HasUnfinishedOrder = true });

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Equal(
            ["FAULT_RECOVERY_OPERATOR_UNIDENTIFIED", "FAULT_RECOVERY_EMERGENCY_LATCHED", "FAULT_RECOVERY_VEHICLE_ORDER_NOT_FINISHED"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(problem.ProblemDetails.Extensions["reasons"]));
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await FaultAsync(fixture)).Level);
    }

    /// <summary>清除成功是 200，正文说明结果与旅程怎么处置了；同一请求再来一次仍是 200，结果是「已经清过」。</summary>
    [Fact]
    public async Task AClearanceIsOkAndTheSameOneAgainSaysItWasAlreadyMade()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        using CredentialScope scope = new();

        var first = await PostAsync(fixture, scope.Variable, $"Bearer {Credential}", Request(fixture));
        var second = await PostAsync(fixture, scope.Variable, $"Bearer {Credential}", Request(fixture));

        VehicleFaultRecoveryResponse cleared = Assert.IsType<Ok<VehicleFaultRecoveryResponse>>(first.Result).Value!;
        Assert.Equal(
            (fixture.Options.AgvId, "CLEAR_FAULT", "Cleared", "REBUILD_SCHEDULED", 0, (long?)1),
            (cleared.AgvId, cleared.Action, cleared.Outcome, cleared.Disposition, cleared.Reasons.Count, cleared.FaultGeneration));
        VehicleFaultRecoveryResponse again = Assert.IsType<Ok<VehicleFaultRecoveryResponse>>(second.Result).Value!;
        Assert.Equal(("AlreadyCleared", "NONE"), (again.Outcome, again.Disposition));
        Assert.Equal(VehicleFaultLevel.None, (await FaultAsync(fixture)).Level);
    }

    /// <summary>这一轮迟迟不结束：503，理由 <c>FAULT_RECOVERY_RUNTIME_BUSY</c>，稍后再试。</summary>
    [Fact]
    public async Task ARuntimeRoundThatDoesNotEndInTimeIsServiceUnavailable()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        using CredentialScope scope = new();
        using JourneyMutationGate gate = new();
        using IDisposable stuckRound = await gate.EnterAsync(Token);

        var result = await PostAsync(
            fixture, scope.Variable, $"Bearer {Credential}", Request(fixture),
            service: Service(fixture, new SiteRiot(fixture), gate: gate, gateTimeout: TimeSpan.FromMilliseconds(200)));

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.Equal(
            ["FAULT_RECOVERY_RUNTIME_BUSY"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(problem.ProblemDetails.Extensions["reasons"]));
        Assert.Equal(VehicleFaultLevel.SuspectedBlocked, (await FaultAsync(fixture)).Level);
    }

    private static VehicleFaultRecoveryHttpRequest Request(RuntimeFixture fixture) =>
        new(fixture.Options.AgvId, "L1-OPERATOR-07", "CLEAR_FAULT", FaultRemedied: true, Note: "L1");

    private static Task<Results<Ok<VehicleFaultRecoveryResponse>, UnauthorizedHttpResult, ProblemHttpResult>> PostAsync(
        RuntimeFixture fixture,
        string credentialVariable,
        string authorization,
        VehicleFaultRecoveryHttpRequest request,
        SiteRiot? site = null,
        Host.Runtime.Faults.VehicleFaultRecoveryService? service = null)
    {
        DefaultHttpContext http = new();
        http.Request.Scheme = "http";
        http.Request.Headers.Authorization = authorization;
        return VehicleFaultRecoveryEndpoints.HandleAsync(
            http,
            request,
            service ?? Service(fixture, site ?? new SiteRiot(fixture)),
            new VehicleRoster(Options.Create(fixture.Options)),
            Options.Create(new VehicleFaultRecoveryOptions
            {
                Enabled = true,
                CredentialEnvironmentVariable = credentialVariable,
            }),
            Token);
    }

    private sealed class CredentialScope : IDisposable
    {
        public CredentialScope()
        {
            Environment.SetEnvironmentVariable(Variable, Credential);
        }

        public string Variable { get; } = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");

        public void Dispose() => Environment.SetEnvironmentVariable(Variable, null);
    }
}
