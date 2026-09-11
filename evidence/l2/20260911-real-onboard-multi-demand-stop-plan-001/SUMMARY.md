# L2 场景证据：real-onboard-multi-demand-stop-plan

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260911T042843208Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `25a298d4aeea93093922819f40100de8d910b5f5` |
| onboardHmiCommit | `0f2cf9d341b019860927f9cd27f4b6c11c32c5a3` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260911T042843208Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 四条需求凑成一趟旅程，四个取货停靠加一个关卡 | PASS | `4 PICKUP + 1 GATE` | `4 PICKUP + 1 GATE` |
| 是一趟旅程，不是四趟 | PASS | `1` | `1` |
| 服务端按契约发出五条腿的行程带（四个取货加一个关卡） | PASS | `5` | `5` |
| 车辆侧收下了这条行程带——没有 PROTOCOL_SCHEMA_INVALID 拒收 | PASS | `0 次拒收` | `0 次拒收` |
| 停靠 1 的 1 号仓走通真 Modbus 闭环 | PASS | `CLOSED/OCCUPIED/1/0` | `CLOSED/OCCUPIED/1/0` |
| 停靠 1 的装载提交，累计 1 次 | PASS | `>= 1` | `1` |
| 停靠 2 的 2 号仓走通真 Modbus 闭环 | PASS | `CLOSED/OCCUPIED/1/0` | `CLOSED/OCCUPIED/1/0` |
| 停靠 2 的装载提交，累计 2 次 | PASS | `>= 2` | `2` |
| 停靠 3 的 3 号仓走通真 Modbus 闭环 | PASS | `CLOSED/OCCUPIED/1/0` | `CLOSED/OCCUPIED/1/0` |
| 停靠 3 的装载提交，累计 3 次 | PASS | `>= 3` | `3` |
| 停靠 4 的 4 号仓走通真 Modbus 闭环 | PASS | `CLOSED/OCCUPIED/1/0` | `CLOSED/OCCUPIED/1/0` |
| 停靠 4 的装载提交，累计 4 次 | PASS | `>= 4` | `4` |
| 四个停靠都装完之后旅程进入去关卡那一段 | PASS | `AwaitingGateArrival` | `AwaitingGateArrival` |
| 装货阶段收尾时记下了理由 | PASS | `a reason` | `NO_FURTHER_CARGO` |
| 全程没有任何一次装载被判 RecoveryRequired | PASS | `0` | `0` |
| 四条需求全程保持 Accepted | PASS | `4 条 Accepted` | `4 条，其中 0 条不是 Accepted` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 72 lines, 72 distinct, 11 message types, 0 distinct violations, 0 known; schema compilation 21195 ms.` |

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
