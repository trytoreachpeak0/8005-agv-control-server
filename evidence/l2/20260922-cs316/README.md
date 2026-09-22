# control-server#316 证据：在途单挂起、状态不明、被人工取消

票：control-server#316（#299 拆出的 T1，上真车前）。修复提交 `8a1a840b`，L2 场景提交 `3cbae94d`。

## L1

| 文件 | 内容 |
| --- | --- |
| `l1/red-before-fix-runtime.txt` | 测试提交 `1ba63371`（fp/v2-impl@8ec088b1 加新用例、不含修复）上 13 条红，每条都是「码为空」「没有释放」「追加进去了」这一类，不是编译或夹具错误 |
| `l1/red-before-fix-dashboard.txt` | 测试提交 `05a197e0` 上看板说明 3 条红：`... has no description` |
| `l1/reverse-verification.py`、`.log` | 在修复之上逐项退回、各跑一遍相关用例（替换不是恰好 1 处就退出，打印 diff 行数）。结果见下表 |

反向验证：

| 退回什么 | 红几条 | 说明 |
| --- | --- | --- |
| M1 引擎从不写码 | 13 | 与修前相同的 13 条 |
| M2 只去掉 underWay 那一道排除 | 0 | 预期内：`ReadEnRoutePlanAsync` 那一道仍在，挡住追加 |
| M3 只去掉 `ReadEnRoutePlanAsync` 那一道 | 0 | 预期内：underWay 那一道仍在 |
| M2+M3 两道都去掉 | 1 | `AJourneyWhoseOrderHangsTakesNoAppendedDemand`。两道是纵深，行为判据只守二者之和 |
| M4 释放不用「当前单已终结」触发 | 3 | 取货单取消／删除后释放两条、多需求拒绝一条 |
| M5 释放只信码、不再问 RIoT | 1 | `AnEndedReasonOverALiveOrderReleasesNothingAndCancelsNothing`：会对活单发取消 |
| M6 车载端静默覆盖 ORDER_HANG | 1 | `ASilentSessionDoesNotOverwriteAHangingOrderAsTheReason` |
| M7 订单继续后不清码 | 1 | `AContinuedOrderClearsTheReasonAndTheJourneyArrives` |

## 合成 L2（本机，经 `Invoke-HeavyLocal.ps1 -Ticket cs#316`）

每次运行只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`，日志与快照没有入库。

| 目录 | 代码 | 结论 |
| --- | --- | --- |
| `red-base-8ec088b1/in-transit-order-hang-continue-001` | fp/v2-impl@8ec088b1，场景脚本从 scratchpad 副本运行 | FAIL：60 秒等不到 `ORDER_HANG`，阻断码为空 |
| `red-base-8ec088b1/in-transit-order-cancelled-redispatch-001` | 同上 | FAIL：60 秒等不到释放，旅程停在 `AwaitingPickupArrival`、码为空 |
| `green-3cbae94d/`（8 条） | 本分支 `3cbae94d` | 全部 PASS：两条新场景，加上故障与急停路径 `command-surface-order-hold`、`emergency-stop-single-trigger`、`emergency-stop-operator-release`，释放路径 `reassign-when-vehicle-ineligible`，失联码 `onboard-silent-liveness-loss`，追加 `multi-stop-append-same-zone` |
