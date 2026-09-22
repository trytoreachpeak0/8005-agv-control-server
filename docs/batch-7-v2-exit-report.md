# 批次 7 出口报告（v2 线）：多需求旅程、持货等单与让站（`FP-IS-08`）

control-server#220（批次7-18）。本报告逐项对照规格 `8005-agv-program/docs/specs/full-product-scope-and-sequence-v2.md` 第 8.3 节批次 7 行
（按第 19～21 节补记与第 22 节补记读；第 8.3 节「卸货时两组同开」由第 20 节补记取代，卸货一次一扇、先前后后）。
需求条目按基线 **`v1.4.0`**（tag `requirements-baseline-v1.4.0`）引用。本批不改协议，门禁与证据绑 `protocol-v2.0.0`。

> **状态：出口达成（第二轮），待调度审查。**第一轮在 G3 journey 上红（产品缺陷，control-server#314 已修并合入）；第二轮在 `82bfa415`／`86d42ce5` 上
> 从头跑，全部 PASS。第一轮结果全部保留，作为红证据与发现记录，不作出口证据。两段本机封锁与两段 CI 真装置持有都已跑完归还。

## 结论

**批次 7 出口达成（第二轮）。**规格第 8.3 节批次 7 行（按第 20 节读）的每一项在 `protocol-v2.0.0` 发布身份上成立：
`FP-IS-08` 四门禁全 PASS；G3 四个 runner 全绿，journey 15/15；CI 上 55 个合成场景各连续三次、共 165 次全 PASS（批次 7 的 19 个场景 57 次）；
七条判据各有三份独立证据；真装置 15 次 PASS（混挂站点三连、`compensate-then-reconnect` 与其余 11 条回归）；两端全量 L1 全绿
（服务端 2353/2353，车载端 938/938）。服务端产品 `82bfa415`（含 control-server#314），车载端 `86d42ce5`（产品即 `ecdb3a0b`），模拟器 `fb5f7c59`。

第一轮（2026-09-22，服务端产品 `517e1c7a`、车载端 `ecdb3a0b`）：两端 G2 PASS；G3 四个 runner 里三个 PASS，journey
`JOURNEY_G3_SLICE_FAIL`（14/15，`g3-multi-stop-plan` 即 `FP-IS-08` 本身 PASS，红在 `FP-IS-10` 的 `g3-task-type-admission-fail-closed`）；
真装置 15 次全 PASS（混挂站点 3 遍、回归 12 条）；CI 三连（参考）165 次全 PASS。journey 那一红是产品缺陷（第四节），
用户定先修再从头重跑，所以第一轮不构成出口证据。

**本报告不写「九条腿已跑通」**：九条腿只由两端 G2 证明（第五节第 5 点）。

| 出口（规格 8.2／8.3 批次 7 行、本票验收） | 状态 | 依据 |
| --- | --- | --- |
| 前置核对逐项通过；七条判据与真装置场景对照表完整 | 前置已核（下一节）；对照表已列 | 「前置核对」「二、L2」 |
| L1：两端测试套件在出口提交上全绿；新能力逐项有新增测试 | **成立**：服务端 2353/2353（CI run `35704027053`）；车载端 938/938（CI run onboard-hmi `35694184570`）；对照表已列 | 第一节 |
| `FP-IS-08` 四门禁全 PASS，`gate-result.json` 绑 `protocol-v2.0.0` 精确身份 | **成立**：G1 协议侧发布证据；两端 G2 PASS；G3（journey）PASS | 第三节 |
| G3 四个 runner 在出口绑定上各一轮全绿；journey 15/15 | **成立（第二轮）**：四个 runner 全绿，journey `JOURNEY_G3_PASS` 15/15，`FP-IS-08` `formalSlicePass true`；第一轮 14/15 的红见第四节 | 第三节、第四节 |
| `real-onboard-mixed-side-one-stop` 连续三遍 PASS；compensate 与回归清单全 PASS | **成立（第二轮）**：3/3（run `35699978289`）；12/12（run `35699989410`） | 第二节 |
| `l2.yml` 批次 7 区块 `Runs = 3`（先红后绿）；CI `consecutive-all` 全部 PASS，逐份核对 | **成立**：`Runs = 3`（`f1286c29`，核对脚本先红后绿）；run `35699901251` 165/165 PASS，逐份核对 | 第二节 |
| 七条判据各有证据目录，持货等单与让站各指出断言正事实的 L2 id | **成立** | 第二节对照表 |
| 出口冻结期间两端集成分支没有合入其它 PR；证据小 PR 合入时点 | **成立**：冻结自 2026-09-22 起，期间只合入了 control-server#314（PR #315，用户定先修再重跑，调度安排）与车载端证据小 PR onboard-hmi#194（第二轮 G3 开跑前合入） | 「身份」 |
| 每次门禁新目录；红证据保留，`docs/defects/` 有记录 | 第一轮全部保留；缺陷单已入库 | 第四节 |
| 「必须如实写明」各点，无第 8.8 节禁用表述 | 已写 | 第五节 |
| 未切换 `C:\Users\szy\Desktop\8005-workspace\repos\` 下任何克隆 | 至今成立 | 全部操作在 `8005-workspace-v2` |
| 本 PR 的 CI `test` 与 `l2` 两项绿 | 转 ready 后的默认一轮，见 PR 检查页（`l2` 核汇总标题无 `superseded`、场景数） | PR 检查页 |

## 前置核对（2026-09-22 实查）

| 项 | 结果 |
| --- | --- |
| 批次7-01～16、19 全部合入 | 成立。服务端 #206（PR #229 `47ae7376`）、#207（PR #232 `13a1db75`）、#208（PR #244 `ce9bce83`）、#209（PR #233 `87511c46`）、#210（PR #301 `8ee99549`）、#211（PR #257 `1e59e21b`）、#212（PR #289 `0b19397b`）、#213（PR #297 `a4fc1cef`）、#214（PR #300 `d074405c`）、#215（PR #295 `6375e884`）、#216（PR #235 `99c71923`）、#217（PR #302 `24d8a778`）、#218（PR #304 `3c930c8f`）；车载端 onboard-hmi#134（PR #143 `226ff87f`）、#135（PR #158 `3a77afc2`）、#136（PR #166 `24af41e4`）；program#126 已关闭 |
| 挂批次 7 的非人工遗留票全部关闭 | **开工核对时不成立，出口会话停下报调度**：control-server#186 与 onboard-hmi#132 都开着、没人认领（调度派出口前只核了主线票与 #306/#259）。处置：#186 由用户 2026-09-22 降级为「出口后、v2 上真车之前」（第六节）；onboard-hmi#132（范围缩到原第 4 条）派出并合入（PR onboard-hmi#193，`ecdb3a0b`，相对 `deeba94c` 只动 `tests/` 与 `evidence/`）。其余 #198～#204、onboard-hmi#128～#130、#61 已关闭；#205 降级（第六节） |
| 调度点名的出口阻塞 | control-server#303（PR #305 `c4b04bb2`，同停靠卸货按侧排序）、#306（PR #308 `8738919a`）、#259（PR #309 `517e1c7a`）已合入；#231、#234，onboard-hmi#139、#140、#142、#145、#146 已关闭 |
| 批次 7 场景（`BatchId = 'batch-7'`）与七条判据逐条对上 | 成立：19 个合成场景，恰好是 `e74c0058..517e1c7a` 新增的全部合成场景文件（第二节）；七条判据的场景都在 |
| 两端等待名单里没有 `CV-MULTI-STOP-PLAN-NINE-LEGS`，`FP-IS-08` 已进两端切片名单 | 成立（`517e1c7a`／`deeba94c` 实读；服务端 `ProtocolVectorTestBindingArchitectureTests.cs:95-96`，车载端同名文件 `:104`） |
| v2 工作区仓在要绑定的提交上、工作树干净；本机没有其它真装置 L2 或 G3 在跑 | 第一轮成立：`repos/` 下克隆在基线分支、干净；本票自己的 worktree 与 detached worktree 当对端，跑完删除；调度核过桌面空闲后放行（持有者「cs#220 封锁」）。第二轮成立：本机只跑服务端 G2 与 G3（G3 自己克隆三端），没有建对端 worktree；跑完核过 `repos/` 下三个克隆在基线分支、干净 |
| control-server#166、批次7-17（#219） | 人工票，open，不挡出口（「等用户拍板的人工项」） |

## 身份

| 项 | 值 |
| --- | --- |
| 协议 | `(AGV_FULL_PRODUCT, 3)`，`releaseVersion 2.0.0`，tag `protocol-v2.0.0` → `86575456c847041515b7b75e8851a00e0d939804`；本批零改动 |
| `ProtocolReleaseIdentity` 其余字段 | manifest `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7`，schema bundle `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221`，vectors `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a`，`APPROVED_RELEASE` |
| 第一轮服务端产品 | `fp/v2-impl@517e1c7a`；runner 与证据提交在本分支，相对它只改 G3 绑定、`real-onboard-mixed-side-one-stop` 的追加顺序、`l2.yml` 批次 7 的 `Runs`、证据与文档，`src/`、`tests/` 零差异 |
| 第一轮车载端 | `w2g/fp-v2-impl@ecdb3a0b` |
| 模拟器 | `main@fb5f7c59`（不变） |
| 第一轮 G3 共享绑定 | `1419ab99`（`chore(g3)`）：`ControlServerCommit 517e1c7a`、`OnboardCommit ecdb3a0b`、`SimulatorCommit fb5f7c59`、`ProtocolCommit 86575456`；tag 字面量已是 `protocol-v2.0.0`，未动 |
| **第二轮（出口）服务端产品** | `fp/v2-impl@82bfa415`（PR #315 合并提交，control-server#314）；`517e1c7a..82bfa415` 只有 #314 的三个产品文件与一个测试文件。本分支 merge 进来（`27686034`，不变基），`src/`、`tests/` 与 `82bfa415` 零差异 |
| **第二轮车载端** | `w2g/fp-v2-impl@86d42ce5`（onboard-hmi#194 合并提交）；相对 `ecdb3a0b` 在 `evidence/` 以外 0 个文件，产品即 `ecdb3a0b` |
| **第二轮 G3 共享绑定** | `f2ddd405`（`chore(g3)`）：`ControlServerCommit 82bfa415`、`OnboardCommit 86d42ce5`、`SimulatorCommit fb5f7c59`、`ProtocolCommit 86575456` |

**车载端 G2 证据能否沿用到第二轮（出口会话的判断，交调度核）**：能，前提是第二轮的车载端仍是 `ecdb3a0b`、协议仍是 `86575456`。
`ONBOARD_HMI_G2` 只构建与测试车载端仓（`run-w2g-g2.ps1`：Release build、带 `IntegrationSlice=FP-IS-08` 的测试、`dotnet format`、在发布态重跑协议 G1），
不启动也不连接服务端（它的 G2 夹具用车载端仓自己的替身 `FakeControlServer`），`gate-result.json` 记的身份只有车载端提交与协议身份。
服务端前移不改变它测的对象，所以结论不变。反过来，若 #314 或其它原因让车载端产品变了（证据小 PR 合入除外，那只多 `evidence/`），就要重出。调度 2026-09-22 同意。

`gate-result.json` 里全部记录身份的字段，原样列出（其余字段是时刻、计数、退出码与 schema 符合性，逐项读过，没有任何服务端提交）：

| 字段 | 值 |
| --- | --- |
| `gate` | `ONBOARD_HMI_G2` |
| `integrationSliceId` | `FP-IS-08` |
| `implementationRepository` | `8005-agv-onboard-hmi` |
| `implementationCommit` | `ecdb3a0be1d95ef51e7d40493808ba4659274f41` |
| `implementationBranch` | `w2g/b7-18-g2-evidence` |
| `protocolReleaseVersion` | `2.0.0` |
| `protocolTag` | `protocol-v2.0.0` |
| `protocolProfileId` | `AGV_FULL_PRODUCT` |
| `protocolVersion` | `3` |
| `protocolApprovalStatus` | `APPROVED_RELEASE` |
| `protocolRepositoryCommit` | `86575456c847041515b7b75e8851a00e0d939804` |
| `protocolManifestSha256` | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| `protocolSchemaBundleSha256` | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| `protocolVectorsSha256` | `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a` |
| `integrationSliceIndexSha256` | `268ce62be4e0ec8fe6d26e2048a59cf1732f02713415a0c5f41b6b009951b6fc` |

同目录 `summary.json` 里唯一提到服务端的是一句说明文字（出站报文的两个产地是「车载端产品与 `FakeControlServer`」），也不是服务端提交。

**证据小 PR**：onboard-hmi#194（调度定在第二轮之前合）。合入后 `w2g/fp-v2-impl` 顶端前移到它的合并提交，相对 `ecdb3a0b` 只多 `evidence/`；
第二轮的 G3 `OnboardCommit` 与 CI 真装置的 `onboard_ref` 都取那个新顶端。第二轮开跑前核对（2026-09-22，`git ls-remote` 与 `git diff --name-only ecdb3a0b 86d42ce5 -- . ':!evidence'`）：车载端顶端 `86d42ce5`，
相对 `ecdb3a0b` 在 `evidence/` 以外 0 个文件；协议仍是 `86575456`（tag `protocol-v2.0.0`）。**车载端与协议两端都没变，所以第一轮的 `ONBOARD_HMI_G2` 证据沿用。**

## 一、L1

### 实测

| 端 | 命令 | 结果 |
| --- | --- | --- |
| 服务端 | CI `test.yml`，`workflow_dispatch` 于 `4f832a39`（run [`35704027053`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35704027053)） | **2353 / 2353 通过**，0 失败 0 跳过（日志原文 `已通过! - 失败: 0，通过: 2353，已跳过: 0，总计: 2353`）。`4f832a39` 之后本分支只改文档，`src/`、`tests/` 即 `82bfa415` |
| 车载端 | 车载端 CI `test` workflow，**push 触发**（onboard-hmi#194 合入 `w2g/fp-v2-impl` 时），run [`35694184570`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/actions/runs/35694184570)，`headSha 86d42ce5`，与第二轮绑定的提交完全相同 | **938 / 938 通过**，0 失败 0 跳过（`SQCD.Agv.UnitTests` 546、`SQCD.Agv.WireToGateG2Tests` 392，解决方案里只有这两个测试项目）；`run-w2g-g2.ps1 -SkipProtocolG1` 不带 `-Slice`，即全量；同一作业的 `ONBOARD_HMI_G2` 与 UI layout audit 都是 `Status: PASS` |

第 8 步原定本机经 `Invoke-HeavyLocal.ps1` 跑。本机那一遍已启动后按调度要求停掉（09-20 起车载端全量只走 CI，本机内存吃紧），它的构建日志里 `MSB4166 Child node exited prematurely`
是停止造成的，不是结果；残留目录已删。第 8 步的证据用上面那次 push 运行。

### 批次 7 每项新能力 ↔ 新增测试

剖面 `Batch = BATCH-7` 共 17 行（`REQ-0155`、`0156`、`0185`、`0189`、`0195`、`0196`、`0197`、`0198`、`0201`、`0202`、`0203`、`0205`、`0206`、`0211`、`0328`、`0354`、`0355`），
外加规格第 3.3 节第 9～12 项。测试名取自各合入新增的 `[Fact]`／`[Theory]`，格子里只列代表方法，括号为该合入新增总数（`git diff <merge>^1 <merge> -- tests` 可复现）。

| 票 | 需求条目（`v1.4.0`） | 新能力 | 新增测试（代表） | 合入 |
| --- | --- | --- | --- | --- |
| control-server#206（批次7-01） | 第 3.3 节第 9 项；为 `REQ-0189`、`0196`、`0354` 建表 | 唯一建表：旅程停靠与需求从属、`VehiclePurposeClaims`、按业务键抑制、每区派车参数、装货阶段列、按车修订号计数器；引擎零行为变化 | `Batch7JourneyAcceptanceTests.AcceptingAJourneyWritesItsStopsItsDemandItsPurposeClaimAndTheCounterInTheOneSaveThatWritesTheJourney`、`.WhenWritingThePurposeClaimFailsNothingOfTheAcceptanceIsLeftBehind`、`TwoJourneysOnOneVehicleKeepTheRevisionsAndMessageIdsTheyHadBeforeTheBatch7Migration`（29） | PR #229 `47ae7376` |
| control-server#207（批次7-02） | 无条目载体（预重构 A） | 「需求→旅程」统一查找口；终结需求与关闭旅程拆两步；行为不变 | `Batch7DemandJourneyLookupTests.ADemandAddedToAJourneyFindsThatJourneyThoughTheJourneyRowNamesAnotherDemand`、`.ARemovedMembershipNoLongerFindsTheJourney`（20） | PR #232 `13a1db75` |
| control-server#208（批次7-03） | 无条目载体（预重构 B） | 推进由当前停靠驱动；单需求旅程两个停靠的消息 id 与今天逐字相同 | `Batch7StopDrivenAdvanceWireParityTests`、`Batch7AnchorColumnReadLedgerTests.EveryAnchorColumnStillReadInTheEngineIsAccountedFor`（17） | PR #244 `ce9bce83` |
| control-server#209（批次7-04） | 无条目载体（预重构 C） | 派车轮搬出引擎、分层比较器、仓位账本端口；行为不变 | `DispatchCandidateOrderingTests.SwappingTheFirstSeenAndCreatedAtLayersIsCaughtByTheCorpus`、`ARoundWithMoreCandidatesThanVehiclesDecidesAsBeforeTheMove`（19） | PR #233 `87511c46` |
| control-server#210（批次7-05） | `REQ-0155`、`0156`、`0211` | 按业务键抑制，本地取消终态原子写抑制，先写者胜 | `Batch7TransportDemandKeyAdmissionTests.ASuppressedKeyUnderANewDemandIdIsRefusedAsSuppressed`、`.AnotherTaskTypeOnTheSameSublotIsAnotherKeyAndPasses`（19）；L2 `transport-demand-key-suppressed` | PR #301 `8ee99549` |
| control-server#211（批次7-06） | `REQ-0189`、`0195`、`0196`、`0198`、`0205`、`0206`；第 3.3 节第 10 项 | 多停靠计划与途中追加：在途车参与竞争、分区连续、延迟门禁、按侧仓位账本、9 腿 8 项（`FP-IS-08` 服务端半边） | `Batch7EnRouteAppendPlannerTests.AZoneWithNoParametersRefusesEveryAppend`、`.TheDelayGateAdmitsExactlyItsAllowanceAndNoMore`、`MultiStopPlanNineLegsVectorTests`（93）；L2 `multi-stop-append-same-zone`、`en-route-append-not-configured`、`en-route-append-delay-gate`、`idle-and-inflight-vehicles-compete` | PR #257 `1e59e21b` |
| control-server#212（批次7-07） | `REQ-0354` | 按侧判满、持货等单、持货超时，装货阶段状态机与 `loadingPhase` 下发 | `Batch7CargoHoldingTests.AVehicleWithRoomLeftHoldsAtItsLastPickupInsteadOfLeaving`、`.TheHoldingDeadlineEndsTheLoadingPhaseAndTheVehicleLeaves`、`.TheHoldingClockSurvivesADisconnectAndARestartWhileTheStationWaitIsVoided`（31）；L2 五个（判据 ①②④⑤⑥） | PR #289 `0b19397b` |
| control-server#213（批次7-08） | `REQ-0355` | 让站：别的车以本站为下一停靠时结束持货等单，不打断阻断离站的状态 | `Batch7StationYieldTests.AVehicleCommittedToTheStationMakesTheHolderYieldAndLeave`、`.AfterYieldingTheVehicleTakesNoFurtherDemand`、`.AFullVehicleYieldsAtOnceButFinishesTheLoadItIsCommittedToBeforeLeaving`（26）；L2 两个（判据 ③） | PR #297 `a4fc1cef` |
| control-server#214（批次7-09） | `REQ-0185`、`0201`、`0202`、`0203`（`REQ-0210` 升级告警） | 任务优先级带、等待年龄与防饥饿超时层、升级告警与标定报表；`REQ-0185` 承载机制钉住 | `Batch7Req0185EutecticExclusionTests.Req0185AnEutecticAreaTheAssignmentTableLeavesOutStaysInTheBacklogSilentlyWhateverTheMapHas`、`Batch7StarvationEscalationTests`、`Batch7StarvationCalibrationReportTests.TheSummaryIsTheExactMedianNearestRankP95AndMaximum`（39）；L2 `task-type-priority-band`、`starvation-threshold-escalation` | PR #300 `d074405c` |
| control-server#215（批次7-10） | `REQ-0197`、`0328` | 后续停靠有界删除与换序、移除已取消的未取货需求、仅当前车不合格时释放改派 | `Batch7DemandReleaseRulesTests.AFaultOnTheVehicleIsATrigger`、`.AFullyEligibleVehicleIsNotATrigger`、`Batch7PlanRevisionTests`（93）；L2 `plan-reorder-and-drop-cancelled`、`reassign-when-vehicle-ineligible` | PR #295 `6375e884` |
| control-server#216（批次7-11） | `REQ-0198`、`0203`（参数载体） | 每区派车参数整表导入，版本快照、不可改写审计、不停车生效 | `DispatchZoneParameterFieldOpsTests.BeforeAnyImportTheReadVerbListsEveryZoneAsUnconfiguredNeverAsZero`、`.DryRunThenImportThenTheSameTableAgainThenReadBackThroughTheProcess`（26）；L2 `dispatch-zone-parameters-import-rejects` | PR #235 `99c71923` |
| control-server#217（批次7-12） | 第 3.3 节第 12 项（看板半边） | 看板显示持货期限、按侧装满、让站原因、等待年龄与超时层、多需求列表 | `Batch7CargoHoldingDashboardTests.AVehicleHoldingCargoShowsTheDeadlineFromTheDatabaseAndTheTimeLeftAtTheRequest`、`.AYieldNamesTheVehicleThatTriggeredIt`（27）；L2 `cargo-holding-dashboard-projection` | PR #302 `24d8a778` |
| control-server#218（批次7-15） | 无条目载体 | G3 认领 `FP-IS-08`（`g3-multi-stop-plan`）；真装置 `real-onboard-mixed-side-one-stop` | G3 场景与真装置场景（无新增 `[Fact]`） | PR #304 `3c930c8f` |
| control-server#303（出口阻塞） | `REQ-0357`（规格第 20 节补记） | 同一停靠多需求卸货按侧排序，先前后后 | `Batch7UnloadSideOrderTests.TheFrontDemandIsUnloadedFirstEvenWhenTheRearOneJoinedEarlier`（7） | PR #305 `c4b04bb2` |
| onboard-hmi#134（批次7-13） | 第 3.3 节第 11、12 项（车载端半边） | 放开清单 8 项与计划 9 腿、修 5 处单需求假设、计划腿与清单列表、持货等单与让站显示（`FP-IS-08` 车载端半边） | `AWorklistOfEightItemsIsAccepted`、`AWorklistOfNineItemsIsRefused`、`APlanOfNineLegsIsAccepted`、`APlanOfTenLegsIsRefused`、`APreDepartureCheckNamingTheFirstDemandIsAnsweredAsForOneItem`（62） | PR onboard-hmi#143 `226ff87f` |
| onboard-hmi#135（批次7-14） | `REQ-0211`（车载端入口） | 扫码前取消由操作员在清单里选需求；装后纠错只针对本站最后一次装货 | `ThePickedDemandIsTheOneCancelledAndTheOtherIsNotNamedAtAll`、`WithTwoItemsAndNothingPickedTheEntryIsDisabledAndNothingIsSent`（19） | PR onboard-hmi#158 `3a77afc2` |
| onboard-hmi#136（批次7-19） | 无条目载体 | 恢复状态「先读后整值写」改走锁内合并，行为不变 | `TheJournalHasNoWholeValueRecoveryStateWrite`、`ClearingAnIsolationNeverOverwritesACheckpointWrittenWhileItClears`（17） | PR onboard-hmi#166 `24af41e4` |

剖面各行都有承载：`REQ-0155`／`0156`／`0211` → #210（与 onboard-hmi#135）；`REQ-0185` → #214；`REQ-0189`／`0195`／`0196`／`0205`／`0206` → #211；
`REQ-0197`／`0328` → #215；`REQ-0198` → #211 的门禁与 #216 的参数；`REQ-0201`／`0202`／`0203` → #214 与 #216；`REQ-0354` → #212；`REQ-0355` → #213。
第 3.3 节第 9 项 → #206；第 10 项 → #211 的按侧账本与多需求 store（不移植 program#61 的旧提交）；第 11 项 → onboard-hmi#134；第 12 项 → onboard-hmi#134 与 #217。

批次 7 期间同一集成分支合入、不属于批次 7 新能力的修复（批次 6 审查后续与追加票）：服务端 #198（PR #223）、#199（PR #241）、#200（PR #227）、#201（PR #225）、#202（PR #224）、
#203（PR #263）、#204（PR #258）、#222（PR #226）、#228（PR #236）、#230（PR #248）、#231（PR #238）、#234（PR #252）、#239（PR #240）、#242（PR #245）、#259（PR #309）、#306（PR #308）等；
车载端 onboard-hmi#128、#129、#132、#139、#140、#142、#145、#146、#149、#152、#156、#162 等。

### 向量绑定

| 向量 | 服务端 | 车载端 |
| --- | --- | --- |
| `CV-MULTI-STOP-PLAN-NINE-LEGS` | `MultiStopPlanNineLegsVectorTests` | onboard-hmi#134 的 `FP-IS-08` G2 测试（第一轮 `ONBOARD_HMI_G2` 选中 2 个） |

**这只证明绑定存在**：向量从未被机械执行（第五节第 18 点）。

## 二、L2

### 七条判据 ↔ 场景 ↔ 证据

持货超时出厂 30 分钟（ADR-cross-0057）；L2 各场景把它缩短，值取自各场景的 `setup.psd1`。

| 判据（规格 8.3 批次 7 行） | 场景（票） | 断言正事实的 L2 id | `CargoHoldingTimeout` | 证据（CI 三连） |
| --- | --- | --- | --- | --- |
| ① 前侧装满后后侧照常接单，两侧都装满才不再等单 | `cargo-holding-side-full`（#212） | `L2-CHS-01`（FRONT 满、REAR 空 → `CARGO_HOLDING_WAIT` 而非 `VEHICLE_FULL`）、`L2-CHS-02`（乙追加进来）、`L2-CHS-05`（两侧都满才 `VEHICLE_FULL`） | 10 分钟 | 3/3 PASS：`evidence/l2/20260922-ci-35699901251-cargo-holding-side-full-01`～`03/` |
| ② 零候选时原地持货等单直至持货超时 | `cargo-holding-timeout`（#212） | `L2-CHT-01`（进入 WAIT）、`L2-CHT-02`（约 30 秒时仍在站上持货、关卡腿未建单）、`L2-CHT-03`（期限一到以 `CARGO_HOLDING_TIMEOUT` 关闭） | **40 秒** | 3/3 PASS：`evidence/l2/20260922-ci-35699901251-cargo-holding-timeout-01`～`03/` |
| ③ 另一车以该站为下一停靠时立即让站，且不打断开门、录入 | `waiting-station-yield`、`waiting-station-yield-waits-for-door`（#213） | `L2-WSY-01`～`04`（持单车 WAIT → 另一车受理 → `CLOSED/WAITING_STATION_YIELD`，触发车记下 → 快照到车）；`L2-WSD-05`～`07`（门开着、装货未落定时不离站，门关后才离站） | 10 分钟 | 各 3/3 PASS：`evidence/l2/20260922-ci-35699901251-waiting-station-yield-01`～`03/`、`…-waiting-station-yield-waits-for-door-01`～`03/` |
| ④ 能服务的分区都禁止途中追加时不等单（负向） | `cargo-holding-disabled-when-append-forbidden`（#212） | `L2-CHD-02`（上限 0）、`L2-CHD-04`（上限未配置），两种都直接离站、不持货 | 40 秒 | 3/3 PASS：`evidence/l2/20260922-ci-35699901251-cargo-holding-disabled-when-append-forbidden-01`～`03/` |
| ⑤ 超大需求、被门禁挡住的候选不触发装满 | `vehicle-full-ignores-oversized-and-gated`（#212） | `L2-VFI-01`（前置 WAIT）、`L2-VFI-02`／`03`（超组需求判 `EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP`、不算满）、`L2-VFI-05`（`TASK_TYPE_HELD` 不算满） | 10 分钟 | 3/3 PASS：`evidence/l2/20260922-ci-35699901251-vehicle-full-ignores-oversized-and-gated-01`～`03/` |
| ⑥ 装满后到离开最后装货停靠前仍接能装入的候选；持货超时与让站之后不再接单 | `vehicle-full-still-appends-before-departure`（#212）＋ ②③ 的后半 | `L2-VFA-03`（`VEHICLE_FULL` 仍接追加）、`L2-VFA-07`（离开最后装货停靠时以 `VEHICLE_FULL` 关闭）；`L2-CHT-05`（超时后新需求不进）；`L2-WSY-06`（让站后新需求不进） | 10 分钟 | 3/3 PASS：`evidence/l2/20260922-ci-35699901251-vehicle-full-still-appends-before-departure-01`～`03/`（后半见 ②③ 的目录） |
| ⑦ 按业务键抑制 | `transport-demand-key-suppressed`（#210） | `L2-TDK-01`（取消时原子写抑制）、`L2-TDK-02`（同键新 DemandId 判 `TRANSPORT_DEMAND_KEY_SUPPRESSED`）、`L2-TDK-04`（后面的无关需求照常受理） | — | 3/3 PASS：`evidence/l2/20260922-ci-35699901251-transport-demand-key-suppressed-01`～`03/` |

持货等单与让站各场景都断言了正事实（上表「断言正事实的 L2 id」），不是「不触发即通过」（规格 8.3、8.5 节）。
批次 7 区块另有 12 个场景：`cargo-holding-dashboard-projection`（#217）、`dispatch-zone-parameters-import-rejects`（#216）、`en-route-append-delay-gate`、
`en-route-append-not-configured`、`idle-and-inflight-vehicles-compete`、`multi-stop-append-same-zone`（#211）、`onboard-silent-liveness-loss`（#234）、
`plan-reorder-and-drop-cancelled`、`reassign-when-vehicle-ineligible`（#215）、`starvation-threshold-escalation`、`task-type-priority-band`（#214），共 19 个。

### CI 三连

- `l2.yml` 批次 7 区块 19 行由 `Runs = 1` 改为 `Runs = 3`（`f1286c29`）。改之前先提交了核对脚本
  `evidence/l2/20260922-batch-7-exit-runs-check/Test-Batch7ConsecutiveRuns.ps1`（`8ec6692b`）：它执行 `l2.yml` 自己的 `$scenarios` 字面量，
  断言批次 7 恰好是上面 19 个场景、`scripts/l2/scenarios/` 下每个合成场景文件都在清单里、各 `Runs = 3`、不带 `DefaultRuns`、手动触发超时不少于 180 分钟。
  改动前 19 行全 FAIL、退出码 1（`red/01-red-before-change.txt`），改动后 PASS（`green/02-green-after-change.txt`）；
  在改后的副本上另做六种变异（批次标签改掉、加 `DefaultRuns`、超时降到 170、多一个未登记的场景文件、删一行、名字改大小写），每种都红在预期那一条
  （`mutations/03-mutations-after-change.txt`）。
- **作业超时不改。**第一轮的参考运行（下条）4 路 2357 秒，约 39 分钟，远低于 `workflow_dispatch` 的 180 分钟。
- 第一轮参考运行：run [`35689298714`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35689298714)，`f1286c29`，`consecutive-all`，
  55 个场景各三遍共 165 次，全部 PASS。**只作参考，不算出口证据**（调度定：第二轮在新身份上从头跑），证据未入库。
- **第二轮出口运行**：`gh workflow run l2.yml --ref b7-18/batch-7-exit -f mode=consecutive-all`，run [`35699901251`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35699901251)，
  提交 `f2ddd405`（产品即 `82bfa415`），4 路 2361 秒，汇总标题 `L2 (consecutive-all, 4 lanes, 2361s)`，没有 `superseded`（日志里唯一一处是源码回显）。
  55 个合成场景各三遍，**165 次全部 PASS，中途没有红**（批次 2～6 的 36 个随批次 7 代码回归，批次 7 的 19 个是本批三连）。
- 下载后逐份核对：165 份 `assertions.json` 的 `outcome` 都是 `PASS`；`identity.protocolReleaseIdentity` 都是 `protocol-v2.0.0`、`APPROVED_RELEASE`；
  `identity.controlServerCommit` 都是 `f2ddd405`；`identity.batchId` 按批次数与 `l2.yml` 逐行一致（`batch-2` 39、`batch-3` 6、`batch-4` 24、`batch-5` 15、`batch-6` 24、`batch-7` 57＝19×3）。
- 入库：`evidence/l2/20260922-ci-35699901251-<场景>-NN/`，165 个目录，连同各自的 `.log`。

### 真装置

固定清单的真装置走 CI（`l2.yml` `rig=real`，vm01 交互式 runner `win11-01-control-server-desktop`）。三端提交从 `Run real-onboard L2 scenarios` 步骤那一行读。

**混挂站点场景改为后侧先追加**（`b86d280a`，调度 09-22 评论第 1 条，票面「不改既有场景文件」的授权例外）：批次7-15 的原始证据是在「先追加前侧」下取的，
那时卸货按加入先后，「先前后后」是碰巧成立。现在丙（后侧）先追加、乙（前侧）后追加，按加入先后卸货会先开后侧那一扇，所以 `L2-MSO-10` 只有 control-server#303 的
按侧排序才绿。装货仍先扫乙（跨需求装货先后由操作员扫码决定，服务端不改）。

**红探针**（出口会话加的，调度批准）：在只把 cs#303 的两个 src 文件还原到合入之前的本地提交 `d259898d`（未推送）上跑一遍，运行前写下预期「只红 `L2-MSO-10`」，
结果一致：`L2-MSO-10` 红（卸货 `FRONT,REAR,FRONT`），其余十一条 PASS。`evidence/l2/20260922-b7exit-red-probe-no303/`。
车载端实际发布的是 `27a82b3`（`ecdb3a0b` 加 G2 证据，`evidence/` 以外 diff 为空），偏差写在该目录 README。

| 场景 | 遍数 | 第一轮（run、结果） | 第二轮 |
| --- | --- | --- | --- |
| `real-onboard-mixed-side-one-stop`（后侧先追加） | 3 | 35689371865，3/3 PASS，各 12 条判据全过 | **35699978289，3/3 PASS**，各 12 条判据全过 |
| `real-onboard-compensate-then-reconnect` | 1 | 35689381489，PASS | **35699989410，PASS** |
| `real-onboard-normal-load`、`-clock-skew`、`-load-door-closed-empty-reopens`、`-station-timeout-door-open`、`-unload-not-emptied`、`-restart-while-waiting-operator`、`-cancellation-authorization-lost`、`-durable-ack-lost`、`-expected-action-overdue`、`-restart-with-open-recovery-session`、`-restart-after-recovery-session-opened` | 各 1 | 35689381489，11/11 PASS（`durable-ack-lost` 未撞上 cs#307 形状） | **35699989410，11/11 PASS**（`durable-ack-lost` 未撞上 cs#307 形状） |

第一轮四行核对（两个 run 相同）：`control-server @ f1286c29…`（产品即 `517e1c7a`）、`8005-agv-onboard-hmi @ ecdb3a0b…`、`slots-simulator @ fb5f7c59…`；
`RIG_COMMIT_GUARD|RIG_DESKTOP_LOCK|RIG_DEADLINE` 各命中 1 次，都是源码回显。15 份 `assertions.json` 逐份核对 `outcome PASS`、三端提交、`batchId batch-7`、
`protocol-v2.0.0`／`APPROVED_RELEASE`。证据 `evidence/l2/20260922-ci-35689371865-*`、`…-35689381489-*`（红证据与发现记录，第二轮另取）。

**真装置清单之外的两条**：`real-onboard-inflight-load-reconnect`（control-server#205 降级）与 `real-onboard-recovery-entry-missing`（control-server#250）都不在
`l2.yml` 的 real-rig 清单里，本批没跑，见第六节。

第二轮四行核对（两个 run 相同）：`control-server @ f2ddd40522432925580d6048dca2d6d54171a7a8`（产品即 `82bfa415`）、
`8005-agv-onboard-hmi @ 86d42ce5362a8273525b8ba1acb387e4331bcfed`、`slots-simulator @ fb5f7c593742bf98bc3957b8729a38aad5321f28`；
`RIG_COMMIT_GUARD|RIG_DESKTOP_LOCK|RIG_DEADLINE` 各命中 1 次，都是源码回显；汇总标题 `Real-onboard L2 (consecutive-all, 3 runs)`、`Real-onboard L2 (default, 12 runs)`。
15 份 `assertions.json` 逐份核对 `outcome PASS`、三端提交如上、`batchId batch-7`、`protocol-v2.0.0`／`APPROVED_RELEASE`。
**出口证据**：`evidence/l2/20260922-ci-35699978289-*`、`…-35699989410-*`。

免跑判断（第 20、21 条）：**没有免跑**。第二轮的 15 次真装置三端都在出口身份上（服务端 `82bfa415`、车载端 `86d42ce5`、模拟器 `fb5f7c59`，从 run 日志读），
第一轮那 15 次的结论不沿用：服务端从 `517e1c7a` 前移到 `82bfa415`，而 #314 改的是就绪闸门之后的派车计划下发，每个真装置场景的派车都会经过那里，逐端问「变的分支会不会被执行」答案是会。

## 三、门禁

### 第一轮（2026-09-22，本机封锁时段，调度放行）

| 步 | 做什么 | 结果 | 证据目录 |
| --- | --- | --- | --- |
| 1 | 移 G3 共享绑定 | `1419ab99` | — |
| 2 | `CONTROL_SERVER_G2` × 1（`FP-IS-08`），经 `Invoke-HeavyLocal.ps1` | **PASS**：选中 62 个测试全过；出站 631 行、166 种、7 类消息，0 违约 | `evidence/g2/20260922-protocol-v2.0.0-f1286c29/` |
| 3 | `ONBOARD_HMI_G2` × 1（`FP-IS-08`，`ecdb3a0b`），协议用 `86575456` 的普通克隆 | **PASS**：选中 2、记录 2，build／test／format 退出码 0，发布态重跑协议 G1 | 车载端仓 `evidence/g2/20260922-protocol-v2.0.0-ecdb3a0b/`（小 PR onboard-hmi#194，第二轮之前合） |
| 4a | `run-staged-g3.ps1` | `STAGED_G3_RECOVERY_REPLAY_PASS` | `evidence/g3/20260922-protocol-v2.0.0-staged-517e1c7a/` |
| 4b | `run-staged-g3-restart.ps1` | `STAGED_G3_PROCESS_RESTART_PASS` | `evidence/g3/20260922-protocol-v2.0.0-restart-517e1c7a/` |
| 4c | `run-demand-bearing-g3-vectors.ps1` | `DEMAND_BEARING_G3_VECTORS_PASS` | `evidence/g3/20260922-protocol-v2.0.0-demand-bearing-517e1c7a/` |
| 4d | `run-journey-g3.ps1`（15 场景） | **`JOURNEY_G3_SLICE_FAIL`，14/15**：`g3-multi-stop-plan` PASS；`g3-reversed-direction-journey` PASS（第一次带着 #272 跑）；`g3-task-type-admission-fail-closed` 在判据之前中断（第四节） | `evidence/g3/20260922-protocol-v2.0.0-journey-517e1c7a/` |

两端 G2 的 `gate-result.json` 都绑 `protocolTag protocol-v2.0.0`、`protocolRepositoryCommit 86575456`、`protocolManifestSha256 4ac095ad…`、`protocolApprovalStatus APPROVED_RELEASE`。

**为什么四个 runner 都跑**：批次7-15（#218）改了四个 runner 共用的切片归属表 `g3-slice-evidence.ps1`。

### 第二轮

2026-09-22，control-server#314 合入后，本机第二段封锁（调度放行）。

| 步 | 做什么 | 结果 | 证据目录 |
| --- | --- | --- | --- |
| 1 | 重移 G3 共享绑定（merge `82bfa415` 进本分支后） | `f2ddd405` | — |
| 2 | `CONTROL_SERVER_G2` × 1（`FP-IS-08`），经 `Invoke-HeavyLocal.ps1` | **PASS**：选中 62 个测试全过；出站 631 行、166 种、7 类消息，0 违约；`implementationCommit f2ddd405` | `evidence/g2/20260922-protocol-v2.0.0-f2ddd405/` |
| 3 | `ONBOARD_HMI_G2` | **沿用第一轮**（车载端与协议两端都没变，见「身份」） | 车载端仓 `evidence/g2/20260922-protocol-v2.0.0-ecdb3a0b/`（onboard-hmi#194 已合入） |
| 4a | `run-staged-g3.ps1` | `STAGED_G3_RECOVERY_REPLAY_PASS` | `evidence/g3/20260922-protocol-v2.0.0-staged-82bfa415/` |
| 4b | `run-staged-g3-restart.ps1` | `STAGED_G3_PROCESS_RESTART_PASS` | `evidence/g3/20260922-protocol-v2.0.0-restart-82bfa415/` |
| 4c | `run-demand-bearing-g3-vectors.ps1` | `DEMAND_BEARING_G3_VECTORS_PASS` | `evidence/g3/20260922-protocol-v2.0.0-demand-bearing-82bfa415/` |
| 4d | `run-journey-g3.ps1`（15 场景） | **`JOURNEY_G3_PASS`，15/15**；`FP-IS-01`／`02`／`03`／`07`／`08`／`10`／`11` 七片 `gate-result.json` 都是 `PASS`、`formalSlicePass true`，`commitSource SHARED_BINDING` | `evidence/g3/20260922-protocol-v2.0.0-journey-82bfa415/` |
| 5 | CI 真装置 | 15/15 PASS | 第二节 |
| 6 | CI `l2.yml` `consecutive-all` | run `35699901251`，165/165 PASS | 第二节 |
| 7 | 服务端全量 L1（草稿上 `workflow_dispatch` `test.yml`）；本 PR 转 ready 后默认 CI | 2353/2353；默认一轮见检查页 | 第一节 |
| 8 | 车载端全量 L1 | 938/938（push 运行，提交相同） | 第一节 |

开跑前核过车载端顶端（`git ls-remote`）是 `86d42ce5`；四个 runner 的构建都没有遇到调度提醒的 `dotnet build-server shutdown`（日志里没有编译服务器断开或 MSBuild 节点退出）。

**journey 这一轮绿不是修复成立的证据。**第一轮那一红是一个时间窗口，命中率推算约两成，这一轮没撞上本来就可能。修复成立的证据是 control-server#314 自己的
确定性红用例（`PickupDispatchPlanPastOwnOrderTests`，修前红、修后绿）；这里的 15/15 证明的只是第二轮代码上各片 G3 不退化。

**`FP-IS-08` 的四道门禁**：G1 是协议侧发布证据——`protocol-v2.0.0` 的 G1 在发布态通过（program#97），本批协议零改动，车载端 G2 在发布态重跑了一次（`logs/protocol-g1.log`）；
两端 G2 PASS；G3 由 journey 的 `g3-multi-stop-plan` 认领（control-server#218 的归属表），PASS。**四道全 PASS。**

## 四、红证据与缺陷单

| 单 | 红在哪里 | 性质 | 修复去向 |
| --- | --- | --- | --- |
| [`20260922-first-plan-lost-when-onboard-sees-own-order.md`](defects/20260922-first-plan-lost-when-onboard-sees-own-order.md) | 第一轮 journey G3：`g3-task-type-admission-fail-closed` 等「派往取货站的计划被确认」超时，判据一条都没写出 | **产品缺陷**：受理时 RIoT 单已建成并确认；车载端在运行时下一轮推进之前读到本服务端的在途单，按 control-server#138 的设计报 `VEHICLE_NOT_READY`，会话进 `RecoveryRequired`；派往取货站的计划只在就绪闸门之后发，于是从未写进发件箱（失败现场库 `ProtocolOutbox` 0 行），车上挂着那张单时会话不会恢复。连接一直活着，不是 cs#307 形状 | control-server#314（挡出口，用户定先修再从头重跑） |

红证据全部原样保留：`evidence/g3/20260922-protocol-v2.0.0-journey-517e1c7a/`（含失败现场库的关键事实 `…/g3-task-type-admission-fail-closed/stage-db-facts.txt`）。

**这个缺陷第一次撞上时被误判为环境偶发，第二次撞上才查清机理。**control-server#211 的 G3 自检（2026-09-21，`fc144d0a`）在 `g3-reversed-direction-journey` 上撞过完全相同的形状
（`evidence/g3/b7-06-journey-final-head/README.md`：`VEHICLE_NOT_READY` → `ONBOARD_SESSION_NOT_READY` → 计划不再发出 → 60 秒不恢复），当时单独重跑绿，
「车载端为什么 60 秒不恢复」记为没有查清，调度接受为环境偶发。本票第一轮在另一个场景上再次撞上，位置不同说明它不是某个场景的问题；
读失败现场库才看到那一条「没有查清」的答案：车上挂着本服务端的单，车载端按设计不会恢复。

窗口的来历（详见缺陷单，读到的与推出的分开）：结构上批次 6 就有（先推进、后受理，受理时建单）；库里批次 5、6 的 journey 证据 10 次机会 0 次命中，
批次 7 的 5 次机会 2 次命中——提示批次 7 把窗口拉宽了，但样本太少，也没找到是哪一次合入。

## 五、必须如实写明的各点

1. **持货超时的 L2 缩短值**：出厂 30 分钟（ADR-cross-0057）。`cargo-holding-timeout` 与 `cargo-holding-disabled-when-append-forbidden` 用 40 秒；
   `cargo-holding-side-full`、`waiting-station-yield`、`waiting-station-yield-waits-for-door`、`vehicle-full-ignores-oversized-and-gated`、`vehicle-full-still-appends-before-departure` 用 10 分钟；
   真装置 `real-onboard-mixed-side-one-stop` 用 3 分钟（起算点是整趟第一笔装货落定，要盖过 11 号站 60 秒修正窗口加 12 号站两次录入装货）。
2. **参数未批时的行为**：`REQ-0198` 未配置或为零的分区禁止途中追加、不持货等单；`REQ-0203` 未配置时只计龄、不跨带升级、不发升级告警；初始派车只取一条需求。
   所以参数批准前 v2 的行为与批次 6 相同（L2 `en-route-append-not-configured`、`cargo-holding-disabled-when-append-forbidden` 的「未配置」半边、`starvation-threshold-escalation` 的「未配置不升级」半边）。
   批次7-17（control-server#219）状态：open，人工标定待批准。
   **control-server#290 必须先于任何分区参数启用合入**：持货超时后车仍开往另一取货停靠装货，而车上收到 `CLOSED/CARGO_HOLDING_TIMEOUT`，
   与 program#94 对 `CLOSED` 的语义矛盾（control-server#212 审查复现）；默认不配置分区参数时不触发，七条判据不依赖它。
3. **没有单独的「每趟需求上限」开关**：第 8.7 节与第 8.4.2 节 S1 的「每趟需求上限 1」在本批由「初始派车只取一条」加「分区途中追加上限 0」共同实现；
   关掉持货等单与多需求累积的办法是把分区途中追加值导入为 0。
4. **`REQ-0185` 的承载方式与现场前提**：靠分区归属表不收录承载（用户 2026-09-19 定，不改代码）。批次7-09 点名 `REQ-0185` 的 L1 是
   `Batch7Req0185EutecticExclusionTests.Req0185AnEutecticAreaTheAssignmentTableLeavesOutStaysInTheBacklogSilentlyWhateverTheMapHas`
   （`OUT_OF_SCOPE_AREA`、留在积压、不派车、不告警，阈值配了也不升级）。**现场前提：分区归属表不收录共晶、低温共晶机台，RIoT 地图上也不放它们的站点。**
   只删地图站点、分区表仍收录，会常年报 `AREA_STATION_NOT_FOUND`（同类 `AnAreaTheTableNamesButTheMapLacksRaisesAStructuralBlockAtOnce`）。运维说明同样写明（第六节）。
5. **九条腿只由两端 G2 证明**：G3 与真装置证明的是三条以上的真实计划，不写「九条腿已跑通」。
6. **让站的车载端显示只由 G2 证明**：真装置只有一个车载端、编排器不支持真车载端与合成对端混跑，让站只能由合成多车 L2 证明服务端、由车载端 G2 证明显示；
   持货倒计时在真装置上只看得到持货超时那一支（`real-onboard-mixed-side-one-stop` 的 `L2-MSO-07`／`08`）。
7. **卸货一次一扇**：第 8.3 节原文「卸货时两组同开」已由第 20 节补记取代，真装置场景按一次一扇、先前后后判（`L2-MSO-10`、`L2-MSO-11`）。
8. **`OperationSession` 粒度**：每次到站一个会话，站内多条需求逐条串行（第 22 节补记，`CONTEXT.md` 已由批次7-16 统一）。离站安全核验在多需求旅程里带第一条受理的需求；
   车载端**不拿它比对清单**：批次7-13 核实 `WireToGateSessionClient` 解析 `PreDepartureSafetyCheck` 只校验 `demandId` 是 UUID，`HandlePreDepartureSafetyCheckAsync`
   只比安全状态版本、按 IO 作答，由 G2 `APreDepartureCheckNamingTheFirstDemandIsAnsweredAsForOneItem`（四例）钉住（PR onboard-hmi#143 正文）。
9. **多需求下车载端的两个操作员入口**：扫码前取消由操作员在清单里选需求、服务端校验它属于本停靠；装后纠错只针对本站最后一次装货，界面文案写明（批次7-14）。
10. **车辆占用**：`VehiclePurposeClaims` 为权威，用途占有与租约同一次保存写、放；订单占用的认领与释放时点沿用今天（正常卸货路径晚一轮释放）；旧两套批次 8 退役，本批未删。
11. **车载端回滚注意**：新车载端把多条清单项与多条腿存进日志后，回滚到批次 7 之前的车载端会在启动恢复时报 `PROTOCOL_SCHEMA_INVALID`；
    回滚前先清日志里的旅程快照，或确认车辆没有在途旅程（批次7-13 的部署说明）。
12. **`REQ-0208` 电量半边仍未实施**（第 19.5 节）：今天比较的是当前电量，只在一趟一单的固定行程下成立；多需求旅程下这条假设更弱（第六节）。`REQ-0210` 的升级告警本批已做（批次7-09）。
13. **`protocol-v3.0.0` 不在本批**：`REQ-0359`（人工判故障）等待办仍攒在 program#115，本批协议零改动；发布时会作废本批全部门禁与 L2 证据。
14. **control-server#166**：人工票，不挡出口（「等用户拍板的人工项」）。
15. **`REQ-0198` 的量纲**：按路径代价增量（毫米）实现，与基线文字「预计到达终点时间」有出入，属实施口径（规格第 22 节补记）。
16. **本批 migration 四张，票面写的是三张。**票面预计「批次7-01 一张，另有 #199、#186 各一张」。实际（`e74c0058..517e1c7a` 在 `Migrations/` 下实读）：
    #206、#199 两张与票面一致；#186 降级未合入，少了这一张；多出两张，一张是 #228（反向旅程准入被撤的独立起点，加一列），
    一张是 #211 的数据回填迁移（票面没有预计到多停靠会为既有旅程回填装货从属行）。逐个列出：
    - `20260919154546_Batch7MultiDemandJourneyPersistence`（批次7-01，#206）：本批唯一建表票。
    - `20260919200353_AreaEndAdmissionRevokedSince`（#228）：反向旅程「准入被撤」的超时升级改用独立持久起点，加一列。
    - `20260920001500_AuditImmutabilityTriggers`（#199）：审计表在数据库层加 `BEFORE UPDATE/DELETE` 触发器。
    - `20260920145604_Batch7LoadingMembershipBackfill`（批次7-06，#211）：为既有旅程回填装货从属行（数据迁移）。
    - #186 未合入（降级，第六节），没有它的迁移。
17. **持货等单不是相对 MVP 的倒退，是有意的行为变化**（第 8.8 节第 4 条）：多需求累积要求车在最后一个装货站等可装入的新需求，这是批次 7 要交付的能力本身。
    它在分区参数批准并导入之后才会发生（第 2 点），每一次都以持货超时、装满或让站收尾。需求条目一律按基线 `v1.4.0` 引用。
18. **`FP-IS-08` 是新切片**，没有可沿用的旧通过结论；向量从未被机械执行（弱绑定），「同名具名测试」只证明绑定存在。
19. **恢复会话开成后断电，「重启后首次提交动作」这条路径没有覆盖**，与已覆盖的那条不是同一条：
    - 造不出来的原因：要丢掉**上行**的 `RecoveryActionSubmitted`，而协议故障代理两处判断都带 `direction == ServerToOnboard`
      （`tools/ControlServer.ProtocolFaultProxy/ProtocolRelay.cs` 的 `TryDropAck` 与 `TryDropMessage`），只能丢下行。
    - control-server#230 覆盖的是另一条：重启后重放 r2 快照、操作员再按同一动作，走车载端 `WireToGateBusinessService.RecoveryVectors.cs`「快照在、选的是同一个动作」的分支重发请求。
    - **两条是不同的代码路径**。要不要扩故障代理支持丢上行，出口时由用户定，本票不自行扩（「等用户拍板的人工项」）。
20. **途中追加只在停站时**（用户 2026-09-22 定，program#133、control-server#286）：混挂站点与多停靠证据中的追加都发生在车停站期间
    （`real-onboard-mixed-side-one-stop` 在车停在 11 号站、甲装完之后才发乙丙）；行驶中追加被 `ONBOARD_FACTS_NOT_READY` 挡住是预期行为，不是已知缺陷。
21. **部署约束：这个服务端要求车载端心跳已经是 2 秒**（control-server#234 的 PR 正文，原样登记）：
    > 六秒的存活窗对两秒的心跳只有三拍余量。用还在发 5 秒心跳的旧车载端部署这个服务端，会进入每六秒一次的重连循环：服务端判失联、关连接，车载端重连、握手，然后再等六秒。
    > 所以本服务端版本要求车载端至少含 onboard-hmi#142（经 onboard-hmi PR #147 于 2026-09-20 01:59Z 合入 `w2g/fp-v2-impl`）。这不是测试约束，是部署约束。

    写进出口报告的理由：出口报告是部署的人会读的东西，PR 不是。

### 经验：真装置免跑的判据要对三端各问一遍

判据本身没问题：「变的那条分支在这个场景里会不会被执行」，比「产品代码有没有变」准，也比「blob 一样不一样」细。错在只对一端问。
control-server#208 那次，服务端自己的 diff 核得没错（返工那四行在单需求路径上两条分支都不执行），但那遍跑的车载端是 `5d4a5814`，
其后车载端又合入了 onboard-hmi#145 与 #152 两张改恢复路径产品代码的票，而它跑的 `real-onboard-compensate-then-reconnect` 走的正是那条路。
于是账变成：服务端新 × 车载端旧跑过、车载端新 × 服务端旧跑过、服务端新 × 车载端新从未跑过——而那恰恰是合入后集成分支的实际状态（Coordinator 7 自己踩过，免跑已收回、补跑）。
做法：先从 run 日志 `Run real-onboard L2 scenarios` 步骤的 `control-server <40>, onboard <40>, simulator <40>` 那一行读出三端，再逐端比对各自集成分支的当前顶端，
对每一端问一遍那个判据；只重跑被影响的场景。本票出口同样适用：两轮真装置各 15 次，三端都在当轮身份上，没有免跑；第二轮服务端前移（#314），第一轮的真装置结论没有沿用。

### 经验：同一机理第二次撞上

journey 那一红（第四节）第一次出现在 control-server#211 的自检里，单跑重跑绿，被接受为环境偶发；「为什么 60 秒不恢复」那一条没有查清就放过了。
第二次在另一个场景上撞上，才去读失败现场的库，答案就在那里：发件箱 0 行、会话因本服务端自己的在途单而未就绪。
**「重跑一遍绿了」只说明那一次没撞上窗口，区分不了偶发与缺陷**；一条「没查清」的记录本身就是没有结论，不能当作「环境」的依据。

## 等用户拍板的人工项

| 票 | 要做什么 | 状态 |
| --- | --- | --- |
| control-server#166（批次6-10） | 在 `mapId 26` 上建派工待送取货站点、按 `mapId` + `STAGING_TO_WIRE` 绑定、做现场用途核对 | 需要现场操作，等用户安排。**从批次 6 挂到现在**。它不是任何会话能做的，要在真实设备上建站、绑定、现场核对 |
| 批次7-17（control-server#219） | 每区途中追加最大允许增量（`REQ-0198`）与每区防饥饿阈值（`REQ-0203`）的标定与批准 | 批次 7 主票，出口时仍 open（人工标定待批准）；参数未批时 v2 行为与批次 6 相同（第五节第 2 点） |
| 故障代理是否支持丢上行 | 第五节第 19 点 | 出口时由用户定 |

## 六、剩余风险

### 上真车前必须合入

用户 2026-09-22 定：control-server#186、control-server#299 与 onboard-hmi#191 同一档，都必须在 v2 上真车（约 10-08）之前做完。

- **control-server#186（Map 级改名检测）**：用户 2026-09-22 降级为「出口后、v2 上真车之前」，与 control-server#299 同档，约 10-08 前做完。
  现状：同一 `mapId` 下地图改名、站点不变时服务端照常派车。兜底只有两道：车辆报告的 `CurrentMap` 与配置不符时挡派车；`mapId` 变化时目录新鲜度门禁整图阻断。

### 行为与现场

- **control-server#314（派往取货站的计划可能发不出）**：第四节。已修并合入（PR #315，`82bfa415`），不再是剩余风险；留在这里是为了记下它在现场的后果窗口只到 #314 部署为止。
- **control-server#290**：持货超时截断未开始装的停靠（第五节第 2 点），必须先于任何分区参数启用合入。open。
- **control-server#291**：卸货侧「已卸」与阶段推进分两次保存，中间崩溃后车静默停住或整轮中止（U1～U3），**默认一车一单配置下即可触发**。open。
- **control-server#310**：同车旧连接半开时，新连接握手完成后才被拒。open。
- **control-server#311**：激活命令在握手换代的毫秒窗口里可能错过补发——修前靠一次断线送达，修后既不断也不送。open。
- **cs#307 形状**：第 2 条连接握手完成约 42 秒后服务端停止应答、车载端 `TimeoutException` 自断；run 35679117127 `real-onboard-durable-ack-lost` 三连的 -01、修前 run 35516706603 都出现过。
  两轮真装置都未撞上（`durable-ack-lost` 各一遍 PASS）。只跑一遍，证不了它不存在。
- **`REQ-0208` 电量半边未实施**（第五节第 12 点），多需求旅程下假设更弱。
- **部署约束：车载端心跳必须已是 2 秒**（第五节第 21 点）。
- 其余开着、票面写明不挡出口的：control-server#237（准入被撤升级后故障事实不记）、#243（多车夹具无限延时钩子）、#246（staged runner 自检覆盖只给服务端）、
  #253（TCP 断开时会话仍 Ready）、#254（未来收件时间不算数）、#255（按车判存活全表读收件箱）、#256（静默失联夹具三处偶发红）；
  onboard-hmi#150（恢复结果 ack 迟到后没有清记录的钩子）、#161（正向判据停在业务层会假绿）、#164（见下）、#167（执行器检查点交错用例缺一半）、#168（一条用例单跑绿整类稳定红）。

### 批次 6 出口审查留下的尾巴

**这不是「批次 6 没做完」。**批次 6 追踪 program#123 已于 2026-09-19 14:13 关闭，出口提交 `e74c0058`（PR #197），出口后的顶端兜底全绿，正常票全部完成。
下面几张是它自己的出口审查发现的尾巴，当时逐条判过不挡出口。四张测试类的共同性质是「判据太松或取样时机不对」：
**这一类的危险不是它们红，是它们绿得不该绿**——判「回退」的看不见「缩水」、断言 `>=` 的看不见「被推后」、数「条目数 > 0」的看不见「清空成空字典」、断「不许调旧方法」的看不见「调新方法但丢弃参数」。

| 票 | 一句话 | 去向（用户 2026-09-20 定） | 出口时状态 |
| --- | --- | --- | --- |
| control-server#203 | `FP-IS-10`／`11` 与恢复收尾的 G3 判据收紧；八条里有一条真假绿：占车冲突会被判成「车放出来了」 | 合入前置 | 已关闭（PR #263 `dd733b40`） |
| control-server#204 | 真装置与合成场景的判据取样收紧；判据缺席型假绿（期限等待超时后那条判据根本不产生结论而整轮 PASS），`L2-DA-09` 把界面读失败算成「扫了一轮没检出」 | 合入前置 | 已关闭（PR #258 `10175635`） |
| control-server#205 | 一次性真装置场景转正式并登记清单 | 降级为剩余风险（见下） | open |
| onboard-hmi#132 | 在途结果补发的四个窗口 | 拆：原第 4 条留作前置，第 1～3 条降级（见下） | 原第 4 条已合入（PR onboard-hmi#193，`ecdb3a0b`）；第 1～3 条见下 |

**`real-onboard-inflight-load-reconnect` 没有进任何常规清单**（control-server#205 降级）：
- 它证的是在途装货断线重连后，同一 attempt 的结果在新代次里受理、会话回 `Ready`、装货收尾（control-server#189 起源，onboard-hmi#127 用它证明修复）。
- 今天只以副本形式存在于两个证据目录里，不在 `scripts/l2/scenarios/`。那条修复将来被改坏，没有任何常规运行会发现。
- 比「不会自动跑」还差一档：#205 票面第 3 条明写不进 `l2.yml`，而 `l2.yml` 的 `scenarios` 输入是白名单——不进清单就连手动 CI dispatch 都跑不了
  （run `35492974357` 一分钟 failure、场景没启动）。转正后它既不会自动跑、也不能手动 CI 跑，只剩「有人本机跑真装置时会跑到」。
- 它与 control-server#250 绑在一起：#250 若选「让 workflow 能跑清单外场景」，#205 才有意义；若选「占本机时段核实」，#205 转正式场景的收益基本为零，该重新评估。

**在途结果补发的三个窗口已降级**（onboard-hmi#132 拆分后，逐条照原文）：
- 第 1 条（同一握手里发两遍）：票面自己写「服务端无害」——字节相同的重放回第一次的响应，只差 `sessionGeneration` 的走 `RebindDurableAckAsync`，不会回 `ProtocolProblem`、不会断线循环。只缺一条 G2 把它钉住。
- 第 2 条（`COMPLETED` 补记要等下一次就绪）：真行为问题，但后果是晚一点，不是错。
- 第 3 条（旧格式拒绝行的升级冲突）：**记成「待核实」，不是「已知风险」**。票面自己写「开工先核实现场升级路径上这类行是否可能存在；确定不可能时只补一条测试」——它可能根本不存在
  （要求升级前恰好有一条未确认的拒绝）。一条「可能根本不存在」的缺陷被降级，和一条「确认存在但不重要」的缺陷被降级，是两回事。

### 证据的边界

- **`real-onboard-recovery-entry-missing` 的「按设计是红的」结论，其根因已于 2026-09-04 修复，而四处描述未更新、场景今日状态未经核实**（control-server#250 排在出口之后）：
  `scripts/l2/README.md` 的状态描述与 `.github/workflows/l2.yml` real-rig 清单上方的注释（它引的正是 README）。旧根因「`DecideReadinessAsync` 不看 `StationOperationStatus.RecoveryRequired`」
  对应的缺陷文档 `docs/defects/20260904-recovery-required-never-reaches-session-state.md` 已 resolved，修复提交 `64e9bcc`、`248d8eb` 都在 `fp/v2-impl` 祖先里。
  它不在自动轮次里，也不能 CI 手动 dispatch（run `35492974357`，`##[error]Not in this workflow's scenario list`）。
  **这条场景在本批既没跑过，也没人能顺手跑一次，而它文档里的定性已经不成立。**
- **`CV-LOAD-CANCELLATION-ALL-EMPTY` 覆盖不完整**：缺「多需求下取消在途装货、清空结果报 `UNKNOWN` 时，恢复入口指向被取消的那一条需求」。该向量今天 8 条 G2 判据
  （`RecoveryVectorG2Tests` 3、`StationDeadlineExpiredG2Tests` 4、`StationDeadlineExpiredG2Tests.TakenOverReplayRace` 1，按 `ProtocolVector` 标记数），无一覆盖这一格。
  搜 `UNKNOWN` 搜得到的用例标的是另一个向量 `CV-OPERATION-RESULT-UNKNOWN-RECONCILE`，按向量标记数才数得准。跟进票 onboard-hmi#164（open）；它合入后这一条可去掉，届时应为 9 条。
- **control-server#312**：L2 自检脚本随机端口与并发作业撞端口偶发红。open。
- **`L2-MSO-04` 的装货半边由驱动顺序保证**（场景等乙提交后才扫丙），证不了车载端执行器串行；有判别力的是卸货的 `L2-MSO-10`、`L2-MSO-11`，红探针证明了 `L2-MSO-10` 的判别力。
- 让站显示、九条腿只由 G2 证明（第五节第 5、6 点）；向量弱绑定（第 18 点）；合成 L2 与真装置都不证明真实硬件（光幕极性、锁反馈时序、机械弹开）。

### 运维说明

- `REQ-0185` 现场前提（第五节第 4 点）：分区归属表不收录共晶、低温共晶机台，RIoT 地图上也不放它们的站点；只删地图站点、分区表仍收录会常年报 `AREA_STATION_NOT_FOUND`。
- 关掉持货等单与多需求累积：把分区途中追加值导入为 0（第五节第 3 点）。
- 车载端回滚前清旅程快照（第五节第 11 点）；服务端要求车载端心跳 2 秒（第五节第 21 点）。

## 七、转后续

| 票 | 内容 | 状态 |
| --- | --- | --- |
| control-server#314 | 派往取货站的计划在车载端看到本服务端在途单后发不出（第四节） | 已修并合入（PR #315，`82bfa415`） |
| control-server#186 | Map 级改名检测 | 上真车前 |
| control-server#290、#291、#310、#311、#312 | 见第六节 | open |
| control-server#205、#250 | 一次性真装置场景转正；`recovery-entry-missing` 定性核实 | open，出口后 |
| onboard-hmi#164 | `CV-LOAD-CANCELLATION-ALL-EMPTY` 缺的那一格 | open |
