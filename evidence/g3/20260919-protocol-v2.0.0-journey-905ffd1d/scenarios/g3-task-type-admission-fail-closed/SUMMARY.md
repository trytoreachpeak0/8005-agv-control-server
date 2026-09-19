# L2 场景证据：g3-task-type-admission-fail-closed

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T125357259Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `905ffd1dc0b4de1f048163342b45e58f3a7261fc` |
| onboardHmiCommit | `44b3aa6e255f0820c3988d3f50f0b4dba1105006` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260919T125357259Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 缺绑定的 STAGING_TO_WIRE 需求始终不受理：没有受理快照、没有旅程、没有订单意图、没有仓位操作，已绑定的那条走完之后再转四轮仍是如此（FAIL_CLOSED_ON_MISSING_BINDING） | PASS | `受理 0 / 旅程 0 / 意图 0 / 操作 0` | `受理 0 / 旅程 0 / 意图 0 / 操作 0` |
| 缺绑定的需求没有被排进任何计划或清单：服务端发件箱里没有一条消息提到它，RIoT 上没有不属于已绑定需求的单 | PASS | `发件箱 0 条 / 外来单 0 张` | `发件箱 0 条 / 外来单 0 张` |
| 不投运原因记在服务端：JourneyBacklog 里这条需求的原因是 TASK_TYPE_BINDING_MISSING（缺绑定，不是范围外或尚未可执行），且没有受理时间 | PASS | `TASK_TYPE_BINDING_MISSING / 未受理` | `TASK_TYPE_BINDING_MISSING / 受理时间=` |
| 准入原因没有下发给车：全部 VehicleBusinessStateSnapshot 的 blockingFacts 里都没有缺绑定的原因码、需求号或任务类型（规格第 5.3 节） | PASS | `快照 ≥1 份 / 含准入原因 0 份` | `快照 2 份 / 含准入原因 0 份` |
| 同一轮里已绑定的 WIRE_TO_GATE 需求照常受理，并在真车载端上装货、卸货、走完（ADMIT_ONLY_BOUND_TASK_TYPES；缺绑定不连带其它任务类型） | PASS | `Succeeded / Completed / 2 笔操作 Committed` | `Succeeded / Completed / 2 笔操作 Committed` |
| 消息顺序与向量一致：已绑定需求的计划快照被真车载端确认，其后有业务状态快照被确认；这一趟的计划与清单没有一份被作废 | PASS | `计划 ack / 其后业务快照 ack ≥1 / 作废或未确认 0` | `计划 ack=True / 其后业务快照 ack 2 / 作废或未确认 0（plan r1 TO_PICKUP@N1-3_N1-7:ACTIVE,TO_DROPOFF@关卡:PLANNED \| worklist r1 @N1-3_N1-7 PICKUP/WIRE_TO_GATE \| plan r2 TO_PICKUP@N1-3_N1-7:ARRIVED,TO_DROPOFF@关卡:PLANNED \| worklist r2 @关卡 DROPOFF/WIRE_TO_GATE \| plan r3 TO_PICKUP@N1-3_N1-7:COMPLETED,TO_DROPOFF@关卡:ARRIVED）` |
| 只有计划、还没有清单项时，车载端不显示任何任务类型：StopDirection 已按计划腿显示方向，TaskType 为空（NEVER_INFER_UNBOUND_TASK_TYPE） | PASS | `StopDirection 非空 / TaskType (empty)` | `StopDirection=「取货」 TaskType=(empty)` |
| 车载端全程只显示被绑定的那一种任务类型：取货点与关卡两站的 TaskType 都非空，四次读数里出现过的文案只有一种，是 WIRE_TO_GATE 的（含「关卡」），不是「未知」（NEVER_INFER_UNBOUND_TASK_TYPE；不比文案全文） | PASS | `两站都显示 / 只一种文案，含「关卡」` | `en-route-to-origin: StopDirection=「取货」 TaskType=(empty)；at-origin: StopDirection=「取货」 TaskType=「焊线→质检关卡」；at-destination: StopDirection=「卸货」 TaskType=「焊线→质检关卡」；completed: StopDirection=「卸货」 TaskType=「焊线→质检关卡」` |
| 终态 NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE：全程只受理一条需求、两张 RIoT 单、两笔仓位操作，卸完的仓关门、空、锁上、开锁输出复位 | PASS | `1 需求 / 2 单 / 2 操作 / CLOSED/EMPTY/1/0` | `1 需求 / 2 单 / 2 操作 / 仓 1 CLOSED/EMPTY/1/0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
