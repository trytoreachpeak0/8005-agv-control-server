# L2 场景证据：real-onboard-recovery-compensate-load

结论：**FAIL**

失败原因：Timed out after 120s waiting for: the vehicle re-opened the slot for the compensation. Last observed: 2

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260910T014343364Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `2b1ece8a078b8efe73323d6d0f1dc7dd7ad2eb2d` |
| onboardHmiCommit | `3d8206fc9fc4ce02b58e78a205d9563be68c6b0e` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260910T014343364Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 起点到位：车报回一份 UNKNOWN，服务端判 RecoveryRequired 并停摆 | PASS | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired / UNKNOWN` | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired / UNKNOWN` |
| 传感器修好之后现场读数是真的：门关、仓空、锁反馈有效、开锁输出已复位 | PASS | `CLOSED/EMPTY/1/0` | `CLOSED/EMPTY/1/0` |
| HMI 上出现可用的「补偿清空」入口（它绑 CanRequestLoadCompensation，与「申请恢复」是两个按钮） | PASS | `True` | `True` |
| 第一步握手：恢复会话已开，作用域是这一单这一次 attempt，管理员角色已认证 | PASS | `8aeb53e7-fee3-47d8-be54-2e06fd9f89f7 / MAINTENANCE_ADMINISTRATOR / 含仓位 1` | `8aeb53e7-fee3-47d8-be54-2e06fd9f89f7 / MAINTENANCE_ADMINISTRATOR / [1]` |
| 第二、三步握手：车载端报的动作是 COMPENSATE_LOAD_ALL_EMPTY，服务端授权并选定它 | PASS | `COMPENSATE_LOAD_ALL_EMPTY / 8aeb53e7-fee3-47d8-be54-2e06fd9f89f7 / 8e5739cf-5792-3855-a5cc-6a79b048baba` | `COMPENSATE_LOAD_ALL_EMPTY / 8aeb53e7-fee3-47d8-be54-2e06fd9f89f7 / 8e5739cf-5792-3855-a5cc-6a79b048baba` |
| 服务端在收到 LoadCompensationRequested 之后才下发补偿命令，并把它绑在工作流上 | PASS | `LoadCompensationCommand / 收到 1 条 LoadCompensationRequested` | `LoadCompensationCommand / 收到 1 条` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
