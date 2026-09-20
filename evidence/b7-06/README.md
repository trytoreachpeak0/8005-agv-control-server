# 批次7-06（control-server#211）的红绿证据

L2 场景的证据在 `evidence/l2/b7-06-*`，这里是单元测试这一层的。

## 红

| 文件 | 它红在哪、为什么值得留 |
| --- | --- |
| `red/01-multi-demand-advance.txt` | 一站多需求的推进判据，实现之前先红。两条需求挂在同一个取货停靠上时，推进段按锚需求走，第二条永远轮不到。 |
| `red/02-anchor-ledger-now-empty.txt` | 批次7-01 留的锚需求列读取账本清空之后的实读：`Actual: SortedDictionary<string, int> []`。按票面要求删掉整个类而不是把数字改成 0——那个类自己的 `Assert.True(Count > 0)` 拦不住「把条目全删光留一本空账」。 |
| `red/04-cancellation-named-demand.txt` | 扫码前取消判的是操作员指名的那一条需求，实现之前先红。 |
| `red/05-slot-ledger-reads-loaded-instead-of-reserved.txt` | **注入出来的红，用来证明新用例有判别力。**把 `JourneyAwareSlotLedger` 的减数从「目标仓位」改成「已装仓位」，九条用例里「正好两条」变红——`AVehicleUnderWayLosesTheSlotsItsOwnDemandHasReserved` 与 `TheSlotsSubtractedAreTheReservedOnesWhileNothingIsLoadedYet`。注入前写下的预期就是这两条：另外七条（空闲车、已终结、别的车、无基线）在两种读法下答案相同，不该红也确实没红。 |

`red/05` 那一条注入的是这一行：

```csharp
(membership, demand) => new { membership.TargetSlotsJson, demand.Status })
//                            ^^^^^^^^^^^^^^^^^^^^^^^^^^ 改成 membership.LoadedSlotsJson
```

改完立刻按备份还原，没有用 `git checkout --`——同一个文件里还有本票未提交的改动时，那条命令会把它们一起抹掉。

## 绿

| 文件 | 它证的是什么 |
| --- | --- |
| `green/03-zero-change-pin-diff.txt` | 十份 `ZeroChangePin` 重录的完整 diff：13 个文件、31 行新增，有且只有三种形状（归属状态由引擎推进、三份 `commanded-ending-*` 纯增停靠行、新列 `DispatchZoneParameterVersion=NULL`）。四份 `WirePin` 一个字没动，那是「车看到的东西没变」的独立证据。 |
