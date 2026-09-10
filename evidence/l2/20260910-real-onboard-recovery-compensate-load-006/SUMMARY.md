# L2 场景证据：real-onboard-recovery-compensate-load

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260910T015828137Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `2b1ece8a078b8efe73323d6d0f1dc7dd7ad2eb2d` |
| onboardHmiCommit | `3d8206fc9fc4ce02b58e78a205d9563be68c6b0e` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260910T015828137Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 起点到位：车报回一份 UNKNOWN，服务端判 RecoveryRequired 并停摆 | PASS | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired / UNKNOWN` | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired / UNKNOWN` |
| 现场读数变成真的：门关、仓里有货、锁反馈有效、开锁输出已复位——这正是补偿向量的入场券 | PASS | `CLOSED/OCCUPIED/1/0` | `CLOSED/OCCUPIED/1/0` |
| HMI 上出现可用的「补偿清空」入口（它绑 CanRequestLoadCompensation，与「申请恢复」是两个按钮） | PASS | `True` | `True` |
| 第一步握手：恢复会话已开，作用域是这一单这一次 attempt，管理员角色已认证 | PASS | `9e34ee70-9069-419d-8b47-deb11c2bceb3 / MAINTENANCE_ADMINISTRATOR / 含仓位 1` | `9e34ee70-9069-419d-8b47-deb11c2bceb3 / MAINTENANCE_ADMINISTRATOR / [1]` |
| 第二、三步握手：车载端报的动作是 COMPENSATE_LOAD_ALL_EMPTY，服务端授权并选定它 | PASS | `COMPENSATE_LOAD_ALL_EMPTY / 9e34ee70-9069-419d-8b47-deb11c2bceb3 / cf055d7d-73c2-3e56-a265-189e65d5ccc4` | `COMPENSATE_LOAD_ALL_EMPTY / 9e34ee70-9069-419d-8b47-deb11c2bceb3 / cf055d7d-73c2-3e56-a265-189e65d5ccc4` |
| 服务端在收到 LoadCompensationRequested 之后才下发补偿命令，并把它绑在工作流上 | PASS | `LoadCompensationCommand / 收到 1 条 LoadCompensationRequested` | `LoadCompensationCommand / 收到 1 条` |
| 第四步握手：车载端在真 Modbus 上重新开锁，门再弹开一次 | PASS | `UNLOCKING > 2 / 门 OPEN` | `UNLOCKING = 3 / 门 OPEN` |
| 车载端报回 ALL_EMPTY，那一仓 COMPLETED/EMPTY/LOCKED/RESET——服务端的对账判据要的正是这四样 | PASS | `ALL_EMPTY / COMPLETED / EMPTY / LOCKED / RESET` | `ALL_EMPTY / COMPLETED / EMPTY / LOCKED / RESET` |
| 第五步握手走完：服务端对账通过，工作流 Reconciled 而不是又一次 RecoveryRequired | PASS | `Reconciled` | `Reconciled` |
| 现场收在安全状态：门关、仓空、已锁、开锁输出复位 | PASS | `CLOSED/EMPTY/1/0` | `CLOSED/EMPTY/1/0` |
| 补偿终结这一单：需求与仓位操作都判 Cancelled | PASS | `Cancelled / Cancelled` | `Cancelled / Cancelled` |
| 旅程以 CANCELLED_BY_LOAD_COMPENSATION 收尾，同一个理由把业务键永久抑制 | PASS | `Completed / CANCELLED_BY_LOAD_COMPENSATION / 抑制 1 条` | `Completed / CANCELLED_BY_LOAD_COMPENSATION / 抑制 1 条 (CANCELLED_BY_LOAD_COMPENSATION)` |
| 补偿不产生替换结果：OperationResults 仍只有那一份 UNKNOWN，且没有被标成被替换 | PASS | `1 份 / UNKNOWN / 未被替换` | `1 份 / UNKNOWN / 未被替换` |
| 那条永远等不到 LoadResult 的 LoadBatch 命令，被补偿这一路同样结算掉了 | PASS | `补偿前挂着 / 补偿后已结算` | `补偿前挂着 / 2026-09-10 01:59:13.8092498+00:00` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
