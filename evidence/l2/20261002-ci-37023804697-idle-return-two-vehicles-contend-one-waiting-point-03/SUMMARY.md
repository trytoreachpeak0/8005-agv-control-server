# L2 场景证据：idle-return-two-vehicles-contend-one-waiting-point

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261002T151113771Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-8` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37023804697-1\_stage\l2-20261002T151113771Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 两台空闲车里恰好一辆形成空闲返回承诺（另等十几轮之后仍是一辆） | PASS | `1` | `1 / 1` |
| 承诺那一辆同一趟预占着 214，是等待点、在途预占；除此之外没有任何站点独占（215 没人预占） | PASS | `214 RESERVED WAITING_POINT BROKERX-L2-0001 idle-return:BROKERX-L2-0001:20261002T151142480Z` | `214 RESERVED WAITING_POINT BROKERX-L2-0001 idle-return:BROKERX-L2-0001:20261002T151142480Z` |
| 另一辆没有任何用途占有：它的承诺没形成，也没留下半截 | PASS | `0` | `BROKERX-L2-0002 : 0` |
| 承诺物化成恰好一趟空闲返回旅程（就是承诺那一趟）与一张开往 214 的意图，没有第二份，也没有搬运旅程 | PASS | `1 idle return idle-return:BROKERX-L2-0001:20261002T151142480Z / 0 transport / 1 intent to 214` | `1 idle returns idle-return:BROKERX-L2-0001:20261002T151142480Z / 0 transport / 1 intents` |
| 承诺之后来的搬运派给了没承诺的那辆，不派给已承诺的那辆 | PASS | `AGV-L2-002` | `AGV-L2-002` |
| 已承诺那辆的空闲返回原样留着：同一趟、同一个点，没被搬运取消、换点或抢走 | PASS | `IDLE_RETURN idle-return:BROKERX-L2-0001:20261002T151142480Z \| 214 idle-return:BROKERX-L2-0001:20261002T151142480Z` | `IDLE_RETURN idle-return:BROKERX-L2-0001:20261002T151142480Z \| 214 idle-return:BROKERX-L2-0001:20261002T151142480Z` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
