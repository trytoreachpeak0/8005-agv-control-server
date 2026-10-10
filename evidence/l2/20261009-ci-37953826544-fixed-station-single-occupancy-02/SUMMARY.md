# L2 场景证据：fixed-station-single-occupancy

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T163541959Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-8` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T163541959Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 两条 WIRE_TO_GATE 需求落在两台不同的车上，受理时关卡都不是它们的下一站、没有任何独占行 | PASS | `two vehicles, no row at 210` | `A=BROKERX-L2-0001 B=BROKERX-L2-0002, row at 210:` |
| A 装完后关卡成了它的下一站、被补预占给 A；到关卡后转占用，卸完车还停在那里仍是占用 | PASS | `BROKERX-L2-0001 OCCUPIED at 210` | `BROKERX-L2-0001 OCCUPIED` |
| B 装完时关卡被 A 占着：B 照常出发（AwaitingGateArrival、没有阻断码），关卡仍是 A 的 | PASS | `B AwaitingGateArrival, gate held by BROKERX-L2-0001` | `B AwaitingGateArrival block=, gate held by BROKERX-L2-0001` |
| A 离开关卡、凭离点证据释放之后，下一轮补预占把关卡给 B（RESERVED），B 的预占不早于 A 的释放 | PASS | `B RESERVED at 210 at or after A's DEPARTED_STATION release` | `held: BROKERX-L2-0002 RESERVED; A released 2026-10-09 16:37:23.3074994+00:00 (DEPARTED_STATION); B reserved 2026-10-09 16:37:23.3286618+00:00` |
| 第一条 STAGING_TO_WIRE 受理即预占派工待送站，持有者是接它的那台车那一趟 | PASS | `BROKERX-L2-0001 RESERVED FIXED_TASK_STATION` | `BROKERX-L2-0001 RESERVED FIXED_TASK_STATION` |
| H 占着派工待送站的整段时间里（在途预占、到站占用、下达离站订单之后），同站的第二条需求一直没被接走 | PASS | `not accepted; 305 held by BROKERX-L2-0001 OCCUPIED after the departure order` | `accepted while reserved: ; while occupied: ; held BROKERX-L2-0001 OCCUPIED` |
| H 到了机台、凭离点证据释放派工待送站之后，第二条需求才被接走（受理时刻不早于释放时刻） | PASS | `accepted at or after H's DEPARTED_STATION release` | `accepted 2026-10-09 16:38:07.1069262+00:00; H released 2026-10-09 16:38:06.2921429+00:00 (DEPARTED_STATION)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
