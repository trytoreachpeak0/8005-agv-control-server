# L2 场景证据：real-onboard-rebuild-stopped-cargo-handoff

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260923T182047691Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `bb71e3567804a53e74c9ead21c36776e9d2e3e19` |
| onboardHmiCommit | `1184bb0762462f443736de749fe242fc9aa2f0d7` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `2.0.0` |
| protocolReleaseIdentity.tag | `protocol-v2.0.0` |
| protocolReleaseIdentity.commit | `86575456c847041515b7b75e8851a00e0d939804` |
| protocolReleaseIdentity.protocolVersion | `3` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| protocolReleaseIdentity.schemaBundleSha256 | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| protocolReleaseIdentity.vectorsSha256 | `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260923T182047691Z` |
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
| 转交接之后服务端向车宣布会话要恢复：就绪 RecoveryRequired / CARGO_HANDOFF_REQUIRED（衔接 b，WireToGateStore.DecideReadinessAsync 只认这一个旅程码） | FAIL | `RecoveryRequired/CARGO_HANDOFF_REQUIRED` | `Ready/READY` |
| 车载端给出「故障交接」入口（它只在会话 RECOVERY_REQUIRED 时给；修前就绪是 READY，这里不出现） | FAIL | `offered` | `90 秒内没出现` |
| 未到达：车载端没有给出「故障交接」入口 | FAIL | `(reached)` | `(not reached) 车载端没有给出「故障交接」入口` |
| 未到达：车载端没有给出「故障交接」入口 | FAIL | `(reached)` | `(not reached) 车载端没有给出「故障交接」入口` |
| 未到达：车载端没有给出「故障交接」入口 | FAIL | `(reached)` | `(not reached) 车载端没有给出「故障交接」入口` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
