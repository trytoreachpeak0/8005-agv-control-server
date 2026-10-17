# L2 场景证据：slot-group-selection

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261017T182734018Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261017T182734018Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 库内整车模型的分组只有 FRONT 与 REAR，本车的仓位模型可解析 | PASS | `SlotModelSlots: FRONT,REAR; vehicle groups: FRONT,REAR` | `SlotModelSlots: FRONT,REAR; vehicle groups: FRONT,REAR` |
| 受理只预留 3 个花篮的数量，不定具体仓号（归属行与旅程行的目标仓位都是空的） | PASS | `baskets 3, membership [], journey []` | `baskets 3, membership [], journey []` |
| N1-3 指 REAR：扫码后分到的目标仓全部属于本车 REAR 组、升序，且恰好是该组编号最小的 3 个可用仓 | PASS | `[5,6,7]（REAR 组最小的可用仓，升序）` | `[5,6,7] on AGV-L2-001` |
| 第一条需求的旅程走到 Completed | PASS | `Completed` | `Completed` |
| 取货点下发给车的 LOAD 命令 slots 与旅程目标仓相同 | PASS | `[5,6,7]` | `1 command(s), slots: [5,6,7]` |
| 关卡下发给车的 UNLOAD 命令 slots 与旅程目标仓相同 | PASS | `[5,6,7]` | `1 command(s), slots: [5,6,7]` |
| 导入把 N1-3 改指 FRONT 的新版本归属表 | PASS | `OK, version 2` | `OK, version 2` |
| 受理只预留 3 个花篮的数量，不定具体仓号（归属行与旅程行的目标仓位都是空的） | PASS | `baskets 3, membership [], journey []` | `baskets 3, membership [], journey []` |
| N1-3 改指 FRONT 之后：扫码后分到的目标仓全部属于本车 FRONT 组、升序，且恰好是该组编号最小的 3 个可用仓 | FAIL | `FRONT 组内升序的最小可用仓` | `[] on AGV-L2-001：there are no target slots` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
