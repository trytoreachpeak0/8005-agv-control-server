# L2 场景证据：route-graph-engine

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260907T164932728Z` |
| agvId | `AGV-L2-001` |
| controlServerCommit | `646624acc98eb3d7cacb330eadb1f3f3c8ecc76f` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260907T164932728Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 设计态读回了种子图的八条有向边 | PASS | `8` | `8` |
| 设计态读回了三个站点 | PASS | `3` | `3` |
| 运行态周期也跑过（移除集已读回，空是正常答案） | PASS | `(非空)` | `2026-09-07 16:49:40.2754021+00:00` |
| 边组指纹为空串且已取过一次（合成 RIoT 与 map25 一样没有边组） | PASS | `'' + 已取过` | `'' + 已取过` |
| 快照不是陈旧态 | PASS | `(无陈旧原因)` | `(null)` |
| 引擎开着时派车照常走通（可达性判据放行） | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| 候选判定结果是 ACCEPTED，没有被任何 ROUTE_GRAPH_* 原因挡住 | PASS | `ACCEPTED` | `ACCEPTED` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
