# control-server#527 证据

## 现场（读到的，调度机 `estop-1009/` 日志副本）

- 05:22:08Z 第一次急停，原因 `STOP_PROOF_POSITION_UNKNOWN,STOP_PROOF_TOO_FEW_SAMPLES`（不含运动）。
- 05:22:10–05:26:08 每轮告警都带 `ORDER_HOLD_PENDING`；05:26:04 门锁自动解除（`EMERGENCY_DOOR_CAUSE_REMOVED`）那一轮才回查到按住确认。
- 05:26:08–05:33:26 只剩 `STOP_PROOF_POSITION_UNKNOWN`，因门锁解除豁免不升级。
- 05:33:26–05:34:28 急停 9 次（`RiotOrderCommandAudit` 中 triggerEmergency 第 2–10 次），原因均为 `STOP_PROOF_MOTION_OBSERVED,STOP_PROOF_POSITION_UNKNOWN`；其间 cancelEmergency 14 次（第 2–15 次），每次都是门锁放行。

## 修前红（`red.txt`）

只加测试、不改产品代码，在 `fp/v2-impl@bf7d4c7f` 上跑 `InTransitDoorEmergencyReleaseTests`：3 红 59 绿。

- `AVehicleStoppedForMotionAfterTheDoorReleaseIsNotReleasedOnTheDoorsAgain` 两格：闩锁下读数 `MT_RUNNING`、`MT_PAUSED` 都多发了一次 cancelEmergency（`Expected: 1, Actual: 2`）。
- `AHoldIssuedInTheEscalatingRoundIsReadBackWithoutWaitingForTheDoors`：按住停在 `Pending`。
- `ADoorReleaseStillHappensWhenNoMotionFollowedIt` 两格修前修后都绿：它们是护栏，守住门锁自动解除的既有行为。

## 修后绿（`green.txt`）

故障、急停、门锁、重建相关的定向测试 519 条全绿。按调度 10-09 转达的流程，本机不跑全量，由 CI 那一轮充当全量。
