# 缺陷：六条老 journey G3 场景仍按「空关仓门就判装货失败」写，批次 5 车载端改成空关即重开之后走不通

Status: open（场景侧，交 control-server#128）；其中一条同时挂着车载端未完成的一项（onboard-hmi#78 第 5 项）
Owner repository: `8005-agv-control-server`（`scripts/l2/scenarios/G3RecoveryCommon.ps1`、`scripts/l2/scenarios/g3-load-cancellation.ps1`）；车载端那一半在 `8005-agv-onboard-hmi`
Found by: control-server#87 的 `run-journey-g3.ps1` 自检（2026-09-18 07:02–07:39Z，控制端 `68f9a1ad`、车载端 `19e8f205`、模拟器 `fb5f7c59`、协议 `protocol-v2.0.0`／`86575456`）。证据是自检证据，按票面写在会话临时目录、不入 `evidence/`：`selfcheck/journey-001/`（整轮）与 `l2/lc-rerun-001/`（`g3-load-cancellation` 空闲时单独重跑）
Product at discovery: control-server `fp/v2-impl@bc5c8e78`＋本票分支；onboard-hmi `w2g/fp-v2-impl@19e8f205`
Peers: onboard-hmi#72（批次5-20，已合入）改了行为；onboard-hmi#78（批次5-29，open）补取消时中止原执行器

**红在哪里要分开说：五条恢复类场景红在场景（前置写法过时），`g3-load-cancellation` 红在车载端一项尚未合入的功能。两者都不是本票新引入的，也都不是服务端缺陷。**

## 背景：车载端的行为在批次 5 按设计变了

onboard-hmi#72（批次5-20）之后，车载端装货执行器读到「门关了、货没放」这种与目标相反的状态，会自动重新开锁、不设上限，不再报 `FAILED`。
规格第 19.4 节决策 3 与 onboard-hmi#78 第 1 项把它定为 v2 的规程：放弃装货的唯一出口是操作员按取消，v2 车载端不产出 `FAILED`／`OPERATOR_TIMEOUT`。
ADR-cross-0058 决策 5 的「空关判取消」那条路径因此作废（onboard-hmi#78 第 9 项负责改写 ADR）。

## 现象一：五条恢复类场景卡在同一个前置

`g3-operation-result-unknown-reconcile`、`g3-exception-resume`、`g3-exception-compensate`、`g3-fault-cargo-handoff`、`g3-forced-mechanical-recovery`
共用 `G3RecoveryCommon.ps1` 的前置：UIA 录入、装货开始后把第一仓空关，然后等约 120 秒车载端操作超时交出一份确定失败的结果。

```
Timed out after 240s waiting for: the onboard reported the load result and the server acknowledged it. Last observed: (nothing)
```

意思是：等了 240 秒，车载端一直没交装货结果、服务端也就没有东西可确认。五条都在 07:21Z 之后跑，调度会话记录的本机高负载窗口（07:03–07:20Z）已经过去，不是干扰。
车载端没交结果，是因为它按新规程一直在重开那扇门。**这个前置在 v2 车载端上不可达**，场景需要换一种方式造出它们要的在途装货或 `UNKNOWN` 结果。

## 现象二：`g3-load-cancellation` 取消收敛之后，原执行器还在开门

场景在第一仓打开时把空仓门关上，再按「取消装货」。空闲时单独重跑（`lc-rerun-001`）：

- 取消经授权，车载端报 `ALL_EMPTY`，需求 `Cancelled`、旅程以 `CANCELLED_BY_OPERATOR` 收尾，这几条（G3-02-22、23、26）都过了；
- 但 G3-02-24 记到「取消后开锁 1 次 / 全程开锁 1,1」：取消请求之后，原装货执行器又给 1 号仓开了一次锁；
- G3-02-25、27 记到 1 号仓物理上是 `OPEN/EMPTY/0/0`，取消已经收敛 4 分钟后门仍开着，而车载端的取消结果说它 `LOCKED`。

整轮自检里那一次（07:09Z，干扰窗口内）更糟：重开发生在取消执行器取证之前，取消结果变成 `FAILED`，需求落到 `RecoveryRequired`。

这正是 onboard-hmi#78 第 5 项要补的：授权取消后先中止原执行器对该 attempt 的目标态闭环，再由取消执行器清空。在它合入之前，这条场景在 v2 车载端上不会绿，也不该为变绿改断言。

## 同一类：staged runner 的两条判据仍按批次 5 之前的重连行为写

control-server#87 在 E 组改动之后跑 `run-staged-g3.ps1` 自检（2026-09-18 07:52–07:55Z，控制端 `96e617df`、车载端 `eafec8b0`、模拟器 `fb5f7c59`、协议 `86575456`；证据 `selfcheck/staged-001/`），29 条里红了两条，两条的判定代码本票都没动：

- `recoveryStateReportFirstAckDropReplay`（`FP-IS-00`）：判据要求丢掉第一份 `RecoveryStateReport` 的确认后，车载端在新连接上**以同一 `messageId`、同一载荷**补发。实测（`fault-proxy-events.ndjson`）车载端重连后先走完整握手，再发一条**新 `messageId`** 的 `RecoveryStateReport`（`recoveryReportSendCount 1`、`forwardedReplayAckCount 0`）。
- `onboardAlarmSnapshotNotRepublishedOnRecoveryResume`（`FP-IS-15`）：判据要求恢复续连时不重发告警快照；实测重连后走了完整握手，快照照发。

两者都对得上批次 5 车载端已合入的改动：onboard-hmi#69（批次5-14，补发后照常握手）与 onboard-hmi#71（批次5-19，每次发送用新 `messageId`、逻辑 id 不变）。所以这是 runner 判据落后于 v2 车载端的已定行为，与上面的六条场景同一类，一并交 control-server#128 核对后改判据（或在确认是缺陷时另开单）。同一轮 `run-staged-g3-restart.ps1` 26/26、`run-demand-bearing-g3-vectors.ps1` 16/16 全部通过。

## 怎么收口

已开票 control-server#128（批次5-37）承接下面两项；它被 control-server#87 与 onboard-hmi#78 阻塞，同时阻塞 control-server#90。

1. 恢复类五条：由 control-server#128 改写 `G3RecoveryCommon.ps1` 的前置，不再依赖车载端报确定失败。要在 control-server#90 按新归属出 G3 证据之前完成，否则 `FP-IS-03`、`FP-IS-07` 的 journey 面整片是红的。
2. `g3-load-cancellation`：由 control-server#128 在 onboard-hmi#78 合入后、在新的车载端提交上复跑；复跑仍红再按车载端缺陷单独开单。
3. 本票（control-server#87）按冲突边界不改既有 `g3-*` 场景，只记录。
