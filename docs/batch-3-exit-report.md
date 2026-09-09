# 批次 3 出口报告：治理面单端工程

日期：2026-09-09　　集成分支：`ControlServer_MVP`

## 结论

**批次 3 尚未出口。**规格 8.2 的默认三条里，L1 成立，L2 与门禁都不成立，而**不成立的原因不是做得
不够，是被批次 2 轨 A 挡住**：`FP-IS-14`／`FP-IS-15` 要收发的协议 v2 消息（7／8／9）与
`CapabilitySnapshot` 的指纹字段尚未实现，两个切片也还没有被登记进协议仓的
`integration-slices/index.json`。

| 默认出口 | 状态 | 说明 |
| --- | --- | --- |
| L1 绿，且新能力有新增覆盖 | **成立** | 420 passed / 0 failed；批次 3 新增 62 条测试，逐项对照见下 |
| L2 绿，新场景连续三次通过 | **不成立** | 批次 3 目前**没有新的 L2 场景**——#15／#16 的 L2 判据都要真实的 v2 消息面 |
| 两切片四门禁全 PASS | **不成立** | `FP-IS-14`／`FP-IS-15` 在协议仓里不存在，四道门禁无从跑起 |

## 一、L1：逐项对照

**逐项对照，不是总数对比。**下表左列是批次 3 每张票立下的能力，右列是守它的测试方法。

### #8 拆分热点文件（`8b076bc`）

| 能力 | 守它的测试 |
| --- | --- |
| 批次 3 只许一个 migration，且必须是最后一个 | `Batch3AddsExactlyOneMigrationAndItIsTheLastOne` |
| 那一个 migration 建齐批次 3 需要的全部表 | `TheOneMigrationCreatesEverySlotConfigurationGovernanceAndDashboardTableBatch3Needs` |
| 新表不必编辑共享的 `ControlServerDbContext.cs` | `EveryBatch3TableIsReachableWithoutADbSetPropertyOnTheSharedContextFile` |

### #9 版本化快照与不可改写审计（`e49b083`）

| 能力 | 守它的测试 |
| --- | --- |
| 版本级完整快照，重复冻结同一版不写第二份 | `FrozenSnapshotComesBackFieldForFieldAndRefreezingTheSameVersionDoesNotWriteASecondOne` |
| 字段级差异现算不另存 | `FieldLevelDifferenceIsComputedFromTwoSnapshotsAndNoDifferenceTableExists` |
| 审计不可改写 | `AuditRecordsCannotBeUpdated` |
| 保留期内不可删除 | `AuditRecordsInsideRetentionCannotBeDeleted` |
| 180 天下限与过期清理 | `RetentionDefaultsTo180DaysAndPurgesOnlyWhatIsPastIt`、`ConfiguredRetentionIsAcceptedAboveTheFloorAndRejectedBelowIt`、`ConfiguredAuditRetentionBelowTheHundredAndEightyDayFloorIsRefusedAtStartup` |
| 人员字段是部署身份，显式标注不可归因到自然人 | `ThePersonFieldIsADeploymentIdentityExplicitlyMarkedNotAttributableToANaturalPerson`、`ADeploymentIdentityThatCouldPassForAPersonIsRefused` |
| 发布→冻结→审计→只读读回的完整链路 | `TracerPublishesAVersionFreezesItAuditsItAndReadsBothBackThroughTheReadOnlyQuery` |

### #10 仓位配置权威与两层模板（`54f77bc`）

| 能力 | 守它的测试 |
| --- | --- |
| 两层各自版本化，已发布版本不可改写 | `BothLayersVersionSeparatelyAndAPublishedVersionCannotBeRewritten` |
| 一个模板被多个仓位引用，发新版模板不动已生效配置 | `OneTemplateIsReferencedByManySlotsAndPublishingANewTemplateDoesNotTouchTheLiveConfiguration` |
| 车载端声明只被核验、永不被采信为权威 | `AVehicleDeclarationIsOnlyVerifiedAndNeverBecomesTheAuthority` |
| 已批准八仓硬件事实以不可改写版本入库 | `TheApprovedEightSlotHardwareFactsAreStoredAsAnImmutableApprovedVersion` |
| 每个已发布版本自带快照与审计 | `EveryPublishedVersionCarriesItsOwnFrozenSnapshotAndAudit` |

### #11 归档恢复最小闭环（`764fed7`）

| 能力 | 守它的测试 |
| --- | --- |
| 一台实体车一份档案，沿用原 `agvId` | `RestorationReusesTheOriginalAgvIdAndASecondArchiveForTheSameCarIsRefused` |
| 车离线可完成恢复，候选绑定原子建立 | `RestorationCompletesWhileTheVehicleIsOfflineAndEstablishesTheCandidateBindingAtomically` |
| 恢复一台从未归档的车不留任何行 | `RestoringACarThatWasNeverArchivedLeavesNothingBehind` |
| 恢复不投运，且无路径可直接置真 | `ARestoredVehicleIsNotBusinessAvailableAndNothingCanSetThatDirectly` |
| 恢复入口不在看板也不在车载端 | `TheRestorationEntryPointExistsNeitherOnTheDashboardNorOnTheOnboardSide` |
| 恢复路径上没有共享密钥冒充身份 | `TheRestorationPathContainsNoSharedEnvironmentVariableSecretPosingAsIdentity` |

### #12 看板骨架（`b695a50`）

| 能力 | 守它的测试 |
| --- | --- |
| 2 秒是一个节奏不是两个 | `BothViewsShareOneTwoSecondCadence` |
| 两个视图各有真实数据卡片 | `AtLeastOneRealDataCardExistsInEachView` |
| 取不到就说原因，不显示旧值 | `WhenTheDataSourceIsUnavailableTheCardSaysWhyAndShowsNoOldValue` |
| 全看板不存在「已过期／上次更新」这一类呈现 | `NoStaleOrLastUpdatedPresentationExistsAnywhereInTheDashboard` |
| 看板上没有恢复／激活／回滚／投运入口 | `NoRestoreActivateRollbackOrCommissionEntryPointExistsOnTheDashboard` |
| 看板数据契约与协议无交集 | `TheDashboardDataContractIsDisjointFromTheProtocol` |
| 新增一张卡片只加文件，不改看板主文件 | `ANewCardIsAddedByAddingAFileAndTheDashboardMainFileIsNotTouched` |
| 端点服务真实数据、卡片渲染它 | `TheTwoQueryEndpointsServeRealRowsAndTheCardsRenderThem` |
| 数据源越出只读前缀即被拒 | `ACardWhoseDataSourceIsOutsideTheReadOnlyQueryPrefixIsRefused` |

### #13 逐仓核验与每车 IO 完整性门禁（`de76715`）

| 能力 | 守它的测试 |
| --- | --- |
| 门禁是每车判定，一台车的缺口不牵连另一台 | `OneVehicleFailingTheGateSaysNothingAboutAnotherVehicle` |
| 一台车少一个仓位的绑定就拿不到业务就绪 | `AVehicleWithOneSlotMissingItsBindingCannotBecomeBusinessAvailable` |
| 抽样整批拒绝，逐仓三信号缺一不算过 | `SamplingIsRejectedAndEveryOneOfTheVehiclesSlotsIsConfirmedOneByOne` |
| 硬件相关变化触发重新核验，此前就绪失效 | `AnyHardwareRelatedChangeSendsTheVehicleBackForReverificationAndTheEarlierReadinessLapses` |
| 断线时进不了整车配置维护态 | `WholeVehicleMaintenanceCannotBeEnteredWhileTheServerLinkIsDown` |
| 车载端根本没有第二个入口 | `TheOnboardSideHasNoEntryOfItsOwnIntoWholeVehicleMaintenance` |
| 门禁上线不改变已核对车辆的状态 | `BringingTheGateOnlineLeavesAnAlreadyVerifiedVehicleExactlyWhereItWas` |

### #14 回滚是受控的新激活，与生效影响预览（`e97d8a4`）

| 能力 | 守它的测试 |
| --- | --- |
| 回滚产出新激活记录，被选中的旧版本逐字段不变 | `ARollbackIsANewActivationAndTheVersionItSelectedIsNotTouched` |
| 只影响冻结点晚于它的消费者，两侧各有覆盖 | `ARollbackReachesOnlyTheConsumersWhoseFreezePointIsLaterThanIt` |
| 四类敏感动作的预览与执行是同一份计算 | `AllFourSensitiveActionsPreviewTheirImpactWithTheSameCalculationThatDoesIt` |
| 空预览仍明说「无影响对象」 | `AnEmptyPreviewStillSaysThereIsNoImpactedObjectRatherThanSayingNothing` |
| 无共享密钥冒充新鲜二次认证（`REQ-0339` 未做那半） | `NoSharedSecretPosesAsTheFreshSecondFactorAnywhereOnTheActivationPath` |
| 覆盖治理明文排除、零实现（`REQ-0326`） | `CoverageGovernanceIsExplicitlyExcludedAndHasZeroImplementation` |

### #15 仓位配置原子激活下发与结果补报，服务端半边（`cf1623e`）

| 能力 | 守它的测试 |
| --- | --- |
| RELIABLE 交付、`SLOT_CONFIGURATION` 恢复角色、落地即待补报 | `AnActivationGoesOutReliableUnderTheSlotConfigurationRecoveryRoleAndWaitsForItsResult` |
| 结果未回时不猜成功也不猜失败 | `WhileTheResultIsOutstandingTheServerGuessesNeitherSuccessNorFailure` |
| 补报幂等，不产生第二次激活 | `AReplayedResultConvergesTheSameActivationInsteadOfProducingASecondOne` |
| 绑定不完备的配置无法下发 | `AConfigurationWithAnUnboundSlotCannotBeIssuedAtAll` |
| 指纹一致才认恢复候选，不一致按不匹配处置 | `TheFingerprintDecidesWhetherTheArchivedConfigurationIsStillARestorationCandidate` |
| 一次激活不要求第二次人工审批 | `NoSecondHumanApprovalStandsBetweenTheDecisionAndTheActivation` |

### #16 服务端消费告警快照并投影到看板（`a0224f1`）

| 能力 | 守它的测试 |
| --- | --- |
| 后一份快照整体取代前一份，序号回退被忽略 | `ALaterSnapshotReplacesTheEarlierOneWholeAndNeverMergesIntoIt` |
| 车辆失联显示原因而非旧告警 | `AVehicleOutOfContactShowsTheReasonRatherThanItsLastKnownAlarms` |
| 收敛规则：与任何车当下都不直接相关的才进看板 | `OnlyAlarmsUnrelatedToAnyVehiclesOwnSituationReachTheDashboard` |
| 可见性求值路径上没有身份或密钥 | `AlarmVisibilityIsEvaluatedWithNoIdentityOrKeyInputAnywhereOnItsPath` |
| 告警码是开放字符串集合，与协议错误码注册表无交集 | `AlarmCodesStayAnOpenStringSetAndShareNothingWithTheClosedProtocolErrorCodeRegistry` |
| 接入告警卡片时未修改看板主文件 | `TheAlarmCardWasAddedWithoutTouchingTheDashboardMainFiles` |
| 端点服务真实数据，卡片渲染含失联原因 | `TheEndpointServesRealRowsAndTheCardRendersThemIncludingTheLostContactReason` |

### #17 W1 现场窗口准备（`d3a427f`）

| 能力 | 守它的测试 |
| --- | --- |
| 门禁档位缺省是关的（W1 先核对后启用） | `TheGateIsOffUntilSomebodyTurnsItOn` |
| 拼错的档位名启动即抛，不静默当成关 | `AMisspelledModeFailsAtStartupInsteadOfSilentlyMeaningOff` |
| 「启用门禁」不等于「门禁在拦车」这个边界被钉住 | `TurningTheGateOnIsNotTheSameThingAsTheGateStoppingAVehicle` |

### 车载端两票（`trytoreachpeak0/8005-agv-onboard-hmi`）

| 票 | 分支 | 覆盖 |
| --- | --- | --- |
| #27 仓位配置原子激活（车载半边） | `w2g/b3-27-slot-config-activation` | `WireToGateServerCommandTranslatorRegistryTests`、`SlotConfigurationActivationTests` |
| #28 车载告警快照与技术日志 | `w2g/b3-28-onboard-alarm-snapshot` | `OnboardAlarmSnapshotTests`、`OnboardTechnicalLogTests` |

两个分支上 `SQCD.Agv.UnitTests` 193 passed、`G2Tests` 67 passed，均 0 failed 0 skipped。

**这两票的新测试一律不挂 `[Trait("IntegrationSlice", ...)]`**：`IntegrationSliceCoverageArchitectureTests`
要求出现的集合与 vendored `integration-slices/index.json` 双向相等，而 `FP-IS-14`／`FP-IS-15` 不在那份
清单里——挂上去会让那条双向相等直接红。这本身就是下面第三节那件事的一个侧影。

### 服务端 L1 实测

```
Passed!  - Failed:     0, Passed:   420, Skipped:     0, Total:   420, Duration: 1 m 5 s
```

批次 3 新增 62 条服务端测试，逐票分布：#8 3 条（另 1 条保留期）、#9 9 条、#10 5 条、#11 6 条、
#12 9 条、#13 7 条、#14 6 条、#15 6 条、#16 7 条、#17 3 条。

## 二、L2：identity 两项已落地，三连跑没有对象

`assertions.json` 的 `identity` 块本期加两项，已落地并实跑验证：

| 项 | 值的来源 |
| --- | --- |
| `protocolReleaseIdentity` | 从 `src/ControlServer.Host/appsettings.json` 的 `ProtocolCandidate` 读 `tag` / `repositoryCommit` / `manifestSha256`，不在脚本里另抄一份——这两处曾漂移过整整一个 release |
| `batchId` | `Invoke-L2Scenario.ps1` 的 `-BatchId` 参数，缺省 `BATCH-3` |

实跑一次合成装置 `normal-load` 验证，PASS，两项都进了 `assertions.json` 与 `SUMMARY.md` 的身份表。

**但批次 3 目前没有新的 L2 场景可供三连跑。**#15 与 #16 各有一条 L2 验收标准
（「下发→断线→重连→补报」与「车载产快照→服务端消费→看板可见」），两条都要真实的 v2 消息面，
所以两票都停在业务语义与单元测试，L2 那一条挂着。**这不是漏做，是纪律 6 那条边。**

`real-onboard` 真装置的三连跑归本票排队，尚未开跑——它独占桌面、要弹两个 WPF 窗口、并要求两个 peer
仓的工作树干净，属于要先与你确认的开销。

## 三、门禁：`FP-IS-14`／`FP-IS-15` 还不存在

实读 `repos/8005-agv-protocol/integration-slices/index.json`：里面只有 `W2G-IS-00` 到 `W2G-IS-07`
八个切片，`FP-IS-14` 与 `FP-IS-15` 在整个协议仓里零命中。

所以四道门禁**不是没跑，是没有可跑的对象**：门禁按切片组织，切片不存在，`gate-result.json` 也就无从
绑定。登记这两个切片需要协议 v2 的消息 7／8／9 与 `CapabilitySnapshot` 的
`activeSlotConfigurationFingerprint` 字段先存在，那是批次 2 轨 A。

规格 3.4 的边「3 ← 2 轨 A」只约束证据、不约束实施，这一点本批次是照做的：`FP-C7`／`FP-C8` 的服务端
代码已经全部落地，卡住的只有联调证据这一半。

## 四、两处表述纪律

**`FP-IS-14`／`FP-IS-15` 是新切片，不存在可沿用的通过结论。**`W2G-IS-00`～`07` 与 RC 目前仍是
`INCONCLUSIVE`——「八类 G3 向量各有证据」不等于「八个切片通过」。本报告没有、也不会把任何旧结论
挪过来当作这两个新切片的通过依据。

**完整产品的验收证据里没有任何人员认证项**，`FP-C10` 与 `FP-C6` 整簇延后。批次 0 的权限骨架**两端零
实现**，唯一的「认证」是对一个全场共用环境变量做定时安全比较——持有密钥者可以自称任何角色。
`REQ-0339` 因此只做了影响预览那一半，另一半空着，并有架构测试守着「没有人拿共享密钥顶上」。

## 五、结账

| 待办 | 阻塞于 |
| --- | --- |
| `FP-IS-14`／`FP-IS-15` 登记进 `integration-slices/index.json` | 批次 2 轨 A |
| 两切片各四道门禁 `G1` / `CONTROL_SERVER_G2` / `ONBOARD_HMI_G2` / `G3` | 切片登记 |
| #15／#16 的 L2 场景与三连跑 | 批次 2 轨 A |
| `real-onboard` 真装置三连跑 | 排期（独占桌面，跑不并行） |
| W1 现场窗口实跑 | 现场安排；代码与证据目录已就绪（#17） |
| 车载端两个分支合入 `OnboardHmi_MVP` | 开 PR，需先经你同意 |
