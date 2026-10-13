using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Infrastructure.Persistence;
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

    private static LoadedEmergencyStopReleaseRequest Request(RuntimeFixture fixture) =>
        new(fixture.Options.AgvId, Admin, "door lock repaired on site", CauseCleared: true, AllDoorsClosed: true, DoorLockRepaired: true);

    private static async Task<LoadedEmergencyStopReleaseResult> DecideAsync(
        RuntimeFixture fixture,
        Roster roster,
        LoadedEmergencyStopReleaseRequest request)
    {
        await using ControlServerDbContext context = new(fixture.DbOptionsForTests);
        LoadedEmergencyStopRelease release = new(
            context,
            fixture.CreateEmergencyStopSupervisor(context),
            new VehicleFaultStore(context),
            new FieldOperatorRoleRoster(Options.Create(new FieldOperatorRoleOptions { Path = roster.Path })),
            new GovernanceStore(context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default),
            fixture.Clock);
        return await release.DecideAsync(
            new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey), request, Token);
    }

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

        public void Dispose() => File.Delete(Path);
    }
}
