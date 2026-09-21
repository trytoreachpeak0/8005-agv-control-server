using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 候选的业务键已有<b>别的</b> <c>DemandId</c> 被受理过——任何状态：进行中、成功、取消、释放待改派（批次7-05，control-server#210）。
/// </summary>
/// <remarks>
/// <para>
/// <b>这一条消除的是一个整轮级的隐患。</b><c>AcceptedDemands.TransportDemandKey</c> 是唯一索引，而第 10 道只按
/// <c>DemandId</c> 挡。MES 以新 <c>DemandId</c> 再发同一个业务键时，候选过完全部判据、被选中，受理存储层查到同键旧行、内容不同，
/// 抛 <c>BusinessIdentityConflictException</c>。control-server#231 之后那一抛只隔离出价的那辆车，可这条任务的每一个出价者都会
/// 走到同一处、各抛一次、各退出本轮——于是排在它后面的任务没有车可派，而且下一轮原样再来，直到那条需求离开目录。
/// 在链里挡住，受理就走不到那里。存储层那一抛留着：它守的是身份，只是正常流程不再碰到它。
/// </para>
/// <para>
/// <b>Order 12：在抑制（11）之后。</b>被取消的键两条都命中（有抑制、也有受理行），报更具体的「已抑制」。
/// 同一个 <c>DemandId</c> 自己不算——那是第 10 道的事，释放待改派的需求也正是靠这一点能被再派一次。
/// </para>
/// <para>
/// <b>每次都读库，不读轮次事实。</b>任务优先的派车轮逐条任务受理、受理后先保存再判下一条，所以本轮前面刚受理的同键需求已经
/// 落库，这里读得到；一份开轮时的快照就读不到它，那正是同一轮里两个同键 <c>DemandId</c> 的情形。
/// </para>
/// </remarks>
public sealed class TransportDemandKeyAlreadyAcceptedCriterion(ControlServerDbContext dbContext)
    : IDispatchAdmissionCriterion
{
    public const string Reason = DispatchReasonCodes.TransportDemandKeyAlreadyAccepted;

    public int Order => 12;

    public async Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        string key = evaluation.Candidate.TransportDemandKey;
        string demandId = evaluation.Candidate.DemandId;
        bool takenByAnother = await dbContext.AcceptedDemands.AsNoTracking()
            .AnyAsync(row => row.TransportDemandKey == key && row.DemandId != demandId, cancellationToken)
            .ConfigureAwait(false);
        return takenByAnother ? Reason : DispatchAdmissionChain.Eligible;
    }
}
