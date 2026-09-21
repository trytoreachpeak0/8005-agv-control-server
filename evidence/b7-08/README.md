# 批次7-08（control-server#213）的红绿证据

让站：别的车被承诺以某站为下一停靠时，停在该站持货等单的车结束等单，而阻断离站的状态不被打断。单元测试（L1）与合成装置 L2
两层都在这个目录里。每份 L2 证据只留 `SUMMARY.md` 与 `assertions.json`（红的另加 `INJECTION.diff`），完整目录没有入库，
做法与 `evidence/b7-07/` 相同。

这是审查返工之后的一整套，**全部在同一个代码提交上重取**：旧的一套跑在一个从没推送过的提交上，审查无法核对。

## 基准

| 项 | 值 |
| --- | --- |
| 集成分支 | 本分支从 `fp/v2-impl@0b19397b` 切出；返工推送前 fetch，顶端没动，没有 merge |
| 代码提交 | `c61009b85933d824289f1a06d4854a8e23651129`（这之后只有证据提交，`src/`、`tests/`、`scripts/` 不再变） |
| L1 红证据 15 份、相关子集 | 在 `c61009b8` 上跑，跑前 `git status -- src tests` 为空。注入脚本从备份还原文件，跑完 `src/`、`tests/` 零改动 |
| L2 证据 | 两条绿、三份红都在 `c61009b8` 上（各自 `SUMMARY.md` 的 `controlServerCommit`） |
| 全量测试 | 走 CI 的 `test` 作业；两条场景三连由 PR 正文的 `L2-Consecutive` 声明，走 CI |
| 持货超时 | 两条场景都用 `00:10:00`，远长于场景本身：装货阶段关了，原因只能是让站 |

## 绿

| 文件 | 它证的是什么 |
| --- | --- |
| `green/01-grep-loading-phase-expectations.txt` | 票面要贴的 `git grep`：G3 runner 对 `loadingPhase`、`closedReason`、车辆业务状态修订号**零命中**；命中的只有批次7-07 的单车持货场景（没有第二辆车，按构造触发不了让站）与 `Test-L2DispatchZoneParameters.ps1:105` 那份「以后票的 setup 键」清单。没有期待需要改，不需要跑 G3 |
| `green/02-l1-related-subset.txt` | 本机相关子集 870/870：批次 7 全部、装货阶段、零变化基线、线上逐字对照、派车、多车、推进段、架构守卫 |
| `green/l2-waiting-station-yield/` | 6/6。主车在 12 号站 `CARGO_HOLDING_WAIT`；需求乙只能给另一台车，它的下一停靠是 12 号站；主车 `CLOSED/WAITING_STATION_YIELD`，触发列记的是另一台车、时刻等于乙的受理时刻；车上收到那张快照；主车开向关卡；让站之后发的需求戊没进主车那一趟（进了另一台车） |
| `green/l2-waiting-station-yield-waits-for-door/` | 7/7。主车满了、装货命令挂着、门开着时被让站，快照当场是 `WAITING_STATION_YIELD`（03、04）；又转 4 轮没有离站核验（05）；**装货落定、门仍开着**，站点等待早已过去，仍没有关卡单（06）；门关上之后放行离站的那一次核验与关卡单的订单意图都晚于服务端收到「门已关」（07） |

## 红：L1（15 份，都在 `c61009b8` 上注入）

每份是一处注入：从备份还原（不用 `git checkout --`），文件头是注入的 diff，`0 Error(s)` 证明测试跑的是注入后的二进制；每个锚点
注入前核过在源码里恰好命中一次。过滤器是 `Batch7StationYield*`、`LoadingPhaseMachineTests`、`DispatchVehicleOrderingTests`
三类，共 112 条。12～15 号是把返工修掉的旧写法**原样放回去**，所以也是那几处修复的反向验证。

| 文件 | 注入 | 红的用例 |
| --- | --- | --- |
| `l1-01-no-trigger-in-the-acceptance.txt` | 受理事务不写触发 | 15 条：存储层受理正例 8 格与「第一个触发者」；推进段 6 条——补判会在下一轮补上让站，所以它们红在「触发时刻等于受理时刻」上，31 分钟那一格红在触发者为空（期限已过，补判不补），「受理之后立即追加」那一条红在追加被放行（没有触发可读） |
| `l1-02-no-trigger-in-the-append.txt` | 追加事务不写触发 | `AnAppendThatMakesTheStationAnotherVehiclesNextStopMakesTheHolderYield` |
| `l1-03-no-trigger-on-departure.txt` | 离站那次保存不写触发 | 推进一路的两条（正例、崩溃点） |
| `l1-04-no-catch-up-for-a-late-holder.txt` | 去掉被让方的补判 | `AVehicleThatStartsHoldingAfterAnotherWasCommittedToItsStationYieldsWithoutWaiting` |
| `l1-05-the-machine-ignores-the-yield.txt` | 状态机不看触发 | 16 条：推进段 10 条，状态机表与先后各 3 行 |
| `l1-06-a-yield-after-the-deadline-still-counts.txt` | 触发晚于持货期限也算让站 | 31 分钟那一格（期望 `CARGO_HOLDING_TIMEOUT`） |
| `l1-07-a-departing-full-vehicle-is-not-standing.txt` | 离站核验已发出不算站在停靠上 | 那两格受理正例，加「下一停靠」表的一行 |
| `l1-08-a-vehicles-own-plan-makes-it-yield.txt` | 去掉「不是本车」 | `AVehiclesOwnPlanNeverMakesItYield` |
| `l1-09-a-later-stop-counts-as-the-next.txt` | 站在停靠上时取最后一个而不是下一个 | 追加正例、「后续停靠不触发」、最后一个停靠 |
| `l1-10-trigger-saved-before-the-acceptance.txt` | 受理之前单独把触发存掉 | `AnAcceptanceThatFailsAtTheCommitLeavesNoTriggerBehind`（这条用例只让带受理行的那一次保存失败，否则这份注入它看不出来） |
| `l1-11-a-holding-vehicle-ranks-first.txt` | 成本层让有在途计划的车排前 | `HoldingAndYieldingChangeNoLayersVerdict` |
| `l1-12-corrections-read-for-the-anchor-only.txt` | 离站查纠错退回本票之前（`0b19397b`）：只看锚需求 | 4 条：追加需求上的纠错挡不住离站；另三条是反向——那个锚需求已卸、是别的车开的、开在加入之前，照样挡 |
| `l1-13-corrections-read-for-every-demand-of-the-journey.txt` | 退回 `5a772043`：本旅程全部需求，不分在不在车上、是谁开的 | 3 条：已卸需求上的纠错、别的旅程留下的两格 |
| `l1-14-an-append-reads-only-the-loading-phase-column.txt` | 追加事务不读触发列（`7cb0723c` 的写法，审查必修 1） | `AnAppendRightAfterTheTriggerIsRefusedBeforeTheHolderRunsAgain` |
| `l1-15-corrections-read-for-any-journey.txt` | 只看在车上的需求，但不分哪一趟旅程（`cca61c80` 的写法，审查必修 3） | 别的旅程留下的两格 |

`red/prefix/` 里是那三条新用例**写好、修复还没做**时在当时代码上的红：13 在 `5a772043` 的 `src/` 上，14、15 在 `cca61c80` 的
`src/` 上（用例本身是当时工作区里的，还没提交）。它们与上表 13～15 是同一件事的两个方向：先红后修，修完放回旧写法再红。

## 红：L2（3 份，都在 `c61009b8` 上）

| 目录 | 注入 | 红的判据 |
| --- | --- | --- |
| `red/l2-red-1-holding-ignores-other-vehicles/` | 票面的形状：状态机只在持货超时时结束等单、不看别的车 | `L2-WSY-03`～`05`：主车一直 `CARGO_HOLDING_WAIT`、没有让站快照、没有离站。`L2-WSY-06` 在这份注入下**仍绿，而且应该绿**：触发列照样写上了，追加事务读触发列本身（审查必修 1 加的那一道），戊被挡在追加事务外，去了另一台车。返工之前同一份注入下 06 是红的（戊追加进了主车），差别正是那一道 |
| `red/l2-red-2-yield-departs-at-once/` | 票面的形状：触发即发离站请求、不等收敛 | `L2-WSD-05`：门开着、装货未落定时就发了离站核验。06、07 仍绿：那一张核验车载端答不出能用的「可以走」，装货落定后门还开着、会话随之离开 Ready，服务端不建关卡单；门关上之后重新问，放行的那一次晚于关门 |
| `red/l2-red-3-departure-ignores-the-door/` | 拿掉离站对门的两层检查：会话就绪把「门开着」一律算作本车操作解释（`IsUnsafetyExplainedByOwnCommandAsync`），离站核验答复不看 `departureSafe` 与 `allTargetSlotsLocked` | `L2-WSD-06`：装货一落定、门还开着就建了关卡单；`L2-WSD-07`：放行离站的核验与订单意图都**早于**关门约 6 秒 |

门那一侧挡住离站的是两层，都是既有的离站路：装货在执行时开着的门由本车操作解释，会话仍 Ready，挡车的是离站核验答复；装货
落定之后门还开着，会话离开 Ready（`06` 的实际值里看得到 `ONBOARD_SESSION_NOT_READY`），推进段对这辆车什么都不做。只拿掉其中
一层，另一层照样挡着，所以 red-3 两层一起拿。

## 作者期与审查指出的失败（没有入库）

- **`waiting-station-yield-waits-for-door` 第一版让一辆空闲等单的车开着门**：触发列写上了，但车的会话随即离开 Ready，推进段不判
  它的装货阶段，让站快照要等门关上才发得出去。这是既有设计，不是缺陷；场景改成门由本车在执行的装货打开，setup 文件写了为什么。
- **`L2-WSY-06` 第一版把积压理由 `ELIGIBLE` 当成定论**，读得太早，当时的 red-1 里它照样绿。改成等受理或非 `ELIGIBLE` 理由。
- **等门场景的离站判据第一版等到关卡单就读已消费答复**，读在两次保存之间就是「核验 (none)」，一绿一红（README 第 14 条的形状）。
  改成等已消费答复落库再读。
- **审查指出（必修 2）：等门场景先关门、后放行装货，「离站晚于关门」按构造必然为真**——离站本来就要等装货落定，三次绿证据里
  核验都比关门晚约 11 秒，正是站点等待。改成先落定装货、门仍开着断言不走（06），再关门（07），并补 red-3。原来那句
  「能让它红的是不看车载端答复就走」不成立：只拿掉答复那一层，会话就绪那一层仍挡着。

这些只说明脚本写错过，不说明产品的任何事，所以留在工作区外。
