# control-server#203 的真装置运行

> **先说两件事，免得看错方向。**
>
> **`cs203-rig-01-nested-closure-red` 与 `cs203-rig-sql-column-red` 不是产品红。** 两份都是本票判据代码自己的
> 错误——前者是 `.GetNewClosure()` 写在闭包内部导致探针拿空需求号去查，后者是 `SELECT` 了一个
> `JourneyBacklog` 根本没有的列。服务端在这两次里没有任何问题。留着是因为它们各自是一个值得记的失效形状，
> 见下面「两次自伤」。
>
> **`cs203-i6-restored-blocked-by-guards` 也不是产品红**，那是票面原本要的缺陷**造不出来**的记录，
> 见 `cs203-defect-patches/README.md`。

## 三端提交

| 端 | 提交 |
| --- | --- |
| 服务端 | `b7-203/g3-assertion-tightening`，见每份证据的 `identity.controlServerCommit` |
| 车载端 | `08569c4f2f83a8f13b734d161055351c2621537a`（`origin/w2g/fp-v2-impl` 顶端） |
| 模拟器 | `fb5f7c593742bf98bc3957b8729a38aad5321f28`（`origin/main` 顶端） |
| 车载端（仅 `G3-10-07` 那次） | `2525b131d6404207e96c0c7a4fcb5c7068ea6719`，**本地 detached worktree，不推** |

三端提交从**每次运行自己的 `identity`** 读，不从这张表读——这张表说的是计划，`identity` 说的是实际发生的事。
本票新加的本机真装置台账（`%LOCALAPPDATA%\8005-l2\rig-runs.log`）记的是同一组值，两边对得上。

## 绿的七个

判据收紧之后，七个场景在真装置上各跑一遍，全部 PASS。逐条核过的关键判据：

| 证据目录 | 关键判据与实际值 |
| --- | --- |
| `cs203-rig-green-g3-task-type-admission-fail-closed` | `G3-10-04`：原因观测于 17:50:34，最后一份快照 17:51:10，**对照 `FirstSeenAt` = 17:51:14**；`G3-10-06`：计划已确认、其后业务快照 ack 2 份、未确认 0 份 |
| `cs203-rig-green-g3-reversed-direction-journey` | `G3-11-07`：路线证据 `MAPCAT-17825775…` 等于按计划方向重算的值，反向重算是另一个；`G3-11-03`：全部 ack、无作废 |
| `cs203-rig-green-g3-pickup-load-and-correction` | `G3-02-05`／`-06`／`-13` 全绿，13 条判据全过 |
| `cs203-rig-green-g3-exception-compensate` | `G3-07-26`：`TO_PICKUP=CONFIRMED`、无停摆原因码 |
| `cs203-rig-green-g3-fault-cargo-handoff` | `G3-07-36`，同一个共用函数 |
| `cs203-rig-green-g3-load-cancellation` | `G3-02-28`，同一个共用函数 |
| `cs203-rig-green-real-onboard-load-door-closed-empty-reopens` | `L2-DC-12`，本票起改为直接调共用函数 |

**`FirstSeenAt` 那一栏是这批绿证据里最有信息量的一格**：它比最后一份快照还晚 4 秒。票面把它列为条目 1 的
候选下界，照做的话「原因记下之后的快照 ≥ 1 份」要找 17:51:14 之后的快照——一份都没有，**判据必然红**。

## 红的八个

每一份都核了三件事：**哪一项让它红、哪些合取项这一次没被触及、有没有连带红**。第二件是这批票的新动作——
它产出的是一份「还欠什么」的清单，而不只是防止误判。

| 证据目录 | 注入 | 红 | 是哪一项让它红的 | 这次没被触及的 |
| --- | --- | --- | --- | --- |
| `cs203-rig-red-item1-reason-leaks` | 准入原因泄露进 `blockingFacts` | 只 `G3-10-04`（9 条中） | 「含准入原因 0 份」→ 实际 **2 份** | 时序前提那一项**是满足的**，这次没验到它 |
| `cs203-rig-red-item2-route-ends-swapped` | 只把喂给哈希的两端反过来 | 只 `G3-11-07`（9 条中） | 路线证据相等那一项 → 实际值**正是期望栏里写的「反向重算」那个 id** | 两端记录、第一张单目的站仍然正确——**这正是要证明的**：旧的「非空」看不见它 |
| `cs203-rig-red-item5-g3-10-07` | 车载端从计划腿猜任务类型 | 只 `G3-10-07`（9 条中） | `TaskType` 为空那一项 → 实际 `「焊线→质检关卡」` | `StopDirection` 仍正确，没连带到 `G3-10-08` |
| `cs203-rig-red-item5-g3-11-08` | 卸货那次不带准入身份 | 只 `G3-11-08`（9 条中） | 「卸货那次有一行」→ 实际 **0 行** | `$onLoad.Count -eq 0` 是满足的——**那一项注入不了**，见 `cs203-defect-patches/README.md` |
| `cs203-rig-red-item7-old-sampling` | 装货后阶段滞留（判据用**旧**取值点） | `G3-02-05` + 六条 not reached | `SlotOperationCommand` 的 ack → 实际 `ack=False`，**命令还没被确认就读了** | 见下 |
| `cs203-rig-green-item7-new-sampling` | 同一个缺陷，判据用**新**取值点 | 无，**PASS** | — | 这是修复的另一半 |
| `cs203-rig-red-item8-compensate` | 下一单必然占车冲突 | 只 `G3-07-26`（6 条中） | 「没有停摆原因码」→ 实际 `Blocked/VEHICLE_OCCUPANCY_CONFLICT` | 占用释放标记仍然写了、阶段仍是 `AwaitingPickupArrival`——**旧写法读的就是这两样** |
| `cs203-rig-red-item8-door-closed` | 同上 | 只 `L2-DC-12`（12 条中） | 同上 | 同上 |

最后两行是条目 8 那个假绿最直接的证据：**旧写法读的两样东西在缺陷下完全正常**，红只出现在新加的两项上。

### 条目 7 那一对：修复证到了一半，另一半是票面的偏差

**一、`G3-02-06` 在这个缺陷下仍然绿。** 它的四个合取项（操作 Committed、结果一份 COMPLETED、需求 Accepted、
阶段 `-ne 'Blocked'`）在取早了的那一刻**全都成立**——`AwaitingSublot` 也不是 `Blocked`。所以它**对取样时机
天然不敏感**，票面把它与 `G3-02-05` 并列进条目 7 并不准确。取值点修好后它读到的阶段更准确，但**判据本身
没有变严**，这次也就没证明它有判别力。

**二、六条 not reached（`G3-02-08` 到 `-13`）是可解释的连带**：实际值栏写着「服务端拒绝了二次提交装载的
请求」——取早了之后场景后续动作的时序乱了，修正流程走不下去。它恰好说明那个取值点取早了不只影响一条判据。

**注入位置错过一次。** 第一版把延迟加在装货 `Committed` 之后、`SettleAnsweredCommand` 之前——那时阶段已经是
`AwaitingLoadResult`，旧写法 `-ne 'AwaitingLoadResult'` 会**继续等**，于是旧写法跑出了 PASS。**期望红的跑成
绿，说明的是「这个注入证不了假绿」，不是「假绿不存在」。**

## 留了哪几份，为什么

证据是在第一次提交之前挑的，不是推上去再删——blob 进了历史就省不掉了。

- **绿运行**只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`。它们要回答的是「这条判据在装置上还过不过」，
  而那个答案在判据表里；日志说不出更多。
- **红运行**同样三份，**日志只在红的原因不在 expected／actual 两栏里时才留**。
  `cs203-i6-restored-blocked-by-guards` 是唯一那种情况：判据表只说「跑了 3 条就结束」，真因
  （存储层守卫抛异常）只在 `control-server.out.log` 里。
- 十五份完整证据约 27 MB，这样挑完是 11 MB 左右，其中一多半是那一份带日志的。

## CI 点不动 `g3-*`，这一条写下来

`l2.yml` 的 `$scenarios` 数组里 **`g3-` 开头的场景一个都没有**，只有 `real-onboard-*` 和合成场景在里面。
点一个不在数组里的场景，结果是**一分钟的 failure、场景一条都不启动**。

**场景文件在 `scripts/l2/scenarios/` 下存在，只说明它能在本机被 `Invoke-L2Scenario.ps1` 跑起来**，与
「CI 点不点得动」是两件独立的事，后者只由那个数组决定——**读数组，不要从文件存在或名字像不像推**。

本票原计划把七个绿里的六个放到 CI 上跑以省本机时段，照这个计划会全部白跑。同一天这条被三个会话各撞一次，
所以它从「某人踩过」写成这里的一段。

## 两次自伤，和一次环境

见 `cs203-defect-patches/README.md` 与 PR 正文。三件事共一条：**本机三道检查（语法、编译、格式）都看不见
的东西，只能靠真装置暴露**，而真装置时段是排他的。
