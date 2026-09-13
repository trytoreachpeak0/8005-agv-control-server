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
| `real-onboard-normal-load` | **真的** | 同一条链路，但条码走 UIA、装卸走真 Modbus | `evidence/l2/20260903-real-onboard-normal-load-005` |
| `real-onboard-clock-skew` | **真的** | 车载端时钟偏差的有界容差，界内、界外、恢复三段 | `evidence/l2/20260903-real-onboard-clock-skew-007` |
| `real-onboard-recovery-entry-missing` | **真的** | 装载失败后车上发起不了任何恢复：授权是齐的，入口是缺的 | **红的，而且红得对**，见下 |
| `three-synthetic-peers` | 合成 ×3 | 三个合成车载端同时在线，两侧互不冒充，服务端同时持有三条 Ready 会话（票 09 之前它钉的是相反的边界：accept 循环串行，只有第一台到 READY） | `evidence/l2/20260909-ticket09-three-synthetic-peers-002` |
| `route-graph-engine` | 合成 | 路网引擎开着跑一趟：五个 imap 端点读回、快照不陈旧、可达性判据放行 | `evidence/l2/20260907-ticket12-route-graph-engine-001` |
| `create-gate` | 合成 | 建单前置门禁：目录被完整确认、两端点冻结、两个证据源分别落进审计 | `evidence/l2/20260908-ticket13-create-gate-002` |
| `create-gate-unapproved` | 合成 | **负向证据**：拿掉 `REQ-0302` 的两个已批准值，服务端照常启动但什么都不建 | `evidence/l2/20260908-ticket13-create-gate-unapproved-001` |
| `three-vehicle-exit` | 合成 ×3 | **轨 B 出口**：三台车在同一次运行里各自派单、装货、卸货、走到 `Completed` | `evidence/l2/20260910-ticket18-three-vehicle-exit-001` |
| `command-surface-order-hold` | 合成 ×3 | **轨 B 出口**：一台车的在途单被报成 FAILED，命令面「该调用时调用了、参数正确、只调一次」，另两台不受牵连 | `evidence/l2/20260910-ticket18-command-surface-order-hold-002` |
| `route-graph-staleness` | 合成 | **轨 B 出口**：引擎陈旧态三种触发各一次 fail-closed 证据 | `evidence/l2/20260910-ticket18-route-graph-staleness-001` |
| `emergency-stop-single-trigger` | 合成 | **票 19 的前置**：车在动时单被报 FAILED，急停只发一次——闩锁晚锁、读不到、锁上都不重发；外部解除只重触发一次；原因还在就不解除 | `evidence/l2/20260913-b2close-emergency-stop-single-trigger-002`（修复前的代码上同一场景红：`-prefix-65bffc0c-001`） |
| `slot-configuration-activation-replay` | 合成 | **批次 3 出口（`FP-IS-14`）**：激活「下发 → 断线 → 重连 → 补报」——断线期间服务端不猜，补发同一行，只收敛一次；顺带经 `FieldOps export-audit` 导出这次激活的业务审计（REQ-0271） | 待 CI 三连跑 |
| `onboard-alarm-snapshot-dashboard` | 合成 | **批次 3 出口（`FP-IS-15`）**：车载告警快照「车载产快照 → 服务端消费 → 看板可见」，断言读看板进程渲染出的页面；看板显示全部告警（REQ-0270）、整体取代、失联直述、重连采纳 | 待 CI 三连跑 |

编号更小的目录是同一批里更早的跑次，多数是稳定性复跑。三个是**红的**，各自的原因见文末：
`load-result-requires-recovery-001`（第 6 条）、`real-onboard-clock-skew-001`（第 8 条）与
`-004`（第 9 条）。

方案第 4 节标 ★ 的三条**现在三条都有了**。第三条（车载端时钟偏差）走了最远：合成对端里根本没有
`VehicleSafetySignal.IsFresh` 那段逻辑，真车载端接进来之后逻辑在跑了，但两端同机共用一个时钟，
偏差不会自己出现——补上 `tools/ControlServer.ClockSkewProxy` 才凑齐。

而且它不再是「复现缺陷」：
[`8005-agv-onboard-hmi#1`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/1)
已由 Kun Wang 在 `abb8e73` 修成**有界容差**，所以场景钉的是那个界的两侧加恢复。

`load-result-requires-recovery` **到 Blocked 为止，不跑到「恢复并继续」**：出口是五步恢复握手，
而合成对端不发起它。给它编出那五条出站消息只会让这条场景变绿而证不出任何新东西。

`real-onboard-recovery-entry-missing` 补的是那半条的**入口**，**它现在是红的，而且红得对**。前四条
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

`.github/workflows/l2.yml`，跑在本仓自己的 `headless` runner 上，每次 push 与 PR。现在是四条，合计
约两分钟，证据当作 artifact 传上去（失败时也传——失败那次的证据才是唯一说明原因的东西）。

**真装置那三条刻意不进 CI，两个各自独立的原因：**

1. 它们要交互式桌面会话（会弹两个 WPF 窗口），session 0 的服务模式 runner 根本跑不了。
2. 改挂到交互式的 `golden-renderer` runner 也不行——那会破坏桌面独占。GitHub 的 `concurrency`
   只在单个仓库内生效，所以这里的作业没办法和 `8005-mes-ingest` 的桌面测试在同一台机器上排队，
   而那台机器同时是黄金渲染机。**跨仓库桌面互斥目前没有解**，见工作区根 `CLAUDE.md`。

合成场景不需要对方两个只读仓：`Get-L2PeerPublish` 只在场景 setup 写了 `Onboard = 'Real'` 时才调用。
所以这条流水线不受对方进度影响。

**新写的合成场景记得加进 `l2.yml` 的清单**——那是一份手写数组，不是扫目录得来的。扫目录会把真装置
那几条也一起领进来，而它们在服务 runner 上跑不了。

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

`assertions.json` 的 `identity` 块在完整产品下带两项额外身份（规格 8.4）：

- `protocolReleaseIdentity` —— 从跑起来的服务端 `/version` **读回**，不在脚本里复述。能按协议
  换代作废 L2 证据的只有 build 真的在线上强制的那一份身份；复述一遍只会让证据与脚本自洽而与
  服务端无关。今天读回的是 `protocol-v1.0.0` 候选的九个字段加 `approvalStatus`（`SUPERSEDING_CANDIDATE`）。
- `batchId` —— `Invoke-L2Scenario.ps1` 的 `-BatchId` 参数，默认 `batch-2`。批次是计划，仓库里
  推不出来，所以它是参数而不是常量；CI 显式传，换批次改一个实参。

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

**要改环境启动方式的场景，写一个同名的 `scenarios/<名字>.setup.psd1`。**目前认这些键：

- `Onboard = 'Real'` —— 换成真车载端 + 真模拟器那套装置（默认 `'Synthetic'`）；
- `OnboardSeed` —— 只对合成装置有效，会变成 `--FakeOnboard:Seed:*`，落在握手那条
  `SafetyStateSnapshot` 携带的安全摘要上。`session-established-while-moving` 靠它让会话在
  「车还在动」的状态下建立——`PUT /control/v1/safety` 只能报告一个**已经存在**的会话的变化，
  做不到这件事。与 `Onboard = 'Real'` 一起给会直接报错。
- `ClockSkewMs` —— 只对真装置有效。车辆安全投影改经 `tools/ControlServer.ClockSkewProxy` 转发，
  `observedAt` 往后推这么多毫秒，等价于车载端时钟慢了这么多。合成对端没有新鲜度判定，给它设这个
  键会直接报错。运行时还能通过代理的 `PUT /control/v1/skew` 改。

- `Fleet` —— 主车**之外**的车，每项一对 `AgvId` / `VehicleKey`。编排器把主对放在第一位再逐车
  注入 `JourneyRuntime:Fleet`（`JourneyRuntimeOptions` 的校验器要求名册包含主对，让每个 setup
  文件自己重写一遍主对就是给它一个写错的机会），同时把额外的 key 交给假 RIoT 的
  `Seed:AdditionalVehicleKeys`。**`OnboardPeers` 缺席时按 `Fleet` 逐车派生对端**，一台车一个
  进程一个控制面。为空即单车，走的是空 `Fleet` 那条路径——一台升级上来的单车部署实际跑的就是它。
- `OnboardPeers` —— 直接列合成对端，每项可带 `AgvId`、`Seed`、`WaitForReady`。只想造几条会话
  而不驱动车队时用它（`three-synthetic-peers` 就是），需要服务端真的派车时用 `Fleet`。
- `RouteGraph` —— 路网引擎的配置，`Enabled` 之外的键原样变成 `RouteGraph__*`。默认不写即引擎
  关闭，那是它出现之前那台服务端。
- `CatalogApproved = $false` —— 拿掉 `REQ-0302` 的两个已批准值。**没有「把门禁关掉」的开关**，
  表达「未批准」的唯一方式就是不配置它们。
- `RouteCosts` —— 假 RIoT 的 `getRouteCostsBy` 应答表，键是 `"{mapId}:{stationId}"`，负值是
  RIoT 说的「不可达」。

- `RiotCommands` —— `RiotCommandOptions` 的键原样变成 `RiotCommands__*`。目前只有
  `emergency-stop-single-trigger` 用它把急停重试退避调长：本装置每秒评估一次，默认退避会让「退避内
  又请求了一次」与「退避到期重试」挤在一起。退避是 `REQ-0248` 允许现场设的参数，不是开关。

- `StationDepartureWaitTimeout` —— 服务端 `JourneyRuntime:stationDepartureWaitTimeout`，装载提交后车在取货点
  等多久才请求出发前安全检查（ADR-cross-0055，产品默认 5 分钟）。这段时间是普通放错唯一的修正窗口
  （`REQ-0237`）。本装置不给这个键时用 `00:00:05`，让与修正无关的场景只多等五秒；
  `g3-pickup-load-and-correction` 给 `00:00:20`，它要证修正期间车不走、修正收敛后等满才走。

写成边车文件而不是命令行开关，是因为忘了传开关的那一次，场景会安安静静地证明另一回事。装置选错
更是如此：把 `real-onboard-*` 跑在合成对端上，它会绿，而绿的是完全另一件事。

**写场景时最容易踩的一条：不要把返回查询结果的函数直接送进管道。**`Invoke-L2Query` 用
`return , $rows` 保住整张结果集，而这个包装**穿得过一层 `return`**：

```powershell
function Get-Journeys { return Invoke-L2Query -Connection $connection -Sql '...' }

Get-Journeys | Where-Object { $_.AgvId -eq $id }   # 错：管道里只有一个元素，那个元素是整张结果集
$rows = Get-Journeys; $rows | Where-Object { ... }  # 对：赋值展开了外面那层
```

写错的症状是「三趟 journey 都在库里，却一趟都找不到」——`$_.AgvId` 成员展开成三个值拼成一行，
一条也匹配不上。单行结果时完全看不出来，多行才现形。`Wait-L2Condition` 会吞掉探针里的异常
（那是「还没到」和「探针写错了」共用的路径），所以它表现为一次干等到超时。

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
| ControlServer 协议监听 | 48405 |
| ControlServer 健康检查 | 48407 |
| 假 RIoT | 48408 |
| 假 MesIngest | 48409 |
| 假车载端控制面 | 48420（第 N 台车 48420+N-1） |
| 模拟器 HTTP 控制面（真装置） | 48411 |
| 模拟器 Modbus TCP（真装置） | 48412 |
| 时钟偏差代理（`ClockSkewMs` 场景） | 48413 |

刻意避开现场运行（58105/58107）、staged G3（58205/58207）与 demand-bearing G3（58305/58307）：
撞上了要的是绑不上端口直接失败，而不是悄悄连到另一台服务器上去。模拟器同理不用它自己的默认
58006/1502——那两个是手工联调时开着的那一份，L2 不该连上去。

**2026-09-08 整块从 584xx 挪到 484xx，原因值得记下来。**Windows 的默认动态端口范围是
49152–65535，任何一条出站连接都可能拿走里面的一个端口做源端口。旧的这一块正落在范围内，于是
机器上任何一个不相干的程序都可能在任意时刻占着其中一个——而且真的发生了：一个代理的出站连接
把 58410 绑在 0.0.0.0 上，跑出来是 `SocketException 10013`（权限不足）而不是 10048（已占用），
看上去完全不像端口冲突。固定端口的装置落在动态范围里就是构造性易碎的。

假车载端单独占一块，而不是紧挨着别的组件：对端数量是唯一不固定的那个，旧布局下第二、第三台
车的端口正好压在模拟器的两个端口上。两套装置今天互斥，所以那是一处潜在冲突而不是现行冲突
——正是哪天有人放宽这条互斥时才会炸的那种。

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
11. **让真车载端报一份失败结果，要等满它自己的 120 秒操作员超时。**
    `real-onboard-recovery-entry-missing` 的做法是等到 `WAITING_OPERATOR` 之后关门但不放货。门关了、
    锁上了、开锁输出复位了，唯独货物事实不对——**车载端并不当即判失败**，它等满
    `workflow.operationTimeoutMs`(120 s) 才发结果，而且 `overallOutcome` 是 `UNKNOWN` 不是
    `FAILED`。服务端一样判 `RecoveryRequired`，因为判据是「没有安全完成」而不是「报了失败」。
    等这一步的判据要给到 240 秒。**不要为了跑得快去 stage 副本里调短那个超时**：它是安全相关的
    时序，调短之后场景证的就是一份没人真的在跑的配置。
12. **模态对话框要按 `AutomationId` 找按钮，不要按标题。**`OnWireToGateRecoveryClick` 会弹一个
    `MessageBox` 要现场确认，它是同进程的另一个顶层窗口，得从 `RootElement` 找而不是从主窗口找——
    主窗口这时正停在模态循环里，什么都不答。按钮用 `AutomationId` 认：`MessageBox` 的按钮沿用
    Win32 控件 id（IDYES = 6、IDNO = 7），不随显示语言变，而标题只有中文 Windows 上才是「是(Y)」。
    驱动里是 `$onboard.Confirm('申请恢复原操作')`。
