# cs#318 本机合成 L2 回归

经 `Invoke-HeavyLocal.ps1 -Ticket cs#318` 逐条跑 `scripts/l2/Invoke-L2Scenario.ps1`（合成车载端，不是真装置）。
「判据」一栏是 SUMMARY.md 判据表里 PASS / FAIL 的行数；等待超时的失败写在 SUMMARY.md 的「失败原因」里，不在判据表里，所以那一行是 2 / 0。

`27e1637a` 那一轮里 `reassign-when-vehicle-ineligible` FAIL，是本票引入的回归（释放服务把自己发出的取消当成「在途单停住」），
红证据在 `evidence/cs318/red/l2-reassign-when-vehicle-ineligible-27e1637a/`，由 `38dc7f19` 修复；`38dc7f19` 上重跑了它和
同样经过释放服务的 `in-transit-order-cancelled-rebuilt`。其余五条在 `27e1637a` 上 PASS，`38dc7f19` 只改了释放服务里
「自己取消的不算停住」一处，它们不走释放路径，没有重跑。

| 场景 | 服务端提交 | runId | 结论 | 判据 PASS / FAIL |
| --- | --- | --- | --- | --- |
| `in-transit-order-cancelled-rebuilt` | `27e1637a` | `20260923T051341263Z` | PASS | 5 / 0 |
| `vehicle-fault-operator-clearance` | `27e1637a` | `20260923T051538463Z` | PASS | 8 / 0 |
| `command-surface-order-hold` | `27e1637a` | `20260923T051615418Z` | PASS | 20 / 0 |
| `emergency-stop-single-trigger` | `27e1637a` | `20260923T051732296Z` | PASS | 14 / 0 |
| `emergency-stop-operator-release` | `27e1637a` | `20260923T051822851Z` | PASS | 10 / 0 |
| `in-transit-order-hang-continue` | `27e1637a` | `20260923T051858865Z` | PASS | 7 / 0 |
| `reassign-when-vehicle-ineligible` | `27e1637a` | `20260923T052000870Z` | FAIL | 2 / 0 |
| `reassign-when-vehicle-ineligible` | `38dc7f19` | `20260923T052607546Z` | PASS | 8 / 0 |
| `in-transit-order-cancelled-rebuilt` | `38dc7f19` | `20260923T052653076Z` | PASS | 5 / 0 |

## 审查修改之后（`8ba67144`，已合并 `fp/v2-impl` `384b9b69`）

审查 M2 之后重建要车载端会话就绪、安全摘要说可以离站才建单，可能改变合成场景里的时序，所以同一组七条全部重跑。
全部 PASS。完整证据只入库本票自己的两条（`green/l2-in-transit-order-cancelled-rebuilt-8ba67144/`、
`green/l2-vehicle-fault-operator-clearance-8ba67144/`），其余五条只记 runId。

| 场景 | 服务端提交 | runId | 结论 | 判据 PASS / FAIL |
| --- | --- | --- | --- | --- |
| `in-transit-order-cancelled-rebuilt` | `8ba67144` | `20260923T092913906Z` | PASS | 5 / 0 |
| `vehicle-fault-operator-clearance` | `8ba67144` | `20260923T093001440Z` | PASS | 8 / 0 |
| `command-surface-order-hold` | `8ba67144` | `20260923T093043462Z` | PASS | 20 / 0 |
| `emergency-stop-single-trigger` | `8ba67144` | `20260923T093155394Z` | PASS | 14 / 0 |
| `emergency-stop-operator-release` | `8ba67144` | `20260923T093243030Z` | PASS | 10 / 0 |
| `in-transit-order-hang-continue` | `8ba67144` | `20260923T093312387Z` | PASS | 7 / 0 |
| `reassign-when-vehicle-ineligible` | `8ba67144` | `20260923T093413708Z` | PASS | 8 / 0 |
