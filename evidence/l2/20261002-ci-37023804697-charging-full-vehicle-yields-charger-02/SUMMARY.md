# L2 场景证据：charging-full-vehicle-yields-charger

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261002T154029708Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-9` |
| controlServerCommit | `6b7b3e38b7d72b454b227a104de2e37a3198db86` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002, AGV-L2-003/BROKERX-L2-0003` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37023804697-1\_stage\l2-20261002T154029708Z-slot1` |
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
