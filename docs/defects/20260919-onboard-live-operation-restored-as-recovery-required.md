# 缺陷：车载端把正在执行的装货当成遗留操作，每条 SessionReadiness 都给它发 RecoveryRequired 投影，期待动作超时告警一出现就被撤下

Status: open（修复票待开，见文末）
Owner repository: `8005-agv-onboard-hmi`
Found by: 真装置 L2 场景 `real-onboard-expected-action-overdue`（control-server#167）本机跑
`C:\Users\szy\Desktop\8005-workspace-v2\evidence\cs167\red-product-3547a97-001\SUMMARY.md`
（工作区侧，不入库；同目录 `red-product-3547a97-001-stage\controlserver.db` 是当时的服务端库）
Product at discovery: 车载端 `w2g/fp-v2-impl@3547a97`（含 onboard-hmi#109、#112）；服务端 `fp/b6-167-expected-action-overdue-l2@7c8f905a`
（即 `fp/v2-impl@75c3d39e` 加本票的场景与编排器改动，产品代码相同）；模拟器 `main@fb5f7c5`；`protocol-v2.0.0`（`AGV_FULL_PRODUCT`）

## 现象

场景把期待动作超时门槛压到 20 秒（REQ-0358），装货开门后第 8 秒空关一次、车自己重开，然后等门槛。13 条判据里
`L2-EAO-03`、`06`、`07`、`08`、`09`、`11`、`13` 红，其余绿：

| 判据 | 期望 | 实际 |
| --- | --- | --- |
| `L2-EAO-03` `raisedAt` ≈ 第一次开锁 + 门槛 | 距第一次开锁 + 门槛 ≤ 2 s | 距第一次开锁 + 门槛 9.8 s，距重开 + 门槛 0.93 s |
| `L2-EAO-06` 看板端点一行 | 1 行 | 0 行 |
| `L2-EAO-09` HMI 提示「已上报」 | 控件在、含「已上报」 | 不在 UIA 树里 |

服务端收到的线上顺序（`ProtocolInbox`，只列相关的几条）：

```
05:53:01.141 OperationProgress UNLOCKING                     -> DurableAck
05:53:01.277 SafetyStateChanged 5                            -> DurableAck,SessionReadiness
05:53:01.333 OnboardAlarmSnapshot [ONBOARD_SLOT_OPERATION_UNFINISHED]
05:53:02.110 OnboardAlarmSnapshot []
05:53:02.123 OperationProgress WAITING_OPERATOR
...（空关、重开，同样的闪烁又来一遍）
05:53:10.968 OperationProgress WAITING_OPERATOR
05:53:30.964 OnboardAlarmSnapshot [SLOT_EXPECTED_ACTION_OVERDUE raisedAt 05:53:30.944]
05:53:31.083 SafetyStateSnapshot 12（服务端要的中途快照）      -> SnapshotAppliedAck,SessionReadiness
05:53:31.129 OnboardAlarmSnapshot [ONBOARD_SLOT_OPERATION_UNFINISHED]   ← 超时告警没了，一直到下次开关门
```

也就是：

1. 超时告警只在线上活了约 170 ms：服务端要中途快照、回一条 `SessionReadiness`，车载端随即把它撤下，换成一条 Critical 的
   `ONBOARD_SLOT_OPERATION_UNFINISHED`「1号仓装货未完成，等待恢复处理。」。看板与 HMI 因此都看不到它。
2. 计时在重开时清零：`raisedAt` 落在「重开后的 WAITING_OPERATOR + 20 秒」，不是「第一次开锁 + 20 秒」，违反 REQ-0358「自动重开不清零」。
3. 每一次开关门（每条 `SafetyStateChanged` 的应答里都带 `SessionReadiness`）都会闪一条 `ONBOARD_SLOT_OPERATION_UNFINISHED`
   上服务端，正常装货里就有。这一条与期待动作超时无关，本身就是一条假的 Critical 告警。

## 根因

`src/SQCD.Agv.Wpf/WireToGateBusinessService.cs` 的 `OnSessionStateChanged` 在会话 `Ready` 或 `RecoveryRequired` 时调用
`RestorePendingRecoveryOperationProjectionAsync`。它本意是进程重启后把上次遗留的未了结操作恢复成投影，但**每一次会话状态变化都会跑**，
包括服务端对 `SafetyStateChanged` 和中途 `SafetyStateSnapshot` 的每一条 `SessionReadiness`。

它读 journal：未了结的 attempt 正是本进程正在执行的这次装货。随后调 `TrySettleInterruptedOperationAsync`，那个函数见到 attempt
在本进程的在执行集合 `_operationAttempts` 里，照注释「Nobody is executing it」的约定返回 `false`、不结算。可调用方把 `false` 当成
「没结算成」，接着往下给这次**正在执行**的装货发布一份 `Stage = RecoveryRequired` 的投影：

- `OnboardAlarmEvaluator.AddOperationAlarm` 见到 `RecoveryRequired` 投影，抬 `ONBOARD_SLOT_OPERATION_UNFINISHED`；
- `AddExpectedActionOverdue` 见到当前投影不在开锁／等操作员，不报超时；
- `SlotExpectedActionWaitTracker.Observe` 见到非等待阶段，清掉计时，下一条执行器进度重新起算。

## 验证

本地临时分支 `w2g/tmp-cs167-diag-skip-live-attempt`（`3034425`，只在本地、不推送）只在 `TrySettleInterruptedOperationAsync` 之前加一个判断：
attempt 在 `_operationAttempts` 里就直接 `return`。同一场景同一服务端提交跑一次，13 条判据全部 PASS：
`C:\Users\szy\Desktop\8005-workspace-v2\evidence\cs167\diag-skip-live-attempt-001\SUMMARY.md`
（`L2-EAO-03` 距第一次开锁 + 门槛 −0.05 s；端点一行、读数取中途快照 v12 且与模拟器一致；HMI「……已上报，班组长或管理员会到现场查看」）。

那一行只是诊断，不是修复方案。修复要在车载端定：怎样区分「本进程在执行」与「上个进程遗留」，以及 G2 上补一条「会话中途收到
`SessionReadiness` 不改变在执行操作的投影、不清计时、不抬 `ONBOARD_SLOT_OPERATION_UNFINISHED`」的用例。
onboard-hmi#109 与 #112 的 G2 都用替身互通，替身不会在每条安全变化后回 `SessionReadiness`，所以没看到。

## 同一次调试里的另一个发现（不在本缺陷内）

在途装货时经协议故障代理断一次链路（`evidence\cs167\debug-001`，库在 `debug-001-stage\controlserver.db`）：重连后服务端据
`RecoveryStateReport` 里的未了结 attempt 判 `RecoveryRequired / PENDING_FACT_RECONCILIATION_REQUIRED`，等这次装货的结果；车载端却因会话
不是 `Ready`，进度和结果都不发，日志：

```
仓位操作进度未能发送，不影响仓位判定：attempt=529547f5-…，phase=VERIFYING，round=0，error=InvalidOperationException。
OperationResult暂未收到DurableAck：attempt=529547f5-… | InvalidOperationException: WIRE_TO_GATE_NOT_READY
```

两端互相等，放货关门后装货也收不了尾。control-server#167 的票面把在途重连列为不做，这里只记下，建议另开票定责（哪一端该让步）。

## 后续

- 修复票：待开（车载端）。开出后在此处填链接。
- control-server#167 的场景保持红，修复合入后在含修复的车载端提交上取正式 PASS 与红证据。
