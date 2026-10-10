# L2 场景证据：fixed-station-single-occupancy

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261002T155409775Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-8` |
| controlServerCommit | `6b7b3e38b7d72b454b227a104de2e37a3198db86` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37023804697-1\_stage\l2-20261002T155409775Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 两条 WIRE_TO_GATE 需求落在两台不同的车上，受理时关卡都不是它们的下一站、没有任何独占行 | PASS | `two vehicles, no row at 210` | `A=BROKERX-L2-0001 B=BROKERX-L2-0002, row at 210:` |
| A 装完后关卡成了它的下一站、被补预占给 A；到关卡后转占用，卸完车还停在那里仍是占用 | PASS | `BROKERX-L2-0001 OCCUPIED at 210` | `BROKERX-L2-0001 OCCUPIED` |
| B 装完时关卡被 A 占着：B 照常出发（AwaitingGateArrival、没有阻断码），关卡仍是 A 的 | PASS | `B AwaitingGateArrival, gate held by BROKERX-L2-0001` | `B AwaitingGateArrival block=, gate held by BROKERX-L2-0001` |
| A 离开关卡、凭离点证据释放之后，下一轮补预占把关卡给 B（RESERVED），B 的预占不早于 A 的释放 | PASS | `B RESERVED at 210 at or after A's DEPARTED_STATION release` | `held: BROKERX-L2-0002 RESERVED; A released 2026-10-02 15:55:49.6295465+00:00 (DEPARTED_STATION); B reserved 2026-10-02 15:55:49.669033+00:00` |
| 第一条 STAGING_TO_WIRE 受理即预占派工待送站，持有者是接它的那台车那一趟 | PASS | `BROKERX-L2-0001 RESERVED FIXED_TASK_STATION` | `BROKERX-L2-0001 RESERVED FIXED_TASK_STATION` |
| H 占着派工待送站的整段时间里（在途预占、到站占用、下达离站订单之后），同站的第二条需求一直没被接走 | PASS | `not accepted; 305 held by BROKERX-L2-0001 OCCUPIED after the departure order` | `accepted while reserved: ; while occupied: ; held BROKERX-L2-0001 OCCUPIED` |
| H 到了机台、凭离点证据释放派工待送站之后，第二条需求才被接走（受理时刻不早于释放时刻） | PASS | `accepted at or after H's DEPARTED_STATION release` | `accepted 2026-10-02 15:56:35.3913203+00:00; H released 2026-10-02 15:56:34.6222463+00:00 (DEPARTED_STATION)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
