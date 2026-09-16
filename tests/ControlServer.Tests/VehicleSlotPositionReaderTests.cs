using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Tests;

/// <summary>
/// 车辆的仓位分组取服务端权威、不取车报：先看生效配置，再看最新已发布 IO 绑定，都没有就是未解析，
/// 不回退到默认模型（program#70 定案 4，control-server#66）。
/// </summary>
public sealed class VehicleSlotPositionReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WithOnlyAnActiveConfigurationTheVehicleReadsTheModelThatConfigurationReferences()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        SlotModelVersionRow threeSlot = await PublishModelAsync(fixture, "three-slot", "FRONT", "FRONT", "REAR");
        await ActivateAsync(fixture, "AGV-02", threeSlot);

        VehicleSlotPositions positions = Assert.IsType<VehicleSlotPositions>(
            await fixture.SlotPositions.ReadAsync("AGV-02", TestContext.Current.CancellationToken));

        Assert.Equal(VehicleSlotPositionSource.ActiveSlotConfiguration, positions.Source);
        Assert.Equal(threeSlot.SlotModelVersionId, positions.SlotModelVersionId);
        Assert.Equal(
            new Dictionary<int, string> { [1] = "FRONT", [2] = "FRONT", [3] = "REAR" },
            positions.SlotPositionByPhysicalSlot);
        Assert.Equal(new Dictionary<string, int> { ["FRONT"] = 2, ["REAR"] = 1 }, positions.PhysicalSlotCountByGroup);
    }

    [Fact]
    public async Task WithOnlyPublishedIoBindingsTheVehicleReadsTheModelOfItsMostRecentBinding()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        SlotModelVersionRow older = await PublishModelAsync(fixture, "older", "REAR", "REAR");
        SlotModelVersionRow newer = await PublishModelAsync(fixture, "newer", "FRONT");
        // The older model is rebound once (its binding version 2) before the car moves to the newer model
        // (binding version 1 of that model). Binding versions count per car and model, so the most recent
        // binding is the one bound last, not the one with the larger number.
        await BindAsync(fixture, "AGV-03", older, Now);
        await BindAsync(fixture, "AGV-03", older, Now.AddHours(1));
        await BindAsync(fixture, "AGV-03", newer, Now.AddHours(2));

        VehicleSlotPositions positions = Assert.IsType<VehicleSlotPositions>(
            await fixture.SlotPositions.ReadAsync("AGV-03", TestContext.Current.CancellationToken));

        Assert.Equal(VehicleSlotPositionSource.LatestPublishedIoBinding, positions.Source);
        Assert.Equal(newer.SlotModelVersionId, positions.SlotModelVersionId);
        Assert.Equal(new Dictionary<int, string> { [1] = "FRONT" }, positions.SlotPositionByPhysicalSlot);
    }

    [Fact]
    public async Task TwoModelsBoundAtTheSameInstantLeaveTheVehicleUnresolvedRatherThanPickingTheLargerVersion()
    {
        // 同一时刻绑了两个车型：车型 A 的第二版与车型 B 的第一版共用同一个 CreatedAt。版本号按（车，车型）
        // 各自从 1 数起，所以「2 比 1 大」不代表 A 比 B 新——它们数的不是同一条线。这种并列没有正确答案，
        // 只有一个安全答案：未解析，由调用方 fail-closed。CreatedAt 本身是调用方传进来的 occurredAt，
        // 不是写入顺序，所以也不能靠写入先后兜底。
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        SlotModelVersionRow modelA = await PublishModelAsync(fixture, "tie-a", "REAR", "REAR");
        SlotModelVersionRow modelB = await PublishModelAsync(fixture, "tie-b", "FRONT");
        await BindAsync(fixture, "AGV-03", modelA, Now);
        await BindAsync(fixture, "AGV-03", modelA, Now.AddHours(1));
        await BindAsync(fixture, "AGV-03", modelB, Now.AddHours(1));

        Assert.Null(await fixture.SlotPositions.ReadAsync("AGV-03", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TwoBindingsOfOneModelAtTheSameInstantStillResolveToThatModel()
    {
        // 并列只在跨车型时才是歧义。同一个车型下并列的两版指的是同一个车型，没有什么要挑的——一次批量
        // 导入用同一个时间戳发两版绑定不该把车变成未解析。
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        SlotModelVersionRow model = await PublishModelAsync(fixture, "same-instant", "FRONT", "REAR");
        await BindAsync(fixture, "AGV-03", model, Now);
        await BindAsync(fixture, "AGV-03", model, Now);

        VehicleSlotPositions positions = Assert.IsType<VehicleSlotPositions>(
            await fixture.SlotPositions.ReadAsync("AGV-03", TestContext.Current.CancellationToken));

        Assert.Equal(VehicleSlotPositionSource.LatestPublishedIoBinding, positions.Source);
        Assert.Equal(model.SlotModelVersionId, positions.SlotModelVersionId);
    }

    [Fact]
    public async Task WhenTheActiveConfigurationAndTheLatestBindingDisagreeTheActiveConfigurationWins()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        SlotModelVersionRow active = await PublishModelAsync(fixture, "active", "FRONT", "REAR");
        SlotModelVersionRow bound = await PublishModelAsync(fixture, "bound-later", "REAR", "REAR", "REAR");
        await ActivateAsync(fixture, "AGV-02", active);
        await BindAsync(fixture, "AGV-02", bound, Now.AddDays(1));

        VehicleSlotPositions positions = Assert.IsType<VehicleSlotPositions>(
            await fixture.SlotPositions.ReadAsync("AGV-02", TestContext.Current.CancellationToken));

        Assert.Equal(VehicleSlotPositionSource.ActiveSlotConfiguration, positions.Source);
        Assert.Equal(active.SlotModelVersionId, positions.SlotModelVersionId);
    }

    [Fact]
    public async Task WithNeitherRecordTheVehicleIsUnresolvedEvenThoughTheApprovedEightSlotModelExists()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        SlotModelVersionRow eightSlot = await fixture.SlotAuthority.EnsureApprovedHardwareFactsAsync(
            Now, TestContext.Current.CancellationToken);
        await BindAsync(fixture, "AGV-01", eightSlot, Now);

        // AGV-02 has no active configuration and no binding. The approved model sits right there, and is
        // still not a default: guessing a car's model is how a different model would go wrong silently.
        Assert.Null(await fixture.SlotPositions.ReadAsync("AGV-02", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnActiveConfigurationPointingAtAModelWithNoSlotsIsUnresolvedRatherThanEmpty()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        await ActivateAsync(fixture, "AGV-02", slotModelVersionId: "model-that-does-not-exist");

        Assert.Null(await fixture.SlotPositions.ReadAsync("AGV-02", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AVehicleEnrolledWithTheApprovedFactsAndBoundReadsOneToFourFrontAndFiveToEightRear()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        SlotModelVersionRow eightSlot = await fixture.SlotAuthority.EnsureApprovedHardwareFactsAsync(
            Now, TestContext.Current.CancellationToken);
        await BindAsync(fixture, "AGV-01", eightSlot, Now);

        VehicleSlotPositions positions = Assert.IsType<VehicleSlotPositions>(
            await fixture.SlotPositions.ReadAsync("AGV-01", TestContext.Current.CancellationToken));

        Assert.Equal(
            new Dictionary<int, string>
            {
                [1] = "FRONT",
                [2] = "FRONT",
                [3] = "FRONT",
                [4] = "FRONT",
                [5] = "REAR",
                [6] = "REAR",
                [7] = "REAR",
                [8] = "REAR",
            },
            positions.SlotPositionByPhysicalSlot);
        Assert.Equal(new Dictionary<string, int> { ["FRONT"] = 4, ["REAR"] = 4 }, positions.PhysicalSlotCountByGroup);
    }

    [Fact]
    public async Task TheLargestGroupAcrossTheMapSkipsUnresolvedVehiclesAndNamesThem()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        SlotModelVersionRow eightSlot = await fixture.SlotAuthority.EnsureApprovedHardwareFactsAsync(
            Now, TestContext.Current.CancellationToken);
        await BindAsync(fixture, "AGV-01", eightSlot, Now);
        SlotModelVersionRow sixFront = await PublishModelAsync(
            fixture, "six-front", "FRONT", "FRONT", "FRONT", "FRONT", "FRONT", "FRONT");
        await ActivateAsync(fixture, "AGV-02", sixFront);
        string[] map = ["AGV-01", "AGV-02", "AGV-03", "AGV-04"];

        SlotPositionGroupCapacity front = await fixture.SlotPositions.ReadLargestGroupCapacityAsync(
            map, "FRONT", TestContext.Current.CancellationToken);
        SlotPositionGroupCapacity rear = await fixture.SlotPositions.ReadLargestGroupCapacityAsync(
            map, "REAR", TestContext.Current.CancellationToken);
        SlotPositionGroupCapacity nobody = await fixture.SlotPositions.ReadLargestGroupCapacityAsync(
            ["AGV-03", "AGV-04"], "FRONT", TestContext.Current.CancellationToken);

        Assert.Equal("FRONT", front.SlotPosition);
        Assert.Equal(6, front.LargestPhysicalSlotCount);
        Assert.Equal(["AGV-03", "AGV-04"], front.UnresolvedAgvIds);
        // AGV-02 has no rear slot at all; AGV-01 has four.
        Assert.Equal(4, rear.LargestPhysicalSlotCount);
        Assert.Equal(["AGV-03", "AGV-04"], rear.UnresolvedAgvIds);
        Assert.Equal(0, nobody.LargestPhysicalSlotCount);
        Assert.Equal(["AGV-03", "AGV-04"], nobody.UnresolvedAgvIds);
    }

    private static async Task<SlotModelVersionRow> PublishModelAsync(
        AreaAssignmentPersistenceFixture fixture,
        string modelKey,
        params string[] positionsBySlot)
    {
        SlotTemplateRow template = await fixture.SlotAuthority.PublishTemplateVersionAsync(
            $"{modelKey}-template",
            new SlotTemplateSpecification(600, 400, 300, ["PDFN5"]),
            Now,
            TestContext.Current.CancellationToken);
        return await fixture.SlotAuthority.PublishModelVersionAsync(
            modelKey,
            [
                .. positionsBySlot.Select((position, index) => new SlotModelSlotSpecification(
                    index + 1, position, template.TemplateKey, template.Version))
            ],
            Now,
            TestContext.Current.CancellationToken);
    }

    private static Task<IReadOnlyList<SlotIoBindingRow>> BindAsync(
        AreaAssignmentPersistenceFixture fixture,
        string agvId,
        SlotModelVersionRow model,
        DateTimeOffset boundAt) =>
        fixture.SlotAuthority.PublishIoBindingsAsync(
            agvId,
            model.SlotModelVersionId,
            [.. ApprovedSlotHardwareFacts.IoBindings.Take(model.SlotCount)],
            boundAt,
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Writes the active-configuration row directly. The real path is an activation handshake with the
    /// onboard, which is not what these tests are about; the reader only reads what that path leaves behind.
    /// </summary>
    private static Task ActivateAsync(AreaAssignmentPersistenceFixture fixture, string agvId, SlotModelVersionRow model) =>
        ActivateAsync(fixture, agvId, model.SlotModelVersionId);

    private static async Task ActivateAsync(
        AreaAssignmentPersistenceFixture fixture,
        string agvId,
        string slotModelVersionId)
    {
        fixture.Context.Set<ActiveSlotConfigurationRow>().Add(new ActiveSlotConfigurationRow
        {
            AgvId = agvId,
            SlotModelVersionId = slotModelVersionId,
            ConfigurationVersion = 1,
            Fingerprint = $"fingerprint-{agvId}",
            ActivatedAt = Now,
            ActivationId = $"activation-{agvId}",
            SnapshotId = $"snapshot-{agvId}"
        });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();
    }
}
