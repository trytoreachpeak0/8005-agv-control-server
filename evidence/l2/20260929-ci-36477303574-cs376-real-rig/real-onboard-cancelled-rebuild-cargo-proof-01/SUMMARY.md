# L2 场景证据：real-onboard-cancelled-rebuild-cargo-proof

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260928T201358971Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `70737b23b9a97c6c6fd1e7e179eb31af94b50b77` |
| onboardHmiCommit | `60f341878a1bce705598aa9043234c1ca2fb1d7a` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-36477303574-1\_stage\l2-20260928T201358971Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 别人的急停锁着（服务端没有发过触发），单 HANG 之后被人在 RIoT 取消：服务端记下取消来源的同车重建（ORDER_CANCELLED_IN_RIOT / PENDING），只有原来那一张 TO_GATE 单 | PASS | `ORDER_HANG / ORDER_CANCELLED_IN_RIOT PENDING / 触发 0 / TO_GATE 1` | `ORDER_HANG / ORDER_CANCELLED_IN_RIOT PENDING / 触发 0 / TO_GATE 1` |
| 延迟在锁着期间走完：重建被车况挡住（OWN_ORDER_REBUILD_WAITING_VEHICLE），记录仍 PENDING、记下这一次挡住开始（VehicleHeldAt），不建新单 | PASS | `OWN_ORDER_REBUILD_WAITING_VEHICLE / PENDING / VehicleHeldAt 有值 / TO_GATE 1` | `OWN_ORDER_REBUILD_WAITING_VEHICLE / PENDING (ONBOARD_SESSION_NOT_READY) / VehicleHeldAt 有值 / TO_GATE 1` |
| 解开急停之后不建开往卸货站的单：证据是解开之后收到的快照（晚于 CargoEvidenceNotBefore 与到期），它显示放货的仓是空的，重建停住（STOPPED / CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS），旅程码 OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE、仍在去关卡的阶段，只有原来那一张 TO_GATE 单 | PASS | `AwaitingGateArrival / OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE / STOPPED CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS / 证据晚于放开与到期 / TO_GATE 1` | `AwaitingGateArrival / OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE / STOPPED CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS / 证据 2026-09-28T20:15:36.0109947+00:00 放开 2026-09-28T20:15:35.0655929+00:00 到期 2026-09-28T20:15:33.1564856+00:00 / TO_GATE 1` |
| 取消来源停住之后人工转交接被受理（200 HandoffPrepared）：旅程 Blocked / OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF，记录 AWAITING_CARGO_HANDOFF，会话 RecoveryRequired / CARGO_HANDOFF_REQUIRED | PASS | `200 HandoffPrepared / Blocked/OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF / AWAITING_CARGO_HANDOFF / RecoveryRequired/CARGO_HANDOFF_REQUIRED` | `200 HandoffPrepared / Blocked/OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF / AWAITING_CARGO_HANDOFF / RecoveryRequired/CARGO_HANDOFF_REQUIRED` |
| 车载端给出「故障交接」入口：这一趟没有故障，也没有故障货物绑定，入口照样出现 | FAIL | `offered` | `90 秒内没出现` |
| 未到达：车载端没有给出「故障交接」入口 | FAIL | `(reached)` | `(not reached) 车载端没有给出「故障交接」入口` |
| 未到达：车载端没有给出「故障交接」入口 | FAIL | `(reached)` | `(not reached) 车载端没有给出「故障交接」入口` |
| 未到达：车载端没有给出「故障交接」入口 | FAIL | `(reached)` | `(not reached) 车载端没有给出「故障交接」入口` |
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
