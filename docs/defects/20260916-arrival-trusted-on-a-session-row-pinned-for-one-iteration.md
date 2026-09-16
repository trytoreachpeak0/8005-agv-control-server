# 缺陷：会话行被整轮迭代钉住，服务端采信了一次车载端已经否定的到站

Status: fixed
Owner repository: `8005-agv-control-server`
Found by: CI [run 35055524167 attempt 1](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35055524167)
的合成 L2 场景 `session-established-while-moving`
Product at discovery: `fp/v2-impl@130954e02bf01d8e51e95e0914e461902790e865`
Peers: `protocol-v1.0.0@9f22db825d52ad86c1d803bd0c1925dcc58d6793`（protocolVersion 2、
`AGV_FULL_PRODUCT`）、合成车载端（`tools/ControlServer.FakeOnboard`）
红证据：`evidence/l2/red-ci-35055524167-attempt1-session-established-while-moving/`（工作区侧）

这是 [`20260903-onboard-safety-facts-frozen-at-session-start.md`](20260903-onboard-safety-facts-frozen-at-session-start.md)
D-1 同一类的第二个窗口。上一次冻结的跨度是整个会话，这一次是一轮 journey 迭代——短得多，
但方向相同，而且是危险的那个方向：**车载端已经报告车辆在动，服务端仍然采信了 RIoT 的到站。**

## 现象

`session-established-while-moving` 第 3 步的判据 `L2-MV-08`：

| 判据 | 期望 | 实际 |
| --- | --- | --- |
| 车载端仍报运动时，服务端不采信 RIoT 的到站 | `AwaitingPickupArrival` | `AwaitingSublot` |

同一跑里其余 30 个场景全 PASS，原样重跑（attempt 2）全绿，此前 11 次 CI 连续 SUCCESS。
也就是说这是一个窄窗口的竞态，不是新引入的回归——尤其不是 PR #102 引入的，那条改动不经过
这条路径。

## 时序（证据里的本地时间，UTC+8）

| 时刻 | 发生了什么 | 出处 |
| --- | --- | --- |
| 12:26:36.9x | journey 迭代开始，`AdvanceAsync` 读会话行，此时 `SafetyRevision = 2`（车停稳） | `logs/control-server.out.log` 里该轮的 `SELECT ... FROM "SessionRecoveries"`，排在下一行之前 |
| 12:26:36.97 | 车载端发出 `SafetyStateChanged`，`vehicleStopped=false` | `snapshots/fake-onboard.json` 的 `wire` |
| 12:26:36.9x–37.00 | 服务端在**传输层自己的 scope** 上落库：`UPDATE "SessionRecoveries" SET ... "SafetyRevision"`，随后写入信封 | 同上日志 |
| 12:26:37.004 | 服务端回 `DurableAck` | `snapshots/fake-onboard.json` |
| 12:26:37.07 | 场景让 RIoT 摆出一次完整到站（订单终态、车在目标站、速度零） | `timeline.jsonl` |
| 12:26:37.149 | 服务端发出 `UpcomingStopPlanSnapshot`——到站被采信，`Stage` 翻到 `AwaitingSublot` | `snapshots/fake-onboard.json`、`UPDATE "JourneyRuntimes" SET "Stage"` |

关键是最后两行：服务端在 37.004 就已经确认收下了「车在动」，145 毫秒后仍然采信了到站。

## 机理

`JourneyRuntimeWorker` 每轮迭代开一个新的 DI scope，所以一轮迭代用一个
`ControlServerDbContext`。`AdvanceAsync` 在开头读一次会话行，接着做 RIoT 的往返
（`EnsureMovementConfirmedAsync`、建单、对账、读车），一轮下来可以跨几百毫秒；到站判定在这之后。

`CurrentReadySessionAsync` 原本是**跟踪查询**。EF Core 的默认行为是：查询虽然照发 SQL，但
遇到变更跟踪器里已有的同主键实体，返回的是那个已有实例，**不会用数据库的新值覆盖它**。
车载端的 `SafetyStateChanged` 走的是传输层自己的 scope、自己的 DbContext，所以它对数据库的
写入对这一轮迭代不可见——迭代开头读到的 `SafetyRevision` 把这一行钉住到迭代结束。

如果到此为止只是「读到旧值」，那还只是慢一拍。真正把它变成安全问题的是
`ReadOnboardFactsAsync` 取安全摘要的方式：它按 `SafetyRevision` 去收件箱里**精确匹配**那条
消息（`LatestSafetySummaryForSessionAsync`，这正是 2026-09-03 那次修复引入的设计）。旧的
revision 不会匹配不上而失败关闭——它会**匹配到上一条消息**，那条消息还在收件箱里，内容是
「车停稳了」。于是：

```
session.SafetyRevision = 2 (陈旧)
  → 取到 safetyStateVersion=2 的 SafetyStateChanged
  → onboard.VehicleStopped = true
  → CheckArrivalAsync 采信到站
```

按 revision 精确匹配的本意是「对不上就说明会话行与收件箱不一致，此时失败关闭」。这个前提在
两者由同一个事务推进时成立；一旦读到的 revision 本身是陈旧的，匹配就变成了「安静地读到一个
过期但看起来完全合法的事实」，没有任何一层会为此报警。

## 为什么归属在服务端，而不是场景写得不够紧

场景在放 RIoT 的到站进来之前，已经等到车载端把 `SafetyStateChanged` 发出去；服务端也已经
回了 `DurableAck`，即「收下并落库了」。在这之后服务端仍然按更早的安全事实判定，这不是场景
催得太急——`DurableAck` 就是服务端自己给出的「我已经知道了」的凭据。场景不做任何改动。

## 修复

`JourneyRuntimeEngine.CurrentReadySessionAsync` 改为 `AsNoTracking()`。

引擎对这一行**只读**，每一次写都属于车载端传输层；不跟踪它，就没有可被钉住的实例，
`ReadOnboardFactsAsync` 每次都按判定当时的数据库状态取 revision。派车准入
（`ReadOnboardFactsAsync` 的另外两个调用点）与到站判定走的是同一个方法，一并受益——准入方向
上同一个窗口会把一台正在移动的车判为可派，那比误采信到站更危险。

`.github/workflows/l2.yml` 里这条场景同时改成连跑三次。它不是新场景，但它现在有前科：一次
race 在 12 次里只现了 1 次，单跑一次分不出「对」和「这次凑巧排对了」。

验证：全量 L1 832 项通过；本地连跑三次 L2 全绿，证据在工作区
`evidence/l2/fix-stale-session-20260916-session-established-while-moving-0{1,2,3}/`。

回归测试：`ArrivalIsNotTrustedWhenOnboardReportsTheVehicleMovingDuringTheSameIteration`。
它按真实形态复现：变更由**另一个 DbContext**（模拟传输层 scope）在迭代进行中写入，落点在
`CheckArrivalAsync` 读 RIoT 车辆状态与读车载事实之间。既有的
`ArrivalIsNotTrustedWhileTheLatestSafetyStateSaysTheVehicleIsMoving` 照不出这一条，因为它
把变更写在两轮迭代之间、而且走的是引擎自己的 DbContext。

## 仍然留着的一处，没有一起改

`ValidatePreDepartureSafetyAsync` 要求车载端的 `PreDepartureSafetyCheckResult` 的
`safetyStateVersion` 等于 `session.SafetyRevision`，而那个 `session` 是 `AdvanceAsync` 开头
读的那一份，在这一轮迭代里同样不再刷新。迭代中途来的新 revision 会让答复对不上而
**失败关闭**（记 `PRE_DEPARTURE_SAFETY_NOT_VALID`，下一轮重问），所以它不会像到站判定那样
悄悄放行。没有失败用例能证明另一个方向确实可达之前不动它——那条路径自己有历史
（[`20260913-expired-predeparture-check-never-asked-again.md`](20260913-expired-predeparture-check-never-asked-again.md)），
改它需要它自己的证据。

## 影响

修复前，任何一次「车载端在一轮 journey 迭代进行中报告车辆开始移动」都可能让服务端采信一次
并不成立的到站，或把一台正在移动的车判为可派。窗口是一轮迭代的长度（默认轮询周期内、一轮
RIoT 往返的时间），命中需要变更恰好落在迭代开头的会话读之后，所以在 CI 上 12 次里只现了 1 次。
不影响已经写入的业务身份，协议内容未变，`W2G-IS-00`～`07` 的既有 G2/G3 证据不因此作废。
