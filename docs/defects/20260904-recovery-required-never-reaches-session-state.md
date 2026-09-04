# 缺陷：旅程停摆需要人来恢复，而车上唯一的入口按会话状态开门——那扇门不开

Status: resolved（2026-09-04，`64e9bcc` + `248d8eb`；真装置证据
[`20260904-recovery-entry-after-journey-fix-001`](../../evidence/l2/20260904-recovery-entry-after-journey-fix-001/SUMMARY.md)）
Owner repository: `8005-agv-control-server`
Found by: [`real-onboard-resume-after-repair` L2 红证据](../../evidence/l2/20260904-real-onboard-resume-after-repair-f0465d9-001/SUMMARY.md)
Product at discovery: `ControlServer_MVP@c9bbf02040008527de2734605561d89bf136f0a8`
Peers: `OnboardHmi_MVP@f0465d9ad9f84607e3db972f1c6cb0ead910ab3d`、`slots-simulator@fb5f7c593742bf98bc3957b8729a38aad5321f28`

> **这份文档被改写过两次，因为归因错了两次。**第一版断言「服务端的恢复授权在生产路径上无法被授予」，
> 第二版（下面「不是什么」那一节）纠正了它，但同时留下了一个新的错误结论——**「车载端静默丢弃了会话
> 中途的 `SessionReadiness`」**。那也是错的，而且是同一个问题上的**第四次**误判，错法和前三次完全一样。
>
> 真正的最后一块在服务端自己：会话转入 `RecoveryRequired` 之后，`JourneyRuntimeEngine.AdvanceAsync`
> 因为就绪门而整体停转，旅程**永远到不了 `Blocked`**。**教训写在最后一节，它比这个缺陷本身更值得记。**

---

## 现象

真装置 L2 跑到「装载失败、旅程 `Blocked`」之后，车载端 HMI 上的「申请恢复」入口始终不出现，操作员
无法发起任何恢复动作。三次运行逐条一致。

同一时刻服务端的记录：

| 表 / 字段 | 值 |
| --- | --- |
| `StationOperations.Status` | `RecoveryRequired` |
| `JourneyRuntimes.Stage` / `BlockReasonCode` | `Blocked` / `LOAD_RESULT_REQUIRES_RECOVERY` |
| `SessionRecoveries.Readiness` / `ReasonCode` | `Ready` / `READY` |
| `SessionRecoveries.UnsettledSlotOperationAttemptId` | `None` |
| `SessionRecoveries.ProvenRecoveryCheckpoint` | `NONE` |

## 不是什么（这一段是第一次更正）

**服务端的恢复授权没有坏。**在上面这个状态下，`AllowedActions` 会给出四个动作，而
`COMPENSATE_LOAD_ALL_EMPTY` 的前置只要求「操作是 Load 且处于 `RecoveryRequired`」——两条都满足，
实测授权通过。

**`RESUME_AFTER_REPAIR` 被拒也不是缺陷，是设计。**它恢复的是**停在物理断点、车辆还握着那个断点**的
操作，所以要求一个已证实的 checkpoint（`PREPARED` / `ACTIVE_UNLOCK_SET` / `SAFE_FINISH_REACHED`）。
车辆一旦记录了结果，那个断点就不存在了：

- 车载端在写结果时把 `UnsettledSlotOperationAttemptId` 与 `OperationContext` 一并清空
  （`WireToGateSlotOperationExecutor.cs:130`）；
- 车载端同样会拒绝 checkpoint 为 `ResultRecorded` 的恢复命令（同文件 `:641`）。

**两端各自独立地把这条路排除了，而协议向量 `CV-EXCEPTION-RESUME` 只规定消息顺序、没有排除它**
（`vectors/CV-EXCEPTION-RESUME/expected.json`）。所以这不是谁违反了契约，是契约没说、两边默认一致。

顺带：**L2 场景选错了向量，已改名为 `real-onboard-recovery-entry-missing`。**它制造「装载跑完、仓位全空」，那是
`COMPENSATE_LOAD_ALL_EMPTY` 的状态；`RESUME_AFTER_REPAIR` 要的是「跑到一半没出结果」，那对应的是
第 4 节恢复表里的「装载中途车载端重启」。

## 也不是什么（这一段是第二次更正）

**车载端没有静默丢弃会话中途的 `SessionReadiness`。**分派一直都在——
`WireToGateSessionClient.cs:957`，Kun Wang 于 2026-08-28 在 `777eff8b` 加的，比这个缺陷被发现早了一周。
中途 `SessionReadiness` 转 `RecoveryRequired` 也一直有测试在跑，只是它们都搭在 `SafetyStateChanged`
的 ack 上，所以从测试名上看不出来它覆盖了这件事。

**恢复入口那一侧也是好的。**车载端 L1
`RecoveryRequiredAnnouncedOnAResultAckOpensTheRecoveryEntry`（`8005-agv-onboard-hmi@550dbe9`）
按服务端真实的发法复刻——`OperationResult` 的 ack 之后附一行 `SessionReadiness`，不带 `correlationId`，
原因码 `SESSION_RECOVERY_REQUIRED`，且**不预置任何恢复状态**（车辆一记录结果就清空了）——
`Readiness` 正确翻转，`CanRequestResumeAfterRepair` 变为真。测试通过。

## 真正的缺陷

**服务端修好了「告诉车辆需要恢复」，同时把旅程锁死在「说不出为什么停」的状态。**

`147f02c` 让被拒的装载结果把会话推向 `RecoveryRequired`。`JourneyRuntimeEngine.AdvanceAsync` 读的是
同一行，会话不是 `Ready` 就直接 return——于是那条改动**同时**把旅程卡在了它本该开启的那一步之前：

1. 装载结果被拒 → `StationOperations.Status = RecoveryRequired`，会话 → `RecoveryRequired`；
2. 会话不是 `Ready` → 旅程引擎整体停转 → 旅程永远到不了 `Blocked`；
3. `BlockReasonCode` 停在 `ONBOARD_SESSION_NOT_READY`，等的是哪一种恢复，没人说得出来。

这正是本文档「修法与代价」一节写下、但当时没有去验证的那个代价：

> 但影响面不止一处：会话就绪同时门控着需求受理与派车，要确认不会把「停摆的车」变成「连恢复都做不了
> 的车」。

没确认，它就发生了。**红证据是
[`20260904-recovery-entry-after-announce-001`](../../evidence/l2/20260904-recovery-entry-after-announce-001/SUMMARY.md)**：
`db-JourneyRuntimes.json` 是 `AwaitingLoadResult` / `ONBOARD_SESSION_NOT_READY`，`SessionRecoveries`
是 `RecoveryRequired` / `OPERATION_RECOVERY_REQUIRED`，那份被拒的结果 11:20:23 就落库了，场景等
`Blocked` 等满 240s 也等不到。合成场景也一样被打挂——隔离实验见
[`20260904-load-result-requires-recovery-at-147f02c-deadlock`](../../evidence/l2/20260904-load-result-requires-recovery-at-147f02c-deadlock/SUMMARY.md)。

## 修法

`64e9bcc`：收窄到两条不需要对端参与的转移。`AwaitingLoadResult` / `AwaitingUnloadResult` 上，一个已被
服务端判为 `RecoveryRequired` 的站点操作，直接把旅程停摆到对应的原因码——这两条只读
`StationOperations`。`AdvanceAsync` 里其余每一条转移都要么向车辆发布、要么读它的 revision，仍然留在
就绪门后面。

**会话在恢复中不等于会话不在。**车还连着，服务端在同一状态下授权得了 `COMPENSATE_LOAD_ALL_EMPTY`，
车载端的恢复入口也确实会开。缺的只是把停摆的原因说出来。

`248d8eb`：`load-result-requires-recovery` 的探针原来等的是「Readiness 离开 Ready」，而自 `147f02c` 起
那在它推送不安全出发状态之前就已经成立，探针于是读到上一条原因码。改成等
`SessionRecoveries.DepartureSafe` 翻成 0——那才是这一步真正在等的事。产品没错，红的是探针的时序假设。

## 证据

真装置 L2 `20260904-recovery-entry-after-journey-fix-001`，5 条判据全 PASS，
`controlServerCommit=248d8eb`、`onboardHmiCommit=550dbe9`。时间线上旅程转 `Blocked` 之后 **75 毫秒**
恢复入口就变为可用：

```
{"at":"2026-09-04T03:53:49.0231058+00:00","criterion":"journey-stage","value":"Blocked"}
{"at":"2026-09-04T03:53:49.0987183+00:00","criterion":"onboard-recovery-entry","value":"True"}
```

**没有一起做的**：`COMPENSATE_LOAD_ALL_EMPTY` 的车载端实现至今 fail-closed。入口开了，能开的恢复会话
里可选的动作还是空的。那是下一件事，按 2026-09-04 的政策在 `8005-agv-onboard-hmi` 的 `w2g/*` 分支上做。

## 教训：四次归因，三次错，错法相同

1. **第一次**：看到入口不出现，归因车载端，开 issue。没查服务端自己的会话状态。
2. **第二次**：查了会话状态，看到 `Ready` 与 `RecoveryRequired` 矛盾，断言服务端授权门禁永远过不去。
   只读了 `RESUME_AFTER_REPAIR` 那一条分支，没看 `AllowedActions` 给出的另外三条。
3. **第三次**：写测试把状态种进去问服务端，才知道 compensate 是通的。**这次是对的。**
4. **第四次**：改完服务端、入口仍不出现，读了车载端 `ApplySessionReadiness` 只在握手里被调用的那一处，
   断言「中途那条没有处理者、被静默丢弃」，并据此写下一份交接文档要去改对端仓。`git blame` 一行就能
   推翻它——分派在两周前就加了。**又一次读了一条代码路径就下结论。**

**三次错都是同一个动作。**第三次之所以对，是因为换了动作：把观察到的状态原样种进 L1，让系统自己回答。
第四次本可以用同一个动作避免——事实上最后正是它给出了答案，两条测试各钉一端：

- 车载端 `RecoveryRequiredAnnouncedOnAResultAckOpensTheRecoveryEntry`：中途宣告到达后入口要开（绿，
  证明车载端无需改动）；
- 服务端 `AResultThatTurnsTheSessionRecoveryRequiredStillBlocksTheJourneyForItsOwnReason`：把 L2 观察到
  的四张表原样种进去（红，一次就指到 `AdvanceAsync`）。

**还有一条更便宜的**：那份红证据里的答案一直摆在 `db-JourneyRuntimes.json` 里——
`AwaitingLoadResult` / `ONBOARD_SESSION_NOT_READY`。前三轮都只读了 `SUMMARY.md` 的判据表，没有翻
`snapshots/`。**证据目录里的快照不是给归档看的，是给读的。**
