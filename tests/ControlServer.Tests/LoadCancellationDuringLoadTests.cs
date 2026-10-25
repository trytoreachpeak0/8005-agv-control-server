using System.Text.Json;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.RemainingBasketsSkipTests;

namespace ControlServer.Tests;

/// <summary>
/// 装货开始之后的取消（修订后的 REQ-0211，CP-0021，协议 6.0.0 候选，control-server#837）：服务端只在全部目标仓都空、门都证明锁闭、开锁输出都复位时
/// 授权；装进一篮、有门没锁好、读数不明都拒，按 wire-notes 的先后 <c>SLOT_STATE_UNKNOWN</c> → <c>LOAD_CANCELLATION_BASKET_LOADED</c> →
/// <c>LOCK_NOT_CLOSED</c> → <c>UNLOCK_OUTPUT_NOT_RESET</c>，第一个不过的决定。两种决定都带空 <c>slots</c>；拒绝不改任何状态。
/// </summary>
/// <remarks>
/// 10-21 agv03 @160：5 号仓已装进一篮，操作员按取消装货，服务端授权了；车报 <c>UNKNOWN</c>，旅程挂在 <c>Blocked</c> 没有出口。判定读的是服务端手上的
/// 逐仓读数（与减篮同一读法：本会话、装货开始之后最新的 <c>SafetyStateSnapshot</c>，之后有目标仓变过就算过期）。
/// </remarks>
public sealed class LoadCancellationDuringLoadTests
{
    private const string CancellationId = "83700000-0000-4000-8000-000000000001";
    private const string SecondCancellationId = "83700000-0000-4000-8000-000000000002";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>一篮未装、门都锁闭、输出都复位，读数新鲜：授权，范围为空，开出取消工作流。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task EveryTargetSlotEmptyLockedAndResetIsAuthorizedWithAnEmptyScope()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied());

        JsonElement authorization = Payload(await SendCancellationAsync(rig, CancellationId));

        Assert.Equal("AUTHORIZED", authorization.GetProperty("decision").GetString());
        Assert.Empty(authorization.GetProperty("slots").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, authorization.GetProperty("problem").ValueKind);
        Assert.Single(await rig.Fixture.Context.RecoveryWorkflows.AsNoTracking()
            .Where(row => row.WorkflowId == CancellationId).ToArrayAsync(Token));
    }

    /// <summary>
    /// 10-21 @160 的形状：一个目标仓已经安全闭环有货。拒，<c>LOAD_CANCELLATION_BASKET_LOADED</c>，范围为空；不开工作流、装货照旧在进行。
    /// 去掉新判定，这条变红。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BASKET-LOADED")]
    public async Task ACancellationOnceABasketIsLoadedIsRejectedAndChangesNothing()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied(1));

        AssertRejected(await SendCancellationAsync(rig, CancellationId), "LOAD_CANCELLATION_BASKET_LOADED");

        Assert.Empty(await rig.Fixture.Context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(Token));
        await rig.AssertLoadUntouchedAsync();
    }

    /// <summary>门没证明锁闭、开锁输出没复位（仓都空）：各自的码拒，状态不变。</summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BASKET-LOADED")]
    [InlineData("UNLOCKED", "RESET", "LOCK_NOT_CLOSED")]
    [InlineData("LOCKED", "ACTIVE", "UNLOCK_OUTPUT_NOT_RESET")]
    public async Task ADoorNotProvenLockedOrAnOutputNotResetIsRejected(string lockState, string output, string reasonCode)
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        SlotReading[] readings = Occupied();
        readings[1] = readings[1] with { Lock = lockState, Output = output };
        await rig.SendSnapshotAsync(readings);

        AssertRejected(await SendCancellationAsync(rig, CancellationId), reasonCode);

        Assert.Empty(await rig.Fixture.Context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(Token));
        await rig.AssertLoadUntouchedAsync();
    }

    /// <summary>
    /// 读数不明从严：一份快照都没有、快照之后有目标仓变过（过期）、目标仓读到 <c>UNKNOWN</c>，都按 <c>SLOT_STATE_UNKNOWN</c> 拒，并在应答之后要一份
    /// 快照，好让操作员再按一次时读数是新的。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BASKET-LOADED")]
    [InlineData("none")]
    [InlineData("stale")]
    [InlineData("unknown")]
    public async Task UnprovenReadingsAreRejectedAsSlotStateUnknownAndASnapshotIsAskedFor(string shape)
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        if (shape == "none")
        {
            // The rig's handshake left an all-empty snapshot on file, taken at the moment the load began.
            await rig.Fixture.Context.ProtocolInbox.Where(row => row.MessageType == "SafetyStateSnapshot")
                .ExecuteDeleteAsync(Token);
        }
        else if (shape == "stale")
        {
            await rig.SendSnapshotAsync(Occupied());
            await rig.SendSafetyChangedAsync(1, 2, 3, 4, 5, 6, 7, 8);
            rig.Fixture.Clock.Advance(OnboardMessageProcessor.SafetySnapshotReaskInterval + TimeSpan.FromSeconds(1));
        }
        else if (shape == "unknown")
        {
            SlotReading[] readings = Occupied();
            readings[2] = readings[2] with { Physical = "UNKNOWN" };
            await rig.SendSnapshotAsync(readings);
        }

        string answer = await SendCancellationAsync(rig, CancellationId);

        AssertRejected(answer, "SLOT_STATE_UNKNOWN");
        if (shape != "unknown")
        {
            Assert.Contains(answer.Split('\n'), line => line.Length > 0 && MessageType(line) == "SafetyStateSnapshotRequested");
        }
        Assert.Empty(await rig.Fixture.Context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(Token));
        await rig.AssertLoadUntouchedAsync();
    }

    /// <summary>
    /// 先后照 wire-notes：读数不明压过有货，有货压过门没锁、输出没复位。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BASKET-LOADED")]
    [InlineData("unknown-and-loaded", "SLOT_STATE_UNKNOWN")]
    [InlineData("loaded-and-unlocked", "LOAD_CANCELLATION_BASKET_LOADED")]
    [InlineData("unlocked-and-active", "LOCK_NOT_CLOSED")]
    public async Task TheFirstFailingCheckInTheProtocolsOrderDecides(string shape, string reasonCode)
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        SlotReading[] readings = Occupied();
        switch (shape)
        {
            case "unknown-and-loaded":
                readings = Occupied(1);
                readings[1] = readings[1] with { Lock = "UNKNOWN" };
                break;
            case "loaded-and-unlocked":
                readings = Occupied(2);
                readings[0] = readings[0] with { Lock = "UNLOCKED" };
                break;
            case "unlocked-and-active":
                readings[0] = readings[0] with { Output = "ACTIVE" };
                readings[2] = readings[2] with { Lock = "UNLOCKED" };
                break;
        }
        await rig.SendSnapshotAsync(readings);

        AssertRejected(await SendCancellationAsync(rig, CancellationId), reasonCode);
    }

    /// <summary>
    /// 只看这次装货的目标仓：别的仓有货（前一单装的）不挡这一单的取消。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ABasketInASlotOutsideTheTargetsDoesNotHoldTheCancellation()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        int outside = Enumerable.Range(1, 8).First(slot => !rig.TargetSlots.Contains(slot));
        await rig.SendSnapshotAsync(Occupied(outside));

        Assert.Equal("AUTHORIZED", Payload(await SendCancellationAsync(rig, CancellationId)).GetProperty("decision").GetString());
    }

    /// <summary>
    /// 被拒之后照常装下去，门关好、读数到了再按一次（新的 <c>cancellationId</c>）：授权。拒绝没留下任何东西挡它。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BASKET-LOADED")]
    public async Task AfterARejectionALaterPressWithProvenReadingsIsAuthorized()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        SlotReading[] open = Occupied();
        open[0] = open[0] with { Lock = "UNLOCKED", Output = "ACTIVE" };
        await rig.SendSnapshotAsync(open);
        AssertRejected(await SendCancellationAsync(rig, CancellationId), "LOCK_NOT_CLOSED");

        await rig.SendSafetyChangedAsync(1, 2, 3, 4, 5, 6, 7, 8);
        await rig.SendSnapshotAsync(Occupied());

        Assert.Equal("AUTHORIZED", Payload(await SendCancellationAsync(rig, SecondCancellationId)).GetProperty("decision").GetString());
    }

    /// <summary>
    /// 授权答复在路上丢了，车以新的 messageId 重提同一个 <c>cancellationId</c>（real-onboard-cancellation-authorization-lost 的形状）：照记录答
    /// 同一个授权，即便此后又有目标仓的变化让读数过期——授权之后车不为取消做任何仓位 IO，过期的读数说明不了授权当时不该给。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AResubmissionOfAnAuthorizedCancellationIsAnsweredFromTheRecord()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied());
        Assert.Equal("AUTHORIZED", Payload(await SendCancellationAsync(rig, CancellationId)).GetProperty("decision").GetString());

        await rig.SendSafetyChangedAsync(1, 2, 3, 4, 5, 6, 7, 8);

        Assert.Equal("AUTHORIZED", Payload(await SendCancellationAsync(rig, CancellationId)).GetProperty("decision").GetString());
        Assert.Single(await rig.Fixture.Context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>故障判定在先：照旧 <c>ACTION_NOT_ALLOWED_IN_STATE</c>，不看读数（有货也一样）。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BASKET-LOADED")]
    public async Task ASlotFaultDeclarationStillRefusesFirst()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied(1));
        await rig.DeclareSlotFaultAsync(rig.TargetSlots[0]);

        AssertRejected(await SendCancellationAsync(rig, CancellationId), "ACTION_NOT_ALLOWED_IN_STATE");
    }

    /// <summary>
    /// 装货进行中车报了目标仓的变化（开门、关门、放篮）：服务端应答之后要一份快照，这样操作员接着按取消时读数已经是新的，不必先被拒一次。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ASafetyChangeDuringALoadAsksForASnapshot()
    {
        await using SkipRig rig = await SkipRig.AtLoadAsync(boxes: 12);
        await rig.SendSnapshotAsync(Occupied());

        string answer = await rig.SendSafetyChangedAsync(1, 2, 3, 4, 5, 6, 7, 8);

        Assert.Contains(answer.Split('\n'), line => line.Length > 0 && MessageType(line) == "SafetyStateSnapshotRequested");
    }

    private static async Task<string> SendCancellationAsync(SkipRig rig, string cancellationId) =>
        await rig.SendAsync(rig.Envelope(Guid.NewGuid().ToString("D"), "LoadCancellationStartRequested", new
        {
            cancellationId,
            demandId = "10000000-0000-4000-8000-000000000001",
            slotOperationAttemptId = rig.AttemptId,
            @operator = new { operatorId = "OP-001", verificationMethod = "BADGE", verifiedAt = Now },
            reason = "Operator pressed cancel."
        }));

    private static JsonElement Payload(string answer)
    {
        using JsonDocument document = JsonDocument.Parse(answer.Split('\n')[0]);
        Assert.Equal("LoadCancellationAuthorization", document.RootElement.GetProperty("messageType").GetString());
        return document.RootElement.GetProperty("payload").Clone();
    }

    private static void AssertRejected(string answer, string reasonCode)
    {
        JsonElement payload = Payload(answer);
        Assert.Equal("REJECTED", payload.GetProperty("decision").GetString());
        Assert.Equal(reasonCode, payload.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Empty(payload.GetProperty("slots").EnumerateArray());
    }
}
