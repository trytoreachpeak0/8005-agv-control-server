using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 有货车的受治理人工解除（REQ-0356 按 CP-0011 修订，control-server#539）这一层服务：查角色、判门锁原因、列本服务端的单、
/// 每一次请求写一条管理员审计。急停本身的判定在 <see cref="EmergencyStopSupervisor.ReleaseLoadedOnGovernedConfirmationAsync"/>，
/// 由 <c>EmergencyStopSupervisorTests</c> 逐格测。
/// </summary>
/// <remarks>
/// 起点都是 <see cref="InTransitDoorEmergencyReleaseTests"/> 的「行驶中门锁没锁、按住、急停锁住」：有货、车上唯一的未完成单是本服务端按住的 7。
/// </remarks>
public sealed class LoadedEmergencyStopReleaseTests
{
    private const string Admin = "L1-ADMIN-13";
    private const string Maintainer = "L1-MAINT-11";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 名单里有 R-13 的人，确认原因消除、仓门关好、门锁已检修：解除发出，单留着；审计一条，结果 <c>RELEASE_ISSUED</c>，
    /// 记着授予的角色与「因为门锁停下」。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0356")]
    [Trait("Requirement", "REQ-0253")]
    public async Task AnR13OperatorReleasesALoadedVehicleStoppedForItsDoorsAndItIsAudited()
    {
        await using RuntimeFixture fixture = await InTransitDoorEmergencyReleaseTests.LatchedForTheDoorsAsync();
        using Roster roster = new();

        LoadedEmergencyStopReleaseResult result = await DecideAsync(fixture, roster, Request(fixture));

        Assert.True(
            result.Decision?.Action is EmergencyStopAction.Recovered or EmergencyStopAction.RecoveryUnconfirmed,
            $"{result.Decision?.Action}: {string.Join(", ", result.Codes)}");
        Assert.Empty(result.Codes);
        AdministratorAuditRecordRow audit = await SingleAuditAsync(fixture);
        Assert.Equal(result.AuditRecordId, audit.AuditRecordId);
        Assert.Equal(
            (LoadedEmergencyStopRelease.AuditAction, GovernedObjectKind.EmergencyStop, fixture.Options.AgvId,
                GovernanceActionOutcome.Succeeded, FieldOperatorRoleRoster.AgvOperationsAdministrator),
            (audit.Action, audit.ObjectKind, audit.ObjectId, audit.Outcome, audit.ClaimedAdministratorRole));
        Assert.Contains("\"result\":\"RELEASE_ISSUED\"", audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains("\"stoppedForDoors\":true", audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains(Admin, audit.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// 不是 R-13 的人（名单里只有 R-11，或根本不在名单里）、没写工号：拒绝，RIoT 一条解除都收不到；拒绝也写审计，结果 <c>REJECTED</c>。
    /// </summary>
    [Theory]
    [InlineData(Maintainer, LoadedEmergencyStopRelease.NotAuthorized)]
    [InlineData("L1-NOBODY", LoadedEmergencyStopRelease.NotAuthorized)]
    [InlineData(" ", "EMERGENCY_CONFIRMER_UNIDENTIFIED")]
    [Trait("Requirement", "REQ-0253")]
    public async Task AnyoneButAnR13OperatorIsRefusedAndTheRefusalIsAudited(string operatorId, string code)
    {
        await using RuntimeFixture fixture = await InTransitDoorEmergencyReleaseTests.LatchedForTheDoorsAsync();
        using Roster roster = new();

        LoadedEmergencyStopReleaseResult result = await DecideAsync(
            fixture, roster, Request(fixture) with { OperatorId = operatorId });

        Assert.Null(result.Decision);
        Assert.Equal([code], result.Codes);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
        AdministratorAuditRecordRow audit = await SingleAuditAsync(fixture);
        Assert.Equal((GovernanceActionOutcome.Failed, (string?)null), (audit.Outcome, audit.ClaimedAdministratorRole));
        Assert.Contains("\"result\":\"REJECTED\"", audit.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// 车是因为门锁停下的：没确认门锁已检修就拒绝（CP-0011：读到锁闭证明不了锁是好的）。拒绝照样写审计。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0356")]
    [Trait("Requirement", "REQ-0167")]
    public async Task AVehicleStoppedForItsDoorsIsNotReleasedWithoutTheLockRepairConfirmed()
    {
        await using RuntimeFixture fixture = await InTransitDoorEmergencyReleaseTests.LatchedForTheDoorsAsync();
        using Roster roster = new();

        LoadedEmergencyStopReleaseResult result = await DecideAsync(
            fixture, roster, Request(fixture) with { DoorLockRepaired = false });

        Assert.Equal(EmergencyStopAction.RecoveryRefused, result.Decision?.Action);
        Assert.Equal(["EMERGENCY_DOOR_LOCK_REPAIR_NOT_CONFIRMED"], result.Codes);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
        AdministratorAuditRecordRow audit = await SingleAuditAsync(fixture);
        Assert.Equal(GovernanceActionOutcome.Failed, audit.Outcome);
        Assert.Contains("EMERGENCY_DOOR_LOCK_REPAIR_NOT_CONFIRMED", audit.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// 审查 M-2：门锁急停之后那张单在 RIoT 被报 FAILED，故障证据码与旅程码都被改成 <c>VEHICLE_ORDER_FAILED</c>。
    /// 这次急停自己记着门锁原因，所以仍要确认门锁已检修——「检修门锁后才能解除」不因后来的症状被绕过。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    [Trait("Requirement", "REQ-0356")]
    public async Task AStopForTheDoorsStillNeedsTheLockRepairAfterItsOrderFails()
    {
        await using RuntimeFixture fixture = await InTransitDoorEmergencyReleaseTests.LatchedForTheDoorsAsync();
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        fixture.Riot.FailOrder(upperId);
        fixture.UnfinishedOrderIds = [];
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            // 前提：证据码与旅程码都已被盖掉。
            Assert.Equal(
                VehicleFaultEvidence.OrderFailed,
                (await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token)).EvidenceCode);
            Assert.Equal(VehicleFaultEvidence.OrderFailed, (await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token)).BlockReasonCode);
        }
        using Roster roster = new();

        LoadedEmergencyStopReleaseResult result = await DecideAsync(
            fixture, roster, Request(fixture) with { DoorLockRepaired = false });

        Assert.Equal(["EMERGENCY_DOOR_LOCK_REPAIR_NOT_CONFIRMED"], result.Codes);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>
    /// 升级前就开着的门锁急停：回执里还没有 <c>cause</c>（这一版之前不记），按钮按过一次、故障证据码已被盖成
    /// <c>RIOT_EMERGENCY_CAN_NOT_RECOVER</c>。剩下认得出门锁原因的只有旅程码，入口照样要求确认门锁已检修。
    /// </summary>
    /// <remarks>审查变异 V7（去掉读旅程码那一路）在补这一条之前存活：急停自己记的原因总是先认出来。</remarks>
    [Fact]
    [Trait("Requirement", "REQ-0167")]
    public async Task AStopRaisedBeforeCausesWereRecordedIsStillKnownByItsJourney()
    {
        await using RuntimeFixture fixture = await InTransitDoorEmergencyReleaseTests.LatchedForTheDoorsAsync();
        await using (ControlServerDbContext writing = new(fixture.DbOptionsForTests))
        {
            foreach (RiotOrderCommandAuditRow row in await writing.RiotOrderCommandAudit
                         .Where(row => row.ReceiptJson != null).ToListAsync(Token))
            {
                row.ReceiptJson = row.ReceiptJson!.Replace(
                    $"\"cause\":\"{VehicleFaultEvidence.DoorNotProvenLocked}\"", "\"cause\":null", StringComparison.Ordinal);
            }
            VehicleFaultStateRow fault = await writing.VehicleFaultStates.SingleAsync(Token);
            fault.EvidenceCode = VehicleFaultEvidence.EmergencyCanNotRecover;
            await writing.SaveChangesAsync(Token);
            // 前提：这次急停自己已经说不出原因，只剩旅程码。
            Assert.Empty(await fixture.CreateEmergencyStopSupervisor(writing).OpenEpisodeCausesAsync(
                new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey), Token));
            Assert.Equal(
                VehicleFaultEvidence.DoorNotProvenLocked,
                (await writing.JourneyRuntimes.AsNoTracking().SingleAsync(Token)).BlockReasonCode);
        }
        using Roster roster = new();

        LoadedEmergencyStopReleaseResult result = await DecideAsync(
            fixture, roster, Request(fixture) with { DoorLockRepaired = false });

        Assert.Equal(["EMERGENCY_DOOR_LOCK_REPAIR_NOT_CONFIRMED"], result.Codes);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>原因或工号太长：拒绝，审计只记长度不记原文。</summary>
    [Fact]
    public async Task AFieldTooLongIsRefusedAndOnlyItsLengthIsAudited()
    {
        await using RuntimeFixture fixture = await InTransitDoorEmergencyReleaseTests.LatchedForTheDoorsAsync();
        using Roster roster = new();
        string tooLong = new('x', LoadedEmergencyStopRelease.MaxTextLength + 1);

        LoadedEmergencyStopReleaseResult result = await DecideAsync(fixture, roster, Request(fixture) with { Reason = tooLong });

        Assert.Equal([LoadedEmergencyStopRelease.FieldTooLong], result.Codes);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
        AdministratorAuditRecordRow audit = await SingleAuditAsync(fixture);
        Assert.DoesNotContain(tooLong, audit.DetailJson, StringComparison.Ordinal);
        Assert.Contains($"\"reasonLength\":{tooLong.Length}", audit.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>名单读不到（文件不存在或不是 JSON）：当成「谁都没有这个权限」，不是「谁都有」。</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("garbled")]
    [Trait("Requirement", "REQ-0253")]
    public async Task AnUnreadableRosterGrantsNobody(string state)
    {
        await using RuntimeFixture fixture = await InTransitDoorEmergencyReleaseTests.LatchedForTheDoorsAsync();
        using Roster roster = new();
        if (state == "missing")
        {
            File.Delete(roster.Path);
        }
        else
        {
            File.WriteAllText(roster.Path, "{ not json");
        }

        LoadedEmergencyStopReleaseResult result = await DecideAsync(fixture, roster, Request(fixture));

        Assert.Equal([LoadedEmergencyStopRelease.NotAuthorized], result.Codes);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>每一个拒绝码都有给现场人员看的中文说明，入口的 409 逐条带上。</summary>
    [Fact]
    public void EveryRefusalTheEntryCanGiveHasADescription()
    {
        string[] codes =
        [
            "EMERGENCY_CONFIRMER_UNIDENTIFIED", LoadedEmergencyStopRelease.NotAuthorized, LoadedEmergencyStopRelease.FieldTooLong,
            "EMERGENCY_RELEASE_REASON_REQUIRED", "EMERGENCY_CAUSE_CLEARED_NOT_CONFIRMED", "EMERGENCY_DOORS_CLOSED_NOT_CONFIRMED",
            "EMERGENCY_DOOR_LOCK_REPAIR_NOT_CONFIRMED", "EMERGENCY_NOT_CAN_RECOVER", "EMERGENCY_NOT_RAISED_BY_8005",
            "EMERGENCY_VEHICLE_ORDERS_UNKNOWN", "EMERGENCY_VEHICLE_ORDER_NOT_FINISHED", EmergencyStopSupervisor.HeldOrderNotPausedReason,
            "EMERGENCY_RELEASE_NOT_CONFIRMED",
        ];
        Assert.All(codes, code => Assert.True(
            LoadedEmergencyStopRelease.Descriptions.TryGetValue(code, out string? text) && !string.IsNullOrWhiteSpace(text), code));
    }

    // ---- HTTP 入口（审查 M-1） -------------------------------------------------------------------------------------

    /// <summary>有货入口的凭据变量没配：回 503，什么都不调；这一次请求也写审计（L-4）。</summary>
    [Fact]
    public async Task AnUnconfiguredLoadedCredentialMakesTheEntryUnavailable()
    {
        await using RuntimeFixture fixture = await InTransitDoorEmergencyReleaseTests.LatchedForTheDoorsAsync();
        using Roster roster = new();
        string variable = "CONTROL_SERVER_TEST_MISSING_" + Guid.NewGuid().ToString("N");

        var result = await PostAsync(fixture, roster, variable, "Bearer anything", Request(fixture));

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
        Assert.Contains("\"result\":\"UNAVAILABLE\"", (await SingleAuditAsync(fixture)).DetailJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// 拿空车入口的凭据来解有货的车：回 401。两把凭据分开，持空车凭据的人解不了有货的车。拒绝照样写审计。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0253")]
    public async Task TheEmptyVehicleCredentialDoesNotReleaseALoadedVehicle()
    {
        await using RuntimeFixture fixture = await InTransitDoorEmergencyReleaseTests.LatchedForTheDoorsAsync();
        using Roster roster = new();
        string loaded = "CONTROL_SERVER_TEST_LOADED_" + Guid.NewGuid().ToString("N");
        string empty = "CONTROL_SERVER_TEST_EMPTY_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope loadedCredential = new(loaded, "loaded-credential");
        using EnvironmentVariableScope emptyCredential = new(empty, "empty-credential");

        var result = await PostAsync(fixture, roster, loaded, "Bearer empty-credential", Request(fixture), emptyVariable: empty);

        Assert.IsType<UnauthorizedHttpResult>(result.Result);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
        Assert.Contains("\"result\":\"UNAUTHORIZED\"", (await SingleAuditAsync(fixture)).DetailJson, StringComparison.Ordinal);
    }

    /// <summary>没写车：422；写了一辆本服务端不管的车：404。都不调 RIoT，都写审计。</summary>
    [Theory]
    [InlineData("", StatusCodes.Status422UnprocessableEntity, "INCOMPLETE")]
    [InlineData("agv09", StatusCodes.Status404NotFound, "UNKNOWN_VEHICLE")]
    public async Task ARequestNamingNoVehicleOfOursIsTurnedAway(string agvId, int status, string audited)
    {
        await using RuntimeFixture fixture = await InTransitDoorEmergencyReleaseTests.LatchedForTheDoorsAsync();
        using Roster roster = new();
        string variable = "CONTROL_SERVER_TEST_LOADED_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "loaded-credential");

        var result = await PostAsync(fixture, roster, variable, "Bearer loaded-credential", Request(fixture) with { AgvId = agvId });

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(status, problem.StatusCode);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
        Assert.Contains($"\"result\":\"{audited}\"", (await SingleAuditAsync(fixture)).DetailJson, StringComparison.Ordinal);
    }

    /// <summary>拒绝是 409：逐条列出原因码、对应的中文说明，以及这次请求的审计记录号。</summary>
    [Fact]
    public async Task ARefusalIsAConflictWithEveryReasonItsDescriptionAndTheAuditRecord()
    {
        await using RuntimeFixture fixture = await InTransitDoorEmergencyReleaseTests.LatchedForTheDoorsAsync();
        using Roster roster = new();
        string variable = "CONTROL_SERVER_TEST_LOADED_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "loaded-credential");

        var result = await PostAsync(
            fixture, roster, variable, "Bearer loaded-credential",
            Request(fixture) with { AllDoorsClosed = false, DoorLockRepaired = false });

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        IReadOnlyList<string> reasons = Assert.IsAssignableFrom<IReadOnlyList<string>>(problem.ProblemDetails.Extensions["reasons"]);
        Assert.Equal(["EMERGENCY_DOORS_CLOSED_NOT_CONFIRMED", "EMERGENCY_DOOR_LOCK_REPAIR_NOT_CONFIRMED"], reasons);
        string[] descriptions = Assert.IsType<string[]>(problem.ProblemDetails.Extensions["descriptions"]);
        Assert.Equal(reasons.Select(code => LoadedEmergencyStopRelease.Descriptions[code]), descriptions);
        Assert.Equal((await SingleAuditAsync(fixture)).AuditRecordId, problem.ProblemDetails.Extensions["auditRecordId"]);
        Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>齐全：解除发出、回读还锁着，202；响应带命令审计行与管理员审计记录。</summary>
    [Fact]
    public async Task ACompleteRequestIsAcceptedWithBothAuditRecords()
    {
        await using RuntimeFixture fixture = await InTransitDoorEmergencyReleaseTests.LatchedForTheDoorsAsync();
        using Roster roster = new();
        string variable = "CONTROL_SERVER_TEST_LOADED_" + Guid.NewGuid().ToString("N");
        using EnvironmentVariableScope credential = new(variable, "loaded-credential");

        var result = await PostAsync(fixture, roster, variable, "Bearer loaded-credential", Request(fixture));

        Accepted<LoadedEmergencyStopReleaseResponse> accepted =
            Assert.IsType<Accepted<LoadedEmergencyStopReleaseResponse>>(result.Result);
        Assert.Equal(nameof(EmergencyStopAction.RecoveryUnconfirmed), accepted.Value!.Action);
        Assert.False(string.IsNullOrWhiteSpace(accepted.Value.CommandAuditId));
        Assert.Equal((await SingleAuditAsync(fixture)).AuditRecordId, accepted.Value.AuditRecordId);
        Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.CancelEmergency));
    }

    /// <summary>
    /// 有货入口只在 <c>EmergencyStopRelease:enabled</c> 打开时挂，与空车入口挂在同一处；服务登记了。产品里没有起整台宿主的 L1，
    /// 这条读 <c>Program.cs</c> 守「挂在开关里面」；依赖注入真能解析由 L2 <c>in-transit-door-not-locked</c> 打到这个入口证。
    /// </summary>
    [Fact]
    public void TheLoadedEntryIsMountedOnlyBehindTheReleaseSwitch()
    {
        string program = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "ControlServer.Host", "Program.cs"));
        Assert.Contains("builder.Services.AddScoped<LoadedEmergencyStopRelease>();", program, StringComparison.Ordinal);
        int gate = program.IndexOf("if (app.Configuration.GetValue<bool>(\"EmergencyStopRelease:enabled\"))", StringComparison.Ordinal);
        Assert.True(gate >= 0);
        int open = program.IndexOf('{', gate);
        int close = program.IndexOf('}', open);
        int mount = program.IndexOf("app.MapLoadedEmergencyStopRelease();", StringComparison.Ordinal);
        Assert.True(open < mount && mount < close, "MapLoadedEmergencyStopRelease is outside the EmergencyStopRelease:enabled block.");
        Assert.Equal(mount, program.LastIndexOf("app.MapLoadedEmergencyStopRelease();", StringComparison.Ordinal));
    }

    private static async Task<Results<
        Ok<LoadedEmergencyStopReleaseResponse>,
        Accepted<LoadedEmergencyStopReleaseResponse>,
        UnauthorizedHttpResult,
        ProblemHttpResult>> PostAsync(
        RuntimeFixture fixture,
        Roster roster,
        string loadedVariable,
        string authorization,
        LoadedEmergencyStopReleaseRequest request,
        string? emptyVariable = null)
    {
        await using ControlServerDbContext context = new(fixture.DbOptionsForTests);
        DefaultHttpContext http = new();
        http.Request.Scheme = "http";
        http.Request.Headers.Authorization = authorization;
        return await LoadedEmergencyStopReleaseEndpoints.HandleAsync(
            http,
            request,
            Service(fixture, context, roster),
            new VehicleRoster(Options.Create(fixture.Options)),
            Options.Create(new EmergencyStopReleaseOptions
            {
                Enabled = true,
                CredentialEnvironmentVariable = emptyVariable ?? "CONTROL_SERVER_TEST_UNUSED_" + Guid.NewGuid().ToString("N"),
                LoadedCredentialEnvironmentVariable = loadedVariable,
            }),
            Token);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string name;

        public EnvironmentVariableScope(string name, string value)
        {
            this.name = name;
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(name, null);
    }

    private static LoadedEmergencyStopReleaseRequest Request(RuntimeFixture fixture) =>
        new(fixture.Options.AgvId, Admin, "door lock repaired on site", CauseCleared: true, AllDoorsClosed: true, DoorLockRepaired: true);

    private static async Task<LoadedEmergencyStopReleaseResult> DecideAsync(
        RuntimeFixture fixture,
        Roster roster,
        LoadedEmergencyStopReleaseRequest request)
    {
        await using ControlServerDbContext context = new(fixture.DbOptionsForTests);
        LoadedEmergencyStopRelease release = Service(fixture, context, roster);
        return await release.DecideAsync(
            new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey), request, Token);
    }

    private static LoadedEmergencyStopRelease Service(RuntimeFixture fixture, ControlServerDbContext context, Roster roster) =>
        new(
            context,
            fixture.CreateEmergencyStopSupervisor(context),
            new VehicleFaultStore(context),
            new FieldOperatorRoleRoster(Options.Create(new FieldOperatorRoleOptions { Path = roster.Path })),
            new GovernanceStore(context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default),
            fixture.Clock);

    private static async Task<AdministratorAuditRecordRow> SingleAuditAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return Assert.Single(await reading.Set<AdministratorAuditRecordRow>().AsNoTracking()
            .Where(row => row.Action == LoadedEmergencyStopRelease.AuditAction).ToArrayAsync(Token));
    }

    private static async Task<int> CountAsync(RuntimeFixture fixture, string commandType)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.RiotOrderCommandAudit.AsNoTracking().CountAsync(row => row.CommandType == commandType, Token);
    }

    /// <summary>一份临时的现场人员角色名单：一个 R-13、一个只有 R-11 的人。</summary>
    private sealed class Roster : IDisposable
    {
        public Roster()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cs539-roster-{Guid.NewGuid():N}.json");
            File.WriteAllText(
                Path,
                $$"""{"operators":[{"operatorId":"{{Admin}}","roles":["R-13"]},{"operatorId":"{{Maintainer}}","roles":["R-11"]}]}""");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
