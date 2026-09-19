# L2 场景证据：real-onboard-partial-load-and-occupied-slot

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T022551141Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `665101a48f9894d82fe856fc97de207d757d51a1` |
| onboardHmiCommit | `a1a54dbd7217dd64f1ae93648a4cfbd8659bc51c` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260919T022551141Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车没有开门：目标仓已经有货，车照实报 FAILED，那一仓 NOT_STARTED / OCCUPIED / LOCKED / RESET 并挂 SLOT_OPERATION_CONFLICT | PASS | `FAILED / NOT_STARTED / OCCUPIED / LOCKED / RESET / [SLOT_OPERATION_CONFLICT]` | `FAILED / NOT_STARTED / OCCUPIED / LOCKED / RESET / [SLOT_OPERATION_CONFLICT]` |
| 整个装货过程一次开锁都没有，门一直关着 | PASS | `0 次 UNLOCKING / CLOSED` | `0 次 UNLOCKING / CLOSED/EMPTY/1/0` |
| 服务端判 RecoveryRequired、旅程停下：仓里那箱货要先取出来，不能悄悄作废需求 | PASS | `RecoveryRequired / Blocked / LOAD_RESULT_REQUIRES_RECOVERY` | `RecoveryRequired / Blocked / LOAD_RESULT_REQUIRES_RECOVERY` |
| 被拒的这一单能走补偿清空：车打开了那一仓、维护人员取出，对账 Reconciled / ALL_EMPTY，需求 Cancelled | PASS | `Reconciled / ALL_EMPTY / Cancelled / 开过 [1]` | `Reconciled / ALL_EMPTY / Cancelled / 开过 [1]` |
| 现场收在安全状态：1 号仓门关、仓空、已锁、开锁输出复位 | PASS | `CLOSED/EMPTY/1/0` | `CLOSED/EMPTY/1/0` |
| 第二单要装三篮，服务端给了三个仓 | PASS | `3` | `3` |
| 车照实报了部分装上：两仓 COMPLETED / OCCUPIED，空关的那一仓 FAILED / EMPTY，整体 FAILED | PASS | `FAILED \| 1:COMPLETED/OCCUPIED 2:COMPLETED/OCCUPIED 3:FAILED/EMPTY` | `FAILED \| 1:COMPLETED/OCCUPIED 2:COMPLETED/OCCUPIED 3:FAILED/EMPTY` |
| 部分装上不再是确定失败：装货 RecoveryRequired、旅程停下、需求留着不作废（#170 的那一处） | PASS | `RecoveryRequired / Blocked / LOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired` | `RecoveryRequired / Blocked / LOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired` |
| 那两篮确实在车上：两仓门关、有货、已锁 | PASS | `CLOSED/OCCUPIED/1/0 ×2` | `1=CLOSED/OCCUPIED/1/0 2=CLOSED/OCCUPIED/1/0` |
| 补偿清空只开了有货的那两仓，取出后对账 Reconciled / ALL_EMPTY，需求 Cancelled | PASS | `Reconciled / ALL_EMPTY / Cancelled / 开过 [1,2]` | `Reconciled / ALL_EMPTY / Cancelled / 开过 [1,2]` |
| 下一单分到的每一仓此刻确实是空的——即便它们正是刚才出过事的那几个 | PASS | `全部 EMPTY` | `1=EMPTY 2=EMPTY` |
| 下一单照常装货并提交 | PASS | `Committed` | `Committed` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 323 lines, 323 distinct, 14 message types, 0 distinct violations, 0 known; schema compilation 38622 ms.` |

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
