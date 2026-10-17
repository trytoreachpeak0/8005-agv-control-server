# L2 场景证据：slot-group-temporarily-full

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261017T182843607Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `6b71be5175eb90151ae01dd050658ee99563fe7e` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `13eca82ac199bd97c1ad49d036c70cb5b012250f` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `128daae52758749d4d6f346231fdb336ca5d1079773bcab5430c20c53ce6b10b` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261017T182843607Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 握手种子下本车 REAR 组没有可用仓、FRONT 组全部可用，且 FRONT 组足够装下 2 个花篮 | PASS | `available [1,2,3,4] = FRONT group; REAR group [5,6,7,8] all unavailable` | `available [1,2,3,4]; FRONT [1,2,3,4]; REAR [5,6,7,8]` |
| 积压原因为「所需分组暂时空仓不足」，再过三轮仍是 | PASS | `SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE` | `first SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE, after three rounds SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE` |
| 整车 FRONT 组空着但不受理：没有受理行、没有旅程、没有建 RIoT 单 | PASS | `AcceptedDemands 0, JourneyRuntimes 0, RIoT orders 0` | `AcceptedDemands 0, JourneyRuntimes 0, RIoT orders 0` |
| StructuralDispatchBlocks 没有这条需求的行（正常积压，不是结构性问题） | PASS | `0` | `0` |
| 换种子重连后，服务端按新会话的快照算出 REAR 组 [5,6] 可用 | PASS | `[1,2,3,4,5,6]` | `[1,2,3,4,5,6]` |
| 受理只预留 2 个花篮的数量，不定具体仓号（归属行与旅程行的目标仓位都是空的） | PASS | `baskets 2, membership [], journey []` | `baskets 2, membership [], journey []` |
| 扫码后分到的目标仓恰好是变空的 [5,6]：全部属于本车 REAR 组、升序，且是该组编号最小的 2 个可用仓 | PASS | `[5,6]（REAR 组最小的可用仓，升序）` | `[5,6] on AGV-L2-001` |
| 分到的目标仓恰好是变空的 [5,6] | PASS | `[5,6]` | `[5,6]` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
