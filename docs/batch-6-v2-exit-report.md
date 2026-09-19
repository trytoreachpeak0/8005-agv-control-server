# 批次 6 出口报告（v2 线）：任务类型与固定站点绑定（`FP-IS-10`、`FP-IS-11`）

control-server#165（批次6-09）。本报告逐项对照规格 `8005-agv-program/docs/specs/full-product-scope-and-sequence-v2.md` 第 8.3 节批次 6 行
（第 19 节、第 21 节补记优先；地图号按第 21.5 节读作 26）。需求条目按基线 **`v1.4.0`**（tag `requirements-baseline-v1.4.0`，359 条）引用。
本批不改协议，门禁与证据绑 `protocol-v2.0.0`。

> **状态：跑批中。**2026-09-19 20:32～21:15 的真装置封锁时段已跑完并归还：两端 G2、G3 四个 runner、真装置 13 次、车载端全量 L1。
> staged G3 红在判据落后（第四节缺陷单），修好后从头重跑 staged；CI `test` 与 `l2` 三连在跑。标 〔待取〕 的格子等这些结果。

## 结论

〔待取〕

| 出口（规格 8.2／8.3 批次 6 行、本票验收） | 状态 | 依据 |
| --- | --- | --- |
| 前置核对逐项通过；六条判据与场景对照表完整 | 前置已核（下一节）；v2 克隆与本机空闲两项开跑时再核 | 「前置核对」「二、L2」 |
| L1：两端测试套件在出口提交上全绿；新能力逐项有新增测试 | 车载端 623/623（`44b3aa6e`）；服务端 CI `test` 〔待取〕；对照表已列 | 第一节 |
| `FP-IS-10`、`FP-IS-11` 四门禁全 PASS，`gate-result.json` 绑 `protocol-v2.0.0` 精确身份 | **成立**：G1 协议侧发布证据；两端 G2 各两片 PASS；G3（journey）两片 PASS | 第三节 |
| G3 按 control-server#164 的归属出证，两片 `formalSlicePass` 由断言算出；同一次 journey 运行里既有场景全绿 | **成立**：`JOURNEY_G3_PASS`，14/14 场景，六片 `formalSlicePass true`。staged runner 另红（不认领这两片），见第四节 | 第三节 |
| `l2.yml` 批次 6 区块 `Runs = 3`；CI 上批次 6 全部场景连续三次通过，证据独立入库 | `Runs = 3` 已改（`7872001e`）；三连〔待取〕 | 第二节 |
| 六条机制判据各有证据目录 | 场景已对上；证据目录〔待取〕 | 第二节 |
| 两端向量等待名单里没有 `CV-TASK-TYPE-ADMISSION-FAIL-CLOSED`、`CV-REVERSED-DIRECTION-JOURNEY` | **成立**（现顶端已核，出口顶端再核一次） | 第一节末 |
| 每次门禁 `-Output`／`-EvidenceRoot` 新目录；红证据保留，`docs/defects/` 有记录 | 成立至今：每次新目录；staged 两次红原样保留，缺陷单已入库 | 第四节 |
| 十三点如实写明，无第 8.8 节禁用表述 | 已写（第五节） | 第五节 |
| 未切换 `C:\Users\szy\Desktop\8005-workspace\repos\` 下任何克隆 | 至今成立 | 全部操作在 `8005-workspace-v2` |
| 本 PR 的 CI `test` 与 `l2` 两项绿 | 〔待取〕 | PR |

## 前置核对（2026-09-19 实查）

| 项 | 结果 |
| --- | --- |
| control-server#90（批次 5 出口）已关闭 | 成立 |
| 批次6-01～08 全部合入：control-server#158（PR #172，`af49ce64`）、#159（PR #171，`c9752b90`）、#160（PR #188，`7261ed6a`）、#161（PR #183，`aa17b059`）、#162（PR #182，`90433957`）、#163（PR #178，`6b5d3adf`）、#164（PR #176，`93558a07`）；车载端 onboard-hmi#115（PR #117，`3547a97`）进 `w2g/fp-v2-impl` | 成立，均已关闭 |
| 批次 6 各票加进 `l2.yml` 的场景（`BatchId = 'batch-6'`）与六条判据逐条对上 | 成立：8 个场景，见第二节对照表；`scripts/l2/scenarios/` 下没有不在清单里的批次 6 合成场景 |
| v2 工作区四个仓在要绑定的提交上、工作树干净；本机没有其它真装置 L2 或 G3 在跑 | 成立：按工作区「Real-rig L2」规则，三端用 detached worktree（cs `76c2ca21`、onboard `44b3aa6e`、sim `fb5f7c59`，开跑前后都干净，跑完已删）；协议 `repos/8005-agv-protocol` 在 `86575456`、干净；调度核过桌面锁空闲、本机无重负载后放行（持有者「cs#165 封锁」） |
| control-server#166（批次6-10，派工待送取货站点建站与绑定） | **open**，已移出批次 6、放到后续批次（调度 2026-09-19 登记的用户决定）；不挡出口，见第五节第 1 点 |

### 出口顶端：追加票

批次 6 在前置之外还有追加票，出口正式证据要等它们合入后在两端顶端上取：

| 票 | 仓 | 内容 | 状态（2026-09-19） |
| --- | --- | --- | --- |
| onboard-hmi#123 | 车载端 | 开锁前挡住补偿、受控取货、强制取出命令时不回结果，服务端会话停在 `EXECUTING`（control-server#187 审查发现） | 已合入：PR onboard-hmi#125（`172077a`） |
| onboard-hmi#124 | 车载端＋服务端 | 已完成的装货因结果确认没回来被报成「上次操作未完成」；重启后遗留 attempt 与重发命令竞态不结算 | 已合入：车载端 PR onboard-hmi#126（`2b04729`）；服务端判据 `L2-DA-09` 进 `real-onboard-durable-ack-lost`，PR #196（`e56ffa4a`） |
| control-server#193 | 服务端 | `load-command-never-answered` 的 `L2-LN-01` 取样竞态；普查改了六个合成场景的同型取样 | 已合入：PR #194（`905ffd1d`） |
| onboard-hmi#127 | 车载端 | 在途装货断线重连后车载端不发结果，与服务端互相等（control-server#189 第二步；#189 第一步 PR #195 已合入 `5a126238`） | 已合入：PR onboard-hmi#131（`44b3aa6e`） |

## 身份

| 项 | 值 |
| --- | --- |
| 协议 | `(AGV_FULL_PRODUCT, 3)`，`releaseVersion 2.0.0`，tag `protocol-v2.0.0` → `86575456c847041515b7b75e8851a00e0d939804`；本批零改动 |
| `ProtocolReleaseIdentity` 其余字段 | manifest `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7`，schema bundle `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221`，vectors `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a`，`APPROVED_RELEASE` |
| 服务端产品代码 | `fp/v2-impl@905ffd1d`（批次 6 全部服务端票与同期修复）；runner 与证据提交 `76c2ca21` 相对它只多 G3 绑定、`l2.yml` 三连与出口报告，`src/`、`tests/` 零差异 |
| 车载端 | `w2g/fp-v2-impl@44b3aa6e`（onboard-hmi#127，PR onboard-hmi#131 合入） |
| 模拟器 | `main@fb5f7c59`（不变） |
| G3 共享绑定 | `76c2ca21`（`chore(g3)`）：`ControlServerCommit 905ffd1d`、`OnboardCommit 44b3aa6e`、`SimulatorCommit fb5f7c59`、`ProtocolCommit 86575456`。原绑 cs `d3003c2f`／onboard `29fbf65e`，后者早于 onboard-hmi#115，runner 要求 `OnboardCommit` 等于 `origin/w2g/fp-v2-impl` 顶端；tag 字面量（`scripts/run-staged-g3.ps1:2020`、`:2129`）与 `run-demand-bearing-g3-vectors.ps1:638` 已是 `protocol-v2.0.0`，未动。移绑定使引用它的 G3 结果全部失效，四个 runner 都重跑了 |

## 一、L1

### 实测

| 端 | 命令 | 结果 |
| --- | --- | --- |
| 服务端 | CI `test.yml`，`workflow_dispatch` 于 `50dc987d`（run `35445345577`） | 〔待取〕 |
| 车载端 | `dotnet test ./SQCD_8005AGV.sln -c Release`（`44b3aa6e`，经 `Invoke-HeavyLocal.ps1 -Ticket cs#165`，封锁时段内） | **623 / 623 通过**，0 失败 0 跳过（`SQCD.Agv.UnitTests` 395、`SQCD.Agv.WireToGateG2Tests` 228）；`evidence/l1/20260919-onboard-hmi-44b3aa6e/` |

车载端仓的 `global.json` 选中的 SDK 是 `8.0.425`（`dotnet --version` 实读），与服务端基线 `8.0.424` 不同；车载端属只报告的仓，这里如实记下，不改。

### 批次 6 每项新能力 ↔ 新增测试

测试名取自各合入提交新增的 `[Fact]`／`[Theory]`；格子里只列代表方法，括号为该合入新增总数；完整清单可用 `git diff <merge>^1 <merge> -- tests` 复现。

| 票 | 需求条目（`v1.4.0`） | 新能力 | 新增测试（代表） | 合入 |
| --- | --- | --- | --- | --- |
| control-server#158（批次6-01） | 无条目载体（预重构） | 固定站经解析器按任务类型取得；计划与腿抽成计划生成器；路线证据起终点具名；行为不变 | `JourneyPlanCharacterizationTests`、`FixedTaskStationResolverTests` 等特征测试（7） | PR #172 `af49ce64` |
| control-server#159（批次6-02） | `REQ-0334`、`0335`、`0338`、`0343`、`0344` | 任务类型规则、`(mapId, TASK_TYPE)` 绑定与整图版本、暂停、需求冻结四组表（本批唯一迁移）；预置配置装载与启动期只拒绝配置本身的错误 | `TaskTypeStationConfigurationValidatorTests.OneStationBoundToTwoTaskTypesRefusesStartAndNamesTheStationAndBothTaskTypes`、`.ATaskTypeThatIsNeitherRequiredNorBoundIsNotEnabledAndDoesNotRefuseStart`、`.AStationNamedAfterAnAreaIsAMachineStationAndRefusesStart`、`TaskTypeStationStoreTests.TheDatabaseRefusesOneStationBoundToTwoTaskTypesAndNothingOfThatVersionIsLeftBehind`、`DemandTaskTypeStationFreezeTests.FreezingADifferentRuleVersionBindingSetVersionOrMapIsRefusedAndTheFirstFreezeStands`、`Batch6MigrationDisciplineTests.Batch6AddsExactlyOneMigrationAndItComesStraightAfterTheBatch5Migration` 等（57）；L2 `task-type-binding-station-reused-refuses-start` | PR #171 `c9752b90` |
| control-server#160（批次6-04） | `REQ-0184`、`0187`、`0324`、`0335`、`0342`、`0344` | 按任务类型准入与按绑定解终点；缺绑定只停该类；准入读暂停；建单冻结版本；`WIRE_TO_GATE` 迁入新机制；`REQ-0187` 跨任务类型；目录新鲜与站点存在性每轮按任务类型判 | `BoundFixedTaskStationResolverTests.ATaskTypeWithoutAnEffectiveBindingIsRefusedAndNeverFallsBackToADefaultStation`、`.ABoundStationMissingFromTheCatalogRefusesOnlyItsOwnTaskType`、`TaskTypeAdmissionChainTests.AMissingBindingStopsOnlyItsOwnTaskTypeAndTheRestOfTheRoundIsJudgedAsUsual`、`AreaEqpUniqueAcrossTaskTypesTests.AnotherTaskTypesRowNamingTheSameAreaWithAnotherEqpBlocksTheCandidate`、`TaskTypeAdmissionFailClosedVectorTests.OnlyTheBoundTaskTypeIsAdmittedAndTheMissingBindingFailsClosedWithoutReachingTheVehicle`（绑 `CV-TASK-TYPE-ADMISSION-FAIL-CLOSED`）、`DemandTaskTypeStationIntakeFreezeTests.AcceptingADemandFreezesTheRuleAndBindingSetVersionsItWasJudgedUnderInTheSameTransaction` 等（33）；L2 `task-type-binding-missing-not-cascading`、`task-type-binding-admits-bound-station`、`area-eqp-unique-across-task-types` | PR #188 `7261ed6a` |
| control-server#161（批次6-05） | `REQ-0337`、`0338`、`0343`、`0347`、`0348` | 绑定集整图版本化：FieldOps 激活（含预演）、回滚、解除暂停、结果未知对账、不可改写审计 | `TaskTypeStationActivationTests.ActivationSwitchesTheWholeMapAtOnceAfterTheFirstStepHeldEveryRequiredTaskType`、`.ProcessDyingBetweenTheStepsLeavesTheMapHeldAcrossARestartUntilReconciliationFindsThePreviousVersion`、`.RollbackIsAFreshlyValidatedActivationOfOldContentAndEveryActivationKeepsItsOwnRecord`、`.EveryActionLeavesAnImmutableExportableAuditRecordWithTheRequiredFields`、`TaskTypeStationFieldOpsTests.ActivateDryRunThenActivateThenReadBackThroughTheProcess` 等（43） | PR #183 `aa17b059` |
| control-server#162（批次6-06） | `REQ-0304`、`0340`、`0341`、`0342`、`0345`、`0348` | 看板按 `Map + TASK_TYPE` 立即收紧；目录变化按稳定身份与风险分类、影响只收敛到受影响的任务类型；已建单不改单 | `CatalogBindingChangeClassifierTests.TheSameIdUnderAnotherNameIsARenameThatNeedsASiteReview`、`.ANewIdCarryingTheOldNameIsANewStationAndIsNeverReboundByName`、`CatalogBindingHoldConvergenceTests.ARenamedStationHoldsOnlyTheTaskTypeBoundToItAndNothingOnAnotherMap`、`.AHoldDoesNotTouchAJourneyAlreadyUnderWayAndCountsItInTheAudit`、`TaskTypeHoldEndpointsTests.TheRouteOnlyTightensThereIsNoWayToReleaseAHoldOverHttp`、`TaskTypeBindingDashboardTests.TheCardListsEachTaskTypeWithItsStatusTheHoldSourcesAndAHoldLinkOnEveryRow` 等（38）；L2 `binding-hold-dashboard-not-cascading`、`catalog-change-binding-hold` | PR #182 `90433957` |
| control-server#163（批次6-07） | `REQ-0184`、`0324`（`STAGING_TO_WIRE` 端点）；分侧 `REQ-0352` | `STAGING_TO_WIRE` 反向旅程，推翻 I6；取货点按任务类型绑定；站点任务类型准入跟 AREA 机台端（卸货端）；按目的机台装侧 | `ReversedDirectionJourneyRuntimeTests.AStagingToWireJourneyIsPlannedFromTheStagingStationToTheAreaMachineAndNeverSwapped`、`.AMachineThatNoLongerAdmitsTheTaskTypeHoldsTheUnloadUntilItDoesAgain`、`ReversedDirectionJourneyTests.AReplayedStagingToWireDemandKeepsItsRouteEvidenceAndASwappedOneIsRefused`、`AreaEndAdmissionStoreTests.AStagingToWireUnloadCarriesAndFreezesTheAreaMachineAdmission` 等（10）；L2 `staging-to-wire-reversed-journey`、`staging-to-wire-slot-group` | PR #178 `6b5d3adf` |
| control-server#164（批次6-08） | 无条目载体 | G3 认领 `FP-IS-10`／`FP-IS-11`：两条 journey 场景、runner 认领与断言归属表 | G3 场景 `g3-task-type-admission-fail-closed`、`g3-reversed-direction-journey`（无新增 `[Fact]`） | PR #176 `93558a07` |
| control-server#191（#161 复审后续） | `REQ-0337`、`0347` | 墓碑（手动关闭）之后被中断的空需求集激活，对账回到墓碑、重启不装预置 | `TaskTypeStationActivationTests.AnInterruptedActivationOfAnEmptyRequirementSetFromATombstoneReconcilesBackToTheTombstoneAndARestartKeepsThePresetOut` 等（5） | PR #192 `a2369ce0` |
| onboard-hmi#115（批次6-03） | 车载半边（`FP-IS-10`／`11`） | 放开非 `WIRE_TO_GATE` 任务的入站校验；按计划显示方向与任务类型；不从计划或 `blockingFacts` 推断未绑定的任务类型 | `TaskTypeAndDirectionVectorG2Tests.AReversedJourneyShowsTheDirectionAsPlanned`（`CV-REVERSED-DIRECTION-JOURNEY`）、`.AnUnboundTaskTypeIsNeverInferredFromThePlanOrTheBlockingFacts`（`CV-TASK-TYPE-ADMISSION-FAIL-CLOSED`）、`InboundPayloadSchemaBoundaryTests.EveryWorkTypeTheSchemaDeclaresIsAccepted`、`WireToGateStopFactsTests.TheSameStopRoleShowsTheSameDirectionWhateverTheTaskType` 等（17） | PR #117 `3547a97` |

`REQ-0336`（两级管理员分工）延后，本批没有它的实现与测试（第五节第 4 点）。`REQ-0359` 不在本批（第五节第 5 点）。

批次 6 期间在同一集成分支上合入、但不属于批次 6 新能力的修复（恢复与会话路径，来自批次 5 遗留或批次 6 审查）：
control-server#169（PR #173 `911ee2ef`，8）、#175（PR #177 `b740d319`，9）、#180（PR #185 `75c3d39e`，3）、#187（PR #190 `c1252932`，7）、
#167（PR #181 `169dfa42`，真装置场景 `real-onboard-expected-action-overdue`）、#189 第一步（PR #195 `5a126238`，复现与结论）、#196（PR #196 `e56ffa4a`，`real-onboard-durable-ack-lost` 加 `L2-DA-09`，onboard-hmi#124 的服务端判据）、#179（PR #184 `969f883d`，26 号图只读核实记录）；
车载端 onboard-hmi#120（PR #121 `7ded1b7`，3）、#119（PR #122 `4a6790e`，12）、#124（PR #126 `2b04729`，5）。
它们的剩余风险见第六节。

### 向量绑定

两端 `ProtocolVectorTestBindingArchitectureTests` 的 `VectorsAwaitingTheirSlice` 各剩 7 条，全部属于批次 7～11 的切片（`FP-IS-08`、`09`、`12`、`13`）；
`CV-TASK-TYPE-ADMISSION-FAIL-CLOSED` 与 `CV-REVERSED-DIRECTION-JOURNEY` 已不在名单里（出口顶端 `905ffd1d`／`44b3aa6e` 实读），各有同名具名测试：

| 向量 | 服务端 | 车载端 |
| --- | --- | --- |
| `CV-TASK-TYPE-ADMISSION-FAIL-CLOSED` | `TaskTypeAdmissionFailClosedVectorTests` | `TaskTypeAndDirectionVectorG2Tests.AnUnboundTaskTypeIsNeverInferredFromThePlanOrTheBlockingFacts` |
| `CV-REVERSED-DIRECTION-JOURNEY` | `ReversedDirectionJourneyRuntimeTests` | `TaskTypeAndDirectionVectorG2Tests.AReversedJourneyShowsTheDirectionAsPlanned` |

**这只证明绑定存在**：向量从未被机械执行（第五节第 7 点）。

## 二、L2

### 六条机制判据 ↔ 场景 ↔ 证据

| 判据（规格 8.3 批次 6 行） | 场景（票） | 判据编号 | 证据目录（CI 三连） |
| --- | --- | --- | --- |
| ① 缺绑定时 fail-closed 正确拒绝，且不连带其它任务类型 | `task-type-binding-missing-not-cascading`（#160） | `L2-TTBM-01`～`04` | 〔待取〕 |
| ② 绑定完备时正确放行（双 Fake） | `task-type-binding-admits-bound-station`（#160） | `L2-TTAB-01`～`03` | 〔待取〕 |
| ③ `REQ-0187` 唯一性跨任务类型 | `area-eqp-unique-across-task-types`（#160） | `L2-AEUT-01`～`02` | 〔待取〕 |
| ④ 同一 Station 被两个任务类型绑定时启动期拒绝 | `task-type-binding-station-reused-refuses-start`（#159） | `L2-TTSR-01`～`05` | 〔待取〕 |
| ⑤ 按 `Map + TASK_TYPE` 暂停不连带 | `binding-hold-dashboard-not-cascading`（#162，看板人工暂停）；`catalog-change-binding-hold`（#162，目录变化自动暂停） | `L2-BH-01`～`12`；`L2-CC-01`～`10` | 〔待取〕 |
| ⑥ 送往前侧机台的 `STAGING_TO_WIRE` 需求在派工待送点装入前侧（后侧同理） | `staging-to-wire-slot-group`（#163）：同一机台站挂 `N1-3`→`REAR`、`N1-7`→`FRONT` 两条需求，两侧各断言一次 | 场景内断言 | 〔待取〕 |
| （⑥ 的前提：反向旅程本身） | `staging-to-wire-reversed-journey`（#163） | `L2-S2W-01`～`07` | 〔待取〕 |

这 8 个场景都是合成 L2（假 RIoT、假 MesIngest、合成车载端），是 `FP-C9a` 机制判据的证据来源（规格 8.5 节）；真车载端上的方向与准入由 G3 两条 journey 场景证明。

### CI 三连

- `l2.yml` 批次 6 区块 8 行由 `Runs = 1` 改为 `Runs = 3`（`7872001e`）。改之前先提交了检查
  `evidence/l2/20260919-batch-6-exit-runs-check/Test-Batch6ConsecutiveRuns.ps1`（`4f1c0888`）：它执行 `l2.yml` 自己的 `$scenarios` 字面量，
  断言批次 6 恰好是上表 8 个场景、各 `Runs = 3`、不带 `DefaultRuns`，手动触发超时不少于 180 分钟。改动前 8 行全 FAIL、退出码 1
  （`01-red-before-change.txt`），改动后 PASS（`02-green-after-change.txt`）。
- **作业超时不改。**拉取请求仍每个场景跑一遍（不带 `DefaultRuns`），默认一轮时长不变；出口 `consecutive-all` 全部 36 个场景各三遍，
  按批次 5 的 28 个场景 859 秒（run `35361077376`，4 路）外推约 20 分钟，远低于 `workflow_dispatch` 的 180 分钟。
  超时不该是结束一轮的方式（取消会卡死 runner），留的余量足够。
- 出口运行：〔待取〕`gh workflow run l2.yml --ref <本分支> -f mode=consecutive-all`，全部 36 个合成场景各三遍（批次 2～5 的 28 个随批次 6 代码回归，批次 6 的 8 个是本批三连）。
  下载后按 `evidence/l2/<日期>-ci-<runId>-<场景>-NN` 入库，逐份核对 `outcome`、`identity.protocolReleaseIdentity`、`identity.batchId`（批次 6 为 `batch-6`）。

### 真装置

本批的六条机制判据没有真装置正式场景（规格 8.5 节，全部由合成 L2 证明）。出口仍跑真装置，理由有二：

1. 调度登记（control-server#167／PR #181 审查）：`real-onboard-expected-action-overdue` 不进 `l2.yml`、不属于任何 G3 片，
   它的正式 PASS 取在服务端 `42c14e9b`（合入 `a2369ce0` 之前），头提交上的回归要靠出口三连兜住。**出口真装置三连必须包含它。**
2. 批次 6 期间车载端恢复与会话路径改过多次（onboard-hmi#115、#119、#120、#124，以及待合入的 #123、#127），规格 21.2 节第 7 条要求
   改视图模型或恢复入口的改动在真装置上跑恢复场景。

计划清单（调度 2026-09-19 认可）：`real-onboard-expected-action-overdue` × 3；`real-onboard-durable-ack-lost`（含 control-server#196 新加的 `L2-DA-09`）× 3；`real-onboard-inflight-load-reconnect` × 1；批次 5 出口的其余六条（control-server#86 三条、#88 除 `durable-ack-lost` 外三条）各 × 1 作回归。
`real-onboard-inflight-load-reconnect` 是 control-server#189 第二步（onboard-hmi#127）唯一的真装置证明，**不是 `scripts/l2/scenarios/` 里的正式场景**：它是 onboard-hmi PR #131 的一次性副本（车载端仓 `evidence/hmi-127/green/rig-inflight-load-reconnect-d21b3e8-001/scenario/`），出口按那份副本在最终顶端上跑一次，副本随证据入库。副本来源 onboard-hmi#131，转正式场景见 control-server#205。
真装置只实测一个管理员恢复入口（「补偿清空」），其余三个入口由 G2 覆盖（onboard-hmi#126 审查，调度要求写明）。

**结果（2026-09-19 封锁时段，13/13 PASS）**：三端 detached worktree 当对端（cs `76c2ca21`、onboard `44b3aa6e`、sim `fb5f7c59`），显式传 `-Repository`／`-OnboardRepository`／`-SimulatorRepository`；
每份 `assertions.json` 的 `outcome` 为 `PASS`，`identity` 记录的正是这三个提交，`protocolReleaseIdentity` 为发布身份（`APPROVED_RELEASE`）。
手动跑的真装置场景 `identity.batchId` 是编排器默认值，只是标签，判定不看它。

| 场景 | 遍数 | 结果 | 证据 |
| --- | --- | --- | --- |
| `real-onboard-expected-action-overdue`（control-server#167，`L2-EAO-01`～`13`） | 3 | 3/3 PASS（89／84／82 秒） | `evidence/l2/20260919-real-onboard-expected-action-overdue-b6exit-01`～`03/` |
| `real-onboard-durable-ack-lost`（含 control-server#196 的 `L2-DA-09`） | 3 | 3/3 PASS | `evidence/l2/20260919-real-onboard-durable-ack-lost-b6exit-01`～`03/` |
| `real-onboard-inflight-load-reconnect`（一次性副本，来源 onboard-hmi#131，转正式场景见 control-server#205） | 1 | PASS | `evidence/l2/20260919-real-onboard-inflight-load-reconnect-b6exit-01/`（副本在 `scenario/`，SHA-256 与所跑一致） |
| `real-onboard-load-door-closed-empty-reopens` | 1 | PASS | `…-b6exit-01/` |
| `real-onboard-station-timeout-door-open` | 1 | PASS | `…-b6exit-01/` |
| `real-onboard-unload-not-emptied` | 1 | PASS | `…-b6exit-01/` |
| `real-onboard-compensate-then-reconnect` | 1 | PASS | `…-b6exit-01/` |
| `real-onboard-restart-while-waiting-operator` | 1 | PASS | `…-b6exit-01/` |
| `real-onboard-cancellation-authorization-lost` | 1 | PASS | `…-b6exit-01/` |

每次运行的控制台输出在同名 `.log`。

## 三、门禁

按票面步骤表，在出口顶端跑（除第 5、6 步外都在 2026-09-19 20:32～21:15 的封锁时段内）：

| 步 | 做什么 | 结果 | 证据目录 |
| --- | --- | --- | --- |
| 1 | 移 G3 共享绑定（`chore(g3)` `76c2ca21`） | 见「身份」 | — |
| 2 | `CONTROL_SERVER_G2` × 2（从 detached worktree `76c2ca21`，经 `Invoke-HeavyLocal.ps1`） | `FP-IS-10` PASS（48/48 测试，出站 42 行零违约）；`FP-IS-11` PASS（11/11，49 行零违约） | `evidence/g2/20260919-protocol-v2.0.0-76c2ca21/` |
| 3 | `ONBOARD_HMI_G2` × 2（`44b3aa6e`，`run-w2g-g2.ps1 -Slice`） | 两片 PASS，build／test／format 退出码 0，出站零违约，每片在发布态重跑协议 G1 | 车载端仓 `evidence/g2/20260919-protocol-v2.0.0-44b3aa6e/`，小 PR trytoreachpeak0/8005-agv-onboard-hmi#133 |
| 4a | `run-staged-g3.ps1` | **`INCONCLUSIVE_RUNNER_ERROR`**，同一封锁内重跑一次同样中止（第四节） | `evidence/g3/20260919-protocol-v2.0.0-staged-905ffd1d/`、`…-staged-905ffd1d-rerun/` |
| 4b | `run-staged-g3-restart.ps1` | `STAGED_G3_PROCESS_RESTART_PASS`，四片 PASS | `evidence/g3/20260919-protocol-v2.0.0-restart-905ffd1d/` |
| 4c | `run-demand-bearing-g3-vectors.ps1`（`-FieldRunRoot …fullloop-20260829T131549Z`） | `DEMAND_BEARING_G3_VECTORS_PASS` | `evidence/g3/20260919-protocol-v2.0.0-demand-bearing-905ffd1d/` |
| 4d | `run-journey-g3.ps1` | **`JOURNEY_G3_PASS`**，14/14 场景；`FP-IS-01`／`02`／`03`／`07`／`10`／`11` 的 `gate-result.json` 都是 `PASS`、`formalSlicePass true` | `evidence/g3/20260919-protocol-v2.0.0-journey-905ffd1d/` |
| 5 | CI `l2.yml` `consecutive-all` | 〔待取〕run `35445347285` | 见第二节 |
| 6 | 本 PR 默认 CI（`test`、`l2`） | 〔待取〕 | PR 检查页 |

两端 G2 的 `gate-result.json` 都绑 `protocolTag protocol-v2.0.0`、`protocolRepositoryCommit 86575456`、`protocolManifestSha256 4ac095ad…`、`protocolApprovalStatus APPROVED_RELEASE`。

**车载端 G2 每片只选中 1 个测试**：`FP-IS-10` 是 `TaskTypeAndDirectionVectorG2Tests.AnUnboundTaskTypeIsNeverInferredFromThePlanOrTheBlockingFacts`，`FP-IS-11` 是 `.AReversedJourneyShowsTheDirectionAsPlanned`——
车载端带这两片 `IntegrationSlice` 标记的只有这两个向量具名测试（`WireToGateStopFactsTests` 等单元测试没有切片标记，随全量 L1 跑）。车载端对这两片的门禁因此很薄，如实记在第六节。

**`FP-IS-10`、`FP-IS-11` 的四道门禁**：G1 是协议侧发布证据——`protocol-v2.0.0` 的 G1 在发布态通过（program#97，`evidence/g1/20260916-protocol-v2.0.0-release-8657545/`），本批协议零改动，
车载端 G2 每片又在发布态重跑了一次（`logs/protocol-g1.log`）；两端 G2 如上；G3 由 journey 认领（control-server#164 的归属表），PASS。**四道全 PASS。**

**staged 那处红挡的是 staged 认领的 `FP-IS-00`、`06`、`07`、`14`、`15` 的 staged 面**，不是本批两片；但按「修好后从头重跑受影响的门禁、不拼接」，修复合入后移绑定、从头重跑 staged。〔待修复〕

## 四、红证据与缺陷单

| 单 | 红在哪里 | 性质 | 修复去向 |
| --- | --- | --- | --- |
| [`20260919-staged-g3-second-forced-submission-predates-cs187.md`](defects/20260919-staged-g3-second-forced-submission-predates-cs187.md) | `run-staged-g3.ps1` 两次 `INCONCLUSIVE_RUNNER_ERROR`：恢复探针在第 1 代强制机械取出未结清时提交第 2 条，期待 `RecoveryActionAccepted`；七条恢复断言 `FAIL_OR_INCONCLUSIVE` | **G3 判据落后**：control-server#187（PR #190）有意让同会话同类动作未结清时再提交回 `RecoveryActionRejected`／`ActionNotAllowedInState`、强制取出不推进代次；staged 探针没跟着改。确定性复现（同一封锁内重跑一次，同一异常）。协议向量不要求受理第二条，不是契约冲突。与批次 5 缺陷单 B 同类 | 〔待调度定票号〕 |

红证据原样保留，没有被绿覆盖：`evidence/g3/20260919-protocol-v2.0.0-staged-905ffd1d/` 与 `…-rerun/`。

## 五、必须如实写明的各点

1. **现场地图（RIoT 26 号）上的公共站点今天只有「关卡」。**依据：control-server#67 的投运前置记录（2026-09-18 21:27:46 读取）
   `evidence/field/2026-09-18-B4-site-prerequisites/02-station-split-check.md`：26 号图 209 个站点，206 个机台站点，3 个非机台站点
   `关卡`、`充电点1`、`充电准备点1`，其中后两个是充电用点、不是任务类型的公共业务点；control-server#179 的只读核实（2026-09-19 13:05）
   `evidence/field/2026-09-19-B6-map-name-baseline-check/SUMMARY.md` 读到 26 号图变成 208 个站点，「关卡」仍是唯一同名站、`stationId 210`。
   少了哪个站没有记录；再读一次要另行批准（调度登记：用户 2026-09-19 定，等决定切 26 号图或做 control-server#166 时再申请），本票不读。
   所以**验收期只有 `WIRE_TO_GATE` 能真实触发**。派工待送取货站点未建：control-server#166 已移出批次 6、放到后续批次，
   `STAGING_TO_WIRE` 在本批验收期只由合成 L2 与真车载端 G3（control-server#163、#164 的场景）证明，不能在现场真实触发；
   它的投运与 W2 备用车预演等 control-server#166 完成。同向四类在批次 10。**本报告不写「六类都跑通」。**
2. **`FP-C9a` 的六条机制判据来自 L2**（规格 8.5 节，第二节对照表），不是「不触发即通过」，也不归入 `证据受限实施`。
3. **焊线2 出柜风险**（规格 5.3 节、第 14 节第 8 项）：MES 条件是 `step IN ('焊线','键合') AND task='入站'`，焊线2 的 step 名是 `焊线2`，
   按字面捞不到。投运后若焊线2 的批次不生成任务，修法是请工厂 IT 把 `焊线2` 加进这一分支的 step 条件。本批未处理。
4. **`REQ-0336` 两级管理员分工延后**（治理取乙档）；`REQ-0339` 不在本批。完整产品的验收证据里没有任何人员认证项，
   **不得表述为「权限已在批次 0 验收过」**。本批的看板暂停入口与 FieldOps 动词只按来源地址判同机，不认人（第六节「运维说明」）。
5. **`REQ-0359`（人工判故障）不在本批**，随 `protocol-v3.0.0` 攒在 program#115（规格 21.3 节）；剖面该行 `Batch` 仍为 `BATCH-6`。**本批协议零改动。**
6. **`CV-TASK-TYPE-ADMISSION-FAIL-CLOSED` 的车载端断言 `DISPLAY_ADMISSION_BLOCK_REASON` 按规格 5.3 节取消**，两端与 G3 都不认领；
   契约措辞已由 onboard-hmi#115 登记到 program#115，随 `protocol-v3.0.0` 处理。
7. **`FP-IS-10`、`FP-IS-11` 是新切片**，没有可沿用的旧通过结论；向量从未被机械执行（弱绑定），「同名具名测试」只证明绑定存在。
8. **onboard-hmi#61 本批只放开「只收 `WIRE_TO_GATE`」一处**，清单项数与腿数两处在批次 7。
9. **反向旅程里 `JourneyRuntimes.GateStationId` 与看板字段 `gateStationId` 装的是 AREA 机台站**（列名沿用 `WIRE_TO_GATE` 口径），
   订单意图名 `TO_GATE` 在反向旅程里同样指 AREA 机台；本批未改名。
10. **本批 migration 一张**：control-server#159 的 `20260919021150_Batch6TaskTypeStationBindings`（`Batch6MigrationDisciplineTests` 钉住「恰好一张、紧跟批次 5 那张」）。
    其它票零迁移：`1f5efed1..905ffd1d`（出口顶端）在 `Migrations/` 下只有这一张新增与模型快照的对应修改。
11. **目录新鲜与站点存在性改为每轮按任务类型判**（control-server#160），不在启动期拒绝。理由：现场共享地图上一处无关的站点变化不应让整个服务起不来、
    还连带所有任务类型，那违背 `REQ-0342`「单一站点变化只阻断绑定在该 Station 上的那个 TASK_TYPE」；目录不新鲜在整图层面本来由 `REQ-0302` 硬阻断。
    **注意这一点的边界**：目录新鲜度过期按设计停整图所有任务类型（`REQ-0302`）；「只停该类」只对绑定站改名或消失成立（control-server#163／PR #178 审查）。
12. **启动期校验只拒绝配置本身的错误**（control-server#159）：未知任务类型、规则缺失或重复、同图同任务类型重复绑定、同一 Station 被两个任务类型绑定、
    需求集缺绑定、身份格式非法、绑到 AREA 命名的机台站、缺现场用途核对引用；「未启用的任务类型没有绑定」不拒绝启动，由准入按该任务类型挡住
    （`TaskTypeStationConfigurationValidatorTests.ATaskTypeThatIsNeitherRequiredNorBoundIsNotEnabledAndDoesNotRefuseStart`）。
13. **需求条目一律按基线 `v1.4.0` 引用。**

调度另登记、须照实写的两项（用户 2026-09-19 决定）：

14. **Map 级改名检测「接口可读、未实施」**（control-server#162）。RIoT 地图接口读得到 Map 名称，但没有可靠基线：绑定集不记 Map 名称；
    control-server#179 核实 `JourneyRuntime:mapIdentity`（`老厂前线new`）与 RIoT 26 号图的 `name`（`老厂前线new_wk`）不是同一字面值。
    用户定走方案一（加一次迁移，把 RIoT `mapInfo/{mapId}` 的 `name` 存作基线），不进批次 6，已开 control-server#186（open），挂后续批次。
    现状兜底：`VehicleDynamicFactsCriterion` 挡车辆地图名不符；Map 删除或换 `mapId` 由目录新鲜度门禁整图阻断。
15. **control-server#166（人工建站票）移出批次 6**，见第 1 点。

## 六、剩余风险

按来源汇总；出处是各票关闭评论与 PR 审查评论（审查结论都写在 PR 评论里）。出口跑批中新发现的另列在第四节。

### 行为与现场

- **反向旅程到机台时准入被撤，带货无限等待**（control-server#163／PR #178 审查）：停在 `AwaitingGateArrival`／`TASK_TYPE_NOT_ALLOWED_AT_STATION`，没有超时也不升级。**已定修法：加超时后升级，批次 7 control-server#198。**
  在现场只有 `STAGING_TO_WIRE` 投运后才可能出现（本批不投运，第五节第 1 点）。
- **目录新鲜度过期停整图所有任务类型**（`REQ-0302`，按设计）；「只停该类」只对绑定站改名或消失成立（同上）。
- **出厂 `allowedWorkTypes` 由一类改为列全六类**（control-server#160，偏离票面）：现场一旦出现同向四类或 `STAGING_TO_WIRE` 需求，
  看板积压里会多出「缺绑定」行；这是 fail-closed 的显示，不建单。生产 MesIngest 里有没有这些类型的需求没有查（属 Ask first 第 2 类）。
- **出厂 `admissionPolicyVersion` 由 1 升到 2**（control-server#163）：某实例若按 20260915 缺陷处置手工调到过 2，升级后会判漂移、所有任务类型停受理；v2 目前没有部署，切生产时核对。
- **出厂 `mapId` 仍是 25，现场是 26**（control-server#159）：切图属切生产配置，不在本批；切时要设 `TaskTypeStations:settingsFile`，
  否则以 `TASK_TYPE_BINDING_MAP_MISMATCH` 拒绝启动。升级会整目录覆盖 `task-type-stations.settings.json`。
- **回滚到 control-server#159 之前的包必须连数据库一起回滚**：旧二进制读不了新表的枚举。
- **人工收尾（`CLOSED_MANUALLY`，墓碑）后该图没有生效版本**，所有需要固定站的任务类型停受理，直到下一次激活（有意的 fail-safe，control-server#161）；
  规格 21.2 节第 4 条「无生效版本时装第一版预置」只适用于从未有过受控版本的图，墓碑不算。control-server#191 起，从未激活、需求集为空的图首次激活中断后也回到墓碑，要人激活一次。
- **180 天审计保留**依赖 `PurgeExpiredAuditAsync`，它还没有生产调用点；「审计不可改写」只在 EF 层，数据库层没有触发器（control-server#161 审查 O1）。
- **FieldOps 激活要运维从 RIoT 取目录带进来**（`--catalog`），且必须落在新鲜窗口内；改了预置里的规则再重启会出新规则版本，下一次激活必须带新规则。
- **在途装货断线重连两端互等**（control-server#189）：车卡在 `Prepared`／`RecoveryRequired`，只有下一次重连才解得开；人工重启能解开是推论、没实测。
  修复由 onboard-hmi#127 承接，已合入（PR onboard-hmi#131，`44b3aa6e`），出口真装置 `real-onboard-inflight-load-reconnect` 在该提交上 PASS。**MVP 线代码同构，生产上是否碰到过未核实（用户定暂不核实）。**
- **onboard-hmi#124 之后仍有的窗口**：结果确认晚到而连接没断时，结果要等下一次重连才在车上结算，HMI 停在「结果等待确认」；
  握手里的 `RecoveryStateReport` 仍把这次 attempt 报成未结算。四条转 onboard-hmi#127。
- **onboard-hmi#123（PR onboard-hmi#125）自列**：IO 预检 `FAILED`／`UNKNOWN` 之后向量与会话不清；绑定失败的拒绝仍不回结果，服务端停在 `AwaitingResult`（PR onboard-hmi#125 正文自列，合入时未改）；转后续见 onboard-hmi#129。
- **REQ-0358 期待动作超时**（control-server#167）：车载端重启时卡片状态没有真装置证据；断开时的 HMI 文案只有 G2 覆盖；卸货侧、锁反馈卡死两种变体不做；
  一次关门服务端约 1 秒内发 4 次快照请求（产品现象，转 control-server#202）；MVP `OnboardHmi_MVP` 有同一段代码，未核实。
- **Map 级改名检测未实施**（第五节第 14 点，control-server#186）。

### 证据的边界

- **车载端 `ONBOARD_HMI_G2` 对 `FP-IS-10`／`11` 每片只选中 1 个测试**（两个向量具名测试）；方向与任务类型显示的其余覆盖在不带切片标记的单元测试里，随全量 L1 跑，不在门禁的片选里。
- **staged G3 本轮没有结论**（第四节），`FP-IS-00`、`06`、`07`、`14`、`15` 的 staged 面等修复后重跑。
- **真装置只实测一个管理员恢复入口**（「补偿清空」），其余三个入口由 G2 覆盖（onboard-hmi#115、#120、#124）。
- **G3 `FP-IS-10`／`11` 的判据**（control-server#164）：「全程只出现一种任务类型」只在四个点采样，不是连续监视；路线证据是哈希，G3 读不出起终点。
- **control-server#162 的三份 L2 红证据**跑在合入 #161 之前的 `d223b14e`，没有在新代码上重取。
- **control-server#159 负向场景**五条判据里只有 `L2-TTSR-03`／`04` 有区分力，另三条被唯一索引兜底。
- **control-server#193 普查**改了六个场景的同型取样，没做故障注入；两处「拿不准」和四处「相邻形状」没改（`L2-SCA-09`、`g3-pickup-load-and-correction`、`L2-DC-12` 可能假绿、`L2-NL-03`、`L2-RW-02`）（PR #194 已合入 `905ffd1d`，这两类仍未改）。
- **向量弱绑定**（第五节第 7 点）；**合成 L2 与真装置都不证明真实硬件**（光幕极性、锁反馈时序、机械弹开），control-server#44 的极性对齐仍是切生产门槛。
- **G2 证据作废**：onboard-hmi#123、#124 作废车载端 `FP-IS-02`／`03`／`07` 的既有 G2 证据；本批出口只重出 `FP-IS-10`／`11`，其余切片在下一次出口或切 RC 时按顶端重出。

### 运维说明

- **站点删除或换 id 造成的暂停，要两步才解得开**：先 FieldOps「激活新版本」（绑到新站），再 FieldOps「解除暂停」（control-server#162／PR #182 审查）。
- **墓碑下置的人工暂停在下次激活前解不掉**：地图被手动关闭（墓碑，没有生效版本）时看板仍能下暂停，但要等下一次激活之后才能解除。
- **服务端的同机判定信任 TCP 对端地址**：看板暂停入口只收本机来的请求，判定看 TCP 对端地址，所以**不能与同机端口转发共存**——
  本机上任何把外部连接转发进来的代理都会让外部请求被当成本机。

## 七、转后续

已开票：

| 票 | 内容 | 状态 |
| --- | --- | --- |
| control-server#166 | 26 号图上建派工待送取货站点、按 `mapId + STAGING_TO_WIRE` 绑定并做现场用途核对 | open，后续批次 |
| control-server#186 | Map 级改名检测：RIoT 地图名存作基线 | open，后续批次 |
| onboard-hmi#127 | 在途装货断线重连后车载端补发结果（control-server#189 第二步）；接 onboard-hmi#124 转来的四条 | open，本批追加 |
| onboard-hmi#123 | 开锁前被挡的恢复命令回结果 | 已合入（PR onboard-hmi#125），本批追加 |
| control-server#193 | `L2-LN-01` 取样竞态 | 已合入（PR #194），本批追加 |
| program#125 | `DISPLAY_ADMISSION_BLOCK_REASON` 契约措辞（随 `protocol-v3.0.0`，program#115） | open |
| onboard-hmi#61 | 清单项数与腿数两处收窄 | open，批次 7 |

审查里提出、当时还没有开成票的：调度 2026-09-19 决定全部开票，由分票会话按模块合并开，都是独立票、待挂批次 7：

| 新票 | 来源与内容 |
| --- | --- |
| control-server#198 | control-server#160（PR #188 审查 ①～④）：目录码守卫只探得到已知五种形状；计划 `StationCatalogRevision` 为空时静默跳过落点冻结；`TASK_TYPE_BINDING_CATALOG_NOT_FRESH` 缺中文说明；「受理被拒 → 改绑」测试缺同轮其它任务类型不受影响的断言。`Invoke-L2Scenario.ps1` 仍传两个无人读的关卡旧环境变量。control-server#163（c）前两条：存储层不校验准入任务类型等于需求 `WorkType`；缺「`STAGING_TO_WIRE` 暂停时第二腿不建」的测试。**反向旅程到机台时准入被撤的无限等待（第六节）：用户已定加超时后升级** |
| control-server#199 | control-server#159：迁移 `Down()` 没有「迁下去再迁回」的测试；control-server#161（PR #183 审查 O1）：审计表加 `BEFORE UPDATE/DELETE` 触发器 |
| control-server#200 | control-server#191（PR #192 审查 a～d）：对账审计记写入后的指针状态；看板 `CLOSED_MANUALLY` 措辞；补「结果未知、有生效版本、unattributed 回 ACTIVE」测试；核对 `GovernanceStore` 四个调用方；control-server#162 解除路径的 `ReleasedBy` 对齐 |
| control-server#201 | control-server#162（PR #182 审查 C、D、F、G 与补测）：目录变化记录按（站点, 修订）幂等在共享地图上会重复记行；`ApplyAsync` 抛异常中断整轮；`L2-CC-07` 判据偏弱；本地证据 `batchId` 为默认值；本机地址为 null、来源非回环时应 403 的测试；请求体在来源判定之前解析；部署文档写明同机判定不能与端口转发共存（本报告第六节已写运维说明） |
| control-server#202 | control-server#167：一次关门服务端约 1 秒内发 4 次快照请求；control-server#187 审查 B、C |
| control-server#203 | control-server#164 六条：`G3-10-04` 快照时序收紧并补红；`G3-11-07` 重算路线证据直证起终点；读机台停靠行前先等清单确认；`L2TaskTypeJourney.psm1:240` 查询缺 `ORDER BY`；补 `G3-11-08`、`G3-10-07` 两份红；`rig-runs.log` 改单一写入者 |
| control-server#204 | control-server#167：`L2-EAO-13` 加「端点读数版本号前进」判据、门槛重复写两处、red-05 两条缺席；control-server#196（a～d）：单元素读名失败仍计数、UIA 持续抛异常拖成等待超时可能假红、journal「Not reached」措辞、README 状态列 |
| onboard-hmi#128 | 假服务端替身在车非 Ready 时仍发快照 |
| onboard-hmi#129 | onboard-hmi#123 审查 B、C 与复审 |
| onboard-hmi#132 | onboard-hmi#127（PR onboard-hmi#131）审查的转后续 1～4 |
| control-server#205 | 一次性场景 `real-onboard-inflight-load-reconnect`（onboard-hmi#131 的副本）转为 `scripts/l2/scenarios/` 的正式场景 |
| onboard-hmi#130 | onboard-hmi#115：`docs/LOCAL_G2_EVIDENCE.md:42` 的已实现切片清单未更新（用户定车载端 `docs/` 按我方文档维护） |

不开票的一条：

- control-server#162 审查里「403 结果码 `FORBIDDEN_NOT_LOOPBACK` 改名为 `FORBIDDEN_NOT_LOCAL` 需知会外部脚本」：旧码从未进主线，没有外部依赖，不开票（调度决定）。
