# cs#499 证据：恢复终结一条需求之后，多需求旅程从 Blocked 放出

本机 Windows，SDK 8.0.425，`-c Release`。每次运行都看 `dotnet test` 的退出码，不只看汇总行。文件是从控制台输出精简出来的，只留失败用例名、失败原文和汇总行。

## 修前的红（`red/`）

| 文件 | 跑在哪 | 结果 |
| --- | --- | --- |
| `00-probe291-rerun-at-1de2d2e2.txt` | `1de2d2e2`（`fp/v2-impl` 顶端，含 cs#291），票面评论里的探针原样放进去跑，未提交 | 与票面在 `efc6744f` 上的读数逐字相同：补偿被接受，被补偿那条 `TERMINATED`，另一条 `LOADED`，旅程仍是 `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` |
| `01-five-red-at-2c3cab97.txt` | `2c3cab97`（只动 tests，产品代码同 `1de2d2e2`） | 新用例 5 红 1 绿：补偿两条、交接两条、强制取出一条全停在 `Blocked`；绿的是护栏用例（交接的不是阻塞旅程的那一条，本来就该留在 `Blocked`） |
| `02-review-M1-two-red-at-f757c423-plus-tests.txt` | `ff9d227f`（产品代码同 `f757c423`，即第一版修复；tests 加了审查 M-1 的两条护栏） | 10 条里 2 红：第一条交接没对上、仍待恢复时补偿第二条，旅程被放出、发了离站核验（`stage=AwaitingDepartureSafety … checks=1`）；第三条扫码前取消的结果在阻塞之后才到、判为没对上时补偿第二条，旅程回到等录入、又发了第三条的录入请求（`stage=AwaitingSublot … entries=1`）。这是第一版修复自己引入的，在 `1de2d2e2` 上旅程留在 `Blocked`（审查实测） |

`2c3cab97` 里补偿用例挂的协议向量名写成了不存在的 `CV-LOAD-COMPENSATION`，之后改为 `CV-EXCEPTION-COMPENSATE`（架构测试 `NoTestClaimsAVectorIdTheProtocolNeverFroze` 查出）。这只改 trait，不影响上表的红。

## 反向验证（`reverse/`）

两轮。第一轮在第一版修复 `f757c423` 上（`mutations.txt`，按时间追加）；第二轮在审查 M-1 修复之后的 `a6a5e6a3` 上、用最终的 10 条用例把全部八个变异重跑一遍（`mutations-round2-at-a6a5e6a3.txt`），下表是第二轮的结果。`mutate.py` 每个变异都按内容改出、`--no-incremental` 重编、跑 `RecoveryEndingReleasesBlockedJourneyTests`，结束后按内容写回并完整重编（`restored build exit=0`）。`mutations.txt` 是原始记录，按时间追加。

| 变异 | 改了什么 | 红的用例 |
| --- | --- | --- |
| M1 | 恢复协调器不放（`BlockedJourneyRelease` 恒返回） | 补偿两条、交接两条、强制取出 |
| M2 | 去掉「结清之前是 RecoveryRequired」 | `AHandoffDoesNotLiftABlockItsOperationDidNotCause` |
| M3 | 去掉「这趟旅程别的操作都已收敛」 | `ACompensationLeavesTheJourneyBlockedWhileAnotherOperationOfItIsUnresolved` |
| M4 | 去掉「是当前停靠上这条需求的那一次操作」 | 无（见下） |
| M5 | 引擎卸货侧不把「卸货命令之后被终结」算作本站进展 | 交接两条 |
| M6 | 引擎卸货侧只看 TERMINATED、不核卸货操作已落库 | 无（见下） |
| M7 | 放出时不清阻塞码 | 补偿（同站两条）、交接（同站两条） |
| M8 | 去掉第 4 条「旅程别的需求没有一条是 RecoveryRequired」（审查 M-1） | `…WhileAnotherDemandStillAwaitsRecovery`、`…WhileACancellationLeftAnotherDemandAwaitingRecovery` |

M7 第一次没有被杀死：当时用例只在引擎跑过之后读阻塞码，而引擎那一轮会改写它。之后两条用例加了「结果落库后、引擎跑之前，阶段已回到等结果、阻塞码已清」的断言（`AssertReleasedAsync`），重跑被杀死；M4、M6 也在加断言后的最终用例上重跑过一次，仍不红。

M4、M6 没有用例杀得死，两处都是防御：

- M4：旅程阻塞时，一次 `RecoveryRequired` 的仓位操作只可能是当前停靠上那一条的（同一站逐条串行，停靠不在结果未定时完成）。M2 的状态条件已经把「结清的是别的、早已提交的操作」挡住，M4 的停靠条件在正常流程里到不了能与之区分的状态。
- M6：卸货停靠上的 TERMINATED 还有一种来历——在取货停靠就被终结、从没装上的需求，归属行同样挂在这个卸货停靠上。它与「卸货命令之后被交接」只在「等卸货结果却一条卸货命令都没落库」这种服务器自己的不变量被破坏的状态里才有区别，正常流程到不了。核卸货操作已落库，是为了让原有那道「本站一条都没卸就抛」的守卫保持原来的松紧。

## 定向回归

见 PR 正文「测试」一节。
