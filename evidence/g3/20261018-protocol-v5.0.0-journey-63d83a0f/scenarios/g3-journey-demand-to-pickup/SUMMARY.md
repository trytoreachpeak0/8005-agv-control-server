# L2 场景证据：g3-journey-demand-to-pickup

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261018T073032973Z` |
| agvId | `AGV-L2-001` |
| batchId | `v5-exit` |
| controlServerCommit | `63d83a0f623a99d0260c2b1c95672a91584336b5` |
| onboardHmiCommit | `a3a6753f6924b8f845483d6b886259c90af9b018` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `18d4fb3495033624cab412a7ee784f675dfe4cc2` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `dd973918cc9c4fdf28ee2ad2ec4ad522bdcc6f2b6ecaa4a4fa1c57a29f29f5aa` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261018T073032973Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 需求只受理一次：AcceptedDemands 里这条需求恰好一行（EXACTLY_ONE_ACCEPTED_DEMAND_SNAPSHOT） | PASS | `1` | `1` |
| 取货意图只建一条且已确认（EXACTLY_ONE_TO_PICKUP_INTENT） | PASS | `1 / CONFIRMED` | `1 / CONFIRMED` |
| RIoT 侧只有一张单，就是这条取货意图的那张（EXACTLY_ONE_RIOT_ORDER） | PASS | `1 / W2G-11a339b9-ef17-45a3-a7e3-e4562921b3be-PICKUP-1` | `1 / W2G-11a339b9-ef17-45a3-a7e3-e4562921b3be-PICKUP-1` |
| 到站前恰好发出一份计划：revision 为旅程的计划起点，取货段 ACTIVE、送货段 PLANNED，已被真车载端确认；工作清单一份都还没发（向量第 3～4 步） | PASS | `计划 1 份 rev 1 TO_PICKUP:ACTIVE,TO_DROPOFF:PLANNED 已确认 / 清单 0 份` | `计划 1 份 rev 1 TO_PICKUP:ACTIVE,TO_DROPOFF:PLANNED 确认=True / 清单 0 份` |
| 到站前没有任何仓位操作，也没有发录入请求或仓位命令（forbidden: slot-operation-before-pickup-arrival） | PASS | `0 / 0` | `0 / 0` |
| 服务端采纳了可信到站：旅程进入 AwaitingSublot（TRUSTED_PICKUP_ARRIVAL） | PASS | `AwaitingSublot` | `AwaitingSublot` |
| 消息顺序与向量一致：到站前计划、到站后工作清单、下一 revision 的到站计划，三份都被真车载端确认、没有一份被作废（向量第 3～9 步） | PASS | `plan r1 TO_PICKUP:ACTIVE,TO_DROPOFF:PLANNED \| worklist \| plan r2 TO_PICKUP:ARRIVED,TO_DROPOFF:PLANNED，全部 ack=True fenced=False` | `plan r1 TO_PICKUP:ACTIVE,TO_DROPOFF:PLANNED ack=True fenced=False \| worklist r1 ack=True fenced=False \| plan r2 TO_PICKUP:ARRIVED,TO_DROPOFF:PLANNED ack=True fenced=False` |
| 车载端日志库里采纳的计划与工作清单，消息号与 revision 就是服务端提交的那两份（DISPLAY_COMMITTED_DEMAND_JOURNEY / DISPLAY_CURRENT_STOP） | PASS | `plan e1064a9f-65c7-a25c-9d2b-edb5e5bc9e85 r2 / worklist 537f156d-590c-8356-bb25-ef1fa6a6fc73 r1` | `plan e1064a9f-65c7-a25c-9d2b-edb5e5bc9e85 r2 / worklist 537f156d-590c-8356-bb25-ef1fa6a6fc73 r1` |
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
