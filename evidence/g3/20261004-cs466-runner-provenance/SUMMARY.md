# control-server#466：正式通过要求跑的是已提交的 runner 和共享绑定——离线自检与变异取红

**这不是 G3 运行证据。**本目录只有离线自检的输出、修复前的复现、以及十六个变异各自的红。没有跑真装置，也没有完整跑过任何一个 G3 runner。共享绑定（`run-staged-g3.ps1` 的四个提交默认值与 `OnboardRemoteRef`）一个字节都没有改：基线与本分支按 AST 逐字比较，结果 `identical: True`。

## 修复前（`before/`）

`before/before.ps1` 取 base（`origin/fp/v2-impl` = `0788a7fd`）的脚本，按 `Test-G3EvidenceHonesty.ps1` 的办法实跑 runner 自己的语句，再交给 base 的 `Get-G3FormalSlicePass` 判定一个 PASS 切片。输出在 `before/before.txt`，三种情况都是 `{"formalSlicePass":true,"formalSliceWithheldReason":null}`：

1. 把 `run-staged-g3.ps1` 的 `ControlServerCommit` 默认值本地改成 `1111…`，不传参：实际用的是 `1111…`，四个来源仍是 `SHARED_BINDING`。
2. `run-journey-g3.ps1 -SharedRunnerSource` 指向一份 `OnboardCommit` 默认值是 `2222…` 的副本：实际用的车载端是 `2222…`，来源仍是 `SHARED_BINDING`。
3. 记录里 `runnerWorktreeCleanAtStart: false`（09-22 正式证据就是这个形状）：判定根本不读这个字段。

## 改了什么

- **绑定改从 runner 仓的 `HEAD` 读。**`g3-slice-evidence.ps1` 新增 `Get-G3RunnerProvenance`，在脚本自己所在的仓里测量。这个仓由 `$PSScriptRoot` 决定，不能通过参数指定。它用 `git cat-file blob HEAD:<相对路径>/run-staged-g3.ps1` 读回已提交的绑定，规则与 `Get-SharedCommitBinding` 相同。四个 runner 都改成把本次实际用的四个提交与这份绑定比较（`Get-G3CommitSources`），对不上就记 `SELF_CHECK_OVERRIDE`。restart runner 原来把四个来源写死为 `SHARED_BINDING`，现在改成真比；journey 与需求承载 runner 的记录也补齐了 simulator、protocol 两个来源。
- **新增来源字段 `runnerSource`。**只有 `COMMITTED_RUNNER` 放行，其余都扣下并写进 `formalSliceWithheldReason`：
  - `RUNNER_WORKTREE_DIRTY`：runner 所在仓有改动或未跟踪文件，或者有文件被标了 assume-unchanged／skip-worktree。这两种标记会让 `git status` 看不见改动，所以另外用 `git ls-files -v` 查。
  - `RUNNER_INPUT_OVERRIDE: <参数名>`：`-SharedRunnerSource`、`-CommitBindingFunctionSource`、`-ControlServerRepository` 中有参数不是默认值。只靠比较绑定值，看不出「默认值不变、只改了 harness」的副本，这一条负责挡住它。
  - `RUNNER_PROVENANCE_UNKNOWN: <原因>`：不在 git 仓里、读不到 `HEAD`、status 失败，或者从 `HEAD` 读不出绑定。
  - 记录里没有这个字段时记 `RUNNER_SOURCE_MISSING`；出现不认识的取值时记 `UNRECOGNISED_RUNNER_SOURCE: …`。
- **只记录，不拒跑。**工作树不干净、用副本、用 `-SelfCheck*` 都照常能跑，只是不算正式通过。出口票的做法是改默认值、commit、再跑：commit 之后 `HEAD` 与磁盘一致，工作树干净，照常拿到正式通过。
- **测量在写任何东西之前。**每个 runner 的 `$runnerProvenance` 语句都排在第一个碰到 `$StageRoot`／`$EvidenceRoot` 的顶层语句之前，自检会钉住这个顺序（见 M16）。
- `runnerWorktreeCleanAtStart` 改为测 runner 所在仓；staged runner 的 `harness*` 两个字段保留原含义，另外加了 `runner`、`runnerWorktreeCleanAtStart`、`runnerSource`。

## 自检结果

- `selfcheck-pass.txt`：`Test-G3EvidenceHonesty.ps1` 共 348 项，全部 PASS（cs#460 时是 168 项）。新增的部分包括：
  - 九种新的 commits 记录 × 四种 run kind，各查 classification 与 gate-result.json；
  - 每个 runner 从 AST 查三件事：恰好一条 `$runnerProvenance` 语句、`-ScriptRoot $PSScriptRoot`、排在写入之前；所有默认值由 `$PSScriptRoot` 推出的参数都是 `-Inputs` 条目，默认值与 param 块逐字一致；记录里的 `runnerSource` 取自测量结果；
  - 四个 runner 的来源语句在「`HEAD` 绑定的提交与磁盘或副本不同」时实跑，对应来源必须是 `SELF_CHECK_OVERRIDE`；
  - 用临时 git 仓实测 `Get-G3RunnerProvenance`：已提交且干净时通过；本地改默认值、同样的改动藏在 assume-unchanged 后面、藏在 skip-worktree 后面、有未跟踪文件、`-SharedRunnerSource` 指向两种副本、`HEAD` 里没有绑定文件、不在任何仓里，全部扣下。
- `g3-selfchecks.txt`：`Test-G3RunnerClaims`、`Test-G3EvidenceLocation`、`Test-StagedG3ErrorPath` 全过。
- `l2-selfchecks.txt`：`scripts/l2/Test-L2*` 共 22 个全过。`Test-L2SafetyDurableWait` 不带参数运行时会报缺少必填的 `-EvidenceRoot`，带上之后 `All checks passed.`。

## 变异取红（`mutations/`）

`mutations/mutate.ps1` 把 `scripts/` 和切片索引复制到临时目录，**在那里 `git init` 并提交**（runner 现在要从自己所在的仓读身份），改一处，再跑自检。M0 不改任何东西，全绿；其余十六个全红：

| 变异 | 改了什么 | 红了几项 |
| --- | --- | --- |
| M0 | 不改（基线） | 0 |
| M1 | 工作树永远算干净 | 6 |
| M2 | 不查 assume-unchanged／skip-worktree | 2 |
| M3 | 路径参数覆盖不记 | 4 |
| M4 | staged runner 从磁盘读绑定（本地改默认值） | 8 |
| M5 | journey 用 `-SharedRunnerSource` 读出的绑定作比较基准 | 8 |
| M6 | 需求承载 runner 同上 | 8 |
| M7 | restart runner 从磁盘读绑定 | 4 |
| M8 | 需求承载 runner 的 `-SharedRunnerSource` 不进 `-Inputs`（指向副本） | 2 |
| M9 | journey 的 `-CommitBindingFunctionSource` 不进 `-Inputs` | 2 |
| M10 | 不判 `runnerSource` | 36 |
| M11 | `runnerSource` 不是 `COMMITTED_RUNNER` 也放行 | 104 |
| M12 | 不认识的 `runnerSource` 放行 | 36 |
| M13 | 读不出来源时不记原因 | 2 |
| M14 | staged runner 记录里的 `runnerSource` 写死成 `'COMMITTED_RUNNER'` | 1 |
| M15 | journey 改为测 `-ControlServerRepository` | 1 |
| M16 | restart runner 改为在建好 `EvidenceRoot` 之后才测量 | 1 |

票面要求的三种情况都覆盖到了：本地改默认值（M1、M4、M7），指向另一份 runner（M3、M5、M6、M8），runner 工作树不干净（M1、M2）。
