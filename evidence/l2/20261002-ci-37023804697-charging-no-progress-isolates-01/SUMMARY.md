# L2 场景证据：charging-no-progress-isolates

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261002T152855372Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37023804697-1\_stage\l2-20261002T152855372Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 观察窗口起点开窗之后不被新样本重置：形成时周期上的起点仍是开窗那一刻（2026-10-02 15:29:26.5213195+00:00），形成时刻距起点不少于窗口 20 秒 | PASS | `window start 2026-10-02 15:29:26.5213195+00:00 / >= 20 s` | `window start 2026-10-02 15:29:26.5213195+00:00 / 21 s` |
| 一直报 CHARGING、电量不涨：窗口满后桩的分配暂停与车的充电资格暂停都写下（NO_PROGRESS_CONFIRMED，根因 UNKNOWN），周期进清桩中，用途转 CLEARING_MAINTENANCE，211 仍是这一趟的占用 | PASS | `NO_PROGRESS_CONFIRMED UNKNOWN \| NO_PROGRESS_CONFIRMED \| CLEARING UNABLE_TO_CHARGE \| claim CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261002T152919595Z \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152919595Z \| journey AwaitingPickupArrival CHARGING_NO_PROGRESS_CONFIRMED` | `NO_PROGRESS_CONFIRMED UNKNOWN \| NO_PROGRESS_CONFIRMED \| CLEARING UNABLE_TO_CHARGE \| claim CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261002T152919595Z \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152919595Z \| journey AwaitingPickupArrival CHARGING_NO_PROGRESS_CONFIRMED` |
| 形成之后另等十秒：RIoT 上一直只有那一张充电单，命令审计 0 条，211 一直是这一趟的占用，两侧各只有一条暂停 | PASS | `W2G-CHARGE-BROKERX-L2-0001-20261002T152919595Z \| 0 commands \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152919595Z \| NO_PROGRESS_CONFIRMED UNKNOWN \| NO_PROGRESS_CONFIRMED` | `W2G-CHARGE-BROKERX-L2-0001-20261002T152919595Z \| 0 commands \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152919595Z \| NO_PROGRESS_CONFIRMED UNKNOWN \| NO_PROGRESS_CONFIRMED` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
