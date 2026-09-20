# cs#242 intake 拒收的 claim 不再被轮末当成已受理——L1 证据

票：<https://github.com/trytoreachpeak0/8005-agv-control-server/issues/242>
分支：`b7-242/intake-refusal-drops-claim`，从 `origin/fp/v2-impl@e3048a25` 开，
中途把 `origin/fp/v2-impl@87041b62`（cs#199）merge 了进来。

| 提交 | 内容 |
| --- | --- |
| `150c0acc` | 测试先行：三种非 `Accepted` 结局各一条，加两个夹具钩子 |
| `1b4adbb2` | 测试先行：同轮不重抢（在顶端即绿，是「不回退」护栏） |
| `06da75b7` | 测试先行：Gone 不回退 + 只挡自己那条阻断 |
| `64bc3afc` | 实现：`DispatchRoundFacts.ClaimsIntakeRefused` |

## 01-red-tests — 测试提交上的红

### three-refusals.txt

在 `e3048a25` 的实现上跑三条新用例：**3 failed / 0 passed / 3 total**。三条红在
同一处——轮末的 `StructuralDispatchBlockSink` 把这条需求的结构性阻断清掉了：

```
  Failed ControlServer.Tests.MultiVehicleExecutionTests.ACandidateChangedAtIntakeDoesNotClearAStructuralBlock
  Error Message:
   Assert.Null() Failure: Value of type 'Nullable<DateTimeOffset>' has a value
Expected: null
Actual:   2026-09-08T06:00:00.0000000+00:00
```

`AFinalAdmissionRejectedAtIntakeDoesNotClearAStructuralBlock` 与
`AJourneyPlanIncompleteAtIntakeDoesNotClearAStructuralBlock` 的失败原文与此逐字相同。

每条在断言阻断之前，先断言 intake 确实走到了那个结局（`AcceptedDemands` 为空、
backlog 写了对应的 `FINAL_*` 原因码），那几句在红的那一轮里全部通过——所以红的是
「阻断被清」，不是「那一轮根本没到 intake」。

### only-its-own-block.txt

同一提交上跑「只挡自己那条阻断」与「Gone 不回退」：**1 failed / 1 passed / 2 total**。
红的是 `ARefusedClaimHoldsBackOnlyItsOwnBlock`，同样红在被拒那条的 `ClearedAt` 已被写上；
`ADemandIntakeFoundGoneStillClearsItsStructuralBlock` 在顶端就是绿的，它钉的是本票
故意不改的那一支。

## 02-green — 实现提交上的绿

在 merge 完 `87041b62` 之后的 head `198a6d63` 上跑。

### dispatch-and-architecture.txt

`--filter MultiVehicleExecutionTests|StructuralDispatchBlockTests|DispatchChainSeamTests|Architecture`，
**202 passed / 0 failed / 202 total**（1 分 47 秒）。范围覆盖派车轮与轮末阻断汇总的
全部测试类，以及切片台账与迁移纪律那几个架构测试。

### guard-gone-stays-claimed.txt

票面点名必须保持绿的既有护栏 `ADemandIntakeFoundGoneStaysClaimedForTheRestOfTheRound`
单独跑一遍：**1 passed**。它连同 `MultiVehicleExecutionTests` 里那批逐字对照的
transcript 一起，一个字母都没有改动——`outcome accepted=` 那一行打印的就是
`round.AcceptedDemandIds`，本票没有动那个集合的内容。

## 03-format — 格式检查

`dotnet format --verify-no-changes`，实现三个文件与测试两个文件各一次，两次都是
exit 0、无输出。

另外核对过五个文件都没有 BOM（前三个字节是 `75 73 69`，即 `usi`），因为编译器与
`dotnet format` 都看不见 BOM。

## 没有做的事

- **没有新增任何无限延时的测试钩子。** 本票加的两个夹具钩子都不等待：
  `FleetCatalog.ChangedOnReread` 只是在重读时把 `DemandRevision` 前进一位，
  `FleetCatalog.OnReread` 是一次性回调、进入即自清。`JourneyPlanIncomplete` 用的是
  cs#239 已有的 `ThrowOnFirstAccept`，抛出而不是挂起。
- **没有跑 L2。** 本票不新增场景，改的是轮末内存集合的读法，既有多车合成场景由 CI 覆盖。
- **没有跑真装置。** 不涉及车载端与界面。
