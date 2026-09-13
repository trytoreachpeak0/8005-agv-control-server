# L2 场景证据：real-onboard-reconnect-then-cancel-before-sublot

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260913T072754203Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `404a74e75da0c1eef9de6872737f41fc0da1902c` |
| onboardHmiCommit | `336a105720f8570eeaf6d48e43a80325634ac540` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260913T072754203Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车载端的会话经协议故障代理建立（否则断开注入不到这条链路上） | PASS | `>= 1 SessionHello through the proxy` | `1` |
| 重连握手时旅程已受理、车还没到站：新世代的握手已落库，stop 仍是第 0 轮 | PASS | `gen > 1 / AwaitingPickupArrival / LoadRound 0` | `gen 2 / RecoveryRequired / DEPARTURE_SAFETY_NOT_READY / AwaitingPickupArrival / LoadRound 0` |
| 到站之后引擎发出第 1 轮条码录入请求，取消之前它还没被结算 | PASS | `AwaitingSublot / LoadRound 1 / SublotEntryRequested unacknowledged` | `AwaitingSublot / LoadRound 1 / SublotEntryRequested unacknowledged` |
| 取消请求在重连握手的同一条连接上发出，需求 Cancelled 并以 CANCELLED_BY_OPERATOR 抑制 | PASS | `#2 / Cancelled / CANCELLED_BY_OPERATOR` | `#2 / Cancelled / CANCELLED_BY_OPERATOR（面答 HTTP 200）` |
| 单需求旅程以 CANCELLED_BY_OPERATOR 结束 | PASS | `Completed / CANCELLED_BY_OPERATOR` | `Completed / CANCELLED_BY_OPERATOR` |
| 第 1 轮条码录入请求被这次取消结算（#40：连接里握手读进来的旧 stop 让它漏掉） | PASS | `SublotEntryRequested acknowledged` | `SublotEntryRequested AcknowledgedAt=2026-09-13 07:28:18.87555+00:00` |
| 断开一次只换来一次重连：之后只有一条连接，而且到收尾还开着（没有哪一端撕会话） | PASS | `2 connections, 1 open` | `2 connections, 1 open (#1: relay disconnected on request; #2: open)` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 16 lines, 16 distinct, 8 message types, 0 distinct violations, 0 known; schema compilation 13479 ms.` |

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
