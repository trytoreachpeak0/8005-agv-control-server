# L2 场景证据：load-cancelled-before-sublot

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260910T105851833Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `02af63730e5f0b5c1520f2240bf41867000b4b2f` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260910T105851833Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车到取货站后停在等条码录入这一步 | PASS | `AwaitingSublot` | `AwaitingSublot` |
| 条码录入请求已发出且尚未被结算 | PASS | `未结算` | `AcknowledgedAt=` |
| 旅程以 CANCELLED_BY_OPERATOR 终结 | PASS | `Completed / CANCELLED_BY_OPERATOR` | `Completed / CANCELLED_BY_OPERATOR` |
| 需求终态为 Cancelled | PASS | `Cancelled` | `Cancelled` |
| 车辆调度租约已释放 | PASS | `已释放` | `ReleasedAt=2026-09-10 10:59:01.5120828+00:00` |
| 那条没人回答的条码录入请求被结算了 | PASS | `已结算` | `AcknowledgedAt=2026-09-10 10:59:01.5120828+00:00` |
| 按业务键写下了永久取消抑制 | PASS | `1 条 / CANCELLED_BY_OPERATOR` | `1 条 / CANCELLED_BY_OPERATOR / key=L2-SUBLOT-20260910T105851833Z-A\|WIRE_TO_GATE` |
| 全程没有下发过任何仓位操作 | PASS | `0` | `0` |
| 取消之后车立刻受理下一单 | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| 第二单的候选判定是 ACCEPTED | PASS | `ACCEPTED` | `ACCEPTED` |
| 被取消的需求不会被重新受理 | PASS | `DEMAND_ALREADY_ACCEPTED` | `DEMAND_ALREADY_ACCEPTED` |
| 两单各自一趟，没有多出来的旅程 | PASS | `2` | `2` |
| 全程只建了两条 RIoT 单，取消本身不派车 | PASS | `2` | `2` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（合成对端发出的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 9 lines, 9 distinct, 7 message types, 0 distinct violations, 0 known; schema compilation 13842 ms.` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照
- `schema-conformance/` —— 车载端报文逐条过 protocol JSON Schema 的覆盖账与违约明细

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
