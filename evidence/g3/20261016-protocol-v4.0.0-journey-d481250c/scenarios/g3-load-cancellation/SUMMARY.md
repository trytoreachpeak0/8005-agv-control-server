# L2 场景证据：g3-load-cancellation

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261016T080234546Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `d481250c08d65b9b03ba854f22f563fb33bfa5aa` |
| onboardHmiCommit | `c9d24dfd6e96dc43781735cac209dc3976fe3a83` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `4.0.0` |
| protocolReleaseIdentity.tag | `protocol-v4.0.0` |
| protocolReleaseIdentity.commit | `3fb214e57d861a909d872511940d26133eb32dca` |
| protocolReleaseIdentity.protocolVersion | `5` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `beca46a7fe98003ce1f21efb88f2c1406f3040883d0b372a51505a843e306f3a` |
| protocolReleaseIdentity.schemaBundleSha256 | `dad44a5e93203eb0b7ade50b23d12f2aaffc0e2abb3c8e5dcdd3de62d931d628` |
| protocolReleaseIdentity.vectorsSha256 | `24cc3a5e5023bc7ead70f609456136208a40c04c1ecd0982a7182880d8eaa0c7` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261016T080234546Z` |
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
| 取消结算之后车辆放出来了：这条需求所在旅程的用途占有记录已释放，同一台车在 60 秒内接了下一单（旅程到 AwaitingPickupArrival、没有停摆原因码；control-server#131，#387 起读用途占有） | PASS | `用途占有记录已释放 / 下一单 AwaitingPickupArrival on AGV-L2-001，TO_PICKUP 意图 CONFIRMED，没有停摆原因码` | `ClaimRecord.ReleasedAt='2026-10-16 08:03:11.2236056+00:00' / 下一单 AwaitingPickupArrival/ TO_PICKUP=CONFIRMED on AGV-L2-001` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
