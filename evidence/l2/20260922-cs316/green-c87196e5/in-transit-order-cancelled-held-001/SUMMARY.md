# L2 场景证据：in-transit-order-cancelled-held

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T111438215Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `c87196e5eaafc1f2faf9096030db3c8089ca76b1` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260922T111438215Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 订单被取消后旅程写 ORDER_ENDED_WITHOUT_ARRIVAL，仍停在开往取货站 | PASS | `AwaitingPickupArrival / ORDER_ENDED_WITHOUT_ARRIVAL` | `AwaitingPickupArrival / ORDER_ENDED_WITHOUT_ARRIVAL` |
| 不释放、不改派、不重建：归属未移除，仍只有一趟旅程、一张移动意图 | PASS | `未移除 / 1 趟 / 1 张` | `未移除 / 1 趟 / 1 张` |
| 没有发出任何订单命令或急停（包括对已终结的单再发取消），码与开始时刻不变 | PASS | `0 / 0 / ORDER_ENDED_WITHOUT_ARRIVAL / 2026-09-22 11:15:23.711547+00:00` | `0 / 0 / ORDER_ENDED_WITHOUT_ARRIVAL / 2026-09-22 11:15:23.711547+00:00` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
