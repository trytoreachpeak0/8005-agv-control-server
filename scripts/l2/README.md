# L2 场景编排器

把「必须有人站在真车前面」的验证变成一条命令。

```powershell
pwsh .\scripts\l2\Invoke-L2Scenario.ps1 -Scenario normal-load -EvidenceRoot .\evidence\l2\<新目录>
```

一趟 15 到 35 秒，全程无人值守。这条链路 2026-09-03 在厂区里跑掉了一个下午。

方案与落地顺序见
[`8005-agv-program/docs/wire-to-gate-test-automation.md`](https://github.com/trytoreachpeak0/8005-agv-program/blob/main/docs/wire-to-gate-test-automation.md)，
这里是它第 3 节「缺口 2」「缺口 4」和落地顺序第 3、4、5 步的产物。

## 现有场景

| 场景 | 车载端 | 讲什么 | 绿证据 |
| --- | --- | --- | --- |
| `normal-load` | 合成 | 全程顺利的基线，不注入任何故障 | `evidence/l2/20260903-normal-load-016` |
| `session-established-while-moving` | 合成 | 会话在车辆运动中建立，随后停稳；到站也要按最新的安全状态判 | `evidence/l2/20260903-session-established-while-moving-006` |
| `load-result-requires-recovery` | 合成 | 装载跑掉操作员超时，旅程与整台车正确停摆 | `evidence/l2/20260903-load-result-requires-recovery-006` |
| `load-cancelled-before-sublot` | 合成 | 到站发现没货，操作员在扫码前取消，车接下一单 | `evidence/l2/20260908-load-cancelled-before-sublot-002` |
| `load-cancelled-in-flight` | 合成 | 装货命令已下发、门已开着时取消：这一单终结，这个停靠不终结 | `evidence/l2/20260909-load-cancelled-in-flight-002` |
| `sublot-wait-timeout` | 合成 | 到站没人扫码，等待窗口到期自己终结，下一单照常跑完 | `evidence/l2/20260908-sublot-wait-timeout-001` |
| `auto-charge-endurance` | 合成 | 一趟串四幕：送完一单、低电自去充电、充满、再送一单 | `evidence/l2/20260908-auto-charge-endurance-007` |
| `multi-demand-one-stop` | 合成 | 一个停靠上多张单，作业清单是复数的 | `evidence/l2/20260908-multi-demand-one-stop-001` |
| `load-command-never-answered` | 合成 | 装载指令石沉大海，服务端重发而不改口 | `evidence/l2/20260908-regression-load-command-never-answered-001` |
| `real-onboard-normal-load` | **真的** | 同一条链路，但条码走 UIA、装卸走真 Modbus | `evidence/l2/20260903-real-onboard-normal-load-005` |
| `real-onboard-clock-skew` | **真的** | 车载端时钟偏差的有界容差，界内、界外、恢复三段 | `evidence/l2/20260903-real-onboard-clock-skew-007` |
| `real-onboard-recovery-entry-missing` | **真的** | 装载失败后车上发起不了任何恢复：授权是齐的，入口是缺的 | **已被 ADR-cross-0058 决策 1 作废**，见下 |
| `real-onboard-load-door-closed-empty` | **真的** | 装货时关门不放料：反复重开、不判失败、不进恢复；提示节拍到期只再提示不重复脉冲 | `evidence/l2/20260909-real-onboard-load-door-closed-empty-002` |
| `real-onboard-unload-not-emptied` | **真的** | 卸货时关门不取货：一直闭环到取空，没有取消分支 | `evidence/l2/20260909-real-onboard-unload-not-emptied-001` |
| `real-onboard-station-timeout-door-open` | **真的** | 站点期限到期而仓门未闭：告警并持续等待，闭合后按决策 5 结算 | `evidence/l2/20260909-real-onboard-station-timeout-door-open-004` |

编号更小的目录是同一批里更早的跑次，多数是稳定性复跑。八个是**红的**，各自的原因见文末：
`load-result-requires-recovery-001`（第 6 条）、`real-onboard-clock-skew-001`（第 8 条）、
`-004`（第 9 条）、`real-onboard-load-door-closed-empty-001`（第 13 条）、
`load-cancelled-in-flight-001`（第 14 条），以及
`real-onboard-station-timeout-door-open` 的 `-001`/`-002`/`-003`（最后一节）。**后五个都红在场景
自己身上，不是产品**——`real-onboard-station-timeout-door-open-001` 那一条连诊断都跟着错了一半。

方案第 4 节标 ★ 的三条**现在三条都有了**。第三条（车载端时钟偏差）走了最远：合成对端里根本没有
`VehicleSafetySignal.IsFresh` 那段逻辑，真车载端接进来之后逻辑在跑了，但两端同机共用一个时钟，
偏差不会自己出现——补上 `tools/ControlServer.ClockSkewProxy` 才凑齐。

而且它不再是「复现缺陷」：
[`8005-agv-onboard-hmi#1`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/1)
已由 Kun Wang 在 `abb8e73` 修成**有界容差**，所以场景钉的是那个界的两侧加恢复。

`load-result-requires-recovery` **到 Blocked 为止，不跑到「恢复并继续」**：出口是五步恢复握手，
而合成对端不发起它。给它编出那五条出站消息只会让这条场景变绿而证不出任何新东西。

> **下面这三段描述的是 2026-09-09 之前的它。**ADR-cross-0058 决策 1 落地后，它制造失败的手法
> ——关门但不放货——已经不再产出任何结果，整条场景卡在 `AwaitingLoadResult`（见第 11 条与
> `evidence/l2/20260909-recovery-entry-missing-after-target-state-loop-001`）。**在重新指向一个
> 真的 UNKNOWN 之前，它不再是一份有效证据。**保留原文，因为它记的那个入口缺口本身没有被修掉。

`real-onboard-recovery-entry-missing` 补的是那半条的**入口**，**它当时是红的，而且红得对**。前四条
判据全过——真车载端确实报回不完美的装载结果，服务端确实判 `RecoveryRequired` 并停摆，失败结果确实
作为该 attempt 唯一一份存活结果落库。第五条过不去：**停摆之后，车载端 HMI 上的恢复入口从来不出现**，
所以任何恢复动作都发起不了。

**这条场景 2026-09-04 改过向量，原来测的是 `RESUME_AFTER_REPAIR`，那是错的。**它制造的状态是「装载
跑完、门关着锁上了、仓位仍是空的」，对应 `COMPENSATE_LOAD_ALL_EMPTY`；而 `RESUME_AFTER_REPAIR` 要的
是「跑到一半没出结果、车辆还握着物理断点」。车辆记录结果时会把 attempt 与 `OperationContext` 一并
清空，也会拒绝 checkpoint 为 `ResultRecorded` 的恢复命令——**两端独立地做了同一判断**，服务端拒绝
resume 是设计不是缺陷。

**服务端没有洞。**同一状态下 `COMPENSATE_LOAD_ALL_EMPTY` 是被授权的，有 L1 为证：
`RecoveryStateMachineG2Tests.AfterARefusedResultResumeIsRefusedButCompensationIsAuthorized`。缺的是
**入口**：车载端按「会话进入 `RecoveryRequired`」显示恢复入口，而 `DecideReadinessAsync` 不看
`StationOperationStatus.RecoveryRequired`，于是会话停在 `Ready`。完整复盘（含三轮归因里错的那两轮）
在 `docs/defects/20260904-recovery-required-never-reaches-session-state.md`。红证据：
`evidence/l2/20260904-real-onboard-resume-after-repair-f0465d9-001`（改名前的最后一次运行）。

## CI 只跑合成场景

`.github/workflows/l2.yml`，跑在本仓自己的 `headless` runner 上，每次 push 与 PR。现在是八条，
证据当作 artifact 传上去（失败时也传——失败那次的证据才是唯一说明原因的东西）。

**真装置那三条刻意不进 CI，两个各自独立的原因：**

1. 它们要交互式桌面会话（会弹两个 WPF 窗口），session 0 的服务模式 runner 根本跑不了。
2. 改挂到交互式的 `golden-renderer` runner 也不行——那会破坏桌面独占。GitHub 的 `concurrency`
   只在单个仓库内生效，所以这里的作业没办法和 `8005-mes-ingest` 的桌面测试在同一台机器上排队，
   而那台机器同时是黄金渲染机。**跨仓库桌面互斥目前没有解**，见工作区根 `CLAUDE.md`。

合成场景不需要对方两个只读仓：`Get-L2PeerPublish` 只在场景 setup 写了 `Onboard = 'Real'` 时才调用。
所以这条流水线不受对方进度影响。

**新写的合成场景记得加进 `l2.yml` 的清单**——那是一份手写数组，不是扫目录得来的。扫目录会把真装置
那几条也一起领进来，而它们在服务 runner 上跑不了。


## 三条新场景踩出来的坑

`load-cancelled-before-sublot`、`sublot-wait-timeout` 与 `auto-charge-endurance` 是 2026-09-08
补的，对应「到站没货」的两条出口和自动充电。写它们的过程里翻出四个坑，三个在被测方，一个在
判据本身，都值得记下来。

1. **到站判定读时钟要在读观测之后。**`IsTrustedChargerArrivalAsync` 一开始拿的是调用 RIoT
   之前算好的 `now`，而 RIoT 是在应答的那一刻盖 `observedAt`。于是「车不能报告未来」这条守卫
   每一轮都命中，充电行程永远停在 `AwaitingChargerArrival`。红证据
   `20260908-auto-charge-endurance-002`。`IsTrustedArrivalAsync` 一直是读完再取钟的，照抄就对。
2. **改地图只能增，不能换。**准入策略把 `admissionPolicyVersion` 绑在按区号解析出的取货站点
   集合上。场景一开始直接写了一张新站点表（少了 `11 = C15-13`），同一个版本号绑到不同内容，
   `ApplyAdmissionPolicyAsync` 每轮抛 `BusinessIdentityConflictException`，运行时整个停摆——
   而日志只说准入策略，不说地图。红证据 `20260908-auto-charge-endurance-001`。现在的写法是读
   出现有站点再追加充电桩。
3. **合成对端的应答缓存过去只能记住一份。**`SublotEntryRequested` 与 `PreDepartureSafetyCheck`
   的缓存键写死成 `sublot` 和 `safety-check`。那个缓存存在的理由是「重放的请求要拿到一模一样
   的回复」，可键不带业务身份时，第二趟旅程的请求会拿到第一趟的回复——服务端当然拒绝一份指名
   另一个需求的证据。**一个会话因此只能装一次货**，第二趟卡在 `AwaitingSublot`（证据 004），
   修了条码之后又卡在 `AwaitingDepartureSafety`（证据 005）。两个键现在都带上了业务 id，与
   `operation:<attemptId>` 一致。**任何新的应答类型都照这个来。**
4. **SQLite 的可空列读回来是 `[System.DBNull]`，不是 `$null`。**判据里写成一行内联的
   `-and ... -or ...` 会被优先级拆错，跑出一条假红（`20260908-load-cancelled-before-sublot-001`）。
   三条场景现在都用同一个 `Test-L2Null` 辅助函数。

另外记一件不是坑、但会被误认成坑的事：**车在充电的那些轮次，`JourneyBacklog` 里什么都不会写。**
充电占用整轮，候选评估根本不跑——车在恢复线以下，每个候选都会被电量政策拒掉，而做出那个判断
要读 MesIngest、箱数与包装规格，全是远程调用。解释在 `AutoChargingRuns` 里：车正在充电，这就
是原因。`auto-charge-endurance` 的 `L2-AC-10` 就是钉这一条的。

## 两套装置

场景在自己的 `scenarios/<名字>.setup.psd1` 里写 `Onboard = 'Real'` 就换装置，命令行不变。

| | 合成车载端（默认） | `Onboard = 'Real'` |
| --- | --- | --- |
| ControlServer | ✅ 真进程、真 SQLite、真协议监听 | ✅ 同左 |
| RIoT | `tools/ControlServer.FakeRiot` | 同左 |
| MesIngest | `tools/ControlServer.FakeMesIngest` | 同左 |
| 车载端 | `tools/ControlServer.FakeOnboard`（合成协议对端） | ✅ `8005-agv-onboard-hmi` 的 `SQCD.Agv.Wpf`，UIA 驱动 |
| 仓位 IO | 无——放取货由合成对端应答 | ✅ `slots-simulator`，真 Modbus TCP |

**这两个是绑在一起的，不能只要车载端不要模拟器**：没有 Modbus，车载端握手时八个仓位全报
`UNKNOWN`（`CreateSlotStates` 里 `snapshot.IsConnected` 一票否决），`departureSafe` 恒为 false，
服务端永远不给会话就绪。

合成装置能证明的是**服务端在一个守协议的对端面前的跨端时序**，它没有 IO、没有 journal、没有
操作员，也没有会因为时钟偏差而拒绝自己观测值的本地新鲜度判定。真装置把这四样都换成真的，代价
是两个 WPF 窗口会弹到桌面上（见文末第 10 条）。

**L2 PASS 不代表现场合格。**没有真实 RCS、没有真车、没有交通管制、没有真实 IO 模块与接线。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。

## 真装置：两个只读仓怎么构建

`8005-agv-onboard-hmi` 与 `slots-simulator` 的**内容对 agent 只读**，包括构建会生成的东西，所以
两者都不在原地构建：`Get-L2PeerPublish` 把仓库克隆一份到 `%LOCALAPPDATA%\8005-l2-peers\`，在克隆
里 `dotnet publish`，按 commit 缓存。原仓工作树全程零改动，而且证据里记的 commit 就是实际发布的
那个。**源仓工作树不干净会直接报错**——克隆出来的是已提交的状态，和你眼前看到的不是一回事。

每次跑再把缓存的 publish 复制到本轮 stage 目录，并**只在副本里**改配置。两个程序都只从 exe 旁边
那一个 JSON 文件读配置，都不支持环境变量或命令行覆盖，所以改副本是唯一不碰只读仓的办法。

## 两条贯穿始终的规则

**不用 sleep 等任何东西。**每一次等待都是「判据 + 超时」，所以慢机器只是慢，不会变成偶发失败；
卡住的时候报的是「哪一条判据一直没成立」，而不是一个光秃秃的超时。

否定判据（「它**没有**做某件事」）也一样，用 `Wait-L2Iterations`：

```powershell
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
# 现在再断言「stage 没变」才有意义
```

它等的是假 RIoT 的 `mapStationReads`。`JourneyRuntimeEngine.ExecuteOnceAsync` 每一轮开头都读一次
Map 站点目录——**包括 journey 已经 Blocked、它什么都不做的那些轮**——所以这是唯一一个「运行时又
有机会了」的可观测量。少了它，「这台车不再受理任何新需求」就只能写成 sleep。

**不走捷径断言。**状态只从服务端自己的 SQLite 库、各替身的 `/control/v1/snapshot` 和模拟器的
`/api/v1/snapshot` 读——运维在现场看的就是这几处。绕过被测方摆出终态，测的就只是脚本自己。

真装置下这条更要守：**UI 只用来驱动，读到的唯一一件事是「现在允不允许录入」**，那是能不能打字的
前提，不是业务事实。「条码提交成功了吗」要从服务端 `ProtocolInbox` 里那条 `SublotSubmitted` 看，
不是从界面上的提示文案看。

## 产出

`-EvidenceRoot` **必须是不存在的目录**。红的证据不允许被绿的重跑覆盖（见仓库 `CLAUDE.md` 的
证据纪律）。一趟跑完留下：

| 文件 | 内容 |
| --- | --- |
| `SUMMARY.md` | 人看的结论与判据表 |
| `assertions.json` | 机器可读的判据结论 |
| `timeline.jsonl` | 一行一次判据翻转，只追加 |
| `logs/` | 每个组件的 stdout、stderr，以及构建日志 |
| `snapshots/` | 收尾时各控制面与六张关键表的快照 |

时间线的形状抄自 `remote-ops/status/Get-WireToGateStatus.ps1`——2026-09-03 定位缺陷时，就是靠
它把「12:56:49 STOPPED → 12:57:15 UNKNOWN」精确卡到秒。

跑失败时 stage root（临时 `controlserver.db` 所在处）**不会删**，路径打在告警里：那个库通常是
唯一写着原因的地方。

## 加一个场景

`scenarios/<名字>.ps1`，接一个 `-Context` 参数。`Context` 上有 `Journal`、`Assertions`、
`Riot`、`MesIngest`、`Onboard`、`Simulator`、`Connection`（只读 SQLite 连接）、`SnapshotRoot`、
`StopComponent` 以及车辆与站点的身份。

`Onboard` 在两套装置下**是两个不同的东西**：合成装置下是假车载端控制面的 `L2Double`，真装置下
是 UIA 驱动（`CanSubmit()` / `SetSublot()` / `SubmitReady()` / `Submit()`）。`Simulator` 只在真装置
下有。一个场景只为一套装置而写，所以这里不需要分支。

`normal-load` 是合成装置的基线，`real-onboard-normal-load` 是真装置的基线：全程顺利，不注入任何
故障。后面每个异常场景都只是在它上面改一处——把车载端某一类应答的策略从 `Auto` 改成 `Manual` 或
`Silent`，或者给假 RIoT 或模拟器注入一个故障模式，然后断言服务端**没有**做它不该做的事。

**要改环境启动方式的场景，写一个同名的 `scenarios/<名字>.setup.psd1`。**目前认两个键：

- `Onboard = 'Real'` —— 换成真车载端 + 真模拟器那套装置（默认 `'Synthetic'`）；
- `OnboardSeed` —— 只对合成装置有效，会变成 `--FakeOnboard:Seed:*`，落在握手那条
  `SafetyStateSnapshot` 携带的安全摘要上。`session-established-while-moving` 靠它让会话在
  「车还在动」的状态下建立——`PUT /control/v1/safety` 只能报告一个**已经存在**的会话的变化，
  做不到这件事。与 `Onboard = 'Real'` 一起给会直接报错。
- `ServerSettings` —— 直接落到 ControlServer 环境变量上的一组配置，键就是 `JourneyRuntime__*`
  这类环境变量名。给的是那些「主语是运行时策略而不是对端行为」的场景用的：出厂五分钟的条码
  等待窗口，或者一趟去充电桩的行程，在十几秒的运行里按出厂值根本穿不过去。
  `sublot-wait-timeout` 用它把窗口压到十秒，`auto-charge-endurance` 用它打开自动充电并给出
  充电桩身份，`load-cancelled-before-sublot` 反过来把窗口拉到十分钟——那条场景要证明终结来自
  操作员那一次取消，而不是窗口自己到期。
- `ClockSkewMs` —— 只对真装置有效。车辆安全投影改经 `tools/ControlServer.ClockSkewProxy` 转发，
  `observedAt` 往后推这么多毫秒，等价于车载端时钟慢了这么多。合成对端没有新鲜度判定，给它设这个
  键会直接报错。运行时还能通过代理的 `PUT /control/v1/skew` 改。

写成边车文件而不是命令行开关，是因为忘了传开关的那一次，场景会安安静静地证明另一回事。装置选错
更是如此：把 `real-onboard-*` 跑在合成对端上，它会绿，而绿的是完全另一件事。

**真装置下驱动条码只用 `SetSublot()` + `Submit()`，不注入按键。**`ValuePattern.SetValue` 和
「手动提交」按钮的 `InvokePattern` 都不需要窗口有焦点，所以跑的时候不跟操作员抢键盘，别的窗口
抢了焦点也不会失败。走 Enter 那条 `KeyBinding` 会命中 `ScannerSubmitCommand` 而不是
`ManualSubmitCommand`，两者最终都进 `WireToGateBusinessService.SubmitSublotAsync`，只差记录下来的
`entryMethod` 是 `SCANNER` 还是 `KEYBOARD`。

**要让某个组件下线，用 `& $Context.StopComponent 'fake-onboard'`。**装载出问题时车通常是关掉的，
这就是那一幕。收尾时的快照抓不到已经停掉的替身，所以停之前先把它的 `/snapshot` 自己存一份到
`$Context.SnapshotRoot`。

## 端口

| 组件 | 端口 |
| --- | --- |
| ControlServer 协议监听 | 58405 |
| ControlServer 健康检查 | 58407 |
| 假 RIoT | 58408 |
| 假 MesIngest | 58409 |
| 假车载端控制面 | 58410 |
| 模拟器 HTTP 控制面（真装置） | 58411 |
| 模拟器 Modbus TCP（真装置） | 58412 |
| 时钟偏差代理（`ClockSkewMs` 场景） | 58413 |

刻意避开现场运行（58105/58107）、staged G3（58205/58207）与 demand-bearing G3（58305/58307）：
撞上了要的是绑不上端口直接失败，而不是悄悄连到另一台服务器上去。模拟器同理不用它自己的默认
58006/1502——那两个是手工联调时开着的那一份，L2 不该连上去。

## 第一次跑出来的坑

都不是产品缺陷，是这套编排器自己的，记在这里省下一次重新踩：

1. **`/health/ready` 不能用来等服务端起来。**它的语义是「有对端完成了恢复握手」，而对端要等服务端
   监听才能连——用它做启动判据就是和自己死锁。等 `/health/live`，握手完成之后再把 `/health/ready`
   当成一条真正的判据。
2. **枚举在库里存的是名字不是序号。**`ControlServerDbContext` 对这些列全用了
   `HasConversion<string>`。按序号读会抛异常，而 `Wait-L2Condition` 里抛异常的探针和「还没到」
   长得一模一样——结果就是白等 90 秒，什么线索都没有。
3. **`area` 要填站点名里解析得出的区号，不是它的前缀。**`MapStationResolver` 把 `N1-3_N1-7` 拆成
   `N1-3` 与 `N1-7`；填 `N1` 谁都匹配不上，判 `AREA_STATION_NOT_FOUND`。
4. **服务端会重发未结的命令，对端必须重放原答案。**新答案换个 `resultId` 就是内容冲突，服务端直接
   拆会话（ADR-cross-0006、ADR-cross-0014）。假车载端现在按 key 缓存答案，重发时原样再送一遍。
5. **受理和建单确认不是同一瞬间。**stage 在受理时就翻到 `AwaitingPickupArrival`，`CONFIRMED` 要
   等建单与对账走完。判据要等，不能取样一次。
6. **把对端进程杀掉不会让会话离开 `Ready`。**`SessionRecoveries` 那一行不由连接断开驱动；服务端
   的存活性走的是另一条路——`ReadOnboardFactsAsync` 给该会话代次最后一条入站消息计龄
   （`JourneyRuntimeEngine.cs` 的注释原话是 "a dead peer leaves a Ready row"）。
   `load-result-requires-recovery` 第一次就写成「杀进程然后等 `Readiness` 翻转」，等满 60 秒读到
   的仍然是 `Ready`，红证据留在 `evidence/l2/20260903-load-result-requires-recovery-001`。要让会话
   真的离开 `Ready`，让车载端报一个 `departureSafe=false` 的 `SafetyStateChanged`。
7. **真装置下别一看到门开就放货。**第一版就是那么写的：`autoPopDoorOnUnlock` 让门几乎立刻弹开，
   脚本随即放货关门，开锁到关门只隔了 **124 ms**。而车载端要求锁反馈稳定 `feedbackStableMs`（300
   ms）才认，于是它从来没观测到一个稳定的「已开锁」状态，报回一份不完美的 `OperationResult`，
   服务端如实判 `LOAD_RESULT_REQUIRES_RECOVERY` 并停摆。**这不是缺陷，是脚本比人快**——真操作员
   放一篮货要几秒钟。可等的判据是车载端自己发的 `OperationProgress`，相位 `WAITING_OPERATOR`：
   它在锁反馈稳定、开锁输出复位之后才发，而且落在服务端的 `ProtocolInbox` 里，是服务端自己收到的
   事实。（那几次红是写场景过程中的迭代，没有留成证据目录。）
8. **时钟偏差场景里，「需求被拒」的原因码不是 `ONBOARD_DEPARTURE_UNSAFE`。**第一版按它写，白等了
   120 秒。实际先翻的是会话本身——`SessionRecoveries` 变成
   `RecoveryRequired / DEPARTURE_SAFETY_NOT_READY`，偏差生效后 0.6 秒内——需求随之判
   `ONBOARD_FACTS_NOT_READY`。`ONBOARD_DEPARTURE_UNSAFE` 是「会话还在 Ready、但车载端说不能走」
   那种情形的原因码。红证据留在 `evidence/l2/20260903-real-onboard-clock-skew-001`。
9. **改了外部条件之后，要等因果事实真的落库再投需求。**同一条场景第二个坑：设完偏差 56 ms 就发布
   需求，而车载端要下一次 1 秒轮询才发现证据过期——运行时在那个窗口里用「仍然 Ready」的会话把需求
   受理掉了（`ELIGIBLE` → `ACCEPTED`），此后 backlog 不再重评，判据只能等到超时。三次稳定性复跑里
   中了一次。红证据留在 `evidence/l2/20260903-real-onboard-clock-skew-004`。**顺序改成「先等会话
   降级落库，再发布需求」**，竞态就没有了。
10. **两个 WPF 窗口会弹到桌面上，这是这一层固有的。**`Start-L2Process -Gui` 刻意不用
    `WindowStyle Hidden`：那个值会进 STARTUPINFO，被 WPF 第一次 `Show()` 采纳，而隐藏的窗口
    UI Automation 未必找得到——驱动会在一个跟真实原因毫不相干的地方超时。跑的时候窗口会抢一次
    焦点，之后不会再抢——驱动不注入按键，见「加一个场景」那一节。要让它进 CI，得有一个交互式桌面
    会话，那是落地顺序第 7 步。
11. **~~让真车载端报一份失败结果，要等满它自己的 120 秒操作员超时。~~ 这条 2026-09-09 起不成立了，
    留在这里是因为它解释了 `real-onboard-recovery-entry-missing` 为什么现在跑不通。**
    原文：`real-onboard-recovery-entry-missing` 等到 `WAITING_OPERATOR` 之后关门但不放货，车载端
    等满 `workflow.operationTimeoutMs`(120 s) 才发结果，`overallOutcome` 是 `UNKNOWN`，服务端据此
    判 `RecoveryRequired`。
    **ADR-cross-0058 决策 1 把这条路整个拿掉了**：「门关了、货没动」现在是目标态闭环里的
    *相反态*，车载端重新打一次开锁脉冲并再提示一次，**不再产出任何结果**。实测复跑
    `evidence/l2/20260909-recovery-entry-missing-after-target-state-loop-001`：旅程 240 秒里一直
    停在 `AwaitingLoadResult`，一条判据都没走到。
    **那条场景要重新指向一个真的 UNKNOWN**——决策 2 留给人工恢复的是「仓位状态未知、锁闭反馈无效、
    开锁输出无法确认复位」，模拟器的 `light-curtain-override` 与 `faults/modbus` 才是它的入口，
    关门不放货已经不是了。在改好之前不要把它算进任何一批证据。
12. **模态对话框要按 `AutomationId` 找按钮，不要按标题。**`OnWireToGateRecoveryClick` 会弹一个
    `MessageBox` 要现场确认，它是同进程的另一个顶层窗口，得从 `RootElement` 找而不是从主窗口找——
    主窗口这时正停在模态循环里，什么都不答。按钮用 `AutomationId` 认：`MessageBox` 的按钮沿用
    Win32 控件 id（IDYES = 6、IDNO = 7），不随显示语言变，而标题只有中文 Windows 上才是「是(Y)」。
    驱动里是 `$onboard.Confirm('申请恢复原操作')`。
13. **重开一轮之后不能照着「门弹开了」就动手，要等车载端自己再发一条 `WAITING_OPERATOR`。**
    这是第 7 条那个坑的第二种长相，代价同样是一整趟运行。`real-onboard-load-door-closed-empty`
    第一版按门的物理状态推进：等 `doorState` 变回 `OPEN` 就关下一轮的门。红证据
    `evidence/l2/20260909-real-onboard-load-door-closed-empty-001` 的时间线写得很清楚——第 1 轮
    门在 `16.4155` 弹开，脚本 `16.4557` 就把它关上了，**隔了 40 ms**，而那一刻开锁输出还是 1
    （下一行的 `CLOSED/EMPTY/1/1`）。车载端于是从来没观测到一个稳定的「已开锁」状态，卡在
    `WaitForLockerAsync` 里，第 3 次 `UNLOCKING` 永远不来。
    **`WAITING_OPERATOR` 才是可等的判据**：它在锁反馈稳定 `feedbackStableMs` 且开锁输出确认复位
    之后才发得出来，所以它同时回答「这一轮走完了没有」和「现在关门算不算数」。
14. **仓位操作转 `Committed` 与需求在旅程里转 `Loaded` 不是同一次写入。**前者由消息处理器在收下
    结果时就写，后者要等引擎的下一轮收尾。`load-cancelled-in-flight` 首跑等到 `Committed` 就直接
    读 `JourneyDemands.State`，读到 `Planned`，红证据
    `evidence/l2/20260909-load-cancelled-in-flight-001`——而同一次运行里随后的判据等到了
    `AwaitingGateArrival`，说明它只是还没轮到。**跨两次写入的判据一律用 `Wait-L2Condition` 等，
    不要读完一个就顺手读下一个**，这与 `L2-CB-12` 那条「转阶段与订单落到假 RIoT 不同时」是同一
    种错误，只是这次两边都在服务端自己的库里，看着更像可以一起读。

## ADR-cross-0058 的三条操作员不作为场景

`real-onboard-load-door-closed-empty`、`real-onboard-unload-not-emptied` 与
`real-onboard-station-timeout-door-open` 是 2026-09-09 补的，对应 ADR-cross-0058 Consequences 里
点名要的那三条：仓门已闭超时、卸货未取空反复闭环、仓门未闭超时。**都走真装置，没得选**——判据
全在 IO 层（光幕极性、锁反馈时序、开锁输出复位），合成对端按策略应答装卸，「关了门但没放料」在
那边压根表达不出来，它会绿，而绿的是另一件事。

三条各自钉的东西不一样，别把前两条看成同一条：

- **`real-onboard-load-door-closed-empty`** 验装货侧的目标态闭环（决策 1、2、6）加提示节拍
  （决策 3）。两轮重开而不是一轮：一轮只证明「重开过一次」，`OnboardController` 那条带
  `MaxReopenAttempts`（默认 2）的老路径也做得到；ADR 要的是不设上限。第二幕把门开着晾满一个
  `OperationTimeout`，断言**又提示了一次而 `UNLOCKING` 的条数不变**——这是决策 3 唯一能被证伪的
  地方，也是这条场景要花 120 秒的原因。
- **`real-onboard-unload-not-emptied`** 验的不是「也会重开」，而是 ADR-cross-0015 的**不对称**：
  装货有「确定失败」这条出口（服务端 `ApplyOperationResultAsync` 的 `determinateFailure` 只对
  Load 成立），卸货没有。所以它的核心判据是否定的——两轮之后既不 `Committed` 也不 `Failed`
  也不 `RecoveryRequired`，需求也没被取消，唯一的终结方式是货真的被取走。
- **`real-onboard-station-timeout-door-open`** 验决策 4——期限到期而仓门未闭时不结束本站，转告警
  并持续等待——外加它闭合之后按决策 5 结算的那一段。**它钉的格子是 `AwaitingLoadResult`，不是
  `AwaitingSublot`**，那是这条场景 2026-09-09 重写时校正的东西，见下一节。

**决策 5 那一格现在有生产者了，就在上面那条场景的尾巴里。**这段话一度写着「它在真装置上没有
生产者」：车载端唯一发 `FAILED` 的地方是 `CreateRejectedResult`，它把所有仓位填成
`NOT_STARTED` + `UNKNOWN`，而服务端的 `determinateFailure` 要求每个仓位
`State != Unknown && DoorLocked && UnlockOutputReset`，两者不可能同时成立。
[`8005-agv-program#24`](https://github.com/trytoreachpeak0/8005-agv-program/issues/24) 补上了那条
路——车载端在**相反态**（门已闭、货没动）过期后结算 `FAILED`——
`20260909-real-onboard-station-timeout-door-open-004` 是它第一次在真装置上被观测到
（`StationOperations.Status = Failed`，不是 `RecoveryRequired`）。

## `real-onboard-station-timeout-door-open` 钉的是哪一格，以及它两次红在自己身上

**决策 4 有两格，这条场景 2026-09-09 重写过一次，就是为了换到对的那一格。**

初版把它摆在 `AwaitingSublot`——操作员既没扫码、又用 `lock-feedback-override` 把 8 号仓的锁反馈
钉成 0 制造一扇虚掩的门。跑出来是红的（`-001`），当时的诊断是「决策 4 的分支在真装置上到不了」。
**那个诊断只对一半**：

- ADR-cross-0058 的 Consequences 点名的是 **`AwaitingLoadResult`**（原文：「服务端改动落在 ……
  `JourneyRuntimeEngine` 的 `AwaitingLoadResult`」）。那一格实测可达，告警照挂——分水岭是
  `IsUnsafetyExplainedByOwnCommandAsync` 那条豁免要求存在 `Prepared` 的 station operation，
  而在途仓位操作只存在于那一格。
- `AwaitingSublot` 那一格**本来就不该挂这个告警**：该阶段的定义是本站一条仓位命令都没发过，
  此时读到一扇开着的门意味着没有任何我方命令能解释它，会话降级成
  `RecoveryRequired / DEPARTURE_SAFETY_NOT_READY` 是正确行为，旅程停在就绪门上记
  `ONBOARD_SESSION_NOT_READY`。

判定本身现在由三条 L1 测试钉住（两格各一条，外加一条「豁免不跨车」），所以这条 L2 不重复钉判定，
只钉真装置才能证的东西。完整复盘在
`docs/defects/20260909-station-timeout-door-not-closed-branch-unreachable.md`。

**门是怎么开着的：不用注入。**换格之后不需要 `lock-feedback-override` 了——操作员扫完 SUBLOT，
车载端为这次装载打开锁脉冲，门弹开，人走了，这就是那一格的现场原样。

重写之后又红了两次，两次都红在场景自己身上，值得记：

1. **`-002`：只关了一次门。**期限过了并不等于立刻判死——决策 1 的目标态闭环先赢一轮：读到相反态
   车载端先重新开锁并提示，那是过期之后留给人的最后一次机会，要**再**读到一次相反态才结算。
   实测门 20:57:56.173 关到位，20:57:56.539 就重新打了脉冲，门又弹开——而脚本在等告警撤销，
   可门开着告警本来就不该撤销。产品是对的，判据错了。现在关两次门。
2. **`-003`：用 `$null` 表达「告警已撤销」。**`Wait-L2Condition` 在 `Probe` 返回 `$null` 时
   **根本不评估 `Until`**（`L2.psm1` 的 `if ($null -ne $last -and (& $Until $last))`），于是等到
   超时，报错还写成 `Last observed: (nothing)`——看着像没读到，其实是读到了想要的那个空。
   **等一个值消失，探针要返回哨兵字符串。**

