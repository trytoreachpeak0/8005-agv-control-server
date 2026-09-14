# L2 场景证据：g3-load-cancellation

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260914T041733853Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `052759bca58a04316dfda260249b3b5697f2cf8e` |
| onboardHmiCommit | `b96010825d43aeee3b861cb3b4716f4d0873c8a0` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260914T041733853Z` |
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
| 终态经得起原装载的迟到结果：取消的收敛一项都没被改回去，没有任何操作落到 RecoveryRequired、没有需求完成记录，RIoT 上只有那一张取货单，仓位物理上全空、锁上（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE；forbidden: duplicate-business-commit、unknown-as-success） | PASS | `工作流 Reconciled / 需求 Cancelled / 装载 Cancelled / 租约释放=True / 旅程 Completed/CANCELLED_BY_OPERATOR / TO_GATE 0 / RecoveryRequired 0 / 完成记录 0 / RIoT 单 1 / 1=CLOSED/EMPTY/1/0 2=CLOSED/EMPTY/1/0` | `工作流 Reconciled / 需求 Cancelled / 装载 Cancelled / 租约释放=True / 旅程 Completed/CANCELLED_BY_OPERATOR / TO_GATE 0 / RecoveryRequired 0 / 完成记录 0 / RIoT 单 1 / 1=CLOSED/EMPTY/1/0 2=CLOSED/EMPTY/1/0 / 迟到结果 UNKNOWN→DurableAck` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
