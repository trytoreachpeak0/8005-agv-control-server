# control-server#909 证据

多需求停靠上扫码前取消一条：去掉它的新一版清单（等录入时连同同号录入请求）随取消落定那一次保存暂存、应答之后立即发出，较早各版清单与录入请求同一次保存退役。修之前要等引擎下一轮由 #723 那一段补，而那一轮的轮首补发先把仍期待被取消子批的同版录入请求再发一遍。

## L1 修前红

`red-before-fix.txt`：测试提交 `a42c6e559`（只有测试、没有修复）上跑 `CancelBeforeScanRefreshesWorklistTests`，新加的 4 条（两个用例 × 待送点开关开/关）全红，都红在「取消那一次处理里没有新增清单行」（`Assert.Single() Failure: The collection was empty`），其余 12 条绿。

## 变异取红

`mutations/`：每个变异只改一个文件的一处文本（`run-mutations.sh` 断言替换恰好一处、`git status` 恰好一个文件），`dotnet build --no-incremental` 后跑 `CancelBeforeScanRefreshesWorklistTests`，跑完只还原那一个文件。结果见 `summary.txt`，七个全红：

| 变异 | 改了什么 | 红的用例 |
| --- | --- | --- |
| M1-no-stage | 协调器不调 `RemainingStopWorklist.StageAsync`（等于修前） | 新加 4 条 |
| M2-no-entry-fence | 不退役较早各版录入请求 | 新加的等录入用例（同版重发那一句）、#723 的等录入与重连用例 |
| M3-no-entry-request | 等录入时也不暂存录入请求（运行期恒假条件；常量 `false` 那一次被 CS0162 挡在编译，已记在 `summary.txt`） | 新加的等录入用例 ×2 |
| M4-entry-while-loading | 在装时也暂存录入请求 | 新加与 #723 的在装用例 |
| M5-no-send | 保存之后不发（只暂存） | 新加 4 条（线上没有那两行） |
| M6-deadline-dropped | 新一版不带车上那一版的期限 | 新加等录入用例、#723 的期限用例 |
| M7-no-worklist-fence | 不退役较早各版清单 | 10 条 |

## L2

场景 `multi-demand-stop-cancel-one-refreshes-at-once`（合成）。判据不靠计时：新一版清单在发件箱里的 `CreatedAt` 必须等于乙的业务键抑制 `SuppressedAt`——两者由取消落定那一次处理用同一个时刻暂存、同一次保存落库。

- `l2/green/`：修复后 4/4 PASS，跑在 `3983fb6e3`（已合入 `fp/v2-impl` 含 PR #908 的基线）。
- `l2/red-M1-no-stage/`：去掉取消时的暂存（`mutations/L2-M1-no-stage.diff`），只有 L2-MDR-02 红：新一版 `06:17:35.997`，取消 `06:17:35.185`，由引擎下一轮写。L2-MDR-03（没有还会补发的录入请求期待乙）修前也绿：合成装置的引擎一轮不到一秒，读的时候旧版已被下一轮退役。同版重发这一点由 L1 守（M2）。

两次都是本机完整运行（含构建）；日志与快照目录已删，只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`。
