# 批次7-03（control-server#208）报文对照的红证据

对照测试是本票的主证据，所以它必须能被证明「会红」。这里注入两种故障，各跑一次
`Batch7StopDrivenAdvanceWireParityTests`，记下失败原文。两次都在**产品代码零改动的基线**
`b7-03/stop-driven-advance@0c4b9d2d` 上做，注入后随即还原。

两次注入都只动一行，都是本票真会改到的东西。

## 01 关卡停靠的一个消息 id 换了派生用途串

`WireToGateStore.ToRuntimeRow` 里 `GateWorklistMessageId = Id("gate-worklist")` 改成
`Id("gate-worklist-derived-the-new-way")`。这模拟本票把 id「改从停靠行读」时算错了一个。

结果：5 条里红 2 条——`ANormalJourneySendsTheSameElevenLinesAndTheSameTwoOrders` 与
`TwoJourneysOnOneVehicleKeepTheirRevisionStreamsMonotonic`。差异定位到第 08 行，报文类型
本身对不上（期望 `VehicleBusinessStateSnapshot`，实际 `CurrentStopWorklistSnapshot`）——
id 一变，发件箱的排序就变了。

另 3 条绿，因为它们都没走到关卡停靠（站点期限与扫码前取消在取货停靠就终结了，
离站核验过期那条停在离站前）。**这正是对照有定位能力的样子**：它不是笼统地全红，
红的恰好是经过那个 id 的路径。

## 02 按车修订号的步长改掉

`WireToGateStore.RevisionsPerJourney` 从 `2` 改成 `3`。这模拟本票把修订号改取按车计数器时
步长搬错。

结果：5 条里只红 1 条——`TwoJourneysOnOneVehicleKeepTheirRevisionStreamsMonotonic`。
差异是载荷摘要不同（`payloadSha256` 对不上），因为第二趟的修订号整体偏了。

**只红这一条，是这次注入最值得记的地方**：步长决定的是「下一趟从哪里起步」，单趟旅程
根本看不见它。一份只跑一趟的对照会对这种错误全绿放行——所以「同车连续两趟」不是凑数的
第二条用例，它是唯一能看见步长的那一条。

## 03 按车计数器的语义改成「已发出的最高一号」

`SeedSnapshotRevisionsAsync` 里车辆业务状态那一条从 `+ RevisionsPerJourney`（本趟基准加 2）改成 `+ 1`。
这模拟一种看起来更「正确」的计数器语义：计数器存已经发出去的最高一号，下一趟从它加一起步。

**为什么值得单独注一次。** 本票把「下一趟从哪里起步」的来源换成了按车计数器，而计数器里存的是**本趟的基准**，
不是「已经发出去的最高一号」。在一趟跑满的旅程上两者只差一号，怎么读都看不出分别；只有在半途终结的旅程上才分岔——
站点期限结束的那一趟只发了取货停靠的三条快照，「已发出的最高」会低于基准，下一趟的基准就跟着往回缩。

**往回缩不会让车载端断会话**（号还是在涨），所以 `SNAPSHOT_REVISION_REGRESSION` 抓不到它。也就是说，这是一种
「全绿地改变行为」的改法——正是预重构票最该防的那种。

结果：红 2 条。

- `AJourneyEndedHalfwayStillCostsTheVehicleAWholeJourneyOfRevisions`：期望 3、实际 2。这条是专为它写的守卫。
- `TwoJourneysOnOneVehicleKeepTheirRevisionStreamsMonotonic`：第二趟的修订号整体低了一号。

跑满两趟的那条对照 pin 这次也红了，但**不能指望它**：它两趟都跑满，只是因为这次注入把公式整个改了才连带红。
把注入换成「只在半途终结时缩」，红的就只剩前一条。
