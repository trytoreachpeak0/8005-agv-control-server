## 进度
- 当前提交：审查返工 543e5dd15（返工用例 c440225fa 在先、先红）；此前 da8f95417（合入 fp/v2-impl 20df70a70）、测试提交 487d5f13a、实现提交 08a8b3c15
- 证据：`evidence/cs830/`（测试提交上的红、绿、变异、合成 L2 红绿；返工的红、绿与 16 个变异在 `rework/`）
- 下一步：返工已报调度复核；放行后推送、转 ready、跑唯一一轮 CI

实现 trytoreachpeak0/8005-agv-control-server#830（进 fp/v2-impl，需手动关票）。追踪 trytoreachpeak0/8005-agv-program#210；语义 CP-0021（trytoreachpeak0/8005-agv-program#213，REQ-0367、修订后的 REQ-0211 与 REQ-0352）；协议 trytoreachpeak0/8005-agv-protocol#26。

## 合入前置与已知空窗
- **合入须等 protocol-v6.0.0 发布后改绑 tag。**现在绑的是候选：`8005-agv-protocol` 合并提交 `98f172642937994ed01668d81fe41e69cf089e10`（protocol PR #27，内容提交 `13f52e67`），6.0.0 / protocolVersion 7，`SUPERSEDING_CANDIDATE`。与车载端 hmi#388 绑的是同一个提交（握手比 commit）。用户 10-21 定：两端与真装置跑通后才打 tag。
- **与车载端 trytoreachpeak0/8005-agv-onboard-hmi#388 同包换装**：两端精确身份绑定；换装前两端发件箱都要清空（没有未确认的 RELIABLE），停服一起换。
- **真装置场景待与 hmi#388 联跑**（调度统一安排，同一组提交）：`real-onboard-load-door-closed-empty-reopens`、`real-onboard-cancellation-authorization-lost`、`real-onboard-staging-row-cancel`，以及车载端票发起的 `real-onboard-compensate-then-reconnect`；受影响的 G3（`g3-load-cancellation`，staged G3 runner）同样等两端合起来。
- **与 cs#837 的边界**（Coordinator13 10-22 定）：两票只在「`LoadCancellationAuthorization` 的 `slots` 置空」这一行重叠。本票只做换装逼出来的置空，和 wire-notes 明文要求的「减篮待判或已授权时拒绝取消（`LOAD_CANCELLATION_BASKET_LOADED`）」；「已经装进一篮就拒绝取消」的通用判定、检查顺序 `SLOT_STATE_UNKNOWN → LOAD_CANCELLATION_BASKET_LOADED → LOCK_NOT_CLOSED → UNLOCK_OUTPUT_NOT_RESET` 与向量 `CV-LOAD-CANCELLATION-BASKET-LOADED` 归 cs#837（向量已登记进 `VectorsAwaitingTheirSlice`，label `FP-IS-02, claimed by 8005-agv-control-server#837`）。**已知空窗：本票合入后到 cs#837 合入前，没有申请减篮时装了一篮仍可能授权取消。**两票都在这个版本里，部署前都会合入。
- **两个车载端场景在 cs#837 与 hmi#392 合入前会红，不进本票与 hmi#388 的联跑**（审查 M3，调度定）：`g3-load-cancellation.ps1` 与 `real-onboard-load-door-closed-empty-reopens.ps1` 的协议可见期待本票已按 6.0.0 改（取消授权 `slots` 为空），而它们走到的取消判定与车载端在空范围下的取消结果分别由 cs#837、hmi#392 交付；两票合入后再跑。
- 不占迁移通道：没有新表、没有迁移（见下「存储」）。

## 做了什么

**协议换装**（`fb39ed5f1`、`00a7febd4`）：vendor 三份用 `git archive` 从 `98f17264` 导出、按 manifest 文件表核对 76/76 后整份拷入；`ProtocolCandidateIdentity`、`appsettings.json` 的 `ProtocolCandidate`、错误码表（+3）、架构钉值（schema 树摘要、index 哈希、61 条目 / 57 去重 / 4 条共享、69 条消息）一并改。

**减篮请求与判定**（`RemainingBasketsSkips`、`RemainingBasketsSkipRule`）：
- 入站 `RemainingBasketsSkipRequested`：持久、回 `DurableAck`；同号不同内容 → `ProtocolProblem BUSINESS_ID_CONTENT_CONFLICT`、不答结果；目标仓读数过期就附一条 `SafetyStateSnapshotRequested`。
- 判定在应答之后另开写事务，先后照 wire-notes：`DEMAND_NOT_CURRENT` → `OPERATION_SESSION_MISMATCH` → `ACTION_NOT_ALLOWED_IN_STATE`（N = 1；这次装货已有结果；已转人工恢复＝异常处置会话开着或人工判故障待答／生效／未对上；纠错或取消在处理；已授权过一次）→ `SLOT_STATE_UNKNOWN`（含读不到）→ `LOCK_NOT_CLOSED` → `UNLOCK_OUTPUT_NOT_RESET` → `REMAINING_BASKETS_SKIP_NOTHING_LOADED` → `SLOT_SET_INVALID`（`loadedSlots` 与服务端读到的不符，或全部装满）。
- **事实来源**（`SessionSlotReadings`）：当前会话、这次装货开始（装货操作 `CreatedAt`）之后收到的最新一份 `SafetyStateSnapshot` 的逐仓读数；下限在库里按 `(MessageType, ReceivedAtUtcTicks)` 索引筛，收件箱不整表读回（审查意见）。取装货开始而不取会话开始：会话行没有开始时刻，而装货开始之前的快照说明不了装进了什么，本来也不能用；两边都是服务端时钟。那份快照之后有 `SafetyStateChanged` 的 `affectedSlots` 动过目标仓，就算过期——先要快照、不判，快照到了（应答之后）再判。要的快照没来（真车载端有未确认的 `SafetyStateChanged` 时不答快照请求），就照 cs#478 的间隔 `SafetySnapshotReaskInterval`（5 秒）在车的下一条报文上再要，直到判出（审查 M2）。`SafetyStateChanged` 不带逐仓状态，所以只能这样。判定不读会话就绪、旅程原因码与阶段计时；读的是旅程阶段 `AwaitingLoadResult`、本停靠在装的那一条、这次装货的操作与读数（有一条 L1 证会话因本服务端自己的在途单未就绪时照样判）。
- 答复暂存进发件箱、与判定同一次提交、提交之后发；messageId 由 `skipRequestId` 派生，同号同内容重提原样重放存下那一行（已确认过也重发，代次变了就由补发改写代次）；车还没确认的随重连后恢复报告之后的补发再发（`ReplayPendingCommandsAsync`）。
- **待判请求的出口**：快照到了；这次装货的 `OperationResult` 先到（结果照常结算，请求随后判 `ACTION_NOT_ALLOWED_IN_STATE`）；车重连后以新 messageId 重提。

**结算**（`WireToGateStore.ApplyOperationResultAsync`、`AuthorizedSkipSettlesAsync`、`CommitSkippedLoadAsync`）：`COMPLETED_REMAINING_SKIPPED` 的装货结果，按 (demandId, attempt) 在发件箱找唯一那次 `AUTHORIZED`；attempt 此前没有任何结果（授权作废的判据：以别的结果结算过就不再用）、结果恰好是授权的形状（授权的仓 `COMPLETED`/`OCCUPIED`，其余 `NOT_STARTED`/`EMPTY`/带 `REMAINING_BASKETS_SKIPPED`，全部 `LOCKED`/`RESET`，合起来恰好是目标仓）才提交为 k 篮。否则走原来的路：不是 `COMPLETED` → 转人工恢复，旅程挡在 `LOAD_RESULT_REQUIRES_RECOVERY`。
- 提交时把**装货操作与归属行的目标仓位一起改成 k 个仓**（归属行另记 `LoadedSlotsJson`）。凡是读「已提交装货的目标仓位＝货在哪」的地方因此一律看到 k 个：卸货命令（只开 k 仓、`expectedBasketCount` k）、派车与扫码后分仓用的账本 `JourneyAwareSlotLedger`（其余仓立刻可分）、待送点判满 `SlotRoomAtStaging`（cs#800 之后只按已装货物，同一列）、自家单重建的货证、纠错与恢复会话的仓位范围。需求的 N 不改，清单上照旧显示 N（wire-notes）。需求照常往下走：不取消、不抑制、不进人工搬运告警，卸完 `Succeeded`、业务键记完成。

**取消**：`LoadCancellationAuthorization` 的 `slots` 两种决定都给空（6.0.0 schema 两支都要求 `maxItems 0`）；工作流内部照旧记目标仓位，取消结果的对账读的是工作流那一份（`OnboardRecoveryCoordinator.cs:1366` 写入、`ApplyCurrentResultAsync` 比对），**不依赖线上授权的那组仓位**。装货中取消报 `ALL_EMPTY`：逐仓报全部目标仓的旧形照旧收；`slotResults` 为空的（v6 车载端没有授权范围可逐仓报）只在服务端自己的读数（同上 `SessionSlotReadings`，新鲜）证明全部目标仓 `EMPTY`/`LOCKED`/`RESET` 时对上，否则 `RecoveryRequired`（审查 M1，`ServerReadingsProveEmptyScopeAllEmptyAsync`）。减篮待判或已授权时申请取消 → `REJECTED`、`LOAD_CANCELLATION_BASKET_LOADED`。协议可见期待一并改：合成车载端（`LoadCancellation.cs` 按 attempt 区分扫码前取消）、`g3-load-cancellation.ps1`、`real-onboard-load-door-closed-empty-reopens.ps1`、`docs/g3-slice-claim-review.md`。

**看板与审计**：车队视图新卡片「剩下的不装记录」（`/query/v1/remaining-baskets-skips`），每次判定一行：批号、车、N→k、已装仓位、操作员工号、申请（服务端收到）与判定时刻、同意或拒绝（拒绝原因码＋中文说明）、是否已按 k 篮结算。每次判定另记一行日志（事件号 8300，含 N→k、工号、时刻、原因码）。工号只记录，不作任何判断依据。车上篮数的审计与持货卡片按已装需求实占的仓数计（减篮后是 k）。

**存储**：没有新表。照 cs#713「出发请求」的先例：请求在收件箱，判定就是发件箱那一行答复。核实过这两张表不会被清：src 里没有任何删除 `ProtocolInbox`／`ProtocolOutbox` 行的代码（唯一的 `ExecuteDeleteAsync` 在别的表：`WireToGateStore.cs:3470` 等）；启动时的围栏只给录入请求两类（`StopScopedOutbox.cs:34`）与三种快照（`SupersededSnapshotOutbox.cs:40-42`）打 `FencedAt`、不删行，减篮答复不在其中；结算查授权也不看 `FencedAt`。

**合成 L2**：合成车载端能申请减篮（先报一份逐仓快照、再发请求，授权后按 `COMPLETED_REMAINING_SKIPPED` 答装货命令）；新场景 `load-remaining-baskets-skipped` 进 `l2.yml`（`program-210`）。

## 验收标准与证据
| 验收标准 | 证据 |
| --- | --- |
| 授权与拒绝 | L1 `RemainingBasketsSkipTests`：三篮装两篮、10-21 agv03 @160 形状（两篮装一篮、另一仓空关锁闭）授权；拒绝 13 格 Theory（门未锁闭、输出未复位、UNKNOWN、UNKNOWN 先于未锁、仓位不符、全部装满、旧 attempt、别的需求、别的站点操作、异常处置会话开着、人工判故障、纠错在处理、取消在处理）＋ N = 1、k = 0、已授权后再申请、待判期间结果先到；每格都断言业务状态不动 |
| 按 k 篮完成、预留释放、卸货 k 仓 | L1 授权那条走到卸货完成：操作与归属行只剩 [1,2]、卸货命令 `slots` [1,2]、`expectedBasketCount` 2；L1 账本那条：结算前前侧可分 [4]、授权后仍 [4]、结算后 [3,4]；合成 L2 `L2-RB-03`、`L2-RB-04` |
| 业务键完成、不发人工搬运告警 | L1：需求 `Accepted` → 卸完 `Succeeded`、一行 `TransportDemandCompletions`、无 `TransportDemandSuppressions`；合成 L2 `L2-RB-05`（另查 `StructuralDispatchBlocks` 为 0） |
| 看板与审计 | L1 `EverySkipDecisionIsOnTheDashboardWithNToKTheOperatorAndItsTimes`：两行（同意、拒绝），N→k、工号、判定时刻、原因码与说明、已结算；卡片渲染含「3 → 2」；日志事件 8300 |
| 重复与断线 | L1：同号同内容原样重放、同号不同内容 `BUSINESS_ID_CONTENT_CONFLICT`；未确认答复重连后改写到新代次补发、重提不重判；未授权／只有拒绝／仓位不符／剩余仓不空的跳过结果不结算、旅程挡住；授权后 `UNKNOWN` 结束即作废；授权后装满报 `COMPLETED` 按三篮结算 |
| L1 红 | `evidence/cs830/red/l1-at-test-commit-487d5f13a.txt`：测试提交上 33 条 32 红，原文 `System.IO.InvalidDataException : Message type 'RemainingBasketsSkipRequested' is not supported by ControlServer.`；没红的一条（从没申请过就报跳过结果）旧服务端本来就不当完成，它的红见变异 M1 |
| L1 绿 | `evidence/cs830/green/targeted-1137.txt`：减篮、架构、取消、恢复状态机、存储、待送点出发、看板、处理器、staged G3、合成车载端等相关类 `Passed! - Failed: 0, Passed: 1137`，退出码 0（出站 schema 校验在收尾，无违规） |
| 变异（取红：去掉授权路径等） | `evidence/cs830/mutations/SUMMARY.txt`，10 个全红：M1 不核授权就结算、M2 不释放归属行仓位、M3 去掉授权路径、M4 锁闭先于 UNKNOWN、M5 用过期读数、M6 减篮不挡取消、M7 重提不重放、M8 允许第二次授权、M9 不核剩余仓为空、M10 取消授权带仓位（schema 收尾判红） |
| 审查返工的红 | `evidence/cs830/rework/red-at-test-commit-c440225fa.txt`：返工用例先提交，在旧实现上 M2（`AnUnansweredSnapshotRequestIsAskedAgainAfterTheReaskInterval`）与 M1（`ADuringLoadCancellationReportedAllEmptyReconcilesWithOrWithoutSlotResults(perSlot: False)`）两条红；U1、U3、M4 补的用例在旧实现上本来就绿，它们的红由对应变异给出 |
| 审查返工的绿与变异 | `evidence/cs830/rework/skip-tests-green.txt`：减篮 44/44；`evidence/cs830/rework/targeted-1315.txt`：上面那组相关类再加安全读数、补偿、故障货物、强制机械、纠错，`Passed: 1315`、退出码 0；`evidence/cs830/rework/mutations/SUMMARY.txt`：16 个全红——原 M1～M10，加审查点名的 U1（去掉「此前没有结果」，复工替换结果重用授权）、U2（不核剩余仓锁闭与输出复位，新增两格 `remaining-slot-unlocked`／`remaining-slot-output-active`）、U3（不按会话代次筛读数），与 R1（空报 ALL_EMPTY 不看读数）、R2（看读数但不核 EMPTY/LOCKED/RESET）、R3（过期读数不按间隔重要快照）；M5 现在作用在共享的 `SessionSlotReadings` 上，同时打红减篮与空报取消的过期读数用例 |
| 合成 L2 红绿 | 绿 `evidence/cs830/l2/green`（6/6）；红 `evidence/cs830/l2/red-m2-no-release`（去掉释放：`L2-RB-03` 实际 `[1,2,3]`、`L2-RB-04` 实际 `UNLOAD [1,2,3] x3`） |
| 受影响 G3 | 待与 hmi#388 联跑（见上；`g3-load-cancellation` 另等 cs#837、hmi#392）。staged G3 的内容冲突自检 `Test-StagedG3ContentConflict.ps1` 的共享载荷版本表补 `'6.0.0' = 7`（审查 H1），`probe-identity.ps1` 退出码 0 |
| CI | 审查放行后转 ready 跑唯一一轮 |

`dotnet format --verify-no-changes` 对全部改动的 .cs 文件：退出码 0。

## 剩余风险
- 「授权后、结果前车断线且车上读数变了」：车按协议报 `FAILED`/`UNKNOWN`，服务端照规则转人工恢复，授权作废；L1 证了「授权后以 `UNKNOWN` 结束」与「作废之后又来一条复工替换结果、不再用那次授权」（审查 U1）。
- **操作员角色不核**（审查意见，调度定不改）：请求里的工号只记录、进看板与日志，不按角色拒绝；协议 6.0.0 没有给服务端角色判据。
- **判定晚于结果时的原因码**（审查意见，调度定不改）：待判期间这次装货的结果先到，请求随后判 `ACTION_NOT_ALLOWED_IN_STATE`（「这次装货已有结果」），没有更细的专用码；屏上显示服务端给的说明。
- 真车载端在 6.0.0 下取消结果是否仍按目标仓逐仓报（对账依赖它）由 hmi#388／hmi#392 与真装置联跑确认。

🤖 Generated with [Claude Code](https://claude.com/claude-code)
