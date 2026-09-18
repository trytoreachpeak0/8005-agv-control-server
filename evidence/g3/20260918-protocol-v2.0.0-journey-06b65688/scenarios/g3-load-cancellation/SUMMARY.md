# L2 场景证据：g3-load-cancellation

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260918T155334406Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `e0f26b3725329c7a05c252c66a444ae9075d9747` |
| onboardHmiCommit | `9748c4187e74aeb46cf95557e8f2430e8fe2abf3` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260918T155334406Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 装载进行中（命令已下、结果未出），车载端界面上出现可用的「取消装货」入口 | PASS | `True` | `True` |
| 取消经服务端显式授权：应答是 LoadCancellationAuthorization AUTHORIZED，指向这条需求、这笔装载，范围就是全部授权仓位（AUTHORIZE_CANCELLATION_EXPLICITLY） | PASS | `LoadCancellationAuthorization AUTHORIZED 1,2` | `LoadCancellationAuthorization AUTHORIZED 1,2` |
| 消息顺序与向量一致：LoadCancellationStartRequested → LoadCancellationAuthorization → LoadCancellationResult → DurableAck，各一次；车载端没有在授权之前单方面报取消结果（CV-LOAD-CANCELLATION-ALL-EMPTY orderedExpectedMessages / NEVER_CANCEL_UNILATERALLY） | PASS | `StartRequested → Authorization < Result → DurableAck，各 1` | `StartRequested×1→LoadCancellationAuthorization / Result×1→DurableAck / 有序=True` |
| 证空不靠开门：取消请求之后车载端没有再开任何仓；整个 attempt 只开过装载时的第 1 仓一次，没开过的仓始终没开（forbidden: duplicate-slot-unlock、expanded-active-unlock-set） | PASS | `取消后开锁 0 次 / 全程开锁 1` | `取消后开锁 0 次 / 全程开锁 1` |
| 车载端证明全部授权仓位为空：ALL_EMPTY，每个仓都是空、锁上、开锁输出复位；模拟器上的物理状态与之一致（PROVE_ALL_SLOTS_EMPTY） | PASS | `ALL_EMPTY 1=COMPLETED/EMPTY/LOCKED/RESET 2=COMPLETED/EMPTY/LOCKED/RESET / 1=CLOSED/EMPTY/1/0 2=CLOSED/EMPTY/1/0` | `ALL_EMPTY 1=COMPLETED/EMPTY/LOCKED/RESET 2=COMPLETED/EMPTY/LOCKED/RESET / 1=CLOSED/EMPTY/1/0 2=CLOSED/EMPTY/1/0` |
| 取消收敛：工作流 Reconciled，需求 Cancelled，装载 Cancelled，车辆租约释放，旅程以 CANCELLED_BY_OPERATOR 收尾、没有去关卡（RECONCILE_EMPTY_FINAL_STATE） | PASS | `工作流 Reconciled / 需求 Cancelled / 装载 Cancelled / 租约释放=True / 旅程 Completed/CANCELLED_BY_OPERATOR / TO_GATE 0` | `工作流 Reconciled / 需求 Cancelled / 装载 Cancelled / 租约释放=True / 旅程 Completed/CANCELLED_BY_OPERATOR / TO_GATE 0` |
| 终态经得起原装载的迟到结果：取消的收敛一项都没被改回去，没有任何操作落到 RecoveryRequired、没有需求完成记录，RIoT 上只有那一张取货单，仓位物理上全空、锁上（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE；forbidden: duplicate-business-commit、unknown-as-success） | PASS | `工作流 Reconciled / 需求 Cancelled / 装载 Cancelled / 租约释放=True / 旅程 Completed/CANCELLED_BY_OPERATOR / TO_GATE 0 / RecoveryRequired 0 / 完成记录 0 / RIoT 单 1 / 1=CLOSED/EMPTY/1/0 2=CLOSED/EMPTY/1/0` | `工作流 Reconciled / 需求 Cancelled / 装载 Cancelled / 租约释放=True / 旅程 Completed/CANCELLED_BY_OPERATOR / TO_GATE 0 / RecoveryRequired 0 / 完成记录 0 / RIoT 单 1 / 1=CLOSED/EMPTY/1/0 2=CLOSED/EMPTY/1/0 / 90s 内没有迟到结果` |
| 取消结算之后车辆放出来了：这条需求的 TO_PICKUP 单车辆占用已释放，同一台车在 60 秒内接了下一单（旅程到 AwaitingPickupArrival，不是 Blocked/VEHICLE_OCCUPANCY_CONFLICT；control-server#131） | PASS | `TO_PICKUP 占用已释放 / 下一单 AwaitingPickupArrival on AGV-L2-001` | `VehicleOccupancyReleasedAt='2026-09-18 15:54:07.2643008+00:00' / 下一单 AwaitingPickupArrival/ on AGV-L2-001` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
