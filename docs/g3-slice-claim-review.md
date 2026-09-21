# G3 断言到切片归属表：逐条复核清单（control-server#60）

`scripts/g3-slice-evidence.ps1` 里 `Get-G3RunnerClaim` 这张「断言 → 切片」归属表，是 agent 根据断言名和 `vendor/8005-agv-protocol/integration-slices/index.json` 里各切片的 `vectorIds` **第一次写下来**的，不是照着已有记录抄的（文件头注释自己也这么说）。`Assert-G3ClaimCoversReport` 只防漂移（runner 增删改名而表没跟上），**防不了一开始就归错**。本清单把每条断言（含运行级）逐条对到向量，并写出在 runner 代码里实际核实到的检查内容，交给切片族负责人逐条拍板。

说明：runner 列用简称：`staged` = `STAGED_G3_REAL_PEERS_DETERMINISTIC_PLAINTEXT`（`scripts/run-staged-g3.ps1`）；`restart` = `STAGED_G3_REAL_PEERS_PROCESS_RESTART_NO_MOVEMENT`（`scripts/run-staged-g3-restart.ps1`）；`demand` = `DEMAND_BEARING_G3_RESULT_AND_RIOT_UNKNOWN_VECTORS_NO_MOVEMENT`（`scripts/run-demand-bearing-g3-vectors.ps1`）；`journey` = `JOURNEY_G3_REAL_ONBOARD_SIMULATED_COUNTERPARTS`（`scripts/run-journey-g3.ps1`）。journey 的检查内容取自 `scripts/l2/scenarios/<场景>.ps1` 中 `$assertions.Add('<L2 id>', '<判定文字>', ...)` 的判定文字，L2 id 写在断言名后的括号里。向量内容（`orderedExpectedMessages`、`productAssertions`）读自协议仓 `vectors/`；`CV-LOAD-CANCELLATION-BEFORE-LOAD` 与 `CV-SUBLOT-REJECTED-AFTER-ENTRY` 两个新向量按 `protocol-v2.0.0`（`86575456`）的 `expected.json` 对照。

## 复核结论（2026-09-18）

用户在 2026-09-18 复核本清单，结论是**整体按推荐**：下面「需要用户拍板的疑点」31 项全部按建议处理；其中要改 runner 判定代码的两项（第 1、13 项，即 E 组）**在 control-server#87 一起改**。落地情况：

- 归属表 `scripts/g3-slice-evidence.ps1` 已按建议改，头注释记下了这次复核与逐项改动。
- 第 19 项的四条 `riot*` 不再是断言，`run-demand-bearing-g3-vectors.ps1` 把它们记在 `fieldStoreProvenance.riotCreateAuditHistory` 里，照实记录、不参与判定。
- 第 3 项合并为 `recoveryStateReportFirstAckDropReplay` 一条；第 9 项拆成两个独立判定。
- 第 1 项：`identityRejections` 只看身份拒绝用例，心跳两条也不再依赖它。第 13 项：readiness／503 从 `noMovementOrExternalSideEffects` 拆出，新断言 `readinessWithheldWhileRecoveryIsUnreconciled` 归 `FP-IS-00`。
- 四个 runner 与归属表的双向一致由 `scripts/Test-G3RunnerClaims.ps1` 离线检查。
- 已入库的 G3 证据不改；按新归属出证由 control-server#90 负责。

下文的计数与逐条表是复核**之前**的状态，保留原样作为这次复核的依据。

## 计数

| runner | 运行级 | 切片断言 | 合计 |
| --- | --- | --- | --- |
| staged | 3 | 27（FP-IS-00 9、FP-IS-06 7、FP-IS-14 5、FP-IS-15 6） | 30 |
| restart | 9 | 16（FP-IS-00 8、FP-IS-06 3、FP-IS-15 2、FP-IS-14 3） | 25 |
| demand | 4 | 16（FP-IS-04 11、FP-IS-05 5） | 20 |
| journey | 8 | 85（FP-IS-01 10、FP-IS-02 33（其中本票新增 13）、FP-IS-03 12、FP-IS-07 30） | 93 |
| **合计** | **24** | **144** | **168** |

按切片汇总（跨 runner）：FP-IS-00 17、FP-IS-01 10、FP-IS-02 33、FP-IS-03 12、FP-IS-04 11、FP-IS-05 5、FP-IS-06 10、FP-IS-07 30、FP-IS-14 8、FP-IS-15 8。

## 需要用户拍板的疑点

建议取值只有三种：keep（保留）/ move to 切片 X（改归）/ make run-wide（改为运行级）。几处附带「判定逻辑要改」的，改的是 runner 代码，不只是归属表。

**staged**

1. `identityRejections`（运行级）：值取的是整个 probe 的 `status`，其中也包含 FP-IS-06 的心跳重复/冲突两项；FP-IS-06 一红就会连带这条运行级断言，把 FP-IS-14/15 也拖红。反过来，`sameConnectionSameMessageIdSameContent` 与 `sameMessageIdDifferentContentStableConflict` 也要求身份拒绝通过。→ **keep 运行级**，但判定改为只看 `rejectionCases`。
2. `recoveryStateReportFirstAckDropReplay`：对的是 `CV-SESSION-RECONNECT-DURING-RECOVERY`，这个向量 FP-IS-00 与 FP-IS-05 都有。→ **keep FP-IS-00**。
3. `recoveryStateReportFirstAckDropReplayOverPlaintext`：和上一条是同一个布尔值（`$replayPass`），一次检查占了两个名字。→ **keep**，建议合并为一条。
4. `recoverySessionAuthorisationBoundary`：查的是异常恢复会话的认证、单开、requestId 幂等，属于 FP-IS-07 的 `RECOVERY_SESSION_REQUIRES_VERIFIED_ADMINISTRATOR`，FP-IS-00 的四个向量里没有能覆盖它的。→ **move to FP-IS-07**。
5. `recoveryActionsRefusedWithoutPersistedOperation`：没有需求时拒绝 RESUME/COMPENSATE/HANDOFF、scope 不符，以及拒绝装载取消/修正/补偿，对的是 FP-IS-07 的 `EVERY_RECOVERY_ACTION_AUTHORIZED`，另有一部分碰到 FP-IS-02。→ **move to FP-IS-07**。
6. `hardwareRecoveryRecordScopeEnforced`：`HardwareRecoveryRecord` 不在任何向量里，内容上属于 FP-IS-07 的恢复平面。→ **move to FP-IS-07**，并注明没有直接对应的向量。
7. `recoveryCommandSurvivesMidFlightDisconnect`：`ForcedMechanicalRecoveryCommand` 丢失后，同一条 outbox 行在新代次补发。→ **move to FP-IS-07**（`CV-FORCED-MECHANICAL-RECOVERY`）；另一个可选归属是 FP-IS-06。
8. `forcedRecoveryGenerationAdvancesMonotonically`：对的是 `CV-FORCED-MECHANICAL-RECOVERY` 的 `FENCE_FORCED_RECOVERY_BY_GENERATION`。→ **move to FP-IS-07**。
9. `supersededGenerationResultIsHistoricalEvidenceOnly`：对的是同一向量的 `REFUSE_STALE_FORCED_RECOVERY_GENERATION`，而且和上一条是同一个布尔值（`$recoveryGenerationPass`）。→ **move to FP-IS-07**，建议拆成两个独立判定。
10. `recoveryNeverReportsFalseCompletion`：对的是 `CV-FORCED-MECHANICAL-RECOVERY` 的 finalState `RECOVERY_REQUIRED_OR_UNIQUELY_RECONCILED`。→ **move to FP-IS-07**。
    （4–10 全部改归后，staged 的 FP-IS-00 只剩第 2、3 条，而这两条其实是同一个检查。）
11. `sameMessageIdDifferentContentStableConflict`、`businessMessageSameMessageIdDifferentContentStableConflict`：只断言连接被关闭，没有查向量要求的 `ProtocolProblem` / `MESSAGE_ID_CONTENT_CONFLICT`。→ **keep FP-IS-06**，注明比向量弱。
12. `businessMessageDelayedDeliveryAccepted`、`businessMessageReorderedDeliveryAccepted`：FP-IS-06 没有专讲延迟或乱序的向量。→ **keep FP-IS-06**，注明没有直接向量。

**restart**

13. `noMovementOrExternalSideEffects`（运行级）：除了副作用计数，还要求 `/health` 返回 503、会话 readiness 是 `RecoveryRequired`。后两项是 FP-IS-00 `READINESS_DECIDED_BY_CONTROL_SERVER` / FP-IS-05 `NEVER_READY_BEFORE_RECONCILIATION` 的切片证据。→ **拆开**：计数部分 keep 运行级，readiness 部分另立一条断言，归 FP-IS-00。
14. `onboardRestartAdvancesGenerationByExactlyOne`、`controlServerRestartAdvancesGenerationByExactlyOne`：对的是 `CV-SESSION-RECONNECT-DURING-RECOVERY`，这个向量 FP-IS-00 与 FP-IS-05 共用。→ **keep FP-IS-00**。
15. `serverSessionIdentityIsScopedPerConnection`：没有向量对应，runner 注释自己也说这是「单独记录的不变量」。→ **make run-wide**。
16. `onboardJournalEpochStableAcrossOnboardRestart`：查的是日志库在重启后的持久性，和 FP-IS-06 的 `onboardOutboxRowsSurviveOnboardRestart` 是一类。→ **move to FP-IS-06**。
17. `controlDatabaseFileReusedAcrossServerRestart`：「同一个库文件」只是其余持久性断言的前提。→ **make run-wide**。
18. `controlInboxRowsSurviveServerRestart`、`onboardOutboxRowsSurviveOnboardRestart`：FP-IS-06 三个向量讲的都是重试，重启后行还在只对得上各向量通用的 `persistenceCheckpoints`。→ **keep FP-IS-06**，注明向量贴合较弱。

**demand**

19. `riotPreCreateReconciliationObservesUnknownOnEveryLeg`、`riotUnknownIsAnExactAbsentAtObservation`、`riotUnknownStillCreatesExactlyOncePerLeg`、`riotUnknownResolvesToTheOrderItCreated`：内容是 RIoT 建单对账，属于 FP-IS-01（`EXACTLY_ONE_RIOT_ORDER`、`RIOT_ORDER_RECONCILIATION`），`CV-DESTINATION-UNLOAD-ALL-EMPTY` 覆盖不到。**更要紧的是**：这四条读的是 `$baseline`，也就是服务端启动之前、刚恢复出来的现场库（2026-08-29 的现场运行，protocol-v0.1.1）里的审计行。它们判的是当时那个构建的历史，不是被绑定的 ControlServer。→ **移出切片证据**，照 `fieldStoreProvenanceRecord` 的做法改为只记录不断言；如果要保留，**move to FP-IS-01**，并注明证据来自历史库。
20. `identicalResultReplayReturnsTheStoredAcknowledgement`：对的是 `CV-RELIABLE-RETRY-SAME-CONTENT`（`ACK_RETRY_WITHOUT_DUPLICATE_EFFECT`）。→ **move to FP-IS-06**。
21. `sameMessageIdWithDifferentContentIsRefused`：对的是 `CV-RELIABLE-RETRY-DIFFERENT-CONTENT`。→ **move to FP-IS-06**。
22. `sameAttemptAndGenerationUnderANewMessageIdIsRefused`：可以算 FP-IS-06 的 `IDEMPOTENT_ON_BUSINESS_KEY`，也可以算 FP-IS-04 的 `COMMIT_UNLOAD_ONCE`。→ **keep FP-IS-04**。
23. `alreadyCommittedAttemptRefusesASecondResult`：用的已提交尝试是现场库里第一条 `Committed` 行，代码没有限定 `operationType`，多半是取货装载而不是卸货（未核实）。→ **keep FP-IS-04**，建议限定为卸货行，或在注释里说明。
24. `preparedAttemptAcceptsItsFirstResult`：由合成对端报 `COMPLETED`，没有车载端，也不是 ALL_EMPTY 证空，向量车载端那一半（`UNLOAD_AUTHORIZED_SLOTS_ONLY`、`REPORT_FINAL_PHYSICAL_STATE`）没有覆盖到。→ **keep FP-IS-04**，注明只覆盖服务端一半。
25. `resultFromASupersededSessionGenerationIsRefused`：对的是 `CV-SESSION-RECONNECT-DURING-RECOVERY` 的 `SUPERSEDE_STALE_SESSION_GENERATION`，这个向量 FP-IS-00 与 FP-IS-05 共用。→ **keep FP-IS-05**。
26. `controlServerHostProcessWasActuallyReplaced`：只证明重启真的发生了，是前提，不是证据。→ **make run-wide**。
27. `acceptedDemandSurvivesTheHostRestart`、`vehicleDispatchLeaseSurvivesTheHostRestart`：FP-IS-05 的两个向量讲的是车载端离线安全收尾与重连，不涉及服务端进程重启后的持久性，其他切片也没有这样的向量。→ 由 FP-IS-05 负责人决定；默认 **keep FP-IS-05**，并注明「无向量对应、范围外补充证据」。

**journey**

28. FP-IS-03 的 `unknownResultReconcileSequenceMatchesVector`、`unknownReportedAsUnknownAndReplayedFromJournal`、`unknownNeverTreatedAsSuccess`、`reconciledFromReportedJournalBeforeReadiness`、`replayTouchesNoSlot`，以及 FP-IS-07 的 `recoveryUnknown*` 五条：两组都对 `CV-OPERATION-RESULT-UNKNOWN-RECONCILE`（FP-IS-03 与 FP-IS-07 共用）。两组是分开的场景运行、各有 L2 id，是有意的设计。→ **keep 两边**。
29. `manualChargingReturn*` / `eligibilityReevaluatedAfterReturn` 四条：`CV-MANUAL-CHARGING-RETURN` 同时在 FP-IS-07 与 FP-IS-13 的 `vectorIds` 里，归属表只认领了 FP-IS-07。另外，车载端的 `NEVER_CLEAR_HOLD_LOCALLY` 没有被任何一条断言覆盖。→ **keep FP-IS-07**；FP-IS-13 以后要认领，得用自己的断言名。
30. `correctionOnlyBeforeDepartureAndHoldsTheVehicle`：依据写的是 REQ-0237 / ADR-cross-0054，`CV-LOAD-CORRECTION` 里没有对应的产品断言。→ **keep FP-IS-02**，注明依据不是向量。
31. `loadClosedOverRealModbus`、`onboardOffersLoadCorrectionAfterCompletedLoad`、`onboardOffersLoadCancellationDuringLoad`、`onboardOffersLoadCancellationBeforeSublot`（本票新增）：判定文字不带向量的产品断言，前一条是真 Modbus 闭环的物理检查，后三条是 HMI 入口可用，也就是驱动场景的前提。→ **keep FP-IS-02**，注明只是支撑 finalState 或前提。

## 逐条表

### staged（`run-staged-g3.ps1`）

判定都在第 3333 行起的 `$assertionReport`，布尔量在它上面约 3045–3250 行计算。

| runner | 断言名 | 当前归属切片 | 依据向量 | 核实到的检查内容 | 疑点 |
| --- | --- | --- | --- | --- | --- |
| staged | `identityRejections` | 运行级 | 运行级前提，不对应向量 | `$probePass`：release、manifest、凭据任一不符的 SessionHello 都被 `SessionRejected`/`PROTOCOL_RELEASE_IDENTITY_MISMATCH` 拒绝；但 probe 的 status 同时包含心跳重复与冲突两项 | 见疑点 1：运行级混进了 FP-IS-06 的内容 |
| staged | `noMovementOrExternalSideEffects` | 运行级 | 运行级前提，不对应向量 | OrderIntents、AcceptedDemands、StationOperations 计数都是 0 | |
| staged | `secretScan` | 运行级 | 运行级前提，不对应向量 | 证据目录里没有出现凭据、恢复证明或治理凭据的明文 | |
| staged | `recoveryStateReportFirstAckDropReplay` | FP-IS-00 | `CV-SESSION-RECONNECT-DURING-RECOVERY` | 丢一次 RecoveryStateReport 的 ack，重连后以同一 messageId、同一载荷补发；两个以上连接和代次；会话仍 RecoveryRequired | 疑点 2：这个向量 FP-IS-05 也有 |
| staged | `recoveryStateReportFirstAckDropReplayOverPlaintext` | FP-IS-00 | `CV-SESSION-RECONNECT-DURING-RECOVERY` | 与上一条是同一个 `$replayPass` | 疑点 3：一个检查占两个名字 |
| staged | `recoverySessionAuthorisationBoundary` | FP-IS-00 | FP-IS-00 内没有能对上的向量；实际对 FP-IS-07 `CV-EXCEPTION-RESUME`（`OPEN_RECOVERY_SESSION_FOR_VERIFIED_ADMINISTRATOR`） | 6 个会话授权用例：认证失败被拒、未受理需求被拒、无需求可打开、requestId 重放幂等、每车只能开一个、requestId 内容冲突 | 疑点 4：应归 FP-IS-07 |
| staged | `recoveryActionsRefusedWithoutPersistedOperation` | FP-IS-00 | FP-IS-00 内没有；实际对 FP-IS-07（`EVERY_RECOVERY_ACTION_AUTHORIZED`） | 无需求时 RESUME/COMPENSATE/HANDOFF 被拒、scope 不符被拒，装载取消/修正/补偿在没有已受理需求时被拒 | 疑点 5：应归 FP-IS-07，并涉及 FP-IS-02 |
| staged | `hardwareRecoveryRecordScopeEnforced` | FP-IS-00 | 没有任何向量覆盖 HardwareRecoveryRecord | 记录被 RECORDED，scope 不符被 REJECTED/`RECOVERY_SCOPE_MISMATCH`，库里恰好 1 行 | 疑点 6：应归 FP-IS-07，没有向量 |
| staged | `recoveryCommandSurvivesMidFlightDisconnect` | FP-IS-00 | FP-IS-00 内没有；实际对 FP-IS-07 `CV-FORCED-MECHANICAL-RECOVERY`（也可算 FP-IS-06） | 代理吞掉 ForcedMechanicalRecoveryCommand 并断线，重连后同一 outbox 行在更高代次补发 | 疑点 7 |
| staged | `forcedRecoveryGenerationAdvancesMonotonically` | FP-IS-00 | FP-IS-00 内没有；实际对 FP-IS-07 `CV-FORCED-MECHANICAL-RECOVERY`（`FENCE_FORCED_RECOVERY_BY_GENERATION`） | 车辆强制恢复代数到 2；旧工作流 HistoricalOnly（代数 1），新工作流 RecoveryRequired（代数 2） | 疑点 8 |
| staged | `supersededGenerationResultIsHistoricalEvidenceOnly` | FP-IS-00 | FP-IS-00 内没有；实际对 FP-IS-07 `CV-FORCED-MECHANICAL-RECOVERY`（`REFUSE_STALE_FORCED_RECOVERY_GENERATION`） | 与上一条是同一个 `$recoveryGenerationPass`：旧代次的结果只留作历史证据 | 疑点 9：应归 FP-IS-07，并与上一条共用一个布尔值 |
| staged | `recoveryNeverReportsFalseCompletion` | FP-IS-00 | FP-IS-00 内没有；实际对 FP-IS-07 `CV-FORCED-MECHANICAL-RECOVERY` 的 finalState | 恢复会话仍 EXECUTING、无需求；没有关闭的会话，没有已对账的工作流；工作流不挂需求或尝试 | 疑点 10 |
| staged | `sameConnectionSameMessageIdSameContent` | FP-IS-06 | `CV-RELIABLE-RETRY-SAME-CONTENT` | 同一连接两次发出同一个 Heartbeat，两个 HeartbeatAck 逐字节相同；同时要求 `$probePass` | 依赖 identityRejections（疑点 1） |
| staged | `sameMessageIdDifferentContentStableConflict` | FP-IS-06 | `CV-RELIABLE-RETRY-DIFFERENT-CONTENT` | 同一 messageId、不同内容：同连接和新连接都被断开；同时要求 `$probePass` | 疑点 11：没查 `ProtocolProblem`/`MESSAGE_ID_CONTENT_CONFLICT` |
| staged | `businessMessageSameMessageIdSameContentReplay` | FP-IS-06 | `CV-RELIABLE-RETRY-SAME-CONTENT` | 4 种业务消息各重发一次，DurableAck 逐字节相同 | |
| staged | `businessMessageSameMessageIdDifferentContentStableConflict` | FP-IS-06 | `CV-RELIABLE-RETRY-DIFFERENT-CONTENT` | 4 种业务消息的内容冲突，两次都断开连接 | 疑点 11：同上，只查断连 |
| staged | `businessMessageAckDropInSessionReplay` | FP-IS-06 | `CV-RELIABLE-RETRY-SAME-CONTENT` | 丢掉 PreDepartureSafetyCheckResult 的 DurableAck 后同代次重发，拿到逐字节相同的存档 ack | |
| staged | `businessMessageDelayedDeliveryAccepted` | FP-IS-06 | 没有直接向量；最接近 `CV-RELIABLE-RETRY-SAME-CONTENT` | SlotOperationCommandRejected 延迟 900 ms 后仍被 DurableAck | 疑点 12 |
| staged | `businessMessageReorderedDeliveryAccepted` | FP-IS-06 | 没有直接向量；最接近 `CV-RELIABLE-RETRY-SAME-CONTENT` | 两条 SublotSubmitted 乱序到达，两条都被接受 | 疑点 12 |
| staged | `slotConfigurationActivationCarriesOneMessageIdOnly` | FP-IS-14 | `CV-SLOT-CONFIGURATION-ACTIVATION` | 一次激活不论发几遍，都只有一个 messageId | |
| staged | `slotConfigurationActivationReplayedByteForByteAfterAMidFlightDrop` | FP-IS-14 | `CV-SLOT-CONFIGURATION-ACTIVATION` | 至少发两次、分布在两个以上连接、载荷哈希唯一 | |
| staged | `slotConfigurationActivationPersistedBeforeItWasSent` | FP-IS-14 | `CV-SLOT-CONFIGURATION-ACTIVATION` | 库里激活行恰好 1 行，`commandMessageId` 就是发出的那个 | |
| staged | `slotConfigurationActivationResultReportedByTheVehicle` | FP-IS-14 | `CV-SLOT-CONFIGURATION-ACTIVATION` | 车报了结果，激活行为 ACTIVATED | |
| staged | `bothEndsComputedTheSameSlotConfigurationFingerprint` | FP-IS-14 | `CV-SLOT-CONFIGURATION-ACTIVATION` | ActiveSlotConfigurations 恰好 1 行，指纹与 activationId 都和激活行一致 | |
| staged | `onboardAlarmSnapshotPublishedOnTheFullHandshake` | FP-IS-15 | `CV-ONBOARD-ALARM-SNAPSHOT` | 至少发出 1 份 OnboardAlarmSnapshot | |
| staged | `onboardAlarmSnapshotAppliedAckOnEverySnapshot` | FP-IS-15 | `CV-ONBOARD-ALARM-SNAPSHOT` | SnapshotAppliedAck 与快照一一对应 | |
| staged | `onboardAlarmSnapshotNotRepublishedOnRecoveryResume` | FP-IS-15 | `CV-ONBOARD-ALARM-SNAPSHOT` | 每次完整握手都有自己的快照；非握手快照的告警内容与前一份不同；至少发生过一次恢复续连 | |
| staged | `onboardAlarmProjectionKeptOnlyTheLatestOfSeveralSnapshots` | FP-IS-15 | `CV-ONBOARD-ALARM-SNAPSHOT` | 两份以上快照、两个以上代次，投影只留 1 行且是最大代次 | |
| staged | `onboardAlarmProjectionIsASingletonPerVehicle` | FP-IS-15 | `CV-ONBOARD-ALARM-SNAPSHOT` | 投影行恰好 1 行 | |
| staged | `onboardAlarmProjectionCarriesTheGenerationItArrivedIn` | FP-IS-15 | `CV-ONBOARD-ALARM-SNAPSHOT` | 投影行的代次等于快照到达时的最大代次，sequence 至少为 1 | |

### restart（`run-staged-g3-restart.ps1`）

判定在第 1050 行的 `$assertions`，布尔量在 820–988 行计算。

| runner | 断言名 | 当前归属切片 | 依据向量 | 核实到的检查内容 | 疑点 |
| --- | --- | --- | --- | --- | --- |
| restart | `commitBindingSharedWithMainRunner` | 运行级 | 运行级前提，不对应向量 | 四个 commit 都是 40 位 hex，三个仓各 checkout 一次 | |
| restart | `runningControlServerReportsBoundBuildCommit` | 运行级 | 运行级前提，不对应向量 | 3 行 SessionHello 的 `serverBuildCommit` 都等于绑定值 | |
| restart | `runningOnboardReportsBoundBuildCommit` | 运行级 | 运行级前提，不对应向量 | 3 行的 `onboardBuildCommit` 与 `onboardInstanceId` 都符合绑定 | |
| restart | `protocolIdentityBoundToRelease` | 运行级 | 运行级前提，不对应向量 | `/version` 的 protocolCommit 等于绑定值；会话恢复行恰好 1 行且 protocolCommit 相同 | 小疑点：「恰好 1 行」与 `sessionRecoveryRowStaysASingletonPerAgv` 重叠（疑点 R2，不必改） |
| restart | `noPeerExitedUnexpectedly` | 运行级 | 运行级前提，不对应向量 | 三个阶段都没有对端意外退出 | |
| restart | `onboardHostProcessReplacedOnlyInPhaseTwo` | 运行级 | 运行级前提，不对应向量 | 车载端进程 ID 只在阶段 2 变化，旧进程已退出 | |
| restart | `controlServerHostProcessReplacedOnlyInPhaseThree` | 运行级 | 运行级前提，不对应向量 | 服务端进程 ID 只在阶段 3 变化，旧进程已退出 | |
| restart | `noMovementOrExternalSideEffects` | 运行级 | 运行级前提，不对应向量 | 副作用计数全为 0，**并且** `/health` 返回 503、readiness 为 RecoveryRequired | 疑点 13：readiness 属于切片证据 |
| restart | `secretScan` | 运行级 | 运行级前提，不对应向量 | 证据里没有凭据明文 | |
| restart | `freshDatabaseStartsAtGenerationOne` | FP-IS-00 | `CV-SESSION-RECOVERY-HAPPY`（SessionGeneration 起点） | 阶段 1 的 sessionGeneration 为 1 | |
| restart | `onboardRestartAdvancesGenerationByExactlyOne` | FP-IS-00 | `CV-SESSION-RECONNECT-DURING-RECOVERY` | 阶段 2 的代次等于阶段 1 加一 | 疑点 14：这个向量 FP-IS-05 也有 |
| restart | `controlServerRestartAdvancesGenerationByExactlyOne` | FP-IS-00 | `CV-SESSION-RECONNECT-DURING-RECOVERY` | 阶段 3 的代次等于阶段 2 加一 | 疑点 14 |
| restart | `sessionGenerationStableWithinEveryPhase` | FP-IS-00 | `CV-SESSION-RECONNECT-DURING-RECOVERY`（`NEVER_TWO_ACTIVE_SESSIONS`） | 每个阶段稳定窗口内所有采样的代次都不变 | |
| restart | `serverSessionIdentityIsScopedPerConnection` | FP-IS-00 | 没有向量对应 | 3 个 `serverInstanceId` 非空且各不相同 | 疑点 15：应改为运行级 |
| restart | `recoveryReportIdentityAgreesAcrossPeers` | FP-IS-00 | `CV-SESSION-RECONNECT-DURING-RECOVERY`（`RESUBMIT_RECOVERY_STATE_AFTER_RECONNECT`） | 车载端 outbox 与服务端 inbox 各有 3 条 RecoveryStateReport，messageId 集合相同，且都已被 ack | |
| restart | `sessionRecoveryRowStaysASingletonPerAgv` | FP-IS-00 | `CV-SESSION-RECONNECT-DURING-RECOVERY`（`NEVER_TWO_ACTIVE_SESSIONS`） | 会话恢复行恰好 1 行、代次为 3，连接恢复行不超过 1 行 | |
| restart | `onboardJournalEpochStableAcrossOnboardRestart` | FP-IS-00 | FP-IS-00 内没有；最接近 FP-IS-06 的持久性检查点 | 日志库的 journalEpoch 与文件创建时间在车载端重启前后相同 | 疑点 16：应归 FP-IS-06 |
| restart | `controlDatabaseFileReusedAcrossServerRestart` | FP-IS-06 | 没有向量对应（只是前提） | 服务端重启前后库文件创建时间相同，长度没有变小 | 疑点 17：应改为运行级 |
| restart | `controlInboxRowsSurviveServerRestart` | FP-IS-06 | 各向量通用的 `persistenceCheckpoints`（durable-before-ack）；三个 FP-IS-06 向量都不直接讲这件事 | 重启前的 inbox 行按 messageId/type/hash 全部保留，行数增加 | 疑点 18 |
| restart | `onboardOutboxRowsSurviveOnboardRestart` | FP-IS-06 | 同上（durable-before-send） | 重启前的 outbox 行全部保留，行数增加 | 疑点 18 |
| restart | `onboardAlarmProjectionAdoptedTheRestartedVehiclesSnapshot` | FP-IS-15 | `CV-ONBOARD-ALARM-SNAPSHOT` | 车载端重启后投影 1 行，代次等于阶段 2 的代次 | |
| restart | `onboardAlarmProjectionNeverRegressedToAnEarlierGeneration` | FP-IS-15 | `CV-ONBOARD-ALARM-SNAPSHOT` | 结束时投影仍只有 1 行，代次没有回退 | |
| restart | `slotConfigurationActivationAcceptedWhileTheVehicleMatched` | FP-IS-14 | `CV-SLOT-CONFIGURATION-ACTIVATION` | 配置一致时激活成功，ACTIVATED 1 行，active 行指向它 | |
| restart | `slotConfigurationActivationRefusedAfterTheVehicleConfigurationChanged` | FP-IS-14 | `CV-SLOT-CONFIGURATION-ACTIVATION`（stableErrorCode） | 改动配置后再激活：FAILED 1 行，结果里有 `SLOT_CONFIGURATION_FINGERPRINT_MISMATCH` | |
| restart | `aRefusedActivationLeftTheActiveConfigurationUntouched` | FP-IS-14 | `CV-SLOT-CONFIGURATION-ACTIVATION` | 被拒之后 active 行的指纹与版本没有变 | |

### demand（`run-demand-bearing-g3-vectors.ps1`）

判定在第 712 行的 `$assertions`，布尔量在 495–641 行计算，结果面由 `run-staged-g3.ps1` 的 `RunDemandBearingResultProbeAsync` 驱动。

| runner | 断言名 | 当前归属切片 | 依据向量 | 核实到的检查内容 | 疑点 |
| --- | --- | --- | --- | --- | --- |
| demand | `protocolAndBuildIdentityBoundToTheSharedBinding` | 运行级 | 运行级前提，不对应向量 | `/version` 的 protocolCommit 等于绑定值、tag 为 `protocol-v1.0.0`，probe 报的 serverBuildCommit 等于绑定值，现场库已读到 | |
| demand | `noMovementOrExternalSideEffects` | 运行级 | 运行级前提，不对应向量 | OrderIntents、RiotDispatchAuditEvents、AcceptedDemands、VehicleDispatchLeases、StationOperations 的计数与基线相同 | |
| demand | `listenersReleased` | 运行级 | 运行级前提，不对应向量 | 结束后控制端口与健康端口都没有监听 | |
| demand | `secretScan` | 运行级 | 运行级前提，不对应向量 | 证据里没有凭据明文 | |
| demand | `riotPreCreateReconciliationObservesUnknownOnEveryLeg` | FP-IS-04 | FP-IS-04 内没有；内容对 FP-IS-01 `CV-DEMAND-ACCEPT-TO-PICKUP`（`EXACTLY_ONE_RIOT_ORDER`） | 从**恢复出来的现场库**（服务端启动前）读审计行：每段都有 PRE_CREATE_RECONCILIATION/UNKNOWN、eligibilityBasis 正确、sequence 为 1 | 疑点 19：归错切片，且判的是历史构建 |
| demand | `riotUnknownIsAnExactAbsentAtObservation` | FP-IS-04 | 同上 | 同样读现场库：UNKNOWN 行 resultPresent 为 0，没有 returnedOrderId | 疑点 19 |
| demand | `riotUnknownStillCreatesExactlyOncePerLeg` | FP-IS-04 | 同上 | 同样读现场库：每段恰好走完 5 个阶段、sequence 为 1..5 | 疑点 19 |
| demand | `riotUnknownResolvesToTheOrderItCreated` | FP-IS-04 | 同上 | 同样读现场库：CREATE_RESPONSE、POST_CREATE、OrderIntent 三处 orderId 一致，状态为 CONFIRMED | 疑点 19 |
| demand | `preparedAttemptAcceptsItsFirstResult` | FP-IS-04 | `CV-DESTINATION-UNLOAD-ALL-EMPTY` | 合成对端对现场库里唯一的 Prepared 尝试报 `COMPLETED`，拿到对应的 DurableAck | 疑点 24：只覆盖服务端一半，不是 ALL_EMPTY |
| demand | `identicalResultReplayReturnsTheStoredAcknowledgement` | FP-IS-04 | FP-IS-04 内较弱；实际对 FP-IS-06 `CV-RELIABLE-RETRY-SAME-CONTENT` | 同一结果逐字节重发，两个 ack 相同 | 疑点 20：应归 FP-IS-06 |
| demand | `sameMessageIdWithDifferentContentIsRefused` | FP-IS-04 | 实际对 FP-IS-06 `CV-RELIABLE-RETRY-DIFFERENT-CONTENT` | 同一 messageId、不同内容，被拒并断开 | 疑点 21：应归 FP-IS-06 |
| demand | `sameAttemptAndGenerationUnderANewMessageIdIsRefused` | FP-IS-04 | `CV-DESTINATION-UNLOAD-ALL-EMPTY`（`COMMIT_UNLOAD_ONCE`），也可算 FP-IS-06 `IDEMPOTENT_ON_BUSINESS_KEY` | 同一尝试、同一代次换一个新 messageId，被拒 | 疑点 22：两个切片都说得通 |
| demand | `alreadyCommittedAttemptRefusesASecondResult` | FP-IS-04 | `CV-DESTINATION-UNLOAD-ALL-EMPTY`（`COMMIT_UNLOAD_ONCE`） | 对现场库里第一条 Committed 尝试再报结果，被拒 | 疑点 23：那一条多半不是卸货（未核实） |
| demand | `replayedResultWasNotProcessedTwice` | FP-IS-04 | `CV-DESTINATION-UNLOAD-ALL-EMPTY`（`COMMIT_UNLOAD_ONCE`） | 尝试变为 Committed，结果 1 行（`historicalOnly=0`），inbox 1 行 | |
| demand | `unloadResultClosedTheDemandAtomically` | FP-IS-04 | `CV-DESTINATION-UNLOAD-ALL-EMPTY`（`RECONCILE_EMPTY_FINAL_STATE`） | UnloadBatches、StopClosures、TransportDemandCompletions 各从 0 变为 1 | |
| demand | `resultFromASupersededSessionGenerationIsRefused` | FP-IS-05 | `CV-SESSION-RECONNECT-DURING-RECOVERY`（`SUPERSEDE_STALE_SESSION_GENERATION`） | 带旧代次的结果被拒并断开 | 疑点 25：这个向量 FP-IS-00 也有 |
| demand | `controlServerHostProcessWasActuallyReplaced` | FP-IS-05 | 没有向量对应（只是前提） | 两次宿主进程 ID 不同，第一个在重启前已退出 | 疑点 26：应改为运行级 |
| demand | `acceptedDemandSurvivesTheHostRestart` | FP-IS-05 | FP-IS-05 两个向量都不涉及服务端进程重启 | 库文件相同，AcceptedDemands 行在重启前后逐列保留 | 疑点 27：没有向量对应 |
| demand | `vehicleDispatchLeaseSurvivesTheHostRestart` | FP-IS-05 | 同上 | VehicleDispatchLeases 行在重启前后逐列保留 | 疑点 27 |
| demand | `restartedHostServesTheSameStore` | FP-IS-05 | `CV-SESSION-RECONNECT-DURING-RECOVERY`（代次在旧库基础上继续） | 重启后握手的代次等于旧库代次加一，build 与 protocol 都等于绑定值 | |

### journey（`run-journey-g3.ps1`）

L2 id 与断言名的对应在 `$scenarioAssertions`（第 74–185 行）；运行级断言在第 427–479 行。

| runner | 断言名 | 当前归属切片 | 依据向量 | 核实到的检查内容 | 疑点 |
| --- | --- | --- | --- | --- | --- |
| journey | `exactClonesAtTheSharedCommitBinding` | 运行级 | 运行级前提，不对应向量 | `$clonesVerified`：四个 clone 都在绑定 commit 上 | |
| journey | `protocolReleaseTagResolvesToTheBoundCommit` | 运行级 | 运行级前提，不对应向量 | clone 已核实，且协议 tag 存在并指向绑定 commit | |
| journey | `everyScenarioRanTheBoundControlServer` | 运行级 | 运行级前提，不对应向量 | 每个场景 `assertions.json` 里的 controlServerCommit 都等于绑定值 | |
| journey | `everyScenarioReportedTheBoundProtocolRelease` | 运行级 | 运行级前提，不对应向量 | 每个场景的 tag、commit、manifestSha256、approvalStatus 都与预期相符 | |
| journey | `everyScenarioRanTheRealOnboardRig` | 运行级 | 运行级前提，不对应向量 | 每个场景的 rig 都是 `RealOnboard` | |
| journey | `everyScenarioPublishedThePeersAtTheBinding` | 运行级 | 运行级前提，不对应向量 | timeline 注记里 onboard-hmi 与 slots-simulator 都在绑定 commit 发布 | |
| journey | `noScenarioAbortedBeforeItsJudgments` | 运行级 | 运行级前提，不对应向量 | 每个场景的 failureReason 都为空 | |
| journey | `secretScan` | 运行级 | 运行级前提，不对应向量 | 证据里没有环境中真实秘密的值 | |
| journey | `exactlyOneAcceptedDemandSnapshot`（G3-01-01） | FP-IS-01 | `CV-DEMAND-ACCEPT-TO-PICKUP` | AcceptedDemands 里这条需求恰好 1 行（`EXACTLY_ONE_ACCEPTED_DEMAND_SNAPSHOT`） | |
| journey | `exactlyOneToPickupIntent`（G3-01-02） | FP-IS-01 | `CV-DEMAND-ACCEPT-TO-PICKUP` | 取货意图只有 1 条且已确认 | |
| journey | `exactlyOneRiotOrder`（G3-01-03） | FP-IS-01 | `CV-DEMAND-ACCEPT-TO-PICKUP` | RIoT 侧只有 1 张单，就是这条意图的那张 | |
| journey | `planPublishedAndAcknowledgedBeforePickupArrival`（G3-01-04） | FP-IS-01 | `CV-DEMAND-ACCEPT-TO-PICKUP`（第 3–4 步） | 到站前恰好发出 1 份计划并被确认，工作清单还没发 | |
| journey | `noSlotOperationBeforePickupArrival`（G3-01-05） | FP-IS-01 | `CV-DEMAND-ACCEPT-TO-PICKUP`（finalState `NO_SLOT_OPERATION_STARTED`） | 到站前没有仓位操作，也没有录入请求或仓位命令 | |
| journey | `trustedPickupArrivalAdopted`（G3-01-06） | FP-IS-01 | `CV-DEMAND-ACCEPT-TO-PICKUP`（`TRUSTED_PICKUP_ARRIVAL`） | 旅程进入 AwaitingSublot | |
| journey | `demandAcceptanceSnapshotSequenceMatchesVector`（G3-01-07） | FP-IS-01 | `CV-DEMAND-ACCEPT-TO-PICKUP` orderedExpectedMessages | 计划、工作清单、下一 revision 的计划三份都被确认，没有一份作废 | |
| journey | `onboardAppliedTheCommittedProjection`（G3-01-08） | FP-IS-01 | `CV-DEMAND-ACCEPT-TO-PICKUP`（`DISPLAY_COMMITTED_DEMAND_JOURNEY` / `DISPLAY_CURRENT_STOP`） | 车载端日志库采纳的消息号与 revision 就是服务端提交的那两份 | |
| journey | `finalStateOneDemandOneOrderAtPickupNoSlotOperation`（G3-01-09） | FP-IS-01 | `CV-DEMAND-ACCEPT-TO-PICKUP` finalState | 全程一条需求、一条意图、一张单，没有仓位操作 | |
| journey | `onboardNeverDiscoversSelectsOrBindsDemand`（G3-01-10） | FP-IS-01 | `CV-DEMAND-ACCEPT-TO-PICKUP`（`NEVER_DISCOVER_SELECT_OR_BIND_DEMAND`） | 车载端没有发过需求发现、选择或绑定类消息 | |
| journey | `sublotBoundToOperationSession`（G3-02-01） | FP-IS-02 | `CV-PICKUP-SUBLOT-LOAD`（`BIND_SUBLOT_TO_OPERATION_SESSION` / `SUBMIT_SCANNED_SUBLOT`） | UIA 录入的 sublot 只提交一次，operationSessionId 相符，只建一笔装载 | |
| journey | `slotSetAuthorizedOnce`（G3-02-02） | FP-IS-02 | `CV-PICKUP-SUBLOT-LOAD`（`AUTHORIZE_SLOT_SET_ONCE`） | 恰好 1 条 SlotOperationCommand，仓位就是服务端选定的那些 | |
| journey | `loadOnlyAuthorizedSlotsEachUnlockedOnce`（G3-02-03） | FP-IS-02 | `CV-PICKUP-SUBLOT-LOAD`（`LOAD_ONLY_AUTHORIZED_SLOTS`） | 开锁合起来恰好是授权集合、没有重复，其余仓始终关着 | |
| journey | `loadClosedOverRealModbus`（G3-02-04） | FP-IS-02 | `CV-PICKUP-SUBLOT-LOAD` finalState（`NO_UNPROVEN_STATE`），没有对应的产品断言 | 等待操作员时仓门确实开着；装完关门、有货、锁上、开锁输出复位 | 疑点 31：只是物理支撑 |
| journey | `pickupSublotLoadSequenceMatchesVector`（G3-02-05） | FP-IS-02 | `CV-PICKUP-SUBLOT-LOAD` orderedExpectedMessages | 五条消息各一次，服务端发的两条都被确认 | |
| journey | `loadOutcomeCommittedOnce`（G3-02-06） | FP-IS-02 | `CV-PICKUP-SUBLOT-LOAD`（`RECONCILE_LOAD_OUTCOME` 属于切片 ownerResponsibilities） | 操作 Committed，结果 1 份且为 COMPLETED | |
| journey | `onboardOffersLoadCorrectionAfterCompletedLoad`（G3-02-07） | FP-IS-02 | `CV-LOAD-CORRECTION`（前提，没有对应的产品断言） | 装完后 HMI 上出现可用的「修正装货」入口 | 疑点 31 |
| journey | `loadCorrectionSequenceMatchesVector`（G3-02-08） | FP-IS-02 | `CV-LOAD-CORRECTION` orderedExpectedMessages | 四条消息各一次，命令就是工作流绑定的那一条 | |
| journey | `correctionAuthorizedAgainstCommittedSet`（G3-02-09） | FP-IS-02 | `CV-LOAD-CORRECTION`（`AUTHORIZE_CORRECTION_AGAINST_COMMITTED_SET`） | 命令的仓位就是已提交的集合，工作流为 Reconciled | |
| journey | `neverCorrectWithoutAuthorization`（G3-02-10） | FP-IS-02 | `CV-LOAD-CORRECTION`（`NEVER_CORRECT_WITHOUT_AUTHORIZATION`） | 修正进度都晚于命令，只开授权仓、每仓一次 | |
| journey | `correctedSlotOutcomeReported`（G3-02-11） | FP-IS-02 | `CV-LOAD-CORRECTION`（`REPORT_CORRECTED_SLOT_OUTCOME`） | 报 COMPLETED，每个授权仓有货、锁上、输出复位 | |
| journey | `correctionCreatesNoDuplicateCommit`（G3-02-12） | FP-IS-02 | `CV-LOAD-CORRECTION` finalState | 仍只有一笔装载、一份结果、一条命令 | |
| journey | `correctionOnlyBeforeDepartureAndHoldsTheVehicle`（G3-02-13） | FP-IS-02 | `CV-LOAD-CORRECTION` 里没有对应的产品断言，依据是 REQ-0237 / ADR-cross-0054 | 修正时旅程在 AwaitingStationDeparture，收敛后等满离站期限才建单出发 | 疑点 30 |
| journey | `onboardOffersLoadCancellationDuringLoad`（G3-02-21） | FP-IS-02 | `CV-LOAD-CANCELLATION-ALL-EMPTY`（前提） | 装载进行中 HMI 上出现可用的「取消装货」入口 | 疑点 31 |
| journey | `cancellationAuthorizedExplicitly`（G3-02-22） | FP-IS-02 | `CV-LOAD-CANCELLATION-ALL-EMPTY`（`AUTHORIZE_CANCELLATION_EXPLICITLY`） | 应答为 AUTHORIZED，指向这笔装载，范围是全部授权仓 | |
| journey | `loadCancellationSequenceMatchesVector`（G3-02-23） | FP-IS-02 | `CV-LOAD-CANCELLATION-ALL-EMPTY` orderedExpectedMessages（`NEVER_CANCEL_UNILATERALLY`） | 四条消息各一次，车载端没有在授权之前报结果 | |
| journey | `cancellationProvesEmptyWithoutUnlocking`（G3-02-24） | FP-IS-02 | `CV-LOAD-CANCELLATION-ALL-EMPTY` | 取消之后没有再开任何仓 | |
| journey | `allSlotsProvenEmpty`（G3-02-25） | FP-IS-02 | `CV-LOAD-CANCELLATION-ALL-EMPTY`（`PROVE_ALL_SLOTS_EMPTY`） | ALL_EMPTY，每仓空、锁上，与模拟器一致 | |
| journey | `cancellationReconciledToEmptyFinalState`（G3-02-26） | FP-IS-02 | `CV-LOAD-CANCELLATION-ALL-EMPTY`（`RECONCILE_EMPTY_FINAL_STATE`） | 工作流 Reconciled，需求与装载 Cancelled，租约释放 | |
| journey | `finalStateSurvivesLateLoadResult`（G3-02-27） | FP-IS-02 | `CV-LOAD-CANCELLATION-ALL-EMPTY` finalState | 原装载的迟到结果没有把收敛状态改回去，也没有落到 RecoveryRequired | |
| journey | `onboardOffersLoadCancellationBeforeSublot`（G3-02-31，本票新增） | FP-IS-02 | `CV-LOAD-CANCELLATION-BEFORE-LOAD`（前提） | 录入请求挂着、还没录入子批时，HMI 上出现可用的「取消装货」入口 | 疑点 31 |
| journey | `beforeLoadCancellationAuthorizedWithoutSlotOperation`（G3-02-32，本票新增） | FP-IS-02 | `CV-LOAD-CANCELLATION-BEFORE-LOAD`（`AUTHORIZE_CANCELLATION_WITHOUT_SLOT_OPERATION`） | 请求不带 attemptId，应答 AUTHORIZED，slots 为空 | |
| journey | `beforeLoadCancellationSequenceMatchesVector`（G3-02-33，本票新增） | FP-IS-02 | `CV-LOAD-CANCELLATION-BEFORE-LOAD` orderedExpectedMessages | 四条消息各一次，没有在授权之前报结果 | |
| journey | `beforeLoadCancellationReportedAllEmptyWithoutSlotIo`（G3-02-34，本票新增） | FP-IS-02 | `CV-LOAD-CANCELLATION-BEFORE-LOAD`（`REPORT_ALL_EMPTY_WITHOUT_SLOT_IO`） | ALL_EMPTY 不带逐仓条目，没有 OperationProgress，模拟器各仓与到站前相同 | |
| journey | `beforeLoadCancellationTerminatedOnlyOnTheResult`（G3-02-35，本票新增） | FP-IS-02 | `CV-LOAD-CANCELLATION-BEFORE-LOAD`（`TERMINATE_ONLY_ON_ALL_EMPTY_RESULT`） | 收到 ALL_EMPTY 之后才终结，租约释放不早于收到结果 | |
| journey | `beforeLoadCancellationLeftNoSlotCommandOrDoorMovement`（G3-02-36，本票新增） | FP-IS-02 | `CV-LOAD-CANCELLATION-BEFORE-LOAD` finalState | 0 笔仓位操作、0 条命令、0 条录入提交，只有 1 张取货单 | |
| journey | `sublotRejectedSequenceMatchesVector`（G3-02-41，本票新增） | FP-IS-02 | `CV-SUBLOT-REJECTED-AFTER-ENTRY` orderedExpectedMessages | Entry、Submitted、Rejected 三条，correlationId 就是那条提交 | |
| journey | `sublotRevalidatedAfterEntry`（G3-02-42，本票新增） | FP-IS-02 | `CV-SUBLOT-REJECTED-AFTER-ENTRY`（`REVALIDATE_SUBLOT_AFTER_ENTRY`） | 改容量对照后重算，以 `EXPECTED_BASKET_COUNT_MISMATCH` 拒收 | |
| journey | `neverUnlockOnRejectedEntry`（G3-02-43，本票新增） | FP-IS-02 | `CV-SUBLOT-REJECTED-AFTER-ENTRY`（`NEVER_UNLOCK_ON_REJECTED_ENTRY`） | 0 笔操作、0 条命令，各仓与到站时相同，预留不变 | |
| journey | `onboardDisplaysServerRejectionReason`（G3-02-44，本票新增） | FP-IS-02 | `CV-SUBLOT-REJECTED-AFTER-ENTRY`（`DISPLAY_SERVER_REJECTION_REASON`） | UIA 树里有 SublotRejectionReason，ItemStatus 是原因码 | |
| journey | `entryKeptOpenForRescan`（G3-02-45，本票新增） | FP-IS-02 | `CV-SUBLOT-REJECTED-AFTER-ENTRY`（`KEEP_ENTRY_OPEN_FOR_RESCAN`） | 录入框仍可用，服务端仍在等录入 | |
| journey | `rescanAfterRestoredDataLoadsNormally`（G3-02-46，本票新增） | FP-IS-02 | `CV-SUBLOT-REJECTED-AFTER-ENTRY` 之后的步骤（同一切片的 `CV-PICKUP-SUBLOT-LOAD`） | 重扫是一条新提交，按冻结的篮数装货，装载 Committed，拒收提示已撤下 | |
| journey | `rejectionLeavesNoDuplicateCommit`（G3-02-47，本票新增） | FP-IS-02 | `CV-SUBLOT-REJECTED-AFTER-ENTRY` finalState | 1 条拒收、1 笔操作、1 条命令、1 份结果，只有 1 张单 | |
| journey | `predepartureExpirySequenceMatchesVector`（G3-03-01） | FP-IS-03 | `CV-PREDEPARTURE-SAFETY-EXPIRES` orderedExpectedMessages 与 stableErrorCode | 检查、答复、状态变化，然后 `ProtocolProblem(PREDEPARTURE_CHECK_EXPIRED)` | |
| journey | `neverDepartOnExpiredCheck`（G3-03-02） | FP-IS-03 | `CV-PREDEPARTURE-SAFETY-EXPIRES`（`NEVER_DEPART_ON_EXPIRED_CHECK`） | 去关卡的单晚于重问那次的 SAFE 答复，旅程用的是重问那份 | |
| journey | `checkExpiresOnSafetyStateChange`（G3-03-03） | FP-IS-03 | `CV-PREDEPARTURE-SAFETY-EXPIRES`（`EXPIRE_CHECK_ON_SAFETY_STATE_CHANGE`） | 过期那次的 outbox 行已作废，重问是新身份 | |
| journey | `safetyStateChangeReportedPromptly`（G3-03-04） | FP-IS-03 | `CV-PREDEPARTURE-SAFETY-EXPIRES`（`REPORT_SAFETY_STATE_CHANGE_PROMPTLY`） | 5 秒内报告不安全，5 秒内报告恢复安全 | |
| journey | `checkAskedAgainAfterExpiry`（G3-03-05） | FP-IS-03 | `CV-PREDEPARTURE-SAFETY-EXPIRES`（`REREQUEST_CHECK_AFTER_EXPIRY`） | 重问得到 SAFE，关卡意图与关卡单各恰好一个 | |
| journey | `expiredCheckRefusalKeepsTheSession`（G3-03-06） | FP-IS-03 | `CV-PREDEPARTURE-SAFETY-EXPIRES` finalState（readiness `UNCHANGED_OR_SPECIFIED_BY_VECTOR`） | 会话代次全程不变，结束时仍 Ready | |
| journey | `expiryLeavesNoDuplicateCommitOrUnprovenState`（G3-03-07） | FP-IS-03 | `CV-PREDEPARTURE-SAFETY-EXPIRES` finalState | 一笔装载 Committed，锁反馈已恢复 | |
| journey | `unknownResultReconcileSequenceMatchesVector`（G3-03-08） | FP-IS-03 | `CV-OPERATION-RESULT-UNKNOWN-RECONCILE` orderedExpectedMessages | 结果、ack、恢复报告、同一 messageId 补发、ack | 疑点 28：这个向量 FP-IS-07 也有 |
| journey | `unknownReportedAsUnknownAndReplayedFromJournal`（G3-03-09） | FP-IS-03 | 同上（`REPORT_UNKNOWN_AS_UNKNOWN` / `REPLAY_RESULT_ON_RECONNECT`） | 结果为 UNKNOWN，出现在 pendingResults 里，补发逐字段相同 | 疑点 28 |
| journey | `unknownNeverTreatedAsSuccess`（G3-03-10） | FP-IS-03 | 同上（`NEVER_TREAT_UNKNOWN_AS_SUCCESS`） | 只有一行 UNKNOWN，装载仍 RecoveryRequired，旅程 Blocked | 疑点 28 |
| journey | `reconciledFromReportedJournalBeforeReadiness`（G3-03-11） | FP-IS-03 | 同上（`RECONCILE_FROM_REPORTED_JOURNAL`） | 新代次大于旧代次，待结清列表已清空，会话仍 RecoveryRequired | 疑点 28 |
| journey | `replayTouchesNoSlot`（G3-03-12） | FP-IS-03 | 同上（finalState） | 重启后没有再开锁，仓位关、空、锁上 | 疑点 28 |
| journey | `recoveryUnknownResultSequenceMatchesVector`（G3-07-01） | FP-IS-07 | `CV-OPERATION-RESULT-UNKNOWN-RECONCILE` orderedExpectedMessages | 与 G3-03-08 同一段流程，在另一个场景里另跑一次 | 疑点 28：这个向量 FP-IS-03 也有 |
| journey | `recoveryUnknownReportedAsUnknownAndReplayedFromJournal`（G3-07-02） | FP-IS-07 | 同上 | UNKNOWN、从日志补发、逐字段相同 | 疑点 28 |
| journey | `recoveryUnknownNeverTreatedAsSuccess`（G3-07-03） | FP-IS-07 | 同上 | 只有一行 UNKNOWN，装载 RecoveryRequired，旅程 Blocked | 疑点 28 |
| journey | `recoveryUnknownReconciledBeforeReadiness`（G3-07-04） | FP-IS-07 | 同上 | 按日志对账，不提前就绪 | 疑点 28 |
| journey | `recoveryUnknownReplayTouchesNoSlot`（G3-07-05） | FP-IS-07 | 同上 | 补发没有碰物理 | 疑点 28 |
| journey | `exceptionResumeSequenceMatchesVector`（G3-07-06） | FP-IS-07 | `CV-EXCEPTION-RESUME` orderedExpectedMessages | 会话请求、打开、RESUME 动作、接受、ResumeCommand、结果 | |
| journey | `recoverySessionOpenedForVerifiedAdministrator`（G3-07-07） | FP-IS-07 | `CV-EXCEPTION-RESUME`（`OPEN_RECOVERY_SESSION_FOR_VERIFIED_ADMINISTRATOR`） | 会话记下管理员与 MAINTENANCE_ADMINISTRATOR 角色 | |
| journey | `resumeOnlyTheAuthorizedScope`（G3-07-08） | FP-IS-07 | `CV-EXCEPTION-RESUME`（`AUTHORIZE_RESUME_SCOPE` / `RESUME_ONLY_AUTHORIZED_SCOPE`） | 命令指向原尝试与原仓，只重开这一个仓一次 | |
| journey | `resumedOutcomeReportedAndSupersedesUnknown`（G3-07-09） | FP-IS-07 | `CV-EXCEPTION-RESUME`（`REPORT_RESUMED_OUTCOME`） | COMPLETED 替换了那份 UNKNOWN | |
| journey | `resumeReconciledWithOneCommit`（G3-07-10） | FP-IS-07 | `CV-EXCEPTION-RESUME` finalState | 工作流 Reconciled，会话 CLOSED，装载 Committed 且只有一笔 | |
| journey | `resumeFinalPhysicalStateProven`（G3-07-11） | FP-IS-07 | `CV-EXCEPTION-RESUME` finalState（`NO_UNPROVEN_STATE`） | 仓关门、有货、锁上，与所报的 COMPLETED 一致 | |
| journey | `exceptionCompensateSequenceMatchesVector`（G3-07-21） | FP-IS-07 | `CV-EXCEPTION-COMPENSATE` orderedExpectedMessages | 七条消息各一次 | |
| journey | `compensationAuthorizedAgainstRecoverySession`（G3-07-22） | FP-IS-07 | `CV-EXCEPTION-COMPENSATE`（`AUTHORIZE_COMPENSATION_AGAINST_RECOVERY_SESSION`） | 工作流挂在这个恢复会话上，命令是它绑定的那一条 | |
| journey | `compensationExecutedOnceWithoutUnlocking`（G3-07-23） | FP-IS-07 | `CV-EXCEPTION-COMPENSATE`（`EXECUTE_COMPENSATION_ONCE`） | 一条命令、一份结果，之后没有开锁 | |
| journey | `compensatedSlotStateReported`（G3-07-24） | FP-IS-07 | `CV-EXCEPTION-COMPENSATE`（`REPORT_COMPENSATED_SLOT_STATE`） | ALL_EMPTY，与模拟器一致 | |
| journey | `compensationReconciledWithoutDuplicateCommit`（G3-07-25） | FP-IS-07 | `CV-EXCEPTION-COMPENSATE` finalState | 工作流 Reconciled，会话 CLOSED，只有取货那一张单 | |
| journey | `faultCargoHandoffSequenceMatchesVector`（G3-07-31） | FP-IS-07 | `CV-FAULT-CARGO-HANDOFF` orderedExpectedMessages | 四条消息各一次 | |
| journey | `faultCargoHandoffRecorded`（G3-07-32） | FP-IS-07 | `CV-FAULT-CARGO-HANDOFF`（`RECORD_FAULT_CARGO_HANDOFF`） | 工作流带交接号，HANDED_OFF，Reconciled | |
| journey | `handoffOnlyOnAuthorizedCommand`（G3-07-33） | FP-IS-07 | `CV-FAULT-CARGO-HANDOFF`（`HANDOFF_ONLY_ON_AUTHORIZED_COMMAND`） | 三处交接号相同，没有额外开锁 | |
| journey | `handoffOutcomeReported`（G3-07-34） | FP-IS-07 | `CV-FAULT-CARGO-HANDOFF`（`REPORT_HANDOFF_OUTCOME`） | HANDED_OFF，授权仓空、锁上 | |
| journey | `handoffTerminatesWithoutDuplicateCommit`（G3-07-35） | FP-IS-07 | `CV-FAULT-CARGO-HANDOFF` finalState | 需求与装载 Cancelled，以 TERMINATED_BY_FAULT_CARGO_HANDOFF 收尾 | |
| journey | `forcedMechanicalRecoverySequenceMatchesVector`（G3-07-41） | FP-IS-07 | `CV-FORCED-MECHANICAL-RECOVERY` orderedExpectedMessages | 四条消息各一次 | |
| journey | `forcedRecoveryFencedByGeneration`（G3-07-42） | FP-IS-07 | `CV-FORCED-MECHANICAL-RECOVERY`（`FENCE_FORCED_RECOVERY_BY_GENERATION`） | 强制恢复代数恰好加一，工作流与命令都签在新代数下 | |
| journey | `forcedRecoveryOutcomeReportedWithoutProof`（G3-07-43） | FP-IS-07 | `CV-FORCED-MECHANICAL-RECOVERY`（`REPORT_FORCED_RECOVERY_OUTCOME`，以及 `REFUSE_STALE_...` 的正向一半） | MECHANICALLY_ISOLATED，带当前代数，不声称任何证明 | |
| journey | `forcedRecoveryLeavesVehicleToReconcile`（G3-07-44） | FP-IS-07 | `CV-FORCED-MECHANICAL-RECOVERY` finalState | 工作流、旅程、会话都仍需核对，没有去关卡 | |
| journey | `forcedRecoveryPerformsNoElectronicAction`（G3-07-45） | FP-IS-07 | `CV-FORCED-MECHANICAL-RECOVERY` finalState | 没有开锁，仓位状态不变 | |
| journey | `manualChargingReturnSequenceMatchesVector`（G3-07-51） | FP-IS-07 | `CV-MANUAL-CHARGING-RETURN` orderedExpectedMessages | 每次申请都得到一份关联的结果，两次请求号不同 | 疑点 29：这个向量 FP-IS-13 也有 |
| journey | `manualChargingReturnRequiresVerifiedAdministrator`（G3-07-52） | FP-IS-07 | `CV-MANUAL-CHARGING-RETURN`（`REQUIRE_VERIFIED_ADMINISTRATOR` / `REQUEST_RETURN_WITH_OPERATOR_CONTEXT`） | 两次请求都带管理员号，服务端记下同一个管理员 | 疑点 29 |
| journey | `eligibilityReevaluatedAfterReturn`（G3-07-53） | FP-IS-07 | `CV-MANUAL-CHARGING-RETURN`（`REEVALUATE_ELIGIBILITY_AFTER_RETURN`） | RecoveryRequired 时拒绝，Ready 时受理 | 疑点 29 |
| journey | `manualChargingReturnHasNoSideEffects`（G3-07-54） | FP-IS-07 | `CV-MANUAL-CHARGING-RETURN` finalState | 会话仍 Ready，没有需求、操作或单，仓位关、空、锁上 | 疑点 29：`NEVER_CLEAR_HOLD_LOCALLY` 没有被断言 |

## 批次 6 新增：FP-IS-10、FP-IS-11（control-server#164）

批次 6 的 G3 认领由 control-server#164 独占。journey runner 新认领两片，各一条场景、各九条断言，全部是切片断言，不加运行级断言（运行级的八条照旧，对新场景同样生效）。向量内容按 `protocol-v2.0.0` 协议仓 `vectors/CV-TASK-TYPE-ADMISSION-FAIL-CLOSED/expected.json` 与 `vectors/CV-REVERSED-DIRECTION-JOURNEY/expected.json` 对照；检查内容取自场景脚本里 `$assertions.Add` 的判定文字。

| runner | 运行级 | 切片断言 | 合计 |
| --- | --- | --- | --- |
| journey 新增 | 0 | 18（FP-IS-10 9、FP-IS-11 9） | 18 |

### 向量产品断言到 G3 断言

每条向量 `productAssertions` 都至少有一个 G3 断言对应，只有一条例外，单列在表末。

| 向量 | 归属 | `productAssertions` 条目 | 对应的 G3 断言 |
| --- | --- | --- | --- |
| `CV-TASK-TYPE-ADMISSION-FAIL-CLOSED` | 服务端 | `ADMIT_ONLY_BOUND_TASK_TYPES` | `boundTaskTypeAdmittedAndCompletedAlongside`（G3-10-05） |
| `CV-TASK-TYPE-ADMISSION-FAIL-CLOSED` | 服务端 | `FAIL_CLOSED_ON_MISSING_BINDING` | `unboundTaskTypeDemandNeverAccepted`（G3-10-01）、`unboundTaskTypeNeverPlannedListedOrOrdered`（G3-10-02）、`missingBindingReasonKeptOnTheServer`（G3-10-03）、`admissionReasonNeverSentToTheVehicle`（G3-10-04） |
| `CV-TASK-TYPE-ADMISSION-FAIL-CLOSED` | 车载端 | `NEVER_INFER_UNBOUND_TASK_TYPE` | `onboardShowsNoTaskTypeBeforeAWorklistItem`（G3-10-07）、`onboardShowsOnlyTheBoundTaskType`（G3-10-08） |
| `CV-TASK-TYPE-ADMISSION-FAIL-CLOSED` | 车载端 | `DISPLAY_ADMISSION_BLOCK_REASON` | **不认领**：规格第 5.3 节取消了这条断言，准入阻断原因只在服务端与看板，不经 `blockingFacts` 下发，v2 没有生产者。契约冲突由 onboard-hmi#115 登记为 trytoreachpeak0/8005-agv-program#125（汇总在 trytoreachpeak0/8005-agv-program#115），下次破坏性协议发布时改措辞。反向的「原因确实没有下发」由 G3-10-04 判 |
| `CV-TASK-TYPE-ADMISSION-FAIL-CLOSED` | 两端 | `orderedExpectedMessages`、finalState | `admissionSequenceMatchesVector`（G3-10-06）、`admissionFinalStateNoDuplicateCommit`（G3-10-09） |
| `CV-REVERSED-DIRECTION-JOURNEY` | 服务端 | `DERIVE_DIRECTION_FROM_TASK_TYPE_RULE` | `reversedPlanRunsFromStagingStationToAreaMachine`（G3-11-01）、`reversedWorklistStopRolesFollowThePlan`（G3-11-02）、`loadAtStagingStationUnloadAtAreaMachineOnTheTargetSlots`（G3-11-06） |
| `CV-REVERSED-DIRECTION-JOURNEY` | 服务端 | `NEVER_SWAP_ORIGIN_AND_DESTINATION` | `originAndDestinationNeverSwapped`（G3-11-07） |
| `CV-REVERSED-DIRECTION-JOURNEY` | 车载端 | `DISPLAY_DIRECTION_AS_PLANNED` | `onboardShowsPickupAtTheStagingStation`（G3-11-04）、`onboardShowsDropoffAtTheAreaMachine`（G3-11-05） |
| `CV-REVERSED-DIRECTION-JOURNEY` | 两端 | `orderedExpectedMessages`、finalState | `reversedSequenceMatchesVector`（G3-11-03）、`reversedJourneyFinalStateNoDuplicateCommit`（G3-11-09） |
| （规格第 4.1 节 I6，批次 6 推翻） | 服务端 | 不是向量条目 | `admissionFrozenOnTheUnload`（G3-11-08） |

`CV-TASK-TYPE-ADMISSION-FAIL-CLOSED` 的 `stableErrorCode`（`ACTION_NOT_ALLOWED_IN_STATE`）没有对应断言：它是车载端对不可做动作的应答码，而本场景里缺绑定的任务类型从未下发，车载端没有被要求做任何它的动作。判的是「根本没下发」（G3-10-02、G3-10-04）。

### 逐条表

场景：FP-IS-10 是 `scripts/l2/scenarios/g3-task-type-admission-fail-closed.ps1`，FP-IS-11 是 `scripts/l2/scenarios/g3-reversed-direction-journey.ps1`；两条共用 `scripts/l2/L2TaskTypeJourney.psm1` 驱动整趟旅程并经 UIA 读车载端的 `StopDirection`、`TaskType` 两个元素（onboard-hmi#115）。车载端文字按 `AutomationId` 读、按含义判（含「关卡」「取」「卸」、不是「未知」），不比文案全文。

| runner | 断言名 | 当前归属切片 | 依据向量 | 核实到的检查内容 | 疑点 |
| --- | --- | --- | --- | --- | --- |
| journey | `unboundTaskTypeDemandNeverAccepted`（G3-10-01） | FP-IS-10 | `CV-TASK-TYPE-ADMISSION-FAIL-CLOSED`（`FAIL_CLOSED_ON_MISSING_BINDING`） | 出厂预置配置下缺绑定的 `STAGING_TO_WIRE` 需求没有受理快照、旅程、订单意图、仓位操作；已绑定那条走完后再转四轮仍是如此 | |
| journey | `unboundTaskTypeNeverPlannedListedOrOrdered`（G3-10-02） | FP-IS-10 | 同上 | 服务端发件箱没有任何一条消息提到它的需求号（两种写法都查），RIoT 上没有不属于已绑定需求的单 | |
| journey | `missingBindingReasonKeptOnTheServer`（G3-10-03） | FP-IS-10 | 同上（规格第 5.3 节：原因只在服务端与看板） | `JourneyBacklog` 里它的原因码是 `TASK_TYPE_BINDING_MISSING`（control-server#160 的缺绑定码，不是 `OUT_OF_SCOPE_WORK_TYPE` 或 `TASK_TYPE_NOT_YET_EXECUTABLE`），没有受理时间 | |
| journey | `admissionReasonNeverSentToTheVehicle`（G3-10-04） | FP-IS-10 | 同上（规格第 5.3 节：不经 `blockingFacts` 下发） | 全部 `VehicleBusinessStateSnapshot` 的 `blockingFacts` 里都没有该原因码、需求号或 `STAGING_TO_WIRE` | |
| journey | `boundTaskTypeAdmittedAndCompletedAlongside`（G3-10-05） | FP-IS-10 | 同一向量（`ADMIT_ONLY_BOUND_TASK_TYPES`） | 同一轮放入的 `WIRE_TO_GATE` 需求受理、Succeeded，旅程 Completed，装卸两笔 Committed（不连带） | |
| journey | `admissionSequenceMatchesVector`（G3-10-06） | FP-IS-10 | 同一向量 `orderedExpectedMessages` | 已绑定需求的第一份计划被确认，其后有业务状态快照被确认；这一趟的计划与清单无作废、无未确认 | 向量四步里的计划属于已绑定那条需求：缺绑定的那条按设计什么都不发 |
| journey | `onboardShowsNoTaskTypeBeforeAWorklistItem`（G3-10-07） | FP-IS-10 | 同一向量（`NEVER_INFER_UNBOUND_TASK_TYPE`） | 到站前只有计划时，`StopDirection` 已按计划腿显示方向，`TaskType` 为空 | |
| journey | `onboardShowsOnlyTheBoundTaskType`（G3-10-08） | FP-IS-10 | 同上 | 取货点与关卡两站 `TaskType` 都非空；四次读数（到站前、取货点、关卡、完成后）出现过的文案只有一种，含「关卡」、不是「未知」 | 「全程」是四个采样点，不是连续监视 |
| journey | `admissionFinalStateNoDuplicateCommit`（G3-10-09） | FP-IS-10 | 同一向量 finalState | 全程只受理一条需求、两张 RIoT 单、两笔仓位操作；卸完的仓 CLOSED/EMPTY/1/0 | |
| journey | `reversedPlanRunsFromStagingStationToAreaMachine`（G3-11-01） | FP-IS-11 | `CV-REVERSED-DIRECTION-JOURNEY`（`DERIVE_DIRECTION_FROM_TASK_TYPE_RULE`） | 第一份计划：第 1 腿 `TO_PICKUP` 到派工待送站、第 2 腿 `TO_DROPOFF` 到 AREA 机台；所有计划的 `publicStationFunction` 为空 | |
| journey | `reversedWorklistStopRolesFollowThePlan`（G3-11-02） | FP-IS-11 | 同上 | 派工待送站的清单项 `PICKUP/STAGING_TO_WIRE`，AREA 机台的 `DROPOFF/STAGING_TO_WIRE` | |
| journey | `reversedSequenceMatchesVector`（G3-11-03） | FP-IS-11 | 同一向量 `orderedExpectedMessages` | 第一份是计划、清单在其后；这一趟的计划与清单全部被真车载端确认，没有作废 | |
| journey | `onboardShowsPickupAtTheStagingStation`（G3-11-04） | FP-IS-11 | 同一向量（`DISPLAY_DIRECTION_AS_PLANNED`） | 派工待送站的清单确认后，`StopDirection` 含「取」、不含「卸」，`TaskType` 非空 | |
| journey | `onboardShowsDropoffAtTheAreaMachine`（G3-11-05） | FP-IS-11 | 同上 | AREA 机台卸货等操作员时，`StopDirection` 含「卸」、不含「取」，`TaskType` 非空 | |
| journey | `loadAtStagingStationUnloadAtAreaMachineOnTheTargetSlots`（G3-11-06） | FP-IS-11 | 同一向量（`DERIVE_DIRECTION_FROM_TASK_TYPE_RULE`），以及 `REQ-0352` 的装卸同仓 | 录入请求的站点是派工待送站；装货命令在车到派工待送站之后、出发去机台之前发出；第二张 RIoT 单开往 AREA 机台，卸货命令在车到机台之后；两条命令的 `slots` 都是目标仓；模拟器上装与卸开的是同一个目标仓 | 装卸发生在哪一站按时刻判：命令里没有站点字段 |
| journey | `originAndDestinationNeverSwapped`（G3-11-07） | FP-IS-11 | 同一向量（`NEVER_SWAP_ORIGIN_AND_DESTINATION`） | 车出发之前判：`JourneyRuntimes` 记的起点是派工待送站（名称与 RIoT 号）、终点是 AREA 机台，只有一行、路线证据非空；RIoT 上第一张单开往派工待送站（第二张的目的站在 G3-11-06） | 路线证据是哈希，G3 读不出起终点；互换失配由 control-server#163 的 L1 证。G3-11-01 与本条在车出发前判，方向排反的服务端会让后面的驱动超时，判据仍写得出来 |
| journey | `admissionFrozenOnTheUnload`（G3-11-08） | FP-IS-11 | 不是向量条目；规格第 4.1 节 I6（批次 6 推翻） | `AdmissionDecisionSnapshots` 里卸货那次一行：AREA 机台站、`STAGING_TO_WIRE`、放行；装货那次没有 | |
| journey | `reversedJourneyFinalStateNoDuplicateCommit`（G3-11-09） | FP-IS-11 | 同一向量 finalState | 需求 Succeeded、旅程 Completed，两张单、两笔操作都 Committed，卸完的仓 CLOSED/EMPTY/1/0 | |

## 批次 7 新增：FP-IS-08（control-server#218）

批次 7 的 G3 认领由 control-server#218 独占。journey runner 新认领一片，一条场景 `g3-multi-stop-plan`、七条断言，全部是切片断言，不加运行级断言（运行级的八条照旧）。journey runner 因此从 14 个场景变成 15 个。向量内容按 `protocol-v2.0.0` 协议仓 `vectors/CV-MULTI-STOP-PLAN-NINE-LEGS/expected.json` 对照；检查内容取自场景脚本里 `$assertions.Add` 的判定文字。

| runner | 运行级 | 切片断言 | 合计 |
| --- | --- | --- | --- |
| journey 新增 | 0 | 7（FP-IS-08 7） | 7 |

这条向量**只覆盖计划腿**。多条清单项、持货等单、让站都没有向量，真装置场景 `real-onboard-mixed-side-one-stop`（混挂站点一次停靠两侧各一条需求）覆盖其中前两样，它不进任何 runner、不认领切片。

### 向量产品断言到 G3 断言

两端五条 `productAssertions` 每条都至少有一个 G3 断言对应，没有例外。

| 向量 | 归属 | `productAssertions` 条目 | 对应的 G3 断言 |
| --- | --- | --- | --- |
| `CV-MULTI-STOP-PLAN-NINE-LEGS` | 服务端 | `PLAN_UP_TO_NINE_LEGS` | `appendedPlanAdvancesRevisionWithAtLeastThreeLegs`（G3-08-02） |
| `CV-MULTI-STOP-PLAN-NINE-LEGS` | 服务端 | `ORDER_LEGS_BY_SEQUENCE` | `planLegsSentInSequenceOrder`（G3-08-03）；序位连续那一半在 `everyPlanRevisionSequencedFromOneWithAPurposePerLeg`（G3-08-01） |
| `CV-MULTI-STOP-PLAN-NINE-LEGS` | 服务端 | `CATEGORISE_EVERY_STOP_PURPOSE` | `everyPlanRevisionSequencedFromOneWithAPurposePerLeg`（G3-08-01） |
| `CV-MULTI-STOP-PLAN-NINE-LEGS` | 车载端 | `DISPLAY_FULL_JOURNEY_PLAN` | `onboardShowsTheDispatchPlanInSequenceOrder`（G3-08-05）、`onboardShowsTheAppendedPlanInSequenceOrder`（G3-08-06） |
| `CV-MULTI-STOP-PLAN-NINE-LEGS` | 车载端 | `NEVER_REORDER_LEGS_LOCALLY` | `onboardShowsTheAppendedPlanInSequenceOrder`（G3-08-06） |
| `CV-MULTI-STOP-PLAN-NINE-LEGS` | 两端 | `orderedExpectedMessages`、finalState | `multiStopSequenceMatchesVector`（G3-08-04）、`multiStopJourneyEachDemandLoadedAndUnloadedOnce`（G3-08-07） |

**`UP_TO_NINE_LEGS_PLANNED` 的「九」由两端 G2 证明，G3 证明的是三条以上的真实计划。**九条腿的计划要八条需求、八个停靠走完一趟，在真装置上约是本场景的四倍机时，而上限本身（腿数门禁、入站 schema 上限）两端 G2 各自按边界值证过：服务端 `Batch7EnRouteAppendPlannerTests` 的腿数门禁，车载端 onboard-hmi#134 的 `InboundPayloadSchemaBoundaryTests.APlanOfNineLegsIsAccepted`／`APlanOfTenLegsIsRefused` 与 `MultiDemandJourneyG2Tests.ANineLegPlanIsAcknowledgedAndShownInFullInSequenceOrder`。G3 这里证的是那条链路在两端真实协议下接通：途中追加让计划从两条腿变成三条，修订号前进、整体重发、车载端按序位整表显示，旅程走完。

`stableErrorCode` 为 null，没有对应断言。

### 逐条表

场景：`scripts/l2/scenarios/g3-multi-stop-plan.ps1`（读取在 `scripts/l2/L2MultiStopJourney.psm1`，驱动在 `scripts/l2/scenarios/MultiStopRigCommon.ps1`）。一辆车、两条需求：甲在 12 号站 `N1-3_N1-7`、乙在 11 号站 `C15-13`，卸货都在关卡；乙在车开往 12 号站途中追加。追加后的计划按站名排会是 2,1,3，本地重排因此看得见。

车载端的计划腿行序读每行第一个 TextBlock（序位），不读行上的 `ItemStatus`：onboard-hmi#134 把它挂在模板里的 `Grid` 上，`Grid` 不进 UIA 树，读不到（`docs/defects/20260922-journey-plan-legs-item-status-not-in-uia-tree.md`）。所以 G3-08-05、G3-08-06 判的是行数与行序，不按原始码判每条腿的用途类别与状态——那两样由服务端一侧的 G3-08-01 判。

| runner | 断言名 | 当前归属切片 | 依据向量 | 核实到的检查内容 | 疑点 |
| --- | --- | --- | --- | --- | --- |
| journey | `everyPlanRevisionSequencedFromOneWithAPurposePerLeg`（G3-08-01） | FP-IS-08 | `CV-MULTI-STOP-PLAN-NINE-LEGS`（`CATEGORISE_EVERY_STOP_PURPOSE`，`ORDER_LEGS_BY_SEQUENCE` 的连续那一半） | 这趟旅程的每一版 `UpcomingStopPlanSnapshot`（至少两版）：腿的 `sequence` 为 1..n、无重复，每条腿的 `stopPurposeCategory` 非空 | 今天服务端恒填 `BUSINESS`，所以「非空」是本条能判的全部；等待点、充电桩腿不在本场景 |
| journey | `appendedPlanAdvancesRevisionWithAtLeastThreeLegs`（G3-08-02） | FP-IS-08 | 同一向量（`PLAN_UP_TO_NINE_LEGS`） | 第一版三条腿以上的计划被车载端确认；它的 `planRevision` 大于此前每一版；腿数 3..9；乙进的是同一趟旅程（归属两条、`JourneyRuntimes` 一行） | 九条由两端 G2 证，见上 |
| journey | `planLegsSentInSequenceOrder`（G3-08-03） | FP-IS-08 | 同一向量（`ORDER_LEGS_BY_SEQUENCE`） | 每一版计划发件箱原文里 `legs` 数组的先后就是 `sequence` 升序；读原文，不经排序 | |
| journey | `multiStopSequenceMatchesVector`（G3-08-04） | FP-IS-08 | 同一向量 `orderedExpectedMessages` | 这趟旅程第一份快照是计划，第一份清单在它之后；每一份计划与清单都被真车载端确认，没有作废。旅程 `Completed` 之后先等全部确认再判 | |
| journey | `onboardShowsTheDispatchPlanInSequenceOrder`（G3-08-05） | FP-IS-08 | 同一向量（`DISPLAY_FULL_JOURNEY_PLAN`） | 派车那一版（两条腿）被车载端确认之后，UIA `JourneyPlanLegs` 的行数与行序等于它的 `sequence` | 两条腿按站名排恰好不变，这一条判不出重排；重排由 G3-08-06 判 |
| journey | `onboardShowsTheAppendedPlanInSequenceOrder`（G3-08-06） | FP-IS-08 | 同一向量（`DISPLAY_FULL_JOURNEY_PLAN`、`NEVER_REORDER_LEGS_LOCALLY`） | 三条腿那一版被车载端确认之后，UIA 行数与行序等于它的 `sequence`（1,2,3）；按站名重排会读成 2,1,3 | |
| journey | `multiStopJourneyEachDemandLoadedAndUnloadedOnce`（G3-08-07） | FP-IS-08 | 同一向量 finalState | 旅程 `Completed`；两条需求各一笔装、一笔卸，都 `Committed`，需求 `Succeeded`；关卡上两笔卸货各属一条需求；装过的两个仓最后 `CLOSED/EMPTY/1/0` | 持货等单在本场景会发生（允许追加就持货），不判；它的判据在 `real-onboard-mixed-side-one-stop` |
