# L2 场景证据：charging-unable-to-charge-pauses-charger

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261002T154618071Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-9` |
| controlServerCommit | `6b7b3e38b7d72b454b227a104de2e37a3198db86` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37023804697-1\_stage\l2-20261002T154618071Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 单在 211 上执行开始充电、RIoT 返回 407802 且停在 HANG（9）、全程不报 CHARGING：一条 UNABLE_TO_CHARGE_CONFIRMED 的暂停（根因 UNKNOWN），周期 UNABLE_TO_CHARGE／CLEARING，用途 CLEARING_MAINTENANCE，旅程 CHARGING_UNABLE_TO_CHARGE | PASS | `UNABLE_TO_CHARGE_CONFIRMED UNKNOWN recovered=0 \| UNABLE_TO_CHARGE CLEARING \| CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261002T154642322Z \| CHARGING_UNABLE_TO_CHARGE / order 9` | `UNABLE_TO_CHARGE_CONFIRMED UNKNOWN recovered=0 \| UNABLE_TO_CHARGE CLEARING \| CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261002T154642322Z \| CHARGING_UNABLE_TO_CHARGE / order 9` |
| 确认之后另等十秒：没有任何新的订单意图、RIoT 上仍只有那一张充电单、211 仍是这一趟的、用途仍是 CLEARING_MAINTENANCE、只有一条暂停、命令审计里一条订单命令都没有（车保持原位，原桩重试为 0，取消开关默认关） | PASS | `1 intents \| W2G-CHARGE-BROKERX-L2-0001-20261002T154642322Z \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T154642322Z \| CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261002T154642322Z \| 1 holds \| 0 order commands` | `1 intents \| W2G-CHARGE-BROKERX-L2-0001-20261002T154642322Z \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T154642322Z \| CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261002T154642322Z \| 1 holds \| 0 order commands` |
| 车被挪开后，L2-R11 经车载端确认清桩；旧单还停在 HANG：CONFIRMED、stationReleased=false，清桩记录写上确认人与此刻的旧单处置 HANG、清桩未完成（REQ-0178），211 不放 | PASS | `CONFIRMED released=0 \| L2-R11 HANG open \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T154642322Z` | `CONFIRMED released=0 \| L2-R11 HANG open \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T154642322Z` |
| 取消开关默认关：确认之后旧单仍 HANG，另等八秒，服务端没有发 CMD_ORDER_CANCEL（RIoT 一侧与命令审计都是 0），211 仍是这一趟的（清桩未完成） | PASS | `0 cancels \| 0 audited \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T154642322Z` | `0 cancels \| 0 audited \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T154642322Z` |
| 旧单在 RIoT 里结束的那一轮：清桩在这一刻完成（旧单处置 CANCELLED）、211 的独占释放（CHARGER_RELEASED_ON_MANUAL_CLEARANCE）、周期以 CHARGING_UNABLE_TO_CHARGE_CLEARED 结束、旅程收尾、用途放开；暂停仍在（没有恢复） | PASS | `(none) \| CHARGER_RELEASED_ON_MANUAL_CLEARANCE \| ENDED CHARGING_UNABLE_TO_CHARGE_CLEARED \| Completed CHARGING_UNABLE_TO_CHARGE_CLEARED \| (none) \| UNABLE_TO_CHARGE_CONFIRMED UNKNOWN recovered=0 \| completed CANCELLED` | `(none) \| CHARGER_RELEASED_ON_MANUAL_CLEARANCE \| ENDED CHARGING_UNABLE_TO_CHARGE_CLEARED \| Completed CHARGING_UNABLE_TO_CHARGE_CLEARED \| (none) \| UNABLE_TO_CHARGE_CONFIRMED UNKNOWN recovered=0 \| completed CANCELLED` |
| 清桩之后车仍低电：另等十秒，没有第二个充电周期、211 没被谁取得、不进人工充电等待；服务端以 211=CHARGER_ALLOCATION_HELD 排队告警（无合格桩） | PASS | `1 cycles \| 0 manual holds \| (none) / warning '211=CHARGER_ALLOCATION_HELD' logged` | `1 cycles \| 0 manual holds \| (none) / warning logged 2 times` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
