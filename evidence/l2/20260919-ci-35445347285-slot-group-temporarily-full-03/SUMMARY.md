# L2 场景证据：slot-group-temporarily-full

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T133640447Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-4` |
| controlServerCommit | `50dc987d680a33c3f8732619a91f5761453ddd63` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35445347285-1\_stage\l2-20260919T133640447Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 握手种子下本车 REAR 组没有可用仓、FRONT 组全部可用，且 FRONT 组足够装下 2 个花篮 | PASS | `available [1,2,3,4] = FRONT group; REAR group [5,6,7,8] all unavailable` | `available [1,2,3,4]; FRONT [1,2,3,4]; REAR [5,6,7,8]` |
| 积压原因为「所需分组暂时空仓不足」，再过三轮仍是 | PASS | `SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE` | `first SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE, after three rounds SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE` |
| 整车 FRONT 组空着但不受理：没有受理行、没有旅程、没有建 RIoT 单 | PASS | `AcceptedDemands 0, JourneyRuntimes 0, RIoT orders 0` | `AcceptedDemands 0, JourneyRuntimes 0, RIoT orders 0` |
| StructuralDispatchBlocks 没有这条需求的行（正常积压，不是结构性问题） | PASS | `0` | `0` |
| 换种子重连后，服务端按新会话的快照算出 REAR 组 [5,6] 可用 | PASS | `[1,2,3,4,5,6]` | `[1,2,3,4,5,6]` |
| REAR 组有空仓后受理，目标仓恰好是变空的 [5,6] | PASS | `AwaitingPickupArrival, [5,6]` | `AwaitingPickupArrival, [5,6]` |
| 目标仓全部属于本车 REAR 组、升序，且恰好是该组编号最小的 2 个可用仓 | PASS | `[5,6]（REAR 组最小的可用仓，升序）` | `[5,6] on AGV-L2-001` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
