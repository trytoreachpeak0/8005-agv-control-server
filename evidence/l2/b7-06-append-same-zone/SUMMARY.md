# L2 场景证据：multi-stop-append-same-zone

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260920T093553500Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `9129fec31c38e9d3abfd5243fdae8e9275002491` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260920T093553500Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 第一条需求被一辆空闲车接走，车已在途（AwaitingPickupArrival） | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| 受理写下两个停靠：取货与卸货各一个 | PASS | `2` | `2` |
| 第二条需求进了同一趟旅程：归属两条（REQ-0205 在途车参与竞争） | PASS | `2` | `2` |
| 没有另起一趟旅程：整个库里仍然只有一条 JourneyRuntime | PASS | `1` | `1` |
| 停靠从两个变成三个：第二条需求的取货新开一个停靠，它的卸货并进了既有的关卡停靠（不是四个） | PASS | `3` | `3` |
| 当前下一站没被动：序位 1 仍是第一条需求的取货站（REQ-0196） | PASS | `1:PICKUP@(非关卡)` | `1:PICKUP@12` |
| 三个停靠的序位是 1,2,3，连续且无重复 | PASS | `1,2,3` | `1,2,3` |
| 车上收到的最后一版计划带三条腿：计划是整体替换下发的（ADR-cross-0053） | PASS | `3` | `3` |
| 第二条需求被受理过：积压行上有受理时刻，没有被任何 EN_ROUTE_APPEND_* 挡住 | PASS | `(有受理时刻，理由不是 EN_ROUTE_APPEND_*)` | `DEMAND_ALREADY_ACCEPTED accepted=yes` |
| 关卡上只有一个停靠：第二条需求的卸货并进了既有那一个，没有同站再开一个 | PASS | `1` | `1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
