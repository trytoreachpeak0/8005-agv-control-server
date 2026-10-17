# control-server#752 证据：只做待送的车空闲时开回待送点

本机时段 2026-10-17 21:54～22:03（焊线送全检调度放行第 1 轮），worktree `F:/8005-wt/fti-752-8005-agv-control-server`，
被测提交 `219687e70`。

## 定向测试

`IdleReturn|FixedStationExclusivity|WaitingPoint|ArchitectureTests`，`--filter` 定向：`Passed!  - Failed: 0, Passed: 422`。

取红：失败用例先单独提交（`0f9fc60fd` 评估器、`64060afa6` 执行），现状下评估器把停在别处的待送专车送去最近的等待点
（`Expected: (IDLE_RETURN_COMMITTED, 215, null, …)`，`Actual: (IDLE_RETURN_COMMITTED, 214, 2, …)`），执行侧开往待送点的承诺被当成孤儿释放。

## 变异（`mutations/`）

每个变异只改一个文件，只跑 `IdleReturnCommitmentTests` 与名字含 `StagingOrigin` 的用例（76 条），跑完 `git checkout` 还原，每轮后
`src`、`tests` 干净。运行期假条件用 `DateTime.UtcNow.Year < 2000`，不用常量。

| 变异 | 改了什么 | 结果 |
| --- | --- | --- |
| M1 | 评估器开回待送点的分支关掉 | 红 4 |
| M2 | `WaitingPointExclusivity.HeldByAsync` 只认等待点 | 红 3 |
| M3 | 离点清扫的守卫退回只对等待点 | 红 3 |
| M4 | 物化只找等待点预占 | 红 3 |
| M5 | 建单前重核去掉公共站分支 | 红 3 |

存活 0。

## L2 合成场景（`l2/`）

每轮只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`。

| 目录 | 场景 | 结果 |
| --- | --- | --- |
| `green-1` | `staging-only-vehicle-returns-to-staging-origin` | PASS，exit 0，L2-SRO-01～03 |
| `red-m1` | 同上，M1 变异下 | FAIL，exit 1，红在 L2-SRO-01：没有车开往 305，两台都去了等待点 |
| `staging-only-vehicle-stays-at-staging-origin` | #712 场景，按本票改了开局与第二段前提 | PASS，exit 0，6 条 |
| `staging-low-battery-nothing-loaded-charges` | 低电量场景，只改描述 | PASS，exit 0，4 条 |

## 车载端只读核查（`8005-agv-onboard-hmi` `w2g/fp-v2-impl@adc1a28`）

车载端对「等待点」的特殊处理全部按服务端发来的事实判：当前腿 `stopPurposeCategory == WAITING_POINT` 或业务用途 `IDLE_RETURN`
（`WireToGateJourney.IsWaitingPointStop`），注释原文 `nothing is inferred from a station id or a station name`。所以开往 217 的空闲返回
在车上与开往等待点的完全同样处理：

- 录入、提交、扫码前取消、离站这几道入口在非业务停靠时关着（`IsNonBusinessStop`），空闲返回途中与到站后都不装货——这是对的。
- 车接到 `STAGING_TO_WIRE` 需求时，服务端整份替换计划为业务腿（与从等待点出发同一条路），这几道入口随之打开；记忆待送站
  （`WireToGateRecoveryState.WithStagingPickupStationOf`）只在业务停靠时记，不受影响。
- 清桩只看业务用途 `CLEARING_MAINTENANCE`，`IDLE_RETURN` 不会被当成清桩。

代价只有屏上文案：到站那一格写「空闲返回：前往等待点 <站名>」「在等待点 <站名> 待命」（`WireToGateIdleReturnText.VisitText`），
站名是待送点的，字面说成等待点。不影响行为。
