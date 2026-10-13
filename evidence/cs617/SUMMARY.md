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

见 `01-targeted-tests.txt`。变异待调度给时段后补在这里。
