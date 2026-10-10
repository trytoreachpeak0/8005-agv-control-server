# L2 场景证据：in-transit-order-cancelled-redispatch

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T102246337Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `3cbae94d0ec5b98a1c0d4b37cc2b42b546434313` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260922T102246337Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 释放：归属标 RELEASED_FOR_REDISPATCH，那趟旅程关闭（Completed / RELEASED_FOR_REDISPATCH） | PASS | `RELEASED_FOR_REDISPATCH / Completed / RELEASED_FOR_REDISPATCH` | `RELEASED_FOR_REDISPATCH / Completed / RELEASED_FOR_REDISPATCH` |
| 订单已经终结，服务端没有再发取消：审计表与假 RIoT 上都是零条 | PASS | `0 / 0` | `0 / 0` |
| 需求重新受理成新旅程：新代次、新 upperId（旧 upperId 不复用） | PASS | `upperId ≠ W2G-a5258f3b-3d6d-47fb-9c7e-1c3e60ed3103-PICKUP-1 / 代次 > 1` | `W2G-a5258f3b-3d6d-47fb-9c7e-1c3e60ed3103-PICKUP-2 / 代次 2` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
