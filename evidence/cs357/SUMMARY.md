# control-server#357 证据：入站与引擎各写旅程行，引擎拿旧行保存

每条标了「读到的」（跑出来的）还是「推的」。基点 `fp/v2-impl@2ded1b38`。

## red/

- `00-census-ungated-writes-run-36314118281.txt`：第一步普查。临时埋点分支 `probe/357-journey-write-census`（不合入）在
  `SaveChanges` 与命令拦截器里记下对旅程族六张表的每一种写法，CI 全量 run `36314118281`（success）得到 503 种写法；
  这里是不在锁内、不在引擎一轮内的那几组。结论见票面评论 issuecomment-5855371856（读到的）。
- `01-lost-update-l1-at-e79d9c27.txt`：修前红，提交 `e79d9c27`（只动测试）。两条都红在预期那一处（读到的）：
  - 事故那一格：`Expected (Completed, "CANCELLED_BY_OPERATOR", 2026-08-26T01:00:10+00:00)`，`Actual (Completed, null, null)`——
    终结原因与它的起始时刻都被写空。
  - 最坏那一格：`Expected (Blocked, "LoadCancellationResult_NOT_RECONCILED")`，`Actual (AwaitingDepartureSafety, …)`——
    入站刚判定要人工恢复的旅程被引擎推回离站流程。同一用例在修前再往下跑（车答 SAFE），引擎建了 `GATE-1` 订单（读到的，
    当时的观察段没有提交，输出记在票面评论里）；继续跑到关卡卸货，整轮没有抛，旅程正常收尾、需求记为 `Succeeded`。
  - 同一次运行里另外 5 条通过的是 `IntegrationSliceTraitArchitectureTests`（过滤条件里一并带上的）。
  - 修前那一版的注入是原始 SQL，没有 `Version`（那时还没有这一列）；修复提交里注入补上 `Version = Version + 1`，
    因为真实的入站写经 `SaveChanges`、钩子一定递增它。补了之后的判别力由下面的反向验证撑着，不由这份修前红撑着。

## green/

- `01-zero-change-pins-vs-integration-2ded1b38.txt`：零变化基线。先算出该是多少：十份终结基线各一行旅程，应有 10 处
  `|Version=<set>`；实数 10 处。删掉之后十四份基线与集成分支 `2ded1b38` 上的旧基线逐字相同，不同 0 份（读到的）。
  这一列在集成分支上不存在，所以只能在本票分支上录；撑着它的是与集成分支旧基线的逐字比对。
- `02-reverse-validation-m1-m6.txt`：反向验证，每个变异都先确认替换恰好命中 1 处、`0 Error(s)`，用 `--no-incremental` 重编，
  跑完用备份还原（读到的）：

  | 变异 | 红了什么 | 为什么只红这些 |
  | --- | --- | --- |
  | M1 去掉守护（不设 `GuardedJourneyId`） | 只有 `ABlockCommittedBeforeTheGateLegIsCreatedStopsTheOrder` | 其余几格里引擎本来就改了旅程行，令牌本身就核得到；只有「建单前那次保存只存订单意图」这一格靠守护 |
  | M2 去掉令牌 | 8 条依赖冲突的用例全红 | 迁移测试里只有「旧行保存被拒」那条依赖令牌，其余看列与数据 |
  | M3 冲突即抛（整轮中止） | 多车那条，另加 4 条引擎用例 | 那 4 条直接调 `ExecuteOnceAsync`，异常从整轮抛出、用例没接住，红在抛出上 |
  | M4 释放服务不处理冲突 | 只有释放那条 | — |
  | M5 故障恢复不处理冲突 | 只有故障恢复那条 | — |
  | M6 在释放服务里加一条绕开钩子的 `ExecuteUpdate` | 只有 `NothingOutsideTheNamedPlacesWritesTheJourneyTableAroundTheSaveHook` | 校准那条不依赖源码 |

- `03-cs342-model-300-*.txt`：cs#342 断线重连模型，同一组 300 个固定种子，本票分支与基点 `2ded1b38` 各跑一遍（读到的）。
  两边逐项相同：录入请求 300/300 送达、确认冲突 0、回退 0、握手接受语义不同的消息 0；「带等人码的轮次推进失败」两边都是
  `ORDER_HANG` 5 次，所以是基点就有的。每轮耗时本票 19.4 ms、基点 13.8 ms：本票那次运行时本机正在检出基点 worktree、磁盘很忙，
  负载没控制，不据此下结论（推的）。模型测不到取消与故障动作，本票的两格按构造走不到；它在这里只证明没改坏断线重连那一块。
