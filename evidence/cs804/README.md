# control-server#804 证据（提速-07）

本机：7945HX 32 线程、63 GB，系统区域 en-US。合成 L2 两轮都是本地 4 路（与 CI 的 `L2_LANES` 相同），`default` 模式 107 次场景。

## 1. FieldOps `session`（单元，`unit/`）

| 文件 | 内容 |
| --- | --- |
| `red-bd68b258.txt` | 测试提交 `bd68b258`（加上 `30ce7c90` 的夹具修正）上跑：4 条里 3 条红，都是 `The session ended before answering`。第 4 条 `ASessionGivenOptionsOfItsOwnRefusesToStart` 在旧版上也是 2，没有实现也过。 |
| `green.txt` | `45d69f81` 上 `FieldOpsSessionTests`、`WaitingPointFieldOpsTests`、`ChargingFieldOpsTests`、全部 `ArchitectureTests`：106/106。 |
| `mutation-no-clear-pool.txt` | 去掉会话里每条命令后的 `SqliteConnection.ClearAllPools()`：只有 `BetweenCommandsTheSessionHoldsNoHandleOnTheDatabaseFile` 红（文件被占用，移动失败）。 |
| `session-died-check.txt` | `L2FieldOpsSession.psm1` 离线：会话进程被杀后再发命令，抛错点名那条命令（`seed-approved-facts`）。 |

## 2. 编排脚本一侧的失败（`red-setup-injected/`）

在改后提交上把搭环境的 `bind-io` 改成不带 `--agv`，单跑 `normal-load`：搭环境中止，`Run failed: ControlServer.FieldOps bind-io exited with 2: ControlServer.FieldOps: bind-io needs --agv <agvId>.`，场景 FAIL；之后本机没有残留的 `ControlServer.FieldOps` 进程。

（先试过把 `--agv` 换成车队外的车号：`bind-io` 照样答 OK，场景在后面派车时超时，不能当证据，没留。）

## 3. 前后对照（`l2-compare/`）

- 改前：基线 `1f31a4a4`，全量构建。改后：`45d69f81`，只用 `l2.yml` 新写法构建的 14 个项目（无 `tests/`），工作树不带 `evidence/`（非 cone 稀疏检出）。
- 两边都打了只在本地用的导出补丁（`tools/Add-PreseedDump.ps1`）：`Environment is up` 之后把库里每张表导成 `snapshots/cs804-preseed-db.json`。
- **逐场景结论一致**：`RESULT-before-1f31a4a4.txt` 与 `RESULT-after-45d69f81.txt` 都是 106 PASS、1 FAIL，且是同一个场景。
- 那个 FAIL 是本机环境：`task-type-binding-station-reused-refuses-start` 的 L2-TTSR-04 要在服务端日志里读到站名「关卡」，en-US（代码页 437/1252）下服务端日志把它写成 `??`。CI run 39264072063（vm01，zh-CN）里同一场景 PASS。见 `env-red-...assertions.json`。
- **预置一致**（`preseed-compare.txt`，`tools/Compare-PreseedDumps.ps1`）：107/107 场景的非运行时表逐行一致（GUID、时间、刻度、内容摘要、stage 路径先归一）。只有 `ProtocolInbox`（车载替身发来的报文，导出那一刻收到几条取决于时序）在 105 个场景里条数不同，单列，不算预置。比对脚本自检：改一份导出里一个 `SlotPosition`，报出那一行。
- **耗时**（`timing-compare.md`、`setup-steps-compare.txt`）：

| | 改前中位 | 改后中位 | 改前 p90 | 改后 p90 | 改前合计 | 改后合计 |
| --- | --- | --- | --- | --- | --- | --- |
| 搭环境（到 `Environment is up`） | 14.3 s | 10.6 s | 21.2 s | 14.1 s | 1631 s | 1178 s |
| 其中 FieldOps | 7.0 s | 1.9 s | 10.7 s | 2.6 s | 793 s | 207 s |

  一轮墙钟 1496 s → 1387 s。按步骤拆：FieldOps 相关四段合计少约 575 s；变多的只有 Host 的包容量导入（+57 s）与服务端启动（+30 s），本票没碰，改后那轮开跑时另有一个 cs#803 审查探针在本机跑（`l2-after.heavy-at-start.txt`）。
- CI 上的基数（`ci-run-39264072063-setup.txt`，vm01）：搭环境中位 22.7 s、FieldOps 中位 11.5 s。本机快约一倍，比例相近：FieldOps 本机省掉约 73%（793 → 207 s）。

## 4. L2 构建不编 Tests（`build-filter/`）

`tools/Test-L2BuildFilter.ps1` 照抄 `l2.yml` 构建步骤的那几行，在干净的稀疏 worktree 上跑：14 个项目、10 s、0 警告 0 错误，构建日志里 `ControlServer.Tests` 出现 0 次，`tests/ControlServer.Tests/bin` 不存在；L2 用到的 8 个可执行文件都在。上面的「改后」一轮就跑在这份输出上。

## 5. 检出排除 evidence/：核法

- `git grep` 了 `l2.yml` 两个作业、`release.yml` 直接或间接执行的每个脚本（`Invoke-L2Scenario.ps1` 与它导入的 `scripts/l2/*.psm1`、`scripts/l2/scenarios/*.ps1`、`Test-L2ReadSnapshot.ps1`、`Select-L2ScenarioRun.ps1`、`New-WireToGateReleaseCandidate.ps1`、`Publish-ControlServer.ps1`）以及所有 csproj/props/targets：`evidence` 只出现在注释与 `-EvidenceRoot` 参数名里，没有一处读克隆里的 `evidence/`。发布脚本读的是 `src/`、`docs/RELEASE-CANDIDATE.md`、`scripts/` 与发布产物。
- 这些作业对 git 只做 `rev-parse HEAD`、`status --porcelain`、提交之间的 `diff --name-only`（不做改名检测）：稀疏检出排除的文件不算改动，按名字比较不读 blob。
- 实跑：改后那一轮 L2 就在不带 `evidence/` 的 worktree 里跑；真装置一轮走 CI（见 PR）。
- actions/checkout v7 的 `src/git-source-provider.ts:182-186`：给了 `sparse-checkout` 就用 `--filter=blob:none` 取；`:253-258`：之后同目录的非稀疏检出会先 `sparse-checkout disable`。所以真装置作业的两个对端（车载端、模拟器）不做稀疏：`Get-L2PeerPublish` 要从检出目录 `git clone`，部分克隆缺的 blob 会让那次克隆回 GitHub 取。

## 6. 独立审查之后（`20da6b47`）

- **S1**：场景侧 `$Context.InvokeFieldOps` 改走 `Invoke-L2FieldOpsProcess`，stdout/stderr 按 UTF-8 读，不改控制台代码页。`tools/Test-FieldOpsDecoding.ps1` 在子进程里把控制台设成指定代码页，同一条输出含「等待点」（9 个 UTF-8 字节、奇数）的 `import-waiting-points --dry-run`：
  - 936：旧写法 `PARSE FAILED: ... unexpected character ... Path 'changes[0].after.stationName'`；新写法 `U+7B49 U+5F85 U+70B9`（`decoding/cp936-*.txt`）。
  - 437：旧写法解析成 9 个乱码字符；新写法同上（`decoding/cp437-*.txt`）。
  - 受影响的 30 个合成场景（`scripts/l2/scenarios` 里用到 `InvokeFieldOps` 的，加上点源 `SameDirectionJourneyCommon.ps1` 的；`g3-*`、`real-onboard-*` 不在合成清单）重跑：30/30 PASS，环境就绪后场景侧 FieldOps 调用合计 65 次（`l2-compare/RESULT-s1-rerun-20da6b47.txt`）。
- **S3**：`ForTheSameDatabaseStateTheSessionPrintsWhatItsOwnProcessPrints` 把 8 条命令（成功、被拒、用法错误、库不存在）在会话里的输出与单独进程的 stdout+stderr 逐行比，退出码一并比。去掉会话里的 `Console.SetError(Console.Out)`：这条与另两条红（`unit/mutation-no-stderr-merge.txt`）；`unit/green-20da6b47.txt` 86/86。
- **S5**：改后那轮同一个环境红场景的断言：`l2-compare/env-red-task-type-binding-station-reused-refuses-start-after.assertions.json`。
