# control-server#820 证据

全部在 vm-win-01 上（16 vCPU，动态内存 8～32 GB；2026-10-21 01:18 CST 起 Hyper-V CPU 保留值 50%）。时间均为 UTC，2026-10-20。

| 文件 | 内容 |
| --- | --- |
| `parallel-safety-inventory.md` | 第 1 步只读盘点：test.yml 与 l2.yml 里两份同类作业会共用的东西，逐项判定；L2 一实例八路与两实例分槽段的比较 |
| `queue/before-burst-1545-1630.txt` | 改前：10-21 00:40 CST 那次扎堆（15:45～16:30 创建的 test、l2 run），每个作业的排队与运行分钟数 |
| `queue/after-burst-1913.txt` | 改后：19:13～19:14 同时派 2 轮 L2、2 轮 test 与 1 轮真装置 |
| `queue/jobs-1600-1730.txt`、`queue/jobs-1719-1820.txt` | 新机上所有作业的起止：第二个 cs-test 实例加入前后、加入后第一个小时 |
| `samples/commit-cpu-*.csv` | 从宿主每 30 秒经 ssh 读的整机已提交内存、提交上限、客户机可见内存、CPU（5 秒平均）、Runner.Worker 个数 |
| `m1/red/`、`m1/green/` | 审查 M1：`Test-G3RunnerPorts.ps1` 两份同时起、各钉 CPU 0～3、连跑 3 轮（驱动脚本 `m1/Invoke-ConcurrentSelfCheck.ps1`）。红在 `13103046f`（合入 #819 后、未修），每轮恰好一份 exit=1；绿在加锁之后，3 轮 6 份全 exit=0，每轮第二份打出 `SELF_CHECK_LOCK_WAITING` |
| `l2-red/` | 扎堆第二轮 L2（run 39481673510）唯一的红：`charging-no-progress-isolates` 的判据结论原文 |

## 读法

- 排队分钟数＝作业开始减 run 创建；同一 run 的重跑（attempt 2）按原创建时刻算，偏大，不作对照。
- `queue/after-burst-1913.txt` 里第二轮 L2 排队 24.3 分钟是有意的：L2 只有一个 runner，第二轮等第一轮。
- 扎堆期间 #822 的 L2（旧 l2.yml，`headless`，槽 1～4）同时在跑，三轮在槽 1～4 上逐场景排队等锁（`L2_PORT_LOCK_WAITING` 19、49、70 次，无 `L2_PORT_LOCK_BUSY`），所以这两轮 L2 比单独跑的 773 秒慢；5～8 路未受影响。见 PR 正文。
- `l2-red/` 的红是判据时序：先等到“无进展已确认”，再单独读旅程阻断原因，中间清桩那一轮已把原因改成 `CHARGING_CLEARING_VEHICLE_STILL_CHARGING`。不属于本票，已报调度。
