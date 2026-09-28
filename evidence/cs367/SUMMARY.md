# control-server#367 证据：普通腿确认前终态，闸门前后都按确认过的单判（FAILED 进故障模型并能清除重建，取消／删除同车重建，8 交人）

每条标了「读到的」（跑出来的、读代码读到的）还是「推的」。基点 `fp/v2-impl@71e62d85`（已含 cs#357、cs#358）。

## 修前红

- 提交 `78bff6e1`（只动 tests）。新测试类 `FailedOrderBeforeConfirmationTests` 在修前 8 条红，都红在同一处：
  `Assert.Single() Failure: The collection was empty`（没有故障记录），或 REQ-0248 那条的
  `Expected: Pending / Actual: null`（没有急停触发）（读到的）。
- 完整的修前运行记录是 `green/01-reverse-validation-first-pass.txt` 里的 **M0**：把 `src` 退回 `71e62d85`、保留全部新测试，
  188 条里红 11 条，全在新测试类里，别的类一条不红（读到的）。比修前红多出的 3 条是后来补的护栏（`4020b51d`）：
  读不到保住故障码（两个参数）、闸门后确认的单照常起名。第三条护栏「闸门后不为没发出过的单建单」在修前是绿的——
  修前闸门后根本不对账，自然不建单；它守的是本票新加的对账不越界，判别力由 M5 撑着。

## 反向验证（green/01～03）

脚本 `green/mutate.ps1`：每个变异先确认替换恰好命中 1 处，`--no-incremental` 重编且 `0 Error(s)`，跑相关的 7～8 个测试类，
读 `dotnet test` 退出码，跑完从 HEAD 还原（读到的）。第一遍里 M2、M8 编译失败（写成 `if (true)`／`if (false)`，
不可达代码按警告即错误处理），那两格不算数，换成编译器看不穿的条件在 `02` 里重跑。

| 变异 | 红了什么 | 为什么别的不红（推的） |
| --- | --- | --- |
| M0 整个修复撤回 | 新测试类 11 条 | 见上 |
| M1 闸门前不交给故障模型 | 闸门前取货、关卡两条；两条清除后重建（故障在闸门前记）；读不到保码 `behindTheGate: False` | 闸门后那条路独立 |
| M2 闸门后整段关掉 | 闸门后 6 条（`02`）；加上计划测试类后另红 `AnOrderWhoseCreateAnswerWasLostIsConfirmedBehindTheGateAndThePlanGoesOut`（`03`，共 7 条） | 清除用例的故障在闸门前记 |
| M3 清除入口不认 Hold 审计 | 两条清除后重建 | 只有它们靠这个认法放行 |
| M4 认 Hold 时不看故障代次 | 反向用例 `AFaultOfAnotherOriginIsNotClearedThroughATerminalIntentAnEarlierFaultWasRecordedOn` | — |
| M5 闸门后对没发过建单的意图也对账 | `AnOrderNeverSentIsNotCreatedBehindTheGate` | — |
| M6 读不到时不保码 | 读不到保码，两个参数 | — |
| M7 记下后只喂一次故障模型 | REQ-0248 那条 | 首轮记故障的断言只看第一轮 |
| M8 闸门后确认了不起名 | HANG 那条 | — |

## 本机 L2（l2/）

`command-surface-order-hold`、`emergency-stop-single-trigger`、`emergency-stop-operator-release` 在 `d67221fa` 上各跑一次，全部 PASS
（读到的）。只留 `SUMMARY.md` 与 `assertions.json`。前两条的目录名在运行时写错成字面量，跑完改名挪进来，内容没动（读到的）。

## cs#342 断线重连模型（green/04）

同一组 300 个固定种子（masterSeed=342），本票 `d67221fa` 与基点对照。基点取 cs#357 的
`evidence/cs357/green/08-cs342-model-300-3742cbc9.txt`：`git diff 3742cbc9 71e62d85 -- src tests` 为空，两棵树的代码与测试逐字相同（读到的）。
逐项相同：录入请求 300/300 送达、确认冲突 0、回退 0、握手接受语义不同的消息 0、「带等人码的轮次推进失败」都是 `ORDER_HANG` 5 次（读到的）。
每组合耗时 123.0 ms 对 115.9 ms，负载没控制（同时有别的会话在跑全量），不据此下结论（推的）。

**模型测不到的格**（推的，依据是 cs#342 PR 正文「模型的局限」与本票的动作）：模型不产生 FAILED、取消，也不丢建单应答，
所以本票的每一格它都走不到；它在这里只证明没改坏断线重连那一块。本票的格由上面的 L1 与变异覆盖。

## 全量

- `d67221fa` 之前那次全量（`0eb26ea2`）：2868 条红 1 条，`PickupDispatchPlanPastOwnOrderTests.NoPlanGoesOutWhenTheUnreadinessIsNotExplainedByTheOwnOrder("order-not-confirmed")`。
  机理：那一格把 RIoT 上正常执行的单改成 `RESULT_UNKNOWN`，本票闸门后的对账同一轮就确认了它，计划随之放行——按 cs#314 这是对的。
  改的是造前提的方式（让 RIoT 这一轮答不出），断言没改，并补了前提断言；见 `d67221fa` 的提交说明（读到的）。

## 并入取消格之后（调度 2026-09-28）

调度把「确认前 CANCELLED/DELETED」并入本票（准入线第 3 条），SUSPENDED(8) 交人。

- 修前红：`39e61e99` 只动 tests，7 条红，都停在 `{PICKUP|GATE}_TerminalReconciliationRequired`（闸门后 `ONBOARD_SESSION_NOT_READY`），
  原文在 `red/01-cancelled-deleted-suspended-at-5bb47d0b.txt`（读到的）。
- 修复 `dabbfcc3`：终态意图交给 `NameStalledOrderAsync`，与确认过的单同一个方法。
- 反向验证最终版 `green/05-reverse-validation-final-dabbfcc3.txt`（读到的）：M0 整体撤回红 19 条（新测试里除「不为没发过的单建单」与反向用例外全部，
  这两条修前也绿，判别力分别由 M5、M4 撑着）；新增 **M9 终态只看 FAILED**（交给 `ObserveOrderFailureAsync` 而不是 `NameStalledOrderAsync`）
  恰好红取消、删除、关卡腿登记、REQ-0361 两条、SUSPENDED 两格、读不到保码两格；M1～M5、M7、M8 各只红预期的。原 M6（读不到保码）随实现换成共用方法而作废，
  那一行为由 M9 覆盖。
- 最终 head `85348e72`：全量 2876 条通过、退出码 0（读到的）；三条故障路径 L2 在 `l2-final/` 各一次，全 PASS，SUMMARY 里的 `controlServerCommit`
  是 `85348e72`（读到的）；`l2/` 是 `d67221fa` 上的那一轮，留作记录。cs#342 模型 `green/06-*`，与基点逐项相同（读到的）。
- 过程中我的一次失误：最终那一串里 L2 误带了 `-SkipBuild`，会跑到旧二进制。在它开始之前用预先建好的证据目录把三条拦下（脚本要求目录必须是新的，
  三份日志各有 `EvidenceRoot must not exist`），再不带 `-SkipBuild` 重跑，才有 `l2-final/`（读到的）。

**与 cs#366 的交界**：关卡腿确认前被取消，本票只把它接进 `REQ-0360` 的重建路径；车上有货时重建前的仓位证明（CP-0007）由 cs#366 在本票之上补。
`AGateOrderCancelledBeforeConfirmationIsRecordedToBeRebuilt` 因此只断「登记了、在等」，不断出不出单。已在 cs#366 评论（issuecomment-5862397150）。

## CI 真装置（run 36374996508）

`l2.yml` `rig=real`，两个场景各一遍，head `a43609e0`（产品代码与 `85348e72` 相同，之间只有证据提交）。四行核对（读到的）：

| 项 | 值 |
| --- | --- |
| control-server | `a43609e0e7fde4da0ace700c28335d299510b92d` |
| 8005-agv-onboard-hmi | `4c2d2dc14656f80e812f37128a7964c2c310217a`（`w2g/fp-v2-impl` 顶端，派发前 `ls-remote` 核过） |
| slots-simulator | `fb5f7c593742bf98bc3957b8729a38aad5321f28`（`main` 顶端，同上） |
| 真跑了 | `real-onboard-compensate-then-reconnect-01 -- PASS, 58s`、`real-onboard-order-hang-continue-01 -- PASS, 31s`；三个停止码命中 8 行，全是 `^[[36;1m` 源码回显 |

artifact `real-rig-evidence` 231593 字节；两份 `assertions.json` 的 `outcome` 分别 10/10、8/8 为 `PASS`，没有 `FAIL`（读到的）。
