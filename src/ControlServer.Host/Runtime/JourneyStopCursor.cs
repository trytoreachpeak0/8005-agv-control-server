using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 一趟旅程此刻停在哪一个停靠，那个停靠带着哪些 id，旅程又还带着哪些需求（批次7-03，control-server#208）。
/// </summary>
/// <remarks>
/// <para>
/// 在这之前推进段是一台两段写死的状态机：每一个消息 id 都是「需求 id ＋ 用途」算出来的，取货那段读
/// <c>runtime.WorklistMessageId</c>、关卡那段读 <c>runtime.GateWorklistMessageId</c>，而「关卡」这两个字直接写在方法名里。
/// 多一个停靠这套算术就不成立。改成从这里取之后，发布方法只问「当前停靠的清单消息 id 是什么」，停靠是第几个、是取货还是
/// 卸货，都由 <c>JourneyStops</c> 的行回答。
/// </para>
/// <para>
/// <b>当前停靠是落库的状态，不再由阶段推出</b>（批次7-06，control-server#211）。批次7-03 用「阶段属于哪个角色的停靠」
/// 定位当前停靠，那只在「一趟恰好一个取货、一个卸货」时成立；途中追加会在序列中间插进新停靠，同一个阶段于是会在不同
/// 停靠上重复出现，阶段就不够用了。现在当前停靠是<b>序位最小的、还没完成的那一个</b>
/// （<see cref="JourneyStopRow.Status"/>），阶段只说「在这个停靠上走到哪一步」。
/// </para>
/// <para>
/// <b>「这个停靠此刻在装哪一条需求」也是落库的状态</b>：从属需求行的 <see cref="JourneyDemandStatuses.Loading"/>。
/// 装货那一条需要状态，是因为装哪一条由操作员扫了什么决定，服务端事后推不出来；卸货那一条不需要，因为卸货是服务端
/// 自己按顺序发的，「此刻在卸的」由已备好的卸货操作与侧的顺序推得出来（<see cref="NextToUnloadAtCurrentStopAsync"/>）。
/// 这个不对称是有理由的，不是漏了一半。
/// </para>
/// <para>
/// 读出来的行都是 <c>AsNoTracking</c> 的。要写停靠状态或需求状态，用 <see cref="ControlServerDbContext"/> 上被跟踪的
/// 那一份（<see cref="JourneyRuntimeEngine"/> 里的 <c>TrackedStopAsync</c> 与 <c>TrackedMembershipAsync</c>）。
/// </para>
/// </remarks>
internal sealed class JourneyStopCursor
{
    /// <summary>
    /// 一次停靠里跨需求卸货的侧的先后：先前侧、后后侧（规格第 20 节第 2 条按这两个分组名写）。不在里面的分组排在它们之后。
    /// </summary>
    private static readonly string[] UnloadSideOrder = ["FRONT", "REAR"];

    private readonly JourneyRuntimeRow _runtime;

    private JourneyStopCursor(
        JourneyRuntimeRow runtime,
        IReadOnlyList<JourneyStopRow> stops,
        IReadOnlyList<JourneyStopDemand> allDemands,
        bool everCarriedMoreThanOneDemand,
        IReadOnlySet<string> demandsThatLeft)
    {
        DemandsThatLeft = demandsThatLeft;
        _runtime = runtime;
        Stops = stops;
        AllDemands = allDemands;
        EverCarriedMoreThanOneDemand = everCarriedMoreThanOneDemand;
        Demands = [.. allDemands.Where(item =>
            item.Demand.Status is not (DemandExecutionStatus.Succeeded or DemandExecutionStatus.Cancelled))];
    }

    /// <summary>旅程的全部停靠，按 <see cref="JourneyStopRow.Sequence"/>。</summary>
    public IReadOnlyList<JourneyStopRow> Stops { get; }

    /// <summary>
    /// 未移除的全部归属，连同各自的需求行，<b>含已终结的</b>。重放白名单用它：白名单是「这趟旅程有权发的消息」，
    /// 一条需求终结不会把它的装卸命令变成别人的消息。用 <see cref="Demands"/> 去筛，终结的那一刻白名单就会缩水，
    /// 而缩水之后那条命令的补发就断了。
    /// </summary>
    public IReadOnlyList<JourneyStopDemand> AllDemands { get; }

    /// <summary>
    /// 这趟旅程是否曾经挂过不止一条需求：数的是全部归属，<b>含已移除的</b>（批次7-10，control-server#215）。
    /// </summary>
    /// <remarks>
    /// 引擎拿它判断「这趟旅程的计划可能被改写过」。只数未移除的那一份（<see cref="AllDemands"/>）等于假定需求只会增加
    /// ——释放改派把一条需求从旅程上移走、删掉它的停靠之后，剩下的是一条，而计划恰恰刚被改过。这个前提由这里承担：
    /// 归属行只会被标移除、从不删行（<c>JourneyMembershipStore.RemoveDemandAsync</c>），所以「曾经有过」按构造数得出来。
    /// </remarks>
    public bool EverCarriedMoreThanOneDemand { get; }

    /// <summary>曾经挂在这趟旅程上、归属已被移除的需求（批次7-10，control-server#215）。</summary>
    public IReadOnlySet<string> DemandsThatLeft { get; }

    /// <summary>
    /// 旅程此刻还带着的需求：归属未被移除，且需求本身还没终结（<see cref="DemandJourneyLookup.OpenDemands"/> 的那个定义）。
    /// 清单项与录入请求的期待子批取它。
    /// </summary>
    public IReadOnlyList<JourneyStopDemand> Demands { get; }

    /// <summary>
    /// 此刻推进的那个停靠：序位最小的、还没完成也没被移除的那一个。一趟旅程的每个停靠都完成之后就没有当前停靠了，
    /// 取它会抛——那一刻旅程已经 <see cref="JourneyRuntimeStage.Completed"/>，<c>AdvanceAsync</c> 在那之前就返回了。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是本轮加载那一刻的答案，而推进段在一轮里会把停靠推完。</b><c>AdvanceAsync</c> 用 <c>goto case</c> 把几个
    /// 阶段串在一轮里推，其中一条（装货落定后回到等录入）现在还会跨停靠。所以完成一个停靠之后要重新加载游标，
    /// 而不是接着用手上这一份——接着用会拿上一个停靠的 id 发报文，测试照绿，只有车上收到的东西不对。
    /// </para>
    /// <para>
    /// 角色不再参与定位。<see cref="RoleOf"/> 把阶段映到角色，本来是想当一道自检用的——<b>而它今天没有任何调用点</b>
    /// （批次7-06 查证，control-server#211），所以它不自检任何东西，只是一张表。
    /// </para>
    /// <para>
    /// <b>那张表仍然是承重的</b>：`OnboardRecoveryCoordinator` 里在途取消那一段靠
    /// <c>stop.Stage is not (AwaitingSublot or AwaitingLoadResult)</c> 保证自己只在取货停靠上走到，
    /// 而「那两个阶段蕴含当前停靠是取货停靠」这件事，全仓只有这张表表达。改了它，那道护栏就失效，
    /// 而在 <c>JourneyStopEntryRequestIdTests.TheStagesThatMeanAPickupStopAreTheOnesTheRecoveryGuardNames</c>
    /// 之前，没有任何东西会因此变红。
    /// </para>
    /// </remarks>
    public JourneyStopRow Current => Stops.FirstOrDefault(IsOpen)
        ?? throw new InvalidDataException($"Journey '{_runtime.JourneyId}' has no open stop left.");

    /// <summary>还没完成也没被移除的停靠，按序位。</summary>
    public IReadOnlyList<JourneyStopRow> OpenStops => [.. Stops.Where(IsOpen)];

    /// <summary>当前停靠上挂着的、还没终结的需求，按加入旅程的先后。</summary>
    public IReadOnlyList<JourneyStopDemand> CurrentStopDemands => AtStop(Current);

    /// <summary>
    /// 当前停靠上<b>这个停靠的作业还没做完</b>的需求，按加入旅程的先后：取货停靠是还没装完的，卸货停靠是装了还没卸的。
    /// </summary>
    /// <remarks>
    /// 清单项与录入请求的期待子批取它，不取 <see cref="CurrentStopDemands"/>。批次7-03 取的是「还没终结的」，因为那时
    /// 装货闭环还没落到从属需求行上，「未装」与「未终结」分不开；一个停靠一条需求时两者恒等。现在分得开了，而分不开
    /// 的那一版会让操作员在清单上看见一条刚装完的需求，再扫一次。
    /// </remarks>
    public IReadOnlyList<JourneyStopDemand> OutstandingAtCurrentStop =>
        [.. ProgressAtStop(Current).Where(IsOutstandingAt(Current))];

    /// <summary>
    /// 当前停靠此刻正在装的那一条：录入已受理、装货命令已发、结果没到。没有正在装的就是空。
    /// </summary>
    /// <remarks>
    /// 至多一条——同一站多条需求逐条串行（规格第 22 节补记）。多于一条是这台服务器自己的不变量被破坏了，所以抛而不是
    /// 挑一条：两条同时在装意味着两条开仓命令同时在飞，而操作员面前只有一排仓门。
    /// </remarks>
    public JourneyStopDemand? LoadingAtCurrentStop => ProgressAtStop(Current)
        .SingleOrDefault(item => item.Membership.Status == JourneyDemandStatuses.Loading);

    /// <summary>
    /// 当前停靠此刻该卸的那一条：已经备好卸货操作的那一条，没有就按侧排在最前的那条装了还没卸的。卸货逐条串行，
    /// 顺序由服务端定，所以不需要一个「正在卸」的状态——见类注释里那段不对称的理由。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>顺序先前侧后后侧，同侧之间按加入旅程的先后</b>（control-server#303；规格第 20 节，REQ-0357、ADR-cross-0061，第 20.2 节表中
    /// 「第 5.1 节第 4 条」一行）。同一条指令内的先后由车载端执行器保证；一次停靠里<b>跨需求</b>的先后只有服务端发命令的顺序决定，
    /// 所以排在这里。侧从需求的目标仓位（卸货命令开的就是它们）经车的仓位分组读（<see cref="VehicleSlotPositionReader"/>，
    /// 派车与按侧判满用的同一个取法），不按仓号区间推；跨两侧的需求排在只有前侧与只有后侧的之间（方法体里写了理由）。
    /// </para>
    /// <para>
    /// <b>排不出侧时退回加入先后，但不静默</b>（cs#303 审查）：车的仓位模型解析不出来，或仓位不在模型里、分组不是前后两组，
    /// <see cref="UnloadChoice.UnorderedReason"/> 说是哪一种，发卸货命令的那一处（<c>JourneyRuntimeEngine.PublishUnloadCommandAsync</c>）
    /// 记一条 Warning。只在真要选下一条卸谁的那一刻记，所以一个停靠至多记「需求条数减一」次，不随每轮推进刷屏。
    /// </para>
    /// <para>
    /// <b>排序要读的两样（已备好的操作、车的仓位分组）只在这里读</b>，也就是推进段真要问「该卸哪一条」的时候：停在卸货站、
    /// 走卸货那几步。游标在车行驶的每一轮都会加载，放在加载里读就是整段路上每轮多读两次（cs#303 审查）。当前停靠上至多一条
    /// 已装需求时一次都不读，答案按构造与修之前相同。
    /// </para>
    /// <para>
    /// <b>已经备好仓位操作的那一条优先于排序。</b>「此刻在卸的」没有落库的状态，是每轮重算的；而排序依据（车的仓位分组）
    /// 可以在一次停靠中途变，升级那一刻在卸的那条也是旧版本按加入先后挑的。只按排序取，重算出来的「第一条」可能不是已经
    /// 发出去的那一条，推进段就去等一条从没发过的命令的结果——旅程静默停住，已发出那条的结果也没人结算。所以一次一条、
    /// 前一条闭环才发下一条这个保证由构造承担：排序只决定<b>下一条</b>发给谁
    /// （<c>Batch7UnloadSideOrderTests.AnUnloadAlreadyCommandedIsSeenThroughBeforeTheSideOrderPicksTheNextOne</c>）。
    /// </para>
    /// <para>
    /// <b>装货没有对应的排序</b>：装哪一条由操作员扫了什么决定（<see cref="LoadingAtCurrentStop"/>），跨需求的先后是扫码的
    /// 先后，服务端不改它。
    /// </para>
    /// </remarks>
    public async Task<UnloadChoice> NextToUnloadAtCurrentStopAsync(
        ControlServerDbContext dbContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        JourneyStopDemand[] loaded =
            [.. ProgressAtStop(Current).Where(item => item.Membership.Status == JourneyDemandStatuses.Loaded)];
        if (loaded.Length <= 1 || Current.StopRole != JourneyStopRoles.Unload)
        {
            return new UnloadChoice(loaded.FirstOrDefault(), null, []);
        }

        string[] attempts = [.. loaded.Select(item => item.Membership.UnloadSlotOperationAttemptId)];
        HashSet<string> prepared = new(
            await dbContext.StationOperations.AsNoTracking()
                .Where(row => attempts.Contains(row.SlotOperationAttemptId))
                .Select(row => row.SlotOperationAttemptId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false),
            StringComparer.Ordinal);
        if (loaded.FirstOrDefault(item => prepared.Contains(item.Membership.UnloadSlotOperationAttemptId)) is { } inFlight)
        {
            return new UnloadChoice(inFlight, null, []);
        }

        VehicleSlotPositions? positions = await new VehicleSlotPositionReader(dbContext)
            .ReadAsync(_runtime.AgvId, cancellationToken).ConfigureAwait(false);
        if (positions is null)
        {
            return new UnloadChoice(
                loaded[0],
                UnloadOrderFallbackReasons.SlotModelUnresolved,
                [.. loaded.Select(item => item.Demand.DemandId)]);
        }

        (JourneyStopDemand Item, int[] Ranks)[] ranked =
        [
            .. loaded.Select(item => (item, (JsonSerializer.Deserialize<int[]>(item.Membership.TargetSlotsJson) ?? [])
                .Select(slot => SideRankOf(positions, slot))
                .DefaultIfEmpty(UnloadSideOrder.Length)
                .ToArray()))
        ];
        string[] unknown =
        [
            .. ranked.Where(entry => entry.Ranks.Contains(UnloadSideOrder.Length))
                .Select(entry => entry.Item.Demand.DemandId)
        ];
        // 先按最先开的那一扇、再按最后开的那一扇排。跨两侧的需求在一条命令内先前后后（车载端执行器按规格第 20 节第 2 条），
        // 所以它要夹在「只有前侧」与「只有后侧」之间，整站开门的次序才单调：前侧、（跨侧的前、后）、后侧。只按最先那一扇排，
        // 跨侧的会与只有前侧的按加入先后混排，开出前、后、前；只按最后那一扇排，它会排到只有后侧的后面，开出后、前、后。
        // OrderBy 是稳定排序：两扇都同侧的保持 ProgressAtStop 给的加入先后。
        JourneyStopDemand next = ranked
            .OrderBy(entry => entry.Ranks.Min())
            .ThenBy(entry => entry.Ranks.Max())
            .First().Item;
        return new UnloadChoice(next, unknown.Length > 0 ? UnloadOrderFallbackReasons.SlotSideUnknown : null, unknown);
    }

    /// <summary>这个停靠上挂着的全部归属（含已终结的），按加入旅程的先后。清单的版数按它数。</summary>
    public IReadOnlyList<JourneyStopDemand> AllAtStop(JourneyStopRow stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        return [.. AllDemands.Where(demand => StopIdOf(demand.Membership, stop.StopRole) == stop.StopId)];
    }

    /// <summary>一条需求在某个停靠上的作业做完了没有：取货停靠看装，卸货停靠看卸。</summary>
    /// <remarks>
    /// <para>
    /// <b>已取消的需求在哪个停靠上都没有作业可做</b>，不管它的归属行写成什么。这一条不是为了对称，是为了兜住
    /// 「需求终结了、归属行没跟着写」的库：批次 7 的迁移回填在途旅程时按受理时的形状写归属，而一趟在途旅程的某条需求
    /// 可能早就取消了。没有这一条，升级之后那趟旅程会停在一个「还有东西要卸」的停靠上等一条永远不会来的结果。
    /// </para>
    /// <para>
    /// <b><see cref="DemandExecutionStatus.Succeeded"/> 故意不在里面。</b>卸货结果落定的那一刻，需求已经是
    /// <c>Succeeded</c> 而归属行还是 <c>LOADED</c>——正是要拿它去结算那条卸货命令的时刻。把它也算成「做完了」，
    /// 推进段会在那一轮找不到该结算的那一条。两者的不对称是这个时间差本身，不是漏了一半。
    /// </para>
    /// </remarks>
    public static bool IsDoneAt(JourneyStopRow stop, JourneyStopDemand item)
    {
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(item);
        if (item.Demand.Status == DemandExecutionStatus.Cancelled ||
            item.Membership.Status == JourneyDemandStatuses.Terminated)
        {
            return true;
        }
        return stop.StopRole == JourneyStopRoles.Pickup
            ? item.Membership.Status is JourneyDemandStatuses.Loaded or JourneyDemandStatuses.Unloaded
            : item.Membership.Status == JourneyDemandStatuses.Unloaded;
    }

    /// <summary>
    /// 清单这条流在 <paramref name="stop"/> 上<b>此刻</b>发的是第几号（批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 一个停靠上挂 N 条需求，清单就发 N 版：到站一版（N 条待做），此后每做完一条再发一版，做完最后一条不发——那时清单
    /// 空了，车直接离站。所以号数 = 基准 + 前面每个停靠发过的版数 + <b>本停靠已做完的条数</b>。
    /// </para>
    /// <para>
    /// <b>集合一变号就升，是这条算式自己保证的</b>（票面第 13 条）：清单项与录入请求的期待子批取的都是
    /// <see cref="OutstandingAtCurrentStop"/>，做完一条，集合少一条，偏移加一。<c>SublotEntryRequested</c> 的业务
    /// 去重键是 <c>(OperationSessionId, WorklistRevision)</c>，同一个键下集合不许变——这条算式让「集合变了」与
    /// 「键变了」成为同一件事，而不是两件要互相记得的事。
    /// </para>
    /// <para>
    /// 单需求两停靠下：取货停靠一条需求、发一版，号数是基准；卸货停靠前面一版、号数是基准 +1。与批次7-03 的
    /// <c>基准 + 序位 - 1</c> 逐字相同。
    /// </para>
    /// </remarks>
    public long WorklistRevisionAt(long journeyBase, JourneyStopRow stop) =>
        FirstWorklistRevisionAt(journeyBase, stop) + DoneAt(stop);

    /// <summary>这个停靠的第一版清单是第几号。</summary>
    public long FirstWorklistRevisionAt(long journeyBase, JourneyStopRow stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        return journeyBase + Stops.Where(earlier => earlier.Sequence < stop.Sequence).Sum(WorklistVersionsOf);
    }

    /// <summary>一个停靠上清单一共发几版：挂在它上面的需求有几条就几版，至少一版。</summary>
    public long WorklistVersionsOf(JourneyStopRow stop) => Math.Max(1, AllAtStop(stop).Count);

    /// <summary>这个停靠上已经做完本停靠作业的需求有几条。</summary>
    public long DoneAt(JourneyStopRow stop) => AllAtStop(stop).Count(item => IsDoneAt(stop, item));

    /// <summary>
    /// 一条录入提交要答复当前停靠，必须对上的那组事实：车、作业会话、站点，以及本停靠<b>发过的任一版</b>修订号。
    /// </summary>
    /// <remarks>
    /// 版号写成区间而不是「当前那一版」：升版前就上路的提交带的是旧版号，它是对旧版清单的合法答复，判成「不是本停靠的」
    /// 会让服务端一直等一个已经到了的录入。已经答复过的那些由消费记录挡住，已经做完的那条由
    /// <see cref="OutstandingAtCurrentStop"/> 挡住，两道都比「按号数卡」准。
    /// </remarks>
    public StopEntryAddress EntryAddressOfCurrentStop(long journeyBase)
    {
        JourneyStopRow stop = Current;
        long first = FirstWorklistRevisionAt(journeyBase, stop);
        return new StopEntryAddress(
            _runtime.AgvId, stop.OperationSessionId, stop.StationId, first, first + WorklistVersionsOf(stop) - 1);
    }

    /// <summary>
    /// 这个停靠上第 <paramref name="revision"/> 版清单的消息 id；录入请求同理，只是用途不同。
    /// </summary>
    /// <remarks>
    /// <b>第一版用停靠行上的那一个</b>，后面的才派生。停靠行上的 id 是受理时从旅程行搬来的，单需求旅程只发一版，于是
    /// 发出去的 id 与批次7-03 之前逐字相同——那是 <c>WirePin</c> 钉着的东西。派生用停靠 × 修订号当去重键
    /// （不是 attempt，见记忆 <c>outbox-message-id-unique</c> 那次教训）。
    /// </remarks>
    public string WorklistMessageIdAt(long journeyBase, JourneyStopRow stop, long revision)
    {
        ArgumentNullException.ThrowIfNull(stop);
        return revision == FirstWorklistRevisionAt(journeyBase, stop)
            ? stop.WorklistMessageId
            : JourneyPlanBuilder.StableGuid($"{stop.StopId}|{revision}", "worklist");
    }

    /// <inheritdoc cref="WorklistMessageIdAt"/>
    public string SublotRequestMessageIdAt(long journeyBase, JourneyStopRow stop, long revision)
    {
        ArgumentNullException.ThrowIfNull(stop);
        return revision == FirstWorklistRevisionAt(journeyBase, stop)
            ? stop.SublotRequestMessageId
              ?? throw new InvalidDataException($"Stop '{stop.StopId}' asks for an entry but has no request id.")
            : JourneyPlanBuilder.StableGuid($"{stop.StopId}|{revision}", "sublot-entry");
    }

    /// <summary>当前停靠此刻这一版录入请求的消息 id——终结时要结算的就是它。</summary>
    public string CurrentSublotRequestMessageId(long journeyBase) =>
        SublotRequestMessageIdAt(journeyBase, Current, WorklistRevisionAt(journeyBase, Current));

    /// <summary>
    /// 同上，但当前停靠<b>本来就不做录入</b>时给 null，而不是抛（批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 给那些「车停在哪个停靠上都可能发生」的终结路径用——故障货物交接就是一例，它在卸货端与取货端都会发生
    /// （<see cref="PickupStopTermination"/> 的 remarks 写着这件事）。卸货停靠没有录入请求，因此也没有
    /// 要结算的那一条，null 是这里正确的答案而不是降级。
    /// </para>
    /// <para>
    /// <b>判的是停靠的角色，不是那一列空不空</b>，这是两件事：按角色判，一个<b>取货</b>停靠缺 id 仍然会抛，
    /// 那道护栏一字未动；按空不空判，它会连同真正的缺失一起吞掉，而那正是护栏存在的理由。
    /// </para>
    /// </remarks>
    public string? CurrentSublotRequestMessageIdOrNone(long journeyBase) =>
        Current.StopRole == JourneyStopRoles.Unload
            ? null
            : CurrentSublotRequestMessageId(journeyBase);

    private static bool IsOpen(JourneyStopRow stop) =>
        stop.Status is not (JourneyStopStatuses.Completed or JourneyStopStatuses.Removed);

    private static Func<JourneyStopDemand, bool> IsOutstandingAt(JourneyStopRow stop) =>
        item => !IsDoneAt(stop, item);

    /// <summary>一个仓位在 <see cref="UnloadSideOrder"/> 里排第几；不在车的仓位模型里、或分组不在其中的，排在最后。</summary>
    private static int SideRankOf(VehicleSlotPositions positions, int slot) =>
        positions.SlotPositionByPhysicalSlot.TryGetValue(slot, out string? side) &&
        Array.IndexOf(UnloadSideOrder, side) is var rank and >= 0
            ? rank
            : UnloadSideOrder.Length;

    public IReadOnlyList<JourneyStopDemand> AtStop(JourneyStopRow stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        return [.. Demands.Where(demand => StopIdOf(demand.Membership, stop.StopRole) == stop.StopId)];
    }

    /// <summary>
    /// 这个停靠上还没做完本停靠作业的归属，按加入旅程的先后——<b>按归属行的状态判，不按需求的执行状态</b>。
    /// </summary>
    /// <remarks>
    /// 两者是两件事，而它们在一个时刻必然分岔：卸货结果刚落定的那一轮，需求已经是
    /// <see cref="DemandExecutionStatus.Succeeded"/>，而归属行还停在 <c>LOADED</c>——正是要拿它去结算那条卸货命令的
    /// 时刻。按需求状态筛会在这里筛空，于是推进段对着一个「没有东西可卸」的停靠抛。所以本停靠的进度只由
    /// <see cref="JourneyDemandRow.Status"/> 回答，需求终结时由 <see cref="PickupStopTermination"/> 把它写成
    /// <see cref="JourneyDemandStatuses.Terminated"/>。
    /// </remarks>
    private IReadOnlyList<JourneyStopDemand> ProgressAtStop(JourneyStopRow stop) =>
        [.. AllDemands.Where(demand => StopIdOf(demand.Membership, stop.StopRole) == stop.StopId)];

    /// <summary>
    /// 锚需求在这趟旅程里的归属行。装卸命令与离站核验的 <c>demandId</c> 都取它。
    /// </summary>
    /// <remarks>
    /// 从<b>未移除的全部</b>归属里找，不是从 <see cref="Demands"/>——一条需求终结之后它的归属行仍然是它那几个 id 的载体，
    /// 而推进段可能正要拿它去结算刚发出去的那条命令。用未终结的那一份去找，终结的那一刻这里就开始抛。
    /// 归属被移除（批次7-10 的改派）才是真的找不到了。
    /// </remarks>
    public JourneyStopDemand Anchor(string demandId) =>
        AllDemands.SingleOrDefault(demand => demand.Membership.DemandId == demandId)
        ?? throw new InvalidDataException($"Demand '{demandId}' is not carried by this journey any more.");

    public static Task<JourneyStopCursor> LoadAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        CancellationToken cancellationToken) =>
        LoadAsync(dbContext, runtime, includeUnsavedChanges: false, cancellationToken);

    /// <summary>
    /// 同 <see cref="LoadAsync(ControlServerDbContext, JourneyRuntimeRow, CancellationToken)"/>，但读到的是<b>本上下文还没保存的</b>
    /// 那一份：终结刚暂存、归属行刚改成 <c>TERMINATED</c>、停靠刚标成已删（control-server#323）。
    /// </summary>
    /// <remarks>
    /// 给旅程收尾算清单号用：收尾与终结同一次保存，而默认那一份 <c>AsNoTracking</c> 读库，看不见刚终结的那一条——按它算，
    /// 本停靠的「已做完」少一条，算出来的正是车上那一版清单的号与 id，同号不同内容。做法是用跟踪查询：EF 对已跟踪的行返回
    /// 被跟踪的那个实例、不拿库里的值覆盖它，游标在客户端做的筛选（未移除、未终结、停靠开没开）于是读到的是改过的值。
    /// 代价是它会把读到的行一并挂进跟踪器（状态 Unchanged），调用方那一次保存不会因此多写任何一列。
    /// </remarks>
    public static async Task<JourneyStopCursor> LoadIncludingUnsavedChangesAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        CancellationToken cancellationToken) =>
        await LoadAsync(dbContext, runtime, includeUnsavedChanges: true, cancellationToken).ConfigureAwait(false);

    private static async Task<JourneyStopCursor> LoadAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        bool includeUnsavedChanges,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(runtime);
        IQueryable<JourneyStopRow> stopRows = includeUnsavedChanges
            ? dbContext.Set<JourneyStopRow>()
            : dbContext.Set<JourneyStopRow>().AsNoTracking();
        IQueryable<JourneyDemandRow> membershipRows = includeUnsavedChanges
            ? dbContext.Set<JourneyDemandRow>()
            : dbContext.Set<JourneyDemandRow>().AsNoTracking();
        IQueryable<AcceptedDemandRow> demandRows = includeUnsavedChanges
            ? dbContext.AcceptedDemands
            : dbContext.AcceptedDemands.AsNoTracking();
        JourneyStopRow[] stops = await stopRows
            .Where(row => row.JourneyId == runtime.JourneyId)
            .OrderBy(row => row.Sequence)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (stops.Length == 0)
        {
            // 受理事务在写旅程行的同一次保存里写两个停靠（WireToGateStore.AcceptAsync），迁移也把在途旅程回填了
            // （Batch7MultiDemandJourneyPersistence），所以一趟没有停靠的旅程是库坏了，不是一种要兜的情况。
            throw new InvalidDataException($"Journey '{runtime.JourneyId}' has no stops.");
        }

        // 归属连着需求行一起取：受理事务同一次保存写下归属与需求，所以这个内连接丢不了行。按加入旅程的先后排，
        // 同一刻加入的再按需求 id 定序；排序在客户端做，因为 SQLite 不接受 DateTimeOffset 的 ORDER BY，而一趟旅程的
        // 归属至多几条，取回来再排没有代价。
        // 已移除的归属一起取回来，只为数「曾经有过几条」；AllDemands 在客户端筛掉它们。
        JourneyStopDemand[] everCarried = await membershipRows
            .Where(row => row.JourneyId == runtime.JourneyId)
            .Join(
                demandRows,
                membership => membership.DemandId,
                demand => demand.DemandId,
                (membership, demand) => new JourneyStopDemand(membership, demand))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new JourneyStopCursor(
            runtime,
            stops,
            [.. everCarried
                .Where(row => row.Membership.RemovedAt == null)
                .OrderBy(row => row.Membership.AddedAt)
                .ThenBy(row => row.Membership.DemandId, StringComparer.Ordinal)],
            everCarried.Length > 1,
            new HashSet<string>(
                everCarried.Where(row => row.Membership.RemovedAt != null).Select(row => row.Membership.DemandId),
                StringComparer.Ordinal));
    }

    /// <summary>
    /// 哪个阶段属于哪个角色的停靠。七个阶段里，前五个在取货停靠上（到站、等录入、等装货结果、等离站、等离站核验），
    /// 后两个在卸货停靠上（到站、等卸货结果）。批次7-06 起它只用于自检，不再用于定位。
    /// </summary>
    public static string RoleOf(JourneyRuntimeStage stage) => stage switch
    {
        JourneyRuntimeStage.AwaitingPickupArrival or
        JourneyRuntimeStage.AwaitingSublot or
        JourneyRuntimeStage.AwaitingLoadResult or
        JourneyRuntimeStage.AwaitingStationDeparture or
        JourneyRuntimeStage.AwaitingDepartureSafety => JourneyStopRoles.Pickup,
        JourneyRuntimeStage.AwaitingGateArrival or
        JourneyRuntimeStage.AwaitingUnloadResult => JourneyStopRoles.Unload,
        _ => string.Empty
    };

    private static string StopIdOf(JourneyDemandRow membership, string stopRole) => stopRole == JourneyStopRoles.Pickup
        ? membership.PickupStopId
        : membership.UnloadStopId;
}

/// <summary>一条需求在这趟旅程里的归属，连同需求本身。</summary>
internal sealed record JourneyStopDemand(JourneyDemandRow Membership, AcceptedDemandRow Demand);

/// <summary>
/// 当前停靠此刻该卸的那一条，以及排不出侧时的原因（control-server#303）。
/// </summary>
/// <param name="Next">该卸的那一条；没有已装需求时为空。</param>
/// <param name="UnorderedReason">
/// 为空表示次序按侧排出来了，或者用不着排；否则是 <see cref="UnloadOrderFallbackReasons"/> 之一，那几条需求之间退回了加入先后。
/// </param>
/// <param name="UnorderedDemandIds">排不出侧的需求。</param>
internal sealed record UnloadChoice(
    JourneyStopDemand? Next,
    string? UnorderedReason,
    IReadOnlyList<string> UnorderedDemandIds);

/// <summary>卸货次序退回加入先后的原因，写进 Warning（control-server#303）。</summary>
internal static class UnloadOrderFallbackReasons
{
    /// <summary>车的仓位模型解析不出来，所有需求都排不出侧。</summary>
    public const string SlotModelUnresolved = "SLOT_MODEL_UNRESOLVED";

    /// <summary>有需求的目标仓位不在车的仓位模型里，或所在分组不是 FRONT／REAR。</summary>
    public const string SlotSideUnknown = "SLOT_SIDE_UNKNOWN";
}

/// <summary>
/// 一条录入提交要答复某个停靠，必须对上的那组事实（批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// 在这之前这组事实是旅程行上的四个列，而其中的清单修订号在一个停靠上只有一个值。一个停靠会发不止一版清单之后，
/// 「对上」就成了一个区间，而把这组事实收成一个类型，是为了两个读者（推进段与取消授权）不会各自比各自的那几列。
/// </remarks>
internal readonly record struct StopEntryAddress(
    string AgvId,
    string OperationSessionId,
    string StationId,
    long FirstWorklistRevision,
    long CurrentWorklistRevision)
{
    /// <summary>这个号是不是本停靠发过的某一版。</summary>
    public bool Covers(long worklistRevision) =>
        worklistRevision >= FirstWorklistRevision && worklistRevision <= CurrentWorklistRevision;
}
