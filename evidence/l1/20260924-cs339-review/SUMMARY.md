# control-server#339 审查修改（PR #353，2026-09-24）

审查 M1：旧的一版清单未确认时，退役那一步自己先保存，把升版次数（`WorklistRefills`）一起带进库，新的一版却还没入队；崩在两次保存之间，
此后按新的号在发件箱里找不到排给车的那一版，再也不升版，车停在旧期限。同一形状往后挪一格：新一版清单落库、跟着它的录入请求还没入队时崩了，
下一轮认不出任何事，而车在会话离开 `Ready` 时已清掉手上的录入请求，操作员没有可答的请求。

## 红

| 文件 | 提交 | 结果 |
| --- | --- | --- |
| `red-tests-on-fab1dd0e-plus-tests.txt` | `5179c6ad`（`fab1dd0e` 加四条新用例，产品代码未动） | 13 条里 2 条红：`ARefillOverAnUnconfirmedWorklistLandsInOneSaveAndSurvivesACrashAtIt`（崩溃之后 `WorklistRefills` 期望 0、实际 1），`ACrashBetweenTheRefilledWorklistAndItsEntryRequestIsHealedOnTheNextRound`（第一版断言，按发出行数判） |
| `red-tests-on-a0a36edf.txt` | `a0a36edf`（补发那条改为按 messageId 判之后，产品代码仍未动） | 同样 2 条红：前者同上；后者在补发之后车手上那一版清单没有对应的录入请求（`AssertTheLastEntryRequestAnswersTheHeldWorklist`，期望 2、实际 1） |

新加的另两条（S2 `TwoDisconnectsWithNoConfirmationBetweenThemEachReachTheVehicle`、S3 `ARefillSavedThroughTheOpenCancellationExitStillReachesTheVehicleOnceItCloses`）
守的是修前就成立的行为，修前修后都绿；它们的判别力由下面的反向验证证明。

## 反向验证（`reverse-verification/`）

在 `210ac78a`（修复之后）上用 `mutate.py` 每次改产品代码一处（先断言恰好命中一次，`matches=[1]` 写在每个文件第一行），构建，跑
`RefilledStationDeadlineReachesVehicleTests` 与 `ArrivalPublishInterruptedThenReconnectedTests` 两个类（34 条），跑完按原字节还原并更新修改时间。
每个文件留 diff、失败的用例、断言与行号。

| 变异 | 改了什么 | 预期只红 | 实际 |
| --- | --- | --- | --- |
| `M1-worklist-retire-saves-on-its-own` | 清单上一版的退役改回自己保存（审查所说的形状） | M1 那条 | 只红它，第 211 行：崩溃之后 `WorklistRefills` 期望 0、实际 1 |
| `H-no-entry-request-heal` | 「这一版清单已排、录入请求没有」恒为假 | 补发那条 | 只红它，第 725 行：录入请求的号期望 2、实际 1 |
| `S3-reissue-only-in-the-refill-round` | 等录入那一处只在「这一轮重填了」时升版（审查 S3 说的被拒判据） | S3 那条 | 只红它，第 714 行：车上 `01:00:30`（到站那一个），服务端 `01:00:37` |
| `S2-one-refill-per-stop` | 一个停靠只许重填升版一次 | S2 那条 | 只红它，第 714 行：车上 `01:00:37`（第一次重填），服务端 `01:00:44`（第二次） |

「为什么别的不红」：M1 变异只在旧一版未确认时才有区别，其余用例都先送了确认；补发只在两次保存之间崩了才有事可做，只有那一条注入了那个崩溃；
S3 变异在其余用例里不改变结果，因为它们的升版都发生在重填那一轮；S2 变异只在同一停靠第二次重填时起作用。

录入请求那一侧的退役没有改成只打标记：崩在它与新一张入队之间，留下的正是补发那条判据接得住的样子，改了也没有用例能区分，所以不动。
