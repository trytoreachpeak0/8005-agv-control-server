# 批次 10 出口报告（v2 线）：同向四类任务放开，`protocol-v3.0.0`

control-server#549（批次10-06）。本报告逐项对照规格 `8005-agv-program/docs/specs/full-product-scope-and-sequence-v2.md`
第 8.2 节默认出口与第 8.3 节批次 10 行，写法照批次 6 出口报告（`docs/batch-6-v2-exit-report.md`）。
需求条目按基线 **`v1.9.0`**（tag `requirements-baseline-v1.9.0`）引用。本批不改协议、不新增切片，门禁与证据绑 `protocol-v3.0.0`。
追踪票是 8005-agv-program#169。

> **状态：出口达成，待调度审查。第 1 步 CI 三连、第 2 步 `CONTROL_SERVER_G2 FP-IS-10`、第 3 步 G3 四个 runner 一次全绿，没有红证据。出口 PR 的 CI 结论见 PR 检查页。**

## 结论

**批次 10 出口达成（合成装置与门禁层面）。**在 `protocol-v3.0.0` 发布身份上：批次 10 的六个合成 L2 场景 CI 连续三次、18 次全 PASS；`CONTROL_SERVER_G2 FP-IS-10` PASS；G3 共享绑定移到 `37a86cbc`／`535c94fc` 后四个 runner 各一轮全 PASS，覆盖 15 片，全部 `formalSlicePass true`，其中 `g3-task-type-admission-fail-closed` 证明放开四类之后缺绑定仍不受理。服务端全量 L1 在冻结基点上 4700/4700。

这一轮也是 control-server#556（G3-07-44 原因码判据）与 control-server#567（staged 的 harness 干净判定、`g3-slice-evidence.ps1` 抽出的函数、充电与 G3-13-27 读法）第一次进入正式证据，两处都在正式一轮成立（第三节）。

**本报告不写「六类都跑通」。**同向四类（`DIE_TO_WIRE_STAGING`、`DIE_TO_OVEN`、`WIRE_TO_OPTICAL`、`WIRE_TO_NITROGEN`）
只在合成装置（假 RIoT、假 MesIngest、合成车载端）上跑通，现场不生效（第五节第 1 点）。

| 出口（规格 8.2／8.3 批次 10 行、本票验收） | 状态 | 依据 |
| --- | --- | --- |
| 开跑前核对逐条结论写进报告 | 成立 | 「前置核对」 |
| L1 绿，新能力逐项有新增测试 | **成立**：服务端 4700/4700（冻结基点 `37a86cbc`，CI test run `38068715514`）；新能力逐项对照见下表 | 第一节 |
| 批次 10 全部新场景（六个）CI 连续三次通过，三份证据各自独立，`identity` 含 `protocolReleaseIdentity` 与 `batchId = batch-10` | **成立**：run [`38060390711`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/38060390711)，6 × 3 = 18 次全 PASS，183 条断言全 PASS，逐份核对 | 第二节 |
| 第 8.3 节批次 10 行：同向四类各一条 L2「缺绑定不投运、绑定完备后走完一趟，含 AREA 端点按侧取仓」 | **成立**（合成装置） | 第二节对照表 |
| `CONTROL_SERVER_G2 -Slice FP-IS-10` PASS，身份 `protocol-v3.0.0` | **成立**：72/72 PASS，schema 一致性 98 行违规 0，`gate-result.json` 绑 `protocol-v3.0.0`／`APPROVED_RELEASE` | 第三节 |
| G3：移绑定（单独的 `chore(g3)` 提交），四个 runner 各一轮；journey 的 `g3-task-type-admission-fail-closed` PASS | **成立**：移绑定提交 `d4f83e59`；staged、restart、demand-bearing、journey 全 PASS，journey 20/20，15 片全部 `formalSlicePass true`；`g3-task-type-admission-fail-closed` 的 `G3-10-01`～`09` 全 PASS | 第三节 |
| 现场状态按票面三选一如实写 | 已写 | 第五节第 1 点 |
| 部署约束四步，分清读到的与推断的 | 已写 | 第四节 |
| 出口 PR 的 CI 绿 | 见 PR 检查页（本报告提交时尚未跑） | PR 检查页 |
| 只改报告、本批证据、G3 共享绑定；不碰 `src/` | 成立：出口分支相对 `37a86cbc` 只改 `scripts/run-staged-g3.ps1` 的绑定（`d4f83e59`）、本报告与 `evidence/` 下本批证据；`src/`、`tests/`、`tools/`、`.github/` 零差异；`l2.yml` 不用改（第二节） | 「身份」 |

## 前置核对（2026-10-10 22:40 起实查）

| 项（票面「开跑前核对」） | 结果 |
| --- | --- |
| 批次10-01 control-server#545 已合入 | 成立：PR #550，`297cc686`（10-10 18:10），issue CLOSED |
| 批次10-02 control-server#546 已合入 | 成立：PR #572，`7ce1f997`（10-10 21:36），issue CLOSED |
| 批次10-03 onboard-hmi#289 已合入 | 成立：PR onboard-hmi#290，`535c94fc`（10-10 17:12），即现在 `w2g/fp-v2-impl` 的顶端，issue CLOSED |
| 批次10-04 control-server#547、批次10-05 control-server#548 的状态（不挡出口） | 开跑时（22:40）两张都开着；cs#547 随后于 10-10 22:56 关闭（建站与回填完成）；cs#548 仍开着（合入前再核一次）。见第五节第 1 点 |
| `l2.yml` 里 `BatchId = 'batch-10'` 的行恰好是 cs#546 登记的场景 | 成立：恰好六行（`.github/workflows/l2.yml:418-423`），`scripts/l2/scenarios/` 下 `same-direction-*.ps1` 恰好也是这六个，没有未登记的。六行在 cs#546 合入时已是 `Runs = 3`，**本票不用改 `l2.yml`**。cs#546 标题写「加五行」，实际合入六行：混装多终点 `same-direction-mixed-load-multi-drop` 单独成场景，负向「缺绑定」是 `same-direction-binding-missing-not-cascading` |
| 集成分支的协议身份是 `protocol-v3.0.0`，各证据身份与它逐项相等 | 成立：`src/ControlServer.Host/appsettings.json` 的 `ProtocolCandidate` 见「身份」；18 份 L2 证据、G2 与 G3 的身份逐项比过（第二、三节） |
| 本批没有改协议，不重跑发布态 G1 | 成立：两端合入的批次 10 票都没有碰协议仓；协议发布态 G1 证据引 8005-agv-program#152 |
| 本机没有别的真装置 L2 或 G3 在跑 | 成立：开跑前 `Get-HeavyLocalStatus.ps1` 显示桌面锁空闲、重负载 0；两次本机时段都由调度放行，持有者行「cs#549 封锁」 |

## 身份

| 项 | 值 |
| --- | --- |
| 协议 | `protocol-v3.0.0` → `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c`（tag 对象 `e08c362e`），`(AGV_FULL_PRODUCT, 4)`，`releaseVersion 3.0.0`，`APPROVED_RELEASE`；manifest `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e`，schema bundle `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43`，vectors `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e`。本批零改动 |
| 服务端产品代码 | `fp/v2-impl@37a86cbc`（冻结基点：批次 10 的 cs#545、#546 与同期合入，见第一节）。出口分支 `chore/b10-06-batch10-exit` 从 `bf1d56d2` 开出（第 1 步三连跑在这里），开冻时快进到 `37a86cbc`，两者只差 `.editorconfig` |
| 车载端 | `w2g/fp-v2-impl@535c94fc`（hmi#289，PR onboard-hmi#290）。本批车载端没有门禁证据要入库 |
| 模拟器 | `main@fb5f7c59`（不变） |
| G3 共享绑定 | `d4f83e59`（`chore(g3)`，单独一个提交）：`ControlServerCommit 37a86cbc`、`OnboardCommit 535c94fc`、`SimulatorCommit fb5f7c59`（不变）、`ProtocolCommit 3f091cb2`（不变）。原绑定是批次 8 出口的 `76c9cfe2`／`b9e67a53`。`b9e67a53..535c94fc` 是 onboard-hmi#288 的证据（只有 `evidence/`）与 hmi#289 的两个测试文件，车载端没有产品改动 |
| 冻结 | 第 1 步 CI 三连不冻结（调度 2026-10-10 改定）。第 2、3 步：2026-10-11 00:40 起冻结 `fp/v2-impl`（`37a86cbc`）与 `w2g/fp-v2-impl`（`535c94fc`），到本出口 PR 合入为止（调度先申请两条一起冻）。开冻前修复批合入的 PR #278 只改 `.editorconfig`。开跑前 `git ls-remote` 实读两端顶端与调度给的值一致 |

## 一、L1

### 实测

| 端 | 来源 | 结果 |
| --- | --- | --- |
| 服务端 | 修复批在冻结基点 `37a86cbc` 上手动跑的 `test.yml`，run [`38068715514`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/38068715514)（run 号由调度转述，结论本票实读） | **4700 / 4700 通过**，原文 `已通过! - 失败: 0，通过: 4700，已跳过: 0，总计: 4700`。出口分支相对它只多 G3 绑定、证据与本报告，`src/`、`tests/` 零差异；出口 PR 那一轮 CI 再兜一次 |
| 车载端 | hmi#289 的 PR CI run [`38040057589`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/actions/runs/38040057589) | success。合入后的 push CI 是红的，见第六节 |

本票只定向跑过一条测试：扫 `docs/` 全文的退役名护栏 `Batch8OccupancyRetirementMigrationTests.NothingUnderScriptsTestsSrcToolsDocsOrWorkflowsNamesTheRetiredOccupancyAnyMore`，报告定稿后 1/1 通过（`--filter` 定向，本机）。

### 批次 10 每项新能力 ↔ 新增测试

| 票 | 需求条目（`v1.9.0`） | 新能力 | 新增测试 | 合入 |
| --- | --- | --- | --- | --- |
| control-server#545（批次10-01） | `REQ-0184`、`REQ-0335`、`REQ-0202` | 同向四类加入可执行集合（`ExecutableTaskTypes`），准入种子由两类变六类；准入策略版本 2 → 3，`appsettings.json` 与 `scripts/parallel/` 下实例定义一起升；「可执行集合变了而版本没升」由护栏判红 | `AdmissionPolicyVersionGuardTests`：`TheVersionTableMovesStrictlyUpward`、`TheExecutableSetThisBuildShipsIsTheLastRowOfTheVersionTable`、`EveryProtocolTaskTypeIsDecidedEitherExecutableOrNot`、`NoShippedAdmissionPolicyVersionIsBelowTheLastRowOfTheVersionTable`、`TheShippedAdmissionPolicyVersionsAgree`；`SameDirectionTaskTypeJourneyRuntimeTests`：`ABoundSameDirectionTaskTypeLoadsAtTheAreaMachineIntoItsGroupAndUnloadsAtTheBoundStation`、`AnUnboundSameDirectionTaskTypeStopsOnlyItselfAndWireToGateIsAcceptedInTheSameRound`、`ThePreviousBuildsSeedUnderTheSameVersionIsDriftAndTheRaisedVersionIsNot`、`TheSixTypeSeedAppliedAgainAfterARestartUnderVersionThreeIsTheSameBinding`、`AnOldPackageInstalledUnderVersionThreeTakesTheVersionAndThisBuildThenDrifts`、`AfterVersionThreeIsBoundTheRolledBackBuildBindsOnlyUnderAHigherVersion`；`TaskTypeAdmissionChainTests.AMissingBindingIsNamedAsSuchForEveryTaskTypeTheDeploymentAllows`（共 12 个 `[Fact]`／`[Theory]`） | PR #550 `297cc686` |
| control-server#546（批次10-02） | `REQ-0184`、`REQ-0187`、`REQ-0334`、`REQ-0335` | 同向四类合成 L2：四类各一条走完一趟（按侧取仓）、一条四类缺绑定不投运、一条混装多终点 | 无新增 `[Fact]`；六个 L2 场景（第二节） | PR #572 `7ce1f997` |
| onboard-hmi#289（批次10-03） | 车载半边 | 核对任务类型在车载端只用于入站校验与显示文案（结论在 PR 里），用 L1 钉住四类的文案与入站校验；不改产品代码 | `InboundPayloadSchemaBoundaryTests.AWorklistMixingTheFourSameDirectionTypesWithWireToGateIsAccepted`、`WireToGateStopFactsTests.EachOfTheFourSameDirectionTaskTypesShowsItsOwnTextOnItsRow` | PR onboard-hmi#290 `535c94fc` |

测试名取自各合入提交新增的 `[Fact]`／`[Theory]`，可用 `git diff <merge>^1 <merge> -- tests` 复现。

## 二、L2

### 第 8.3 节批次 10 行 ↔ 场景 ↔ 证据

六个场景都是合成 L2。站号是合成装置里的：四类各绑一个固定站（`DIE_TO_WIRE_STAGING` 401「派工待送卸货」、`DIE_TO_OVEN` 402「烘箱」、
`WIRE_TO_OPTICAL` 403「三光」、`WIRE_TO_NITROGEN` 404「氮气柜」），归属表 `N1-3 → REAR`、`N1-7 → FRONT`，同挂 AREA 机台站 12。
**这些站号不是 26 号图上的站号**（现场站号见第五节第 1 点）。

| 判据 | 场景 | 判据编号 | 证据目录（CI 三连，3/3 PASS） |
| --- | --- | --- | --- |
| 缺绑定不投运：四类都不受理，积压原因 `TASK_TYPE_BINDING_MISSING`，看板列出并带中文说明；不是结构性告警；同一次运行里 `WIRE_TO_GATE` 照常受理走完，不被连带；再转两轮仍不受理 | `same-direction-binding-missing-not-cascading` | `L2-SDBM-01`～`06` | `evidence/l2/20261010-ci-38060390711-same-direction-binding-missing-not-cascading-01`～`03/` |
| `DIE_TO_OVEN` 绑定完备走完一趟：AREA 机台站取货、绑定站 402 卸货、两端不对调；`N1-3` 装进本车 `REAR` 组、`N1-7` 装进 `FRONT` 组，都是该组编号最小的可用仓；结清 | `same-direction-die-to-oven-journey` | `L2-SDJ-DOV-00`、`-REAR-01`～`05`、`-FRONT-01`～`05` | `evidence/l2/20261010-ci-38060390711-same-direction-die-to-oven-journey-01`～`03/` |
| `DIE_TO_WIRE_STAGING`，同上，绑定站 401 | `same-direction-die-to-wire-staging-journey` | `L2-SDJ-DWS-*`（11 条） | `…-same-direction-die-to-wire-staging-journey-01`～`03/` |
| `WIRE_TO_OPTICAL`，同上，绑定站 403 | `same-direction-wire-to-optical-journey` | `L2-SDJ-WOP-*`（11 条） | `…-same-direction-wire-to-optical-journey-01`～`03/` |
| `WIRE_TO_NITROGEN`，同上，绑定站 404 | `same-direction-wire-to-nitrogen-journey` | `L2-SDJ-WNI-*`（11 条） | `…-same-direction-wire-to-nitrogen-journey-01`～`03/` |
| （补充）混装多终点：`WIRE_TO_GATE`、`WIRE_TO_OPTICAL`、`WIRE_TO_NITROGEN` 三单在停站时追加进同一趟，各装进自己 AREA 指派的那一组，在三个绑定站依次各卸各的；上限调小后第二单在追加延误门被拒 | `same-direction-mixed-load-multi-drop` | `L2-SDMX-01`～`11` | `…-same-direction-mixed-load-multi-drop-01`～`03/` |

`L2-SDMX-02` 只证明两次追加都发生在停站窗口里，**不证明行驶中会拒绝追加**，那一条的合成侧证据由 control-server#577 补（不挡本出口）。

### CI 三连

- **为什么用手动 dispatch，不靠 PR**：六行在 cs#546 合入时已是 `Runs = 3`，本票没有要改的 `l2.yml`；PR 的默认一轮每个场景只跑一遍
  （这六行不带 `DefaultRuns`）。所以在出口分支上手动触发，模式用 `consecutive-all`（每个场景至少三遍，与登记的 `Runs = 3` 结果相同，
  批次出口的惯例），只跑这六个场景：
  `gh workflow run l2.yml --ref chore/b10-06-batch10-exit -f mode=consecutive-all -f scenarios=<六个>`。
- 运行：run [`38060390711`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/38060390711)，提交 `bf1d56d2`
  （出口分支当时与 `fp/v2-impl` 顶端同一提交，没有新提交），结论 success。
- 下载后逐份核对（读到的）：18 份 `assertions.json` 的 `outcome` 都是 `PASS`，183 条断言都是 `PASS`；
  `identity.protocolReleaseIdentity` 的 `tag`、`commit`、三个哈希、`approvalStatus` 都与「身份」一节相等；`identity.batchId` 都是 `batch-10`；
  `identity.controlServerCommit` 都是 `bf1d56d2608597d86fec9fb530f7d393cf3ffe0a`；18 个 `runId` 与 18 个 `stageRoot` 各不相同，三遍各自独立。
- **三连跑的是 `bf1d56d2`，出口提交比它多两样**：开冻前合入 `fp/v2-impl` 的 PR #278（`37a86cbc`，`git diff --stat bf1d56d2 37a86cbc`
  原文 `.editorconfig | 11 +++++++++++`，只此一个文件），以及本票自己的 G3 绑定、证据与本报告。合成 L2 走的 `src/`、`tests/`、`tools/`、
  `scripts/l2/` 都没变，三连结果沿用。旁证：修复批在冻结基点 `37a86cbc` 上手动跑的顶端兜底全绿——test run
  [`38068715514`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/38068715514)（原文
  `已通过! - 失败: 0，通过: 4700，已跳过: 0，总计: 4700`）、l2 run
  [`38068720173`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/38068720173)（`mode=default`，`scenarios` 作业 success）；
  两个 run 号由调度转述，结论本票实读。

### 真装置

本批不需要真装置（票面「资源」一节）：第 8.3 节批次 10 行的判据全部由合成 L2 证明；车载端本批只有 hmi#289 一张测试票，
没有改车载端行为。真车载端上的准入由 G3 journey 的 `g3-task-type-admission-fail-closed` 证明（第三节）。

## 三、门禁

本批不新增切片。按票面，门禁只重证被批次 10 代码直接触到的地方：`FP-IS-10` 的服务端 G2（cs#545 改了它的准入测试），
以及 G3 四个 runner（移绑定之后全部失效，必须全部重跑）。协议本批零改动，G1 引 8005-agv-program#152 的发布态证据，不重跑。
车载端本批只有 hmi#289 一张测试票，不跑 `ONBOARD_HMI_G2`。

### 服务端 G2：`FP-IS-10`

| 项 | 值 |
| --- | --- |
| 命令 | `scripts/test-wire-to-gate.ps1 -Gate G2 -Slice FP-IS-10 -ProtocolManifest <protocol-v3.0.0 的干净克隆>/manifest/release.json -Output <新目录>` |
| 时间 | 2026-10-11 00:43～00:45（冻结之后） |
| 结果 | **`status PASS`**；72/72 测试通过（原文 `Passed! - Failed: 0, Passed: 72, Skipped: 0, Total: 72`）；schema 一致性 `linesChecked 98`、`linesInViolation 0` |
| 身份 | `implementationCommit d4f83e59`（出口分支移绑定之后，`src/`、`tests/` 与 `37a86cbc` 相同）；`protocolTag protocol-v3.0.0`、`protocolRepositoryCommit 3f091cb2`、manifest `d5e1a53f…`、`APPROVED_RELEASE`，与「身份」一节逐项相等 |
| 证据 | `evidence/g2/20261011-protocol-v3.0.0-FP-IS-10-d4f83e59/` |

### G3 预跑（不是证据）

调度改定第 3 条：第 1 步 CI 三连期间，先在本机用自检覆盖对 fp 顶端预跑四个 runner，把脚本问题挪到冻结之前。
2026-10-10 22:53～2026-10-11 00:06，在一个临时 detached worktree（`bf1d56d2`）里临时改绑定默认值（不提交），
服务端 `bf1d56d2`、车载端 `535c94fc`：staged、restart、demand-bearing、journey 全 PASS，journey 20/20、156 条断言 0 失败。
按设计每片 `formalSlicePass false`（`SELF_CHECK_OVERRIDE; RUNNER_WORKTREE_DIRTY`），staged 的 `harnessWorktreeCleanAtStart` 也必然是 `false`
（它量的正是被临时改过的那棵工作树），所以 cs#567 那处只能在正式一轮验证。预跑**没有发现任何脚本问题**，正式一轮没有插票。
预跑证据不入库，临时 worktree 与 stage 用完即删。

### G3 正式一轮

绑定 `d4f83e59`（见「身份」），2026-10-11 00:45～01:46，四个 runner 依次各一轮，从出口 worktree 跑，对端由 runner 自己从 GitHub
按绑定精确克隆。四个都是 `runnerSource COMMITTED_RUNNER`，四个提交来源都是 `SHARED_BINDING`，失败断言都是 0。

| runner | 时间 | 结论 | 认领切片（`formalSlicePass`） | 证据 |
| --- | --- | --- | --- | --- |
| staged | 00:45～00:53 | `STAGED_G3_RECOVERY_REPLAY_PASS` | `FP-IS-00`、`06`、`07`、`14`、`15`（都 true） | `evidence/g3/20261011-protocol-v3.0.0-staged-37a86cbc/` |
| restart | 00:55～01:04 | `STAGED_G3_PROCESS_RESTART_PASS` | `FP-IS-00`、`06`、`14`、`15`（都 true） | `…-restart-37a86cbc/` |
| demand-bearing | 01:05～01:11 | `DEMAND_BEARING_G3_VECTORS_PASS`，16/16 | `FP-IS-04`、`05`、`06`（都 true） | `…-demand-bearing-37a86cbc/` |
| journey | 01:13～01:45 | `JOURNEY_G3_PASS`，20/20 场景，156 条断言 | `FP-IS-01`、`02`、`03`、`07`、`08`、`10`、`11`、`12`、`13`（都 true） | `…-journey-37a86cbc/` |

并集是 `FP-IS-00`～`08`、`10`～`15`，共 15 片，全部 `formalSlicePass true`（`FP-IS-09` 属批次 11，本线还没有 G3 面）。

**本批点名要看的几条（读到的）：**

- `g3-task-type-admission-fail-closed`（`FP-IS-10`）：`G3-10-01`～`09` 全 PASS。放开同向四类之后，缺绑定的任务类型仍不受理：
  `G3-10-03` 实际值 `TASK_TYPE_BINDING_MISSING`、无受理时间；`G3-10-01` 受理 0、旅程 0、意图 0、操作 0；`G3-10-02` 发件箱 0 条。
  票面「测试接缝」担心的情况（cs#545 删了 `TASK_TYPE_NOT_YET_EXECUTABLE` 之类、让这个场景失去依据）没有出现。
- control-server#567：staged 的 `commits.harnessWorktreeCleanAtStart` 为 **`true`**（预跑里必然是 false，见上）；抽进 `g3-slice-evidence.ps1` 的函数
  四个 runner 都走到了，`formalSlicePass` 由它们算出。
- control-server#556：`G3-07-44`（`g3-forced-mechanical-recovery`）PASS，车辆会话 `RecoveryRequired (FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED，报过的代数 1)`。
- `G3-13-27`（`g3-unable-to-charge-field-confirmation`，cs#567 改的读法）PASS，12 次读取 0 次不是 `CONFIRMED`、0 次读不到。
- `G3-12-03`（`g3-waiting-point-idle-return`）PASS，到点收敛，界面 `AT_WAITING_POINT`。

**为什么四个 runner 都重跑**：绑定一动，四个 runner 共用的绑定就变了；只跑 journey 会让四份证据绑在不同提交上，
而且 cs#567 改的是四个 runner 共用的函数（调度改定第 2 条）。

### 证据精简

入库前定好留哪几份，并报调度同意（合计约 77 MB）：

| 证据 | 处理 | 体积 |
| --- | --- | --- |
| `evidence/g2/20261011-protocol-v3.0.0-FP-IS-10-d4f83e59/` | 整份 | 129 KB，4 个文件 |
| `evidence/g3/20261011-protocol-v3.0.0-{staged,restart,demand-bearing,journey}-37a86cbc/` | 整份，journey 也不精简 | 789 KB、505 KB、2.5 MB、72 MB，共 1639 个文件 |
| `evidence/l2/20261010-ci-38060390711-<场景>-01`～`03/` 与同名 `.log` | 每次只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`，`.log` 全留 | 18 个目录加 18 个 `.log`，约 0.6 MB |
| G3 预跑 | 不入库（不是证据） | — |

- **为什么 G3 整套入库**：G3 是本机跑的，没有 CI artifact 可以重下，删了就没有原件；批次 8 出口的正式一轮也是整套入库。
- **CI 三连删了什么**：每次运行的 `logs/`（服务端与假对端的完整日志，单份 3～10 MB）与 `snapshots/`。原件在 run `38060390711` 的
  artifact `l2-evidence` 里，可以重下（artifact 约保留 90 天）。`.log` 留着，它们很小，也是「18 次都跑了」的凭据。
- 入库前核过：未跟踪文件只有本报告与上表这些目录，没有预跑或 stage 的残留。

## 四、部署约束

将来往 factory01 部署批次 10 的包（切换生产那一步）之前，按以下顺序核对，**全部通过才部署**（Coordinator 10 10-10 要求）：

1. 按 `fp/v2-impl` 上 `scripts/parallel/README.md` 的「读库里已绑的版本」一节（cs#545 随 PR #550 于 2026-10-10 合入；
   `bf1d56d2` 上在第 225 行起），**实读** factory01 上 v2 实例库里当时绑定的 `admissionPolicyVersion`（以及装过的最高版本；
   `AdmissionPolicyState` 那一行的 `Version` 就是装过的最高版本，版本只升不降）。
2. 确认这次要装的实例定义里的 `admissionPolicyVersion` **高于装过的任何版本**（「装过的最高版本 + 1」；回滚同理，见同一 README 的
   「回滚：库不跟着回滚，版本只能往上走」）。
3. 确认实例定义与安装包来自同一提交：control-server#552（批次10-08）给部署脚本 19 加「包与克隆同一提交、克隆干净」检查；
   **它合入之前人工核对**部署用克隆的 `HEAD` 与包内 `release-manifest.json` 的 `components.controlServer.commit` 相等。
4. 每次部署把所用版本号记进部署记录。

**读到的与推断的：**

- 读到的：`fp/v2-impl@bf1d56d2` 上 `scripts/parallel/instance-factory01-v2.json:65` 与 `instance-factory01-v2.production-mes.json:60`
  写的都是 `admissionPolicyVersion: 3`（`instance-factory01-v2.two-car-example.json:82` 也是 3）。票面写的「两份定义是 2」是开票时
  `2fe490d7` 上的值，cs#545 已把它们升到 3。
- 转述的：factory01 上 v2 实例的实际绑定 10-10 18:02 实读为 2、没有漂移（cs#566 现场会话装上 `2fe490d7` 的包之后读的，
  调度 Coordinator 10 转述，出处在看板与 cs#566 的证据）。**本票没有在 factory01 上读过**，按工作区规则本票也不碰 factory01。
- 推断的：照上面两条，今天装批次 10 的包配 3 会通过第 2 步（3 > 2）。**这只是今天的推断**：之后任何重装或回滚都可能改变库里的版本，
  所以切换部署那一刻仍须按第 1 步再实读，不能按仓库里的值或这次转述推。

## 五、必须如实写明的各点

1. **现场状态：四类只在合成装置上跑通；26 号图上已建四个站，绑定尚未准备也未激活，现场不生效。**按票面三选一表，这是第二行
   「站建了、绑定没激活」（开票时是第一行，10-10 晚用户建站后变成第二行）。
   - 站号（读到的，出处是 control-server#547 2026-10-10T14:54:48Z 的评论，那是调度转录的用户回填）：`DIE_TO_OVEN` 烘箱 218、
     `WIRE_TO_OPTICAL` 三光 219、`DIE_TO_WIRE_STAGING` 装片氮气柜 220、`WIRE_TO_NITROGEN` 焊线氮气柜 221
     （另有既有的 `WIRE_TO_GATE` 关卡 210、`STAGING_TO_WIRE` 派工待送取货 217）。装片氮气柜与焊线氮气柜是同一个柜子前两个停靠位不同的站，
     各绑一类（用户 10-10 答复，见同票第一条评论）。**本票没有实读 RIoT**，站号只来自这条评论。
   - 用途核对（`REQ-0338`）：cs#547 关闭评论（14:56:40Z）写「用途核对由用户 10-10 确认」；用户原话「都是我10-10确认的」与记录号
     `MAP-26-<任务类型>-SITE-20261010` 是调度 Coordinator 批次10 转述的，本票没有在票上读到。每站的现场位置用户无法用文字描述，
     改由 control-server#548 开工时从 RIoT 只读取节点与坐标代替。cs#547 已于 2026-10-10 关闭。
   - 绑定：control-server#548（为 26 号图准备同向四类绑定、FieldOps 激活候选文件与步骤）仍开着（合入前再核一次），**还没有候选文件，也没有在任何实例上激活**。
   - 所以：上表四个站在任何现场实例上都还没有被绑到任务类型，四类在现场不生效；合成装置里的站号 401～404 与这里的 218～221 无关。
2. **四类进生产是切换之后的事。**生产来源模式（control-server#535）今天只允许 `STAGING_TO_WIRE`，factory01 上的 v2 实例
   不受理同向四类，与绑定有没有激活无关；MVP 只做 `WIRE_TO_GATE`。整体割接（停 MVP、v2 接全部任务类型）仍按原计划。
3. **六个场景全部是合成装置**：假 RIoT、假 MesIngest、合成车载端。PASS 不证明真实 RIoT 上四类的站点、路线与订单能走通，
   也不证明真实硬件（模块、接线、锁、光幕）。
4. **准入种子由两类变六类与 `allowedWorkTypes` 无关**：生产来源模式下种子照样是六类，所以带批次 10 的包装到 factory01 的 v2 实例时，
   版本必须升（第四节），否则判准入策略漂移、停接单，正在试运行的 `STAGING_TO_WIRE` 也会停。
5. **G3 是本机跑的合成对端。**四个 runner 用真车载端 WPF 加模拟器，配假 RIoT、假 MesIngest，不动车。demand-bearing 用的是生成库（`storeSource SYNTHETIC_RIG`），不是现场库。`fullG3` 与 `releaseCandidate` 两栏是 `INCONCLUSIVE`：`scripts/g3-slice-evidence.ps1:991-992` 有意写死（注释原文 `one runner covering its own slices says nothing about the gate as a whole`），批次 8 出口同样如此，不表示有红。

## 六、剩余风险

- 现场：四类没有在任何现场实例上跑过；26 号图上四个站已建（cs#547 已关），绑定未准备也未激活（cs#548）。站号与用途核对出自票上评论与调度转述，本票没有实读 RIoT。
- 部署：批次 10 的包与准入版本 3 必须一起换，错配会停接单（第四节）；cs#552 合入前同提交检查只能人工做。
- 日志：准入策略漂移日志 2116 只比站点，任务类型集合变了时写「added: none; removed: none」，排查会被误导（control-server#551，未合）。
- 测试：「途中追加只在停站时、行驶中不接」没有合成侧证据（control-server#577，未合，不挡出口）。
- **车载端 G3 所用提交的 push CI 是红的。**G3 绑定的车载端 `535c94fc`（hmi#289 的合并提交）合入 `w2g/fp-v2-impl` 后的 push CI
  run [`38040598662`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/actions/runs/38040598662) 结论 failure，红在车载端
  `ONBOARD_HMI_G2` 测试步骤（`dotnet-test-release exited with code 1`），两条失败（读到的，取自该 run 的 `g2-evidence` 证据包
  `logs/dotnet-test-release.log`）：
  - `RecoveryVectorG2Tests.AHoldForAnUnprovenDoorIsReleasedThroughTheRepairRecordAndASafeHoldReleaseCheck`：
    `Timed out after 5s waiting for: the control server to receive PreDepartureSafetyCheckResult`（等发车前安全检查结果超时 5 秒）；
  - `MultiDemandJourneyG2Tests.TheRunningDemandsUnknownResultIsShownBeforeTheQueuedCommandTakesOver`：
    `Assert.Equal() Failure: Strings differ`（字符串不等）。

  判断是 CI 下车载端 G2 夹具不稳，不是 hmi#289 引入，依据：hmi#289 只加了两个单元测试文件（没有产品代码，也没碰这两个 G2 测试类），
  它自己的 PR CI run [`38040057589`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/actions/runs/38040057589) 是绿的；
  失败分散在两个互不相关的测试类；另据调度转述（本票未实读），hmi#287 的 PR CI 在同一基点上又红在 `StationDeadlineExpiredG2Tests`
  （迟到 ack），这一族夹具在 CI 并行下本来就不稳。**这是推断，不是复现证明**：本票没有在 `535c94fc` 上重跑车载端 G2，
  本批也不要求车载端门禁。是否开稳定性票由调度与分票王定。本批 G3 四个 runner 在这个车载端提交上全 PASS（第三节），
  但 G3 走的是真车载端界面加模拟器，不覆盖这两个 G2 测试走的路径。


## 七、转后续

- 车载端 `ONBOARD_HMI_G2` 在 CI 下的两条失败（第六节）是否开稳定性票，调度已转分票王定。
- `g3-unable-to-charge-field-confirmation` 的 G3-13-27 描述文字仍写「已知红，等 onboard-hmi#242」，但它在预跑与正式一轮都 PASS，描述看起来过时了；预跑时报给调度，已转分票王。本票不改 G3 脚本。
- control-server#548（26 号图同向四类绑定）、#551（漂移日志）、#552（部署同提交检查）、#577（行驶中拒绝追加的合成证据）照常推进，不挡本出口。
