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
| `auto-charge-endurance` | 合成 | 一趟串四幕：送完一单、低电自去充电、充满、再送一单 | `evidence/l2/20260912-auto-charge-endurance-001` |
| `multi-demand-one-stop` | 合成 | 一个停靠上多张单，作业清单是复数的 | `evidence/l2/20260908-multi-demand-one-stop-001` |
| `multi-demand-four-stops` | 合成 | 四张单落在四个不同站点：到第一站后吸收成一趟，行程带发五条腿，四站依次装完去关卡 | `evidence/l2/20260910-multi-demand-four-stops-009` |
| `load-command-never-answered` | 合成 | 装载指令石沉大海，服务端重发而不改口 | `evidence/l2/20260908-regression-load-command-never-answered-001` |
| `real-onboard-normal-load` | **真的** | 同一条链路，但条码走 UIA、装卸走真 Modbus | `evidence/l2/20260903-real-onboard-normal-load-005` |
| `real-onboard-clock-skew` | **真的** | 车载端时钟偏差的有界容差，界内、界外、恢复三段 | `evidence/l2/20260903-real-onboard-clock-skew-007` |
| `real-onboard-recovery-entry-on-unknown` | **真的** | 锁反馈失效报出一份真的 `UNKNOWN`：旅程停摆，而车上打得开恢复入口 | `evidence/l2/20260910-real-onboard-recovery-entry-on-unknown-001` |
| `real-onboard-recovery-compensate-load` | **真的** | 上一条的下半段：按下「补偿清空」，五步恢复握手走到底，仓位真被清空 | `evidence/l2/20260910-real-onboard-recovery-compensate-load-007` |
| `real-onboard-load-door-closed-empty` | **真的** | 装货时关门不放料：反复重开、不判失败、不进恢复；提示节拍到期只再提示不重复脉冲 | `evidence/l2/20260909-real-onboard-load-door-closed-empty-002` |
| `real-onboard-unload-not-emptied` | **真的** | 卸货时关门不取货：一直闭环到取空，没有取消分支 | `evidence/l2/20260909-real-onboard-unload-not-emptied-001` |
| `real-onboard-station-timeout-door-open` | **真的** | 站点期限到期而仓门未闭：告警并持续等待，闭合后按决策 5 结算；结算之后旅程自己结束（#39 之前停在原地） | `evidence/l2/20260910-real-onboard-station-timeout-door-open-001` |
| `real-onboard-multi-demand-stop-plan` | **真的** | 四停靠旅程：车辆侧收下五条腿的行程带（#37 的回归守卫），四站在真 Modbus 上依次装完 | `evidence/l2/20260910-real-onboard-multi-demand-stop-plan-007` |
| `real-onboard-multi-demand-operator-inaction` | **真的** | 四停靠旅程里的三种操作员不作为：关门不放料、门开着过期、两次关门判确定失败——然后旅程自己离开那一站，后两站照常装完 | `evidence/l2/20260910-real-onboard-multi-demand-operator-inaction-003` |
| `real-onboard-restart-while-waiting-operator` | **真的** | 开锁等操作员时杀掉客户端再拉起：车辆按实时 IO 交出那次中断的结论，旅程停摆，补偿清空走到对账（现场窗口一的死锁） | `evidence/l2/20260911-real-onboard-restart-while-waiting-operator-005` |
| `real-onboard-field-operator-compensate` | **真的**，自动化面 | 现场驱动脚本的「制造真的 UNKNOWN + 补偿清空」两幕：扫码与按钮都走车载端 HTTP 自动化面，一次 UIA 都不用 | `evidence/l2/20260911-real-onboard-field-operator-compensate-002` |
| `real-onboard-field-window-rehearsal` | **真的**，自动化面 | 现场窗口一（无人）整窗彩排：驱动脚本演 A、C 接 B、正常装，采集器在它说的那一刻打 checkpoint、在它写出的记录上 finalize；之后开去关卡卸货（`L2-FW-40`，8005-agv-program#48 修好之前红） | `evidence/l2/20260911-real-onboard-field-window-rehearsal-006`（车载端 `54772ff`，含 #48 修复；`-004`/`-005` 是 #48 的红） |
| `real-onboard-multi-demand-compensate` | **真的**，自动化面 | 四停靠旅程里停靠 2 真的 `UNKNOWN` + 补偿清空：旅程自己离开那一站（#47 之前停在 `Blocked`），后两站照常装，关卡把三条卸完 | `evidence/l2/20260911-real-onboard-multi-demand-compensate-004`（车载端 `96c7513`，含 #48 修复；`-003` 只红 `L2-MDC-60`/`-61`，原因是 #48） |
| `real-onboard-recovery-retry-after-refusal` | **真的**，自动化面 | 旅程还没 `Blocked` 时按「补偿清空」被拒，转 `Blocked` 后同一 attempt 再按：开得出会话、补偿走完、车不被掐连接（现场旅程 54d2cf63 卡在这里，8005-agv-program#49） | `evidence/l2/20260911-real-onboard-recovery-retry-after-refusal-006`（车载端 `ab346ed`；`-004` 是修复前的红基线，见最后一节） |

编号更小的目录是同一批里更早的跑次，多数是稳定性复跑。十二个是**红的**，各自的原因见文末：
`load-result-requires-recovery-001`（第 6 条）、`real-onboard-clock-skew-001`（第 8 条）、
`-004`（第 9 条）、`real-onboard-load-door-closed-empty-001`（第 13 条）、
`load-cancelled-in-flight-001`（第 14 条），以及
`real-onboard-station-timeout-door-open` 的 `-001`/`-002`/`-003`（最后一节），以及
`real-onboard-recovery-compensate-load` 的 `-001`（第 12 条）/`-002`（第 16 条）/`-004`（第 14
条的第四例），以及 `schema-conformance-normal-load-001`（合成对端的 `Heartbeat` 违反 schema，见
「车载端报文的 schema 校验」一节）。**除头两条之外全都红在场景、驱动或替身自己身上，不是产品**——`real-onboard-station-timeout-door-open-001` 那一条连诊断都跟着错了一半。
另有 `real-onboard-restart-while-waiting-operator` 的 `-001`/`-002`/`-003` 三个，前两个**红在产品**、
第三个红在驱动，见最后一节。`real-onboard-field-window-rehearsal-001` 红在现场驱动脚本自己（第 21 条），`-002` 红在场景拷活库
（`Copy-Item` 读不了被 SQLite 字节区间锁住的 `-shm`，改为只读连接上 `VACUUM INTO`），`-003` 红在场景
假设关卡是停靠 5（实际编号 9）、以及驱动把恢复窗口开着时装货途中本来就显示的「取消装货」算成了恢复入口，
`-004` 与 `-005` 的 `L2-FW-40` **红在产品**：车载端把三项的关卡作业清单判 `PROTOCOL_SCHEMA_INVALID`
（8005-agv-program#48）。`-004` 在卸货里空等满 30 分钟，服务端日志与假 RIoT 日志、库快照里的
`ProtocolInbox` 因此是其余证据的十倍大，**这三个文件提交时无损 gzip**（原文件 SHA-256 记在提交说明里），
`-005` 起卸货只等 5 分钟。`-006` 换上车载端 `54772ff`（`w2g/multi-demand-gate-worklist`，清单项数上限跟
schema 走到 8）后整条全绿：关卡那份三项 `GATE` 清单 20 ms 内被应答，三条卸货 `Committed`、旅程 `Completed`，
会话全程停在 generation 1，没有一次重连。`real-onboard-multi-demand-compensate` 的 `-001` **红在产品**（#47，补偿之后旅程停在
`Blocked`），`-002` 红在场景自己的会话判据与诊断（最后一节），`-003` 只红关卡那两条，原因与 `L2-FW-40` 相同（#48）；
`-004` 换上车载端 `96c7513` 后全绿。

方案第 4 节标 ★ 的三条**现在三条都有了**。第三条（车载端时钟偏差）走了最远：合成对端里根本没有
`VehicleSafetySignal.IsFresh` 那段逻辑，真车载端接进来之后逻辑在跑了，但两端同机共用一个时钟，
偏差不会自己出现——补上 `tools/ControlServer.ClockSkewProxy` 才凑齐。

而且它不再是「复现缺陷」：
[`8005-agv-onboard-hmi#1`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/1)
已由 Kun Wang 在 `abb8e73` 修成**有界容差**，所以场景钉的是那个界的两侧加恢复。

`load-result-requires-recovery` **到 Blocked 为止，不跑到「恢复并继续」**：出口是五步恢复握手，
而合成对端不发起它。给它编出那五条出站消息只会让这条场景变绿而证不出任何新东西。

`real-onboard-recovery-entry-on-unknown` 补的是那半条的**入口**：一份真的 `UNKNOWN` 报上去、旅程
停摆之后，车上的人有没有出路。**它 2026-09-10 改了名，因为它证的事情反过来了**——原名
`real-onboard-recovery-entry-missing`，最后一条判据长期是红的、而且红得对：停摆之后车载端 HMI 上的
恢复入口从来不出现，任何恢复动作都发起不了。

**那个缺口 2026-09-04 就补上了，六天没人看见。**服务端 `8c6d400`（fix(session): 会话不再在有操作
待恢复时报 READY）给 `GetRecoveryReason` 加了 `OPERATION_RECOVERY_REQUIRED`，
`docs/defects/20260904-recovery-required-never-reaches-session-state.md` 里也写着「入口开了」——
但**这条 L2 在那之后一次都没有在能跑通的状态下跑过**：ADR-cross-0058 决策 1 落地后它的旧向量
（关门但不放货）不再产出任何结果，整条场景卡在 `AwaitingLoadResult`，见第 11 条与
`evidence/l2/20260909-recovery-entry-missing-after-target-state-loop-001`。换完向量第一次跑，六条
判据全绿。**教训是第 15 条**：一条不再是有效证据的场景，同时也不再是有效的缺陷记录。

**它现在的向量是「锁反馈失效」，不是「人没放料」。**等车载端自己发出 `WAITING_OPERATOR` 之后，用
`lock-feedback-override FIXED_1` 把那一仓的锁反馈 DI 钉死在「已锁」：门是真开着的，车辆读到的却是
一个稳定的**相反态**，按决策 1 重打一次开锁脉冲，而这一次它再也等不到「未锁」——`UnlockFeedbackTimeout`
(3 s) 到期抛 `TimeoutException`，走进执行器那个
`catch (IOException or TimeoutException or InvalidDataException)` 分支。**决策 1 之后那里是
`overallOutcome = UNKNOWN` 唯一的产地**，对应决策 2 三件事里的「锁闭反馈无效」。
整趟 41 秒，注入到停摆之间 4.9 秒；旧向量光是等操作员超时就要 120 秒。

**这条场景有意不去点那个恢复按钮。**按下去之后是五步恢复握手加一次真 Modbus 再闭环，那是另一条
判据链；混进来只会让这一条同时说两件事，而其中一件失败时说不清是哪一件。**那条链 2026-09-10 起有
自己的场景了**：`real-onboard-recovery-compensate-load`，见下面它自己那一节。

**它 2026-09-04 还改过一次向量，原来测的是 `RESUME_AFTER_REPAIR`，那是错的。**它制造的状态是「装载
跑完、门关着锁上了、仓位仍是空的」，对应 `COMPENSATE_LOAD_ALL_EMPTY`；而 `RESUME_AFTER_REPAIR` 要的
是「跑到一半没出结果、车辆还握着物理断点」。车辆记录结果时会把 attempt 与 `OperationContext` 一并
清空，也会拒绝 checkpoint 为 `ResultRecorded` 的恢复命令——**两端独立地做了同一判断**，服务端拒绝
resume 是设计不是缺陷。同一状态下 `COMPENSATE_LOAD_ALL_EMPTY` 是被授权的，有 L1 为证：
`RecoveryStateMachineG2Tests.AfterARefusedResultResumeIsRefusedButCompensationIsAuthorized`。
完整复盘（含四轮归因里错的那三轮）在
`docs/defects/20260904-recovery-required-never-reaches-session-state.md`。红证据：
`evidence/l2/20260904-real-onboard-resume-after-repair-f0465d9-001`（第一次改名前的最后一次运行）。

## CI 只跑合成场景

`.github/workflows/l2.yml`，跑在本仓自己的 `headless` runner 上，每次 push 与 PR。现在是十条，
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

**`CHARGING` 不能由场景来写（2026-09-12，`8005-agv-program#53`）。**两条充电场景过去在车到桩时经
`PUT /vehicle` 自己写 `batteryState = CHARGING`，于是服务端下的充电单只有一段移动也照样绿；现场同一张单让车在
211 上停了十三个小时没通电。现在假 RIoT 只在一张带 `act(78,1,0)` 的单完成时报 `CHARGING`，场景只推订单状态
与电量，`L2-AC-19` 与 `L2-FW2-19` 钉的就是这个动作真的在单子里。同一张票还让服务端在桩解析不到时拒绝一切
接单，所以假 RIoT 的默认地图带上了 `211 充电点1`。

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
| `snapshots/` | 收尾时各控制面与十一张表的快照，含 `ProtocolInbox` / `ProtocolOutbox` 两张协议表 |
| `schema-conformance/` | 车载端报文逐条过 protocol JSON Schema 的结果：`schema-coverage.json`（按产地记哪几种报文、各几条）、`schema-conformance.txt`，有违约时还有 `schema-violations.json` |

两张协议表是 2026-09-10 加进快照的（`8005-agv-program#35`）：PASS 会把 stage 库连同它们一起删掉，
此前绿跑之后一条报文都不剩。快照走 `ConvertTo-Json`，`RequestJson` / `PayloadJson` 被转义成 JSON
字符串——能还原，但不能直接 grep。

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
- `OnboardAutomation` —— 只对真装置有效。打开真车载端自己的 loopback HTTP 自动化面（端口 58414），
  场景经 `$Context.OnboardAutomationPort` 找到它。给的是现场驱动脚本 `scripts/field/FieldOperator.psm1`
  的彩排用的：车上驱动脚本说话的对象只有它，照旧走 UIA 就证不到上车的那份代码。其余真装置场景不开，
  保持它们绿的那一套配置。
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
| 车载端 HTTP 自动化面（`OnboardAutomation` 场景） | 58414 |

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
11. **~~让真车载端报一份失败结果，要等满它自己的 120 秒操作员超时。~~ 这条 2026-09-09 起不成立，
    2026-09-10 已经换掉，留在这里是因为它解释了那次改向量。**
    原文：`real-onboard-recovery-entry-missing` 等到 `WAITING_OPERATOR` 之后关门但不放货，车载端
    等满 `workflow.operationTimeoutMs`(120 s) 才发结果，`overallOutcome` 是 `UNKNOWN`，服务端据此
    判 `RecoveryRequired`。
    **ADR-cross-0058 决策 1 把这条路整个拿掉了**：「门关了、货没动」现在是目标态闭环里的
    *相反态*，车载端重新打一次开锁脉冲并再提示一次，**不再产出任何结果**。实测复跑
    `evidence/l2/20260909-recovery-entry-missing-after-target-state-loop-001`：旅程 240 秒里一直
    停在 `AwaitingLoadResult`，一条判据都没走到。
    **现在那条场景叫 `real-onboard-recovery-entry-on-unknown`，用的是「锁闭反馈无效」**——
    `lock-feedback-override FIXED_1`，见上面它自己那一节。这里当初写的三个候选入口
    （`light-curtain-override` 与 `faults/modbus`）**有一个是错的**：`light-curtain-override` 的
    `FIXED_0`/`FIXED_1` 都只是把光幕钉成一个**已知**值，而 `LockerSnapshot.IsKnown` 要的是三个
    raw 位都非 null，所以它产不出 `UNKNOWN`。`faults/modbus` 能产，代价是整条 IO 连接断掉、
    八个仓位一起未知，那是另一件事。
12. **模态对话框是主窗口的后代，不是桌面根的子窗口——而这一条 2026-09-10 之前写反了。**
    `OnWireToGateRecoveryClick` 这一类按钮会弹一个 `MessageBox` 要现场确认。原文写着它「是同进程
    的另一个顶层窗口，得从 `RootElement` 找而不是从主窗口找」，`Confirm` 也照那样实现了。**那是
    照代码推的，一次都没跑过**——唯一可能跑到它的场景（`real-onboard-recovery-entry-on-unknown`）
    有意停在按钮前面。
    实测：驱动「补偿清空」时 `RootElement.FindAll(Children, pid)` 只返回主窗口，
    连全桌面枚举里都没有任何 `#32770`；而 `AutomationElement::FocusedElement` 就落在对话框的
    「No」按钮上，往上走一层就是 `补偿清空~ControlType.Window~#32770`，
    `$window.FindAll(Descendants, ClassName='#32770')` 一找就到。红证据
    `evidence/l2/20260910-real-onboard-recovery-compensate-load-001`。
    **症状是「点了没反应」，而那正是一个你看不见的对话框的样子**：`InvokePattern.Invoke()` 正常
    返回、按钮仍然 enabled、车载端日志一行不写。六次运行才定位到，因为每一个观测都在说「点击没
    发生」。`Confirm` 现在两处都找，并在超时时把两处看到的 `#32770` 一起报出来。
    按钮仍然按 `AutomationId` 认（IDYES = 6、IDNO = 7），**理由比原来写的更强**：实测这台中文
    Windows 上按钮标题回来的是「Yes」/「No」——`MessageBox` 的按钮文案跟的是进程的 UI 语言，
    不是系统显示语言。按标题找会两头落空。
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
    **第三例是 `load-command-never-answered` 的 `L2-LN-01`**（`8005-agv-program#30`）：它等到命令
    到了对端就直读 `Stage`，而下发与推阶段同样不是同一次写入，在 self-hosted runner 上
    间歇性读到 `AwaitingSublot`（run 34362936547、34359517708），而同一次运行里紧接着的
    `L2-LN-02` 读到的就是 `AwaitingLoadResult`。**假红比真红贵**：这条场景钉的正是
    「服务端重放未结命令而不改口」，它每红一次都要人去判一次是不是真坏了。
    **第四例是 `real-onboard-recovery-compensate-load`**（`8005-agv-program#31`）：它在读完握手那
    几条判据之后才取「补偿之前的 `UNLOCKING` 条数」当基线，而那几条判据自己要花几百毫秒——实测
    车辆在补偿命令下发后 **106 ms** 就打了脉冲，基线晚了它 90 ms，于是基线里已经含着要等的那一条，
    「又开锁了一次」永远等不到。第一跑侥幸绿了，第二跑红。红证据
    `evidence/l2/20260910-real-onboard-recovery-compensate-load-004`，绿的是 `-003`——**同一份脚本
    一绿一红，这就是这类竞态的样子**。基线现在取在按下按钮之前。
    **第五例是 `load-result-requires-recovery` 的 `L2-LR-01`，和第三例是同一对写入**：等到
    `pending-operation` 就直读 `Stage`。CI run 34437756302 读到 `AwaitingSublot`，330 ms 后同一次
    运行里的探测就读到了 `AwaitingLoadResult`；同一份脚本在 run 34448095070 上是绿的。红证据
    `evidence/l2/20260910-ci-34437756302-load-result-requires-recovery`。它漏掉的原因是第三例
    只修了出事的那一条，而两条场景的第二段是同一个模板抄出来的。**修一例的时候，要把同一形状的
    其他地方一起找出来。**这次顺手核了 `scenarios/` 里剩下的 8 处直读 `Get-Stage`：前面要么是
    `Wait-L2Iterations`，要么读的是早就落库、不会再变的状态，都不是这种形状。

15. **一条不再是有效证据的场景，同时也不再是有效的缺陷记录。**
    `real-onboard-recovery-entry-missing` 的最后一条判据从 2026-09-04 起红得对，服务端同一天
    `8c6d400` 就把它修好了，而这条 L2 因为决策 1 打断了它的向量，**六天里没有任何一次跑到过那条
    判据**。README 与它自己的文件头在这六天里一直写着「那个缺口本身没有被修掉」——那句话在写下的
    第二天就过期了，只是没有任何东西会去推翻它。**场景一旦停跑，它讲的故事就开始腐坏**；重新指向
    之后第一件事是把它当成一份未知结论去读，而不是去确认已经写好的结论。

16. **一个已经处在目标态的仓位，恢复向量一次 IO 都不碰——而它照样报 `ALL_EMPTY`。**
    `WireToGateRecoveryVectorExecutor` 在 `!correction` 分支里对每个 `!locker.HasCargo` 的仓位
    **直接标 `COMPLETED`**，然后发一条 `PREPARING` 就收尾。`real-onboard-recovery-compensate-load`
    首个可跑版本让维护人员先关门再补偿，仓位于是「门关、已锁、空的」——正是它要达到的终态——
    服务端侧全绿（工作流 `Reconciled`、`Outcome = ALL_EMPTY`、需求判死），**握手确实走完了，
    车载端却什么都没做**。红证据
    `evidence/l2/20260910-real-onboard-recovery-compensate-load-002`。
    这不是缺陷，是「补偿清空」的定义：空仓本来就不需要清。**但一条只证明了服务端记账的场景，
    不该挂在「在真 Modbus 上把仓位清空」这句话下面。**现在的场景让维护人员先在仓里发现一箱货
    （车辆放弃之后放进去的，正是那份 `UNKNOWN` 盖住的窗口），向量才真的走一遍开锁—取空—回锁。
    另一头还有两道硬前置，一起记着：`ValidateInitialSnapshot` 要求每个目标仓
    **已锁且开锁输出已复位**，否则当场判 `LOCK_NOT_CLOSED` 返回 `FAILED`——**车辆不会去驱动
    一扇已经开着的门**，所以「关门」是补偿的入场券，不是布景。

17. **`dotnet` 按当前目录找 `global.json`，不按传给它的 `.sln` 路径找。**2026-09-10 从工作区根
    用绝对路径调这个脚本，两次都红在构建上：`multi-demand-four-stops-007` 报
    `error CA1859: Change type of parameter 'stops' ...`，`-008` 报
    `C:\Program Files\dotnet\sdk\10.0.302\...\Microsoft.NET.Sdk.Analyzers.targets(43,5): error MSB4184`。
    工作区根没有 `global.json`，于是落到机器上最新的 SDK 10.0.302，而本仓
    `TreatWarningsAsErrors` 加 `AnalysisLevel=latest-recommended` 让新 SDK 的分析器直接判死。同一
    个 commit 进仓目录构建是 0 警告——**像偶发，其实取决于调用者站在哪里**。现在构建与 peer 发布都
    先 `Push-Location` 进各自的仓（或克隆），并把实际解析到的 SDK 版本记进 timeline。
    **历史证据不受影响**：文档里的调用写法 `pwsh .\scripts\l2\Invoke-L2Scenario.ps1` 本身就要求站
    在仓里；而且 SDK 10 下 ControlServer 构建必然失败，所以凡是构建成功的那次运行，同一进程里
    发布的 peer 用的也是 8.0.425。同一个坑也在 `scripts/build.ps1`、`test-wire-to-gate.ps1`、
    `Publish-ControlServer.ps1` 与 `field/Invoke-W1FieldWindow.ps1` 里，已由 `fcfb1ad`（PR #21）一并修掉。

18. **`ProtocolInbox` 里全过 schema，不等于车载端没发过坏报文。**每条场景收尾都判一条
    `L2-SC-01`（见下一节），真装置下它读的就是这张表——而这张表**只有服务端处理成功的行**。
    身份不对、世代过期、类型不支持、服务端自己的校验不过，这些行一个字都不落库，异常一路传到
    `OnboardTcpServer` 把连接掐掉。所以「库里全绿」只能说「服务端收下的那些都合法」。另外两件事
    同样别从这张表里推：它按 `MessageId` 最后写入者赢（`RecoveryStateReport` 等价重放会就地覆盖），
    所以行数不是发送次数；`SessionHello` 与 `ExceptionRecoverySessionRequested` 存的是凭据脱敏成
    `"[REDACTED]"` 之后重序列化的行，schema 只要求非空字符串所以照样能验，但**拿它们核
    `ContentHash` 必然对不上**，那个哈希取自原始行。

19. **服务端每条 TCP 连接只有一个 `DbContext`，它跟踪的实体不会因为别人写了库而变。**握手时
    `WireToGateStore` 把这台车的活动旅程带跟踪读进去，之后引擎用自己的上下文改 stage，同一连接上
    再查到的仍是握手那一刻的样子。只有在「车在某个状态下连上、之后状态被别人改掉」时才看得见，
    所以前面所有场景（车都是在旅程开始之前连上的）一次都没撞上，重启场景第一次跑修好的车载端就撞上了
    （`-002`）。恢复协调器现在读旅程时先 `ReloadAsync`；**同一类读法在别处还有没有，没有逐条核**。
20. **重新拉起车载端之后别马上点弹窗。**`-003` 在拉起后约 1 秒点「补偿清空」，确认框已经在 UIA
    树里，按钮却还不接受输入，`Invoke` 抛 `Operation is not valid due to the current state of the
    object.`，请求没发出去。`Confirm` 现在在截止前把 `InvalidOperationException` 当「还没好」重试。
21. **判「车重开了、在等人」要看先后，不能看计数。**`real-onboard-field-window-rehearsal-001` 在停靠 2
    期限后第一次空关，驱动脚本（`scripts/field/FieldOperator.psm1`）等的是「`UNLOCKING` 与
    `WAITING_OPERATOR` 都比关门前多」。车辆门开着时每满一个 `operationTimeoutMs` 发一条提示节拍的
    `WAITING_OPERATOR`，而这一条恰好在关门后约 1 秒、重开的 `UNLOCKING` **之前** 290 ms 到——两个计数
    都涨了，脚本在开锁脉冲还没撤的时候关了第二次门，车辆从此观测不到稳定的已开锁，3 秒
    `UnlockFeedbackTimeout` 之后交出 `UNKNOWN`（`ACTION_NOT_ALLOWED_IN_STATE`），本该是确定失败的一站
    停摆成 `RecoveryRequired`。现在判的是「基线之后有一条点名这一仓的 `UNLOCKING`，**它之后**又有一条
    点名这一仓的 `WAITING_OPERATOR`」（`Get-FieldReopenedSlot`）。与第 13、14 条是同一类：条件在读
    的那一刻碰巧成立，而成立的原因不是要等的那件事。彩排的 3 分钟等待让关门点落在节拍前 2 秒，
    才撞出来；现场 20 分钟不一定对齐，判法照样是错的。
22. **`Invoke-L2Query` 的结果不能直接接管道。**它以 `return , $rows` 结尾，整批行是作为**一个对象**
    交出来的：`(Get-X)[-1]`、`foreach ($row in (Get-X))` 拿到的是行，`Get-X | Where-Object { ... }` 拿到的
    却是一个 `$_`——整个数组——`$_.RequestJson` 于是把每一行拼成一个空格分隔的字符串。只有一行时拼出来
    的仍是合法 JSON，所以它只在第二行落库的那一刻才炸：`ConvertFrom-Json` 报 `Additional text
    encountered after finished reading JSON content`。`real-onboard-restart-while-waiting-operator-007` 与
    `real-onboard-recovery-compensate-load-009` 都死在这一条上，而车早在几十毫秒内交了扫码。遍历结果用
    `foreach`；`@(...)` 包一层也救不了它。
23. **和别的 agent 并行跑真装置时换端口，别落进 Windows 的排除端口段。**2026-09-11 第一次把整组端口
    +100（58505–58514）跑，假 RIoT 绑 58508 当场抛 `SocketException (10013): An attempt was made to access
    a socket in a way forbidden by its access permissions`——不是端口被占，是控制端
    `netsh int ipv4 show excludedportrange protocol=tcp` 里有 `58473–58572` 与 `58573–58672` 两段（Hyper-V /
    WinNAT 动态保留，重启会变）。改 +300（58705–58714）就过了。**挑端口之前先看那张表**；症状是 10013
    而不是「地址已在使用」（10048）。
24. **点源进来的文件里的 `exit` 不结束外层脚本。**`real-onboard-field-window2-rehearsal-001`：采集器
    `. FullLoopWindowFinalize.ps1` 之后，那个文件以 `exit` 收尾，外层照样往下跑，FW-SC1 的判据在窗口二上又跑
    一遍、覆盖了 `assertions.json`——日志里先 `FW-FL2 PASS` 再 `FW-FL2 FAIL`。**点源的文件只设变量，由外层 `exit`。**

## 车载端报文的 schema 校验：`L2-SC-01`

每条场景收尾时，不论场景本身红绿，都把**车载端那一侧发出的每一行**交给
`tools/ControlServer.SchemaConformance` 逐条对 protocol JSON Schema 校验（`8005-agv-program#35`）。
校验器、vendor 的 schema 与已登记违约表（`tests/ControlServer.Tests/schema-known-violations.json`）
都与 `dotnet test` 里那道校验是同一份。**红了判死**：退出码非 0 或一行都没验到，场景就是 FAIL。

两套装置的来源不一样，因为能看见的东西不一样：

| 装置 | 验的是 | 从哪来 |
| --- | --- | --- |
| 合成 | 合成对端发出的每一行 | `ControlServer.FakeOnboard` 带 `--FakeOnboard:SchemaRecordPath` 启动，每发一行就记一条，带发送方法名 |
| 真的 | 真部署车载端包写进 `ProtocolInbox.RequestJson` 的每一行 | 收尾时从服务端库导出，`site` 记成 `ProtocolInbox[<MessageId>]` |

**为什么这两处要在 L2 验，而不是 G2**：合成对端的报文在 `dotnet test` 里一条都不产生（测试项目不
引用它）；真车载端的车载端 G2 验的是 test build 里的咽喉，与真部署包之间隔着一次打包和一次部署，
那段缝只有这里看得见。**服务端出站不在这里验**：`ProtocolOutbox.PayloadJson` 与 G2 在
`ProtocolEnvelope` 上验的是同一批字节，重放改写那一行也经过同一个钩子。

**首跑就抓到一条**：合成对端的 `Heartbeat` 一直多带一个 `observedAt`，0.3.0 的 schema 不允许
（`additionalProperties: false`），服务端从来不读它。红证据
`evidence/l2/20260910-schema-conformance-normal-load-001`，十条判据只红了这一条。

**代价**：每种报文的 schema 首次编译约 1.25 秒，一条场景十来种报文，约多 15–20 秒。

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

## `real-onboard-recovery-compensate-load`：入口之后那五步

`real-onboard-recovery-entry-on-unknown` 回答「人在车上有没有出路」，**这一条回答「那条出路走不走
得通」**。2026-09-10 之前它没有任何一层覆盖：服务端授权侧有 L1
（`RecoveryStateMachineG2Tests.AfterARefusedResultResumeIsRefusedButCompensationIsAuthorized`），
车载端请求侧有实现（`WireToGateBusinessService.RecoveryVectors.cs`），**两端合起来跑通过没有，
没人知道**——合成对端不走界面，`load-result-requires-recovery` 明写「到 Blocked 为止」，G3 的
恢复向量走的也是合成对端。而现场窗口一（`8005-agv-program#19`）要在真车上打开
`recoveryResumeEnabled` 验这条路：在那里发现它断了，代价是一整个窗口；在这里，31 秒。

**按的是「补偿清空」，不是「申请恢复」。**HMI 上这是两个按钮、两个 `Can...` 属性、两条向量：
「申请恢复」是 `RESUME_AFTER_REPAIR`（恢复停在物理断点的操作，本场景的状态下服务端会拒，而且拒得
对），「补偿清空」是 `COMPENSATE_LOAD_ALL_EMPTY`（前置只要「操作是 Load 且 `RecoveryRequired`」）。
上一条场景的 `L2-RR-06` 读的是前者在不在，那是更弱的问题。

判据链是握手的五步各一条，加上终局：开恢复会话（作用域与管理员角色）→ 报动作并授权 → 收到
`LoadCompensationRequested` 之后才下发命令（`COMPENSATE_LOAD_ALL_EMPTY` 是唯一一个授权时不当场
下发的动作）→ 车载端在真 Modbus 上开锁、取空、回锁 → 服务端对账 `Reconciled`。终局钉的是
**这一单被判死**：需求与仓位操作 `Cancelled`、旅程 `Completed` 记
`CANCELLED_BY_LOAD_COMPENSATION`、业务键永久抑制、那条永远等不到 `LoadResult` 的 `LoadBatch`
命令被结算掉（`8005-agv-program#28` 挖出来的第二个洞，补偿这一路同样走它）。

**补偿不产生「替换结果」，别去找第二行。**`SupersededByResultId` 只由
`WireToGateStore.RequireResumeAuthorizationAsync` 那条路写，那是 `RESUME_AFTER_REPAIR` 的形状；
`LoadCompensationResult` 根本不经过 `OperationResults`，它走
`OnboardRecoveryCoordinator.ApplyCurrentResultAsync` 把这一单判死。`L2-RC-13` 就钉这一条。

现场那三步（发现有货、关门、修传感器）每一步都由执行器的代码要求着，理由见第 16 条与场景文件头。
写这条场景踩出来的三个坑分别记在第 12 条（对话框在树里的位置）、第 16 条（空仓不碰 IO）和第 14 条
的第四例（进度基线取晚了）。

## `real-onboard-restart-while-waiting-operator`：客户端没了之后那一次开锁怎么收场

2026-09-11 现场窗口一停在这一格（`8005-agv-program#40`，证据
`evidence/field/20260911-FW-SC1-operator-inaction/` 帧 `03`）：停靠 2 开锁等操作员时客户端被关掉，
重启后两端互相等——车辆只报一个没了结的 attempt、不补交结果；服务端没有结果就不判
`RecoveryRequired`、不让旅程停摆，会话也不 `Ready` 所以不重放命令；恢复入口又要求旅程已停摆。
断电、崩溃、系统更新重启都落在同一格。

**杀进程，不断网。**车载端执行器挂在服务生命周期上，不跟连接走：断网重连时它还在跑，握手上报的
日志与进程重启后一字不差。只有进程真的没了，那次 attempt 才是没人认领的。`StopComponent` 用
`Kill`，与断电同形；`RelaunchOnboard` 用同一个 stage、同一份 journal 再起一次。客户端死着的时候
操作员把门关上、没放货，照抄现场。

三个红证据，两层产品缺陷加一个驱动时序：

1. **`-001`（服务端 `5cc347f`、车载端 `3cf2665`）**：现场原样——`L2-RW-04` 停在
   `AwaitingLoadResult / ONBOARD_SESSION_NOT_READY / Prepared`，`L2-RW-06` 被拒
   `RECOVERY_DEMAND_NOT_BLOCKED`。修在车载端（`6846e98`）：会话进入 `Ready`/`RecoveryRequired` 时，
   日志里的 attempt 若不在本进程在途集合里、发件箱里也没有它的结果，就不再开锁、按实时 IO 交一份
   `OperationResult`——开过的仓全到最终态且没有未开始的仓为 `COMPLETED`，否则 `UNKNOWN`，物理字段照实填。
   「有没有人在执行」只有车辆知道，所以这件事修不到服务端。
2. **`-002`（车载端 `6846e98`）**：`L2-RW-04/05` 转绿，旅程在库里已经 `Blocked`，`L2-RW-06` 仍被拒
   `RECOVERY_DEMAND_NOT_BLOCKED`——第 19 条。修在服务端 `7401978`。现场即使拿到了结果，那台重启后的车
   在同一条连接上也开不出恢复会话。
3. **`-003`**：两端都修好，驱动点弹窗太早——第 20 条。

**这一条不证补偿在 Modbus 上怎么清仓**：仓是空的，补偿向量一次 IO 都不碰（第 16 条），那是
`real-onboard-recovery-compensate-load` 的事。

**补偿对账之后会话回不到 `Ready`**（`8005-agv-program#46`）。`-004`/`-005` 两次绿里只记不判，timeline
最后一条 `Session after compensation` 写着 `RecoveryRequired / PENDING_FACT_RECONCILIATION_REQUIRED`：
握手上报的 attempt 只在 `RecoveryStateReport` 时写一次，车辆早清掉了、服务端再也不看；
`real-onboard-recovery-compensate-load` 的历次绿证据里则停在 `OPERATION_RECOVERY_REQUIRED`——恢复结果
处理完没人重算就绪。救完一趟旅程车接不了下一单，唯一出路是再重启一次客户端。

现在三条场景都判它：`L2-RW-11`/`L2-RC-15`/`L2-FOC-08` 判服务端会话回到 `Ready`，`L2-RW-12`/`L2-RC-16`/
`L2-FOC-09` 判**车在下一站收下扫码**——车载端只在自己的会话是 READY 时放行提交，所以这一条证的是车辆
也被告知了，不只是服务端库里改了。红证据 `-006`（服务端 `f09768c`）红在 `L2-RW-11`，其余十条全绿；修在
服务端 `369919f`：未结事实按服务端手里的证据判（有结论才算对上账，断网重连时还在跑的 attempt 不放行），
恢复结果处理完重算就绪，就绪变了就发 `SessionReadiness`。修好之后 `-007` 与
`real-onboard-recovery-compensate-load-009` 的会话判据已经转绿，却都以异常收场——红在判据脚本自己，
第 22 条；`9f28ed1` 之后 `-008` **PASS/13**、`real-onboard-recovery-compensate-load-010` **PASS/17**，
`real-onboard-field-operator-compensate-003`（`369919f`）**PASS/10**。

## `real-onboard-multi-demand-compensate`：补偿之后旅程还走不走

前面三条补偿场景（`real-onboard-recovery-compensate-load`、`real-onboard-restart-while-waiting-operator`、
`real-onboard-field-operator-compensate`）**全是单需求旅程**：补偿掉那一条就是整趟结束，服务端直接判
`Completed`，所以一直绿。现场窗口一（`8005-agv-program#45`）组出来的是四需求满仓旅程，要在里面补偿之后接着跑——
那种形状谁都没跑过（`8005-agv-program#47`）。

四停靠：停靠 1 正常装（补偿时车上得有货），停靠 2 经 `FieldOperator.psm1` 的 `Invoke-FieldActUnknownLoad` 制造真的
`UNKNOWN`、`Invoke-FieldActCompensate` 补偿清空，然后判出口；停靠 3、4 正常装，关卡逐条卸完。`L2-MDC-21` 先确认
补偿那一刻车上确实还有别的需求（停靠 1 `Loaded`、停靠 3、4 `Planned`），否则这一格问不出东西。

三个证据，一层产品缺陷、一层场景判据、一层 #48：

1. **`-001`（服务端 `813af00`，修复之前）红在产品**：补偿对账 `Reconciled / ALL_EMPTY`、需求 `Cancelled`、业务键
   抑制、`LoadBatch` 命令结算、会话 `Ready` 全都对，`L2-MDC-30` 却读到 `2/Blocked / CANCELLED_BY_LOAD_COMPENSATION`，
   等满 120 秒不动。`OnboardRecoveryCoordinator.ApplyCurrentResultAsync` 只在 `journeyComplete` 时改 stage，否则只写
   理由码；而补偿与故障货物交接只对 `Blocked` 的旅程授权、引擎对 `Blocked` 只 `return`。修在服务端 `8be28b1`：旅程
   不完整且仍 `Blocked` 时把 stage 交还给被终结那次仓位操作的「等结果」那一格，余下由引擎在 #28 的
   `TerminatedCommandedAt` 收尾分支里按既有判断走（本站再装一轮 / 下一站 / 关卡；在关卡则卸下一条）。
2. **`-002`（`8be28b1`）出口转绿，红在场景自己的 `L2-MDC-32`**：它读 `SessionRecoveries` 等 `Ready / READY`，读到
   `RecoveryRequired / DEPARTURE_SAFETY_NOT_READY`。**那是正常的行驶中状态，不是 #46 回归**：`LoadCompensationResult`
   之后 0.6 秒旅程就过完出发安全检查、车开动，车载端随即报 `VEHICLE_NOT_READY`（`vehicleStopped=false`），会话降级到
   到站为止——停靠 3 出发时一模一样。探针开始读时 Ready 已经过去。现在读持久的那一份：服务端收下
   `LoadCompensationResult` 时连同 `DurableAck` 发给车、缓存在 `ProtocolInbox.FirstResponseJson` 里的
   `SessionReadiness`（`READY`）。同一次还把 `L2-MDC-60` 的诊断写错成 not-gate：`PayloadJson` 里中文站名是 `\uXXXX`
   转义的，按原文匹配对不上；被拒 19 次的那条正是关卡的三项清单。**等一个会被正常流程马上改写的瞬时状态，要读它留下
   的持久记录，不要去赶那个窗口**——与第 14 条是同一类错误的反面。
3. **`-003`（`372c055`）**：关卡之前十二条全绿，`L2-MDC-60` 红在车载端把关卡作业清单判
   `PROTOCOL_SCHEMA_INVALID@关卡清单(3 项) ×12`、`L2-MDC-61` 随之红——都是 `8005-agv-program#48`，与
   `real-onboard-field-window-rehearsal` 的 `L2-FW-40` 同一处。卸货只等 5 分钟。

绿证据 **`-004`**（服务端 `caffdee`，车载端 `96c7513` 即 #48 合并之后）**PASS/15**，约 100 秒：补偿之后旅程自己到
`3/AwaitingPickupArrival`，停靠 3、4 提交，以 `NO_FURTHER_CARGO` 去关卡，三条卸货 `Committed`、旅程 `Completed`；停靠 1、
3、4 的需求 `Succeeded`、停靠 2 `Cancelled`；收尾会话 `Ready / READY`，对账之后再没有仓位操作进 `RecoveryRequired`、
也没开第二个恢复会话。

## `real-onboard-recovery-retry-after-refusal`：被拒过一次之后还能不能再请求

现场救旅程 54d2cf63（`8005-agv-program#44`）时，旅程还到不了 `Blocked` 那会儿有人按过一次「补偿清空」，服务端正确地拒了
`RECOVERY_DEMAND_NOT_BLOCKED`；等旅程真 `Blocked` 了再按，两次都是 `409 ControlServer在旅程会话期间关闭了连接。`。车载端的
会话请求 id 由 attempt 算出，第二次按下带着同一个 messageId、却是新的 `verifiedAt`/`reason`/`sentAt`，而服务端
`ProtocolInbox` 的 `contentHash` 是**整行字节**的哈希——同一个 messageId 再发只有冲突掐连接或回放旧拒绝两种结局
（`8005-agv-program#49`）。修在车载端：恢复请求每次发送都用新 messageId。

**「旅程还没 Blocked」由从假 RIoT 地图上拿掉关卡站造出来**：引擎每轮先 `RequireFixedStation`，认不出就整轮 `return`，
而仓位操作 `RecoveryRequired` 与会话就绪是传输层写的。六个证据：一个绿，一个修复前的红基线，其余四个都至少有一处红在场景自己：

1. **`-001` 用 `faults/http ServerError` 卡引擎，红在场景**：那个开关是整个假 RIoT 的，**车载端也从它读车辆状态**，
   车报 `SafetyUnknownPresent` / `VEHICLE_NOT_READY`，会话在结果出来之前掉进 `DEPARTURE_SAFETY_NOT_READY`，
   `OperationResult` 发不出去（`WIRE_TO_GATE_NOT_READY`）。**要只卡服务端引擎，别动整个假 RIoT。**
2. **`-002` 按钮探针读成空**：`Get-FieldAvailableRecoveryActions` 以 `return , @(...)` 返回，放进 `Wait-L2Condition`
   的 `Probe` 读不到——第 22 条的同一种一元数组包装。改用 `Invoke-FieldActCompensate` 自己的
   `@($s.state.availableRecoveryActions)`，并且等不到也照按，自动化面回的原因码比超时有用。
3. **`-003`（车载端 `96c7513`）与现场原样**，但 `L2-RAR-06` 假绿：它在冲突当场读会话世代，车还没重连。改为数
   `SessionHello`，失败分支先等 20 秒重连。
4. **`-004`（`96c7513`）是判据定稿后的红基线**：`L2-RAR-04/05/06/07` 四条红，服务端只收到一条会话请求，
   `SessionHello 1 → 2`。
5. **`-005`（车载端 `ab346ed`）产品已绿、红在判据**：两条请求行经管道拼成一个字符串（第 22 条）。另实测一条第 22 条没写
   的：**`$x = @(Invoke-L2Query ...)` 的 `Count` 恒为 1**（数组被嵌套一层），`foreach` 拿到的也是整个数组——赋值不要包 `@()`。

绿证据 **`-006`**（服务端 `6af64ab`，车载端 `ab346ed`）**PASS/8**：两条 messageId 不同的会话请求先拒后开，补偿对账
`Reconciled / ALL_EMPTY`、需求 `Cancelled`、旅程 `Completed`，车载端全程 `SessionHello` 1 条。同一个车载端上既有的四条补偿
回归全绿（服务端 `077574d`）：`real-onboard-field-operator-compensate-004` PASS/10、`real-onboard-recovery-compensate-load-011`
PASS/17、`real-onboard-multi-demand-compensate-005` PASS/15、`real-onboard-restart-while-waiting-operator-009` PASS/13。

## `real-onboard-field-window2-rehearsal`：现场窗口二整窗彩排

`8005-agv-program#20`。与 `scripts/field/Invoke-FullLoopFieldDrive.ps1` 同一个编排：第一趟四需求，停靠 1 没人扫码等
站点期限（T）、停靠 2 扫码前取消（X）、停靠 3/4 照常装、关卡第一个开的仓关门不取空两轮（NE）；旅程完成后重启服务端
（R1）；电量 15% 去 211 充电、22% 接上电、80% 释放（CH）；第二趟两需求照常装卸；再重启（R2）。采集器以
`-WindowId FW-FL2 -SublotWaitMinutes 1` 判。

**重启服务端是新加的装置能力**：`Context.RestartServer` 杀掉服务端进程、以同一个库与环境再起，车载端留着自己重连，返回新
进程的启动时刻。与现场的差别是 `Kill` 而不是 `Stop-Service`。

红证据 `-001` 红在采集器（第 24 条），场景本身全绿。绿证据 **`-002`**（服务端 `5015da4`，车载端 `6b8a0b0`）**PASS/40**，
约三分钟：T 期限后 1.1 秒结算、X 以 `CANCELLED_BY_OPERATOR` 抑制、NE 那一仓 `UNLOCKING=3` 期间卸货保持 `Prepared`、R1 从
进程启动到 Ready 3.7 秒（中间一瞬 `RecoveryRequired / HANDSHAKE_INCOMPLETE`）、R2 2.6 秒。去充电桩的路上与第二趟出发时
会话是 `RecoveryRequired`，那是行驶中出车安全不成立，不是缺陷。
