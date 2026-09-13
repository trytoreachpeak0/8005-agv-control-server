# L2 场景证据：g3-journey-demand-to-pickup

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260913T114223149Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `fd29c51405c4396cae0ed8ebedf2152c321e6527` |
| onboardHmiCommit | `c86bac5eaec57c36351f7d45b458deaa42fde22e` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `1.0.0` |
| protocolReleaseIdentity.tag | `protocol-v1.0.0` |
| protocolReleaseIdentity.commit | `9f22db825d52ad86c1d803bd0c1925dcc58d6793` |
| protocolReleaseIdentity.protocolVersion | `2` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `a0e1deedb50419057dbe6aa7a7e8df983fb9ea901bbc452f97020ebf4743ef23` |
| protocolReleaseIdentity.schemaBundleSha256 | `885191e7a9e5da98a44f17f131756f9eb2033e7e11f13f4df965d4e35ac55685` |
| protocolReleaseIdentity.vectorsSha256 | `51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260913T114223149Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 需求只受理一次：AcceptedDemands 里这条需求恰好一行（EXACTLY_ONE_ACCEPTED_DEMAND_SNAPSHOT） | PASS | `1` | `1` |
| 取货意图只建一条且已确认（EXACTLY_ONE_TO_PICKUP_INTENT） | PASS | `1 / CONFIRMED` | `1 / CONFIRMED` |
| RIoT 侧只有一张单，就是这条取货意图的那张（EXACTLY_ONE_RIOT_ORDER） | PASS | `1 / W2G-f83e1b00-6507-4b74-9821-fb3952377325-PICKUP-1` | `1 / W2G-f83e1b00-6507-4b74-9821-fb3952377325-PICKUP-1` |
| 到站前恰好发出一份计划：revision 为旅程的计划起点，取货段 ACTIVE、送货段 PLANNED，已被真车载端确认；工作清单一份都还没发（向量第 3～4 步） | PASS | `计划 1 份 rev 1 TO_PICKUP:ACTIVE,TO_DROPOFF:PLANNED 已确认 / 清单 0 份` | `计划 1 份 rev 1 TO_PICKUP:ACTIVE,TO_DROPOFF:PLANNED 确认=True / 清单 0 份` |
| 到站前没有任何仓位操作，也没有发录入请求或仓位命令（forbidden: slot-operation-before-pickup-arrival） | PASS | `0 / 0` | `0 / 0` |
| 服务端采纳了可信到站：旅程进入 AwaitingSublot（TRUSTED_PICKUP_ARRIVAL） | PASS | `AwaitingSublot` | `AwaitingSublot` |
| 消息顺序与向量一致：到站前计划、到站后工作清单、下一 revision 的到站计划，三份都被真车载端确认、没有一份被作废（向量第 3～9 步） | PASS | `plan r1 TO_PICKUP:ACTIVE,TO_DROPOFF:PLANNED \| worklist \| plan r2 TO_PICKUP:ARRIVED,TO_DROPOFF:PLANNED，全部 ack=True fenced=False` | `plan r1 TO_PICKUP:ACTIVE,TO_DROPOFF:PLANNED ack=True fenced=False \| worklist r1 ack=True fenced=False \| plan r2 TO_PICKUP:ARRIVED,TO_DROPOFF:PLANNED ack=True fenced=False` |
| 车载端日志库里采纳的计划与工作清单，消息号与 revision 就是服务端提交的那两份（DISPLAY_COMMITTED_DEMAND_JOURNEY / DISPLAY_CURRENT_STOP） | PASS | `plan 2d632b88-83b4-645f-a808-4a10a87e32b9 r2 / worklist f71f0b54-a6dc-095b-9751-9487cfaa5e6b r1` | `plan 2d632b88-83b4-645f-a808-4a10a87e32b9 r2 / worklist f71f0b54-a6dc-095b-9751-9487cfaa5e6b r1` |
| 终态 ONE_ACCEPTED_DEMAND_ONE_TO_PICKUP_ORDER_AT_PICKUP / NO_SLOT_OPERATION_STARTED：全程一条需求、一条意图、一张 RIoT 单，没有仓位操作（forbidden: duplicate-demand-acceptance、duplicate-riot-order） | PASS | `1 单 / 1 意图 / 1 需求 / 0 操作` | `1 单 / 1 意图 / 1 需求 / 0 操作` |
| 车载端没有发过任何涉及需求发现、选择或绑定的消息（NEVER_DISCOVER_SELECT_OR_BIND_DEMAND） | PASS | `(none)` | `(none)；车载端发来的类型：CapabilitySnapshot OnboardAlarmSnapshot RecoveryStateReport SafetyStateChanged SafetyStateSnapshot SessionHello SnapshotAppliedAck` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
