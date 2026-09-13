# L2 场景证据：real-onboard-cancellation-authorization-lost

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260913T035244125Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `50d7f4e701548cb3727ad427613b6c40253de79f` |
| onboardHmiCommit | `1086c4a816e502bc2f0794d972af03aeca1c8322` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260913T035244125Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 服务端授权了第一次取消、工作流在等结果，授权应答被代理丢掉，车载端没拿到回答 | PASS | `非 200 / AwaitingResult / 丢 1 条` | `409 The operation has timed out. / AwaitingResult / 丢 1 条` |
| 授权应答丢了之后再按一次，拿到同一个授权并清空完成：自动化面回 200 | PASS | `200` | `200` |
| 取消工作流凭空仓证明对账 Reconciled，需求判 Cancelled | PASS | `Reconciled / Cancelled` | `Reconciled / Cancelled` |
| 两次按下是两条 messageId 不同的取消请求，payload 相同（沿用首发的操作员与理由），两次都拿到 AUTHORIZED | PASS | `2 条 / 2 个 id / payload 相同 / AUTHORIZED,AUTHORIZED` | `2 条 / 2 个 id / payload 相同 / AUTHORIZED,AUTHORIZED` |
| 从第一次按下到收尾，车载端没有重连：丢一次应答不换来一次掐连接 | PASS | `SessionHello 1 条不变` | `SessionHello 1 → 1 条` |
| 现场收在安全状态：门关、仓空、已锁、开锁输出复位 | PASS | `CLOSED/EMPTY/1/0` | `CLOSED/EMPTY/1/0` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 23 lines, 23 distinct, 12 message types, 0 distinct violations, 0 known; schema compilation 17869 ms.` |

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
