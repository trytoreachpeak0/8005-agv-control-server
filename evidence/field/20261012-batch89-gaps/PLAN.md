# 批次 8/9 五个缺口真车补测方案（2026-10-12 17:2x 起草，待调度核准）

方案核准前不动任何东西。下文「读到的」都是按服务端 `origin/fp/v2-impl` `fc2af82b`、车载端 `origin/w2g/fp-v2-impl` `27ecf245e`、工作区 `origin/main` `1416bd2` 实读的代码；「推断」单独标出。

## 零、现状（17:01～17:15 只读）

- RIoT：agv02 在 216，41%，`sysState=IDLE`；agv03 在 214，33%，`sysState=IDLE`（13:42 的 `ERROR` 已消失）；三车在 RIoT 上都没有未结束单。agv01 在地图 25 的 71 号站。
- V2：停服，Manual；定义为 `two-car-example` 原值（闸关、VFR/路网/运行时/清桩全 false）；策略 v1（30/80）；名册空；未恢复暂停 0；未结束周期 0；需求 3 条，未完成 0。
- 持货要用的分区参数在库里：WIRE 分区 `EnRouteAdditionMaxPathCostIncrease=100000`，agv02、agv03 都在 WIRE 分区。
- 操作员名册 `field-operator-roles.json`：1 人，R-11/R-13（只看了结构，没打印工号）。
- 两车车载端栈都停着，IO 都指向模拟器 `127.0.0.1:1502`。
- **电量是个约束**：agv03 只有 33%，比 v1 强制线 30% 高 3 个点；agv02 41%。第 3、4 项要拉闸，第 5 项要跑一趟搬运，都只耗电不充电。见第八节的问题。

## 一、包与定义

- 新分支 `evidence/batch89-gaps-field`，从 `fp/v2-impl` 顶端 `fc2af82b` 切（#655 已合入，REQ-0178 两份定义已在上面，不用再拷）。
- 新增两份定义，都从 `two-car-example` 逐键复制，只改下表列出的键，改完跑 `Assert-ParallelInstanceDefinition` 和含 `AdmissionPolicyVersionGuardTests` 的定向测试：

| 键 | `…batch89-auto.json`（第 3 项，附带第 4、5 项） | `…batch89-manual.json`（第 4 项单做时） |
| --- | --- | --- |
| `routeGraph.enabled` | true | true |
| `vehicleFaultRecovery.enabled` | true | true |
| `journeyRuntime.enabled` | true | true |
| `journeyRuntime.clearanceToWaitingPointEnabled` | true | false |
| `fieldOperatorRoles.onboardClearanceEntryDeclared` | true | true |
| `journeyRuntime.stationDepartureWaitTimeout` | 00:01:30 | 00:01:30 |
| `journeyRuntime.cargoHoldingTimeout` | 00:02:00 | 00:02:00 |

  后两项是用户 10-11 定的模拟器测试时长（离站 1:30、持货 2:00），这次写进定义，不再手改 appsettings；收工用 `two-car-example` 装回 5:00/30:00。**做定义前先把上一次实际运行的生效配置（今天 15:39 那次 on 版装完后的回读）和新定义逐项比对**（REQ-0178 的教训）。
- 出包：`gh workflow run release.yml --ref evidence/batch89-gaps-field -f onboard_commit=<27ecf245e 的 40 位> -f onboard_branch=w2g/fp-v2-impl`。服务端已含 cs#649 与 cs#652（#654）：升级前先写每车凭据，`-Rollback` 会在执行前拒绝，出了问题只能往前修。
- 所有 19、20、06、09、10、16、17、18 号脚本都从本次专用的工作区 worktree（`F:/8005-wt/b89-8005-workspace`，origin/main 最新）运行。

## 二、准备段 P（闸门全程关着，不动车，按规定不用问用户；约 70 分钟）

1. 分支、两份定义、定向测试、推送、出包（约 30 分钟，出包本身约 15 分钟）。
2. 19 号用 `batch89-auto` 定义升级 V2（准入版本覆盖 5）。回读：闸门 False，四个开关和两项时长与定义一致，`CLEARANCE_EXIT_READINESS` 显示车载入口已声明、名册有人。
3. 06 号给 agv02、agv03 部署车载端 `27ecf245e`：`-OnboardPort 58105 -KeepCurrentIo`（ws#28 新加的选项，保留模拟器 IO，不会切到真模块）。06 同时从加密存储写入车上的 `CONTROL_SERVER_OPERATOR_ID`。回读两车 `ioModule` 仍是 `127.0.0.1:1502`。
4. **18 号必须在第 6 步起栈之前跑**：给 agv02、agv03 开恢复窗口（车载 `recoveryResumeEnabled=true` 和证明变量）。18 要求服务端没有车连着，起栈之后再跑会被拒。跑之前先回读两车栈进程为 0，跑完回读 `-Status`。漏了这一步就只能停栈重来。
5. 名册空、策略 v1 保持不动，到第 2 项才改。
6. 第 4 步回读通过之后，才问用户起两车的栈（闸关，车不动）。

## 三、第 1 项：只导入、不批准的策略不生效（不动车；约 15 分钟）

前提：两车车载端栈已起（起栈要问用户），两车 READY。

1. 导入一版强制线 90%、充满线 95% 的策略（这条线比两车电量都高，只要它生效，两车马上都会被判定需要强制充电），**不批准**。
2. 回读 `charging-policy`：`activeVersion` 仍为 1；新版本 `approvals: []`，不在 `activations[]` 里；两车 `vehicles[].policyVersion=1`。
3. 尝试激活新版本：预期 `outcome=REJECTED`、`CHARGING_POLICY_VERSION_NOT_APPROVED`、退出码 1，同时回读确认激活表没有新增行（代码：`ChargingPolicyGovernance.cs:396-411`，`Program.ChargingPolicy.cs:191-208`）。
4. 观察 2 分钟：两车都没有 2205（强制线日志），也没有 2240／2241 分配；日志里出现的策略版本号都是 1。

判据：以上四条全部读到。读不到算失败。依据 REQ-0282：「无已批准版本的车辆不得投运」，新版本只作用于新的判断。

## 四、第 2 项：两车在同一轮里排队，只分给一台（闸关、不动车；约 25 分钟）

代码规则（读到的，`ChargingAllocator.cs:528-555,702-712,796-802`）：同一轮里通过资格判定的车按当前电量从低到高排，电量相同按 vehicleKey 排。先排到的车分到 211，记 2240；后一辆读到 211 已被预占，得到 `CHARGER_RESERVED_OR_OCCUPIED` → `CHARGING_NO_CHARGER_AVAILABLE`，记 2241/2246。没有专门的「同轮排队」事件，所以「同一轮」靠看板 `charging-vehicles` 同一个 pass 里两车都有结论来证明。

1. 前置核对（都读不到就不往下做）：两车 `VehiclePurposeClaims` 为 0 行（定义里没有空闲返回，默认是关的，但上午 agv02 挂过空闲返回用途，所以还是要实读）；两车 READY；人工充电保持 0 行；两车电量都低于将要激活的强制线。
2. 名册写入 211；导入、批准、激活临时策略（强制线 60%、充满线 95%，范围是两车）。
3. 预期：同一轮里电量低的 agv03 分到 211（2240），agv02 得到 `CHARGING_NO_CHARGER_AVAILABLE`，detail 里有 `211=CHARGER_RESERVED_OR_OCCUPIED`（2241/2246）；看板同一个 pass 里两车都有结论。闸门关着，agv03 的周期停在 ALLOCATED、阻塞原因 `CHARGER_CreateDispatchDisabled`，不会建单，车不动。
4. 撤销（顺序不能反，读到的，`JourneyRuntimeEngine.Charging.cs:185-216,459-483`；`ChargingAllocator.cs:396-403`）：
   - 先激活一版强制线 20% 的临时策略，让两车都变成「不需要充电」；
   - 再清空名册，等 agv03 的周期以 `CHARGING_WITHDRAWN_CHARGER_NO_LONGER_ELIGIBLE` 撤回（2254）；
   - 回读：未结束周期 0、用途 0、独占里没有 211、人工充电保持 0。
   - 如果先清名册再降线，车仍低于强制线时会被放上「人工充电保持」（2242）。这个保持只能在车载屏上按「充电后返回服务」解除，会留成残留。只调低强制线也撤不掉，因为周期按分配时冻结的策略判断。
5. 可选（见第八节 Q-A）：不撤销，改为经用户授权开闸，让 agv03 真去 211 充到 60% 左右，顺便解决 agv03 电量低的问题。
6. **第 2 项结束后，把 agv03 排除在派车之外（若 Q-A 选「agv03 只参与第 2 项」）：停掉 agv03 的车载端栈**（10 号 `-SshAlias agv03 -Stop`）。
   - 为什么选停栈，不选挂车辆资格暂停：资格暂停只能由「充电中断已确认」这条路径写入，没有受治理的写入入口，靠手工造它等于改库。再说资格暂停只挡充电分配，挡不住空闲返回、搬运派车和清桩后的常规派车。第 3 项那版「只覆盖 agv02」的临时策略同样只管充电分配，代替不了这一步。停栈之后，V2 读不到 agv03 的车载端事实，所有新用途都会以 `ONBOARD_FACTS_NOT_READY` 拒绝，车不会被派走。三条路径都经过同一个判定 `VehicleDynamicFactsCriterion.Evaluate`（车载端事实为空即拒，`VehicleDynamicFactsCriterion.cs:72-75`），均为实读：搬运派车链 `DispatchAdmissionCriteria.cs:84`；空闲返回 `IdleReturnEvaluator.cs:274` → `VehicleNewPurposeReadiness.JudgeAsync`（`VehicleNewPurposeReadiness.cs:107-127`）；充电分配 `ChargingAllocator.cs:603` → `JudgeForChargingAsync`。在途追加链（`DispatchAdmissionCriteria.cs:130`）换掉了这条判定，但它只作用于身上已有旅程的车，agv03 没有旅程。
   - 回读：agv03 车上的车载端、模拟器进程都为 0；V2 日志里 agv03 的会话断开；分配原因 2241 对 agv03 为 `ONBOARD_FACTS_NOT_READY`；`VehiclePurposeClaims` 里 agv03 为 0 行。这几条都读到之后，才问用户开闸。
   - 第 5、3、4 项开闸期间，监看脚本每轮都读 agv03 的用途行和 RIoT 未结束单，一旦出现就先关闸，再报调度。
   - 收工：不用解除。栈本来就要停，收工时回读 agv03 进程仍为 0 即可。

## 五、第 5 项：持货截屏（要开闸动车；约 30 分钟）

持货屏只会出现在搬运旅程里：车到取货站装满期望篮数后，进入 `CARGO_HOLDING_WAIT`。充电旅程里不会出现，所以**这一项没法和第 3、4 项合并**。我把它排在拉闸之前做：第 3、4 项做完后，211 被暂停，车停在哪也不确定。

做法照 10-11 晚上那次（`evidence/field/20261011-hold-countdown-capture`），只改正那次的失误：

1. 闸关时注入一条 WIRE_TO_GATE 需求（N1-3，最多 8 箱，期望 1 篮，比上次的 4 篮省时间），V2 分给 agv02，停在 `PICKUP_CreateDispatchDisabled`。读出 `ExpectedBasketCount`。
2. 问用户开闸（要问用户）。agv02 开到 N1-3。
3. 到站后用 16 号自动录条码，**紧接着**跑 `cs566-sim-respond.ps1 -Mode Load`，按期望篮数跑够，避免上次那样过了离站期限、屏上同时出现两个倒计时。
4. 进入 `CARGO_HOLDING_WAIT` 后，立刻用 17 号截屏（持货 2 分钟，截屏约 30 秒）。看点：倒计时显示（hmi#310）、期限刷新（cs#633）。
5. 到期 `CARGO_HOLDING_TIMEOUT` 后车去关卡 210，后台挂 `-Mode Unload`；需求 `UNLOADED`（卸货站，cs#637）；车回等待点。关闸。

判据：17 号结果码 `CAPTURED`，截图和 UIA 文本里有持货倒计时；屏上没有已到期的离站倒计时；V2 读到 `CARGO_HOLDING_WAIT` 以及之后的 `CARGO_HOLDING_TIMEOUT`、`UNLOADED`。

## 六、第 3 项（附带第 4 项）：到桩前拉闸，人在 RIoT 结束旧单，车自己出发

### 读到的机制

- 车到 211 但从没充上电时，走「充不上」这条路，不是「中断」（`UnableToChargeFacts.cs:92-161`；`UnableToCharge.cs:202-252`）。需要满足：车在 211、速度 0、订单是 HANG(9)、动作结果码 407802，并且连续两次观测都成立。满足后记 2264，写桩分配暂停（`UNABLE_TO_CHARGE_CONFIRMED`）和清桩记录，车的用途改为 `CLEARING_MAINTENANCE`。**这条路不写车辆资格暂停。**
- 人在 RIoT 结束旧单后，CANCELLED、DELETED、SUCCESS、FAILED、ABSENT 都算终结（HANG 不算），随后每一轮调用 `TryStartClearanceMoveAsync`。**能否自己出发，只看那一刻车是不是稳定停在 211 上**（在线、IDLE、站号 211、速度 0、无任务号、读数新鲜、没在充电）（`ClearanceMove.cs:110-169`；`ChargingCycle.cs:580-588`）：
  - 停在 211 上：2300 承诺等待点 → 建清桩移动单 → 2305 到点，清桩以 `ARRIVED_AT_WAITING_POINT` 完成。第 3 项通过。
  - 停在 212、站号 0、读不到或停在半路：记 2306 `CHARGING_CLEARANCE_VEHICLE_OFF_CHARGER`，不承诺，退回人工确认。这时接着做第 4 项。
- 服务端**不会**把车重新派回 211：清桩中这一支不经过自动重建，充电旅程也不登记重建（`UnableToCharge.cs:291-292`；`OwnOrderRebuild.cs:861-869`）。
- **推断，要现场留意**：如果车停在 211 以外，后来又有人把它挪回 211 并停稳，而清桩开关开着，服务端会在那一轮自动承诺并开走。所以退回人工确认之后、确认完成之前，不要让人手动挪车。
- RIoT 在 212 和 211 之间重试期间，如果旧单又被拉回排队或执行，记 2273，已记的人工确认作废（`UnableToCharge.cs:343-364`）。所以人工确认一定要在旧单终结之后再按。

### 步骤

1. 只用 agv02。停掉 agv03 的栈（它 READY 时也会排进队，而且电量更低，会先抢到 211）。
2. 用户在车到达前给 211 拉闸（要问用户）。拉闸后先只读确认 211 不出电（RIoT 上没有车在 211 充电），这一步只是确认，不作为判据。
3. 名册 211；激活一版只覆盖 agv02、强制线高于它当前电量的临时策略；关闸看分配：agv02 分到 211。
4. 开闸前只读确认 agv01 不在 211/212 附近，也不会去（要问用户）。然后问用户开闸（要问用户）。
5. agv02 开到 211，充不上。等 2264（充不上已确认，桩暂停，清桩记录已开）。RIoT 开始在 212 和 211 之间重试。
6. 我每 5 秒读一次 RIoT 的 agv02 `currentPosition`、速度、procState，**等它读成停在 211 上时**，把旧单号（`W2G-CHARGE-…` 的 RIoT orderId）发给用户，请用户在 RIoT 上结束这张单（要问用户）。
7. 结束后看下一轮：
   - 出现 2300 → 车自己开往等待点 → 2305。判据同 REQ-0178：车到等待点、211 独占 0 行、211 分配暂停仍在、全程不对 agv01 发任何东西，再加上「旧单是人结束的」（RIoT 上旧单状态为 CANCELLED 或 DELETED，V2 读到终结）。
   - 出现 2306 OFF_CHARGER → **立刻关闸**（防止清桩完成后车回到常规派车被派走），然后做第 4 项（下一节）。
   - 都没出现，或者出现 2301（没有合格等待点）、2303（移动单挂起）、2273（旧单又被执行）：关闸，报调度，不自己处理。
8. **停的位置不对怎么判**：以 V2 的判定为准（2300 或 2306）。我另外记录结束旧单前后各三次 RIoT 读数（站号、速度、状态），作为旁证。不对的情形分三种处理：
   - 车停在某个站上（例如 212）、IDLE、速度 0：走第 4 项人工确认，确认里车的最终位置记为该站。
   - 站号 0 或读不到：先关闸，请现场人看车的实际位置并告诉我；车停稳了就走第 4 项，确认时最终位置记 `UNKNOWN`（代码允许）。
   - 车还在动：关闸，等它停，不在车动时确认。

### 第 4 项：车载屏「确认清桩」

按钮出现的条件（读到的，`WireToGateBusinessService.StationClearance.cs:264-302`）：车的用途是 `CLEARING_MAINTENANCE`；`recoveryResumeEnabled=true`；会话 Ready 或 RecoveryRequired；车上有操作员号和证明变量；计划里恰好只有一条 CHARGER 腿。服务端这边要求定义声明了车载入口、名册里有同一个操作员号（R-11/R-13）。

1. 旧单已终结、车不在 211 上（2306）时，用 17 号截屏，证明按钮出现（截图和 UIA 文本里有「确认清桩」）。
2. 请现场人在车载屏上按「确认清桩」（要问用户；屏上按按钮不会让车动）。
3. 判据：V2 记 2266（判定结果为接受），审计里有 `MANUAL_STATION_CLEARANCE_CONFIRMATION`；`StationClearances` 新行 `Proof=MANUAL_CONFIRMATION`、`ConfirmedByRole=R-11` 或 `R-13`、`ConfirmedBy` 有值（入证据前脱敏）、`CompletedAt` 有值；桩的分配暂停仍在。再截一次屏，确认按钮已消失。

**如果第 3 项一次就自动开走了**，第 4 项单独再做一轮：19 号换成 `batch89-manual` 定义（清桩开关 false，约 5 分钟，会重启 V2），用 station-recoveries 解除 211 暂停（211 仍断电，恢复理由如实写「测试中，桩仍断电，为第二轮造场景」），重复第 3 项的 2～6 步。这次清桩开关关着，旧单结束后一定退回人工确认，然后按上面 1～3 做。约 35 分钟。

## 七、收工（约 30 分钟）

1. 关闸。2. 合闸（要问用户）。3. 策略回 v1。4. 解除 211 的分配暂停（`station-recoveries`）；如果有车辆资格暂停也一并解除；回读未恢复暂停 0。5. 名册空。6. 19 号用 `two-car-example` 装回原值（时长回 5:00/30:00），然后在服务器上删掉 `CONTROL_SERVER_V2_FAULT_RECOVERY_CREDENTIAL` 并回读（19 不会自己撤）。7. 18 号 `-Revert` 给两车关恢复窗口，`-Status` 回读 `RESIDUE: none`。8. 停两车栈，回读 IO 指向（只记录）。9. 残留：未恢复暂停 0、未结束周期 0、用途 0、人工充电保持 0、独占只剩等待点占用、需求无未完成。10. V2 停服、Manual。MVP 只读 pid。11. 证据精简、扫凭据和工号，开 PR 进 fp/v2-impl。

## 八、顺序、用时与要先定的事

建议顺序：**P → 1 → 2 → 5 → 3（附带 4）→ 4（如需单做）→ 收工**。

| 段 | 用时 | 动车 | 要问用户 |
| --- | --- | --- | --- |
| P 准备 | 70 分钟 | 否 | 否 |
| 起栈（两车） | 10 分钟 | 否（闸关） | 是 |
| 1 | 15 分钟 | 否 | 否 |
| 2 | 25 分钟 | 否（闸关） | 否；可选的真充要问 |
| 5 | 30 分钟 | 是 | 开闸 |
| 3（+4） | 40 分钟 | 是 | 拉闸、agv01 确认、开闸、RIoT 结束旧单、按确认清桩 |
| 4 单做 | 35 分钟 | 是 | 同上 |
| 收工 | 30 分钟 | 否 | 合闸 |
| 合计 | 约 3.5～4 小时（不含等待现场） | | |

和调度建议（1 → 2 → 3+5 → 4）的不同：第 5 项要走搬运旅程，没法和充电那两项合并，所以单列，放在拉闸之前做。

要先定的事：

- **Q-A agv03 电量**：agv03 只有 33%，第 5 项和第 3、4 项都不给车充电。两个办法：第 2 项观察完之后不撤销，经授权开闸让 agv03 真去 211 充到约 60%（多约 40 分钟）；或者整个过程只用 agv02（agv03 只参与第 2 项，闸关不动），收工前再看 agv03 的电量。我建议后者：第 2 项只需要 agv03 READY 待着，不需要它动。
- **Q-B 时间**：现在 17:2x，全部做完要到 21:00 以后。现场人员能不能盯到那时？如果不能，建议今天做 P、1、2、5（不需要拉闸），明天做 3、4。
- **Q-C 第 4 项谁按**：「确认清桩」由现场人在车载屏上按。工具里没有自动点按钮的脚本，这次也不写。

## 九、急停与异常

- 现场有人盯着，物理急停可按。出现以下任一情况，**先关闸，再报调度**，不自己处理：两车互等、移动单挂起（2303）、选点不对（2301）、旧单又被执行（2273）、车在非预期的位置停住、任何与 agv01 有关的指令。
- 急停后不在 RIoT 手动解锁（V2 会判为外部解除并立刻重新触发急停），由调度和用户决定释放步骤。
- 每次开闸、动车、拉闸、合闸、起栈、在 RIoT 结束旧单、按确认清桩，都单独问用户，授权不跨次。凭据和工号不进证据，也不发到对话里。

## 十、准备段补记（17:4x）

- **第 1 项与 10-11 的关系**：`evidence/field/20261011-W3-policy-unapproved` 已证明「未批准不能激活」和 `activeVersion` 不变，但它自己写明派车侧的对照不干净：那次关闸重启后一条派车判断都没写，「没有充电判断」不能单独当证据。本次补的正是这一点：两车 READY、未批准的 90/95 版本在库里时观察 2 分钟，没有 2205／2240／2241 分配；紧接着第 2 项批准并激活 60/95 之后，同样的两车在下一轮就出现 2205 与分配。后者就是前者的对照：日志在策略生效时确实会写。
- **空闲返回关着**：10-11 的生效配置里空闲返回是手改打开的（`20261011-W2-prep6-two-car/11`：`IdleReturn={"Enabled":true}`），定义里没有这个键，本次按定义装，空闲返回是关的。影响：第 5 项卸完货后车停在关卡 210，不会自己回等待点；第 3 项从那里出发去 211。收工时车停在哪里照实记录。这也省掉了上午「车挂着空闲返回用途不进充电队」那个坑。
