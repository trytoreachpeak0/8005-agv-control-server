# L2 场景证据：charging-unable-to-charge-pauses-charger

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T163118053Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-9` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T163118053Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 单在 211 上执行开始充电、RIoT 返回 407802 且停在 HANG（9）、全程不报 CHARGING：一条 UNABLE_TO_CHARGE_CONFIRMED 的暂停（根因 UNKNOWN），周期 UNABLE_TO_CHARGE／CLEARING，用途 CLEARING_MAINTENANCE，旅程 CHARGING_UNABLE_TO_CHARGE | PASS | `UNABLE_TO_CHARGE_CONFIRMED UNKNOWN recovered=0 \| UNABLE_TO_CHARGE CLEARING \| CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261009T163142419Z \| CHARGING_UNABLE_TO_CHARGE / order 9` | `UNABLE_TO_CHARGE_CONFIRMED UNKNOWN recovered=0 \| UNABLE_TO_CHARGE CLEARING \| CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261009T163142419Z \| CHARGING_UNABLE_TO_CHARGE / order 9` |
| 确认之后另等十秒：没有任何新的订单意图、RIoT 上仍只有那一张充电单、211 仍是这一趟的、用途仍是 CLEARING_MAINTENANCE、只有一条暂停、命令审计里一条订单命令都没有（车保持原位，原桩重试为 0，取消开关默认关） | PASS | `1 intents \| W2G-CHARGE-BROKERX-L2-0001-20261009T163142419Z \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T163142419Z \| CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261009T163142419Z \| 1 holds \| 0 order commands` | `1 intents \| W2G-CHARGE-BROKERX-L2-0001-20261009T163142419Z \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T163142419Z \| CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261009T163142419Z \| 1 holds \| 0 order commands` |
| 车被挪开后，L2-R11 经车载端确认清桩；旧单还停在 HANG：CONFIRMED、stationReleased=false，清桩记录写上确认人与此刻的旧单处置 HANG、清桩未完成（REQ-0178），211 不放 | PASS | `CONFIRMED released=0 \| L2-R11 HANG open \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T163142419Z` | `CONFIRMED released=0 \| L2-R11 HANG open \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T163142419Z` |
| 取消开关默认关：确认之后旧单仍 HANG，另等八秒，服务端没有发 CMD_ORDER_CANCEL（RIoT 一侧与命令审计都是 0），211 仍是这一趟的（清桩未完成） | PASS | `0 cancels \| 0 audited \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T163142419Z` | `0 cancels \| 0 audited \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T163142419Z` |
| 旧单在 RIoT 里结束的那一轮：清桩在这一刻完成（旧单处置 CANCELLED）、211 的独占释放（CHARGER_RELEASED_ON_MANUAL_CLEARANCE）、周期以 CHARGING_UNABLE_TO_CHARGE_CLEARED 结束、旅程收尾、用途放开；暂停仍在（没有恢复） | PASS | `(none) \| CHARGER_RELEASED_ON_MANUAL_CLEARANCE \| ENDED CHARGING_UNABLE_TO_CHARGE_CLEARED \| Completed CHARGING_UNABLE_TO_CHARGE_CLEARED \| (none) \| UNABLE_TO_CHARGE_CONFIRMED UNKNOWN recovered=0 \| completed CANCELLED` | `(none) \| CHARGER_RELEASED_ON_MANUAL_CLEARANCE \| ENDED CHARGING_UNABLE_TO_CHARGE_CLEARED \| Completed CHARGING_UNABLE_TO_CHARGE_CLEARED \| (none) \| UNABLE_TO_CHARGE_CONFIRMED UNKNOWN recovered=0 \| completed CANCELLED` |
| 清桩之后车仍低电：另等十秒，没有第二个充电周期、211 没被谁取得、不进人工充电等待；服务端以 211=CHARGER_ALLOCATION_HELD 排队告警（无合格桩） | PASS | `1 cycles \| 0 manual holds \| (none) / warning '211=CHARGER_ALLOCATION_HELD' logged` | `1 cycles \| 0 manual holds \| (none) / warning logged 2 times` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
