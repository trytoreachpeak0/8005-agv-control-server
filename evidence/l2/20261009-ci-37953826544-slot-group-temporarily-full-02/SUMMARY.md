# L2 场景证据：slot-group-temporarily-full

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T165754924Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-4` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `3.0.0` |
| protocolReleaseIdentity.tag | `protocol-v3.0.0` |
| protocolReleaseIdentity.commit | `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c` |
| protocolReleaseIdentity.protocolVersion | `4` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e` |
| protocolReleaseIdentity.schemaBundleSha256 | `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43` |
| protocolReleaseIdentity.vectorsSha256 | `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T165754924Z-slot1` |
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
