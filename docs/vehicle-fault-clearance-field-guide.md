# 车辆故障的人工清除与续行

**给现场人员与值班工程师看。**服务端把一台车判为故障之后，车怎么回来：谁来确认、服务端自己核对什么、
清除之后需求和货怎么走。

本入口由 control-server#299 落地（2026-09-22 用户定方案 F-a 与 HTTP 入口）。实现在
`src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.cs`，HTTP 入口在
`src/ControlServer.Host/Runtime/VehicleFaultRecoveryEndpoints.cs`。急停本身的现场兜底与人工解除见
[`emergency-stop-field-fallback.md`](emergency-stop-field-fallback.md)，**本说明默认你已经读过那一份**。

## 车为什么会带着故障

服务端今天唯一会自动判出故障的情况是：**车正在执行的运单在 RIoT 上变成 FAILED**（旅程阻断码
`VEHICLE_ORDER_FAILED`）。按 `REQ-0232`，一次 FAILED 只是「症状」，证明不了车真的坏了，但服务端会把车判为
「疑似故障」并做三件事：

- **这台车不再接新单**，它身上的需求也不改派；
- 尝试把那张单 Hold 住（对 FAILED 单这一步必然不成功，没关系）；
- 证不出车停稳时（比如车停在两站之间、读不到位置），**自动发软件急停**。

在本入口之前，这个故障**没有任何办法清掉**：唯一的清除路径要求订单被 Hold 成 PAUSED，而 FAILED 永远不是。
车会一直不接单，只能改数据库。现在由人确认、服务端核对后清除。

**不要改数据库，不要重启服务端来「清」故障。**故障存在 SQLite 的 `VehicleFaultStates` 表里，重启不会清掉；
手工改表会绕过下面每一条核对。

## 处置顺序

1. **到现场确认原因并排除。**车为什么失败（障碍、导航、机构），排除了才往下走。
2. **急停锁住了吗？**看 RIoT 或看板。锁住了（`CAN_RECOVER`），**先按急停现场说明里的「人工确认解除」
   （`REQ-0356`）解开**；`CAN_NOT_RECOVER` 转 RIoT 人员。**清除故障本身不会解除急停**，锁着的车清不了故障。
3. **在 RIoT 里确认这台车没有未结束的订单。**FAILED 的那张单已经结束，不用处理；如果还有别的单挂在这台车上，
   看它是不是服务端建的（`upperId` 以 `W2G-` 开头）：**不是的，直接在 RIoT 里取消**（2026-09-22 用户定，见下面
   「急停锁着、车上还挂着一张 HANG 的单」一节）；是的，不要取消，找值班工程师。
4. **经下面的入口清除故障。**服务端核对通过就清除，并按车上有没有货处置旅程。

## 服务端自己核对什么

请求里只信两件事：**你是谁**（`operatorId`），以及**你确认原因已经排除**（`faultRemedied`）。其余每一条都由
服务端自己读，读不到就当不满足。**任何一条不满足都拒绝，响应里列出全部原因**，不是只列第一条。

| 原因码 | 意思 | 怎么办 |
| --- | --- | --- |
| `FAULT_RECOVERY_OPERATOR_UNIDENTIFIED` | 没填工号 | 填上，清除人身份必须记下 |
| `FAULT_RECOVERY_REMEDY_NOT_CONFIRMED` | 没确认「故障原因已排除」 | 排除后再确认 |
| `FAULT_RECOVERY_FAULT_NOT_IN_EFFECT` | 这台车没有故障 | 不需要清除 |
| `FAULT_RECOVERY_EMERGENCY_LATCHED` | 急停还锁着 | 先按 `REQ-0356` 人工解除，再来清除 |
| `FAULT_RECOVERY_EMERGENCY_STOP_OPEN` | 服务端的急停还没走完：发了急停但还没读到锁住，或锁住后被别人解开、服务端正在重新急停 | 等急停锁住，按 `REQ-0356` 解除后再来 |
| `FAULT_RECOVERY_EMERGENCY_STATE_UNKNOWN` | 读不到急停状态 | 等 RIoT 恢复后再试 |
| `FAULT_RECOVERY_VEHICLE_ORDER_NOT_FINISHED` | RIoT 里这台车还有没结束的订单（排队、执行、暂停或 HANG） | 不是服务端建的单，直接在 RIoT 里取消；是服务端建的，找值班工程师。**清除之后车会重新接单**，RIoT 上还有活单时清除，车可能被那张单开走 |
| `FAULT_RECOVERY_VEHICLE_ORDERS_UNKNOWN` | 读不到这台车的订单 | 等 RIoT 恢复后再试；读不到不当成「没有订单」 |
| `FAULT_RECOVERY_CURRENT_ORDER_NOT_ENDED` | 旅程等的那张单还没结束：还在跑、被 Hold（PAUSED）、HANG、状态 8，或者其实已经 SUCCESS（车到了） | PAUSED 的走下面的「续行」；HANG 在 RIoT 里 continue；SUCCESS 说明车到了，旅程会自己往下走 |
| `FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT` | 旅程等的那张单是服务端自己的，被人在 RIoT 里取消或删除了 | 不走本入口：这种单不改派，由服务端按同车同需求重建（见下面「要分清两种取消」） |
| `FAULT_RECOVERY_CURRENT_ORDER_UNKNOWN` | 读不到那张单，或 RIoT 说没有这张单 | 等 RIoT 恢复后再试 |
| `FAULT_RECOVERY_STATE_CHANGED` | 服务端读 RIoT 的这几秒里，调度把这趟旅程往前推了（比如车刚好到站），读到的东西已经不作数 | 重发同一个请求 |
| `FAULT_RECOVERY_RUNTIME_BUSY` | 服务端这一轮调度 30 秒内没结束 | 稍后再试 |

「那张单已结束」**只认 FAILED**。CANCELLED、DELETED 也是结束，但清除之后需求会被释放改派，而服务端自己的单被
取消要的是重建、不是改派，所以单独拒绝。

服务端**先读 RIoT、再进调度锁**：调度每一轮都要拿同一把锁，RIoT 慢的时候（车出故障时往往正是这样）如果在锁里读，
一个清除请求就能把所有车的调度拖住。代价是读到的东西可能在进锁之前就过时了，所以进锁后会再对一遍服务端自己的
记录，对不上就回 `FAULT_RECOVERY_STATE_CHANGED`，什么都不改。

## 清除之后

清除和下面的旅程处置**在同一个数据库事务里一起完成**：要么全做了，要么什么都没做。中途断电或进程退出，
重发同一个请求即可。

- **车上没有货**（车还在去取货的路上）：这趟旅程上还没取货的需求**释放改派**，旅程关闭。车重新空闲，
  下一轮调度就可能接到新单——包括刚被释放的那条需求。返回里 `disposition = RELEASED_FOR_REDISPATCH`。
- **车上可能有货**（已经装货、正在装货，或者服务端说不清）：**货物绑定保留，需求不释放**，旅程转为阻断，
  码是 `VEHICLE_FAULT_CLEARED_CARGO_ON_BOARD`。这台车不接新单，等人处置。按 `REQ-0328`，已经取货的需求不能改派；
  让这辆车带着货继续走，服务端**目前没有入口**，找值班工程师。返回里 `disposition = HELD_FOR_PERSON`。

故障清除后，这台车的急停自动解除条件里「原因已消除」这一条就满足了；但本入口要求急停已经解除才清除，所以
正常情况下清除时车上已经没有锁。

**同一个请求点两次**：第二次返回 200，`outcome = AlreadyCleared`，什么都不做——不会碰清除之后这台车刚接的新单。
续行也一样，第二次不会再发一次 continue。但「已经清过」之前**仍然先核对工号和「原因已排除」**：没填的照样拒绝，
不因为车已经清过就放过。

## 续行被 Hold 的原单

订单在 RIoT 里是 **PAUSED（7）**，也就是被 Hold 住了，原因排除后可以让**同一辆车继续同一张单**（`REQ-0239`
上半句）。把 `action` 换成 `RESUME_HELD_ORDER`：服务端核对工号、「原因已排除」、急停已解除，再核对订单确实是
PAUSED、订单号与车辆一致、货物绑定与这趟旅程一致，然后发 `CONTINUE_FROM_HELD`，**读回订单在执行之后才清故障**。
旅程不动，照常往下走。拒绝原因除了上表的几条，还有 `RESUME_ORDER_NOT_HELD`、`RESUME_ORDER_IDENTITY_MISMATCH`、
`RESUME_CARGO_BINDING_MISMATCH`、`RESUME_CONTINUE_NOT_CONFIRMED` 等。

**急停锁着时不能续行**，入口会直接拒绝：锁住时 RIoT 会拒绝 continue；而续行之后订单在跑，人工解除急停又要求
车上没有未结束的订单，两头互等。要续行，先解除急停。

HANG（9）不走这里：在 RIoT 里 continue，或者等 #319。

## 急停锁着、车上还挂着一张 HANG 的单

**服务端里没有出口。**三条路都走不通：

- 在 RIoT 里 continue：急停锁着时 RIoT 拒绝（实验室 Round27，`100021`）。
- 人工解除急停（`REQ-0356`）：要求车上没有未结束的订单，HANG 算未结束，拒绝（`EMERGENCY_VEHICLE_ORDER_NOT_FINISHED`）。
- 本入口清除故障：急停锁着、车上有未结束订单，两条都不满足，拒绝。

**但按代码，本服务端自己不会造出这个局面。**服务端只在旅程当前那张单被 RIoT 报成 FAILED 时才记故障、才可能急停
（`JourneyRuntimeEngine.ObserveOrderFailureAsync`）；HANG 只写 `ORDER_HANG`、告警，不 Hold、不急停、不记故障（#316）。
FAILED 是终态，那张单不会再变成 HANG（按 RIoT 状态模型推，实验室没有这个观测）。所以看到「锁着 + HANG」，说明两件事之一：

- 那张 HANG 的单**不是服务端这趟旅程正在执行的那张**（比如有人在 RIoT 里给这台车另建了单）；
- 或者这个急停**不是服务端发的**（急停说明里的 `EMERGENCY_NOT_RAISED_BY_8005`，本来就归 RIoT 人员）。

**现场怎么做**（2026-09-22 用户定，原话「如果是外界创建的订单直接取消就好了，系统不能被干扰」）：

1. 先确认那张 HANG 的单**不是服务端建的**：服务端建的单，`upperId` 以 `W2G-` 开头，并出现在看板这台车的旅程上。
2. **不是服务端建的，现场人员直接在 RIoT 里取消它。**系统不能被外来订单干扰。
3. 车上没有未结束的订单之后，按急停说明人工解除急停（`REQ-0356`）。
4. 回到本入口清除故障。

**要分清两种取消。**服务端**自己的**在途单在 RIoT 里被取消，仍然算误操作，不改派：#318 合入之后，服务端会自己按
同一辆车、同一条需求重建订单，**不需要人确认**（2026-09-22 用户定，原话「直接自己恢复好了，不用人确定，因为一般没人盯着系统」）。
#318 合入之前，服务端只挡住、报警（`ORDER_ENDED_WITHOUT_ARRIVAL`，#316），旅程停在原处等它。本入口不处理这种单
（`FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT`）。只有**外来**订单才由现场直接取消。

不要改数据库，不要在 RIoT 里手动解锁（会被当成意外恢复立即重触发）。急停不是服务端发的（`EMERGENCY_NOT_RAISED_BY_8005`），
仍然转 RIoT 人员。把 HANG 纳入故障模型、给服务端加闩锁下的续行顺序，是 #319。

## 入口

`POST /api/safety/v1/vehicle-fault-recoveries`，在服务端的健康端口上（默认 `127.0.0.1:58007`）。

- **默认不开。**现场要把配置项 `VehicleFaultRecovery:enabled` 设为 `true`，并在环境变量
  `CONTROL_SERVER_FAULT_RECOVERY_CREDENTIAL`（变量名由 `VehicleFaultRecovery:credentialEnvironmentVariable` 决定）里
  放入调用凭据。打开入口却没放凭据，服务端启动校验不通过、起不来；运行中凭据变量被清空，入口回 503。
  **这份凭据与急停人工解除的凭据是分开的**，现场可以只开其中一个。
- **这还不是登录。**服务端没有登录和权限体系（`REQ-0253` 要求管理员个人账号），过渡期用一份共用凭据挡住随手调用，
  清除人是谁由请求里的 `operatorId` 说明。服务端把它写进故障事实的 `ClearedReason`（续行时还写进 continue 的命令
  审计），并打一条事件 9203 日志，连同结果、全部原因和备注（2026-09-22 用户认可沿用 `REQ-0356` 的做法）。
- 看板按钮后补；车载端入口要新增协议消息，随协议 v3.0.0 提供。

示例（PowerShell 7）：

```powershell
$body = @{
    agvId         = 'agv02'
    operatorId    = '工号'
    action        = 'CLEAR_FAULT'      # 或 RESUME_HELD_ORDER
    faultRemedied = $true
    note          = '现场说明，可不填'
} | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:58007/api/safety/v1/vehicle-fault-recoveries' `
    -Headers @{ Authorization = "Bearer $env:CONTROL_SERVER_FAULT_RECOVERY_CREDENTIAL" } `
    -ContentType 'application/json' -Body $body
```

**返回**：

| 状态码 | 意思 |
| --- | --- |
| 200 | 已清除（`Cleared`，看 `disposition`）、已续行（`Resumed`），或这台车的故障早已由人工清除（`AlreadyCleared`） |
| 409 | 拒绝，响应里的 `reasons` 列出全部原因码 |
| 401 / 404 / 422 / 503 | 凭据不对 / 不是本服务端管的车 / 缺 `agvId` 或 `action` 不认识 / 入口没配凭据或服务端这一轮太久没结束 |

## 目前还做不到的，先说清楚

- **故障期间的需求不会自动改派**，要等人清除之后才释放（cs#215 的规则，故障监看解耦后放开，见 #317）。
- **车上有货时清除之后只能人工处置**，服务端没有让它继续的入口。
- **HANG 不是故障**（#316 的做法 H-a），不经过本入口；纳入故障模型见 #319。
- **车载端会话未就绪时，旅程码会被写成 `ONBOARD_SESSION_NOT_READY`，盖掉 `VEHICLE_ORDER_FAILED`**，引擎那时也不推进
  故障监看（停车证明、`REQ-0248` 重触发都挂在旅程上）。这是按代码推出来的（会话闸门对 FAILED 单不保留原码），
  还没在真车载端上看到过；真车载端在急停锁住后会不会未就绪，也还没核实。**判断车有没有故障以故障状态为准，
  不以旅程码为准**；故障监看与旅程解耦见 #317。清除本身不依赖会话是否就绪。
