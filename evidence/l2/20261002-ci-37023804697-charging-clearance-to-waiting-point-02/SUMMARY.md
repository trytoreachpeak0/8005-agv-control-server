# L2 场景证据：charging-clearance-to-waiting-point

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261002T152507478Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-9` |
| controlServerCommit | `6b7b3e38b7d72b454b227a104de2e37a3198db86` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
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
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37023804697-1\_stage\l2-20261002T152507478Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| A 在 211 上充不上（407802 + HANG）：桩暂停（UNABLE_TO_CHARGE_CONFIRMED，未恢复），周期清桩中，A 的用途 CLEARING_MAINTENANCE；B 收敛占着 214 | PASS | `UNABLE_TO_CHARGE_CONFIRMED recovered=0 \| CLEARING \| CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261002T152538141Z / 214 OCCUPIED BROKERX-L2-0002` | `UNABLE_TO_CHARGE_CONFIRMED recovered=0 \| CLEARING \| CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261002T152538141Z / 214 OCCUPIED BROKERX-L2-0002 idle-return:BROKERX-L2-0002:20261002T152536050Z` |
| 旧单结束、214 被 B 占着、215 不可达：A 原地排队（CHARGING_CLEARANCE_NO_WAITING_POINT），另等十秒没有清桩移动意图、名下只有 211、只有那一张充电单；不猜别的站（REQ-0178）；告警恰好一次 | PASS | `AwaitingPickupArrival CHARGING_CLEARANCE_NO_WAITING_POINT \| 0 clearance intents \| 1 stations held by A \| 1 intents of A \| 214 OCCUPIED BROKERX-L2-0002 / warned once` | `AwaitingPickupArrival CHARGING_CLEARANCE_NO_WAITING_POINT \| 0 clearance intents \| 1 stations held by A \| 1 intents of A \| 214 OCCUPIED BROKERX-L2-0002 / warned 1` |
| B 离开 214、离点清扫放掉它的那一轮，清桩车 A 与空闲返回车 B 争这最后一个点：只有一个成功——A（清桩承诺在引擎推进里，空闲返回评估在派车轮末尾）；另等十秒，214 一直是 A 那趟充电旅程的预占，B 没有空闲返回占有 | PASS | `RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152538141Z \| (none)` | `RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152538141Z / RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152538141Z \| (none)` |
| A 的清桩移动单建成（单段 move，目标 214），车在路上：另等十秒，211 一直是这一趟的独占、清桩记录一直未完成、214 一直是预占 | PASS | `RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152538141Z \| open \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152538141Z / to 214` | `RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152538141Z \| open \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152538141Z / to 214` |
| 到点证据满足的那一轮：清桩完成（ARRIVED_AT_WAITING_POINT，等待点 214）、211 释放（CHARGER_RELEASED_ON_CLEARANCE_AT_WAITING_POINT）、周期以 CHARGING_UNABLE_TO_CHARGE_CLEARED_AT_WAITING_POINT 结束、旅程收尾、用途放开、214 转为 A 的占用；211 的暂停仍在（没有恢复） | PASS | `(none) \| CHARGER_RELEASED_ON_CLEARANCE_AT_WAITING_POINT \| ENDED CHARGING_UNABLE_TO_CHARGE_CLEARED_AT_WAITING_POINT \| Completed CHARGING_UNABLE_TO_CHARGE_CLEARED_AT_WAITING_POINT \| (none) \| UNABLE_TO_CHARGE_CONFIRMED recovered=0 \| completed ARRIVED_AT_WAITING_POINT 214 \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152538141Z` | `(none) \| CHARGER_RELEASED_ON_CLEARANCE_AT_WAITING_POINT \| ENDED CHARGING_UNABLE_TO_CHARGE_CLEARED_AT_WAITING_POINT \| Completed CHARGING_UNABLE_TO_CHARGE_CLEARED_AT_WAITING_POINT \| (none) \| UNABLE_TO_CHARGE_CONFIRMED recovered=0 \| completed ARRIVED_AT_WAITING_POINT 214 \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152538141Z` |
| 全程本服务端没有发任何订单命令（不取消旧单——开关默认关——也不取消清桩移动单） | PASS | `0` | `0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
