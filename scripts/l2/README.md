# L2 场景编排器

把「必须有人站在真车前面」的验证变成一条命令。

```powershell
pwsh .\scripts\l2\Invoke-L2Scenario.ps1 -Scenario normal-load -EvidenceRoot .\evidence\l2\<新目录>
```

一趟 14 到 30 秒，全程无人值守。这条链路 2026-09-03 在厂区里跑掉了一个下午。

方案与落地顺序见
[`8005-agv-program/docs/wire-to-gate-test-automation.md`](https://github.com/trytoreachpeak0/8005-agv-program/blob/main/docs/wire-to-gate-test-automation.md)，
这里是它第 3 节「缺口 4」和落地顺序第 3、4 步的产物。

## 现有场景

| 场景 | 讲什么 | 绿证据 |
| --- | --- | --- |
| `normal-load` | 全程顺利的基线，不注入任何故障 | `evidence/l2/20260903-normal-load-014` |
| `session-established-while-moving` | 会话在车辆运动中建立，随后停稳；到站也要按最新的安全状态判 | `evidence/l2/20260903-session-established-while-moving-004` |
| `load-result-requires-recovery` | 装载跑掉操作员超时，旅程与整台车正确停摆 | `evidence/l2/20260903-load-result-requires-recovery-004` |

编号更小的目录是同一批里更早的跑次：`-001` 到 `-003` 是稳定性复跑，
`load-result-requires-recovery-001` 是**红的**，留着的原因见文末第 6 条。

后两条是方案第 4 节标 ★ 的三条里能做的两条。第三条（**车载端时钟慢于服务端 100 ms**）在这一层
做不了：缺陷在车载端的 `VehicleSafetySignal.IsFresh`
（[`8005-agv-onboard-hmi#1`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/1)），
合成对端根本没有那段逻辑，用它「复现」出来的只会是自己写的假象。要等落地顺序第 5 步的车载端 UIA
驱动。

`load-result-requires-recovery` **到 Blocked 为止，不跑到「恢复并继续」**：出口是车载端的五步恢复
握手，而车载端从不发起
（[`8005-agv-onboard-hmi#4`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/4)）。
服务端侧的出口是完整的且有 L1 测试（`RecoveryStateMachineG2Tests`）。给合成车载端编出那五条出站
消息只会让这条场景在现场仍然停摆的时候变绿。

## 现在能跑什么

| | 真的 | 替身 |
| --- | --- | --- |
| ControlServer | ✅ 真进程、真 SQLite、真协议监听 | |
| RIoT | | `tools/ControlServer.FakeRiot` |
| MesIngest | | `tools/ControlServer.FakeMesIngest` |
| 车载端 | | `tools/ControlServer.FakeOnboard`（合成协议对端） |
| 仓位模拟器 / 真实 IO | | 尚未接入 |

**这还不是方案里定义的 L2。**方案的 L2 是「真 ControlServer + 真车载端 WPF + 真模拟器 + 假
RIoT」；这里的车载端是一个合成协议对端，没有 IO、没有 journal、没有操作员。它能证明的是**服务端
在一个守协议的对端面前的跨端时序**——也就是让场景真正关于服务端。换成真车载端要等落地顺序第
5 步（UIA 驱动）和第 7 步（交互式桌面会话）。

**L2 PASS 不代表现场合格。**没有真实 RCS、没有真车、没有交通管制、没有真实 IO 模块与接线。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。

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

**不走捷径断言。**状态只从服务端自己的 SQLite 库和各替身的 `/control/v1/snapshot` 读——运维在
现场看的就是这两处。绕过被测方摆出终态，测的就只是脚本自己。

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
`Riot`、`MesIngest`、`Onboard`、`Connection`（只读 SQLite 连接）、`SnapshotRoot`、
`StopComponent` 以及车辆与站点的身份。

`normal-load` 是基线：全程顺利，不注入任何故障。后面每个异常场景都只是在它上面改一处——把车载端
某一类应答的策略从 `Auto` 改成 `Manual` 或 `Silent`，或者给假 RIoT 注入一个故障模式，然后断言
服务端**没有**做它不该做的事。

**要改环境启动方式的场景，写一个同名的 `scenarios/<名字>.setup.psd1`。**目前认 `OnboardSeed`，
它会变成 `--FakeOnboard:Seed:*`，落在握手那条 `SafetyStateSnapshot` 携带的安全摘要上。
`session-established-while-moving` 靠它让会话在「车还在动」的状态下建立——`PUT /control/v1/safety`
只能报告一个**已经存在**的会话的变化，做不到这件事。写成边车文件而不是命令行开关，是因为忘了传
开关的那一次，场景会安安静静地证明另一回事。

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

刻意避开现场运行（58105/58107）、staged G3（58205/58207）与 demand-bearing G3（58305/58307）：
撞上了要的是绑不上端口直接失败，而不是悄悄连到另一台服务器上去。

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
