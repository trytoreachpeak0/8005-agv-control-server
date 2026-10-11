# cs#566 续场二收工（2026-10-11 21:41～21:45）

调度转达用户「收工」，按调度清单逐项收尾，每项回读。全程不动车。

| 项 | 结果 | 出处 |
| --- | --- | --- |
| 1. 闸门 | `RiotCreateDispatch.enabled=false`（21:36:22 关闸，收尾前后回读都是 false） | `01` |
| 2. 测试等待 | `stationDepartureWaitTimeout` 00:01:30 → 00:05:00，`cargoHoldingTimeout` 00:02:00 → 00:05:00，回读确认 | `01` |
| 3. 故障恢复 | `VehicleFaultRecovery.enabled` true → false；V2 服务注册表 `Environment` 删除 `CONTROL_SERVER_V2_FAULT_RECOVERY_CREDENTIAL`，回读该键不存在（只记键名） | `01` |
| 4. 开测前的值 | 服务端 `FieldOperatorRoles.onboardClearanceEntryDeclared` true → false；与开测前备份 `bak-20261011200735-before-clearance-entry` 逐键比对，只差第 2 项的两条等待时长。agv02 `recoveryResumeEnabled`、`unableToChargeEntryEnabled` 恢复为 false，用开测前备份覆盖，SHA-256 前 16 位 `153B41F00CF7DA40` 与备份相同。agv03 两项一直是 false，未改 | `01`、`04`、`05` |
| 5. 名册与策略 | 充电名册导入空名册，version 3，`entryCount=0`、`emptyRoster=true`；策略 activeVersion=1（30/80）。残留：未恢复的桩暂停 0 行、车辆资格暂停 0 行、人工充电保持 0 行、未结束充电周期 0 行；`StationExclusivities` 只剩 agv02 占着等待点 216（OCCUPIED，空闲返回后的占用，离开时才释放），211 无记录 | `01` |
| 6. 车载端 | agv02 用 `10-start-onboard-stack -Stop` 停客户端与模拟器；agv03 之前已停。回读两车 `SQCD.Agv.Wpf`、`SQCD_8005AGV_Simulator` 进程数都是 0。IO 指向两车都仍是模拟器 `127.0.0.1:1502`（`ioModule`），未切回真模块 | `02`、`03`、`04` |
| 7. V2 | 停服，启动类型 Manual。MVP `8005 AGV ControlServer` 收尾前后都是 Running、pid 15624、Auto，未碰 | `01` |

## 凭据怎么处理的（只记名字，不记值）

- 恢复凭据 `V2FaultRecoveryCredential`：今晚生成，存在控制端 DPAPI 存储；曾写入 V2 服务注册表 `Environment`，收工已删。控制端存储里的那一份保留，未下发到任何车。
- 每车出发安全凭据（`OnboardSafetyCredential:<vehicleKey>`，cs#600）：今晚两车段生成，存在控制端 DPAPI 存储，同时在 V2 服务注册表 `Environment`（`CONTROL_SERVER_V2_ONBOARD_SAFETY_CREDENTIAL_RIOT59`／`_RIOT60`）与两车车载端配置里。这是两车实例运行所需，收工不删。
- 工号：用的是存储里已有的 `OperatorId`，库里审计行照录；本证据里出现处都已替换为 `<redacted>`。现场人员名单 `field-operator-roles.json` 仍登记今晚那 1 人（R-11、R-13），因为服务端清桩入口开关已关，名单不再被用到；开测前的备份在 `cs566b/field-operator-roles.json.bak-20261011200649-before-cannot-charge`。

## 最后一次只读 RIoT（21:43:01）

- agv01：电量 7%，`NO_CHARGE`，`emergencyState=CAN_NOT_RECOVER`，位置 0，无未结束单。由用户处理，本会话未碰。
- agv02：216，73%，空闲，无未结束单。
- agv03：位置 0，55%，无未结束单（用户手动使用中）。
