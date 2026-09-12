# L2 场景证据：real-onboard-compensate-then-reconnect

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260912T135757910Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `5d92bab784aca6071426ce95c83905de9069be4c` |
| onboardHmiCommit | `6b8a0b06575f40fa0e682c5d307b9ce46bbe9227` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260912T135757910Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车载端的会话经协议故障代理建立（否则断开注入不到这条链路上） | PASS | `>= 1 SessionHello through the proxy` | `1` |
| 补偿清空走到对账：UNKNOWN 之后 Reconciled / ALL_EMPTY，需求 Cancelled，旅程 Completed | PASS | `RecoveryRequired -> Reconciled / ALL_EMPTY / Cancelled / Completed` | `RecoveryRequired -> Reconciled / ALL_EMPTY / Cancelled / Completed` |
| 补偿对账之后会话回到 Ready（断开之前的前提） | PASS | `Ready / READY` | `gen 1 / Ready / READY` |
| 补偿对账之后，恢复会话快照与补偿命令都已结清：快照被车确认或被新 revision 取代，命令被补偿结果结算 | FAIL | `>= 2 recovery rows, 0 neither acknowledged nor fenced` | `5 recovery rows, 2 live (LoadCompensationCommand[d1b71aa7] ack=False fenced=False; ExceptionRecoverySessionSnapshot[6972a98f] ack=False fenced=False)` |
| 重连之后会话在新世代回到 Ready | PASS | `gen > 1 / Ready / READY` | `gen 2 / Ready / READY` |
| 补偿会话留下的恢复会话快照与补偿命令，一条都没有被重放进新会话 | FAIL | `0 of 5 replayed` | `1 of 5 replayed (ExceptionRecoverySessionSnapshot[6972a98f] on #2)` |
| 重连之后车还接得了单：下一条需求被受理并派车，驱动脚本经自动化面扫码后车开锁等操作员 | PASS | `cfef0087-cb08-44da-9d10-b3cea54a2476 / 车在等操作员` | `cfef0087-cb08-44da-9d10-b3cea54a2476 / 车在等操作员` |
| 断开一次只换来一次重连：之后只有一条连接，而且到收尾还开着（没有哪一端撕会话） | PASS | `2 connections, 1 open` | `2 connections, 1 open (#1: relay disconnected on request; #2: open)` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 57 lines, 57 distinct, 14 message types, 0 distinct violations, 0 known; schema compilation 18416 ms.` |

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
