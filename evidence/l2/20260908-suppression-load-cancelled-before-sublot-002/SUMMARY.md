# L2 场景证据：load-cancelled-before-sublot

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260908T021433316Z` |
| agvId | `AGV-L2-001` |
| controlServerCommit | `05614da9319afa964fa5ca47fc739149cdf98916` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260908T021433316Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车到取货站后停在等条码录入这一步 | PASS | `AwaitingSublot` | `AwaitingSublot` |
| 条码录入请求已发出且尚未被结算 | PASS | `未结算` | `AcknowledgedAt=` |
| 旅程以 CANCELLED_BY_OPERATOR 终结 | PASS | `Completed / CANCELLED_BY_OPERATOR` | `Completed / CANCELLED_BY_OPERATOR` |
| 需求终态为 Cancelled | PASS | `Cancelled` | `Cancelled` |
| 车辆调度租约已释放 | PASS | `已释放` | `ReleasedAt=2026-09-08 02:14:41.9889534+00:00` |
| 那条没人回答的条码录入请求被结算了 | PASS | `已结算` | `AcknowledgedAt=2026-09-08 02:14:41.9889534+00:00` |
| 按业务键写下了永久取消抑制 | PASS | `1 条 / CANCELLED_BY_OPERATOR` | `1 条 / CANCELLED_BY_OPERATOR / key=L2-SUBLOT-20260908T021433316Z-A\|WIRE_TO_GATE` |
| 全程没有下发过任何仓位操作 | PASS | `0` | `0` |
| 取消之后车立刻受理下一单 | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| 第二单的候选判定是 ACCEPTED | PASS | `ACCEPTED` | `ACCEPTED` |
| 被取消的需求不会被重新受理 | PASS | `DEMAND_ALREADY_ACCEPTED` | `DEMAND_ALREADY_ACCEPTED` |
| 两单各自一趟，没有多出来的旅程 | PASS | `2` | `2` |
| 全程只建了两条 RIoT 单，取消本身不派车 | PASS | `2` | `2` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
