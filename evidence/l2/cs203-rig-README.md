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
| `cs203-rig-red-item8-compensate` | 下一单必然占车冲突 | 只 `G3-07-26`（6 条中） | 「没有停摆原因码」→ 实际 `Blocked/VEHICLE_OCCUPANCY_CONFLICT` | 占用释放标记仍然写了；`TO_PICKUP=CONFIRMED` 也成立，**那一项对这个缺陷判别力为零**（见下） |
| `cs203-rig-red-item8-door-closed` | 同上 | 只 `L2-DC-12`（12 条中） | 同上 | 同上 |

### 更正：条目 8 的机理我写反了，而这份红证据自己反证了它

上面两行原先写着「阶段仍是 `AwaitingPickupArrival`——旧写法读的就是这两样」，并据此说这是假绿的直接证据。
**那是错的，而且被它自己引用的那份 `assertions.json` 否掉**：实际值是

```
VehicleOccupancyReleasedAt='…' / 下一单 Blocked/VEHICLE_OCCUPANCY_CONFLICT TO_PICKUP=CONFIRMED on AGV-L2-001
```

**阶段是 `Blocked`，不是 `AwaitingPickupArrival`。** 逐行实读服务端之后，真实顺序是：

1. `DispatchRoundRunner.cs:499` → `AcceptAndDispatchToPickupAsync`：先 `AcceptJourneyAsync` 写 JourneyRuntimes
   （阶段 `AwaitingPickupArrival`），**紧接着** `ReconcileOrCreateAsync` 建单并把 TO_PICKUP 置 `CONFIRMED`；
2. `:548` `TryClaimVehicleOccupancyAsync` 在**这之后**，失败才 `Block(...)`（`:712` 把 Stage 设为 `Blocked`）；
3. `:560` 另一条分支：建单没到 `Confirmed` 时只 `SetBlockReason(...)`，**不改 Stage**。

**由此三件事要更正：**

- **`CONFIRMED` 对占车冲突判别力为零**——冲突发生时它早就是 CONFIRMED 了。红是「没有停摆原因码」那一项判出来的。
- **「旧写法在这个缺陷下会绿」是未经测量的断言。** 那次运行跑的是新代码，**从未观测过旧判据**；而阶段是
  `Blocked`，旧判据的阶段项在这一刻同样不成立，所以它更可能继续等或超时红。要坐实必须拿旧脚本对着
  `item8-vehicle-occupancy-always-conflicts.patch` 再跑一次——**本票没有这一份**。
- **条目 8 真正抓住的错误实现是第 3 条分支**，不是我注入的那个：一个「释放了车、却没能给下一单建成／确认
  RIoT 单」的服务端，落库是 `AwaitingPickupArrival` + `PICKUP_DISPATCH_NOT_CONFIRMED`——**旧写法只看阶段与
  AgvId，会绿**，而新加的 `-not (Test-G3Present $next.BlockReasonCode)` 会红。**收紧是真的，但我注入的缺陷
  证的不是它。**

写错的那条机理也进了 `G3RecoveryCommon.ps1` 的注释，已一并改正。**一条错的注释比没有注释更贵**——它会被
下一个人当成关于服务端的既定事实读走。

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

## 条目 3 那个等待：从未生效的保险，和它的前提

票面要求「贴 `timeline.jsonl` 里等过的那一段」。`cs203-rig-green-g3-reversed-direction-journey` 里这两个
等待总共只有两条观测：

```json
{"at": "2026-09-20T17:52:19.7913086+00:00", "criterion": "origin-worklist-acknowledged", "value": "1"}
{"at": "2026-09-20T17:52:55.9562928+00:00", "criterion": "destination-worklist-acknowledged", "value": "2"}
```

**两个等待都是第一次探测就满足的——一次都没有真的等。** 探针跑在 `-WhileWaiting` 回调里，而那个回调要等
服务端发出装卸命令、车载端报出 `WAITING_OPERATOR` 之后才触发，那时清单早就确认过了。**所以它是一份到
目前为止从未生效的保险。** 它仍然要有，是因为停靠行显示的是车载端**应用了的那份清单**——「清单已确认」
是这条读数的前提而不是结论，不等就读可能读到上一站的行或一个还没更新的旧值，**而判据分辨不出这两种情况**。

**前提钉在这里**：门槛是按条数写的（已确认的 `CurrentStopWorklistSnapshot` ≥ 2），而条数是「第二站那份
清单已确认」的**代理指标**，不是它本身。这个代理今天成立，靠的是「这两个场景里每站恰好一份清单」。
**第一站的清单一旦改版，条数会在第二站确认之前就满到 2，保险在它唯一该生效的场合失效。** 去掉这个前提
要把判据换成「存在一份已确认的清单，其 `StationId` 不同于第一份的」——同一来源的值，不跨命名空间。

**本票没有改成那样**，改动归 [#265](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/265)：它今天从未生效，这两个场景里也没有清单改版；而本票已经两次因为「推理上等价」栽过
跟头，在一次重跑之前再加一处只有推理支撑的改动不划算。**前提有了承担者，本票不为它冒险。**

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

## 条目 4 在 PR 之后又改了一次，原因是它原本不生效

**这一节记的是本票交付之后自查出来的缺陷，改动在 `L2TaskTypeJourney.psm1` 与新增的
`scripts/l2/Test-L2ProbeClosureResolvable.ps1`。**

条目 4 要求「第二腿意图不止一条时抛错说明，不要悄悄取第一条」。第一版把那个 `throw` 写进了
`Wait-L2Condition` 的 `-Probe` 里。**那个位置上它不生效**：轮询体是

```
try { $last = & $Probe } catch { $last = $null }     # L2.psm1:24
```

探针里抛出的一切异常都被吞掉。所以真出现第二条意图时，读证据的人看到的不是那句解释，而是 120 秒后的
`Timed out after 120s waiting for: the second leg's intent was confirmed`——**读起来像服务端没确认意图**，
而查清它要再烧一次真装置机时。那个 `catch` 是所有场景共用的、必要的瞬时错误容错，不能为这里改。

改法是把条数检查挪到等待之外，两条路径都查：等到了要查（第一条被确认、第二条也在），等不到也要查
（只有排在后面那条被确认时，探针一直返回 `$null`，走的是超时那条路径）。

### 改这一下又踩了第二个坑，它比第一个隐蔽

把查询提成模块内的 `Get-L2SecondLegIntents` 之后，**探针调不到它**。实测的规则是：

| 情形 | 闭包里能否解析 |
| --- | --- |
| 模块内**未导出**的函数 | **不能** |
| 模块内**已导出**的函数 | 能 |
| `.ps1` 脚本里定义的函数（即使闭包由另一个模块的函数调用） | 能 |

`.GetNewClosure()` 之后的 scriptblock 按**全局**函数表解析命令名，不按它所在的模块。原来那版探针里调的
`Invoke-L2Query` 是 `L2.psm1` 导出的，所以一直没事；换成模块私有函数就断了。**而它断的样子正是上面那个
——被吞掉，再以超时的面目出现，缺的那个函数名一个字都不出现。** 所以 `Get-L2SecondLegIntents` 是导出的，
那不是整洁，那是它能被调用的前提，源码里写了这句。

### 补的守卫，以及它的反向验证

`scripts/l2/Test-L2ProbeClosureResolvable.ps1`（约 3.4 秒，已进 `test.yml`）分两半：

1. **先把规则重新量一遍**：生成两个只差 `Export-ModuleMember` 一个名字的模块，真导入、真经
   `Wait-L2Condition` 驱动，要求私有那个失败**且失败成一句不含函数名的超时**，导出那个成功。
   哪天 PowerShell 改了这个行为，它报的是一条发现，不是悄悄变绿。
2. **再扫 `scripts/l2` 下全部 17 个模块**，找「闭包里调用同模块未导出函数」。同时扫一个**故意写坏的
   夹具**——一个报零的扫描器和一个坏掉的扫描器从外面看是一样的。

反向验证是在**真文件**上做的，不只是夹具：把 `Get-L2SecondLegIntents` 从导出清单里撤掉，扫描器报
`L2TaskTypeJourney.psm1:279 -> Get-L2SecondLegIntents`，正是那一行、那个名字。

### 真装置证据与这次改动的关系，说清楚

**上面那批绿证据是在这次改动之前取的，没有重跑。** 改动对已经跑过的那条路径是等价的：探针换成调用
同一段 SQL（逐字对照过，含拼接处的空格），判据不变，多出来的只有等待结束后一次只读 SELECT。
**但"推理上等价"正是这一夜两次栽跟头的地方**，所以这句话按原样写在这里。

重跑 `g3-task-type-admission-fail-closed` 与 `g3-reversed-direction-journey` 两个绿场景约 3 分钟机时，
**它覆盖的是探针改走 `Get-L2SecondLegIntents` 那条跨模块解析路径，以及等待成功之后新增的那次检查调用**
——都在成功路径上，每个场景都会走到。**它覆盖不了失败路径**（见下一节），那条绿场景到不了。
写清楚覆盖了什么，是因为「重跑过了」这四个字会被读成「这次改动验过了」。

## 再往下一层：失败路径那次读，会把失败本身顶替掉

**调度读到「等不到也要查」这句，问它在真装置上跑过没有。答案是没有——而且重跑绿场景也到不了那里**，
`catch` 只在等待失败时走，绿场景的等待是成功的。顺着这个问题去看那条分支，发现它自己有缺陷：

```powershell
} catch {
    & $assertOneSecondLeg (Get-L2SecondLegIntents ...)   # ← 这次读抛了，就顶替掉原来的超时
    throw
}
```

**等待超时最常见的原因之一就是服务端不在了**，而那时这次读会抛 SQLite 错误。抛出去就顶替了
「等待第二腿意图确认超时」那句话——**那是唯一说明"在等什么"的一句**。这正是本节上面要修掉的那个形状
（失败被压成另一副样子），我在修它的过程中自己又写了一遍。

改法：那次读单独包一层 `try/catch`，读失败就当没读到，原来的异常照常抛出去。**允许顶替的只有一种情况，
就是断言真的发现了第二条意图**——那句话比超时更能说明问题。

### 这段逻辑提成了独立函数，因为要让提交进仓的测试直接驱动它

`Wait-L2SecondLegIntent`（已导出）+ `scripts/l2/Test-L2SecondLegIntentWait.ps1`（6.3 秒，已进 `test.yml`）。
**测试驱动的是真函数**，只把 `Invoke-L2Query` 换掉，而且是换在 `L2TaskTypeJourney` 自己的模块作用域里——
全局桩不生效，因为那个模块自己 `Import-Module L2.psm1`，它自己的作用域优先。

**为什么不写一份结构镜像：上一节那个坑就是镜像造成的。**第一版镜像把探针建在模块外面再传进去，
于是「跨模块解析」这件事根本没发生，**在坏代码上跑出了绿**。测试的结构必须忠实于被测的结构，
否则它测的是另一个东西。

八条判据，三处反向验证，**每处只红一条且都是对的那一条**：

| 变异 | 红的那一条 | 实际值 |
| --- | --- | --- |
| 去掉失败路径那次读的内层 `try/catch` | 「失败路径的读不得顶替失败」 | `throws other: the database went away` |
| 整个失败路径的检查删掉（只 `throw`） | 「两条意图、只有后一条被确认」 | `throws timeout`（而非 `throws multi`） |
| 去掉 `ORDER BY` | 「查询按 UpperId 排序」 | SQL 里没有 `ORDER BY` |

**今天每趟只有一条第二腿意图，所以除第一条外的每一种情形，真装置从来没有处在过**；其中「等不到也查」
那条连重跑绿场景都到不了——`catch` 只在等待失败时走，而绿场景的等待是成功的。它们的判别力来自这八条，
不来自真装置——这句写在这里，免得读者以为真装置覆盖了它们。

**那为什么要为一条今天走不到的路径写三处反向验证？**因为它走不到的原因是「每趟只有一条第二腿意图」，
**所以它被走到的那一天，本身就是一个需要解释的异常**。它的价值不在防止误判——今天没有误判可防——
而在**那一天到来时，证据里有一句话能指路**：`Demand X has 2 non-TO_PICKUP order intents (…)`，
而不是一个 120 秒的超时，加一次真装置机时去查它是什么意思。

## 重跑（`a34495b6`）：两个绿场景，覆盖什么说清楚

改动之后重跑了两个绿场景，**各 9 条判据、零红**。

| 行 | 值 |
| --- | --- |
| 服务端 | `a34495b609bb9869a63adb9fbd69858212666932` |
| 车载端 | `08569c4f2f83a8f13b734d161055351c2621537a`，跑前 `ls-remote` 核过**等于 `origin/w2g/fp-v2-impl` 顶端** |
| 模拟器 | `fb5f7c593742bf98bc3957b8729a38aad5321f28`，同上**等于 `origin/main` 顶端** |
| 这遍真跑了 | 台账由 52 行涨到 56 行，两次运行各一对配对的 `start`／`end`；机时 3m05s + 55.7s |

**它覆盖的是**：探针改走 `Get-L2SecondLegIntents` 那条跨模块解析路径，以及等待成功之后新增的那次检查调用
——都在成功路径上，每个场景都会走到。**它覆盖不了失败路径**（`catch` 分支），绿场景到不了那里；那条的
判别力来自 `Test-L2SecondLegIntentWait.ps1` 的八条判据与三处反向验证。

### 更正：上一段我写错过一句，`5b655a9a` 的提交信息里还留着那句错话

原先这里写「`assertions.json` 的 `identity` 里只有 `controlServerCommit`，没有两个对端，所以三端核对
只能从台账读」。**那是错的。** 实读 `identity` 的全部字段：

```
['protocolReleaseIdentity', 'stageRoot', 'slotsSimulatorCommit', 'agvId', 'batchId',
 'rig', 'vehicleKey', 'controlServerCommit', 'onboardHmiCommit']
```

**三端全在**，`SUMMARY.md` 的表格里也有两端。我的脚本按**猜的字段名**（`onboardCommit`／`simulatorCommit`）
去取，取到空就断定「没有」——**「工具没说不等于事实没有」的第三种形状：取字段取出空值，而空值和「没有这个
字段」在脚本里长得一模一样，连报错都没有。取字段之前先把键列出来。**

`5b655a9a` 的提交信息里有这句错话。**提交信息没有改**——改它要 force push，那是要先问用户的四类之一，
不为一句话动远端历史。以本节为准。

### 那么台账独有的是什么

三端从 `identity` 就读得到，**台账独有的是第四行：这一遍真的跑了，以及跨运行的账**——`start`／`end` 配对、
时长、总行数 52 → 56。单个证据目录证不了这件事，因为它是自证的：**它存在就说明它跑过，而它可能是上一次
留下的、可能被覆盖、也可能是手工拼的。**

那 4 行随证据入库在 `cs203-rig-rerun-run-ledger.jsonl`（台账本身不是仓库跟踪文件，它在
`%LOCALAPPDATA%\8005-l2\rig-runs.log`，可用 `W2G_L2_RUN_LEDGER` 覆盖）。**它证的是「这两次运行发生过、
各跑了多久、三端是什么」，不证任何判据的对错。**

### 读台账时撞回一个老问题，记在这里

第一次读台账用的是 Bash→python，报 `FileNotFoundError`——**而文件就在那里**，换 Bash→pwsh 直接读就有了
（`Test-Path` 为 True，56 行）。**这就是取红证据那晚量到的进程层可见性差异**，那次撞的是写这一侧，
这次是读这一侧。

**值得记的是它的失败样子：`FileNotFoundError` 与「文件真的不存在」一模一样。** 所以 `%LOCALAPPDATA%` 下
的东西报「不存在」时，先换 pwsh 读一遍再下结论。

## 验收标准里两项「贴进 PR」的东西

**台账并发自检**（两个进程各追加 300 行）：

```
ok    two processes appending at once lose no line and break no line -> 600 line(s) of 600, 0 unparsable, 0 malformed, A=300 B=300, sequences intact=True
ok    a line carrying a line break is refused rather than written -> threw: A ledger line cannot contain a line break; one record is one line. / 0 line(s) on disk
ok    the default path is under LOCALAPPDATA and outside any checkout -> default 'C:\Users\szy\AppData\Local\8005-l2\rig-runs.log' / override honoured=True
L2RunLedger self-check: all 3 cases as expected.
```

**`Test-G3RunnerClaims.ps1`**（1.0 秒，退出 0）：

```
JOURNEY_G3_REAL_ONBOARD_SIMULATED_COUNTERPARTS: 114 reported, run-wide 8; FP-IS-01 10, FP-IS-02 34, FP-IS-03 12, FP-IS-07 32, FP-IS-10 9, FP-IS-11 9
G3_CLAIMS_OK: every runner's reported names are attributed exactly once, and every attributed name is reported.
```

`FP-IS-10` 9 条、`FP-IS-11` 9 条，正是本票这两个场景。**这一条我一开始没跑**——它是按「票面交付要求逐条
对一遍」这个动作查出来的，不是想起来的。
