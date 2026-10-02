# L2 场景证据：charging-thresholds-relation-refused

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261002T153233298Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37023804697-1\_stage\l2-20261002T153233298Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：编排器导入并激活了默认测试策略，库里恰好一版 | PASS | `1 版，已激活` | `1 版，激活 1` |
| 完成线 = 入口线的一版被整份拒绝：退出码 1、REJECTED、原因码是阈值关系那一个，库里版本数不变、生效的仍是默认那一版 | PASS | `1 / REJECTED / CHARGING_POLICY_THRESHOLD_RELATION_VIOLATED / 1 版 / 激活 1` | `1 / REJECTED / CHARGING_POLICY_THRESHOLD_RELATION_VIOLATED / 1 版 / 激活 1` |
| 被拒之后照常派车：需求派给了车，旅程记下的是默认那一版的版本号、SUFFICIENT | PASS | `BROKERX-L2-0001 / v1 / SUFFICIENT` | `BROKERX-L2-0001 / v1 / SUFFICIENT` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
