# L2 场景证据：load-cancelled-in-flight

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260909T145420835Z` |
| agvId | `AGV-L2-001` |
| controlServerCommit | `42a85f0853ac2e190ff705dc14be4c8ee90e72fe` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260909T145420835Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 第一条需求的装货命令已下发，停靠在等结果 | PASS | `AwaitingLoadResult` | `AwaitingLoadResult` |
| 取消之前，那条装货命令确实还挂着没结算 | PASS | `未结算` | `AcknowledgedAt=` |
| 取消工作流凭空仓证明结清，且钉的是在途那个 attempt | PASS | `Reconciled / 9ff21dd3-1beb-545c-abf6-caf8645708c1` | `Reconciled / 9ff21dd3-1beb-545c-abf6-caf8645708c1` |
| 在途的那次仓位操作终态是 Cancelled | PASS | `Cancelled` | `Cancelled` |
| 被取消的需求终态是 Cancelled | PASS | `Cancelled` | `Cancelled` |
| 按业务键写下了永久取消抑制 | PASS | `1 条 / CANCELLED_BY_OPERATOR` | `1 条 / CANCELLED_BY_OPERATOR` |
| 那条永远等不到结果的装货命令被取消结算掉了 | PASS | `已结算` | `2026-09-09 14:54:34.4126797+00:00` |
| 取消终结的是这一单不是这个停靠：旅程回到 AwaitingSublot | PASS | `AwaitingSublot` | `AwaitingSublot` |
| 跑起来的旅程不带阻塞理由 | PASS | `(空)` | `` |
| 取消之后剩下那条需求照常完成装载闭环 | PASS | `1` | `1` |
| 剩下那条需求在旅程里的状态是 Loaded | PASS | `Loaded` | `Loaded` |
| 装完之后这趟车照常开去关卡 | PASS | `AwaitingGateArrival` | `AwaitingGateArrival` |
| 全程只建了两条 RIoT 单，取消本身不派车 | PASS | `2` | `2` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
