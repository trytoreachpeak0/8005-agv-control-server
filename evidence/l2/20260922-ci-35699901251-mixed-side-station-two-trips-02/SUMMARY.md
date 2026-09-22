# L2 场景证据：mixed-side-station-two-trips

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T073238780Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-4` |
| controlServerCommit | `f2ddd40522432925580d6048dca2d6d54171a7a8` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T073238780Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：取货点 12 号站名为 N1-3_N2-5，归属表 N1-3 → FRONT、N2-5 → REAR（同站两侧） | PASS | `12=N1-3_N2-5 / N1-3/FRONT,N2-5/REAR` | `12=N1-3_N2-5 / N1-3/FRONT,N2-5/REAR` |
| 需求甲（N1-3，2 花篮）：目标仓全部属于 FRONT 组、升序，且恰好是该组编号最小的 2 个可用仓 | PASS | `[1,2]（FRONT 组最小的可用仓，升序）` | `[1,2] on AGV-L2-001` |
| 需求甲 走完一趟，没有被阻断：取货停 12 号站，装货与卸货命令开的都是目标仓（FRONT 组） | PASS | `Completed / no block / station 12 / LOAD [1,2]; UNLOAD [1,2]` | `Completed / block '' / station 12 / LOAD [1,2]; UNLOAD [1,2]` |
| 需求乙（N2-5，2 花篮）：目标仓全部属于 REAR 组、升序，且恰好是该组编号最小的 2 个可用仓 | PASS | `[5,6]（REAR 组最小的可用仓，升序）` | `[5,6] on AGV-L2-001` |
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
