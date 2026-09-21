# 最终 head 上的 journey G3：`JOURNEY_G3_SLICE_FAIL`，红在一个场景，单独重跑绿

**这一轮是红的，结论没有被重跑盖掉。**重跑的结果放在旁边，是用来判断红的性质的，不是替代品。

三端：服务端 `fc144d0a565329a4115c657e73edc6d9fb5246b7`（`--SelfCheckControlServerCommit`）、车载端
`e6bea2674a54a80ea92f9df8031999d46c77d321`（`w2g/fp-v2-impl` 顶端，`git ls-remote` 实读）、模拟器
`fb5f7c593742bf98bc3957b8729a38aad5321f28`。两项 `commitSource` 都是 `SELF_CHECK_OVERRIDE`：这是本票的自检，
不是那几个切片的门禁证据（理由见 `../b7-06-journey-final/README.md`）。

## 红在哪

14 个场景里 13 个 PASS。`g3-reversed-direction-journey`（从暂存站取货、送到区域机台）在判据之前中断：

```
Timed out after 60s waiting for: the onboard acknowledged the plan sent before the first arrival. Last observed: 0
```

它的 9 条判据因此全是 `FAIL_OR_INCONCLUSIVE`，`noScenarioAbortedBeforeItsJudgments` 也随之失败。

## 因果链（都是从本目录的文件里读到的）

1. 车载端 10:46:19 会话建立、`readiness=Ready`（`reversed-direction-journey/onboard-app.log`）。
2. 旅程 02:46:37 UTC 受理并派出。
3. **02:46:39.17** 服务端的会话记录变成 `ReasonCode=DEPARTURE_SAFETY_NOT_READY`、
   `SafetyReasonCodesJson=["VEHICLE_NOT_READY"]`（`db-SessionRecoveries.json`）——车载端报上来一份「车辆未就绪」的安全快照。
4. **02:46:39.33** 运行时的就绪闸门据此把旅程标成 `ONBOARD_SESSION_NOT_READY`（`db-JourneyRuntimes.json`），计划不再发出。
5. 此后 60 秒会话记录一次都没更新，车载端没有再报就绪，等待超时。

车载端对 `VEHICLE_NOT_READY` 的定义（`WireToGateSafetyEvaluator`，只读核的）是**上游车辆安全信号过期或状态不明**。

## 已经排除的，与没有排除的

**已排除：本票改了那道闸门或那条安全信号路径。**写入 `ONBOARD_SESSION_NOT_READY` 那段（`JourneyRuntimeEngine.cs:440–490`）
`git blame` 出来的四个提交全部是集成分支上已有的；服务端给车载端的车辆安全接口本票没有改。

**单独重跑 PASS**（`reversed-direction-journey-rerun/`）：同一台机器、同样的车载端与模拟器提交（独立 detached 工作树），
服务端 `3094befc`——它比 `fc144d0a` 只多一份证据说明文件，产品代码逐字相同。9 条判据全过，那份计划 0.3 秒就被确认。

**没有排除的**：为什么车载端 60 秒都没报恢复。一次短暂停顿（本机常只剩 2～3 GiB 内存、有秒级停顿）足以让车辆信号过期，
但不足以解释一整分钟不恢复。这一点本票没有查清，只能记下：它发生在车载端与会话恢复那一侧，而那两侧本票都没碰。

一红一绿不是对照（条件不同：连跑 14 个场景 vs 单跑一个），所以这不构成「偶发」的结论，只构成「不是任何条件下都红」。

## 为什么只留这些

整份目录 24M，大头是每个场景上 MB 的服务端 SQL 日志。留下的是：总结果 `run-result.json`、红掉那个场景的判据与时间线、
说明因果链的两张表快照、车载端日志、场景输出，以及重跑的判据与时间线。完整目录没有入库。
