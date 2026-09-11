# L2 场景证据：real-onboard-restart-while-waiting-operator

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260911T025332106Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `5f55777b6890624e05c81231905ddc23b94ce994` |
| onboardHmiCommit | `3cf26651111220cec67a370ac5b9f81bc21bd697` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260911T025332106Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 起点到位：车载端开了锁、在等操作员，门开着，仓位操作 Prepared，装货命令挂着 | PASS | `OPEN / Prepared / 装货命令挂着` | `OPEN / Prepared / 装货命令挂着` |
| 客户端退出时没有留下结果：OperationResults 0 行，仓位操作仍是 Prepared | PASS | `0 行 / Prepared` | `0 行 / Prepared` |
| 重启后车载端握手如实上报那一次未结的 attempt | PASS | `060c23a2-e7a6-3156-bf9f-e2acc6aab48a` | `060c23a2-e7a6-3156-bf9f-e2acc6aab48a` |
| 重启之后这次装载拿到结论：服务端判 RecoveryRequired 并让旅程停摆，恢复入口才有地方落 | FAIL | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired，结果 1 份` | `AwaitingLoadResult / ONBOARD_SESSION_NOT_READY / Prepared，结果 0 份` |
| 那份结论报的是 UNKNOWN，但仓位的三个物理字段是重启后的真实读数：EMPTY / LOCKED / RESET | FAIL | `UNKNOWN / UNKNOWN / EMPTY / LOCKED / RESET` | `(no result)` |
| 重启后按「补偿清空」开得出恢复会话，不被拒 RECOVERY_DEMAND_NOT_BLOCKED | FAIL | `OPENED` | `REJECTED:RECOVERY_DEMAND_NOT_BLOCKED` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 39 lines, 39 distinct, 10 message types, 0 distinct violations, 0 known; schema compilation 18822 ms.` |

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
