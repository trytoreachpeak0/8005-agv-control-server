# 票面点名、而此前没有判据的三件事（审查严重 2／3／4）

三条都是票面明写要求、实现做了、但**没有任何判据**的。补的用例各自做了反向验证。

## 严重 2：追加重放的冲突分支一条判据都没有

`WireToGateStore.AssertAppendReplayMatchesAsync` 有三条冲突路径——需求身份四项不符、归属指向别的旅程或别的停靠、
以及「序列不同判冲突」的那段循环。既有的幂等用例两次传的是**同一个** `JourneyAppendPlan`，走的只是「判同」那一半。

**审查给出的错误实现**：把整个方法体换成 `return;`，三条持久化用例、`Batch7ThreeStopJourneyTests` 与四条 L2 全绿。

补了两条：

- `AReplayedAppendWhoseSequenceDisagreesWithWhatIsStoredIsRefused` — 重放算出的插入位与已落下的不一致时判冲突，
  且**拒绝要干净**（库里一个字没动，否则「判冲突」就成了「先改一半再抛」）。
- `AReplayedAppendCarryingDifferentContentUnderTheSameDemandIdIsRefused` — 同一个需求 id 带着不同内容回来时判冲突。

**反向验证**（`inject-replay-check-emptied.txt`）：把那个方法掏空，**只有新加的这两条红**，原有四条绿。

> **这一次差点被一个假绿骗过去。**第一版注入写成 `return;` 之后编译失败（`CA1822`：方法不再访问实例数据），
> 而 `dotnet test --no-build` 照样跑完并报 6 条全绿——**跑的是旧二进制**。若只看测试输出，结论会是
> 「新用例没有判别力」，正好反了。改成让方法体里留一次 `dbContext` 查询之后编译才通过，红也才出现。
> 同一形状本票已是第三次（前两次是 `CA1848`、探针没插进去）。

## 严重 3：「同名不同源」那条判据没有写

旅程行与停靠行上都有一列 `SublotRequestMessageId`，受理时从前者搬到后者，此后一直恒等——**直到清单升版**：
升版换一个新 id 写在停靠行上，而旅程行那一列还停在受理时那个。读旅程行的那一版会结算不到当前这条录入请求，
于是它被补发进下一个会话，车载端把它当成内容已变的业务 id 而断会话。

**票面点名要求在「两列已分岔」的状态下断言**，而此前没有任何本票新测试断过录入请求被结算。审查还指出：
既有的 `Batch7MultiDemandCancellationTests` 那条发生在第一条需求还在 `LOADING` 时，`DoneAt` 为 0，
**两列仍然恒等**——在那个状态下写的断言分不出谁读了谁。

补的是 `JourneyStopEntryRequestIdTests.ClosingTheJourneySettlesTheStopsCurrentEntryRequestNotTheJourneyRowsOriginal`。
**关键在夹具**：两列只有在 `WorklistRevisionAt = First + DoneAt` 里 `DoneAt > 0` 时才分岔，所以把锚需求推到 `LOADED`，
并且**先断言两列真的不相等**——没有那一句，这条用例哪天夹具退回恒等状态也照样绿。

**反向验证**（`inject-ignore-the-cursor-id.txt`）：按审查给的错误实现，把新重载里的 `currentSublotRequestMessageId`
忽略掉、仍旧查 `runtime.SublotRequestMessageId`，**108 条里只有新加的这一条红**。红的不是别人，绿的包括
那条多需求取消用例——**审查说它在恒等状态下分辨不出，这次实测证实了**。

## 严重 4：L2-MSA-06 那条断言不认需求

`multi-stop-append-same-zone.ps1` 的 L2-MSA-06 判据文字写的是「序位 1 仍是**第一条需求**的取货站（REQ-0196）」，
实际只判了「序位 1 是一个 `PICKUP`、且不是关卡」。

**这次跑出来的实际停靠是 `1:PICKUP@12 2:PICKUP@11 3:UNLOAD@210`**——第二条需求的取货（序位 2）同样是 `PICKUP`、
同样不是关卡，**旧判据对它一样成立**。

改成比 `StopId`，而且比的是**追加前后的同一个停靠**：追加之前先读下序位 1 的 `StopId` 与 `StationRiotId`
（新的 `current-next-stop-before-append` 观察），追加之后断言两者都没变。这比审查建议的「断 `StopId` 里的 demandId」
更贴 REQ-0196 的原话——「当前下一站不可变」要的不只是「还是它」，还有「它没被改写」。

本机跑过一遍 PASS，期望与实际都是 `journey:4fce72a7-…|PICKUP@12`。
