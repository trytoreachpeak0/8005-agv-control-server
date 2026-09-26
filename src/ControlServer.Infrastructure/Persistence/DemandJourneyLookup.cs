using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// The one answer to "which journey carries this demand" and "which demands does this journey carry" (control-server#207):
/// the demand memberships in force in <c>JourneyDemands</c>, never the journey row's anchor column
/// <see cref="JourneyRuntimeRow.DemandId"/>.
/// </summary>
/// <remarks>
/// <para>
/// Until batch 7 a journey carried exactly one demand, and a dozen places found it by <c>JourneyRuntimes.DemandId</c>. In a
/// single-demand journey the two answers are the same row, which is why moving every one of those reads here changes
/// nothing observable. In a journey of several demands only the membership answers for any demand but the anchor.
/// </para>
/// <para>
/// The queries are composable rather than executed here, so that each caller keeps what it did before: tracked or not,
/// single or any, joined into a larger query. They read the same memberships, under the same "not removed" rule, as
/// <see cref="JourneyMembershipStore.FindJourneyIdByDemandAsync"/>.
/// </para>
/// </remarks>
public static class DemandJourneyLookup
{
    /// <summary>The tag on the "is this the last open demand" read, so a test can find that one command.</summary>
    public const string LastOpenDemandTag = "control-server#207 last open demand of journey";

    /// <summary>The demands that have not reached a terminal state: neither <c>Succeeded</c> nor <c>Cancelled</c>.</summary>
    /// <remarks>
    /// One definition, used by the orphan check (<c>JourneyRuntimeEngine</c>) and by <see cref="IsLastOpenDemandAsync"/>.
    /// Two copies of it would drift into a journey closed while a demand it carries is still open, or the other way round.
    /// </remarks>
    public static IQueryable<AcceptedDemandRow> OpenDemands(ControlServerDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return dbContext.AcceptedDemands.Where(row =>
            row.Status != DemandExecutionStatus.Succeeded && row.Status != DemandExecutionStatus.Cancelled);
    }

    /// <summary>
    /// 释放改派时写在归属行上的移除原因（REQ-0328，批次7-10，control-server#215）。
    /// </summary>
    public const string ReleasedForRedispatchReason = "RELEASED_FOR_REDISPATCH";

    /// <summary>
    /// 已释放、等着改派的需求：没终结、没有生效的归属、而且<b>最近一次</b>移除的原因是 <see cref="ReleasedForRedispatchReason"/>
    /// （批次7-10，control-server#215）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是这个判据唯一的定义处</b>，两处共用：派车轮次的「已受理」集合要放它过去（它要能再被派一次），孤儿检查要把它排除
    /// （它不属于任何旅程是应该的，不是库坏了）。两份拷贝会分叉成「放过去了却被当孤儿抛」或者反过来。
    /// </para>
    /// <para>
    /// <b>「最近一次」按派车代次认，不按移除时间。</b>SQLite 不能在库里比较或排序 <see cref="DateTimeOffset"/>，而代次是整数：
    /// 一条需求每被改派一次，新归属的 <see cref="JourneyDemandRow.DispatchGeneration"/> 比上一条大。<b>这个前提由释放改派服务承担</b>
    /// ——它写新归属时代次必须严格递增；若有人让两条归属同代次，这里会把「更早那次是释放、后来那次是别的原因」也判成待改派。
    /// </para>
    /// <para>
    /// 仍然是 open 的需求（<see cref="OpenDemands"/> 里有它）：它在业务上还没完成，只是暂时不在任何旅程上。
    /// </para>
    /// </remarks>
    public static IQueryable<AcceptedDemandRow> ReleasedForRedispatch(ControlServerDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        IQueryable<JourneyDemandRow> memberships = dbContext.Set<JourneyDemandRow>();
        return OpenDemands(dbContext).Where(demand =>
            !memberships.Any(row => row.DemandId == demand.DemandId && row.RemovedAt == null) &&
            memberships.Any(released =>
                released.DemandId == demand.DemandId &&
                released.RemovedAt != null &&
                released.RemovalReason == ReleasedForRedispatchReason &&
                !memberships.Any(later =>
                    later.DemandId == demand.DemandId && later.DispatchGeneration > released.DispatchGeneration)));
    }

    /// <summary>
    /// 孤儿检查要看的那些需求：open，而且不是已释放待改派的（批次7-10，control-server#215）。
    /// </summary>
    /// <remarks>
    /// 已释放待改派的需求没有生效的归属是设计如此；把它算进孤儿，释放一落库，引擎每一轮都在孤儿检查处抛
    /// <see cref="BusinessIdentityConflictException"/>，整轮中止、车队不推进。
    /// </remarks>
    public static IQueryable<AcceptedDemandRow> OrphanCandidates(ControlServerDbContext dbContext)
    {
        IQueryable<AcceptedDemandRow> released = ReleasedForRedispatch(dbContext);
        return OpenDemands(dbContext).Where(demand => !released.Any(row => row.DemandId == demand.DemandId));
    }

    /// <summary>
    /// Of <paramref name="demandIds"/>, those whose journey has ended: a journey row naming the demand is Completed, no other
    /// journey row naming it is still going, and it is not waiting to be dispatched again (control-server#215).
    /// </summary>
    /// <remarks>
    /// "Its journey row is Completed" was the whole rule while a demand had exactly one journey row. A release for redispatch
    /// closes the first journey with the demand still alive: it waits in the backlog, then rides a second journey. Both
    /// exclusions are empty for every demand that was never released, so for those the answer is the old one by construction.
    /// </remarks>
    public static async Task<HashSet<string>> EndedJourneyDemandIdsAsync(
        ControlServerDbContext dbContext,
        IReadOnlyCollection<string> demandIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(demandIds);
        var rows = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => demandIds.Contains(row.DemandId))
            .Select(row => new { row.DemandId, row.Stage })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        string[] released = await ReleasedForRedispatch(dbContext).AsNoTracking()
            .Where(row => demandIds.Contains(row.DemandId))
            .Select(row => row.DemandId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> ended = new(
            rows.Where(row => row.Stage == JourneyRuntimeStage.Completed).Select(row => row.DemandId), StringComparer.Ordinal);
        ended.ExceptWith(rows.Where(row => row.Stage != JourneyRuntimeStage.Completed).Select(row => row.DemandId));
        ended.ExceptWith(released);
        return ended;
    }

    /// <summary>The memberships in force: not removed.</summary>
    public static IQueryable<JourneyDemandRow> Memberships(ControlServerDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return dbContext.Set<JourneyDemandRow>().Where(row => row.RemovedAt == null);
    }

    /// <summary>The journey this demand currently belongs to: none, or one.</summary>
    public static IQueryable<JourneyRuntimeRow> JourneyOf(ControlServerDbContext dbContext, string demandId)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        IQueryable<JourneyDemandRow> memberships = Memberships(dbContext);
        return dbContext.JourneyRuntimes.Where(journey => memberships.Any(
            membership => membership.JourneyId == journey.JourneyId && membership.DemandId == demandId));
    }

    /// <summary>The id of the journey this demand currently belongs to: none, or one.</summary>
    public static IQueryable<string> JourneyIdOf(ControlServerDbContext dbContext, string demandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        return Memberships(dbContext).Where(row => row.DemandId == demandId).Select(row => row.JourneyId);
    }

    /// <summary>The demands this journey currently carries.</summary>
    public static IQueryable<string> DemandIdsOf(ControlServerDbContext dbContext, string journeyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        return Memberships(dbContext).Where(row => row.JourneyId == journeyId).Select(row => row.DemandId);
    }

    /// <summary>
    /// Whether <paramref name="demandId"/> is the journey's last open demand: no other demand it carries is still open,
    /// that is neither <see cref="DemandExecutionStatus.Succeeded"/> nor <see cref="DemandExecutionStatus.Cancelled"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read it inside the write transaction whose save acts on the answer.</b> Two demands of one journey can end at the
    /// same moment on two connections -- an unload result on one, a cancellation on the other. Read outside the write lock,
    /// each sees the other still open, neither is last, and the journey is left with every demand ended and its vehicle
    /// still held. <c>BeginTransaction</c> on this store is <c>BEGIN IMMEDIATE</c>, so a read inside it sees every write
    /// committed before it and the second writer waits for the first.
    /// </para>
    /// <para>
    /// <b>Where each caller's transaction comes from.</b> The endings driven by an inbound line -- the unload result in
    /// <c>WireToGateStore.ApplyOperationResultAsync</c> and every ending through <c>PickupStopTermination</c> in
    /// <c>OnboardRecoveryCoordinator.ApplyCurrentResultAsync</c> -- run inside the inbox's transaction, which
    /// <c>CaptureFirstResponseAsync</c> opens before it processes the line; none of them opens one of its own. The
    /// runtime's own endings (the station deadline and the determinate load failure) open theirs explicitly, and
    /// <c>CompleteDemandAfterUnloadAsync</c> opens its own. A future caller that does neither would read this outside the
    /// write lock, and with several demands per journey that is the failure above -- so a new caller either holds a
    /// transaction or opens one.
    /// </para>
    /// <para>
    /// <b>It also sees the endings staged in this same change and not yet saved</b> (control-server#327). The query reads the
    /// store, so a demand the caller has just set <c>Cancelled</c> on a tracked row still reads as open there. The station
    /// deadline ends every outstanding demand of its stop in one change, one after another: read from the store alone, each
    /// saw the others still open, none was the last, and the journey was left with every demand ended, its vehicle held and
    /// its next round throwing on a stop with no leg after it. So the store's answer is corrected by what this context
    /// tracks -- the same rule <c>JourneyPlanRevisionStage</c> already follows by reading tracked rows.
    /// </para>
    /// <para>
    /// <b>What the correction covers, and no more.</b> One dimension: a demand whose tracked row carries a terminal
    /// <see cref="DemandExecutionStatus"/>. Memberships are not corrected -- a membership removed in this change still counts
    /// as the store has it. And the test is "tracked and terminal", not "staged and unsaved": a row tracked from an earlier
    /// save of the same context reads the same. That is only equivalent because every caller's context starts the change
    /// with nothing stale tracked (the runtime and the inbox clear tracking first); a caller that kept a long-lived context
    /// would need a sharper test.
    /// </para>
    /// </remarks>
    public static async Task<bool> IsLastOpenDemandAsync(
        ControlServerDbContext dbContext,
        string journeyId,
        string demandId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        string[] othersOpenInStore = await Memberships(dbContext)
            .Where(row => row.JourneyId == journeyId && row.DemandId != demandId)
            .Join(OpenDemands(dbContext), membership => membership.DemandId, demand => demand.DemandId,
                (membership, demand) => demand.DemandId)
            .TagWith(LastOpenDemandTag)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return othersOpenInStore.All(other => EndedInThisChange(dbContext, other));
    }

    /// <summary>Whether this context tracks <paramref name="demandId"/> with a terminal status not yet saved.</summary>
    private static bool EndedInThisChange(ControlServerDbContext dbContext, string demandId) =>
        dbContext.AcceptedDemands.Local.FirstOrDefault(row => row.DemandId == demandId)?.Status
            is DemandExecutionStatus.Succeeded or DemandExecutionStatus.Cancelled;
}
