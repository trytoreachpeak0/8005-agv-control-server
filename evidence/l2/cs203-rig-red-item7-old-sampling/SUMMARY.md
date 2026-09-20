# L2 场景证据：g3-pickup-load-and-correction

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260920T184203976Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `3078716cfce9bfdee0c88cfed16a34678973b6c4` |
| onboardHmiCommit | `08569c4f2f83a8f13b734d161055351c2621537a` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260920T184203976Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| sublot 绑定到操作会话：UIA 录入的 sublot 经真车载端只提交一次、回的是服务端请求录入的那个 operationSessionId，服务端为这条需求只建一笔装载，记的就是这个 sublot（SUBLOT_BOUND_TO_OPERATION_SESSION / BIND_SUBLOT_TO_OPERATION_SESSION / SUBMIT_SCANNED_SUBLOT） | PASS | `提交 1 次 / 会话一致 / 操作 1 笔 / G3-02-20260920T184203976Z` | `提交 1 次 / 会话一致=True / 操作 1 笔 / G3-02-20260920T184203976Z` |
| 仓位集合只授权一次：这条需求恰好一条 SlotOperationCommand，指向这笔装载，仓位就是服务端选定的 2 个（SLOT_SET_AUTHORIZED_ONCE / AUTHORIZE_SLOT_SET_ONCE） | PASS | `1 条 / 2 仓 1,2` | `1 条 / 2 仓 1,2` |
| 只开授权的仓、每仓只开一次：装载期间车载端报的开锁逐条只含一个仓，合起来恰好是授权集合、无重复；其余仓门始终关着、空着（LOAD_ONLY_AUTHORIZED_SLOTS；forbidden: duplicate-slot-unlock、expanded-active-unlock-set） | PASS | `开锁 1,2 各一次 / 其余仓未动` | `开锁 1,2 / 其余仓未动=True` |
| 装载走的是真 Modbus 闭环：车载端在等操作员时那个仓门确实开着；装完每个授权仓都关着、有货、锁反馈 1、开锁输出复位 | PASS | `等待时 OPEN / 装完 1=CLOSED/OCCUPIED/1/0 2=CLOSED/OCCUPIED/1/0` | `等待时 1=OPEN/EMPTY/0/0 2=OPEN/EMPTY/0/0 / 装完 1=CLOSED/OCCUPIED/1/0 2=CLOSED/OCCUPIED/1/0` |
| 消息顺序与向量一致：SublotEntryRequested → SublotSubmitted → SlotOperationCommand → OperationResult → DurableAck，各一次，服务端发的两条都被车载端确认（CV-PICKUP-SUBLOT-LOAD orderedExpectedMessages） | FAIL | `SublotEntryRequested(ack) < SublotSubmitted < SlotOperationCommand(ack) < OperationResult → DurableAck，各 1` | `SublotEntryRequested×1(ack=True) / SublotSubmitted×1 / SlotOperationCommand×1(ack=False) / OperationResult×1→DurableAck / 有序=True` |
| 装载结果只提交一次：操作 Committed，结果一份且 COMPLETED，需求仍在执行，旅程没有停摆（RECONCILE_LOAD_OUTCOME；forbidden: duplicate-business-commit、unknown-as-success） | PASS | `Committed / 1 份 COMPLETED / Accepted / 未停摆` | `Committed / 1 份 COMPLETED / Accepted / AwaitingSublot` |
| 装载完成后，车载端界面上出现可用的「修正装货」入口 | PASS | `True` | `True` |
| 未到达：服务端拒绝了对已提交装载的修正 | FAIL | `(reached)` | `(not reached) 服务端拒绝了对已提交装载的修正` |
| 未到达：服务端拒绝了对已提交装载的修正 | FAIL | `(reached)` | `(not reached) 服务端拒绝了对已提交装载的修正` |
| 未到达：服务端拒绝了对已提交装载的修正 | FAIL | `(reached)` | `(not reached) 服务端拒绝了对已提交装载的修正` |
| 未到达：服务端拒绝了对已提交装载的修正 | FAIL | `(reached)` | `(not reached) 服务端拒绝了对已提交装载的修正` |
| 未到达：服务端拒绝了对已提交装载的修正 | FAIL | `(reached)` | `(not reached) 服务端拒绝了对已提交装载的修正` |
| 未到达：服务端拒绝了对已提交装载的修正 | FAIL | `(reached)` | `(not reached) 服务端拒绝了对已提交装载的修正` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
