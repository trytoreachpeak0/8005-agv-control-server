# control-server#879 证据：旅程停靠序位重号的来源与修复

## 结论

11-05 agv02 那趟旅程（`journey:8f62304a-…`）的两个 5 号、缺 8 号，是 11-04 20:32:35 UTC（11-05 04:32:35 CST）同一时刻的两次写入叠出来的：

1. 车载端连接上，操作员取消了一条还没装的需求 bb32。计划修订（`JourneyPlanRevisionStage`）删掉它的卸货停靠 N6-11，把后面的停靠往前挪一位，提交。
2. 引擎这一轮的上下文（派车轮与引擎共用）在本轮早先已经带跟踪读过这趟旅程的停靠。派车轮按库里最新的计划算出追加 9974 的插位（N6-4 插在第三个取货停靠之后，后面的停靠往后挪一位），再用这个上下文写（`WireToGateStore.ApplyResequencingAsync`）。
3. 带跟踪的查询把本轮早先的旧实例原样交回。一删一插正好抵消，N4-1、N2-2、N2-4 三个停靠要写的新序位恰好等于旧实例的原值，EF 判「没改」，不发 UPDATE；库里留着第 1 步写的值（5、6、7），与这一步写下的 N6-5=5 撞号，8 号空出来。

推算与改库前备份逐行对上：`field/reconstruct-20261104T203235Z.txt`（13 个停靠全部一致）。推算的依据是服务端当时发给车的每一版计划（`field/plan-snapshots-20261104T2018-2053Z.txt`，只读查 v2 库发件箱）：20:30:20、20:31:08、20:31:54、20:32:35 四版里，每一版都恰好少一个 d355 那一组的卸货停靠、多一个新追加的停靠，删与追加落在同一轮里。20:48:46 的 8822 版把 N4-1 与 N6-5 同时标成 ACTIVE，说明那时库里已经重号。

## 复现（L1）

`tests/ControlServer.Tests/JourneyStopSequenceRaceTests.cs`，最小形状、同一机理：删一个停靠、插一个停靠，两个上下文先后写。

- 修前（`98245f847`，产品代码同 `b82899e40`）两条都红，`red/new-tests-on-unfixed-98245f847.txt`：
  - 修订先提交、追加后写：`B-UNLOAD=3, D-8794|PICKUP=3, … D-PICKUP=6`，两个 3、缺 5。
  - 追加先提交、修订后写：`B-UNLOAD=5, C-UNLOAD=6, D-PICKUP=6`，两个 6、缺 4。
- 修后全绿，`green/targeted-on-fix.txt`。

## 修复

| 改动 | 做什么 |
| --- | --- |
| `JourneyStopsForWrite.LoadAsync`（新） | 要改停靠行的人从这里读：交回被跟踪的实例，但先按库对齐。没暂存过的列，原值与当前值都改成库里的值；暂存过的列保留暂存值，只把原值改成库里的值。计划修订、追加重排、让站判下一站三处都改用它。 |
| 写入时判不变量 | 计划修订暂存完、追加重排写完，各判一次「序位是 1 到停靠数、不重号」，不守就整笔不存（`JourneyStopSequenceBrokenException`；派车轮把它当作本服务端不变量被破坏，记 Error、只隔离这一辆车）。 |
| 推进开头判不变量 | 序位坏了的旅程写 `JOURNEY_STOP_SEQUENCE_BROKEN`、记一次 Error（事件 2341，列出重了哪几号、缺了哪几号、每个停靠现在几号），原地等人，不建腿、不下单、不发计划；之后每轮不再重记。序位改好之后下一轮自动清码、照常推进。派车轮不再往这样的旅程追加。看板有中文说明。 |
| 下一站取法统一 | 建腿取的下一站改为 `JourneyStopCursor.NextOpenStopAfterCurrent`：开放停靠里紧跟在当前停靠后面的那个，与推进取当前停靠出自同一个列表、同一个次序。原来按「序位严格大于当前」取，同号时跳过同号的那个。游标与让站的排序在同号时按 `StopId` 定序。 |

## 不在本证据里

- 现场恢复（11-05 08:37 改库）见 `C:/work/8005-scratch/agv02-seqfix-20261105/`，不在本仓库。
- 变异结果见 `red/mutations.txt`：M1～M9 各红在对应用例上；M10（让站读停靠不对齐）存活，因为它之后的那次写入自己会再对齐一遍，它是纵深的一道。

## 审查返工（第一轮审查结论：需返工）

| 项 | 怎么改的 |
| --- | --- |
| H1 序位坏了时推进开头返回，就绪一侧的在途单监看随之停掉 | 停下之前，两个到站阶段照闸门关着那一侧跑一遍 `NameInFlightOrderWithoutThePeerAsync`（只读 RIoT、只向 RIoT 发命令，不碰车载端，也不依赖序位）；单自己的码（门锁故障、挂起、失败、重建）立着时留着它，不盖成序位码。看板文案改为「已下发的那一段照常开到并停下，路上安全监看照常」。 |
| H2 追加按旧计划写，会并进刚被修订删掉的停靠 | `JourneyAppendPlan.PlannedAgainst`：派车轮把读计划那一刻每个停靠的序位与状态带下来，写库事务内与库逐个比，有不同就抛 `JourneyPlanChangedBeforeAppendException`（受理报成 `CandidateChanged`，可重试、不记 Error），什么也不写；另有一道不靠调用方的：要并入的停靠必须仍开放。 |
| M1 释放服务接不住新异常 | 序位坏了的旅程在释放判断开头就跳过；循环里按旅程接住 `JourneyStopSequenceBrokenException`，丢掉暂存、记 Warning 2342，别的旅程照常。 |
| S1 入库的计划快照是空输出 | 修好脚本后重新只读查一次，`field/plan-snapshots-20261104T2018-2053Z.txt` 现在是 21 版计划；8822、8824 两版都是 N4-1 与 N6-5 同时 ACTIVE。 |
| S2 会话码盖掉序位码、序位码盖掉等人码 | 闸门关着那一侧不再把序位码换成 `ONBOARD_SESSION_NOT_READY`；序位码不盖单自己的码（见 H1），Blocked 与 AREA 站等准入不改码。 |
| S3 派车轮按码挡 | 改为直接判已加载游标的 `SequenceProblem`。 |
| S4 补版数在旧实例上加 | 对齐时把 `WorklistRefills` 当计数列：按库重放本上下文暂存的增量；写补版数的七处单停靠读改走 `JourneyStopsForWrite.TrackedAsync`（按库对齐后再交回跟踪实例）。 |
| L1 让站读法没有测试 | 新用例 `TheStopsStationYieldReadsAreTheStopsAsTheyAreNotAsThisContextFirstSawThem`。 |
| L2 `SetStopSequenceAsync` | 读停靠改走 `TrackedOrDefaultAsync`。 |
| L3 等人码「每类一行」 | 没加：这个码每轮按停靠重新判，序位好好的那个夹具里它会在第一轮就被清掉，那一行的前提（码留着）不成立。码在等人码清单里由 `ReconnectModelTests` 钉住。 |
| L4 清码那次保存 | 与 `ClearFailedAdvanceAsync` 同一条：别处还有暂存时不清。 |

变异见 `red/mutations-rework.txt`：R1～R14 各红在对应用例上；R15（L4，清码前判别处暂存）存活，没有用例在清码那一轮暂存别的改动。本机定向 3390 条全过：`green/targeted-on-rework.txt`。

修前红（返工新用例搬到审查时的 head `0d662b1d` 上跑）与审查探针在返工代码上的结果：`red/rework-h1-h2-s2-before-and-probes-after.txt`。H2 的新用例要用新类型，搬不回旧 head；修前的样子以审查探针在 `0d662b1d` 与 `b82899e4` 上的记录为准（追加被接受、E 的卸货挂在 REMOVED 的停靠上）。
