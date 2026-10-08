using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.StopEndedJourneyContinuesTests;
using static ControlServer.Tests.UnloadCrashResumeTests;

namespace ControlServer.Tests;

/// <summary>
/// 「恢复结果没对上」留下的阻塞（<c>*_NOT_RECONCILED</c>）按标记放行（control-server#505）：每个这样的阻塞都对应一条
/// <c>RecoveryRequired</c> 的需求，那条需求被终结或被修复续行放回之后、旅程里再没有别的没结论的事，旅程放出。
/// </summary>
/// <remarks>
/// <para>
/// cs#499 与 cs#506 时阻塞码不一定有标记（需求已送达或已取消时只写码），两条放行路径对 <c>*_NOT_RECONCILED</c> 一律不放。这里的放行格子
/// 在那时全部留在 <c>Blocked</c>、只能改库：交接第一次没对上再交接对上、补偿第一次没对上再补偿对上、修复续行被另一条挡住而那一条后来交接对上、
/// 纠错没对上再交接。护栏格子：两条都没对上只结一条、已终结的需求开不出会话、扫码前取消的结果晚到而需求已被终结。
/// </para>
/// <para>
/// 打不上标记的那一种改写成不可放行的码，它的格子在 <see cref="ACompensationDoesNotLiftABlockAnUnmarkedResultLeft"/>；纠错只授权给在车上的
/// 需求，见 <see cref="ACorrectionForADemandNoLongerOnBoardIsRefused"/>。
/// </para>
/// </remarks>
public sealed partial class RecoveryEndingReleasesBlockedJourneyTests
{
    private const string FailedCompensationRequestId = "44990000-0000-4000-8000-000000000011";
    private const string FailedCompensationActionId = "54990000-0000-4000-8000-000000000011";
    private const string RetryHandoffActionId = "54990000-0000-4000-8000-000000000012";
    private const string EndedDemandRequestId = "44990000-0000-4000-8000-000000000013";
    private const string CorrectionId = "74990000-0000-4000-8000-000000000002";

    /// <summary>
    /// 票面第 1 件，也是「在后一个取货站交接掉前一站装的需求」：第一站装上第一条；车在第二站，第二条在装、第三条还没装。第二条装货结果要恢复，
    /// 旅程阻塞。管理员先交接第一条，车报已交接、一个仓位却读到 <c>OCCUPIED</c>，没对上，第一条留在 <c>RecoveryRequired</c>；补偿第二条，被第一条挡住。
    /// 再为第一条重开会话，交接对上了。第一条的装货早在第一站就已提交，终结的不是旅程阻塞在其上的那一次——修之前第 2 条不成立，旅程永远停在
    /// <c>Blocked</c>。修之后它是那个标记，旅程放回当前停靠（第二个取货站）等装货结果，接着向车要第三条的录入。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AHandoffRetriedUntilItReconcilesReleasesTheJourneyAtTheCurrentStop()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
            await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
            await BlockOnLoadAsync(fixture, SecondDemandId);
            int[] firstSlots = await SlotsOfAsync(fixture, FirstDemandId, SlotOperationType.Load);
            await HandOffAsync(
                fixture, FirstDemandId, SlotOperationType.Load, FirstHandoffRequestId, FirstHandoffActionId, occupiedSlot: firstSlots[0]);
            await CompensateAsync(fixture, SecondDemandId);
            Assert.Equal(JourneyRuntimeStage.Blocked, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

            await HandOffAsync(fixture, FirstDemandId, SlotOperationType.Load, RetryRequestId, RetryHandoffActionId);
            await AssertReleasedAsync(fixture, JourneyRuntimeStage.AwaitingLoadResult);
            await fixture.RestoreSessionReadyAsync();
            Exception? round = await RunRoundAsync(fixture);

            JourneyRuntimeRow after = await JourneyOfAsync(fixture, ThirdDemandId);
            Assert.True(
                after.Stage == JourneyRuntimeStage.AwaitingSublot && after.BlockReasonCode is null,
                $"The second pickup still has the third demand to load. stage={after.Stage} block={after.BlockReasonCode} " +
                $"round={round?.Message}");
            await EnterSublotAsync(fixture, FirstDemandId, ThirdSublot, ThirdSubmissionId);
            Assert.Equal(JourneyDemandStatuses.Loading, (await MembershipAsync(fixture, ThirdDemandId)).Status);
        });
    }

    /// <summary>
    /// cs#499 的代价那一格：一个取货站两条，第二条装货结果要恢复；补偿第一次车报 <c>UNKNOWN</c>，没对上，第二条留在 <c>RecoveryRequired</c>、
    /// 阻塞码 <c>LoadCompensationResult_NOT_RECONCILED</c>；第二次补偿对上了。修之前留在 <c>Blocked</c>（第 5 条）；修之后车带着第一条离站。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationThatReconcilesOnTheSecondTrySendsTheVehicleOnWithTheOther()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(fixture);
            await FailCompensationAsync(fixture, SecondDemandId, FailedCompensationRequestId, FailedCompensationActionId);
            int checksBefore = await CountAsync(fixture, "PreDepartureSafetyCheck");

            await CompensateAsync(fixture, SecondDemandId);
            await AssertReleasedAsync(fixture, JourneyRuntimeStage.AwaitingLoadResult);
            await fixture.RestoreSessionReadyAsync();
            Exception? first = await RunRoundAsync(fixture);
            Exception? second = await RunRoundAsync(fixture);

            await AssertAskedToLeaveAsync(fixture, checksBefore, first, second);
            Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
        });
    }

    /// <summary>
    /// cs#506 留下的卡死 (1)：旅程阻塞在第二条的装货恢复上；第一条交接没对上、留在 <c>RecoveryRequired</c>；第二条修复续行对上了，被第一条挡住
    /// （cs#506 的第 4 条）。之后第一条重开会话交接对上了：修之前它的装货早已提交、阻塞码又是 <c>*_NOT_RECONCILED</c>，两条放行路径都不放；
    /// 修之后它是最后一个标记，车带着第二条离站。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task ARepairResumeHeldByAnotherDemandIsReleasedWhenThatDemandIsHandedOff()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(fixture);
            int[] firstSlots = await SlotsOfAsync(fixture, FirstDemandId, SlotOperationType.Load);
            await HandOffAsync(
                fixture, FirstDemandId, SlotOperationType.Load, FirstHandoffRequestId, FirstHandoffActionId, occupiedSlot: firstSlots[0]);
            await ResumeLoadAsync(fixture, SecondDemandId);
            Assert.Equal(JourneyRuntimeStage.Blocked, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
            int checksBefore = await CountAsync(fixture, "PreDepartureSafetyCheck");

            await HandOffAsync(fixture, FirstDemandId, SlotOperationType.Load, RetryRequestId, RetryHandoffActionId);
            await AssertReleasedAsync(fixture, JourneyRuntimeStage.AwaitingLoadResult);
            await fixture.RestoreSessionReadyAsync();
            Exception? first = await RunRoundAsync(fixture);
            Exception? second = await RunRoundAsync(fixture);

            await AssertAskedToLeaveAsync(fixture, checksBefore, first, second);
            Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, SecondDemandId)).Status);
        });
    }

    /// <summary>
    /// 纠错没对上，再交接（REQ-0237 的窗口里）：一个取货站两条都装上了，车在等离站；对第一条开纠错，车报 <c>FAILED</c>，没对上，第一条留在
    /// <c>RecoveryRequired</c>、旅程阻塞在 <c>LoadCorrectionResult_NOT_RECONCILED</c>。这时能做的只有交接（修复续行与补偿都要求那一次操作待恢复，
    /// 而它早已提交）。交接对上之后，修之前旅程永远停在 <c>Blocked</c>；修之后车带着第二条离站。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AHandoffAfterAnUnreconciledCorrectionSendsTheVehicleOnWithTheOther()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
            await LoadBothAtOnePickupAsync(fixture);
            Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
            await FailCorrectionAsync(fixture, FirstDemandId);
            JourneyRuntimeRow blocked = await JourneyOfAsync(fixture, FirstDemandId);
            Assert.Equal((JourneyRuntimeStage.Blocked, "LoadCorrectionResult_NOT_RECONCILED"), (blocked.Stage, blocked.BlockReasonCode));
            Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await DemandOfAsync(fixture, FirstDemandId)).Status);
            int checksBefore = await CountAsync(fixture, "PreDepartureSafetyCheck");

            await HandOffAsync(fixture, FirstDemandId, SlotOperationType.Load);
            await AssertReleasedAsync(fixture, JourneyRuntimeStage.AwaitingLoadResult);
            await fixture.RestoreSessionReadyAsync();
            // 放回取货站之后进离站段，离站等待从那一刻重新计时：等满再走，与装完货离站同一个规矩。
            Exception? first = await RunRoundAsync(fixture);
            fixture.Clock.Advance(TimeSpan.FromSeconds(15));
            Exception? second = await RunRoundAsync(fixture);

            await AssertAskedToLeaveAsync(fixture, checksBefore, first, second);
            Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, SecondDemandId)).Status);
        });
    }

    /// <summary>
    /// 护栏（票面要做 3）：两条都没对上、只结掉一条，旅程仍留在 <c>Blocked</c>。第一条交接没对上；第二条补偿第一次也没对上；第二条第二次补偿对上了，
    /// 被终结——第一条仍是标记，不放出、不发离站核验，管理员还能为第一条重开会话。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task TwoUnreconciledDemandsWithOnlyOneSettledLeaveTheJourneyBlocked()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(fixture);
            int[] firstSlots = await SlotsOfAsync(fixture, FirstDemandId, SlotOperationType.Load);
            await HandOffAsync(
                fixture, FirstDemandId, SlotOperationType.Load, FirstHandoffRequestId, FirstHandoffActionId, occupiedSlot: firstSlots[0]);
            await FailCompensationAsync(fixture, SecondDemandId, FailedCompensationRequestId, FailedCompensationActionId);
            int checksBefore = await CountAsync(fixture, "PreDepartureSafetyCheck");

            await CompensateAsync(fixture, SecondDemandId);
            JourneyRuntimeRow atResult = await JourneyOfAsync(fixture, FirstDemandId);
            await fixture.RestoreSessionReadyAsync();
            Exception? round = await RunRoundAsync(fixture);
            await RunRoundAsync(fixture);

            JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
            int checks = await CountAsync(fixture, "PreDepartureSafetyCheck") - checksBefore;
            Assert.True(
                atResult.Stage == JourneyRuntimeStage.Blocked && after.Stage == JourneyRuntimeStage.Blocked && checks == 0,
                $"The journey was released while the first demand still awaits recovery. at-result={atResult.Stage}/" +
                $"{atResult.BlockReasonCode} after-rounds={after.Stage}/{after.BlockReasonCode} checks={checks} round={round?.Message}");
            await using ControlServerDbContext connection = fixture.OpenConnectionContext();
            await OpenSessionAsync(
                fixture, RecoveryProcessor(fixture, connection), RecoveryConnection(fixture), FirstDemandId, firstSlots, RetryRequestId);
        });
    }

    /// <summary>
    /// 已终结的需求开不出会话（control-server#505 的 A3）：形状同上一格的前半，第二条补偿对上、被终结，旅程因第一条仍阻塞。为第二条开会话被拒
    /// （<c>RECOVERY_DEMAND_NOT_BLOCKED</c>）——它的货在账上已不在车上，会话没有事可做，而它的结果一旦没对上，只能写成不可放行的阻塞。
    /// 第一条照常开得出。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ASessionIsNotOpenedForADemandThatHasEnded()
    {
        await WithProofAsync(async () =>
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(fixture);
            int[] firstSlots = await SlotsOfAsync(fixture, FirstDemandId, SlotOperationType.Load);
            int[] secondSlots = await SlotsOfAsync(fixture, SecondDemandId, SlotOperationType.Load);
            await HandOffAsync(
                fixture, FirstDemandId, SlotOperationType.Load, FirstHandoffRequestId, FirstHandoffActionId, occupiedSlot: firstSlots[0]);
            await CompensateAsync(fixture, SecondDemandId);
            Assert.Equal(JourneyRuntimeStage.Blocked, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
            Assert.Equal(DemandExecutionStatus.Cancelled, (await DemandOfAsync(fixture, SecondDemandId)).Status);

            await using ControlServerDbContext connection = fixture.OpenConnectionContext();
            OnboardMessageProcessor processor = RecoveryProcessor(fixture, connection);
            string refused = await processor.ProcessAsync(
                Envelope(fixture, "ExceptionRecoverySessionRequested", new
                {
                    requestId = EndedDemandRequestId,
                    administrator = BeforeSublotOperator(fixture),
                    administratorRole = "MAINTENANCE_ADMINISTRATOR",
                    eventId = EventId,
                    demandId = SecondDemandId,
                    slots = secondSlots,
                    reason = "The operation cannot be recovered in place.",
                    authenticationProof = Proof
                }),
                RecoveryConnection(fixture),
                token);
            Assert.Equal("ExceptionRecoverySessionRejected", FirstLineType(refused));
            Assert.Equal(
                "RECOVERY_DEMAND_NOT_BLOCKED",
                FirstLinePayload(refused).GetProperty("problem").GetProperty("reasonCode").GetString());
            await OpenSessionAsync(fixture, processor, RecoveryConnection(fixture), FirstDemandId, firstSlots, RetryRequestId);
        });
    }

    /// <summary>
    /// A3 的另一半（独立审查 S2）：已送达（<c>Succeeded</c>）的需求同样开不出会话。旅程阻塞在第二条的装货恢复上，第一条被改库标成已卸送达；
    /// 为第一条开会话被拒，第二条照常开得出。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ASessionIsNotOpenedForADemandAlreadyDelivered()
    {
        await WithProofAsync(async () =>
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(fixture);
            int[] firstSlots = await SlotsOfAsync(fixture, FirstDemandId, SlotOperationType.Load);
            int[] secondSlots = await SlotsOfAsync(fixture, SecondDemandId, SlotOperationType.Load);
            await using (ControlServerDbContext context = fixture.OpenConnectionContext())
            {
                (await context.Set<JourneyDemandRow>().SingleAsync(row => row.DemandId == FirstDemandId, token)).Status =
                    JourneyDemandStatuses.Unloaded;
                (await context.AcceptedDemands.SingleAsync(row => row.DemandId == FirstDemandId, token)).Status =
                    DemandExecutionStatus.Succeeded;
                await context.SaveChangesAsync(token);
            }
            fixture.Context.ChangeTracker.Clear();

            await using ControlServerDbContext connection = fixture.OpenConnectionContext();
            OnboardMessageProcessor processor = RecoveryProcessor(fixture, connection);
            string refused = await processor.ProcessAsync(
                Envelope(fixture, "ExceptionRecoverySessionRequested", new
                {
                    requestId = EndedDemandRequestId,
                    administrator = BeforeSublotOperator(fixture),
                    administratorRole = "MAINTENANCE_ADMINISTRATOR",
                    eventId = EventId,
                    demandId = FirstDemandId,
                    slots = firstSlots,
                    reason = "The operation cannot be recovered in place.",
                    authenticationProof = Proof
                }),
                RecoveryConnection(fixture),
                token);
            Assert.Equal("ExceptionRecoverySessionRejected", FirstLineType(refused));
            Assert.Equal(
                "RECOVERY_DEMAND_NOT_BLOCKED",
                FirstLinePayload(refused).GetProperty("problem").GetProperty("reasonCode").GetString());
            await OpenSessionAsync(fixture, processor, RecoveryConnection(fixture), SecondDemandId, secondSlots, RetryRequestId);
        });
    }

    /// <summary>
    /// 扫码前取消的结果晚到、而那条需求在授权与结果之间已被终结（control-server#505 的 A4）：第二站上第二条在装、第三条还没装，操作员对第三条
    /// 按扫码前的「取消装货」，授权了；第二条结果要恢复、旅程阻塞；第三条在结果到之前被终结（站点期限一类，这里改库）。取消结果报 <c>ALL_EMPTY</c>：
    /// 没下过命令、车证明仓空，旅程不因它多阻塞一层，原来的阻塞码照旧。修之前它被当成落错阶段，写出一个哪条需求都不带标记的
    /// <c>LoadCancellationResult_NOT_RECONCILED</c>。第二格是已送达的那一种终结（扫码前取消的需求真实里不会已送达，改库造出）：它走不到 A4，由更早的「已送达不终结」那道检查（control-server#481）接住，结果同样是旅程照旧——独立审查的变异 R4 在这一半上存活，所以 A4 只认已取消，这一格钉的是那道检查。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    [InlineData(nameof(DemandExecutionStatus.Cancelled), JourneyDemandStatuses.Terminated)]
    [InlineData(nameof(DemandExecutionStatus.Succeeded), JourneyDemandStatuses.Unloaded)]
    public async Task ALateCancellationOfADemandAlreadyEndedLeavesTheBlockAsItWas(string demandStatus, string membershipStatus)
    {
        await WithProofAsync(async () =>
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
            await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
            const string cancellationId = "d4990000-0000-4000-8000-000000000002";
            await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
            {
                string authorization = await RecoveryProcessor(fixture, connection).ProcessAsync(
                    Envelope(fixture, "LoadCancellationStartRequested", new
                    {
                        cancellationId,
                        demandId = ThirdDemandId,
                        slotOperationAttemptId = (string?)null,
                        @operator = BeforeSublotOperator(fixture),
                        reason = "Nothing to load for this demand."
                    }),
                    Connection(fixture),
                    token);
                Assert.Equal("AUTHORIZED", FirstLinePayload(authorization).GetProperty("decision").GetString());
            }
            fixture.Context.ChangeTracker.Clear();
            await BlockOnLoadAsync(fixture, SecondDemandId);
            await using (ControlServerDbContext context = fixture.OpenConnectionContext())
            {
                (await context.AcceptedDemands.SingleAsync(row => row.DemandId == ThirdDemandId, token)).Status =
                    Enum.Parse<DemandExecutionStatus>(demandStatus);
                (await context.Set<JourneyDemandRow>().SingleAsync(row => row.DemandId == ThirdDemandId, token)).Status =
                    membershipStatus;
                await context.SaveChangesAsync(token);
            }
            fixture.Context.ChangeTracker.Clear();

            await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
            {
                Assert.Equal("DurableAck", FirstLineType(await RecoveryProcessor(fixture, connection).ProcessAsync(
                    Envelope(fixture, "LoadCancellationResult", new
                    {
                        cancellationId,
                        demandId = ThirdDemandId,
                        slotOperationAttemptId = (string?)null,
                        overallOutcome = "ALL_EMPTY",
                        slotResults = Array.Empty<object>(),
                        observedAt = fixture.Clock.GetUtcNow()
                    }),
                    Connection(fixture),
                    token)));
            }
            fixture.Context.ChangeTracker.Clear();

            JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
            Assert.Equal((JourneyRuntimeStage.Blocked, "LOAD_RESULT_REQUIRES_RECOVERY"), (after.Stage, after.BlockReasonCode));
            Assert.Equal(Enum.Parse<DemandExecutionStatus>(demandStatus), (await DemandOfAsync(fixture, ThirdDemandId)).Status);
        });
    }

    /// <summary>
    /// 纠错只授权给还在车上的需求（control-server#505 的 A2，cs#287 评论里归本票的那一半）：车在第一站等离站，第一条已装上；改库把它标成不在车上
    /// 之后再请求纠错，被拒，不发纠错命令。修之前授权只看装货已提交、仓位子集与旅程阶段，照样授权。「在车上」是两样一起成立：需求
    /// <c>Accepted</c>、归属 <c>LOADED</c>；前两格各只改其中一样（独立审查 S2），第三格是两样都改的真实形状（卸完货就是这样）。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    [InlineData(nameof(DemandExecutionStatus.Succeeded), JourneyDemandStatuses.Loaded)]
    [InlineData(nameof(DemandExecutionStatus.Accepted), JourneyDemandStatuses.Unloaded)]
    [InlineData(nameof(DemandExecutionStatus.Succeeded), JourneyDemandStatuses.Unloaded)]
    public async Task ACorrectionForADemandNoLongerOnBoardIsRefused(string demandStatus, string membershipStatus)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        await LoadBothAtOnePickupAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        await using (ControlServerDbContext context = fixture.OpenConnectionContext())
        {
            (await context.Set<JourneyDemandRow>().SingleAsync(row => row.DemandId == FirstDemandId, token)).Status =
                membershipStatus;
            (await context.AcceptedDemands.SingleAsync(row => row.DemandId == FirstDemandId, token)).Status =
                Enum.Parse<DemandExecutionStatus>(demandStatus);
            await context.SaveChangesAsync(token);
        }
        fixture.Context.ChangeTracker.Clear();

        StationOperationRow load = await OperationOfAsync(fixture, FirstDemandId, SlotOperationType.Load);
        string answer;
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            answer = await fixture.RequestLoadCorrectionOnConnectionAsync(
                connection, CorrectionId, FirstDemandId, load.SlotOperationAttemptId,
                JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!);
        }
        fixture.Context.ChangeTracker.Clear();

        // An authorized correction has no reply line of its own: its answer is the command it queues.
        Assert.Equal(
            ("LoadCorrectionRejected", 0),
            (answer.Length == 0 ? "(authorized)" : FirstLineType(answer), await CountAsync(fixture, "LoadCorrectionCommand")));
    }

    /// <summary>
    /// 第 2 条「标记」那一支的前一半单独钉住：阻塞码是 <c>*_NOT_RECONCILED</c>，被终结的需求却不带标记，不放。第一条纠错没对上、旅程阻塞后，
    /// 改库把它放回 <c>Accepted</c>——标记已不在而码还在，正是旧版本留下的无标记行的样子。交接掉第二条（它从没待恢复过，装货早已提交）：
    /// 旅程仍阻塞。若「终结的那条带着标记」不判，第 3、4 条都成立，车就带着纠错没对上的第一条走了。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AHandoffOfADemandThatCarriedNoMarkDoesNotLiftAnUnreconciledBlock()
    {
        await WithProofAsync(async () =>
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
            await LoadBothAtOnePickupAsync(fixture);
            await FailCorrectionAsync(fixture, FirstDemandId);
            await using (ControlServerDbContext context = fixture.OpenConnectionContext())
            {
                (await context.AcceptedDemands.SingleAsync(row => row.DemandId == FirstDemandId, token)).Status =
                    DemandExecutionStatus.Accepted;
                await context.SaveChangesAsync(token);
            }
            fixture.Context.ChangeTracker.Clear();

            await HandOffAsync(fixture, SecondDemandId, SlotOperationType.Load);

            JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
            Assert.Equal((JourneyRuntimeStage.Blocked, "LoadCorrectionResult_NOT_RECONCILED"), (after.Stage, after.BlockReasonCode));
        });
    }

    /// <summary>
    /// 第 2 条「标记」那一支的后一半单独钉住：被终结的需求带着标记，阻塞码却不是「结果没对上」，不放。形状是停住的自有订单重建转交给会话
    /// （<c>OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF</c>）：交接第一次没对上之后人再转一次交接，普通的 <c>*_NOT_RECONCILED</c> 被这个码盖掉，
    /// 需求仍待恢复。那一趟的出口是交接之后由人放弃剩下的（<c>TERMINATE_STOPPED_TRIP</c>），不是放回引擎接着走——它的单已经停了。改库造出。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AHandoffOfADemandAwaitingRecoveryDoesNotLiftABlockThatNoResultLeft()
    {
        await WithProofAsync(async () =>
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            JourneyRuntimeRow runtime = await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
            await AddInboxAsync(
                fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));
            await TickAndRunAsync(fixture);
            await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
            await TickAndRunAsync(fixture);
            const string otherBlock = ControlServer.Host.Runtime.Faults.VehicleFaultRecoveryService.AwaitingCargoHandoffReason;
            await using (ControlServerDbContext context = fixture.OpenConnectionContext())
            {
                JourneyRuntimeRow row = await context.JourneyRuntimes.SingleAsync(item => item.JourneyId == runtime.JourneyId, token);
                row.Stage = JourneyRuntimeStage.Blocked;
                row.SetBlockReason(otherBlock, fixture.Clock.GetUtcNow());
                (await context.AcceptedDemands.SingleAsync(item => item.DemandId == FirstDemandId, token)).Status =
                    DemandExecutionStatus.RecoveryRequired;
                await context.SaveChangesAsync(token);
            }
            fixture.Context.ChangeTracker.Clear();

            await HandOffAsync(fixture, FirstDemandId, SlotOperationType.Load);

            JourneyRuntimeRow after = await JourneyOfAsync(fixture, SecondDemandId);
            Assert.Equal((JourneyRuntimeStage.Blocked, otherBlock), (after.Stage, after.BlockReasonCode));
        });
    }

    /// <summary>看板给不可放行的两种后缀各一句说明，五种结果共用；普通的 <c>*_NOT_RECONCILED</c> 不归它们。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public void TheDashboardDescribesEveryUnreleasableCode()
    {
        foreach (string messageType in new[]
                 {
                     "LoadCancellationResult", "LoadCompensationResult", "FaultCargoRecoveryResult", "LoadCorrectionResult",
                     "ForcedMechanicalRecoveryResult"
                 })
        {
            Assert.Equal(
                BlockedJourneysQueryEndpoint.UnreleasableNotReconciledDescription,
                BlockedJourneysQueryEndpoint.DescribeByFamily(messageType + "_NOT_RECONCILED_ON_ENDED_DEMAND"));
            Assert.Equal(
                BlockedJourneysQueryEndpoint.NotReconciledBeforeUpgradeDescription,
                BlockedJourneysQueryEndpoint.DescribeByFamily(messageType + "_NOT_RECONCILED_BEFORE_UPGRADE"));
            Assert.Null(BlockedJourneysQueryEndpoint.DescribeByFamily(messageType + "_NOT_RECONCILED"));
        }
        foreach (string description in new[]
                 {
                     BlockedJourneysQueryEndpoint.UnreleasableNotReconciledDescription,
                     BlockedJourneysQueryEndpoint.NotReconciledBeforeUpgradeDescription
                 })
        {
            Assert.DoesNotContain("#", description);
            Assert.DoesNotContain("调度", description);
        }
    }

    /// <summary>一个取货站两条都装上：车停在等离站（调用方先把离站等待调长，否则装完就走）。</summary>
    private static async Task LoadBothAtOnePickupAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow runtime = await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));
        await TickAndRunAsync(fixture);
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        await EnterSublotAsync(fixture, FirstDemandId, SecondSublot, SecondSubmissionId);
        await ApplySafeResultAsync(fixture, SecondDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
    }

    /// <summary>对 <paramref name="demandId"/> 开一条纠错并授权，车报 <c>FAILED</c>、仓位状态读不出：没对上。</summary>
    private static async Task FailCorrectionAsync(RuntimeFixture fixture, string demandId)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        StationOperationRow load = await OperationOfAsync(fixture, demandId, SlotOperationType.Load);
        int[] slots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!;
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            await fixture.RequestLoadCorrectionOnConnectionAsync(connection, CorrectionId, demandId, load.SlotOperationAttemptId, slots);
        }
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(1, await CountAsync(fixture, "LoadCorrectionCommand"));
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            Assert.Equal("DurableAck", FirstLineType(await RecoveryProcessor(fixture, connection).ProcessAsync(
                Envelope(fixture, "LoadCorrectionResult", new
                {
                    correctionId = CorrectionId,
                    demandId,
                    slotOperationAttemptId = load.SlotOperationAttemptId,
                    overallOutcome = "FAILED",
                    slotResults = UnknownSlots(slots, "FAILED"),
                    observedAt = fixture.Clock.GetUtcNow()
                }),
                RecoveryConnection(fixture),
                token)));
        }
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>一次没对上的装货补偿：开会话、选补偿、车请求授权、车报 <c>UNKNOWN</c>（仓位状态读不出）。需求留在 <c>RecoveryRequired</c>。</summary>
    private static async Task FailCompensationAsync(RuntimeFixture fixture, string demandId, string requestId, string actionId)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        StationOperationRow load = await OperationOfAsync(fixture, demandId, SlotOperationType.Load);
        int[] slots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!;
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            OnboardMessageProcessor processor = RecoveryProcessor(fixture, connection);
            OnboardConnectionState state = RecoveryConnection(fixture);
            string sessionId = await OpenSessionAsync(fixture, processor, state, demandId, slots, requestId);
            await processor.ProcessAsync(
                Action(fixture, sessionId, "COMPENSATE_LOAD_ALL_EMPTY", demandId, slots, actionId), state, token);
            await processor.ProcessAsync(Envelope(fixture, "LoadCompensationRequested", new
            {
                recoveryActionId = actionId,
                exceptionRecoverySessionId = sessionId,
                demandId,
                slotOperationAttemptId = load.SlotOperationAttemptId,
                @operator = BeforeSublotOperator(fixture)
            }), state, token);
            Assert.Equal("DurableAck", FirstLineType(await processor.ProcessAsync(Envelope(fixture, "LoadCompensationResult", new
            {
                recoveryActionId = actionId,
                demandId,
                slotOperationAttemptId = load.SlotOperationAttemptId,
                overallOutcome = "UNKNOWN",
                slotResults = UnknownSlots(slots, "UNKNOWN"),
                observedAt = fixture.Clock.GetUtcNow()
            }), state, token)));
        }
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await DemandOfAsync(fixture, demandId)).Status);
    }

    private static object[] UnknownSlots(int[] slots, string outcome) =>
    [
        .. slots.Select(slot => new
        {
            slotNo = slot,
            outcome,
            finalPhysicalState = "UNKNOWN",
            lockState = "UNKNOWN",
            unlockOutputState = "UNKNOWN",
            reasonCodes = UnknownReasonCodes
        })
    ];
}
