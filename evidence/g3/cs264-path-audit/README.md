# cs#264 的路径审计：四个 G3 runner 里，哪些吃路径的参数会被交给子进程

> **这个目录回答的是一个判断题，不是一次运行的记录**：给四个 runner 加 `Resolve-Path`，哪几处是在**修一个
> 现存缺陷**，哪几处只是**防御性加固**。两者的注释必须写不同的话，否则四份一样的注释里有三份在描述一个
> 它们没有的故障——而它会以「另外三处也这么写」的样子被相信。

## 结论

| runner | `$EvidenceRoot` 派生路径进子进程 | `$StageRoot` 派生路径进子进程 |
| --- | --- | --- |
| `run-staged-g3.ps1` | **无** | **有**（9 处） |
| `run-staged-g3-restart.ps1` | **无** | **有**（9 处） |
| `run-demand-bearing-g3-vectors.ps1` | **无** | **有**（1 处） |
| `run-journey-g3.ps1` | **有**（`scenarioEvidence`，就是 cs#211 栽的那一处） | **有**（3 处） |

`$FieldRunRoot` 与 `$Output` 两个种子跑下来都是零命中（`-Output` 根本不是这四个脚本的参数）。

**所以 `-EvidenceRoot` 的那个缺陷只存在于 `run-journey-g3.ps1`**，另外三个加绝对化是把保证从纪律搬到构造；
而 **`-StageRoot` 四个 runner 全都有**，面比 `-EvidenceRoot` 还广，而且**原先没有任何一个绝对化它**。

## 为什么相信这张表

三件事撑着它，缺一条它就只是「我扫了一遍」：

**一、追的是数据流，不是名字。** 第一版按变量名过滤（`evidence|logsRoot|output|…`），**漏掉了
`$proxyTranscript`**——它是 `Join-Path $EvidenceRoot 'fault-proxy-events.ndjson'`，证据派生，名字里却完全
没有那个词。**名字是人给的，数据流不是。** 现在从种子变量出发沿赋值传播到不动点，再看有没有派生变量进了
`-Arguments`／`-ArgumentList`。传播是**过度包含**的（`run-staged-g3.ps1` 有 122 个派生变量），而对
「有没有漏报」这个问题，过度包含是安全的那一侧。

**二、种过坏例，确认它会叫。** 在一份副本里给 `run-staged-g3.ps1` 加一行

```powershell
Start-Process -FilePath 'dotnet' -WorkingDirectory $controlSource -ArgumentList @('run', '--transcript', $proxyTranscript)
```

它报出 `line 2295: proxyTranscript  [cmd: Start-Process]`——**命令类型不同（`Start-Process` 而非
`Invoke-LoggedCommand`）、变量名不同，都抓得到。** 一个报零的扫描器和一个坏掉的扫描器从外面看是一样的，
这一步是唯一能区分它们的东西。

**三、第二条通道也排了。** 相对路径走丢还有一种可能：父进程自己在 `Push-Location` 窗口内写文件。实读四个
runner 的窗口——**一模一样，各只有 7 行，里面只有 `& $FilePath @Arguments`，没有任何写**（父进程的
`Out-File` 在 `Pop-Location` 之后）。**所以「唯一通道是子进程参数」是结论，不是推测**，上面那张表才立得住。

## cs#211 那次为什么骗人

`run-journey-g3.ps1` 把 `-EvidenceRoot $scenarioEvidence` 交给一个 `-WorkingDirectory $controlSource`
（stage 里的 ControlServer 副本）的子 `pwsh`。相对时，这个脚本在操作员所在目录建了那棵树，**而每个子进程
在 stage 树下各建各的**。

**失败的样子会骗人**：目录在、场景全跑、全 PASS、退出码全 0——**那些都是父进程自己的写，而父进程从不改
自己的工作目录**。只有汇总回头读 `$EvidenceRoot` 时发现空的，然后它说
`No scenario reported a protocol release identity.`——**听起来像协议出了问题**，于是一整个 G3 时段花在了
错误的方向上。

**`Invoke-L2Scenario.ps1` 自己也绝对化 `-EvidenceRoot`，那救不了**：它绝对化的已经是解析错了的那个位置。
**一个下游的正确动作修不了上游传下来的相对路径**——更麻烦的是，那个下游动作的存在会让人以为这件事整个
仓库都已经想过了。

## 怎么重跑

```powershell
pwsh -File .\evidence\g3\cs264-path-audit\Find-PathArgumentsReachingChildren.ps1                # 默认种子 $EvidenceRoot
pwsh -File .\evidence\g3\cs264-path-audit\Find-PathArgumentsReachingChildren.ps1 -Seed StageRoot
```

`result-EvidenceRoot.txt` 与 `result-StageRoot.txt` 是本票改动**之前**的输出。改动只加绝对化、不动那些
调用点，所以重跑结果不变——**它记录的是「哪里需要修」，不是「修好了没有」。**
