# 车辆故障的人工清除与续行

**给现场人员与值班工程师看。**服务端把一台车判为故障之后，车怎么回来：谁来确认、服务端自己核对什么、
清除之后需求和货怎么走。

本入口由 control-server#299 落地（2026-09-22 用户定方案 F-a 与 HTTP 入口）；清除之后怎么处置由 control-server#318 改写
（2026-09-23 用户定，原话「不改派啊，留在本车上」：不论车上有没有货，都给同一辆车、同一条需求重建订单）。实现在
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
   看它是不是服务端建的（`upperId` 以 `W2G-` 开头，重建出来的单是 `W2G-…-REBUILD-…`）：是的，不要取消，找值班工程师；不是的，按下面「急停锁着、
   车上还挂着一张 HANG 的单」一节的做法，**先核实它确实在我们这台车上，只取消这一张**。RIoT 上还有别的区域的车
   和它们的订单，那些绝不能动。
4. **经下面的入口清除故障。**服务端核对通过就清除；需求留在本车，**延迟一段时间（默认 30 秒）之后服务端自动给这辆车
   重建订单，车会再动**。清除之前请确认车周围可以走车。

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
| `FAULT_RECOVERY_VEHICLE_ORDER_NOT_FINISHED` | RIoT 里这台车还有没结束的订单（排队、执行、暂停或 HANG） | 不是服务端建的单：核实它在我们的车上（按 RIoT `deviceKey`／`id`，见下一节）后只取消这一张；是服务端建的，找值班工程师。**清除之后服务端会给这辆车重建订单**，RIoT 上还有活单时清除，车可能先被那张单开走 |
| `FAULT_RECOVERY_VEHICLE_ORDERS_UNKNOWN` | 读不到这台车的订单 | 等 RIoT 恢复后再试；读不到不当成「没有订单」 |
| `FAULT_RECOVERY_CURRENT_ORDER_NOT_ENDED` | 旅程等的那张单还没结束：还在跑、被 Hold（PAUSED）、HANG、状态 8，或者其实已经 SUCCESS（车到了） | PAUSED 的走下面的「续行」；HANG 在 RIoT 里 continue；SUCCESS 说明车到了，旅程会自己往下走 |
| `FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT` | 旅程等的那张单是服务端自己的，被人在 RIoT 里取消或删除了 | 不走本入口：服务端自己会按同车同需求重建（#318），不需要人清除。见下面「要分清两种取消」 |
| `FAULT_RECOVERY_CURRENT_ORDER_UNKNOWN` | 读不到那张单，或 RIoT 说没有这张单 | 等 RIoT 恢复后再试 |
| `FAULT_RECOVERY_STATE_CHANGED` | 服务端读 RIoT 的这几秒里，调度把这趟旅程往前推了（比如车刚好到站），读到的东西已经不作数 | 重发同一个请求 |
| `FAULT_RECOVERY_RUNTIME_BUSY` | 服务端这一轮调度 30 秒内没结束 | 稍后再试 |
| `FAULT_RECOVERY_RESUME_IN_PROGRESS` | 同一台车的另一个续行请求正在进行（只用于续行） | 稍后再发，会得到结果或「已经清过」 |

「那张单已结束」**只认 FAILED**。CANCELLED、DELETED 也是结束，但服务端自己的单被取消由服务端自己重建（#318 的第一个来源），
不经过人清除；让本入口也接它，同一次终结就会有两条路去处理，所以单独拒绝。

服务端**先读 RIoT、再进调度锁**：调度每一轮都要拿同一把锁，RIoT 慢的时候（车出故障时往往正是这样）如果在锁里读，
一个清除请求就能把所有车的调度拖住。代价是读到的东西可能在进锁之前就过时了，所以进锁后会再对一遍服务端自己的
记录，对不上就回 `FAULT_RECOVERY_STATE_CHANGED`，什么都不改。

## 清除之后

清除和下面的旅程处置**在同一个数据库事务里一起完成**：要么全做了，要么什么都没做。中途断电或进程退出，
重发同一个请求即可。

**不论车上有没有货，需求都不释放、不改派，旅程不关闭**（#318，2026-09-23 用户定：「不改派啊，留在本车上」）。
旅程停在它原来的阶段（开往取货站或开往卸货站），服务端记下「这张单要为同一辆车、同一条需求重建」，返回里
`disposition = REBUILD_SCHEDULED`。延迟到点、车况允许之后，服务端自动给同一辆车建一张开往**同一个停靠**的新单，
这趟接着走，剩下的停靠一个不变。

- **车上没有货**（车还在去取货的路上）：旅程码 `VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD`，新单开往原来那个取货站。
- **车上可能有货**（已经装货、正在装货，或者服务端说不清）：旅程码 `VEHICLE_FAULT_CLEARED_CARGO_ON_BOARD`，新单开往
  原来那个卸货站，把这趟送完（2026-09-23 用户定，原话「清除后自动重建订单送完这趟」）。故障时保留的货物绑定在新单确认
  建成之后以 `REBUILT_ON_ORIGINAL_VEHICLE` 了结：货在原车上继续走。
  **有货时多一步：先要车证明货还在原仓**（`REQ-0362` 保留的修复续行前提「货物仍完整保留于原仓位并重新安全闭环」）。
  清除之后服务端向车要一份新的仓位读数（`SafetyStateSnapshotRequested`），只认**清除之后**收到的读数：装货时放货的每一个仓
  都要读到有货（`OCCUPIED`）、门锁着（`LOCKED`）、开锁输出已复位（`RESET`），且整车没有未知（`unknownPresent=false`），
  才进入重建。读数还没到时旅程码是 `OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE`（多半是车载端没连上或没就绪，检查车载端）；
  读数到了、而且**确证货不在**——放货的仓读到空（`EMPTY`），哪怕只有一个——就**不再自动重建**，旅程码
  `OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE`，错误级告警：货可能已经不在原仓，请到车前核对货物与仓门，再按下面「重建停住之后」
  一节把这一趟转进车载端的异常处置会话取货交接。
  读数到了但**判不了**——货在但门没锁好、开锁输出没复位、仓位读数是未知或没上报、整车报有未知，或装货本身还没落定——
  服务端**不停、也不建单**，等车下一次报读数，旅程码 `OWN_ORDER_REBUILD_CARGO_UNPROVEN`（`OwnOrderRebuilds` 那一行的
  「在等什么」写明卡在哪一项）。车只在连上时和被要时才报读数，所以判不了的读数收到满 10 秒、且离上一次要读数也满 10 秒后，服务端会再要一次；
  车一直不回应时也是每 10 秒至多一次。门锁好、读数恢复之后自动重建；一直不消失就到车前看仓门与货。门没锁好归「判不了」而不是
  「货不在」，因为货还在、只是仓没锁好，这是一个人走开就会变的状态；它也不会让车带着没锁好的仓出发——建单前还要车载端说
  目标仓全锁、开锁输出全复位（下一节第 2 条）。
  服务端向车要读数有节流：同一个会话只要一次，会话在未就绪时要过的、就绪之后可以再要一次，重连后的新会话再要一次；
  车在握手过程中不会被要。这一步是 cs#318 的范围补充（2026-09-23，用户选定「由车报的新鲜读数证明」，经调度转达）。

重建的延迟、车况判据和「短时二次出问题即停」见下一节。这趟旅程不关闭，所以清除时**不给车发收尾快照**（#299 合入时
无货清除会发，#318 起没有这一步）：车上显示的那一站，正是重建的单要去的那一站。

故障清除后，这台车的急停自动解除条件里「原因已消除」这一条就满足了；但本入口要求急停已经解除才清除，所以
正常情况下清除时车上已经没有锁。

**同一个请求点两次**：第二次返回 200，`outcome = AlreadyCleared`，什么都不做——不会碰重建出来接着走的这趟，
也不会再记一次重建（重建记录按那张 FAILED 单的单号认，一张单只记一次）。
续行也一样，第二次不会再发一次 continue；两个续行请求**同时**到（连点两下、两个人同时点），只有一个会发
continue，另一个回 409 `FAULT_RECOVERY_RESUME_IN_PROGRESS`，过一会儿再发会得到「已经清过」。但「已经清过」之前**仍然先核对工号和「原因已排除」**：没填的照样拒绝，
不因为车已经清过就放过。

## 清除之后的自动重建（#318）

服务端自己建的单终结之后——被人在 RIoT 里取消或删除，或者 FAILED 之后故障由人经本入口清除——服务端为**同一辆车、
同一条需求**重建订单，去原来那一站，**不释放、不改派、不等人确认**（2026-09-22 用户定，原话「直接自己恢复好了，
不用人确定，因为一般没人盯着系统」）。三道防护：

1. **延迟**：清除成功（或读到取消）之后先等一段时间再建，默认 30 秒（配置项 `JourneyRuntime:OwnOrderRebuildDelay`）。
   这段时间里旅程码是上一节那两个码之一（取消来源是 `ORDER_ENDED_WITHOUT_ARRIVAL`）。**要让车别动，就在这段时间里让车
   急停或切到手动**，重建会一直等下去（见第 2 条）。
2. **车况不允许就不建**：服务端读 RIoT 的车辆安全读取——车载端安全投影用的同一次读、同一组原因码——再加服务端自己的
   故障事实与车所在的图。车在急停（`RIOT_EMERGENCY_NOT_OK`）、手动或下线（`RIOT_VEHICLE_NOT_ENABLED`、
   `RIOT_VEHICLE_NOT_ONLINE`、解抱闸 `RIOT_BRAKE_NOT_MOVABLE`）、故障（`RIOT_CONTROL_NOT_OK`、服务端的
   `VEHICLE_FAULT_IN_EFFECT`）、不在本图（`RIOT_VEHICLE_MAP_MISMATCH`）、车上还挂着别的单或还在动时，都不建。
   **车载端也要说这辆车可以走**：会话已就绪，且车载端的安全摘要是可离站、车已停、目标仓全锁、开锁输出全复位、没有未知——
   与一趟旅程派第一张单时同一组判据（记录上写 `ONBOARD_FACTS_NOT_READY` 或 `ONBOARD_DEPARTURE_UNSAFE`）。车载端会话没就绪时
   也只记下、不建单。这些情况旅程码都是 `OWN_ORDER_REBUILD_WAITING_VEHICLE`，日志事件 2172 写明在等什么，都恢复之后下一轮自动建。
3. **短时二次出问题即停**（`REQ-0361`）：同一条需求第一次出问题之后的一段时间内（默认 10 分钟，配置项
   `JourneyRuntime:OwnOrderRebuildRepeatWindow`，**从第一次出问题的时刻算起**，不是从重建建成算起）又出问题，就**不再自动重建**，
   旅程码 `OWN_ORDER_REBUILD_STOPPED`，错误级日志事件 2173。「又出问题」按第一次的来源认：第一次是被取消的，只有再被取消或删除才算；
   第一次是故障清除的，再 FAILED（清除时判）或被取消、删除都算。清除时遇到这种情况，返回 `disposition = REBUILD_STOPPED`。这时要人到现场与 RIoT 查明为什么反复停下，
   然后按下一节「重建停住之后」选一个出口；在那之前需求不改派、这台车不接新单。

有货时的仓位读数一步见上一节「车上可能有货」。新单和每一张还不存在的移动单一样，建之前还要过建单门禁（`REQ-0305`：地图目录新鲜、任务类型没被挂起、目标站可达）；
门禁不放行时旅程码 `OWN_ORDER_REBUILD_BLOCKED_BY_CREATE_GATE`。新单发给 RIoT 之后还没确认建成时是
`OWN_ORDER_REBUILD_ORDER_UNCONFIRMED`，服务端每一轮按同一个单号对账，不会建第二张。新单**还没确认建成就终结了**，按它怎么
终结处理，与确认过的单一样：被取消或删除的，算一次新的出问题，按上面第 3 条的窗口判（窗口内停住、窗口外再重建）；FAILED 的，
按普通 FAILED 记故障（旅程码 `VEHICLE_ORDER_FAILED`），照本文前面的步骤清除即可，清除后照常重建——第一次是取消时，FAILED
不算「又出问题」。RIoT 报状态 8（SUSPENDED，实验室里从没见过）时停住等人，旅程码 `OWN_ORDER_REBUILD_STOPPED`；
紧接着再读没读到的，旅程码 `OWN_ORDER_REBUILD_ORDER_UNCONFIRMED`，下一轮再读。确认前就已成功的单直接算建成。每一次终结、每一次被拦下、每一次重建，
都记在表 `OwnOrderRebuilds` 的同一行上（来源、谁清除的、终结的单、新单、在等什么、何时建成或为何停下），并打告警日志
（事件 2170～2174）。

**服务端自己取消的单不重建**：释放改派时服务端会经订单命令面取消开往取货站的那张单，那是有意的决定，旅程随即关闭；
万一关闭没落库，旅程停在 `ORDER_ENDED_WITHOUT_ARRIVAL` 等人，事件 2174。

## 重建停住之后（#345）

自动重建停住有两种：护栏三停住（旅程码 `OWN_ORDER_REBUILD_STOPPED`）与货不在原仓（`OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE`）。
`REQ-0361` 说这时「由人员处理」；#345 之前服务端没有给人的出口，只能找工程师改库。现在三个出口都走本入口（下面「入口」一节），
与清除故障同一份凭据、同一个 `operatorId`、同一条事件 9203。**只对停住的旅程生效**：重建还在等延迟、旅程正常在途、
重建因车不再合格而停（那一种由释放服务改派），一律拒绝（409），什么都不改。

先到现场与 RIoT 查明为什么反复停下，再按车上有没有货选：

| 情况 | 能用的出口 |
| --- | --- |
| 护栏三停住，车上没货（车还在去取货的路上） | 人工重建 `REBUILD_STOPPED_ORDER`，或放弃这趟 `TERMINATE_STOPPED_TRIP` |
| 护栏三停住，车上有货或可能有货 | 人工重建 `REBUILD_STOPPED_ORDER`，或转交接 `PREPARE_CARGO_HANDOFF` |
| 货不在原仓 | 只能转交接 `PREPARE_CARGO_HANDOFF`（`REQ-0238`：货不在原仓，就不能修复续行） |

**人工重建 `REBUILD_STOPPED_ORDER`**（要 `faultRemedied = true`，意思是「反复停下的原因已查明」）。服务端立即给同一辆车、
同一条需求重建一次，去原来那一站；**不等延迟**，但车况、建单门禁、车载端会话就绪与离站判定照样要过，车上有货的故障来源
还要车在这次请求**之后**报一份仓位读数证明货在原仓，和自动重建完全一样（上一节第 2 条、上一节「车上可能有货」）。
**`REQ-0361` 的窗口从这次请求的时刻重新算**（调度 2026-09-23 定）：人工重建之后在窗口内（默认 10 分钟，配置项
`JourneyRuntime:OwnOrderRebuildRepeatWindow`）这条需求再出问题，照样停住等人；过了窗口才恢复自动重建。返回 `outcome = RebuildRequested`、`disposition = REBUILD_SCHEDULED`。停住的那条重建记录
原行重开，`OperatorId` 改成发起人工重建的人；故障来源那一行原来记的清除人被覆盖，清除人仍在故障事实的 `ClearedReason`
与当时的事件 9203 里查得到。

**放弃这趟 `TERMINATE_STOPPED_TRIP`**（2026-09-23 用户允许，原话「允许」）。只在**车上没货**、且 RIoT 上这辆车没有未结束的
订单时生效：这趟旅程上还没装的需求全部终结，旅程以 `TERMINATED_BY_OPERATOR_AFTER_REBUILD_STOP` 收尾，车辆占用释放，
车恢复接单，收尾快照当场发给车。**不释放、不改派**：用户的读法是它属于 `REQ-0361` 说的「由人员处理」。
**业务后果要知道**：终结按 `DemandId` 算，**MES 那边这条需求还挂着，8005 以后不会再接它**；货要人另外搬，MES 要人手工收尾。
返回 `outcome = TripTerminated`、`disposition = TRIP_TERMINATED`，`terminatedDemandIds` 列出这次终结的需求（事件 9203 里同样有
`terminated: …`），**MES 收尾照这张单子做**；放弃的人记在那条重建记录的 `OperatorId` 上。以下情况拒绝，什么都不改：

- 车上有货或可能有货（`OWN_ORDER_REBUILD_EXIT_CARGO_ON_BOARD`）；
- RIoT 上有未结束的单或读不到（与清除时同样的 `FAULT_RECOVERY_VEHICLE_ORDER_NOT_FINISHED`、`FAULT_RECOVERY_VEHICLE_ORDERS_UNKNOWN`）；
- 车被急停锁着或急停还没收尾（`FAULT_RECOVERY_EMERGENCY_*`），或者还挂着一次没清除的故障（`FAULT_RECOVERY_FAULT_IN_EFFECT`）：
  旅程收尾会停掉挂在旅程上的故障监看，先解除急停、清除故障；
- 停住的是「重建出来的新单还没确认就被读成终结」那一种（`OWN_ORDER_REBUILD_EXIT_NEW_ORDER_UNSETTLED`）：那张单在 RIoT 上可能
  还挂在状态 8（SUSPENDED），而放弃前服务端核对「这辆车有没有未结束的单」时看不见状态 8，所以不许放弃，只能人工重建。
  能放弃的只有「重建出来的单在窗口内又出问题」的那一种。

**转交接 `PREPARE_CARGO_HANDOFF`**。把这一趟转进**车载端**的异常处置会话，在那里取出、交接并终止（`REQ-0238`）：

1. 经本入口发 `PREPARE_CARGO_HANDOFF`。旅程转为阻断，旅程码 `OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF`；服务端向车要一次
   仓位读数，车载端会话随之判为需要恢复（原因 `CARGO_HANDOFF_REQUIRED`），车载端才会出现故障货物交接的入口。
   返回 `outcome = HandoffPrepared`、`disposition = AWAITING_CARGO_HANDOFF`。车上没货时拒绝（`OWN_ORDER_REBUILD_EXIT_NOTHING_ON_BOARD`）。
   转交接的人记在那条重建记录的 `OperatorId` 上。转交接之后这一趟不能再人工重建（`OWN_ORDER_REBUILD_EXIT_AWAITING_CARGO_HANDOFF`）。
2. **车载端要事先打开 `wireToGate.recoveryResumeEnabled`（出厂是 `false`）**，并配好恢复认证凭据与操作员工号；不然交接入口
   不会出现。服务端这边的恢复管理员凭据（`Recovery:AuthenticationProofEnvironmentVariable` 指的那个变量）也要配好。
3. 在车载端打开异常处置、选故障货物交接（`FAULT_CARGO_HANDOFF`），按车载端提示把货取出、交接。车报交接完成、放货的仓读空之后，
   需求以 `TERMINATED_BY_FAULT_CARGO_HANDOFF` 终结、旅程收尾，故障时留下的货物绑定以 `HANDED_OFF_IN_EXCEPTION_SESSION` 了结，
   车恢复接单。

**交接没有一次成功时**，这一趟不会回到只能改库（独立审查 M1）：

- **交接失败**（车报 `FAILED`）：旅程码换成 `FaultCargoRecoveryResult_NOT_RECONCILED`，货还在车上、货物绑定不了结，车载端会话
  仍是需要恢复、交接入口还在。可以直接在车载端再交接一次；要把旅程码挂回来、让服务端再向车要一次仓位读数，就再发一次
  `PREPARE_CARGO_HANDOFF`。这时放弃会以「车上有货」被拒。
- **一趟里只交接掉一部分**（例如一条已装的交接了，另一条还没装）：旅程不收尾、仍阻断，还没装的那条开不出会话。这时车上没货了，
  发 `TERMINATE_STOPPED_TRIP` 放弃剩下的：它们终结、旅程收尾、车恢复接单，MES 那边照 `terminatedDemandIds` 手工收尾。

**已知限制**：车上装着**两条及以上**已装需求时，车载端只能对**最后装的那一条**发起交接，更早装的那条交接不了——这是车载端的
限制，另开车载端票处理。遇到这种情况找值班工程师。

三个出口**同一个请求点两次**，第二次都返回 200、`outcome = AlreadyDone`，什么都不做：人工重建不会再重开、不会改写时刻与署名，
新单已经发出的也不会被退回；放弃这趟不会再终结一次、不会再发收尾快照；转交接不会再要一次读数。

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

**现场怎么做**（2026-09-22 用户定，原话「如果是外界创建的订单直接取消就好了，系统不能被干扰」；同日用户补充：
RIoT 上还有别的区域的车和它们的订单，绝不能随手取消）：

1. 先确认那张 HANG 的单**不是服务端建的**：服务端建的单，`upperId` 以 `W2G-` 开头，并出现在看板这台车的旅程上。
2. **再确认它确实在我们的车上。**只认 `agv02`、`agv03`，而且要按 RIoT 的 `deviceKey` 或 `id` 对，不能只看车名：
   两台车的这两个值见 `remote-ops/fleet.md`（工作区里的车队登记册）。对不上，或者说不清，就不要动，转 RIoT 人员。
3. **两条都确认了，才在 RIoT 里取消这一张单，只取消这一张。**
4. 车上没有未结束的订单之后，按急停说明人工解除急停（`REQ-0356`）。
5. 回到本入口清除故障。

**第 2、3 步什么时候改由服务端做：**control-server#330 之后，服务端每一轮都认一遍我们车上的外来订单，结果在看板车队视图
「车上的外来订单」里，一张单一行。它只认**正在运行（执行中、暂停、HANG）而且 RIoT 显示就在我们车上**的单；排队中的订单
在 RIoT 上看不出归哪辆车，服务端永远不碰。认出来之后：

- **这套部署被授权取消外来订单时**（配置 `RiotForeignOrderCancel:Enabled` 为 `true`，默认 `false`）：服务端自己取消，
  只发一次；取消没见效，看板写 `FOREIGN_RUNNING_ORDER_STILL_RUNNING_AFTER_CANCEL`，第 3 步仍由人做。
- **没被授权时**（默认，例如并行实例）：服务端不取消，看板写 `FOREIGN_RUNNING_ORDER_CANCEL_NOT_AUTHORIZED`，第 2、3 步照旧
  由现场人员手工做。
- 服务端证明不了是不是自己建的（看板 `FOREIGN_RUNNING_ORDER_OWNERSHIP_UNPROVEN`）：永远不取消，第 1～3 步由人做。
- 那张单离开了 RIoT 的运行列表，按订单号回查却读不到它已终结，超过 10 秒（看板 `FOREIGN_RUNNING_ORDER_UNSETTLED`）：
  常见的是回查接口调用失败，或单子处在 SUSPENDED(8)；RIoT 一般不删单，删了查不到是少见情况。服务端只认明确终结才放车，
  而本服务端目前没有人工解除入口，所以要到 RIoT 按订单号核对并把它结束；RIoT 里查不到这张单时找值班工程师。

无论哪种，那张单明确终结之前，这台车都不接新单、不接途中追加。

**授权取消之后要知道的：**落在我们车上的非本系统订单一律取消，不豁免（用户 2026-09-23 定）。在 RIoT 里给我们的车手工下的
挪车单和充电单、行为实验室的实验单、RIoT 自己生成的充电单，都算外来订单，会被服务端取消，车会停在半路。要挪我们的车、
给它充电，在车上用单机方式操作，不要在 RIoT 里给它下单。

**要分清两种取消。**服务端**自己的**在途单在 RIoT 里被取消，仍然算误操作，不改派：服务端自己按同一辆车、同一条需求
重建订单，**不需要人确认**，带上一节的三道防护（#318）。所以**不要为了让车停下而在 RIoT 里取消服务端的单**——延迟一过
它会被重建，车会再动；要车停下，让车急停或切到手动。本入口不处理这种单（`FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT`）。
只有**外来**订单才取消，而且只取消核实过在我们车上的那一张。

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
    action        = 'CLEAR_FAULT'      # 或 RESUME_HELD_ORDER；重建停住之后：REBUILD_STOPPED_ORDER、TERMINATE_STOPPED_TRIP、PREPARE_CARGO_HANDOFF
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
| 200 | 已清除（`Cleared`，看 `disposition`）、已续行（`Resumed`），或这台车的故障早已由人工清除（`AlreadyCleared`）；重建停住之后的三个出口：已交还重建（`RebuildRequested`）、已放弃这趟（`TripTerminated`）、已转交接（`HandoffPrepared`），或同一请求早已办过（`AlreadyDone`） |
| 409 | 拒绝，响应里的 `reasons` 列出全部原因码 |

`AlreadyDone` 的意思是「这台车上一次同样的出口已经办过」，**不是**「你这一次请求办成了」：请求没有编号，服务端分不清是
同一个请求重发，还是另一个人隔了一会儿又点了一次，也不看隔了多久。拿到 `AlreadyDone` 时以看板上旅程的当前状态为准。
放弃这趟的 200 响应里 `terminatedDemandIds` 列出这次终结的需求，`AlreadyDone` 时它是空的。
| 401 / 404 / 422 / 503 | 凭据不对 / 不是本服务端管的车 / 缺 `agvId` 或 `action` 不认识 / 入口没配凭据或服务端这一轮太久没结束 |

## 目前还做不到的，先说清楚

- **故障期间的需求不会自动改派**，清除之后也不改派，留在本车重建（#318）。
- **重建停住之后的出口只在本入口上**（#345），看板上没有按钮：规格第 5.7 节只允许看板做往安全方向的动作，恢复一类走受控入口加审计。
- **车上两条及以上已装需求时，车载端只能交接最后装的那一条**（车载端限制，另开车载端票）。
- **车载端交接入口出厂是关的**（`wireToGate.recoveryResumeEnabled = false`）：不打开，货不在原仓的那一趟没法在现场交接。
- **HANG 不是故障**（#316 的做法 H-a），不经过本入口；纳入故障模型见 #319。
- **故障监看仍然挂在旅程上**（停车证明的采样、升级急停、急停触发的确认、`REQ-0248` 重触发，都由引擎每轮推进这趟旅程时做），
  旅程结束或被释放，这辆车的监看也跟着停；与旅程解耦见 #317。**判断车有没有故障以故障状态为准，不以旅程码为准。**
  - 车载端会话未就绪**不再**挡住它（#358）：真车载端挂着本服务端的在途单时整段路都是未就绪，这期间单报 `FAILED`，
    同一轮就记故障、Hold、急停，旅程码是 `VEHICLE_ORDER_FAILED`，不会被 `ONBOARD_SESSION_NOT_READY` 盖掉；已经急停的车
    会话后来才未就绪，触发的确认与 `REQ-0248` 重触发照常推进。这些都只对 RIoT 读与发，不给车载端发任何东西。
    这是 L1 用例证实的服务端行为；真车载端在「行驶中 `FAILED`」时会话到底就不就绪，见 #358 的记录。
  - 会话行仍是就绪、但车载端失联（6 秒没有任何入站）时，引擎在判到站之前就停下这一轮，这期间单报 `FAILED` 要等车重新
    说话才被看到（#358 已报调度，是否一并处理另定）。
  清除本身不依赖会话是否就绪。
