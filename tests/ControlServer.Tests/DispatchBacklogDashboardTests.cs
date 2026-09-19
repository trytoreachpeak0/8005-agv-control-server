using System.Text.Json;
using ControlServer.Application;
using ControlServer.Dashboard;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Tests;

/// <summary>
/// 看板的派车积压卡片（control-server#70）：受理前的正常积压与未清除的结构性派车阻断，分两组显示；
/// 未映射 AREA 静默；原因码说明取服务端码表，未登记的原样显示。
/// </summary>
/// <remarks>
/// 结构性阻断行由 control-server#74 写；它合入前这里用 <see cref="StructuralDispatchBlockStore"/> 预置测试行。
/// 库由迁移建出，证明本票不需要 migration。
/// </remarks>
public sealed class DispatchBacklogDashboardTests
{
    private const string UnknownReason = "SOME_REASON_NOBODY_REGISTERED";

    private static readonly string[] SlotGroupReasons =
    [
        DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable,
        DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup,
        DispatchReasonCodes.VehicleSlotModelUnresolved,
        DispatchReasonCodes.AreaSlotGroupNotAssigned,
    ];

    [Fact]
    public async Task TheEndpointReturnsWaitingBacklogAndUnclearedStructuralBlocksAsTwoSeparateGroups()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        DateTimeOffset firstSeen = DateTimeOffset.UtcNow.AddMinutes(-90);
        AddBacklog(fixture, "D-WAIT", DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable, firstSeen);
        AddBacklog(fixture, "D-DONE", "ACCEPTED", firstSeen, acceptedAt: firstSeen.AddMinutes(1));
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await fixture.Blocks.RaiseOrRefreshAsync(
            "D-BIG", DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup, "TDK-D-BIG", """{"expected":6}""",
            firstSeen, TestContext.Current.CancellationToken);
        await fixture.Blocks.RaiseOrRefreshAsync(
            "D-BIG", DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup, "TDK-D-BIG", """{"expected":6}""",
            firstSeen.AddMinutes(30), TestContext.Current.CancellationToken);

        using JsonDocument fact = await ReadAsync(fixture);

        JsonElement waiting = Assert.Single(fact.RootElement.GetProperty("backlog").EnumerateArray());
        Assert.Equal("D-WAIT", waiting.GetProperty("demandId").GetString());
        Assert.Equal(
            DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable, waiting.GetProperty("reasonCode").GetString());
        Assert.Equal(firstSeen, waiting.GetProperty("firstSeenAt").GetDateTimeOffset());
        long waitingSeconds = waiting.GetProperty("waitingSeconds").GetInt64();
        Assert.InRange(waitingSeconds, 90 * 60, (90 * 60) + 60);

        // 受理过的需求不再是积压；结构性阻断不混进积压列表，积压也不混进阻断。
        JsonElement block = Assert.Single(fact.RootElement.GetProperty("structuralBlocks").EnumerateArray());
        Assert.Equal("D-BIG", block.GetProperty("demandId").GetString());
        Assert.Equal(
            DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup, block.GetProperty("reasonCode").GetString());
        Assert.Equal(firstSeen, block.GetProperty("firstRaisedAt").GetDateTimeOffset());
        Assert.Equal(firstSeen.AddMinutes(30), block.GetProperty("lastSeenAt").GetDateTimeOffset());

        string html = new DispatchBacklogCard().RenderFact(fact.RootElement);
        int blocksAt = html.IndexOf("structural-dispatch-blocks alarm", StringComparison.Ordinal);
        int backlogAt = html.IndexOf("class=\"dispatch-backlog\"", StringComparison.Ordinal);
        Assert.True(blocksAt >= 0 && backlogAt > blocksAt, html);
        Assert.Contains("TDK-D-BIG", html[blocksAt..backlogAt], StringComparison.Ordinal);
        Assert.DoesNotContain("TDK-D-WAIT", html[blocksAt..backlogAt], StringComparison.Ordinal);
        Assert.Contains("TDK-D-WAIT", html[backlogAt..], StringComparison.Ordinal);
        Assert.DoesNotContain("TDK-D-BIG", html[backlogAt..], StringComparison.Ordinal);
        Assert.Contains("1 小时 30 分", html[backlogAt..], StringComparison.Ordinal);
        Assert.DoesNotContain("D-DONE", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOutOfScopeAreaDemandIsNotListedAndNeverRenderedAsAnAlarm()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        DateTimeOffset firstSeen = DateTimeOffset.UtcNow.AddMinutes(-5);
        AddBacklog(fixture, "D-EUTECTIC-1", DispatchReasonCodes.OutOfScopeArea, firstSeen);
        AddBacklog(fixture, "D-EUTECTIC-2", DispatchReasonCodes.OutOfScopeArea, firstSeen);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using JsonDocument fact = await ReadAsync(fixture);

        Assert.Empty(fact.RootElement.GetProperty("backlog").EnumerateArray());
        Assert.Empty(fact.RootElement.GetProperty("structuralBlocks").EnumerateArray());
        Assert.Equal(2, fact.RootElement.GetProperty("silentBacklogCount").GetInt32());

        string html = new DispatchBacklogCard().RenderFact(fact.RootElement);
        Assert.DoesNotContain("D-EUTECTIC", html, StringComparison.Ordinal);
        Assert.DoesNotContain(DispatchReasonCodes.OutOfScopeArea, html, StringComparison.Ordinal);
        Assert.DoesNotContain("alarm", html, StringComparison.Ordinal);
        // 只在折叠处给计数。
        Assert.Contains("<details class=\"dispatch-backlog-silent\"><summary>未映射 AREA 的需求：2 条", html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADemandNoLongerInTheCatalogIsNeitherWaitingNorCountedAsSilent()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        DateTimeOffset firstSeen = DateTimeOffset.UtcNow.AddMinutes(-5);
        AddBacklog(fixture, "D-LEFT", DispatchReasonCodes.DemandLeftCatalog, firstSeen);
        AddBacklog(fixture, "D-EUTECTIC", DispatchReasonCodes.OutOfScopeArea, firstSeen);
        AddBacklog(fixture, "D-WAITING", DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable, firstSeen);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using JsonDocument fact = await ReadAsync(fixture);

        Assert.Equal(
            ["D-WAITING"],
            fact.RootElement.GetProperty("backlog").EnumerateArray()
                .Select(row => row.GetProperty("demandId").GetString()));
        Assert.Equal(1, fact.RootElement.GetProperty("silentBacklogCount").GetInt32());

        string html = new DispatchBacklogCard().RenderFact(fact.RootElement);
        Assert.DoesNotContain("D-LEFT", html, StringComparison.Ordinal);
        Assert.DoesNotContain(DispatchReasonCodes.DemandLeftCatalog, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClearedStructuralBlockIsNotShown()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        DateTimeOffset raised = DateTimeOffset.UtcNow.AddHours(-2);
        await fixture.Blocks.RaiseOrRefreshAsync(
            "D-CLEARED", DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup, "TDK-D-CLEARED", "{}", raised,
            TestContext.Current.CancellationToken);
        await fixture.Blocks.ClearAsync(
            "D-CLEARED", DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup, raised.AddHours(1),
            TestContext.Current.CancellationToken);
        await fixture.Blocks.RaiseOrRefreshAsync(
            "D-HOLDING", DispatchReasonCodes.VehicleSlotModelUnresolved, "TDK-D-HOLDING", "{}", raised,
            TestContext.Current.CancellationToken);

        using JsonDocument fact = await ReadAsync(fixture);

        JsonElement only = Assert.Single(fact.RootElement.GetProperty("structuralBlocks").EnumerateArray());
        Assert.Equal("D-HOLDING", only.GetProperty("demandId").GetString());
        string html = new DispatchBacklogCard().RenderFact(fact.RootElement);
        Assert.DoesNotContain("D-CLEARED", html, StringComparison.Ordinal);
        Assert.Contains("D-HOLDING", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoStructuralBlocksTheCardSaysNoneWithoutAlarmStyling()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();

        using JsonDocument fact = await ReadAsync(fixture);

        string html = new DispatchBacklogCard().RenderFact(fact.RootElement);
        Assert.Contains("<h3>结构性派车阻断</h3><p>无</p>", html, StringComparison.Ordinal);
        Assert.Contains("<h3>派车积压（等待派车）</h3><p>无</p>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("alarm", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<details", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCardExplainsTheFourSlotGroupReasonsAndShowsAnUnregisteredCodeAsIs()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        DateTimeOffset firstSeen = DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach (string reason in SlotGroupReasons)
        {
            AddBacklog(fixture, "D-" + reason, reason, firstSeen);
        }
        AddBacklog(fixture, "D-UNKNOWN", UnknownReason, firstSeen);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await fixture.Blocks.RaiseOrRefreshAsync(
            "D-UNKNOWN-BLOCK", UnknownReason, "TDK-D-UNKNOWN-BLOCK", "{}", firstSeen,
            TestContext.Current.CancellationToken);

        using JsonDocument fact = await ReadAsync(fixture);
        string html = new DispatchBacklogCard().RenderFact(fact.RootElement);

        foreach (string reason in SlotGroupReasons)
        {
            string description = DispatchBacklogQueryEndpoint.Descriptions[reason];
            Assert.Matches(@"\p{IsCJKUnifiedIdeographs}", description);
            string row = RowOf(html, "TDK-D-" + reason);
            Assert.Contains(reason, row, StringComparison.Ordinal);
            Assert.Contains(description, row, StringComparison.Ordinal);
            Assert.DoesNotContain(DispatchBacklogCard.UnregisteredDescription, row, StringComparison.Ordinal);
        }
        // 未登记的码不猜：码值原样显示，说明栏标「未登记说明」，积压与阻断两处一样。
        foreach (string demand in new[] { "TDK-D-UNKNOWN（", "TDK-D-UNKNOWN-BLOCK" })
        {
            string row = RowOf(html, demand);
            Assert.Contains($"<td>{UnknownReason}</td><td>{DispatchBacklogCard.UnregisteredDescription}</td>", row,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// control-server#198：站点目录不新鲜时整图所有任务类型停受理（REQ-0302），这个码归积压，会出现在积压卡上，
    /// 所以要有中文说明，并且说清是整图状态、不是只停这一类。
    /// </summary>
    [Fact]
    public async Task TheCatalogNotFreshReasonIsExplainedAsAWholeMapStateRatherThanShownAsUnregistered()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        string reason = TaskTypeStationReasonCodes.BindingCatalogNotFresh;
        AddBacklog(fixture, "D-NOT-FRESH", reason, DateTimeOffset.UtcNow.AddMinutes(-1));
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using JsonDocument fact = await ReadAsync(fixture);
        string html = new DispatchBacklogCard().RenderFact(fact.RootElement);

        Assert.True(
            DispatchBacklogQueryEndpoint.Descriptions.TryGetValue(reason, out string? description),
            reason + " has no description.");
        Assert.Matches(@"\p{IsCJKUnifiedIdeographs}", description);
        Assert.Contains("所有任务类型", description, StringComparison.Ordinal);
        JsonElement row = Assert.Single(fact.RootElement.GetProperty("backlog").EnumerateArray());
        Assert.Equal(description, row.GetProperty("reasonDescription").GetString());
        string rendered = RowOf(html, "TDK-D-NOT-FRESH");
        Assert.Contains(description, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(DispatchBacklogCard.UnregisteredDescription, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCardIsSelfRegisteredAndReadsOnlyItsDashboardQueryEndpoint()
    {
        IDashboardCard card = Assert.Single(
            DashboardCardCatalog.Discovered.Cards, candidate => candidate is DispatchBacklogCard);
        Assert.StartsWith(DashboardPaths.QueryPrefix, card.SourcePath, StringComparison.Ordinal);
        Assert.Contains(
            DashboardQueryEndpointCatalog.Discover(typeof(DispatchBacklogQueryEndpoint).Assembly).Endpoints,
            endpoint => endpoint is DispatchBacklogQueryEndpoint && endpoint.Path == card.SourcePath);
    }

    private static void AddBacklog(
        AreaAssignmentPersistenceFixture fixture,
        string demandId,
        string reasonCode,
        DateTimeOffset firstSeenAt,
        DateTimeOffset? acceptedAt = null) =>
        fixture.Context.JourneyBacklog.Add(new JourneyBacklogRow
        {
            DemandId = demandId,
            TransportDemandKey = "TDK-" + demandId,
            FirstSeenAt = firstSeenAt,
            DemandCreatedAt = firstSeenAt.AddMinutes(-1),
            DecisionFingerprint = "fingerprint",
            ReasonCode = reasonCode,
            LastSeenAt = acceptedAt ?? firstSeenAt,
            AcceptedAt = acceptedAt
        });

    private static async Task<JsonDocument> ReadAsync(AreaAssignmentPersistenceFixture fixture)
    {
        fixture.Context.ChangeTracker.Clear();
        object fact = await new DispatchBacklogQueryEndpoint()
            .ReadAsync(fixture.Context, TestContext.Current.CancellationToken);
        return JsonDocument.Parse(JsonSerializer.Serialize(fact));
    }

    private static string RowOf(string html, string demandText)
    {
        int cell = html.IndexOf("<td>" + demandText, StringComparison.Ordinal);
        Assert.True(cell >= 0, "No row for " + demandText + ".");
        int end = html.IndexOf("</tr>", cell, StringComparison.Ordinal);
        return html[cell..end];
    }
}
