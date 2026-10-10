# 批次 8 与协议 v3 合并出口报告（v2 线）：车辆用途占有、等待点与空闲返回（`FP-IS-12`），`protocol-v3.0.0`

control-server#393（批次8-23）。本报告逐项对照规格 `8005-agv-program/docs/specs/full-product-scope-and-sequence-v2.md` 第 8.3 节批次 8 行，以及 v3 的重证口径（第 6.5 节、第 8.3 节批次 5 行）。规格按第 19～24 节补记读，第 24 节由本票的 program PR #167 补。
需求条目按基线 **`v1.9.0`** 引用（开工时的现行版本；票面写的 `v1.6.0` 之后又批了 `CP-0008`、`CP-0009`、`CP-0010`）。门禁与证据全部绑定 `protocol-v3.0.0`。

> **状态：出口达成，待调度审查。整轮先红后绿，红的是前三轮 G3 与第一轮真装置的一个场景。**
> - G3 一共跑了四轮。前三轮都红，红的全部是 G3 场景脚本或 runner 判据：要么没跟上已合入的有意改动，要么在出口前从没真跑过，要么把界面读写夹具的一次差错当成了产品结果。**三轮里没有一处是产品行为出错**（第四节）。修脚本的三张票 control-server#541、#555、#560 都只改 `scripts/`，作为出口冻结例外合入。第四轮（绑定 `76c9cfe2`）四个 runner 全 PASS，15 个切片全部 `formalSlicePass true`。
> - 第一轮真装置 31 次中 30 次 PASS，红的是 `real-onboard-restart-with-open-recovery-session`，形状与还开着的 control-server#307 一致。vm01 空闲时单独连跑三遍，全部 PASS（第二节）。
> - 全部红证据保留。红轮次里 PASS 场景的证据按调度定的方案精简入库，完整原件的去处见「证据精简」一节。

## 结论

**批次 8 与 v3 合并出口达成。**两端在 `protocol-v3.0.0` 发布身份上：L1 全量绿；第 8.3 节批次 8 行的 L2 判据与本批场景三连全 PASS；两端 G2 各 15 片全 PASS；G3 四个 runner 在出口绑定上全 PASS，覆盖全部 15 片；部署包在出口最终提交上打得出来。

| 出口（规格 8.2／8.3 批次 8 行、v3 重证口径、本票验收） | 状态 | 依据 |
| --- | --- | --- |
| 前置核对逐项通过；上真车验证与用户授权本次发布的出处已写明 | 成立 | 「前置核对」 |
| tag 指向的提交等于冻结候选；两端发布身份 PR 合入批次分支，CI 绿 | 成立：服务端 PR #538（`4477ec08`），test run `37932707562`、l2 run `37932707567` 均 success；车载端 onboard-hmi PR #284（`33f26018`），test run `37933398396` success | 「身份」 |
| 两个合回 PR 已合入，提交前全仓 grep 冲突标记无命中；合回后两端全量 L1 绿 | 成立：PR #540（`5f3adc42`）、onboard-hmi PR #286（`b9e67a53`），2026-10-09 23:06 连着合入；服务端 4657/4657，车载端 881 + 812 | 第一节 |
| 15 片 `CONTROL_SERVER_G2` 与 `ONBOARD_HMI_G2` 全 PASS，`gate-result.json` 绑精确身份 | 成立：两端各 15/15；服务端每片 `schemaConformance` 违规 0 | 第三节 |
| G3 四个 runner 在出口绑定上各一轮全绿；`FP-IS-12` 的 `formalSlicePass` 由断言算出 | 成立：第四轮（绑定 `490faa02`，ControlServer `76c9cfe2`）staged、restart、demand-bearing、journey 全 PASS，journey 20/20；15 片全部 `formalSlicePass true` | 第三节 |
| 服务端部署包在出口最终提交上打得出来 | 成立，只打包不部署：见 `evidence/rc/20261010-batch-8-exit-final/`（打包在本报告所在提交之后做，产物清单作为只含证据的追加提交入库） | 第三节第 10 步 |
| 真装置默认清单全 PASS，本批新增真装置场景连续三遍 PASS | 成立（带一处说明）：`real-onboard-slot-fault-declaration` 3/3；默认清单 30/31，红的那个在空闲 vm01 上 3/3；出口期间录入路径改过的两个场景在出口分支上补跑 6/6（run `38032912362`） | 第二节 |
| `l2.yml` 批次 8 各行 `Runs = 3`（改前红、改后绿）；CI `consecutive-all` 全部 PASS，证据逐份核对 | 成立：run `37953826544`，80 × 3 = 240 次全 PASS，逐份核对 | 第二节 |
| 第 8.3 节批次 8 行两条 L2 判据与第 19.5 节第 8 条两个急停场景各有证据目录 | 成立 | 第二节对照表 |
| program 仓规格措辞 PR 已合入（以补记写） | **未合入**：program PR #167 写好待合 | 第五节第 18 点 |
| 出口冻结期间没有合入其它 PR；证据小 PR 的合入时点 | 冻结自 2026-10-09 23:06 起。例外只有调度裁定的三张脚本票 control-server#541、#555、#560（都只改 `scripts/`）；车载端证据小 PR onboard-hmi#288 在 G3 全部跑完后合入（`ca89ef8f`，只加 `evidence/`） | 「身份」 |
| 每次门禁新目录；红证据保留，`docs/defects/` 有记录 | 成立：四份缺陷单 | 第四节 |
| 「必须如实写明」各点，无第 8.8 节禁止的表述 | 已写 | 第五节 |
| 未切换 `C:\Users\szy\Desktop\8005-workspace\repos\` 下任何克隆 | 成立：全部操作在 `8005-workspace-v2`，`repos/` 下的克隆没有切换分支 | — |
| 本 PR 的 CI `test` 与 `l2` 两项绿 | 见 PR 检查页 | PR 检查页 |

## 前置核对（2026-10-09 实查）

| 项 | 结果 |
| --- | --- |
| program#152 已完成：`protocol-v3.0.0` 注释 tag 存在、发布态 G1 通过、attestation 一名批准 | 成立。tag 对象 `e08c362eaceb9dda34d18f3da81596d783992047`（`git cat-file -t` 为 `tag`）解引用到 `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c`（本机协议仓实读）。批准者 `AI_AGENT`，`authorizedBy Zhengyu Shao`，`decidedAt 2026-10-09T10:41:00Z`；用户 18:41 CST 授权本次发布，见 program#152 评论 6079269621；发布态 G1 PASS（program#152 切换说明，评论 6079776769）。program#152 已关闭 |
| 上真车验证完成、用户授权这一次发布 | **用户授权**：program#152 评论 6079269621，调度记录。2026-10-09 18:41 CST，用户原话「现在就授权」，选「授权 AI 代批」；范围只限 `protocol-v3.0.0`／`3f091cb2…`／`d5e1a53f…`。<br>**上真车验证**：同一条评论与第 0 步复核（评论 6079532782）都写着，用户 10-09 认定完成，依据是 agv02 跑了两趟主流程、IO 为模拟器（调度看板 `ds-tasks/board.md`）。这里要说清楚：那两趟跑的是 **v2.0.0 包**，**v3 发布前没有在车上跑过**，因为候选身份打不出上车包（`New-WireToGateReleaseCandidate.ps1:366`）。v3 第一次上车是在本票之后，而且属于另外的动作，要逐次授权。<br>这两条都是读调度的记录得来，本票没有重新核验现场 |
| 服务端批次8-08～11 已合入 `batch-p3/v3` | 成立：#382（PR #421 `33f3df17`）、#383（PR #424 `454fb3c2`）、#384（PR #467 `c3b09112`）、#385（PR #425 `3cf62fec`） |
| 车载端批次8-12～14 已合入 `w2g/batch-p3/v3` | 成立：hmi#214（PR #224 `582ebc76`）、#215（PR #247 `2852de0b`）、#216（PR #225 `c79b4c6f`）；评论里补的前置 hmi#219（PR #248 `cda80e4d`）也已合入 |
| 服务端批次8-15～21 与车载端批次8-22 已合入各自集成分支 | 成立：#386（PR #394 `af2b02cb`）、#387（PR #413 `d4d3ad08`）、#388（PR #397 `7ee525b7`）、#389（PR #417 `fa296d3d`）、#390（PR #426 `d5efa06f`）、#391（PR #418 `46dcc6c5`）、#392（PR #438 `b1548d0d`）；hmi#217（PR #218 `b28cd9cc`） |
| 协议仓批次8-01～06 已关闭 | 成立：program#146～151 全部 CLOSED |
| tag 指向的提交等于批次8-06 冻结的候选提交 | **成立**：program#151 关闭评论的候选提交是 `3f091cb2…`（父提交 `86575456`），等于 tag 解引用的提交；manifest `d5e1a53f…` 与候选相同 |
| cs#251 已关闭；挂批次 8 追踪 issue（program#153）的其余非人工票全部关闭 | 成立：cs#251 经 PR #398（`0c0edfa5`）合入后关闭；program#153 的子票 cs#395、hmi#219 都已关闭；正文票表里的票全部 CLOSED。唯一开着的是人工票 cs#166 |
| 本批各票加进 `l2.yml` 的场景（`BatchId = 'batch-8'`）与第 8.3 节批次 8 行等逐条对上 | 成立，见第二节对照表。合成 7 行，真装置 1 行（`real-onboard-slot-fault-declaration`） |
| 两端 `VectorsAwaitingTheirSlice` 已无 `CV-WAITING-POINT-IDLE-RETURN`、`VectorsThisBatchOwesANamedTest` 已空、`FP-IS-12` 已进两端 `SlicesThisLineImplements` | 成立（v3 合并后的代码实读）。两端的等待名单都只剩 `CV-WORKLIST-SELECTION-ACCEPTED`、`CV-WORKLIST-SELECTION-STALE-REVISION`（`FP-IS-09`，批次 11）。车载端 `VectorsThisBatchOwesANamedTest` 为空，服务端没有这张表。两端都是 **15 片**（`FP-IS-00`～`08`、`10`～`15`）：票面写的「14 片」之后批次 9 又加了 `FP-IS-13`（调度 10-02 补记） |
| `repos/` 下的仓工作树干净；没有切换 `8005-workspace\repos\`；没有其它真装置 L2 或 G3 在跑 | 成立：开工时 `repos/` 下五个克隆都在基线分支且干净（只是落后远端，由调度快进）；工作区根的 `Get-HeavyLocalStatus.ps1` 显示桌面锁空闲、重负载 0 |

## 身份

| 项 | 值 |
| --- | --- |
| 协议 | `(AGV_FULL_PRODUCT, 4)`，`releaseVersion 3.0.0`，注释 tag `protocol-v3.0.0`（对象 `e08c362e`）→ `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c`，`APPROVED_RELEASE` |
| `ProtocolReleaseIdentity` 其余字段 | manifest `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e`，schema bundle `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43`，vectors `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e`，slice index `4f1ec1b186089f0f4dca9fa749cae2ad0044f061b868bac7f1f98b312b1b68b5`（读自服务端 G2 的 `gate-result.json`） |
| 身份逐项核对 | 协议仓 `manifest/release.json`（`C:/w2g/p3`，检出在 tag 上）、服务端 `vendor/8005-agv-protocol/manifest/release.json`、车载端同名文件三者 SHA-256 都是 `d5e1a53f…`；身份常量服务端 `ProtocolCandidateIdentity.cs:59-65`（`3.0.0`／`protocol-v3.0.0`／`APPROVED_RELEASE`）、车载端 `WireToGateProtocol.cs`；两端 G2 的 `gate-result.json` 的 `protocolTag`、`protocolRepositoryCommit`、`protocolManifestSha256`、`protocolApprovalStatus` 与之相等 |
| 发布身份 PR | 服务端 PR #538 → `batch-p3/v3`，合并提交 `4477ec08`（`448ab5bf` 改 `ApprovalStatus` 与 `appsettings.json`）；车载端 onboard-hmi PR #284 → `w2g/batch-p3/v3`，合并提交 `33f26018`（`WireToGateProtocol.cs`、`run-w2g-g2.ps1` 的期望值） |
| 合回 | 服务端 PR #540 → `fp/v2-impl@5f3adc42`；车载端 onboard-hmi PR #286 → `w2g/fp-v2-impl@b9e67a53`。两个合并提交的树分别与各自批次分支相同（`rev-parse ^{tree}` 实读） |
| 服务端产品 | `fp/v2-impl@5f3adc42`。出口分支相对它 `git diff 5f3adc42 <出口提交> -- src tests tools` 为 0 行（第三节附原文） |
| 车载端 | `w2g/fp-v2-impl@b9e67a53`；证据小 PR onboard-hmi#288（head `e615ef73`）在 G3 全部跑完后合入，合并提交 `ca89ef8f`，分支顶端前移到它。`git diff --stat b9e67a53 ca89ef8f -- . :!evidence` 输出为空，210 个文件全部在 `evidence/` 下（调度与出口会话各自核过）。G3、真装置、车载端 G2 与部署包用的都是 `b9e67a53` |
| 模拟器 | `main@fb5f7c59`（不变） |
| G3 共享绑定 | 移了四次，见第三节「G3 四轮」。出口绑定是 `490faa02`：`ControlServerCommit 76c9cfe2`、`OnboardCommit b9e67a53`、`SimulatorCommit fb5f7c59`、`ProtocolCommit 3f091cb2` |
| `76c9cfe2` 与集成分支 | `76c9cfe2` 是 cs#560（PR #561）的分支头。调度要求不等合并就开第四轮，所以绑的是它；PR #561 合入后的 `fp/v2-impl@52056c48` 与它的树相同（都是 `7127ddcdd0bca4d26c62bc363704214e9884e3f0`），`git diff --stat 76c9cfe2 52056c48` 输出为空。出口分支随后 merge 了 `52056c48`（`6d56e906`），这次 merge 不改任何文件 |
| 冻结 | 2026-10-09 23:06 起冻结 `fp/v2-impl` 与 `w2g/fp-v2-impl`。期间合入的只有调度裁定的三张脚本票：control-server#541（PR #542，`3411887d`）、#555（PR #557，`1f63fe0b`）、#560（PR #561，`52056c48`），三张都只改 `scripts/` 与 `evidence/`；车载端证据小 PR onboard-hmi#288 在 G3 全部跑完后合入（`ca89ef8f`，只加 `evidence/`） |

## 一、L1

### 实测

| 端 | 运行 | 结果 |
| --- | --- | --- |
| 服务端 | 合回 PR #540 的 CI `test`，run [`37941608361`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/37941608361)。头是 `batch-p3/v3@4477ec08`，它的树与合回后的 `fp/v2-impl@5f3adc42` 相同（`rev-parse ^{tree}` 实读） | **4657 / 4657 通过**，0 失败 0 跳过。日志原文 `已通过! - 失败: 0，通过: 4657，已跳过: 0，总计: 4657` |
| 车载端 | 合回 PR onboard-hmi#286 的 CI `test`，run [`37941494850`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/actions/runs/37941494850)。头是 `w2g/batch-p3/v3@33f26018`，树与 `w2g/fp-v2-impl@b9e67a53` 相同 | **881 + 812 全过**：`SQCD.Agv.UnitTests` 881、`SQCD.Agv.WireToGateG2Tests` 812，读自 artifact 的 `logs/dotnet-test-release.log`。同一作业的全量 `ONBOARD_HMI_G2`（`-SkipProtocolG1`，不带 `-Slice`）与 UI layout audit 都是 `Status: PASS` |
| 服务端（合回之前） | 改身份 PR #538 的 CI，run `37932707562`（test）、`37932707567`（l2 默认一轮） | 都是 success |
| 服务端（cs#560） | PR #561 的 CI，run `38028129759`（test）、`38028129756`（l2） | 都是 success |

出口分支相对 `5f3adc42` 在 `src/`、`tests/`、`tools/` 下没有一处差异。改了的只有 G3 绑定与 runner、G3 场景脚本、`l2.yml` 的 `Runs`、CI 里新增的几个脚本自检、证据与文档（第三节附 `git diff --stat` 原文）。

### 批次 8 与 v3 每项新能力 ↔ 新增测试

范围：剖面 `Batch = BATCH-8` 的十条（`REQ-0204`、`0289`～`0297`）；`REQ-0359`（剖面记在 `BATCH-6`，见规格第 21.3 节）；program#115 第 2～6 项。测试类名取自各合入的 `git grep`，按需求号与票号搜，是读到的。

| 能力 | 票与合入 | 服务端测试 | 车载端测试 | L2 |
| --- | --- | --- | --- | --- |
| `REQ-0290` 用途占有（四值、占有记录） | cs#386（PR #394 `af2b02cb`）、cs#387（PR #413 `d4d3ad08`） | `Batch8PersistencePortTests`、`Batch8VehicleOccupancyTests`、`Batch8OccupancyRetirementMigrationTests`、`Batch8MigrationDisciplineTests`、`Batch7VehicleOccupancyReleaseTests`（改） | — | — |
| `REQ-0289`、`0297` 等待点登记与启动校验 | cs#388（PR #397 `7ee525b7`） | `WaitingPointImportTests`、`WaitingPointStartupCheckTests`、`WaitingPointFieldOpsTests` | — | `waiting-points-fewer-than-vehicles-refuses-start` |
| `REQ-0290`～`0292` 空闲返回资格与原子承诺 | cs#389（PR #417 `fa296d3d`） | `IdleReturnCommitmentTests`、`MultiVehicleExecutionTests.IdleReturn`、`DispatchChainSeamTests` | — | `idle-return-two-vehicles-contend-one-waiting-point` |
| `REQ-0293`～`0296` 空闲返回执行 | cs#390（PR #426 `d5efa06f`） | `IdleReturnExecutionTests`（含 `.ArrivalNotProven`、`.DashboardVerdicts`、`.CodeDescriptions`）、`CreateGateTests`、`JourneyRuntimeWorkerAdmissionTests` | hmi#217（PR #218 `b28cd9cc`）等待点腿 | `waiting-point-exclusive-reserve-occupy-release`；G3 `g3-waiting-point-idle-return` |
| `REQ-0204` 修订：固定公共站点单车位 | cs#391（PR #418 `46dcc6c5`） | `Batch8FixedStationSingleOccupancyTests`（23 个）、`DispatchVehicleOrderingTests`、`StructuralDispatchBlockTests` | — | `fixed-station-single-occupancy` |
| 看板显示用途、等待点与站点占用 | cs#392（PR #438 `b1548d0d`） | `VehiclePurposeDashboardTests` | — | — |
| 站点独占的人工释放（批次 8 跟进） | cs#419（PR #422 `507b2af0`） | `Batch8StationExclusivityManualReleaseTests`、`StationExclusivityFieldOpsTests`、`StationExclusivityReleaseEndpointsTests` | — | — |
| `REQ-0359` 人工判故障 | cs#383（PR #424）、cs#384（PR #467）、hmi#215（PR #247） | `SlotFaultDeclarationTests`、`DashboardActionTests`、`ExpectedActionOverdueTests` | `WireToGateSlotOperationExecutorTests.SlotFaultDeclaration`、`StationDeadlineExpiredG2Tests.SlotFaultDeclaration`、`.AbandonedDeclarationAnswer` | `slot-fault-declaration`；真装置 `real-onboard-slot-fault-declaration`；G3 `g3-slot-fault-declaration` |
| program#115 第 2 项：删 `supportsBatchUnlock` | cs#382、hmi#214 | 服务端测试里已不再引用 | `ConfigurationTests` | — |
| 第 3 项：强制取出交接记录 | cs#385（PR #425）、hmi#216（PR #225） | `JourneyRuntimeWorkerCargoRecoveryTests`、`RecoveryEndingReleasesBlockedJourneyTests`（含 `.DoorHold`） | `ForcedMechanicalRecoveryResultShapeTests`、`ForcedCargoHandoffViewModelTests`、`RecoveryVectorG2Tests.CargoHandoff`、`MultiDemandJourneyG2Tests.PerDemandHandoff` | G3 `g3-forced-mechanical-recovery` |
| 第 4a 项：删 `DISPLAY_ADMISSION_BLOCK_REASON` | cs#382、hmi#214 | `TaskTypeAdmissionFailClosedVectorTests`（既有） | `TaskTypeAndDirectionVectorG2Tests` | — |
| 第 4b 项：会话 `closedReason` | cs#382、cs#385 | `RecoveryStateMachineG2Tests.RecoverySurface`、`.AdministratorClose` | `RecoveryVectorG2Tests.ClosedReason`、`InboundPayloadSchemaBoundaryTests` | — |
| 第 5 项：锁存码 `ONBOARD_FATAL_FAULT_LATCHED` | hmi#214（PR #224） | — | `SafetyRulesTests`、`MultiDemandJourneyG2Tests.FatalFaultLatch`、`.FatalFaultLatchDepartureSafety`、`RecoveryVectorG2Tests.FatalFaultLatch` | — |
| 第 6 项：清单 `stopEndedReason` | cs#382 | `StopEndedReasonsTests`、`StopEndedJourneyContinuesTests` | `MultiDemandJourneyG2Tests.Paths`、`.EntryRequestExpiry`、`MultiDemandViewModelTests` | — |
| v3 身份与发件箱守卫 | cs#382、hmi#214、本票 | `ProtocolIdentityArchitectureTests`、`ProtocolOutboxIdentityStartupCheckTests`、`ProtocolMessageSurfaceArchitectureTests` | `ProtocolIdentityArchitectureTests`、`ProtocolPayloadShapeArchitectureTests` | — |
| 光幕证空而门未证明（`CP-0009`） | hmi#219（PR #248 `cda80e4d`）、cs#385 第 5 条 | `RecoveryEndingReleasesBlockedJourneyTests.DoorHold` | hmi#219 的 G2 用例（`CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN`、`CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE`） | 只有 L1 与 G2 覆盖，见第五节第 19 点 |

`REQ-0292` 在两端测试里都搜不到需求号，它的覆盖是靠票号 cs#389 找到的（`IdleReturnCommitmentTests` 等）。

### 向量绑定

两端 `ProtocolVectorTestBindingArchitectureTests` 在全量 L1 里都是绿的。等待名单只剩 `FP-IS-09` 的两条（批次 11），`FP-IS-12` 的 `CV-WAITING-POINT-IDLE-RETURN` 已有同名具名测试。这只证明绑定存在：**向量本身从来没有被机械执行过**，属于弱绑定（第五节第 8 点）。

## 二、L2

### 第 8.3 节批次 8 行与本批场景 ↔ 证据

合成证据全部来自 CI run [`37953826544`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/37953826544)（`consecutive-all`，提交 `563242d0`）。每个场景 3 份证据，彼此独立。

| 判据 | 场景（票） | 断言正事实的 L2 id | 三连 |
| --- | --- | --- | --- |
| 等待点独占一行两状态、离点证据释放 | `waiting-point-exclusive-reserve-occupy-release`（cs#390） | `L2-WPR-01`：在途预占；`L2-WPR-02`：到点后转为在点占用；`L2-WPR-04`：下达离点订单时不释放；`L2-WPR-05`：凭离点证据释放，预占、占用、释放三个时刻记在同一段经过上 | 3/3 |
| 投运车辆数大于登记等待点数时拒绝启动（负向） | `waiting-points-fewer-than-vehicles-refuses-start`（cs#388） | `L2-WPR-02`：非零退出码，`/health/live` 不通；`L2-WPR-03`：日志写出 `WAITING_POINTS_FEWER_THAN_VEHICLES`，车辆数 2、点数 1；`L2-WPR-04`：再等 15 秒，没有受理、没有建单 | 3/3 |
| 双车争一个等待点 | `idle-return-two-vehicles-contend-one-waiting-point`（cs#389） | `L2-IRC-01`～`06` | 3/3 |
| `REQ-0204` 固定公共站点单车位 | `fixed-station-single-occupancy`（cs#391） | `L2-FSO-01`～`07` | 3/3 |
| `REQ-0359` 人工判故障 | `slot-fault-declaration`（cs#383）；真装置 `real-onboard-slot-fault-declaration` | `L2-SFD-01`～`10` | 合成 3/3；真装置 3/3（run `37953803085`） |
| `REQ-0169` 等站车辆的电量看护（cs#273，批次 7 出口之后合入） | `waiting-journey-battery-watch` | `L2-WJ-01`～`05` | 3/3 |
| `REQ-0341` 地图改名挡住全部任务类型（cs#186，批次 7 出口之后合入） | `map-rename-holds-all-task-types` | `L2-MR-01`～`09` | 3/3 |
| 第 19.5 节第 8 条：急停只触发一次 | `emergency-stop-single-trigger` | `L2-ES-*` | 3/3 |
| 第 19.5 节第 8 条：命令面订单挂起 | `command-surface-order-hold` | `L2-CS-*` | 3/3 |

**cs#273、cs#186 为什么算批次 8**：这两张票都是在批次 7 出口（cs#220）之后才合入集成分支的，批次 7 的三连没有覆盖到它们，所以登记在 `batch-8` 下，由本批出口三连（`l2.yml` 里两行的注释都写着「after the batch-7 exit」）。

### CI 三连

- **改 `Runs`**：`563242d0` 把 `l2.yml` 批次 8 合成区块的 7 行，以及真装置清单里的 `real-onboard-slot-fault-declaration`，从 `Runs = 1` 改为 `Runs = 3`。作业超时不改。
- **核对脚本**：改之前先写了 `evidence/l2/20261009-batch-8-exit-runs-check/Test-Batch8ConsecutiveRuns.ps1`。它直接执行 `l2.yml` 自己的两个 `$scenarios` 字面量，断言下面五条：
  1. 批次 8 恰好是上表的 7 个场景；
  2. 每个合成场景文件都已登记（需求承载生成器 `demand-bearing-store-at-unload` 除外，它是 G3 的库生成器，不是 L2 判据）；
  3. 这 7 行都是 `Runs = 3`，且没有 `DefaultRuns`；
  4. 真装置那一行是 `Runs = 3`；
  5. 手动触发的超时不少于 180 分钟，真装置作业不少于 360 分钟。
- **先红后绿**：改前 8 条 FAIL、退出码 1（`red/`），改后 PASS（`green/`）。另外做了八种变异，每种都红在预期的那一条（`mutations/`）。
- **出口运行**：`gh workflow run l2.yml --ref b8-23/batch-8-exit -f mode=consecutive-all`，run [`37953826544`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/37953826544)，提交 `563242d0`，4 路共 4526 秒，汇总标题 `L2 (consecutive-all, 4 lanes, 4526s)`。80 个合成场景各三遍，**240 次全部 PASS，中途没有一次红**。
- **逐份核对**：脚本 `evidence/cs393/ci-evidence-identity/Test-CiEvidenceIdentity.ps1` 读全部 240 份 `assertions.json`，结果在同目录 `syn-37953826544.txt`：
  - `outcome` 全部是 `PASS`。
  - `identity.protocolReleaseIdentity` 全部是 `protocol-v3.0.0`／`3f091cb2`／`d5e1a53f…`／`APPROVED_RELEASE`／`(AGV_FULL_PRODUCT, 4)`。
  - `identity.controlServerCommit` 全部是 `563242d0`。
  - `identity.batchId` 的分布：`batch-2` 57、`batch-3` 6、`batch-4` 24、`batch-5` 15、`batch-6` 24、`batch-7` 57、`batch-8` 21（7 × 3）、`batch-9` 36。
  - 反向对照：故意给一个全零的提交，240 份全部报出偏差（`negative-control-wrong-commit.txt`）。
  - 这份结果是对**入库后的副本**跑的（`-DirectoryPrefix 20261009-ci-37953826544-`），不是对下载的原件。
- **同时运行**：这一轮与真装置那一轮在 vm01 上同时跑。真装置那一轮的一处红见下一小节。
- **入库（调度定的方案乙，分级保留）**：路径是 `evidence/l2/20261009-ci-37953826544-<场景>-NN/`，240 个目录，每个都带同名 `.log`。
  - **整套保留 27 份**：批次 8 的 7 个场景与第 19.5 节点名的两个急停场景，各 3 份。
  - **只留三份文件 213 份**：其余 71 个场景只留 `assertions.json`、`SUMMARY.md`、`timeline.jsonl`。这三样足以核对结论与身份，上面的逐份核对也只读 `assertions.json`。
  - 合计约 126 MB。完整的 artifact `l2-evidence`（压缩后 58 MB）在 GitHub 上保留到 **2027-01-07T15:43Z**（`gh api …/runs/37953826544/artifacts` 的 `expires_at`）。过期以后，那 213 份只剩入库的三份文件。
  - **为什么不照批次 7、9 的先例整套入库**：这次 240 份原始证据有 734 MB，其中单份服务端日志最大 14.5 MB；本机 C 盘 10-10 凌晨被写满过一次；审查克隆和 G3 runner 的克隆每次都要搬整个仓库。真装置的证据整套入库。

### 真装置

固定清单走 CI（`l2.yml` `rig=real`，vm01 交互式 runner）。三端提交从 run 日志 `Run real-onboard L2 scenarios` 那一步读。

| run | 三端 | 内容 | 结果 |
| --- | --- | --- | --- |
| [`37953803085`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/37953803085) | `563242d0`／`b9e67a53`／`fb5f7c59` | `mode=consecutive`、`batch_id=batch-8`，清单 23 个场景按登记次数共 31 次 | **30/31**。`real-onboard-slot-fault-declaration` 3/3；`durable-ack-lost`、`expected-action-overdue`、`charging-cycle` 各 3/3；其余 19 个各 1/1。**红一个**：`real-onboard-restart-with-open-recovery-session` |
| [`37963800400`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/37963800400) | 同上 | 只跑那一个红场景，`consecutive-all` | **3/3 PASS**。开跑前确认五个仓都没有在跑或排队的作业 |
| [`38032912362`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/38032912362) | `6d56e906`／`b9e67a53`／`fb5f7c59` | 出口期间录入路径改过的两个场景，`consecutive-all`，各 3 遍（下文「脚本变化」） | **6/6 PASS**：`real-onboard-charging-cycle` 3/3、`real-onboard-mixed-side-one-stop` 3/3。新的录入路径确实走到了：每次录入都有「提交前读回」记录（charging-cycle 每遍 2 条，mixed-side 每遍 6 条），没有一次重输，也没有 `PRODUCT_REFUSED` 或 `RIG_FIXTURE`（`timeline.jsonl` 实读）。vm01 已提交内存最高 9.34 GiB，上限 16 GiB |

- **那一红的现象**：车载端重启后第一次按「补偿清空」被拒，再按一次才完成。车载端日志 `SafetyStateChanged发送失败，正在断开会话 | TimeoutException`，服务端同一秒记了 `SocketException (10053)`。
- **归属**：形状与还开着的 **control-server#307** 一致，即服务端单条连接的处理循环停顿超过 2.5 秒，车载端等不到应答，超时后自己断开。
- **不是内存压力**：那段时间 vm01 已提交内存 6.4～7.6 GiB，上限 16 GiB。
- **结论**：vm01 空闲时三连都绿，只说明那三遍没撞上停顿，证不了 cs#307 不存在。缺陷单 `docs/defects/20261009-real-rig-restart-recovery-first-press-cs307-shape.md`；cs#307 列入剩余风险。
- **逐份核对**：`evidence/cs393/ci-evidence-identity/rig-37953803085.txt`、`rig-rerun-37963800400.txt`、`rig-38032912362.txt`。40 份 `assertions.json` 的三端提交、`batchId batch-8`、`protocol-v3.0.0`／`APPROVED_RELEASE` 都对，唯一的偏差就是那份 `outcome FAIL`。
- **证据**：`evidence/l2/20261009-ci-37953803085-*`、`evidence/l2/20261010-ci-37963800400-*`、`evidence/l2/20261010-ci-38032912362-*`，整套入库。红证据原样保留。

**脚本变化与能否沿用（三端逐端问一遍）**：真装置的前两轮跑在 `563242d0` 上。之后：

- 车载端、模拟器都没有变（G3 绑定与 CI 输入都是 `b9e67a53`、`fb5f7c59`）。
- 服务端 `git diff 563242d0 6d56e906 -- src tests tools` 为空，产品没变。
- 但 cs#541、#555、#560 改了四个公共脚本，真装置清单里有场景引用它们（`grep` 实读）：
  - `G3RecoveryCommon.ps1`（6 个 real-onboard 场景点号引入）、`L2ExpectedActionOverdue.psm1`（`real-onboard-expected-action-overdue`、`real-onboard-slot-fault-declaration`）、`L2MultiStopJourney.psm1`（另含 `real-onboard-stale-stop-after-station-timeout`）：这三个**只新增了函数**，原有函数一行没改，也没有任何 real-onboard 场景调用新函数。这些场景的结论沿用。
  - `MultiStopRigCommon.ps1` 的 `Invoke-L2RigLoad` **改了行为**（cs#560：提交前读回框内文本、看车载端拒收）。调用它的 `real-onboard-charging-cycle`、`real-onboard-mixed-side-one-stop` 在前一轮跑的是旧版录入路径，所以在出口分支 `6d56e906` 上补跑（run `38032912362`，调度裁定），6/6 PASS。`6d56e906` 到出口提交之间只加 `evidence/` 与 `docs/`，`scripts/` 与 `src/` 不变（出口 PR 正文附 `git diff --stat`）。

## 三、门禁

| 步 | 做什么 | 结果 | 证据目录 |
| --- | --- | --- | --- |
| 1 | 第一次移 G3 共享绑定 | `f63732b8`：`ControlServerCommit 5f3adc42`、`OnboardCommit b9e67a53`、`SimulatorCommit fb5f7c59`、`ProtocolCommit 3f091cb2` | — |
| 2 | 协议 G1（发布态） | PASS，在短路径普通克隆 `C:/w2g/p3` 上跑（协议仓的 G1 不能在 git worktree 里跑）；也作为 staged runner 与车载端 G2 每片的一部分重跑 | 车载端各片 `logs/protocol-g1.log` |
| 3 | `CONTROL_SERVER_G2` 15 片 | **15/15 PASS**（下文） | `evidence/g2/20261010-protocol-v3.0.0-b6be67ac/` |
| 4 | `ONBOARD_HMI_G2` 15 片（`b9e67a53`） | **15/15 PASS**，经工作区根的 `Invoke-HeavyLocal.ps1` | 车载端仓 `evidence/g2/20261009-protocol-v3.0.0-b9e67a53/`（onboard-hmi#288，合并提交 `ca89ef8f`） |
| 5 | G3 四个 runner | 四轮，第四轮全 PASS（下文） | `evidence/g3/20261010-protocol-v3.0.0-*` |
| 6 | CI `l2.yml` `consecutive-all` | 240/240 PASS | 第二节 |
| 7 | CI 真装置 | 第二节 | 第二节 |
| 8 | 迁移顺序实测 | PASS（第五节第 5 点） | `evidence/cs393/migration-order-probe/` |
| 9 | 本 PR 默认 CI | 见 PR 检查页 | — |
| 10 | 部署包（`New-WireToGateReleaseCandidate.ps1`，只打包不部署） | 在出口最终提交的干净 detached worktree 上打 | `evidence/rc/20261010-batch-8-exit-final/`（下文） |

### 服务端 G2

`evidence/g2/20261010-protocol-v3.0.0-b6be67ac/`：**15/15 PASS**（逐份读 `gate-result.json`）。

- `implementationCommit` 都是 `b6be67acfe5f8f84e40885ca3c47dc1b827060f9`；`protocolTag protocol-v3.0.0`、`protocolApprovalStatus APPROVED_RELEASE`。
- 每片 `schemaConformance` 都有结果：`linesInViolation 0`、`knownViolationsMatched 0`。`linesChecked` 从 34（`FP-IS-14`）到 7667（`FP-IS-13`），合计 25,843 行（脚本求和）。
- **不在出口最终提交上重跑（调度同意）**。从 `b6be67ac` 到出口提交，`evidence/` 以外改的只有 G3 场景脚本、自检与绑定，`git diff --stat b6be67ac 6d56e906 -- . ':!evidence'` 原文：

  ```
   scripts/l2/L2MultiStopJourney.psm1                 |  43 +++-
   scripts/l2/Test-G3JourneyUiaFixtureReads.ps1       | 264 +++++++++++++++++++++
   scripts/l2/scenarios/MultiStopRigCommon.ps1        | 123 +++++++++-
   .../l2/scenarios/g3-waiting-point-idle-return.ps1  |  21 +-
   scripts/run-staged-g3.ps1                          |   7 +-
   5 files changed, 438 insertions(+), 20 deletions(-)
  ```

  `git diff --stat b6be67ac 6d56e906 -- src tests` 输出为空，`src/` 与 `tests/` 零差异。G2 测的是 `src/` 与 `tests/`，所以结论沿用。出口提交（`6d56e906` 之后）只加 `evidence/` 与 `docs/`。
- **作废的一遍**：`evidence/g2/20261009-protocol-v3.0.0-f63732b8/` 传了相对路径的 `-Output`，15 片 `schemaConformance` 全是空值（schema 报告落进了 `tests/…/bin`），按作废处理，原样保留。用绝对路径重跑的就是上面这一份。

### 车载端 G2

车载端仓 `evidence/g2/20261009-protocol-v3.0.0-b9e67a53/`：15/15 PASS，选中的测试全过（`FP-IS-07` 410 个、`FP-IS-02` 126 个、`FP-IS-12` 6 个等，见其 `SUMMARY.md`）。每片都在发布态重跑了协议 G1。`FP-IS-04` 的 `schemaConformance` 为空是设计如此：这一片没有选中 G2 测试项目（`run-w2g-g2.ps1:488-500`），批次 5 那份同样为空。同一夜有一遍因相对路径的 `-EvidenceRoot` 全部 exit 1 而无效，留在同级 `…-relpath-inconclusive/README.md`。

### G3 四轮

四轮都在本机封锁时段跑（持有者行「cs#393 封锁」），暂存根都用短路径。每轮四个 runner 依次跑：staged、restart、demand-bearing、journey。runner 来源都是 `COMMITTED_RUNNER`。

| 轮 | 绑定提交（ControlServer） | 时间（CST） | staged | restart | demand-bearing | journey |
| --- | --- | --- | --- | --- | --- | --- |
| 一 | `f63732b8`（`5f3adc42`） | 10-10 00:19 起 | `STAGED_SLICE_FAIL`：`FP-IS-06`、`07` 红 | PASS | PASS | `JOURNEY_G3_SLICE_FAIL` 18/20 |
| 二 | `3fa909e4`（`3411887d`，含 cs#541） | 10:47 起 | PASS | PASS | PASS | `JOURNEY_G3_SLICE_FAIL` 18/20 |
| 三 | `b6be67ac`（`1f63fe0b`，含 cs#555） | 12:32 起 | PASS | PASS | PASS | `JOURNEY_G3_SLICE_FAIL` 18/20 |
| **四（出口）** | **`490faa02`（`76c9cfe2`，含 cs#560）** | **14:15 起** | **`STAGED_G3_RECOVERY_REPLAY_PASS`** | **`STAGED_G3_PROCESS_RESTART_PASS`** | **`DEMAND_BEARING_G3_VECTORS_PASS`** | **`JOURNEY_G3_PASS` 20/20** |

证据目录 `evidence/g3/20261010-protocol-v3.0.0-<runner>-<ControlServer 绑定>/`。每轮红在哪、为什么红，见第四节。

第四轮各切片（逐份读 `slices/*/gate-result.json`，全部 `status PASS`、`formalSlicePass true`）：

| runner | 切片 |
| --- | --- |
| staged | `FP-IS-00`、`06`、`07`、`14`、`15` |
| restart | `FP-IS-00`、`06`、`14`、`15` |
| demand-bearing | `FP-IS-04`、`05`、`06` |
| journey | `FP-IS-01`、`02`、`03`、`07`、`08`、`10`、`11`、`12`、`13` |

并集是 `FP-IS-00`～`08`、`10`～`15`，共 15 片。**`FP-IS-12` 的四道门禁**：G1 是协议侧发布证据（program#152）；两端 G2 PASS；G3 由 journey 的 `g3-waiting-point-idle-return` 认领，`formalSlicePass true` 由断言算出。

**为什么每一轮都把四个 runner 从头跑**：G3 绑定一动，四个 runner 共用的绑定就变了；只重跑红的那个，会让四份证据绑在不同的提交上。

**需求承载 runner 用的是生成库**：`storeProvenance.storeSource SYNTHETIC_RIG`，`fieldRunRoot` 为空（读到的）。这是 control-server#453 以来的默认做法，唯一的外场库已不存在。生成库没有真车、没有真 RIoT，也没有跨构建还原（第五节第 21、22 点）。

**第四轮里 cs#560 的夹具重试一次也没有触发**（各场景 `timeline.jsonl` 实读）：`g3-multi-stop-plan` 的 A、B，`g3-waiting-point-idle-return`、`g3-automatic-charging-cycle`，每次录入第一次读回就是正确的子批，只提交一次；没有一条「读不到元素」的记录，也没有 `RIG_FIXTURE` 或 `PRODUCT_REFUSED`。所以第三轮的两处没有复现，cs#560 的重试路径在真装置上还没被走到过（第五节第 23 点）。

**暂存根**：每个 runner 跑完、核过证据落盘之后就删掉它的暂存根（调度 10-10 同意）。

### 部署包

在出口最终提交的干净 detached worktree 上运行 `New-WireToGateReleaseCandidate.ps1`，车载端用 **`b9e67a53`**，只打包不部署。

- **为什么车载端是 `b9e67a53`，不是 onboard-hmi#288 合入后的顶端**：G3、真装置和车载端 G2 都绑在 `b9e67a53` 上；onboard-hmi#288 合入后的顶端 `ca89ef8f` 相对 `b9e67a53` 只加 `evidence/`，车载端产品不变。用合入后的顶端打包，包里的车载端提交就和全部门禁证据对不上了。
- 打包在本报告所在提交之后进行。打包产物的 `release-manifest.json`、`SHA256SUMS.txt` 与构建日志作为只含证据的追加提交放进 `evidence/rc/20261010-batch-8-exit-final/`；manifest 的 SHA-256 与打包所用的提交见那个目录，也写在出口 PR 正文与票面评论里。
- **过程记录**：出口早期在 `563242d0` 上打过一次（退出码 0），之前两次失败（工作树不干净、C 盘写满）。三次的日志在 `evidence/rc/20261009-batch-8-exit-563242d0-process-record/`，只作过程记录，不是出口的包。

## 四、红证据与缺陷单

| 红 | 原因 | 处置 | 证据 |
| --- | --- | --- | --- |
| G3 第一轮 staged：`FP-IS-06`、`07` 四条内容冲突判据 | **runner 判据过时**：判据要求「同一 messageId、内容不同就断开连接」，服务端自 cs#478（PR #479，10-05）起回 `ProtocolProblem MESSAGE_ID_CONTENT_CONFLICT` 并保持连接，这正是协议向量 `CV-RELIABLE-RETRY-DIFFERENT-CONTENT` 要求的。cs#478 之后没人再跑过 staged G3 | control-server#541 改判据，第二轮起 PASS | `docs/defects/20261010-staged-g3-content-conflict-expects-disconnect.md`；`evidence/g3/20261010-protocol-v3.0.0-staged-5f3adc42/` |
| G3 第一轮 journey：`g3-slot-fault-declaration`、`g3-forced-mechanical-recovery` | **两个场景出口前从没真跑过**：一个等卸货超时告警时认到了装货阶段的旧告警；一个在交接框没填时就等「已隔离并完成机械取出」按钮可用（按钮要填完框才可用） | control-server#541 | `docs/defects/20261010-journey-g3-two-scenarios-first-run-stale-scripts.md`；`…-journey-5f3adc42/` |
| G3 第二轮 journey：同上两个场景 | 修好第一层后露出的下一层：一个在 StrictMode 下读了不存在的属性 `PayloadJson`；一个把 `Get-G3Scalar` 读回的 `""` 当成「不为空」 | control-server#555 | 同一份缺陷单第二轮一节；`…-journey-3411887d/` |
| G3 第三轮 journey：`g3-multi-stop-plan`、`g3-waiting-point-idle-return`（前两轮都 PASS） | **界面读写夹具的一次差错被当成产品结果**：多停靠那一处车载端本地拒收 `SUBLOT_NOT_IN_WORKLIST`，唯一剩下的解释是提交那一刻框内文本不对，但证据里没记框内文本，定不了；空闲返回那一处是持续断言读到一次「读不到」就判红，那段时间产品侧没有任何东西会改这个值 | control-server#560：夹具读写失败在夹具层重试并记录，产品结果不重试；第四轮 PASS，但重试没有被触发 | `docs/defects/20261010-journey-g3-uia-fixture-reads-not-told-from-product.md`；`…-journey-1f63fe0b/` |
| 真装置 `real-onboard-restart-with-open-recovery-session` 1/1 红 | cs#307 形状：服务端单条连接处理循环停顿超过 2.5 秒，车载端超时自断 | 空闲 vm01 上 3/3 PASS；cs#307 列入剩余风险 | `docs/defects/20261009-real-rig-restart-recovery-first-press-cs307-shape.md`；`evidence/l2/20261009-ci-37953803085-real-onboard-restart-with-open-recovery-session-01/` |
| 服务端 G2 第一遍（`f63732b8`） | 不是红，是**无效**：相对路径的 `-Output`，`schemaConformance` 全空 | 绝对路径重跑 15/15 | `evidence/g2/20261009-protocol-v3.0.0-f63732b8/`（保留） |
| 车载端 G2 第一遍 | 不是红，是**无效**：相对路径的 `-EvidenceRoot`，15 片全部 exit 1 | 绝对路径重跑 15/15 | 车载端仓 `…-relpath-inconclusive/README.md` |

**这三轮红说明的事**：G3 场景脚本平时没人跑，只有出口移绑定时才跑，所以合入时没跟上的改动、从没走到过的分支，都要到出口才暴露。本批出口为此多跑了三轮 G3、合了三张冻结例外票（第七节转后续）。

## 证据精简

调度 10-10 定的方案甲：

- **第四轮 journey（`76c9cfe2`）整套入库**，约 70 MB，照批次 7、9 的先例。
- **红的三轮 journey**（`5f3adc42`、`3411887d`、`1f63fe0b`）：
  - 红场景整套入库，包括服务端与车载端日志、库快照，这是缺陷单的出处。
  - 其余 18 个 PASS 场景只留 `assertions.json`、`SUMMARY.md`、`timeline.jsonl`；删掉的是每个场景的 `logs/`（服务端、车载端、假 RIoT、假 MES 的标准输出与错误）、`snapshots/`（库与发件箱快照）与其余辅助文件。
  - 顶层的 `run-result.json`、`configuration.json`、`slices/`、`logs/` 全部保留。
  - 每轮从 1477 个文件减到 240～242 个，体积从 69～79 MB 减到 13～22 MB。
- **为什么删**：每轮 journey 的大头是各场景的服务端日志（单个最大 9 MB），四轮全部入库是 293 MB。红轮次里 PASS 场景的完整日志，第四轮又在同一套脚本上覆盖了一遍，没有额外的诊断价值。仓库当前 pack 约 73 MiB，全部入库会让它接近翻倍。
- **删了哪些、原件在哪**：
  - 清单 `evidence/g3/20261010-batch-8-exit-journey-trim/removed-files.tsv`：3707 个文件，每个都记了轮次、场景、相对路径、字节数、SHA-256。精简脚本 `Invoke-JourneyEvidenceTrim.ps1` 在同一目录，删每个文件之前都核过备份的 SHA-256 相同。
  - 完整原件在本机 `C:\w2g\cs393\g3-journey-originals\`，**保留到出口 PR 合入为止**。G3 是本机跑的，没有 CI artifact；合入之后，被删的文件只剩清单里的哈希。
- staged、restart、demand-bearing 四轮与服务端 G2 体积小（合计约 17 MB），整套入库。
- CI L2 证据按方案乙分级入库，见第二节；它的完整 artifact 保留到 2027-01-07T15:43Z。

**引用核对**：提交前扫了缺陷单与本报告里引用的每一个仓库内路径，确认都真实存在，结果在 `evidence/cs393/reference-check/`。

## 五、必须如实写明的各点

1. **v3 发布了什么，作废了什么。** `protocol-v3.0.0` 包含 program#115 第 1、2、3、4a、4b、5、6 项。
   - 第 7 项 `expiresOnRevisionChange` 只改措辞。理由：这个字段是 `const: true`，两端没有任何分支按它的值走；录入撤不撤由第 23.5 节的规则决定，服务端另有 `WORKLIST_REVISION_STALE` 兜底；准入线四条都碰不到它。
   - 第 8 项 `items[]` 加 `operationSessionId` 不做，因为第 22.2 节第 1 条已定每次到站一个会话。
   - 绑定 `protocol-v2.0.0` 的全部门禁与 L2 证据，自 v3 发布（2026-10-09）起不再是现行证据（第 6.5 节）。本报告的证据全部绑定 v3。
2. **部署约束：两端必须同时换。** 两端没有版本协商，按精确身份匹配（ADR-cross-0031）；v3 服务端配 v2 车载端，握手当场就会被拒。合回之后集成分支打出的服务端与车载端必须一起部署。
3. **部署约束：车载端从 v2 升 v3 之前，先清空发件箱，或者做行迁移（program#168「切换须知」）。** 原因有三：
   - v2 时代没送出的行带着 v2 发布身份，v3 车载端会在握手补发之前，在本地以 `PROTOCOL_RELEASE_IDENTITY_MISMATCH` 把它们拒掉（车载端 `WireToGateProtocol.cs:227`、`:287`）。服务端看不到这些行。
   - v3 的 `ForcedMechanicalRecoveryResult` 新增必填字段 `demandId`、`cargoHandoff`。
   - `LoadCancellationResult` 与 `LoadCompensationResult` 的 `overallOutcome` 多了 `ALL_EMPTY_DOOR_UNPROVEN`。

   不处理的话，对 MES 交代的结果可能丢失。服务端同样如此：服务端 `ProtocolOutbox` 里升级前写入的 v2 身份未确认行，v3 补发时会被车载端拒收，会话会反复断开。cs#382 加了启动守卫，发件箱里有异身份的未确认行就拒绝启动（`ProtocolOutboxIdentityStartupCheckTests`）。所以升级（以及回滚）之前，每辆车都要没有在途旅程，服务端与车载端的发件箱都要没有未确认行，否则换新库目录（本票评论 5895208507、5896646945）。**这条约束同样适用于 W2，以及之后 agv02、agv03 升 v3。**本票只写这一条约束，不改代码。
4. **回滚约束，逐端写。**
   - **车载端**：v3 车载端写进日志的旅程快照与恢复上下文带新字段，回滚到 v2 车载端时，启动重放会因 `JsonUnmappedMemberHandling.Disallow` 抛错。回滚前先清日志里的旅程快照，或者确认车辆没有在途旅程（hmi#214、hmi#216 的部署说明）。
   - **服务端**：cs#387 删了 `VehicleDispatchLeases` 与 `OrderIntents.VehicleOccupancy*`。**回滚到批次 8 之前的服务端要连库一起还原，不能只换程序。**旧版二进制里没有这个迁移，执行不了它的 `Down`。要回去，只能先用新二进制执行 `Down`，或者还原升级前的库备份（推的，依据见 PR #413 正文「Down 的已知限制」）。
     - `Down` 本身也有限制：它按每趟搬运旅程最新的一条记录重建一行租约，订单占用的两列回来是空值。
     - 还有两条会让 `Down` 失败：cs#386 的 `Down` 遇到 `DemandId` 为 NULL 的行会整体失败，而空闲返回旅程正会写出这种行（PR #394 审查评论第 7 条）；cs#399 的 `Down` 在有 `CHARGER` 行时拒绝。
   - **cs#387 删表前的数据核对**：迁移在删表之前先核数据，只有「旧占用还开着、却找不到对应的新载体」时才整体拒绝；拒绝时 `RAISE(ABORT)`，报错列出每一行，什么都不删。这一规则相对票面「有未释放行就拒绝」有放宽，已报调度并获批（cs#387 关闭评论 5888713868）。
     - 它在 L1 迁移测试 `Batch8OccupancyRetirementMigrationTests`（12 条以上）和审查造的三种升级库上跑过，都通过了。
     - **没有在任何真实旧库上跑过**：唯一的外场库 `fullloop-20260829T131549Z` 已经不存在；factory01 上的 v2 并行实例是从空库建起来的（推的，依据是调度看板 10-07 记的「factory01 上没有 v2 目录」）。
5. **本批 migration。**
   - **批次 8 两次**：`20260929044052_Batch8VehiclePurposePersistence`（cs#386，建表）、`20260929070322_Batch8RetireOldVehicleOccupancy`（cs#387，删表）。
   - **v3 两次**：`20260930012829_Batch8SlotFaultDeclarations`（cs#383，建 `SlotFaultDeclarations` 表）、`20260930041750_Batch8RecoverySurface`（cs#385，加 `ClosedReason` 与三个 `Handoff*` 列，建 `SlotDoorHolds` 表）。两个都在 `batch-p3/v3` 上建。
   - **v3 两个迁移没有在合回前重建（调度定）。** 它们的 id 早于集成分支上已有的 `20261008052643_UnreleasableNotReconciledBlocksBeforeUpgrade`（cs#505，只改数据）。调度同意不重建，前提是实测成立，实测结果如下（`evidence/cs393/migration-order-probe/run-01/probe-result.json`，`verdict PASS`）：
     - 用 `fp/v2-impl@883348f8` 构建的 `ControlServer.Host --migrate-only` 建库，此时 38 条迁移，最后一条是 1008。
     - 再用 v3 构建（`448ab5bf`）对同一个库迁移，两条 0930 被补上，执行顺序是 1008 → 0930 → 0930。
     - 与 v3 新建的库逐项相同：迁移历史 40 条，106 张表、186 个索引的 schema 完全一致（`sqlite_master`、`table_xinfo`、`index_list/index_xinfo`、`foreign_key_list`）。
     - 反向对照：升级前的库与参照库比较会报差异，差的正好是这两个迁移的内容，说明这个比较会报红（`negative-control.txt`）。
     - 库文件本身按仓库 `.gitignore` 不入库，schema 导出（`*.schema.json`）入库。
     - **边界**：探针用的是空库。有数据的库结果相同，这一点是从迁移内容推出来的：两个 0930 迁移只有 CREATE TABLE 和可空列的 ADD COLUMN，没有实测。factory01 上那个 v2 并行库将来升 v3，就是走这条路径。
6. **两处交给做票人定的设计。**
   - **cs#386：空闲返回挂在「无需求旅程」上，不另立记录（选甲，PR #394「设计取舍一」）。** 理由：故障监看、急停确认、自建单重建、建单与对账、`JourneyRuntimes.Version` 并发令牌全部挂在旅程上；另立记录等于在上真车前新写一条没人跑过的动车路径。PR 正文逐条写了准入线四条；审查没有逐条复述四条的结论，反而查出一条违反准入线 3 的必修（迁移回填会让在途车永远 `VehicleHeld`）。调度把那段回填挪到了 cs#387，增量复核无必修（cs#386 关闭评论 5885009779）。
   - **cs#389：强制充电阈值的过渡办法（调度 Coordinator 9 于 09-29 定）。** 线只经一个接缝 `IMandatoryChargeLine` 读取，过渡实现读 `JourneyRuntime:MinimumBatteryPercent`（默认 30），低于就拒。**低电量的车不会被派去干活**：搬运今天就用同一个值、同一个比较挡低电量车，所以低电量车既不接搬运，也不去等待点（由 `ALowBatteryVehicleIsSentNowhereNeitherToTransportNorToAWaitingPoint` 钉住）。这个接缝之后已被批次9-05（cs#403）换成按充电策略读线的 `PolicyMandatoryChargeLine`（`IdleReturnModule.cs:25`）。
7. **`REQ-0208` 的电量半边**：批次 8 的范围里仍未实施（第 19.5 节）；批次 9 的 cs#403 之后实施了「预计任务后余量」，见批次 9 出口报告第五节第 6 点。
8. **`FP-IS-12` 是新切片**，没有可以沿用的旧通过结论；它的向量从来没有被机械执行过（弱绑定），「同名具名测试」只证明绑定存在。
9. **等待点。**
   - **只读核对（cs#388 第一步，经用户确认后做）**：09-29 15:27 执行 `GET /api/imap/v1/mapInfo/stations/26` 一次，HTTP 200。站 214、215、216 存在，站名逐字为「等待点1/2/3」；全图 211 个站里没有两个共享坐标。两处现场观察：相邻等待点间距只有 1.22～1.38 m，车身会不会互相干涉要现场判断；站数 211 与 09-18 记的 209 对不上（cs#388 评论 5885698092）。
   - **登记**：仓里有登记文件 `docs/field/waiting-points-map26.csv`（214/215/216，全部启用，白名单为空）。票面写明本票不去任何现场机器上导入，也没有找到后来在 factory01 v2 实例上导入的记录。该实例目前只跑 agv02 一台车，多车启动校验不适用（推的）。
   - **负向证据**：第二节 `waiting-points-fewer-than-vehicles-refuses-start` 的 `L2-WPR-02`～`04`。
   - 第 8.5 节：等待点独占的机制由 L2 与 G3 证明，**物理验收在 W2 与 W3-S2**。本报告不写「等待点独占已在现场验证」。
10. **`REQ-0359` 过渡办法什么时候退役。** `CP-0005` 第五节写的是：v3 发布，且判定表单、接口与车载端处理「随 RC 上线之后」，规程 5.4 才以看板判定为主；过渡办法只留给「没到超时门槛、但现场已经明显损坏又不能等」的情形（program 仓 `CP-0005.md:324-325`）。**出口不等于上线**：本票出口之后，v3 还没有上车（前置核对）。program 仓规程 `docs/site-procedures/station-door-not-closed-escalation.md` 的 5.4.1（看板判定）与 5.4.2（过渡办法）已经同时写好，**但没有任何文档指定由谁、在什么时候把规程改写为以看板判定为主**（查过 `CP-0005`、规格第 21.3 节、cs#383/#384 票面）。在那之前，过渡办法（到车上重启车载端）仍是现场的做法。这一项转调度定。
11. **人工判故障只覆盖 `LOAD`／`UNLOAD`**（program 仓 `CP-0005.md:161-162`，说明 3 末段）。装货纠错重放、取消清空、补偿清空、故障货物受控取货卡住的情形不适用，它们走各自的结果消息，其中取消清空超时本来就会转为 `UNKNOWN`。维护开门模式与强制机械取出也不适用。
12. **锁存码。** 出发安全的原因码由借用的 `DEPARTURE_UNSAFE` 换成 `ONBOARD_FATAL_FAULT_LATCHED`（车载端 `WireToGateSafetyEvaluator.cs:33`）。**逐仓结果里的锁存码也一并换了**：由 `VEHICLE_NOT_READY` 换成 `ONBOARD_FATAL_FAULT_LATCHED`（`WireToGateSlotOperationExecutor.cs:2167`）。依据是 v3 注册表 `errors/error-codes.json` 里这个码的 `allowedMessageTypes` 共 8 条：出发安全 3 条，加上带 `slotResults` 的 5 条结果消息。hmi#214 按票面表第一行「含五条结果消息 → 一并改」处理（hmi#214 评论 5894860333）。
13. **第 19.5 节切生产门槛第 8 条在 v3 上的证据**：`emergency-stop-single-trigger`、`command-surface-order-hold` 在 run `37953826544` 里各 3/3 PASS，证据目录 `evidence/l2/20261009-ci-37953826544-emergency-stop-single-trigger-01..03`、`…-command-surface-order-hold-01..03`。规格措辞由 program PR #167 改为现行发布身份（第 24 节补记）。
14. **真装置免跑的判据要对三端各问一遍。** 本出口没有免跑。前两轮真装置的 34 次都在 `563242d0`／`b9e67a53`／`fb5f7c59` 上（从 run 日志读）。之后车载端、模拟器不变，服务端产品不变；出口期间改过行为的脚本只有 `MultiStopRigCommon.ps1` 的录入路径，用到它的两个真装置场景在出口提交上补跑（第二节「脚本变化与能否沿用」）。
15. **cs#251**：已关闭（2026-09-29），经 PR #398 合入 `fp/v2-impl`（`0c0edfa5`）。关于历史行：v2 今天没有持有真实数据的库，所以没有读生产库；如果切换改为就地升级 MVP 库，要重新回答这个问题（cs#251 评论 5887924671）。
16. **W2 的剩余前置。**
    - 等待点：已在仓里登记（第 9 点），尚未导入任何现场实例。
    - 派工待送取货站点：人工票 **cs#166 在 GitHub 上仍是 OPEN**。但按调度看板（二手记录）实际工作已经做了：10-09 用户在 RIoT 建了站 217「派工待送取货」；15:10 现场用途核对通过（`MAP-26-STAGING_TO_WIRE-SITE-20261009`）；正式激活 26 号图的绑定 217→`STAGING_TO_WIRE`、210→关卡；15:27 agv02 跑完 v2 首趟 `STAGING_TO_WIRE`（IO 用模拟器）。关卡 210 已被用户挪到新坐标，原来的核对 `B6-IDENTITY-20260919` 对应的是旧位置。票本身还没有回帖、没有关闭。
    - W2 在 v3 身份上演练（第 22.3 节）。
17. **`REQ-0204` 修订**（cs#391）：固定公共站点单车位与等待点用同一个独占原语，零 migration，复用 cs#386 建好的 `StationExclusivityStore`，种类为 `FIXED_TASK_STATION`。L1：`Batch8FixedStationSingleOccupancyTests`（23 个），另改了 `DispatchVehicleOrderingTests`、`StructuralDispatchBlockTests`；L2：`fixed-station-single-occupancy` 的 `L2-FSO-01`～`07` 三连。人工释放出口拆到 cs#419。
18. **批次 8 与 v3 热点文件的合回。** 第 2 步的最后一次同步（`18b72d5b`，把 `fp/v2-impl@883348f8` merge 进 `batch-p3/v3`）**没有冲突**。之前每合入一张票都同步过一次，冲突在那些同步 PR 里已经处理掉（#423、#457、#469、#482、#534；车载端 #246、#253、#257、#265、#277、#283）。提交前全仓 grep 冲突标记无命中。两个合回 PR 的基都是头的祖先，合回在内容上是快进（合并提交的树与批次分支相同）。合回后的全量 L1 见第一节。**规格措辞**：program PR #167（分支 `docs/cs393-batch8-exit-spec-wording`）写了第 24 节补记，并在 7.2 节、8.4.2 第 5 条、19.5 第 8 条、20.2 表加了指针；截至本报告**尚未合入**。
19. **hmi#219 新增的 `ALL_EMPTY_DOOR_UNPROVEN` 结果与 `HOLD_RELEASE` 作答**（本票评论）：本票不补 G3 或 L2 场景，**这两条只由 L1 与 G2 覆盖**。G2 是车载端 `FP-IS-07` 的 `CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN`、`CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE`；服务端是 `RecoveryEndingReleasesBlockedJourneyTests.DoorHold`。服务端脚本里两个词都搜不到。
20. **车载端回答 `NON_BUSINESS_MOVE` 出发前检查时不看扣车**（本票评论，来自 hmi#219 审查）：服务端今天不发这种检查，而且扣着的车不会被派新任务（`VehicleNewPurposeReadiness.cs:64-66`），所以眼下只是少了一道纵深防线。等服务端开始对充电或空闲返回发 `NON_BUSINESS_MOVE` 时，两端要一起把扣车纳入这项检查。
21. **需求承载 G3 的库生成器稳定性**（本票评论）：移绑定之前，先在 v3（`448ab5bf`）上把 `demand-bearing-store-at-unload` 连跑三遍，3/3 PASS（`evidence/cs393/generator-stability/demand-bearing-store-at-unload-448ab5bf-01..03/`）。再用 `-SelfCheckControlServerCommit 448ab5bf` 跑需求承载 runner，结果是 14/16（`…/demand-bearing-runner-selfcheck-448ab5bf/`）：红的两条（`protocolAndBuildIdentityBoundToTheSharedBinding`、`restartedHostServesTheSameStore`）都是在比「服务端或库的 `protocolCommit` = 绑定里的 `ProtocolCommit`」，当时绑定还是 v2 的 `86575456`，自检只能覆盖服务端提交、覆盖不了协议提交，所以这两条在移绑定之前必然红。移绑定之后的四轮正式运行全部 `DEMAND_BEARING_G3_VECTORS_PASS`。
22. **cs#387 增量复核留下的 runner 待办没有做，调度裁定本票不改。** 待办内容（Coordinator 9 在本票的评论，来自 cs#387 / PR #413 增量复核）：从 baseline 独立算出迁移回填应有的行数，并把「表不存在按 0」收窄到记录表。
    - **为什么不改（调度裁定）**：本次需求承载 G3 跑的是合成库路径，`run-result.json` 的 `storeProvenance.storeSource` 为 `SYNTHETIC_RIG`、`fieldRunRoot` 为空（读到的）。生成的库就是本构建自己的 schema，不经过跨版本迁移，库里没有需要回填的旧占用，改了在本次出口里也证明不了任何东西。
    - **回填行数有 L1 护栏**：`Batch8OccupancyRetirementMigrationTests.WithJourneysInFlightItRunsKeepsEveryOtherRowAndLeavesOneRecordPerJourneyOpenOnlyWhileItsClaimIs`（`:39-88`）。种子是 2 条占有、4 条租约，其中 2 条已释放且没有记录；迁移后逐行断言恰好 4 条记录，等于「占有行数 + 已释放且无记录的租约数」。边界是夹具造的小数据，没有在真实旧库上跑过。
    - **剩余风险**：外场库路径（`-FieldRunRoot`）上，runner 的回填断言仍然偏弱。由调度另开票承接。
23. **cs#560 的夹具重试在真装置上还没被走到过。** 它把「界面读写失败」（读不到元素、框内文本没进去）与「产品给出的结果」（车载端拒收、读到的值不对）分开：前者在夹具层重读或重输，每次都记进时间线；后者不重试、不放宽。第四轮 G3 与真装置补跑（run `38032912362`）里，新路径的读回每次都走到了，但重试一次都没有触发（第二、三节），所以本报告**不写「第三轮的两处已修好」**。能说的是：第三轮的两处没有复现；新路径的正确性目前由离线自检 `Test-G3JourneyUiaFixtureReads.ps1`（12 条，修复前红 11 条）与 7 个变异证明（`evidence/l2/cs560-journey-uia-fixture-reads/`）；多停靠那一处的真因仍然没定，下次再出现时时间线会记下框内文本、拒收码与时刻。
24. **强制机械恢复之后的就绪原因**（第二轮 G3 看到的事实，不是缺陷）：G3-07-44 的实际值里，车辆会话是 `RecoveryRequired (FORCED_RECOVERY_GENERATION_MISMATCH)`，线上码 `FORCED_RECOVERY_GENERATION_STALE`，而不是「等硬件恢复记录」（`FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED`）。原因是「车载端报过的代数」只在握手时的恢复报告里更新（`WireToGateStore.cs:306`），而结果送达后车载端没有重新握手。这是不是设计预期没有定，调度裁定不在本票钉原因码，照原口径只判 `Readiness`，另开 control-server#556 承接。

## 等用户拍板的人工项

- `REQ-0359` 规程什么时候改写为以看板判定为主（第五节第 10 点）。
- cs#166（派工待送取货站点）票面回帖与关闭（第五节第 16 点）。
- 等待点间距 1.22～1.38 m 是否会让车身互相干涉，要现场判断（第五节第 9 点）。

## 六、剩余风险

### 上真车与切换前必须处理

- **两端同时换、发件箱先清空**（第五节第 2、3 点）。这条同样适用于 W2，以及 agv02、agv03 升 v3（program#168「切换须知」）。
- **回滚要连库**（第五节第 4 点）：回滚到批次 8 之前的服务端不能只换程序；`Down` 在空闲返回行或 `CHARGER` 行面前会失败。
- **v3 还没上过车**：上真车验证用的是 v2.0.0 包；v3 第一次上车要逐次授权（前置核对）。

### 行为与现场

- **control-server#307**：服务端单条连接处理循环停顿超过 2.5 秒时，车载端会超时自断；本批真装置撞上一次（第二节）。三连全绿证不了它不存在。
- **cs#560 的重试路径没在真装置上走过**（第五节第 23 点）。多停靠那一处的真因没定。
- **强制机械恢复之后的就绪原因**（第五节第 24 点，control-server#556）。
- **cs#387 的迁移没有在真实旧库上跑过**（第五节第 4 点）；runner 外场库路径上的回填断言偏弱（第五节第 22 点）。
- **v3 两个迁移的乱序只在空库上实测**（第五节第 5 点）。
- **等待点只在仓里登记，没有导入任何现场实例**；物理验收在 W2 与 W3-S2（第五节第 9 点）。
- **`NON_BUSINESS_MOVE` 出发前检查不看扣车**（第五节第 20 点）。

### 测试与证据的边界

- 向量是弱绑定，从未被机械执行（第五节第 8 点）。
- `ALL_EMPTY_DOOR_UNPROVEN` 与 `HOLD_RELEASE` 只有 L1 与 G2（第五节第 19 点）。
- 需求承载 G3 用的是生成库：没有真车、没有真 RIoT、没有跨构建还原。
- 红轮次 PASS 场景的完整日志只在本机保留到出口 PR 合入（「证据精简」）；CI L2 的 213 份只留三份文件，artifact 2027-01-07 过期（第二节）。
- L2 与 G3 都跑在模拟器与假 RIoT 上，PASS 不证明真实硬件（模块、接线、锁、光幕）。

### 运维说明

- 并行期 v2 实例与 MVP 的隔离规则不变（工作区 `CLAUDE.md`）；本票没有碰 factory01、agv02、生产 MesIngest，也没有碰 v2.0.0 包。
- 部署包只打包不部署（第三节）。

## 七、转后续

- **G3 场景脚本平时没人跑，三次出口都返工**：本批为此多跑了三轮 G3、合了三张冻结例外票（cs#541、#555、#560）。建议出口前先在集成分支顶端预跑一轮 G3 与真装置，把脚本问题挪到出口之前。由调度定是否立票。
- control-server#556：强制机械恢复之后的就绪原因。
- control-server#307：连接处理循环停顿。
- cs#387 runner 待办：外场库路径上的回填行数断言（调度另开票）。
- program PR #167：规格第 24 节补记，待合入。
- `REQ-0359` 规程改写的归属（第五节第 10 点）。
