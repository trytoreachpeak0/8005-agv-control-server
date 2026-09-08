# L2 场景证据：sublot-wait-timeout

结论：**FAIL**

失败原因：Exception calling "ExecuteReader" with "0" argument(s): "SQLite Error 1: 'no such column: DemandId'."

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260908T044625258Z` |
| agvId | `AGV-L2-001` |
| controlServerCommit | `bc145629ff8beafe5e09f65fef0c089815d2061a` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260908T044625258Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车到取货站后停在等条码录入这一步 | PASS | `AwaitingSublot` | `AwaitingSublot` |
| 等待起点被单独记下来，不是复用 UpdatedAt | PASS | `有值` | `SublotWaitStartedAt=2026-09-08 04:46:33.7264361+00:00` |
| 窗口未到期时旅程原地不动 | PASS | `AwaitingSublot` | `AwaitingSublot` |
| 窗口未到期时需求仍是 Accepted | PASS | `Accepted` | `Accepted` |
| 旅程以 CANCELLED_BY_STATION_TIMEOUT 终结 | PASS | `Completed / CANCELLED_BY_STATION_TIMEOUT` | `Completed / CANCELLED_BY_STATION_TIMEOUT` |
| 需求终态为 Cancelled | PASS | `Cancelled` | `Cancelled` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
