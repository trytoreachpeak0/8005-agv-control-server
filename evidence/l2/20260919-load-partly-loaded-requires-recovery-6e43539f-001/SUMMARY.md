# L2 场景证据：load-partly-loaded-requires-recovery

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T021457999Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `6e43539f19d3e773ef0e4395f30bfe5ea8231c7b` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260919T021457999Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 装载指令针对三个仓位，与现场那一单同形 | PASS | `3` | `3` |
| 旅程停下等恢复，而不是作废需求继续走 | PASS | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` |
| 装载操作判 RecoveryRequired，不是确定失败 Failed | PASS | `RecoveryRequired` | `RecoveryRequired` |
| 需求留着（RecoveryRequired），没有被判 Cancelled，也没有被永久抑制——车上那两篮还有记录指向它 | PASS | `RecoveryRequired / 抑制 0 条` | `RecoveryRequired / 抑制 0 条` |
| 又跑了几轮之后旅程仍然停着，没有派车去关卡 | PASS | `Blocked / RIoT 单 1 条` | `Blocked / RIoT 单 1 条` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（合成对端发出的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 13 lines, 13 distinct, 8 message types, 0 distinct violations, 0 known; schema compilation 45297 ms.` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照
- `schema-conformance/` —— 车载端报文逐条过 protocol JSON Schema 的覆盖账与违约明细

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
