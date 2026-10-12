# cs#645 证据摘要：占用未知时日志写明是哪一样事实没读到

## 问题

cs#566 续场三（2026-10-12）11:03:58，agv03 对 211 的充电承诺被出发前复核撤回，事件 2254 只写了
`CHARGER_OCCUPANCY_UNKNOWN`。占用要靠三样事实：车位置、RIoT 未结束单清单、订单目的站。日志没写是哪一样没读全，
现场只能推测是清单分页读不全，无法证实（`evidence/field/20261012-cs566c-contention/20a-withdrawal-1103-analysis.md`）。

## 改动（只改诊断，不改判定）

- 撤回事件 2254：码为 `CHARGER_OCCUPANCY_UNKNOWN` 时细节接 `; unknown: ...`。
- 同族事件：分配拒绝 2241、等桩告警 2246、放桩告警 2247／2249 的细节同样写明未知来源。
- 写法：
  - 车位置：`AGV-2[<vehicleKey>]:VEHICLE_POSITION_UNREADABLE (HttpRequestException)`、`_STALE (...)`、`_MISSING (...)`；
  - 清单覆盖：`ORDER_LISTING_INCOMPLETE (page 2 of 3 does not add up (...))`、`truncated: total N needs M pages, past the cap of 20; only page 1 was read`、
    `page 1 cannot be paged over (...)`、`RIoT could not be read at page 2 of 3 (...)`、`k of N records carry no orderId`；
  - 订单目的站：`ORDER_<orderId>[<vehicleKey>]:DESTINATION_UNREADABLE (upperId ...: Unknown)`、`:NO_UPPER_ID`。
- 判「结论变了没有」（2241 要不要重记、2246 的等待键）只比不带页号与数字的稳定条目；日志与看板写完整细节（审查 S1）。

判定不变：撤回与否只看码；`ChargerOccupancySnapshot.Judge` 未改；未知即不分配（REQ-0170）不变。读取流程、分页校验、页数上限未改。

## 测试

| 用例 | 守的是什么 |
| --- | --- |
| `ChargingExecutionTests.AWithdrawalBecauseOccupancyIsUnknownNamesWhichFactCouldNotBeRead`（7 格） | 每种未知来源，2254 细节写明是哪一样、哪辆车 |
| `ChargingExecutionTests.TheWithdrawalVerdictForUnknownOccupancyIsWordForWordWhatItWasBefore`（7 格） | 撤回码、周期结束原因、预占释放原因按字面不变；不建单；下一轮仍不分配 |
| `ChargingExecutionTests.AWithdrawalForAKnownReasonCarriesOnlyThatCode` | 对照：非未知撤回细节只有那个码 |
| `ChargingExecutionTests.AReservationThatCannotBeReleasedForAnUnknownFactNamesTheFact`（2 格） | 2247 写明清单哪一页没对上／哪辆车读不到 |
| `ChargingAllocationTests.AnIncompleteListingWhoseNumbersMoveEveryRoundIsLoggedOnceAndTheBoardShowsTheLatestWhy` | total 逐轮变化 4 轮：2241、2246 各只记 1 次；看板细节是最近一轮的完整说法 |
| `HttpRiotMovementGatewayTests.TheUnfinishedOrderListingIsIncompleteWhenRiotDoesNotAccountForEveryOrder`（4 格） | 读不全原因：分页不全、首页无法分页、读失败（带页号）、缺 orderId |
| `HttpRiotMovementGatewayTests.APagedReadThatDoesNotAddUpIsIncomplete`（清单 6 格） | 截断、总数变、页错位、短页、记录重复各自的原因 |
| `HttpRiotMovementGatewayTests.AListingThatFailsPartWayNamesThePageItFailedOn` | 第 2 页读失败时原因写 `at page 2 of 3`（审查 L1） |

## 变异表

每个变异单独改源码、只跑对应用例、跑完还原。全部取红。

| 变异 | 改法 | 跑的用例 | 结果 |
| --- | --- | --- | --- |
| D | 引擎撤回时只传码，不传细节 | 2254 两组 15 格 | 14 红 1 绿（绿的是非未知对照） |
| G | 2241 细节改回只拼稳定条目 | `AnIncompleteListing…` | 红 |
| R | `ChargingAllocationBoard.Record` 改回比较完整细节 | `AnIncompleteListing…` | 红（2241 记 4 次） |
| W | 等待键改用完整细节 | `AnIncompleteListing…` | 红（2246 逐轮重记） |
| E | `WhyNotYetReleasable`「桩未确认空闲」分支去掉 unknown | `AReservationThatCannotBeReleased…` 2 格 | 清单那格红 |
| F | 同方法「车读不到」分支改回旧文本 | `AReservationThatCannotBeReleased…` 2 格 | 车读不到那格红 |
| J | 「page 1 cannot be paged over」文案改掉 | 网关读不全 4 格 | 首页那格红 |
| L1 | 异常时原因不带页号 | 网关读不全 4 格 + 中途失败 1 格 | 2 红（首页读失败、第 2 页读失败） |

脚本：本地临时脚本逐个替换、`dotnet test --filter "FullyQualifiedName~..."`、还原，未入库。

## 后续（不在本票）

- 占用读与出发前复核在清单读不全时都不重读。车载端安全读 `ReadForOnboardAsync` 已经在预算内重读清单
  （`NonFinalOrderReadRetries` 默认 2，`OnboardSafetyProjectionOptions.cs:23`；审查读到的事实）。
- 是否给占用读加重读，调度定为等本票合入、现场有了读不全的频率统计再说。
