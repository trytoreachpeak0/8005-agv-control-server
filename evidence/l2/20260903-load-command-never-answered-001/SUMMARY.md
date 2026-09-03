# L2 场景证据：load-command-never-answered

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260903T150945386Z` |
| agvId | `AGV-L2-001` |
| controlServerCommit | `62767f484ef02df31d1faf8be15791b9db1db242` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260903T150945386Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 装载指令已下发，旅程在等结果 | PASS | `AwaitingLoadResult` | `AwaitingLoadResult` |
| 旅程仍停在 AwaitingLoadResult（没有进 Blocked） | PASS | `AwaitingLoadResult` | `AwaitingLoadResult` |
| 没有阻塞原因码（Blocked 与「没有结果」是两种状态） | PASS | `(none)` | `(none)` |
| 装载操作停在 Prepared（不是 RecoveryRequired，也不是 Committed） | PASS | `Prepared` | `Prepared` |
| 需求仍是 Accepted（既没成功，也没转恢复） | PASS | `Accepted` | `Accepted` |
| 未结的装载指令在被持续重放（不是发一次就忘） | FAIL | `> 0` | `0` |
| 重放用的是同一条命令身份（对端只有一条待答请求） | PASS | `operation:d87b219b-d57a-1d54-8a2a-05bb3f1813a8` | `operation:d87b219b-d57a-1d54-8a2a-05bb3f1813a8` |
| 没有装载结果就不建 TO_GATE 单 | PASS | `(none)` | `(none)` |
| 没有为关卡段派过车（RIoT 单仍只有一条） | PASS | `1` | `1` |
| 没有下发出发前安全检查 | PASS | `0` | `0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
