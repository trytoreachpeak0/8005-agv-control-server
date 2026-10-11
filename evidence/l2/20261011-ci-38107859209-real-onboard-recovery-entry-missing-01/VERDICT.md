# cs#250：real-onboard-recovery-entry-missing 今日实跑定论

结论：**红，且红在别处**（票面三种结果里的第三种）。五条判据一条都没走到，`SUMMARY.md` 的判据表因此是空的。

| 项 | 值 |
| --- | --- |
| CI run | `38107859209`（`l2.yml`，`rig=real`，`win11-01-control-server-desktop`） |
| 服务端 | `f72853140e66197e010149df1ad17b1dfbc4e4ff`（本票分支，只是把该场景临时放进 `real-rig` 清单） |
| 车载端 | `8bdee644b23a6eaf71864308d73f72c3fb5a0e45`（`origin/w2g/fp-v2-impl` 顶端） |
| 模拟器 | `fb5f7c593742bf98bc3957b8729a38aad5321f28`（`origin/main` 顶端，与 2026-09-13 那次同一提交） |

## 发生了什么（`timeline.jsonl`、`logs/onboard-app/agv-20261011.log`）

1. 11:21:44 车载端进入 `WAITING_OPERATOR`，场景关上 1 号仓空门，开始等车载端 120 秒操作员超时后报结果。
2. 11:21:47 车载端第二次向 1 号仓发开锁脉冲，锁 DI 1→0：**它自己把门重开了**，此后一直等操作员，不报结果。
3. 11:25:44 场景等「旅程因不完整的装载结果停摆」超时：

   ```
   Run failed: Timed out after 240s waiting for: the journey blocked on the incomplete load result. Last observed: "AwaitingLoadResult"
   ```

   意思是 240 秒内旅程一直停在等装载结果的阶段，从没收到结果。

## 为什么

车载端 onboard-hmi#72（`b85b97a`，2026-09-16）起「相反态自动重开、只有 `UNKNOWN` 进恢复」，onboard-hmi#78（`96dffa0`，
2026-09-18）起期限过后仍重开，依据 ADR-cross-0058 决策 1、5 的 2026-09-18 改写。这是设计，清单内场景
`real-onboard-load-door-closed-empty-reopens` 判的就是它。所以这条场景「关门不放货、等超时」的造故障手法在今天的车载端上
造不出失败结果。2026-09-13 那次绿（`evidence/l2/20260913-b2close-real-onboard-recovery-entry-missing-001`）用的车载端
`c86bac5` 早于 onboard-hmi#72，两次结果不矛盾。

旧根因（停摆后恢复入口不出现，2026-09-04 已修）这次没有被测到，既不能说它回来了，也不能说它没回来；09-13 那次是它最后一次实测。

## 目录说明

- 由 CI artifact `real-rig-evidence` 下载，`_stage/` 未入库。
- `logs/control-server.out.log`（5.5 MB，57023 行）未入库，只留 `logs/control-server.out.excerpt.log`：按
  `warn|fail|error|34a24071|LoadResult|RecoveryRequired|OperationResult`（不分大小写）筛出的前 400 行，行首是原行号。
- `round-SUMMARY.md`、`commits.json`、`real-onboard-recovery-entry-missing-01.log` 是这一轮的汇总、内存采样与控制台输出。
