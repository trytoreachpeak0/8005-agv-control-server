# cs#428：整体返回数组的读法，改动前后逐处实测

`Invoke-L2Query` 以 `return , $rows` 交回整张结果表。调用方写成 `@(Invoke-L2Query …)` 后拿到的是「一个元素、
那个元素是整张表」的数组。本票把这样的调用改成直接赋值，加了扫描护栏 `scripts/l2/Test-L2WholeArrayReturn.ps1`，
并让「本该恰好一行」的读取显式判行数。这个目录是每一处改动的实测记录。

## 数量怎么对上的

| 数法 | 结果 | 说明 |
| --- | --- | --- |
| `git grep -n "@(Invoke-L2Query" -- scripts`（票面的数法，`d5efa06f`） | 39 行、18 个文件 | 其中 4 行是注释，写的正是「不要写成 `@(Invoke-L2Query ...)`」 |
| 语法树扫描，`@()` 包 `Invoke-L2Query` | 35 处、14 个文件 | 39 − 4 = 35 |
| 语法树扫描，`@()` 包别的整体返回函数 | 2 处 | `Get-PlanLegs`（票面评论）、`Get-AcknowledgedWorklists`（没人报过） |
| 第一轮改的调用点 | 37 处、16 个文件 | `red/guard-on-d5efa06f.txt` |
| 独立审查找到、第一版护栏漏掉的 | 1 处 | `g3-reversed-direction-journey.ps1:165`，`@(Wait-L2RealOrLast … -Probe { Get-L2DemandJourneySnapshots … })`，`red/guard-relay-on-07f83a8f.txt` |
| **合计** | **38 处、17 个文件** | |
| 改完后 | 0 处 | `green/guard-and-helper-list.txt` |

第 38 处为什么第一版漏了：`Wait-L2RealOrLast` 自己不是整体返回的函数，它超时后走 `return & $Probe`，把探针里那次
整体返回原样交出来；成功路径交回的是展开的值。护栏现在把这种「转交脚本块参数输出」的函数单独认出来。

「整体返回」的函数一共 123 个（36 个在共享文件里），清单在 `green/guard-and-helper-list.txt`。第一轮的 122 个用文本搜索
独立对过：带 `return ,` 行的函数 112 个，减去 10 个不是真函数的（9 个是自检夹具里的字符串，1 个是类方法脚本块），
加上 20 个没有 `return ,` 行的（19 个原样转交 `Invoke-L2Query`，1 个用不带 `return` 的一元逗号），102 + 20 = 122。
第 123 个是这一轮护栏模块自己新增的一个函数。

## 旧写法到底坏在哪：和票面说的不完全一样

票面说「`.Count -eq 1` 恒真，所以出现重复行也照样判绿」。前半句对，后半句实测**多数情况下不成立**。
`tools/Test-CriteriaOffline.ps1` 把每处读法从新旧两版文件里原样切出来，对着真实的库各跑三种条件
（原样／行数翻倍／零行），结果在 `green/criteria-offline.txt`：

| 8 条「恰好 N 行」判据，旧写法，行数翻倍 | 条数 | 是哪些 |
| --- | --- | --- |
| 仍然绿（真的在空转） | 1 | `G3-02-36` 的录入请求（`Count -eq 1` 之后只判「有值」） |
| 变红，但不是 `.Count` 判出来的 | 5 | `L2-CPM-00`、`L2-MCT-00`、`L2-SRJ-10`、`G3-02-47`、`G3-02-35` 工作流 |
| 抛异常（场景中断） | 2 | `L2-AAU-04`、`G3-02-35` 租约 |

那 5 条之所以还会红，是因为 `.Count -eq 1` 后面紧跟着 `[string]$rows[0].X -eq '某值'`：`$rows[0]` 是整张表，
取属性成员展开成两个值，`[string]` 把它们拼成 `"COMPLETED COMPLETED"`，比较失败。计数那半句确实没在判，
整条判据是被旁边那半句碰巧救了。`G3-02-36` 没有这样的半句，它能一直没出事是因为查的是主键。

真正一直在发生的是另外两件事：

- **零行时抛异常而不是给结论。**38 个用例里旧写法有 26 个在零行时抛异常，新写法 2 个（见下）。
  这就是 control-server#390 第一轮 G3 的 G3-12-07 中断的原因。
- **「取第一行」的函数返回了所有行。**`if ($rows.Count -eq 0) { return $null }; return $rows[0]` 在两行时
  交回两行的数组，`[string]` 之后是 `"Completed Completed"`。

## 去掉包装之后的另一面：两行被悄悄放过（审查 L1）

只去掉 `@()` 的话，「取第一行」在两行时会安静地取第一行。旧写法遇到两行碰巧判红或抛异常，所以单纯去包装
在这一点上比原来松。按语义本该恰好一行、而键不唯一也没有 `ORDER BY` 的读取一共 13 处，现在都经
`Read-L2SingleRow`（`scripts/l2/L2SingleRow.psm1`）：一行交回那一行；多行交回一个替身，列与查询相同，每一列的值都是
`(N rows, expected 1)`，拿它和期望值比都不成立，判据变红，actual 里写着行数。

| 读取 | 处数 | 位置 | 零行 |
| --- | --- | --- | --- |
| `JourneyRuntimes WHERE DemandId` | 7 | `Get-Stage`（3 个任务类型场景）、`area-assignment-unmapped-silent` 的探针、`charging-policy-missing…` 与 `charging-thresholds…` 的探针、`mandatory-charge…` 的 `Get-Journey` | `$null`（等待探针轮询的就是它） |
| `OrderIntents WHERE DemandId AND Purpose` | 2 | `Get-Intent`（2 个任务类型场景；第三个场景里的 `Get-Intent` 没有调用方，已删） | `$null` |
| 原来是 `(…)[0]`、没有 `ORDER BY` | 4 | `g3-sublot-rejected.ps1` 的 `$dispatched`、`$refusedRuntime`、`$finalRuntime`，`stop-ended-journey-continues.ps1` 的 `$gateStopRow` | 替身（`-Required`），不再下标越界 |

每处按 0／1／2 行离线重放过（`green/criteria-offline.txt` 里 `single-row` 与 `single-row-required` 两类，共 13 个用例）：

- 一行：和改动前读到的值相同；
- 两行：`(2 rows, expected 1)`，或判据为 False；
- 零行：前 9 处是 `$null`，后 4 处是 `(0 rows, expected 1)` 或判据为 False。

`Read-L2SingleRow` 自己有自检 `scripts/l2/Test-L2SingleRow.ps1`（28 条，`green/single-row.txt`），因为正确的产品上永远恰好一行，
任何装置上的绿跑都走不到它存在的那两个分支。

**替身的边界（增量审查）。**替身每一列都是非空字符串：和字面期望值比相等一定不成立，但「不是 X」、「有值」、真值判断、
和数按文本比大小、替身与替身相等，这些在替身上都是 True。增量审查把 13 处读取在 0／1／2 行下逐处实跑过，没有误绿；
规则写成一句——**建在单行读取上的判据，至少要有一个与字面期望值比较的 `-eq`**——清单在 `L2SingleRow.psm1` 头注释里，
每一条在自检里有一条 HAZARD 用例钉着。自检里对 NULL 的那条用例原来是空转的（连接替身对 NULL 直接交回 `$null`，
删掉 `IsDBNull` 分支仍然全绿）；替身改成交回 `DBNull` 之后，删掉那个分支红 1 条：
`red/single-row-null-case-with-isdbnull-branch-removed.txt`。

**仍然用 `(…)[0]` 的 2 处**，零行时照旧抛异常，是这份报告里「新写法零行抛异常 2 个」的来源：
`task-type-binding-admits-bound-station.ps1` 的 `$freeze`（全是标量子查询，恒为一行）、
`stop-ended-journey-continues.ps1` 的 `$bStopRow`（按主键 `StopId` 读）。

## 新写法，逐类结论

| 类别 | 用例数 | 新写法：原样／翻倍／零行 |
| --- | --- | --- |
| 「恰好 N 行」判据 | 8 | True／**False**／**False** |
| `Read-L2SingleRow`（轮询里的读取） | 9 | 一行／`(2 rows, expected 1)`／`$null` |
| `Read-L2SingleRow -Required` | 4 | 一行的值／替身或 False／替身或 False |
| 取第一行或 `$null`（键唯一或带 `LIMIT 1`） | 7 | 一行／同一行／`$null` |
| `(…)[0]`（恒为一行） | 2 | 值／同一个值／抛异常 |
| 「至少一行」的判据（`L2-UE-05`／`L2-UE-08`） | 1 | True／True／**False** |
| 说明文字 | 7 | 三种条件都不抛异常；两行不再被说成「没有行」 |

`38 cases, 38 as required, 0 not.` `Get-L2SecondLegIntents` 那一处不在这 38 个里，它有自己的自检，见下；
`area-eqp-unique-across-task-types.ps1` 里没有调用方的 `Get-Intent` 已删，也不在。
38 个用例对应 36 处读取加 2 条说明文字（同一处读取的判据和说明文字各算一个用例）。

说明文字里原来有三处把两行说成没有行（审查 L2）：`L2-SRJ-10` 的 `(no attempt)`、`G3-02-35` 的 `(no workflow row)`、
`G3-02-45` 的 `(no row)`。现在照实写行数：`(2 attempts)`、`(2 workflow rows)`、`(2 rows)`。

## `Get-L2SecondLegIntents`：自检的桩把毛病藏住了

`L2TaskTypeJourney.psm1` 里它原来是 `return , @(Invoke-L2Query …)`，而它的自检
`Test-L2SecondLegIntentWait.ps1` 全绿。原因是自检里替换 `Invoke-L2Query` 的桩写的是 `return $global:l2StubRows`
（展开返回），在这种桩下面 `@(…)` 恰好是对的。真库上它交回的是一个元素，「一条需求有两条第二程就拒绝判定」
那条保护从来走不到。

- 桩改成真函数的形状以后，**旧模块 8 条红 3 条**：`red/second-leg-intent-wait-on-old-module.txt`
  （`BAD two intents, the first confirmed … -> throws timeout`）。
- 新模块 8 条全绿：`green/second-leg-intent-wait.txt`。
- 护栏因此多了一条 reshaped：模块外与模块里整体返回函数同名、却展开返回的函数判红。另一个同样形状的桩
  （`Test-L2WorklistStationWait.ps1`）一并改了，23 条仍全绿：`green/worklist-station-wait.txt`。

## 护栏自己的验证

- **在原始代码树上是红的**：`red/guard-on-d5efa06f.txt`，37 处，退出码 1。
- **在第一轮的顶端上，补了转交之后是红的**：`red/guard-relay-on-07f83a8f.txt`，报出 `g3-reversed-direction-journey.ps1:165`。
- **放回包装必须红**：`red/guard-on-injected-copy.txt`。在 `scripts/` 的一份副本里放回七种写法
  （票面原样、换行加小写加多余空格加反引号续行、`Get-PlanLegs`、把模块函数 `Get-L2RealInbound` 直接送管道、
  包一个场景自己的转交函数、`foreach` 直接放在 `in` 后面、把桩改回展开返回），7 处全部报出，退出码 1。
  注入做在副本上，工作树没有动过，所以不存在还原的问题。
- **每一种判定都量过**：护栏自检里 42 个夹具，各是一个小脚本，既看扫描结论，也实际运行，记下读到 0／1／3 行时
  调用方看到几行。16 种错误写法必须被报出（其中 reshaped 那个桩量出来是 0,1,3，这正是它被判红的理由）；
  12 种合法写法必须不报且量出 0,1,3。
- **查不到的写法也量过**：14 个 `miss-` 夹具，实测运行结果是错的、扫描确实不报。清单在模块头注释和
  `scripts/l2/README.md` 里。第一版头注释写过「这些写法当前一处也没有」，那句话没量过，而且是错的（第 38 处就是）；
  现在改成只说量过的事：扫描今天对 `scripts/` 报 0 处，标了夹具的写法已知会漏。
- 模块限定名（`L2\Invoke-L2Query`）第一版查不到，现在查得到，有夹具。

## 运行结论

| 内容 | 结果 | 位置 |
| --- | --- | --- |
| 本机全部合成 L2，default 模式，`849a09ad`（第一轮） | 70 个场景、72 次运行，72 PASS、0 FAIL，2093 秒 | `green/synthetic-l2-default-849a09ad.txt` |
| 审查意见改动之后，重跑被改到的 9 个合成场景（加 1 个提供库的），`8cee3acf` | 10 PASS | `green/synthetic-l2-kept-databases.txt` |
| 离线判据重放（用上一行留下的库） | 38 个用例，38 个符合 | `green/criteria-offline.txt` |
| 本机服务端全量 `dotnet test`，`4cff9f8a`（第一轮） | 3633 通过、0 失败 | 见 PR 正文 |
| 增量审查意见之后，`area-eqp-unique-across-task-types` 重跑，`91bddb1a` | 1 PASS | |
| `test.yml` 的 15 个脚本自检步骤，`91bddb1a` | 全部退出码 0 | |

`849a09ad` 之后被改过的合成场景正好是第二行重跑的那 9 个；其余 61 个合成场景的文件和它们加载的模块自那以后没有变。
服务端全量在审查意见改动之后没有在本机重跑：这一轮没有动 `src/` 与 `tests/`，由 CI 的那一轮覆盖。

## 未在真装置上重跑

下面这些文件被改了，而本机跑不了它们所在的装置，只做了离线重放。调度的决定是合入前不为它们单独申请时段：
审查把只在真装置或 G3 上才执行的改动逐处离线重放过，正确产品上恰好一行时新旧读出的值相同，改动不碰 UIA 和驱动。
这些场景在下一次本来就要跑的 G3 或真装置轮次里带上，**到时如果有判据变红，当产品信号处理**。

| 场景 | 被改到的文件与位置 | 离线用的库 |
| --- | --- | --- |
| `g3-sublot-rejected` | 场景文件 5 处读取（其中 3 处现在经 `Read-L2SingleRow -Required`） | cs#390 真实 G3 运行（`evidence/g3/cs390-journey-fa4a5ce3`）的表快照；读 `ProtocolOutbox` 的一处用合成场景 `sublot-rejected-after-entry` 的库 |
| `g3-load-cancellation-before-load` | 场景文件 3 处读取、1 处说明文字 | 同一次 G3 的表快照；读 `ProtocolOutbox` 的一处用合成场景 `load-cancelled-before-sublot` 的库 |
| `g3-waiting-point-idle-return` | `Format-Plan` 的说明文字 | 不读库 |
| `g3-reversed-direction-journey` | 场景文件的 `$snapshots`（超时路径）；`L2TaskTypeJourney.psm1` 的 `Get-L2SecondLegIntents` | 不读库（超时路径用真的 `Wait-L2RealOrLast` 重放）；`Get-L2SecondLegIntents` 见自检 |
| `g3-task-type-admission-fail-closed` | `L2TaskTypeJourney.psm1` 的 `Get-L2SecondLegIntents` | 见自检 |
| `real-onboard-unload-not-emptied` | `Get-RecoveryFootprint`，它进 `L2-UE-05` 与 `L2-UE-08` 两条判据 | 合成场景 `sublot-rejected-after-entry` 的库（该场景的证据快照把空表存成空文件，还原不出表结构） |
| `real-onboard-mixed-side-one-stop` | 一条判据失败时的说明文字 | 不读库 |
| `g3-exception-compensate`、`g3-fault-cargo-handoff`、`g3-load-cancellation`、`real-onboard-load-door-closed-empty-reopens`、`real-onboard-rebuild-stopped-cargo-handoff` | 共用的 `G3RecoveryCommon.ps1` 里 `Add-G3VehicleReleasedForNextDemand` 的一处读取，读出来只进「下一单没有建旅程」时的说明文字 | cs#390 真实 G3 运行的表快照 |

离线拿到的是运行终态的库，场景中途的读取（`g3-sublot-rejected` 的 `$refusedRuntime`、`$pendingEntry`）没有覆盖那个时刻。

## 工具

- `tools/Run-SyntheticL2Local.ps1`：按 `l2.yml` 的 default 模式在本机跑合成场景。场景清单是从 `l2.yml` 里读出来的，
  不是抄的。`-KeepStage` 在运行期间握住每个库文件的读句柄，让通过的运行也留下库。
- `tools/Test-CriteriaOffline.ps1` 与 `tools/Cases.ps1`：离线重放。读法按锚点从场景文件里切出来，判据文字必须在场景
  文件里原样找得到，否则拒绝运行。翻倍与零行是在 `Invoke-L2Query`／`Read-L2SingleRow` 这一层把查询包成
  `SELECT * FROM (…) UNION ALL SELECT * FROM (…)` 与 `SELECT * FROM (…) WHERE 0` 做的，底下调用的仍是仓库里的真函数。
