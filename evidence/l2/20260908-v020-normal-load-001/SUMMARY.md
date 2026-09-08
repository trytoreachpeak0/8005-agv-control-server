# L2 场景证据：normal-load

结论：**FAIL**

失败原因：Timed out after 120s waiting for: the arrival was trusted and the journey reached the gate leg. Last observed: "AwaitingSublot"

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260908T030937070Z` |
| agvId | `AGV-L2-001` |
| controlServerCommit | `37ae1e4da337ddf7f5483a6322dfa64d92796f85` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260908T030937070Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 受理后建出 TO_PICKUP 单并确认 | PASS | `CONFIRMED` | `CONFIRMED` |
| 候选判定结果是 ACCEPTED | PASS | `ACCEPTED` | `ACCEPTED` |
| 车在路上时不采信到站 | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
