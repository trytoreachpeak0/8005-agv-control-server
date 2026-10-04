# control-server#460：G3 证据不得多报——离线自检与变异取红

**这不是 G3 运行证据。**本目录只有离线自检 `scripts/Test-G3EvidenceHonesty.ps1` 的输出，以及十一个变异各自的红。没有跑真装置，也没有跑任何 G3 runner 的完整一轮；共享绑定（`run-staged-g3.ps1` 的 param 默认值）没有动。

## 改了什么

1. **自检覆盖不再算正式通过（票面 1–3）。**`scripts/g3-slice-evidence.ps1` 新增 `Get-G3FormalSliceWithheldReason` 与 `Get-G3FormalSlicePass`，`New-G3Classification` 和 `Write-G3GateResult` 都只经这一处定 `formalSlicePass`。判据读运行的 commits 记录里每个 `*CommitSource`：只要有一个是 `SELF_CHECK_OVERRIDE`，所有切片 `formalSlicePass = false`、`formalSliceWithheldReason = SELF_CHECK_OVERRIDE`；`status` 与断言照实记录。只有 `SHARED_BINDING` 放行，不认识的取值也扣下（`UNRECOGNISED_COMMIT_SOURCE: …`），免得以后新加一种来源时默认放行。按独立审查 S3，缺来源也扣下：记录里没有 `controlServerCommitSource`（空记录、忘了记、甚至不是记录）一律 `COMMIT_SOURCE_MISSING: controlServerCommitSource`；几种原因同时成立时全部写下，用 `; ` 连接，`SELF_CHECK_OVERRIDE` 在前。`run-staged-g3.ps1` 的四个绑定就是它的 param 默认值，命令行传 `-ControlServerCommit` 等就能换提交，以前不留痕迹（调度要求一并补上）：现在它用其余三个 runner 读绑定的同一个 `Get-SharedCommitBinding` 读回自己的默认值，经 `Get-G3CommitSources` 与实参逐字比较，四个提交各记一个 `*CommitSource`，不等即 `SELF_CHECK_OVERRIDE`；只记录，不拒绝覆盖。restart runner 没有提交参数，四个来源恒为 `SHARED_BINDING`，照同样形状记录。绑定的值和存放位置都没动（param 默认值四行与基线逐字一致）。四个 runner 都改成把同一个 `$commitsRecord` 交给 gate result、classification 和 `run-result.json`；`New-G3Classification` 的 `-Commits` 是必填参数。gate-result `schemaVersion` 由 `1.3.0` 升到 `1.4.0`（只加了一个字段）。
2. **需求承载 runner 中途失败时先写真实原因（审查补充第 4 项）。**try 之后的第一句就是 `Write-StagedRunError`（从 `run-staged-g3.ps1` 按 AST 取来，不复制，新加 `-RunLabel`），写 `runner-error.json` 并打到控制台，然后才轮到 `Write-G3GateResults` 的二次拒绝。生成器失败时，消息里带上 L2 自己 `assertions.json` 的 `outcome` 与 `failureReason`；任何子命令非零退出时，消息里带日志最后 15 行。
3. **FIELD_RUN 拒绝合成库（审查补充第 5 项）。**`$baseline = Read-ControlDatabase` 之后紧跟 `Assert-FieldRunStoreIsNotGenerated`：`-FieldRunRoot` 给的库里只要出现 `TransportDemandKey` 以 `L2-SUBLOT-` 开头、`VehicleKey` 为 `BROKERX-L2-*`、`AgvId` 为 `AGV-L2-*` 中的任一项，就以 `FIELD_RUN_STORE_IS_GENERATED` 拒绝，在服务端启动之前。选的是按内容认，而不是让生成器留标记：标记只会出现在以后生成的库里，而 #461 对照组那种已有的合成库只能按内容认出来。

## 自检结果

`selfcheck-pass.txt`：168 项全部 PASS，`G3EvidenceHonesty self-check: every check as expected.`，约 10 秒。其中：

- 四种 run kind × 十种 commits 记录，各查 classification（整体与每片）和 gate-result.json。只有 `SHARED_BINDING` 那种必须是 `formalSlicePass true`，这钉住了「断言全过」这个前提，否则其余的 false 可能只是没过；缺 `controlServerCommitSource` 的四种（无来源、只有车载端来源、空记录、裸字符串）、覆盖加未知取值、三种覆盖或未知取值，都必须 false 且原因逐字对上。
- staged runner 自己的来源：取它的 `$commitSources` 语句实跑，默认值时四个 `SHARED_BINDING`，四个提交逐一换成别的值时对应那一项变 `SELF_CHECK_OVERRIDE`、PASS 切片被扣下；大写拼写的绑定值也不算绑定。
- journey 与需求承载 runner 自己的来源（审查 S1）：从 AST 取出各自的 `*CommitSource` 赋值、`-SelfCheck*` 分支和 `$commitsRecord` 字面量，按原顺序实跑；不带覆盖时记录里全是 `SHARED_BINDING`，逐个带上覆盖参数时，记录里对应那一项是 `SELF_CHECK_OVERRIDE`，PASS 切片被扣下。记录里把变量换成常量就会变红。
- 四个 runner 的接线：`-Commits $commitsRecord`、`commits = $commitsRecord`，以及四个 runner 的记录里确实带着各自的 `*CommitSource`。
- 用临时 git 仓造一次生成器失败（`outcome FAIL` + 唯一的 `failureReason`，exit 1），实跑真的需求承载 runner：控制台和 `runner-error.json` 都读得到那个原因；同时确认挡住这一轮的仍是 `fieldStoreProvenance with no protocolCommit`，也就是审查看到的那条二次报错。
- FIELD_RUN 守卫：#461 对照组的合成库（行取自 `evidence/g3/20261003-cs453-demand-bearing-synthetic-b660f80e/field-path-control`）被拒、三个标记都点名；三个标记各自单独也能拒；2026-08-29 的外场库（含与不含 claim 记录两种形状）放行；合成库走 `SYNTHETIC_RIG` 放行。

## 变异取红

`mutations/mutate.ps1` 把 `scripts/` 复制到临时目录，改一处，再跑自检。十一个变异全红（M8、M9 就是独立审查的 R1、R2，审查当时用的自检对它们全绿）：

| 变异 | 改了什么 | 红了几项 |
| --- | --- | --- |
| M1 | `Get-G3FormalSlicePass` 去掉 `-and ($null -eq $withheld)`，即 #460 之前的行为 | 103 |
| M2 | journey 的 `$commitsRecord` 去掉 `onboardCommitSource` | 5 |
| M3 | 去掉需求承载 runner 里的 `Write-StagedRunError` 调用 | 3 |
| M4 | 去掉 `Assert-FieldRunStoreIsNotGenerated` 调用 | 1 |
| M5 | 守卫函数一进来就 `return` | 4 |
| M6 | 去掉 `BROKERX-L2-*` 这个标记 | 2 |
| M7 | staged runner 的来源比较换成四个常量 `SHARED_BINDING` | 8 |
| M8 | 需求承载 runner 记录里的 `controlServerCommitSource` 写死成 `'SHARED_BINDING'`（审查 R1） | 2 |
| M9 | journey 记录里的 `onboardCommitSource` 写死成 `'SHARED_BINDING'`（审查 R2） | 2 |
| M10 | 去掉「缺 `controlServerCommitSource` 即扣下」 | 48 |
| M11 | 多个原因只留第一个 | 12 |

M3 的输出也是本票第 4 项的「修之前」：控制台只剩 `g3-slice-evidence.ps1:821` 的 `The gate result for … carries a fieldStoreProvenance with no protocolCommit`，生成器自己的原因不见了，`runner-error.json` 也没有写出来。
