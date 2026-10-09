# 变异（提交 37d9d6fb 之上，跑完用 git checkout 还原、重新编译）

过滤：`FullyQualifiedName~GetsOutOnceItsOrderIsEndedInRiot|FullyQualifiedName~TheAllowanceIsGivenOnlyPastTheOwnConfirmedHold`

| 编号 | 改法 | 结果 |
| --- | --- | --- |
| M1 | `DoorReleaseAllowance` 零单分支的 `history.DoorReleasedAfterMotion` 改成 `history.MovedAfterDoorRelease`（运动急停后零单放行也收回，即第一版的做法） | 杀死：`cancelled-moved-after-door-release`（`Expected: EmergencyReleaseAllowance { FaultGeneration = 3, HeldOrderId =  }`，`Actual: null`）；`GetsOutOnceItsOrderIsEndedInRiot` 两格（`Expected: 2`，`Actual: 1`） |
| M2 | 删去零单分支的 `history.DoorReleasedAfterMotion ||`（零单放行不限次数） | 杀死：`cancelled-released-again-after-motion`（`Expected: null`）；`GetsOutOnceItsOrderIsEndedInRiot(after: "moves-again")`（`Expected: 2`，`Actual: 3`） |
