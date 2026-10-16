# protocol-v4.0.0 出口第 1 步 G3：四个 runner 的轮次与 journey 偶发红（program#184）

## 绑定与 runner

| 项 | 值 |
| --- | --- |
| ControlServer | `d481250c08d65b9b03ba854f22f563fb33bfa5aa`（PR #735 合并提交） |
| Onboard HMI | `c9d24dfd6e96dc43781735cac209dc3976fe3a83`（onboard-hmi PR #358 合并提交） |
| slots-simulator | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| Protocol | `3fb214e57d861a909d872511940d26133eb32dca`（`protocol-v4.0.0`，protocolVersion 5，APPROVED_RELEASE） |
| runner | `3c2e6d0555d8d7e6f601532a8df5cc38d3481a54`（PR #736 head，COMMITTED_RUNNER，四个提交来源都是 SHARED_BINDING） |

本机笔记本真装置时段，三轮都由调度 C11 放行（`ds-tasks/board.md` 第 227 行持有者为 program#184）。

## 结论

四个 G3 都在上述绑定上取得了一轮 PASS，以下作为门禁证据：

| runner | 时间（2026-10-16，CST） | 结论 | 片 | 证据 |
| --- | --- | --- | --- | --- |
| staged | 15:14:37～15:16:48 | `STAGED_G3_RECOVERY_REPLAY_PASS` | FP-IS-00、06、07、14、15 | `../20261016-protocol-v4.0.0-staged-d481250c` |
| restart | 15:16:48～15:19:36 | `STAGED_G3_PROCESS_RESTART_PASS` | FP-IS-00、06、14、15 | `../20261016-protocol-v4.0.0-restart-d481250c` |
| demand-bearing | 15:19:37～15:22:00 | `DEMAND_BEARING_G3_VECTORS_PASS` | FP-IS-04、05、06 | `../20261016-protocol-v4.0.0-demand-bearing-d481250c` |
| journey（第 3 轮） | 15:58:22～16:26:24 | `JOURNEY_G3_PASS`，20/20 场景，失败断言 0 | FP-IS-01、02、03、07、08、10、11、12、13 | `../20261016-protocol-v4.0.0-journey-d481250c` |

15 片 `formalSlicePass` 均为 true。这一轮证实 staged harness 换成 v4 身份后载荷不需要改：v4 只新增了消息与枚举，此前这一点只是推断。

## journey 第 1 轮的红与两次单场景重跑（本目录）

第 1 轮 journey（15:22:00～15:51:11，runId `20261016T072201038Z`）退出码 1，结论 `JOURNEY_G3_SLICE_FAIL`。20 个场景中 19 个 PASS，只有 `g3-task-type-admission-fail-closed` 失败，10 条失败断言都来自这个场景。证据在 `round1-red/`。

场景的失败原因（SUMMARY.md 原文）：

```
Timed out after 180s waiting for: the server issued the Load command. Last observed: (nothing)
```

经过：车载端收到录入请求后，场景通过 UI Automation 输入了期望的那个子批号并点击「手动提交」。车载端在本机拒绝了这次提交，没有发出 `SublotSubmitted`：

```
2026-10-16T15:38:24.6272161+08:00 服务端请求录入Sublot：station=N1-3_N1-7，revision=1，expectedSublots=G3-10-20261016T073753812Z。
2026-10-16T15:38:25.1186611+08:00 Warning MainViewModel 界面命令被业务规则拒绝：SUBLOT_NOT_IN_WORKLIST。
```

服务端那头需求已经受理，订单已确认，车也已到站，但一直没收到子批号（`ConsumedSublotMessageId` 为空）。30 秒离站期限一到，服务端把需求退回待派，场景就停在等 Load 这一步。

为了区分偶发和必现，在同一绑定、同一组克隆上把这个场景单独直调 `Invoke-L2Scenario.ps1` 重跑了两次（15:54:31～15:57:04，`rerun-1/`、`rerun-2/`），两次都 PASS：

| 轮次 | 录入请求 → 手动提交 | 结果 |
| --- | --- | --- |
| 第 1 轮 | 471 ms | 本机拒绝 `SUBLOT_NOT_IN_WORKLIST`，没有 Load |
| 重跑 1 | 317 ms | 约 1.05 s 后收到 Load，PASS |
| 重跑 2 | 287 ms | 约 1.05 s 后收到 Load，PASS |
| 第 3 轮（整轮 journey） | — | PASS |

在录入请求之前，三次的会话事件顺序完全相同：READY v1、v2，接着 RECOVERY_REQUIRED [DEPARTURE_UNSAFE] v3，然后 READY v4。车载端日志不记录本机工作单的内容，所以无法判断第 1 轮踩中的是哪个时间窗口。车载端有两处代码会报这个码：`OnboardController.ValidateJourneySublot` 与 `WireToGateBusinessService` 的录入校验，现有日志分不清是哪一处。

调度判定为偶发，另开 `8005-agv-onboard-hmi#359` 跟进：先补诊断日志，再定位。这个问题不在 program#184 范围内，不挡本次换装。第 1 轮的红与两次重跑只作对照，不作门禁证据。

## 作废的证据

绑定在 `f12bc809`／`d4957461`／`fb5f7c59`／`3f091cb2`（protocol-v3.0.0）上的四份 G3 证据（`../20261014-protocol-v3.0.0-*-f12bc809`）不再对应当前绑定，由上表四份取代。
