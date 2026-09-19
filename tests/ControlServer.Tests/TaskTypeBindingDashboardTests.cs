using System.Text.Json;
using ControlServer.Application;
using ControlServer.Dashboard;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Infrastructure.Persistence;
using static ControlServer.Tests.TaskTypeStationTestData;

namespace ControlServer.Tests;

/// <summary>
/// 看板按图、按任务类型列出绑定与暂停（REQ-0340、REQ-0268，control-server#162）：服务端只读查询端点与看板卡片两半。
/// </summary>
public sealed class TaskTypeBindingDashboardTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheQueryListsEveryRuleTaskTypeWithItsBindingAndStatusAndEveryHoldWithItsSource()
    {
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        long version = await ActivateWithRequirementAsync(fixture);
        fixture.Context.MapStationCatalogStates.Add(new MapStationCatalogStateRow
        {
            MapId = 25,
            State = MapStationCatalogState.Fresh,
            LastCompleteConfirmationAt = Now,
            CatalogRevision = TaskTypeHoldTestKit.Revision25,
            UpdatedAt = Now
        });
        await fixture.Context.SaveChangesAsync(Token);
        // 305 is renamed (a catalog-change hold on STAGING_TO_WIRE), 210 is gone (a hold on WIRE_TO_GATE, released
        // below so that the bare status shows), and a person holds STAGING_TO_WIRE as well.
        await TaskTypeHoldTestKit.Convergence(fixture)
            .ApplyAsync(TaskTypeHoldTestKit.Catalog25((305, "派工待送取货-改")), Token);
        TaskTypeStationHold gateHold = (await fixture.Holds.ListUnreleasedAsync(25, Token))
            .Single(hold => hold.TaskType == TransportTaskTypes.WireToGate);
        await fixture.Holds.ReleaseAsync(gateHold.HoldId, "fieldops:test", Now.AddMinutes(1), Token);
        await fixture.Holds.RaiseAsync(
            25, TransportTaskTypes.StagingToWire, TaskTypeStationHoldSource.Manual,
            TaskTypeHoldEndpoints.ManualHoldReasonCode, """{"reason":"派工待送取货点被占用"}""", "deployment:test",
            Now.AddMinutes(2), Token);
        // A hold from batch 6-05's activation path, written as that ticket writes it.
        fixture.Context.Set<TaskTypeStationHoldRow>().Add(new TaskTypeStationHoldRow
        {
            HoldId = "activation-1",
            MapId = 25,
            TaskType = TransportTaskTypes.DieToOven,
            Source = "ACTIVATION_RESULT_UNKNOWN",
            ReasonCode = "TASK_TYPE_ACTIVATION_RESULT_UNKNOWN",
            DetailJson = "{}",
            RaisedAt = Now.AddMinutes(3),
            RaisedBy = "deployment:fieldops"
        });
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        JsonElement map = Assert.Single((await ReadAsync(fixture.Context)).GetProperty("maps").EnumerateArray());
        Assert.Equal(25, map.GetProperty("mapId").GetInt32());
        Assert.Equal(version, map.GetProperty("activeBindingSetVersion").GetInt64());
        Dictionary<string, JsonElement> rows = map.GetProperty("taskTypes").EnumerateArray()
            .ToDictionary(row => row.GetProperty("taskType").GetString()!, StringComparer.Ordinal);

        Assert.Equal(TransportTaskTypes.All.Order(StringComparer.Ordinal), rows.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("STATION_NOT_IN_CATALOG", rows[TransportTaskTypes.WireToGate].GetProperty("status").GetString());
        Assert.Equal(210, rows[TransportTaskTypes.WireToGate].GetProperty("stationRiotId").GetInt32());
        Assert.Equal("关卡", rows[TransportTaskTypes.WireToGate].GetProperty("stationName").GetString());
        Assert.Equal("NOT_REQUIRED", rows[TransportTaskTypes.WireToOptical].GetProperty("status").GetString());
        Assert.False(rows[TransportTaskTypes.WireToOptical].GetProperty("required").GetBoolean());
        Assert.True(rows[TransportTaskTypes.StagingToWire].GetProperty("required").GetBoolean());

        JsonElement staging = rows[TransportTaskTypes.StagingToWire];
        Assert.Equal("HELD", staging.GetProperty("status").GetString());
        Assert.Equal(
            [("目录变化", "站点改名"), ("看板人工", "派工待送取货点被占用")],
            staging.GetProperty("holds").EnumerateArray()
                .Select(hold => (hold.GetProperty("sourceLabel").GetString(), hold.GetProperty("reason").GetString())));
        Assert.Equal(
            "激活结果未知",
            Assert.Single(rows[TransportTaskTypes.DieToOven].GetProperty("holds").EnumerateArray())
                .GetProperty("sourceLabel").GetString());
        Assert.Equal("HELD", rows[TransportTaskTypes.DieToOven].GetProperty("status").GetString());
    }

    [Fact]
    public async Task AMapLeftWithoutAnActiveBindingSetByAManualCloseShowsThatInsteadOfPretendingEachTaskTypeIsUnused()
    {
        // #161's tombstone: a contradictory activation closed by hand leaves the pointer CLOSED_MANUALLY with no active
        // version until the next FieldOps activation or rollback.
        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        await ActivateWithRequirementAsync(fixture);
        TaskTypeStationActiveBindingSetRow pointer = fixture.Context.Set<TaskTypeStationActiveBindingSetRow>().Single();
        pointer.ActiveVersion = null;
        pointer.State = TaskTypeStationActivationState.ClosedManually;
        await fixture.Context.SaveChangesAsync(Token);
        await fixture.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual,
            TaskTypeHoldEndpoints.ManualHoldReasonCode, """{"reason":"收尾之后先停着"}""", "deployment:test", Now, Token);
        fixture.Context.ChangeTracker.Clear();

        JsonElement map = Assert.Single((await ReadAsync(fixture.Context)).GetProperty("maps").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, map.GetProperty("activeBindingSetVersion").ValueKind);
        Assert.Equal(TaskTypeStationActivationState.ClosedManually, map.GetProperty("activationState").GetString());
        Dictionary<string, JsonElement> rows = map.GetProperty("taskTypes").EnumerateArray()
            .ToDictionary(row => row.GetProperty("taskType").GetString()!, StringComparer.Ordinal);
        Assert.Equal("HELD", rows[TransportTaskTypes.WireToGate].GetProperty("status").GetString());
        Assert.Equal("NO_ACTIVE_BINDING_SET", rows[TransportTaskTypes.StagingToWire].GetProperty("status").GetString());

        using JsonDocument card = JsonDocument.Parse("{\"maps\":[" + map.GetRawText() + "]}");
        string html = new TaskTypeBindingCard().RenderFact(card.RootElement);
        Assert.Contains("无生效绑定集", html, StringComparison.Ordinal);
        // control-server#200 b: a tombstone is also what an unknown attempt with no hold to restore reconciles back to, when
        // nobody closed anything by hand. The card says what the pointer means, not how it got there; the code stays beside it.
        // (Not "等待下一次激活": the dashboard source may not carry 激活 or 回滚, DashboardSkeletonTests.)
        Assert.Contains("生效指针：无生效版本，已收尾，等 FieldOps 换上新的一版", html, StringComparison.Ordinal);
        Assert.Contains(TaskTypeStationActivationState.ClosedManually, html, StringComparison.Ordinal);
        Assert.DoesNotContain("人工收尾", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCardListsEachTaskTypeWithItsStatusTheHoldSourcesAndAHoldLinkOnEveryRow()
    {
        using JsonDocument fact = JsonDocument.Parse("""
            {"maps":[{"mapId":25,"activeBindingSetVersion":3,"taskTypes":[
              {"taskType":"WIRE_TO_GATE","required":true,"stationRiotId":210,"stationName":"关卡","status":"NORMAL","holds":[]},
              {"taskType":"WIRE_TO_OPTICAL","required":true,"stationRiotId":null,"stationName":null,"status":"BINDING_MISSING","holds":[]},
              {"taskType":"DIE_TO_OVEN","required":true,"stationRiotId":401,"stationName":"烘箱","status":"STATION_NOT_IN_CATALOG","holds":[]},
              {"taskType":"STAGING_TO_WIRE","required":true,"stationRiotId":230,"stationName":"派工待送取货","status":"HELD","holds":[
                {"source":"MANUAL","sourceLabel":"看板人工","raisedAt":"2026-09-19T08:00:00+00:00","reason":"取货点被占用"},
                {"source":"CATALOG_CHANGE","sourceLabel":"目录变化","raisedAt":"2026-09-19T08:01:00+00:00","reason":"站点改名"},
                {"source":"ACTIVATION_RESULT_UNKNOWN","sourceLabel":"激活结果未知","raisedAt":"2026-09-19T08:02:00+00:00","reason":null}]},
              {"taskType":"WIRE_TO_NITROGEN","required":false,"stationRiotId":null,"stationName":null,"status":"NOT_REQUIRED","holds":[]}
            ]}]}
            """);

        string html = new TaskTypeBindingCard().RenderFact(fact.RootElement);

        Assert.Contains("正常", RowOf(html, "WIRE_TO_GATE"), StringComparison.Ordinal);
        Assert.Contains("关卡", RowOf(html, "WIRE_TO_GATE"), StringComparison.Ordinal);
        Assert.Contains("缺绑定", RowOf(html, "WIRE_TO_OPTICAL"), StringComparison.Ordinal);
        Assert.Contains("绑定站点不在目录", RowOf(html, "DIE_TO_OVEN"), StringComparison.Ordinal);
        string held = RowOf(html, "STAGING_TO_WIRE");
        Assert.Contains("已暂停", held, StringComparison.Ordinal);
        Assert.Contains("看板人工", held, StringComparison.Ordinal);
        Assert.Contains("目录变化", held, StringComparison.Ordinal);
        Assert.Contains("激活结果未知", held, StringComparison.Ordinal);
        Assert.Contains("取货点被占用", held, StringComparison.Ordinal);
        Assert.Contains("2026-09-19T08:01:00", held, StringComparison.Ordinal);
        Assert.Contains("不在本图需求集合", RowOf(html, "WIRE_TO_NITROGEN"), StringComparison.Ordinal);
        foreach (string taskType in new[] { "WIRE_TO_GATE", "STAGING_TO_WIRE", "WIRE_TO_NITROGEN" })
        {
            Assert.Contains(
                $"href=\"/actions/task-type-hold?mapId=25&amp;taskType={taskType}\"",
                RowOf(html, taskType),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WhenTheBindingsCannotBeReadTheCardSaysWhy()
    {
        string rendered = DashboardPageRenderer.RenderCard(
            new TaskTypeBindingCard(), DashboardCardData.Unavailable("ControlServer 返回 500，本轮取不到当前事实。"));

        Assert.Contains("ControlServer 返回 500，本轮取不到当前事实。", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCardRegistersItselfAgainstTheReadOnlyEndpoint()
    {
        IDashboardCard card = Assert.Single(
            DashboardCardCatalog.Discovered.Cards, candidate => candidate is TaskTypeBindingCard);
        IDashboardQueryEndpoint endpoint = Assert.Single(
            DashboardQueryEndpointCatalog.Discover(typeof(TaskTypeBindingsQueryEndpoint).Assembly).Endpoints,
            candidate => candidate is TaskTypeBindingsQueryEndpoint);
        Assert.Equal("/api/dashboard/task-type-bindings", endpoint.Path);
        Assert.Equal(endpoint.Path, card.SourcePath);
    }

    private static async Task<long> ActivateWithRequirementAsync(TaskTypeStationPersistenceFixture fixture)
    {
        TaskTypeStationRuleVersion rules = (await fixture.Rules.WriteVersionAsync(SixRules, Source, Now.AddDays(-1), Token)).Version;
        TaskTypeStationBinding[] bindings = [GateBinding, StagingBinding];
        TaskTypeStationBindingSetVersion written = (await fixture.Bindings.WriteVersionAsync(
            25,
            rules.Version,
            [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire],
            bindings,
            catalogRevision: null,
            Source,
            Now.AddDays(-1),
            Token)).Version;
        await fixture.Bindings.SetActiveAsync(25, written.Version, Now.AddDays(-1), Token);
        fixture.Context.ChangeTracker.Clear();
        return written.Version;
    }

    private static async Task<JsonElement> ReadAsync(ControlServerDbContext context)
    {
        object result = await new TaskTypeBindingsQueryEndpoint().ReadAsync(context, Token);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        return document.RootElement.Clone();
    }

    private static string RowOf(string html, string taskType)
    {
        int start = html.IndexOf("<td>" + taskType + "</td>", StringComparison.Ordinal);
        Assert.True(start >= 0, "No row for " + taskType + ".");
        int end = html.IndexOf("</tr>", start, StringComparison.Ordinal);
        return html[start..end];
    }
}
