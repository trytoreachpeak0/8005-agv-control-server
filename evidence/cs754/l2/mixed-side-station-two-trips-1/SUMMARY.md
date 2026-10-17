# L2 场景证据：mixed-side-station-two-trips

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261017T183210031Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261017T183210031Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：取货点 12 号站名为 N1-3_N2-5，归属表 N1-3 → FRONT、N2-5 → REAR（同站两侧） | PASS | `12=N1-3_N2-5 / N1-3/FRONT,N2-5/REAR` | `12=N1-3_N2-5 / N1-3/FRONT,N2-5/REAR` |
| 需求甲（N1-3，2 花篮）：扫码后分到的目标仓全部属于 FRONT 组、升序，且恰好是该组编号最小的 2 个可用仓 | PASS | `[1,2]（FRONT 组最小的可用仓，升序）` | `[1,2] on AGV-L2-001` |
| 需求甲 走完一趟，没有被阻断：取货停 12 号站，装货与卸货命令开的都是目标仓（FRONT 组） | PASS | `Completed / no block / station 12 / LOAD [1,2]; UNLOAD [1,2]` | `Completed / block '' / station 12 / LOAD [1,2]; UNLOAD [1,2]` |
| 需求乙（N2-5，2 花篮）：扫码后分到的目标仓全部属于 REAR 组、升序，且恰好是该组编号最小的 2 个可用仓 | PASS | `[5,6]（REAR 组最小的可用仓，升序）` | `[5,6] on AGV-L2-001` |
| 需求乙 走完一趟，没有被阻断：取货停 12 号站，装货与卸货命令开的都是目标仓（REAR 组） | PASS | `Completed / no block / station 12 / LOAD [5,6]; UNLOAD [5,6]` | `Completed / block '' / station 12 / LOAD [5,6]; UNLOAD [5,6]` |
| 同一台车两趟停的是同一个站点，而开的是不相交的两组仓 | PASS | `same vehicle / same station 12 / disjoint slots` | `A AGV-L2-001 N1-3_N2-5 (12) [1,2], B AGV-L2-001 N1-3_N2-5 (12) [5,6]` |
| StructuralDispatchBlocks 无行（含已清除的）：混挂站点不形成结构性派车阻断 | PASS | `0 rows` | `0 rows` |
| 积压里没有与站点一致性相关的原因码：各时点见过的原因码里没有带站点、开门侧、分组、混挂或一致性字样的 | PASS | `none` | `none (observed: ACCEPTED,DEMAND_ALREADY_ACCEPTED)` |
| 服务端日志里没有针对该站点（站名 N1-3_N2-5、站号、两个区域号）的 Warning 及以上记录 | PASS | `0 records` | `0 records` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
