# 批次7-07（control-server#212）的红绿证据

装货阶段状态机、按侧判满、持货等单与持货超时。单元测试（L1）与合成装置 L2 两层，都在这个目录里。
每份 L2 证据只留 `SUMMARY.md` 与 `assertions.json`，完整目录（约 4 MB，大半是数据库快照）没有入库，做法与
`evidence/b7-06/red/13-*` 相同。

## 基准

| 项 | 值 |
| --- | --- |
| 集成分支顶端 | `fp/v2-impl@1e59e21b`（开 PR 前 fetch 核对过，没有前移） |
| 产品代码与测试 | `ac24df1c`；此后到最终提交 `724d6a3d`，`src/` 与 `tests/` 的差异是 0 行 |
| L2 绿证据 | 五条全部在最终提交 `724d6a3d` 上跑 |
| 持货超时 | `cargo-holding-timeout` 与 `cargo-holding-disabled-when-append-forbidden` 用 `00:00:40`（票面值）；另三条用 `00:10:00`，理由写在各自 setup 文件里 |

## 绿

| 文件 | 它证的是什么 |
| --- | --- |
| `green/01-zero-change-pin-vs-integration-tip.txt` | `ZeroChangePins/unload.txt` 与集成分支顶端逐列对照。227 列里只变了装货阶段三列：`CargoHoldingStartedAt`、`LoadingPhaseState`、`LoadingClosedReason` 由 `NULL` 变为落库值。持货不适用时这三列照样写（装货落定记起算点，离站前落 `CLOSED/PLANNED_LOADING_COMPLETE`），但不多发快照——发给车的内容由 L1 `WithoutHoldingEverySnapshotIsTheOneTheStageDerivedMappingGave` 逐字对照批次7-07 之前的映射。 |
| `green/l2-cargo-holding-side-full/` | 10/10。两侧各以一种方式满；快照整串恰好是 `LOADING CARGO_HOLDING_WAIT LOADING CARGO_HOLDING_WAIT VEHICLE_FULL CLOSED/VEHICLE_FULL`；两个停靠的 WAIT 快照期限相同。 |
| `green/l2-cargo-holding-timeout/` | 6/6。起算后 30 秒车仍在站上；期限后约 1 秒关闭；关闭后的需求判 `LOADING_PHASE_CLOSED`。 |
| `green/l2-cargo-holding-disabled-when-append-forbidden/` | 8/8。上限 0 与未配置各一趟：装完到离站 10～11 秒（只有站点等待），每趟只发 `LOADING` 与 `CLOSED/PLANNED_LOADING_COMPLETE` 两张快照，都不带期限。 |
| `green/l2-vehicle-full-ignores-oversized-and-gated/` | 5/5。超大单两条、暂停之下一条，都不把 REAR 算满。 |
| `green/l2-vehicle-full-still-appends-before-departure/` | 6/6。整车满之后需求戊照样追加，这段时间装货阶段一直是 `VEHICLE_FULL`。 |

## 红：L1（8 份，最终代码上重跑）

每份都是一处注入：按备份还原（不用 `git checkout --`），文件头是对备份做的 diff，`0 Error(s)` 证明测试跑的是注入后的二进制。

| 文件 | 注入 | 红的用例 |
| --- | --- | --- |
| `l1-01-station-deadline-leaves-vehicle-stuck.txt` | 去掉站点期限结束第二个取货停靠后的 `SetStage(AwaitingStationDeparture)` | `AfterTheStationDeadlineEndsASecondPickupTheVehicleLeavesWithWhatItCarries`：车卡在 `AwaitingSublot`（批次7-06 缺陷的复现） |
| `l1-02-plan-resend-over-18-not-replayed.txt` | 重放计划改回只认 1..18 号修订 | `AReSentPlanIsReplayedAfterAReconnectWhateverItsRevision`（批次7-06 缺陷的复现） |
| `l1-03-no-hold-at-last-pickup.txt` | 离站不看 `CARGO_HOLDING_WAIT` | 6 条：持货用例，加「只有本车货物才能判满」那组参数化用例的 5 行——它们都断言没满的车不离站。第一次注入时那组用例还没写，所以当时只红 1 条 |
| `l1-04-announce-the-silent-close.txt` | 不持货时的 `LOADING→CLOSED/PLANNED` 也发快照 | 6 条：不持货快照对照、三条线上逐字对照、状态机的两行 |
| `l1-05-holding-clock-restarts-each-batch.txt` | 每次装货落定都重置起算点 | `AnAppendWhileHoldingLoadsItAndTheDeadlineWaitsForThatBatchToClose` |
| `l1-06-deadline-interrupts-a-running-batch.txt` | 到期不等正在执行的装货批次 | 3 条：同上一条，加状态机两行 |
| `l1-07-state-saved-apart-from-its-snapshot.txt` | 状态先单独保存、再发快照 | `WhenTheSnapshotCannotBeStoredTheStateIsNotStoredEither`：发件箱写失败时状态已经是 WAIT |
| `l1-08-crash-between-the-two-load-saves-loops.txt` | 去掉续跑路径 | 同上一条，`System.IO.InvalidDataException`——写发件箱失败的那一轮正好落在两次保存之间（批次7-06 缺陷的复现） |

## 红：L2（6 份，外加一份「场景看不出来」）

| 目录 | 注入 | 红的判据 |
| --- | --- | --- |
| `l2-red-1-one-side-full-is-vehicle-full/` | 有一侧满就算整车满 | 5 条，从 `L2-CHS-01` 起：车在去第一站的路上就判满（FRONT 被需求甲预留满），此后一路连带 |
| `l2-red-2-any-refusal-marks-side/` | 读口把任何装不下都算占侧 | `L2-VFI-02`、`L2-VFI-03`。`L2-VFI-05`（暂停之下的小单）**仍绿**：暂停判据（顺序 20）排在区域归属之前，被暂停挡下的候选没有「侧」，读口按侧归类时它不在任何一侧。那一步靠的是判据顺序，不是原因码过滤；场景注释照此写明 |
| `l2-red-3-departure-ignores-holding/` | 离站不看持货 | `L2-CHT-02`（起算后 30 秒车已离站）、`L2-CHT-03`（关闭理由成了 `PLANNED_LOADING_COMPLETE`） |
| `l2-red-4-no-loading-phase-open-criterion/` | 去掉「装货阶段已关闭」判据 | `L2-CHT-05`：需求戊的理由成了 `ELIGIBLE`。它仍没进这趟旅程，是写入事务里那道 CLOSED 检查挡住的——两道防线里去掉一道，另一道还在，而 L2 看得出少了哪一道 |
| `l2-red-5-holding-ignores-zone-limit/` | 持货是否适用不看每区上限 | 两趟各 3 条：车持货到超时（40 秒）才走，快照带期限 |
| `l2-red-6-full-refuses-appends/` | 整车满也拒绝追加 | `L2-VFA-05`、`L2-VFA-06`：判满后车退回 `CARGO_HOLDING_WAIT` 再接单 |
| `l2-red-6a-first-version-scenario-blind/` | 同上，场景是第一版 | **全绿**，这正是留它的理由。「满了就不接」让需求丁下一轮被判 `LOADING_PHASE_CLOSED` 而不再是「本车货物占侧」，REAR 不再算满，车退回 WAIT，戊就在退回那一轮进来了。第一版只断言「戊进来了」，看不出这种翻转；补上的两条判据就是上一行那两条 |

## 作者期的失败（没有入库）

写场景时有两次失败出自场景脚本本身，不是产品：第一版「开到当前停靠」没等当前停靠换站，把第一站又开了一遍；`L2-VFA-06` 把空数组经 `if` 赋值，展开成 `$null` 后严格模式抛（`724d6a3d` 修）。它们只说明脚本写错过，不说明产品的任何事，所以留在工作区外。
