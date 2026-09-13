# L2 场景证据：real-onboard-field-continuous-rehearsal

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260913T063904789Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `9f12f25f44b16ca9ea22bee7b65477910b7777aa` |
| onboardHmiCommit | `6b8a0b06575f40fa0e682c5d307b9ce46bbe9227` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260913T063904789Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 开跑时会话 Ready、没有未完成旅程 | PASS | `(无未完成旅程)` | `(无未完成旅程)` |
| 一次 Invoke-FieldContinuousDrive 调用开完两趟，两趟都 Completed、都不需要续跑 | PASS | `2 趟 / Completed / 1 次` | `2 趟 / Completed×1,Completed×1` |
| 两趟共三次装货全部 Committed（第一趟两站、第二趟一站） | PASS | `3 条 Committed` | `1:Committed,2:Committed,1:Committed` |
| 两趟共三次卸货全部 Committed | PASS | `3 条 Committed` | `Committed,Committed,Committed` |
| 两趟之间车自己去充电并被释放，驱动空转等过了它，第二趟在充电行程之后才建 | PASS | `1 次充电 Completed，第二趟晚于它` | `1 次 Completed / 第二趟 2026-09-13 06:39:57.365692+00:00` |
| 再起的驱动从装货半路接手第三趟（停靠 1 已扫码、车等在仓位上）并开完 | PASS | `1 趟 / 接手于 1/AwaitingLoadResult / Completed` | `1 趟 / 接手于 1/AwaitingLoadResult / Completed` |
| 第三趟：停靠 1 那次接手的装货与停靠 2 的照常装货都 Committed | PASS | `1:N-RESUMED:Committed,2:N:Committed` | `1:N-RESUMED:Committed,2:N:Committed` |
| 第三趟关卡：两条卸货 Committed | PASS | `2 条 Committed` | `Committed,Committed` |
| 全程没有旅程 Blocked，会话一直是开跑时那一代（没有重连）、仍 Ready | PASS | `0 / generation 1 / Ready` | `0 / generation 1 / Ready` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 167 lines, 167 distinct, 11 message types, 0 distinct violations, 0 known; schema compilation 20997 ms.` |

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
