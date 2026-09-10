# L2 场景证据：sublot-wait-timeout

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260910T070813610Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `b5a5e0ebb77a54f6cec727ddd90418c2425119fe` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260910T070813610Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车到取货站后停在等条码录入这一步 | PASS | `AwaitingSublot` | `AwaitingSublot` |
| 等待起点被单独记下来，不是复用 UpdatedAt | PASS | `有值` | `SublotWaitStartedAt=2026-09-10 07:08:25.8153994+00:00` |
| 窗口未到期时旅程原地不动 | PASS | `AwaitingSublot` | `AwaitingSublot` |
| 窗口未到期时需求仍是 Accepted | PASS | `Accepted` | `Accepted` |
| 旅程以 CANCELLED_BY_STATION_TIMEOUT 终结 | PASS | `Completed / CANCELLED_BY_STATION_TIMEOUT` | `Completed / CANCELLED_BY_STATION_TIMEOUT` |
| 需求终态为 Cancelled | PASS | `Cancelled` | `Cancelled` |
| 车辆调度租约已释放 | PASS | `已释放` | `ReleasedAt=2026-09-10 07:08:36.8146678+00:00` |
| 那条没人回答的条码录入请求被结算了 | PASS | `已结算` | `AcknowledgedAt=2026-09-10 07:08:36.8146678+00:00` |
| 按业务键写下了永久取消抑制 | PASS | `1 条 / CANCELLED_BY_STATION_TIMEOUT` | `1 条 / CANCELLED_BY_STATION_TIMEOUT / key=L2-SUBLOT-20260910T070813610Z-A\|WIRE_TO_GATE` |
| 超时不下发也不残留任何仓位操作 | PASS | `0` | `0` |
| 超时不开任何恢复流程 | PASS | `0` | `0` |
| 下一单照常装载并发往关卡 | PASS | `AwaitingGateArrival` | `AwaitingGateArrival` |
| 一次超时之后，下一单仍能走到 Succeeded | PASS | `Completed / Succeeded` | `Completed / Succeeded` |
| 全程三条 RIoT 单：超时那单只到取货口，第二单跑完两段 | PASS | `3` | `3` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
