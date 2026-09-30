# 红证据：下达离点订单即释放

在提交之前的工作树上跑，并注入一处缺陷（`SUMMARY.md` 里的 `controlServerCommit` 是基线 `99c35544`，不是被测代码）。

注入：`FixedStationExclusivitySweep.HasDepartedAsync` 对等待点行，只要这辆车有另一趟未完成的旅程就答「已离点」。

事先写下的预期：只有 `L2-WPR-04`（离点订单已下达、车还在 214，占用不放）红，其余五条绿。结果与预期一致：`L2-WPR-04` 读到 214 已无独占行
（`(none)`）。`idle-return.log` 里 10:31:38 车到 214 转为占用，10:31:41 搬运单一下达、车还没动，214 就以离点证据释放了。

同一注入在 L1 上红两格：`CvWaitingPointIdleReturnReleasesTheWaitingPointOnDepartureEvidence`、
`TheOccupancyStaysWhenTheDepartingOrderIsIssuedAndIsReleasedOnlyOnDepartureEvidence`。
