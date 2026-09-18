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
| `emergency-stop-operator-release` | 合成 | **control-server#63（REQ-0356）**：车在两站之间被急停锁住后，锁住即停稳；确认不全或车上还有未结束订单就拒绝；确认齐全服务端自己发一次 `cancelEmergency`，稍后读到 `OK` 结算；解除后不重触发、不因位置读不到再急停；车再动按新急停处理 | 待本地与 CI 三连跑 |
| `slot-configuration-activation-replay` | 合成 | **批次 3 出口（`FP-IS-14`）**：激活「下发 → 断线 → 重连 → 补报」——断线期间服务端不猜，补发同一行，只收敛一次；顺带经 `FieldOps export-audit` 导出这次激活的业务审计（REQ-0271） | 待 CI 三连跑 |
| `onboard-alarm-snapshot-dashboard` | 合成 | **批次 3 出口（`FP-IS-15`）**：车载告警快照「车载产快照 → 服务端消费 → 看板可见」，断言读看板进程渲染出的页面；看板显示全部告警（REQ-0270）、整体取代、失联直述、重连采纳 | 待 CI 三连跑 |
| `station-deadline-sublot-timeout` | 合成 | **批次 5（control-server#79，ADR-cross-0055、ADR-cross-0058 决策 7）**：到站起算站点期限，没人扫码到期服务端自己结束本站（需求 `Cancelled`、`CANCELLED_BY_STATION_TIMEOUT`、租约与占用释放、录入请求结算、同一 `DemandId` 不再被派）；期限走到一半断联重连，从会话回到 Ready 那一刻重新计满 | 本地 PASS（证据未入库），三连在批次 5 出口 |
| `load-cancelled-before-sublot` | 合成 | **批次 5（control-server#83，ADR-cross-0046 第一种情形）**：到站没人扫码，操作员取消——授权 `slots` 为空，车报 `ALL_EMPTY` 空结果被确认之后服务端才终结（需求 `Cancelled`、`CANCELLED_BY_OPERATOR`、租约与占用释放、录入请求结算、没有仓位命令）；再加到站前断联重连、到站后在新连接上取消（control-server#40 那一格），之后再重连一次录入请求不被重放 | 本地 PASS（证据未入库），三连在批次 5 出口 |
| `load-determinate-failure-and-door-open-timeout` | 合成 | **批次 5（control-server#81，ADR-cross-0058 决策 4、5）**：装货中期限。期限后报确定失败（`FAILED`＋首仓 `OPERATOR_TIMEOUT`、其余 `NOT_STARTED`，全部 `EMPTY`／`LOCKED`／`RESET`）→ 操作 `Failed`、需求 `Cancelled`／`CANCELLED_BY_STATION_TIMEOUT`、租约与占用释放、装货命令结算、无恢复，同一台车接下一单——**防御路径，v2 车载端不产出**；期限后不报结果、仓门未闭 → 挂 `STATION_TIMEOUT_DOOR_NOT_CLOSED`、stage 仍 `AwaitingLoadResult`、开始时间不动，关门撤销，再报 `COMPLETED` 提交 | 本地 PASS（证据未入库），三连在批次 5 出口 |
| `blocked-journey-dashboard-projection` | 合成 | **批次 5（control-server#80，program#55）**：旅程阻断带开始时间上看板——读只读端点 `/api/dashboard/blocked-journeys`：检查点等待挂上即列出、清掉即消失；会话未就绪带会话的三个安全字段、安全证据不全直接最高档，引擎每轮重写这一行而开始时间不动，看板按档上色；装货结果需要恢复停摆后开始时间不变；装货中期限过了门还开着（`STATION_TIMEOUT_DOOR_NOT_CLOSED`，control-server#81 补）列出 `AwaitingLoadResult`、门关上即消失 | 本地 PASS（证据未入库），三连在批次 5 出口 |
| `real-onboard-durable-ack-lost` | **真的**＋协议故障代理 | **批次 5（control-server#88，program#61 ①：cs#77＋onboard-hmi#69）**：丢一次装货结果的 `DurableAck` → 车重连以原 `messageId` 补发、服务端按首次受理重签不掐连接 → 同一连接照常走完握手（两份快照、新 `messageId` 的 `RecoveryStateReport`、`SessionReadiness`），旧报告不补发 → 会话回 `Ready`、旅程走完、只重连一次 | 调试证据不入库，正式证据在 control-server#90 |
| `real-onboard-compensate-then-reconnect` | **真的**＋协议故障代理 | **批次 5（control-server#88，program#61 ②：cs#78＋onboard-hmi#70）**：等人时杀车载端、门被空着关上 → 重启后中断结算报 `UNKNOWN` → 补偿清空对账 → 经代理断一次链路 → CLOSED 的恢复会话快照已被确认、补偿命令已结算，一条都不重放进新会话，车还接得了下一单（`L2-CR-07`，control-server#131 修复前红） | 同上 |
| `real-onboard-restart-while-waiting-operator` | **真的** | **批次 5（control-server#88，program#61 ②：onboard-hmi#70，ADR-cross-0058 决策 2）**：等人时杀车载端、它不在时货放好门关上 → 重启后按实时 IO 补交 `COMPLETED` → 装货提交、会话回 `Ready`、不进恢复，旅程走完；出厂配置 | 同上 |
| `real-onboard-cancellation-authorization-lost` | **真的**＋协议故障代理 | **批次 5（control-server#88，program#61 ③：onboard-hmi#71＋onboard-hmi#78）**：出厂配置下两仓装货、第一仓装好锁上、第二仓开着时按取消 → 丢掉授权应答、车载端报失败 → 再按一次，新 `messageId`、payload 与首发相同 → 取消 `ALL_EMPTY`、需求 `Cancelled`，全程不重连、不替原 attempt 报结果，取货单的车辆占用释放（`L2-CAL-09`，control-server#131 修复前红）；取消先收尾接手的开门再开已装货的仓，模拟器采样里任一时刻至多一仓未锁闭（`L2-CAL-10`，REQ-0357，onboard-hmi#106） | 同上 |

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

`.github/workflows/l2.yml`，跑在本仓自己的 `headless` runner 上，每次 push 与 PR。证据当作 artifact 传上去
（失败时也传——失败那次的证据才是唯一说明原因的东西）。

**PR 上默认每个场景跑一遍，三连按需手动跑**（2026-09-17 起）。清单里每行的 `Runs` 是这个场景的**三连次数**，
不再是每次 PR 都付的次数：

| 模式 | 触发 | 每个场景跑几遍 |
| --- | --- | --- |
| `default` | push、PR | `DefaultRuns`，没写就是 1 |
| `consecutive` | 手动 | `Runs` |
| `consecutive-all` | 手动 | 至少 3（批次出口用） |

手动三连对任意分支随时可跑，排上 runner 就开始，不等夜里：

```powershell
gh workflow run l2.yml --ref <分支> -f mode=consecutive                       # 按各行登记的 Runs
gh workflow run l2.yml --ref <分支> -f mode=consecutive-all -f scenarios=a,b  # 只跑其中几条，各三遍
```

注意 `consecutive` 只按各行登记的 `Runs` 跑，登记 `Runs = 1` 的场景用它挑出来也只跑一遍；要挑几条各跑三遍，用
`consecutive-all`。

作业摘要里有一张表，列出每个场景实际跑到第几遍、结果如何、在哪一路哪个槽位跑的，引用三连证据时贴那次 run 的链接。

### 分路并行（control-server#130，2026-09-18 起）

作业先 `dotnet build ControlServer.sln -c Release` **构建一次**，再把场景分到几路（`L2_LANES`，现在是 4）同时跑，
每一路占一个端口槽位（槽位 1～N，见下面「端口槽位」），路内一个接一个跑，每一趟都带 `-SkipBuild`。分路由
`L2Lanes.psm1` 的 `Get-L2LanePlan` 做：按「单遍估时 × 遍数」从长到短，每次放进当前最空的那一路。估时是 `l2.yml`
里的 `$estimates` 表（09-17 那 31 个作业的中位数），没列的场景按 30 秒算；估时只影响各路是否均衡，不影响跑什么。

为什么这么做：以前所有 L2 共用一组端口、一把锁，29 趟只能排成一队，一次 L2 作业 19 分钟，而 win11-01 的 20 个
vCPU 大部分时间闲着。每一趟里大头是起服务端和替身、再等业务超时（取货站那 30 秒），这些都不占 CPU。

几条规则：

- **同一场景的几遍总在同一路里连着跑。**三连说的是「连着三遍都绿」，分到三路同时跑就什么也证明不了。
- **哪一遍红了，这个场景剩下的遍数不跑，这一路接着跑下一个场景**，和以前一样。
- **PR 头换了，所有路在各自下一趟开始前停下**，同样不取消作业。
- **每一趟的控制台输出写进证据目录旁边的 `<场景>-<遍>.log`**，跑的时候日志里只打每趟的开始和结束一行，全部跑完后
  再按路逐趟分组打出来。几路交织在一起的日志没法读。
- **每一趟的 stage root 放在证据目录下的 `_stage/`**（作业把 `TEMP` 指到那里）。通过的一趟自己删掉，失败的那一趟
  跟着证据一起上传，作业最后一步连同证据目录一起删。以前它们留在 NetworkService 的临时目录里，到 09-18
  攒了 3607 个。
- **并行下哪个场景红了一次，不靠重跑，把它固定到单独一路**，并在 PR 里写明（票的验收标准）。

`L2_LANES` 是 4，因为除槽位 0 外一共只有四个槽位。它依赖 win11-01 的页面文件：一路合成 L2 约占 0.7 GB 已提交内存，
车载端一次 CI 约 2.9 GB，页面文件还是 512 MB 时提交上限只有 8.7 GB，两者撞在一起就会超，超过时内存申请直接失败
而不是变慢。2026-09-18 页面文件调成固定 4 GB，上限 12.0 GB（`remote-ops/factory-server/scripts/06-configure-golden-renderer-vm.ps1 -GuestPageFile`）。

**几个 pwsh 同时启动时，L2 模块里的 class 不能写 `Microsoft.PowerShell.Commands.*` 类型。**class 在模块解析时就编译，
那些类型所在的程序集是 pwsh 懒加载的，几路同时起进程时偶尔还没加载，整趟直接 `ParserError` 死在场景开始之前
（09-18 本机第一次 4 路跑，30 趟里 3 趟；48 次并发导入复现 4 次）。需要的话在方法里按类型名在运行时判断，
`Test-L2Lanes.ps1` 有一条断言守着。

本机要复现 CI 的分路跑法，用同一个模块：先构建，再 `Get-L2LanePlan` + `Invoke-L2LanePlan`。自检：

```powershell
pwsh -NoProfile -File .\scripts\l2\Test-L2Lanes.ps1
```

为什么改：服务端仓只有一个 runner，四张票并行时 PR 排队一小时以上，而三连的第 2、3 遍占了一次 L2 作业的 46%
（run `35171499974`，1354 秒里的 618 秒）。**代价**：除 `DefaultRuns = 3` 的场景外，低频竞态在 PR 上只剩一遍机会。
所以动到时序的票（引擎推进、连接会话、恢复协调、急停，或改动任一 `Runs = 3` 场景）合入前跑一次手动三连。
`session-established-while-moving` 写了 `DefaultRuns = 3`，每个 PR 仍跑三遍：它守的是 CI 上真红过一次的竞态
（`docs/defects/20260916-arrival-trusted-on-a-session-row-pinned-for-one-iteration.md`）。

**PR 头已经换了的运行会自己跳过剩下的场景并正常结束**：作业开头与每个场景之前查一次 PR 当前头，不是本次提交
就打一行 `L2 superseded` 说明后收尾，更新的那次推送有它自己的运行。这里**绝不取消作业**，手动取消与
`cancel-in-progress` 都会让 runner 会话卡死（2026-09-03 空转 4 小时 14 分）。查询失败时照常跑完，不猜；
手动触发的运行从不因此跳过。

### 一张票只跑一轮 CI（2026-09-17 起）

一张服务端票原来平均跑 3～4 轮 CI：开 PR 首跑、改审查意见后、带入顶端后，再加一次手动三连。现在的目标是一轮，
三连也并进这一轮：

1. **工作会话开 PR 一律开草稿**：`gh pr create --draft`。草稿 PR 上 `test` 与 `l2` 两个工作流都显示「跳过」，
   是 GitHub 在分配 runner 之前按 job 级 `if` 判掉的，不占 runner，也不是取消。草稿期间推多少次都不跑。
2. 本地全量测试与票里要求的 L2 场景跑完、调度的审查意见改完、带入集成分支顶端之后，**时序敏感的票在 PR 正文里
   加一行**（行首写，大小写与空格随意，逗号或空格分隔）：

   <pre>L2-Consecutive: three-vehicle-exit, emergency-stop-single-trigger</pre>

   列出的场景这一轮各跑三遍，其余照默认（每场景一遍，`DefaultRuns` 的例外不变）。**写了清单里没有的名字，这一轮
   直接失败**并说出是哪个名字，免得拼错之后三遍悄悄变成一遍。只写 `L2-Consecutive:` 不跟名字也算错。
   时序敏感指：动到引擎推进、连接会话、恢复协调、急停，或改动任一 `Runs = 3` 场景。
3. **`gh pr ready` 转正式，触发唯一的一轮。** 作业摘要表的 `Consecutive` 列标出哪些场景是按 `L2-Consecutive`
   跑的、各自 n/3 的结果，引用三连证据就贴这次 run。
4. 调度会话合并前核对这一轮：L1、L2 都绿，该三连的场景在表里是 3/3。

几条要知道的：

- 工作流只读**触发那一刻**的 PR 正文，改正文不会触发新一轮（刻意没有监听 `edited`）。所以先改正文，再 `gh pr ready`
  或推送。
- 正文里任何以 `L2-Consecutive:` 开头的行都算数，包括代码块里的示例行；PR 正文里要举例时别把它写在行首。
- 转正式之后再推送照常每次都跑（`synchronize`），PR 头已换的旧运行照样自己跳过。已经不是草稿的 PR 不受这套流程影响。
- `workflow_dispatch` 的 `consecutive`、`consecutive-all` 不变，不读 PR 正文。

**真装置那三条刻意不进 CI，两个各自独立的原因：**

1. 它们要交互式桌面会话（会弹两个 WPF 窗口），session 0 的服务模式 runner 根本跑不了。
2. 改挂到交互式的 `golden-renderer` runner 也不行——那会破坏桌面独占。GitHub 的 `concurrency`
   只在单个仓库内生效，所以这里的作业没办法和 `8005-mes-ingest` 的桌面测试在同一台机器上排队，
   而那台机器同时是黄金渲染机。**跨仓库桌面互斥目前没有解**，见工作区根 `CLAUDE.md`。

合成场景不需要对方两个只读仓：`Get-L2PeerPublish` 只在场景 setup 写了 `Onboard = 'Real'` 时才调用。
所以这条流水线不受对方进度影响。

**新写的合成场景记得加进 `l2.yml` 的清单**——那是一份手写数组，不是扫目录得来的。扫目录会把真装置
那几条也一起领进来，而它们在服务 runner 上跑不了。加一行即可：`@{ Name = '<名字>'; Runs = 1 }`，要三连就写
`Runs = 3`（PR 上仍只跑一遍）。`DefaultRuns = 3` 只给 CI 上真红过的竞态用，不要顺手加。

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

**车载端报了一个事实、接下来要看服务端据它拒绝别的东西时，先等服务端把这个事实落库。**
`PUT /control/v1/safety` 在合成车载端把 `SafetyStateChanged` **发出去**时就返回了，服务端未必已经处理。
紧接着改假 RIoT，就是在和服务端的传输层赛跑：control-server#141 里 CI 四路并跑时服务端处理「在动」
用了 183 ms，运行时在这段空当里采信了到站，`L2-MV-08` 红。所以报安全状态用 `Set-L2OnboardSafety`，
它等到 `SessionRecoveries.SafetyRevision` 到了刚发的版本才返回——服务端在存信封的同一个事务里推进这个
revision，`DurableAck` 在事务提交之后才发，到站判定读的也正是这一行：

```powershell
$null = Set-L2OnboardSafety -Onboard $onboard -Connection $connection -AgvId $Context.AgvId `
    -Safety @{ vehicleStopped = $false } -Journal $journal
# 现在再让 RIoT 摆出到站，服务端一定已经知道车在动
```

这个窗口不靠数红绿证明关上了，靠 `Test-L2SafetyDurableWait.ps1`：它在每次安全报告期间拿住服务端库的
写锁（服务端因此迟迟落不了库），原写法（报完不等）每次都红，新写法在同样的延迟下全绿。

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
唯一写着原因的地方。跑通过时删掉；删不掉会打告警。2026-09-18 之前通过的运行其实也删不掉：读库用的只读连接
进了连接池，`Close()` 之后文件句柄还开着，删除静默失败，win11-01 上因此攒了 3607 个 `l2-*` 目录。现在那条连接
不入池（`Pooling=False`），`Test-L2PortLockQueueing.ps1` 的第八组断言两趟通过的运行都没留下 stage root。

## 加一个场景

`scenarios/<名字>.ps1`，接一个 `-Context` 参数。`Context` 上有 `Journal`、`Assertions`、
`Riot`、`MesIngest`、`Onboard`、`Simulator`、`Connection`（只读 SQLite 连接）、`SnapshotRoot`、
`StopComponent`、`InvokeFieldOps`、`DispatchZone`（服务端 `appsettings.json` 里的调度分区）、
`SlotModelVersionId`（默认前置入库的那一版模型，没做入库时为 `$null`）以及车辆与站点的身份。

### 派车场景的默认前置（control-server#71）

批次 4 起，派车要求需求的 AREA 在分区归属表里、车辆有服务端仓位模型，缺一样就一辆车也派不出。所以**经本编排器跑的每个场景（两套装置都算）**，编排器在服务端就绪之后、进入场景之前，经
`InvokeFieldOps` 依次做三步，与现场 W1 窗口用的是同一个 `ControlServer.FieldOps.exe`：

1. `seed-approved-facts` —— 已批准八仓事实入库（1～4 号 `FRONT`，5～8 号 `REAR`）；
2. 对名册里每台车 `bind-io --agv <车>`（`OnboardPeers` 里不在名册上的合成对端不绑，服务端不给它们派车）；
3. `import-area-assignments` —— 默认表：假 RIoT **当时**站点表里每个站点名解析出的 AREA（与 `MapStationResolver`
   同一规则：`_` 分隔的一到三个区号），全部归服务端的调度分区，分组 `FRONT`。默认站点下是 `C15-13`、`N1-3`、`N1-7`。
   导入之前先等服务端把这个分区写进库内调度策略（`DispatchZoneVehicles`），否则导入会判「分区不存在」。

**不设「只对派车场景」的开关**：编排器给每个服务端都写死 `JourneyRuntime__enabled = 'true'`，也没有边车键能关掉它，所以
经它跑的场景都会派车，默认前置一律做；不要的场景用下面两个键退出。

**不做激活握手**：`IVehicleSlotPositionReader` 在车辆没有生效配置时退到该车最新一版已发布 IO 绑定引用的模型
（control-server#66），入库加绑定就足以让派车读到分组。每一步的 JSON 输出以
`slot-model-preseed:seed-approved-facts`、`slot-model-preseed:bind-io:<车>`、`slot-model-preseed:import-area-assignments`
三类判据写进 `timeline.jsonl`，导入的那份 CSV 留在 `snapshots/preseed-area-assignments.csv`；任一步失败整场景失败。

三步都在场景发布第一条需求之前结束，而站点离站期限从车到站才起算，所以默认前置不会被 `StationDepartureWaitTimeout`
截断，不论它是 5 秒还是 30 秒。

`run-journey-g3.ps1` 经本编排器跑它的十个 `g3-*` 真装置场景，所以在它的 ControlServer 绑定挪到含这三步的提交之后，
那十个场景同样获得默认前置（绑定不动，跑的仍是旧编排器）。

与默认前置冲突的场景用下面「`SlotModelPreseed`」「`AreaAssignments`」两个键退出。今天退出的两条：
`slot-configuration-activation-replay`（自己入库、绑定并断言计数，两者都关）、`area-assignment-import-rejects`
（断言导入之前一版表都没有，只关导入）。

### 批次 4 的辅助模块：`L2SlotGroups.psm1`

与 `L2Change.psm1` 同样是单独一个文件，用的场景自己导入：
`Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SlotGroups.psm1') -Force`。

- `Get-L2VehicleSlotPositions -Connection -AgvId` —— 某车的「仓号 → `SlotPosition`」。从 `SlotModelSlots` 经该车的
  记录读：生效配置引用的模型，没有就取最新一版已发布 IO 绑定引用的模型（与 `VehicleSlotModelResolver` 同一顺序）。
  **脚本里不写 1～4／5～8**，那是今天这一版已批准模型的性质，不是车的性质。
- `Get-L2AvailableSlots -Connection -AgvId` —— 服务端就这台车当前会话算出的可用仓：读该会话代次的
  `CapabilitySnapshot` 与 `SafetyStateSnapshot`，规则同 `JourneyRuntimeEngine.SlotAvailable`。
- `Assert-L2SlotGroupTargets -Assertions -Id -Connection -DemandId -SlotPosition [-AvailableSlots] [-Description]` —— 断言需求
  旅程的 `TargetSlotsJson` 全部属于指定分组、严格升序，且恰好是该组内编号最小的 N 个可用仓。判定本体是不碰库的
  `Test-L2SlotGroupTargets`；`scripts/l2/Test-L2SlotGroups.ps1` 用一份刻意不是 1～4／5～8 的构造模型给出两个通过、
  七个各因一种原因失败的例子，并在模块内替换掉两处读库，核对不传与传 `-Description` 时证据行的判据文本（几秒钟，不起装置）。
- `Get-L2StructuralDispatchBlock -Connection -DemandId [-IncludeCleared]`、`Get-L2JourneyBacklogRow -Connection -DemandId`
  —— 读某需求在 `StructuralDispatchBlocks` 的当前行（默认只要未清除的）与 `JourneyBacklog` 的那一行。

`Onboard` 在两套装置下**是两个不同的东西**：合成装置下是假车载端控制面的 `L2Double`，真装置下
是 UIA 驱动（`CanSubmit()` / `SetSublot()` / `SubmitReady()` / `Submit()`）。`Simulator` 只在真装置
下有。一个场景只为一套装置而写，所以这里不需要分支。

`normal-load` 是合成装置的基线，`real-onboard-normal-load` 是真装置的基线：全程顺利，不注入任何
故障。后面每个异常场景都只是在它上面改一处——把车载端某一类应答的策略从 `Auto` 改成 `Manual` 或
`Silent`，或者给假 RIoT 或模拟器注入一个故障模式，然后断言服务端**没有**做它不该做的事。

**两条读取纪律，都来自 MVP 线上「读完一个就顺手读下一个」那一串假红（control-server#26，下面第 14 条）。**

- **断言的实际值来自等待的返回值，不来自等待之后的另一次读取。**要一起断言的第二个事实，若不与被等的条件在同一次
  提交里，就写进同一个等待的 `Probe`，或者自己再等一次。判不准是不是同一次提交，看第 14 条记下的写入边界。
- **「记下一个值、做一件事、再等它变」交给 `Wait-L2Change`**：
  `Wait-L2Change -Baseline {…} -Action {…} -Probe {…} -Until { param($before, $now) … }`，返回 `Baseline` 与 `Value`。
  基线在函数里、紧贴动作之前读；自己分三行写，基线就可能落到动作之后。它在单独的 `L2Change.psm1` 里（批次 4、5
  并行加共享辅助函数，各占一个新文件，不改 `L2.psm1` 主体），用的场景自己导入：
  `Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Change.psm1') -Force`。
  已有场景里手写且写对了的地方不迁移，新写的用函数。

**要改环境启动方式的场景，写一个同名的 `scenarios/<名字>.setup.psd1`。**目前认这些键：

- `Onboard = 'Real'` —— 换成真车载端 + 真模拟器那套装置（默认 `'Synthetic'`）；
- `OnboardSeed` —— 只对合成装置有效，会变成 `--FakeOnboard:Seed:*`，落在握手那条
  `SafetyStateSnapshot` 携带的安全摘要上。`session-established-while-moving` 靠它让会话在
  「车还在动」的状态下建立——`PUT /control/v1/safety` 只能报告一个**已经存在**的会话的变化，
  做不到这件事。与 `Onboard = 'Real'` 一起给会直接报错。
- `ClockSkewMs` —— 只对真装置有效。车辆安全投影改经 `tools/ControlServer.ClockSkewProxy` 转发，
  `observedAt` 往后推这么多毫秒，等价于车载端时钟慢了这么多。合成对端没有新鲜度判定，给它设这个
  键会直接报错。运行时还能通过代理的 `PUT /control/v1/skew` 改。
- `ProtocolFaultProxy = $true` —— 只对真装置有效（control-server#88）。车载端的 `wireToGate` 连接改经
  `tools/ControlServer.ProtocolFaultProxy`（控制面 48415，数据面 48416）转发，场景经 `Context.ProtocolProxy` 布计划：
  `drop-durable-ack`（丢一次某类报文的 `DurableAck` 并断链）、`drop-message`（丢一条服务端应答、链路不断）、
  `disconnect`（不丢任何行、断一次）。代理默认什么都不丢；它的 `/snapshot` 记下每条连接、每一行的信封身份，收尾时存成
  `snapshots/protocol-fault-proxy.json`。合成对端没有 journal 也不重试，给它设这个键会直接报错。

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
  `emergency-stop-operator-release` 出于同一个理由也用它。

- `EmergencyStopRelease = $true` —— 打开 `REQ-0356` 的人工确认解除入口（`EmergencyStopRelease__enabled`），并给它
  配一份本装置用的调用凭据，经 `Context.EmergencyReleaseCredential` 交给场景。产品里入口默认不挂；
  `emergency-stop-operator-release` 用它。

- `StationDepartureWaitTimeout` —— 服务端 `JourneyRuntime:stationDepartureWaitTimeout`，装载提交后车在取货点
  等多久才请求出发前安全检查（ADR-cross-0055，产品默认 5 分钟）。这段时间是普通放错唯一的修正窗口
  （`REQ-0237`）。本装置不给这个键时用 `00:00:30`（control-server#71 起；原来是 `00:00:05`，为什么改见下面「真装置场景」一段）；
  `g3-pickup-load-and-correction` 给 `00:00:20`，它要证修正期间车不走、修正收敛后等满才走。
  **批次 5（control-server#79）起同一个值也从到站起算**：到站后这么久没人录入，服务端就以
  `CANCELLED_BY_STATION_TIMEOUT` 结束本站。所以一条场景若要在 `AwaitingSublot` 停得比它久（让录入挂起、
  到站后先做别的），就要在自己的 setup 里给足；`station-deadline-sublot-timeout` 给 `00:00:20`。

  **真装置场景每一条都落在这条约束里，旧的五秒默认值不够用。**实测「服务端采信到站 → UIA 录入并提交」要
  **4.7–5.4 秒**（`evidence/l2/20260913-b2close-real-onboard-normal-load-003`、
  `20260914-real-onboard-restart-with-open-recovery-session-004` 的 `timeline.jsonl`），与当时装置默认的 5 秒是同一量级——
  等于抛硬币。所以默认值在 control-server#71 抬到了 `00:00:30`（#79 的审查建议）。在那之前，`Onboard = 'Real'` 且会录入的
  场景都在自己的 setup 里写了这个键，这些显式值保留不动：多数给 `00:00:30`；
  `g3-journey-demand-to-pickup` 给 `00:05:00`（它刻意停在 `AwaitingSublot`，到站之后还要等快照确认、读车载端
  日志库、判终态，整条尾巴都在期限内）；`g3-predeparture-check-expires` 由 `00:00:15` 抬到 `00:00:40`；
  `g3-pickup-load-and-correction` 维持 `00:00:20`，因为场景里 `$stationDepartureWait` 与判据文案钉着同一个数，
  改它要连脚本一起改。取值的上界来自装货提交之后那条等待的判据超时（例如 `real-onboard-normal-load` 是 180 秒，
  `g3-predeparture-check-expires` 是 90 秒），下界来自录入那一段，两者之间才是安全区。
  `real-onboard-clock-skew` 与 `g3-manual-charging-return` 不进 `AwaitingSublot`，不受影响，没有加这个键。

下面四个键是批次 4 的仓位分组（control-server#71），默认前置见上面「派车场景的默认前置」。四个键的结构（仓号、字段名、键之间的组合规则，含
`OnboardPeers` 各项自带的 `SlotStates`）都在启动任何进程之前校验，写错直接报错，而不是几分钟后表现成「一辆车也没派出去」；
只有 `SlotStates` 的取值是假车载端启动时按协议枚举校验，写错那台对端启动即失败。

- `SlotModelPreseed = $false` —— 本场景不做默认的入库与绑定。分区归属表的导入要按已发布模型校验分组，所以同时
  **必须**写 `AreaAssignments = $false`，否则编排器直接报错：自己入库的场景自己导入。
- `AreaAssignments` —— 覆盖默认的分区归属表。给 `$false` 表示本场景不导入；给一组行则导入的就是这些行，每行
  `@{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }`，三项都必填，原样写进那份 CSV。
  写 `$true` 或空数组会报错（要默认表就不写这个键）。
- `SlotStates` —— 合成对端握手快照（`CapabilitySnapshot` 与 `SafetyStateSnapshot`）里逐仓状态的覆盖，每项 `SlotNo`
  加 `physicalState`／`administrativeAvailability`／`operability` 中要改的字段，例如
  `@(@{ SlotNo = 1; physicalState = 'OCCUPIED' }, @{ SlotNo = 2; administrativeAvailability = 'DISABLED' })`。
  变成 `--FakeOnboard:Seed:slotStates:<i>:*`，未给的仓保持 `OPERABLE`／`ENABLED`／`EMPTY`／`LOCKED`／`RESET`；取值按协议枚举
  校验，写错的值假车载端启动即退出。给所有合成对端；`OnboardPeers` 某一项自己带 `SlotStates` 时那一台用自己的。
  与 `Onboard = 'Real'` 同给直接报错（真装置读自己的 IO）。**只支持握手种子**：服务端只从会话的两份快照读可用仓，
  而假车载端没有运行中改逐仓状态的入口，要换状态就得换种子重起对端，同一次运行里做不到。
- `Stations` —— 覆盖假 RIoT 在本场景地图上的整张站点表，`@{ '210' = '关卡'; '12' = 'N1-3_N2-5' }` 这样的站点号到站点名。
  在服务端启动之前经假 RIoT 控制面 `PUT /control/v1/maps/{mapId}/stations` **整张替换**（命令行种子只能往默认表里加），
  所以表里要自己留着关卡 `210 关卡` 与场景要用的取货点（`Context.PickupStationRiotId` 仍是 12）。默认分区归属表按替换后的
  站点名推 AREA。开了路网引擎的场景另需站点所在节点，这个键不管。

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
| 看板（`Dashboard` 场景） | 48414 |
| 协议故障代理控制面／数据面（`ProtocolFaultProxy` 场景） | 48415 ／ 48416 |

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

### 端口槽位（control-server#130）

上表是**槽位 0**，也是 `Invoke-L2Scenario.ps1` 不带 `-PortSlot` 时用的那一组。`-PortSlot N`（1～4）把整组端口
下移 `1000 × N`，锁名加后缀 `-slotN`：

| 槽位 | 端口 | 锁 |
| --- | --- | --- |
| 0 | 48405–48416，假车载端 48420 起 | `Global\W2G-L2PortBlock` |
| 1 | 47405–47416，假车载端 47420 起 | `Global\W2G-L2PortBlock-slot1` |
| 2 | 46405–46416，假车载端 46420 起 | `Global\W2G-L2PortBlock-slot2` |
| 3 | 45405–45416，假车载端 45420 起 | `Global\W2G-L2PortBlock-slot3` |
| 4 | 44405–44416，假车载端 44420 起 | `Global\W2G-L2PortBlock-slot4` |

**槽位 0 的端口与锁名和加槽位之前逐字相同**，真装置场景、`run-journey-g3.ps1` 和比槽位更早的检出都不知道槽位的
存在，照旧拿槽位 0，彼此照旧排队。CI 的分路只用槽位 1～4，所以不会和它们抢。

为什么往下移：槽位 0 已经紧贴在 Windows 动态端口范围（49152 起）下面，往上就进了动态范围，那正是 2026-09-08
搬家要躲的东西（见上一节）。往下这一段里唯一被系统保留的是 47001（WinRM 的 HTTP 监听，控制端笔记本
`netsh int ipv4 show excludedportrange protocol=tcp` 里有，win11-01 上只保留了 5357），各槽位都躲开了。
`Test-L2PortLockQueueing.ps1` 断言槽位 0 的端口和锁名是上表的字面值、编排器参数默认值就是槽位 0、五个槽位两两
不重叠且都在 49152 以下、不碰保留端口，并且槽位 0 被占着时槽位 1 照样拿得到锁。

**几个槽位同时跑时共用一份构建输出。**同一个检出里并行跑，要先构建一次，每一趟都带 `-SkipBuild`；否则一趟的构建
会覆盖另一趟正在运行的 exe。`-SkipBuild` 时构建输出不存在会直接失败。

### 同一个槽位同一时刻只能有一个 L2

同一个槽位里，所有装置、所有场景用的都是**同一组固定端口**，所以两个 L2 同时跑不是「偶尔撞一下」，而是必然
互相串台（2026-09-14 真出过，见文末第 13 条）。`Invoke-L2Scenario.ps1` 因此在**构建之前**先拿一把
机器级命名 mutex `Global\W2G-L2PortBlock`（`L2PortLock.psm1`），一直拿到收尾把所有组件停掉之后才放。
拿不到就排队，最长等一小时，排队时会打印：

```
L2_PORT_LOCK_WAITING: another L2 run owns this machine's L2 port block (Global\W2G-L2PortBlock); queueing up to 3600s for: ...
L2_PORT_LOCK_ACQUIRED: the L2 port block is now ours for: ...
```

打出 WAITING 之后迟迟没有 ACQUIRED，是前面那一趟还没跑完；什么都没打，说明根本没排队。

几件要知道的事：

- **放在构建之前，不只是为了端口。**构建会覆盖 `bin/` 下的 exe，而同一工作树里正在跑的那一趟正是从
  那里启动的这些 exe。
- **锁在脚本里面拿，不在外面包一层。**手工直接跑、CI、`run-journey-g3.ps1` 调它，三种入口都要被
  保护到；包在外面的话，直接敲命令那一种就漏了。和桌面锁的理由一样。
- **和桌面锁的顺序：先端口锁，后桌面锁，释放时反过来。**只有真装置场景两把都拿，其它持有者只拿
  其中一把（staged G3 的三个脚本和 `8005-mes-ingest` 只拿桌面锁），所以等待关系成不了环，不会死锁。
  代价是一趟真装置场景在排桌面锁时手里握着端口锁，合成场景只能排在它后面。反过来先拿桌面锁不行：
  那样构建和发布对端的整段时间都占着桌面，`8005-mes-ingest` 的桌面测试得等我们的编译器。
- **这把锁对所有已登录账户开放，这一点和桌面锁不同。**`win11-01` 上 CI 的合成场景以 NetworkService
  身份在 session 0 里跑，真装置场景是人在交互账户下跑，用的是同一组端口。默认 DACL 的命名 mutex
  只有创建它的账户打得开，另一个账户会直接抛 `UnauthorizedAccessException` 而不是排队，所以 DACL
  给了 Authenticated Users 完全控制。**跨账户这一条目前只验证了 DACL 确实写上了，还没有在两个账户
  之间真跑过。**
- **比这把锁更早的检出不拿锁，锁管不到它们。**`run-journey-g3.ps1` 也在其中：它从**绑定提交**的
  克隆里跑编排器，所以在 ControlServer 绑定挪到含这把锁的提交之前，journey G3 仍然不拿锁。
- **其它 G3 runner 不用这组端口，所以不拿这把锁。**`run-staged-g3.ps1` 用 58205/58207/58215/58216，
  `run-staged-g3-restart.ps1` 用 58105/58107，`Invoke-AuthorizedAbsentObservationShadow.ps1` 用
  58888/59005/59007，这三个还共用模拟器默认的 1502/58006，彼此之间靠桌面锁串行；
  `run-demand-bearing-g3-vectors.ps1` 用 58305/58307。

**锁拦不住的，由启动等待的端口归属检查兜底。**每个启动等待在探针成立之后，还要确认那个端口上
监听的**就是这一趟自己起的那个进程**（`Wait-L2Condition -Port` → `Assert-L2PortOwner`，读的是
`netstat -ano`）。不是的话立刻失败，并点名占着端口的进程和它的路径：比锁更早的检出在跑的 L2、被杀掉
那一趟遗留下来的替身、不相干的程序，都会这样被报出来，而不是跑出一份测的其实是别人进程的证据。
进入场景之前还会把所有启动过的组件再确认一遍都还活着。

自检：

```powershell
pwsh -NoProfile -File .\scripts\l2\Test-L2PortLockQueueing.ps1
```

前七条只测锁本身和编排器里的拿锁顺序，几秒钟；第八条真起两个编排器跑 `normal-load`，证明第二个
在第一个跑完之前一直排着、放锁之后才开始构建，而且两个都 PASS，大约一分钟（`-SkipOrchestrators`
跳过这一条）。锁正被占着、或者端口上已经有监听时，它拒绝开始，免得红在别人的运行上。

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
13. **两个 L2 同时跑会互相串台，而启动等待看不出来。**2026-09-14 10:28，一个会话在跑真装置的
    `g3-exception-resume`，另一个会话几乎同一秒起了 `three-synthetic-peers` 和
    `slot-configuration-activation-replay`。合成那边先绑上了 48408/48409，G3 这趟自己的假 RIoT、
    假 MesIngest 启动即死（`SocketException 10048`，address already in use），可是「fake RIoT is
    live」第一次探测就成立了——答话的是**对方**的假 RIoT。`Wait-L2Condition` 只在探针**不成立**时
    才看组件死没死，之后也再没有哪个等待看过这两个句柄，于是这趟带着两个死掉的替身跑进了场景，四分钟
    后在一个毫不相干的判据上超时。另一边是对称的：合成那趟自己的 ControlServer 绑 48405 失败退出，
    `/health/live` 却由 G3 那趟的服务端答了，最后报出来的是假车载端握手收到 `SessionRejected`，
    点名点错了组件。「开跑前看看端口有没有人监听」救不了这件事：检查的那一刻对方还一个端口都没绑。
    修法是上面「同一时刻只能有一个 L2 占着这组端口」那一节的端口锁，加上启动等待的端口归属检查。
    两份红证据在仓库之外：`C:\g3dbg\resume-002`、
    `C:\g3dbg\20260914-l2query-slot-configuration-activation-replay-001`。
14. **读到一个状态就顺手读另一个，而两者落在不同的写入里，就会间歇性假红或漏记。**这一条是从 MVP 线
    （`ControlServer_MVP` 的 `scripts/l2/README.md` 第 14 条）搬过来的：那边前后六例，有等到仓位操作
    `Committed` 就直读旅程、读到还没轮到的旧阶段的；有基线取在动作之后、基线里已经含着要等的那一次开锁、
    于是永远等不到的——同一份脚本一绿一红，这就是这类竞态的样子。2026-09-13 普查
    （control-server#26）之后收口成两样东西：上面「加一个场景」一节的两条读取纪律，与 `Wait-L2Change`。
    **修一例的时候，要把同一形状的其他地方一起找出来**：那边第五例和第三例是同一个模板抄出来的，第三例只修了
    出事的那一条。

    判法只有一条：直读的东西要么与被等的条件落在**同一次提交**，要么在因果上**必然先于**它落库，否则就是这种
    形状。v2 服务端的写入边界，核对过的记在这里，下次不必再读一遍服务端（行号会漂，按名字查）：
    - **站点期限到期结束本站**（`JourneyRuntimeEngine.TryEndStopAtStationDeadlineAsync` →
      `PickupStopTermination.StageAsync`）一次提交：需求 `Cancelled`、调度租约 `ReleasedAt`、取货单的
      `VehicleOccupancyReleasedAt`、录入请求在发件箱里结算、旅程 `Completed` / `CANCELLED_BY_STATION_TIMEOUT`。
      等到旅程 `Completed` 再读这几样是安全的。
    - **到站那一轮**：车辆业务状态、工作清单、计划、录入请求四条出站报文各自在发布时落库
      （`WireToGateStore.QueueOutboundEnvelopeAsync` 每条一次保存），之后引擎才保存 `AwaitingSublot`；期限起点随工作清单那次
      保存一起落库。等到 `AwaitingSublot` 再读这几样是安全的，反过来不是。
    - **装货结果**由消息处理器收下时写 `StationOperations.Status = Committed`（`ApplyOperationResultAsync`），旅程转
      `AwaitingStationDeparture` 是引擎下一轮的另一次写入。卸货结果那一次提交里有需求 `Succeeded`、租约释放与
      `TransportDemandCompletions`，旅程 `Completed` 与车辆占用释放仍是引擎之后的另一次写入。
    - **重连**：`BeginSessionRecoveryAsync` 把会话退回 `HANDSHAKE_INCOMPLETE` 的同一次保存里作废本车旅程的期限起点；
      重新计满是会话回到 Ready 之后引擎某一轮的另一次写入。所以「重连之后期限起点变了」要等，不能在
      重连命令返回时直读。
