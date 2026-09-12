# 缺陷：本站没装货就结束之后，出车前安全检查拖到下一轮才判，回答必然过期，旅程永远停在 AwaitingDepartureSafety

Status: fixed，已上线（2026-09-12 14:53，release run `34678572182`，服务端 `e0d6df7`、车载端 `6b8a0b0` 未变，包 SHA-256 `529b8d5b…`；上线前 `CONTROL_SERVER_G2` 重跑 `W2G-IS-02`/`-03`/`-04`/`-07` 全 PASS，证据 `evidence/g2/20260912-rc-889cbcb/`）。现场自救 `bb16f190` 待下一次开窗时观测
Tracking: [8005-agv-program#52](https://github.com/trytoreachpeak0/8005-agv-program/issues/52)
Found by: 现场窗口二（无人）实跑，`agv01`，2026-09-12，证据
`evidence/field/20260912-FW-FL2-unattended/snapshots/04-stuck-departure-safety-after-x/`（窗口中止，未 finalize，见同目录 `ABORTED.md`）。
Product at discovery: 服务端 `403f306`、车载端 `6b8a0b0`、`protocol-v0.3.0`（run `34596927445` 的包）

---

## 现象

第一趟旅程 `bb16f190` 在停靠 2，操作员（驱动脚本冒充）扫码前按「取消装货」。服务端 3 秒内把这条需求结算成
`Cancelled / CANCELLED_BY_OPERATOR`，旅程转入 `AwaitingDepartureSafety`，然后就一直停在那里：

| 时刻（UTC） | 事件 |
| --- | --- |
| 05:42:18 | 自动化面按下 `LOAD_CANCELLATION`，HTTP 200 |
| 05:42:20.107 | 服务端发出 `PreDepartureSafetyCheck` `f9dda99b` |
| 05:42:20.134 | 车回 `SAFE`，`observedAt` 05:42:20.118，`validUntil` 05:42:22.118（车载端固定给 2 秒） |
| 05:42:22 之后 | 引擎每一轮都记 `PRE_DEPARTURE_SAFETY_NOT_VALID`；那份回答始终没被消费，请求始终没被确认，**再没有发过新的检查** |
| 13:59（本地） | 车在站 84 空等 17 分钟后，被现场人员手动开走 |

期间车完全正常：8 仓全锁、开锁输出全复位、会话 `Ready / READY`、自动化面 `departurePermitted=true`。

## 根因

两层叠在一起。

**第一层：结束本站的两条路径没有当场判回答。**`JourneyRuntimeEngine.AdvanceAsync` 的 `AwaitingSublot` 分支里，
本站需求全部被取消（`pending.Length == 0`）或持货期限到期时调 `ConcludeLoadingStopAsync`，它发出检查、
把 stage 置为 `AwaitingDepartureSafety`，然后 `break`——回答要等下一轮（生产轮询间隔 2 秒）才去读。
`TryTimeOutSublotWaitAsync` 结算完同样 `return`。而车给的有效期正好是 2 秒。

装货提交（`AwaitingLoadResult` 的 `Committed` 分支）与终态结束两条路径早就写了
`goto case AwaitingDepartureSafety`，注释说的正是这个坑；这两条是漏掉的。同一天停靠 1 走的是站点期限
（`TryTimeOutSublotWaitAsync`），下一轮离 `validUntil` 只差约 30 ms，碰巧落在有效期里。

**第二层：回答一旦过期，就再也没有出路。**`AwaitingDepartureSafety` 每一轮判的都是同一个检查 id 的同一份回答，
从不重发。同一个 id 重发也没用：车载端每次都现算，但回答的 messageId 由
`predeparture:{checkId}:{safetyStateVersion}:{outcome}` 算出，第二份回答是同一个 messageId、不同内容，
服务端 `ProtocolInbox` 按整行哈希会判成内容冲突（与 8005-agv-program#49 同形），或者车载端直接重放旧的那份。
所以**任何一份无效回答都会让旅程永远卡住**——过期的 `SAFE`，也包括出发时门没关好的 `UNSAFE`，
哪怕门随后已经关上。

**为什么 L2 没看出来**：L2 的轮询间隔是 1 秒（`JourneyRuntime__pollInterval=00:00:01`），下一轮总在 2 秒有效期之内。
`real-onboard-field-window2-rehearsal-002` 在这一格全绿。

## 修复

1. `AwaitingSublot` 里凡是结束本站的路径（需求全部取消、持货期限到期、站点期限到期），存盘之后
   `goto case AwaitingDepartureSafety`，本轮就判。
2. 新增 `TryReaskLapsedDepartureSafetyAsync`：当前检查的所有回答都已过期满 10 秒时，结算旧请求，
   换上从旧 id 派生的新 `PreDepartureSafetyCheckId` / messageId，再问一次（每轮最多一次，EventId 2110 记日志）。
   没有回答的检查不重问（发件箱在重连时重放），仍有效的回答照原样判。协议与表结构都不动。
3. 回归：L1 新增三条（`AStopEndedByItsStationDeadlineJudgesTheDepartureSafetyAnswerWhileItIsStillValid`、
   `AStopWhoseDemandsWereAllCancelledJudgesTheDepartureSafetyAnswerWhileItIsStillValid`、
   `ALapsedDepartureSafetyAnswerIsAskedForAgainUnderANewCheckId`），改动前三条都红：前两条停在
   `AwaitingDepartureSafety`，第三条检查 id 不变。L2 整窗彩排改用生产轮询间隔 2 秒。

## 上线后怎么救 `bb16f190`

新包上线、两个门打开之后，引擎会发现停靠 2 那份回答早已过期，于是换 id 重问一次；车回 `SAFE` 后旅程自己出发去停靠 3。
车在修复前已被人挪走，RIoT 会从它现在的位置派单。
