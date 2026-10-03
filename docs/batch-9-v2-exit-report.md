# 批次 9 出口报告（v2 线）：自动充电（`FP-IS-13`）

control-server#412（批次9-14）。本报告逐项对照规格 `8005-agv-program/docs/specs/full-product-scope-and-sequence-v2.md` 第 8.3 节批次 9 行，
需求条目按基线 **`v1.7.0`** 引用（各票票面的基线）。本批不改协议，门禁与证据绑 `protocol-v2.0.0`。

> **状态：出口达成，待调度审查。**journey G3 第一轮红在场景判据 G3-13-13（读一个在清桩结束时必被清空的界面行，不是产品缺陷）；
> 判据改写并经变异验证后，在新绑定上从头重跑，全部 PASS。第一轮红证据全部保留（第四节）。需求承载 G3 本轮免跑（外场库已不存在，第三节）。

## 结论

**批次 9 出口达成。**第 8.3 节批次 9 行的四项在 `protocol-v2.0.0` 发布身份上成立：

- `FP-IS-13` 四门禁 PASS：G1 引 `protocol-v2.0.0` 发布态证据（program#97）；`CONTROL_SERVER_G2` 263/263；`ONBOARD_HMI_G2` 18/18；G3 由 journey 的三个场景认领，`formalSlicePass true`。
- G3：staged、restart、journey 三个 runner 全绿（journey 19/19、146 条断言，九片 `formalSlicePass true`）；**需求承载 runner 本轮未跑**（第三节）。
- CI 合成 L2 `consecutive-all`：79 个场景各三遍，237/237 PASS（批次 9 的 12 个场景 36 次）。
- CI 真装置：22 个场景 28 次全部 PASS，`real-onboard-charging-cycle` 连续三遍。
- 两端全量 L1：车载端 1292/1292（`4e40e196`）；服务端取本 PR 的默认 CI（第一节）。
- 服务端部署包在出口提交上打得出来（第三节第 8 步）。

产品身份：服务端 `fp/v2-impl@8467480d`，车载端 `w2g/fp-v2-impl@4e40e196`（证据小 PR 合入后顶端 `17043d05`，`evidence/` 以外 0 个文件），模拟器 `fb5f7c59`，协议 `86575456`。

**本报告不写任何「现场已验证」。**10-08 上真车在本出口之后，见第五节第 2、3 条。

| 出口（规格 8.2／8.3 批次 9 行、本票验收） | 状态 | 依据 |
| --- | --- | --- |
| 前置核对逐项通过；身份按表判定，「身份未变」逐项核对通过 | 成立 | 「前置核对」「身份」 |
| `FP-IS-13` 两端 G2 均 PASS，`gate-result.json` 绑出口身份精确值；车载端证据小 PR 已合入，时点符合冻结 | 成立：服务端 263/263；车载端 18/18；onboard-hmi PR #244 在 G3 全部跑完后合入（`17043d05`） | 第三节 |
| G3 四个 runner 在出口绑定上各一轮全绿；`FP-IS-13` 的 `formalSlicePass` 由断言算出 | **三个成立、一个未跑**：staged、restart、journey 全绿；需求承载因外场库缺失本轮免跑（调度定） | 第三节 |
| 真装置默认清单全 PASS；`real-onboard-charging-cycle` 连续三遍 PASS、三份证据各自独立 | 成立：run `37013472279`，28/28 | 第二节 |
| `l2.yml` 批次 9 各行 `Runs = 3`（核对脚本先红后绿、变异各红在预期处）；CI `consecutive-all` 全 PASS，逐份核对 | 成立：run `37023804697`，237/237 | 第二节 |
| 第 8.3 节批次 9 行四条 L2 判据各有证据目录，正事实逐条指到 L2 id | 成立 | 第二节对照表 |
| 服务端部署包在出口提交上打得出来 | 成立 | 第三节 |
| 冻结期间两端集成分支没有合入其它 PR | 成立：冻结自 2026-10-02 起，期间只合入车载端证据小 PR #244（G3 之后） | 「身份」 |
| 每次门禁新目录；红证据保留，`docs/defects/` 有记录 | 成立 | 第四节 |
| 「必须如实写明」十五点，不出现第 8.8 节禁止的表述，不把告警推送写成已有能力 | 已写 | 第五节 |
| 未切换 `C:\Users\szy\Desktop\8005-workspace\repos\` 下任何克隆 | 成立 | 全部操作在 `8005-workspace-v2` |
| 本 PR 的 CI `test` 与 `l2` 两项绿 | 转 ready 后的默认一轮，见 PR 检查页 | PR 检查页 |

## 前置核对（2026-10-02 实查）

| 项 | 结果 |
| --- | --- |
| 本批各票合入 | 成立。服务端 cs#399（PR #416 `b611688e`）、#400（PR #420 `99c35544`）、#401（PR #415 `f55669db`）、#402（PR #414 `3fc3587b`）、#403（PR #427 `e96341bb`）、#404（PR #430 `ed61ead8`）、#405（PR #433 `e8d08d77`）、#406（PR #437 `7f672635`）、#407（PR #442 `283db3ee`）、#408（PR #441 `967fa668`）、#409（PR #446 `8467480d`）、#410（PR #445 `6281bdab`）；车载端 onboard-hmi#220（PR #223 `59dd5452`）、#221（PR #229 `b31e5196`）、#222（PR #235 `c5b2f9fa`） |
| riot-sdk Facade PR 合入、服务端 vendor 是 `0.2.0-fp.4` | 成立：riot-sdk PR #2 合入 `fp/v2-facade`（`073e40fd`）；`Directory.Packages.props:21` 为 `RIoT.Sdk.Facade 0.2.0-fp.4`，包在 `vendor/nuget/riot-sdk/0.2.0-fp.4/` |
| 挂 program#162 的其余非人工票全部关闭 | 成立：子票里只有 cs#411（人工）与本票 open |
| 身份按表判定 | `protocol-v2.0.0` 一行：cs#393 未合入，协议仓没有 `protocol-v3.0.0` tag（见「身份」） |
| 两端 `VectorsAwaitingTheirSlice` 无 `FP-IS-13` 向量、`VectorsThisBatchOwesANamedTest` 为空；`FP-IS-13` 进两端已实现集合；四条充电确认消息的钉已删 | 成立。两端 `VectorsAwaitingTheirSlice` 只剩 `CV-WORKLIST-SELECTION-*` 两条（`FP-IS-09, batch 11`）；车载端 `VectorsThisBatchOwesANamedTest` 为空（服务端没有这个字段）；服务端 `SlicesThisLineImplements` 含 `FP-IS-13`（00～08、10～15，共 15 片），车载端 `IntegrationSliceTraitArchitectureTests.cs:137` 断言 15；两端 `ProtocolMessageSurfaceArchitectureTests` 只剩 `DemandSelection*` 两条（`FP-IS-09, batch 11`） |
| `l2.yml` 中 `BatchId = 'batch-9'` 的行与各票逐条对上 | 成立：12 行，与批次 9 各合并提交新增的合成场景文件逐一相等（第二节） |
| 真装置清单里有 `real-onboard-charging-cycle` | 成立（另有 `real-onboard-manual-charging-hold-return`） |
| v2 工作区 `repos/` 工作树干净；不切换 `8005-workspace\repos\`；没有其它真装置或 G3 在跑 | 成立：五个克隆都在基线分支、零改动；本机无 L2／G3／G2 进程；对端用 detached worktree，跑完已删 |
| cs#411（人工） | open，不挡出口，见第五节第 4、5 条 |
| 顶端兜底（`8467480d`） | l2 run `37006675764` PASS；test run `37006671205` 1/4120 红：`ExpectedActionOverdueTests.TheEndpointAnswersAGetOverHttpAndTheCardRendersWhatItReturns`（`Assert.Single() Failure: The collection was empty`）。调度判为 cs#448 的偶发（同一棵树 `c51781f9` 的 run `37003452808` 全绿），列入剩余风险 |

## 身份

| 项 | 值 |
| --- | --- |
| 协议 | `(AGV_FULL_PRODUCT, 3)`，`releaseVersion 2.0.0`，tag `protocol-v2.0.0` → `86575456c847041515b7b75e8851a00e0d939804`；本批零改动 |
| `ProtocolReleaseIdentity` 其余字段 | manifest `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7`，schema bundle `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221`，vectors `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a`，slice index `268ce62be4e0ec8fe6d26e2048a59cf1732f02713415a0c5f41b6b009951b6fc`，`APPROVED_RELEASE` |
| 「身份未变」逐项核对 | tag 指向 `86575456`；发布态 `manifest/release.json`、服务端 `vendor/8005-agv-protocol/manifest/release.json`、车载端同名文件三者 SHA-256 都是 `4ac095ad…22a7`；身份常量服务端 `ProtocolCandidateIdentity.cs:52-53`、车载端 `WireToGateProtocol.cs:62-64` 都是 `2.0.0`／`protocol-v2.0.0`；两端 G2 的 `gate-result.json` 的 `protocolTag`、`protocolRepositoryCommit`、`protocolManifestSha256`、`protocolApprovalStatus APPROVED_RELEASE` 与之相等 |
| 服务端产品 | `fp/v2-impl@8467480d`。本分支相对它 `git diff 8467480d <分支> -- src tests tools` 为 0 行，只改 G3 runner 与认领表、一个 G3 场景的判据、`l2.yml` 的 `Runs`、证据与文档 |
| 车载端 | `w2g/fp-v2-impl@4e40e196`；证据小 PR #244 在 G3 之后合入，顶端 `17043d05`，相对 `4e40e196` 在 `evidence/` 以外 0 个文件（调度与出口会话各自核过） |
| 模拟器 | `main@fb5f7c59`（不变） |
| G3 共享绑定（第一轮） | `65a698c2`（`chore(g3)`）：`ControlServerCommit 8467480d`、`OnboardCommit 4e40e196`、`SimulatorCommit fb5f7c59`、`ProtocolCommit 86575456`；tag 字面量 `protocol-v2.0.0` 只核对未动 |
| **G3 共享绑定（出口）** | `438ca0aa`（`chore(g3)`）：`ControlServerCommit 8d0a644e`，其余三个不变。理由：journey runner 的场景脚本取自绑定的服务端提交（`run-journey-g3.ps1:25`），改写后的 G3-13-13 要进绑定；`8467480d..8d0a644e` 在 `src`、`tests`、`tools` 下 0 行，产品即 `8467480d`（调度同意） |
| 冻结 | 2026-10-02 起冻结 `fp/v2-impl` 与 `w2g/fp-v2-impl`；期间只合入车载端证据小 PR #244（G3 全部跑完后），服务端集成分支没有合入任何 PR |

## 一、L1

| 端 | 来源 | 结果 |
| --- | --- | --- |
| 车载端 | 车载端 CI `test`，push 运行 [`36998672629`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/actions/runs/36998672629)，`headSha 4e40e196`（与绑定相同） | **1292／1292 通过**，0 失败 0 跳过（`SQCD.Agv.UnitTests` 678、`SQCD.Agv.WireToGateG2Tests` 614，取自该运行上传的 `dotnet-test-release.log`）；同一作业 `ONBOARD_HMI_G2`（全量）与 UI layout audit 都是 `Status: PASS` |
| 服务端 | 本 PR 转 ready 后的默认 CI `test` | 见 PR 检查页；`8467480d` 上的兜底一轮是 4119/4120（上表，cs#448） |

### 批次 9 每项新能力 ↔ 新增测试

剖面 `Batch = BATCH-9` 共 19 行，全部是 `FP-C1`（`REQ-0170`～`0180`、`REQ-0281`～`0288`），外加 `REQ-0208` 的电量半边（规格第 19.5 节）。
括号里是该合入新增的 `[Fact]`／`[Theory]` 属性行数（`git diff <merge>^1 <merge> -- tests | grep -cE '^\+.*\[(Fact|Theory)'`，删除行全部为 0），
不含 `InlineData` 展开，也看不到改动的既有用例；代表方法是挑的。服务端 12 个合入共 415，车载端 3 个合入共 106。

| 票 | 需求条目（`v1.7.0`） | 新能力 | 新增测试（代表） | 合入 |
| --- | --- | --- | --- | --- |
| control-server#399（批次9-01） | 为 `REQ-0171`、`0173`、`0176`～`0179`、`0281`、`0282`、`0285`、`0288` 建表 | 本批唯一一次迁移：名册与策略版本、`CHARGER` 站点独占、充电周期、两类暂停、清桩、人工充电等待、两类现场确认；引擎零行为变化 | `Batch9MigrationDisciplineTests.WithJourneysInFlightEveryExistingRowAndColumnStaysExactlyAsItWasAndTheNewTablesStartEmpty`、`Batch9PersistencePortTests.TwoVehiclesReservingTheSameChargerWithoutReadingFirstOnlyOneHoldsIt`（34） | PR #416 `b611688e` |
| control-server#400（批次9-02） | `REQ-0171`、`0282`、`0288` | 名册与策略的受治理导入（置空与启用同一动词）；策略导入、批准、激活三步留痕；无已批准策略的车逐车不投运 | `ChargerRosterImportTests.NoStationIsRecognisedOrRefusedAsAChargerByItsName`、`MultiVehicleExecutionTests.AVehicleThePolicyDoesNotCoverIsRefusedWithTheNewReasonAndTheOtherVehicleTakesTheDemand`（39）；L2 `charging-policy-missing-vehicle-not-commissioned` | PR #420 `99c35544` |
| control-server#401（批次9-03） | `REQ-0283`、`0174`、`0175` | RIoT 充电建单（move ＋ `act(78,1,0)`），读出 act 结果码与 `HANG` | `HttpRiotChargingOrderGatewayTests.AChargeIntentCreatesAMoveToTheChargerFollowedByTheStartChargingAct`、`RiotChargingOrderContractTests.AChargeOrderThatCannotChargeReadsBackHangAndTheActResultCodeRaw`（27） | PR #415 `f55669db` |
| control-server#402（批次9-04） | `REQ-0174`、`0175`、`0285`、`0287`（取证能力） | 假 RIoT 充电模拟与注入（407802＋`HANG`、只 `HANG`、中断、不涨、读不到） | `FakeRiotChargingTests.CannotChargeHangsTheOrderAtNineWith407802AndTheVehicleNeverReportsCharging`、`.WithNothingRegisteredASingleMoveOrderIsAnsweredByteForByteAsBefore`（22） | PR #414 `3fc3587b` |
| control-server#403（批次9-05） | `REQ-0281`、`REQ-0208`（电量半边）、`0290`、`0282`、`0169` | 三阈值读策略快照、关系在导入与启动时拒绝；预计任务后余量判资格；强制充电优先 | `BatteryThresholdEligibilityTests.EachBatteryBoundaryAnswersItsOwnReason`、`ChargingPolicyStartupCheckTests.AnActiveVersionThatBreaksTheRelationRefusesToStart`（33）；L2 `charging-thresholds-relation-refused`、`mandatory-charge-vehicle-takes-no-transport` | PR #427 `e96341bb` |
| control-server#404（批次9-06） | `REQ-0170`～`0173`、`0283`、`0290`（充电半边）、`0169`、`0360`／`0361` | 候选只取名册子集；按电量统一排队、失败车不插队；同一次保存取得用途与预占才建单；名册为空退化 `ManualChargingHold` 并告警 | `ChargingAllocationTests.WithOneChargerAndThreeVehiclesTheLowestBatteryGetsIt`、`.TwoVehiclesCommittingToOneChargerAtTheSameMomentLeaveOnlyOneWithAnything`（71）；L2 `charging-one-charger-three-vehicles-contend`、`charging-registry-emptied-degrades-and-resumes` | PR #430 `ed61ead8` |
| control-server#405（批次9-07） | `REQ-0173`、`0281`、`0282`、`0287`、`0290` | 到桩且报 `CHARGING` 才开始；达完成阈值即 `COMPLETE`、放用途不放桩；离桩三项确认才释放；`CV-AUTOMATIC-CHARGING-CYCLE` 服务端具名测试 | `ChargingCycleProgressTests.AtTheCompletionThresholdTheCycleCompletesAndReleasesThePurposeInOneSaveWhileTheChargerStaysOccupied`、`.CvAutomaticChargingCycleNeverDispatchesDuringCharging`（38）；L2 `charging-full-cycle`、`charging-full-vehicle-yields-charger`；G3 `g3-automatic-charging-cycle`；真装置 `real-onboard-charging-cycle`、`real-onboard-manual-charging-hold-return` | PR #433 `e8d08d77` |
| control-server#406（批次9-08） | `REQ-0174`、`0175`、`0177`～`0180`、`0284`、`0288`、`0169` | 严格事实自动确认充不上并暂停桩，一般异常不暂停；车保持原位；人工清桩确认（R-11／R-13）；`CV-MANUAL-STATION-CLEARANCE` 服务端具名测试 | `ChargingUnableToChargeTests.StrictFactsSeenTwiceConfirmUnableToChargeAndPauseTheChargerInOneSave`、`ManualStationClearanceTests.TheRosterGrantsTheClearanceOnlyToR11AndR13`（48）；L2 `charging-unable-to-charge-pauses-charger`、`charging-general-fault-does-not-pause`；G3 `g3-manual-station-clearance` | PR #437 `7f672635` |
| control-server#407（批次9-09） | `REQ-0285`、`0286`、`0287`（边界）、`0282` | 中断或无进展即同时暂停桩与车的充电资格（完整版，第五节第 8 条） | `ChargingInterruptionTests.TwoFreshReadingsNotChargingBelowTheThresholdConfirmAnInterruptionAndPauseBothSidesInOneSave`、`.TheChargerAndTheVehicleEachRecoverOnTheirOwn`（38）；L2 `charging-interruption-isolates`、`charging-no-progress-isolates` | PR #442 `283db3ee` |
| control-server#408（批次9-10） | `REQ-0268`、`0269`、`0171`、`0177`、`0179`、`0285`、`0287`、`0288` | 看板显示充电状态、名册、两类暂停、清桩中、人工充电等待与告警 | `ChargingDashboardTests.AnEmptyRosterIsShownProminentlyWithTheVehiclesWaitingForManualCharging`、`ChargingDashboardRealRunTests.AVehicleWhoseRiotReadFailsThisRoundReadsAsNotEvaluatedRatherThanItsPreviousBattery`（28） | PR #441 `967fa668` |
| control-server#409（批次9-11） | `REQ-0178`、`0179`（系统证明）、`0180`、`0293`～`0296` | 清桩中的车在旧单终态后开往等待点，到点即完成清桩并放桩（开关出厂关，第五节第 15 条） | `ChargingClearanceToWaitingPointTests.TheClearingVehicleDrivesToAWaitingPointAndArrivingCompletesTheClearanceInOneSave`、`.AClearingVehicleAndAnIdleReturnRacingForTheLastPointLeaveExactlyOneWinner`（19）；L2 `charging-clearance-to-waiting-point` | PR #446 `8467480d` |
| control-server#410（批次9-12） | `REQ-0176`、`0177`、`0174`、`0175`、`0284` | 现场确认充不上（只认 R-11），服务端定 `chargingPolicyDecision`；`FP-IS-13` 进服务端已实施集合 | `UnableToChargeFieldConfirmationTests.OnlyR11ConfirmsAndAnyoneElseOnlyReports`、`.AFieldConfirmationRacingTheSystemOneMakesOneEventAndOneDecision`（18）；G3 `g3-unable-to-charge-field-confirmation` | PR #445 `6281bdab` |
| onboard-hmi#220（批次9-15） | `REQ-0281`、`0290`、`0173`、`0178`、`0287`（车载端） | 充电显示；`CHARGER` 停靠是非业务停靠、桩上禁装；录入门去掉电量一支；`CV-AUTOMATIC-CHARGING-CYCLE` 车载端 | `AutomaticChargingCycleG2Tests.AChargeIsShownOnTheWayAtTheChargerAndWhenFull`、`.ATransportUnderWayIsOfferedTheEntryWhateverItsBattery`（29） | PR onboard-hmi#223 `59dd5452` |
| onboard-hmi#221（批次9-16） | `REQ-0178`、`0179`、`0180`（车载端入口） | 人工清桩确认入口，不在本地放桩；`CV-MANUAL-STATION-CLEARANCE` 车载端 | `ManualStationClearanceG2Tests.TheEntryIsNotOfferedWithoutAVerifiedMaintainer`、`.AConfirmedClearanceChangesNothingOnTheVehicleUntilTheServersNextSnapshot`（32） | PR onboard-hmi#229 `b31e5196` |
| onboard-hmi#222（批次9-17） | `REQ-0176`、`0174`／`0175`、`0177`（车载端入口） | 现场确认充不上入口（出厂关），显示 Result；`FP-IS-13` 车载端翻为已实现 | `UnableToChargeFieldConfirmationG2Tests.EveryObservedConditionIsOfferedAndGoesOutAsChosen`、`WireToGateUnableToChargeTests.TheUnableToChargeEntryShipsSwitchedOff`（45） | PR onboard-hmi#235 `c5b2f9fa` |

剖面 19 行都有承载：单票承载的是 `REQ-0170`、`0172`（#404）、`0286`（#407）与 `REQ-0208` 电量半边（#403）。两处剖面与规格不一致，照实记：
`REQ-0208` 在剖面里的 `Batch` 仍是 `BATCH-4`，没有体现第 19.5 节的拆分；cs#404 引用的 `REQ-0360`／`0361` 在剖面里没有行（剖面止于 `REQ-0359`），基线 `v1.7.0` 里有，原因未查。

同期合入、不属于批次 9 新能力的：服务端批次 8 各票（#386～#392、#419 跟进）与修复 #375、#376、#380、#186、#395、#251、#428、#431、#435、PR #439（`test.yml` 超时）、PR #443（hmi#236 的服务端前提）；
车载端 hmi#217、#228、#230、#233、#236、#239、#242 与 cs#380 的车载端。hmi#242（PR #243）修的是批次 9 的显示，属审查后续。

### 向量绑定

| 向量 | 服务端 | 车载端 |
| --- | --- | --- |
| `CV-AUTOMATIC-CHARGING-CYCLE` | `ChargingCycleProgressTests`（`CvAutomaticChargingCycle*`） | `AutomaticChargingCycleG2Tests` |
| `CV-MANUAL-STATION-CLEARANCE` | `ManualStationClearanceTests` | `ManualStationClearanceG2Tests` |
| `CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION` | `UnableToChargeFieldConfirmationTests` | `UnableToChargeFieldConfirmationG2Tests` |
| `CV-MANUAL-CHARGING-RETURN` | 批次 6 起已有（FP-IS-07） | 同 |

**这只证明绑定存在**（第五节第 12 条）。

## 二、L2

### 第 8.3 节批次 9 行 ↔ 场景 ↔ 证据

证据都在 `evidence/l2/`；CI 三连 run `37023804697`（提交 `6b7b3e38`，产品即 `8467480d`）。每条都断言了正事实，不是「不触发即通过」（规格 8.3、8.5 节）。

| 判据 | 场景（票） | 断言正事实的 L2 id | 证据（三连） |
| --- | --- | --- | --- |
| 1 桩 3 车争用 | `charging-one-charger-three-vehicles-contend`（#404） | `L2-COC-01`（三车同时要充，恰好一台取得 `CHARGING` 用途与 211 预占，是电量最低那台，单号周期派生）、`L2-COC-02`（另两台报「211 已被预占」，什么都没有）、`L2-COC-04`（队里一台电量降到比预占者更低后仍不插队，预占者逐字未变、无取消命令）、`L2-COC-05`（十几轮预占不易手，211 上从不超过 1 辆）；前置 `L2-COC-00` | 3/3 PASS：`20261002-ci-37023804697-charging-one-charger-three-vehicles-contend-01`～`03/` |
| 1 桩 3 车充满让桩 | `charging-full-vehicle-yields-charger`（#405） | `L2-CFY-01`（A 充到 `COMPLETE`，用途放开、211 仍是 A 的，B、C 在队里无动作）、`L2-CFY-02`（需求派给充满的 A，此刻 211 仍是 A 的）、`L2-CFY-03`（A 离桩后 211 以 `CHARGER_RELEASED_ON_DEPARTURE` 释放，电量最低的 C 取得预占） | 3/3 PASS：`…-charging-full-vehicle-yields-charger-01`～`03/` |
| 名册只登记一个桩可正常运行 | `charging-full-cycle`（#405），另见上两条（名册只有 211） | `L2-CFC-01`（到桩报 `CHARGING`，预占转占用）、`L2-CFC-02`（过 80 即 `COMPLETE`，用途以 `CHARGING_COMPLETE` 释放，桩仍占用）、`L2-CFC-04`（下达下一单不放桩）、`L2-CFC-05`（离桩、不再充电、桩可确认空闲后以 `CHARGER_RELEASED_ON_DEPARTURE` 释放，周期 `CHARGING_DEPARTED`） | 3/3 PASS：`…-charging-full-cycle-01`～`03/` |
| 无已批准 `ChargingPolicyVersion` 的车辆不得投运（负向，逐车） | `charging-policy-missing-vehicle-not-commissioned`（#400） | `L2-CPM-01`（需求派给策略范围内的 A，不是排在前面的 B）、`L2-CPM-02`（B 以 `CHARGING_POLICY_NOT_APPROVED` 被挡，服务端日志）、`L2-CPM-03`（B 整个窗口没有任何建单——第二个事实）、`L2-CPM-04`（逐车判定，服务端照常运行、存活 200） | 3/3 PASS：`…-charging-policy-missing-vehicle-not-commissioned-01`～`03/` |
| 名册为空退化到 `ManualChargingHold` 且不静默 | `charging-registry-emptied-degrades-and-resumes`（#404） | `L2-CRE-03`（名册为空时需要充电的 B 进入服务端持有的人工充电等待 `ROSTER_EMPTY`，告警恰好一次，车载端确认 `manualChargingHold=true`；B 没有用途、预占、周期或建单——车不去任何桩）；另 `L2-CRE-02`（置空是同一动词、生成新空版本）、`L2-CRE-05`（返回服务后才解除）、`L2-CRE-06`（在充的 A 不受置空影响） | 3/3 PASS：`…-charging-registry-emptied-degrades-and-resumes-01`～`03/` |

批次 9 区块另有 7 个场景：`charging-thresholds-relation-refused`、`mandatory-charge-vehicle-takes-no-transport`（#403）、`charging-unable-to-charge-pauses-charger`、`charging-general-fault-does-not-pause`（#406）、`charging-interruption-isolates`、`charging-no-progress-isolates`（#407）、`charging-clearance-to-waiting-point`（#409），共 12 个，各 3/3 PASS。

### CI 三连

- `l2.yml` 批次 9 区块 12 行由 `Runs = 1` 改为 `Runs = 3`（`c1311678`）。改之前先提交核对脚本
  `evidence/l2/20261002-batch-9-exit-runs-check/Test-Batch9ConsecutiveRuns.ps1`（`a56288f2`）：执行 `l2.yml` 自己的 `$scenarios` 字面量，
  断言批次 9 恰好是 12 个场景（名单由批次 9 各合并提交新增的合成场景文件独立推出）、`scripts/l2/scenarios/` 下每个合成场景文件都在清单里、各 `Runs = 3`、不带 `DefaultRuns`、手动触发超时不少于 180 分钟。
  改动前 12 行 FAIL、退出码 1（`red/01-red-before-change.txt`），改动后 PASS（`green/02-green-after-change.txt`）；在改后的副本上做七种变异
  （批次标签、`DefaultRuns`、超时 170、未登记场景文件、删一行、名字改大小写、一行改回 `Runs = 1`），每种都红在预期那一条（`mutations/03-mutations-after-change.txt`）。
- **作业超时不改**：`workflow_dispatch` 仍是 180 分钟，本轮用了 4467 秒（约 74 分钟）。
- 运行：`gh workflow run l2.yml --ref b9-14/batch-9-exit -f mode=consecutive-all`，run [`37023804697`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/37023804697)，
  提交 `6b7b3e38`（产品即 `8467480d`），汇总标题 `L2 (consecutive-all, 4 lanes, 4467s)`，没有 `superseded`（日志里唯一一处是源码回显）。
  79 个合成场景各三遍，**237 次全部 PASS，中途没有红**。
- 下载后逐份核对：237 份 `assertions.json` 都是 `outcome PASS`、`identity.controlServerCommit 6b7b3e38`、`protocol-v2.0.0`／`APPROVED_RELEASE`、`rig SyntheticOnboard`；
  `identity.batchId` 与 `l2.yml` 逐行一致：`batch-2` 57、`batch-3` 6、`batch-4` 24、`batch-5` 15、`batch-6` 24、`batch-7` 57、`batch-8` 18、`batch-9` 36（12×3）。
- 入库：`evidence/l2/20261002-ci-37023804697-<场景>-NN/`，连同各自的 `.log`。精简：去掉 `control-server.out.log`、库文件与 `_stage`（745M → 约 171M）。

### 真装置

走 CI（`l2.yml` `rig=real`，vm01 交互式 runner）：`-f mode=consecutive -f batch_id=batch-9`，`onboard_ref 4e40e196…`、`simulator_ref fb5f7c59…`，
run [`37013472279`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/37013472279)（提交 `65a698c2`，产品即 `8467480d`），汇总 `Real-onboard L2 (consecutive, 28 runs)`，45 分钟。
清单里 `real-onboard-charging-cycle` 先改为 `Runs = 3`（`f73b1551`）。

| 场景 | 遍数 | 结果 |
| --- | --- | --- |
| `real-onboard-charging-cycle` | 3 | **3/3 PASS**，三份证据目录各自独立（`20261002-ci-37013472279-real-onboard-charging-cycle-01`～`03/`） |
| `real-onboard-durable-ack-lost`、`real-onboard-expected-action-overdue` | 各 3 | 各 3/3 PASS（`durable-ack-lost` 没有撞上 cs#307 形状） |
| `real-onboard-compensate-then-reconnect` | 1 | PASS（车载端 hmi#236、#239、#242 改过恢复路径与界面，这一条是它们的界面回归） |
| `real-onboard-manual-charging-hold-return` | 1 | PASS |
| 其余 17 条（`normal-load`、`clock-skew`、`load-door-closed-empty-reopens`、`station-timeout-door-open`、`unload-not-emptied`、`restart-while-waiting-operator`、`cancellation-authorization-lost`、`order-hang-continue`、`restart-with-open-recovery-session`、`restart-after-recovery-session-opened`、`mixed-side-one-stop`、`refilled-deadline-reaches-vehicle`、`rebuild-stopped-cargo-handoff`、`cancelled-rebuild-cargo-proof`、`stale-stop-after-station-timeout`、`in-transit-door-facts`、`unread-pre-create-read`） | 各 1 | 17/17 PASS |

三端读自 `Run real-onboard L2 scenarios` 那一行：`control-server @ 65a698c27ea263782e7b16805ff8c1be58d74e19`、`8005-agv-onboard-hmi @ 4e40e196205d55c363f97850d10bd18df9a686c0`、
`slots-simulator @ fb5f7c593742bf98bc3957b8729a38aad5321f28`；`RIG_COMMIT_GUARD|RIG_DESKTOP_LOCK|RIG_DEADLINE` 只命中 1 处，是源码回显。
28 份 `assertions.json` 逐份核对 `outcome PASS`、三端提交如上、`batchId batch-9`、`protocol-v2.0.0`／`APPROVED_RELEASE`、`rig RealOnboard`。
精简后入库（去掉 `control-server.out.log`、库文件与 `_stage`，127M → 28M）：`evidence/l2/20261002-ci-37013472279-*`。

**免跑判断（第五节第 14 条）：没有免跑。**28 次三端都在出口身份上，逐端读自 run 日志；没有沿用任何更早的真装置结论。

## 三、门禁

| 步 | 做什么 | 结果 | 证据目录 |
| --- | --- | --- | --- |
| 1 | 移 G3 共享绑定 | `65a698c2`（第一轮）；`438ca0aa`（出口，见「身份」） | — |
| — | 登记两个 G3 场景（越界例外，单独提交） | `f29a0ac1`：`g3-manual-station-clearance`（G3-13-11～14）、`g3-unable-to-charge-field-confirmation`（G3-13-21～27）进 journey runner 与 `FP-IS-13` 认领表，journey 17 → 19 个场景；认领表与 runner 名字双向一致（`Assert-G3ClaimCoversReport`，删一个名字会被抓住） | `docs/g3-slice-claim-review.md` 新增一节 |
| 2 | `CONTROL_SERVER_G2 -Slice FP-IS-13`，经 `Invoke-HeavyLocal.ps1` | **PASS**：选中 263 个测试全过；`implementationCommit 65a698c2`（产品即 `8467480d`）；`vectorIds` 四条 | `evidence/g2/20261002-protocol-v2.0.0-65a698c2/` |
| 3 | `ONBOARD_HMI_G2 -Slice FP-IS-13`（`4e40e196`），协议用 `86575456` 的普通克隆 | **PASS**：选中 18；build／test／format 通过；发布态重跑协议 G1 PASS（日志里 `approvalAttestationStatus` 为 `PENDING`，与批次 7 出口那份相同） | 车载端仓 `evidence/g2/20261002-protocol-v2.0.0-4e40e196/`（onboard-hmi PR #244，G3 之后合入，`17043d05`） |
| 4a | `run-staged-g3.ps1` | 第一轮 `STAGED_G3_RECOVERY_REPLAY_PASS`；**出口绑定 `STAGED_G3_RECOVERY_REPLAY_PASS`** | `evidence/g3/20261002-protocol-v2.0.0-staged-8467480d/`；`…-staged-8d0a644e/` |
| 4b | `run-staged-g3-restart.ps1` | 第一轮 `STAGED_G3_PROCESS_RESTART_PASS`；**出口绑定 `STAGED_G3_PROCESS_RESTART_PASS`** | `…-restart-8467480d/`；`…-restart-8d0a644e/` |
| 4c | `run-demand-bearing-g3-vectors.ps1` | **未跑**（见下） | — |
| 4d | `run-journey-g3.ps1`（19 场景） | 第一轮 `JOURNEY_G3_SLICE_FAIL` 145/146（第四节）；**出口绑定 `JOURNEY_G3_PASS` 19/19、146/146**，`FP-IS-01`／`02`／`03`／`07`／`08`／`10`／`11`／`12`／`13` 九片 `formalSlicePass true`，`commitSource SHARED_BINDING`，`runnerWorktreeCleanAtStart true` | `…-journey-8467480d/`（红）；`…-journey-8d0a644e/`（出口） |
| 5 | CI 真装置 | 28/28 PASS | 第二节 |
| 6 | CI `l2.yml` `consecutive-all` | 237/237 PASS | 第二节 |
| 7 | 本 PR 默认 CI | 见 PR 检查页 | — |
| 8 | 部署包（`New-WireToGateReleaseCandidate.ps1 -OnboardCommit 4e40e196… -OnboardBranch w2g/fp-v2-impl`，只打包不部署） | **成功**，58 秒：ControlServer `6b7b3e38`（产品即 `8467480d`），OnboardHmi `4e40e196`，`protocol-v2.0.0`；`release-manifest.json` SHA-256 `27de488991782f6462319830823f941531080e78e07fcaa9334f9a6666d1fd4c`，`SHA256SUMS` SHA-256 `8161f5c0222e12b9765cbea2a3bb3ce0c7a1c798a28a7a018dea949df7172de4`；密钥扫描 0、钥匙材料 0；扫描门禁 PASS（白名单内未定许可：`riot.sdk.core`／`facade`／`generated`） | 本机 `C:\w2g\cs412-rc`，不入库 |

**为什么四个 runner 都要跑**：本批改了派车、用途与建单，每个 runner 的派车都会经过。

**需求承载 G3 本轮未跑（调度 2026-10-02 定，先例 cs#387 09-29）**：`run-demand-bearing-g3-vectors.ps1` 必须传 `-FieldRunRoot`，一直用的外场库
`C:\Users\szy\w2g-stage\run\fullloop-20260829T131549Z` 已不存在，本机 `C:\Users\szy`、`D:\` 五层内没有副本。
**这样覆盖不到的**：这个 runner 认领 `FP-IS-04`（11 条断言）与 `FP-IS-05`（5 条），外加 4 条运行级断言（`docs/g3-slice-claim-review.md` 计数表），
即需求承载那几条向量在 G3 层的面；它们在批次 9 代码上的 G3 结论本轮没有。G2 与合成 L2 仍覆盖它们。外场库重建跟进 cs#453。

**`FP-IS-13` 的四道门禁**：G1 是协议侧发布证据——`protocol-v2.0.0` 的 G1 在发布态通过（program#97），本批协议零改动，车载端 G2 在发布态重跑了一次；
两端 G2 PASS；G3 由 journey 的 `g3-automatic-charging-cycle`、`g3-manual-station-clearance`、`g3-unable-to-charge-field-confirmation` 认领，18 条断言全过，`formalSlicePass true`。**四道全 PASS。**
`CV-MANUAL-CHARGING-RETURN`（也属 `FP-IS-13`）仍只以 `FP-IS-07` 的四个名字断言：给它另加 `FP-IS-13` 的名字要改既有场景，出口票不改，所以 `FP-IS-13` 的 `formalSlicePass` 由另三条向量的 18 条断言算出。

## 四、红证据与缺陷单

| 红 | 原因 | 处置 | 证据 |
| --- | --- | --- | --- |
| journey 第一轮 G3-13-13（`g3-manual-station-clearance`） | **场景判据的竞态，不是产品缺陷**：判据读车载端结果一行 `StationClearanceStatus`，车载端的设计是清桩一结束就清空它，而这里确认即结束（服务端随 Result 收尾），这一行只存在几毫秒。自 onboard-hmi#221 起就是如此，cs#406 那次绿是轮询赶上了窗口。最初判断为 onboard-hmi#222 引入，对照（车载端 `27b58310`）同样红，否定了这个判断 | 判据改为断言操作员被告知（`b0f070db`，调度批准的越界例外）：线路与应答不变；屏上操作记录列表出现「服务端已确认清桩…站点已释放」；车载端日志记下本次 `Confirmed`／`stationReleased=True`；入口收起。新判据在 `4e40e196` 上 PASS；三个变异（日志 outcome、记录文字、入口不收）各只红在预期那一项；另一个变异因分析器 `CA1822` 构建失败，记为无效。随后移绑定、从头重跑 | `docs/defects/20261002-manual-station-clearance-result-line-blank-after-clearance-ends.md`；`evidence/g3/20261002-protocol-v2.0.0-journey-8467480d/`；`evidence/l2/20261002-b9exit-control-*`、`…-g3-13-13-*` |
| 顶端兜底 test（`8467480d`）1/4120 | cs#448：真时钟 6 秒窗口在负载下偶发拿不到行 | 调度判偶发（同树 `37003452808` 全绿），不重跑 | run `37006671205` |

**journey 这一轮绿不是「清桩结果显示没问题」的证据**：结果一行在清桩结束时清空是车载端的设计，判据只是不再去读它。操作员能看到的是操作记录里那一条（第五节第 9 条后的说明）。

## 五、必须如实写明的各点

1. **协议身份**：本批不改协议，属身份表的 **`protocol-v2.0.0` 一行**（cs#393 未合入）。G1 引 program#97 的发布态证据；「身份未变」逐项核对见「身份」一节，全部相等。
   **cs#393 合回集成分支后，本批证据随 v2 一起不再现行**，由 cs#393 的全量重证覆盖；cs#393 票面写的「两端各 14 片」届时要按 15 片（含 `FP-IS-13`）做——开跑前已报调度，调度已在 cs#393 上补记。
2. **10-08 上真车的实况：截至出口（2026-10-03）尚未上车，实况由 10-08 后的追加文档提交补入。**单桩 211 完整充电周期的每一段（低电进桩、充满离桩接活或回等待点、充不上时暂停桩与人工清桩、名册置空时退化）**都未在现场取得**。
   10-08 的运行是并行期在 `agv02`／`agv03` 上的运行，不是 W3，不写成「W3-S1 已完成」；每一次充电窗口都按工作区「Ask first」第 1 类逐次授权，不在本票。
3. **`REQ-0174` 现场补证状态：未补证，待首次现场充电失败**（第 8.4.2 节）。`407802` ＋ 最终 `HANG` 至今没有在现场出现过；L2 与假 RIoT 的注入（`charging-unable-to-charge-pauses-charger`、`g3-manual-station-clearance`）不代替现场补证。
4. **211 的隔离方式**（用户 2026-09-29 定）：并行期只在用户授权的窗口里自动充电；窗口外名册置空，车退化成人工充电等待并告警；不改 MVP 配置。名册的置空与启用是受治理导入（cs#400），不改库。
   名册两版已随 cs#400 入库：「启用」`docs/field/charger-roster-map26-station211.json`（26 号图、站 211「充电点1」、进出点 212、`approvedBy: Zhengyu Shao`），「置空」`docs/field/charger-roster-empty.json`（`chargers: []`）。
   **开关窗记录尚无**：cs#411 open，还没有在任何 v2 实例上导入过。与第 8.4.1 节「W2 不含自动充电」的关系：那条的理由是唯一在用的桩 211 由生产 `agv01` 占用；现在这一理由由开窗规程处理（开窗前确认 `agv01` 不在充、也不会去充），不是被取消。
5. **投运参数**（cs#411，人工票，open，不挡出口）：
   - **数值已定**（用户 2026-09-29，经调度转达）：最低任务后余量 20%、强制充电线 30%、充满线 80%、每趟耗电估计 10%；无进展判断稳定期 180 秒、观察窗口 600 秒、最小增量 3%；`vehicleScope` 为空（全部车）。文件 `docs/field/charging-policy-map26.json`（硬关系 80 > 30 ≥ 20）。10-08 临时测试策略 50%／62%，**仓库里没有这份文件**。
   - **批准尚未执行**：还没有在任何 v2 实例上导入、`--source FIELD` 批准与激活，所以 FieldOps 的批准记录（批准人、角色、依据）还不存在。
   - **缺失即硬阻断**（第 8.6 节）：没有已批准 `ChargingPolicyVersion` 的车逐车不投运（`L2-CPM-02`／`03`），名册缺失只退化不阻断（`L2-CRE-03`）。
   - 并行实例配置 `scripts/parallel/instance-factory01-v2.json` 里还没有 `VehicleFaultRecovery`、`FieldOperatorRoles` 两节；cs#411 10-01 的评论要求的上车配置尚未进仓库。
6. **`REQ-0208` 电量半边**：批次 8 出口写的是「未实施」；本批 cs#403 实施了「预计任务后余量」：`after = battery − EstimatedTaskConsumptionPercent × tasksToCover`，要求不低于 `MinimumPostTaskBatteryMarginPercent`（`BatteryEligibility.cs:65-72`），途中追加按追加后整趟估。
   证据：`BatteryThresholdEligibilityTests`（`EachBatteryBoundaryAnswersItsOwnReason`、`AnAppendIsJudgedForTheWholeJourneyAfterIt`、`TasksToCoverAfterAnAppendCountEveryOpenDemandAsAWholeTask`）、`MultiVehicleExecutionTests.BatteryThresholds.cs`；
   L2 `mandatory-charge-vehicle-takes-no-transport` 证的是强制充电线（该场景每趟估计取 0），**余量那一支只有 L1 边界用例，没有单独的 L2**。
7. **`program#134`**：按「只告警」实施——等人期间耗电维持现行需求，只升级告警。**告警推送现场值班人未实施，已撤出本批、登记为待决事项**（program#162「渠道待定，不开票，登记为待决」）；
   注意 program#134 09-29 的评论原话是「告警推送给现场值班人」，与 program#162 不一致，以 program#162 的待决为现状。「救命充电」以后再做、切换后单独评估。**本报告不把推送写成已有的能力。**
8. **无进展的降级**：cs#407 交的是**完整版**，不是「只告警」降级版——确认中断或无进展时，同一次保存同时暂停这个桩的分配（`ChargingStationAllocationHoldRow`）与这辆车的充电资格（`VehicleChargingEligibilityHoldRow`）（`JourneyRuntimeEngine.ChargingInterruption.cs:350`、`:373`、`:401`），
   `REQ-0285` 双向隔离的证据是 `ChargingInterruptionTests.TwoFreshReadingsNotChargingBelowTheThresholdConfirmAnInterruptionAndPauseBothSidesInOneSave`、`.TheChargerAndTheVehicleEachRecoverOnTheirOwn` 与 L2 `charging-interruption-isolates`、`charging-no-progress-isolates`（各 3/3）。
   **限定**：隔离要「人工清桩出口可用」且「Host 恢复入口已映射并配了凭据」，缺一样就只告警（`StationClearanceExit.IsolationUnavailable()`，用例 `WithoutAWayBackFromAPauseItIsOnlyAlarmedAndNothingMoves`）。cs#411 要求在核对 RIoT 停充电量之前不开 Host 入口，所以 **10-08 实际跑的是哪一支取决于现场配置**。
9. **人员核验**：协议 `OperatorContext` 没有角色字段（`operatorId`、`verificationMethod`、`verifiedAt`）。服务端按 `operatorId` 查只读 JSON 名单（`FieldOperatorRoleRoster.cs`，配置 `FieldOperatorRoles:Path`；没配、读不到、为空都是「没人有权限」）：
   人工清桩（`REQ-0179`）认 R-11／R-13（`StationClearanceRoles`，`FieldOperatorRoleRoster.cs:50`），现场确认充不上（`REQ-0176`）只认 R-11（`UnableToChargeFieldConfirmations.cs:125`）。
   车载端两个入口（onboard-hmi#221、#222）只对已验证的维护人员出现，不在本地判角色。**名单只把 operatorId 映射到角色，不认证这个人是谁**；第 8.8 节第 2 条：验收证据里没有人员认证项，**本报告不写「权限已验收」**。
   附：人工清桩确认成功时，操作员在屏上看到的是操作记录列表里持续的一条「服务端已确认清桩（充电桩 X），站点已释放…」，入口下方的结果一行在清桩结束时清空（第四节）。
10. **本批 migration 数：一次**，`20260929114754_Batch9ChargingPersistence`（cs#399，PR #416）。`af2b02cb..8467480d` 区间里另一个 `20260929070322_Batch8RetireOldVehicleOccupancy` 属批次 8 的 cs#387（PR #413），只是合入时间落在这个区间，不是批次 9 的追加；批次 9 没有追加列。
11. **RIoT SDK**：`RIoT.Sdk.Facade 0.2.0-fp.4` 是本地包（`vendor/nuget/riot-sdk/0.2.0-fp.4/`，源提交 riot-sdk `073e40fd`），**不对外发布、不打 tag**——riot-sdk 没有任何 tag 或 release。riot-sdk 那边的 PR：https://github.com/trytoreachpeak0/riot-sdk/pull/2 。
    **白名单第 1.2 节「形态二」本批改过一次**（票面说「没改」，不成立）：服务端副本 `vendor/8005-agv-program/docs/riot-call-allowlist.md` 随 cs#406（PR #437，提交 `70ba9acee`）刷新到 program v1.9.0（CP-0010）：形态二改由 `CreateMoveOrderAsync` 带开始充电动作的重载建、只批 `act(78,1,0)`、目标桩须在名册内；1.3 节 `CMD_ORDER_CANCEL` 扩到本服务端自建的充电单（两种情形）。获批调用的行没有增减。**1.2「形态一」还没有加「清桩开往等待点」**（第 15 条）。
12. **`FP-IS-13` 是新切片**，没有可沿用的旧通过结论；向量是弱绑定（规格第 6.6 节），从未被机械执行，「同名具名测试」只证明绑定存在。
13. **车载端录入门的电量那一支**（onboard-hmi#220 第 5 条）：已去掉，`CanAcceptSublot` 只剩非业务停靠、就绪、`manualChargingHold` 为假、清单与需求一致（`WireToGateJourney.cs:255-260` @`4e40e196`），不再看 `batteryState`；
    在途搬运不因电量越线拒装由 `WireToGateChargingJourneyTests.ABusinessStopAdmitsASublotWhateverBatteryStateTheServerProjects`、`OnboardControllerTests.ATransportStopLoadsWhateverBatteryStateTheServerProjects` 与 G2 `AutomaticChargingCycleG2Tests.ATransportUnderWayIsOfferedTheEntryWhateverItsBattery` 钉住。
    与 cs#403 同一口径（`REQ-0281` 途中越线不中断）：服务端那一半是 `BatteryStateProjectionTests.AVehicleThatFallsBelowItsLineOnTheWayFinishesItsJourneyAndThenTakesNoNewWork`。合入顺序 onboard-hmi#220（09-29）先于 cs#403（09-30），成立。
14. **真装置免跑的判据对三端各问一遍**：本出口没有免跑。28 次真装置三端从 run `37013472279` 日志 `Run real-onboard L2 scenarios` 那一行读出（`65a698c2`／`4e40e196`／`fb5f7c59`），逐端与各自集成分支顶端比对：服务端产品即 `fp/v2-impl@8467480d`，车载端即 `w2g/fp-v2-impl@4e40e196`（之后只多 `evidence/`），模拟器即 `main@fb5f7c59`。
15. **剩余风险**：见第六节；其中出厂关闭的开关单列。

## 六、剩余风险

### 存在但出厂关闭的行为（打开前要什么）

| 开关 | 默认（代码） | 关着时的行为 | 打开之前要什么 |
| --- | --- | --- | --- |
| `JourneyRuntime:ClearanceToWaitingPointEnabled`（cs#409） | `false`（`JourneyRuntimeOptions.cs:200`，无初值；服务端与并行实例配置都没设） | 清桩中的车停在原地等人工清桩，同 cs#406 | ① RIoT 调用白名单 1.2「形态一」的「用」列补上「清桩开往等待点」（`REQ-0178`、`REQ-0294`），调度另走变更，**未做**；② 现场知情：开关打开后，RIoT 里结束旧充电单的那一刻车会自行开往等待点（动车）；③ 车载端确认途中计划里 `CHARGER` 腿为 `COMPLETED`、等待点腿 `ACTIVE` 时清桩入口仍能取到站号、按钮还在（用例或真装置）。10-08 按默认关（cs#411 10-02 评论） |
| `JourneyRuntime:UnableToChargeOldOrderCancelEnabled`（cs#406／#410） | `false`（`JourneyRuntimeOptions.cs:174`） | 清桩中的旧充电单由人在 RIoT 里结束 | ① 10-08 经用户授权在 `agv02` 上实测：空载、有人盯着，验证取消 `HANG` 充电单（`act(78,2,0)`）会不会让车动；② 打开后在形成「确认充不上」后的第一轮就发取消，车还在桩上，所以人先到位、单独授权；③ 现场确认充不上不看 `HANG` 的原因，急停或切手动造成的 `HANG`（`REQ-0175`）一旦被确认，打开时也会被取消（cs#411 10-01、10-02 评论） |
| 车载端 `wireToGate.unableToChargeEntryEnabled`（onboard-hmi#222） | `false`（`Configuration.cs:327`；`appsettings.json:34`、`appsettings.Production.example.json:32`） | 车上没有「现场确认充不上」入口 | 服务端要支持 `UnableToChargeFieldConfirmationRequested`（cs#410，已合入）；更早的服务端遇到这条消息会抛异常并断开连接。上车时在车载端现场配置里打开 |
| 相关：`FieldOperatorRoles:OnboardClearanceEntryDeclared` | `false`（`FieldOperatorRoleRoster.cs:33`） | 照旧按 `ORDER_HANG` 处理并告警（事件 2271／2272） | 车上已开清桩入口（`wireToGate.recoveryResumeEnabled`，车载端出厂 `false`） |
| 相关：`VehicleFaultRecovery:Enabled` | `false`（`VehicleFaultRecoveryOptions.cs:25`） | 中断／无进展只告警、不隔离（第五节第 8 条） | 配好凭据；在 RIoT 里核对 `agv02`／`agv03` 车组的 `batteryLevelFullyRecharged`、`keepRechargingUntilFullyCharged`，并实际充一次看停充读数——RIoT 停充电量低于完成阈值时正常充满会被判成中断 |

### 行为与现场

- **人工充电等待的投影**：清桩中快照的 `manualChargingHold=false` 投影（cs#410 关票评论「跟进事项」），跟进 cs#451。
- **RIoT 读取放在收件箱写锁内**（cs#410 关票评论，与 cs#406 同形）：RIoT 慢时拖住引擎与其它车入站，跟进 cs#452。
- **日志事件号重号**：`2108`、`2110`、`2189`、`2250`（以及 `1`、`2`）各有两处或以上定义；其中 `2189`（`ForeignRunningOrderSupervisor.cs:154` 与 `JourneyRuntimeEngine.cs:456`）、`2250`（`ChargingAllocator.cs:467` 与 `JourneyRuntimeEngine.Charging.cs:18`）是批次 9 引入的（2108、2110 早于批次 9）。按事件号筛日志的人会把两件事混在一起。cs#449 跟进（唯一性架构测试并修现有重号），不挡 10-08。
- cs#447：等待点移动 `SUCCESS` 后证明不了到点时，旅程收不了尾、没有受治理的出口（空闲返回与清桩同一机理，读代码推断，未跑过）。清桩开往等待点的开关出厂关，所以批次 9 默认配置下只有空闲返回一支可能遇到（推断，未核实）。
- cs#432：合成 RIoT 与 L1 替身对不存在的单回 404，真实 RIoT 回 200 不带 `result`，测试走不到真车上那条分支。
- `REQ-0208` 余量一支只有 L1，没有 L2（第五节第 6 条）。
- 10-08 实际跑的是隔离还是只告警，取决于现场配置（第五节第 8 条）。
- 告警推送现场值班人没有实施（第五节第 7 条）。

### 测试与证据的边界

- **需求承载 G3 本轮未跑**，`FP-IS-04`／`05` 在批次 9 代码上没有 G3 结论；外场库重建跟进 cs#453（第三节）。
- `CV-MANUAL-CHARGING-RETURN` 只以 `FP-IS-07` 的名字被 G3 断言，`FP-IS-13` 名下没有（第三节）。
- 人工清桩结果在屏上只能按文字找到（操作记录那一条没有 AutomationId／ItemStatus），与 onboard-hmi#241 同类，待决；入口下方的结果一行要不要在清桩结束后短暂保留，是产品取舍，批次 9 之后定（第四节、缺陷单）。
- cs#448：`ExpectedActionOverdueTests` 的 HTTP 集成用例在负载下偶发红（兜底 run `37006671205`）。
- cs#440：`OnboardPowerLossReconnectTests.AVehicleThatKeepsTalkingButStopsReadingIsLetGoAndReconnects` 高负载下偶发红。
- cs#444：hmi#239 的一次性真装置场景（重启后补发恢复命令被扣住）还没转正进清单；onboard-hmi#241 是它的前提（加 `ItemStatus`）。
- onboard-hmi#237：启动结算绕过缓存，恢复入口读到过期状态，在途装货的取消入口可能误显示为可按。
- 并发读改写、重放与重连、崩溃点：由各功能票的 L1、G2 与场景覆盖，本票未新增；出口没有专门构造「充电单建单结果未知 × 进程重启」「清桩确认 × 断线」这类组合时刻的 G3。`unknown-as-success` 一项在 G3 上造不出来，只由 G2 同名测试守（`docs/g3-slice-claim-review.md`）。

### 运维说明

- 部署前按 `docs/field/batch-9-charger-roster-and-charging-policy.md` 的顺序：放包 → `Install-ParallelInstanceLocal.ps1`（服务自己迁移）→ 服务运行中导入、批准、激活 → 核对。**不要用 `--migrate-only`**：取不到连接串时它迁移的是默认库，也就是 MVP 生产库（cs#411 09-29 评论）。
- 升级前删掉配置里的 `minimumBatteryPercent`，否则服务拒绝启动；激活前人工核对强制充电线高于 `JourneyRuntime:WaitingJourneyRescueBatteryPercent`。
- 车上配置的操作员号要在 R-11／R-13 名单里，否则每次清桩确认都被拒。

## 七、转后续

- 10-08 上车实况与 `REQ-0174` 现场补证：docs-only 追加提交，由调度在 10-08 后派。
- cs#411（人工）：批准、导入、激活与开关窗记录。
- 需求承载 G3 外场库重建（cs#453）。
- 待决：操作记录里清桩结果的稳定读取句柄；结果一行是否短暂保留；告警推送渠道（program#134／#162）；清桩中 `manualChargingHold` 投影（cs#451）与 RIoT 读取在写锁内（cs#452）。
- cs#393 合回后按 15 片全量重证。
