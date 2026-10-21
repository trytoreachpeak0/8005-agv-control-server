# 软件急停的现场兜底

**给现场人员与值班工程师看。** 服务端发出急停相关告警时该做什么、不该做什么；急停锁住之后，车怎么回来。

载体是 `REQ-0247`（停稳必须依靠正面证据）、`REQ-0248`（急停调用按状态重试并保留现场兜底）、
`REQ-0249`（发起急停遵循「停车宽、恢复严」）与 `REQ-0356`（急停锁住后经服务端人员确认由服务端解除）。
`REQ-0247`、`REQ-0248` 在需求基线 `v1.3.0` 由变更提案 `CP-0003` 修订，`REQ-0356` 由它新增（2026-09-15 批准）；
`REQ-0248`、`REQ-0356`、`REQ-0167`、`REQ-0246` 又在需求基线 `v1.10.0` 由 `CP-0011` 修订（2026-10-13 批准，急停分三类，见下一节）。
实现在 `src/ControlServer.Host/Runtime/Commands/EmergencyStopSupervisor.cs`，人工确认入口在
`src/ControlServer.Host/Runtime/EmergencyStopReleaseEndpoints.cs`（车上无货）与
`src/ControlServer.Host/Runtime/LoadedEmergencyStopReleaseEndpoints.cs`（车上有货或不确定）。

## 急停分三类（`CP-0011`）

RIoT 上一台车只有一个急停状态（`OK`／`CAN_RECOVER`／`CAN_NOT_RECOVER`），不说是谁按的。现场的急停其实有三类：

| 类 | 是什么 | 谁来解 |
| --- | --- | --- |
| 一类：不可恢复 | 车上的急停按钮按着，或不可恢复的硬件故障。RIoT 报 `CAN_NOT_RECOVER`，同时总带着一层二类 | 先在车上处理（松开按钮、修好硬件），之后按二类解 |
| 二类：可恢复 | 触边、软件急停、可恢复的硬件故障；在 Matrix、RIoT 或车尾触屏上按的也是这一类 | 在原来的地方解：Matrix、RIoT 或车尾触屏 |
| 三类：8005 自己的 | 服务端因为自己的原因（例如行驶中门锁没锁）要车停下。借二类的 `triggerEmergency` 实现，只记在服务端自己的审计里 | 原因消除后服务端自动解除；**读到过门锁没锁的例外**，要人确认门锁已检修后经本文的人工确认入口解除；只是门锁读不到的，读数恢复满 10 秒后服务端自己解（`CP-0017`） |

由此来的三条规矩：

- **车已经被别人锁住时，服务端只记不发。**它想停这台车，但车已经锁着，就在自己的审计里记下「这台车上有 8005 的三类」以及停车的原因，
  不再调用 RIoT。急停按钮按着时也一样。只要本服务端这一类还没解除，服务端每一轮都盯着这台车的急停状态，不看这一轮有没有新症状。
  门锁原因还在、车被别人锁着时，即使车已经被按住、证明停稳，服务端也照样记下自己这一类：用户定的是「自己有停车原因就下自己那一层」。
- **三类还在、闩锁却被别处解开了，服务端立即重新急停。**别人在 RIoT、Matrix 或车尾触屏上解开的是他自己那一层，8005 那一层原因还在。
- **订单 FAILED 不急停。**用户 2026-10-13 裁定：FAILED 的单 RIoT 不会再执行。服务端仍然记故障、不再给这台车派活，只是不下急停。

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
不算这种情况。车被别人先锁住、服务端只记了自己那一类的，闩锁一被别处解开，同样立即重触发。

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

只针对 8005 自己的三类急停，要同时满足：

| 条件 | 服务端怎么读到 |
| --- | --- |
| RIoT 报 `CAN_RECOVER` | 回查 `emergencyState`；`CAN_NOT_RECOVER` 一律禁止调用 |
| 这次急停是 8005 自己的 | 命令审计里有本次的 `triggerEmergency`，或车被别人先锁住时服务端记下的那一行；人工、外部或来源不明的急停不在自动解除范围内 |
| 原因已经消除 | 故障事实被清除（`VehicleFaultStates` 的 `Level=None` 且 `ClearedAt` 有值） |
| 停稳 | 急停锁住即视为停稳，上一条已经读到锁住（`REQ-0247`） |
| RIoT 里这台车没有未结束的订单 | 与人工确认解除同一个读法：原因消除不等于 RIoT 手里没有能开走这台车的单 |

任何一条不满足，服务端会拒绝自动解除并给出具体的原因码（`EMERGENCY_NOT_CAN_RECOVER`、
`EMERGENCY_FAULT_FACT_ABSENT`、`EMERGENCY_CAUSE_NOT_CLEARED`、`EMERGENCY_FAULT_GENERATION_MOVED`、
`EMERGENCY_VEHICLE_ORDER_NOT_FINISHED`、`EMERGENCY_VEHICLE_ORDERS_UNKNOWN`；`EMERGENCY_STOP_NOT_PROVEN` 只会出现在没读到锁住的时候）。

清除故障事实有两个入口：修复续行（原订单确认为 HELD），和人工清除
（[`vehicle-fault-clearance-field-guide.md`](vehicle-fault-clearance-field-guide.md)）。**两个入口都要求急停已经解除**，
所以按这张表，自动解除**从不发生**：故障在效时急停只能由人解，人解了之后才能清故障。现场会遇到的自动解除只有下面
「只是门锁读不到」那一种，它不要求故障先清除；其余的车回来一律走路径二或路径三。

**行驶中门锁读不到：先宽限 3 秒（`CP-0017`，需求基线 `v1.16.0`）。**车载端报「仓位状态未知」（`SLOT_STATE_UNKNOWN`，IO 读不到锁反馈）时，
服务端从收到那一刻起等 3 秒：期间只告警（日志事件 2321「graced」，恢复时 2322「came back locked after x s unread」），不按住、不急停；
3 秒内读回锁闭就过去了。3 秒仍读不到、期间读到任一扇门没锁、或这台车 10 分钟内已经宽限过 2 次，照旧立即按住、证不出停稳就急停。
宽限期间车照样不算「门锁已证明锁闭」，出发之类要锁闭证明的动作照样不放行。按车速约 0.55 米/秒（RIoT 现场读数），
3 秒最多多走约 1.65 米。

**行驶中只是门锁读不到引起的急停：读数恢复后服务端自己解（`CP-0017`）。**条件全部满足才解：这次故障从头到尾没读到过一扇门没锁、
读数恢复后**连续 10 秒**读回全部锁闭（中间读不到一次就从头数）、急停锁着且是 `CAN_RECOVER`、服务端为这次故障按住的那张单在 RIoT
上仍是暂停（7）、车上没有别的未结束订单。解除后单仍停着、故障仍在效，**要人经故障处置入口「继续原单」车才会再走**，或清除故障。
同一次故障只自动解一次；之后又读不到被再停，就交给人走下面的路。服务端在急停期间重启过的，也交给人（重启后它不知道这次故障里
有没有读到过没锁，按读到过处理）。条件不满足时同样走下面的人工确认解除，这时只是读不到、读数已恢复的，**不用确认门锁已检修**。
等它自己解的这段时间里，**同样不要在 RIoT 上解锁**：服务端那一层还在，一解立即重新急停。

**行驶中读到过门锁没锁的急停：不自动解除（`CP-0011`）。**门锁读回锁闭也不解除——锁可能坏了，读到的锁闭证明不了车门真的锁着，
车开起来门一开就会撞坏。车退出服务、故障在效，单停着。出路是：

1. 现场检修门锁，确认修好。
2. 走下面的人工确认解除，确认项里勾上「门锁已检修」：车上无货走路径二，车上有货或不确定走路径三。
   服务端按住的那张单（暂停，7）在路径三里可以留着。
3. 急停解开之后，经故障处置入口「继续原单」或清除故障
   （[`vehicle-fault-clearance-field-guide.md`](vehicle-fault-clearance-field-guide.md)）。

**解除之后门锁又读不到锁闭，车会被再停一次。**单还挂在车上时，人解除只免掉「车停在两站之间、报不出站点」这一条，
门锁读不到锁闭仍是急停原因。所以门锁要真的修好、读回锁闭再解除。

**门锁传感器坏了、修不好、一直读不到锁闭时的出路**（审查时实测过这条路）：

1. 值班工程师在 RIoT 上结束服务端按住的那张单（`W2G-` 开头，现场人员不要自己取消）。单结束之后 RIoT 再也驱动不了这台车，
   门锁读不到锁闭就不再让服务端重新急停；读到车在动、读不到运动或读数过期仍会。
2. 走路径三（车上有货）或路径二（车上无货）解除，确认门锁已检修。
3. 经故障清除入口清除故障（[`vehicle-fault-clearance-field-guide.md`](vehicle-fault-clearance-field-guide.md)），
   货物按清除之后的重建或异常处置走。

修订之前（2026-09-28 到 2026-10-13）门锁恢复为新鲜、锁闭、仓位已知时服务端会自动解除，那一套连同它的振荡兜底一起撤掉了。
2026-10-19 起（`CP-0017`）只给「只是读不到」的那一类恢复了自动解除，而且单仍停着、要人续行，不是修订前那一套。

### 路径二：人工确认解除（`REQ-0356`）

服务端人员确认三件事并留下身份，服务端据此解除。

**要确认的三件事**：急停原因已经消除、车上没有货、全部仓门已经关好。**不需要确认停稳**，锁住就算停稳。
这台车若是因为行驶中门锁没锁被停下的，还要确认第四件：**门锁已经检修**（`doorLockRepaired`）。
只是门锁读不到、读数已经恢复（服务端这次运行从头看着这次故障）的，不要求这一件（`CP-0017`）。
「仓门已经关好」只用来解除急停，不是车再出发所需的锁闭证明，车再动仍要通过出发前安全检查（`REQ-0244`）。

**服务端自己再核对的事**，任何一条不满足都会拒绝：

| 原因码 | 意思 | 怎么办 |
| --- | --- | --- |
| `EMERGENCY_CONFIRMER_UNIDENTIFIED` | 没填确认人工号 | 填上。确认人身份必须记下 |
| `EMERGENCY_CAUSE_CLEARED_NOT_CONFIRMED` | 没确认「原因已消除」 | 查清并排除原因后再确认 |
| `EMERGENCY_VEHICLE_EMPTY_NOT_CONFIRMED` | 没确认「车上无货」 | **车上有货走路径三** |
| `EMERGENCY_DOORS_CLOSED_NOT_CONFIRMED` | 没确认「仓门已关」 | 关好仓门后再确认 |
| `EMERGENCY_DOOR_LOCK_REPAIR_NOT_CONFIRMED` | 这台车是因为行驶中门锁没锁停下的，没确认「门锁已检修」 | 先检修门锁，再确认 |
| `EMERGENCY_NOT_CAN_RECOVER` | RIoT 不是报 `CAN_RECOVER` | `CAN_NOT_RECOVER` 转 RIoT 人员；`OK` 说明已经没锁 |
| `EMERGENCY_NOT_RAISED_BY_8005` | 这个急停不是 8005 触发的，或服务端已经解除过、又被别人锁上 | 不归 8005 解，转 RIoT 人员 |
| `EMERGENCY_VEHICLE_ORDER_NOT_FINISHED` | RIoT 里这台车还有没结束的订单（排队、执行、暂停或 HANG） | **先让这张单结束，再确认。**订单还在执行时一解锁，车可能接着开走，而确认的人可能就在车旁。先看是谁建的：服务端建的（`upperId` 以 `W2G-` 开头）不要取消，找值班工程师；不是服务端建的，按 [`vehicle-fault-clearance-field-guide.md`](vehicle-fault-clearance-field-guide.md) 「急停锁着、车上还挂着一张 HANG 的单」一节，核实它在我们的车上（RIoT `deviceKey`／`id`）后只取消这一张（2026-09-22 用户定） |
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
    # 只有因为行驶中门锁没锁停下的车才需要；其他情况不填即可
    doorLockRepaired = $true
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

### 路径三：有货时的人工确认解除（`REQ-0356`，`CP-0011` 修订）

车上有货或说不清有没有货时，由持异常处置权限的人解除 8005 自己那一层急停，不要求车上无货。

**要确认的事**：写明办理人（`operatorId`）和解除原因（`reason`），确认急停原因已经消除、全部仓门已经关好；
因为行驶中门锁没锁停下的车，还要确认门锁已经检修。

**服务端自己再核对的事**与路径二相同（`CAN_RECOVER`、是 8005 的急停、读得到 RIoT 订单），只有订单这一条放宽：
RIoT 上这台车唯一的未结束订单，若是服务端自己建的、此刻读作暂停（7），可以留着，解除后它仍然停着，要人「继续原单」。
多出来的拒绝码：

| 原因码 | 意思 | 怎么办 |
| --- | --- | --- |
| `EMERGENCY_CONFIRMER_NOT_AUTHORIZED` | 办理人不在现场人员角色名单里，或没有 R-13（AGV 运维/调度管理员）角色 | 找有这个角色的人来办 |
| `EMERGENCY_RELEASE_REASON_REQUIRED` | 没写解除原因 | 写上 |
| `EMERGENCY_HELD_ORDER_NOT_PAUSED` | 服务端自己那张单此刻不是暂停 | 解除后车可能直接开走，不能解；找值班工程师 |
| `EMERGENCY_RELEASE_FIELD_TOO_LONG` | 原因超过 500 字或办理人超过 128 字 | 写短一些 |

**每一次请求都会写一条不能改的管理员审计**（动作 `EMERGENCY_STOP_GOVERNED_LOADED_RELEASE`），被拒的也写。

**入口**：`POST /api/safety/v1/emergency-stop-releases/loaded`，和路径二挂在同一个开关上（`EmergencyStopRelease:enabled`），
但**凭据单独一把**：环境变量 `CONTROL_SERVER_EMERGENCY_LOADED_RELEASE_CREDENTIAL`（变量名由
`EmergencyStopRelease:loadedCredentialEnvironmentVariable` 决定），拿着路径二凭据的人解不了有货的车。没放这把凭据时入口回 503，
不影响路径二。

**和需求的差距，如实写在这里。**需求说异常处置权限归维护管理员和系统管理员、要本人凭据。服务端还没有按人登录，也没有这两个角色：
现在认的是「拿着这把凭据、自报的工号在现场人员角色名单里有 R-13」。等有了登录体系再改。

示例（PowerShell 7）：

```powershell
$body = @{
    agvId            = 'agv02'
    operatorId       = '工号'
    reason           = '门锁已更换，现场复核锁闭'
    causeCleared     = $true
    allDoorsClosed   = $true
    doorLockRepaired = $true
} | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:58007/api/safety/v1/emergency-stop-releases/loaded' `
    -Headers @{ Authorization = "Bearer $env:CONTROL_SERVER_EMERGENCY_LOADED_RELEASE_CREDENTIAL" } `
    -ContentType 'application/json' -Body $body
```

返回码与路径二相同；409 时响应里除了 `reasons`，还有逐条的中文说明 `descriptions` 和这次请求的审计记录号 `auditRecordId`。

**v2 并行实例上**（workspace#49）：上面两个示例写的是产品默认的端口与变量名，那是 MVP 的。v2 实例的入口在它自己的健康端口（`58107`），
两把凭据在它服务自己的 `Environment` 里，变量名是 `CONTROL_SERVER_V2_EMERGENCY_RELEASE_CREDENTIAL` 与
`CONTROL_SERVER_V2_EMERGENCY_LOADED_RELEASE_CREDENTIAL`，值存在控制端的 DPAPI 秘密存储里。怎么部署出来、谁在哪台机器上调用、示例命令，
见 8005-workspace `remote-ops/factory-server/docs/wire-to-gate-parallel-cd.md` 第 7.3 节。

### 解除之后

- **解除只结束这一次急停。**车辆故障阻断还在，这台车照样不派新单；清除故障有自己的规则，见
  [`vehicle-fault-clearance-field-guide.md`](vehicle-fault-clearance-field-guide.md)。
- **服务端不会因为车停在两站之间就再急停它。**对同一次故障，解除后只有读到车在动、读不到车的状态、读数过期，
  或者车出现在另一个站点时，服务端才会再次急停，这算新的一次（2026-09-15 用户裁定）。门锁原因停下的车，
  解除后门锁又读不到锁闭，也会再次急停。
- **不要在 RIoT 里手动解锁 8005 的急停。**服务端没有解除过的急停变回 `OK`，会被当成意外恢复，立即重新急停并报
  `EMERGENCY_LATCH_RELEASED_EXTERNALLY`（`REQ-0248`）。要解除，走上面的人工确认。别人按的急停（触边、Matrix、
  RIoT、车尾触屏）在原来的地方解；若服务端在那上面记了自己的一类，解开后它会立即再停一次，这时同样走人工确认。

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
