# control-server#712 证据：只做待送的车留在待送点，不空闲返回

本机时段 2026-10-14 21:17～21:29（C11 放行第 1 轮），worktree `F:/8005-wt/cs712-8005-agv-control-server`。
被测提交：实现 `b2f177e30`，L2 那几轮在 `c4f623536`（之后两个提交只改测试辅助方法与场景脚本）。

## 定向测试

`IdleReturnCommitmentTests`、`ChargingAllocationTests`、`VehiclePurposeDashboardTests`、`ArchitectureTests`，`--filter` 定向跑：

- 第一遍 7 失败，全部来自测试辅助方法 `BindAsStagingOriginAsync`：激活绑定时默认的必需任务类型含 `WIRE_TO_GATE`，只绑待送站被拒
  （`TASK_TYPE_BINDING_REQUIRED_MISSING: WIRE_TO_GATE is in this map's requirement set but has no binding.`）。不是产品代码问题，
  改为只声明 `STAGING_TO_WIRE` 为必需类型（`bb93cf3df`）。
- 重跑：`Passed!  - Failed: 0, Passed: 251, Skipped: 0, Total: 251`，退出码 0（含出站 schema 收尾检查）。

## 变异（`mutations/`）

每个变异只改 `IdleReturnEvaluator.cs` 一个文件，只重跑 `IdleReturnCommitmentTests`，跑完 `git checkout` 还原。

| 变异 | 改了什么 | 结果 |
| --- | --- | --- |
| M1 | 规则整条去掉 | 红 1（留站用例）；L2 红在 L2-SIS-03 |
| M2 | 去掉「只做待送」条件 | 红 4（兼做、只做机台、什么都不接、车队里没有这辆车） |
| M3 | 去掉「停在待送起点」条件 | 红 1（只做待送、停在别处）。第一次用常量条件，被 CS0162 挡在编译，不计 |
| M4 | `All` 改 `Any` | 红 1（兼做待送与关卡） |

存活 0。

## L2 合成场景 `staging-only-vehicle-stays-at-staging-origin`（`l2/`）

每轮只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl` 与服务端日志里空闲返回相关的行（`control-server-idle-return.log`）。

- **`green-1` FAIL（场景脚本自身的错，保留）**：`OnboardPeers` 的每一项是 `Name／Port／AgvId／Double`，脚本直接对它调 `Command`
  （`Method invocation failed because [System.Management.Automation.PSCustomObject] does not contain a method named 'Command'.`）。
  改为按 `AgvId` 取 `.Double`（`c4f623536`）。
- **`green-2` PASS**：五条判据全过。V 在 305 收尾后原来的等待点 214 空着，V 在 305 停十几轮没有 `IDLE_RETURN` 占有、开往等待点的单仍是开局那一张；
  第二条待送需求派给 V，开往 305 的单在 V 停在 305 时建出，到站后录入请求送到 V 的车上。服务端日志里
  `IDLE_RETURN_HELD_AT_STAGING_ORIGIN` 只记一条（事件 2199，Information），没有警告或错误。
- **`red-m1-rule-removed` FAIL（M1 下取红）**：红在 L2-SIS-03，实际 `claim idle-return:BROKERX-L2-0001:20261014T132545767Z / 2 intents`——
  V 在 305 收尾后下一轮就被承诺空闲返回到 214（日志 21:25:45）。之后第二条需求 180 秒没被受理：305 仍被 V 占着，V 已有空闲返回用途接不了单，
  另一台车被单占挡住（`FIXED_TASK_STATION_OCCUPIED_BY_OTHER_VEHICLE`），场景到此结束。红是规则失效本身，不是超时预算。

- **`green-3-dashboard-verdict` PASS（审查 L2 补判据后，本机时段第 2 轮 21:44～21:46，被测 `f4a7a2994`）**：新加的 L2-SIS-06 从
  `/api/dashboard/idle-returns` 读 V 那一行的 `verdict.reasonCode`，读到 `IDLE_RETURN_HELD_AT_STAGING_ORIGIN`；其余五条照旧全过。

**这个场景证明不了的**：假 RIoT 照场景指令把单置为完成，所以真 RIoT 对「目的站就是当前站」的单怎么处理不在这里证明（RIoT 行为实验室样本数为 0），
写在票面「上线前实测」。

完整证据（含数据库快照与全部日志）留在控制端 `F:/8005-scratch/cs712-evidence-full/`，未入库。
