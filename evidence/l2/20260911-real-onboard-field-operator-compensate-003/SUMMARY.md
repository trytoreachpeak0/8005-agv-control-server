# L2 场景证据：real-onboard-field-operator-compensate

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260911T073227488Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `369919f53f5a779ba1e2ac88d78f8d3ba9bc5858` |
| onboardHmiCommit | `bb58b217e95c611de597212d763f7579fdcb0e03` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260911T073227488Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 驱动脚本经自动化面扫码并钉死锁反馈之后，车报回真的 UNKNOWN：仓位操作 RecoveryRequired、旅程 Blocked | PASS | `a02a4f4b-27cf-4134-8772-5c62ab979be6 / RecoveryRequired / LOAD_RESULT_REQUIRES_RECOVERY` | `a02a4f4b-27cf-4134-8772-5c62ab979be6 / RecoveryRequired / LOAD_RESULT_REQUIRES_RECOVERY` |
| 维护人员三步之后的现场是补偿的入场券：门关、有货、锁反馈有效、开锁输出复位 | PASS | `CLOSED/OCCUPIED/1/0` | `CLOSED/OCCUPIED/1/0` |
| 驱动脚本经自动化面发起 COMPENSATE_LOAD_ALL_EMPTY，服务端对账 Reconciled、结论 ALL_EMPTY，需求判 Cancelled | PASS | `Reconciled / ALL_EMPTY / Cancelled` | `Reconciled / ALL_EMPTY / Cancelled` |
| 补偿时车真的开了门，驱动脚本替维护人员把那一仓取空关上——且只服务了那一仓 | PASS | `[1]` | `[1]` |
| 恢复会话的理由带车载端主机加的前缀【车载端自动化接口】——事后分得清这一下是脚本按的 | PASS | `1 个会话 / 理由含【车载端自动化接口】 / COMPENSATE_LOAD_ALL_EMPTY` | `1 个会话 / 【车载端自动化接口】L2 彩排：驱动脚本补偿清空 / COMPENSATE_LOAD_ALL_EMPTY` |
| 单需求旅程以 CANCELLED_BY_LOAD_COMPENSATION 收尾并永久抑制，悬空的 LoadBatch 命令被结算 | PASS | `Completed / CANCELLED_BY_LOAD_COMPENSATION / 抑制 1 条 / 补偿前挂着、之后已结算` | `Completed / CANCELLED_BY_LOAD_COMPENSATION / 抑制 1 条 / 补偿前挂着、已结算` |
| 现场收在安全状态：门关、仓空、已锁、开锁输出复位 | PASS | `CLOSED/EMPTY/1/0` | `CLOSED/EMPTY/1/0` |
| 补偿对账之后服务端会话自己回到 Ready | PASS | `Ready / READY` | `Ready / READY` |
| 车还回来了：下一条需求被受理并派车，驱动脚本经自动化面扫码后车开锁等操作员 | PASS | `6e127bec-ce54-42b7-8c39-b67229cd0bd3 / L2-FOC-20260911T073227488Z-NEXT / 车在等操作员` | `6e127bec-ce54-42b7-8c39-b67229cd0bd3 / L2-FOC-20260911T073227488Z-NEXT / 车在等操作员` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 49 lines, 49 distinct, 14 message types, 0 distinct violations, 0 known; schema compilation 21148 ms.` |

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
