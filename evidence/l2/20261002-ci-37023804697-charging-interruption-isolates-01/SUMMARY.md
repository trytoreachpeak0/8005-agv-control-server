# L2 场景证据：charging-interruption-isolates

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261002T152724894Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37023804697-1\_stage\l2-20261002T152724894Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 充着电在 45% 停了（完成阈值 80 之前）：桩的分配暂停与车的充电资格暂停都写下（INTERRUPTION_CONFIRMED，根因 UNKNOWN），周期进清桩中、线上 UNABLE_TO_CHARGE，用途转 CLEARING_MAINTENANCE，211 仍是这一趟的占用 | PASS | `INTERRUPTION_CONFIRMED UNKNOWN \| INTERRUPTION_CONFIRMED \| CLEARING UNABLE_TO_CHARGE \| claim CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261002T152749489Z \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152749489Z \| journey AwaitingPickupArrival CHARGING_INTERRUPTION_CONFIRMED \| charging seen True` | `INTERRUPTION_CONFIRMED UNKNOWN \| INTERRUPTION_CONFIRMED \| CLEARING UNABLE_TO_CHARGE \| claim CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261002T152749489Z \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152749489Z \| journey AwaitingPickupArrival CHARGING_INTERRUPTION_CONFIRMED \| charging seen True` |
| 形成之后另等十秒：RIoT 上一直只有那一张充电单（不在原桩重启、不换桩、不移动），命令审计里订单命令 0 条，211 一直是这一趟的占用，两侧各只有一条暂停 | PASS | `W2G-CHARGE-BROKERX-L2-0001-20261002T152749489Z \| 0 commands \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152749489Z \| INTERRUPTION_CONFIRMED UNKNOWN \| INTERRUPTION_CONFIRMED` | `W2G-CHARGE-BROKERX-L2-0001-20261002T152749489Z \| 0 commands \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152749489Z \| INTERRUPTION_CONFIRMED UNKNOWN \| INTERRUPTION_CONFIRMED` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
