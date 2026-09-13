# 批次 3 出口报告：治理面单端工程

日期：2026-09-09　　集成分支：`ControlServer_MVP`

## 结论

**批次 3 尚未出口。**规格 8.2 的默认三条里，L1 成立，L2 与门禁都不成立。

**不成立的原因在本报告写就后被查清，与第一版写的不是一回事**：批次 1 与批次 2 轨 A **都已经完成**，
协议 v2 候选、`FP-IS-14`／`FP-IS-15` 两个切片、以及两端的 v2 实现分别在协议仓的 `fp/v2-candidate`、
控制服务端的 `fp/v2-impl` 与车载端的 `w2g/fp-v2-impl` 上。批次 3 的十三票做在 `ControlServer_MVP`
这条 v0.3.0 线上，**两条线还没有汇合**——这才是门禁跑不起来的真实原因。详见第三节。

| 默认出口 | 状态 | 说明 |
| --- | --- | --- |
| L1 绿，且新能力有新增覆盖 | **成立** | 420 passed / 0 failed；批次 3 新增 62 条测试，逐项对照见下 |
| L2 绿，新场景连续三次通过 | **不成立** | 批次 3 目前**没有新的 L2 场景**——#15／#16 的 L2 判据都要真实的 v2 消息面 |
| 两切片四门禁全 PASS | **不成立** | 切片与 v2 两端实现在 `fp/*` 线上，批次 3 在 v0.3.0 线上，两条线未汇合 |

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

### `real-onboard` 真装置三连跑：三次全 PASS

`real-onboard-normal-load` 连续三次，证据三个独立目录，无覆盖：

| 目录 | 结论 | 判据数 |
| --- | --- | --- |
| `evidence/l2/20260909-batch3-exit-real-onboard-normal-load-001` | PASS | 12 |
| `evidence/l2/20260909-batch3-exit-real-onboard-normal-load-002` | PASS | 12 |
| `evidence/l2/20260909-batch3-exit-real-onboard-normal-load-003` | PASS | 12 |

三次的 peer 都是 `8005-agv-onboard-hmi` `3d8206f`（`OnboardHmi_MVP`）与 `slots-simulator` `fb5f7c5`
（`main`），协议 `protocol-v0.3.0`，`batchId` `BATCH-3`。

**这三份证明的是什么：批次 3 的服务端改动在真装置下没有回归。**它们不是 `FP-IS-14`／`FP-IS-15` 的
门禁证据，也不能替代那四道门禁——真装置跑的车载端是 `OnboardHmi_MVP`，**不含 #27／#28**（那两个分支
按你的决定暂不合入），协议也还是 v0.3.0。

**这个先后是有意的**：先在 v0.3.0 下拿到一条真装置基线，等协议换代之后再红，就能把「批次 3 的问题」
与「换代的问题」分开；反过来先换代再跑，红了分不清是谁的。

## 三、门禁：切片存在，但不在这条线上

**本节的第一版写错了，纠正记在这里。**当时只读了协议仓的 `main`（也就是控制服务端 vendored 的
`protocol-v0.3.0`），据此写下「`FP-IS-14` 与 `FP-IS-15` 在整个协议仓里零命中」。那句话对 `main`
成立，对协议仓整体**不成立**：

| 事实 | 位置 |
| --- | --- |
| 批次 1 已完成——协议 v2 候选由生成器一把产出，G1 通过 | 协议仓分支 `fp/v2-candidate`（`f6ee75d`） |
| 消息面 63 条、切片表 16 行，**含 `FP-IS-14` 与 `FP-IS-15`** | 同上 |
| 批次 2 轨 A 服务端半边——控制服务端切到协议 v2，身份／消息面／trait 面钉死 | `fp/v2-impl`（`978a9e4`） |
| `FP-IS-00`～`07` 在协议 v2 下的 `CONTROL_SERVER_G2` 重证 | `fp/v2-impl`（`5915cf7`、`e3ea250`） |
| 协议 v2 身份下的 G3，逐切片 `gate-result.json` | `fp/v2-impl`（`0d26bc9`→`1f25f6e`） |
| 轨 A 车载端半边——车载端切到协议 v2；`FP-IS-00`～`07` 的 `ONBOARD_HMI_G2` 重证 | onboard-hmi `w2g/fp-v2-impl`（`f0b4e0d`、`153b705`） |

所以门禁跑不起来的真实原因不是「切片不存在」，而是**批次 3 的代码在 v0.3.0 这条线上，切片与 v2 两端
实现在另一条线上，两条线还没有汇合**。规格 3.4 的边「3 ← 2 轨 A」只约束证据、不约束实施——批次 3 的
服务端代码可以先做，这一点本批次照做了；现在轨 A 已经到位，缺的是把批次 3 搬到 v2 线，并按 v2 的
消息 7／8／9 与 `CapabilitySnapshot.activeSlotConfigurationFingerprint` 把 #15／#16 停下的那半补齐。

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
| 批次 3 的十三票合到 v2 线（`fp/v2-impl`） | 探测过一次合并：20 个文件冲突，含 `ProtocolCandidateIdentity`、`ProtocolErrorCodes`、`ControlServerDbContext` 与 EF 的 `ModelSnapshot` |
| #15／#16 按 v2 消息面补齐传输与序列化 | 上一行 |
| 车载端 #27／#28 合到 `w2g/fp-v2-impl` 并接上 v2 消息面 | 上一行；且 PR 合入 `OnboardHmi_MVP` 暂不做（用户 2026-09-09 决定） |
| 两切片各四道门禁 `G1` / `CONTROL_SERVER_G2` / `ONBOARD_HMI_G2` / `G3` | 以上全部 |
| #15／#16 的 L2 场景与三连跑 | 同上 |
| `real-onboard` 真装置三连跑（v0.3.0 基线） | **已完成，三次全 PASS** |
| W1 现场窗口实跑 | **2026-09-13 已做，W1 PASS**（`evidence/field/20260913-W1-three-vehicle-qualification/`）：在生产现有的这条 v0.3.0 库上，三台车 24 仓逐仓真信号核对全部通过、逐台放行、门禁启用时刻留审计。探针在车上直读仓位 IO 模块 `192.168.71.150`、产品负责人在车前操作仓门；现场不拍照（产品负责人决定），W1-06 按目录形状与逐仓记录判定。当天修了三件事：光幕极性常量改按票据 35 逐信号写明（seed 之前）、窗口脚本在服务器上跑的三处问题、C2000 模块只回写 8 位事务号（`aeadd667`，车载端同一问题另开 issue）。两次中途停下的尝试原样保留在 `evidence/field/20260913-W1-aborted-attempts/`。`Governance:slotConfigurationReadinessGate` 仍是 `Off`，且生产代码没有调用点读它 |
| 车载端两个分支合入 `OnboardHmi_MVP` | 开 PR，需先经你同意 |
