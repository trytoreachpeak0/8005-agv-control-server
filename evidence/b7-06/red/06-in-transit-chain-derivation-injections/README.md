# 在途准入链「派生关系」五条判据的注入验证

针对 `tests/ControlServer.Tests/DispatchAdmissionChainDerivationTests.cs`（批次7-06，control-server#211）。

这五条用例守的是 `DispatchAdmissionCriteria.InTransit` 的一句话：在途车那条准入链是从空闲链**派生**的，
不是另写一张表。在它们之前，那句话只是 remarks 里的纪律——把 `InTransit` 改成另写一张固定表，全量测试一条
都不会红，因为没有任何判据读这条链**装了什么**。

判据自己也要验。下面每一行是一次注入：先写下「预期只有哪一条红」，再跑，逐条对。

| 注入 | 改了什么 | 预期红 | 实测红 |
| --- | --- | --- | --- |
| A | `InTransit` 忽略传入的空闲链，自己用 `Default` 重建一份**内容完全相同**的 | 只有「派生关系」 | 只有 `ACriterionAddedToTheIdleChainReachesTheInTransitChainOnItsOwn` |
| B | `InTransitVehicleFactsCriterion.Order` 由 80 改成 81 | 只有「同一个位置」 | 只有 `TheTwoVehicleFactCriteriaOccupyTheSamePositionInTheirChains` |
| C | 去掉 `if (routeGraph is not null)`，无条件装追加门 | 只有「没有路网就不装」 | 只有 `WithoutARouteGraphTheInTransitChainGainsNoAppendGate` |
| D | 某条共用判据装两次（`criteria.Add(criteria[3])`） | 只有「每条只出现一次」 | 只有 `EverythingTheTwoChainsShareRunsInTheSameOrder` |
| E | 过滤器多吃掉一条共用判据（`AreaScopeCriterion`） | 「差集恰好三条」，**连带** 2、3、5 | 四条，见下 |

注入脚本是本目录的 `inject.py`（一次性用途，不入产品树）；原始输出是同目录的 `inject-<字母>.txt`。

## A 的意义：它是唯一一条「另写一张表」通不过的

A 注入的那份链，内容与今天的在途链**逐条相同**——差集、次序、位置全对。前四条用例因此全绿，只有「往空闲链
塞一条，看它自己会不会出现在在途链里」红了。这正是那五条里其余四条比的是**今天长什么样**、而这一条比的是
**它从哪来**的区别。

## E 的四条红，逐条说明为什么

多吃掉一条共用判据之后：

- `TheTwoChainsDifferByExactlyThoseThreeCriteria` — 空闲链独有的从 1 条变 2 条。这是预期的那条。
- `WithoutARouteGraphTheInTransitChainGainsNoAppendGate` — 它也断同一个差集（只是不带路网）。
- `EverythingTheTwoChainsShareRunsInTheSameOrder` — 共用序列少了一条。
- `ACriterionAddedToTheIdleChainReachesTheInTransitChainOnItsOwn` — 它的后半段也断差集。

绿的那条是 `TheTwoVehicleFactCriteriaOccupyTheSamePositionInTheirChains`：它只比两个 `Order` 字面量，
与链装了什么无关。**它在这里绿是对的**，不是漏判。

## D 的第一版：注入了，五条全绿

`inject-D-against-first-version-of-the-assertion.txt` 是这一轮里最值钱的一份，而它的内容是
`Passed! - Failed: 0, Passed: 5`。

那份输出跑的是**判据的第一版**加注入 D。第一版这样算共用部分：

```csharp
string[] sharedOnIdle = [.. InOrder(idle).Except(OnlyOnTheIdleChain, StringComparer.Ordinal)];
```

`Enumerable.Except` 是集合运算，**它自己会去重**。于是装了两次的那条判据在比较之前就被判据本身吃掉了——
而「装了两次」正是这条用例唯一要抓的东西。当时那条用例的注释写着「`Except` 是集合运算，对重复不敏感，
这条比序列的才红」，**而它自己也用了 `Except`**：判据和它要抓的缺陷共享了同一个盲区。

现在的版本改用 `Where`，保留重复，注入 D 因此红（`inject-D.txt`）。

留着这份「全绿」是因为它说明了一件不做注入就看不见的事：**一条带着错误自我说明的用例，比没有注释更糟**——
下一个人会信那句话，而 CI 会一直是绿的。
