# L2 场景证据：idle-return-two-vehicles-contend-one-waiting-point

结论：**FAIL**

失败原因：Timed out after 120s waiting for: the demand published after the commitment was taken. Last observed: (nothing)

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260929T121256175Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `5e2c4485a909db70ab41cdc8c0dc77fee2ffbb34` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260929T121256175Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 两台空闲车里恰好一辆形成空闲返回承诺（另等十几轮之后仍是一辆） | PASS | `1` | `1 / 1` |
| 承诺那一辆同一趟预占着 214，是等待点、在途预占；除此之外没有任何站点独占（215 没人预占） | PASS | `214 RESERVED WAITING_POINT BROKERX-L2-0001 idle-return:BROKERX-L2-0001:20260929T121438955Z` | `214 RESERVED WAITING_POINT BROKERX-L2-0001 idle-return:BROKERX-L2-0001:20260929T121438955Z` |
| 另一辆没有任何用途占有：它的承诺没形成，也没留下半截 | PASS | `0` | `BROKERX-L2-0002 : 0` |
| 承诺不建单也不物化旅程：没有旅程行、没有订单意图 | PASS | `0 / 0` | `0 / 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
