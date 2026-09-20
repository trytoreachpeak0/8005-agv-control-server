# `Test-L2HmiPhraseWatch.ps1` 的红绿证据（control-server#204，条目 4～6）

这两份输出证明的不是「新扫描逻辑跑得通」，而是**旧写法在这些判据上确实是红的**——也就是
`L2-DA-09` 此前那句「10 轮扫描、0 次检出」并不等于「看了 10 次、一次都没出现」。

## 红：`red/output.txt`

红的那一版**不是为了取红造的稻草人**，它是 `real-onboard-durable-ack-lost.ps1` 里
`Watch-UnfinishedProjection`（旧 `:62-74`）逐行搬进 `L2HmiPhraseWatch.psm1` 的结果：单个元素读名失败
`continue`、整轮仍 `Scans++`，整轮枚举失败直接往外抛，10 秒连扫借用会写 `Not reached` 的等待函数。

8 条用例里 6 条红，各对应旧写法的一个缺陷：

| 用例 | 旧写法读到的 | 期望 | 为什么它让 `L2-DA-09` 说得比它知道的多 |
| --- | --- | --- | --- |
| 一个元素读不到名字的那一轮不算一次「看」 | `clean 1 / failed 0 / bad elements 0` | `clean 0 / failed 1 / bad elements 1` | 读失败的元素被 `continue` 掉，这一轮照样计入 `Scans`；「看了 10 次」里可能有几次没读全 |
| 整棵树枚举不出来是失败轮、不是异常 | `threw=True / failed 0` | `threw=False / failed 1` | 异常抛进承载它的业务等待，最后表现为那个等待超时，红的原因指向错的地方 |
| 窗口没了是失败轮、不是静默的什么都没有 | `clean 0 / failed 0` | `clean 0 / failed 1` | 既不计入看过、也不计入失败，这一轮从账上消失 |
| 失败轮里的检出仍然是检出 | `seen 1 / clean 1 / failed 0` | `seen 1 / clean 0 / failed 1` | 检出确实记下了，但那一轮被记成了干净的——这条用例的作用是反向验证：收紧之后不许为了「这轮脏」把检出一起丢掉 |
| 承载失败扫描的业务探针仍返回业务值 | `threw=True` | `value='AwaitingGateArrival' threw=False` | 同第二条，业务等待被 UIA 异常拖垮，是 PR #196 审查点名的假红来源 |
| 走完采样窗口要记读数，不是 `Not reached` | `'Not reached' notes 1` | `'Not reached' notes 0` | 绿的那次运行**必然**走完 10 秒不检出，于是每次绿运行的 journal 里都留着一条「未到达」 |

另外 2 条本来就是绿的（干净轮计数、检出即停）。留着它们是为了说明这份自检**不是全红**：
判别力在于哪几条红、哪几条不红，而不在于数量。检出即停那一条同时挡住一种偷懒的实现——
把采样写成「无论如何扫满整个窗口」。

**这份红重录过一次。**第一次录的时候，构造假元素的 `New-Element([string]$Name)` 带着 `[string]`
类型约束，而 PowerShell 把 `$null` 转成空字符串，于是「读名会抛错的元素」根本没抛。那一版里
第 1、4 两条仍然是红的（旧实现压根没有 `FailedScans` 这个计数，怎么都对不上），**红的数量一样、
红的理由却是错的**。夹具改成不带类型约束之后重录，才是这里这一份。

## 绿：`green/output.txt`

同一份自检，在收紧后的 `L2HmiPhraseWatch.psm1` 上 8 条全绿。
