# WIRE_TO_GATE 单次真实建单人工 Runbook

本 runbook 推进最多一次真实网络写事务：对一个取货 intent 执行至多一次
`POST /api/order/v1/add/byDefaultMissions`，随后只读对账。它不判断行人、避障、急停或现场
物理安全；这些由现场人员和车辆安全系统负责。

## 安全论据

至多一次建单由三层保证，全部在产品内部，不依赖操作者记忆：

1. **服务端幂等**（BC-ORDER-004）：RIoT 对已持有的同一 `upperId` 返回业务
   `code=0610008 订单已存在`，不创建第二单。因此重复提交的最坏情况是一个确定性的业务
   拒绝，而不是第二台车动起来。这是不再需要「建单前先证明订单不存在」的原因。
2. **持久 at-most-once**：`ArmCreateDispatchAsync` 要求 intent 处于
   `PENDING_RECONCILIATION`、`DispatchAuditVersion == 1`、`CreateAttemptCount == 0` 且
   `CreateAttemptId is null`，并在同一事务里把状态推进为 `CREATE_ATTEMPTED`。一个 intent
   在数据库层面至多产生一次建单尝试。
3. **操作开关**：`RiotCreateDispatch:enabled` 默认关闭，且缺失该配置段解析为关闭。开关关闭
   时任何建单路径都在 arm 之前返回 `CreateDispatchDisabled` 并且**不写任何东西**，审计链、
   intent 状态和计数器保持原样。

第 3 条与 `JourneyRuntime:enabled` 是两个独立开关，这是本 runbook 分两段执行的前提：
开启 runtime 只让旅程状态机跑起来并做只读对账，不等于授权真实下单。

## 冻结输入

- ControlServer 产品：`1a0158c87c36fb2e3e0f4ddca1f7ed9c84d5672b`
- package manifest SHA-256：`3475c33e1962eafb0e076afb94bea496af8730a0551dd990c1fe822005f84c4b`
- OnboardHmi：`84b7f3f66ff2f867b18121760f38e26e0bbd6fa5`
- slots-simulator：`fb5f7c593742bf98bc3957b8729a38aad5321f28`
- 协议 release：`protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- 协议 manifest SHA-256：`a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`
- RIoT upstream：`http://172.19.206.222:8888`

执行前必须回读已安装的 `ControlServer.Host.dll` ProductVersion 确认就是上面的产品 commit。
任一值不符即停止。凭据只留在本次进程环境中，不进入 Git、日志或证据。

## 第一段：零 mutation 演练（开关关闭）

目的是在不写任何东西的前提下证明整条链路可用。

1. 现场负责人先给出物理安全 GO；没有 GO 就不启动任何进程。
2. 确认已安装配置中 `RiotCreateDispatch:enabled` 为 `false`。
3. 启动 simulator、OnboardHmi，最后启动 ControlServer，并开启 `JourneyRuntime:enabled`。
4. 观察一个 Demand 被受理、产生 `AcceptedDemand`、`TO_PICKUP` intent 和车辆租约，并且
   `JourneyRuntime.BlockReasonCode` 稳定为 `CreateDispatchDisabled`。
5. 回读 RIoT 侧：本段只应出现只读 GET，`CREATE_REQUEST` 审计事件数必须为 0，订单数不变。
6. 演练可以重复。开关关闭时的每一次尝试都不改变 intent 的建单资格。

演练不通过就停在这里，不进入第二段。

## 第二段：单次授权建单（开关打开）

7. 取得用户对**这一次**建单的逐次授权，并确认第一段的 intent 身份没有变化。
8. 只打开 `RiotCreateDispatch:enabled`，重启服务使配置生效。不要同时改动其它配置。
9. 观察 SQLite 审计：正确的链是 `PRE_CREATE_RECONCILIATION -> CREATE_DISPATCH(ARMED) ->
   CREATE_REQUEST -> CREATE_RESPONSE`。`CREATE_REQUEST` 一旦出现，本次授权即已消耗，
   不得重启 Host 重来。
10. 用同一 `upperId` 只读对账。得到确定订单身份记 `orderConfirmed=true`；得到
    `0610008` 说明该 upperId 已被既有订单持有，同样只读对账，不得再次 POST；超时或响应
    不明确时先停止 Host，之后只允许继续只读对账。
11. 立即把 `RiotCreateDispatch:enabled` 改回 `false` 并重启服务，再收尾其余进程，确认临时
    端口全部释放。保留数据库和日志供人工复核。

结束时只记录：

```json
{"sent":null,"orderConfirmed":null,"writeCount":null,"cleanupPassed":false}
```

`null` 表示无法证明，不得改写为 `false`。`writeCount` 只能是 `0`、`1` 或 `null`。

## 立即停止条件

- 冻结输入、已安装产品版本、订单对象或 route 任一不一致；
- 第一段出现任何 `CREATE_REQUEST` 审计事件或 RIoT 侧订单数变化；
- 出现第二个写请求、Host 重启请求或任何 cancel 请求；
- POST 超时、连接中断或响应无法确认；
- cleanup 未能停止精确子进程或释放临时端口。

## 已知剩余风险

- upstream 是明文 HTTP，凭据和响应没有传输层机密性或端点身份保证；
- at-most-once 依赖 RIoT 真的按 BC-ORDER-004 执行 upperId 幂等。若该契约在 Map 25 现场
  不成立，重复提交可能建出第二单；用户已将此类偏差定性为 RIoT 自身缺陷；
- 缺少独立的网络 egress interlock，开关是进程内的配置门而不是外部阻断；
- 本 runbook 只证明网络事务与订单身份，不证明车辆到站或现场安全。

上述风险必须在新的逐次授权中被明确接受；旧授权不得复用。
