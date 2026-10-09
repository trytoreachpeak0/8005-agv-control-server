# 变异（提交 37d9d6fb 之上，跑完用 git checkout 还原、重新编译）

过滤：`FullyQualifiedName~GetsOutOnceItsOrderIsEndedInRiot|FullyQualifiedName~TheAllowanceIsGivenOnlyPastTheOwnConfirmedHold`

| 编号 | 改法 | 结果 |
| --- | --- | --- |
| M1 | `DoorReleaseAllowance` 零单分支的 `history.DoorReleasedAfterMotion` 改成 `history.MovedAfterDoorRelease`（运动急停后零单放行也收回，即第一版的做法） | 杀死：`cancelled-moved-after-door-release`（`Expected: EmergencyReleaseAllowance { FaultGeneration = 3, HeldOrderId =  }`，`Actual: null`）；`GetsOutOnceItsOrderIsEndedInRiot` 两格（`Expected: 2`，`Actual: 1`） |
| M2 | 删去零单分支的 `history.DoorReleasedAfterMotion ||`（零单放行不限次数） | 杀死：`cancelled-released-again-after-motion`（`Expected: null`）；`GetsOutOnceItsOrderIsEndedInRiot(after: "moves-again")`（`Expected: 2`，`Actual: 3`） |

## 审查轮（提交 aa9180a7 之上，同样跑完 git checkout 还原）

过滤：`FullyQualifiedName~TheDoorReleaseHistoryCountsOnlyMotionAfterAReleaseThatTookEffect|FullyQualifiedName~OnlyAReasonThatCannotRuleOutMotionWithdrawsTheDoorRelease|FullyQualifiedName~ItCannotRuleOutAfterTheDoorReleaseIsNotReleasedOnTheDoorsAgain|FullyQualifiedName~GetsOutOnceItsOrderIsEndedInRiot|FullyQualifiedName~TheAllowanceIsGivenOnlyPastTheOwnConfirmedHold`

| 编号 | 改法 | 结果 |
| --- | --- | --- |
| N2 | `CannotExcludeMotion` 去掉 `STOP_PROOF_POSITION_CHANGED` | 杀死：`OnlyAReasonThatCannotRuleOutMotionWithdrawsTheDoorRelease(reason: "STOP_PROOF_POSITION_CHANGED", withdraws: True)` |
| N3 | `MovedAgainAfterLaterRelease` 改成 `doorReleases.Length > 1` | 杀死：`TheDoorReleaseHistoryCountsOnlyMotionAfterAReleaseThatTookEffect(script: "DMDS")`；`GetsOutOnceItsOrderIsEndedInRiot(after: "doors-again")` |
| M5 | `DoorReleaseHistoryAsync` 去掉门锁解除的 `Outcome == Confirmed` 过滤 | 杀死：`TheDoorReleaseHistoryCountsOnlyMotionAfterAReleaseThatTookEffect(script: "dM")` |

| M2' | 第一轮 M2 在新字段上的同一改法：零单分支删去 `history.MovedAgainAfterLaterRelease ||`（零单放行不限次数） | 杀死：`cancelled-moved-again-after-a-later-release`（`Expected: null`）；`GetsOutOnceItsOrderIsEndedInRiot(after: "moves-again")`（`Expected: 2`，`Actual: 3`） |

第一轮的 M1、M2 写的是改名前的字段 `DoorReleasedAfterMotion`；它在审查轮改名为 `MovedAgainAfterLaterRelease`，含义也改成「零单解除之后又有运动急停」，所以 M2 在新字段上重跑了一次（M2'）。

## 复核轮（提交 3a3ab4f6、9b21da62 之上，同样跑完 git checkout 还原）

过滤：`FullyQualifiedName~InTransitDoor|FullyQualifiedName~TheDoorReleaseHistory|FullyQualifiedName~VehicleFault|FullyQualifiedName~StopProof`（295 条）

| 编号 | 改法 | 结果 |
| --- | --- | --- |
| M6 | `MovedAgainAfterLaterRelease` 里 `trigger.IssuedAt > laterRelease.IssuedAt` 改成 `> firstMotionStop.IssuedAt` | 杀死：`TheDoorReleaseHistoryCountsOnlyMotionAfterAReleaseThatTookEffect(script: "DMMD")` |
| M7 | 删去 `ledger.StartAfterRelease(...)`（解除后不换新窗口，即复核前的实现） | 杀死：`ADoorsOnlyStopRightAfterAReleaseIsNotJudgedOnSamplesFromUnderTheLatch` 四格；`GetsOutOnceItsOrderIsEndedInRiot(after: "doors-again")` |
| M8 | `StartAfterRelease` 去掉「同一次解除只清一次」的判断（每轮都清空） | 第一次跑存活（295 条全绿）；补 `APositionChangeAfterTheDoorReleaseIsStillSeenAcrossTheFreshWindow`（提交 9b21da62）后杀死：`Expected: 2, Actual: 1` |
