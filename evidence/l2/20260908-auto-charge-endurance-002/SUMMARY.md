# L2 场景证据：auto-charge-endurance

结论：**FAIL**

失败原因：Timed out after 90s waiting for: the charger arrival was trusted. Last observed: {"ChargingRunId":"e32c3b7a-d135-e15f-ba14-7356c1822a68","VehicleKey":"BROKERX-L2-0001","AgvId":"AGV-L2-001","Stage":"AwaitingChargerArrival","ChargerStationId":"充电桩","ChargerStationRiotId":211,"MovementLegId":"860fc2ce-3fa1-4754-9392-ad47452566af","UpperId":"W2G-CHARGE-e32c3b7a-d135-e15f-ba14-7356c1822a68-1","TriggeredAtBatteryPercent":15,"ReleasedAtBatteryPercent":null,"BlockReasonCode":null,"CreatedAt":"2026-09-07 18:18:11.0036053+00:00","UpdatedAt":"2026-09-07 18:18:11.068169+00:00"}

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260907T181757572Z` |
| agvId | `AGV-L2-001` |
| controlServerCommit | `2b62ebc8ca03a99ea7ac05b2b1ee7dcdaed07f03` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260907T181757572Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 地图上有一个 211 号充电桩 | PASS | `station 211` | `11,12,210,211` |
| 第一单在电量充足时正常走完 | PASS | `Completed` | `Completed` |
| 第一单终态为 Succeeded | PASS | `Succeeded` | `Succeeded` |
| 电量充足时不会没事跑去充电 | PASS | `0` | `0` |
| 低电触发一趟去充电桩的行程 | PASS | `AwaitingChargerArrival / 211 / 15` | `AwaitingChargerArrival / 211 / 15` |
| 充电行程建出并确认了一条 TO_CHARGER 单 | FAIL | `CONFIRMED / 211` | `CREATE_ATTEMPTED / 211` |
| 充电不产生旅程，也不占用需求 | PASS | `1` | `1` |
| 去充电桩的路上不会重复派单 | PASS | `1` | `1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
