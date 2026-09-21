# 批次7-08（control-server#213）的红绿证据

让站：别的车被承诺以某站为下一停靠时，停在该站持货等单的车结束等单，而阻断离站的状态不被打断。单元测试（L1）与合成装置 L2
两层都在这个目录里。每份 L2 证据只留 `SUMMARY.md` 与 `assertions.json`（红的另加 `INJECTION.diff`），完整目录没有入库，
做法与 `evidence/b7-07/` 相同。

## 基准

| 项 | 值 |
| --- | --- |
| 集成分支 | 本分支从 `fp/v2-impl@0b19397b` 切出；推送前 fetch，顶端没动 |
| 代码提交 | `5a772043` |
| L2 证据 | 四条绿、两份红全部在 `5a772043` 上跑（`SUMMARY.md` 的 `controlServerCommit`） |
| L1 红证据 12 份与相关子集 | 跑在 `4ee28dc1` 的内容上（跑时还没提交）。`4ee28dc1` → `5a772043` 只改了两份 L2 场景脚本，`src/` 与 `tests/` 零差异（`git diff --stat` 为空），所以对最终提交同样成立 |
| 全量测试 | 走 CI 的 `test` 作业，不在本机跑 |
| 持货超时 | 两条场景都用 `00:10:00`，远长于场景本身：装货阶段关了，原因只能是让站 |

## 绿

| 文件 | 它证的是什么 |
| --- | --- |
| `green/01-grep-loading-phase-expectations.txt` | 票面要贴的 `git grep`：G3 runner 对 `loadingPhase`、`closedReason`、车辆业务状态修订号**零命中**；命中的只有批次7-07 的单车持货场景（没有第二辆车，按构造触发不了让站）与 `Test-L2DispatchZoneParameters.ps1:105` 那份「以后票的 setup 键」清单。没有期待需要改，不需要跑 G3 |
| `green/02-l1-related-subset.txt` | 本机相关子集 866/866：批次 7 全部、装货阶段、零变化基线、线上逐字对照、派车、多车、推进段、架构守卫 |
| `green/l2-waiting-station-yield/` | 6/6。主车在 12 号站 `CARGO_HOLDING_WAIT`；需求乙只能给另一台车，它的下一停靠是 12 号站；主车 `CLOSED/WAITING_STATION_YIELD`，触发列记的是另一台车、时刻等于乙的受理时刻；车上收到那张快照；主车开向关卡；让站之后发的需求戊没进主车那一趟（进了另一台车） |
| `green/l2-waiting-station-yield-waits-for-door-{1,2,3}/` | 各 6/6，同一提交连跑三次（第一版在这里一绿一红过，见文末）。主车满了、装货命令挂着、门开着时被让站，快照当场是 `WAITING_STATION_YIELD`；又转 4 轮没有离站核验、没有关卡单；放行离站的那一次核验请求与关卡单的订单意图都晚于服务端收到「门已关」 |

## 红：L1（12 份）

每份是一处注入：从备份还原（不用 `git checkout --`），文件头是注入的 diff，`0 Error(s)` 证明测试跑的是注入后的二进制；每个锚点
注入前核过在源码里恰好命中一次。过滤器是 `Batch7StationYield*`、`LoadingPhaseMachineTests`、`DispatchVehicleOrderingTests`
三类，共 108 条。

| 文件 | 注入 | 红的用例 |
| --- | --- | --- |
| `l1-01-no-trigger-in-the-acceptance.txt` | 受理事务不写触发 | 14 条：存储层受理正例 8 格与「第一个触发者」；推进段 5 条——补判会在下一轮补上让站，所以它们红在「触发时刻等于受理时刻」上，31 分钟那一格红在触发者为空（期限已过，补判不补） |
| `l1-02-no-trigger-in-the-append.txt` | 追加事务不写触发 | `AnAppendThatMakesTheStationAnotherVehiclesNextStopMakesTheHolderYield` |
| `l1-03-no-trigger-on-departure.txt` | 离站那次保存不写触发 | 推进一路的两条（正例、崩溃点） |
| `l1-04-no-catch-up-for-a-late-holder.txt` | 去掉被让方的补判 | `AVehicleThatStartsHoldingAfterAnotherWasCommittedToItsStationYieldsWithoutWaiting` |
| `l1-05-the-machine-ignores-the-yield.txt` | 状态机不看触发（票面 L2 红证据的形状） | 16 条：推进段 10 条，状态机表与先后各 3 行 |
| `l1-06-a-yield-after-the-deadline-still-counts.txt` | 触发晚于持货期限也算让站 | 31 分钟那一格（期望 `CARGO_HOLDING_TIMEOUT`） |
| `l1-07-a-departing-full-vehicle-is-not-standing.txt` | 离站核验已发出不算站在停靠上 | 那两格受理正例，加「下一停靠」表的一行 |
| `l1-08-a-vehicles-own-plan-makes-it-yield.txt` | 去掉「不是本车」 | `AVehiclesOwnPlanNeverMakesItYield` |
| `l1-09-a-later-stop-counts-as-the-next.txt` | 站在停靠上时取最后一个而不是下一个 | 追加正例、「后续停靠不触发」、最后一个停靠 |
| `l1-10-trigger-saved-before-the-acceptance.txt` | 受理之前单独把触发存掉 | `AnAcceptanceThatFailsAtTheCommitLeavesNoTriggerBehind`（这条用例只让带受理行的那一次保存失败，否则这份注入它看不出来） |
| `l1-11-a-holding-vehicle-ranks-first.txt` | 成本层让有在途计划的车排前 | `HoldingAndYieldingChangeNoLayersVerdict` |
| `l1-12-corrections-read-for-the-anchor-only.txt` | 离站判定查纠错退回只看锚需求（本票一并修的缺陷） | `AnOpenCorrectionOnAnAppendedDemandHoldsTheDeparture` |

## 红：L2（2 份）

| 目录 | 注入 | 红的判据 |
| --- | --- | --- |
| `red/l2-red-1-holding-ignores-other-vehicles/` | 票面的形状：只在持货超时时结束等单、不看别的车 | `L2-WSY-03`～`06`：主车一直 `CARGO_HOLDING_WAIT`、没有让站快照、没有离站；让站之后的需求戊**追加进了主车** |
| `red/l2-red-2-yield-departs-at-once/` | 票面的形状：触发即发离站请求、不等收敛 | `L2-WSD-05`：门开着、装货未落定时就发了离站核验。`L2-WSD-06` 仍绿，而且应该绿：门开着时车载端答不出能用的「可以走」，服务端照旧不建关卡单；门关上时安全版本前移、旧核验过期、重新问——放行的那一次仍晚于关门。能让 06 红的是「不看车载端答复就走」，那是离站路本身坏了 |

## 作者期的失败（没有入库）

- **`waiting-station-yield-waits-for-door` 第一版让一辆空闲等单的车开着门**：触发列写上了，但车的会话随即离开 Ready（门开着又没有
  本车的仓位操作来解释，`WireToGateStore.IsUnsafetyExplainedByOwnCommandAsync`），推进段对它什么都不发、也不判装货阶段，让站快照要等
  门关上才发得出去。这是既有设计，不是缺陷；场景改成门由本车在执行的装货打开，setup 文件写了为什么。那一格由 L1 覆盖（断联那一例）。
- **`L2-WSY-06` 第一版把积压理由 `ELIGIBLE` 当成定论**，读得太早，red-1 里它照样绿（戊后来追加进了主车）。改成等受理或非 `ELIGIBLE`
  理由之后，red-1 里它红。
- **`L2-WSD-06` 第一版等到关卡单就读已消费答复**：关卡腿的订单意图由 `AuthorizeMovementAsync` 先单独保存，已消费答复是这一轮最后那次
  保存，读在两次之间就是「核验 (none)」——同一份脚本在 `b728e665` 上一绿一红（README 第 14 条的形状）。改成等已消费答复落库再读，
  最终提交上连跑三次全绿。

这三次只说明脚本写错过，不说明产品的任何事，所以留在工作区外。
