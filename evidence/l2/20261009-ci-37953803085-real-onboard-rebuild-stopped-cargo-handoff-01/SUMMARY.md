# L2 场景证据：real-onboard-rebuild-stopped-cargo-handoff

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T162009572Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-8` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| onboardHmiCommit | `b9e67a538ba4cdf1916d201a08af40dd28270d14` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `3.0.0` |
| protocolReleaseIdentity.tag | `protocol-v3.0.0` |
| protocolReleaseIdentity.commit | `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c` |
| protocolReleaseIdentity.protocolVersion | `4` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e` |
| protocolReleaseIdentity.schemaBundleSha256 | `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43` |
| protocolReleaseIdentity.vectorsSha256 | `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-37953803085-1\_stage\l2-20261009T162009572Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| TO_GATE 单 FAILED：旅程码 VEHICLE_ORDER_FAILED，故障已记，车上的货有一条活着的故障货物绑定；车静止在已知站点，没有急停 | PASS | `VEHICLE_ORDER_FAILED / 故障已记 / 活绑定 1 / 急停 0` | `VEHICLE_ORDER_FAILED / 故障 SuspectedBlocked / 活绑定 1 / 急停 0` |
| 人工清除故障：200 Cleared / REBUILD_SCHEDULED，旅程码 VEHICLE_FAULT_CLEARED_CARGO_ON_BOARD（车上有货，重建要等清除之后的货物快照） | PASS | `200 Cleared REBUILD_SCHEDULED / VEHICLE_FAULT_CLEARED_CARGO_ON_BOARD（或已往后走）` | `200 Cleared REBUILD_SCHEDULED / VEHICLE_FAULT_CLEARED_CARGO_ON_BOARD` |
| 清除之后车载端的快照显示目标仓空：重建停住（STOPPED / CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS），旅程码 OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE、仍在去关卡的阶段；只有原来那一张 TO_GATE 单 | PASS | `AwaitingGateArrival / OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE / STOPPED CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS / TO_GATE 1` | `AwaitingGateArrival / OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE / STOPPED CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS / TO_GATE 1` |
| 货不在原仓时人工重建不许：409 OWN_ORDER_REBUILD_EXIT_CARGO_NOT_IN_PLACE（REQ-0238 只许交接），旅程码与单数不变 | PASS | `409 OWN_ORDER_REBUILD_EXIT_CARGO_NOT_IN_PLACE / OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE / TO_GATE 1` | `409 OWN_ORDER_REBUILD_EXIT_CARGO_NOT_IN_PLACE / OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE / TO_GATE 1` |
| 人工转交接被受理（200 HandoffPrepared / AWAITING_CARGO_HANDOFF）：旅程转 Blocked、码 OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF，重建记录 AWAITING_CARGO_HANDOFF | PASS | `200 HandoffPrepared AWAITING_CARGO_HANDOFF / Blocked/OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF / AWAITING_CARGO_HANDOFF` | `200 HandoffPrepared AWAITING_CARGO_HANDOFF / Blocked/OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF / AWAITING_CARGO_HANDOFF` |
| 同一转交接请求再来一次：200 AlreadyDone，记录仍是 AWAITING_CARGO_HANDOFF | PASS | `200 AlreadyDone / AWAITING_CARGO_HANDOFF` | `200 AlreadyDone / AWAITING_CARGO_HANDOFF` |
| 转交接之后服务端向车宣布会话要恢复：就绪 RecoveryRequired / CARGO_HANDOFF_REQUIRED（衔接 b，WireToGateStore.DecideReadinessAsync 只认这一个旅程码） | PASS | `RecoveryRequired/CARGO_HANDOFF_REQUIRED` | `RecoveryRequired/CARGO_HANDOFF_REQUIRED` |
| 车载端给出「故障交接」入口（它只在会话 RECOVERY_REQUIRED 时给；修前就绪是 READY，这里不出现） | PASS | `offered` | `offered` |
| 车载端的交接走完 CV-FAULT-CARGO-HANDOFF：RecoveryActionSubmitted(FAULT_CARGO_HANDOFF) 被接受，一条 FaultCargoRecoveryCommand，结果 HANDED_OFF；工作流 Reconciled / HANDED_OFF | PASS | `Action×1 RecoveryActionAccepted FAULT_CARGO_HANDOFF / Command×1 / HANDED_OFF / Reconciled HANDED_OFF` | `Action×1 RecoveryActionAccepted FAULT_CARGO_HANDOFF / Command×1 / HANDED_OFF / Reconciled HANDED_OFF` |
| 交接收敛：需求 Cancelled，旅程 Completed / TERMINATED_BY_FAULT_CARGO_HANDOFF，这条需求唯一的故障货物绑定以 HANDED_OFF_IN_EXCEPTION_SESSION 了结，停住的重建记录 ENDED，全程只有一张 TO_GATE 单，会话回到 Ready | PASS | `Cancelled / Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / 绑定 1 了结 1 / ENDED / TO_GATE 1 / Ready` | `Cancelled / Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / 绑定 1 了结 1 / ENDED / TO_GATE 1 / Ready` |
| 交接收敛之后车放出来了：这条需求的 TO_PICKUP 占用已释放，同一台车在 60 秒内接了下一单（旅程到 AwaitingPickupArrival、意图 CONFIRMED、没有停摆原因码） | PASS | `用途占有记录已释放 / 下一单 AwaitingPickupArrival on AGV-L2-001，TO_PICKUP 意图 CONFIRMED，没有停摆原因码` | `ClaimRecord.ReleasedAt='2026-10-09 16:21:57.5773265+00:00' / 下一单 AwaitingPickupArrival/ TO_PICKUP=CONFIRMED on AGV-L2-001` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
