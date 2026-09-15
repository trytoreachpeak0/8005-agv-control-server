# L2 场景证据：real-onboard-stale-stop-after-station-timeout

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260915T082400673Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `11da37745b5b5ec8742e5852361fd435f5df7ae7` |
| onboardHmiCommit | `243aa66add6967ea8e4bb7419fa19023dcfae5c2` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260915T082400673Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车到站后要子批、给「取消装货」，当前站清单挂着这单（前提） | PASS | `canSubmitSublot / offersCancel / worklistHasDemand 均为 True` | `canSubmitSublot=True offersCancel=True station=N1-3_N1-7 deadline=09/15/2026 16:25:15 worklistHasDemand=True planLegs=2` |
| 站点等待到期，服务端以 CANCELLED_BY_STATION_TIMEOUT 结束需求、旅程 Completed、没发过仓位命令（前提） | PASS | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / 0 / */Completed` | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / 0 / 1/Completed` |
| 旅程被站点超时结束之后，车不再要子批 | PASS | `canSubmitSublot=False` | `canSubmitSublot=False offersCancel=False station=N1-3_N1-7 deadline= worklistHasDemand=False planLegs=0` |
| 旅程被站点超时结束之后，车不再给「取消装货」 | PASS | `offersCancel=False` | `canSubmitSublot=False offersCancel=False station=N1-3_N1-7 deadline= worklistHasDemand=False planLegs=0` |
| 旅程被站点超时结束之后，车上的当前站清单不再挂着这单 | PASS | `worklistHasDemand=False` | `canSubmitSublot=False offersCancel=False station=N1-3_N1-7 deadline= worklistHasDemand=False planLegs=0` |
| 旅程结束之后按两下「取消装货」，服务端不掐连接、车不重连 | PASS | `SessionHello 次数不变（1）` | `按前 1 / 按后 1；第一下 HTTP 409 reasonCode=RECOVERY_RESUME_DISABLED error=；第二下 HTTP 409 reasonCode=RECOVERY_RESUME_DISABLED error=` |
| 迟到的取消不会被再授权一次：不落取消工作流，需求仍是超时判的 Cancelled | PASS | `0 条工作流 / Cancelled` | `0 条工作流 / Cancelled` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 29 lines, 29 distinct, 7 message types, 0 distinct violations, 0 known; schema compilation 12626 ms.` |

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
