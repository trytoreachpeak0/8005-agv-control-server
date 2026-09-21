# 卸货停靠上的故障货物交接：本票把它从能走变成抛异常

## 缺陷

`OnboardRecoveryCoordinator.ApplyCurrentResultAsync` 终结一条需求时，要结算它那条还没人回答的录入请求。
批次7-06 之前，那个 id 读的是**旅程行**上的 `SublotRequestMessageId`——受理时写下，此后一直在。
本票把它改成读**当前停靠行**上的那一版，好让清单升版之后结算到对的那一条（「同名不同源」）。

可是**当前停靠不一定是取货停靠**。受理只给取货停靠写那一列（`SingleDemandJourneyShape`，卸货停靠那三列是 null），
而取值器按它的设计「要录入却没有 id」抛 `InvalidDataException`。

`before-the-fix.txt` 是那一刻的样子，栈完整：

```
System.IO.InvalidDataException : Stop 'journey:...|UNLOAD' asks for an entry but has no request id.
   at JourneyStopCursor.SublotRequestMessageIdAt(...)
   at JourneyStopCursor.CurrentSublotRequestMessageId(...)
   at OnboardRecoveryCoordinator.ApplyCurrentResultAsync(...) line 1317
   at OnboardRecoveryCoordinator.ProcessResultAsync(...)
   at OnboardMessageProcessor.ProcessAsync(...)
```

后果：一条**人工介入**的恢复路径在卸货停靠上直接抛。故障货物交接是操作员把卡住的货亲手交出去之后车上报的结果，
它抛了就意味着那次交接结算不掉，需求不终结、旅程不关闭、车不释放。

## 它真的会发生吗——三条都是实读的，不是推的

1. **结果处理那一段没有任何阶段护栏。**同一个方法里上面那一段（在途取消）有
   `stop.Stage is not (AwaitingSublot or AwaitingLoadResult)` 挡着，所以它只在取货停靠上走到；
   下面这一段（已下命令的仓位作业终结）**没有**，只要恢复会话认了一条需求、结果是 `HANDED_OFF` 且各仓位证空就走到。
2. **`PickupStopTermination` 自己的注释写着这件事会发生**：「A fault cargo handoff can happen at the gate as well
   as at the pickup」（`PickupStopTermination.cs:32`，至今未改）。
3. **当前停靠在取货完成后就是卸货停靠**：`Current => Stops.FirstOrDefault(IsOpen)`，而 `IsOpen` 按 `Status` 判。

## 现有测试一条都没走到过——拿探针量过的

把 `CurrentSublotRequestMessageId` 临时改成「当前停靠是卸货停靠就抛」，跑全量 1948 条：**全绿**。

所以这不是「测试没跟上」，是**这条路径从来没有被看过**。而全绿本身是零信息——没被执行过的分支不出事，
说明的是它没有机会出事，不是它不会出事。判断只能靠上面那三条实读。

## 修法，以及那个判据为什么按角色而不按空不空

新加 `JourneyStopCursor.CurrentSublotRequestMessageIdOrNone`：当前停靠**本来就不做录入**（卸货）时给 null。
`PickupStopTermination` 收到 null 时不查发件箱——卸货停靠没有录入请求，也就没有要结算的那一条。
恢复路径里**只有第二处**改用它；第一处（在途取消）有阶段护栏，用严格的那个是正确且更强的。

**判据是「停靠的角色是不是卸货」，不是「那一列是不是 null」。**后者看起来更直接、甚至更「防御」，
但它会把一个**取货**停靠缺 id 的情形一起吞掉——而那正是原来那个 throw 存在的理由：
缺了就是受理漏写或者库坏了，终结时不结算任何录入请求，那条请求会被补发进下一个会话，
车载端把它当成内容已变的业务 id 而断会话。

`inject-guard-by-nullness-instead-of-role.txt` 是把判据换成按空不空之后的结果：
`JourneyStopEntryRequestIdTests` 三条里**只有** `APickupStopMissingItsEntryRequestThrowsFromEitherGetter` 红，
另外两条绿。那条用例因此是这个选择唯一的护栏。

## 顺带补上的：那道红线原本自己没有护栏

`SublotRequestMessageIdAt` 的 throw 是本票新立的，而在 `JourneyStopEntryRequestIdTests` 之前
**没有任何用例断过它**——把那个 throw 换成返回空串，全量一条都不会红。新立一条红线就要给它自己的护栏，
否则下一个人（包括我自己，就在这次修复里）会带着正当理由把它削掉，而且削完全绿。
