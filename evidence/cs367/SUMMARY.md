# control-server#367 证据：普通腿确认前终态，闸门前后都按确认过的单判

FAILED 进故障模型并能经受控入口清除、清除后同车重建；CANCELLED/DELETED 按 `REQ-0360` 同车重建；SUSPENDED(8) 交人。
每条标了「读到的」（跑出来的、读代码读到的）还是「推的」。基点 `fp/v2-impl@71e62d85`（已含 cs#357、cs#358）。

## 修前红（red/）

| 提交（只动 tests） | 格 | 结果 |
| --- | --- | --- |
| `78bff6e1` | 确认前 FAILED | 8 条红，都红在没有故障记录（`Assert.Single() Failure: The collection was empty`）或 REQ-0248 那条 `Expected: Pending / Actual: null`（读到的） |
| `39e61e99` | 确认前 CANCELLED/DELETED/SUSPENDED（调度 2026-09-28 并入） | 7 条红，都停在 `{PICKUP\|GATE}_TerminalReconciliationRequired`（闸门后 `ONBOARD_SESSION_NOT_READY`），原文 `red/01-*`（读到的） |

整体的修前运行记录是下面变异表里的 **M0**：`src` 退回 `71e62d85`、保留全部新测试。

## 反向验证（green/07-reverse-validation-final.txt）

脚本 `green/mutate.ps1`：工作树有未提交的 `src`／`tests` 改动就退出；每个变异先确认替换恰好命中 1 处，`--no-incremental` 重编且 `0 Error(s)`，
跑相关的 10 个测试类（299 条），读 `dotnet test` 退出码，跑完从 HEAD 还原。跑在审查补测之后的测试提交上，产品代码与 `dabbfcc3` 相同（读到的）。
更早的几轮（`01`～`05`）留作过程记录，结论以本表为准。

| 变异 | 红了什么（读到的） | 为什么别的不红（推的） |
| --- | --- | --- |
| M0 `src` 整体撤回 | 新测试 20 条（含计划测试类那条正向用例） | 没红的两条修前也绿：「闸门后不为没发过的单建单」（修前闸门后不对账，判别力由 M5 撑）、反向用例（判别力由 M4 撑） |
| M1 闸门前不按终态判 | 闸门前的 FAILED、取消、SUSPENDED、急停等车、窗口二次取消、两条清除重建、读不到保码（闸门前）共 10 条 | 闸门后与失联那一路走另一处 |
| M2 闸门后整段关掉 | 闸门后与失联那一路共 10 条（含失联 L1、计划放行正向用例） | 两条清除用例的故障在闸门前记 |
| M3 清除入口不认 Hold 审计 | 两条清除后重建 | 只有它们靠这个认法放行 |
| M4 认 Hold 时不看故障代次 | 反向用例 | — |
| M5 闸门后对没发过建单的意图也对账 | `AnOrderNeverSentIsNotCreatedBehindTheGate` | — |
| M7 记下后不再喂故障模型 | REQ-0248 那条 | 首轮记故障的断言只看第一轮 |
| M8 闸门后确认了不起名 | HANG 那条 | — |
| M9 终态只看 FAILED | 取消、删除、关卡腿登记、REQ-0361 两条、SUSPENDED 两格、读不到保码两格，共 9 条 | FAILED 那几格不受影响 |
| M10 去掉重建延迟（`OwnOrderRebuild.cs`，既有代码） | 本票取消重建那条（延迟护栏那一句），另有 10 条既有的延迟用例 | 延迟在共用的重建代码里，本票只是新增一条看得见它的入口 |

原 M6（读不到保码）随实现改成共用 `NameStalledOrderAsync` 而作废，那一行为由 M9 覆盖。

## 本机 L2（l2-4aaa8841/）

`command-surface-order-hold`、`emergency-stop-single-trigger`、`emergency-stop-operator-release` 在 `4aaa8841` 上各跑一次，全部 PASS（读到的）。
**这一轮没带 `-SkipBuild`**：每个场景的 `timeline.jsonl` 里有 `Building ControlServer and the test doubles.`（带 `-SkipBuild` 时这一行是
`Build skipped (-SkipBuild)`），`build-summary.txt` 是 `logs/build.log` 的末 8 行（`Build succeeded`、`0 Error(s)`、耗时）。后两个场景的构建耗时
3～5 秒，是增量构建：第一个场景刚在同一棵树上构建过（耗时是读到的，「增量」这个解释是推的）。`SUMMARY.md` 里 `controlServerCommit` 都是 `4aaa8841`。
每个场景只留这四个文件。

更早的 `l2/`（`d67221fa`）与 `l2-final/`（`85348e72`）留作记录。那两轮的构建日志在精简证据时被我删掉了，所以它们拿不出「没带 `-SkipBuild`」的原文，
本轮就是为此补跑的。`l2-final/` 那一轮之前，我在一串命令里误带了 `-SkipBuild`，在它开始前用预先建好的证据目录拦下（三份日志各有
`EvidenceRoot must not exist`），再不带它重跑（读到的）。

## cs#342 断线重连模型（green/04、06）

同一组 300 个固定种子（masterSeed=342）。基点取 cs#357 的 `evidence/cs357/green/08-cs342-model-300-3742cbc9.txt`：
`git diff 3742cbc9 71e62d85 -- src tests` 为空（读到的）。`d67221fa` 与 `85348e72` 两次都与基点逐项相同：录入请求 300/300 送达、确认冲突 0、
回退 0、握手接受语义不同的消息 0、「带等人码的轮次推进失败」都是 `ORDER_HANG` 5 次（读到的）。耗时没控制负载，不据此下结论（推的）。
`85348e72` 之后产品代码只多了一处注释（`4aaa8841`），没有重跑。

**模型测不到的格**（推的，依据是 cs#342 PR 正文「模型的局限」）：模型不产生 FAILED、取消，也不丢建单应答，本票的每一格它都走不到；它只证明没改坏断线重连。

## 全量

- `0eb26ea2` 那次全量：2868 条红 1 条，`PickupDispatchPlanPastOwnOrderTests.NoPlanGoesOutWhenTheUnreadinessIsNotExplainedByTheOwnOrder("order-not-confirmed")`。
  机理：那一格把 RIoT 上正常执行的单改成 `RESULT_UNKNOWN`，本票闸门后的对账同一轮就确认了它、计划随之放行——按 cs#314 这是对的。改的是造前提的方式，
  断言没改，另补前提断言与正向用例（`d67221fa`）（读到的）。
- `85348e72`：2876 条全过，退出码 0（读到的）。之后只加了测试与注释，最终 head 的全量由 CI `test` 跑。

## CI 真装置（run 36374996508）

`l2.yml` `rig=real`，两个场景各一遍，head `a43609e0`。之后的提交只改了测试、注释、文档与证据；唯一的 `src` 改动是 `4aaa8841` 在
`VehicleFaultCoordinator.HoldCurrentOrderAsync` 上加的一段注释（读到的 diff），产品逻辑不变。四行核对（读到的）：

| 项 | 值 |
| --- | --- |
| control-server | `a43609e0e7fde4da0ace700c28335d299510b92d` |
| 8005-agv-onboard-hmi | `4c2d2dc14656f80e812f37128a7964c2c310217a`（`w2g/fp-v2-impl` 顶端，派发前 `ls-remote` 核过） |
| slots-simulator | `fb5f7c593742bf98bc3957b8729a38aad5321f28`（`main` 顶端，同上） |
| 真跑了 | `real-onboard-compensate-then-reconnect-01 -- PASS, 58s`、`real-onboard-order-hang-continue-01 -- PASS, 31s`；三个停止码命中 8 行，全是 `^[[36;1m` 源码回显 |

artifact `real-rig-evidence` 231593 字节；两份 `assertions.json` 的 `outcome` 分别 10/10、8/8 为 `PASS`，没有 `FAIL`（读到的）。

「建单应答丢失后确认前终态」在真装置与合成 L2 上都造不出（读到的）：假 RIoT 的建单端点先过故障注入
（`tools/ControlServer.FakeRiot/RiotDataPlane.cs:159` `ApplyFaultAsync`），注入在建单之前返回，只能造「建单失败」，造不出「单已建、应答丢了」。

## 与 cs#366 的交界

关卡腿确认前被取消：本票只把它接进 `REQ-0360` 的重建路径。车上有货时重建前的仓位证明（CP-0007）由 cs#366 在本票之上补；在那之前，这一格与确认过的关卡腿
被取消一样，自动重建且不验仓位（审查第一路探针实测，见 PR 合并意见）。`AGateOrderCancelledBeforeConfirmationIsRecordedToBeRebuilt` 只断「登记了、在等」。
已在 cs#366 评论（issuecomment-5862397150）。
