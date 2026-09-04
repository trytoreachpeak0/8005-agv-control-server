# L2 场景证据：load-result-requires-recovery

结论：**FAIL**

失败原因：Timed out after 120s waiting for: the journey blocked on the incomplete load result. Last observed: "AwaitingLoadResult"

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260904T034647380Z` |
| agvId | `AGV-L2-001` |
| controlServerCommit | `64e9bcca8e8212d6c39cb9b21b18ab41194d7462` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260904T034647380Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 装载指令已下发，旅程在等结果 | PASS | `AwaitingLoadResult` | `AwaitingLoadResult` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
