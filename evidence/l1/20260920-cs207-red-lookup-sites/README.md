# cs#207 红证据：非锚需求的三处读取（改动前）

提交 `62d41fe1`：新增查找口 `DemandJourneyLookup` 与 `Batch7DemandJourneyLookupTests`，但各处读取还没改走查找口。

在库里直接造一趟两条需求的旅程（锚需求 `D-7201` 与后加的 `D-7202`），3 条测试红，原文见 `dotnet-test.txt`：

| 测试 | 失败原文 | 说明 |
| --- | --- | --- |
| `AnOperationOfADemandAddedToAJourneyThatNeedsRecoveryHoldsItsVehicle` | `Expected: RecoveryRequired` / `Actual: Ready` | 会话就绪判定按 `StationOperations ⋈ JourneyRuntimes ON DemandId` 找车，非锚需求的操作待恢复时车照样判就绪 |
| `APreparedOperationOfADemandAddedToAJourneyExplainsTheVehiclesOwnOpenLock` | `Expected: Ready` / `Actual: RecoveryRequired` | 同一个 join 的另一处：非锚需求自己下发的开锁解释不了车报的锁未关 |
| `EveryDemandAnOpenJourneyCarriesCountsAsInFlightForItsTaskType` | `Expected: 2` / `Actual: 1` | 任务类型暂停审计的在途计数按旅程行算，漏掉非锚需求 |

改走查找口之后（`ea9d10a0`）这 3 条转绿，单需求的 14 份 `ZeroChangePin` 基线不变。
