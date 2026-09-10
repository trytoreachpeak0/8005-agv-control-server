# 缺陷：车断线之后，看板一直显示它失联前的最后一批告警

Status: fixed
Owner repository: `8005-agv-control-server`
Found by: [`L2 onboard-alarm-snapshot-dashboard 第一次运行`](../../evidence/l2/20260910-batch3-onboard-alarm-snapshot-dashboard-001/)
Product at discovery: `fp/b3-on-v2@e3c7c68`（`src/` 与 `6dc4bc8` 之间只多了 `AuditExport`，与本缺陷无关）
Peers: 合成车载端 `tools/ControlServer.FakeOnboard`（同一工作树）；协议 `fp/v2-candidate@f6ee75defe6e2d18f63f4082bee445dbb678ab1b`

## 现象

批次 3 出口的 L2 场景 `onboard-alarm-snapshot-dashboard` 第七条断言红。前六条全过：握手快照被收下、看板显示
「无」、进看板的那条可见而只该出现在车上的那条不可见、新快照整体取代旧快照、一车一行。然后合成车载端断开
会话，场景等看板显示「车辆失联」，等满六十秒：

```
WARNING: Timed out after 60s waiting for: the dashboard says the vehicle is out of contact.
Last observed: "AGV-L2-001  L2_ALARM_FLEET_SECOND"
```

运行结束时库里那台车的会话行：

```
"Readiness": "Ready",
"ReasonCode": "READY",
"UpdatedAt": "2026-09-10 08:34:28.6057731+00:00"   ← 握手那一刻，断线之后没再动过
```

**REQ-0269 的失联直述没有兑现。**车已经不在了，看板仍把它当成在线，显示一份不确定新旧的旧告警——那正是
REQ-0269 与 #16 验收标准明文禁止的形态。

## 根因

`OnboardAlarmProjectionStore.ReadDashboardProjectionAsync` 判「在线」只看 `SessionRecoveries.Readiness == Ready`。
而车断线时 `OnboardTcpServer` 只 `peer.Detach(...)` 把连接摘掉，落库的会话行原样留着 Ready。

**后一半是对的**：恢复握手靠的正是这一行，断线不改它符合设计。`JourneyRuntimeEngine` 早就知道这件事，
派车前的注释原话是 a dead peer leaves a Ready row and its last snapshots behind，并因此另加了一层「多久没听到
这一代会话」的存活判定。**看板这一侧没有加。**

## 为什么没被发现

`OnboardAlarmProjectionTests.AVehicleOutOfContactShowsTheReasonRatherThanItsLastKnownAlarms` 用**删掉会话行**来模拟
失联。产品从来不这么做，所以那条测试证的是一个不会发生的状态转移，而真实的形状——行还在、还是 Ready、只是
安静了——没有任何一层测过。G3 的 `FP-IS-15` 断言只读库里的快照行，不读看板投影，也看不到。

这是 L2 存在的理由：它是唯一在真服务端进程上把「断线」与「看板渲染」放进同一次运行的层。

## 处置

判「在线」改成两样都要有：会话行是 Ready，**并且**服务端在 `OnboardAlarmProjectionStore.LinkLivenessTimeout`
（六秒，ADR-cross-0027 的心跳存活超时）之内收到过**这一代**会话的任何入站消息。规则与 `JourneyRuntimeEngine`
那一处一致：

- 取服务端收件时间，不取载荷时间——一个停走或配错的车载时钟不能让死会话看起来活着；
- 任何入站消息都算，心跳两秒一次；
- 收件时间晚于此刻的不算；
- 只认会话行上那一代，上一代最后那条心跳再新也不替这一代说话。

只读，不改会话行。代价与引擎那一处相同：agvId 与会话代只在信封 JSON 里，要扫一遍收件箱；只有落在存活窗口
之内的行才被解析。

**没有选的两条路**：给会话行加一列 `LastInboundAt`（要新迁移，并且每条入站消息多一次写）；断线时把会话行改掉
（抓不到「连接开着但一直不说话」的车，还会碰恢复语义）。

## 回归守卫

- `OnboardAlarmProjectionTests.AReadyRowWhoseSessionHasGoneQuietShowsTheReasonRatherThanItsLastKnownAlarms`：三台车，
  一台刚听到、一台超过存活超时、一台最后那条消息属于上一代——后两台显示失联、不显示旧告警，会话行一个字没改。
- 测试夹具 `MarkSessionReadyAsync` 现在同时写一条入站消息：看板判在线要两样都有，夹具不再能造出「只有 Ready
  行」这种产品里只在死车身上出现的状态而把它当成在线。
- L2 `onboard-alarm-snapshot-dashboard` 的 `L2-OAS-07`（失联直述）与 `L2-OAS-08`（重连后恢复显示）。

## 仍然开着的

**车队会话卡片有同一类问题，本次没有修。**`FleetSessionsQueryEndpoint` 直接把 `SessionRecoveries.Readiness`
投到看板上，一台断了线的车在那张卡片上同样一直显示 `Ready`。它不在批次 3 两个切片的验收标准里，也没有场景
在看它；记在这里，免得「告警卡片修好了」被读成「看板的失联判定修好了」。

**`FP-IS-15` 的 `CONTROL_SERVER_G2` 与 G3 证据绑的是 `6dc4bc8`**，本次改了这个切片的服务端代码
（`OnboardAlarmProjectionStore` 与 `FP-IS-15` 名下的测试），那两份不再绑定现行产品代码。
