# cs#291 证据：卸货落定与取货站终结之后的续跑

本机 Windows，SDK 8.0.425，`-c Release`。每次运行都看 `dotnet test` 的退出码，不只看汇总行。文件是从控制台输出精简出来的，只留失败用例名、失败原文和汇总行。

## 修前的红（`red/`）

| 文件 | 跑在哪 | 结果 |
| --- | --- | --- |
| `01-unload-U1-U5-at-2482e88e.txt` | `2482e88e`（只动 tests） | 卸货侧五条全红，U4 用例绿（U4 已由 cs#357 修掉） |
| `02-S2-at-4420f845.txt` | `4420f845`（只动 tests） | S2 红：`waits for a load result with no demand loading at its stop.` |
| `03-cancel-while-loading-at-ed777654.txt` | `ed777654`（只动 tests） | 装货途中取消红，原文同上 |
| `04-ending-resume-at-4723b748.txt` | `4723b748`，引擎为续跑 A 之前 | 3 红 2 绿：取消、已卡住的旧库、同站两条取消一条；绿的两条由 S2 修复覆盖 |
| `05-ack-reconnect-at-15d97944.txt` | `15d97944`（只动 tests） | 确认加换代三条全红：`Outbound MessageId was replayed with different semantics or a non-advancing session generation.` |
| `06-ack-reconnect-with-deadline-pre-fix-engine.txt` | 引擎临时换回 `15d97944` 的版本，tests 为后来补了开期限用例的版本 | 四条全红，原文同上 |
| `07-U4-mutation-intent-at-now.txt` | 修后代码上把离站意图时刻改回 `now` | U4 用例红：`BusinessIdentityConflictException : Movement identity is already bound to different intent content.` |

## 反向验证（`reverse/`）

每个变异都从当时修后的文件按内容改出，`--no-incremental` 重编，跑对应的新用例；结束后按内容写回并完整重编，退出码为 0 才算还原。

| 变异 | 改了什么 | 红的用例 |
| --- | --- | --- |
| M1 | 去掉「停靠全部完成就收尾」（U3 后半） | U3 后半 |
| M2 | 去掉「下一条卸货命令没落库就补发」（U1/U5） | U1、U5 |
| M3 | 恢复「卸货等结果时没有可卸的一律抛」 | U2、U3 前半 |
| M4 | 离站意图时刻改回 `now`（U4） | `ADepartureThatCrashes…` |
| MA1 | 去掉「本站有一条 TERMINATED 就续跑」 | 续跑 A 的四条 |
| MA2 | 续跑 A 的离站等待起点改为一律重置 | `…PastTheDeadlineDoesNotRestartTheDepartureWait` |
| MS2 | S2 修复「还有待装回 AwaitingSublot」改为一律离站 | `…OneOfTwoDemands…HandsTheStopToItsDeadline` |
| MK1 | 卸货侧续跑不带 `keepAcknowledgedIgnoring` | 卸货侧确认加换代 |
| MK2 | 装货侧续跑不带 `keepAcknowledgedIgnoring` | 装货侧确认加换代两条 |
| MK3 | 去掉「期限变了就升一版」 | 开期限那一条 |
| MK4 | 去掉续跑的离站等待补填 | 开期限那一条 |
| MK5 | 补填对正常路径也生效（即 `10a7a35e` 的写法） | `AFreshLoadAfterAVoidedWaitStillSendsItsWorklistWithoutADeadlineAsBefore` |

MK4 第一次跑时没有被杀死：当时开期限那一条只断「升了一版」，不补填时新的一版不带期限，照样升版。之后那条用例加了「新清单带期限」的断言，重跑才被杀死；`reverse/MK4-no-refill.test.txt` 是第二次的输出。M1、M2 第一次用 `if (false)` 改，触发 CS0162 编译不过，改用恒假条件后重跑，文件是重跑的输出。

## 本机全量（`full/`）

`full/855fff71-summary.txt`：`855fff71` 上全量 `Passed: 4325, Failed: 0`，退出码 0，TRX `total=4328 executed=4325 passed=4325 failed=0`；出站 schema 一致性 `0 distinct violations`（报告原文附在文件末尾）。出站 schema 一致性检查在同一条命令的收尾里跑，违规时以 `[Test Assembly Cleanup Failure]` 报出、退出码为 1。

## 没有入库的

- 补偿路径的复现探针，已贴在 cs#499 的评论里，基于 `efc6744f`。
- 修后定向回归的完整控制台输出：结果写在 PR 正文里（`855fff71` 上 613 条，`Failed: 0`）。
