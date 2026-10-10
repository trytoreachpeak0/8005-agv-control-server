# L2 场景证据：charging-full-vehicle-yields-charger

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T162327122Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-9` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002, AGV-L2-003/BROKERX-L2-0003` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T162327122Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| A 在 211 上充到 COMPLETE：充电用途放开、211 仍是 A 的占用；B、C 在队里，另等十秒，两台都没有用途、站点独占或订单意图，RIoT 上仍只有 A 那一张充电单 | PASS | `OCCUPIED BROKERX-L2-0001 \| AGV-L2-002: 0 claims, 0 stations, 0 intents; AGV-L2-003: 0 claims, 0 stations, 0 intents \| 1 orders / A no claim` | `OCCUPIED BROKERX-L2-0001 \| AGV-L2-002: 0 claims, 0 stations, 0 intents; AGV-L2-003: 0 claims, 0 stations, 0 intents \| 1 orders / A claim` |
| 需求派给了充满的 A（仍报 CHARGING；B、C 低于强制充电线），开往取货站的单已确认；此刻 211 仍是 A 的占用 | PASS | `BROKERX-L2-0001 CONFIRMED / OCCUPIED BROKERX-L2-0001` | `BROKERX-L2-0001 CONFIRMED / OCCUPIED BROKERX-L2-0001` |
| A 离桩（到了机台 12、不再充电）之后 211 释放（CHARGER_RELEASED_ON_DEPARTURE），电量最低的 C 取得预占；B 仍在队里 | PASS | `RESERVED BROKERX-L2-0003 \| A released CHARGER_RELEASED_ON_DEPARTURE \| B 0 claims` | `RESERVED BROKERX-L2-0003 \| A released CHARGER_RELEASED_ON_DEPARTURE \| B 0 claims` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
