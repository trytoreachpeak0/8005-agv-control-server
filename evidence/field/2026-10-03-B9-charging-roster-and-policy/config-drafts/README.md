# 并行实例的清桩出口配置草稿（cs#411）

这里是 10-08 并行实例 `appsettings.Production.json` 要补的两节，**只是草稿**：没有任何脚本读这里，本 PR 也不改出厂配置。
由用户在 factory01 上、并行实例装好之后手工合进**并行实例安装目录**（`scripts/parallel/instance-factory01-v2.json` 的
`installRoot`）里的 `appsettings.Production.json`。不碰 MVP 的安装目录与配置。

## 为什么是手工合入

部署链今天带不上这两节（读代码，`fp/v2-impl@46148e35`）：

- 并行实例定义是白名单（`scripts/parallel/ParallelInstance.psm1` 的 `$script:AllowedKeys`），写进这两节会被校验拒绝；
  覆盖层 `New-ParallelInstanceConfigurationOverlay` 只写 `MesIngest`、`JourneyRuntime`、`RouteGraph`、`RiotCreateDispatch`、`RiotForeignOrderCancel`。
- **首次安装**走 `scripts/Install-ControlServerLocal.ps1`，它整份重写 `appsettings.Production.json`（第 326 行起），并且 `New-Service` 时重建服务的
  `Environment` 注册表值；**升级**走 `scripts/Update-ControlServerLocal.ps1`，原样保留已装的 `appsettings.Production.json`（第 164 行）。
  `-Rollback` 会重跑产品安装脚本（`Install-ParallelInstanceLocal.ps1` 第 383 行起）。

所以**每次首装或 `-Rollback` 之后，这两节与凭据都要重核一遍**。冲掉时不报错，服务照常启动，表现是：启动日志告警 2272、每个周期告警 2271，
「充不上」只按 `ORDER_HANG` 处理、清桩没有出口；中断／无进展只告警不隔离。让部署链自己带上这两节由调度另开票处理。

## 文件

| 文件 | 用途 |
| --- | --- |
| `appsettings.Production.phase1.draft.json` | **10-07 用这一份**：两个开关都关，只配名单路径 |
| `appsettings.Production.phase2.draft.json` | 第二阶段：两个开关都开。前置条件见下，**不在 10-07 用** |
| `field-operator-roles.template.json` | R-11／R-13 名单模板，`FieldOperatorRoles:Path` 指向它填好后的副本 |
| `roster-template-check.txt` | 名单模板留空时的实测读法（见下） |

10-07 也可以先**完全不合入** `VehicleFaultRecovery` 一节（代码默认 `enabled=false`，`VehicleFaultRecoveryOptions.cs:25`），效果与第一阶段那份相同。
两种都可以，清单里两种都写了。

## 各项说明

### `VehicleFaultRecovery`

- `enabled`：打开时同时映射三类 Host 入口——车辆故障恢复（cs#299）、站点独占释放（cs#419）、充电桩与车辆充电资格的恢复与人工清桩确认
  （`VehicleFaultRecoveryEndpoints.cs:61-71`、`ChargingStationEndpoints.cs`）。**不只是充电入口。**
- 路由只在启动时映射一次，清桩出口判定也在启动时取一次（`StationClearanceExit.cs` 的 `_hostEntryOffered`），所以**改这个开关要重启服务**。
- `credentialEnvironmentVariable`：**写变量名，不写值。**名字用 `CONTROL_SERVER_V2_FAULT_RECOVERY_CREDENTIAL`，不用代码默认的
  `CONTROL_SERVER_FAULT_RECOVERY_CREDENTIAL`，与并行实例对共用变量一贯另起 V2 名的做法一致（调度 10-03 同意）。值由用户生成，写进**并行实例服务**的
  注册表 `Environment`（与 MesIngest 共享密钥同一种放法），不写机器范围、不写仓库。
- `enabled=true` 而变量没有值时，`VehicleFaultRecoveryOptionsValidator` 让服务**拒绝启动**（`ValidateOnStart`，`Program.cs:153-156`）。

### `FieldOperatorRoles`

- `path`：名单文件的绝对路径。**不要放在安装目录里**（每次装包会被替换），服务账户要能读。名单每次判定现读，改名单不用重启。
- 名单格式（`FieldOperatorRoleRoster.cs`）：`{"operators":[{"operatorId":"…","roles":["R-11"]}]}`。R-11 设备／电气维护人员，R-13 AGV 运维／调度管理员。
  人工清桩认 R-11 或 R-13，现场确认充不上只认 R-11。
- `onboardClearanceEntryDeclared`：部署方声明车上开着清桩入口。服务端从协议读不出来，只能靠这条声明，而且**全车队共用一个值**。

### 名单模板为什么留空而不写占位串

模板的 `operatorId` 是空串，`fillIn` 只是说明（服务端读名单时忽略未知字段）。理由是实测（`roster-template-check.txt`，用
`fp/v2-impl@46148e35` 的 `FieldOperatorRoleRoster` 直接读三份文件）：

- 留空原样部署：`AnyoneHolds(R-11/R-13)=False`，服务端判「没人有权限」，清桩出口不可用，照旧 `ORDER_HANG` 并告警。**忘了填是安全的。**
- 写成 `<R11-operatorId>` 这类占位串原样部署：`AnyoneHolds=True`，服务端以为有具名的 R-11，会把「充不上」推进到清桩，而车上的真实操作员号匹配不上，
  每次确认都被拒——车进了清桩就出不来。

填的时候：每台车上实际配置的车载操作员号各写一条，并且必须写 `operatorId`，不能只写角色（cs#411 10-01 评论）。
