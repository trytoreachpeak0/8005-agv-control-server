# 现场窗口一（2026-09-11）：派车跑了两站，三幕一幕没演成，停在一个恢复死锁上（未 finalize）

结论：**未完成，且本窗口到此结束**——用户当场决定不再以人工开关仓门、放料、按 HMI 的方式验收，
改为全部自动化。本目录不会 finalize，也不会再往里追加。

对应地图票 [现场窗口一](https://github.com/trytoreachpeak0/8005-agv-program/issues/19)（已关闭）。
前两次是 `../20260910-FW-SC1-operator-inaction/`（卡在 #37）与 `../20260910-FW-SC1-operator-inaction-rerun/`（只开了窗）。

## 身份

| 项 | 值 |
| --- | --- |
| 包 | `20260910T121022Z` |
| 服务端 | `56a06ef` |
| 车载端 | `3cf2665` |
| 协议 | `protocol-v0.3.0` |
| 车 | agv01 `老厂前线新多仓位1`；10:19 之前 IO 是真实模块 `192.168.71.150:502`，之后是车上的 slots-simulator `127.0.0.1:1502` |

## 时间线（本地时间）

| 时刻 | 事件 |
| --- | --- |
| 09:48–09:51 | 恢复窗口开、车载端 `-NoSimulator` 起、`generation=416 Ready`；帧 `01-00-ready` |
| 09:55:17 | 两个旅程门开到 `Dispatch`（用户在对话里授权） |
| 09:55:21 | 受理 `Q26092501-2`，车开往停靠 1；到站后吸收成四需求旅程（`Q26091208-22`、`Q26091789-2`、`Q26092004-5`） |
| 09:59:23 | 停靠 1 到站，进 `AwaitingSublot` |
| 10:04:23 | 停靠 1 期限到期：**5 分钟内服务端一条 `SublotSubmitted` 都没收到**，`Q26092501-2` 按站点期限转 `Cancelled` 并永久抑制；车开往停靠 2 |
| 10:06:01 | 停靠 2 到站 |
| 10:08:23 | 扫码 `Q26091208-22` 受理，装载命令下发，`UNLOCKING=1`、`WAITING_OPERATOR=1`（3 号仓） |
| 10:09:53 | 车载端日志 `IO模块离线。`，此后再无日志，客户端随后被关闭 |
| 10:12:03 | 两个旅程门关回 `Off`（用户决定） |
| 10:19:45 | agv01 IO 切到模拟器；帧 `02-aborted-before-simulator-switch` 在切换之前 |
| 10:20:08 | 车载端重启，`generation=418 RecoveryRequired`，服务端 `PENDING_FACT_RECONCILIATION_REQUIRED`，`PendingAttemptIdsJson=["e6f561f4-…"]`、`ActiveUnlockSlotsJson=[3]`、`ProvenRecoveryCheckpoint=ACTIVE_UNLOCK_SET` |
| 10:23:04 | HMI 上点「补偿清空」被拒：`恢复向量LOAD_COMPENSATION未执行：reason=RECOVERY_DEMAND_NOT_BLOCKED` |
| 10:26:08–10:26:49 | 只开引擎门（`Intake`）试探：旅程只挂上 `ONBOARD_SESSION_NOT_READY`，不转 `Blocked`；关回 `Off`，核实没有受理任何新需求 |
| 10:28 前后 | 帧 `03-deadlocked-recovery-refused` |
| 10:30:54–10:31:35 | 客户端与模拟器停止，恢复窗口 `-Revert`，两端复核关闭 |

## 读这个目录要知道的

- **死锁的形状**：客户端在「开了锁、等操作员」时退出，重启后车只上报一个没了结的开锁（`PendingAttemptIds`），
  不补交 `OperationResult`；服务端没有结果，仓位操作停在 `Prepared`，引擎只在 `RecoveryRequired` 时才把旅程判
  `Blocked`（`JourneyRuntimeEngine.cs:654`）；而恢复入口要求旅程是 `Blocked`（`OnboardRecoveryCoordinator.cs:987`）。
  产品里没有任何按钮能从这里出去。
- **`SUBLOT_SUBMISSION_MISMATCH` 在两个停靠都是到站 2–3 秒就挂上的**，不是扫错码：`FindMatchingSublotAsync`
  每轮把 `ProtocolInbox` 里全部历史 `SublotSubmitted` 捞出来比，旧提交对不上会话就写这个码。
- **停靠 1 没有扫码到达服务端**，现有证据分不清是没人扫还是扫了没发出去。
- **10:09:53 的 `IO模块离线` 没有归因**。现场没有断电或拔线的记录，之后从车上探 502 是通的。
- 旅程 `54d2cf63-6274-6751-a64b-a7866e61ac4e` **仍卡在停靠 2**：需求 2 `Planned` 带一条 `Prepared` 装载操作，
  需求 3、4 `Planned`，车停在 `N17-8_N18-8`，两个旅程门关闭。`12-reset-journey-state.ps1` 会拒绝它（有 `StationOperations`）。
- 车上 3、4 号仓的真实仓门在关闭客户端时由用户确认是关着、没放料的。
