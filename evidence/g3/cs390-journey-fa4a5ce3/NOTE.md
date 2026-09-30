# 第一轮 journey G3：红在新场景的脚本上（不是产品缺陷）

- 运行：2026-09-30 07:21:46～07:45:37 UTC，`run-journey-g3.ps1` 自检覆盖（`SELF_CHECK_OVERRIDE`），控制端 `fa4a5ce3`、车载端 `f3c3e939`、模拟器 `fb5f7c59`、协议 `86575456`。
- 结论 `JOURNEY_G3_SLICE_FAIL`。16 个场景里 15 个 PASS，`g3-waiting-point-idle-return`（本票新场景，第一次在真装置上跑）FAIL。
- 8 个切片全判 FAIL，唯一不通过的是整轮级断言 `noScenarioAbortedBeforeItsJudgments`：新场景中途抛异常，整轮的每个切片随之判不过；各切片自己的断言全是 PASS（见 `slices/*/gate-result.json` 的 `assertionIds`）。

## 新场景为什么中断

`G3-12-01`、`G3-12-02` PASS 之后，脚本在算 `G3-12-07` 时抛出：

```
The property 'OperationType' cannot be found on this object. Verify that the property exists.
```

`$operationsAtPoint = @(Invoke-L2Query …)`：`Invoke-L2Query` 以 `return , $rows` 整体返回，外面再套 `@()`，查询结果为空时得到「一个元素、该元素是空数组」，取 `OperationType` 就抛异常。`G3-12-03`～`07` 因此都没有落表。修法是直接赋值、不再包一层（场景脚本第 197 行附近，旁边写了注释）。

## 服务端这一轮其实做对了什么

失败时保留的临时库（`C:\Users\szy\AppData\Local\Temp\l2-20260930T074440450Z\controlserver.db`）离线读出：途中计划（`WAITING_POINT`、`ACTIVE`）与 `IDLE_RETURN` 业务状态都被真车载端确认；到点后收尾计划是 `ARRIVED` 的等待点腿、业务状态 `activePurpose` 为空，两张都被确认；214 转为在点占用，用途以 `IDLE_RETURN_CONVERGED_AT_WAITING_POINT` 释放；没有任何装卸操作。也就是 `G3-12-03`、`G3-12-07` 的服务端一半在这一轮是满足的，只是没能落表。这不替代下一轮的判据结论。

## 教训

写新的真装置场景，申请时段之前应该先用保留下来的库离线把判据的读法跑一遍——这条早就记着，这次跳过了，占掉了一轮时段。修复后已用离线脚本在这一轮的库和合成场景的库上把新场景的全部读库判据跑过一遍，再申请下一轮。
