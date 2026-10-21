# control-server#828 试验机批号（REQ-0368）— L1 证据

| 文件 | 内容 |
| --- | --- |
| `red/01-tests-at-7cc626e0f.txt` | 测试提交（判据、规则、日志、配置都是只够编译的桩）上跑 `TrialSublotAdmissionTests`、`StructuralDispatchBlockTests`、`IntegrationSliceTraitArchitectureTests`：23 条红，含三条样本试验批被接走（去掉过滤，正例变红）；反例与「配置为空」照常派车的几条是绿的 |
| `green/01-…-at-b8c1dbb12.txt` | 实现提交上，上面三类加 `CatalogAgeWindowTests`：176 条全过 |
| `green/02-wider-dispatch-classes-at-b8c1dbb12.txt` | 架构测试、看板原因码说明守卫、`JourneyRuntimeOptionsTests`、多车、防饥饿、分区归属、派车次序、电量等派车相关类：369 条全过 |
| `green/03-parallel-instance-selftest-at-6a72761d0.txt` | `scripts/parallel/Test-ParallelInstance.ps1`：1330 条全过，含 10 条试验批用例（取值合法性 8 条、缺省写关、四份生产段写 6／`9` 且试验段不写） |
| `green/04-format.txt` | 改动的 `.cs` 文件 `dotnet format`：style 0；whitespace 只报 `DispatchRoundRunner.cs` 既有行（714、825、1030、1174 附近）的 14 处，本票新增行为零 |
| `green/05-guards-and-architecture.txt` | 扫全仓文本的退役名护栏（`Batch8OccupancyRetirementMigrationTests`）、全部架构测试与本票测试类：132 条全过 |
| `mutations/` | 13 个变异，每次只改一个文件、跑 `TrialSublotAdmissionTests`、`git checkout` 还原；脚本 `run-mutations.ps1`，结论 `00-summary.txt` |

变异对照（全部变红）：

| 变异 | 改了什么 | 变红的用例 |
| --- | --- | --- |
| M1 | 从准入链去掉判据 | `ATrialSublotIsNotDispatchedAndWaitsWithItsReason` |
| M2 | 删掉依赖注入里判据的注册 | `TheHostRegistersTheCriterionAndTheSingletonLog` |
| M3 | AREA→EQP 观测照旧计入试验批行 | `ATrialRowDoesNotMakeAnOrdinaryDemandsAreaAmbiguous` |
| M4 | 结构性汇总不把试验批码当静默 | `ABlockRaisedBeforeTheRuleWasOnIsClearedOnceItIs` |
| M5 | 防饥饿排除集合不排试验批 | `ATrialSublotIsNeverEscalatedForStarvation` |
| M6 | 判据序号 42 → 46（排到时间窗之后） | `ATrialSublotPastTheCatalogWindowIsRefusedAsATrialSublot` |
| M7 | 放回待派不豁免 | `ATrialSublotReleasedBackForRedispatchIsRedispatched` |
| M8 | 比较前去空白、转半角（NFKC） | `ASublotThatIsNotATrialOneIsDispatched`（全角「９」与前导空格两例） |
| M9 | 字符位差一（取第 5 个字符） | `TheProductionRuleMatchesCharacterSixBeingAHalfWidthNine` |
| M10 | 校验放过只写一半的规则 | `TheRuleShipsEmptyAndHalfARuleIsRefused` |
| M11 | 日志每轮都记 | `TheFilteredSublotsAreLoggedOnlyWhenTheyChange` |
| M12 | 派车轮不取配置的规则 | `ATrialSublotIsNotDispatchedAndWaitsWithItsReason` |
| M13 | 看板说明表缺这个码 | `TheDashboardListsTheRefusalWithAChineseDescription` |

M7、M11 第一轮写成 `if (false)`，编译器报 CS0162（不可达代码，警告即错误），测试根本没跑；改成运行期才知道的假条件（`Length < 0`）后重跑，两条都变红。目录里的 `M7.txt`、`M11.txt` 是重跑那一次。

受理前重读目录不另设一道：SUBLOT 是重读比对的决策事实之一，重读到批号变了就放弃受理（`ASublotThatTurnsIntoATrialOneAtIntakesRereadIsNotAccepted`）；规则一轮只读一次，所以最终门里再判一次的写法杀不死任何变异，没有加。
