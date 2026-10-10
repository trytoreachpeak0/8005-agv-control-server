# g3-unable-to-charge-field-confirmation 第一遍：FAIL（G3-13-23 拆分之前）

- 时间：2026-10-02 08:26～08:28 UTC，笔记本真装置（调度放行的时段，跑 1 遍）。
- 三端提交：control-server `5c201348c30381070b54b79a3bd2d43fe3bec3b3`、onboard-hmi `52970e5091a57b07caae472f33145d12f8081e7f`、
  slots-simulator `fb5f7c593742bf98bc3957b8729a38aad5321f28`。
- 结果：G3-13-21、22、24、25、26 通过；G3-13-23 失败。

## 红在哪一侧

G3-13-23 当时把线路与车载端界面放在一条断言里。线路那一半全对：服务端回 `CONFIRMED`、`problem` 为空、`chargingPolicyDecision=MANUAL_CHARGING_HOLD`；
车载端日志（`onboard-app.excerpt.log` 第 23 行）也记下它收到了 `outcome=Confirmed`。红的是界面结果一行 `UnableToChargeStatus`，30 秒里一直是空串。

原因在车载端：`WireToGateBusinessService.UnableToCharge.cs` 的 `ServerSaysTheChargingIsOver` 在业务状态的 `activePurpose` 不再是 `CHARGING` 时清掉上一次的结果；
而服务端确认之后按票面把用途转成 `CLEARING_MAINTENANCE`（与系统确认同一条路），清桩中的业务状态在结果之后不到一秒就到了（G3-13-24）。
修复归 onboard-hmi#242。之后的场景把这一项拆成 G3-13-27，G3-13-23 只断线路。

## 入库前删掉了什么

`logs/`（服务端日志 2.7 MB，绝大部分是 SQL 语句）与 `snapshots/`。保留了 `SUMMARY.md`、`assertions.json`（含 `identity`）、`timeline.jsonl`、
角色名单，以及两端日志的片段：`control-server.excerpt.log`（结果前后几秒的非 SQL 行，加判定与发件箱的写入），`onboard-app.excerpt.log`（第 23 行前后）。
片段保留了原日志的行号。
