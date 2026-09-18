# 批次 5 出口报告（v2 线）：协议 `protocol-v2.0.0` 与站点作业补齐

control-server#90（批次5-36）。本报告逐项对照规格 `8005-agv-program/docs/specs/full-product-scope-and-sequence-v2.md` 第 8.3 节批次 5 行（第 19 节补记优先）。
需求条目按基线 **`v1.4.0`**（tag `requirements-baseline-v1.4.0` → `3154c41f`，359 条）引用。

**按用户 2026-09-17 决定，门禁（`CONTROL_SERVER_G2`、`ONBOARD_HMI_G2`、`G3`）与本机 L2（含真装置）不再逐次询问。**本票票面写于 2026-09-15 的
「每一道门禁、每一次真装置跑批、CI 三连，开跑前都先在对话里问用户」由这一决定取代；验收第一条里「问过用户的记录」以此代替。
真装置时段按工作区规则向调度会话申请并获放行（2026-09-18，整段约 3 小时），跑完归还。

## 结论

**批次 5 出口达成。**规格第 8.3 节批次 5 行的每一项都在 `protocol-v2.0.0` 发布身份上成立：G1、两端 G2 十片、四个 G3 runner（十片的 G3 面全 PASS）、
CI 上 28 个合成场景各连续三次、真装置七条、两端全量 L1。

过程中三处红，都先读证据再定性、修好后从头重跑受影响的门禁，没有拼接：第一轮（2026-09-18）红在缺陷单 A（车载端回归）与 B（staged 判据落后）；
修复后第二轮（2026-09-19）只剩缺陷单 C（journey 场景缺一步）；C 修复后第三轮四个 G3 runner 全绿。见「重跑」一节与第四节。

| 出口（规格 8.2／8.3 批次 5 行、19.5 节、本票验收） | 状态 | 依据 |
| --- | --- | --- |
| `protocol-v2.0.0` 发布：G1 在发布态通过、attestation 一名批准 | **成立** | `evidence/g1/20260916-protocol-v2.0.0-release-8657545/`（program#97）；本轮两端 G2 每片都在发布态重跑了协议 G1 |
| L1：两端测试套件全绿；新能力逐项有新增覆盖 | **成立** | 服务端 1255/1255（`e0f26b37`，产品代码与 `d3003c2f` 相同）；车载端现行 538/538（`29fbf65e`，`evidence/l1/20260919-onboard-hmi-29fbf65e/`），第一轮 536/536（`9748c418`）保留；逐项对照表见第一节 |
| `CONTROL_SERVER_G2` × 10 片 PASS，绑定发布身份，`schemaConformance` 零未登记违约 | **成立** | `evidence/g2/20260918-protocol-v2.0.0-06b65688/` |
| `ONBOARD_HMI_G2` × 10 片 PASS，出站校验零违约 | **成立** | 现行：车载端仓 `evidence/g2/20260919-protocol-v2.0.0-29fbf65e/`（缺陷 A 修复后重跑）；第一轮 `…-9748c418/` 保留。九片零违约，`FP-IS-04` 无出站报文可验（见第三节） |
| G3：十片的 G3 面 PASS；`FP-IS-02` 含 control-server#87 两条新场景 | **成立** | 第三轮（绑定 cs `d3003c2f`／onboard `29fbf65e`）：staged、restart、需求承载 PASS，journey `JOURNEY_G3_PASS`、12/12 场景（含 #87 两条）；`evidence/g3/20260919-protocol-v2.0.0-*-d3003c2f/` |
| CI 上全部合成 L2 在 `v2.0.0` 上连续三次通过，证据独立，`identity` 为发布身份、`batchId` 与 `l2.yml` 一致 | **成立** | CI run `35361077376`，`mode=consecutive-all`，28 场景 × 3 = 84 次全 PASS；`evidence/l2/20260918-ci-35361077376-*` |
| ADR-cross-0058、到站无货出口、录入后拒收各有 L2 证据（合成与真装置分列） | **成立** | 见第二节表格 |
| control-server#86 三条、#88 四条真装置场景在发布身份上各一次 PASS | **成立** | 重跑 7/7 PASS（`evidence/l2/20260919-real-onboard-*-002/`）；第一轮 `compensate-then-reconnect` 的红（缺陷单 A）保留在 `-001` |
| 两端 `VectorsAwaitingTheirSlice` 没有批次 5 的暂钉 | **成立** | 第一节末 |
| 急停场景按第 19.6 节处理 | **成立** | control-server#63 已合入（PR #92，`ee74ac82`），急停三场景随三连用新判据跑，全绿 |
| 红证据全部保留，失败原因在 `docs/defects/` 有记录 | **成立** | 第四节 |
| 十一点如实写明、无第 8.8 节禁用表述 | **成立** | 第五节 |
| 未切换 `C:\Users\szy\Desktop\8005-workspace\repos\` 下任何克隆 | **成立** | 全部操作在 `8005-workspace-v2`；v2 的 `repos/` 克隆跑完均切回基线分支、快进、干净 |

## 重跑（2026-09-19，修复 A、B 之后）

前置：control-server#151（PR #153，`c3c81eaf`）、control-server#154（PR #155，`c12f0498`）、onboard-hmi#112（PR #114，`29fbf65e`）都已合入。出口分支 merge 了两条主线（`bbe6ae32`；车载端证据分支 `7c6a0ae`），
G3 共享绑定移到 `ControlServerCommit c12f0498`、`OnboardCommit 29fbf65e`（提交 `69894550`），模拟器与协议不变。真装置时段由调度会话放行，重负载都经 `Invoke-HeavyLocal.ps1 -Ticket cs#90`。

| 重跑 | 结果 | 证据 |
| --- | --- | --- |
| `ONBOARD_HMI_G2` × 10（`29fbf65e`） | 全 PASS，九片出站 2595 行零违约 | 车载端仓 `evidence/g2/20260919-protocol-v2.0.0-29fbf65e/` |
| 车载端全量 L1（`29fbf65e`） | 538/538（`SQCD.Agv.UnitTests` 354、`SQCD.Agv.WireToGateG2Tests` 184，含 PR #114 的两个新测试） | `evidence/l1/20260919-onboard-hmi-29fbf65e/` |
| `run-staged-g3.ps1` | `STAGED_G3_RECOVERY_REPLAY_PASS`（缺陷 B 已修） | `evidence/g3/20260919-protocol-v2.0.0-staged-c12f0498/` |
| `run-staged-g3-restart.ps1` | `STAGED_G3_PROCESS_RESTART_PASS` | `evidence/g3/20260919-protocol-v2.0.0-restart-c12f0498/` |
| `run-demand-bearing-g3-vectors.ps1` | `DEMAND_BEARING_G3_VECTORS_PASS` | `evidence/g3/20260919-protocol-v2.0.0-demand-bearing-c12f0498/` |
| `run-journey-g3.ps1` | **`JOURNEY_G3_SLICE_FAIL`**：11/12 场景退出码 0（`g3-exception-resume`／`-compensate`／`g3-fault-cargo-handoff` 已转绿，缺陷 A 已修）；`g3-forced-mechanical-recovery` 中止（缺陷单 C） | `evidence/g3/20260919-protocol-v2.0.0-journey-c12f0498/` |
| 真装置七条 | 7/7 PASS（`compensate-then-reconnect` 已转绿） | `evidence/l2/20260919-real-onboard-*-002/` |

**第三轮（缺陷 C 修复后）：**control-server#156（PR #157，`d3003c2f`）合入，出口分支 merge 主线（`ed9d1b04`），绑定 `ControlServerCommit` 移到 `d3003c2f`（提交 `90654ade`），
其余三项不变。四个 G3 runner 从头重跑：staged `STAGED_G3_RECOVERY_REPLAY_PASS`、restart `STAGED_G3_PROCESS_RESTART_PASS`、需求承载 `DEMAND_BEARING_G3_VECTORS_PASS`、
journey **`JOURNEY_G3_PASS`**（12/12 场景，`FP-IS-01`／`02`／`03`／`07` PASS，`G3-07-41`～`45` 全 PASS），证据 `evidence/g3/20260919-protocol-v2.0.0-*-d3003c2f/`。
这是十片 G3 面的**现行证据**；第二轮 `-c12f0498` 与第一轮 `-06b65688` 两组原样保留。`e0f26b37..d3003c2f` 在 `src/`、`tests/` 下仍零差异。

**沿用、不重跑的两项，理由：**`e0f26b37..c12f0498` 在服务端 `src/`、`tests/` 下零差异（#153、#155 只改 `scripts/`），所以服务端 `CONTROL_SERVER_G2`（绑 `06b65688`）
与服务端全量 L1 的产品代码与重跑身份相同。CI 三连的 28 个合成场景只用合成车载端，不经过车载端代码；#155 改的 `Get-L2RealInbound` 只在真装置装置里调用，
#153 改的是 staged runner，都不在合成场景的路径上。

## 身份

| 项 | 值 |
| --- | --- |
| 协议 | `(AGV_FULL_PRODUCT, 3)`，`releaseVersion 2.0.0`，tag `protocol-v2.0.0` → `86575456c847041515b7b75e8851a00e0d939804` |
| `ProtocolReleaseIdentity` 其余字段 | manifest `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7`，schema bundle `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221`，vectors `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a`，`APPROVED_RELEASE` |
| 服务端产品代码 | `e0f26b37`（本票开工时的 `fp/v2-impl` 顶端，含 control-server#142 的 PR #147） |
| G3 共享绑定提交（第 1 步） | `06b65688`，`chore(g3)`：`ControlServerCommit e0f26b37`、`OnboardCommit 9748c418`、`SimulatorCommit fb5f7c59`、`ProtocolCommit 86575456`；内嵌假对端身份同步到 v2.0.0；`run-demand-bearing-g3-vectors.ps1` 的 tag 判据由 `protocol-v1.0.0` 改为 `protocol-v2.0.0` |
| 车载端 | `w2g/fp-v2-impl@9748c418`（onboard-hmi PR #111，hmi#109 合入） |
| 模拟器 | `main@fb5f7c59` |
| 编排器 | `06b65688`（`scripts/` 以 control-server#71 之后的 L2 编排器为准，含 #141 的「在动」落库等待） |

`06b65688` 相对 `e0f26b37` 只改两个 G3 脚本，`src/`、`tests/` 零差异，所以 G2 与 CI 三连绑 `06b65688` 与 G3 绑 `e0f26b37` 是同一份产品代码。

## 一、L1

### 实测

两端在出口身份上各跑一次全量（本机，真装置时段内）：

| 端 | 命令 | 结果 |
| --- | --- | --- |
| 服务端 | `dotnet test .\tests\ControlServer.Tests\ControlServer.Tests.csproj -c Release`（`e0f26b37` 代码） | **1255 / 1255 通过**，0 失败 0 跳过；收尾的出站 schema 校验 1491 行、34 种消息、0 违约，`SublotRejected` 18 行被校验 |
| 车载端 | `dotnet test .\SQCD_8005AGV.sln -c Release`（`9748c418`） | **536 / 536 通过**（`SQCD.Agv.UnitTests` 354、`SQCD.Agv.WireToGateG2Tests` 182），0 失败 0 跳过 |

摘要与服务端全量的 schema 报告存在 `evidence/l1/20260919-protocol-v2.0.0-e0f26b37-9748c418/`。**车载端全量在 `9748c418` 上是全绿的，而缺陷单 A 就在这个提交上**：车载端单元测试覆盖不到恢复入口在重启后的显隐，修复票 onboard-hmi#112 要补能复现它的测试。

出站 schema 门禁（control-server#85、onboard-hmi#74）在两端各有新增覆盖：服务端全量运行末尾的 `schema-conformance/` 报告；车载端经 G2 的 `OutboundSchemaConformance` 夹具。

### 批次 5 每项新能力 ↔ 新增测试

测试名取自各合入提交新增的 `[Fact]`／`[Theory]`；服务端类名按现代码（control-server#130 拆过 `JourneyRuntimeWorkerTests`）。
格子里只列代表方法，括号为该票新增总数；完整清单可用 `git diff <merge>^1 <merge> -- tests` 复现。「无条目载体」指该能力在剖面里没有挂 `BATCH-5` 的需求条目（批次 5 的条目只有 `REQ-0356`～`0358`，其余工程在规格第 3.3 节）。

| 票 | 需求条目（`v1.4.0`） | 新能力 | 新增测试 | 合入 |
| --- | --- | --- | --- | --- |
| control-server#77（批次5-08） | 无条目载体 | 重连补发的持久报文按首次受理重签 `DurableAck`；入站消息开头清跟踪（program#61 ①，MVP `e90e924e`） | `OnboardMessageProcessorTests.AProgressReportResentIntoTheNextSessionIsAcknowledgedFromItsFirstAcceptance`、`RecoveryStateMachineG2Tests.AResentRecoveryResultIsNotJudgedASecondTimeOnItsWireHash` 等（5） | PR #100 `e876a970` |
| control-server#78（批次5-09） | 无条目载体 | 恢复结果结算它回答的恢复命令；握手报告核销已结清 attempt（MVP `219b033f`、`74019789`） | `RecoveryStateMachineG2Tests.ACompensationResultSettlesItsCommandSoTheNextSessionDoesNotReplayIt`、`.AReportNamingAnAttemptTheServerAlreadySettledNeedsNoFurtherResult` 等（5） | PR #110 `256ef202` |
| control-server#79（批次5-10） | 无条目载体 | 站点离站期限：到站起算、未录入超时结束本站、断联作废重新计满 | `AnUnscannedPickupEndsAtTheStationDeadlineInOneSaveAndReleasesTheVehicle`、`TheStationDeadlineDoesNotEndTheStopWhileADoorIsOpenOrUnknown` 等（12）；L2 `station-deadline-sublot-timeout` | PR #97 `97608fb7` |
| control-server#80（批次5-13） | 无条目载体 | 旅程阻断原因带开始时间上看板（批次 5 唯一带迁移） | `BlockedJourneyDashboardTests` 18 个、`Batch5MigrationDisciplineTests` 2 个等（21）；L2 `blocked-journey-dashboard-projection` | PR #118 `1d1c2ce5` |
| control-server#81（批次5-16） | 无条目载体 | 装货中期限：仓门未闭只告警不结束本站；确定失败防御性结算；会话豁免按车过滤（MVP `770447f5` 会话豁免半） | `JourneyRuntimeWorkerLoadDeadlineTests.ADoorLeftOpenPastTheDeadlineRaisesTheAlarmWithoutEndingTheStop` 等（10）；L2 `load-determinate-failure-and-door-open-timeout` | PR #123 `72496a85` |
| control-server#82（批次5-17） | 无条目载体 | 录入后按派车范围找需求并按 BR-013 重算，不符时回 `SublotRejected`（MVP `25a298d4`） | `JourneyRuntimeWorkerSublotRejectedAfterEntryTests.AnEntryWhosePackageCapacityIsGoneAfterTheDispatchIsRefusedAndUnlocksNothing`（绑 `CV-SUBLOT-REJECTED-AFTER-ENTRY`）等（13）；L2 `sublot-rejected-after-entry` | PR #127 `bc5c8e78` |
| control-server#83（批次5-18） | 无条目载体 | 扫码前取消服务端半边：授权后等 `ALL_EMPTY` 空结果再终结 | `ACancellationBeforeAnySublotIsAuthorizedWithNoSlotsAndEndsTheStopOnlyOnAnAllEmptyResult`（绑 `CV-LOAD-CANCELLATION-BEFORE-LOAD`）等（15）；L2 `load-cancelled-before-sublot` | PR #116 `d0250a10` |
| control-server#84（批次5-22） | 无条目载体 | 服务端协议包升 v2.0.0：身份、vendor、七项改动、恢复消息带 attempt | `ProtocolPayloadShapeArchitectureTests.TheSublotRejectionTypeMatchesItsFrozenSchema`、`RecoveryStateMachineG2Tests.EachRecoveryMessageNamesTheAttemptOfTheLoadTheSessionIsAbout` 等（19） | PR #107 `f563efa8` |
| control-server#85（批次5-24） | 无条目载体 | 出站报文 schema 门禁 | `SchemaConformanceToolTests` 13 个、`ProtocolEnvelopeObserverArchitectureTests` 2 个等（19） | PR #126 `4a5f6707` |
| control-server#86（批次5-30） | 无条目载体 | ADR-cross-0058 真装置 L2 三条 | `real-onboard-load-door-closed-empty-reopens`、`real-onboard-station-timeout-door-open`、`real-onboard-unload-not-emptied` | PR #132 `4036bd76` |
| control-server#87（批次5-31） | 无条目载体 | G3：`FP-IS-02` 两条新向量的 journey 场景与 runner 认领 | `g3-load-cancellation-before-load`（`G3-02-31`～`36`）、`g3-sublot-rejected`（`G3-02-41`～`47`） | PR #129 `7373578d` |
| control-server#88（批次5-32） | 引用 `REQ-0357`（`L2-CAL-10`） | program#61 成对修复的真装置 L2 与协议故障代理 | `ProtocolFaultProxyTests` 6 个；`real-onboard-durable-ack-lost`、`-compensate-then-reconnect`、`-restart-while-waiting-operator`、`-cancellation-authorization-lost` | PR #134 `b7d211d4` |
| control-server#89（批次5-34） | 无条目载体 | 服务端绑定已发布的 v2.0.0 | `ProtocolIdentityArchitectureTests.ThisIdentityIsTheApprovedReleaseItNames` | PR #125 `f8d52ffb` |
| control-server#128（批次5-37） | 无条目载体 | 既有 journey G3 场景适配 v2 执行器 | G3 断言 `G3-02-28`、`G3-07-26`、`G3-07-36` | PR #136 `326da23c` |
| control-server#131 | 无条目载体 | 在途取消、补偿、故障交接三条结算路径释放车辆占用 | `AfterAnInFlightLoadIsCancelledTheVehicleTakesTheNextDemand`、`RecoveryStateMachineG2Tests.EachResultThatEndsACommandedLoadFreesTheVehicleForItsNextOrder` 等（3） | PR #133 `c75ca69e` |
| control-server#137 | 引用 `REQ-0240`～`0242` | 强制机械取出结算与会话关闭、受控取货写永久抑制 | `AForcedRecoveryWithANamedHandoffEndsTheDemandAndClosesTheSessionSoTheVehicleCanOpenAnother`、`AfterAForcedRecoveryTheVehicleStaysUnreadyUntilAHardwareRecoveryRecordForItArrives` 等（8） | PR #140 `3dcde12b` |
| control-server#138 | 无条目载体 | 车在自己的关卡路段上会话不就绪直到停稳（按设计钉住） | `WireToGateStoreTests.ByDesignAVehicleOnItsOwnGateLegHoldsTheSessionNotReadyUntilItStandsStill` | PR #143 `4b2f2b65` |
| control-server#139 | 无条目载体 | 看板：在途运单可解释的「车辆状态未知」不直接升级维护管理员 | `BlockedJourneyDashboardTests` 6 个 | PR #144 `5e32c6bc` |
| control-server#141 | 无条目载体 | L2 场景等「在动」落库后再摆到站 | `scripts/l2/Test-L2SafetyDurableWait.ps1`；`L2-MV-08` | PR #145 `63ddbbae` |
| **control-server#142** | **`REQ-0358`**（`CP-0005`） | 看板「期待动作超时」只读卡片；超时告警后向车要一次中途安全快照 | `ExpectedActionOverdueTests.ANewOverdueAlarmAfterTheHandshakeAsksTheVehicleOnceForItsSafetySnapshot`、`.AMidSessionSnapshotAdvancesTheSafetyRevisionWithoutDroppingTheSessionBackIntoTheHandshake`、`.TheEndpointListsTheOverdueSlotWithVehicleStationActionWaitAndTheReadingsTheVehicleSent`、`.WhenTheVehicleWithdrawsTheAlarmTheRowDisappears` 等（23） | PR #147 `e0f26b37` |
| control-server#67（批次4（第二版）-03） | 引用 `REQ-0191`、`0350`、`0353` | 投运前三份现场前置记录 | 无（文档与证据） | PR #146 `1a4dc6ae` |
| control-server PR #62 | 无条目载体 | 准入策略与共享地图漂移只挡新单，在途照常推进 | `JourneyRuntimeWorkerAdmissionTests.AStationAddedToTheSharedMapDoesNotStrandCargoAlreadyBoundForTheGate` 等（3） | `56f169e3` |
| control-server#63（PR #92） | `REQ-0356`，`REQ-0247`、`0248`（`CP-0003`） | 急停锁住即停稳；人员确认后服务端解除且不重触发 | `EmergencyStopSupervisorTests` 12、`EmergencyStopReleaseEndpointsTests` 7 等（31）；L2 `emergency-stop-operator-release` | PR #92 `ee74ac82` |
| onboard-hmi#67（批次5-11） | 无条目载体 | 并入 `w2g/b3-on-v2` | `RecoveryVectorG2Tests.RecoveryCanBeRequestedAgainAfterTheServerRefusedTheSession` 等（2） | PR #63 `5acf4738` |
| onboard-hmi#69（批次5-14） | 无条目载体 | 会话换代重置安全签名去重；补发后照常握手（MVP `004891f`、`3ecb490`＋`a56a59d`） | `WireToGateG2Tests.ReconnectDuringRecoverySupersedesInterruptedReportWithFreshHandshake` 等（3） | PR #81 `15e831fd` |
| onboard-hmi#70（批次5-15） | 无条目载体 | 中断操作按实时 IO 结算；只确认 CLOSED 快照（MVP `6846e98`、`a696add`＋`86fe0a4`、`f1077b4` 行为部分） | `WireToGateSlotOperationExecutorTests.AnOperationTheOperatorFinishedAfterTheProcessDiedSettlesAsCompleted` 等（14） | PR #80 `ecf688cd` |
| onboard-hmi#71（批次5-19） | 无条目载体 | 操作员应答每次新 `messageId`（MVP `cd1254e`、`297dd81`＋`5e29f58`） | `RecoveryVectorG2Tests.ALostCancellationAuthorizationIsAskedForAgainWithTheFirstPressContentAcrossARestart` 等（10） | PR #87 `b2187498` |
| onboard-hmi#72（批次5-20） | 无条目载体 | 执行器目标态闭环：相反态自动重开，只有 `UNKNOWN` 进恢复 | `ALoadDoorShutEmptyIsReopenedEveryRoundUntilTheBasketIsIn` 等（18） | PR #91 `d84cff99` |
| onboard-hmi#73（批次5-23） | 无条目载体 | 车载端协议包升 v2.0.0 | `SublotEntryScopeG2Tests` 8、`InboundPayloadSchemaBoundaryTests` 7 等 | PR #88 `80093358` |
| onboard-hmi#74（批次5-25） | 无条目载体 | 车载端出站 schema 门禁 | G2 夹具 `OutboundSchemaConformance`＋`tools/SQCD.Agv.SchemaConformance`（无新增 `[Fact]`） | PR #93 `ff59512a` |
| onboard-hmi#75（批次5-26） | 无条目载体 | 离站期限倒计时显示 | `StationDepartureCountdownFormatterTests` 9、`StationDepartureCountdownViewModelTests` 12 | PR #94 `acd52b2f` |
| onboard-hmi#76（批次5-27） | 无条目载体 | 扫码前取消车载端半边 | `LoadCancellationBeforeSublotG2Tests.ACancellationBeforeAnySublotReportsAllEmptyWithoutOpeningADoor` 等（16） | PR #95 `08bcf7dd` |
| onboard-hmi#77（批次5-28） | 无条目载体 | `SublotRejected` 显示真实原因 | `SublotRejectedAfterEntryG2Tests.ARejectionAfterEntryIsShownAsItsReasonAndNotAsABlockedRecovery` 等（14） | PR #96 `19e8f205` |
| onboard-hmi#78（批次5-29） | 无条目载体 | 期限到期后仍重开不判死；在途装货可取消（MVP `1acb018`） | `StationDeadlineExpiredG2Tests.PastTheDeadlineEveryEmptyCloseIsReopenedAndPromptedWithTheWayOut` 等（24） | PR #103 `8153946b` |
| onboard-hmi#79（批次5-35） | 无条目载体 | 车载端绑定已发布的 v2.0.0 | `ProtocolIdentityArchitectureTests.TheTagIsSchemaLegalAndTheApprovalStatusSaysItIsTheApprovedRelease` | PR #97 `579f19cb` |
| **onboard-hmi#106** | **`REQ-0357`**（`CP-0004`） | 一次只开一扇仓门：装货途中取消先收尾接管的开门；开锁前整车一门校验 | `ACancelledLoadsOpenDoorIsFinishedBeforeAnyLoadedSlotIsUnlocked`、`EveryUnlockOfALoadOrAnUnloadFindsEveryOtherDoorShut`、`SqliteWireToGateJournalActiveUnlockSetTests.AnActiveUnlockSetOfMoreThanOneSlotIsRefused` 等（14）；真装置 `L2-CAL-10`（本轮 PASS） | PR #108 `b65969ba` |
| onboard-hmi#107 | 引用 `REQ-0240`～`0242` | 强制机械取出不发 DO；卸货开放受控取货与强制取出；硬件记录解除隔离 | `RecoveryVectorG2Tests.ForcedMechanicalRecoverySendsNoUnlockAndReportsOnlyAfterTheOperatorConfirms` 等（14） | PR #110 `8f308bb1` |
| **onboard-hmi#109** | **`REQ-0358`**（`CP-0005`） | 期待动作超时上报与 HMI 提示；应答中途安全快照；异常处置会话可填原因 | `SlotExpectedActionOverdueTests.TheWaitIsCountedFromTheSlotsFirstUnlockAndAReopenDoesNotResetIt`、`.TheAlarmIsRaisedExactlyAtTheThresholdAndNotAMomentBefore`、`StationDeadlineExpiredG2Tests.AMidSessionSafetySnapshotRequestIsAnsweredFromTheLiveIo` 等（26） | PR #111 `9748c418` |

`REQ-0359`（服务端人工判故障）挂 `BATCH-6`、待 `protocol-v3.0.0`（program#115），不在本批。

### 向量绑定

两端 `ProtocolVectorTestBindingArchitectureTests` 的 `VectorsAwaitingTheirSlice` 各剩 9 条，全部属于批次 6～11 的切片（`FP-IS-08`～`13`）。
批次 5 的两条新向量都没有暂钉，各有同名具名测试：

| 向量 | 服务端 | 车载端 |
| --- | --- | --- |
| `CV-LOAD-CANCELLATION-BEFORE-LOAD` | `JourneyRuntimeWorkerLoadCancellationBeforeSublotTests` 2 个 | `LoadCancellationBeforeSublotG2Tests` 12 个等共 14 个 |
| `CV-SUBLOT-REJECTED-AFTER-ENTRY` | `JourneyRuntimeWorkerSublotRejectedAfterEntryTests.AnEntryWhosePackageCapacityIsGoneAfterTheDispatchIsRefusedAndUnlocksNothing` | `SublotRejectedAfterEntryG2Tests` 3 个 |

车载端的 `VectorsThisBatchOwesANamedTest` 为空。**这只证明绑定存在**：向量从未被机械执行（第五节第 2 点）。

## 二、L2

### CI 三连

`gh workflow run l2.yml --ref b5-36/batch-5-exit -f mode=consecutive-all`，run [`35361077376`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35361077376)，
提交 `06b65688`，4 路并行，859 秒。**不改 `l2.yml`**：`consecutive-all` 让清单里每个场景至少跑三遍、每遍一个独立证据目录（`<场景>-01/02/03`），
正是规格要的三连；`session-established-while-moving` 的 `Runs = 3; DefaultRuns = 3` 保持不动。调度会话 2026-09-17 的登记与 PR #119 已把「在 `l2.yml` 改成 `Runs = 3`」改为这一口径。

- 清单 28 个合成场景：批次 2 的 13 个、批次 3 的 2 个、批次 4 的 8 个、批次 5 的 5 个；`scripts/l2/scenarios/` 下没有不在清单里的合成场景（真装置 23 个不在 CI 上，符合 `l2.yml` 头注释）。
- **84 次全部 PASS，中途没有红**，所以不存在「从 1 重新计数」。
- 下载后逐份核对：84 份 `assertions.json` 的 `outcome` 都是 `PASS`，`identity.protocolReleaseIdentity` 都是上面的发布身份（`APPROVED_RELEASE`），
  `identity.batchId` 与 `l2.yml` 逐行一致，`controlServerCommit` 都是 `06b65688`。
- 入库：`evidence/l2/20260918-ci-35361077376-<场景>-NN/`，84 个目录，照 `2b2aa51c` 的先例连日志一起入库。

| 批次 | 场景（各 3/3 PASS） |
| --- | --- |
| 批次 2 | `normal-load`、`session-established-while-moving`、`load-result-requires-recovery`、`load-command-never-answered`、`route-graph-engine`、`create-gate`、`create-gate-unapproved`、`three-synthetic-peers`、`three-vehicle-exit`、`command-surface-order-hold`、`route-graph-staleness`、`emergency-stop-single-trigger`、`emergency-stop-operator-release` |
| 批次 3 | `slot-configuration-activation-replay`、`onboard-alarm-snapshot-dashboard` |
| 批次 4 | `area-assignment-import-rejects`、`area-assignment-unmapped-silent`、`slot-group-selection`、`slot-group-temporarily-full`、`structural-block-oversized-demand`、`slot-group-disabled-no-alert`、`area-assignment-version-freeze`、`mixed-side-station-two-trips` |
| 批次 5 | `station-deadline-sublot-timeout`、`load-cancelled-before-sublot`、`sublot-rejected-after-entry`、`blocked-journey-dashboard-projection`、`load-determinate-failure-and-door-open-timeout` |

### 规格点名的三类场景

| 能力 | 合成 L2（CI 三连） | 真装置 L2（本轮） |
| --- | --- | --- |
| ADR-cross-0058（站点期限） | `station-deadline-sublot-timeout`、`load-determinate-failure-and-door-open-timeout` | `real-onboard-load-door-closed-empty-reopens` PASS、`real-onboard-station-timeout-door-open` PASS、`real-onboard-unload-not-emptied` PASS |
| 到站无货出口（扫码前取消） | `load-cancelled-before-sublot` | G3 `g3-load-cancellation-before-load` PASS（journey） |
| 录入后拒收 | `sublot-rejected-after-entry` | G3 `g3-sublot-rejected` PASS（journey） |

### 真装置七条

在 `repos/8005-agv-control-server` detached 于 `06b65688`，车载端 `9748c418`、模拟器 `fb5f7c59`，依次各跑一次：

| 场景 | 票 | 结果 | 证据 |
| --- | --- | --- | --- |
| `real-onboard-load-door-closed-empty-reopens` | #86 | PASS | `evidence/l2/20260919-real-onboard-load-door-closed-empty-reopens-001/` |
| `real-onboard-station-timeout-door-open` | #86 | PASS | `evidence/l2/20260919-real-onboard-station-timeout-door-open-001/` |
| `real-onboard-unload-not-emptied` | #86 | PASS | `evidence/l2/20260919-real-onboard-unload-not-emptied-001/` |
| `real-onboard-durable-ack-lost` | #88 | PASS | `evidence/l2/20260919-real-onboard-durable-ack-lost-001/` |
| `real-onboard-compensate-then-reconnect` | #88 | 第一轮 **FAIL**（缺陷单 A）；重跑 `-002` **PASS** | `evidence/l2/20260919-real-onboard-compensate-then-reconnect-001/`；对照 `…-onboard-8f308bb1-001/` |
| `real-onboard-restart-while-waiting-operator` | #88 | PASS | `evidence/l2/20260919-real-onboard-restart-while-waiting-operator-001/` |
| `real-onboard-cancellation-authorization-lost` | #88 | PASS，含 `L2-CAL-10`（REQ-0357）：`至多 1 仓 / 采样错误 0 次` | `evidence/l2/20260919-real-onboard-cancellation-authorization-lost-001/` |

`L2-CAL-10` 的红绿对照已在 PR #134 取得（车载端 `8153946b` 上两仓同开），本轮在发布身份上再跑一次，绿。
手动跑的真装置场景 `identity.batchId` 是编排器默认值 `batch-2`，只是标签，判定不看它。

## 三、门禁

### `CONTROL_SERVER_G2`

`evidence/g2/20260918-protocol-v2.0.0-06b65688/`：十片全 `PASS`，每份 `gate-result.json` 绑 `protocolTag protocol-v2.0.0`、`protocolRepositoryCommit 86575456`、
`protocolManifestSha256 4ac095ad…`、`protocolApprovalStatus APPROVED_RELEASE`。`schemaConformance` 十片 `linesInViolation = 0`、`knownViolationsMatched = 0`，
共校验 2234 行；`FP-IS-02` 的覆盖报告里 `SublotRejected` 有 18 行被校验。按方法名比对上一轮（`052759bc`），没有测试被删。

### `ONBOARD_HMI_G2`

车载端仓 `evidence/g2/20260918-protocol-v2.0.0-9748c418/`（分支 `w2g/b5-36-g2-evidence`，小 PR 进 `w2g/fp-v2-impl`）：十片全 `PASS`，build／test／format 退出码全 0，
身份同上。九片出站校验零违约，共 2595 行；**`FP-IS-04` 的 `schemaConformance` 为 `null`**：它选中的 7 个测试都是执行器单元测试、不发协议报文，
`run-w2g-g2.ps1` 对这种片按约定写 `null`（选中了 G2 测试却没产出覆盖文件才判失败）。唯一「消失」的测试名是 onboard-hmi#69 的有意改名。

### G3

绑定见「身份」一节。runner 从 `repos/8005-agv-control-server`（`06b65688`，干净）启动，`harnessWorktreeCleanAtStart: true`。

| runner | 结果 | 片 | 证据 |
| --- | --- | --- | --- |
| `run-staged-g3.ps1` | **`STAGED_SLICE_FAIL`** | `FP-IS-00`、`06`、`14`、`15` PASS；**`FP-IS-07` FAIL**（缺陷单 B） | `evidence/g3/20260918-protocol-v2.0.0-staged-06b65688/` |
| `run-staged-g3-restart.ps1` | `STAGED_G3_PROCESS_RESTART_PASS` | | `evidence/g3/20260918-protocol-v2.0.0-restart-06b65688/` |
| `run-demand-bearing-g3-vectors.ps1`（`-FieldRunRoot …fullloop-20260829T131549Z`） | `DEMAND_BEARING_G3_VECTORS_PASS` | | `evidence/g3/20260918-protocol-v2.0.0-demand-bearing-06b65688/` |
| `run-journey-g3.ps1` | **`JOURNEY_G3_SLICE_FAIL`** | `FP-IS-01`、`02`、`03` PASS；**`FP-IS-07` FAIL**（缺陷单 A） | `evidence/g3/20260918-protocol-v2.0.0-journey-06b65688/` |

- `FP-IS-02` 的 journey 面包含 control-server#87 的两条新场景 `g3-load-cancellation-before-load`、`g3-sublot-rejected`，都 PASS。
- G3 归属按 control-server#60 复核后的 `g3-slice-evidence.ps1`（头注释记录了结论）。
- `FP-IS-03` 的 `g3-predeparture-check-expires` 本轮 PASS（control-server#138 修正判据后）。
- `g3-forced-mechanical-recovery`（`G3-07-44`，control-server#137 合入后欠的真装置复跑）本轮没走到判据：它和另外三条恢复场景一起卡在缺陷单 A。
- 需求承载 G3 用的是单需求 fullloop 库，只核对、不含整库生产数据（control-server#43 结论）。

## 四、红证据与缺陷单

两轮的红全部保留，没有被绿覆盖；每处红都先读证据再定性，没有「重跑一次看看」：

| 单 | 红在哪里 | 性质 | 修复去向 |
| --- | --- | --- | --- |
| A [`20260919-onboard-recovery-entries-missing-after-hmi109.md`](defects/20260919-onboard-recovery-entries-missing-after-hmi109.md) | journey `FP-IS-07` 四条恢复场景；真装置 `real-onboard-compensate-then-reconnect` | **车载端回归**，onboard-hmi PR #111（`9748c418`）引入：重启进 `RecoveryRequired` 后四个管理员入口不出现。车载端退到 `8f308bb1` 对照，入口出现、补偿收敛。根因：`MainViewModel.SetRecoveryEntry` 没把属性名转交给 `SetProperty`，变更通知名成了 `"SetRecoveryEntry"`，绑定收不到；修复在 onboard-hmi PR #114 | [onboard-hmi#112](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/112) |
| B [`20260919-staged-g3-forced-recovery-criteria-predate-cs137.md`](defects/20260919-staged-g3-forced-recovery-criteria-predate-cs137.md) | staged `FP-IS-07` 三条强制取出判据 | **G3 判据落后**：仍断言 control-server#137 移除的断头行为；产品行为与 #137 设计一致 | [control-server#151](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/151)（改 `run-staged-g3.ps1`） |
| C [`20260919-g3-forced-recovery-scenario-skips-isolation-confirm.md`](defects/20260919-g3-forced-recovery-scenario-skips-isolation-confirm.md) | 重跑的 journey `g3-forced-mechanical-recovery` 中止，连带 `FP-IS-01`／`02`／`03`／`07` | **场景脚本缺陷**：只按「强制机械恢复」入口，不按 onboard-hmi#107 加的「已隔离并完成机械取出」，车载端按设计不上报结果，服务端工作流停在 `AwaitingResult` | [control-server#156](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/156)（PR #157，`d3003c2f`） |

另有一处第一轮就在的服务端 L2 脚本缺陷由 onboard-hmi#112 的会话发现并修复：`Get-L2RealInbound` 在单条回应时读 `.Count` 抛异常（control-server#154，PR #155），
它让第一轮车载端退到 `8f308bb1` 的对照运行误判超时，记在缺陷单 A 里。

**状态：**A、B、C 都已修复并复验 PASS，三份缺陷单均改为 fixed、附复验证据。

**修好之后从头重跑了什么**（不拼接）：A 改了车载端产品代码，所以车载端 `ONBOARD_HMI_G2` 十片、车载端全量 L1、四个 G3 runner、真装置七条都在新身份上重跑（见「重跑」一节）；
B 随这一整轮 G3 重跑。服务端 G2、服务端全量 L1 与 CI 三连沿用，理由同「重跑」一节。
C 修复合入后：G3 共享绑定的 `ControlServerCommit` 移到 `d3003c2f`，四个 G3 runner 从头重跑，全绿；其余证据不经过那个场景文件，保留。

这两处与 control-server#90 评论里调度会话留档的那一类（「读到的事实比它被采信的时刻旧」，D-1 与 `20260916-arrival-trusted-on-a-session-row-pinned-for-one-iteration.md`）
不是同一类：本轮 `session-established-while-moving` 三连全绿，没有再现。那一类在这套代码里出现过两次，仍值得在后续引擎改动里当作固定检查项。

## 五、必须如实写明的各点

1. **批次 2、3 在批次 5 重证不等于它们被重开**（规格 8.8 第 7 条）。它们的完成结论不变，只是 `FP-IS-00`～`07`、`14`、`15` 的证据在 `protocol-v2.0.0` 身份上重新出一份。
2. **向量从未被机械执行（弱绑定）。**「新增向量各有同名具名测试」只证明 `vectorId` 与测试名之间的绑定存在，不证明测试逐字执行了向量。
3. **期限到期后仓门已闭而货没动：按 program#55 一直重开、不判失败。**v2 车载端不产出 `FAILED`／`OPERATOR_TIMEOUT`；服务端 control-server#81 的确定失败结算只做防御，在 v2 上没有生产者。
   ADR-cross-0058 决策 1、5 的回写由 onboard-hmi#78 在 program 仓提的 PR [program#110](https://github.com/trytoreachpeak0/8005-agv-program/pull/110) 完成，**已合入**（`b503b66e`）。
4. **按业务键永久抑制（`REQ-0155`／`0156`／`0211`）不在批次 5。**批次 5 的站点超时取消只按 `DemandId` 挡重派。control-server#137 的受控取货抑制同样不含 `TERMINATED_BY_FAULT_CARGO_HANDOFF` 在 `REQ-0156` 里的来源，留批次 7。
5. **program#61 的 B 类：**
   - 本批次合入（MVP 提交 → v2 提交 → 合入提交）：`e90e924e`（随 `8822a599`）→ `dd0cb585` → `e876a970`（control-server#77）；`219b033f` → `5ccc3cb4` → `256ef202`（#78，同提交带 A 类 `369919f5` 的残差测试）；
     `74019789` 由 #78 的 `ALoadCorrectionAfterAResumeOnTheSameConnectionIsJudgedOnTheStoredStage` 覆盖，没有提交信息直接引用它，按 v2 重写的对应提交**未核实**；
     `770447f5` 会话豁免半 → `4925fb57` → `72496a85`（#81）；`25a298d4` → `8c5ee5ab` → `bc5c8e78`（#82）；
     车载端 `004891f` → `82066c0`、`3ecb490`＋`a56a59d` → `e11c292`，均在 `15e831fd`（onboard-hmi#69）；`6846e98`、`a696add`＋`86fe0a4`、`f1077b4` 行为部分 → `2400088`（`ecf688cd`，#70），`f1077b4` 协议字段部分 → `27cba8c`（`80093358`，#73）；
     `cd1254e`、`297dd81`＋`5e29f58` → `f070bf1`（`b2187498`，#71）；`1acb018` → `2bcd742`（`8153946b`，#78）。
   - **不在批次 5**：`ef5dad8e`＋`42269d43`（带迁移的活性窗口）、车载端 `1d584af`（配置挂住），`770447f5` 依赖 `JourneyDemands` 的多需求 store 部分（批次 7）。
   - **Q4 只做了出站**：control-server#85、onboard-hmi#74 针对 v2.0.0 落地出站 schema 门禁；入站普查与在 L2 里校验车辆发来的行都没有做。
6. **真装置 L2 用的是模拟器，不证明光幕极性、锁反馈时序与机械弹开**（ADR-cross-0058 Verification「没有证明的」第 3 条同理）。control-server#44 的极性对齐仍是切生产门槛。
7. **批次 4 的 L2 场景已包含在三连里**（8 个，`batch-4`）。批次 4 在批次 5 之前出口（control-server#76），其三连证据读回的是 `protocol-v2.0.0@86575456` 的候选态（`SUPERSEDING_CANDIDATE`）；
   本轮在发布态（`APPROVED_RELEASE`）上全部重跑。所用编排器为 control-server#71 之后的 `06b65688`。
8. **需求条目一律按基线 `v1.4.0` 引用**（本票票面写 `v1.3.0`，写票后基线已升）。急停相关的 `REQ-0247`、`REQ-0248` 以 `CP-0003` 修订后的条文为准、`REQ-0356` 为新增，
   实现归 control-server#63，不在批次 5 的功能票里。`REQ-0357`（`CP-0004`）、`REQ-0358`（`CP-0005`）挂 `BATCH-5`，已列入第一节对照表；`REQ-0359` 挂 `BATCH-6`。
9. **control-server#63 在第 5 步时已合入**（PR #92，`ee74ac82`，2026-09-17）。`emergency-stop-single-trigger`、`command-surface-order-hold`、`emergency-stop-operator-release`
   三个急停场景按合入后的判据随三连在 `protocol-v2.0.0` 上各三次 PASS，满足规格 19.5 节切生产门槛第 8 条的 L2 部分。
   白名单核对：服务端 vendor 副本 `vendor/8005-agv-program/docs/riot-call-allowlist.md` 与 program 仓 `main` 上的同名文件是同一个 git blob（`7315961a`），
   `RiotCallAllowlistArchitectureTests.ApprovedAllowlistSha256` 钉的 `ad15dd9b04a8b18ac1119b32a7e8ad05c1a57e216e575bfb3ea8e87c816c838f` 与文件字节一致；program#101 的合入提交是 `54621cfa`。
10. **`protocol-v2.0.0` 的 `ProtocolVersion` 是 3，与 `WIRE_TO_GATE_MVP 0.3.0` 同数。**整数只在同一 `profileId` 内单调递增，比较身份一律用完整 `ProtocolReleaseIdentity`；本报告与证据都成对写 `(profileId, ProtocolVersion)`。
11. **`loadingPhase` 在批次 5 只出现 `LOADING` 与 `CLOSED`＋`PLANNED_LOADING_COMPLETE`；`chargingCycleState` 按语义表取值，v2 没有自动充电。**第 5、6 项的语义在批次 7、9。

## 六、剩余风险

- **REQ-0358 两端没有真装置证据。**现有 L2 与 G3 场景都没有覆盖「车载端超时告警上报 → 服务端请求中途快照 → 车载端回快照 → 看板出卡片」这条链；
  control-server#142 与 onboard-hmi#109 各自只在假服务端或替身上互通过（`ExpectedActionOverdueTests`、`StationDeadlineExpiredG2Tests`）。
  门槛默认 6 分钟，真装置场景要用缩短的门槛取证。**建议另开一张真装置场景票**，本票不新写场景。
- 缺陷单 A 暴露的问题类型：车载端视图模型的通知名错误，车载端 CI 与单元测试都看不到，只有真装置与 journey G3 能看到。onboard-hmi#112 补了视图模型层的守卫测试（每个变更通知都以真实的公开属性命名），但恢复入口的端到端覆盖仍只在真装置上。
- control-server#137 登记的范围外问题：强制结果为 `FAILED`／`UNKNOWN`、以及其它恢复动作结果对不上账时，恢复会话仍停在 `EXECUTING`，同车开不了新会话。需要另开票。
- `run-journey-g3.ps1` 与 staged runner 在批次 5 期间都只在出口票里第一次跑新行为，这次两个红都是这样暴露的。恢复入口的端到端覆盖只在真装置上，车载端 CI 看不到。
