# cs#753 证据

`g3-load-cancellation-before-load` 不在 `l2.yml` 的真装置场景列表里，CI `rig=real` 跑不了，所以在笔记本本机真装置上跑（调度 10-17 放行本机时段）。三端都显式传：控制端 `7b302c9c`（本分支），模拟器 `fb5f7c59`。

| 目录 | 车载端 | 结论 |
| --- | --- | --- |
| `l2-local-green/` | `e60e70e9`（hmi#363 最终 head，机台每行有「取消」） | PASS，6/6 |
| `l2-local-red/` | `adc1a28f`（车载端集成分支顶端，不含 hmi#363） | FAIL，红在 `G3-02-31`：行内「取消」不可按、底部「取消装货」还在；`G3-02-32`～`36` 判「未到达」 |

只留 `SUMMARY.md` 与 `assertions.json`；日志、快照、时间线留在本机 `F:/8005-scratch/cs753-l2/`。

`real-onboard-stale-stop-after-station-timeout` 的绿轮与取红轮在 CI 真装置上跑，run 号写在 PR #756 正文。
