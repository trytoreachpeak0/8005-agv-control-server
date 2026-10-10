# control-server#342 断线重连基于模型随机测试：量测与判别力证据

全部是本机（控制端笔记本）跑出来的 L1 输出，没有真装置。完整的解读在 PR 正文里，这里只说明每个文件是什么。

## 模型的几个版本

- **原型**（`prototype/`，提交 `75cfd407`）：握手直接写库，8 种动作，只判「录入请求到车上或看板有码、不每轮抛同一异常」。
- **最终**（`final/`，提交 `9966beca`，即按第二轮审查改完之后）：握手经真实 `OnboardMessageProcessor`，加了安全变化（三种送达方式）、RIoT 单挂起与继续、同 messageId 不同内容的重放；每个组合随机选真车载端或合成车载端（真车载端到站前报不出安全）；不变量四条全开，另有「恢复健康之后至多 2 轮录入请求到车上」。动作多了，同样的种子生成的序列与原型不同，两版的种子不能互相对照。

前两版最终模型的输出在提交 `241895b1`（首轮审查前）与 `2077b198`（首轮审查后，`506b6f11`）里，已被本目录替换：每轮审查改的都是判据与状态，旧数字不再对应现在的模型。下面标了 `506b6f11` 的几份是那一轮留下、结论仍然成立的。

## 在哪个提交上跑

| 文件名里的提交 | 是什么 |
| --- | --- |
| `af01fd27` | cs#331 修复前 |
| `62d5c560` | cs#331 第一版修复（PR #338 的第一个实现提交） |
| `18172346` | cs#331 合入（PR #338 合入提交），cs#340 修复前 |
| `9966beca`、`38318ef9` | 本分支，含 `384b9b69`（cs#340 合入）。`38318ef9` 只比 `9966beca` 多改了注释 |
| `63b397e9` | 本分支 merge 了 `fp/v2-impl` 顶端 `b6be32d5`（cs#318，改了引擎：自建单取消／故障清除后重建）之后。模型的等人码表补上 cs#318 加的十个码 |
| `506b6f11` | 本分支上一轮 |
| `cb44fb60` | PR #343 头，产品代码与本分支相同（`git diff cb44fb60 HEAD -- src` 为空）。只用来做变异，每个变异跑完都 `git checkout` 还原、`src` 零改动 |

测试文件（两个模型文件、回归用例、`Directory.Packages.props`、测试 csproj、`packages.lock.json`）拷进各提交的分离 worktree 再构建；`repos/` 克隆没有动。跑法全部在 `final/r3-runs.sh`（本机路径换成了必填的环境变量 `S`、`W`），每个变异的原文与替换文也在里面，替换不是恰好匹配 1 处就退出。

## 文件

- `*/measure-<提交>.txt`：`ReconnectModelTests.PrototypeMeasurement` 的报告。固定种子 300 个组合：每个组合的耗时；每一类违规的次数、种子与序列（一个组合里出现的每一类都计数）；等人码出事的机会按码分开数；恢复健康到录入请求用了几轮的分布；`DifferentMessageAccepted` 在握手中与会话中途各几次；第一个违规组合的逐步记录；CsCheck 化简与确定性删减的结果。
- `final/regression-<提交>.log`：`ReconnectModelRegressionTests` 在各提交上的输出，修前红、修后绿。
- `final/measure-rounds-to-entry-constant-1-probe.txt`：`RoundsFromRecoveryToEntry` 定值之前的一次测量，常量还是 1（提交 `9966beca` 之前的工作树，模型判定与 `9966beca` 相同，只差这个常量与后来加的按码计数）。报 `EntryLateAfterRecovery` 的恰好是分布里「2 轮」的那 8 个，这就是这条新不变量的反向验证；读完 cs#331 那条用例之后定成 2。
- `final/measure-63b397e9.txt`、`final/regression-63b397e9.log`：merge cs#318 之后在同样 300 个种子上重跑。违规的种子与序列和 `9966beca` 完全相同（hmi#206 129 个，别的类 0 个），恢复健康到录入请求的轮数分布也相同；回归用例全绿。cs#318 没有改变这个模型看得见的任何结果。
- `final/wait-on-person-sync-reverse-on-63b397e9.log`：新护栏 `TheModelsWaitOnPersonCodesAreExactlyTheProductsSet` 的反向验证。模型的等人码表里删掉 `OWN_ORDER_REBUILD_STOPPED`、加一个假码，报出缺了哪个、多了哪个；跑完已还原。
- `final/ci-batch-test-local-38318ef9.log`：CI 那一批用例（200 个组合）在本机单独跑一次的耗时。
- `final/*mutant-guard-on-cb44fb60*`：去掉 `NameFailedAdvanceAsync` 里的 `CarriesACodeThatNamesAWaitOnAPerson` 守卫（cs#331 第二轮审查补的「推进失败不覆盖等人码」）。
- `final/*mutant-silent*-on-cb44fb60*`：失联码那一格的反向验证（第二轮审查必修 M2）。`silent` 是把「失联判定让这一轮返回」改成判完照样往下发；`silent-guard` 再加上去掉守卫；`new-model` / `old-model` 是模型里失联码豁免有没有存活窗口上限。`full-trace` 与 `with-ack` 两份是逐步记录，说明失联码在这个变异下是在同一轮之内被覆盖的。
- `final/*mutant-rebind-on-cb44fb60*`：握手补发的等价哈希（`GenerationRebindReplayHash`）改成不看载荷（第二轮审查建议 3）。
- `final/*mutant-replay-guard-on-cb44fb60*`：`WireToGateStore.CaptureFirstResponseAsync` 的等价判断整个去掉（与上一轮同一个变异，在本轮模型上重跑）。
- `prototype/calibration-*.txt`：四串手写序列（后来成了回归用例）在三个提交上的判定。`calibration-62d5c560-deadline-off-probe.txt` 是把模型的离站等待期限临时关掉之后在 `62d5c560` 上的结果，跑完已还原。
- `prototype/replay-af01fd27-stuck-seeds-on-18172346.txt`：`af01fd27` 上卡在取货站的 17 个种子，在 `18172346` 上逐个复跑。
- `final/regression-unskipped-hmi206-on-506b6f11.log`：把 onboard-hmi#206 那条的 Skip 临时去掉，在本分支上一轮跑出的红（两行都报 `LegitimateMessageRefused`），跑完已还原。
- `final/known-defect-guard-fake-row-on-506b6f11.log`：已知缺陷表零命中护栏的反向验证。临时往表里加一行永不命中的假条目，CI 那批变红，报的正是「这一行零命中、缺陷可能已修」。跑完已还原。本轮把零命中改成按行判，由单元用例 `AKnownDefectRowThatNothingHitsFailsTheRun` 新加的一条断言覆盖（同票的另一行命中，这一行照样判死）。
- `final/replay-entry-never-reached-on-*.txt`：`af01fd27`、`62d5c560`、`18172346` 上各有 1 个「录入请求没到车上」，三处删减到同一串 9 步的序列。这两份是它在 `18172346` 与 `506b6f11` 上的逐步记录：修前每次重连的握手都在补发那一条多回的一行上断开（cs#340），每次只消掉日志里一条补发，而这一串攒了 8 条，收尾给的重连次数用完了；修后同一串走到录入请求。所以归到 cs#340，不是新缺陷。本轮模型把这一类的说明分成「预算用完」与「恢复之后停住」。`measure-*` 里的说明还是分之前的措辞（写成 `healthy since tail round 8, 0 round(s) since`，其实是最后一次重连恰好在收尾最后一轮成功、一轮都没剩下）；分开之后的措辞见 `replay-entry-never-reached-budget-wording-on-18172346.txt`：`budget exhausted: healthy again only 0 round(s) before the end, 8 reconnect(s) used of 8 tail rounds`。改的只是说明文字，判定与计数不变。
