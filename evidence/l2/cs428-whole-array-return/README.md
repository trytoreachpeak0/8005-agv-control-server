# cs#428：整体返回数组的读法，改动前后逐处实测

`Invoke-L2Query` 以 `return , $rows` 交回整张结果表。调用方写成 `@(Invoke-L2Query …)` 后拿到的是「一个元素、
那个元素是整张表」的数组。本票把 37 处这样的调用改成直接赋值，并加了扫描护栏
`scripts/l2/Test-L2WholeArrayReturn.ps1`。这个目录是每一处改动的实测记录。

## 数量怎么对上的

| 数法 | 结果 | 说明 |
| --- | --- | --- |
| `git grep -n "@(Invoke-L2Query" -- scripts`（票面的数法，`d5efa06f`） | 39 行、18 个文件 | 其中 4 行是注释，写的正是「不要写成 `@(Invoke-L2Query ...)`」 |
| 语法树扫描，`@()` 包 `Invoke-L2Query` | 35 处、14 个文件 | 39 − 4 = 35 |
| 语法树扫描，`@()` 包别的整体返回函数 | 2 处 | `Get-PlanLegs`（票面评论）、`Get-AcknowledgedWorklists`（没人报过） |
| 合计要改的调用点 | **37 处、16 个文件** | `red/guard-on-d5efa06f.txt` |
| 改完后 | 0 处 | `green/guard-and-helper-list.txt` |

35 处里有 10 处在 CI 每轮都不跑的真装置与 G3 场景里（`g3-sublot-rejected` 5、`g3-load-cancellation-before-load` 3、
`real-onboard-unload-not-emptied` 1、`G3RecoveryCommon.ps1` 1）。

「整体返回」的函数一共 122 个（35 个在共享文件里），清单在 `green/guard-and-helper-list.txt`。这个数用文本搜索独立
对过：带 `return ,` 行的函数 112 个，减去 10 个不是真函数的（9 个是自检夹具里的字符串，1 个是类方法脚本块），
加上 20 个没有 `return ,` 行的（19 个原样转交 `Invoke-L2Query`，1 个用不带 `return` 的一元逗号），102 + 20 = 122。

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
取属性成员展开成两个值，`[string]` 把它们拼成 `"COMPLETED COMPLETED"`，比较失败。也就是说计数那半句确实没在判，
整条判据是被旁边那半句碰巧救了——换一条不比较属性的判据就救不了，`G3-02-36` 就是。那一条能一直没出事，是因为
它查的是主键，库里本来就不可能有第二行。

真正一直在发生的是另外两件事：

- **零行时抛异常而不是给结论。**36 个用例里旧写法有 23 个在零行时抛异常；新写法 0 个判据抛异常（剩下 6 个见下）。
  这就是 control-server#390 第一轮 G3 的 G3-12-07 中断的原因。
- **「取第一行」的函数返回了所有行。**`if ($rows.Count -eq 0) { return $null }; return $rows[0]` 在两行时
  交回两行的数组，`[string]` 之后是 `"Completed Completed"`；新写法交回第一行。17 处。

## 新写法，逐类结论

| 类别 | 处数 | 新写法：原样／翻倍／零行 |
| --- | --- | --- |
| 「恰好 N 行」判据 | 8 | True／**False**／**False**（都是结论，不抛异常） |
| 取第一行或 `$null`（函数或探针） | 17 | 一行／**同一行**／`$null` |
| `(…)[0]` 后取属性 | 6 | 值／同一个值／抛异常（见下） |
| 只影响说明文字 | 5 | 三种条件都不抛异常 |
| `Get-L2SecondLegIntents`（模块函数） | 1 | 见下一节，有自己的自检 |

合计 37 处。36 个离线用例全部符合上表（`36 cases, 36 as required, 0 not`）。

**零行时仍然抛异常的 6 处**都是 `(Invoke-L2Query …)[0]`，是紧跟在一次等待之后的前置读取，不是判据：
`g3-sublot-rejected.ps1` 的 194、293、395 行，`task-type-binding-admits-bound-station.ps1:115`，
`stop-ended-journey-continues.ps1` 的 92、196 行。改动前它们在零行时同样抛异常，只是位置从「取属性」提前到了
「取下标」。本票没有改它们的行为。

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
- **放回包装必须红**：`red/guard-on-injected-copy.txt`。在 `scripts/` 的一份副本里放回七种写法
  （票面原样、换行加小写加多余空格加反引号续行、`Get-PlanLegs`、把模块函数 `Get-L2RealInbound` 直接送管道、
  包一个场景自己的转交函数、`foreach` 直接放在 `in` 后面、把桩改回展开返回），7 处全部报出，退出码 1。
  注入做在副本上，工作树没有动过，所以不存在还原的问题。
- **每一种判定都量过**：护栏自检里 13 种错误写法、10 种合法写法各是一个小脚本，既看扫描结论，也实际运行，
  记下读到 0／1／3 行时调用方看到几行。错误写法看到的不是 0,1,3（多数是 1,1,1），合法写法是 0,1,3。唯一的例外是 reshaped 那个桩：
  它量出来是 0,1,3，而这正是它被判红的理由——桩把被测代码里的包装错误量成了对的。
- **查不到的写法也量过**：名字放在变量里调用、经别名调用、存在变量里的脚本块、`$( )` 收集循环输出。这四种运行
  结果确实是错的，扫描确实不报，夹具名以 `miss-` 开头。以后扫描学会了其中一种，对应夹具会红，提醒把说明一起改。

## 运行结论

| 内容 | 结果 | 位置 |
| --- | --- | --- |
| 本机全部合成 L2，default 模式，`849a09ad` | 70 个场景、72 次运行，72 PASS、0 FAIL，2093 秒 | `green/synthetic-l2-default-849a09ad.txt` |
| 留库重跑受影响的 10 个合成场景（`4cff9f8a`，场景文件与 `849a09ad` 相同） | 10 PASS | `green/synthetic-l2-kept-databases.txt` |
| 离线判据验证 | 36 个用例，36 个符合 | `green/criteria-offline.txt` |

`849a09ad` 之后的提交没有再动任何场景文件和被场景加载的模块，`git diff 849a09ad HEAD -- scripts/l2/scenarios
scripts/l2/Invoke-L2Scenario.ps1 scripts/l2/L2.psm1 scripts/l2/L2TaskTypeJourney.psm1` 为空。

## 未在真装置上重跑

下面这些文件被改了，而本机跑不了它们所在的装置。它们只做了离线读法验证：

| 文件 | 改动处 | 离线用的库 |
| --- | --- | --- |
| `g3-sublot-rejected.ps1` | 194、293、319、391、395 | cs#390 真实 G3 运行（`evidence/g3/cs390-journey-fa4a5ce3`）的表快照还原；319 行读 `ProtocolOutbox`，快照里没有这张表，用合成场景 `sublot-rejected-after-entry` 的库 |
| `g3-load-cancellation-before-load.ps1` | 256、258、278 | 同一次 G3 运行的表快照；278 行读 `ProtocolOutbox`，用合成场景 `load-cancelled-before-sublot` 的库 |
| `G3RecoveryCommon.ps1` | 370（说明文字） | 同一次 G3 运行的表快照 |
| `g3-waiting-point-idle-return.ps1` | 98（说明文字） | 不读库，用两条腿的计划对象 |
| `real-onboard-unload-not-emptied.ps1` | 57 | 合成场景 `sublot-rejected-after-entry` 的库（该场景的证据快照把空表存成空文件，还原不出表结构） |
| `real-onboard-mixed-side-one-stop.ps1` | 174（说明文字） | 不读库，用两份已确认清单 |

cs#390 那次真实 G3 运行的终态里，`OperationResults`、`RecoveryWorkflows`、`VehiclePurposeClaimRecords` 都恰好一行，
所以 `G3-02-47`、`G3-02-35` 在那份真实数据上用新写法读出来是 True。这不等于在真装置上重跑过：场景中途的读取
（293、319 行）离线只能拿到终态。

## 工具

- `tools/Run-SyntheticL2Local.ps1`：按 `l2.yml` 的 default 模式在本机跑合成场景。场景清单是从 `l2.yml` 里读出来的，
  不是抄的。`-KeepStage` 在运行期间握住每个库文件的读句柄，让通过的运行也留下库。
- `tools/Test-CriteriaOffline.ps1` 与 `tools/Cases.ps1`：离线验证。判据文字必须在场景文件里原样找得到，否则拒绝运行。
  翻倍与零行是在 `Invoke-L2Query` 这一层把查询包成 `SELECT * FROM (…) UNION ALL SELECT * FROM (…)` 与
  `SELECT * FROM (…) WHERE 0` 做的，底下调用的仍是 `L2.psm1` 里的真函数。
