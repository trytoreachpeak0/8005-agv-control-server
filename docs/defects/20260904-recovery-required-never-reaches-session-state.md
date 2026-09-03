# 缺陷：服务端判定「需要恢复」，却把这件事完全交给车载端来报，于是恢复永远授权不了

Status: open
Owner repository: `8005-agv-control-server`
Found by: [`real-onboard-resume-after-repair` L2 红证据](../../evidence/l2/20260904-real-onboard-resume-after-repair-f0465d9-001/SUMMARY.md)
Product at discovery: `ControlServer_MVP@c9bbf02040008527de2734605561d89bf136f0a8`
Peers: `OnboardHmi_MVP@f0465d9ad9f84607e3db972f1c6cb0ead910ab3d`、`slots-simulator@fb5f7c593742bf98bc3957b8729a38aad5321f28`

`CV-EXCEPTION-RESUME` 两端的实现都在了，端到端却跑不通。三次 L2 真装置运行（`-001`、`-sweep01`、
本条的 `-f0465d9-001`）逐条一致地停在同一处：车辆停摆之后，车载端 HMI 上那个「申请恢复」入口始终
不出现。前两次归因为车载端，**那是错的**。

---

## 现象

服务端自己的数据库在同一时刻是自相矛盾的：

| 表 / 字段 | 值 |
| --- | --- |
| `StationOperations.Status` | `RecoveryRequired` |
| `JourneyRuntimes.Stage` / `BlockReasonCode` | `Blocked` / `LOAD_RESULT_REQUIRES_RECOVERY` |
| `SessionRecoveries.Readiness` / `ReasonCode` | **`Ready`** / **`READY`** |
| `SessionRecoveries.UnsettledSlotOperationAttemptId` | **`None`** |
| `SessionRecoveries.ProvenRecoveryCheckpoint` | **`NONE`** |

操作需要恢复、整台车停摆，而**会话是就绪的、没有未结 attempt、没有已证实的恢复断点**。

后果不只是 HMI 不显示入口。`ValidateActionPreconditions`
（`src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs:944`）对
`RESUME_AFTER_REPAIR` 要求

    connection.UnsettledSlotOperationAttemptId == operation.SlotOperationAttemptId
    && connection.ProvenRecoveryCheckpoint is ("PREPARED" or "ACTIVE_UNLOCK_SET" or "SAFE_FINISH_REACHED")

两项都不成立。**所以即使入口出现、操作员点了、请求发到服务端，服务端也会以
`PROVEN_RECOVERY_CHECKPOINT_REQUIRED` 拒绝。**恢复授权在当前生产路径上无法被授予，与车载端做什么
无关。

## 为什么归属在服务端

`UnsettledSlotOperationAttemptId` 与 `ProvenRecoveryCheckpoint` 在全仓**只有一个写入点**
（`src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs:132`），数据源是车载端的
`RecoveryStateReport`。而 `DecideReadinessAsync` 判就绪只看：能力与安全修订是否齐、恢复报告在不在、
发车安全是否可用、**车载端报的** pending facts 是否为空、以及强制恢复代次是否对齐。

`StationOperationStatus.RecoveryRequired` 不是它的输入。grep 全仓，这个状态只有两类消费者：旅程引擎
用它去 `Block`（`JourneyRuntimeEngine.cs:434`、`:510`），恢复协调器用它当授权前置
（`OnboardRecoveryCoordinator.cs:475`、`:750`、`:941`、`:948`）。**没有任何一条路把它写回会话恢复
状态。**

于是形成一个闭环死结：

- **服务端**是「这次结果算不算安全完成」的唯一判定者——`ApplyOperationResultAsync` 正是它把操作
  置为 `RecoveryRequired` 的地方。
- **车载端**拿到了那份结果的 `DurableAck`，从它的视角这次 attempt 已经结清，它无从得知服务端拒绝了
  这份结果。日志里它一直在稳定地做正确的事：`忽略重复SlotOperationCommand：attempt=...，保留原
  OperationResult重放。`
- 所以车载端如实报告「没有未结的东西」，服务端据此把会话判为 `Ready`，而它自己的授权门禁要的正是
  那份「未结」记录。

**把自己的判定结论托付给对端来告知自己**，是这个缺陷的形状。

## 为什么单元测试没有抓到

`tests/ControlServer.Tests/RecoveryStateMachineG2Tests.cs` 的 `SeedBlockedJourneyAsync` **手工种了**
`UnsettledSlotOperationAttemptId = AttemptId` 与 `ProvenRecoveryCheckpoint = "PREPARED"`。整套 L1
因此在一个生产路径从不产生的状态上验证恢复状态机，279 个测试全绿。

这是 L2 半实物这一层存在的全部理由，而它确实抓到了：同一段代码在 L1 全绿、在真两端下必然失败。
**教训与 `.gitattributes` 那次同类**：种出来的前置状态需要有一条路证明它真的会出现。

## 未修：修法不是一行，而且不能靠放宽 fail-closed

难点在 `ProvenRecoveryCheckpoint` 是一个**物理**检查点，证明仓门当时被留在哪一步。服务端不能自己
编一个——那等于拿掉一道物理安全门禁，而这套系统里「宁可停摆」是设计（见
`20260903-onboard-safety-facts-frozen-at-session-start.md` 里为什么**没有**给 `Blocked` 加出口）。

三条候选，代价各不相同，都需要人来定：

1. **服务端把自己的判定写进会话恢复状态，并要求车载端重报一次带 checkpoint 的恢复状态。**需要一条
   「你上报的结果被拒、该 attempt 需要恢复」的下行消息——**很可能要改协议**，而协议一动两边受影响
   切片的 G1/G2/G3 证据全部作废。
2. **授权前置改为信服务端自己的 `RecoveryRequired` 记录**，不再要求车载端报的未结 attempt。最省事，
   但等于在没有已证实物理断点的情况下授权开仓门。**不建议。**
3. **车载端在 `DurableAck` 之后仍把 attempt 视为未结**，直到服务端明确宣告结清。改的是对端语义，同样
   要先在协议层把「结清」定义清楚。

## 影响

- **`CV-EXCEPTION-RESUME` 端到端不可用**，因此 `W2G-IS-05` 与 `W2G-IS-07` 的 `G3` 无法在真两端上
  取得绿证据。
- **本仓 `CONTROL_SERVER_G2` 现有的 8/8 PASS 不受此影响，但也不能被当作这条向量可用的证据**——它
  与 L1 一样跑在种出来的会话状态上。
- 已在 [`8005-agv-onboard-hmi#4`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/4)
  纠正归属：前两次报告把这条挂在车载端，Kun Wang 据此在 `f0465d9` 加固了车载端恢复引导并修掉一个
  真实的循环依赖（快照与请求互为前置），那部分不是白做，但它不是这条场景的阻塞点。
