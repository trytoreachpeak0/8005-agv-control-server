# 缺陷：装货进行中断线重连，车载端因会话不是 Ready 不发这次装货的结果，服务端等结果才肯就绪，两端互等

Status: open（control-server#189 第一步结论：只查不改；第二步做不做由用户定）
Owner repository: `8005-agv-onboard-hmi`（修法落在车载端；服务端不需要配对改动）
Found by: 真装置 L2 场景 `real-onboard-expected-action-overdue` 调试运行（control-server#167）
[`evidence/l2/20260919-cs167-debug-001/SUMMARY.md`](../../evidence/l2/20260919-cs167-debug-001/SUMMARY.md)（车载端 `3547a97`）；
在含 onboard-hmi#120、#119 修复的车载端上复现：
[`evidence/l2/20260919-cs189-inflight-reconnect-4a6790e-002/SUMMARY.md`](../../evidence/l2/20260919-cs189-inflight-reconnect-4a6790e-002/SUMMARY.md)
Product at reproduction: 服务端 `fp/v2-impl@169dfa42`（含 control-server#187 `c1252932`）；车载端 `w2g/fp-v2-impl@4a6790e5`
（含 onboard-hmi#120 `7ded1b70`、#119 `4a6790e5`）；模拟器 `main@fb5f7c5`；`protocol-v2.0.0`（`AGV_FULL_PRODUCT`）

## 结论先说

- **在含 onboard-hmi#120 修复的车载端上照样复现**，所以 #120 不是原因，第二步仍然需要。
- **该让步的是车载端，服务端已经按文档做了。**协议与 ADR 明文允许会话未就绪时「结果补报」，服务端也照此受理；
  是车载端正常装货路径的发送口只认 `Ready`，把这次装货的 `OperationResult` 挡在门外，而且挡在写入持久发件箱之前。
- **不需要改协议或 ADR。**照 ADR-cross-0028、ADR-cross-0029 第 4 步与 manifest 里 `OperationResult` 的
  `durableBeforeSend: true`／`recoveryRole: PENDING_RESULT_REPLAY` 修车载端即可。
- **服务端不需要配对票。**复现里第二次重连后结果一到，服务端当场对账、推 `SessionReadiness=READY`、装货 `Committed`，
  现有代码已经做对了。
- onboard-hmi#119 那条线索（非 READY 时原始 `SlotOperationCommand` 的拒绝发不出去）**是同一根因**：同一道只认 `Ready`
  的发送口。但它不是「非 READY 期间车载端应答通道整体缺失」——恢复类消息都有放行 `RecoveryRequired` 的发送口，缺的
  只是正常路径上的这几条。建议在同一张车载端票里一起修，优先级低于结果。

## 现场含义

装货进行中（车在站、门开着等人）现场网络抖一次，车自己重连上来，接下来：

- **这次装货卡在 `Prepared`，会话停在 `RecoveryRequired`。**操作员照常放货关门，车上判完成，但结果送不出去；
  车载端界面停在「结果等待确认，禁止重复操作仓门」。
- **这台车不接新活，也不走。**服务端旅程引擎每一轮先找这台车的 `Ready` 会话，找不到就不推进它的任何一步，只把旅程标成
  `ONBOARD_SESSION_NOT_READY`（`JourneyRuntimeEngine.cs:606-648`），看板的阻断旅程卡片上能看到；不建去关卡的单、不做出发前
  安全检查。服务端「每台车同一时刻只有一趟未完成旅程」（`:246-254`），这趟不结束，派车也不会再给这台车新的需求。站点期限的
  计时在未就绪期间清空（同一段，ADR-cross-0055），所以也不会因为超时自己结束这一站。
- **只有下一次断线重连才自愈**：新握手触发车载端的中断结算，按实时 IO 重算一份结果发出去（复现第 3 步）。现场网络不再抖，
  它就一直卡着；人能做的是让车重连一次，例如管理员到车上重启车载端程序——这条推论依据的是中断结算在重启后同样会跑
  （onboard-hmi#120 的缺陷单与本复现第 3 步），本票没有在真装置上单独试重启。
- 自愈时发出去的不是执行器自己那份结果，而是事后按 IO 重算的一份；对「门关、锁上、有货」的正常装货两者结论相同，本复现即如此。

## 现象（复现 `-002`）

场景 `real-onboard-inflight-load-reconnect`（一次性调试场景，脚本存在证据目录 `scenario/` 下，不进 `scripts/`）：
装货开门、车载端在等操作员时，经协议故障代理断开一次（不丢任何行，车自己重连）；然后操作员放货关门；再断开一次作探针。

1. **重连后服务端在等结果（正确）。**新代次 2，`SessionRecoveries`：
   `RecoveryRequired / PENDING_FACT_RECONCILIATION_REQUIRED / pendingAttempts ["1d1c97b5-…"] / pendingResults [] /
   checkpoint ACTIVE_UNLOCK_SET / activeUnlock [1]`（判据 `L2-IR-01` PASS）。
2. **装货在车上做完了，结果没发出去。**车载端日志（`logs/onboard-app/agv-20260919.log`）：

   ```
   18:22:42.39 上层会话已建立：generation=2，readiness=RecoveryRequired。
   18:22:45.60 1号仓IO变化：DO 0->0，锁DI 0->1，光幕DI 1->0。
   18:22:45.97 仓位操作进度未能发送，不影响仓位判定：attempt=1d1c97b5-…，phase=VERIFYING，round=0，error=InvalidOperationException。
   18:22:45.98 仓位操作进度未能发送，不影响仓位判定：attempt=1d1c97b5-…，phase=SAFE_FINISH，round=0，error=InvalidOperationException。
   18:22:45.98 OperationResult暂未收到DurableAck：attempt=1d1c97b5-…。 | InvalidOperationException: WIRE_TO_GATE_NOT_READY
   ```

   代理记录的第 2 条连接（心跳略）在握手之后只有两条 `SafetyStateChanged` 与服务端的应答，**从 18:22:45 到 18:23:37 手动断开的
   约 52 秒里没有任何 `OperationResult` 或 `OperationProgress`**；服务端一直 `RecoveryRequired`，装货一直 `Prepared`。这就是互等。
3. **探针：再断一次，结果才到。**第 3 条连接握手后，车载端日志
   `上次仓位操作在执行中中断，未再输出开锁，按实时IO结算：attempt=1d1c97b5-…，outcome=COMPLETED，checkpoint=SAFE_FINISH_REACHED`，
   线上 `onboard->server OperationResult` → `DurableAck` + `SessionReadiness`；会话 `READY`，装货 `Committed`，旅程随后推进
   （收尾快照 `AwaitingStationDeparture`）。收件箱里这个 attempt 只有一条结果（`L2-IR-05` PASS），没有重复结算。
4. **全程 0 条 `ProtocolProblem`**，重连后的连接上服务端没有重发行程快照（`L2-IR-06` PASS）。onboard-hmi#124 在 G2 替身里
   看到的「重连后行程快照 → `ProtocolProblem` 断开」在真的这一对上不出现：会话 `RecoveryRequired` 期间真服务端不推行程快照，
   那是替身的问题，不属于本缺陷。

**判据表有两处读数是场景脚本的错，结论以线上序列与车载端日志为准：**

- `L2-IR-02` 结论 FAIL 是对的，但「实际」栏里的「结果 1」是错读：`Get-Results` 返回的空数组又被 `@()` 包成了一个元素。
  代理流量里第 2 条连接没有任何 `OperationResult`，服务端收件箱里这个 attempt 唯一的一条结果是第 3 代次的（`L2-IR-05` 的
  「gen 3」）。同一个错读还让「等 45 秒」那一步立刻返回；实际的观察窗口由线上时间给出：从车载端 18:22:45.98 抛错到
  18:23:37 手动断开，约 52 秒。
- `L2-IR-04` 红在旅程阶段：脚本在结果受理后约 0.3 秒就读阶段，旅程还没推进到下一段；结果送达、`READY`、`Committed`
  三项都已满足，收尾快照里旅程已是 `AwaitingStationDeparture`。探针的结论按「解开了」读。`-001` 在场景脚本自身的 bug 上中止（断开接口只回一条连接时 `.Count`
在 StrictMode 下抛错），中止前已拿到与 `-002` 第 1 步相同的前半段，作为前半段复现引用。

## 根因

两端各自在等什么：

| 端 | 在等什么 | 代码（本次复现的提交） |
| --- | --- | --- |
| 服务端 | 新代次的 `RecoveryStateReport` 报了未结 attempt，没对上账就不给 `READY`：`PENDING_FACT_RECONCILIATION_REQUIRED` | `WireToGateStore.cs:2682`；报告落库 `:192` |
| 车载端 | 正常装货路径发结果走 `SendOperationResultAsync` → `SendDurableAsync`，只允许 `Ready`；不是 `Ready` 就抛 `WIRE_TO_GATE_NOT_READY` | 车载端 `WireToGateBusinessService.cs:1801`、`WireToGateSessionClient.cs:242`、`:924`、`:955-960` |

车载端这一半有两层：

1. **发送口挑错了。**`WireToGateSessionClient` 对同一条 `OperationResult` 有两个发送口：`SendOperationResultAsync`（`:242`，只认
   `Ready`）与 `SendRecoveryOperationResultAsync`（`:255`，`allowRecoveryRequired: true`）。中断结算路径（`WireToGateBusinessService.cs:882`，
   注释写明「The session is RecoveryRequired at this moment -- precisely because this attempt was never settled」）与维修后续作路径
   （`:1606`）用后者；正常装货路径（`:1801`）用前者。可是断线重连之后，正常路径上还在执行的这次装货，恰恰就是新代次报告里那个
   未结 attempt——它与中断结算面对的是同一个局面，却用了不同的门。
2. **挡在落盘之前。**`SendDurableCoreAsync` 先查就绪（`:955-960`），通过了才 `SaveOutgoingBeforeSendAsync`（`:1019`）。所以被挡下的结果
   既没发出去也没进持久发件箱，下次重连握手的补发（`:641` 一带的 `ReplayDurableOutgoingAsync`）也没有东西可补。`:1837` 的注释
   「The result is already in the durable outbox. A reconnect will replay…」对 `TimeoutException`／`IOException` 成立，对这里的
   `InvalidOperationException("WIRE_TO_GATE_NOT_READY")` 不成立。车一直停在「结果等待确认，禁止重复操作仓门」。

**为什么第二次重连能解开：**握手后的 `SessionReadiness` 触发 `RestorePendingRecoveryOperationProjectionAsync`；此时这次装货已经不在
执行集合里、发件箱里也没有它的结果行，于是走中断结算，按实时 IO 判 `COMPLETED`，经 `SendRecoveryOperationResultAsync` 在
`RecoveryRequired` 下发出。也就是说，今天互等只会被一次与它无关的会话变化解开（再断一次、或恰好之后又来一条 `SessionReadiness`），
发出去的也不是执行器自己的那份结果，而是事后按 IO 重算的一份。现场网络只抖一下时，就一直卡着。

## 对照协议与 ADR：哪一端让步

- **ADR-cross-0028**（`8005-agv-program/docs/adr/cross/0028-connected-session-requires-explicit-business-readiness.md`）：
  服务端授予就绪之前，「只允许心跳、能力同步、恢复对账、**结果补报**、诊断和必要的安全处置消息」。
- **ADR-cross-0029** 第 4 步：「车载端沿用原 SlotOperationAttemptId **补报 OperationResult**，服务端按既定规则逐条确认，直到双方对待
  确认结果达成一致」；第 5 步之后才是 `SessionReadiness`。服务端是就绪的唯一授予方。
- **manifest**（`8005-agv-protocol/manifest/release.json`，`protocol-v2.0.0`）：`OperationResult` 为 `RELIABLE`、`durableBeforeSend: true`、
  `recoveryRole: PENDING_RESULT_REPLAY`。
- **向量** `CV-CONNECTION-LOSS-SAFE-FINISH`：服务端 `NEVER_READY_BEFORE_RECONCILIATION`，车载端 `FINISH_IN_PROGRESS_OPERATION_SAFELY`，
  终态 `RECOVERY_REQUIRED_OR_UNIQUELY_RECONCILED`；`CV-OPERATION-RESULT-UNKNOWN-RECONCILE`：`RecoveryStateReport` 之后车载端补发
  `OperationResult`（`REPLAY_RESULT_ON_RECONNECT`）。

因此：

1. **服务端在待定事实未对账时不给 `READY`，是对的，不让步。**它也已经允许「只收结果」：`OperationResult` 的处理只要求代次对得上
   （`OnboardMessageProcessor.cs:862` 的 `RequireCurrentSession`），不看就绪；受理后 `ReconcileReportedPendingResultAsync`、
   `SettleReportedAttemptsAsync`（`:486-488`）结掉报告里的 attempt，重判就绪，变了就在同一应答里推 `SessionReadiness`（`:507` 起）。
   复现第 3 步就是这条路径跑通的样子。
2. **车载端在非 `Ready`（`RecoveryRequired`）时应当放行在途操作的 `OperationResult`**——它正是 ADR 说的「结果补报」。
3. **进度（`OperationProgress`）文档没有要求。**它是 `TELEMETRY`、`durableBeforeSend: false`，不在 ADR-cross-0028 列举的放行类别里，
   解开互等也不需要它。维修后续作路径已经在 `RecoveryRequired` 下发进度（`SendRecoveryOperationProgressAsync`），服务端也照收；
   第二步是否顺带放行，照最小改动原则建议**不放行**（维持今天「未能发送，不影响仓位判定」），这不涉及改文档。

这些都是既有文档已经定了、实现没照做，**不需要改协议或 ADR**。

## 应当怎么修（第二步，本批未做）

修在车载端，一张配对票（`8005-agv-onboard-hmi`，`w2g/*` 分支合回 `w2g/fp-v2-impl`）；服务端本票不改代码，关闭时写明去向。

1. **正常装货路径的结果改走允许 `RecoveryRequired` 的发送口**：`WireToGateBusinessService.HandleSlotOperationAsync` 在 `:1801` 把
   `SendOperationResultAsync` 换成 `SendRecoveryOperationResultAsync`（同一个去重键 `operation-result:{attempt}`、同一个 `messageId`，
   与中断结算路径发的是同一条消息）。这一步就解开复现里的互等。
2. **结果先落盘、再看能不能发**（`durableBeforeSend`）：会话断开（`Connected=false`）或更早的握手阶段时装货结束，结果也应当先
   写进持久发件箱，由重连握手按 ADR-cross-0029 第 4 步补发，而不是今天这样既不发也不存、只能等中断结算事后重算。改法可以是
   只对 `OperationResult` 在门外先 `SaveOutgoingBeforeSendAsync`；不建议对所有持久消息一刀切改 `SendDurableCoreAsync` 的先后，那会
   改变 `SafetyStateChanged` 等消息的补发行为，范围开工时定。
3. **`:1837` 的注释改成与事实一致**，并让 onboard-hmi#124 的「只差确认」判断仍以发件箱里真有结果行为准（调度已转达，#124 已按此做、
   并补了非 Ready 时结果未落盘的守护测试）。
4. **一并处理 onboard-hmi#119 的线索**：`SendOperationRejectedAsync`（`:2108`）对原始 `SlotOperationCommand` 的拒绝同样走只认 `Ready`
   的 `SendDurableAsync`（`:2118`），而它只在会话不是 `Ready` 时被调用（`:1666`），所以永远发不出去。按 onboard-hmi#119 给续行拒绝
   开的口子（`SendSlotOperationResumeRejectedAsync`，`allowRecoveryRequired: true`）同样处理即可。它的后果比结果轻：服务端在未就绪时
   不下发新的仓位操作（ADR-cross-0028），只在就绪翻转的竞态里才会碰上，碰上时服务端收不到拒绝、就绪后照常重发。

目标行为与验收照 control-server#189 票面「第二步」：在途装货断线重连后，同一 attempt 的结果受理、会话回到 `Ready`、装货收尾；
已送达、已对账的结果不重复结算。测试接缝：车载端 G2（替身会话在 `RecoveryRequired` 下接受结果）先红后绿；真装置复用本场景，
判据 `L2-IR-02` 在第 2 代次内变绿（顺带修掉上面说的两处场景读数），红证据就是本目录的 `-002` 与 `cs167-debug-001`。

**先后：**第 1、3 条改 `HandleSlotOperationAsync` 的结果发送段，onboard-hmi#124（在做）改的正是同一段，配对票应在 #124 合入后开工。
第 2 条碰 `WireToGateSessionClient.cs`，#124 不碰。

## 与 MVP 线对照

只读代码，没碰生产。`OnboardHmi_MVP` 的 `WireToGateSessionClient.cs` 与 v2 同构：`SendOperationResultAsync` 只认 `Ready`、
`SendRecoveryOperationResultAsync` 放行 `RecoveryRequired`，正常装货路径（`WireToGateBusinessService.cs:1360`）用前者，
`HandleSlotOperationAsync` 同样在非 `Ready` 时拒绝（`:1245`）；`ControlServer_MVP` 的 `WireToGateStore.cs:2716` 同样给
`PENDING_FACT_RECONCILIATION_REQUIRED`。所以 MVP 在代码上有同一个互等。生产上有没有碰到过**未核实**：核实要读 `agv01` 的日志或
MVP 的生产库，属于要先问用户的范围，本票不做。MVP 那边没有 onboard-hmi#120 的修复，旧的恢复投影在每条 `SessionReadiness` 上都会
跑中断结算判断，可能因此在下一次安全状态变化时「碰巧」解开，行为与 v2 不完全相同，这一点同样未核实。

## 需要用户拍板的事

1. **第二步本批（批次 6）做不做。**建议做：现场网络抖一次就能让一台车停在站上不接活，只能靠再断一次网或人工重启解开。
   不做的代价是这段时间现场只能人工处置；延后到批次 7 也可以，不影响别的票。
2. **修法落点与规模（按本报告的建议）。**只改车载端，一张配对票（`8005-agv-onboard-hmi`）：
   `HandleSlotOperationAsync` 的结果改走允许 `RecoveryRequired` 的发送口；`OperationResult` 先落盘再看能不能发；
   原始命令的拒绝同样放行（onboard-hmi#119 线索）；改一处过时注释。预计是 `WireToGateBusinessService.cs`、
   `WireToGateSessionClient.cs` 两个文件的小改动加车载端 G2 测试，另加一次真装置验证（复用本场景，约 5 分钟机器时间）。
   要排在 onboard-hmi#124 合入之后。进度消息不在非就绪时放行（ADR 没有要求）。
3. **是否需要服务端配对：不需要。**服务端已经照 ADR 在未就绪时受理结果并重判就绪，复现第 3 步证明这条路是通的。
   control-server#189 在第二步里不改代码，关闭时写明去向车载端配对票。
4. **改协议或 ADR：不需要。**以上都是照既有文字修实现。

## 证据

| 目录 | 内容 |
| --- | --- |
| `evidence/l2/20260919-cs167-debug-001/` | 原始发现（车载端 `3547a97`，含 onboard-hmi#120 的缺陷） |
| `evidence/l2/20260919-cs189-inflight-reconnect-4a6790e-001/` | 前半段复现（场景脚本自身 bug 中止于第 2 步之后） |
| `evidence/l2/20260919-cs189-inflight-reconnect-4a6790e-002/` | 完整复现与探针；`scenario/` 下是场景脚本与 setup，放进服务端 L2 编排器副本的 `scenarios/` 即可重跑 |

重跑方法：把服务端 `scripts/l2/` 与 `scripts/DesktopLock.psm1` 复制到临时目录的 `scripts/` 下，把 `scenario/` 里两个文件放进
`scripts/l2/scenarios/`，运行副本的 `Invoke-L2Scenario.ps1 -Scenario real-onboard-inflight-load-reconnect -EvidenceRoot <新目录>
-Repository <服务端 worktree> -OnboardRepository <车载端 worktree> -SimulatorRepository <模拟器 worktree>`。真装置时段按工作区规则申请。
