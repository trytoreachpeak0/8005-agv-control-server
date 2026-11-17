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

未覆盖：写锁内重读阶段与游标（并发窗口）那两处判断没有专门的变异；与 cs#709 待送点缺电同一写法。
