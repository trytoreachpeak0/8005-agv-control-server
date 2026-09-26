# cs#349 证据：急停人工解除（REQ-0356）与自建单被取消即同车重建（REQ-0360）的交互

基线：`fp/v2-impl@da1eaf8a`（已含 cs#330、cs#339、cs#345）。只加测试，不改产品行为。
用例：`tests/ControlServer.Tests/EmergencyReleaseVersusOwnOrderRebuildTests.cs`，五条确定性 L1：假 RIoT、可拨时钟，引擎、故障协调器、
急停解除入口（`EmergencyStopSupervisor.ReleaseOnConfirmationAsync`）与故障清除入口（`VehicleFaultRecoveryService`）都是真的。

## 结论

- **今天走得到的链上，两种停下条件都没有出现。**解除急停本身不建单，解除后拨过十个延迟（300 秒）仍然不建；车再动的那一刻是人经 #299
  清除故障之后延迟到点。车上有货的那一趟，人取出货物、确认「车上无货」解除之后，重建因仓位读数为空而停住，永远不建开往卸货站的单。
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

### 第 3 格：真车载端会话因本服务端在途单未就绪——`WithTheRealOnboardsSessionNotReadyTheRebuildWaitsForTheSessionAsWellAsTheDelay`

| 步骤 | 会不会重建、何时 | 新单承载的需求 | 护栏 |
| --- | --- | --- | --- |
| 会话未就绪时单 FAILED、车在动，跑三轮 | **不记故障、不急停**（旅程码 `ONBOARD_SESSION_NOT_READY`） | — | — |
| 会话就绪的那一轮 | 记故障、发急停；锁住后会话又未就绪 | — | — |
| 解除、清除（都不看会话） | 记重建 | — | 延迟起算 |
| 清除后 60 秒，会话仍未就绪 | 不建，记录 `ONBOARD_SESSION_NOT_READY` | — | 会话闸门挡住 |
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

### 第 5 格（对照）：别人的急停、没有 REQ-0356——`UnderSomeoneElsesStopAnEmptiedVehicleWhoseOwnOrderIsCancelledIsRebuiltToDeliverAnyway`

| 步骤 | 会不会重建、何时 | 新单承载的需求 | 护栏 |
| --- | --- | --- | --- |
| 装着货去卸货站，别人急停，单 HANG | 不建 | — | — |
| 人试 REQ-0356 解除 | 拒绝 `EMERGENCY_NOT_RAISED_BY_8005`、`EMERGENCY_VEHICLE_ORDER_NOT_FINISHED` | — | — |
| 人在 RIoT 取消本服务端的单、取走货物，车报放货仓 `EMPTY` | 记重建：来源 `CancelledInRiot` | — | 延迟起算 |
| 延迟到点 | 不建，等 `RIOT_EMERGENCY_NOT_OK` | — | 车况护栏挡住 |
| 别人解开急停的下一轮 | **建 1 张 `TO_GATE`**，重建记录不带仓位读数 | **原已装货需求**，而仓是空的 | 取消来源没有 REQ-0362 那一道 |

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

`red/l1-mutations/`：六处临时改动产品代码（每处替换恰好命中一处，跑完按字节还原，`git status -- src` 为空），每处之后只跑本类。
注入前写下的预期与实际：

| 变异 | 拿掉的是什么 | 预期红 | 实际红 |
| --- | --- | --- | --- |
| M1 | 重建延迟 | 第 1 格 | 第 1 格（「C + 29 s 不建」） |
| M2 | 车况护栏里的「故障在效」 | 第 4 格 | 第 4 格（解除后建了单） |
| M3 | 故障清除来源的仓位读数（REQ-0362） | 第 2 格 | 第 2 格 |
| M4 | 重建的会话就绪闸门 | 第 3 格 | 第 3 格 |
| M5 | 解除入口的「有未结束订单即拒」 | 第 4、5 格 | 第 4、5 格 |
| M6 | 给取消来源也加仓位读数（将来可能的修法） | 第 5 格 | 第 5 格，外加第 4 格 |

M6 多出的第 4 格红在等待理由的断言上：加上仓位读数后，重建先等读数、再看车况，等待理由从 `VEHICLE_FAULT_IN_EFFECT,RIOT_EMERGENCY_NOT_OK`
变成 `CARGO_EVIDENCE_NOT_RECEIVED`；同一格「一张单都不建」的断言仍然成立。所以将来那样修，第 5 格要翻、第 4 格要改等待理由。

`summary.txt` 是脚本原样输出；`mutate349.ps1` 是脚本本身。
