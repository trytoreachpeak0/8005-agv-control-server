# cs#883 证据：空车取货旅程跌破强制充电线（方案 B）

票：control-server#883。用户 2026-11-17 定方案 B：当前这一站照常等完，之后撤掉后面没去的取货站、需求退回待派，车去充电。

## 改前取红

`red-before-fix.log`：在 `5c7819c3b`（fp/v2-impl）上只加新测试与原因码常量、不接行为，`EmptyPickupMandatoryChargeTests` 三条里现场序列那条红：

```
The empty vehicle below the line was asked to leave for the next pickup. stage=AwaitingDepartureSafety block=
```

即电量 10%（夹具线 40%）、车上什么都没有，第一站期限到了照常发离站核验去第二站，与 11-05 夜 agv01 一样。另两条（当前站装上货、电量未跌破）改前即绿，它们钉的是「不变」。

## 变异（`mutations/`）

每个变异只改一个文件一处，在独立的临时 worktree（`4c24964a8`）上做，跑 `EmptyPickupMandatoryChargeTests`，跑完 `git checkout` 还原并核对树干净。

| 变异 | 改了什么 | 红的用例与断言 |
| --- | --- | --- |
| M1 | 离站前不调用本规则（`if (false && …)`） | 现场序列：车被要求离站去下一站 |
| M2 | 「车上没货」判断改成只要有需求就算（`&&` → `\|\|`） | 装上货那条：旅程被收尾（`Not Completed` 不成立） |
| M3 | 电量恒判为低于线 | 电量未跌破那条：旅程被收尾 |
| M4 | 撤下改成终结并写抑制行 | 现场序列：第二站需求不再是 `Accepted` |
| M5 | 放回记成站点超时放回（`RELEASED_AT_STATION_DEADLINE`，会计入 cs#703 次数） | 现场序列：归属移除原因不对 |
| M6 | 照常放回，但多写一条按业务键的抑制行 | 现场序列：抑制表里多出第二站的键（`Assert.Single` 见 2 条） |

M6 单独证明「撤下的需求不被压制」由抑制表断言自己钉住，不靠前面的状态断言。

## 审查返工（`cf5e9a6fd`，审查 M-1、M-2、M-3）

新增两条用例：`ADoorNotProvenClosedAtDepartureKeepsTheRuleFromEndingTheJourney`（M-2）、`ADemandReleasedElsewhereWhileTheRuleDecidesIsNotWithdrawnAgain`（M-1）。现场序列那条多断言一句：收尾清单仍在第一站、仍说 `STATION_DEADLINE_EXPIRED`（M-3）。

变异做法同上（临时 worktree `cf5e9a6fd`，只改 `JourneyRuntimeEngine.EmptyPickupCharging.cs` 一处，跑完还原并核对干净）：

| 变异 | 改了什么 | 结果 |
| --- | --- | --- |
| R1 | 收尾不沿用本站空清单的原因（传 null，即返工前的行为） | 红：现场序列，收尾清单是 `DEMAND_RELEASED` 而非 `STATION_DEADLINE_EXPIRED`。这就是 M-3 的「改前红」 |
| R2 | 去掉仓门闸（审查 MC） | 红：仓门用例，旅程以 `EMPTY_PICKUP_CHARGING_REQUIRED` 收尾 |
| R3 | 撤的需求从锁外游标取（审查 ME） | 红：并发用例，引擎抛 `Sequence contains no elements`（锁外游标里那条已被别处释放） |
| R4 | 去掉锁内重读阶段（审查 MA） | 存活 |
| R5 | 去掉锁内游标判定（审查 MB） | 存活 |

R4、R5 存活：注入的并发释放不改旅程阶段，也不会让剩下的需求不再全是待装，所以这两道判断在这条用例里不起作用。调度裁定要求 MB 或 ME 之一变红，ME（R3）已红。在线闸（MD）与 `Accepted` 判定（MF）仍无专门用例（裁定不强求）。
