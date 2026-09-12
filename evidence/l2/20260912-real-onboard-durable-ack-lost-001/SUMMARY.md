# L2 场景证据：real-onboard-durable-ack-lost

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260912T124008316Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `3558415026ab00e95796b4bc80ee2f276c4914cf` |
| onboardHmiCommit | `6b8a0b06575f40fa0e682c5d307b9ce46bbe9227` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260912T124008316Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车载端的会话经协议故障代理建立（否则丢 ack 注入不到这条链路上） | PASS | `>= 1 SessionHello through the proxy` | `1` |
| 丢掉的是一份服务端已经收下的装载结果：ProtocolInbox 有这一行，装载已 Committed | PASS | `OperationResult / Committed` | `OperationResult / Committed` |
| 车重连后在新会话里补发同一 messageId 的 OperationResult，sessionGeneration 换成新的 | PASS | `same messageId replayed at a newer generation` | `first on connection 1 at generation 1; 27 replay(s), first on connection 2 at generation 2` |
| 补发的 OperationResult 被服务端以 DurableAck 确认（不是掐连接） | FAIL | `DurableAck for 02d19379-9e59-1c52-ad8e-6e583632ca23 on a connection after 1` | `no acknowledgement within 60 s` |
| 丢一次 ack 只换来一次重连：之后只有一条连接，而且它还开着 | FAIL | `2 connections, 1 open` | `28 connections, 0 open (#1: relay dropped DurableAck for OperationResult; #2: server closed; #3: server closed; #4: server closed; #5: server closed; #6: server closed; #7: server closed; #8: server closed; #9: server closed; #10: server closed; #11: server closed; #12: server closed; #13: server closed; #14: server closed; #15: server closed; #16: server closed; #17: server closed; #18: server closed; #19: server closed; #20: server closed; #21: server closed; #22: server closed; #23: server closed; #24: server closed; #25: server closed; #26: server closed; #27: server closed; #28: server closed)` |
| 补发被确认之后旅程照常走完 | FAIL | `Completed` | `(not reached: the replay was never acknowledged; stage AwaitingLoadResult)` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 48 lines, 48 distinct, 10 message types, 0 distinct violations, 0 known; schema compilation 15570 ms.` |

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
