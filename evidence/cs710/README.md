# control-server#710 证据：待送点按前后仓判「全满或等着的需求全部装不下」后立即出发（R1）

本机第 1 轮（C11 放行，2026-10-14 23:19:24～23:31:49 CST），被测提交 `d4d49930a`；L2 第二个场景的重跑与用例写法修正在 `1746afc0a`。

## 定向单元测试

`dotnet test tests/ControlServer.Tests -c Release --no-build --filter ...`，范围：本票新增的 `StagingDepartureRuleTests`、`StagingDepartureFullOrNoFitTests`、
`StagingSwitchOffFullOrNoFitWireParityTests`，以及 `StagingPickupNoCountdownTests`、`StagingSwitchOffWireParityTests`、`Batch7CargoHoldingTests`、
`Batch7LoadingPhaseDispatchTests`、`LoadingPhaseMachineTests`、`Batch7StationYield*`、`DispatchAdmissionChainDerivationTests` 与全部 `ArchitectureTests`。

```
Failed!  - Failed:     2, Passed:   273, Skipped:     0, Total:   275, Duration: 1 m 10 s - ControlServer.Tests.dll (net8.0)
```

失败的两条是 `StagingSwitchOffFullOrNoFitWireParityTests` 的两条：这一轮与下面录基线同时在跑，那时基线文件还不存在
（`Wire pin '...' has no expected state`）。基线搬回之后单独重跑 `StagingSwitchOff*`：

```
Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 8 s - ControlServer.Tests.dll (net8.0)
```

退出码 0。

**编译红过一次，留档**：`AStagingSublotOpensOnlyTheSideItsAreaIsAssigned` 的 `InlineData` 用了数组常量，`error CA1861`，改成上下界后编译 0 错误
（定向测试在改正之后跑）。

## 开关关着时逐字节不变（WirePin）

`tests/ControlServer.Tests/WirePins/staging-switch-off-one-side-full.txt`（一侧满、等着的待送候选只因本车货占着那一侧而装不下，持货期限到才走）与
`staging-switch-off-both-sides-full.txt`（两侧都满，离站等待到期才走）：在集成分支 `fp/v2-impl@b4e68e67`（cs#709 合入后）的 detached worktree 里只放入
`StagingSwitchOffFullOrNoFitWireParityTests.cs`、产品代码一行未动跑出的 `.actual`，改名搬回本票分支。集成分支上两条用例在基线之前的断言都过了
（开关关着：一侧满仍 `CARGO_HOLDING_WAIT`；两侧满 `VEHICLE_FULL` 但等完离站等待）。本票分支上两条都逐字节对上。临时 worktree 录完即删。

同样的事实在开关打开时会让车走，由 `StagingDepartureFullOrNoFitTests` 的验收 1、3 两条证（三种停靠参数化，其中「待送点、开关关」一种也在那里断言不走）。

## L2 合成场景

| 目录 | 场景 | 结论 |
| --- | --- | --- |
| `l2/staging-side-full-departs-001` | 待送点 FRONT 满、没有等着的需求时不走；来一条也要 FRONT 的，判 `SLOT_GROUP_OCCUPIED_BY_OWN_CARGO`，车 `VEHICLE_FULL`、1.8 秒后发出开往卸货站的离站核验 | PASS（6 条） |
| `l2/staging-other-side-fits-holds-001-red-script` | FRONT 满、来一条 REAR 装得下的 | FAIL：场景脚本错（单元素数组经 `if` 表达式赋值被展开成标量，StrictMode 下取 `.Count` 抛 `The property 'Count' cannot be found on this object`）；跑到的 2 条判据都 PASS |
| `l2/staging-other-side-fits-holds-002` | 同上，脚本改成 `@(if ...)` 后重跑 | PASS（4 条）：丙追加进同一趟、装进仓 5，离站核验只开往待送站本身，之后五轮仍 `CARGO_HOLDING_WAIT` |
| `l2/same-direction-mixed-load-multi-drop-001` | 回归（开关关） | PASS |
| `l2/stop-ended-journey-continues-001` | 回归（开关关） | PASS |

只保留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`；组件日志与快照未入库。

「全部装不下才走」与「任一装不下就走」的分别 L2 看不出来：合格的候选在判出它的那一轮就被追加，不会以「合格而没装」的样子留到下一轮；那一格由单测证。
