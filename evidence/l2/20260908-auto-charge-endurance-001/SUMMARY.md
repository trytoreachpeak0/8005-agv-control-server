# L2 场景证据：auto-charge-endurance

结论：**FAIL**

失败原因：Timed out after 90s waiting for: demand L2-SUBLOT-20260907T181505515Z-A was accepted and dispatched to the pickup station. Last observed: (nothing)

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260907T181505515Z` |
| agvId | `AGV-L2-001` |
| controlServerCommit | `2b62ebc8ca03a99ea7ac05b2b1ee7dcdaed07f03` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260907T181505515Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 地图上有一个 211 号充电桩 | PASS | `station 211` | `12,210,211` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
