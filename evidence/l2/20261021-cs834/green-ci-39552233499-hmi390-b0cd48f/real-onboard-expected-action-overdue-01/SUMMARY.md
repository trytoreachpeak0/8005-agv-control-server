# L2 场景证据：real-onboard-expected-action-overdue

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261021T055913628Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `24e7f55dd26c916b882de5250a731c5cf21c544e` |
| expectedActionOverdueThreshold | `00:00:20` |
| onboardHmiCommit | `b0cd48f808972710b9d8bba9cc3607e86f58103f` |
| protocolFaultProxy | `True` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `18d4fb3495033624cab412a7ee784f675dfe4cc2` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `dd973918cc9c4fdf28ee2ad2ec4ad522bdcc6f2b6ecaa4a4fa1c57a29f29f5aa` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\actions-runner\vmwin01-control-server-desktop\_work\_temp\real-rig-39552233499-1\_stage\l2-20261021T055913628Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 空关后车不自己重开（5 秒门关着、锁扣上、没有 UNLOCKING）；按「重新开门」后重开：这次装货的 UNLOCKING 进度 ≥ 2 条，1 号仓门又弹开、空着、开锁输出复位，重开早于门槛 | PASS | `空关后车没有自己重开 / 按了重新开门 / UNLOCKING ≥ 2 / OPEN/EMPTY/0/0 / 重开早于门槛` | `空关后车没有自己重开（UNLOCKING 1→1；+0s CLOSED/EMPTY/0/0 -> +0.2s CLOSED/EMPTY/1/0） / 按了重新开门 / UNLOCKING 2 / OPEN/EMPTY/0/0 / 重开于第一次开锁后 13.6 s` |
| 门槛之前没有超时告警：最新一份告警快照里没有这个码，端点 slots 为空，HMI 上 ExpectedActionOverdue 不在 UIA 树里，服务端没要过中途快照；读完仍在门槛之前 | PASS | `告警 0 / 端点 0 行 / HMI 无 / 请求 0 / 读于门槛前` | `告警 0 / 端点 0 行 / HMI 无 / 请求 0 / 读于门槛前 4.5 s` |
| 越过门槛后告警出现且字段正确：code、subjectType=SLOT、subjectId=1、displayMessage 是三种期待动作之一；raisedAt ≈ 第一次开锁 + 门槛（±2 s，等按键的那段照常累计），不 ≈ 按键重开 + 门槛（按键不清零） | PASS | `SLOT_EXPECTED_ACTION_OVERDUE / SLOT / 1 / 三种期待动作之一 / 距第一次开锁 + 门槛 ≤ 2 s / 距重开 + 门槛 > 2 s` | `SLOT_EXPECTED_ACTION_OVERDUE / SLOT / 1 / '放入货物并关好1号仓门' / 距第一次开锁 + 门槛 -0.03 s / 距重开 + 门槛 -13.67 s` |
| 线上：这份告警快照之后，同一连接上出现 server->onboard:SafetyStateSnapshotRequested，随后 onboard->server:SafetyStateSnapshot；到此只有一条连接 | PASS | `告警、请求、快照同一连接 / 1 条连接` | `告警 #1 请求 #1 快照 #1 / 1 条连接` |
| 中途快照被采纳、不把会话打回握手：同一代次 1 有第二份 SafetyStateSnapshot，版本大于握手那份（1），回应 SnapshotAppliedAck + SessionReadiness；会话仍 Ready、不是 HANDSHAKE_INCOMPLETE；没有恢复痕迹 | PASS | `v > 1 / SnapshotAppliedAck,SessionReadiness / gen 1 Ready / 恢复会话 0 / 恢复工作流 0 / RecoveryRequired 操作 0` | `v10 / SnapshotAppliedAck,SessionReadiness / gen 1 / Ready / READY / 恢复会话 0 / 恢复工作流 0 / RecoveryRequired 操作 0` |
| 端点一行、字段对得上：thresholdSeconds = 20；slots 恰好一行，车、仓、取货站、LOAD、期待动作与 raisedAt 同告警，waitedSeconds ≥ 门槛，站点期限未过 | PASS | `threshold 20 / 1 行 / AGV-L2-001 slot 1 N1-3_N1-7 LOAD '放入货物并关好1号仓门' raisedAt 2026-10-21T05:59:50.5562904+00:00 waited ≥ 20 stationTimeout=False` | `threshold 20 / 1 行 / AGV-L2-001 slot 1 N1-3_N1-7 LOAD '放入货物并关好1号仓门' raisedAt 2026-10-21T05:59:50.5562904+00:00 waited 20s stationTimeout=False readings v10 UNLOCKED/EMPTY/RESET changed=False` |
| 读数来自中途快照且与模拟器一致：readings 非空，版本 = 中途那份（v10，不是握手的 v1），锁／光幕／开锁输出 = 模拟器此刻（UNLOCKED/EMPTY/RESET），changedSinceObserved = false | PASS | `v10 UNLOCKED/EMPTY/RESET changed=False（模拟器 UNLOCKED/EMPTY/RESET）` | `v10 UNLOCKED/EMPTY/RESET changed=False（模拟器 UNLOCKED/EMPTY/RESET）` |
| 看板页出卡片：「期待动作超时」卡片门槛前写「无期待动作超时的仓位」，越过门槛后有这台车、这个仓、装货与期待动作的一行 | PASS | `门槛前「无期待动作超时的仓位」/ 门槛后「AGV-L2-001 N1-3_N1-7 装货 1 放入货物并关好1号仓门」` | `门槛前 '期待动作超时 门槛 0 分 20 秒：当前仓自第一次开锁起累计等待达到门槛仍未闭环时由车载端上报；只让人看到，班组长或管理员到现场查看。 无期待动作超时的仓位' / 门槛后 '期待动作超时 门槛 0 分 20 秒：当前仓自第一次开锁起累计等待达到门槛仍未闭环时由车载端上报；只让人看到，班组长或管理员到现场查看。 车 站点 操作 仓位 期待的动作 已等待 读数 站点期限 人工判故障 AGV-L2-001 N1-3_N1-7 装货 1 放入货物并关好1号仓门 0 分 20 秒 锁 UNLOCKED；光幕 EMPTY；开锁输出 RESET（13:59:50 读数） 判故障'` |
| HMI 提示「已上报」：ExpectedActionOverdue 在 UIA 树里，文字含告警的 displayMessage 与「已上报」，不是断开文案 | PASS | `含 '放入货物并关好1号仓门' 与 '已上报'` | `'期待的操作（放入货物并关好1号仓门）很久没有完成，已上报，班组长或管理员会到现场查看'` |
| 上报不改变行为（守护判据）：告警在时这次装货没有结果，最新进度仍是开锁或等操作员，旅程仍 AwaitingLoadResult，期限起点不变，没有恢复痕迹 | PASS | `0 result / UNLOCKING 或 WAITING_OPERATOR / AwaitingLoadResult / 起点 2026-10-21 05:59:29.3898569+00:00 / 恢复会话 0 / 恢复工作流 0 / RecoveryRequired 操作 0` | `0 result / WAITING_OPERATOR / AwaitingLoadResult / 起点 2026-10-21 05:59:29.3898569+00:00 / 恢复会话 0 / 恢复工作流 0 / RecoveryRequired 操作 0` |
| 门槛后重开，服务端又要了一次快照：重开之后的流量里出现 server->onboard:SafetyStateSnapshotRequested 与随后的 onboard->server:SafetyStateSnapshot，两条都在告警那条连接（#1）上、全程仍只有一条连接；端点读数的版本也从中途那份（v10）前进 | PASS | `重开后请求与快照各一条、都在 #1 / 1 条连接 / 端点读数 v > 10` | `请求 #1 快照 #1 / 1 条连接 / 端点读数 v18` |
| 门槛后再空关：车不自己重开，按「重新开门」后重开；端点仍恰好一行、raisedAt 不变（按键不清零）；各份告警快照里这一仓的超时都是同一个 alarmId、同一个 raisedAt | PASS | `空关后车没有自己重开 / 按了重新开门 / 重开 OPEN/EMPTY/0/0 / 1 个 alarmId / 1 个 raisedAt / 端点 1 行 raisedAt 2026-10-21T05:59:50.5562904+00:00` | `空关后车没有自己重开（UNLOCKING 2→2；+0s CLOSED/EMPTY/0/0 -> +0.2s CLOSED/EMPTY/1/0） / 按了重新开门 / 重开 OPEN/EMPTY/0/0 / 1 份快照带它，1 个 alarmId、1 个 raisedAt / 端点 1 行 AGV-L2-001 slot 1 N1-3_N1-7 LOAD '放入货物并关好1号仓门' raisedAt 2026-10-21T05:59:50.5562904+00:00 waited 29s stationTimeout=False readings v18 UNLOCKED/EMPTY/RESET changed=False` |
| 站点期限过后合成一行：期限过去、门仍开着（STATION_TIMEOUT_DOOR_NOT_CLOSED），端点 slots 仍恰好一行，stationTimeoutDoorNotClosed = true，raisedAt 不变 | PASS | `STATION_TIMEOUT_DOOR_NOT_CLOSED / 1 行 stationTimeout=True raisedAt 2026-10-21T05:59:50.5562904+00:00 / 门开` | `STATION_TIMEOUT_DOOR_NOT_CLOSED / 1 行 AGV-L2-001 slot 1 N1-3_N1-7 LOAD '放入货物并关好1号仓门' raisedAt 2026-10-21T05:59:50.5562904+00:00 waited 59s stationTimeout=True readings v18 UNLOCKED/EMPTY/RESET changed=False / OPEN/EMPTY/0/0` |
| 撤下（守护判据）：放货关门后装货 COMPLETED、Committed；随后最新一份告警快照里不再有这个码，端点 slots 为空，HMI 控件不在 UIA 树里 | PASS | `Committed / COMPLETED / 告警 0 / 端点 0 行 / HMI 无` | `Committed / COMPLETED / 告警 0 / 端点 0 行 / HMI 无` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
