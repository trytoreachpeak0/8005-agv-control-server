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
- `04-cs362-recheck-clauses-after-cs357.txt`：cs#362 写锁内复核的四条，在本票代码上按 cs#362 的编号重做单删与组合删（读到的）。
  R5（取消）红 1 条、R23（需求+归属）红终结那 5 条，与 cs#362 当时相同；**R24（需求+阶段）不再红**——「证明不了空」那一格写了旅程行
  （Blocked），现在被版本冲突先挡。因此：
  - **需求**一条补了只写需求表、不碰旅程行的一格 `ADemandHeldForRecoveryWithoutItsJourneyBeingWrittenIsNotLoaded`，
    单删需求（R2）时恰好只红它（同一文件末尾）；前提断言写明挡住它的是复核（2190）而不是版本冲突（2191）。
  - **阶段**一条没有护栏了，写明是有意保留的纵深防御：阶段一变就是旅程行被写，版本必然跟着变；要让它单独起作用只能绕开保存钩子，
    那正是 `JourneyRowWriteArchitectureTests` 禁止的。
  - **归属**一条照旧只在与需求组合时有护栏（R23）；**取消**一条照旧单删就红（R5）。

## 独立审查之后（PR #370 issuecomment-5856084438）

- `red/02-review-fixes-at-73ea9f87.txt`：本轮新写的测试文件原样拷到上一轮 head `73ea9f87`（稀疏 worktree，用完即删）上跑，
  红 4 条、绿 14 条，红的正是四条必修的用例（读到的）：
  - 必修 1（两格）：`Actual: BusinessIdentityConflictException: Movement identity is already bound to different intent content.`——
    建单后的保存冲突，下一轮起每一轮都抛；
  - 必修 2：`Expected ("VEHICLE_WAITING_AT_CHECKPOINT", null)`，`Actual (null, null)`——A 让开时 B 的实例被一并解除跟踪，B 的码没落库；
  - 必修 4：追加没被拒（期望 `BusinessIdentityConflictException`）——事务里的复核读到的是本轮开头跟踪着的旧实例。
  补了前提断言（必修 3）的三条在旧 head 上照样绿，说明前提本来就成立，现在它们会在前提不成立时红。
- `green/05-review-fixes-reverse-validation.txt`：在修后代码上把每一处修复单独撤回（脚本 `scripts/mutate_multi.py`，
  带过滤范围、失败断言行号、`dotnet test` 退出码）：
  - R1a 意图按新的 now 生成：必修 1 两格红在重放那一轮抛出（332 行，`Assert.Null(thrown)` 那一次 await）；
  - R1b 建单后不撤守护：必修 1 两格红在「让开那一轮结束时关卡意图已记 CONFIRMED」（314 行）。这一条第一次跑是**存活**的——
    `ReconcileOrCreateAsync` 每次先向 RIoT 按单号对账，所以不会建第二张单；它丢的是建单之后那次记录（确认与派车审计），
    只有断言记录本身才看得见，于是补了 314 行那一句，再跑即红；
  - R2 让开时解除所有条目：必修 2 那条红（`MultiVehicleExecutionTests.JourneyCommit.cs:119`）；
  - R4 追加复核读回带跟踪：必修 4 那条红（`Batch7JourneyAppendPersistenceTests.cs:168`）。
- 前一轮的反向验证 `green/02` 用的脚本是 `scripts/mutate.py`（只打印失败用例名）。
- `green/06-cs342-model-300-final-5c5641a1.txt`：合入集成分支（cs#276）之后的 head `5c5641a1` 上，同一组 300 个种子，结果与基点逐项相同；
  每轮 11.8 ms（基点那次 13.8 ms），印证上一轮 19.4 ms 是当时本机磁盘忙造成的，不是本票的开销（读到的）。
