using System.Data.Common;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// control-server#659：路网的两条黏住陈旧原因（边组指纹变化、动态代价出现）不会被一次刷新失败盖掉，也只能经受治理的人工出口解除。
/// </summary>
/// <remarks>
/// <para>
/// <b>修之前</b>：<c>MarkStaleAsync</c> 直接覆写原因。指纹变化黏住 → 一次刷新失败把原因改成 <c>ROUTE_GRAPH_REFRESH_FAILED</c> → 下一次刷新成功，
/// 原因被清空，派车恢复——用的是一张没人看过的图（REQ-0207 不允许可达性降级）。反过来，黏住之后除了直接改库没有任何解除路径。
/// </para>
/// <para>
/// 前四条是 #657 审查探针的场景：变化 → 失败（超时与非超时各一）→ 成功，派车仍被挡在黏住原因上。其后钉住两条黏住原因先后出现时后一条覆盖前一条，
/// 然后是解除出口的每一条判据、它与刷新器同刻写时的裁决，以及审计。
/// </para>
/// </remarks>
public sealed class RouteGraphStickyStaleTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
    private const int MapId = 25;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly RouteGraphEdgeFact[] Edges =
    [
        new(1, 1, 2, 10000, 0, 0, 0, 10000, 1, false),
        new(2, 2, 3, 10000, 0, 10000, 0, 20000, 1, false),
    ];

    private static readonly RouteGraphStationFact[] Stations =
    [
        new(11, "C15-13", 1, 0, 0, 1, 0),
        new(12, "N1-3_N1-7", 2, 0, 20000, 3, 0),
    ];

    private static readonly RouteGraphEdgeGroupFact[] OneGroup = [new("ONE-WAY", 1, "DIRECTION")];

    public enum Failure
    {
        TimeOut,
        HttpRequest,
    }

    // ---- the review's probe: a failure does not erase a sticky reason -----------------------------------------------

    /// <summary>
    /// 审查探针：边组指纹变了，派车被挡；接着一次刷新失败（RIoT 读超时，或 <see cref="HttpRequestException"/>），再一次刷新成功。原因仍是
    /// <c>ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED</c>，陈旧起始时刻没动，可达性判据照样以它挡住派车。
    /// </summary>
    [Theory]
    [InlineData(Failure.TimeOut)]
    [InlineData(Failure.HttpRequest)]
    public async Task AFailedRefreshDoesNotEraseAnEdgeGroupFingerprintChange(Failure failure)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.RefreshAsync();
        Assert.Equal(DispatchAdmissionChain.Eligible, await fixture.ReachabilityAsync());

        fixture.Source.EdgeGroups = OneGroup;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));
        DateTimeOffset since = (await fixture.HeaderAsync()).StaleSince!.Value;

        fixture.Source.Fail = failure;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));
        fixture.Source.Fail = null;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));

        RouteGraphSnapshotHeader header = await fixture.HeaderAsync();
        Assert.Equal((RouteGraphStaleReasons.EdgeGroupFingerprintChanged, since), (header.StaleReason, header.StaleSince));
        Assert.Equal(RouteGraphStaleReasons.EdgeGroupFingerprintChanged, await fixture.ReachabilityAsync());
    }

    /// <summary>同上，黏住的是动态代价由空变非空。动态代价一直在，失败之后的成功刷新不会再判出「由空变非空」，所以原因丢了就再也回不来。</summary>
    [Theory]
    [InlineData(Failure.TimeOut)]
    [InlineData(Failure.HttpRequest)]
    public async Task AFailedRefreshDoesNotEraseDynamicRouteCostAppearing(Failure failure)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.RefreshAsync();

        fixture.Source.DynamicRouteCostPresent = true;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RouteGraphStaleReasons.DynamicRouteCostAppeared, (await fixture.HeaderAsync()).StaleReason);

        fixture.Source.Fail = failure;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));
        fixture.Source.Fail = null;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RouteGraphStaleReasons.DynamicRouteCostAppeared, (await fixture.HeaderAsync()).StaleReason);
        Assert.Equal(RouteGraphStaleReasons.DynamicRouteCostAppeared, await fixture.ReachabilityAsync());
    }

    /// <summary>
    /// 两条黏住原因先后出现：后一条覆盖前一条（保持现状，是有意的），陈旧起始时刻是后一次检测的时刻（审查 S1 后调度裁定）。原因串只剩一条没关系——
    /// 解除时操作员确认的是当前边组指纹与动态代价有无，两件事都要看过才放得出去；起始时刻跟着最近一次变化走，「图在变化之后重读过」比的才是那一次。
    /// </summary>
    [Fact]
    public async Task AStickyReasonReplacesTheOtherStickyReasonAndRestartsTheSince()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.RefreshAsync();

        fixture.Source.EdgeGroups = OneGroup;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));
        DateTimeOffset first = (await fixture.HeaderAsync()).StaleSince!.Value;

        fixture.Source.DynamicRouteCostPresent = true;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));

        RouteGraphSnapshotHeader header = await fixture.HeaderAsync();
        Assert.Equal(
            (RouteGraphStaleReasons.DynamicRouteCostAppeared, first.AddSeconds(10)),
            (header.StaleReason, header.StaleSince));
    }

    /// <summary>
    /// 失败发生在黏住之后很久——设计态 TTL 都过了、图已经在变化之后重读过——仍然盖不掉黏住原因，之后的成功刷新也清不掉它（审查 L4，守「只在
    /// 刚黏住时才不让盖」这种变异）。
    /// </summary>
    [Theory]
    [InlineData(Failure.TimeOut)]
    [InlineData(Failure.HttpRequest)]
    public async Task AFailureLongAfterTheChangeStillDoesNotEraseIt(Failure failure)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.RefreshAsync();
        fixture.Source.EdgeGroups = OneGroup;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));
        await fixture.RefreshAsync(TimeSpan.FromMinutes(11));
        DateTimeOffset since = (await fixture.HeaderAsync()).StaleSince!.Value;

        fixture.Source.Fail = failure;
        await fixture.RefreshAsync(TimeSpan.FromMinutes(5));
        fixture.Source.Fail = null;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));

        RouteGraphSnapshotHeader header = await fixture.HeaderAsync();
        Assert.Equal((RouteGraphStaleReasons.EdgeGroupFingerprintChanged, since), (header.StaleReason, header.StaleSince));
        Assert.Equal(RouteGraphStaleReasons.EdgeGroupFingerprintChanged, await fixture.ReachabilityAsync());
    }

    /// <summary>
    /// 审查探针 P1：黏住原因落在一条会自愈的原因之上，起始时刻不能继承那条原因的。t=590s 读超时，原因变刷新失败；t=600s 设计态到期先重读、
    /// 同一轮读边组判出指纹变化。图是在这次变化之前（同一轮读边组之前）读的，照 GET 填好的确认立即提交必须被拒。
    /// </summary>
    [Fact]
    public async Task AStickyChangeOverAFailedRefreshDoesNotInheritItsSince()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.RefreshAsync();
        fixture.Source.Fail = Failure.TimeOut;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(590));
        Assert.Equal(RouteGraphStaleReasons.RefreshFailed, (await fixture.HeaderAsync()).StaleReason);

        fixture.Source.Fail = null;
        fixture.Source.EdgeGroups = OneGroup;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));
        RouteGraphSnapshotHeader header = await fixture.HeaderAsync();
        Assert.Equal(
            (RouteGraphStaleReasons.EdgeGroupFingerprintChanged, fixture.Clock.GetUtcNow()),
            (header.StaleReason, header.StaleSince));

        RouteGraphStickyStaleReleaseResult refused = await fixture.ReleaseAsync(await fixture.AcknowledgedAsync());

        Assert.Equal([RouteGraphStickyStaleReleaseCodes.DesignNotRefreshedSinceStale], refused.Codes);
        Assert.Equal(RouteGraphStaleReasons.EdgeGroupFingerprintChanged, await fixture.ReachabilityAsync());
    }

    /// <summary>
    /// 审查探针 P1b：动态代价先黏住（t=10s），10 分钟后设计态重读，再过 10 秒边组指纹变化覆盖原因。这次编辑之后图还没重读过，立即提交必须被拒。
    /// </summary>
    [Fact]
    public async Task AStickyChangeOverAnotherStickyReasonDoesNotInheritItsSince()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.RefreshAsync();
        fixture.Source.DynamicRouteCostPresent = true;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));
        await fixture.RefreshAsync(TimeSpan.FromMinutes(10));

        fixture.Source.EdgeGroups = OneGroup;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));

        RouteGraphStickyStaleReleaseResult refused = await fixture.ReleaseAsync(await fixture.AcknowledgedAsync());

        Assert.Equal([RouteGraphStickyStaleReleaseCodes.DesignNotRefreshedSinceStale], refused.Codes);
        Assert.Equal(RouteGraphStaleReasons.EdgeGroupFingerprintChanged, await fixture.ReachabilityAsync());
    }

    /// <summary>
    /// 审查探针 P2：刷新轮的陈旧判定已经读到黏住原因，这时一次解除落了地。判定不能把读到的黏住原因原样写回去（那只是回声）：解除报了成功、写了
    /// RELEASED 审计，库里就必须是清掉的。
    /// </summary>
    [Fact]
    public async Task TheStalenessEvaluationDoesNotEchoAStickyReasonBackOverARelease()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.RefreshAsync();
        fixture.Source.EdgeGroups = OneGroup;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));
        await fixture.RefreshAsync(TimeSpan.FromMinutes(10));
        RouteGraphStickyStaleReleaseRequest acknowledged = await fixture.AcknowledgedAsync();

        RouteGraphStickyStaleReleaseResult? released = null;
        await fixture.RefreshAsync(
            TimeSpan.FromSeconds(10),
            afterEvaluationRead: async () => released = await fixture.ReleaseAsync(acknowledged));

        Assert.True(released?.Released, string.Join(',', released?.Codes ?? []));
        Assert.Null((await fixture.HeaderAsync()).StaleReason);
        Assert.Equal(DispatchAdmissionChain.Eligible, await fixture.ReachabilityAsync());
    }

    /// <summary>
    /// 刷新器同一轮里先把快照行读进了自己的上下文（黏住原因还在），这时解除在另一个上下文里把它清了，刷新器紧接着又判出同一条黏住原因。这次写入不能
    /// 因为「和我手上那份一样」被当成没变化而丢掉：库里必须重新是黏住原因。
    /// </summary>
    [Fact]
    public async Task AStickyReasonMarkedAfterAReleaseIsWrittenEvenWhenTheStoreStillTracksTheOldRow()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.RefreshAsync();
        fixture.Source.EdgeGroups = OneGroup;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));

        await using ControlServerDbContext roundContext = fixture.NewContext();
        RouteGraphSnapshotStore roundStore = new(roundContext);
        // Tracks the header row, sticky reason and all, the way a refresh round does before it marks anything.
        await roundStore.ReplaceRuntimeStateAsync(MapId, [], [], fixture.Clock.GetUtcNow(), Token);

        await using (ControlServerDbContext other = fixture.NewContext())
        {
            await other.RouteGraphSnapshots.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.StaleReason, (string?)null)
                    .SetProperty(row => row.StaleSince, (DateTimeOffset?)null),
                Token);
        }

        await roundStore.MarkStaleAsync(
            MapId, RouteGraphStaleReasons.EdgeGroupFingerprintChanged, fixture.Clock.GetUtcNow(), Token);

        Assert.Equal(RouteGraphStaleReasons.EdgeGroupFingerprintChanged, (await fixture.HeaderAsync()).StaleReason);
    }

    // ---- the governed release -------------------------------------------------------------------------------------

    /// <summary>
    /// 走解除出口：指纹变了，等设计态在变化之后重读过，操作员照 GET 读到的状态确认、写明理由与核实记录。解除之后下一次刷新照常，派车恢复；
    /// 审计记下这次成功、读到的快照与放行时动态代价不在。
    /// </summary>
    [Fact]
    public async Task AReleaseOnTheAcknowledgedStateRestoresDispatchAndIsAudited()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.RefreshAsync();
        fixture.Source.EdgeGroups = OneGroup;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));
        await fixture.RefreshAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(RouteGraphStaleReasons.EdgeGroupFingerprintChanged, await fixture.ReachabilityAsync());

        RouteGraphStickyStaleReleaseResult result = await fixture.ReleaseAsync(await fixture.AcknowledgedAsync());

        Assert.True(result.Released, string.Join(',', result.Codes));
        Assert.Null((await fixture.HeaderAsync()).StaleReason);
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));
        Assert.Null((await fixture.HeaderAsync()).StaleReason);
        Assert.Equal(DispatchAdmissionChain.Eligible, await fixture.ReachabilityAsync());

        AdministratorAuditRecordRow audit = await fixture.SingleAuditAsync();
        Assert.Equal(
            (result.AuditRecordId, GovernanceActionOutcome.Succeeded, GovernedObjectKind.RouteGraphSnapshot, "25", "值班工程师"),
            (audit.AuditRecordId, audit.Outcome, audit.ObjectKind, audit.ObjectId, audit.ClaimedAdministratorRole));
        using JsonDocument detail = JsonDocument.Parse(audit.DetailJson);
        Assert.Equal("RELEASED", detail.RootElement.GetProperty("result").GetString());
        Assert.False(detail.RootElement.GetProperty("dynamicRouteCostPresentAtRelease").GetBoolean());
        Assert.Equal(
            RouteGraphStaleReasons.EdgeGroupFingerprintChanged,
            detail.RootElement.GetProperty("snapshot").GetProperty("staleReason").GetString());
    }

    /// <summary>
    /// 动态代价仍在时放行等于接受它（剩余风险，有意如此）：解除之后它还在，刷新不再因为它触发；审计里记下放行时它在。
    /// </summary>
    [Fact]
    public async Task ReleasingOverDynamicRouteCostStillPresentAcceptsItAndTheAuditSaysSo()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.RefreshAsync();
        fixture.Source.DynamicRouteCostPresent = true;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));
        await fixture.RefreshAsync(TimeSpan.FromMinutes(10));

        RouteGraphStickyStaleReleaseResult result = await fixture.ReleaseAsync(await fixture.AcknowledgedAsync());
        Assert.True(result.Released, string.Join(',', result.Codes));

        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DispatchAdmissionChain.Eligible, await fixture.ReachabilityAsync());
        using JsonDocument detail = JsonDocument.Parse((await fixture.SingleAuditAsync()).DetailJson);
        Assert.True(detail.RootElement.GetProperty("dynamicRouteCostPresentAtRelease").GetBoolean());
    }

    /// <summary>
    /// 图还没在变化之后重读过——包括检测到变化的那一轮里读的设计态（它在读边组之前）——拒绝 <c>DESIGN_NOT_REFRESHED_SINCE_STALE</c>，仍挡着，
    /// 审计记下这次失败。设计态到期重读之后同一份确认就放得出去。
    /// </summary>
    [Fact]
    public async Task AReleaseBeforeTheGraphIsReadAgainAfterTheChangeIsRefused()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.RefreshAsync();
        fixture.Source.EdgeGroups = OneGroup;
        // The change lands in the round where the design state is due, so the design state is read in that same round.
        await fixture.RefreshAsync(TimeSpan.FromMinutes(10));
        RouteGraphSnapshotHeader header = await fixture.HeaderAsync();
        Assert.Equal(header.StaleSince, header.DesignRefreshedAt);

        RouteGraphStickyStaleReleaseRequest acknowledged = await fixture.AcknowledgedAsync();
        RouteGraphStickyStaleReleaseResult refused = await fixture.ReleaseAsync(acknowledged);

        Assert.Equal([RouteGraphStickyStaleReleaseCodes.DesignNotRefreshedSinceStale], refused.Codes);
        Assert.Equal(RouteGraphStaleReasons.EdgeGroupFingerprintChanged, await fixture.ReachabilityAsync());
        Assert.Equal(GovernanceActionOutcome.Failed, (await fixture.SingleAuditAsync()).Outcome);

        await fixture.RefreshAsync(TimeSpan.FromMinutes(10));
        Assert.True((await fixture.ReleaseAsync(acknowledged)).Released);
    }

    /// <summary>不是黏住原因（这里是刷新失败）不归人解除：拒绝 <c>NOT_STICKY_STALE</c>，原因不动。</summary>
    [Fact]
    public async Task AReasonThatClearsItselfIsNotReleasedByHand()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.RefreshAsync();
        fixture.Source.Fail = Failure.TimeOut;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));

        RouteGraphStickyStaleReleaseResult refused = await fixture.ReleaseAsync(await fixture.AcknowledgedAsync());

        Assert.Equal([RouteGraphStickyStaleReleaseCodes.NotStickyStale], refused.Codes);
        Assert.Equal(RouteGraphStaleReasons.RefreshFailed, (await fixture.HeaderAsync()).StaleReason);
    }

    /// <summary>
    /// 确认的状态与库里任何一项不一致——原因、起始时刻、边组指纹、动态代价有无——拒绝 <c>STATE_CHANGED_SINCE_READ</c>，仍挡着。
    /// </summary>
    [Theory]
    [InlineData("reason")]
    [InlineData("since")]
    [InlineData("fingerprint")]
    [InlineData("dynamic")]
    public async Task AnAcknowledgementThatDoesNotMatchTheSnapshotIsRefused(string mismatch)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.RefreshAsync();
        fixture.Source.EdgeGroups = OneGroup;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));
        await fixture.RefreshAsync(TimeSpan.FromMinutes(10));

        RouteGraphStickyStaleReleaseRequest acknowledged = await fixture.AcknowledgedAsync();
        RouteGraphStickyStaleReleaseRequest wrong = mismatch switch
        {
            "reason" => acknowledged with { ExpectedStaleReason = RouteGraphStaleReasons.DynamicRouteCostAppeared },
            "since" => acknowledged with { ExpectedStaleSince = acknowledged.ExpectedStaleSince!.Value.AddSeconds(-1) },
            "fingerprint" => acknowledged with { AcknowledgedEdgeGroupFingerprint = "" },
            _ => acknowledged with { AcknowledgedDynamicRouteCostPresent = true },
        };
        RouteGraphStickyStaleReleaseResult refused = await fixture.ReleaseAsync(wrong);

        Assert.Equal([RouteGraphStickyStaleReleaseCodes.StateChangedSinceRead], refused.Codes);
        Assert.Equal(RouteGraphStaleReasons.EdgeGroupFingerprintChanged, await fixture.ReachabilityAsync());
    }

    /// <summary>
    /// 判定读完、清除之前，刷新器把快照改了（同刻写）：指纹、原因（换成另一条黏住原因）、动态代价有无、起始时刻（同一条原因再次检测到）任何一项，
    /// 带条件的清除都更新到 0 行，拒绝 <c>STATE_CHANGED_SINCE_READ</c>，原因不清，不写成功审计（审查 L2、L3）。
    /// </summary>
    [Theory]
    [InlineData("\"EdgeGroupFingerprint\" = 'changed-meanwhile'")]
    [InlineData("\"StaleReason\" = 'ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED'")]
    [InlineData("\"DynamicRouteCostPresent\" = 1")]
    [InlineData("\"StaleSince\" = '2026-09-07 08:20:20+00:00'")]
    public async Task ARefreshChangingTheSnapshotBetweenTheReadAndTheClearWins(string meanwhile)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.RefreshAsync();
        fixture.Source.EdgeGroups = OneGroup;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));
        await fixture.RefreshAsync(TimeSpan.FromMinutes(10));

        RouteGraphStickyStaleReleaseResult refused = await fixture.ReleaseAsync(
            await fixture.AcknowledgedAsync(),
            new BeforeTheClear("UPDATE \"RouteGraphSnapshots\" SET " + meanwhile));

        Assert.Equal([RouteGraphStickyStaleReleaseCodes.StateChangedSinceRead], refused.Codes);
        Assert.True(RouteGraphStickyStaleReasons.IsSticky((await fixture.HeaderAsync()).StaleReason));
        Assert.Equal(GovernanceActionOutcome.Failed, (await fixture.SingleAuditAsync()).Outcome);
    }

    /// <summary>缺办理人、理由、核实记录与确认状态，各是一条拒绝理由，一次全列出来，审计照写；什么都不清。</summary>
    [Fact]
    public async Task EveryMissingFieldIsARefusalCodeAndIsAudited()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.RefreshAsync();
        fixture.Source.EdgeGroups = OneGroup;
        await fixture.RefreshAsync(TimeSpan.FromSeconds(10));
        await fixture.RefreshAsync(TimeSpan.FromMinutes(10));

        RouteGraphStickyStaleReleaseResult refused = await fixture.ReleaseAsync(
            new RouteGraphStickyStaleReleaseRequest(MapId, " ", null, "", null, null, null, null, null));

        Assert.Equal(
            [
                RouteGraphStickyStaleReleaseCodes.OperatorRequired,
                RouteGraphStickyStaleReleaseCodes.ReasonRequired,
                RouteGraphStickyStaleReleaseCodes.SiteVerificationRequired,
                RouteGraphStickyStaleReleaseCodes.AcknowledgementRequired,
            ],
            refused.Codes);
        Assert.Equal(RouteGraphStaleReasons.EdgeGroupFingerprintChanged, (await fixture.HeaderAsync()).StaleReason);
        Assert.Equal(GovernanceActionOutcome.Failed, (await fixture.SingleAuditAsync()).Outcome);
    }

    /// <summary>没有快照的图：拒绝 <c>SNAPSHOT_NOT_FOUND</c>，审计照写。</summary>
    [Fact]
    public async Task AMapWithoutASnapshotIsRefused()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        RouteGraphStickyStaleReleaseResult refused = await fixture.ReleaseAsync(
            new RouteGraphStickyStaleReleaseRequest(
                MapId, "OP-7", "边组改动已核", "RIOT-MAP-0001", null,
                RouteGraphStaleReasons.EdgeGroupFingerprintChanged, Origin, "", false));

        Assert.Equal([RouteGraphStickyStaleReleaseCodes.SnapshotNotFound], refused.Codes);
        Assert.Equal(GovernanceActionOutcome.Failed, (await fixture.SingleAuditAsync()).Outcome);
    }

    /// <summary>每一个拒绝码都有给现场人员看的中文说明（按反射核对，新加码漏写说明会红）。</summary>
    [Fact]
    public void EveryRefusalCodeHasAChineseDescription()
    {
        string[] codes =
        [
            .. typeof(RouteGraphStickyStaleReleaseCodes).GetFields()
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
        ];

        Assert.Equal(9, codes.Length);
        Assert.All(codes, code => Assert.False(
            string.IsNullOrWhiteSpace(RouteGraphStickyStaleReleaseCodes.Descriptions.GetValueOrDefault(code)), code));
        Assert.Equal(codes.Order(StringComparer.Ordinal), RouteGraphStickyStaleReleaseCodes.Descriptions.Keys.Order(StringComparer.Ordinal));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static RouteGraphOptions GraphOptions() => new()
    {
        Enabled = true,
        MapId = MapId,
        DesignStateTtl = TimeSpan.FromMinutes(10),
        RuntimeRefreshPeriod = TimeSpan.FromSeconds(10),
        RuntimeStateMaxAge = TimeSpan.FromSeconds(45),
    };

    /// <summary>The route-graph source: the two-edge map, edge groups and dynamic cost as the test sets them, and an optional failure.</summary>
    private sealed class Source : IRouteGraphSource
    {
        public IReadOnlyList<RouteGraphEdgeGroupFact> EdgeGroups { get; set; } = [];

        public bool DynamicRouteCostPresent { get; set; }

        public Failure? Fail { get; set; }

        public Task<IReadOnlyList<RouteGraphEdgeFact>> ReadEdgesAsync(int mapId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RouteGraphEdgeFact>>(Edges);

        public Task<IReadOnlyList<RouteGraphStationFact>> ReadStationsAsync(
            int mapId, IReadOnlyList<RouteGraphEdgeFact> edges, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RouteGraphStationFact>>(Stations);

        public Task<IReadOnlyList<int>> ReadRemovedEdgeIdsAsync(int mapId, CancellationToken ct) => Fail switch
        {
            // What HttpClient throws when its own Timeout elapses: the caller's token is untouched.
            Failure.TimeOut => throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.",
                new TimeoutException("The operation was canceled.")),
            Failure.HttpRequest => throw new HttpRequestException("Connection refused"),
            _ => Task.FromResult<IReadOnlyList<int>>([]),
        };

        public Task<IReadOnlyList<int>> ReadRemovedStationIdsAsync(int mapId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<int>>([]);

        public Task<IReadOnlyList<RouteGraphEdgeGroupFact>> ReadEdgeGroupsAsync(int mapId, CancellationToken ct) =>
            Task.FromResult(EdgeGroups);

        public Task<bool> ReadDynamicRouteCostPresentAsync(CancellationToken ct) => Task.FromResult(DynamicRouteCostPresent);
    }

    /// <summary>The real store, except that the <c>onRead</c>-th header read runs <c>interleave</c> after reading and before returning.</summary>
    private sealed class InterleavingStore(IRouteGraphSnapshotStore inner, int onRead, Func<Task> interleave) : IRouteGraphSnapshotStore
    {
        private int _reads;

        public async Task<RouteGraphSnapshotHeader?> ReadHeaderAsync(int mapId, CancellationToken cancellationToken)
        {
            RouteGraphSnapshotHeader? header = await inner.ReadHeaderAsync(mapId, cancellationToken);
            if (++_reads == onRead)
            {
                await interleave();
            }
            return header;
        }

        public Task<RouteGraphState?> ReadStateAsync(int mapId, CancellationToken cancellationToken) =>
            inner.ReadStateAsync(mapId, cancellationToken);

        public Task<long> ReplaceDesignStateAsync(
            int mapId,
            IReadOnlyList<RouteGraphEdgeFact> edges,
            IReadOnlyList<RouteGraphStationFact> stations,
            string? sourceGmtUpdate,
            DateTimeOffset refreshedAt,
            CancellationToken cancellationToken) =>
            inner.ReplaceDesignStateAsync(mapId, edges, stations, sourceGmtUpdate, refreshedAt, cancellationToken);

        public Task ReplaceRuntimeStateAsync(
            int mapId,
            IReadOnlyList<int> removedEdgeIds,
            IReadOnlyList<int> removedStationIds,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken) =>
            inner.ReplaceRuntimeStateAsync(mapId, removedEdgeIds, removedStationIds, observedAt, cancellationToken);

        public Task ReplaceEdgeGroupsAsync(
            int mapId,
            IReadOnlyList<RouteGraphEdgeGroupFact> groups,
            string fingerprint,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken) =>
            inner.ReplaceEdgeGroupsAsync(mapId, groups, fingerprint, observedAt, cancellationToken);

        public Task RecordDynamicRouteCostObservationAsync(
            int mapId, bool present, DateTimeOffset observedAt, CancellationToken cancellationToken) =>
            inner.RecordDynamicRouteCostObservationAsync(mapId, present, observedAt, cancellationToken);

        public Task MarkStaleAsync(int mapId, string reason, DateTimeOffset at, CancellationToken cancellationToken) =>
            inner.MarkStaleAsync(mapId, reason, at, cancellationToken);

        public Task ClearStaleAsync(int mapId, DateTimeOffset at, CancellationToken cancellationToken) =>
            inner.ClearStaleAsync(mapId, at, cancellationToken);
    }

    private sealed class AdvanceableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>Runs one SQL statement on the release's own connection just before its clearing UPDATE, as a refresh landing then would.</summary>
    private sealed class BeforeTheClear(string sql) : DbCommandInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done && command.CommandText.Contains("UPDATE \"RouteGraphSnapshots\"", StringComparison.Ordinal))
            {
                _done = true;
                await using DbCommand meanwhile = command.Connection!.CreateCommand();
                meanwhile.Transaction = command.Transaction;
                meanwhile.CommandText = sql;
                await meanwhile.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
        }
    }

    /// <summary>
    /// One in-memory database and one clock. Every refresh, read and release gets its own context, the way the worker's rounds and
    /// the Host entry each get their own scope.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection)
        {
            _connection = connection;
        }

        public Source Source { get; } = new();

        public AdvanceableClock Clock { get; } = new(Origin);

        public static async Task<Fixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(Token);
            Fixture fixture = new(connection);
            await using ControlServerDbContext context = fixture.NewContext();
            await MigratedDatabaseTemplate.ApplyAsync(context.Database, Token);
            return fixture;
        }

        public ControlServerDbContext NewContext(params IInterceptor[] interceptors) =>
            new(new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(_connection).AddInterceptors(interceptors).Options);

        /// <param name="afterEvaluationRead">Runs right after the staleness evaluation has read the header, before it writes.</param>
        public async Task RefreshAsync(TimeSpan? after = null, Func<Task>? afterEvaluationRead = null)
        {
            if (after is { } by)
            {
                Clock.Advance(by);
            }
            await using ControlServerDbContext context = NewContext();
            IRouteGraphSnapshotStore store = new RouteGraphSnapshotStore(context);
            if (afterEvaluationRead is not null)
            {
                // The refresh reads the header twice: once at the start, once in the staleness evaluation.
                store = new InterleavingStore(store, onRead: 2, afterEvaluationRead);
            }
            RouteGraphRefresher refresher = new(
                Source, store, Options.Create(GraphOptions()), Clock, NullLogger<RouteGraphRefresher>.Instance);
            await refresher.RefreshOnceAsync(Token);
        }

        public async Task<RouteGraphSnapshotHeader> HeaderAsync()
        {
            await using ControlServerDbContext context = NewContext();
            return (await new RouteGraphSnapshotStore(context).ReadHeaderAsync(MapId, Token))!;
        }

        /// <summary>The criterion's verdict for a vehicle at station 11 taking a demand picked up at station 12.</summary>
        public async Task<string> ReachabilityAsync()
        {
            await using ControlServerDbContext context = NewContext();
            RouteGraphReachabilityCriterion criterion = new(
                new RouteGraphAccess(new RouteGraphSnapshotStore(context), Options.Create(GraphOptions()), Clock));
            return await criterion.EvaluateAsync(Evaluation(11, 12), Token);
        }

        /// <summary>A complete request acknowledging the snapshot exactly as it reads now.</summary>
        public async Task<RouteGraphStickyStaleReleaseRequest> AcknowledgedAsync()
        {
            RouteGraphSnapshotHeader header = await HeaderAsync();
            return new RouteGraphStickyStaleReleaseRequest(
                MapId, "OP-7", "边组改动已在 RIoT 侧核对，单向组不影响 C15-13 到 N1-3 的可达性", "RIOT-MAP-2026-1012-01", "值班工程师",
                header.StaleReason, header.StaleSince, header.EdgeGroupFingerprint, header.DynamicRouteCostPresent);
        }

        public async Task<RouteGraphStickyStaleReleaseResult> ReleaseAsync(
            RouteGraphStickyStaleReleaseRequest request, params IInterceptor[] interceptors)
        {
            await using ControlServerDbContext context = NewContext(interceptors);
            return await RouteGraphStickyStaleRelease.ReleaseAsync(
                context,
                new GovernanceStore(context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default),
                request,
                Clock.GetUtcNow(),
                Token);
        }

        /// <summary>The newest release audit record; asserts there is at least one.</summary>
        public async Task<AdministratorAuditRecordRow> SingleAuditAsync()
        {
            await using ControlServerDbContext context = NewContext();
            AdministratorAuditRecordRow[] rows = await context.Set<AdministratorAuditRecordRow>().AsNoTracking()
                .Where(row => row.Action == RouteGraphStickyStaleRelease.AuditAction)
                .ToArrayAsync(Token);
            return Assert.Single(rows);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

        private static DispatchCandidateEvaluation Evaluation(int vehicleAtStation, int pickupStation)
        {
            AcceptedDemandSnapshot candidate = new(
                "DEMAND-1",
                TransportDemandKey: "DEMAND-1",
                DemandRevision: 0,
                HistoryEpoch: "EPOCH",
                CatalogRevision: 0,
                AcceptedAt: Origin,
                SeriesId: "SERIES",
                WorkType: "WIRE_TO_GATE",
                Sublot: "SUBLOT",
                CreatedAt: Origin);
            DispatchRoundFacts round = new(
                new DemandCatalogSnapshot("EPOCH", 0, [candidate]),
                new RiotMapStationCatalogSnapshot(MapId, Origin, "sha", []),
                new SingleStationView(new RiotMapStation(pickupStation, "N1-3_N1-7")),
                new HashSet<string>(),
                Origin,
                new VehicleDispatchPolicy([], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY"));
            DispatchVehicleFacts vehicle = new(
                "BROKERX-TEST-0001",
                "AGV-TEST-0001",
                new OnboardDispatchFacts(1, [1, 2], true, true, true, true, false),
                new RiotVehicleObservation(
                    "BROKERX-TEST-0001", true, true, "IDLE", "MAP", vehicleAtStation, 80, "NO_CHARGE", 0, Origin, 0, null),
                Origin);
            return new DispatchCandidateEvaluation(candidate, round, vehicle)
            {
                Route = new ResolvedJourneyRoute(
                    "ZONE", "EVIDENCE", "N1-3_N1-7", pickupStation, "关卡", 210,
                    FixedTaskStationResolution.Resolved("WIRE_TO_GATE", FixedStationEnd.Destination, new RiotMapStation(210, "关卡"))),
            };
        }
    }
}
