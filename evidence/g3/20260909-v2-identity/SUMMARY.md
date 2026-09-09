# 2026-09-09 协议 v2 身份下的 G3：三个 runner 各跑一次，两过一败

## 结论

| runner | 运行 ID | 结果 | 断言 |
| --- | --- | --- | --- |
| `run-staged-g3.ps1` | `20260909T073011387Z` | `STAGED_G3_RECOVERY_REPLAY_PASS` | 19 / 19 |
| `run-staged-g3-restart.ps1` | `20260909T073227665Z` | `STAGED_G3_PROCESS_RESTART_PASS` | 20 / 20 |
| `run-demand-bearing-g3-vectors.ps1` | `20260909T073439654Z` | **`DEMAND_BEARING_SLICE_FAIL`** | **19 / 20** |

三个 `status` 与全部断言值都是 runner **自身发射**的机器可读结果，落在各自的
`run-result.json` 里，本文档只是转述，不是判定来源。三次 `secretLeakFiles` 均为空数组。

**失败那份证据保留，不重跑覆盖。**理由见下面第三节——它失败在一条关于历史的断言上，
不是关于被测构建的断言。

## 冻结身份

| 组件 | commit | 来源 |
| --- | --- | --- |
| ControlServer | `e3ea250d6eb4c0f3f11814c938da531b1657edb7` | `fp/v2-impl`，从本地工作副本克隆 |
| OnboardHmi | `153b70594f75ce945e717afd80be9f6279423080` | `origin/w2g/fp-v2-impl`，2026-09-09 推送 |
| slots-simulator | `fb5f7c593742bf98bc3957b8729a38aad5321f28` | `origin/main`，未变 |
| protocol | `f6ee75defe6e2d18f63f4082bee445dbb678ab1b` | `origin/fp/v2-candidate`（v2 候选） |
| harness／runner | `c6cc965615b4155f8357276dc27a11b4b0bb15a9` | 跑这三次的脚本自身 |

**harness 比被测的 ControlServer 新两个提交**，那两个只动 `scripts/`（`git diff --stat
e3ea250 1987b73` 两个文件全在 `scripts/` 下），服务端内容逐字节相同。这正是
`harnessCommit`／`runnerCommit` 与 `controlServer` 分开记的原因。

`runnerWorktreeCleanAtStart` 在后两次是 `false`：第一个 runner 的证据目录此刻还是未跟踪
文件。与 2026-09-04 那一轮同形（那轮也是第一次 `true`、后两次 `false`）。

### 协议 tag 未打，绑的是候选 commit

`run-result.json` 的 `protocol` 节记 `tagExists: false`、
`candidateCommit: f6ee75de…`、`approvalStatus: SUPERSEDING_CANDIDATE`。
`protocol-v1.0.0` 至今没打——规格 6.6 要两名产品负责人 attestation ＋ 注释 tag，两件都没发生。

**这一轮之前 `run-staged-g3.ps1` 断言该 tag 解析得到候选 commit，在 v2 线上必然开跑即抛。**
票 15 已在车载端做过同一处改动（`run-w2g-g2.ps1:370`），控制端这次补上。
tag 若存在却指向别处仍然抛。

### 协议 G1 在三次运行里都跑通

`logs/protocol-g1.log`：`"status": "PASS"`、`failures: []`、
`candidateManifestSha256` = `84f984ea…`，与协议仓已提交的 `evidence/g1-result.json` 逐字段相同。

## `run-demand-bearing-g3-vectors.ps1` 为什么失败

唯一失败的断言是 `protocolAndBuildIdentityBoundToTheSharedBinding`
（`run-demand-bearing-g3-vectors.ps1:614`）。它是六个合取项，其中五个通过：

| 合取项 | 实际 | 结论 |
| --- | --- | --- |
| `$version.protocolCommit -eq $ProtocolCommit` | `f6ee75de…` | ✅ |
| `$version.protocolTag -eq 'protocol-v1.0.0'` | `protocol-v1.0.0` | ✅ |
| `$probeResult.serverBuildCommit -eq $ControlServerCommit` | `e3ea250d…` | ✅ |
| `$baseline.sessionRecoveryRows[0]['protocolCommit'] -eq $ProtocolCommit` | **`1531489e…`** | ❌ |

`1531489e42e328f28bfe0c51ed3f8c56e5ce0279` 是 **`protocol-v0.1.1` 的 commit**。那一行是
2026-08-29 那次已授权现场运行（`C:\Users\szy\w2g-stage\run\fullloop-20260829T131549Z`，
只读恢复）在**当时的协议**下写进 `SessionRecoveries` 的。

**所以这条断言问的不是被测构建，而是这份现场库的历史。**现场库是 v0.1.1 时代的冻结产物，
任何 v2 身份的运行对它都过不了这一条——除非重新采一次 v2 下的已授权现场运行。

被这个 runner 真正要证的那 19 条全部通过：

- RIoT 预建单对账逐腿观察到 `UNKNOWN`，`UNKNOWN` 是一次精确的 absent-at 观测，
  仍然逐腿恰好建一次单，且解析回它自己建的那张单（4 条）
- 已 `Prepared` 的 attempt 接受它的第一份结果；同内容重放返回存下的那份确认；
  同 `messageId` 不同内容被拒；同 attempt 同 generation 换 `messageId` 被拒；
  已 `Committed` 的 attempt 拒第二份结果；被取代的 session generation 的结果被拒；
  重放没有被处理两次（7 条）
- 卸货结果原子收尾需求（1 条）
- 宿主进程确实被替换、被接受的需求与车辆租约在重启后存活、重启后的宿主服务同一个库（4 条）
- 无移动与外部副作用、监听器释放、密钥扫描（3 条）

库的计数在探针前后如实变化：`OperationResults` 1→2、`UnloadBatches` 0→1、
`StopClosures` 0→1、`TransportDemandCompletions` 0→1，`AcceptedDemands` 那一行由
`Accepted` 变 `Succeeded`。**这些都写在 StageRoot 的副本上，`-FieldRunRoot` 只读。**

## 本次不是什么

三次运行的 `classification` 均据实记为 `formalSlicePass: false`、`fullG3: INCONCLUSIVE`、
`releaseCandidate: INCONCLUSIVE`。三个 runner 加起来只提到四个正式切片
（`FP-IS-00`／`04`／`05`／`06`），且都保持 `INCONCLUSIVE`。

**这不是本轮的疏漏，是这三个 runner 的既定立场**，与 2026-09-04 那一轮逐字相同：
staged G3 绑 commit、从 exact clone 重新 publish、不碰任何候选产物，因此不构成切片通过。
`FP-IS-01`／`02`／`03`／`07` 在任何 G3 runner 里都不出现。

**所以票 17 的「八个切片各跑一遍 G3 全 PASS，八份 `gate-result.json`」用现有 runner 做不出来。**
三个 runner 都只发 `run-result.json`，全仓只有 `test-wire-to-gate.ps1` 发 `gate-result.json`。
要做出来需要一次 G3 runner 的按片改造（与票 22 给车载端 G2 做的同形），
并先定清「staged G3 下一片算不算通过」。**这不在本轮授权内。**

## 同一轮的其他层

| 层 | 结果 |
| --- | --- |
| `ControlServer.Tests` | `587 passed / 0 failed / 0 skipped`，Debug 与 Release 各一遍 |
| 车载端 `SQCD.Agv.UnitTests` ＋ `SQCD.Agv.WireToGateG2Tests` | `164 + 42 passed / 0 failed / 0 skipped` |
| 协议 G1 | `PASS`，`failures: []` |
