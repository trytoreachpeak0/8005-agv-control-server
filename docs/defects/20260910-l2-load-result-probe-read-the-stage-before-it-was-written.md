# 缺陷：L2 `load-result-requires-recovery` 的第一条判据读在了服务端两步写之间

Status: fixed
Owner repository: `8005-agv-control-server`（`scripts/l2/scenarios/load-result-requires-recovery.ps1`）
Found by: [`CI run 34461279984 的 load-result-requires-recovery-01`](../../evidence/l2/20260910-ci-34461279984-load-result-requires-recovery-01/)
Product at discovery: `fp/b3-on-v2@b38b9ab710b237605832c448a01df698c5c88c08`
Peers: 合成车载端（同一工作树）

**红在场景，不在产品。**

## 现象

`l2.yml` 在 `fp/b3-on-v2` 上 `workflow_dispatch` 的那一次（run `34461279984`），23 次场景运行里 22 次 `PASS`，
唯一的红是这条批次 2 的既有场景，而且十三条判据里只红第一条：

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 装载指令已下发，旅程在等结果 | FAIL | `AwaitingLoadResult` | `AwaitingSublot` |

其余十二条——旅程进 `Blocked`、原因码 `LOAD_RESULT_REQUIRES_RECOVERY`、装载判 `RecoveryRequired`、不建 `TO_GATE` 单、
Blocked 期间不受理新需求——全部 `PASS`。同一次运行的时间线里，旅程确实走过了 `AwaitingLoadResult`：

```
09:41:35.945  pending-operation  operation:f39aa4a2-…     ← 车载端收到装载指令，判据在这一刻读 stage
09:41:36.000  journey-stage      AwaitingLoadResult        ← 55 ms 之后，服务端才把 stage 写进库
09:41:37.019  journey-stage      Blocked
```

## 根因

`JourneyRuntimeEngine.AdvanceAsync` 的 `AwaitingSublot` 分支在同一轮迭代里先 `PublishLoadAsync`（发件箱落库、上线），
再 `SetStage(runtime, JourneyRuntimeStage.AwaitingLoadResult, now)`，stage 在迭代末尾才保存。**这个顺序是对的**：
`durableBeforeSend` 约束的是那条命令，不是旅程 stage。

场景那一步写的是「等到车载端收到指令，然后读一次 stage」。车载端收到指令与 stage 落库之间有一个窗口，读一次就可能
读进去。本机上这个窗口小到读不到——在 `85cb743` 上（场景与产品代码与 CI 那次的 `b38b9ab` 相同）连跑三次，
`L2-LR-01` 三次都读到 `AwaitingLoadResult`；CI 机器的调度把它放大了。

## 为什么以前没红

**以前也可能红，只是没轮上。**这条场景在这条线上的两份绿证据（`evidence/l2/20260909-ticket09-load-result-requires-recovery-001`、
`evidence/l2/20260910-ticket18-load-result-requires-recovery-001`）都是本机运行。`l2.yml` 的 push 触发器只盯
`ControlServer_MVP` 与 `main`，v2 线上此前只在 CI 上跑过一次：`fp/v2-impl` 2026-09-08 的 `workflow_dispatch`
（run `34212712488`），`success`——那一次没读进窗口。一个时有时无的读取窗口，两次里撞上一次，与「代码变了」无关。

## 与本批次改动的关系

没有关系，查过：

- 本批次对合成车载端的改动是握手里多一份 `OnboardAlarmSnapshot`、多一条激活命令分支、多一个断开／重连控制面，
  都不在 `SlotOperationCommand` 这条路上，也不改 stage 的写入时机。
- 本批次对服务端的改动（`OnboardAlarmProjectionStore` 的在线判定、`AuditExport`）都不在旅程引擎里。

## 处置

判据改成 `Wait-L2Condition` 等 stage 变成 `AwaitingLoadResult`，30 秒上限。**等待不会放过任何真错误**：场景把车载端
的应答策略设成挂起，车载端应答之前旅程只可能停在 `AwaitingLoadResult`，停不在别处——等不到就是真的没到。

## 这类写法还有没有

「等到 A 出现，然后立刻读一次 B」只要 A 与 B 不是同一次写入，就是同一类窗口。本次只修了撞上的这一处，没有系统地
扫其它场景。
