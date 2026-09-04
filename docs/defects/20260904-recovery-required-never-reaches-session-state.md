# 缺陷：旅程停摆需要人来恢复，而车上唯一的入口按会话状态开门——那扇门不开

Status: open
Owner repository: `8005-agv-control-server`
Found by: [`real-onboard-resume-after-repair` L2 红证据](../../evidence/l2/20260904-real-onboard-resume-after-repair-f0465d9-001/SUMMARY.md)
Product at discovery: `ControlServer_MVP@c9bbf02040008527de2734605561d89bf136f0a8`
Peers: `OnboardHmi_MVP@f0465d9ad9f84607e3db972f1c6cb0ead910ab3d`、`slots-simulator@fb5f7c593742bf98bc3957b8729a38aad5321f28`

> **本文第一版的归因是错的，已重写。**原文断言「服务端的恢复授权在生产路径上无法被授予」。用一条
> L1 测试把 L2 观察到的状态种进去实测之后，那个断言不成立：**服务端在完全相同的状态下会授权
> `COMPENSATE_LOAD_ALL_EMPTY`**。测试是
> `RecoveryStateMachineG2Tests.AfterARefusedResultResumeIsRefusedButCompensationIsAuthorized`。
>
> 这是同一个问题上的第三次归因，前两次分别错怪了车载端和服务端的授权门禁。**教训写在最后一节，
> 它比这个缺陷本身更值得记。**

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

## 不是什么（这一段是更正）

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

顺带：**L2 场景 `real-onboard-resume-after-repair` 选错了向量。**它制造「装载跑完、仓位全空」，那是
`COMPENSATE_LOAD_ALL_EMPTY` 的状态；`RESUME_AFTER_REPAIR` 要的是「跑到一半没出结果」，那对应的是
第 4 节恢复表里的「装载中途车载端重启」。

## 真正剩下的缺陷

**车上发起不了任何恢复。**车载端的恢复入口按「会话进入 `RecoveryRequired`」显示
（`f0465d9` 的实现前提），而服务端在这个状态下把会话判为 `Ready`：

`DecideReadinessAsync` 的就绪判据是「能力与安全修订齐、恢复报告在、发车安全可用、车载端报的 pending
facts 为空、强制恢复代次对齐」。**`StationOperationStatus.RecoveryRequired` 不是它的输入**——grep 全仓，
这个状态只有旅程引擎（用来 `Block`）和恢复协调器（用作授权前置）两类消费者。

于是：服务端知道要恢复、也授权得了适配的动作，但车上的人打不开那扇门。**授权是齐的，入口是缺的。**

## 修法与代价

不是「服务端自己编一个 checkpoint」——那是放宽物理安全门禁，且在这个更正后的理解下也不需要。要改的是
**会话就绪要不要反映服务端自己的恢复判定**：一个 `Blocked` 且需要恢复的旅程，其会话不该报 `Ready`。

这个改动只会把会话从 `Ready` 推向 `RecoveryRequired`，不会反向，因此是 fail-closed 的。但影响面不止
一处：会话就绪同时门控着需求受理与派车，要确认不会把「停摆的车」变成「连恢复都做不了的车」。

**另一半在车载端**：`COMPENSATE_LOAD_ALL_EMPTY` 至今 fail-closed，未实现。按 2026-09-04 的政策变更，
那部分开发工作现在也在我方（`w2g/*` 分支 + PR）。

## 教训：三次归因，两次错，错法相同

1. **第一次**：看到入口不出现，归因车载端，开 issue。没查服务端自己的会话状态。
2. **第二次**：查了会话状态，看到 `Ready` 与 `RecoveryRequired` 矛盾，断言服务端授权门禁永远过不去。
   只读了 `RESUME_AFTER_REPAIR` 那一条分支，没看 `AllowedActions` 给出的另外三条。
3. **第三次**：写测试把状态种进去问服务端，才知道 compensate 是通的。

**两次错都是「读了一条代码路径就下结论」。**修正的做法是这一次用的：把观察到的状态原样种进 L1，让
系统自己回答，而不是让我替它回答。这条测试也留下来了，它同时钉住「resume 在此状态被拒」与
「compensate 在此状态被授权」两件事——下一个人不必重走这三轮。
