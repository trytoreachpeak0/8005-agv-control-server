# cs#349 证据：急停人工解除（REQ-0356）与自建单被取消即同车重建（REQ-0360）的交互

基线：`fp/v2-impl@da1eaf8a`（已含 cs#330、cs#339、cs#345）。只加测试，不改产品行为。
用例：`tests/ControlServer.Tests/EmergencyReleaseVersusOwnOrderRebuildTests.cs`，五条确定性 L1：假 RIoT、可拨时钟，引擎、故障协调器、
急停解除入口（`EmergencyStopSupervisor.ReleaseOnConfirmationAsync`）与故障清除入口（`VehicleFaultRecoveryService`）都是真的。

## 结论

- **头条：REQ-0356 那条链上两种停下条件都没有出现；急停不是本服务端发的那条链（第 5 格）今天走得到，两种形状在那里都出现了（L1），
  已报调度，是否改由用户决定。**
- REQ-0356 那条链（本服务端的急停，今天走得到的起点只有「单 FAILED → 急停」）：解除急停本身不建单，解除后拨过十个延迟（300 秒）仍然不建；
  车再动的那一刻是人经 #299 清除故障之后延迟到点。车上有货的那一趟，人取出货物、确认「车上无货」解除之后，重建因仓位读数为空而停住，
  永远不建开往卸货站的单。
- 别人的急停那条链（第 5 格）：人在 RIoT 取消本服务端的单、取走货物，重建的延迟在锁着期间就走完了，**解开急停的那一轮就给空车建开往
  卸货站的单**，承载那条已装货需求。取消来源的重建不看仓位读数。
- **票面字面那条链今天服务端自己造不出来**：服务端只在旅程当前那张单 FAILED 时记故障、才可能急停，FAILED 是终态，锁着时它自己的单
  不会还活着。构造起点跑下去的结局是「永远不建单」：清除入口拒绝（`FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT`），重建一直等故障清除。
- 顺带钉住两处今天的行为，已按上真车准入规则报调度：会话未就绪期间单 FAILED 不记故障、不急停（已开成 cs#358，上真车前）；
  取消来源的重建不看仓位读数（调度已交用户决定要不要出变更提案，待用户决定）。

## 每一步之后的三件事

时刻相对各用例里的事件，由可拨时钟给出；延迟前后都断言了钟确实动了。延迟默认 30 秒，护栏三窗口默认 10 分钟。

### 第 1 格：去取货站（车上没货）——`OnTheWayToPickupTheReleaseAloneBuildsNothingAndOnlyTheClearanceRebuildsAfterTheDelay`

| 步骤 | 会不会重建、何时 | 新单承载的需求 | 护栏 |
| --- | --- | --- | --- |
| 单 FAILED、车在动 → 故障、急停；下一轮（+1 s）锁住确认 | 不建，无重建记录 | 无新单 | 不涉及 |
| 人在 RIoT 看这台车：那张单已 FAILED（终态），车上无未结束订单 | 无可取消的 | 无新单 | 不涉及 |
| REQ-0356 解除（接受，202 形态），下一轮读到 OK；再拨 300 秒 | 不建，无重建记录；故障仍在，急停不重触发 | 无新单 | 不涉及 |
| #299 清除（时刻 C） | 记重建：来源 `FaultClearedNothingOnBoard`，`DueAt = C + 30 s` | — | 延迟起算 |
| C + 29 s | 不建 | — | 延迟挡住 |
| C + 30 s | 建 1 张 `TO_PICKUP`，`RebuiltAt = C + 30 s` | 原 `DemandId`、同车、原取货站 | 只出一次问题，护栏三不介入 |

### 第 2 格：去卸货站（装着货），人取出货物——`OnTheWayToTheGateAnEmptiedVehicleIsReleasedButItsDeliveryIsNeverRebuilt`

| 步骤 | 会不会重建、何时 | 新单承载的需求 | 护栏 |
| --- | --- | --- | --- |
| 单 FAILED、急停锁住 | 不建 | 无新单 | 不涉及 |
| 人取出货物，REQ-0356 确认「车上无货」解除：**接受**（解除入口不拿服务端的「已装货」核这句确认） | 不建 | 无新单 | 不涉及 |
| #299 清除 | 记重建：来源 `FaultClearedCargoOnBoard`（清除入口按「旅程里有已装需求或货物绑定」选） | — | 延迟起算 |
| 延迟到点 | 不建，等清除之后的仓位读数（`OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE`） | — | REQ-0362 |
| 车报读数：放货的仓 `EMPTY`；再拨 300 秒 | **永不建**，重建停住（`CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS` / `OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE`） | 需求仍记在本车、仍是 `Loaded`，等 #345 的出口 | REQ-0362 挡住 |

### 第 3 格：真车载端会话未就绪——`WithTheRealOnboardsSessionNotReadyTheRebuildWaitsForTheSessionAsWellAsTheDelay`

分两段。前半段的未就绪是本服务端在途单造成的，**被推迟的是记故障与急停（cs#358）**，不是重建。后半段（锁住之后）的未就绪不是在途单造成的
——那张单已经 FAILED——是借同一形状取的、对重建更不利的假设，真车上没核实过。

| 步骤 | 会不会重建、何时 | 新单承载的需求 | 护栏 |
| --- | --- | --- | --- |
| 会话因在途单未就绪时单 FAILED、车在动，跑三轮 | **不记故障、不急停**（旅程码 `ONBOARD_SESSION_NOT_READY`；cs#358） | — | — |
| 会话就绪的那一轮 | 记故障、发急停；锁住后假设会话又未就绪 | — | — |
| 解除、清除（都不看会话） | 记重建 | — | 延迟起算 |
| 清除后 60 秒，会话仍未就绪 | 不建，记录 `ONBOARD_SESSION_NOT_READY`（两道挡：会话闸门、车况护栏的 `ONBOARD_FACTS_NOT_READY`） | — | 会话闸门挡住 |
| 会话就绪的下一轮 | 建 1 张 `TO_PICKUP`，`RebuiltAt` 就是这一轮 | 原 `DemandId`、同车、原取货站 | 时机 = max(延迟到点, 会话就绪) |

### 第 4 格（构造）：锁着时本服务端的单 HANG，人在 RIoT 取消它——`ConstructedALatchOverALiveOwnOrderThatAPersonCancelsInRiotNeverRebuildsBecauseTheFaultCannotBeCleared`

起点用故障协调器的公开入口、以一个今天没人传的症状码构造，模拟将来 #319（把 HANG 纳入故障模型）或 REQ-0249 两条人工急停来源有了入口之后的样子。

| 步骤 | 会不会重建、何时 | 新单承载的需求 | 护栏 |
| --- | --- | --- | --- |
| 单 HANG，故障、急停锁住 | 不建 | 无新单 | — |
| 人试 REQ-0356 解除 | 拒绝 `EMERGENCY_VEHICLE_ORDER_NOT_FINISHED`；**不发任何订单命令** | — | — |
| 人在 RIoT 取消这张单（时刻 X） | 记重建：来源 `CancelledInRiot`，`DueAt = X + 30 s` | — | 延迟起算 |
| X + 30 s | 不建，等 `VEHICLE_FAULT_IN_EFFECT,RIOT_EMERGENCY_NOT_OK` | — | 车况护栏挡住 |
| 人再解除：接受 | 不建，等 `VEHICLE_FAULT_IN_EFFECT` | — | 车况护栏挡住 |
| 人去 #299 清除 | 拒绝 `FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT`；再拨 1 小时仍不建 | 无新单 | 出不去的环 |

### 第 5 格：别人的急停、没有 REQ-0356，今天走得到——`UnderSomeoneElsesStopAnEmptiedVehicleWhoseOwnOrderIsCancelledIsRebuiltToDeliverAnyway`

| 步骤 | 会不会重建、何时 | 新单承载的需求 | 护栏 |
| --- | --- | --- | --- |
| 装着货去卸货站，别人急停，单 HANG | 不建 | — | — |
| 人试 REQ-0356 解除 | 拒绝 `EMERGENCY_NOT_RAISED_BY_8005`、`EMERGENCY_VEHICLE_ORDER_NOT_FINISHED` | — | — |
| 人在 RIoT 取消本服务端的单（读到取消的时刻 X） | 记重建：来源 `CancelledInRiot`，`RecordedAt = X`，`DueAt = X + 30 s` | — | 延迟起算 |
| X + 1 s：人取走货物，车报放货仓 `EMPTY`（读数严格晚于记录，将来加仓位证明时会被认） | 不建 | — | — |
| 延迟到点 | 不建，等 `RIOT_EMERGENCY_NOT_OK` | — | 车况护栏挡住 |
| 别人解开急停的那一轮（`DueAt` 早已过） | **建 1 张 `TO_GATE`**，`RebuiltAt` 就是这一轮，重建记录不带仓位读数 | **原已装货需求**，而仓是空的 | 延迟锁着期间已走完，没有缓冲；取消来源没有 REQ-0362 那一道 |

这一格不需要有人违反现场说明：说明禁止的是「为了让车停下」取消服务端的单；别人的急停按说明转 RIoT 人员，他们为清场取消不一定违反它，
而 REQ-0356 条文本身也写着「须先取消该订单」。

交用户的两种读法：按 REQ-0360 字面（「继续承载原订单中尚未终止的 DemandId」「不等待人员确认」），今天的行为符合条文；但 REQ-0360
同时要求重建「须按 REQ-0239 重新通过正常派车和安全门禁」，REQ-0239 写的是原订单明确终结、相关阻断收敛后才可重建，订单结果未知、
身份/位置冲突时继续载货保全——按这个读法，车报仓空而服务端记着已装货，是该保全而不是重建的冲突。

## 本类没覆盖的格子

| 格子 | 为什么没单写 |
| --- | --- |
| 别人的急停 + 去取货站（车上没货） | 同样在解开急停那一轮重建，只有「解开即派走」一种形状，机理与第 5 格相同；没有货，不涉及「送不存在的货」 |
| 别人的急停 + 会话未就绪 | 重建多一道会话闸门，时机变成「解开急停」与「会话就绪」取晚，机理同第 3 格后半 |
| 本服务端急停 + 货没取走 | REQ-0356 要求确认「车上无货」，急停现场说明写明「车上有货不能走这条路」；清除后按 `OCCUPIED` 读数重建去卸货站，由现有 `VehicleFaultRecoveryTests.AClearedFaultWithCargoOnBoardRebuildsTheOrderToDeliverIt` 覆盖（那条不经急停） |
| 本服务端急停 + 货已取走 + 会话未就绪 | 读数要在清除之后收到，会话不就绪就一直等读数，由现有 `VehicleFaultRecoveryTests.Req0362ALoadedClearanceWhoseSessionNeverBecomesReadyWaitsForTheSnapshot` 覆盖 |
| 真车与真车载端 | 本票只做 L1；锁住后真车载端会话是否未就绪、FAILED 后车在动时会话是否一直不就绪，都没在真车上核实过 |

## 两个问题

1. **「先取消该订单」由谁取消？**急停锁着时服务端自己不会取消这辆车上本服务端的单：解除入口只拒绝不取消；释放服务在车有未清除故障时
   明确不取消（`DemandReleaseService`，`RELEASE_FAULT_SUPERVISION_IN_EFFECT`）；外来订单监看只取消外来单（`ForeignRunningOrderSupervisor`）。
   所以走得通的只有人在 RIoT 里手动取消，那算「8005 没发过取消」，按 REQ-0360 触发重建（第 4、5 格）。现场说明两份都教「服务端建的单不要取消，
   找值班工程师；只取消核实过在我们车上的外来单」。今天走得到的链上，锁住时那张单已经 FAILED，没有要取消的（第 1 格）。
2. **解除入口有没有顺带处理？**没有。`ReleaseOnConfirmationAsync` 读 RIoT 这辆车有没有未结束订单，有就拒绝
   （`EMERGENCY_VEHICLE_ORDER_NOT_FINISHED`），读不到也拒绝；它不发任何订单命令（第 4 格断言订单命令列表不变），所以也不存在
   「取消算不算 8005 发过」的问题——服务端认「自己取消过」只看命令审计里有没有这张单的 `CANCEL_ORDER`。

## 第 4 格为什么今天走不到，走到了又会怎样

判断：**今天现场走不到，所以不属于上真车准入规则第 (3) 条「现场会卡死、只能改库」；将来有了别的急停来源就走得到，那时属于第 (3) 条。**

它的前提是「本服务端的急停锁着，而这辆车上还有本服务端的单没结束」。读码（`fp/v2-impl@da1eaf8a`）：

1. 发急停只有一处：`VehicleFaultCoordinator.EscalateAsync`（`VehicleFaultCoordinator.cs:733`，`EmergencyStopSupervisor.RequestStopAsync` 在产品里唯一的调用方），只对在效的故障升级。
2. 记故障在产品里只有一处：引擎看到旅程当前那张单在 RIoT 是终态 FAILED（`JourneyRuntimeEngine.cs:1516`，`ObserveOrderFailureAsync`）。`ConfirmIsolationAsync` 没有产品调用方；REQ-0249 的两条人工急停来源没有入口。
3. 故障在效期间，本服务端不会给这辆车再建单：派车被故障挡住；重建的车况护栏看 `VEHICLE_FAULT_IN_EFFECT`；旅程停在那张 FAILED 单上，不往下一段走；释放服务在有故障时不取消也不建。

所以本服务端的急停锁着时，这辆车上本服务端的单只有那张 FAILED 的，而 FAILED 是终态，没有可取消的。这里只有一环是推的：RIoT 的 FAILED 不会再变回执行或 HANG（行为实验室没有这种观测，故障清除现场说明也记为「按 RIoT 状态模型推」）。

走得到的那一天：#319（把 HANG 纳入故障模型）落地，或者 REQ-0249 的人工急停有了入口，或者任何改动让故障由「单 FAILED」以外的症状记下。那时这一格就是第 (3) 条：清除入口拒绝（`FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT`），#345 的出口只接停住的重建，续行要 PAUSED 的单，释放服务在有故障时不动，除了改库没有出口。cs#358 的修法若只是让会话未就绪时也记 FAILED，不改变这个判断。

## 临时探针（cs#358 引用）

`probe/Probe349.cs.txt` 是发现「会话未就绪期间单 FAILED 不记故障、不急停」时跑的临时探针，只作证据，不在任何工程里；
`probe/probe-output.txt` 是它在 `da1eaf8a` 上的输出。提交前拷回测试工程重跑过一次，输出逐行相同。同一行为由第 3 格的用例钉住，
已开成 cs#358（上真车前）。

## 反向验证

`red/l1-mutations/`：六处临时改动产品代码（每处替换恰好命中一处，`--no-incremental` 重编，跑完按字节还原，`git status -- src` 为空，
还原后重编 0 错误），每处之后只跑本类。表里是审查修改之后那一轮（`summary.txt`）；注入前写下的预期与实际：

| 变异 | 拿掉的是什么 | 预期红 | 实际红 | 红在哪条断言 |
| --- | --- | --- | --- | --- |
| M1 | 重建延迟 | 第 1 格 | 第 1 格 | 清除后 29 秒「单还是 1 张」（实际 2 张） |
| M2 | 车况护栏里的「故障在效」 | 第 4 格 | 第 4 格 | 解除**之前**的等待理由应含 `VEHICLE_FAULT_IN_EFFECT`（实际只有 `RIOT_EMERGENCY_NOT_OK`）。按构造，解除之后会建单，但用例停在第一条红上，没观测到 |
| M3 | 故障清除来源的仓位读数（REQ-0362） | 第 2 格 | 第 2 格 | 延迟过后「开往卸货站的单还是原数」（实际多 1 张） |
| M4 | 重建的会话就绪闸门 | 第 3 格 | 第 3 格 | 等待理由应为 `ONBOARD_SESSION_NOT_READY`（实际 `ONBOARD_FACTS_NOT_READY`）：「不建」有第二道挡，拿掉闸门仍然不建 |
| M5 | 解除入口的「有未结束订单即拒」 | 第 4、5 格 | 第 4、5 格 | 第 4 格第一次解除应被拒（实际发出）；第 5 格拒绝理由少了 `EMERGENCY_VEHICLE_ORDER_NOT_FINISHED` |
| M6 | 给取消来源也加仓位读数（将来可能的一种修法） | 第 4、5 格 | 第 4、5 格 | 都在等待理由上：第 4 格变成 `CARGO_EVIDENCE_NOT_RECEIVED`（没有装货，没有读数）；第 5 格变成 `SLOT_…:EMPTY…`，即读到空仓后停住 |

M6 只是一种修法的样子：将来怎么改由用户定，第 4、5 格要跟着修法改哪条断言，随修法而定。

第一轮（`summary-9dacdd0a.txt`，测试文件 `9dacdd0a`、上一版脚本，只记了红在第几行）与这一轮的差别：M6 下第 5 格那时红在「没收到读数」，
因为读数与重建记录同一时刻、而 `CargoEvidenceAsync` 只认严格更晚的读数；审查指出后读数前拨了一秒，这一轮红在「读到空仓」上。
第一轮的证据说明曾把 M2 写成「解除后建了单」，那是推断、没观测到，这一版已改。

`summary.txt`、`summary-9dacdd0a.txt` 是两轮的原样输出；`mutate349.ps1` 是这一轮的脚本（上一版在 git 历史里）。
