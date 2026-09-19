# 批次 6 出口报告（v2 线）：任务类型与固定站点绑定（`FP-IS-10`、`FP-IS-11`）

control-server#165（批次6-09）。本报告逐项对照规格 `8005-agv-program/docs/specs/full-product-scope-and-sequence-v2.md` 第 8.3 节批次 6 行
（第 19 节、第 21 节补记优先；地图号按第 21.5 节读作 26）。需求条目按基线 **`v1.4.0`**（tag `requirements-baseline-v1.4.0`，359 条）引用。
本批不改协议，门禁与证据绑 `protocol-v2.0.0`。

> **状态：准备中。**正式证据（两端 G2、G3、CI 三连、真装置三连、两端全量 L1）要在批次 6 追加票全部合入后的两端集成分支顶端上取，
> 见「出口顶端」一节。本版只有不占真装置的准备：前置核对、判据与证据来源对照、`l2.yml` 三连改动、新能力与测试对照、剩余风险与转后续汇总。
> 标 〔待取〕 的格子在开跑后填。

## 结论

〔待取〕

| 出口（规格 8.2／8.3 批次 6 行、本票验收） | 状态 | 依据 |
| --- | --- | --- |
| 前置核对逐项通过；六条判据与场景对照表完整 | 前置已核（下一节）；v2 克隆与本机空闲两项开跑时再核 | 「前置核对」「二、L2」 |
| L1：两端测试套件在出口提交上全绿；新能力逐项有新增测试 | 〔待取〕；对照表已列 | 第一节 |
| `FP-IS-10`、`FP-IS-11` 四门禁全 PASS，`gate-result.json` 绑 `protocol-v2.0.0` 精确身份 | 〔待取〕 | 第三节 |
| G3 按 control-server#164 的归属出证，两片 `formalSlicePass` 由断言算出；同一次 journey 运行里既有场景全绿 | 〔待取〕 | 第三节 |
| `l2.yml` 批次 6 区块 `Runs = 3`；CI 上批次 6 全部场景连续三次通过，证据独立入库 | `Runs = 3` 已改（`7872001e`）；三连〔待取〕 | 第二节 |
| 六条机制判据各有证据目录 | 场景已对上；证据目录〔待取〕 | 第二节 |
| 两端向量等待名单里没有 `CV-TASK-TYPE-ADMISSION-FAIL-CLOSED`、`CV-REVERSED-DIRECTION-JOURNEY` | **成立**（现顶端已核，出口顶端再核一次） | 第一节末 |
| 每次门禁 `-Output`／`-EvidenceRoot` 新目录；红证据保留，`docs/defects/` 有记录 | 〔待取〕 | 第四节 |
| 十三点如实写明，无第 8.8 节禁用表述 | 已写（第五节） | 第五节 |
| 未切换 `C:\Users\szy\Desktop\8005-workspace\repos\` 下任何克隆 | 至今成立 | 全部操作在 `8005-workspace-v2` |
| 本 PR 的 CI `test` 与 `l2` 两项绿 | 〔待取〕 | PR |

## 前置核对（2026-09-19 实查）

| 项 | 结果 |
| --- | --- |
| control-server#90（批次 5 出口）已关闭 | 成立 |
| 批次6-01～08 全部合入：control-server#158（PR #172，`af49ce64`）、#159（PR #171，`c9752b90`）、#160（PR #188，`7261ed6a`）、#161（PR #183，`aa17b059`）、#162（PR #182，`90433957`）、#163（PR #178，`6b5d3adf`）、#164（PR #176，`93558a07`）；车载端 onboard-hmi#115（PR #117，`3547a97`）进 `w2g/fp-v2-impl` | 成立，均已关闭 |
| 批次 6 各票加进 `l2.yml` 的场景（`BatchId = 'batch-6'`）与六条判据逐条对上 | 成立：8 个场景，见第二节对照表；`scripts/l2/scenarios/` 下没有不在清单里的批次 6 合成场景 |
| v2 工作区四个仓在要绑定的提交上、工作树干净；本机没有其它真装置 L2 或 G3 在跑 | 〔开跑时核〕 |
| control-server#166（批次6-10，派工待送取货站点建站与绑定） | **open**，已移出批次 6、放到后续批次（调度 2026-09-19 登记的用户决定）；不挡出口，见第五节第 1 点 |

### 出口顶端：追加票

批次 6 在前置之外还有追加票，出口正式证据要等它们合入后在两端顶端上取：

| 票 | 仓 | 内容 | 状态（2026-09-19） |
| --- | --- | --- | --- |
| onboard-hmi#123 | 车载端 | 开锁前挡住补偿、受控取货、强制取出命令时不回结果，服务端会话停在 `EXECUTING`（control-server#187 审查发现） | open |
| onboard-hmi#124 | 车载端 | 已完成的装货因结果确认没回来被报成「上次操作未完成」；重启后遗留 attempt 与重发命令竞态不结算 | 车载端 PR #126 已合入（`2b04729`）；服务端场景判据 PR 未合入 |
| control-server#193 | 服务端 | `load-command-never-answered` 的 `L2-LN-01` 取样竞态 | open |
| onboard-hmi#127 | 车载端 | 在途装货断线重连后车载端不发结果，与服务端互相等（control-server#189 第二步；#189 第一步 PR #195 已合入 `5a126238`） | open，等 #124 服务端 PR 合入后开工 |

## 身份

| 项 | 值 |
| --- | --- |
| 协议 | `(AGV_FULL_PRODUCT, 3)`，`releaseVersion 2.0.0`，tag `protocol-v2.0.0` → `86575456c847041515b7b75e8851a00e0d939804`；本批零改动 |
| `ProtocolReleaseIdentity` 其余字段 | manifest `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7`，schema bundle `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221`，vectors `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a`，`APPROVED_RELEASE` |
| 服务端产品代码 | 〔出口时的 `fp/v2-impl` 顶端〕 |
| 车载端 | 〔出口时的 `w2g/fp-v2-impl` 顶端〕 |
| 模拟器 | `main@fb5f7c59`（不变） |
| G3 共享绑定 | 〔`chore(g3)` 提交〕：`ControlServerCommit` 〔`fp/v2-impl` 顶端〕、`OnboardCommit` 〔`w2g/fp-v2-impl` 顶端〕、`SimulatorCommit fb5f7c59`、`ProtocolCommit 86575456`。现绑 cs `d3003c2f`／onboard `29fbf65e`，后者早于 onboard-hmi#115，runner 要求 `OnboardCommit` 等于 `origin/w2g/fp-v2-impl` 顶端，所以非移不可；tag 字面量（`scripts/run-staged-g3.ps1:2020`、`:2129`）与 `run-demand-bearing-g3-vectors.ps1:638` 已是 `protocol-v2.0.0`，不动。移绑定使引用它的 G3 结果全部失效，按 control-server#90 先例四个 runner 都重跑 |

## 一、L1

### 实测

〔待取〕服务端全量走 CI `test.yml`（出口提交），车载端全量经 `Invoke-HeavyLocal.ps1 -Ticket cs#165` 在出口顶端跑。

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
| control-server#191 | `REQ-0337`、`0347` | 墓碑（手动关闭）之后被中断的空需求集激活，对账回到墓碑、重启不装预置 | `TaskTypeStationActivationTests.AnInterruptedActivationOfAnEmptyRequirementSetFromATombstoneReconcilesBackToTheTombstoneAndARestartKeepsThePresetOut` 等（5） | PR #192 `a2369ce0` |
| onboard-hmi#115（批次6-03） | 车载半边（`FP-IS-10`／`11`） | 放开非 `WIRE_TO_GATE` 任务的入站校验；按计划显示方向与任务类型；不从计划或 `blockingFacts` 推断未绑定的任务类型 | `TaskTypeAndDirectionVectorG2Tests.AReversedJourneyShowsTheDirectionAsPlanned`（`CV-REVERSED-DIRECTION-JOURNEY`）、`.AnUnboundTaskTypeIsNeverInferredFromThePlanOrTheBlockingFacts`（`CV-TASK-TYPE-ADMISSION-FAIL-CLOSED`）、`InboundPayloadSchemaBoundaryTests.EveryWorkTypeTheSchemaDeclaresIsAccepted`、`WireToGateStopFactsTests.TheSameStopRoleShowsTheSameDirectionWhateverTheTaskType` 等（17） | PR #117 `3547a97` |

`REQ-0336`（两级管理员分工）延后，本批没有它的实现与测试（第五节第 4 点）。`REQ-0359` 不在本批（第五节第 5 点）。

批次 6 期间在同一集成分支上合入、但不属于批次 6 新能力的修复（恢复与会话路径，来自批次 5 遗留或批次 6 审查）：
control-server#169（PR #173 `911ee2ef`，8）、#175（PR #177 `b740d319`，9）、#180（PR #185 `75c3d39e`，3）、#187（PR #190 `c1252932`，7）、
#167（PR #181 `169dfa42`，真装置场景 `real-onboard-expected-action-overdue`）、#189 第一步（PR #195 `5a126238`，复现与结论）、#179（PR #184 `969f883d`，26 号图只读核实记录）；
车载端 onboard-hmi#120（PR #121 `7ded1b7`，3）、#119（PR #122 `4a6790e`，12）、#124（PR #126 `2b04729`，5）。
它们的剩余风险见第六节。

### 向量绑定

两端 `ProtocolVectorTestBindingArchitectureTests` 的 `VectorsAwaitingTheirSlice` 各剩 7 条，全部属于批次 7～11 的切片（`FP-IS-08`、`09`、`12`、`13`）；
`CV-TASK-TYPE-ADMISSION-FAIL-CLOSED` 与 `CV-REVERSED-DIRECTION-JOURNEY` 已不在名单里（现顶端 `5a126238`／`2b04729` 实读），各有同名具名测试：

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

计划清单〔申请时段时定稿〕：`real-onboard-expected-action-overdue` × 3；批次 5 出口的七条（control-server#86 三条、#88 四条）各 × 1 作回归；
onboard-hmi#124 服务端场景判据 PR 与 onboard-hmi#127 若新增真装置场景，一并各 × 3。

## 三、门禁

〔待取〕按票面步骤表，在出口顶端依次：

| 步 | 做什么 | 证据目录（新） |
| --- | --- | --- |
| 1 | 移 G3 共享绑定（单独一个 `chore(g3)` 提交） | — |
| 2 | `CONTROL_SERVER_G2` × 2（`FP-IS-10`、`FP-IS-11`） | `evidence/g2/<日期>-protocol-v2.0.0-<提交>/` |
| 3 | `ONBOARD_HMI_G2` × 2，车载端仓 `run-w2g-g2.ps1 -Slice`，证据经 `w2g/` 小 PR 进 `w2g/fp-v2-impl` | 车载端仓 `evidence/g2/<日期>-protocol-v2.0.0-<提交>/` |
| 4 | G3 四个 runner：staged、restart、需求承载、journey（14 场景，`FP-IS-01`／`02`／`03`／`07`／`10`／`11`，一轮约 19.5 分钟） | `evidence/g3/<日期>-protocol-v2.0.0-*-<提交>/` |
| 5 | CI `l2.yml` `consecutive-all` | 见第二节 |
| 6 | 本 PR 默认 CI（`test`、`l2`） | PR 检查页 |

`FP-IS-10`、`FP-IS-11` 的 G1 是协议侧发布证据：`protocol-v2.0.0` 的 G1 在发布态通过（program#97，`evidence/g1/20260916-protocol-v2.0.0-release-8657545/`），本批协议零改动，不重跑；
车载端 `ONBOARD_HMI_G2` 每片会在发布态重跑协议 G1。

## 四、红证据与缺陷单

〔待取〕目前没有红。

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
    其它票零迁移：`1f5efed1..5a126238` 在 `Migrations/` 下只有这一张新增与模型快照的对应修改。〔出口顶端再核一次〕
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
    用户定走方案一（加一次迁移，把 RIoT `mapInfo/{mapId}` 的 `name` 存作基线），不进批次 6，由分票会话单开一张票挂到后续批次〔票号待补〕。
    现状兜底：`VehicleDynamicFactsCriterion` 挡车辆地图名不符；Map 删除或换 `mapId` 由目录新鲜度门禁整图阻断。
15. **control-server#166（人工建站票）移出批次 6**，见第 1 点。

## 六、剩余风险

〔汇总中〕

### 运维说明

- **站点删除或换 id 造成的暂停，要两步才解得开**：先 FieldOps「激活新版本」（绑到新站），再 FieldOps「解除暂停」（control-server#162／PR #182 审查）。
- **墓碑下置的人工暂停在下次激活前解不掉**：地图被手动关闭（墓碑，没有生效版本）时看板仍能下暂停，但要等下一次激活之后才能解除。
- **服务端的同机判定信任 TCP 对端地址**：看板暂停入口只收本机来的请求，判定看 TCP 对端地址，所以**不能与同机端口转发共存**——
  本机上任何把外部连接转发进来的代理都会让外部请求被当成本机。

## 七、转后续

〔汇总中〕
