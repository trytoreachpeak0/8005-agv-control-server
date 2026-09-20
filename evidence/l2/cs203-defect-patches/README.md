# control-server#203 取红证据用的缺陷补丁

这些补丁**只打在本机**，用完还原，**一个都不推**。每一个对应票面的一个条目，各自只改一处。

## 补丁与它们要让谁变红

| 补丁 | 打在哪 | 场景 | 期望只红 |
| --- | --- | --- | --- |
| `item1-admission-reason-leaks-to-blockingfacts.patch` | 服务端 | `g3-task-type-admission-fail-closed` | `G3-10-04` |
| `item2-route-evidence-ends-swapped.patch` | 服务端 | `g3-reversed-direction-journey` | `G3-11-07` |
| `item5-g3-10-07-onboard-infers-task-type-from-plan.patch` | **车载端** | `g3-task-type-admission-fail-closed` | `G3-10-07`（可能连带 `G3-10-08`） |
| `item5-g3-11-08-unload-admission-dropped.patch` | 服务端 | `g3-reversed-direction-journey` | `G3-11-08` |
| `item7-stage-advance-delayed-after-load-commit.patch` | 服务端 | `g3-pickup-load-and-correction` | `G3-02-05`、`-06`（判据用**旧写法**时） |
| `item8-vehicle-occupancy-always-conflicts.patch` | 服务端 | `g3-exception-compensate`、`real-onboard-load-door-closed-empty-reopens` | `G3-07-26`、`L2-DC-12` |

车载端那一份不能像服务端补丁那样「打了就跑」：真装置的对端必须是**干净**的仓库
（`Get-L2PeerPublish` 拒绝有未提交改动的对端，免得测到的是你没看见的状态）。所以它在一个 detached
worktree 上**本地提交**，再把那个提交当对端发布。提交本身绝不推送。

## 验证到什么程度，两件事分开说

**这两件事不一样，读的时候别混：**

- **「注入确实让那条判据红了」**——这是判别力，只有跑到那条判据的场景能证明。
- **「注入没把服务端编坏、旅程照样走」**——这只是注入没有连带损伤，证明不了任何判别力。

| 补丁 | 已经验到哪一步 |
| --- | --- |
| `item5-g3-11-08-...` | **合成场景上只红一条**：`staging-to-wire-reversed-journey` 8 条判据里只有 `L2-S2W-08`（同一件事的合成版）红，实际值 `0 decision(s)`，旅程走完、`WIRE_TO_GATE` 那条也绿 |
| `item8-...` | **确认生效**：合成场景第二个需求变成 `Blocked`。注意它**天然有连带损伤**——让下一单必然冲突正是它的作用，所以合成场景不能用来证明「无连带」 |
| `item1-...`、`item7-...` | **只到编译通过**。它们的判据只在真装置场景里，合成台上既证不了判别力，也没有对应判据可看 |
| `item2-...` | 同一形状的注入在合成场景上验过（见 `../cs203-route-evidence-README.md`），真装置那次取 `G3-11-07` 的红 |

## 被否掉的那一个，以及为什么值得留着

`rejected-i6-restored-blocked-by-two-guards.patch` 是**票面原本要的那个缺陷**——「`STAGING_TO_WIRE`
的准入冻结到装货那次上（恢复 I6）」。**它造不出来**，实跑结果不是红在 `G3-11-08` 上，而是
8 条判据只跑了 3 条、超时在 `AwaitingSublot`。服务端日志给出真因：

```
BusinessIdentityConflictException: Only the operation at the AREA machine station may carry a
station/task admission identity.
```

挡住它的有两道，互相独立：

1. **存储层守卫**（`WireToGateStore.PrepareSlotOperationAsync`，control-server#163 推翻 I6 时加的）：
   准入身份只能挂在 AREA 端那次操作上，**从需求冻结的规则判定，不听调用方的**；
2. **准入关系表只含 AREA 命名的站**（`JourneyRuntimeEngine` 只给 `ParseAreaNamedMachineStations`
   播种）。而「派工待送」站名**刻意不是 AREA 格式**。所以即使绕过第 1 道，装货那次也查不到准入关系。

两道都绕过就不是一个缺陷提交，是重写这块设计。

**这件事比这份红证据本身重要。**`G3-11-08` 今天是绿的，**理由有一部分不在它自己身上**，而在这两道
守卫上——而那两道失效时，它未必会红：它会以完全不同的方式响，整趟卡死、判据**缺席**。缺席比变红
难读得多，这次就是去翻服务端日志才看出来的。

由此，`G3-11-08` 的两个合取项判别力并不对等：

- `$onUnload.Count -eq 1`（卸货那次有一行）——**能注入、能红**，上表那份红证明的就是它；
- `$onLoad.Count -eq 0`（装货那次没有）——**注入不了**，因为让它不成立正是那两道守卫拒绝的事。
  拆掉这一项，判据在今天的产品上照样绿。**它断的是一条由别处保证的不变量，不是这个场景能观测到的行为。**

## 一个不是缺陷的补丁

`item7-revert-sampling-to-pre-ticket.patch` 把 `G3-02-05`／`-06` 的取值点**回退到本票之前的形态**
（`-ne 'AwaitingLoadResult'`）。它配合 `item7-...` 那个缺陷一起用，取的是「旧写法在这个缺陷下会红」那一半；
同一个缺陷不打这个补丁再跑一次，取的是「新写法不会红」那一半。**一个修复要两半证明**，只证前一半是常见的省略。

## 真装置编排别隔着 Python 调 pwsh

取红证据时撞到的，记在这里免得下一个人再烧四次机时：**多隔一层进程，`%LOCALAPPDATA%` 下的对端发布缓存
会读成空**。同一个 `pwsh.exe`、同一个硬编码绝对路径，三次交替探测：

```
Bash → pwsh          : stamp=True  files=19
Bash → python → pwsh : stamp=False files=0
Bash → pwsh          : stamp=True  files=19
```

于是 `Get-L2PeerPublish` 判定缓存不存在、走重建，而重建在那个视图里也写不成。**连锁在于重建第一步是
`Remove-Item -Recurse -Force $target`，`publish.ok` 与 `publish` 一起删**——一次失败的重建会让后面每一次都
失败。合成场景不受影响，因为它们没有真对端、根本不读那个缓存；工作树里的写是可见的，受影响的是
`AppData\Local` 这类路径。

## 怎么用

服务端补丁在控制服务端的 worktree 里 `git apply`，跑完 `git apply -R` 还原，**还原后核 `git diff` 为空**。
不要用 `git checkout --` 还原：同一个文件里有本票未提交的改动，那样会一并抹掉。

车载端那一份已经是一个本地提交（`2525b131d6404207e96c0c7a4fcb5c7068ea6719`，基于
`08569c4f2f83a8f13b734d161055351c2621537a`），跑那一次时把 `-OnboardRepository` 指到那个 worktree。
