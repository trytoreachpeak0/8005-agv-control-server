# L2 场景证据：create-gate-unapproved

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260918T152630764Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `06b656880ce6a32c8250b4ad62ad8ad3ce11b936` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35361077376-1\_stage\l2-20260918T152630764Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 两个参数未批准时服务端照常启动（REQ-0303） | PASS | `已启动` | `已启动` |
| 阻断原因是 CATALOG_PARAMETERS_NOT_APPROVED，能追到具体这道门禁 | PASS | `CATALOG_PARAMETERS_NOT_APPROVED` | `CATALOG_PARAMETERS_NOT_APPROVED` |
| 一个 JourneyRuntime 都没有 | PASS | `0` | `0` |
| 一张 RIoT move 单都没建 | PASS | `0` | `0` |
| 需求没有被接受 | PASS | `0` | `0` |
| 没有冻结任何端点——站点解析压根没发生（REQ-0303） | PASS | `0` | `0` |
| 门禁审计是空的——目录级阻断不是关于任何一个需求端点的裁决 | PASS | `0` | `0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
