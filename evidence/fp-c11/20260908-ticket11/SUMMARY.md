# 票 11：`FP-C11` 故障两级事实模型与隔离处置的验收证据

## 运行类型

纯本机 tier 1 ＋ 一次 L2 合成场景回归。**不动车、不建单、不使用任何现场凭据、不碰真 RIoT、
不需要桌面**。

## 结论

| 项 | 结果 |
| --- | --- |
| 新增测试 | **83 条**（`VehicleFaultIsolationTests`） |
| 全量套件 | **534 passed / 0 failed / 0 skipped**（票 10 基准 451 ＋ 83） |
| 新增 migration | **零**。`Persistence/Migrations/` 与 `ControlServerDbContext` 一行未动 |
| `Ports.cs` 改动 | **零**。新端口落在 `VehicleFaultPorts.cs` |
| `src/` 下 `.Raw` | 仍零命中；白名单守卫本票**未变红**，两个 Facade 方法都已在放行清单里 |
| L2 `normal-load` | PASS，行为无变化 |

原文：[`fault-model-tests.txt`](fault-model-tests.txt)（83 条逐条）、
[`full-suite.txt`](full-suite.txt)（全量）、
`../../l2/20260908-ticket11-normal-load-001/`（L2）。

## 负例的比例是有意的

83 条里，**断言「拒绝」并且断言那次拒绝报出的原因码的有 29 条**（52 个测试方法，其中带
`InlineData` 的展开后共 83 例）。规格 8.7 第 4 条不允许安全类能力带病
投运，而这段代码可能有的缺陷只有一种形状——把没证明的当成证明了的。所以每一条拒绝都按它
报出来的名字断言，不用「什么都没发生」代替：一次因为错误原因发生的拒绝能通过后者，而它会
把人派到错误的地方去。

## 三条最值得看的负例

### 一、订单 HELD ＋ 急停闩锁同时成立，仍然判定为「未停稳」

`AHeldOrderAndAnEngagedLatchDoNotProveTheStop`。这一条同时构造了 REQ-0247 点名的第二和第三个
反例：订单确实被 `OrderHold` 打到了 `PAUSED` 且回读确认，急停闩锁确实是 `CAN_RECOVER`。
`StopProof` **读不到这两件事**——它的入参里根本没有它们——所以判定只看运动采样，而采样是
`Unknown`，于是不成立。

一个能被这两件事满足的停稳证明，会对**每一台刚被本服务端命令过的车**都成立。

### 二、明确停着但窗口没攒够，不升级

`AVehicleReadingAsStoppedDoesNotEscalateWhileTheWindowFills`。这是 REQ-0246 第三个条件
（「车辆仍在移动或因监控失败无法排除继续移动」）在挡住的事。一台读起来明确非移动、位置已知、
采样新鲜的车，没有任何一件事说它在动，所以窗口还没填满只会**扣住证明**，不会**主张移动**。

如果没有这条，`OrderHold` 这条路永远走不完：第一次评估必然样本不足，于是每一次故障都立刻
变成急停。

### 三、位置未知就是未知，不是「没变」

`AVehicleBetweenStationsHasNoPositionAndCannotBeProvenStopped`。行为实验室 Round 10 实测：
车在移动全程 `station: 0, noStation: true`，到站才有站号。所以**没有位置是移动中车辆的常态**，
把它读成「位置没变」会让每一台停在轨道中间的车都通过停稳判定。这条断言 `PositionUnknown`
出现且 `PositionChanged` **不**出现——两者都不对的话，报告会把「看不见」说成「看见了没动」。

## 一处自审改出来的东西

`StopProof.Evaluate` 第一版在样本不足时**短路返回** `TooFewSamples`，不再检查别的。写测试时
撞红两条，看了一眼发现短路是错的：窗口里唯一那个样本是「读不到车」时，报告只说「采样不够」，
而那两件事要人做的动作不一样——一个是继续等，一个是等不出来了。已改为不短路，样本不足与
样本本身的问题一起报。

## 与票 10 的接缝，实测过一次

`AResumptionThatClearsTheFaultLetsTheEmergencyLatchBeReleased` 端到端走了一遍：车在移动中进
故障 → 停稳证不出来 → 立刻升级触发急停 → 此时请求解除被拒，原因码 `EMERGENCY_CAUSE_NOT_CLEARED`
→ 三次采样证明停稳 → 续行确认 → 故障事实真的 `ClearAsync`（`Level=None` 且 `ClearedAt` 有值）
→ 再评估，急停自动解除。

**只把 `Level` 降回去不算**——票 10 的 `ReleaseObstacles` 同时要 `ClearedAt` 与 `StopProven`，
这条测试就是证明那两个条件在本票这一侧真的被满足了。

## 两处如实登记的缺口

### 一、协调器没有产品调用方

`VehicleFaultCoordinator.ObserveAsync` 今天**只有测试在调**。谁去检测「离线／通信中断／导航
失败／单个订单 FAILED」这四类征兆、以什么节奏驱动，票 11 的验收里没有这一条，票据的冲突边界
也只说「故障模型实现为独立文件」。

不自行接上运行时循环，有三个理由：接上会改变每一轮派车的行为并需要重跑 L2；「哪个信号算离线」
在服务端有多个候选来源，选哪个是一次没人授权的判断；票 09（B2 多车）会重写车辆循环，那时接
更自然。**建议在票 09 里接**，输入是它已经在读的车辆观测。

注意这不影响 REQ-0234 的阻断：`VehicleFaultBlockCriterion` 已经挂在派车链上，只要故障事实存在
就挡住派车，无论那条事实是谁写的。

### 二、人工确认隔离只有策略，没有传输

`ConfirmIsolationAsync` 要求身份 ＋ 异常处置会话号，两者缺一即拒。但 REQ-0253 的权限模型
（`ExceptionRecoveryPermission`）在本批次不存在，所以没有入口调它——与票 10 留下的「人发起
急停的两条来源只有策略没有传输」是同一种形状。身份与会话号目前落在日志里，表上没有一等列，
补它是三个列加一次 migration。
