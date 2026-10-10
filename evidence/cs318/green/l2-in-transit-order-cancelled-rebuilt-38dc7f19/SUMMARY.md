# L2 场景证据：in-transit-order-cancelled-rebuilt

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260923T052653076Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `38dc7f194ed57d17829828412ec49d49074dbc4c` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260923T052653076Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 订单被取消后旅程写 ORDER_ENDED_WITHOUT_ARRIVAL，仍停在开往取货站 | PASS | `AwaitingPickupArrival / ORDER_ENDED_WITHOUT_ARRIVAL` | `AwaitingPickupArrival / ORDER_ENDED_WITHOUT_ARRIVAL` |
| 延迟之内不释放、不改派、不重建：归属未移除，仍只有一趟旅程、一张移动意图，码与开始时刻不变 | PASS | `未移除 / 1 趟 / 1 张 / ORDER_ENDED_WITHOUT_ARRIVAL / 2026-09-23 05:27:22.5123183+00:00` | `未移除 / 1 趟 / 1 张 / ORDER_ENDED_WITHOUT_ARRIVAL / 2026-09-23 05:27:22.5123183+00:00` |
| 没有发出任何订单命令或急停（包括对已终结的单再发取消） | PASS | `0 / 0` | `0 / 0` |
| 延迟之后重建：取货停靠（同一个停靠、同一序位、同一站）改指向一张服务端自己的新单，意图已确认，需求与车都是原来那条、那辆，目标站不变；RIoT 上有这张单、指派给同一辆车 | PASS | `新单号 W2G-… / CONFIRMED / 7d258da5-fc4f-46e4-8472-9453fe44ad0a / BROKERX-L2-0001 / 站 12 / 序位 1 / RIoT 1 张` | `W2G-7d258da5-fc4f-46e4-8472-9453fe44ad0a-REBUILD-1-1 / CONFIRMED / 7d258da5-fc4f-46e4-8472-9453fe44ad0a / BROKERX-L2-0001 / 站 12 / 序位 1 / RIoT 1 张` |
| 重建之后：仍是同一趟旅程、开往取货站，码清掉；恰好两张移动意图（旧单留作记录）、一条重建记录（REBUILT、来源 ORDER_CANCELLED_IN_RIOT）；仍没有任何订单命令 | PASS | `AwaitingPickupArrival / (无码) / 1 趟 / 2 张 / REBUILT ORDER_CANCELLED_IN_RIOT / 0 条命令` | `AwaitingPickupArrival / (无码) / 1 趟 / 2 张 / REBUILT ORDER_CANCELLED_IN_RIOT / 0 条命令` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
