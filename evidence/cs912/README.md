# control-server#912 证据

车载程序停掉以后，服务端在最后一条消息还不到 `MaximumEvidenceAge`（30 秒）时仍把车当就绪，11-23 agv03 因此在没有车载程序的情况下被派空闲返回开到 214。修复：派新用途、补建与重建、空闲返回/充电/清桩出发，都要求这一代会话此刻有活连接。

## 变异取红

`mutations/`：每个变异只改一个文件的一处（脚本核对恰好一个文件改动），跑 `MultiVehicleExecutionTests.DisconnectedOnboard` 的用例（第三轮加 `OwnOrderRebuildTests`），跑完只还原那一个文件。每次变异前都已提交。结果见 `summary.txt`。

| 变异 | 改了什么 | 红的用例 |
| --- | --- | --- |
| M1-reader-ignores-connection | `ReadOnboardFactsForNewPurposeAsync` 不看连接（等于缺陷版本） | 空闲返回、充电、搬运三条 |
| M2-round-read-unguarded | 派车轮入轮读取改回旧方法 | 同上三条 |
| M3-final-recheck-unguarded | 受理前复核改回旧方法 | 第一轮没杀掉（入轮那一处先挡住）；补 `AConnectionThatEndsWhileTheDemandIsBeingAcceptedStopsTheAcceptance` 后第二轮红 |
| M4-departure-gate-unguarded | 出发闸不看连接（在第一个提交 `f057737b3` 上跑；`3162df2b3` 之后车况复核也看连接，这条用例不再单独守这一格，见 M4b） | 承诺后断线那条 |
| M5-vehicle-condition-unguarded | `VehicleConditionReasonsAsync` 改回旧读法 | `OwnOrderRebuildTests.ARebuildDueRightAfterTheOnboardDisconnectedWaitsUntilItReconnects` |
| M6-departure-leg-unguarded（审查 L1） | 搬运离站建单前不看连接 | `JourneyRuntimeWorkerDepartureSafetyTests.ASafeAnswerHeldBackByTheCreateGateDoesNotSendOffAVehicleWhoseOnboardDisconnected` |
| M4b-session-gap-cell-unguarded（审查 L2/L3，在 `dce9a7653` 上） | 只去掉 `SessionGapsAsync` 的连接那一格 | `AConnectionThatEndsBeforeTheChargingAllocationChecksTheGateCommitsNoCharger` |

## L2

场景 `onboard-disconnected-takes-no-new-purpose`（合成），本机完整运行（含构建）。

- `l2/green-01/`：提交 `ef60f6cf0` 上 PASS，5/5 判据；断线后 20.1 秒、20 轮里没有任何用途占有、旅程、订单意图，积压原因 `ONBOARD_FACTS_NOT_READY`；重连后会话代 1→2，需求派给这台车。
- `l2/green-02/`：PR #913 第一次 CI（l2 45215158195）红在 L2-ODN-01：服务端头几轮赶在场景第一条命令之前，在关卡上给车承诺了空闲返回。加 setup 键 `RiotStartStationId = 0` 后，在 `46e0dac42` 上本机 PASS，5/5。
- `l2/red-M1/`：提交 `768e0213d`（比 `ef60f6cf0` 只多了 M5 的证据文件，产品代码相同）加 M1（`M1-reader-ignores-connection.diff`），FAIL：断线后 1 秒那一轮就受理了需求（`claims=1 journeys=1 intents=1`），积压行变成 `ACCEPTED`——与 agv03 同一个缺陷。

日志与快照目录已删，只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`。
