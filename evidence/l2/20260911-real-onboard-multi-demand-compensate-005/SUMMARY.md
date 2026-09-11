# L2 场景证据：real-onboard-multi-demand-compensate

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260911T112156601Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `077574d9777191701cd75727db5270e62bb4e91c` |
| onboardHmiCommit | `ab346ed4b15d86110429273014d8a9be034bdab1` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260911T112156601Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 四条需求凑成一趟旅程：四个取货停靠加一个关卡 | PASS | `5` | `5` |
| 停靠 1：驱动脚本照常装载提交 | PASS | `Committed` | `Committed` |
| 停靠 2：驱动脚本钉死锁反馈之后车报回真的 UNKNOWN，仓位操作 RecoveryRequired、旅程 Blocked | PASS | `RecoveryRequired / LOAD_RESULT_REQUIRES_RECOVERY / CLOSED/OCCUPIED/1/0` | `RecoveryRequired / LOAD_RESULT_REQUIRES_RECOVERY / CLOSED/OCCUPIED/1/0` |
| 补偿之前：停靠 1 的货在车上（Loaded），停靠 3、4 还没装（Planned）——旅程不止这一条需求 | PASS | `1=Loaded 2=Planned 3=Planned 4=Planned` | `1=Loaded/Accepted 2=Planned/RecoveryRequired 3=Planned/Accepted 4=Planned/Accepted` |
| 停靠 2：补偿清空对账 Reconciled、结论 ALL_EMPTY，车开的就是那一仓，需求判 Cancelled | PASS | `Reconciled / ALL_EMPTY / Cancelled / [2]` | `Reconciled / ALL_EMPTY / Cancelled / [2]` |
| 停靠 2：补偿对账之后旅程自己离开这一站、去停靠 3，不需要再按任何按钮 | PASS | `3/*（离开停靠 2）` | `3/AwaitingPickupArrival /` |
| 停靠 2 这条需求本身的结局不变：CANCELLED_BY_LOAD_COMPENSATION 永久抑制，悬空的 LoadBatch 命令已结算 | PASS | `CANCELLED_BY_LOAD_COMPENSATION / 已结算` | `CANCELLED_BY_LOAD_COMPENSATION / 已结算` |
| 补偿对账那一刻服务端会话回到 Ready 并告诉了车：LoadCompensationResult 的首个应答里带 SessionReadiness READY（#46） | PASS | `READY` | `READY / 现在 Ready / READY` |
| 停靠 3：补偿之后照常装载提交 | PASS | `Committed` | `Committed` |
| 停靠 4：补偿之后照常装载提交 | PASS | `Committed` | `Committed` |
| 三站装完、一站补偿之后旅程进入去关卡那一段，理由 NO_FURTHER_CARGO | PASS | `AwaitingGateArrival / NO_FURTHER_CARGO` | `AwaitingGateArrival / NO_FURTHER_CARGO` |
| 关卡：三条装上车的需求逐条取空，每条卸货都提交，旅程 Completed（车载端拒收多项关卡清单时红，见 8005-agv-program#48） | PASS | `Completed / 3 条卸货 Committed` | `Completed / Committed,Committed,Committed / 会话 Ready / READY` |
| 收尾：停靠 1、3、4 的需求卸下成功，停靠 2 的需求是补偿判死的那一条 | PASS | `1=Unloaded/Succeeded 2=Cancelled/Cancelled 3=Unloaded/Succeeded 4=Unloaded/Succeeded` | `1=Unloaded/Succeeded 2=Cancelled/Cancelled 3=Unloaded/Succeeded 4=Unloaded/Succeeded` |
| 补偿对账之后没有任何仓位操作再进 RecoveryRequired，也没有第二个恢复会话 | PASS | `RecoveryRequired 0 / 恢复会话 1` | `RecoveryRequired 0 / 恢复会话 1` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 130 lines, 130 distinct, 15 message types, 0 distinct violations, 0 known; schema compilation 22792 ms.` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照
- `schema-conformance/` —— 车载端报文逐条过 protocol JSON Schema 的覆盖账与违约明细

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
