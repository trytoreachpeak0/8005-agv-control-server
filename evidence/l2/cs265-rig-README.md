# cs#265 的真装置运行：两个场景，不是一次 G3 门禁

> **这一轮证的是「这两个场景在真装置上跑得过」，不是一次 G3 门禁。**
>
> 这句话写在最前面，因为**一个更窄的运行最大的风险不是它窄，是它被当成不窄的那个引用**。几周之后，
> 证据目录还躺在这里，而「它是怎么跑出来的」只剩这份文件还记得。

## 为什么单跑两个场景

cs#265 改的那道等待（第二站清单等待，`L2TaskTypeJourney.psm1`）**只在真装置上执行**，而自检用的是
构造的桩数据。要在真机上看它一眼，需要走到它的场景。

**走到它的场景只有两个**——`g3-reversed-direction-journey` 与 `g3-task-type-admission-fail-closed`，
它们是唯一调用 `Invoke-L2TaskTypeJourney` 的。

**而 `l2.yml` 真装置作业的固定清单（批次出口那一轮复现的就是它）全部是 `real-onboard-*`，不含任何
`g3-*`**，所以出口那一轮覆盖不到这条改动。`run-journey-g3.ps1` 会跑到它们，但它**没有按名字挑场景的
参数**（`-Slice` 的注释明写「决定这一轮认证什么，不决定跑什么」），一跑就是全部 14 个。

于是照 `run-journey-g3.ps1` 自己的做法逐场景调 rig——**那不是绕过它，就是它内部做的同一件事**，
少了外面那层。

## 结果

| 场景 | 判据 | 结果 |
| --- | --- | --- |
| `g3-reversed-direction-journey` | 9 条 | **9 PASS**，退出码 0 |
| `g3-task-type-admission-fail-closed` | 9 条 | **9 PASS**，退出码 0 |

三端提交：

| 端 | 提交 |
| --- | --- |
| `8005-agv-control-server` | `f4a2f970`（本票的改动） |
| `8005-agv-onboard-hmi` | `e6bea2674a54a80ea92f9df8031999d46c77d321`（`w2g/fp-v2-impl` 顶端） |
| `slots-simulator` | `fb5f7c593742bf98bc3957b8729a38aad5321f28`（`main` 顶端） |

## PASS 本身证明不了这条改动，所以判据的取值才是重点

一次全绿运行**可能根本没有走到那道等待**，那样两个 PASS 对本票就是零信息。真正的判据是
`timeline.jsonl` 里那两行——**它们记下的是站点 id，而改之前记的是条数**：

| 场景 | `origin-worklist-acknowledged` | `destination-worklist-acknowledged` |
| --- | --- | --- |
| `g3-reversed-direction-journey` | `派工待送` | `N1-3_N1-7` |
| `g3-task-type-admission-fail-closed` | `N1-3_N1-7` | `关卡` |

三件事因此在真机上成立：

1. **那道等待被执行到了**，两个场景都是；
2. **两站的 id 真实、非空、不同**——新判据在真机上可满足，而不只是在桩数据上；
3. **两个场景用的是两组不同的站点**，所以这不是撞对了某一组特定值。

### 记下的站点确实是「第二站」，不只是「另一个站点」

新判据只要求「站点不同于第一站」。**「不同」不等于「就是第二站」**，所以下面这一步是单独核的，
用的是每一轮**自己的** timeline 记录，不是读配置推出来的：

| 场景 | 该场景的第二站（运行自己记的） | `destination-worklist-acknowledged` |
| --- | --- | --- |
| `g3-reversed-direction-journey` | `$areaName = 'N1-3_N1-7'`（场景源码第 52 行） | `N1-3_N1-7` ✓ |
| `g3-task-type-admission-fail-closed` | timeline note：`WIRE_TO_GATE -> 关卡/210` | `关卡` ✓ |

两轮都逐字对上。**将来这条判据出问题时，上面这两张表里的字符串是唯一能说明「当时真机上是什么情况」
的东西**——所以写的是实际值，不是「通过了」。

### 「只有一行」＝「第一次探测就满足」，而这条推理有一个前提

**每个准则只出现一行。** journal 只在取值变化时写（`L2.psm1` 的 `Observe`），所以一行意味着
**第一次探测就满足**——与票面记的一致（这道等待从未真正等过），也说明改判据**没有引入新的等待时长**。

**这条推理依赖 `Wait-L2Condition` 在判 `Until` 之前调 `Observe`，而且每一轮都调**
（`L2.psm1:91-92`）。因为那样第一轮必写一行：不满足时写的是 `'(null)'`，下一轮拿到真值取值就变了、
必然写第二行。所以「只有一行」严格等价于「第一次拿到的就是最终那个值，且它满足条件」。

**把这个前提写下来，是因为它失效时不会有任何东西变红。** 如果将来有人把 `Observe` 挪到 `Until`
之后（看起来更省——只记满足的那一次），timeline 照样每个准则一行，含义却从「第一次就满足」悄悄变成
「满足过」，而上面那句关于等待时长的结论就不再成立。

## 这份证据没有什么

**它不是 G3 门禁证据，不许当门禁引用。** 与 `run-journey-g3.ps1` 的一轮相比，缺四样：

- 协议身份与 G1 校验；
- slice 认证（`-Slice` / `Write-G3GateResults`）；
- 跨场景的汇总 `SUMMARY.md`；
- staged exact clone——对端用的是 detached worktree（跑完已删），不是 runner 那套一次性精确克隆。

**另外 `logs/` 与 `snapshots/` 没有入库**（两个场景合计 4.2 MB 日志），照已入库的同类证据
`cs203-rig-green-*` 的先例：那些目录也只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`。
**这是入库前决定的**——推上去再删只省可读性，blob 已经进历史，体积省不掉。要日志就照下面重跑。

## 怎么重跑

两把机器级锁（`Global\W2G-L2PortBlock` 先、`Global\W2G-InteractiveDesktop` 后）由 rig 自己取；
桌面上会出现两个 WPF 窗口；不动车、不发急停。

```powershell
$w = 'C:/Users/szy/Desktop/8005-workspace-v2'
pwsh -NoProfile -File $w/worktrees/<cs-worktree>/scripts/l2/Invoke-L2Scenario.ps1 `
  -Scenario g3-reversed-direction-journey `
  -EvidenceRoot <一个新目录> `
  -Repository $w/worktrees/<cs-worktree> `
  -OnboardRepository <onboard 的干净 worktree> `
  -SimulatorRepository <simulator 的干净 worktree>
```

第二个场景同理，换 `-Scenario g3-task-type-admission-fail-closed`。**对端必须是干净的 worktree**，
发布会拒绝脏工作区；**跑完删掉自己建的 detached worktree**。
