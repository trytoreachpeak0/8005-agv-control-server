# 软件急停的现场兜底

**给现场人员与值班工程师看。** 当服务端发出 `EMERGENCY_STOP_UNCONFIRMED` 这条最高优先级告警时，
该做什么、不该做什么，以及为什么做完之后车仍然不会自动恢复。

载体是 `REQ-0248`（急停调用按状态重试并保留现场兜底）与 `REQ-0249`（发起急停遵循「停车宽、恢复严」）。
实现在 `src/ControlServer.Host/Runtime/Commands/EmergencyStopSupervisor.cs`。

## 服务端在自动做什么

8005 判定某台车必须停下时，会向 RIoT 发 `triggerEmergency`，**然后回查这台车的 `emergencyState`**。
调用被 RIoT 接受**不算**停住了——只有回查读到 `CAN_RECOVER` 或 `CAN_NOT_RECOVER` 才算闩锁已确认。

在确认之前，服务端**按受控退避一直重试，没有次数上限**（默认首次 2 秒、每次翻倍、封顶 30 秒；
配置项 `RiotCommands:EmergencyRetry*`）。这是有意的：一台没能证明停住的车，过一小时仍然是一台没能
证明停住的车。

确认之后服务端**停止重复触发**，只继续监控。**如果闩锁在原因消除前自己变回 `OK`**——例如被别的系统
解除——服务端**立即重触发并告警**，不等下一个退避时隙。

## 告警意味着什么

| 告警码 | 含义 | 现场动作 |
| --- | --- | --- |
| `EMERGENCY_STOP_UNCONFIRMED` | 已经要求这台车停下，但**电子上无法确认它停住了**。远程调用失败、超时或结果未知。 | **立即到现场确认车辆，隔离危险区域。** |
| `EMERGENCY_LATCH_RELEASED_EXTERNALLY` | 闩锁曾经确认过，现在 RIoT 报 `OK`，而原因还在。有别的东西解除了它。 | 同上，并查清是谁解除的。 |
| `EMERGENCY_STOP_NOT_RECOVERABLE` | RIoT 报 `CAN_NOT_RECOVER`。**服务端不会、也不允许调用 `cancelEmergency`。** | 转人工处置。 |
| `EMERGENCY_RELEASE_UNCONFIRMED` | 解除条件已满足、`cancelEmergency` 也发了，但闩锁没开。**车是停稳的**，只是回不了岗。 | 不是跑向车辆的紧急情况；查 RIoT 侧为何不解除。 |

**注意前两条与最后一条要跑的方向相反。**`EMERGENCY_STOP_UNCONFIRMED` 是「车可能还在动」，
`EMERGENCY_RELEASE_UNCONFIRMED` 是「车肯定停着，回不了岗」。用同一个告警码承载两者会让人白跑一趟，
所以它们是两个码。解除也按同一套退避重试，不是每个评估周期发一次。

告警会在每一次评估中重新发出，直到条件消失——它是**持续**的，不是一次性的。

## 现场可以做什么

- **任何在场人员都可以按下已有的物理急停。不需要系统登录，不需要任何审批。** 这是 `REQ-0248` 明写的，
  也是 `REQ-0249` 的「停车宽」：停车这个动作在任何路径上都不设权限门槛。
- **断电、抱闸等专业隔离只由具备相应作业资质的人执行。** 不是「谁先到谁做」。

## 现场兜底不构成电子证据

**这一条是整份文档的重点。**

`REQ-0248` 的原话是：物理隔离可控制现场风险，**但不自动构成电子停稳证明或恢复资格**。

所以：

- 人按下了物理急停 → 现场安全了，**但服务端的 `EMERGENCY_STOP_UNCONFIRMED` 不会因此消失**。
- 人在现场看着车不动了 → **不构成停稳证明**。停稳判定要的是新鲜且明确的组合证据
  （`REQ-0247`：非移动的 `movementState` ＋ 连续多次位置不变 ＋ 期间无新的移动迹象），由服务端自己取。
- **车不会因为有人到过现场而自动恢复。** 自动解除软件急停要同时满足四件事，缺一不可：

  | 条件 | 服务端怎么读到 |
  | --- | --- |
  | RIoT 报 `CAN_RECOVER` | 回查 `emergencyState`；`CAN_NOT_RECOVER` 一律禁止调用 |
  | 这次急停是 8005 自己触发的 | 命令审计里有本次 episode 的 `triggerEmergency`；人工、外部或来源不明的急停不在自动恢复范围内 |
  | 原因已经消除 | 故障事实被清除（`VehicleFaultStates` 的 `Level=None` 且 `ClearedAt` 有值） |
  | 停稳已被独立证明 | 故障事实上的 `StopProven` |

  任何一条不满足，服务端会拒绝解除并给出具体的原因码（`EMERGENCY_NOT_CAN_RECOVER`、
  `EMERGENCY_FAULT_FACT_ABSENT`、`EMERGENCY_CAUSE_NOT_CLEARED`、`EMERGENCY_STOP_NOT_PROVEN`、
  `EMERGENCY_FAULT_GENERATION_MOVED`）。**没有「强制恢复」这条路。**

- 解除之后服务端**还要回查一次** `emergencyState=OK`。调用被接受但闩锁没开，记为
  `EMERGENCY_RELEASE_NOT_CONFIRMED`，不算恢复。

## 谁可以要求停车

`REQ-0249` 的三条路径，权限门槛依次是：

| 来源 | 要求 |
| --- | --- |
| 服务端自动触发 | **无**。满足条件即触发，不需要任何人授权 |
| 现场人员从车载端为本车请求 | **无需登录** |
| 服务端已登录人员对明确选中的车辆请求 | 只要求**能记下是谁**；不要求二次认证，不要求审批 |

三条路径的请求都会记下**来源、身份（若有）、车辆、原因和结果**。

## 一处尚未落地的能力，先说清楚

`REQ-0167` 还有另外半条：自动恢复「由 8005 自身造成的临时 `DispatchDisable`」。

**目前 8005 从不向 RIoT 发出车辆级的 `DispatchDisable`**（服务端代码里那几处 `DispatchDisable` 全是
`CreateDispatchDisabled`，即 8005 自己的建单开关，与 RIoT 侧的车辆调度使能是两回事）。既然没有由
8005 造成的 RIoT 侧禁用，也就没有需要自动恢复的对象。这一半等到真的有东西去禁用车辆时再落地。
