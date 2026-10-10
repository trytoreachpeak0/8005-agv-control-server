# scripts/parallel —— 并行期 v2 在 factory01 上的第二套部署

control-server#262。约 2026-10-08 起 `factory01` 上同时跑两套 ControlServer：MVP 驱动 `agv01`，
这一套驱动 `agv02`／`agv03`，只吃注入的测试需求。权威文档是 `8005-workspace` 仓的
`remote-ops/factory-server/docs/wire-to-gate-parallel-cd.md`，控制端入口是同目录下的
`scripts/19-deploy-control-server-parallel.ps1`。这里只放随产品版本走的部分。

## 需求来源：`mesIngest.source`（control-server#535）

2026-10-09 用户批准：v2 可以读生产 MesIngest，**但只做 `STAGING_TO_WIRE`**，MVP 继续只做 `WIRE_TO_GATE`。
实例定义的 `mesIngest.source` 选两种模式之一：

| 模式 | 取需求 | 规则 |
| --- | --- | --- |
| `fake`（缺省，与 #535 之前一字不差） | 本实例自己的 FakeMesIngest 替身 | `baseUrl` 必须回环、端口不能是 5088；`fakeMesIngest` 必填、端口与 `baseUrl` 一致 |
| `production` | 本机生产 MesIngest | `baseUrl` 必须恰好是 `http://127.0.0.1:5088`；`fakeMesIngest` 必须**不写**；`journeyRuntime.allowedWorkTypes` 必须恰好是 `["STAGING_TO_WIRE"]`，含 `WIRE_TO_GATE` 时单独报「会和 MVP 抢同一批料」；部署配置里必须有 MesIngest 共享密钥 |

分流**只靠两边任务类型不相交**：MesIngest 只读、没有认领，两边看到的是同一份目录，一条需求被一边拿走后
在另一边**仍然看得见**（09-20 那条「认领后对方看不见」的理由按代码并不成立）。所以 `production` 的
任务类型是写死在 `ParallelInstance.psm1` 里校验的，不从定义文件里读。

`instance-factory01-v2.production-mes.json` 是 10-10 受控试运行用的定义：与出厂文件只差 MES 来源、
没有替身段、任务类型只留 `STAGING_TO_WIRE`，旅程运行时与建单闸门仍是关的（自测断言两份文件的其余部分逐字相同）。
**实际生效的值以 Host 为准，不以定义为准**（#535 审查 M1/M2）。.NET 配置跨文件按下标合并数组，叠加层的
`["STAGING_TO_WIRE"]` 曾经只盖住包内 `appsettings.json` 六项里的第一项，`WIRE_TO_GATE` 照样生效。现在 Host
对 `allowedWorkTypes`、`allowedDispatchZones` 取最后一个写了它的配置层的整份列表（`JourneyRuntimeOptionsRegistration`），
启动后记一条 `EFFECTIVE_CONFIGURATION` 事件；安装器在安装与回滚之后从 Host 的日志里读这条事件、与定义逐项比对，
`production` 下读不到或不一致都算失败。

从 `fake` 装成 `production` 时，安装器在记录新定义之前先撤掉上一次安装留下的替身（计划任务、进程、目录），
否则之后的卸载按新定义找不到它。卸载与关闸两种模式都能走；`production` 下的足迹里没有计划任务和替身目录。

## map 26 的取值与出处

用户 2026-09-21 答复：**MVP 跑 map 25，v2 跑 map 26**。

| 键 | 现值 | 出处 |
| --- | --- | --- |
| `routeGraph.mapId`、`journeyRuntime.mapId` | 26 | 用户答复 |
| `journeyRuntime.mapIdentity` | `老厂前线new_wk` | `evidence/field/2026-09-19-B6-map-name-baseline-check/real-riot/fields.json` |
| `journeyRuntime.dispatchZone`、`allowedDispatchZones` | `WIRE` | 用户 2026-09-18 定：`evidence/field/2026-09-18-B4-site-prerequisites/03-area-assignment-table.md` 第 58、122 行（区域分配表的 `dispatch_zone` 全部是 `WIRE`，实例必须配成同一个值，否则那张表导入会报 `DISPATCH_ZONE_NOT_FOUND`） |
| `journeyRuntime.admissionPolicyDeploymentId` | `MAP-26-WIRE_TO_GATE-20261007` | 调度 2026-10-07 定，格式沿用 map 25 的 `MAP-25-WIRE_TO_GATE-20260827`（control-server#411）。它是部署标签，不是业务参数。**实例第一次带着运行时启动之后，它就和 `admissionPolicyVersion` 一起固定在库里**：只改标签、不升版本，服务端会报 `Admission policy version is already bound to different content or deployment identity.`，判为准入策略漂移（`WireToGateStore.cs` 的 `ApplyAdmissionPolicyAsync`）。要换标签，就同时升 `admissionPolicyVersion` |
| `journeyRuntime.admissionPolicyVersion` | `3` | 批次 10（control-server#545）放开同向四类时由 2 升到 3，与 `appsettings.json` 一致。**今后用批次 10 之后的包重装时，两份定义都必须是这个值或更高**，见下文「准入策略版本」 |
| `taskTypeStations.settingsFile` | `task-type-stations.map-26.settings.json` | control-server#518。包里随 Host 带一份 26 号图站点清单，定义点名它，覆盖层写成 `TaskTypeStations:settingsFile`（Host 按安装目录解析相对路径）。包内默认的 `task-type-stations.settings.json` 仍绑 25，给 MVP 线用，不动。见下文 |

这三个值都是服务端自己的配置：调度区存在本实例的库里，准入策略部署号是服务端写库时带的标签。**RIoT 里没有它们，也就无从「从 RIoT 取回」**——此前这里和工作区文档都这么写过，那是错的（control-server#411）。

在 control-server#411 之前，这三个键是 `REPLACE_*` 占位，校验见到 `REPLACE_` 就拒绝，出厂定义因此装不上。这条检查留着，防的是以后有人再写占位。map 25、`老厂前线new`、任何 `MAP-25-*` 标识符也一律拒绝（control-server#262 复审 M3）。

**站点清单与准入策略都只在 `JourneyRuntime.enabled=true` 时才会被读**（`TaskTypeStationStartup.cs` 在运行时关着时直接返回；准入策略在 `JourneyRuntimeEngine` 的一轮迭代里写库）。所以运行时关着的实例用不到它们；开运行时要用的 26 版站点清单已经随包提供，并由定义点名（见下一段）。

**26 版站点清单自 control-server#518 起在包里**：`src/ControlServer.Host/task-type-stations.map-26.settings.json`，只绑
`WIRE_TO_GATE` → 210「关卡」。站号与站名取自已入库证据（`evidence/field/2026-09-19-B6-map-name-baseline-check/SUMMARY.md` 结论 4、
`evidence/field/2026-10-03-B9-charging-roster-and-policy/catalog-26.json`），没有读 RIoT。`siteVerificationRef` 是
`MAP-26-WIRE_TO_GATE-B6-IDENTITY-20260919`：依据的是 B6 的身份核对（26 号图上唯一叫「关卡」的站是 210），**不是现场用途核对**
（`REQ-0338`）。预置清单只装进一张图的第一版；以后做了关卡 210 的现场用途核对，用 FieldOps 激活新版本换成真实记录号，重启不会覆盖。

- 定义必须写 `taskTypeStations.settingsFile`，缺了就拒绝。它只能是裸文件名 `task-type-stations.map-<N>.settings.json`，
  不带目录：文件来自包、装在安装目录里，每次装包随 Host 一起替换，所以与 Host 永远同一构建。`<N>` 必须等于
  `journeyRuntime.mapId`，否则装的时候就拒绝，而不是等开运行时被 `BindingMapMismatch` 拦下。
- 文件内容与图号由 `TaskTypeStationStartupTests` 钉住（26 号图启动不报 `BindingMapMismatch`，并与 `catalog-26.json` 交叉核对）；
  覆盖层写的键与 Host 的 `TaskTypeStationPreset.SettingsFileKey` 一致、点名的文件确实由 Host 项目输出，由 `Test-ParallelInstance.ps1` 核对。
- 取货端（例如 N1-3）是 AREA 端，不进这份清单，靠站点目录与区域分配表导入解析。

**所以出厂定义里 `journeyRuntime.enabled` 与 `routeGraph.enabled` 都是 `false`**（control-server#411 审查 S2）。当时包里的站点清单只有绑 map 25 的那份（control-server#518 之后定义改为点名 26 版，见上文）；运行时开着时，站点清单的图号与 `JourneyRuntime:mapId`（26）对不上，Host 会以 `BindingMapMismatch` 拒绝启动（`TaskTypeStationConfigurationValidator.cs`），照原样装会再次半装。两个开关必须一起改：只开路网引擎、不开运行时会被校验拒绝。**打开运行时是以后单独授权的一步，前提是 26 版站点清单已经进包。**10-07 的首装用的就是这两项为 `false` 的定义。

## 清桩出口的两节配置与恢复凭据由部署链写，不再手工合入（control-server#454）

人工清桩出口要两节配置：`VehicleFaultRecovery`（Host 恢复入口开关与凭据变量名）和 `FieldOperatorRoles`（名单路径、车载端入口声明）。
以前部署链不写这两节，只能装完手工合入；首装会整份重写 `appsettings.Production.json`、重建服务的 `Environment`，手工合入的东西
就被静默冲掉，服务端只告警 2271/2272、照旧 `ORDER_HANG`。现在：

- 实例定义里必须显式写 `vehicleFaultRecovery.enabled` 与 `fieldOperatorRoles.{path, onboardClearanceEntryDeclared}`，缺了就拒绝。
  `path` 必须在 `opsRoot` 里（装包会替换安装目录）。凭据变量名**不由定义给**，覆盖层固定写 `CONTROL_SERVER_V2_FAULT_RECOVERY_CREDENTIAL`，
  定义里写 `credentialEnvironmentVariable` 会被拒——免得并行实例指到 MVP 的变量。出厂定义是第一阶段：入口关、未声明车载端入口。
- 首装、升级、回滚三条路径都跑同一个 `Set-InstanceConfiguration`：合并覆盖层并回读核对覆盖层写的**每一个**值；把凭据写进服务的
  `Environment`；名单文件不存在就建一个空名单（`{"operators":[]}`，服务端读作「没人有权限」，安全方向），已有的绝不覆盖。
- 凭据的值：安装与升级取 `deploy-config.json` 的 `faultRecoveryCredential`（控制端从 DPAPI 秘密存储填），没有就沿用服务里已有的那一条；
  回滚没有 `deploy-config.json`，只沿用已有的。**在产品脚本运行之前**就读，因为首装会重建 `Environment`。入口开着而两处都没有，
  在停任何东西之前拒绝。
- 结束时打一行 `CLEARANCE_EXIT_READINESS ...`，按服务端 `StationClearanceExit` 的规则读生效配置、`Environment` 里的变量名（不读值）
  和名单文件。两节缺失、或入口开着却没有凭据：抛 `CLEARANCE_EXIT_BROKEN`，安装算失败。出口不可用（例如第一阶段名单为空）：
  打 `CLEARANCE_EXIT_UNAVAILABLE` 警告并列原因码，不算失败。它读的是服务将读的文件，不是运行中进程的判定，服务端没有对外暴露这个状态。

## 升级与回滚前先把旅程运行时关掉（control-server#454）

产品升级脚本 `Update-ControlServerLocal.ps1` 的预检要求已装配置里 `JourneyRuntime.enabled` 为 false（`ae2f99be9` 起）：
它会用保留下来的配置拉起还没验证过的新版本，做启动、存活检查、重启、再检查，结果文件写 `journeyRuntimeEnabled=false`、
`vehicleMoved=false`；运行时开着，新版本就会在这次检查里取需求、建单。当时出厂定义的覆盖层写的是 true（control-server#411
起出厂改为 false，以后开了运行时就又是 true），所以首装之后的升级和 `-Rollback` 以前都被这道预检拒掉。

现在升级分支走 `ParallelHost.psm1` 的 `Invoke-ParallelProductUpgrade`，顺序是：

1. **派车闸门开着就拒绝**（`UPGRADE_REFUSED_DISPATCH_OPEN`）。已装配置里 `RiotCreateDispatch.enabled` 为真时，车可能在途，
   停服务等于中途停掉运行时对它的故障监看。安装脚本在最开头就查一次（在记录定义、回滚对调目录、解包之前），包装函数停服务前
   再查一次。要升级或回滚，**先用 `8005-workspace` 仓的 `remote-ops/factory-server/scripts/20-set-control-server-parallel-dispatch-gate.ps1 -State Closed`
   关闸门**（见下面「关、开派车闸门」）；脚本跑不了时才手工关，**按这个顺序**（拒绝消息里也照抄了这几步）：
   1. 停止往 FakeMesIngest 注入新需求，等 `agv02`／`agv03` 的最后一张单都 `Completed`；
   2. 编辑 **V2 的** `C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json`，把 `RiotCreateDispatch.enabled`
      改成 false。**不是 MVP 的 `C:\Program Files\8005 AGV\ControlServer\appsettings.Production.json`**，两者只差一个 `.V2`。
      用能保持 UTF-8 的编辑器改；用记事本「另存为」可能换掉编码，把中文 `agvId` 存坏；
   3. 重启服务 **「8005 AGV ControlServer V2」**，不是 MVP 的「8005 AGV ControlServer」。

   同一个开头检查（`Get-ParallelPreInstallRefusal`）还按失败即关拒绝这几种说不清的状态：
   - 服务在而已装配置不在（`INSTALLED_CONFIGURATION_MISSING`），或配置读不出来（`INSTALLED_CONFIGURATION_UNREADABLE`）。以前这次
     检查只在配置文件存在时才跑，`-Rollback` 会先对调包目录再失败；
   - **配置改了、服务还没重启**（`CONFIGURATION_CHANGED_SINCE_START`）：已装配置的修改时间晚于服务进程的启动时间。只改了文件、
     没重启时，文件里读到 false，可运行中的进程闸门还开着——正是这道检查要防的情况；
   - **服务正在启停或状态拿不准**（`SERVICE_NOT_SETTLED`）：只有 Running 和 Stopped 两种状态才判断；StartPending、StopPending、
     ContinuePending、PausePending、Paused 或读不到状态时直接拒绝，提示稍后再试，不比时间——过渡状态下进程可能还没读完配置，
     比时间不可靠；
   - 服务是 Running、却读不到进程启动时间（`SERVICE_START_TIME_UNKNOWN`），或读不到文件修改时间（`CONFIGURATION_WRITE_TIME_UNKNOWN`）。

   服务是 Stopped 时不比时间（没有在跑的进程，文件就是真相）；服务不在（首装）时整个不查。所有拒绝都在记录定义、回滚对调目录、
   解包之前，所以拒绝消息里的「Nothing was stopped or changed」是真的。回滚还要求 `<包目录>.previous\controlserver` 存在，同样在
   动手之前查。
2. 停服务，再把文件里的开关置为 false，然后调升级脚本。于是升级的检查在运行时关着时进行，它的备份和失败回退也都停在 false。
3. 升级成功后，`Set-InstanceConfiguration` 的覆盖层把定义里的值写回去——**包括派车闸门**：`RiotCreateDispatch.enabled` 恢复成
   实例定义里的值。出厂定义是 false，所以照常还是关着；定义里是 true（部署时带 `-AllowRiotCreateDispatch`）的话，升级成功后闸门会
   重新打开。手工关闸门只为了让这一次升级放行，不改变定义。

升级失败时开关留在 false（安全方向），打 `JOURNEY_RUNTIME_LEFT_DISABLED` 警告，写明文件里的值和服务状态，原样抛出。失败后
服务处在哪种状态取决于升级脚本在哪一步失败：在它自己的预检里失败（例如包清单不对、输出路径已存在），服务停在我们停下的状态；
预检之后再失败，升级脚本自己的回退会用**旧二进制加 false** 把服务拉起来。两种情况实例都不派车。

**恢复办法是重新部署当前在跑的那个 commit（或修好的新包），不要用 `-Rollback` 来恢复开关。**安装模式失败时代际对调还没发生，
这时再跑 `-Rollback`，装上的是 `.previous`，也就是更早一代，车载端版本可能对不上。

## 准入策略版本：批次 10 起是 3（control-server#545）

**先说要做什么：带批次 10（control-server#545）及之后代码的包，装到 factory01 上已有的 v2 实例时，用的实例定义里
`journeyRuntime.admissionPolicyVersion` 必须是 3。今后 factory01 上的 v2 实例用批次 10 之后的包重装时，两份实例定义
（`instance-factory01-v2.json` 与 `instance-factory01-v2.production-mes.json`）的 `admissionPolicyVersion` 都必须跟着升到
本票的值 3（以后的票再升，就是那时的值），不能沿用控制端手上那份旧定义。**本仓两份定义与 `appsettings.json` 已经一起改成 3。

反过来同样不行，而且更隐蔽：**批次 10 之前的包（例如 `e03d8e8b`）不得配版本 3 的定义。**旧包配 3 装上去当时不报错——3 比库里的
2 高，库就把 3 绑到旧包的两类种子上；之后再装批次 10 的包、配 3，同一个版本号下内容不同，判漂移、停接单（L1
`SameDirectionTaskTypeJourneyRuntimeTests.AnOldPackageInstalledUnderVersionThreeTakesTheVersionAndThisBuildThenDrifts`）。所以：

- **部署脚本 19（`8005-workspace` 仓 `remote-ops/factory-server/scripts/19-deploy-control-server-parallel.ps1`）的
  `-ControlServerRepository` 必须检出到与安装包同一个 control-server 提交**（包里 `release-manifest.json` 的
  `components.controlServer.commit`）。19 的实例定义取自这个克隆的 `scripts/parallel/`，不从包里取；克隆停在顶端、包是旧的，
  定义就是新的。要部署旧包，就为那个提交建一个 detached worktree 当 `-ControlServerRepository`。19 自己做这道核对是
  control-server#552，在那之前只能靠人核。
- **本票合入之后、批次 10 的 release 跑出来之前，不要用不带 `-RunId` 的默认方式部署。**19 不带 `-RunId` 时取
  `release.yml` 最近一次成功的产物，那时它还是批次 10 之前的包，而克隆顶端的定义已经是 3——正是上面这种错配。

为什么：服务端每一轮都把「本图每个 AREA 命名的机台站 × 本构建能执行的每个任务类型」写进库，作为站点准入（准入种子），
并和 `admissionPolicyVersion`、`admissionPolicyDeploymentId` 一起固定下来。同一个版本号下种子内容变了，库拒绝重绑，
本轮判准入策略漂移（`ADMISSION_POLICY_DRIFT`）：**已经在途的旅程照常走完，任何新需求都不再受理**——生产来源模式下正在试运行的
`STAGING_TO_WIRE` 也一样停接单（缺陷记录 `docs/defects/20260915-admission-policy-drift-halts-runtime.md`）。批次 10 让
`DIE_TO_WIRE_STAGING`、`DIE_TO_OVEN`、`WIRE_TO_OPTICAL`、`WIRE_TO_NITROGEN` 可执行（`ExecutableTaskTypes`），种子由两类变成六类，
所以版本由 2 升到 3。这与 `allowedWorkTypes` 无关：生产来源模式只允许 `STAGING_TO_WIRE`，种子照样是六类。

`AdmissionPolicyVersionGuardTests` 守着这件事：版本表逐个写出每一代的可执行集合，版本必须严格递增，本构建的集合必须等于最后
一行；协议里每个任务类型都必须明确表态可执行或不可执行；`appsettings.json` 与 `scripts/parallel/instance-*.json` 的每一份定义
都不得低于最后一行的版本，且彼此一致。它守的是仓库里同一个提交的文件，**守不住控制端拿别的提交的定义或别的包去装**——那一步
只能靠这一节。

部署标签 `admissionPolicyDeploymentId` 不换：本票没有改任何站点绑定，标签照旧是 `MAP-26-WIRE_TO_GATE-20261007`。

### 装错了是什么样、怎么判

种子只在 `JourneyRuntime.enabled=true` 时写，所以运行时关着装上去不会立刻出事，**开运行时的第一轮**才漂移。

- 看板「派车积压」里，新需求的原因码全部是 `ADMISSION_POLICY_DRIFT`，不分任务类型；
- 服务端日志有警告事件 2116（`LogAdmissionPolicyDrift`），每轮一条。它的文案是按「站点变了」写的，**本票这种情形里
  `added: none; removed: none`**——站点没变，变的是任务类型集合，原因在附带的异常文本里，二选一：
  - `Admission policy version is already bound to different content or deployment identity.`：配置的版本等于库里的版本，内容不同。
    三种来路：
    1. 用旧定义（版本 2）装了新包；
    2. 用新定义（版本 3）回滚了旧包；
    3. **定义和包都是新的，仍然漂移**：之前某次用旧包配版本 3 的定义装过（见本节开头），库里的 3 已经绑着旧包的两类种子。
  - `Admission policy version cannot move backwards.`：配置的版本低于库里已经绑过的版本（回滚，见下）。
- 核对实例实际读到的值：`C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json` 的
  `JourneyRuntime.admissionPolicyVersion`；库里已绑的版本见下面「读库里已绑的版本」。

恢复：**把定义里的版本设为库里已绑的版本 + 1**，按上面「升级与回滚前先把旅程运行时关掉」的流程重新部署同一个包。不用动库；版本
一升，下一轮就按这个包的种子重新绑定、恢复受理。**照原样把定义「改回 3」没有用**：第 3 种来路里库里本来就是 3，配 3 还是漂移。

### 读库里已绑的版本

库里 `AdmissionPolicyState` 表只有一行（`Id = 1`），`Version` 就是这个实例绑过的最高版本（版本只升不降）。v2 实例的库是
`C:\ProgramData\8005\ControlServer.V2\data\controlserver.db`。**不是** MVP 的 `C:\ProgramData\8005\ControlServer\data\controlserver.db`，
两者只差一个 `.V2`，MVP 的生产库不碰。

在 factory01 上另起一个用完就退出的 pwsh 进程只读查询，DLL 从 opsRoot 的 `sqlite-tools` 副本加载，**不要从安装目录加载**：
`Add-Type` 加载的 `e_sqlite3.dll` 在进程退出前不释放，常驻进程会锁住安装目录，之后卸载会 `ABORTED`（10-09 踩过）。

```powershell
pwsh -NoProfile -Command {
    $tools = 'D:\zhengyushao\control-server-v2-ops\sqlite-tools'
    Get-ChildItem $tools -Filter '*.dll' | Where-Object Name -NotLike 'e_sqlite3*' | ForEach-Object { Add-Type -Path $_.FullName }
    [SQLitePCL.Batteries_V2]::Init()
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new(
        'Data Source=C:\ProgramData\8005\ControlServer.V2\data\controlserver.db;Mode=ReadOnly')
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandText = 'SELECT Version, DeploymentId, ImportedAt FROM AdmissionPolicyState WHERE Id = 1'
    $reader = $command.ExecuteReader()
    while ($reader.Read()) { '{0}  {1}  {2}' -f $reader.GetInt64(0), $reader.GetString(1), $reader.GetString(2) }
    $connection.Dispose()
}
```

这段命令是按调度 10-09 起用 `sqlite-tools` 副本读 v2 库的做法写的，**本票没有在 factory01 上实跑过**；目录里的 DLL 名以实际为准。
`AdmissionPolicyAudit` 表留着每一次绑定（版本、内容哈希、时间），要看历史时查它。

### 回滚：库不跟着回滚，版本只能往上走（已实测）

`-Rollback` 换回旧包，然后按控制端**这一次传入的**实例定义重写覆盖层。库里一旦绑过版本 3（批次 10 的六类种子），旧包
（只种两类）就**既不能用 2，也不能用 3**：2 是版本倒退，库拒绝；3 已经绑着别的内容，库也拒绝。两种都判漂移、停接单。
L1 `SameDirectionTaskTypeJourneyRuntimeTests.AfterVersionThreeIsBoundTheRolledBackBuildBindsOnlyUnderAHigherVersion` 钉着这个结果。

所以回滚时：

1. 回滚用的定义副本里，把 `admissionPolicyVersion` 设为**这个实例装过的最高版本 + 1**（第一次从 3 回滚就是 4）。最高版本以
   库里为准（上面「读库里已绑的版本」），部署记录只作对照。只改那一次部署用的副本，不提交回本仓。
2. 之后再装回批次 10 及之后的包，又要**再高一个**（上例是 5）。这时仓库里的三处出厂值低于实例实际用过的值，护栏不会替你发现；
   要么部署时用副本写更高的值，要么开票把三处一起升上去。
3. 每次部署都在部署记录里写下用的版本号，回滚时才知道「装过的最高版本」是多少。

## 路径和名字只认一种写法

control-server#262 复审找到过一个严重缺陷：卸载脚本在一种很常见的写错下（JSON 里用正斜杠写路径）
会删掉 MVP 的安装根和生产数据库目录。修法是白名单，不是补黑名单：

- **路径**必须是规范写法——盘符开头、全反斜杠、没有 `.`／`..` 段、没有 `~` 短名、不是 UNC 或
  `\\?\`、每段不以点或空格结尾——而且必须**直接**位于 `C:\Program Files\8005 AGV`、`C:\ProgramData\8005`、
  `D:\zhengyushao` 三者之一下面，**目录名里带 `V2` 标记**（`.V2`、`-v2-` 这种，前后是分隔符）。
- **服务名和计划任务名**不许有 `* ? [ ]`，也必须带 `V2` 标记。`Get-Service`、`Stop-Service`、
  `Get-ScheduledTask` 都会展开通配符。
- 生产路径清单还留着，作第二道；但它必然不全，所以第一道是白名单——白名单漏了，后果是「删不掉」，
  黑名单漏了，后果是「删错了」。

卸载时服务那一步只要失败（尤其是产品卸载脚本以 production 拒绝），整个卸载立即中止，一个目录都不删。
「失败」不是靠识别各种失败方式判断的：产品卸载脚本可以不抛异常就失败（`exit 1`、`Continue` 下的
`Write-Error`、原生命令的退出码），所以 `Invoke-ParallelProductUninstaller` 反过来要求**正面确认成功**——
退出码为 0、这一次新写出的结果文件里是 `PASS` 且服务名对得上、服务确实已经不在了。缺一样就当失败。
结果文件名带 GUID，别的进程猜不到。这个确认成立还有一个前提：产品卸载脚本开头就设了
`$ErrorActionPreference = 'Stop'`，而且只在末尾写一次 `PASS`。自测对这两点有一道防回归检查，但它只认得四种违反
写法（第一句不是设 `Stop`、`PASS` 出现次数不是一次、`PASS` 不在最后三句、有 `trap`），认不出之后再改回 `Continue`、
`$PSDefaultParameterValues`、用 try/catch 吞掉错误、把 `PASS` 拆开拼接这些。产品脚本一改，要人重读一遍。
同样的检查在卸载时也会对**实际要调用的那一份**产品脚本再做一次（通常是已装包或上一代包自带的那份，不是仓库里的），
不过就中止，由 `Get-ParallelProductUninstallerPath` 负责。

关于 `sc.exe delete` 返回 1072（服务已被标记为待删除）：复审担心这会让退出码非 0、造成误报。实测不会
（`evidence/deploy/20260921-cs262-parallel-instance/review8-sc1072-probe.txt`）：产品脚本在 `sc.exe` 之后还会跑一次
`netstat.exe`，调用方拿到的是最后一个原生命令的退出码 0。这依赖两条命令的先后；哪天 `netstat` 被删掉或挪到
`sc.exe` 前面，1072 就会被判为失败、卸载中止——方向是安全的，重跑一次即可。

卸载脚本应当只经由 `Invoke-ParallelProductUninstaller` 调用产品脚本。自测里有一道**防回归护栏，不是证明**：它挡住
常见的直接调用写法（用变量、表达式、字符串或点源当命令，`Invoke-Expression`、`Start-Process`、`pwsh`、
`[scriptblock]::Create`，出现产品脚本的文件名），每种都有一段合成代码证明它认得出来。已知它挡不住的：别名和函数
定义（`Set-Alias`、`Set-Item function:`）、.NET 与 CIM 的进程接口（`[Diagnostics.Process]::Start`、
`Invoke-CimMethod`、`[powershell]::new()`）、计划任务，以及写在模块里的定义。

**删目录只有一个入口 `Remove-ParallelInstanceDirectory`**，安装和卸载都用它。它每次删之前都重新检查那个
具体路径：两道路径检查，外加「它本身不是 junction 或符号链接」。本机 pwsh 7.6.6 实测，`Remove-Item -Recurse`
碰到目录里的 junction 只删链接、不顺着删进去；但这是某个 cmdlet 今天的行为，不是这里的代码保证的，所以要删的
路径本身是链接就拒绝，并且自测把实测行为钉住。

另一个删除入口是 `Remove-ParallelInstanceDeploymentConfig`，只删控制端拷来的那个装着密钥的配置文件。它只认布局里的
路径 `<运维目录>\deploy-config.json`，而且必须是普通文件；安装脚本在开工前就拒绝别的路径。
这个文件里是 RIoT 调用密钥、MesIngest 共享密钥和（有的话）V2 恢复凭据的明文，所以从拷上服务器那一刻起，每一条退出路径都要清掉它：安装
脚本从第一个检查之前就包了一层 `try/finally`（定义被拒、还没有布局时，只删安装脚本自己目录下的 `deploy-config.json`）；
安装脚本根本没跑起来时，由控制端事后经 ssh 调用同一个函数再查一遍。删不掉就打印 `SECRET_FILE_LEFT_BEHIND`，不会静默。

除了这两个函数，这几个文件自己不应该删任何东西。自测里同样有一道**防回归护栏，不是证明**：两个脚本和两个模块用
同一个扫描函数、同一套规则，挡住常见的删除写法（删除命令及其别名、带模块名的写法、`cmd`、`robocopy`、`.Delete(`、
`ForEach-Object Delete`、用变量或表达式当命令名），例外按「函数名 + 原文」逐字列出。已知它挡不住的：别名和函数定义、
VB 的 `DeleteDirectory`、FSO 的 `DeleteFolder`、CIM，以及挪走、清空、改权限这类不叫「删」的操作（`Move-Item`
安装脚本自己要用）。

**真正保证不删错东西的是构造，不是扫描**：删前逐次复检的路径白名单与生产清单、拒删链接、服务一步的正面确认与失败
即中止、配置文件只删布局路径（或没有布局时安装脚本旁边那一个）。扫描只是让人改这几个文件时，常见写法不会悄悄绕开它们。

## 关、开派车闸门（control-server#472）

`Set-ParallelDispatchGateLocal.ps1` 在 factory01 上关或开 v2 实例的 `RiotCreateDispatch.enabled` 并重启服务，由控制端的
`20-set-control-server-parallel-dispatch-gate.ps1` 复制过去、经 ssh 执行。**真跑会停、启一个在线服务，只在用户为那一次授权后运行。**
步骤、判据与拒绝码在 `ParallelHost.psm1` 的 `Invoke-ParallelDispatchGateChange` 与 `ParallelInstance.psm1` 的
`Get-ParallelDispatchGateRefusal` 里写着；完整的中文说明在 `8005-workspace` 仓 `remote-ops/factory-server/docs/wire-to-gate-parallel-cd.md`
第 10 节。要点：

- 在途判断读 v2 自己的 SQLite 库（只读），不读看板接口：`/api/dashboard/*` 每个接口只返回在途旅程的一个子集。
- 关：任何旅程不是 `Completed` 就拒绝。开：只拒绝单已经发出或可能已经发出的旅程，「没发过」照搬 `WireToGateStore.IsNeverSentAsync`；
  闸门关着时存在的旅程都在等闸门，拒绝它们就是死锁。读不出一律拒绝。
- 服务在跑时读一次，停服后再读一次；写开关时回读开关和其余每个值（中文 `agvId`）；重启后要求进程启动时间晚于文件修改时间。
- 判据依赖的服务端事实钉在 .NET 测试 `tests/ControlServer.Tests/DispatchGatePremiseArchitectureTests.cs` 里，CI 会跑：建单在全仓只有
  `gateway.CreateAsync(` 一个调用点；走到它的只有那两条建单路径，且都在任何 `await` 和 store 调用之前先查闸门；旅程阶段的完整成员表；
  引擎读在途旅程的那一整条查询；`IsNeverSentAsync` 的定义原文。`Test-ParallelInstance.ps1` 只核对那些用例还在，另外对照 EF 模型快照
  核对读取的列名。它们是防回归，不是证明：不经这些写法的改动（换了字段名、反射、绕过网关的 HTTP）看不见。
- 脚本放在服务器上 `opsRoot\dispatch-gate\<commit>` 子目录里，不覆盖 19 号部署在 `opsRoot` 的安装、卸载脚本和模块。

## 文件

| 文件 | 做什么 |
| --- | --- |
| `instance-factory01-v2.json` | 实例定义：端口、目录、服务名、车、RouteGraph、建单闸门（`fake` 模式，出厂默认） |
| `instance-factory01-v2.production-mes.json` | 同一实例的 `production` 模式定义：读生产 MesIngest，只做 `STAGING_TO_WIRE`（control-server#535） |
| `ParallelInstance.psm1` | 定义的校验、布局（所有路径与名字的唯一来源）、部署足迹、卸载的删除顺序、唯一的删目录函数。检查全是纯函数，例外只有读路径属性的 `Test-ParallelInstanceReparsePoint` 和删目录的 `Remove-ParallelInstanceDirectory` |
| `ParallelHost.psm1` | 读写机器的辅助函数（MVP 服务指纹、调用产品卸载脚本并确认成功、把覆盖层合并进 `appsettings.Production.json` 并回读核对、只读读取旅程状态、关开派车闸门），安装、卸载与闸门脚本共用 |
| `Install-ParallelInstanceLocal.ps1` | 在 factory01 上安装／升级／回滚 |
| `Uninstall-ParallelInstanceLocal.ps1` | 在 factory01 上按部署足迹逐项卸载 |
| `Set-ParallelDispatchGateLocal.ps1` | 在 factory01 上关、开派车闸门并重启 V2 服务（control-server#472） |
| `Start-FakeMesIngestResident.ps1` | 在运维目录里给正在跑的 FakeMesIngest 重灌种子（control-server#512 之前是计划任务的动作；现在任务直接执行替身 exe，安装器灌一次，重启后要手动重灌，见 #519） |
| `Publish-FakeMesIngest.ps1` | 替身的 self-contained 发布（控制端跑） |
| `Test-ParallelInstance.ps1` | 自测，不碰任何机器 |
| `Test-FakeMesIngestScheduledTask.ps1` | 自测，**要管理员、会建一个 SYSTEM 计划任务**：用安装器的函数真把替身拉起来、灌一次种子，带一条「注册了但起不来」的反面对照。只在 vm01 跑，不在 factory01 跑；它的绿证明不了 factory01（control-server#512） |
| `Invoke-ReverseCheck.ps1` | 在真实定义文件上做的反向验证，不碰任何机器 |

## 改完这里的任何东西之后

```bash
pwsh -File scripts/parallel/Test-ParallelInstance.ps1
```

```bash
pwsh -File scripts/parallel/Invoke-ReverseCheck.ps1
```

两者都要全绿。改到 FakeMesIngest 计划任务那一层（`Get-ParallelFakeMesIngestTaskAction`、`Register-`／`Wait-ParallelFakeMesIngestTask`、`Invoke-ParallelFakeMesIngestSeed`）时，再在 vm01 上以管理员跑一次 `Test-FakeMesIngestScheduledTask.ps1 -FakeMesIngestZip <Publish-FakeMesIngest.ps1 的 zip>`。它们不在 CI 里（本仓 CI 跑的是 .NET 测试套件），所以没人会替你跑。

**孪生脚本**：MVP 那套的对应物是 `8005-workspace` 仓的 `remote-ops/factory-server/scripts/15-deploy-control-server.ps1`
与 `control-server/Install-ControlServerRemote.ps1`。两边刻意分开，所以一边的修复不会自己到达另一边——
改到机器层面的东西（清理通配符、防火墙规则、共享环境变量）时，去看另一边。
