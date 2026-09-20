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

<!-- 跑完补：每份的「哪一项让它红的 / 哪些合取项没被触及 / 有没有连带红」 -->

## 留了哪几份，为什么

证据是在第一次提交之前挑的，不是推上去再删——blob 进了历史就省不掉了。

- **绿运行**只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`。它们要回答的是「这条判据在装置上还过不过」，
  而那个答案在判据表里；日志说不出更多。
- **红运行**同样三份，**日志只在红的原因不在 expected／actual 两栏里时才留**。
  `cs203-i6-restored-blocked-by-guards` 是唯一那种情况：判据表只说「跑了 3 条就结束」，真因
  （存储层守卫抛异常）只在 `control-server.out.log` 里。
- 十五份完整证据约 27 MB，这样挑完是 11 MB 左右，其中一多半是那一份带日志的。

## 两次自伤，和一次环境

见 `cs203-defect-patches/README.md` 与 PR 正文。三件事共一条：**本机三道检查（语法、编译、格式）都看不见
的东西，只能靠真装置暴露**，而真装置时段是排他的。
