# 软件急停的现场兜底

**给现场人员与值班工程师看。** 服务端发出急停相关告警时该做什么、不该做什么；急停锁住之后，车怎么回来。

载体是 `REQ-0247`（停稳必须依靠正面证据）、`REQ-0248`（急停调用按状态重试并保留现场兜底）、
`REQ-0249`（发起急停遵循「停车宽、恢复严」）与 `REQ-0356`（急停锁住后经服务端人员确认由服务端解除）。
`REQ-0247`、`REQ-0248` 在需求基线 `v1.3.0` 由变更提案 `CP-0003` 修订，`REQ-0356` 由它新增（2026-09-15 批准）。
实现在 `src/ControlServer.Host/Runtime/Commands/EmergencyStopSupervisor.cs`，人工确认入口在
`src/ControlServer.Host/Runtime/EmergencyStopReleaseEndpoints.cs`。

## 服务端在自动做什么

8005 判定某台车必须停下时，会向 RIoT 发 `triggerEmergency`，**然后回查这台车的 `emergencyState`**。
调用被 RIoT 接受**不算**停住了——只有回查读到 `CAN_RECOVER` 或 `CAN_NOT_RECOVER` 才算急停已锁住。

**锁住就算停稳。**回查读到这两个值之一，服务端就认为车已经停稳，不再另读运动状态、车速或位置（`REQ-0247`）。
这是 2026-09-15 用户的裁定：急停一定会停稳。之所以不能改成读数据，是因为现场实测过：订单还在执行时锁住的车，
RIoT 会一直报 `MT_RUNNING` 加车速 0；停在两站之间的车，站点号是 0、位置读不到。照读数判，这样的车永远证不出停稳。
急停状态回到 `OK` 以后，这条就不再适用。

在锁住确认之前，服务端**按受控退避一直重试，没有次数上限**（默认首次 2 秒、每次翻倍、封顶 30 秒；
配置项 `RiotCommands:EmergencyRetry*`）。这是有意的：一台没能确认锁住的车，过一小时仍然是一台没能确认锁住的车。

确认之后服务端**停止重复触发**，只继续监控急停状态。**如果急停在原因消除前自己变回 `OK`**——例如有人在 RIoT
里手动解锁——服务端**立即重触发并告警**，不等下一个退避时隙。服务端自己解除的（自动解除，或下面的人工确认解除）
不算这种情况。

## 告警意味着什么

| 告警码 | 含义 | 现场动作 |
| --- | --- | --- |
| `EMERGENCY_STOP_UNCONFIRMED` | 已经要求这台车停下，但**还没读到急停锁住**。远程调用失败、超时或结果未知。 | **立即到现场确认车辆，隔离危险区域。** |
| `EMERGENCY_LATCH_RELEASED_EXTERNALLY` | 急停曾经锁住，现在 RIoT 报 `OK`，而服务端没有解除过它。有别的东西解除了它，服务端已重新急停。 | 同上，并查清是谁解除的。 |
| `EMERGENCY_STOP_NOT_RECOVERABLE` | RIoT 报 `CAN_NOT_RECOVER`。**服务端不会、也不允许调用 `cancelEmergency`**，人工确认也解不开。 | 转 RIoT 人员处置。 |
| `EMERGENCY_RELEASE_UNCONFIRMED` | 解除命令已经发出，但还没读到 `OK`。**车是停着的**，只是还没解开。 | 不是跑向车辆的紧急情况。RIoT 解锁要几秒（2026-09-15 实测 3.6 秒），通常下一轮评估就读到 `OK`；一直不好再查 RIoT 侧为何不解除。 |

**注意第一条与最后一条要跑的方向相反。**`EMERGENCY_STOP_UNCONFIRMED` 是「车可能还在动」，
`EMERGENCY_RELEASE_UNCONFIRMED` 是「车肯定停着，只是还没解开」。用同一个告警码承载两者会让人白跑一趟，
所以它们是两个码。解除也按同一套退避重试，不是每个评估周期发一次。

告警会在每一次评估中重新发出，直到条件消失——它是**持续**的，不是一次性的。

## 现场可以做什么

- **任何在场人员都可以按下已有的物理急停。不需要系统登录，不需要任何审批。** 这是 `REQ-0248` 明写的，
  也是 `REQ-0249` 的「停车宽」：停车这个动作在任何路径上都不设权限门槛。
- **断电、抱闸等专业隔离只由具备相应作业资质的人执行。** 不是「谁先到谁做」。

## 现场兜底不构成恢复资格

`REQ-0248` 的原话是：物理隔离可控制现场风险，**但不自动构成电子停稳证明或恢复资格**。

所以：

- 人按下了物理急停 → 现场安全了，**但服务端的 `EMERGENCY_STOP_UNCONFIRMED` 不会因此消失**。停稳要服务端自己
  读到软件急停锁住。
- **车不会因为有人到过现场而自动恢复。** 解开急停只有下面两条路，而且都由服务端自己调用 `cancelEmergency`、
  自己回查 `OK`。

## 急停之后车怎么回来

### 路径一：自动解除（`REQ-0167`）

只针对 8005 自己触发的急停，要同时满足：

| 条件 | 服务端怎么读到 |
| --- | --- |
| RIoT 报 `CAN_RECOVER` | 回查 `emergencyState`；`CAN_NOT_RECOVER` 一律禁止调用 |
| 这次急停是 8005 自己触发的 | 命令审计里有本次急停的 `triggerEmergency`；人工、外部或来源不明的急停不在自动解除范围内 |
| 原因已经消除 | 故障事实被清除（`VehicleFaultStates` 的 `Level=None` 且 `ClearedAt` 有值） |
| 停稳 | 急停锁住即视为停稳，上一条已经读到锁住（`REQ-0247`） |

任何一条不满足，服务端会拒绝自动解除并给出具体的原因码（`EMERGENCY_NOT_CAN_RECOVER`、
`EMERGENCY_FAULT_FACT_ABSENT`、`EMERGENCY_CAUSE_NOT_CLEARED`、`EMERGENCY_FAULT_GENERATION_MOVED`；
`EMERGENCY_STOP_NOT_PROVEN` 只会出现在没读到锁住的时候）。

今天清除故障事实只有一个入口：修复续行，它要求原订单确认为 HELD。**被 RIoT 报成 FAILED 的单永远不满足**，所以这类
急停不会自动解除，要走路径二。

### 路径二：人工确认解除（`REQ-0356`）

服务端人员确认三件事并留下身份，服务端据此解除。

**要确认的三件事**：急停原因已经消除、车上没有货、全部仓门已经关好。**不需要确认停稳**，锁住就算停稳。
「仓门已经关好」只用来解除急停，不是车再出发所需的锁闭证明，车再动仍要通过出发前安全检查（`REQ-0244`）。

**服务端自己再核对的事**，任何一条不满足都会拒绝：

| 原因码 | 意思 | 怎么办 |
| --- | --- | --- |
| `EMERGENCY_CONFIRMER_UNIDENTIFIED` | 没填确认人工号 | 填上。确认人身份必须记下 |
| `EMERGENCY_CAUSE_CLEARED_NOT_CONFIRMED` | 没确认「原因已消除」 | 查清并排除原因后再确认 |
| `EMERGENCY_VEHICLE_EMPTY_NOT_CONFIRMED` | 没确认「车上无货」 | **车上有货不能走这条路**，货物先按异常处置条目处理 |
| `EMERGENCY_DOORS_CLOSED_NOT_CONFIRMED` | 没确认「仓门已关」 | 关好仓门后再确认 |
| `EMERGENCY_NOT_CAN_RECOVER` | RIoT 不是报 `CAN_RECOVER` | `CAN_NOT_RECOVER` 转 RIoT 人员；`OK` 说明已经没锁 |
| `EMERGENCY_NOT_RAISED_BY_8005` | 这个急停不是 8005 触发的，或服务端已经解除过、又被别人锁上 | 不归 8005 解，转 RIoT 人员 |
| `EMERGENCY_VEHICLE_ORDER_NOT_FINISHED` | RIoT 里这台车还有没结束的订单（排队、执行、暂停或 HANG） | **先在 RIoT 取消该订单，再确认。**订单还在执行时一解锁，车可能接着开走，而确认的人可能就在车旁 |
| `EMERGENCY_VEHICLE_ORDERS_UNKNOWN` | 读不到这台车的订单状态 | 等 RIoT 恢复后再试；读不到不当成「没有订单」 |

**入口**：`POST /api/safety/v1/emergency-stop-releases`，在服务端的健康端口上（默认 `127.0.0.1:58007`）。

- **默认不开。**现场要把配置项 `EmergencyStopRelease:enabled` 设为 `true`，并在环境变量
  `CONTROL_SERVER_EMERGENCY_RELEASE_CREDENTIAL`（变量名由 `EmergencyStopRelease:credentialEnvironmentVariable`
  决定）里放入调用凭据。打开入口却没放凭据，服务端启动校验不通过、起不来；运行中凭据变量被清空，入口回 503。
- **这还不是登录。**服务端目前没有登录和权限体系，入口靠一份全场共用的凭据挡住随手调用，确认人是谁由请求里的
  `operatorId` 说明，服务端原样记进命令审计和日志。有了登录体系后改用登录会话（2026-09-15 用户裁定）。
- 车载屏不能确认解除，也不需要第二人复核。

示例（PowerShell 7）：

```powershell
$body = @{
    agvId          = 'agv02'
    operatorId     = '工号'
    causeCleared   = $true
    vehicleEmpty   = $true
    allDoorsClosed = $true
    note           = '现场说明，可不填'
} | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:58007/api/safety/v1/emergency-stop-releases' `
    -Headers @{ Authorization = "Bearer $env:CONTROL_SERVER_EMERGENCY_RELEASE_CREDENTIAL" } `
    -ContentType 'application/json' -Body $body
```

**返回**：

| 状态码 | 意思 |
| --- | --- |
| 200 | 解除命令已发出，RIoT 已经读到 `OK` |
| 202 | 解除命令已发出，还没读到 `OK`（最常见，RIoT 要几秒）。服务端下一轮评估读到 `OK` 就记为解除成功；这期间再点一次不会重发 |
| 409 | 拒绝，响应里的 `reasons` 列出全部原因码（见上表） |
| 401 / 404 / 422 / 503 | 凭据不对 / 不是本服务端管的车 / 请求缺 `agvId` 或 `operatorId` / 入口没配凭据 |

### 解除之后

- **解除只结束这一次急停。**车辆故障阻断还在，这台车照样不派新单；清除故障有自己的规则。
- **服务端不会因为车停在两站之间就再急停它。**对同一次故障，解除后只有读到车在动、读不到车的状态、读数过期，
  或者车出现在另一个站点时，服务端才会再次急停，这算新的一次（2026-09-15 用户裁定）。
- **不要在 RIoT 里手动解锁。**服务端没有解除过的急停变回 `OK`，会被当成意外恢复，立即重新急停并报
  `EMERGENCY_LATCH_RELEASED_EXTERNALLY`（`REQ-0248`）。要解除，走上面的人工确认。

## 谁可以要求停车

`REQ-0249` 的三条路径，权限门槛依次是：

| 来源 | 要求 |
| --- | --- |
| 服务端自动触发 | **无**。满足条件即触发，不需要任何人授权 |
| 现场人员从车载端为本车请求 | **无需登录** |
| 服务端已登录人员对明确选中的车辆请求 | 只要求**能记下是谁**；不要求二次认证，不要求审批 |

三条路径的请求都会记下**来源、身份（若有）、车辆、原因和结果**。后两条目前还没有调用入口。

## 一处尚未落地的能力，先说清楚

`REQ-0167` 还有另外半条：自动恢复「由 8005 自身造成的临时 `DispatchDisable`」。

**目前 8005 从不向 RIoT 发出车辆级的 `DispatchDisable`**（服务端代码里那几处 `DispatchDisable` 全是
`CreateDispatchDisabled`，即 8005 自己的建单开关，与 RIoT 侧的车辆调度使能是两回事）。既然没有由
8005 造成的 RIoT 侧禁用，也就没有需要自动恢复的对象。这一半等到真的有东西去禁用车辆时再落地。
