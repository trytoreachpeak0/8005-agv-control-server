# control-server#793 证据：车停在已登记等待点却无占用行时原地认领

PR #796。worktree `F:/8005-wt/cs793-8005-agv-control-server`，本机时段均由焊线送全检调度放行，均不重试。

## 定向测试

过滤条件（下称「空闲返回相关全部」）：

```
FullyQualifiedName~ControlServer.Tests.IdleReturnCommitmentTests|FullyQualifiedName~ControlServer.Tests.IdleReturnExecutionTests|FullyQualifiedName~ControlServer.Tests.VehiclePurposeDashboardTests|FullyQualifiedName~ControlServer.Tests.MultiVehicleExecutionTests|FullyQualifiedName~ControlServer.Tests.ChargingClearanceToWaitingPointTests
```

| 时刻（10-19） | 被测提交 | 结果 |
| --- | --- | --- |
| 17:35 | `f0102bd60`（第一版） | 411 条：过 408、红 3。红的三条是多车夹具把车站着的 300 登记成等待点、断言承诺开往它（即 H1），见 PR 正文「改了三条旧用例」 |
| 17:40 | `f3d2a2ad9`（夹具换成 301） | 411 条全过 |
| 18:21 | `7db44db0a`（审查返工 M1/L1/L2/L3） | 414 条全过 |
| 18:29 | `5a4a7e1f3`（L2 用例的拦截器只拦第一次） | 变异用例集 88 条全过 |

## 变异（`mutations/`）

每个变异只改 `src/ControlServer.Host/Runtime/IdleReturn/IdleReturnEvaluator.cs` 的一行运行期假条件（不用编译期常量），改前确认工作区干净、
改后确认恰好一个文件一行，跑完 `git checkout` 只还原该文件，事后 `git status` 干净。第一版的两个变异跑 `IdleReturnCommitmentTests`
与多车空闲返回 4 条（85 条）；返工后的五个跑同一集合加新增 3 条（88 条）。`mutations/<名>.txt` 是失败用例名、断言原文与汇总行。

| 变异 | 被测提交 | 改了什么 | 结果 |
| --- | --- | --- | --- |
| `v1-adopt-off` | `f3d2a2ad9` | 原地认领的候选条件加 `origin < 0 &&`（关掉原地认领） | 红 5。`Actual: ("IDLE_RETURN_COMMITTED", 214, …)`：承诺开往当前站，即审查探针 |
| `v1-standing-off` | `f3d2a2ad9` | `StoodOnByAnother` 加 `vehicleKey.Length < 0 &&` | 红 2。`("VK-B", "IDLE_RETURN_COMMITTED", 214)`：B 被派去 A 站着的点 |
| `Ma` | `7db44db0a` | 同节点代价为 0 的自排除加 `origin < 0 &&`（审查 M1） | 红 1：`AWaitingPointOnTheSameNodeAsTheVehiclesStationIsNeitherAdoptedNorDrivenToAndStaysClosedToOthers` |
| `Mb` | `7db44db0a` | `AdoptInPlaceAsync` 入口加 `origin >= 0 ||`（关掉原地认领） | 红 5，含两轮用例 `AnAdoptedPointSurvivesTheNextRoundsDepartureSweepAndIsReleasedOnlyOnceTheVehicleHasLeft` |
| `Mc` | `7db44db0a` | `StoodOnByAnother` 加 `vehicleKey.Length < 0 &&` | 红 4 |
| `Md` | `7db44db0a` | 只把待送固定起点那一处「有车站着」改成 `stationId < 0 && …`（审查 L1） | 红 1：`AStagingOriginAnotherVehicleStandsOnIsNotTakenAndTheVehicleFallsBackToAWaitingPoint` |
| `Me-survived` | `7db44db0a` | 认领撞键那一支 `return Refuse(...)` 改 `return null`（审查 L2） | **存活**（88 条全过）：用例的拦截器每次插入都拦，落回选点后那次插入也被拦成同一个原因码 |
| `Me` | `5a4a7e1f3` | 同上，用例改为只拦第一次插入 | 红 1：`Expected: ("IDLE_RETURN_COMMITMENT_REFUSED", null)`，`Actual: ("IDLE_RETURN_COMMITTED", 214)` |
