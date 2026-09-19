# L2 场景证据：g3-journey-demand-to-pickup

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T133641515Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `85381ea2a37e46b4c720ff5f1843161ad6deb69d` |
| onboardHmiCommit | `4d716340982de4e39339c2151c291efe1a21e1d1` |
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
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260919T133641515Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 需求只受理一次：AcceptedDemands 里这条需求恰好一行（EXACTLY_ONE_ACCEPTED_DEMAND_SNAPSHOT） | PASS | `1` | `1` |
| 取货意图只建一条且已确认（EXACTLY_ONE_TO_PICKUP_INTENT） | PASS | `1 / CONFIRMED` | `1 / CONFIRMED` |
| RIoT 侧只有一张单，就是这条取货意图的那张（EXACTLY_ONE_RIOT_ORDER） | PASS | `1 / W2G-6a24b560-f17a-4e08-8e62-8f9e4bb4b850-PICKUP-1` | `1 / W2G-6a24b560-f17a-4e08-8e62-8f9e4bb4b850-PICKUP-1` |
| 到站前恰好发出一份计划：revision 为旅程的计划起点，取货段 ACTIVE、送货段 PLANNED，已被真车载端确认；工作清单一份都还没发（向量第 3～4 步） | PASS | `计划 1 份 rev 1 TO_PICKUP:ACTIVE,TO_DROPOFF:PLANNED 已确认 / 清单 0 份` | `计划 1 份 rev 1 TO_PICKUP:ACTIVE,TO_DROPOFF:PLANNED 确认=True / 清单 0 份` |
| 到站前没有任何仓位操作，也没有发录入请求或仓位命令（forbidden: slot-operation-before-pickup-arrival） | PASS | `0 / 0` | `0 / 0` |
| 服务端采纳了可信到站：旅程进入 AwaitingSublot（TRUSTED_PICKUP_ARRIVAL） | PASS | `AwaitingSublot` | `AwaitingSublot` |
| 消息顺序与向量一致：到站前计划、到站后工作清单、下一 revision 的到站计划，三份都被真车载端确认、没有一份被作废（向量第 3～9 步） | PASS | `plan r1 TO_PICKUP:ACTIVE,TO_DROPOFF:PLANNED \| worklist \| plan r2 TO_PICKUP:ARRIVED,TO_DROPOFF:PLANNED，全部 ack=True fenced=False` | `plan r1 TO_PICKUP:ACTIVE,TO_DROPOFF:PLANNED ack=True fenced=False \| worklist r1 ack=True fenced=False \| plan r2 TO_PICKUP:ARRIVED,TO_DROPOFF:PLANNED ack=True fenced=False` |
| 车载端日志库里采纳的计划与工作清单，消息号与 revision 就是服务端提交的那两份（DISPLAY_COMMITTED_DEMAND_JOURNEY / DISPLAY_CURRENT_STOP） | PASS | `plan eefad324-3c4d-4853-b9cf-bc2dac83790f r2 / worklist 086c7cc1-670f-6152-80a5-94d7a918058a r1` | `plan eefad324-3c4d-4853-b9cf-bc2dac83790f r2 / worklist 086c7cc1-670f-6152-80a5-94d7a918058a r1` |
| 终态 ONE_ACCEPTED_DEMAND_ONE_TO_PICKUP_ORDER_AT_PICKUP / NO_SLOT_OPERATION_STARTED：全程一条需求、一条意图、一张 RIoT 单，没有仓位操作（forbidden: duplicate-demand-acceptance、duplicate-riot-order） | PASS | `1 单 / 1 意图 / 1 需求 / 0 操作` | `1 单 / 1 意图 / 1 需求 / 0 操作` |
| 车载端没有发过任何涉及需求发现、选择或绑定的消息（NEVER_DISCOVER_SELECT_OR_BIND_DEMAND） | PASS | `(none)` | `(none)；车载端发来的类型：CapabilitySnapshot, Heartbeat, OnboardAlarmSnapshot, RecoveryStateReport, SafetyStateChanged, SafetyStateSnapshot, SessionHello, SnapshotAppliedAck` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
