# L2 场景证据：slot-assignment-after-scan

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261017T182445712Z` |
| agvId | `AGV-L2-001` |
| batchId | `wire-to-full-inspection` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261017T182445712Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 受理只预留 2 个花篮的数量，不定具体仓号（归属行与旅程行的目标仓位都是空的） | PASS | `baskets 2, membership [], journey []` | `baskets 2, membership [], journey []` |
| 乙在车停站时同站追加进同一趟旅程：另开一个停靠排在甲的停靠后面、同一个站，只预留 2 个花篮的数量、不定仓号 | PASS | `journey journey:d0dbc24e-62a2-4e5d-80e4-970a191f934e / new stop after sequence 1 at station 12 / baskets 2 / []` | `journey journey:d0dbc24e-62a2-4e5d-80e4-970a191f934e / stop journey:37ce76ee-5250-44bf-879a-03d7cb72358b\|PICKUP seq 2 at 12 / baskets 2 / []` |
| 甲在站点期限内没人扫码，被结掉（终结或放回），从没下装货命令，归属行上也没有仓号 | PASS | `TERMINATED or removed / no load / []` | `TERMINATED removed=  / no load / []` |
| 乙扫码后分到的仓全部属于前侧、升序，且恰好是扫码之前前侧编号最小的 2 个空仓（甲结掉后空回来的那两个也算） | PASS | `[1,2]（FRONT 组最小的可用仓，升序）` | `[1,2] on AGV-L2-001` |
| 乙的装货命令开的就是分到的仓，与装货记录一致：现场那种「1～4 都空却开 3 号」不再出现 | PASS | `LOAD [1,2] / StationOperations [1,2]` | `LOAD [1,2] / StationOperations [1,2]` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
