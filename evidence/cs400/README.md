# control-server#400 的证据

批次9-02：充电桩名册与充电策略版本的受治理导入、逐车投运判定。三次本机合成 L2，场景都是
`charging-policy-missing-vehicle-not-commissioned`（两台车，已激活的策略只覆盖第二台 `BROKERX-L2-0002`）。

| 目录 | 跑的是什么 | 结论 |
| --- | --- | --- |
| `l2-charging-policy-missing-1/` | 提交 `b582d638` | **FAIL，判据自身的缺陷**：L2-CPM-03 读到「B: 1 intents」，而快照 `db-OrderIntents.json` 里只有 A 的一张单。原因是查询零行时返回 `$null`，`@($null).Count` 等于 1。判据改成在 SQL 里 `COUNT(*)` 后重跑 |
| `l2-charging-policy-missing-2/` | 同一产品代码，修正后的判据 | **PASS**：需求派给 A；服务端日志有 `Vehicle BROKERX-L2-0001 takes no new work: CHARGING_POLICY_NOT_APPROVED`；B 在整个窗口里 0 张订单意图、0 张 RIoT 订单；服务端存活 |
| `l2-charging-policy-missing-red-no-criterion/` | 注入缺陷：宿主不注册 `ChargingPolicyCommissioningCriterion` | **FAIL，红证据**：需求派给排在前面的 B（`BROKERX-L2-0001 / AGV-L2-001`），日志里没有那一行，B 有 1 张订单意图、1 张 RIoT 订单。L2-CPM-01、02、03 三条变红 |

为了体积只留了判据用到的东西：`SUMMARY.md`、`assertions.json`、`timeline.jsonl`、订单意图与旅程、策略与名册各表的快照、假 RIoT
快照、导入的策略文件；服务端日志只留 `grep -E 'takes no new work|CHARGING_POLICY'` 的摘录（`logs/control-server.excerpt.log`）。

## L1 注入变异（本机，每个变异单独重编、只跑本票新测试，跑完从备份还原）

| 变异 | 改了什么 | 变红的用例 |
| --- | --- | --- |
| M1 | 阈值关系把 `<=` 改成 `<`（完成线等于入口线被放过） | `EachThresholdRelationViolationRejectsTheWholeFile(30,80,80)`、`TheOneThresholdRelationFunctionDecidesTheBoundaries(30,30,30)` |
| M2 | 名册内容未变时不写「已是此内容」审计 | `TheSameRosterAgainWritesNoVersionButRecordsTheImportInTheAudit`、`AnEmptyRosterOntoNothingIsVersionOneAndEmptyingAgainIsUnchangedButAudited`、`ACrashBeforeCommitOfAnUnchangedImportLeavesNoAudit`、`TwoProcessesImportingTheSameRosterAtOnceWriteOneVersion`、`TheShippedRosterFilesOpenAndCloseTheWindowThroughTheSameVerb` |
| M3 | 判定读库出错时不再按「没有」处理（异常外抛） | `APolicyThatCannotBeReadIsNoPolicy` |
| M4 | 去掉「只有测试批准要显式开关」的检查 | `ApprovalAndActivationAreRecordedAndATestOnlyApprovalNeedsTheExplicitSwitch`、`AnL2PresetApprovalActivatesOnlyWithTheExplicitSwitch` |
| M5 | 名册校验不再拒绝等待点 | `EachClassOfErrorRejectsTheWholeFileAndWritesNothing(waiting)`、`AFileWithSeveralErrorsReportsThemAllAtOnce` |
| M6 | 从 `DispatchAdmissionCriteria.Default` 里去掉新判据 | `AVehicleThePolicyDoesNotCoverIsRefusedWithTheNewReasonAndTheOtherVehicleTakesTheDemand`、`WithNoPolicyAtAllTheRoundStillRunsAndEveryVehicleIsRefused`、`TheEnRouteChainCarriesTheSameCommissioningCriterionAndRefusesTheSameVehicle` |
| MC（审查后补） | 判据对解析器回 `Unreadable` 的决定放行 | `AnUnreadablePolicyDecisionIsRefusedLikeAMissingOne`（补这条之前，该变异下定向 65 条全绿存活） |

M2、M4 第一次写成 `if (false)`，编译器以 CS0162（不可达代码）拒绝、没有跑成，不算绿；改成 `if (dryRun && !dryRun)` 这类非常量条件后才生效。
宿主 DI 那一行注册（`AddDispatchAdmission`）的变异由上面的 L2 红证据覆盖。

## 这些证据跑的是哪一版

- 三份 L2 证据的 `SUMMARY.md` 都写 `controlServerCommit` 为 `b582d638`。其中 `l2-charging-policy-missing-red-no-criterion` 那一份
  跑的是**在 `b582d638` 上注入过缺陷的工作树**（宿主不注册新判据），不是 `b582d638` 本身；编排器只记 HEAD，看不出工作树改过。
- 三份都停在 `b582d638`，**不含之后合入的 `f55669db`（#415，充电建单）**。合并之后的回归靠 CI 的 `l2`。
- 审查后场景脚本把 `$logRoot`、`$HealthPort` 改为从 `$Context` 取（编排器上下文新增 `LogRoot`），判据本身没变。
