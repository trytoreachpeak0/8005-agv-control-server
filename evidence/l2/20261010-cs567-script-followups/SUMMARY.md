# cs#567 证据：出口脚本跟进五项（加 G3-13-27 读法）

control-server#567。只改 `scripts/` 与 `.github/workflows/test.yml`。

## 目录

| 目录 | 内容 |
| --- | --- |
| `red/` | 测试提交上的自检失败输出（修前红）：`uia-on-test-commit-ea2720f5.txt`（A6、C2 红）、`honesty-on-test-commit-ea2720f5.txt`（harness 两条红）、`uia-d-group-before-fix.txt`（D2、D3 红）、`scope-on-stub.txt`（选择器桩上全红）；审查变异 X10 原样：`x10.diff`，旧自检下 `uia-x10-old-selfcheck.txt` 为 exit 0（漏掉），新自检下 `uia-x10-with-b8.txt` 为 exit 1（B8 红） |
| `green/` | 实现之后的自检输出，全部 exit 0 |
| `mutations/` | M1～M15 每个变异的 diff 与自检输出，全部被预期的那条检查杀死 |
| `rig/` | 本机真装置两遍（RealOnboard，cs `86da18af`／hmi `535c94fc`／sim `fb5f7c59`），只留 `assertions.json`、时间线与车载端应用日志：`g3-automatic-charging-cycle` PASS（G3-13-01～07），`g3-unable-to-charge-field-confirmation` PASS（G3-13-21～27） |
| `content-conflict-measure/` | `Test-StagedG3ContentConflict.ps1 -Expect Pass` 对 `0c2c27eb` 本机实测：197 秒，四项判定 PASS |

## 变异一览

| 编号 | 改法 | 杀死它的检查 |
| --- | --- | --- |
| M1 | G3-12-03 条件去掉 `$heldReading.Readable`（X10 前半） | B8 |
| M2 | 不可读时 `$heldAtPoint` 取 `$atPoint`（X10 后半） | B8 |
| M3 | 充电窗口 `Readable` 恒为 `$true` | C2 |
| M4 | 充电窗口改为读不到也结束 | C3 |
| M5 | G3-13-03 加判 `Status` | C3 |
| M6 | 子批拒收行取 `sublot` 当拒绝码 | A6 |
| M7 | 子批拒收行匹配不到 `reason=` | A6 |
| M8 | harness 退回 `git status --porcelain` 行数 | harness：只有 `evidence/` 未跟踪文件 |
| M9 | 豁免一切未跟踪文件 | harness 与 provenance 的「工作树其它地方」诸条 |
| M10 | 豁免前缀去掉斜杠（`?? evidence`） | harness 与 provenance 的 `evidence-copy.ps1` 条 |
| M11 | harness 恒为干净 | harness 三条脏用例 |
| M12 | 选择器路径比较不分大小写 | 选择器：近似路径应跳过 |
| M13 | 前缀去掉斜杠（`scripts/l2`） | 选择器：近似路径应跳过 |
| M14 | 去掉 `src/ControlServer.Host/Transport/` | 选择器：Transport 应触发 |
| M15 | 非 PR 事件跳过 | 选择器：非 PR 应跑 |
| R2（审查） | G3-13-27 把空串也算成不可读 | D3（期望串加 `0 unreadable` 之后；之前的期望下 exit 0，`mutations/R2-under-previous-expectation.txt`） |

## 审查轮（PR #574 独立审查 B1～B6）

- B3 的三处路径先进选择器自检，修前红：`red/scope-review-b3-before-fix.txt`；修后 `green/scope.txt`。
- B1、B2 在本机核过：按 SHA `fetch --depth=2` 能取到父提交；把文件移出 `scripts/l2/` 时，默认 diff 只列新路径，`--no-renames` 才列出旧路径。
