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

## 第二次提交：零单放行保留一次

`AVehicleStoppedForMotionAfterTheDoorReleaseGetsOutOnceItsOrderIsEndedInRiot` 两格守着有货车的人工出口；两个方向的变异见 `mutations.md`。

## 修后绿（`green.txt`）

故障、急停、门锁、重建相关的定向测试 523 条全绿；改看板文案之后看板相关 332 条全绿。按调度 10-09 转达的流程，本机不跑全量，由 CI 那一轮充当全量。

## 审查轮（M-1、S-1、S-2、S-3）

- 测试先行：`49ef80d6`（新用例，加两处只改名不改行为）与 `7ace03b6`（探针 E 用例的采样窗口修正）；实现在 `aa9180a7`。
- 修前红（`red-review.txt`）：产品代码退回 `7ace03b6` 那一版，6 红 42 绿——读不到运动、读数过期没被当作运动（各 1）；探针 D 两格解除 4 次（`Expected: 1, Actual: 4`）；探针 E 门锁锁好后没再解除（`Expected: 3, Actual: 2`）；`DMDS` 被误判为零单解除后又动了。
- 修后绿（`green.txt`）：故障、急停、门锁、重建、看板相关定向测试 877 条全绿。
- 变异 N2、N3、M5、M2' 全部杀死（`mutations.md`）。
