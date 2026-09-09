# L2 场景证据：real-onboard-normal-load

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260909T154954448Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `a7337091b6329325ff7f7fad32a0de83113f908f` |
| onboardHmiCommit | `3d8206fc9fc4ce02b58e78a205d9563be68c6b0e` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260909T154954448Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 真车载端在场时需求被受理并建出 TO_PICKUP 单 | PASS | `CONFIRMED` | `CONFIRMED` |
| UIA 录入的 sublot 经真车载端以 KEYBOARD 方式提交到服务端 | PASS | `L2-SUBLOT-20260909T154954448Z / KEYBOARD / L2-OPERATOR` | `L2-SUBLOT-20260909T154954448Z / KEYBOARD / L2-OPERATOR` |
| 车载端上报在等操作员时，它说要开的那个仓门确实是开的 | PASS | `slot 1 OPEN` | `slot 1 OPEN` |
| 装载走的是真 Modbus 闭环：车载端开锁、放货、关门、锁反馈回到 1、开锁输出复位 | PASS | `CLOSED/OCCUPIED/1/0` | `CLOSED/OCCUPIED/1/0` |
| 装载操作提交（Committed），且记的是 UIA 录进去的那个 sublot | PASS | `Committed / L2-SUBLOT-20260909T154954448Z` | `Committed / L2-SUBLOT-20260909T154954448Z` |
| 出发前安全检查通过后才建 TO_GATE 单 | PASS | `CONFIRMED` | `CONFIRMED` |
| 关卡卸的是装货时用的那个仓位 | PASS | `1` | `1` |
| 卸载后仓位回到空、门关、锁上 | PASS | `CLOSED/EMPTY/1/0` | `CLOSED/EMPTY/1/0` |
| journey 走到 Completed | PASS | `Completed` | `Completed` |
| 卸载操作提交（Committed） | PASS | `Committed` | `Committed` |
| 需求终态为 Succeeded | PASS | `Succeeded` | `Succeeded` |
| 全程只建了两条 RIoT 单（取货一条、关卡一条） | PASS | `2` | `2` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
