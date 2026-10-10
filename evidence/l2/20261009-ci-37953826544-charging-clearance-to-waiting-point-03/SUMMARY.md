# L2 场景证据：charging-clearance-to-waiting-point

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T161000694Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-9` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
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
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T161000694Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| A 在 211 上充不上（407802 + HANG）：桩暂停（UNABLE_TO_CHARGE_CONFIRMED，未恢复），周期清桩中，A 的用途 CLEARING_MAINTENANCE；B 收敛占着 214 | PASS | `UNABLE_TO_CHARGE_CONFIRMED recovered=0 \| CLEARING \| CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261009T161031487Z / 214 OCCUPIED BROKERX-L2-0002` | `UNABLE_TO_CHARGE_CONFIRMED recovered=0 \| CLEARING \| CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261009T161031487Z / 214 OCCUPIED BROKERX-L2-0002 idle-return:BROKERX-L2-0002:20261009T161029312Z` |
| 旧单结束、214 被 B 占着、215 不可达：A 原地排队（CHARGING_CLEARANCE_NO_WAITING_POINT），另等十秒没有清桩移动意图、名下只有 211、只有那一张充电单；不猜别的站（REQ-0178）；告警恰好一次 | PASS | `AwaitingPickupArrival CHARGING_CLEARANCE_NO_WAITING_POINT \| 0 clearance intents \| 1 stations held by A \| 1 intents of A \| 214 OCCUPIED BROKERX-L2-0002 / warned once` | `AwaitingPickupArrival CHARGING_CLEARANCE_NO_WAITING_POINT \| 0 clearance intents \| 1 stations held by A \| 1 intents of A \| 214 OCCUPIED BROKERX-L2-0002 / warned 1` |
| B 离开 214、离点清扫放掉它的那一轮，清桩车 A 与空闲返回车 B 争这最后一个点：只有一个成功——A（清桩承诺在引擎推进里，空闲返回评估在派车轮末尾）；另等十秒，214 一直是 A 那趟充电旅程的预占，B 没有空闲返回占有 | PASS | `RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161031487Z \| (none)` | `RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161031487Z / RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161031487Z \| (none)` |
| A 的清桩移动单建成（单段 move，目标 214），车在路上：另等十秒，211 一直是这一趟的独占、清桩记录一直未完成、214 一直是预占 | PASS | `RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161031487Z \| open \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161031487Z / to 214` | `RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161031487Z \| open \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161031487Z / to 214` |
| 到点证据满足的那一轮：清桩完成（ARRIVED_AT_WAITING_POINT，等待点 214）、211 释放（CHARGER_RELEASED_ON_CLEARANCE_AT_WAITING_POINT）、周期以 CHARGING_UNABLE_TO_CHARGE_CLEARED_AT_WAITING_POINT 结束、旅程收尾、用途放开、214 转为 A 的占用；211 的暂停仍在（没有恢复） | PASS | `(none) \| CHARGER_RELEASED_ON_CLEARANCE_AT_WAITING_POINT \| ENDED CHARGING_UNABLE_TO_CHARGE_CLEARED_AT_WAITING_POINT \| Completed CHARGING_UNABLE_TO_CHARGE_CLEARED_AT_WAITING_POINT \| (none) \| UNABLE_TO_CHARGE_CONFIRMED recovered=0 \| completed ARRIVED_AT_WAITING_POINT 214 \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161031487Z` | `(none) \| CHARGER_RELEASED_ON_CLEARANCE_AT_WAITING_POINT \| ENDED CHARGING_UNABLE_TO_CHARGE_CLEARED_AT_WAITING_POINT \| Completed CHARGING_UNABLE_TO_CHARGE_CLEARED_AT_WAITING_POINT \| (none) \| UNABLE_TO_CHARGE_CONFIRMED recovered=0 \| completed ARRIVED_AT_WAITING_POINT 214 \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161031487Z` |
| 全程本服务端没有发任何订单命令（不取消旧单——开关默认关——也不取消清桩移动单） | PASS | `0` | `0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
