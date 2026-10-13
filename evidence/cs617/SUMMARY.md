# cs#617 证据摘要：没有 upperId 的外来单读不出结束，以及受治理的人工放车

## 现场复现材料

`evidence/field/20261011-W3-S1-charging/13-foreign-order-readback.txt`（cs#566 续场二，已在库里）：

```
2026-10-11 15:23:21 GET detailByOrderId/order-2109181133127286784 -> HTTP 200
code=0 message=成功 resultPresent=True
id=1757698 orderId=order-2109181133127286784 upperId=[] upperIdIsNull=False orderState=2 orderName=iiii createBy=
```

RIoT 回 HTTP 200、code 0、result 完整，`orderState=2`（已取消，明确终结），`upperId` 是空串。本票所有替身都照这个形态回答。

## 票里那条「推断」的逐行核对（读到的）

票里写的是：运行列表那条路径（`ListUnfinishedOrders`）不经过 `ToOrderRef`，所以认外来单正常，只有确认结束坏了。逐行核对，**成立**。

| 路径 | 调用链 | 经不经 `ToOrderRef` |
| --- | --- | --- |
| 认外来单 | `ForeignRunningOrderSupervisor.cs:201` → `HttpRiotMovementGateway.ListUnfinishedOrdersAsync`（`:599`）→ `ReadAllNonFinalOrdersAsync`（`:604`）→ SDK `ListOrdersByStatesAsync`（`:664`、`:695`）→ riot-sdk `cf58c11` `OrderClient.cs:291`，记录在 `:325` 直接 `new OrderStateRecord(...)`，`UpperId` 是 `string?` 原样透传 → 网关 `:612` `new RiotListedOrder(...)` | **不经过** |
| 确认结束 | `ForeignRunningOrderSupervisor.cs:349`（以及 `:676`、`:724`）→ `HttpRiotMovementGateway.ReadOrderStateAsync`（`:755`）→ SDK `GetOrderByOrderIdAsync`（`:760`）→ riot-sdk `cf58c11` `OrderClient.cs:269`，`:283` `return ToOrderRef(response.Result, "detailByOrderId")` → `:369` `ToOrderRef`，`:375` `string.IsNullOrWhiteSpace(result.UpperId)` 即抛 `RiotApiException`（`:380`，`order-ref-missing`）→ 网关 `:771` `IsSdkFailure`（`RiotCallFailureClassification.cs:27` 含 `RiotApiException`）吞掉，`OrderState=null` | **经过**，空 upperId 必失败 |

所以 RIoT 找到了单、也答了已结束，失败的是 SDK 对 `detailByOrderId` 也要求 `upperId`。行号是合入 `fp/v2-impl` `e53a94e5` 之后本分支的。

## 修法

修在 SDK，**不新增方法**：

- riot-sdk `62a827d`：`GetOrderByOrderIdAsync`（`detailByOrderId`）不再要求 `upperId`，空串原样，`null`／缺字段读成空串；`id`、`orderId`、`orderState` 与 `result` 仍然必须有（真实 RIoT 查无此单是 200 且无 `result`，仍是 `order-ref-missing`）。建单回执与 `detailByUpperId` 不变。C# 与 Python 一致。
- 本仓换 `RIoT.Sdk.Facade 0.2.0-fp.7`（从 `62a827d` 干净克隆打包，未打 tag、未发 release）。网关调用名不变。

一开始的做法是在 SDK 加一个只读状态的新方法 `GetOrderStateByOrderIdAsync`，被 `RiotCallAllowlistArchitectureTests` 拦下：RIoT 调用白名单（`vendor/8005-agv-program/docs/riot-call-allowlist.md`，按 SHA 钉住的批准文档）按端点逐条批准，第三栏写 Facade 方法名，新方法名要改批准文档。改成放宽原方法之后端点与方法名都不变，白名单不用动。这一版没有推送过，`0.2.0-fp.7` 只以现在这一版出现在远端。

## 受治理的人工出口（逐项）

| 要求 | 做法 | 钉住它的用例（`ForeignRunningOrderManualReleaseTests`） |
| --- | --- | --- |
| 不改库就能解除「外来单按住」 | `POST /api/field-ops/v1/foreign-running-order-releases`，判定在 `ForeignRunningOrderManualRelease` | `AnUnsettledOrderIsReleasedByANamedOperatorOnceRiotNoLongerListsIt` |
| 管理员凭据 | 故障人工恢复那把 Bearer 凭据，同一个开关 `VehicleFaultRecovery:enabled`，开关关着不挂路由 | `TheHostEntryAuthenticatesNamesTheVehicleAndMapsTheDecisionOntoHttp`（401／503）、`StationExclusivityReleaseEndpointsTests.TheEntryIsMappedOnlyWithTheFaultRecoverySwitchOn` |
| 具名、有角色的人 | R-11／R-13 名单（与人工清桩、等待点到点收尾同一份） | 拒绝矩阵 `not-authorized`、`operator-missing` |
| 审计 | 每次请求（拒绝也算）写管理员审计 `FOREIGN_RUNNING_ORDER_MANUAL_RELEASE`，对象种类 `ForeignRiotOrder`，detail 记办理人、角色、理由、核实记录、那一行、这一刻读到的 RIoT、码、结果 | 放车用例断言审计内容；拒绝矩阵每格断言一条失败审计 |
| 先回读 RIoT 确认那张单已不在运行 | RIoT 在事务外读：未完成列表读全且没有它（列表覆盖 RIoT 全部非终态 1、3、7、8、9、10）；按单号回读不是未完成状态 | 拒绝矩阵 `listing-incomplete`、`listing-unreachable`、`still-listed-running`／`suspended`／`queueing`、`read-suspended`、`read-running` |
| 只解 `UNSETTLED` | 别的挡车状态都表示单还在跑或取消在进行 | 拒绝矩阵 `not-unsettled` |
| 拒绝码有中文说明 | 11 个码，`Descriptions` 按反射核全 | `EveryReleaseRefusalCodeHasAChineseDescription`、拒绝矩阵每格 |
| 并发 | 事务里按状态与 `UpdatedAt` 复核，变了答 `STATE_CHANGED`、什么也不写 | `ARowChangedWhileRiotIsReadIsRefusedAndNothingIsWritten`（同时断言 RIoT 读在事务外） |
| 放开不是永久豁免 | 新状态 `RELEASED_BY_OPERATOR` 不挡车；监看把它当已终结，再跑在我们车上就重新认出、重新挡车 | `AReleasedOrderThatRunsOnOurVehicleAgainIsHeldAgain`；放车后再跑三轮仍是放开 |
| 看板 | 四句 `UNSETTLED` 说明（通用与 #611 的三种本服务端放弃的单）都以指向这个入口的同一句收尾，不再写「没有人工解除入口」 | `EveryUnsettledDashboardDescriptionPointsAtTheManualRelease` |

## 测试三项（票「要做出什么」第 3 条）

| 情形 | 用例 | 替身 |
| --- | --- | --- |
| upperId 空串、`orderState=2`，判定结束并放车 | `AWebPageOrderWithoutAnUpperIdReadsBackEndedThroughTheRealGatewayAndReleasesTheVehicle` | 真实网关 + SDK，HTTP 层替身 |
| 仍在运行时不放 | `AWebPageOrderStillRunningOrUnreadableKeepsTheVehicleHeld(still-running)`（列表里还在，哪怕按单号读到终结） | 同上 |
| 回读真失败（503）仍按原规则 | 同上 `(read-fails)`：过落定时间转 `UNSETTLED`，车继续挡住 | 同上 |
| 网关单测 | `HttpRiotMovementGatewayTests.AnOrderStateIsReadByItsRiotOrderId`：空串已结束、空串仍在跑、`null`、缺字段、真实 RIoT 查无此单（200 无 result）为无状态 | HTTP 层替身 |

`RecordingRiot` 直接实现端口、不经 SDK，测不出这个缺陷；所以复现用例走真实网关。

## 运行结果

见 `01-targeted-tests.txt`；变异见文末。

## 审查第一阶段跟改（C10 定）

| 项 | 改法 | 用例 |
| --- | --- | --- |
| M1 生产定义里入口不挂路由 | 入口不再挂在 `VehicleFaultRecovery:enabled` 下，改看自己的开关 `ForeignRunningOrderRelease:enabled`（`ForeignRunningOrderReleaseOptions`）；凭据仍是故障恢复那把（`VehicleFaultRecovery:credentialEnvironmentVariable`，部署脚本无论开关都写这个名字）。部署脚本：实例定义必须显式写 `foreignRunningOrderRelease.enabled`，覆盖层写 `ForeignRunningOrderRelease`，两个开关任一打开而拿不到凭据时装之前就拒绝；早于本票装的定义在停止方向容忍缺节。七份出厂定义都打开它，生产定义里故障恢复仍然关着 | `TheEntryIsMappedOnItsOwnSwitchWhateverFaultRecoverySays`（5 格）；`StationExclusivityReleaseEndpointsTests` 改为断言故障恢复开关不再挂它；`Test-ParallelInstance.ps1` 新增 3 个定义拒绝格、选项类镜像、2 个凭据格 |
| S1 放开后以挂起重现不再挡车 | 监看里放开的行在列表里重新出现、状态 8、执行车辆不是别人的车时，退回 `UNSETTLED` | `AReleasedOrderListedAgainSuspendedHoldsAgainUnlessAnotherVehicleRunsIt`（3 格） |
| S2 现场说明打到 MVP 端口 | 示例改成 v2 实例的 `172.19.205.222:58107`，变量 `CONTROL_SERVER_V2_FAULT_RECOVERY_CREDENTIAL`，注明 58007 是 MVP 的 | — |
| L1 放开不收尾取消审计 | 放开时像 `EndAsync` 一样收尾：读到 CANCELLED 记 Confirmed，否则记 Failed；读到终结时行上的取消结果一并改写；审计 detail 记 `cancelSettledAs` | `AReleaseSettlesTheCancelAttemptOfARowThatHadSentOne`（2 格） |
| L3 端口抛别的异常时 500 且无审计 | 读 RIoT 的两处改为除调用方取消外一律当「读不到」 | 拒绝矩阵 `listing-throws-other`；放车用例 `read-throws` |
| 审查第二阶段要的格 | P1 回读 1、7、9、10、11 拒绝；P2 列表有 Executing、回读为 null 拒绝（`ORDER_STILL_UNFINISHED`） | 拒绝矩阵新增 6 格 |

监看里对放开状态的处理现在有三处（行号是本分支当前的）：`ForeignRunningOrderSupervisor.cs:206` 查询条件不每轮读放开的行；`:318` 放开的行重新跑在我们车上时重新挡车；`:353` 放开的行不在场时直接返回（`:342` 是 S1 新加的挂起退回）。审查说的「两处」是 `:206` 与 `:353`；单独去掉、同时去掉各会怎样，变异结果见下面「变异」。

## 增量审查跟改（C10 定）

| 项 | 改法 | 用例 |
| --- | --- | --- |
| S-1 Host 启动时不查这个入口的凭据 | 不拒绝启动。入口开着而凭据变量为空时，启动时告警一次（事件 2235）；`EFFECTIVE_CONFIGURATION` 末尾加 `foreignRunningOrderRelease={Enabled, CredentialPresent}`，交给部署回读去核。七个占位符超过 `LoggerMessage.Define` 的上限，事件改为像 `Define` 一样直接把带 `{OriginalFormat}` 的状态交给日志器；试过日志源生成器，它把集合格式化成字符串，名册与目录批准值在 JSON 里变成字符串，`ParallelInstanceEffectiveConfigurationTests` 当场变红，所以没用它 | `TheStartupCheckWarnsOnlyWhenTheEntryIsOnWithoutItsCredential`（4 格）、`TheEffectiveConfigurationEventCarriesTheReleaseEntryAsExactlyTwoBooleans`；原有事件测试全过（其中一条「目录批准值是最后一项」改为「包含」） |
| L-1 回读为空时记 Failed | 改记 Unknown，与监看的 `Outcome()` 一致 | `AReleaseSettlesTheCancelAttemptOfARowThatHadSentOne` 的空回读格改为 Unknown |
| L-4 停止方向写错一节没有用例 | 照 `stopWritten` 先例补：缺节被拒（非停止方向）；停止方向下 `enabled` 未写、写成字符串、整节写成字符串都被拒 | `Test-ParallelInstance.ps1` 840 passed |

`Program.cs` 里先算放车入口的状态，再读目录批准值、紧接着记事件：部署自检有一条按源码文本核「Host 记的是它自己绑定的目录批准值」，要求这两行相邻。

## 变异（调度放行的本机时段，头 `5f30c0c0`；SDK 侧 `62a827d`）

每项只改一个文件，并核对确实恰好改了那一个文件；跑完只还原那个文件，核对工作区干净。原始记录在 `02-mutations.jsonl`（一行一项，带退出码、失败用例、耗时、还原后是否干净）。

| 项 | 变异 | 结果 | 退出码 | 变红的用例 |
| --- | --- | --- | --- | --- |
| a | `Directory.Packages.props` 换回 fp.6 | 红 | 1 | 真网关链路复现、网关回读里网页单那几格、`TheCheckedVersionIsTheVersionTheBuildConsumes`，共 6 |
| b1 | SDK C# 的 `detailByOrderId` 恢复 `requireUpperId` | 红 | 1 | 4 |
| b2 | SDK Python 恢复 `require_upper_id`（审查 P5，`python -I`） | 红 | 1 | 3 |
| c1 | 监看 `:206` 查询条件去掉放开状态 | **存活** | 0 | — |
| c2 | 监看 `:353` 直接返回去掉放开状态 | 红 | 1 | S1 用例 `on-another-vehicle` 格 |
| c3 | `:206` 与 `:353` 同时去掉 | 红 | 1 | 4 |
| d1 | 入口去掉列表检查 | 红 | 1 | 拒绝矩阵 4 格 |
| d2 | 入口回读检查改窄成只拒挂起（审查 P1） | 红 | 1 | 拒绝矩阵 6 格 |
| d3 | 入口去掉事务复核 | 红 | 1 | `ARowChangedWhileRiotIsReadIsRefusedAndNothingIsWritten` |
| d4 | 入口不再只解 `UNSETTLED` | 红 | 1 | 2 |
| e1 | 映射改读 `VehicleFaultRecovery:enabled` | 红 | 1 | 独立开关用例 4 格 |
| e2 | S1 去掉 `!ExecutedByAVehicleNotOurs` | 红 | 1 | S1 用例 `on-another-vehicle` 格 |
| e3 | `Resolve-ParallelFaultRecoveryCredential` 只看 `vehicleFaultRecovery` | 红 | 1 | `release entry on, recovery off, no credential` |
| e4 | 停止方向容忍缺节那处改成只看 `$ForStopDirection`（按内容定位，审查说的 `:1074`） | 红 | 1 | 照 `stopWritten` 补的 3 格 |

过程中的一处差错：a 那一项的驱动在 Windows 下用 bash 语法还原 restore 改写的 lock 文件，没有生效；驱动发现工作区不干净就停了。核对那 4 个 lock 文件只是 RIoT.Sdk 退回 fp.6，`git checkout` 还原、工作区干净后修好驱动，从 b1 接着跑。

**c1 为什么存活：纵深防御，没有只归 `:206` 管的场景（读代码，加 c2／c3 的实测）。** 放开的行一共两种处境：

| 处境 | `:206`（查询条件）管不管 | `:353`（不在场直接返回）管不管 | 只去掉 `:206` | 只去掉 `:353` | 两处都去掉 |
| --- | --- | --- | --- | --- | --- |
| 单不在列表里（被删、已结束） | 管：行根本不读出来 | 管：读出来也直接返回，不回读 | 不变（`:353` 兜住） | 不变（`:206` 兜住） | 每轮回读，读不出终结，10 秒后翻回 `UNSETTLED`、重新挡车 |
| 单在列表里，但不在我们车上跑、也不是挂起在我们车上 | 不管：列表里有它，行照样读出来 | 管 | 不变 | 落到回读路径被翻回 `UNSETTLED` | 同左 |

（单在列表里、跑在我们车上，由 `:318` 重新挡车；以挂起出现在我们车上，由 `:342` 退回 `UNSETTLED`；这两种与这两处无关。）`rows` 的其余用处——`RecogniseAsync`、`ChaseAbandonedChargeOrdersAsync` 的「已知」集合、取消循环——只看列表里的单或只挑 `DETECTED`／`CANCEL_DECIDED`／`HELD_CANCEL_NOT_AUTHORIZED` 的行，放开的行多读出来也不改它们的结果。

所以 `:206` 没有独占的场景：第一种处境它与 `:353` 互为备份，第二种处境只有 `:353` 管。实测对得上：c1 存活；c2 只红「在列表里、别人的车在执行」那一格（第二种处境）；c3 多红了放车用例 `AnUnsettledOrderIsReleasedByANamedOperatorOnceRiotNoLongerListsIt` 里「放车后再跑三轮仍是放开」那一段（第一种处境，单不在列表里），这正是第一种处境的用例，只在两道都拆掉时才红。不补用例。

**c2 变红，和审查的推测不同。** 审查推测 `:206`、`:353` 各自单独去掉都会存活。实测 `:353` 单独去掉就红：S1 用例的 `on-another-vehicle` 格里，那张单在列表里（所以 `:206` 照样把行读出来），执行车辆是别人的车（所以 `:342` 不接），落到回读路径，读到 8 不是终结，转回 `UNSETTLED`，与「别人的车执行就不碰」相反。只有放开的行既在列表里、又不在我们车上跑、又不是挂起在我们车上时，`:353` 才是唯一的一道。

## 一条既有断言被改弱（增量审查要求写明）

`ParallelInstanceEffectiveConfigurationTests.EffectiveConfigurationEventCarriesTheCatalogApprovalAsExactlyTwoStrings` 原来断言模板以 ` mapStationCatalog={MapStationCatalog}` **结尾**，本票改成**包含**。

- **它原本防的是什么（推的，依据是 #531 引入它时的事件说明）**：`EffectiveConfigurationEvent` 的说明写着每一项都是「追加」——#571 的名册、#638 的清桩开关、#531 的目录批准值依次接在后面，「三个字段保住名字、位置与事件号」。结尾断言钉的是「目录批准值追加在最后，前面各项的位置没被挪动」。部署回读本身按属性名取值（`Find-ParallelEffectiveConfiguration` 读 `$event['MapStationCatalog']` 等，`ParallelInstance.psm1` 约 `:2080`～`:2110`），不按位置。
- **改弱之后由什么接替**：`Test-ParallelInstance.ps1` 的源码文本检查 `catalog read-back: the Host source logs the pair under the template the module reads` 要求 Host 源码里含有完整前缀 `$catalogTemplate`（从 `EFFECTIVE_CONFIGURATION allowedWorkTypes=...` 一直到 ` mapStationCatalog={MapStationCatalog}`，连续一段），所以前面各项的顺序与相邻关系仍被钉住；本票新用例 `TheEffectiveConfigurationEventCarriesTheReleaseEntryAsExactlyTwoBooleans` 钉住新项以 ` foreignRunningOrderRelease={ForeignRunningOrderRelease}` 结尾，即仍是「只在末尾追加」。原断言里对目录批准值内容（恰好两个键、`hh:mm:ss` 或 null）的检查一字未动。
