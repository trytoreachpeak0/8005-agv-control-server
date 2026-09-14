# `CONTROL_SERVER_G2`：`052759bc` 上十片全部 **`PASS`**（已发布的 `protocol-v1.0.0`）

**它取代 `../20260912-protocol-v1.0.0-6b21662/`，作为这十片服务端这一半的现行证据。**那一份绑的是 `6b21662`，原样保留、一字未改。

## 为什么要重跑

批次 2 收尾给 `FP-IS-01/02/03/07` 补 G3 面时，真车载端场景撞出了几处服务端产品缺陷，都已修在 `fp/b2-close`：

| 提交 | 修了什么 | 缺陷单 |
| --- | --- | --- |
| `dfdbe9a3` | 一次急停只发一次，发出去的急停由故障循环跟进到确认 | `docs/defects/20260913-emergency-stop-reissued-every-evaluation-and-never-settled.md` |
| `5293c43f` | 取货单确认后、到站前先发一次行程计划，到站后按清单、计划的顺序发 | `docs/defects/20260913-no-plan-snapshot-before-pickup-arrival.md` |
| `6e8dea5a` | 离站等待（`REQ-0237`），默认 5 分钟；离站前才授权装货修正 | — |
| `1312a512` | 迟到的装载结果不再重开已取消的装载 | — |
| `6c252816`、`11f3d69d` | 出发前检查在安全状态变化后作废、以新身份重问；只因答复窗口关闭而过期的，过了证据年龄才重问 | — |
| `e62b136d` | 状态未知的结果跨会话重放后对账，`UNKNOWN` 永不当成功 | `docs/defects/20260913-unknown-result-never-reconciled.md` |
| `d2a19c7a` | 恢复原操作的命令带原装载命令哈希 | `docs/defects/20260914-resume-command-hash-never-matches-vehicle.md` |
| `e6b92ee3` | 恢复收尾后划掉已结清的尝试、重判就绪并通知车载端 | `docs/defects/20260914-recovery-settlement-never-restores-readiness.md` |

`052759bc` 是 G3 共享绑定所指的服务端提交；它相对 `1b1f3dd7` 的 `src/`、`tests/` 零差异，只多了 L2 编排器的两处修正。

## 结论

| 片 | 状态 | `selectedTestCount` | `testExitCode` | 与 `6b21662` 相比 |
| --- | --- | --- | --- | --- |
| `FP-IS-00` | **`PASS`** | 59 | 0 | 相同 |
| `FP-IS-01` | **`PASS`** | 71 | 0 | +2 |
| `FP-IS-02` | **`PASS`** | 25 | 0 | +7 |
| `FP-IS-03` | **`PASS`** | 31 | 0 | +4 |
| `FP-IS-04` | **`PASS`** | 10 | 0 | 相同 |
| `FP-IS-05` | **`PASS`** | 14 | 0 | +2 |
| `FP-IS-06` | **`PASS`** | 40 | 0 | +2 |
| `FP-IS-07` | **`PASS`** | 22 | 0 | +3 |
| `FP-IS-14` | **`PASS`** | 25 | 0 | 相同 |
| `FP-IS-15` | **`PASS`** | 12 | 0 | 相同 |

十份 `gate-result.json` 的身份逐字段一致：

| 字段 | 值 |
| --- | --- |
| `implementationCommit` | `052759bca58a04316dfda260249b3b5697f2cf8e` |
| `protocolTag` | `protocol-v1.0.0` |
| `protocolRepositoryCommit` | `9f22db825d52ad86c1d803bd0c1925dcc58d6793` |
| `protocolManifestSha256` | `a0e1deedb50419057dbe6aa7a7e8df983fb9ea901bbc452f97020ebf4743ef23` |
| `protocolSchemaBundleSha256` | `885191e7a9e5da98a44f17f131756f9eb2033e7e11f13f4df965d4e35ac55685` |
| `protocolApprovalStatus` | **`APPROVED_RELEASE`** |

## 多出的测试

按 `.trx` 里的测试名逐片对比 `6b21662` 那一轮：**没有测试被删**，多出的都是上表修复的修复前红／修复后绿测试。

- `FP-IS-01`：`ThePlanGoesOutBeforeArrivalAndAgainAfterTheWorklistAtThePickup`、`ThePlanSentBeforeArrivalIsRetiredWhenThePickupPlanSupersedesIt`。
- `FP-IS-02`：离站等待的默认值与等待（2 条）、修正打开期间扣车并重新计时、修正只在车仍在取货点等待时授权（3 例）、`ALateResultForALoadAlreadyCancelledIsKeptButReopensNothing`。
- `FP-IS-03`：出发前检查过期后重问的三种情形、`AnUnknownResultReportedAsPendingIsReplayedInTheNextSessionAndReconciledWithoutSuccess`。
- `FP-IS-05`：恢复原操作、补偿清空之后回到就绪（2 条）。
- `FP-IS-06`：计划作废那一条、恢复原操作之后回到就绪。
- `FP-IS-07`：未知结果对账那一条、恢复之后回到就绪（2 条）。

## 装置

从 `b2c` 本地克隆出干净目录 `C:\Users\szy\8005-b3\control-g2-052759bc`，detached 在 `052759bc`，跑完 `git status` 仍干净；`-ProtocolManifest` 指向协议克隆
`C:\Users\szy\8005-b3\proto-gov`（HEAD 即 `protocol-v1.0.0` 指向的 `9f22db8`，跑完干净）。十片由一个 pwsh 脚本依次调用 `scripts/test-wire-to-gate.ps1 -Gate G2 -Slice`。

## 未在本轮证明的

- 只证服务端这一半。车载端十片在车载端仓 `w2g/b3-on-v2` 的 `evidence/g2/20260914-protocol-v1.0.0-8d19fee/`（产品代码与共享绑定 `b960108` 相同）。
- 联合 G3 见同日 `../../g3/20260914-protocol-v1.0.0-*-052759bc/`。
