# cs#543 staged G3 两处判据的红绿证据

票：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/543
运行时间：2026-10-11 11:44–12:07（CST），本机，经 `Invoke-HeavyLocal.ps1 -Ticket cs#543`，暂存目录在 F 盘。
驱动脚本：`run-evidence.ps1`，完整输出：`run-evidence.log`。

**这些运行都不是 G3 门禁证据。**判据脚本取自命令行（`RUNNER_INPUT_OVERRIDE`）或服务端提交取自 `-SelfCheckControlServerCommit`（`SELF_CHECK_OVERRIDE`），不给任何切片记正式通过。它们只用来证明新判据能区分正常与变异的行为。

## 提交

| 用途 | 提交 |
| --- | --- |
| 新判据（最终） | `88f60e448fed508cc08cb2e326e62f8cd81f5a09` |
| 旧判据 | `da6e40e27`（`fp/v2-impl`，本票开分支处）的 `scripts/run-staged-g3.ps1` |
| 「服务端不吭声」变异的服务端 | `1a4452d1bcc9694d400cfe54ea8af5f3ef030ca9`，本地分支 `mut/cs543-silent-stale`，未推送；补丁见 `mutations/server-silent-stale-generation.patch` |

## 第 1 处：`resultFromASupersededSessionGenerationIsRefused`

驱动：`scripts/run-demand-bearing-g3-vectors.ps1`（合成车载端生成带需求的库，明文回环，不起窗口）。
变异：服务端对旧会话代次的报文返回空串——既不回报文，也不断开，连接要等 6 秒静默断开才结束。

| 运行 | 判据 | 服务端 | 结果 | 关键观测 |
| --- | --- | --- | --- | --- |
| A（`red/`） | 旧 | 变异 | `PASS` | 旧判据被骗：10 秒内读不到任何东西就算「已拒绝」 |
| B（`red/`） | 新 | 变异 | `FAIL` | `streamEnded:false`，`elapsedMilliseconds:3010`，无应答 |
| C（`green/`） | 新 | 未变异 | `PASS` | `streamEnded:true`，`elapsedMilliseconds:0`，无应答 |

## 第 2 处：`forced-mechanical-command-lost-to-disconnect`

驱动：`scripts/Test-StagedG3ContentConflict.ps1`（cs#567 的无头自检，它经故障代理跑 `RunRecoveryProbeAsync`，与 runner 同序）。
变异：去掉代理对 `ForcedMechanicalRecoveryCommand` 的 `drop-and-close` 规则（旧判据版：删掉 `AddFault` 那一行；新判据版：规则对象不加入规则表），补丁见 `mutations/`。

| 运行 | 判据 | 变异 | 结果 | 关键观测 |
| --- | --- | --- | --- | --- |
| D（`red/`） | 旧 | 去掉规则 | `FAIL` | 命令被正常转发（代理 `forwarded`），`ExpectClosedAsync` 读到的第一行就是命令，判未关闭 |
| E（`red/`） | 新 | 去掉规则 | `FAIL` | `streamEnded:false`（3050 ms），`dropRuleFired:false`，结束前读到 `ForcedMechanicalRecoveryCommand`、`ExceptionRecoverySessionSnapshot` |
| F（`green/`） | 新 | 无 | `PASS` | `streamEnded:true`，`elapsedMilliseconds:0`，`dropRuleFired:true` |

**D 与票面预期不同。**票面预计旧判据在去掉规则后会因服务端 6 秒静默断开而变绿。实测命令到达了客户端，旧判据读到它就判失败，所以这个变异没有打中旧判据的弱点。旧判据会被骗的情形是「命令根本没到这条连接、连接被服务端静默断开」，即 control-server#306 那次（规则打在了下一条连接上）。新判据对那种情形同样会红：3 秒窗口挡住 6 秒静默断开，`dropRuleFired` 也只认这条规则。

三次自检原有的四项内容冲突判据（`heartbeat`、`business`、`recoverySessionRequestId`、`forcedRecoveryResult`）都是 `AS_EXPECTED`。

## 精简

每次运行只留判定直接用到的文件。第 1 处：`probe-result.json`、`probe-transcript.ndjson`、`run-result.json`、`configuration.json`。第 2 处：`recovery-probe-events.ndjson`、`business-fault-proxy-events.ndjson`、`self-check-result.json`。构建日志、生成库的快照与切片目录没有入库，完整原件留在本机 `F:/8005-scratch/cs543-evidence/`。
