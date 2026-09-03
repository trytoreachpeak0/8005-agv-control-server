# L2 场景证据：load-result-requires-recovery

结论：**FAIL**

失败原因：Timed out after 60s waiting for: the server noticed the peer is gone. Last observed: "Ready"

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260903T082638716Z` |
| agvId | `AGV-L2-001` |
| controlServerCommit | `8fcbdbcf456d62fec19013efb7a8d7eeecab679e` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260903T082638716Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 装载指令已下发，旅程在等结果 | PASS | `AwaitingLoadResult` | `AwaitingLoadResult` |
| 旅程进入 Blocked | PASS | `Blocked` | `Blocked` |
| 阻塞原因是 LOAD_RESULT_REQUIRES_RECOVERY | PASS | `LOAD_RESULT_REQUIRES_RECOVERY` | `LOAD_RESULT_REQUIRES_RECOVERY` |
| 装载操作判 RecoveryRequired（不是 Committed，也不是停在 Prepared） | PASS | `RecoveryRequired` | `RecoveryRequired` |
| 需求状态转为 RecoveryRequired | PASS | `RecoveryRequired` | `RecoveryRequired` |
| 装载没成时不建 TO_GATE 单（RIoT 单仍只有一条） | PASS | `1` | `1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
