# L2 场景证据：real-onboard-recovery-compensate-load

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260911T073559948Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `9f28ed1856a4dd031c491c2400ff0265e0195c1b` |
| onboardHmiCommit | `bb58b217e95c611de597212d763f7579fdcb0e03` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260911T073559948Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 起点到位：车报回一份 UNKNOWN，服务端判 RecoveryRequired 并停摆 | PASS | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired / UNKNOWN` | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired / UNKNOWN` |
| 现场读数变成真的：门关、仓里有货、锁反馈有效、开锁输出已复位——这正是补偿向量的入场券 | PASS | `CLOSED/OCCUPIED/1/0` | `CLOSED/OCCUPIED/1/0` |
| HMI 上出现可用的「补偿清空」入口（它绑 CanRequestLoadCompensation，与「申请恢复」是两个按钮） | PASS | `True` | `True` |
| 第一步握手：恢复会话已开，作用域是这一单这一次 attempt，管理员角色已认证 | PASS | `589a6c4b-b759-4a46-9f3c-d2289601f2e1 / MAINTENANCE_ADMINISTRATOR / 含仓位 1` | `589a6c4b-b759-4a46-9f3c-d2289601f2e1 / MAINTENANCE_ADMINISTRATOR / [1]` |
| 第二、三步握手：车载端报的动作是 COMPENSATE_LOAD_ALL_EMPTY，服务端授权并选定它 | PASS | `COMPENSATE_LOAD_ALL_EMPTY / 589a6c4b-b759-4a46-9f3c-d2289601f2e1 / cc8acd4b-1e2f-005c-80a6-9026ae7c1398` | `COMPENSATE_LOAD_ALL_EMPTY / 589a6c4b-b759-4a46-9f3c-d2289601f2e1 / cc8acd4b-1e2f-005c-80a6-9026ae7c1398` |
| 服务端在收到 LoadCompensationRequested 之后才下发补偿命令，并把它绑在工作流上 | PASS | `LoadCompensationCommand / 收到 1 条 LoadCompensationRequested` | `LoadCompensationCommand / 收到 1 条` |
| 第四步握手：车载端在真 Modbus 上重新开锁，门再弹开一次 | PASS | `UNLOCKING > 2 / 门 OPEN` | `UNLOCKING = 3 / 门 OPEN` |
| 车载端报回 ALL_EMPTY，那一仓 COMPLETED/EMPTY/LOCKED/RESET——服务端的对账判据要的正是这四样 | PASS | `ALL_EMPTY / COMPLETED / EMPTY / LOCKED / RESET` | `ALL_EMPTY / COMPLETED / EMPTY / LOCKED / RESET` |
| 第五步握手走完：服务端对账通过，工作流 Reconciled 而不是又一次 RecoveryRequired | PASS | `Reconciled` | `Reconciled` |
| 现场收在安全状态：门关、仓空、已锁、开锁输出复位 | PASS | `CLOSED/EMPTY/1/0` | `CLOSED/EMPTY/1/0` |
| 补偿终结这一单：需求与仓位操作都判 Cancelled | PASS | `Cancelled / Cancelled` | `Cancelled / Cancelled` |
| 旅程以 CANCELLED_BY_LOAD_COMPENSATION 收尾，同一个理由把业务键永久抑制 | PASS | `Completed / CANCELLED_BY_LOAD_COMPENSATION / 抑制 1 条` | `Completed / CANCELLED_BY_LOAD_COMPENSATION / 抑制 1 条 (CANCELLED_BY_LOAD_COMPENSATION)` |
| 补偿不产生替换结果：OperationResults 仍只有那一份 UNKNOWN，且没有被标成被替换 | PASS | `1 份 / UNKNOWN / 未被替换` | `1 份 / UNKNOWN / 未被替换` |
| 那条永远等不到 LoadResult 的 LoadBatch 命令，被补偿这一路同样结算掉了 | PASS | `补偿前挂着 / 补偿后已结算` | `补偿前挂着 / 2026-09-11 07:36:20.1838734+00:00` |
| 补偿对账之后服务端会话自己回到 Ready，不是停在 OPERATION_RECOVERY_REQUIRED | PASS | `Ready / READY` | `Ready / READY` |
| 车还回来了：下一条需求被受理并派车，车在取货点收下扫码并交给服务端 | PASS | `SublotSubmitted(L2-SUBLOT-20260911T073559948Z-NEXT) 1 条` | `1 条` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 40 lines, 40 distinct, 14 message types, 0 distinct violations, 0 known; schema compilation 20793 ms.` |

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
