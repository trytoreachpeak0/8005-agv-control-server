# 生产切换模式方案（切换-02，control-server#564 第一步）

本文是 control-server#564 的第一步交付物：**只写方案，不改脚本、不在 factory01 上执行任何操作**。用户选定后，第二步按选定的那一条改
`scripts/parallel/` 的定义校验、19 号部署脚本与 `8005-workspace` 的运维文档。

> **2026-10-13 更新**：用户已定 D1～D9（原话记在 control-server#564 评论，Coordinator 10 转达）：D1、D2、D4～D8 按本文推荐；D3 由用户本人在 RIoT 上把
> `agv01` 搬到 map 26，初始站点建议见第 11 节；D9 生产等待值是离站 5 分钟、持货 5 分钟。第二步已实现，做了什么、还剩什么见第 10 节。

文中「读到的」指在 `fp/v2-impl@33cc27d4`、`8005-workspace@84ba0b4`（`origin/main`）、PR #676 head `5873125d` 上实读的代码与文档；
「推的」指由读到的事实推出、没有实测的结论。凡是动 `agv01`、factory01 上 MVP 服务（进程、安装目录、配置、库，含只读查询）、生产 MesIngest
的步骤，都标 **【执行前问用户】**（工作区「Ask first」第 2 类）；凡是会让车动或发急停的步骤，标 **【逐次授权·现场有人】**（第 1 类）。

## 0. 先说结论

- **形态选甲**：把今天 factory01 上的 v2 并行实例（服务名 `8005 AGV ControlServer V2`、端口 58105／58107）转正，**不新装正式实例，不接管
  58005／58007**。MVP 原样留在磁盘上、只停不删，回滚就是把它重新启动、把 `agv01` 改连回去。
- **只放开两道护栏**：`agv01` 车辆白名单、生产 MesIngest 来源下的任务类型限制。MVP 的端口、服务名、安装目录、map 25 这几道**照旧拒绝**——
  甲形态用不着放开它们，放开反而会让「回滚＝原样启动 MVP」不再成立。
- **开关是部署时的显式参数**（19 号脚本 `-ProductionCutover`）**加**定义里的批准记录，两者缺一即拒；首次以切换模式安装时要求数据目录里没有库，
  把「切换定义只装在新库上」从文档约束变成机器检查。
- **分两段走**：试跑段（真 MES、仓位模拟器、条码脚本录入、MVP 停）→ 换新库 → 生产段（真 IO、逐仓重核、等待参数设为 D9 的生产值 5 分钟／5 分钟、按 S1～S4 放开）。
- **回滚只有一条**：停 v2、把 MVP 的安装与库换回切换前那份（试跑段 MVP 库没被动过，核对哈希即可）、`agv01` 车载端换回切换前那份现场线构建并改连
  58005／58007、`agv01` 在 RIoT 上回到 map 25。**不改 MVP 代码。**
- 有一处票面和交接里都没提、但切换绕不过去的事：**`agv01` 今天在 RIoT map 25 上，v2 只跑 map 26**。切换要把 `agv01` 搬到 map 26，回滚要搬回去。
  这一步碰 `agv01`，要用户做或授权（见第 6 节第 0 步与第 9 节决定 D3）。

## 1. 现状（读到的）

| 项 | 现状 | 出处 |
| --- | --- | --- |
| v2 并行实例 | factory01 上 `8005 AGV ControlServer V2`，`C:\Program Files\8005 AGV\ControlServer.V2`、库在 `C:\ProgramData\8005\ControlServer.V2`，绑 `172.19.205.222:58105`（车载端 NDJSON）／`58107`（健康与车辆安全投影） | `scripts/parallel/instance-factory01-v2.production-mes.json` |
| v2 需求来源 | 10-10 起 `mesIngest.source=production`，只接 `STAGING_TO_WIRE`，车只有 `agv02`（`agv03` 做过两车实验） | cs#535；`ParallelInstance.psm1:101-120`、`:623-639` |
| MVP | 服务 `8005 AGV ControlServer`，`C:\Program Files\8005 AGV\ControlServer`，库在 `C:\ProgramData\8005\ControlServer`，包 `D:\zhengyushao\ControlServer`（`.previous` 留一代），端口 58005／58007，只做 `WIRE_TO_GATE`，驱动 `agv01`，RIoT map 25 | `8005-workspace` 的 `wire-to-gate-cd.md` 第 5 节；`ParallelInstance.psm1:56-99` |
| `agv01` 车载端 | 跑的是现场线 `w2g/field-agv01-*` 的构建，不是 `OnboardHmi_MVP`；安装根 `C:\8005\OnboardHmi`，`.previous` 只留一代 | `remote-ops/onboard-hmi/config/site-agv01.json`；工作区记忆「agv01 跑的是现场线」 |
| MVP 的闸门 | 两道：`JourneyRuntime.enabled`（受理需求）与 `RiotCreateDispatch.enabled`（建单、动车），都只在启动时读，改了要重启服务；`-Off` 必须在 `Completed` 之后，装货后卸货前关会让车到关卡没人开门；最后一趟用 `13-close-gates-when-idle.ps1` 在服务器上 250 ms 盯着关 | `remote-ops/factory-server/scripts/control-server/Set-JourneyRuntime.ps1` 说明 |
| 切换用定义 | PR #676（cs#669，草稿）新增 `instance-factory01-v2.cutover-two-zones.json`：三车专车专用、`WIRE_BONDING`／`DIE_ATTACH` 两区、六类任务、生产来源、map 26，其余与 `production-mes` 逐字相同（同实例、同目录、同端口、版本 6）。今天被定义校验恰好拒两条：车队表第 0 行是 `agv01`；生产来源下 `allowedWorkTypes` 含 `WIRE_TO_GATE` | PR #676 正文与 `scripts/parallel/README.md`「切换用定义」 |

## 2. 第 1 问：形态，甲还是乙

### 甲：并行实例转正（推荐）

服务名、安装目录、数据目录（换新库，旧目录改名留存）、端口、防火墙规则、凭据变量名都不变；装的定义换成 PR #676 的切换用定义；三车车载端都连
58105／58107。

要改的护栏（第 4 节细说）：

| 护栏 | 改法 | 文件 |
| --- | --- | --- |
| `agv01` 被点名拒绝（`ParallelInstance.psm1:141-147`、`:1263`、`:1321`、`:1333`），白名单只有 `agv02`／`agv03`（`:132-135`） | 切换开关打开时，白名单加入 `agv01` 的那一行（`deviceKey`、`agvId`、`riotId=58` 与 `remote-ops/fleet.md` 一致），仍按三坐标逐一比对；开关关时照旧点名拒绝 | `scripts/parallel/ParallelInstance.psm1` |
| 生产来源只许 `STAGING_TO_WIRE`、按名字拒 `WIRE_TO_GATE`（`:116-118`、`:623-639`；`:2143` 回读时也比） | 开关打开时，生产来源下 `allowedWorkTypes` 必须**恰好等于**六类全集（不是「任意子集」），回读 `EFFECTIVE_CONFIGURATION` 时比同一个集合；开关关时照旧 | 同上 |
| 自测里的「切换定义恰好被拒两条」（PR #676） | 改成「开关关时恰好拒这两条、开关开时接受」，不删 | `scripts/parallel/Test-ParallelInstance.ps1` |
| 19 号脚本 | 加 `-ProductionCutover` 参数，传给服务器上的安装器；首装版本下限照旧读 `installed-instance.json` | `8005-workspace` 的 `19-deploy-control-server-parallel.ps1`、服务器端 `Install-ParallelInstanceLocal.ps1` |
| 车载端与运维脚本里写死的「`agv01` ↔ MVP」 | 见第 3 节第 4 点，四处一起改 | `8005-workspace` 的 `remote-ops/` |

**不改**的护栏：MVP 端口 58005／58007／58009／5088（`:93-99`）、MVP 服务名（`:56`、`:714`）、MVP 路径（`:57-77`）、map 25 与 `老厂前线new`（`:82-90`、`:990-1006`）、
`v2` 实例标记（`:49`）、证书与凭据变量名（`:205-218`）。

甲的好处：

1. **回滚件是完整的**：MVP 的安装目录、库、配置、服务注册、机器级凭据变量都原地不动，只是服务停着。回滚不需要「重新部署 MVP」，只需要启动它。
2. MVP 的部署与 v2 的部署共用一部分机器级变量（例如 `CONTROL_SERVER_ONBOARD_CREDENTIAL` 由 MVP 部署写、v2 只读，`Install-ParallelInstanceLocal.ps1:803-816`）。
   甲不卸 MVP，这些变量继续在。
3. 换库有现成的快路线（停 V2、数据目录改名、空目录启动自动迁移、对运行中的库导入，10-11 实测约 8～10 分钟），PR #676 的定义也是按这个前提写的
   （同一 `instanceId`／`dataRoot`，准入版本 6 在新库首绑）。
4. 护栏改动面最小：两道放开，其余全部照旧，误用时照旧被拒。

甲的代价：服务名、目录名里一直带着 `V2`，「并行实例」这个名字在 MVP 停掉之后名不副实。这是命名问题，不影响行为；整体割接稳定、MVP 线确认退役后，
可以另开票做「正名」，那时 MVP 已无回滚价值。

### 乙：新装正式实例（不推荐）

用正式服务名、正式目录（例如沿用 `C:\Program Files\8005 AGV\ControlServer`）新装一个 v2，并行实例退役。

- 正式目录今天被 MVP 占着，而 MVP 的目录正是回滚件。要么先把 MVP 挪走（回滚变成「把挪走的东西再挪回来并重新注册服务」，比甲多出好几步），
  要么换一个新目录名（那只是另一个名字，换不来什么）。
- 要放开的护栏多得多：服务名、实例标记、生产路径列表、证书与凭据变量名（MVP 用的是不带 `V2` 的名字，`Update-ControlServerLocal.ps1` 会删它被指向的机器级变量），
  还要新 `instanceId`／`dataRoot` 并按 `AdmissionPolicyVersionGuardTests` 另立历史（PR #676 正文已点出）。
- 并行实例退役要卸载，卸载脚本的护栏也要跟着改。

乙唯一的收益是名字好看，代价是回滚变复杂、护栏大面积放开。**推荐甲。**

## 3. 第 2 问：端口与车载端改连

**推荐 v2 继续用 58105／58107，不接管 58005／58007。**

1. 车载端连哪个地址写在车载端的配置里，由 `06-deploy-onboard-hmi.ps1` 按 `-OnboardPort`／`-HealthPort` 渲染（两个都必须给，workspace#32）。切换时
   `agv01` 要换装 v3 车载端，本来就要重新部署一次，端口跟着这一次改，**不额外多一步**。所以接管 58005 换不来「车载端不用改」。
2. 接管 58005 的话：回滚时必须先停 v2 才能起 MVP（端口冲突）；要放开 MVP 端口护栏；而且「哪个服务在 58005 上」从此不再能说明「这是哪条线」，
   今天 `06` 的配对检查（58005 ⇔ MVP）就靠这一点。
3. 三车怎么改：
   - `agv02`：今天已连 58105／58107，切换时只换库，车载端日志簿、发件箱要清（第 6 节）。
   - `agv03`：已部署过 v2 车载端、连过 58105，按 cs#565 重新部署 v3 车载端并清日志簿、发件箱。
   - `agv01`：**从未跑过 v2**。按 cs#565 部署 v3 车载端，`-OnboardPort 58105 -HealthPort 58107`。部署前把 `C:\8005\OnboardHmi`（含它的数据与日志）整份
     另存为带日期的目录（第 7 节），因为 `06` 只留一代 `.previous`，试跑期间再部署一次就会把现场线那份挤掉。
4. **四处写死的配对要一起改**（workspace PR #39 审查 S2，cs#565 10-12 评论）。今天的规则是「`agv01` 只连 MVP、`agv02`／`agv03` 只连 v2」：
   - `remote-ops/factory-server/scripts/control-server/ControlServerTarget.psm1:60`（`$script:MvpVehicleAliases = @('agv01')`，`TARGET_VEHICLE_INSTANCE_MISMATCH`）；
   - `remote-ops/onboard-hmi/scripts/06-deploy-onboard-hmi.ps1:364`、`:380`（`$v2OnlyAliases = @('agv02', 'agv03')`）；`:139` 的 `-SshAlias` 默认值是 `agv01`；
   - `remote-ops/onboard-hmi/scripts/16-enter-sublot-uia.ps1:38`（`ValidateSet('agv02', 'agv03')`——试跑要靠它给 `agv01` 自动录条码）；
   - `remote-ops/factory-server/scripts/status/Get-WireToGateStatus.ps1:119`：**在 `8005-workspace@84ba0b4` 上没有这个文件**，可能在未合入的分支，第二步实施时重核。

   改法（D7，已实现）：把「这台车现在属于哪条线」收成 `site-agv0N.json` 里的单一字段 `"controlServerLine": "mvp" | "v2"`，由
   `remote-ops/onboard-hmi/scripts/VehicleControlServerLine.psm1` 读，三处脚本都读它；没有这个字段、或不是这两个值之一，一律拒绝（`VEHICLE_LINE_UNSET`），不按别名猜。
   切换那天只改 `site-agv01.json` 一处；回滚改回去也只一处。**改错的后果**（cs#565 评论，推的）：只改一部分时，正确的 v2 调用会被拒（安全方向），但
   「全按默认走 MVP＋`agv01`」反而能通过配对，可能去结算已停的 MVP 库、清掉 `agv01` 上属于 v2 的日志簿。单一字段消掉的正是这种半改状态。
5. **端口改错的后果**：车载端连到一个不认识它的服务端，表现是连接被拒或握手失败，车载端不就绪、不会动车（明文期没有协商，见 `wire-to-gate-cd.md`）。
   最危险的是 10-12 踩过的那种：健康端口填成 58007，把 v2 的投影凭据发给了 MVP。`06` 已强制两个端口都给；切换后 MVP 停着，58007 上没人听，
   同样的错只会表现为就绪探测失败。

## 4. 第 3 问：护栏怎么放开而不变成常开

工作区一贯的原则（`ParallelInstance.psm1:111-114`、`:121-123` 的注释）是「从被检查的文件里读出来的规则不算规则，因为改文件的人同一下就改了规则」。
所以开关不能只是定义里的一个布尔键。方案：

1. **两个条件同时满足才放开**：
   - 部署命令带 `-ProductionCutover`（19 号脚本传给服务器端安装器；`Test-ParallelInstanceDefinition` 增加同名参数，默认关）；
   - 定义里有 `productionCutover.approvalRecord`：一句写明「谁、哪天、在哪里批准了切换」的文字（形状照 `mapStationCatalog.approvalRecord`，cs#531），
     不写进覆盖层，只进部署记录。

   只有参数没有记录、只有记录没有参数，都拒绝，拒绝信息写明缺哪一半。
2. **放开的只有两道，且放开后的形状是钉死的**：
   - 车队表每一行都是登记册里的三台车之一，四个坐标逐一比对，`agv01` 只能以登记册里的 `deviceKey`／`riotId=58` 出现；登记册之外的车照旧拒绝；
     少于三台是分阶段（S1、S2）的定义，接受；
   - 生产来源下 `allowedWorkTypes` 恰好是六类全集；同一类任务不能出现在两辆车上（`CUTOVER_TASK_TYPE_SHARED`）；必须用车队表写车
     （`CUTOVER_REQUIRES_FLEET`）；必须是生产来源（`CUTOVER_REQUIRES_PRODUCTION_SOURCE`）。
   其余全部照旧：MVP 端口、服务名、路径、map 25、`v2` 实例标记、凭据变量名。开关开着时，把定义指向 MVP 的任何一种写法仍然被拒。
3. **首次以切换模式安装必须是新库**：安装器在 `-ProductionCutover` 且 `installed-instance.json` 记的上一次不是切换定义时，要求数据目录下（含子目录、
   跟随目录联接）没有 `*.db`／`-wal`／`-shm`，已装配置点名的库也不能存在；有就拒绝（`CUTOVER_REQUIRES_NEW_DATABASE`），也不能是 `-Rollback`。
   这把 PR #676 的失败定义「在旧库实例上就改了区名」变成机器检查。
4. **已装记录就是那份定义**：安装器把这次装的整份定义复制成 `installed-instance.json`，切换定义里带着 `productionCutover` 一节
   （`approvalRecord` 与 `phase`）。之后按这份记录判断：
   - 已是切换定义、这次没带 `-ProductionCutover` → 拒绝（`CUTOVER_SWITCH_MISSING`）。防的是有人照旧习惯用并行定义重装，把 `agv01` 从车队表里拿掉、
     而它可能正挂着在途旅程。
   - `phase` 从 `trial` 到 `production`：数据目录必须重新为空（`CUTOVER_PRODUCTION_REQUIRES_NEW_DATABASE`），也不能是 `-Rollback`；
     从 `production` 回到 `trial` 一律拒绝（`CUTOVER_PHASE_BACKWARDS`）。理由见第 5 节「为什么试跑转生产必须换新库」。
   - **离开切换**只有一条受治理的路：带 `-LeaveProductionCutover` 装一份并行定义，数据目录必须为空（切换期间的库改名留存作证据），
     不能同时带 `-ProductionCutover`，不能是 `-Rollback`。这是第 7 节回滚的最后一步。
   - 不是切换定义、这次带了开关 → 只在第 3 条（新库）成立时接受。
   - **MVP 必须已停**：带开关安装、以及给切换实例开闸，都要求 MVP 服务不存在，或者是 Stopped 且启动类型 Disabled（`CUTOVER_MVP_NOT_STOPPED`）。
     关闸不加这条。防的是回滚后 MVP 重新驱动 `agv01`，而 v2 仍装着切换定义，开闸就会给 `agv01` 建单。
5. **并发**：开关只在部署时生效，运行中的服务不读它；19 号脚本与安装器现有的「闸门开着拒绝升级」「配置改了没重启拒绝」等预检照旧（README「升级与回滚前先把旅程运行时关掉」）。
   两个控制端会话同时部署同一实例的问题与今天相同，不因开关变化。
6. **默认值**：所有出厂定义、19 号脚本的默认都是关。CI 与自测在开关关的条件下跑全部既有拒绝用例（第二步的离线测试第一条）。
7. **误用时的拒绝信息**（第二步实现，举例）：
   - `CUTOVER_SWITCH_WITHOUT_RECORD`：带了 `-ProductionCutover`，定义里没有 `productionCutover.approvalRecord`。
   - `CUTOVER_RECORD_WITHOUT_SWITCH`：定义带了批准记录，命令没带开关。
   - `CUTOVER_WORK_TYPES_NOT_FULL_SET`：切换模式下生产来源的任务类型不是六类全集。
   - 开关关着时，`agv01` 与 `WIRE_TO_GATE` 的既有拒绝信息末尾加一句「生产切换另见 `docs/production-cutover-mode-plan.md`，需 `-ProductionCutover` 与批准记录」，
     让看到拒绝的人知道正路在哪，而不是去改白名单。

## 5. 两段：试跑与生产

用户 10-12 定（program#171 10-12 评论、看板 10-12 21:37）：「三个车都上v2，然后对接真的mes需求，agv01停掉，但是用脚本自动进行上货扫码等一列操作，
用仓位模拟器，这样可以测出一些问题，跑稳定后再进入生产状态」；同日补「但是现在先不做，要找时间」。用户还确认：试跑期间车按真实需求开到真实机台但不真的上下货，
这段时间真实需求在物理上没人送，符合预期。

| | 试跑段 | 生产段 |
| --- | --- | --- |
| 需求来源 | 生产 MesIngest，六类全收 | 同左 |
| MVP | 停（服务停、启动类型 Disabled） | 同左 |
| 仓位 IO | slots-simulator（模拟器） | 真 IO 模块（`09-switch-io-module.ps1`） |
| 条码与装卸 | 脚本：`16-enter-sublot-uia.ps1` 录条码，代现场脚本按期望篮数逐仓 Load／Unload | 操作员在车上扫码、真上下货 |
| 等待类参数 | 按用户 10-11 定，模拟器测试调短（离站等待、持货等待），记调短前后的值 | **设为 D9 的生产值并实读核对：离站 `00:05:00`、持货 `00:05:00`**（用户 10-13 定）。不是「改回原值」：证据 `evidence/field/20261010-W2-test-waits/README.md` 记的持货原值是 `00:30:00`，照它改回就回到 30 分钟 |
| 库 | 新库 A（切换首装） | **新库 B**（试跑转生产必须换，见下） |
| 逐仓核对 | 不做（模拟 IO 下没有意义） | `REQ-0263`：整车逐仓重新核验，不能搬旧记录 |
| 动车 | 每次开闸【逐次授权·现场有人】 | 同左 |

**为什么试跑转生产必须换新库**（调度 C10 10-12 读码，program#170 评论）：v2 只按自己库里的 `AcceptedDemands` 判「已受理」，受理过的需求永不再选
（`AlreadyAcceptedCriterion.cs:7-15,35-38`）；旅程完成不会让需求从 MesIngest 目录消失；本地取消／故障交接终止的需求按业务键 `{sublot}|{workType}` 永久抑制
（`TransportDemandKeySuppressedCriterion.cs:11-13,32-37`）。试跑在模拟 IO 下「完成」的需求实物没动，MES 里一直挂着；同库进生产，它们会被永远判为已受理，没人送。
FieldOps 没有作废需求或重置状态的动词，手改约 50 张表风险更大。所以试跑与生产之间固定一步「换新库＋重导受治理配置＋清三车日志簿与发件箱＋逐仓重核」。

## 6. 第 4 问：停 MVP 与起 v2 的顺序

每一步写：谁执行、做什么、看到什么才进下一步。「调度」指负责切换的调度会话，「现场」指现场操作会话；所有碰 MVP、`agv01`、生产 MesIngest 的步骤，
调度在执行前向用户要授权、把原话记进本票。数据一半（需求排空、硬件事实）以 program#170 为准，这里只排顺序。

### 第一段：从 MVP 切到 v2 试跑

| # | 谁 | 做什么 | 进下一步的判据 | 标记 |
| --- | --- | --- | --- | --- |
| 0 | 调度通知用户 | 确认 `agv01` 改到 RIoT map 26 的时点与初始站点（用户 10-13 定：用户本人在 RIoT 上操作；站点建议见第 11 节，搬之前再与用户确认），**本步只定方案，不执行** | 用户确认站点 | 【执行前问用户】 |
| 1 | 调度 | 前置核对：cs#564 第二步、cs#669、cs#565 步骤表、program#170 方案都已合入或定稿；19 号部署的包是 v3 发布身份、含光幕极性（cs#44／hmi#294）；v2 并行实例闸门关、无在途旅程 | 逐项有出处 | — |
| 2 | 现场 | **停 MVP 派新单**：等 `agv01` 当前这趟 `Completed`，用 `13-close-gates-when-idle.ps1`（服务器上 250 ms 盯着，完成即停服务再写 `-Off`）关 MVP 的两道闸门。不在装货后、卸货前关 | MVP 两个开关回读为 false；服务日志不再有受理；`agv01` 停在站上、无 RIoT 在途单 | 【执行前问用户】 |
| 3 | 现场 | **确认 MVP 在途为零**：MVP 库里受理未完成的需求、未终结的旅程、未结的 RIoT 订单都为 0；RIoT 上 `agv01` 无未结单；`agv01` 车载端发件箱无欠送（三处，program#170 第 1 节写具体表与状态） | 三处都为 0；任何一处非 0 就停在这一步 | 【执行前问用户】（读 MVP 库也是第 2 类） |
| 4 | 现场 | **停 MVP 服务并设为 Disabled**；备份 MVP（第 7 节清单）：安装目录、`D:\zhengyushao\ControlServer`（含 `.previous`）、库目录整份复制、`appsettings.Production.json`、服务注册信息（`sc qc`）、机器级环境变量名单。记录 SHA-256 | 服务 Stopped／Disabled；备份哈希写进证据目录 | 【执行前问用户】 |
| 5 | 现场 | **备份 `agv01` 车载端**：`C:\8005\OnboardHmi` 整份（含日志簿、发件箱、本地仓位配置、日志）另存为 `C:\8005\OnboardHmi.mvp-field-<日期>`；记录版本串（`appsettings.json` 的 `wireToGate.onboardBuildCommit`） | 备份目录存在、哈希记录 | 【执行前问用户】 |
| 6 | 用户本人（RIoT） | 按第 0 步确认的站点把 `agv01` 改到 map 26 | RIoT 读回 `agv01` 在 map 26、已定位在确认的站点 | 【执行前问用户】 |
| 7 | 现场 | **v2 换新库 A**：停 V2（闸门已关、无在途），`C:\ProgramData\8005\ControlServer.V2` 改名留存，建空目录。**不要按换库快路线在空目录上先起服**：起服会自动建库，第 8 步就会被 `CUTOVER_REQUIRES_NEW_DATABASE` 拒 | 空目录；旧目录名记录 | — |
| 7a | 调度 | **准备切换用的定义并出包**（审查 S1）：19 号要求定义是克隆里被跟踪的文件、包的提交等于克隆 HEAD。所以执行前提交一次，内容是本次要用的全部阶段定义（见第 8 节：S1 运行时关、S1 运行时开、S2、S3），每份都写入 `productionCutover.approvalRecord`（用户对这一次切换的授权原话出处）与 `phase: "trial"`；然后为这个提交跑一次 release。出厂的切换定义不带记录，原样装会被 `CUTOVER_SWITCH_WITHOUT_RECORD` 拒 | 提交与 release run id 记进本票 | 授权原话由调度向用户要 |
| 8 | 现场 | **以切换模式首装 S1 定义**（只有 `agv02`，运行时与建单闸门都关）：19 号脚本带 `-ProductionCutover -InstanceDefinitionPath <S1 运行时关的定义>` | 回读 `EFFECTIVE_CONFIGURATION`：车队只有 `agv02`、区只有 `WIRE_BONDING`、六类、生产来源；`installed-instance.json` 是这份定义（带 `productionCutover`）；MVP 指纹前后一致（停着、Disabled） | 读生产 MesIngest 全部六类属于切换本身，本步之前向用户确认 |
| 9 | 现场 | 车载端按 cs#565：先确认发件箱无欠送再清日志簿与发件箱，部署 v3 车载端（`-OnboardPort 58105 -HealthPort 58107`），IO 指向模拟器。S1 只需要 `agv02`；`agv01`、`agv03` 留到它们所在阶段之前做（第 8 节）。`agv01` 那次要先把 `site-agv01.json` 的 `controlServerLine` 由 `"mvp"` 改为 `"v2"`（`8005-workspace` 提交） | 该车握手 `Ready`，不动车 | `agv01` 每一步【执行前问用户】 |
| 10 | 现场 | 导入受治理事实（program#170 第 2 节：已批准硬件事实、`bind-io`、站点绑定、等待点、充电名册与策略）。分区归属表这时还不能导 | 每项导入回读 | — |
| 11 | 现场 | **让本阶段的区进库**（PR #676 README 第 4 步）：建单闸门保持关，用 19 号带 `-ProductionCutover` 换装本阶段运行时开的定义，至少一台车连上并空闲，等一轮，`DispatchZoneVehicles` 出现本阶段各区 | 库表读回本阶段各区（S1、S2 只有 `WIRE_BONDING`） | 运行时打开读真实 MES：本步向用户确认 |
| 12 | 调度 C11 | **本阶段的归属表**：导入是整表替换（PR #676 审查 M1），所以每次导入的都是「当前已进库各区」的一份合并表。S1、S2 只导 N 区表 `field-prep/area-assignments-N-WIRE_BONDING.csv`；S3 加入 `DIE_ATTACH` 后导合并表 `field-prep/area-assignments-merged-N-T.csv`，替换掉 N 区那份。先离线比区名（`Test-ParallelInstance.ps1 -AreaAssignmentPath <本阶段的表>`，区名集合必须恰好等于本阶段定义的 `allowedDispatchZones`），再 `--dry-run` | 不报 `DISPATCH_ZONE_NOT_FOUND`；区名比对 0 条不一致 | — |
| 13 | 用户批准 → 现场导入 | **每区参数**（cs#219）：`REQ-0203`（防饥饿阈值）按区批准后导入；`REQ-0198`（途中追加上限）在 S1～S3 期间**为 0 或不导入**（规格第 22.2 节第 3 条：这样一趟只带一条需求），S4 才导入批准值。`DIE_ATTACH` 的一套在 S3 之前批；`WIRE_BONDING` 也没有已批准的旧 `WIRE` 值可抄。必须在第 14 步之前，或用户明确接受「先按未配置跑」 | 导入回读；或用户原话 | 【执行前问用户】（批准） |
| 14 | 现场 | 正式导入本阶段的归属表。**导入之后表里的 AREA 立刻可被受理**（运行时开着时闸门关着也受理、旅程等闸门） | 导入回读 | — |
| 15 | 现场 | 试跑等待参数调短（离站、持货），记调短前后的值与位置；生产段一律以 D9 的 5 分钟／5 分钟为准，不以这里记的「调短前」为准 | 实读安装目录 `appsettings.Production.json` | — |
| 16 | 现场 | **开闸放本阶段的车**：只有本阶段定义里的车会被派单 | 本阶段判据（第 8 节） | 【逐次授权·现场有人】 |
| 16a | 现场 | **换下一阶段**（S1→S2→S3）：关闸（20 号 `-State Closed`）、确认无在途；下一阶段新加的车先做第 9 步（`agv01` 加入前还要先跑 `Set-ControlServerSecret.ps1 -NewVehicleSafetyCredential agv01`，否则 19 号会以缺投影凭据拒绝，审查 S2）；19 号带 `-ProductionCutover` 换装该阶段运行时开的定义；新区进库后重做第 12～14 步；再按第 16 步开闸 | 每次换装的 `EFFECTIVE_CONFIGURATION` 车队与区等于该阶段定义 | 动 `agv01`【执行前问用户】；开闸【逐次授权·现场有人】 |

开闸之前，车队表里有哪几台车就只有哪几台会被派单：派车轮判空闲车看的是旅程占用，不看车载端连没连（第 8 节）。所以第 8 步装的是只有 `agv02` 的
S1 定义，`agv01`、`agv03` 在各自阶段换装定义之后才进车队表（审查 H1）。

试跑段要靠脚本代替人操作 `agv01` 的屏幕（条码录入、读屏）。`16-enter-sublot-uia.ps1` 已改为看站点文件的单一字段；`17-capture-hmi-screen.ps1`、
`18-read-hmi-screen-state.ps1` 今天仍然拒绝 `agv01`，它们属于「碰 `agv01` 先问用户」那一类护栏，试跑前要单独决定怎么处理（审查 M2）。

### 第二段：试跑转生产

**顺序（cs#609 审查 M1）**：cs#609 合入之后，现场 v2 实例必须先用 19 号脚本重装（把持货 5 分钟写进已装定义与配置），然后才能开闸。原因是 20 号开闸会检查服务器上的 `installed-instance.json`，而现场现在装的是持货 30 分钟的定义，不重装就开闸会被拒。这一条对第一段第 8 步同样适用：切换首装用的定义必须已经带着 5 分钟。「不重装就开闸会被拒」的前提是 20 号所用的 control-server 检出已含 cs#609。

| # | 谁 | 做什么 | 判据 | 标记 |
| --- | --- | --- | --- | --- |
| 17 | 现场 | 关 v2 闸门（`20-set-control-server-parallel-dispatch-gate.ps1 -State Closed`，任何旅程不是 `Completed` 就拒绝）、关运行时；无在途 | 闸门脚本成功；库里无非终态旅程 | — |
| 18 | 现场 | **换新库 B**：同第 7 步，库 A 改名留存（它是试跑证据）；同样不要先起服 | 空目录 | — |
| 18a | 调度 | 同第 7a 步，提交生产段的阶段定义（`phase: "production"`，批准记录写生产段的授权）并出包 | 提交与 release run id | 授权原话由调度向用户要 |
| 19 | 现场 | 19 号脚本带 `-ProductionCutover` 装生产段 S1 定义。安装器按已装记录的 `phase` 从 `trial` 到 `production`，要求数据目录为空（`CUTOVER_PRODUCTION_REQUIRES_NEW_DATABASE`），所以漏了第 18 步会被拒。**前提：cs#609 已合入**，定义与产品默认值里的持货已从 `00:30:00` 改为 `00:05:00` | **实读**安装目录 `C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json`：`stationDepartureWaitTimeout=00:05:00`、`cargoHoldingTimeout=00:05:00`（D9）。`EFFECTIVE_CONFIGURATION` 事件今天不带这两个值（`src/ControlServer.Host/Runtime/EffectiveConfigurationEvent.cs:40`），等 cs#672 加进事件后再改为按事件回读。不要手改已装文件代替重装：手改不经过部署校验，cs#609 的上限挡不住 | — |
| 20 | 现场 | 本阶段的车 IO 从模拟器切回真模块（`09-switch-io-module.ps1`），清日志簿与发件箱 | 握手 `Ready` | `agv01`【执行前问用户】 |
| 21 | 现场 | 重导受治理事实；**逐仓重核**（`REQ-0263`，cs#565 第 5 步） | 核对证据目录 | 动车部分【逐次授权·现场有人】 |
| 22 | 现场 | 按第 11～16a 步逐阶段走 S1→S4（第 8 节） | 每阶段判据 | 【逐次授权·现场有人】 |

## 7. 第 5 问：回滚

规格第 8.4.2 节：**唯一回退是换回切换前的 MVP 部署与数据，不改 MVP 代码**；MVP 线切换后不再开发。

### 切换前要备份的（第 6 节第 4、5 步）

| 件 | 位置 | 为什么 |
| --- | --- | --- |
| MVP 安装目录 | `C:\Program Files\8005 AGV\ControlServer` | 回滚时原地启动它；备份用来核对没人动过 |
| MVP 包目录 | `D:\zhengyushao\ControlServer`、`.previous` | 同上；`-Rollback` 的依据 |
| MVP 库 | `C:\ProgramData\8005\ControlServer`（整个目录，停服后复制） | 「切换前的数据」 |
| MVP 配置 | `appsettings.Production.json`（闸门两个开关是关着的那份） | 回滚后的闸门状态 |
| MVP 服务注册 | `sc qc "8005 AGV ControlServer"` 的输出、原启动类型 | 回滚时把启动类型改回去 |
| MVP 用到的机器级环境变量名单 | 只记名字不记值 | 核对没被 v2 部署删掉 |
| `agv01` 车载端 | `C:\8005\OnboardHmi` 整份另存 `C:\8005\OnboardHmi.mvp-field-<日期>`，含日志簿、发件箱、仓位配置 | `06` 只留一代 `.previous`，不另存就会被挤掉 |
| `agv01` 在 RIoT 上的地图与站点 | map 25、当前定位站 | 回滚时搬回去 |

### 回滚步骤（每一步都碰 MVP 或 `agv01`，全部【执行前问用户】）

1. **关 v2**：v2 闸门关（第 17 步同法），等在途收尾，再关运行时（审查 S4）。v2 在途的单怎么收尾、已完成的单对 MES 怎么交代，以 program#170 第 4 节为准。
   v2 服务停、设 Manual，**不卸载、不删库**（库是切换期间的证据，也是 program#170 第 4 节要回答的数据）。v2 停着的这段时间，MVP 已停的检查
   保证没人能给切换实例开闸。
2. **`agv01` 车载端换回**：停车载端栈；把 `C:\8005\OnboardHmi` 改名留存，`C:\8005\OnboardHmi.mvp-field-<日期>` 改回 `C:\8005\OnboardHmi`。核对版本串等于备份时记录的那个。
   这一份里的配置本来就连 58005／58007，不需要重新渲染。
3. `site-agv01.json` 改回 MVP 线（第 3 节第 4 点的单一字段）。
4. `agv01` 在 RIoT 上回到 map 25（第 6 节第 0 步定下做法的逆操作）。
5. **MVP 换回**：核对 MVP 安装目录、包目录、库与备份哈希一致。
   - 试跑段回滚：MVP 停着、没人动过，哈希一致即可，**不需要还原**。
   - 若哈希不一致（有人动过），用备份整份覆盖。
6. MVP 服务启动类型改回原值、启动；闸门照切换前的做法用 `Set-JourneyRuntime.ps1` 开（`-Intake`／`-Dispatch`，后者【逐次授权·现场有人】）。
7. 回读：MVP 健康、`agv01` 握手、MVP 指纹与切换前一致（路径、启动类型）。
8. **离开切换**（要让 v2 回到并行期时才做）：v2 数据目录改名留存、建空目录、不起服；19 号带 `-LeaveProductionCutover` 装并行定义
   （例如 `instance-factory01-v2.production-mes.json`）。不带这个开关、或数据目录里还有库，安装器都会拒绝。卸载不带 `-RemoveData` 会留着
   `installed-instance.json`，之后不带开关的重装照样被拒，所以离开切换要走这一步，不要手改服务器上的记录。

两点要知道：

- **v2 那边的 `-Rollback`（换回上一代包）不是这里的回滚**。它只换 v2 的包，版本只能往上走（README「回滚：库不跟着回滚」）。切换回滚不碰 v2 的包。
- **生产段回滚时 MVP 会不会重做 v2 已经送过的需求**，取决于 MES 侧在实物移动后需求是否离开目录，属于 program#170 第 1、4 节要论证的内容；试跑段没有实物移动，
  需求本来就没人送，MVP 接回去是对的。

## 8. 第 6 问：与 S1～S4 的关系

规格第 8.4.2 节四阶段：S1 一台车、一趟一条需求；S2 两台；S3 三台；S4 放开一车多需求。每阶段「在跑的每项能力都至少完整走通一次、无需人工处置」才进下一阶段；
出现安全类异常即停在当前阶段，当场修不好就回退。

怎么表达（读到的）：

- **车辆数**只能用车队表的行数表达，不能用「另外两台车不连」表达：派车轮判空闲车看的是旅程占用，不看车载端连没连（`JourneyRuntimeEngine.cs:779-789`），
  一台在车队表里但车载端没起的车照样会被派单。所以每个阶段一份定义，都按第 4 节同一套规则校验，阶段之间用 19 号脚本带 `-ProductionCutover` 换定义重装
  （同库，已装记录已是切换定义）。换定义前关闸、无在途。各阶段定义从 PR #676 的切换定义复制，只改下表几项：

  | 阶段 | `journeyRuntime.fleet` | `allowedDispatchZones` | 归属表（整表替换） |
  | --- | --- | --- | --- |
  | S1 | `agv02` 一行 | `["WIRE_BONDING"]` | N 区表 |
  | S2 | `agv02`、`agv01` 两行 | `["WIRE_BONDING"]` | N 区表（不变） |
  | S3、S4 | 三行（即 PR #676 的切换定义） | `["WIRE_BONDING", "DIE_ATTACH"]` | N+T 合并表 |

  `dispatchZone` 都是 `WIRE_BONDING`；`allowedWorkTypes` 都是六类（切换开关要求全集）。S1、S2 不写 `DIE_ATTACH`：那时没有车服务这个区，它进不了
  `DispatchZoneVehicles`，写进允许列表只会让区名比对报「这个区没有表」。S1 时其余五类需求读得到但没有车能接：`WIRE_TO_GATE` 等 N 区需求按任务类型
  只能给 `agv01`，T 区需求没有归属表、一律 `OUT_OF_SCOPE_AREA`（推的，依据同 PR #676 的读码，未实测）。每个阶段都要一份运行时开的定义；S1 另要一份运行时关的
  首装用。本票的自测证明了 S1、S2 两种车队形状被接受。
- **一趟一条需求**：规格第 22.2 节第 3 条——每区参数批准前初始派车只取一条需求，受理后加入的都算途中追加、受 `REQ-0198` 约束；未配置即本区禁止途中追加。
  所以 S1～S3 不导入 `REQ-0198`（或导入 0），S4 才导入批准值；`REQ-0203` 可以先导。第 6 节第 13 步已按此写。
- **专车专用使 S1 只能覆盖一类车的任务**：规格 S1 列的首次能力里同时有 `WIRE_TO_GATE` 与 `STAGING_TO_WIRE`，三车专车专用后一台车做不了两类。
  所以 S1 的「每项能力走通」只能按那台车的任务类型算，另一类在 S2 补齐。哪台车先上见第 9 节决定 D5。

试跑段与生产段各走不走一遍 S1～S4，是用户要定的（决定 D4）。

## 9. 要用户定的事

| # | 问题 | 选项 | 推荐与理由 |
| --- | --- | --- | --- |
| D1 | 形态 | 甲：并行实例转正；乙：新装正式实例 | **甲**。回滚件（MVP 目录、库、服务注册）原地不动，只放开两道护栏；乙只换来名字好看 |
| D2 | 端口 | 保持 58105／58107；接管 58005／58007 | **保持**。`agv01` 本来就要重新部署车载端，接管换不来少一步，反而让回滚多一步、要放开 MVP 端口护栏 |
| D3 | `agv01` 的地图 | `agv01` 搬到 RIoT map 26（v2 的图），回滚时搬回 25；或 v2 改跑 map 25 | **搬到 26**。v2 的站点清单、准入种子、归属表、等待点都绑 26，改跑 25 等于全部重做。要用户定的是：谁在 RIoT 上做、什么时候做、map 26 上 `agv01` 的初始站点 |
| D4 | 试跑与生产各怎么放开 | a：试跑按 S1→S3 逐台加车，生产换库后再按 S1→S4；b：试跑三车一起上，生产按 S1→S4；c：试跑按 S1→S3，生产直接从试跑达到的阶段开始 | **a**。车在真实通道里跑、多车争用充电桩与等待点，这些风险与有没有货无关，试跑也该逐台加；生产换库、切真 IO、逐仓重核后是另一套条件，规格的阶段判据要在它上面重新走 |
| D5 | S1 先上哪台车 | `agv02`（`STAGING_TO_WIRE`）／`agv01`（`WIRE_TO_GATE`）／`agv03`（装片两类） | **`agv02`**：v2 上现场时间最长、10-10 起已在生产来源下跑 `STAGING_TO_WIRE`；`agv01` 第二个上（它从没跑过 v2，前一阶段稳定后再加） |
| D6 | 开关的形状 | 命令行 `-ProductionCutover` ＋ 定义里的批准记录；只用其中一个 | **两个都要**。只放定义里，改文件的人一下就打开了；只放命令行，部署记录里看不出是谁批准的 |
| D7 | 车与线的对应 | 四处脚本各自改；收成 `site-agv0N.json` 单一字段 | **单一字段**。切换与回滚各只改一处，消掉半改状态 |
| D8 | 每区参数 | 归属表正式导入前批完（cs#219）；或接受先按未配置跑 | 用户批。`DIE_ATTACH` 必须新批；`WIRE_BONDING` 也没有已批准的旧值可抄 |
| D9 | 生产等待值 | 定义里的 `00:05:00`／`00:30:00`；cs#566 收工记录写的「5 分钟／5 分钟」 | 用户确认哪个是生产值（两处不一致，至少一处记错） |

**用户 2026-10-13 的决定**（control-server#564 评论）：D1、D2、D4、D5、D6、D7、D8 按上表推荐。D3：搬到 map 26，由用户本人在 RIoT 上操作，做到那一步时由调度
通知用户，初始站点按第 11 节的建议、搬之前再确认。D9：离站 5 分钟、持货 5 分钟。只读核对（仓库与证据，未碰 factory01）：持货 `00:30:00` 出现在
五份在用定义、PR #676 的切换定义与产品 `appsettings.json:53`；现场最后一次实读（`evidence/field/20261012-req0178/52-closeout-server.txt`）也是
`00:30:00`——前一天手改的 5 分钟被那次 19 号重装按定义写回了。改值归 cs#609。

另外两件不是决定、但要用户知道：试跑期间真实需求在物理上没人送（用户 10-12 已确认）；切换窗口里每一次碰 MVP、`agv01`、生产 MesIngest 与每一次开闸，仍逐次问。

## 10. 第二步：做了什么、还剩什么

**本仓（control-server）**

- `scripts/parallel/ParallelInstance.psm1`：`Test-ParallelInstanceDefinition`／`Assert-` 加 `-ProductionCutover`；定义加顶层 `productionCutover.approvalRecord`
  （进白名单，覆盖层不写）；两半缺一即拒（`CUTOVER_SWITCH_WITHOUT_RECORD`、`CUTOVER_RECORD_WITHOUT_SWITCH`）；两半都在时只放开 `agv01` 白名单
  （按登记册四坐标比对）与生产来源六类（`CUTOVER_WORK_TYPES_NOT_FULL_SET`），另加 `CUTOVER_REQUIRES_FLEET`、`CUTOVER_TASK_TYPE_SHARED`、
  `CUTOVER_REQUIRES_PRODUCTION_SOURCE`；开关关着时 `agv01` 与 `WIRE_TO_GATE` 的拒绝信息末尾指向本文。新函数 `Get-ParallelCutoverInstallRefusal`
  管安装历史：首次切换安装要求数据目录里没有 `*.db`／`-wal`／`-shm`（`CUTOVER_REQUIRES_NEW_DATABASE`）、不能是 `-Rollback`
  （`CUTOVER_FIRST_INSTALL_NOT_ROLLBACK`）；已是切换模式后不带开关的安装或回滚一律拒（`CUTOVER_SWITCH_MISSING`）。读回
  `EFFECTIVE_CONFIGURATION` 时切换定义按六类集合比对。
- `Install-ParallelInstanceLocal.ps1` 接开关，在记录定义、停任何东西之前做上面的历史检查；`Uninstall-`、`Set-ParallelDispatchGateLocal.ps1` 与退役替身
  从已装定义里的批准记录取开关（记录只能经带开关的安装进 `installed-instance.json`），出口方向不会被新校验挡住。
- `Test-ParallelInstance.ps1` 新增 66 条：开关关时切换形状的定义恰好被那两道拒；开关开时三车、S1、S2 都收；MVP 端口、服务名、路径、map 25、闸门、
  MesIngest 地址等 13 种改法在开关开、关两种情况下拒绝信息逐字相同；误用；安装历史 12 种组合（含 v2 包回滚与离开切换）；读回。

**`8005-workspace`**（另一个 PR，合回 `main`）

- 19 号脚本加 `-ProductionCutover`，传给服务器端安装器；只在打开时才把参数传给模块，旧的 control-server 检出照常部署今天的定义。
- 车与线的单一字段（D7）：`site-agv0N.json` 的 `controlServerLine`，新模块 `VehicleControlServerLine.psm1`；`ControlServerTarget.psm1`、
  `06-deploy-onboard-hmi.ps1`、`16-enter-sublot-uia.ps1` 改为读它（06 新增 `V2_PORTS_FOR_MVP_VEHICLE`：MVP 线的车拿到 v2 端口也拒）。
  cs#565 评论点名的第 4 处 `status/Get-WireToGateStatus.ps1` 在 `origin/main` 上没有这个文件，未改。
- `server-v2-recovery-window.ps1` 同样从已装定义的记录取开关。

**审查第一轮（2026-10-13）之后加的**

- `productionCutover.phase`（`trial`／`production`，必填）；安装历史加 `CUTOVER_PRODUCTION_REQUIRES_NEW_DATABASE`、`CUTOVER_PRODUCTION_NOT_ROLLBACK`、
  `CUTOVER_PHASE_BACKWARDS`（审查 S3）。
- 受治理的离开切换：安装器与 19 号加 `-LeaveProductionCutover`，配套 `CUTOVER_LEAVE_REQUIRES_NEW_DATABASE` 等拒绝码（审查 S4）。
- MVP 必须已停：`Get-ParallelCutoverMvpRefusal`，带开关安装与切换实例开闸时要求 MVP 不存在或 Stopped＋Disabled（审查 M3）。
- 安装历史读什么收进 `ParallelHost.psm1` 的 `Get-ParallelCutoverInstallState`：只读 `installed-instance.json`；库文件扫描含子目录、跟随联接，
  并加上已装配置点名的那个库（审查 L）。
- 审计行在切换时写「PRODUCTION CUTOVER」与阶段，不再说「只接 STAGING_TO_WIRE」（审查 M1 的服务端一半；19 号那一半在 `8005-workspace`）。

**还没做、由谁做**

- PR #676（已合入）的两份切换定义：原用例「开关关时恰好拒这两条」保留；本票新增一条：开关开、且执行当天写入批准记录时收；开关开而没有记录时，
  报 `CUTOVER_SWITCH_WITHOUT_RECORD`，两道护栏照拒。出厂的切换定义**不带**批准记录，这是有意的：记录写的是「谁在哪天批准了这一次切换」，
  执行当天才填，没填就装不上。
- 分阶段定义（D4 选 a）：执行前由第 6 节第 7a 步提交并出包，形状见第 8 节的表；本票自测已证明 S1、S2 两种车队形状被接受。
- 持货 5 分钟：cs#609。
- `wire-to-gate-parallel-cd.md` 第 14 节（`8005-workspace` PR #41）记部署脚本一侧；逐步操作单以本文第 6、7 节为准。

**本方案怎么算失败**（票面）：没写回滚，或回滚写成了改 MVP 代码。本方案的回滚是第 7 节，只换回 MVP 的部署与数据、`agv01` 的车载端与地图，不改任何 MVP 代码。

## 11. `agv01` 在 26 号图上的初始站点：建议 210「关卡」

用户 10-13 要求给一个具体建议，写清：车实际停在哪、离它习惯的位置近不近、是不是 v2 的等待点或充电桩、会不会和 `agv02`／`agv03` 抢。
以下「读到的」出自仓库与证据，没有碰 RIoT 或 `agv01`；「推的」要在搬之前由用户现场确认。

**建议：210「关卡」。**理由：

1. **车实际会停在那里（推的，执行时读 RIoT 核对）。**第 6 节第 2 步要等 `agv01` 当前这趟 `WIRE_TO_GATE` 完成才关 MVP 的闸门，而 MVP 的这类任务终点就是
   关卡（MVP 配置 `gateStationRiotId 210`，见 `8005-workspace` 的 `Set-JourneyRuntime.ps1` 说明）。所以排空时车大概率就停在关卡，在原地改图、
   原地定位，不需要先手动挪车。若那时车不在关卡（例如 MVP 让它去充电了），就请用户先在 RIoT 上把它挪到关卡再改图。
2. **两张图上是同一个点（推的，要用户确认）。**26 号图的 210 名为「关卡」（读到的，`evidence/field/20261011-B10-05-map26-task-type-stations/catalog-26.json`）；
   25 号图的 210 也是关卡。旁证：MVP 在 25 号图上给 `agv01` 下的充电单用的是 212→211、终点名「充电点1」（读到的，
   `evidence/field/20261011-W3-cannot-charge/05-agv01-precheck.txt`），与 26 号图 211「充电点1」、212「充电准备点1」编号一致，说明两张图的站点编号是对应的。
   回滚时搬回 25 号图，同样在 210 原地定位，前后对称。
3. **它不是 v2 管的公共资源（读到的）。**26 号图上 v2 自己管独占的点是 211「充电点1」（充电名册）、212「充电准备点1」（进桩路径）、214～216「等待点1～3」
   （等待点登记，空闲返回与清桩都往那里开）。把 `agv01` 放在这几个点上，v2 并不知道那里已经有车，可能再派别的车过去。210 不在其中。
4. **不和另两台车抢（读到的）。**26 号图六类任务的固定站（`task-type-stations.map-26.settings.json`）：`WIRE_TO_GATE`→210、`STAGING_TO_WIRE`→217、
   `DIE_TO_OVEN`→218、`WIRE_TO_OPTICAL`→219、`DIE_TO_WIRE_STAGING`→220、`WIRE_TO_NITROGEN`→221。专车专用之后，以 210 为固定端的任务只有
   `agv01` 自己的 `WIRE_TO_GATE`，`agv02`、`agv03` 的任务都不以 210 为终点。**没有核对的一点**：别的车的路线会不会从 210 旁边经过——这要在 RIoT 地图上看，
   请用户确认 210 停一台车不挡 `agv02` 去 217、N 区，`agv03` 去 218、220 与 T 区的通道。
5. 运行时与闸门打开后，v2 看到 `agv01` 空闲，可能按空闲返回把它开到等待点；那是 v2 管着的移动，属于 S2 那次开闸的授权范围。

**不建议的点**：211、212（充电桩与进桩路径）、214～216（等待点）、217～221（其余五类任务的固定站，217 是 `agv02` 的取货站）、N／T 开头的机台站
（归属表里的站，别的车会来）。
