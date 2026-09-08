# L2 场景证据：load-cancelled-before-sublot

结论：**FAIL**

失败原因：Exception calling "ExecuteReader" with "0" argument(s): "SQLite Error 1: 'no such column: DemandId'."

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260908T044615749Z` |
| agvId | `AGV-L2-001` |
| controlServerCommit | `bc145629ff8beafe5e09f65fef0c089815d2061a` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260908T044615749Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车到取货站后停在等条码录入这一步 | PASS | `AwaitingSublot` | `AwaitingSublot` |
| 条码录入请求已发出且尚未被结算 | PASS | `未结算` | `AcknowledgedAt=` |
| 旅程以 CANCELLED_BY_OPERATOR 终结 | PASS | `Completed / CANCELLED_BY_OPERATOR` | `Completed / CANCELLED_BY_OPERATOR` |
| 需求终态为 Cancelled | PASS | `Cancelled` | `Cancelled` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
