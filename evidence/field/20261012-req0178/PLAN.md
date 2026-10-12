# REQ-0178 清桩后自动开往等待点（cs#409）真车验证方案

2026-10-12 下午写，调度 C10 转达用户「现在就做」。写这份方案时没有动任何东西。判据先提交，后出结果。
每一次动车（开清桩开关、开闸、拉闸、合闸）都在工作会话窗口里单独向用户要授权，现场有人，手放在物理急停边上。

## 一、包与提交（13:3x 实读）

| 组件 | 提交 | 依据 |
| --- | --- | --- |
| 服务端 | `a24e8f766dcdb6c9a374a69f2a0ff5110cfcd4db` | `git rev-parse origin/fp/v2-impl`。含 cs#638（`2138f7b9`，`merge-base --is-ancestor` 已核）、cs#633（`8f866374`）、hmi#312 配套（`09fbd219`、`51916853`、`ceb7804e`）、#644 证据 |
| 车载端 | `b2f602ecb107b972319106060593ad146a07c8f2` | `origin/w2g/fp-v2-impl` 顶端，含 hmi#310、#312、#313 |
| 模拟器 | 不重装，用车上现有的 | 照旧 |

**出包**：走 CI `release.yml`（`workflow_dispatch`，`--ref fp/v2-impl`，`onboard_commit=b2f602ec…`，`onboard_branch=w2g/fp-v2-impl`，跑在 vm01 的 headless runner 上），不占本机。本机重测试时段在 hmi#323 手里，所以不在本机出包。13:4x 查过 control-server 的 CI，最近 8 个 run 都已结束；dispatch 之前我再查一次 vm01 是否空闲。包用 `gh run download` 下载，并核对 sha256。

## 二、部署（闸门全程关闭，不用问用户）

1. **服务端**：用 `19-deploy-control-server-parallel.ps1`（workspace `origin/main`，含 ws#24 的摘要显示）装新包。定义从 `installed-instance.json` 复制一份新的，只改这几项：
   - `journeyRuntime.enabled = true`
   - `vehicleFaultRecovery.enabled = true`。打开后安装器从控制端 DPAPI 写入 `CONTROL_SERVER_V2_FAULT_RECOVERY_CREDENTIAL`；隔离要求 Host 恢复入口可用，否则只写 2278 告警。
   - `journeyRuntime.clearanceToWaitingPointEnabled = false`，**第一次安装时关着**。
   - 其余照旧，包括两车定义、准入版本 5、等待时长 5:00/30:00。
   库不换，沿用 newdb5（新包启动时自动迁移），所以不用清车载端日志簿。19 号脚本的摘要和 `EFFECTIVE_CONFIGURATION` 回读要原样存进证据。
2. **车载端**：只装 agv02，`06-deploy-onboard-hmi.ps1 -SshAlias agv02`（必须写明 agv02，脚本默认是 agv01，hook 也会拦）。hmi#312 把技术日志挪到了 `%LOCALAPPDATA%\SQCD\8005AGV\logs`，部署后核对实际写入的目录，我读车载日志的小脚本也改到这个路径。agv03 不装、不起栈，这次不用它。
3. 起栈前读两车电量与位置，13:42 的读数是：agv02 在 216，67%，`MT_FINISHED`；agv03 位置 0，68%，`sys=ERROR`，可能有人在用；agv01 在 80，66%，没有单。**用 agv02**。

## 三、实读的机制（服务端 `a24e8f76`）

- 开关 `JourneyRuntime:ClearanceToWaitingPointEnabled` 默认关；在 v2 实例上，只能经实例定义 `journeyRuntime.clearanceToWaitingPointEnabled` 打开（cs#638，`scripts/parallel/README.md:53`）。改它要重跑 19 号，Host 在 `EFFECTIVE_CONFIGURATION` 里记下实际绑定的值，回读不一致就停服务。
- 承诺等待点的位置：`JourneyRuntimeEngine.UnableToCharge.cs:386-399`。清桩中、旧单已终结、还没人确认时，才调 `TryStartClearanceMoveAsync`（`ClearanceMove.cs`）。它的前置条件（`ClearanceMove.cs:110-169`）：
  - 开关开着。
  - 这趟清桩没有被人结束过一次移动。被结束过就记 `CLEARANCE_MOVE_ENDED`，之后不再自己出发，只能人工清桩。
  - 上次撤回之后已经过了冷却期。
  - **车还停在这个桩上**。否则记 `CLEARANCE_VEHICLE_OFF_CHARGER`。所以这次**不能先让人把车挪走**。
  - 出发门通过。
  - 选得到合格的等待点。选不到就记 2301 `CLEARANCE_NO_WAITING_POINT`，不猜别的站。
- 事件：2300 承诺；2302 撤回；2303 移动被挂起；2304 有人结束了移动；2305 到点、清桩完成；2306 本轮不出发。
- **到点之后**（2305 原文）：清桩完成，桩的独占与车的用途释放，等待点转为占用；**桩的分配暂停保留**（"The charger's allocation hold stays"）。所以判据里「211 的保持状态被释放」要改成「211 的独占释放、分配暂停仍在，待人工恢复」。
- 建单走 `movementDispatch.ReconcileOrCreateAsync`，与空闲返回是同一条建单路径。**推测**：闸门关着时它同样建不了单（昨晚闸门关时空闲返回报 `WAITING_POINT_CreateDispatchDisabled`，实测过）。清桩移动在闸门关时的行为，要在 B 段开闸前读一次日志，看到对应的 `…CreateDispatchDisabled` 才算证实。

## 四、造清桩场景：建议走「充电中拉闸」（G4 那一路）

上午 G4 已经实测：车在桩上一显示 `CHARGING`，那张充电单在 RIoT 上就结束了。拉闸后约 74 秒记 2276，并在同一次保存里转入清桩中。这时旧单已经终结，车还停在 211，正好满足承诺等待点的全部前提，**不需要用户在 RIoT 手动结束旧单**。

另一条路是「到桩前拉闸」（G1 那一路）：RIoT 子动作会失败重试 5 次（212→211），旧单 HANG，记 2264，要人在 RIoT 结束旧单。我**不建议**这条路，原因有两个：重试过程中车在 212 和 211 之间来回走，最后停在哪里不确定；如果不在 211 上，就是 `CLEARANCE_VEHICLE_OFF_CHARGER`，测不下去。调度如果坚持要验证「人结束旧单 → 车自己出发」这一环，就走这条路，并接受这个风险。

**临时策略**：强制线 75、完成线 95，余量 20、每趟 10，稳定期、窗口、增量与 v1 相同。约束 95 > 75 ≥ 20。
- agv02 现在 67% < 75，会被派去充电。agv03 68% 也低于 75，但它的车载端不起，按 `ONBOARD_FACTS_NOT_READY` 被拒（昨晚与今早都实测过）。
- 回差：上午的疏漏是「充满离桩后又掉回强制线以下，再次进队」。这次车不会充满，拉闸后中断确认会同时写车辆资格暂停，暂停期间这台车不进充电队（`VEHICLE_ELIGIBILITY_HELD`）。所以不会给这台车重建充电单，这一条也正是判据之一。
- 完成线 95 远高于拉闸时的电量（约 68～70%），不会在拉闸前充满。
- 名册只登记 211。等待时长不改：这次没有搬运，用不到 1:30/2:00。

## 五、分段与判据（读不到就算失败）

### A 段：部署（不动车）

- 19 号摘要显示 `clearanceToWaitingPointEnabled=false`，`EFFECTIVE_CONFIGURATION` 回读 PASS。
- V2 启动后没有 2272（出口不可用）。
- agv02 新车载端就绪；版本日志显示 `b2f602ec`；日志目录核对完毕。

### B 段：打开清桩开关（**要用户授权**）

- 闸门关着时，用同一个包、改一项定义重跑 19 号：`clearanceToWaitingPointEnabled=true`。
- 判据：19 号摘要和 `EFFECTIVE_CONFIGURATION` 回读都是 true。

### C 段：开窗、进桩（**要用户授权：开闸**）

- 开窗前只读 RIoT，确认 agv01 不在 211、身上没有单，并请用户确认 agv01 不会去 211、也不会经过 211 到等待点那一段。导入并激活 75/95，名册登记 211。
- 闸门关着时先看：agv02 记 2205、2240，预约 211；agv03 记 `ONBOARD_FACTS_NOT_READY`。
- 开闸后：2252 建单（39 字符），2257、2258 到桩并开始充电。

### D 段：拉闸（**要用户授权**）

- 见到 `CHARGING` 约 1 分钟后，请现场拉闸。
- 判据：2276 `INTERRUPTION_CONFIRMED`；桩暂停与车辆资格暂停各一行；周期 `CLEARING`。
- 下一轮应出现 2300（承诺某个等待点，记下是哪个点、路程多少）。如果是 2301、2306 或 `CLEARANCE_VEHICLE_OFF_CHARGER`，照实记录、关闸、报调度。

### E 段：车自己开到等待点（同一次开闸授权内，开始前在窗口提醒现场）

- 判据：
  - RIoT 上出现这辆车开往该等待点的移动单（单号与长度记下）。
  - 到点后记 2305。
  - 周期结束原因与清桩记录 `CompletedAt` 照实记录。
  - `StationExclusivities` 里 211 为 0 行，等待点有 agv02 的 OCCUPIED。
  - 211 的分配暂停仍在（未恢复）。
  - 车辆资格暂停仍在，之后每轮 agv02 记 `CHARGING_VEHICLE_ELIGIBILITY_HELD`，不再给它建充电单：到点后观察 5 分钟，RIoT 上没有 agv02 的新 W2G 单。
  - RIoT 上整段没有任何单或命令提到 agv01：每次读 `nonfinal orders mentioning agv01`，并在服务端日志里搜 agv01 的 deviceKey。
  - 211 独占每一轮都至多一行；没有 2303、2304。
- **要用户在 RIoT 上操作的只有一种情况**：车开往等待点途中卡住（2303），这时由用户决定。我给出单号，用户在 RIoT 上处理；按 2304 的规则，被人结束之后，这趟清桩只能人工清桩收尾。

### F 段：合闸与恢复（**要用户授权：合闸**）

- 现场合闸后，用 Host 入口做桩恢复和车辆资格恢复（`station-recoveries`、`vehicle-eligibility-recoveries`），先把策略换回 v1，再恢复，避免恢复后马上又进队。回读未恢复暂停为 0。

## 六、风险、怎么停、急停后怎么释放

- 车会在两个时刻自己动：开闸后去 211，以及清桩后去等待点。第二段没有人工触发，开关打开时就决定了。开始前要在窗口里提醒现场。
- 要停：关闸。但 20 号脚本在有在途旅程时会拒绝关闸，上午实测过。所以真要立刻停，靠现场物理急停，或者用户在 RIoT 取消那张移动单。取消后按 2304 不再重建，这趟清桩转人工。
- 急停后：先不要在 RIoT 解锁。V2 运行中，外部解除会被判为外部解除并重新急停。我先关闸（能关的话）、查原因，再和用户商量释放的办法。
- 出现异常（两车互等、移动单挂起、选点不对、出现任何与 agv01 相关的指令），一律先关闸再报调度。
- 不碰 agv01、MVP、生产 MesIngest。不走车载屏的「确认清桩」入口（ws#25 未合入），也不在车上手工配凭据。

## 七、收工清单

- 关闸。
- 定义里 `clearanceToWaitingPointEnabled` 和 `vehicleFaultRecovery.enabled` 改回 false、`journeyRuntime.enabled` 改回原值，然后重跑 19 号，回读确认 false、注册表凭据变量已撤。
- 策略 v1，名册空。
- 残留：未恢复暂停 0、211 独占 0、未结束周期 0。
- agv02 栈停。
- V2 停服，启动类型 Manual。
- MVP 不碰，只读核对 pid。
- 证据走新分支，另开 PR。

## 八、时长

出包约 15～20 分钟（CI），部署约 15 分钟，B～E 段约 20 分钟，F 段与收工约 20 分钟，合计约 1～1.5 小时。

---

## 核准与补充（调度 C10，2026-10-12 13:5x）

- (a) 走「充电中拉闸」。**本次不测**「人在 RIoT 结束旧单之后，车自己出发」这一环。原因：那条路（到桩前拉闸）要靠 RIoT 重试，车最后停在 212 还是 211 说不准，而承诺等待点要求车还停在桩上（`ClearanceMove.cs:110-169`）；充电中拉闸时旧单本来就已经终结。
- (b) 判据改为「211 独占 0 行、分配暂停仍在」，依据是 2305 的原文。
- 「闸门关着时清桩移动建不了单」目前是**推测**。开闸前必须读到证据，并写进本文件；读不到就报调度，不开闸。
- 收工时把 `vehicleFaultRecovery.enabled` 改回 false，并回读确认凭据变量已删。
- agv03（13:42 读到 `sys=ERROR`、pos 0）这次不用，也不碰，只把读数记下来。
- 出包：dispatch 前查过，两仓都没有在跑或排队的 run，control-server 三台 runner 都 online、busy=false。release run `38209404692`（headSha `a24e8f76…`，onboard `b2f602ec`）。
- 逐次向用户要授权：起 agv02 栈、打开开关、开闸、拉闸、合闸。

---

## 恢复步骤（调度 C10，2026-10-12 14:2x：用户要求尽快恢复）

**部署时用的是未合入但已审过的安装脚本**：cs#649 的 PR 一通过独立审查，就把它的分支 merge 进 `evidence/req0178-field`（用 merge，不 rebase，不 force push），从合并后的 HEAD 出包、部署，和它的 CI 并行，不等它合入。部署对象只是 v2 测试实例，闸门关着，MVP 不碰。这个代价调度已认可。

1. `git merge <cs#649 分支>` 进本分支，推送。合并后的 HEAD 记作 R。
2. dispatch 前查 vm01 与两仓的 CI 是否空闲，然后在本分支上跑 `release.yml`（onboard `b2f602ec`）。
3. 下载包，核对 sha256，并核对 manifest 的 `controlServer.commit` = R、onboard = `b2f602ec`。19 号用的 clone 是一个 detached 在 R 的 worktree。
4. **全新安装**：V2 现在是未安装状态。19 `-PackagePath`，定义用 `req0178-field-off.json`，带 `-AdmissionPolicyVersionOverride 5`。这一步顺带验证 cs#649 对「首次就绪即 ready 时读不到 reason」的修复。回读：19 号摘要与 `EFFECTIVE_CONFIGURATION` 中 `clearanceToWaitingPointEnabled=false`；两车按车凭据的安全投影探针都返回 200；旧库数据还在（独占、暂停表的行数与收工时一致）。
5. 部署 agv02 车载端 `b2f602ec`（06 号，`-SshAlias agv02 -OnboardPort 58105 -HealthPort 58107`），核对 hmi#312 的新日志目录。
6. **打开开关（要用户授权）**：同一个包，定义改用 `req0178-field-on.json`，19 号走**升级路径**。这一步顺带验证 cs#649 对升级路径 401 的修复。回读：摘要与生效配置都是 `clearanceToWaitingPointEnabled=true`。
7. 之后按本文件第五节 C～F 段执行，每段都单独授权。

## 上午 F 段之后的状态（实读）

- 211 的合闸与桩恢复：10:12 合闸（用户报）；10:12:07 `station-recoveries` 返回 200 `RECOVERED`；10:12:09 `vehicle-eligibility-recoveries` 返回 200 `RECOVERED`（`20261012-cs566c-g4/34`～`36`）。合闸之后 211 出电正常：D 段 agv03 在 10:16:55～10:46:43 从 29% 充到 80%，争用段两车在 11:13～11:58 也都正常充电（`20261012-cs566c-contention/12`、`16`）。此后没有再拉过闸。
- 库（14:2x 回读，`10-state-after-stop.txt`）：未恢复的桩暂停 0、车辆资格暂停 0、未结束周期 0；独占 2 行，是 215/216 的等待点占用。
- RIoT（14:21:15 只读，`11-riot-3cars-resume-prep.txt`）：
  - agv02 在 **216**，不在 211；61%，`NO_CHARGE`，`sys=IDLE`，`MT_FINISHED`，无未结束单。
  - agv03：pos 0，60%，无未结束单，不用也不碰。
  - agv01：25 号图 pos 210，60%，无未结束单。
- 阈值：agv02 61% < 强制线 75，完成线 95 不变；agv03 不起栈，不会被派单。

## 应急补充（cs#649 审查提醒，调度 C10 15:0x 转达）

- cs#649 审查通过，没有 M 级问题，不阻碍今天部署到 v2 测试实例。
- 装上这一版（`d0687b29`）以后，对两车实例执行 19 号 `-Rollback` **一定会因 401 失败并自动回滚**：运行时保持关闭，属于安全方向的失败。真要退回旧版，只能 `19 -Uninstall`（保留数据，不加 `-RemoveData`），再全新安装。
- （调度 C10 补充，依据 cs#649 实读）今天装上 `d0687b29` 之后，两车实例**没有可靠的办法退回旧版**：`-Rollback` 必然 401；如果「`-Uninstall` 保留数据，再全新安装旧包」，跑的是旧包里带 reason 缺陷的 Install，保留的库第一次就绪检查就是 ready，照样回滚，也就是 `09` 那一幕。所以出了问题只能往前修，或者换空数据目录（那就不是保留数据了）。这不影响今天的步骤：出现异常时，照旧先关闸、停在安全状态，再报调度。

## 恢复后的部署结果（15:07～15:10）

- release `38214156334`：manifest 中 controlServer=`d0687b29`，onboard=`b2f602ec`，sha256 `4b67fdbc…`（`12`）。
- 19 号全新安装 off 版：PASS，没有再出现 reason 缺陷。19 号摘要与 Host 回读（Held/Released 两次）都是 `clearanceToWaitingPointEnabled=False`；`CLEARANCE_EXIT_READINESS exit=available`（`13`）。
- 按车凭据的安全投影探针：RIOT59（agv02）、RIOT60（agv03）都是 HTTP 200，`motionState=STOPPED`（`14`）。
- 旧库：未恢复暂停 0、未结束周期 0、周期 8 行、等待点 3 个。独占从 2 行变为 1 行：15:09:12 V2 记 2210，因为 agv03 已不在 215（离开证据），按设计释放了 215 的占用；216 仍是 agv02 的 OCCUPIED。
- 闸门 false；等待 5:00/30:00（off 版定义的出厂值，本次不需要改）；MVP pid 10912 未碰。

## 开闸前证据：「闸门关着时清桩移动建不了单」（调度要求，15:2x）

**读到的**（`d0687b29`）：
- 清桩移动建单调用的是 `movementDispatch.ReconcileOrCreateAsync(move.UpperId)`（`JourneyRuntimeEngine.ClearanceMove.cs:309`）。它的实现在 `ControlServer.Application/WireToGateOrchestration.cs:274`，和空闲返回、充电单走的是同一个方法。
- 这个方法里真正向 RIoT 建单的只有 `CreateAfterConfirmedAbsenceAsync`（4 处调用）和 `CreateAfterExperimentalAbsenceAsync`（1 处调用）。两者一进来先判断 `if (!createDispatchPolicy.CreateEnabled) return CreateDispatchDisabled(intent);`（`:463`、`:522`），闸门关着就直接返回 `MovementDispatchOutcome.CreateDispatchDisabled`，不调 RIoT。
- 清桩移动收到这个结果后，按「不是 Confirmed、也不是 TerminalReconciliationRequired」处理：把码写成 `ClearanceMoveNotConfirmed`，然后返回，下一轮再试（`ClearanceMove.cs:318-321`）。

**同一路径的运行实测**：今天 11:00 闸门关着时，agv02 的空闲返回经同一个方法被挡下，日志 2163 写的是 `AwaitingPickupArrival (reason WAITING_POINT_CreateDispatchDisabled)`（`20261012-cs566c-contention`）。昨晚 21:00 也是同样情形（`20261011-W3-low-battery/04`）。

**没测到**：清桩移动自身在闸门关着时的运行表现。要先造出清桩才能看到，而造清桩要开闸让车进桩。

## 开关打开（15:22～15:24）

- 19 号用 on 版定义走**升级路径**：PASS。cs#649 的 401 修复生效：升级自检读到两车各自的按车凭据，"Vehicle-safety projection credentials loaded for … (values not shown)"。
- 19 号摘要、Host 回读两次都是 `clearanceToWaitingPointEnabled=True`（`21`）。

## 收尾清单补充

- 收工时回读 agv02（以及动过的车）的 `ioModule` 指向，并记录下来。06 号部署会按 site 配置写成真模块，本次已在起栈前用 09 号 `-Simulator` 切回 `127.0.0.1:1502`（`17`）。

## 漏项记录：routeGraph（15:28）

开窗、激活 75/95 后关闸看分配，agv02 被 `CHARGING_ROUTE_GRAPH_UNAVAILABLE`（`ROUTE_GRAPH_DISABLED_OR_OTHER_MAP`）拒绝（`25`）。两份测试定义从 `two-car-example` 复制时，我只比对了 runtime、VFR、clearance 这三项，漏掉了 `routeGraph.enabled`。昨天和今天上午能跑，是因为 cfg2 手改时一并把它打开了（`20261011-W2-prep6-two-car/11` 回读 `RG.enabled=True`），而那些手改项没有清单。修正：两份定义同时打开路网图（`020138c9`，证据 `26`、`27`），重新出包 `38218402243`，闸门关着用 on 版走升级路径重装。重装会重启 V2：之后先确认 agv02 重连回到 READY，回读名册、窗口、策略 v5 仍然生效，再关着闸看分配。

**教训**：做测试定义时，要和**上一次实际运行时的生效配置**逐项比对，不能只和示例文件比；手改过的项要列清单。

## 收工补记（16:05）

- 第一次跑 19 号恢复原值时，我从工作区主目录 `8005-workspace` 运行，那份克隆停在 10-11，19 号里还有 `try {}; catch {}` 的旧缺陷，读已装版本这一步被拒，什么都没改（`51a`）。改从本次专用的工作区 worktree（origin/main 最新）重跑就通过了（`51`）。16:01 的关闸（20 号）也是从那份旧克隆跑的，结果 PASS 且回读为 False，不影响结论。**教训**：现场脚本一律从本次专用的工作区 worktree 运行，不用主目录那份。
- 19 号只要控制端加密存储里有故障恢复凭据就会把它写入服务（关着 VFR 也写，见脚本注释），所以「装回 VFR=false 后凭据变量自动消失」不成立，收工时要在服务器上单独删掉并回读（`52`）。

结论与判据见 `STATUS.md`。
