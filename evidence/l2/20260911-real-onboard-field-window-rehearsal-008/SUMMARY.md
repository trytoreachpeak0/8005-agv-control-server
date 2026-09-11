# L2 场景证据：real-onboard-field-window-rehearsal

结论：**FAIL**

失败原因：The property 'Status' cannot be found on this object. Verify that the property exists.

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260911T142626342Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `f60534c896c10adba532d30595689946c2d1efa0` |
| onboardHmiCommit | `6b8a0b06575f40fa0e682c5d307b9ce46bbe9227` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260911T142626342Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 四条需求凑成一趟旅程：四个取货停靠加一个关卡 | PASS | `5` | `5` |
| 停靠 1 场景 A：驱动脚本空关两轮都换来车自己重开，之后放料提交，恢复入口一次没出现 | PASS | `2 轮 / Committed / 入口未出现 / UNLOCKING >= 3` | `2 轮 / Committed / 入口未出现（看到过的按钮：LOAD_CANCELLATION） / UNLOCKING 3` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 108 lines, 108 distinct, 11 message types, 0 distinct violations, 0 known; schema compilation 21024 ms.` |

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
