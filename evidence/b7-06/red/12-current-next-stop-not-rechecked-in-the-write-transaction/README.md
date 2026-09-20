# 写入事务里没有重读当前下一站，车在路上时目的地会被改掉

审查条目 13 点名「票面要求的『读完之后订单状态变了』那个交错没有对应用例」。**写那条用例的时候发现实现也缺一道。**

## 缺陷

派车轮次是先读计划、算插位，再进写事务落库。`StageAndCommitAppendAsync` 在事务里重读了旅程阶段
（`Blocked` / `Completed`），**但没有重读当前下一站**。

这中间车可能刚好到站：它原本驶向的那个停靠完成了，当前下一站前移到下一个——而手上这份重排是按**旧的**当前下一站
算的，它给那个新的当前下一站安排了一个更靠后的序位。

探针跑出来的库是：

```
1:PICKUP:COMPLETED   ← 车刚到的取货站
2:PICKUP:PENDING     ← 新追加的取货，插到了车此刻正驶向的位置
3:UNLOAD             ← 车原本的目的地，被推后
```

**车在路上，目的地被改了。**这正是 REQ-0196「当前下一站不可变」要防的事。

## 为什么既有的两道守卫都挡不住

- `DispatchRoundRunner.ReadEnRoutePlanAsync` 的准入口径看的是「轮次开始时这辆车值不值得算插位」，
  它**不在**写事务里。
- `ApplyResequencingAsync` 的覆盖检查看的是「重排说全了每个停靠没有」，车到站不会让任何停靠消失，所以它照过。
- 事务里的阶段重读只看 `Blocked` / `Completed`，而一趟正常跑着的旅程两者都不是。

**判一道竞态守卫有没有用，看它和它守护的那次写入在不在同一个事务里。**这三道里只有阶段重读在，
而它看的不是这件事。

## 修法与判据

在 `ApplyResequencingAsync` 里，事务内算出当前下一站（库里第一个未完成的停靠），断言**它的序位没有变**：
合法的追加按 REQ-0196 本来就不会把任何东西插到它前面，所以它的序位必然原样；一旦变大，就说明有东西插进去了。
不一致时抛 `BusinessIdentityConflictException`，与 `Blocked` 那条同一形状。

用例 `AnAppendIsRefusedWhenTheVehicleReachedTheNextStopWhileItWasBeingPlanned` 断三件事：拒了、
库里一个字没动（这条需求没被写进受理表——否则它从此不再是候选、却又没有旅程可去）、车正驶向的那一站序位原样。

**反向验证**（`inject-guard-removed.txt`）：把那道重读整段去掉，12 条里**只有这一条红**。

**不误伤**：全量 1957 通过 0 失败；`multi-stop-append-same-zone` 与 `idle-and-inflight-vehicles-compete`
两条 L2 本机各跑一遍 PASS——真实时序下的正常追加不会撞上这道拒绝。
