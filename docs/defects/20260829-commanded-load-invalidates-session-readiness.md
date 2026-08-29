# 服务端命令的装货操作会摧毁自己的执行前提

状态：**已修复**，`ControlServer_MVP@060dba9659b569f2a639c9b9a3f01fdc9fb77628`。现场复跑验证仍待安排。

归属由用户授权本仓判定。协议只定义 `departureSafe` 这一事实，`SessionReadiness` 的 schema 并不规定
就绪度如何计算，`DEPARTURE_SAFETY_NOT_READY` 是本仓自选的字符串，因此该规则完全是本仓的实现决定，
不涉及协议审批，也不需要车载仓配合：本仓的修改既必要（车载端单方面改拦不住本仓降级会话并停住旅程）
又充分（本仓不降级则车载端前提成立，装货可继续）。

## 一句话

执行一次服务端下发的 `SlotOperationCommand` 必然要开锁，开锁必然使 `departureSafe=false`，而服务端把
`departureSafe` 当作**会话就绪**的必要条件，会话一降级车载端就中止这次操作——命令摧毁了自己的执行
条件，任何时序都无法完成装货。

## 现场证据

运行 `sublot2-20260829T081205Z`，ControlServer `f94483c`、OnboardHmi `304e6ad`、simulator `fb5f7c5`、
协议 `protocol-v0.1.1`。零 RIoT 变更、零车辆移动。

| 时刻（UTC） | 事件 |
| --- | --- |
| `08:15:49.501` | 服务端下发 `SlotOperationCommand`，`operationType=LOAD`，`slots=[1]` |
| `08:15:49.61` | 车载发开锁脉冲，DO 0→1 |
| `08:15:49.68` | 车载 `SafetyStateChanged` v5：`departureSafe=false`，`reasonCodes=["UNLOCK_OUTPUT_NOT_RESET"]` |
| `08:15:49.80` | v6：`["LOCK_NOT_CLOSED","UNLOCK_OUTPUT_NOT_RESET"]`，锁已打开 |
| `08:15:50.13` | v7：`["LOCK_NOT_CLOSED"]`，解锁输出已复位 |
| `08:15:50.49` | 车载 `InvalidOperationException: WIRE_TO_GATE_NOT_READY`，中止装货，「已保持物理安全阻塞」 |

车载端只上报了 `PREPARING` 与 `UNLOCKING` 两条 `OperationProgress`，从未进入 `WAITING_OPERATOR`，
因此 `OperationResult` 永远不会产生。此后会话未能恢复，`SessionGeneration` 由 1 爬升至 14，最终停在
`RecoveryRequired / HANDSHAKE_INCOMPLETE`。

补充事实：解锁序列本身是成功的（DO 0→1 开锁输出、锁 DI 1→0 已解锁、DO 1→0 输出复位都已观测到）。
失败点在解锁完成之后、等待操作员放货之前。

## 机制

服务端侧，`OnboardMessageProcessor.cs` 的 `SafetyStateChanged` 分支把 `departureSafe` 直接落库并重新
判定就绪：

```csharp
await store.ApplySafetySnapshotAsync(agvId, generation, revision, departureSafe, contentHash, ct);
SessionReadinessDecision decision = await store.DecideReadinessAsync(agvId, generation, ct);
```

`WireToGateStore.DecideReadinessAsync` 把它作为就绪的**必要条件**：

```csharp
bool ready = row.CapabilityRevision is not null && row.SafetyRevision is not null &&
             row.RecoveryReportId is not null && row.DepartureSafe == true && noPendingFacts &&
             row.ReportedForcedRecoveryGeneration == row.ForcedRecoveryGeneration;
```

车载侧（`8005-agv-onboard-hmi`，只读引用，不在本仓修改）继续执行操作的前提是会话就绪：

```csharp
() => !settings.WireToGate.Enabled
    || (_wireToGate?.Current.Readiness == WireToGateSessionReadiness.Ready && vehicleStoppedProvider())
```

三者构成闭环：**下发 LOAD → 必须开锁 → `departureSafe=false` → 会话降级 → 车载中止 LOAD**。

## 归属判断

判为**服务端缺陷**。`departureSafe` 的语义是「此刻能否开走」，不是「会话能否使用」。装货期间车辆当然
不能开走，而这正是服务端自己刚下达的命令造成的；把它作为会话级 fail-close 条件，是服务端把两个不同
的概念混在一起。

车载端要求「会话就绪」才继续执行已经开始的操作，也是这条闭环的一环，但即便车载端改为在 attempt 期间
持有授权，服务端仍会把会话打成 `RecoveryRequired` 并引发重连，因此服务端这一处无论如何都要改。车载端
是否一并调整由其负责人决定，本仓不修改该仓代码。

## 为什么修这里不会削弱安全

`SessionRecoveryRow.DepartureSafe` 在本仓只有两处使用：

1. `DecideReadinessAsync` 的就绪判定（本缺陷）
2. `GetRecoveryReason` 的原因码 `DEPARTURE_SAFETY_NOT_READY`

**真正的移动授权门禁不在这条路上**。`AuthorizeMovementAsync` 使用的是独立的
`SafetyCheckObservation`——来自一次专门的 `PreDepartureSafetyCheck` 往返，且带 `ObservedAt` /
`ValidUntil` 新鲜度窗口：

```csharp
bool valid = safety.DepartureSafe && safety.ObservedAt <= now && safety.ValidUntil >= now;
if (!valid) throw new UnsafeMovementAuthorizationException(...);
```

因此把会话就绪的判定收窄，不改变任何一次真实移动的授权条件。

## 建议修法

在 `DecideReadinessAsync` 中，把 `row.DepartureSafe == true` 替换为「安全，**或**不安全的全部原因都由
一次仍在执行中的、服务端自己授权的仓位操作解释」：

```csharp
private static readonly string[] OperationInducedUnsafety =
    ["LOCK_NOT_CLOSED", "UNLOCK_OUTPUT_NOT_RESET"];

bool departureSafeOrExplainedByOwnCommand =
    row.DepartureSafe == true
    || (hasInFlightAuthorizedOperation
        && row.SafetyUnknownPresent != true
        && reasonCodes.Length > 0
        && reasonCodes.All(code => OperationInducedUnsafety.Contains(code, StringComparer.Ordinal)));
```

需要的支撑改动：

1. `ApplySafetySnapshotAsync` 增加参数接收 `safety.reasonCodes` 与 `safety.unknownPresent`，
   并随 `DepartureSafe` 一起持久化（新增可空列 + 一段迁移，旧库回填为空）。目前这两项信息在入站
   处理中被丢弃。
2. `hasInFlightAuthorizedOperation` 定义为：存在 `StationOperations` 行，其 `Status == Prepared`
   （已下发命令、尚无结果），且属于该 AGV 当前旅程。操作一旦结算（结果落库、`Status` 变更），
   放宽立即失效。
3. 原因码不变：不满足放宽条件时仍返回 `DEPARTURE_SAFETY_NOT_READY`。

### 必须保持的边界

- **严格子集**：原因码只要出现 `OperationInducedUnsafety` 之外的任何一项（`VEHICLE_STATE_UNKNOWN`、
  车辆移动、急停等），一律照旧 fail-close。
- **`unknownPresent=true` 一律不放宽**：证据不完整时不得当作已解释。
- **没有在执行中的授权操作时一律不放宽**：空闲车辆开着仓门仍然必须 fail-close。
- **不触碰 `AuthorizeMovementAsync`**：发车安全仍由独立的新鲜 `PreDepartureSafetyCheckResult` 证明。
- 放宽只作用于**会话就绪**，不改变 `departureSafe` 本身的取值、持久化或上报。

### 备选方案与为何不选

- **车载端在 attempt 期间持有授权**（不中途重读会话就绪）：合理，且可能仍值得做，但阻止不了服务端把
  会话打成 `RecoveryRequired` 与随之而来的重连，故不能替代本修法；且该仓对本仓 agent 只读。
- **车载端在操作期间不上报 `departureSafe=false`**：不可取。装货期间确实不能开走，压制该事实等于说谎。
- **服务端完全不把 `departureSafe` 纳入会话就绪**：过宽。空闲车辆开着仓门应当 fail-close，该信号仍有
  价值，只是需要按「是否由自己的命令解释」收窄。

## 已实施的修改

`ControlServer_MVP@060dba9`，按上述方案落地，未偏离：

- `SessionRecoveries` 新增可空列 `SafetyReasonCodesJson`、`SafetyUnknownPresent`，迁移
  `ScopedOperationInducedUnsafety`；旧行为 NULL，而放宽要求 `SafetyUnknownPresent == false`，
  故历史数据默认不放宽（fail-closed）。
- `SafetyStateSnapshot` 与 `SafetyStateChanged` 两条入站路径不再丢弃 `reasonCodes` 与
  `unknownPresent`，一并持久化。
- `DecideReadinessAsync` 用 `IsUnsafetyExplainedByOwnCommandAsync` 收窄判定；原因码逻辑不变。
- `AuthorizeMovementAsync` 一行未动。

门禁：Release 构建 0 warning / 0 error、`dotnet format` 通过、完整测试 **225/225、0 skip**、
全新库与降级/升级两条迁移路径均通过（`Down` 可逆）、W2G-IS-00～07 八片 G2 全部 PASS 并绑定
`060dba9`（合计 133 个筛选测试、0 skip）。

三条新测试：正向那条（本仓自己命令造成的不安全）在去掉放宽后**变红**；两条反向保护测试
（原因码超出集合、`unknownPresent=true`、无原因码、旧行、无在执行操作）在两种情况下都绿——
它们断言的正是**不该改变**的行为。

## 复现

零 RIoT 变更、零车辆移动即可复现，前提是车辆已停在取货站且该段订单已 `orderState=5`：

1. 建单开关关闭运行（`Invoke-PeerRehearsal.ps1`，runtime 开、create 关）
2. 现场操作员在 HMI 录入期望子批号，旅程推进到 `AwaitingLoadResult`
3. 观察车载日志出现 `WIRE_TO_GATE_NOT_READY`，服务端 `ProtocolInbox` 中 `OperationProgress` 停在
   `UNLOCKING`，且始终没有 `OperationResult`
