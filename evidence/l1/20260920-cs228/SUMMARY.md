# cs#228 L1 证据：准入被撤的等待起点独立持久

票：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/228 。每个文件开头记着跑在哪个提交上、工作树是否干净、用的过滤条件；除 05 外工作树都干净。

| 文件 | 提交 | 结果 | 说明 |
| --- | --- | --- | --- |
| `01-tests-before-implementation-red-083f9b19.txt` | `083f9b19`（只有测试） | 8 红 / 12 绿 | 票面两条先红：ORDER_FAILED 打断后从 01:06 重计；心跳过期时停在 `AwaitingGateArrival`。其余 6 条红在新列不存在 |
| `02-implementation-green-4c4c1aec.txt` | `4c4c1aec`（实现） | 21 绿 | |
| `03-crash-point-red-12455451.txt` | `12455451`（崩溃点测试） | 1 红 | 准入已恢复、卸货已备好、阶段保存丢失后重启，实现初版把它升级成 `Blocked` |
| `04-crash-point-green-35c1a15f.txt` | `35c1a15f`（修复） | 104 绿 | 加跑全部迁移纪律测试、切片台账架构测试与 `BlockedJourneyDashboardTests` |
| `05-escalation-after-arrival-check-injected-fault.txt` | 注入故障（未提交） | 2 红 | 把升级判断挪回到站检查之后，心跳过期与「到点那一轮订单同时 FAILED」两条变红，说明它们钉的是判断顺序 |

票面两条的红原文（`01`）：

```
Failed ControlServer.Tests.ReversedDirectionJourneyRuntimeTests.AnOrderFailureInTheMiddleOfTheWaitDoesNotRestartTheCount
Expected: Tuple (Blocked, "TASK_TYPE_NOT_ALLOWED_AT_STATION_TIMEOUT", 2026-08-26T01:00:00.0000000+00:00)
Actual:   Tuple (AwaitingGateArrival, "TASK_TYPE_NOT_ALLOWED_AT_STATION", 2026-08-26T01:06:00.0000000+00:00)

Failed ControlServer.Tests.ReversedDirectionJourneyRuntimeTests.AHeldStopWhoseOnboardStopsHeartbeatingIsStillEscalatedOnTime
Expected: Tuple (Blocked, "TASK_TYPE_NOT_ALLOWED_AT_STATION_TIMEOUT", 2026-08-26T01:00:00.0000000+00:00)
Actual:   Tuple (AwaitingGateArrival, "TASK_TYPE_NOT_ALLOWED_AT_STATION", 2026-08-26T01:00:00.0000000+00:00)
```

第一条：01:00 第一次停住，01:03 订单报 FAILED，01:06 回到停住时起点被重计成 01:06，到 01:10 不升级。第二条：心跳停了、会话仍是 `Ready`，
到站检查每轮不可信就返回，01:10 仍停在原地。
