using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using SiteRiot = ControlServer.Tests.VehicleFaultRecoveryTests.SiteRiot;

namespace ControlServer.Tests;

/// <summary>
/// control-server#897：门锁故障升级、依据被改写之后，本服务端的单在 RIoT 被人取消——清除入口仍按「起源是门锁故障」接受，不再死锁。
/// </summary>
/// <remarks>
/// <para>
/// <b>现场（2026-11-15 agv03，v2 生产）。</b>车装着 4 篮开往卸货站，4 号仓行驶中读到没锁：服务端记门锁故障、按住单、急停。之后有人按了车上
/// 急停，故障升级为 <c>ConfirmedIsolated</c>，依据改写成 <c>RIOT_EMERGENCY_CAN_NOT_RECOVER</c>。按住的单被人在 RIoT 取消，引擎记下同车重建
/// （PENDING，等故障清除）；有货急停人工解除之后，清除入口以 <c>FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT</c> 拒绝——修前只认「当前依据是门锁」。
/// 重建等清除、清除拒重建，只能改库。
/// </para>
/// <para>
/// <b>修法。</b>故障行在进入新一代时记下服务端看到的症状（<c>OriginSymptomCode</c>），本代内不再改写；清除入口认「起源是门锁、并且本代故障
/// 按住过当前这张单」。#335 审查 M1 那条规则（被取消的单归引擎重建，清除不能是第二条路）对其余故障不变：第二、第三条用例守它。
/// </para>
/// </remarks>
public sealed class DoorFaultEscalatedThenCancelledInRiotTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;


    /// <summary>
    /// 迁移之前的最后一个迁移：本票的迁移紧跟在它后面（迁移纪律台账也这么登记）。
    /// </summary>
    private const string PreviousMigration = "20261110075917_JourneyDemandsRedispatchIndex";

    // ---- 现场出路：PR 正文「Vehicles already stuck when this is deployed」一节逐字引用这三句，正文改了这里要跟着改 ----------------

    /// <summary>部署时只读预查：有没有在效的故障行没有起源症状（即部署前记下的、可能需要这条出路的车）。</summary>
    internal const string FieldWayOutDeploymentCheck =
        "SELECT COUNT(*) AS Value FROM VehicleFaultStates WHERE Level <> 'None' AND OriginSymptomCode IS NULL";

    /// <summary>
    /// 改库前只读预查：这一代故障按住过旅程现在等的那张单。没有这一行，补了起源也解不开（清除还要看这一行），不必改库。
    /// </summary>
    internal const string FieldWayOutHoldCheck =
        "SELECT COUNT(*) AS Value FROM RiotOrderCommandAudit " +
        "WHERE CommandType = 'OrderHold' AND TargetUpperId = @upperId AND FaultGeneration = @generation";

    /// <summary>出路本身：只补空的起源症状，限这辆车、这一代、仍在效，依据码不动。</summary>
    internal const string FieldWayOutUpdate =
        "UPDATE VehicleFaultStates SET OriginSymptomCode = 'VEHICLE_DOOR_NOT_PROVEN_LOCKED' " +
        "WHERE AgvId = @agvId AND FaultGeneration = @generation AND Level <> 'None' AND OriginSymptomCode IS NULL";

    /// <summary>
    /// 现场序列：门锁故障（运行中的单被按住）→ 车上急停按下，故障升级、依据改写 → 单在 RIoT 被取消，引擎记重建、等故障清除 →
    /// 有货急停人工解除 → 人工清除成功，只清故障、不记第二次重建 → 重建过了护栏（延迟、车况、#349 的仓位读数）才建，建且只建一张开往卸货站的单。
    /// 修前在「人工清除」那一步红：<c>Refused / FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT</c>。
    /// </summary>
    /// <remarks>
    /// 两种先后（调度 Coordinator14 2026-11-17 要求）：<c>after</c> 是现场的样子——服务端先记门锁故障、写下起源症状，之后按钮才按下、依据被改写；
    /// <c>before</c> 是按钮在服务端第一次看到门锁之前就按着——这一代故障一开始依据就是 <c>RIOT_EMERGENCY_CAN_NOT_RECOVER</c>，从来没有过门锁的依据码，
    /// 只有起源症状记着门锁。修前两格都红；只看「第一个依据码」的修法会在 <c>before</c> 这一格红。
    /// </remarks>
    [Theory]
    [InlineData("after")]
    [InlineData("before")]
    [Trait("Requirement", "REQ-0246")]
    [Trait("Requirement", "REQ-0360")]
    public async Task ADoorFaultEscalatedByAHeldButtonClearsAfterItsOrderIsCancelledInRiotAndTheOneRebuildRuns(string escalation)
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        (OwnOrderRebuildRow recorded, int gateCreates) =
            await DoorFaultedEscalatedCancelledAndReleasedAsync(fixture, buttonFirst: escalation == "before");

        // 五：人工清除。修前在这里拒（FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT）。只清故障，不记第二次重建。
        VehicleFaultRecoveryDecision cleared = await ClearAsync(fixture);
        Assert.True(VehicleFaultRecoveryOutcome.Cleared == cleared.Outcome, string.Join(", ", cleared.Reasons));
        Assert.Equal(VehicleFaultRecoveryDispositions.None, cleared.Disposition);
        Assert.Equal(VehicleFaultLevel.None, (await FaultAsync(fixture)).Level);
        Assert.Equal(recorded.RebuildId, Assert.Single(await RebuildsAsync(fixture)).RebuildId);

        // 六：重建在 #349 的仓位读数到之前不建（车上有货，要先证明货还在原仓位）。
        await fixture.RestoreSessionReadyAsync();
        fixture.Context.ChangeTracker.Clear();
        fixture.Riot.MovementState = "MT_FINISHED";
        await fixture.SetDepartureSummaryAsync();
        await DriveOneRoundAsync(fixture);
        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(OwnOrderRebuildStates.Pending, Assert.Single(await RebuildsAsync(fixture)).State);

        // 七：仓位读数到了 -> 建且只建一张开往卸货站的单，还是那一条重建记录。
        await OwnOrderRebuildTests.ReportCargoInPlaceAndRunAsync(fixture);
        for (int round = 0; round < 3; round++)
        {
            await DriveOneRoundAsync(fixture);
        }

        Assert.Equal(gateCreates + 1, fixture.Riot.CreateCount("TO_GATE"));
        OwnOrderRebuildRow rebuilt = Assert.Single(await RebuildsAsync(fixture));
        Assert.Equal((recorded.RebuildId, OwnOrderRebuildStates.Rebuilt), (rebuilt.RebuildId, rebuilt.State));
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>
    /// 部署之前已经记下的故障行，<c>OriginSymptomCode</c> 是空的：按今天的规则判，只看依据码。所以上线前已经卡在这条死锁上的车，这次修复
    /// 解不开——依据已被改写，清除照旧拒。现场的出路是核实这一代故障确是门锁故障（它的急停原因、门锁告警）之后，只把空的
    /// <c>OriginSymptomCode</c> 补成门锁的症状码，不动依据码；清除随即接受，走引擎已登记的那一条重建。
    /// </summary>
    /// <remarks>
    /// 执行的是 PR 正文里的原句（<see cref="FieldWayOutDeploymentCheck"/>、<see cref="FieldWayOutHoldCheck"/>、<see cref="FieldWayOutUpdate"/>，
    /// 审查 cs#899 意见 (a)(b)）：两条预查各读到这辆车；代数写错改 0 行、写对改 1 行。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task AFaultRowFromBeforeTheColumnIsJudgedOnItsEvidenceAndClearsOnceItsOriginIsFilledIn()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        (OwnOrderRebuildRow recorded, _) = await DoorFaultedEscalatedCancelledAndReleasedAsync(fixture, buttonFirst: false);

        // 部署前的行：起源症状为空，依据已是 CAN_NOT_RECOVER。
        Assert.Equal(1, await fixture.Context.Database.ExecuteSqlRawAsync(
            "UPDATE VehicleFaultStates SET OriginSymptomCode = NULL", Token));
        VehicleFaultRecoveryDecision refused = await ClearAsync(fixture);
        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, refused.Outcome);
        Assert.Equal(["FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT"], refused.Reasons);

        // 现场的出路，照 PR 正文原句：两条只读预查，再只补空的起源症状。
        VehicleFaultStateRow stuck = await FaultAsync(fixture);
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        Assert.Equal(1L, await fixture.Context.Database.SqlQueryRaw<long>(FieldWayOutDeploymentCheck).SingleAsync(Token));
        Assert.Equal(1L, await fixture.Context.Database.SqlQueryRaw<long>(
                FieldWayOutHoldCheck, new SqliteParameter("@upperId", upperId), new SqliteParameter("@generation", stuck.FaultGeneration))
            .SingleAsync(Token));
        Assert.Equal(0, await fixture.Context.Database.ExecuteSqlRawAsync(
            FieldWayOutUpdate,
            [new SqliteParameter("@agvId", fixture.Options.AgvId), new SqliteParameter("@generation", stuck.FaultGeneration + 1)],
            Token));
        Assert.Equal(1, await fixture.Context.Database.ExecuteSqlRawAsync(
            FieldWayOutUpdate,
            [new SqliteParameter("@agvId", fixture.Options.AgvId), new SqliteParameter("@generation", stuck.FaultGeneration)],
            Token));
        Assert.Equal(VehicleFaultEvidence.EmergencyCanNotRecover, (await FaultAsync(fixture)).EvidenceCode);
        VehicleFaultRecoveryDecision cleared = await ClearAsync(fixture);
        Assert.True(VehicleFaultRecoveryOutcome.Cleared == cleared.Outcome, string.Join(", ", cleared.Reasons));
        Assert.Equal(VehicleFaultRecoveryDispositions.None, cleared.Disposition);
        Assert.Equal(recorded.RebuildId, Assert.Single(await RebuildsAsync(fixture)).RebuildId);
    }

    /// <summary>
    /// 同一辆车：上一代是门锁故障（按住了取货单），人清除了；下一代以别的症状记在开往卸货站的单上，按钮按着、依据同样是 CAN_NOT_RECOVER。
    /// 这张单被人在 RIoT 取消之后清除照旧拒绝：新一代的起源症状是它自己的，不继承上一代的门锁。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task ASecondFaultOnTheSameVehicleDoesNotInheritTheDoorOriginOfTheFirst()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        string upperId = runtime.GateUpperId!;
        string pickupUpperId = runtime.PickupUpperId;
        SiteRiot site = new(fixture) { HasUnfinishedOrder = false };

        await ObserveAsync(
            fixture, site, VehicleFaultEvidence.DoorNotProvenLocked,
            new RiotOrderCommandTarget(fixture.Options.AgvId, pickupUpperId, await OrderIdAsync(fixture, pickupUpperId)));
        VehicleFaultStateRow first = await FaultAsync(fixture);
        Assert.Equal(VehicleFaultEvidence.DoorNotProvenLocked, first.OriginSymptomCode);
        await using (ControlServerDbContext writing = new(fixture.DbOptionsForTests))
        {
            await new VehicleFaultStore(writing).ClearAsync(
                fixture.Options.AgvId, first.FaultGeneration, "L1_CLEARED", fixture.Clock.GetUtcNow(), Token);
        }

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.ButtonHeld = true;
        await ObserveAsync(
            fixture, site, "L1_CONSTRUCTED_SYMPTOM",
            new RiotOrderCommandTarget(fixture.Options.AgvId, upperId, await OrderIdAsync(fixture, upperId)));
        VehicleFaultStateRow second = await FaultAsync(fixture);
        Assert.Equal(
            (first.FaultGeneration + 1, VehicleFaultEvidence.EmergencyCanNotRecover, "L1_CONSTRUCTED_SYMPTOM"),
            (second.FaultGeneration, second.EvidenceCode, second.OriginSymptomCode));
        Assert.True(await HeldUnderGenerationAsync(fixture, upperId, second.FaultGeneration));

        fixture.ButtonHeld = false;
        fixture.Riot.SetOrderState(upperId, RiotOrderState.Cancelled, terminal: true);
        VehicleFaultRecoveryDecision clearance = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, clearance.Outcome);
        Assert.Contains("FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT", clearance.Reasons);
    }

    /// <summary>
    /// 同一辆车、同一张单：上一代是门锁故障、按住过开往卸货站的单，人清除了；这一代又是门锁故障，但按住的是取货单，开往卸货站的单在这一代
    /// 下从没被按住过。它被人在 RIoT 取消之后清除照旧拒绝：「按住过这张单」只认这一代的按住，上一代的不算（审查 cs#899 L2，变异 R2）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task AHoldFromAnEarlierGenerationDoesNotLetTheCurrentOneClearOverTheSameOrderCancelledInRiot()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        string upperId = runtime.GateUpperId!;
        string pickupUpperId = runtime.PickupUpperId;
        SiteRiot site = new(fixture) { HasUnfinishedOrder = false };

        await ObserveAsync(
            fixture, site, VehicleFaultEvidence.DoorNotProvenLocked,
            new RiotOrderCommandTarget(fixture.Options.AgvId, upperId, await OrderIdAsync(fixture, upperId)));
        VehicleFaultStateRow first = await FaultAsync(fixture);
        Assert.True(await HeldUnderGenerationAsync(fixture, upperId, first.FaultGeneration));
        await using (ControlServerDbContext writing = new(fixture.DbOptionsForTests))
        {
            await new VehicleFaultStore(writing).ClearAsync(
                fixture.Options.AgvId, first.FaultGeneration, "L1_CLEARED", fixture.Clock.GetUtcNow(), Token);
        }

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await ObserveAsync(
            fixture, site, VehicleFaultEvidence.DoorNotProvenLocked,
            new RiotOrderCommandTarget(fixture.Options.AgvId, pickupUpperId, await OrderIdAsync(fixture, pickupUpperId)));
        VehicleFaultStateRow second = await FaultAsync(fixture);
        Assert.Equal(
            (first.FaultGeneration + 1, VehicleFaultEvidence.DoorNotProvenLocked),
            (second.FaultGeneration, second.OriginSymptomCode));
        Assert.False(await HeldUnderGenerationAsync(fixture, upperId, second.FaultGeneration));

        fixture.Riot.SetOrderState(upperId, RiotOrderState.Cancelled, terminal: true);
        VehicleFaultRecoveryDecision clearance = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, clearance.Outcome);
        Assert.Contains("FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT", clearance.Reasons);
    }

    /// <summary>
    /// 迁移只加一列可空的 <c>OriginSymptomCode</c>：降回上一个迁移时列消失、已有的故障行不丢；再升上来，那一行的这一列是空的、其余不变。
    /// 模型快照与模型一致。
    /// </summary>
    [Fact]
    public async Task TheMigrationAddsOneNullableColumnAndGoesDownAndUpWithoutTouchingAFaultRow()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(migrate: true);
        DatabaseFacade database = fixture.Context.Database;
        Assert.False(database.HasPendingModelChanges());
        string[] migrations = [.. database.GetMigrations()];
        Assert.Equal(PreviousMigration, migrations[^2]);
        Assert.EndsWith("_VehicleFaultOriginSymptomCode", migrations[^1], StringComparison.Ordinal);
        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT \"notnull\" AS Value FROM pragma_table_info('VehicleFaultStates') WHERE name = 'OriginSymptomCode'"));

        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Token);
        Assert.Equal(0L, await ColumnCountAsync(fixture));
        await database.ExecuteSqlRawAsync(
            "INSERT INTO VehicleFaultStates (AgvId, Level, FaultGeneration, EvidenceCode, EvidenceOnAutoConfirmWhitelist, EnteredAt, " +
            "LastEvaluatedAt, StopProven) VALUES ('agv-legacy', 'ConfirmedIsolated', 2, 'RIOT_EMERGENCY_CAN_NOT_RECOVER', 1, " +
            "'2026-11-15 07:46:01+00:00', '2026-11-15 08:14:24+00:00', 0)",
            Token);
        string before = await LegacyRowAsync(fixture);

        await database.MigrateAsync(Token);
        Assert.Equal(1L, await ColumnCountAsync(fixture));
        Assert.Equal(before, await LegacyRowAsync(fixture));
        Assert.Equal(1L, await ScalarAsync(fixture, "SELECT COUNT(*) AS Value FROM VehicleFaultStates WHERE AgvId = 'agv-legacy' AND OriginSymptomCode IS NULL"));

        await migrator.MigrateAsync(PreviousMigration, Token);
        Assert.Equal(0L, await ColumnCountAsync(fixture));
        Assert.Equal(before, await LegacyRowAsync(fixture));

        await database.MigrateAsync(Token);
        Assert.Equal(1L, await ColumnCountAsync(fixture));
        Assert.Equal(before, await LegacyRowAsync(fixture));
    }

    /// <summary>
    /// 从来不是门锁故障：以别的症状记下、按钮按着所以依据同样是 <c>RIOT_EMERGENCY_CAN_NOT_RECOVER</c>（与现场升级后的样子一模一样）；
    /// 单被人在 RIoT 取消之后，清除入口照旧拒绝——被取消的单归引擎重建（#335 审查 M1），清除不能是第二条路。
    /// </summary>
    /// <remarks>
    /// 起点是构造的，照 <see cref="EmergencyReleaseVersusOwnOrderRebuildTests"/> 第四条：产品里在运行中的单上记故障的只有门锁那一路。它守的是
    /// 「按依据放行」这种修法：只看 <c>EvidenceCode</c> 是不是 CAN_NOT_RECOVER，这一格会被放过去。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task AFaultThatNeverWasADoorFaultStillRefusesAClearanceOverAnOrderCancelledInRiot()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        string orderId = await OrderIdAsync(fixture, upperId);
        SiteRiot site = new(fixture) { HasUnfinishedOrder = false };

        fixture.ButtonHeld = true;
        await ObserveAsync(fixture, site, "L1_CONSTRUCTED_SYMPTOM", new RiotOrderCommandTarget(fixture.Options.AgvId, upperId, orderId));
        VehicleFaultStateRow fault = await FaultAsync(fixture);
        Assert.Equal(
            (VehicleFaultLevel.ConfirmedIsolated, VehicleFaultEvidence.EmergencyCanNotRecover),
            (fault.Level, fault.EvidenceCode));
        Assert.True(await HeldUnderGenerationAsync(fixture, upperId, fault.FaultGeneration));

        fixture.ButtonHeld = false;
        fixture.Riot.SetOrderState(upperId, RiotOrderState.Cancelled, terminal: true);
        VehicleFaultRecoveryDecision clearance = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, clearance.Outcome);
        Assert.Contains("FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT", clearance.Reasons);
    }

    /// <summary>
    /// 起源是门锁故障，但它按住的是另一张单（这里是同一趟旅程已经跑完的取货单），旅程现在等的这张单从没在这一代故障下被按住过：
    /// 单在 RIoT 被取消之后清除照旧拒绝。票面的条件是「在该单运行时发起」，不是「这辆车曾有门锁故障」。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0360")]
    public async Task ADoorFaultRaisedOnAnotherOrderDoesNotClearOverTheCurrentOrderCancelledInRiot()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        string upperId = runtime.GateUpperId!;
        string pickupUpperId = runtime.PickupUpperId;
        SiteRiot site = new(fixture) { HasUnfinishedOrder = false };

        await ObserveAsync(
            fixture, site, VehicleFaultEvidence.DoorNotProvenLocked,
            new RiotOrderCommandTarget(fixture.Options.AgvId, pickupUpperId, await OrderIdAsync(fixture, pickupUpperId)));
        VehicleFaultStateRow fault = await FaultAsync(fixture);
        Assert.Equal(VehicleFaultEvidence.DoorNotProvenLocked, fault.OriginSymptomCode);
        Assert.True(await HeldUnderGenerationAsync(fixture, pickupUpperId, fault.FaultGeneration));
        Assert.False(await HeldUnderGenerationAsync(fixture, upperId, fault.FaultGeneration));

        fixture.Riot.SetOrderState(upperId, RiotOrderState.Cancelled, terminal: true);
        VehicleFaultRecoveryDecision clearance = await VehicleFaultRecoveryTests.Service(fixture, site)
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);

        Assert.Equal(VehicleFaultRecoveryOutcome.Refused, clearance.Outcome);
        Assert.Contains("FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT", clearance.Reasons);
    }

    // ---- 夹具 ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 现场序列的前四步，停在人工清除之前：门锁故障按住运行中的单 → 按钮按下、依据改写（<paramref name="buttonFirst"/> 时按钮在第一次观测前就按着）
    /// → 单在 RIoT 被取消、引擎记重建，过了到期仍等故障清除 → 有货急停人工解除。返回那一条重建记录与此前开往卸货站的建单数。
    /// </summary>
    private static async Task<(OwnOrderRebuildRow Recorded, int GateCreates)> DoorFaultedEscalatedCancelledAndReleasedAsync(
        RuntimeFixture fixture, bool buttonFirst)
    {
        string upperId = (await fixture.RuntimeAsync()).GateUpperId!;
        int gateCreates = fixture.Riot.CreateCount("TO_GATE");

        // 一：行驶中门锁没锁 -> 门锁故障，同一轮按住这张单；RIoT 停住单，闩锁锁上。
        fixture.Riot.MovementState = "MT_RUNNING";
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = 0 };
        fixture.ButtonHeld = buttonFirst;
        await fixture.ReportSafetySummaryAsync(
            allTargetSlotsLocked: false, unknownPresent: false, ["LOCK_NOT_CLOSED", "ACTION_NOT_ALLOWED_IN_STATE"]);
        await DriveOneRoundAsync(fixture);
        string orderId = await OrderIdAsync(fixture, upperId);
        fixture.Riot.SetOrderState(upperId, RiotOrderState.Paused, terminal: false);
        fixture.Riot.MovementState = "MT_PAUSED";
        fixture.EmergencyLatched = true;
        fixture.UnfinishedOrderIds = [orderId];
        await DriveOneRoundAsync(fixture);
        VehicleFaultStateRow first = await FaultAsync(fixture);
        Assert.Equal(VehicleFaultEvidence.DoorNotProvenLocked, first.OriginSymptomCode);
        Assert.True(await HeldUnderGenerationAsync(fixture, upperId, first.FaultGeneration));
        if (buttonFirst)
        {
            // 按钮先按着：这一代故障从一开始依据就是 CAN_NOT_RECOVER，从没有过门锁的依据码；服务端只在那个闩锁上记下本服务端的一类。
            Assert.Equal(
                (VehicleFaultLevel.ConfirmedIsolated, VehicleFaultEvidence.EmergencyCanNotRecover),
                (first.Level, first.EvidenceCode));
            Assert.Equal(0, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));
        }
        else
        {
            Assert.Equal(
                (VehicleFaultLevel.SuspectedBlocked, VehicleFaultEvidence.DoorNotProvenLocked),
                (first.Level, first.EvidenceCode));
            Assert.Equal(1, await CountAsync(fixture, RiotCommandTypeNames.TriggerEmergency));

            // 二：有人按下车上急停 -> 升级为 ConfirmedIsolated，依据改写成 CAN_NOT_RECOVER；同一代故障，起源症状不变。
            fixture.ButtonHeld = true;
            await DriveOneRoundAsync(fixture);
            VehicleFaultStateRow escalated = await FaultAsync(fixture);
            Assert.Equal(
                (VehicleFaultLevel.ConfirmedIsolated, VehicleFaultEvidence.EmergencyCanNotRecover, first.FaultGeneration,
                    VehicleFaultEvidence.DoorNotProvenLocked),
                (escalated.Level, escalated.EvidenceCode, escalated.FaultGeneration, escalated.OriginSymptomCode));
        }

        // 三：按钮松开（CAN_RECOVER）；按住的单被人在 RIoT 取消 -> 引擎记重建，过了到期仍等故障清除，一张单都不建。
        fixture.ButtonHeld = false;
        fixture.Riot.SetOrderState(upperId, RiotOrderState.Cancelled, terminal: true);
        fixture.UnfinishedOrderIds = [];
        await ReportLockedAsync(fixture);
        await DriveOneRoundAsync(fixture);
        OwnOrderRebuildRow recorded = Assert.Single(await RebuildsAsync(fixture));
        Assert.Equal((OwnOrderRebuildSources.CancelledInRiot, OwnOrderRebuildStates.Pending), (recorded.Source, recorded.State));
        fixture.Clock.Advance(fixture.Options.OwnOrderRebuildDelay);
        await DriveOneRoundAsync(fixture);
        OwnOrderRebuildRow waiting = Assert.Single(await RebuildsAsync(fixture));
        Assert.True(fixture.Clock.GetUtcNow() > waiting.DueAt);
        Assert.Contains("VEHICLE_FAULT_IN_EFFECT", waiting.WaitingReason, StringComparison.Ordinal);
        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));

        // 四：有货急停人工解除（确认门锁已检修），闩锁读回 OK。故障仍在，仍不建。
        await fixture.ReleaseByAPersonAsync();
        fixture.EmergencyLatched = false;
        for (int round = 0; round < 3; round++)
        {
            await DriveOneRoundAsync(fixture);
        }

        Assert.Equal(gateCreates, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(VehicleFaultLevel.ConfirmedIsolated, (await FaultAsync(fixture)).Level);
        return (recorded, gateCreates);
    }

    private static async Task<VehicleFaultRecoveryDecision> ClearAsync(RuntimeFixture fixture)
    {
        fixture.Context.ChangeTracker.Clear();
        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, new(fixture) { HasUnfinishedOrder = false })
            .RecoverAsync(VehicleFaultRecoveryTests.Clear(fixture), Token);
        fixture.Context.ChangeTracker.Clear();
        return decision;
    }

    private static Task<long> ColumnCountAsync(RuntimeFixture fixture) =>
        ScalarAsync(fixture, "SELECT COUNT(*) AS Value FROM pragma_table_info('VehicleFaultStates') WHERE name = 'OriginSymptomCode'");

    private static Task<long> ScalarAsync(RuntimeFixture fixture, string sql) =>
        fixture.Context.Database.SqlQueryRaw<long>(sql).SingleAsync(Token);

    /// <summary>The legacy row over the columns the previous migration has, so the two schemas compare.</summary>
    private static Task<string> LegacyRowAsync(RuntimeFixture fixture) =>
        fixture.Context.Database.SqlQueryRaw<string>(
                "SELECT quote(AgvId) || '|' || quote(Level) || '|' || quote(FaultGeneration) || '|' || quote(EvidenceCode) || '|' || " +
                "quote(EvidenceOnAutoConfirmWhitelist) || '|' || quote(EnteredAt) || '|' || quote(LastEvaluatedAt) || '|' || " +
                "quote(StopProven) || '|' || quote(StopProvenAt) || '|' || quote(EscalatedAt) || '|' || quote(ClearedAt) || '|' || " +
                "quote(ClearedReason) AS Value FROM VehicleFaultStates WHERE AgvId = 'agv-legacy'")
            .SingleAsync(Token);

    private static async Task<RuntimeFixture> GateArrivalWaitAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync()).Stage);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    /// <summary>经故障协调器的公开入口记一次症状，站在 RIoT 现场一侧的替身上（照 <c>EmergencyReleaseVersusOwnOrderRebuildTests.Coordinator</c>）。</summary>
    private static async Task ObserveAsync(RuntimeFixture fixture, SiteRiot site, string symptom, RiotOrderCommandTarget target)
    {
        await using ControlServerDbContext context = new(fixture.DbOptionsForTests);
        VehicleFaultStore faults = new(context);
        RiotOrderCommandAuditStore audit = new(context);
        EmergencyStopSupervisor supervisor = new(
            site, site, site, audit, faults, Options.Create(new RiotCommandOptions()), fixture.Clock,
            NullLogger<EmergencyStopSupervisor>.Instance);
        IOptions<VehicleFaultOptions> faultOptions = Options.Create(new VehicleFaultOptions());
        VehicleFaultCoordinator coordinator = new(
            faults, site, site, site, audit, new RiotOrderCommandService(site, audit, site, fixture.Clock), supervisor,
            new VehicleMotionLedger(faultOptions), faultOptions, fixture.Clock, NullLogger<VehicleFaultCoordinator>.Instance);
        await coordinator.ObserveAsync(
            new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey), symptom,
            new FaultedVehicleContext(target, Cargo: null), Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static async Task<bool> HeldUnderGenerationAsync(RuntimeFixture fixture, string upperId, long generation)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.RiotOrderCommandAudit.AsNoTracking().AnyAsync(
            row => row.CommandType == RiotCommandTypeNames.OrderHold && row.TargetUpperId == upperId && row.FaultGeneration == generation,
            Token);
    }

    private static async Task<VehicleFaultStateRow> FaultAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.VehicleFaultStates.AsNoTracking().SingleAsync(Token);
    }

    private static async Task<OwnOrderRebuildRow[]> RebuildsAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token);
    }

    private static async Task<int> CountAsync(RuntimeFixture fixture, string commandType)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.RiotOrderCommandAudit.AsNoTracking().CountAsync(row => row.CommandType == commandType, Token);
    }

    private static async Task<string> OrderIdAsync(RuntimeFixture fixture, string upperId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return (await reading.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == upperId, Token)).OrderId!;
    }

    private static async Task DriveOneRoundAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static Task ReportLockedAsync(RuntimeFixture fixture) =>
        fixture.ReportSafetySummaryAsync(allTargetSlotsLocked: true, unknownPresent: false, ["ACTION_NOT_ALLOWED_IN_STATE"]);
}
