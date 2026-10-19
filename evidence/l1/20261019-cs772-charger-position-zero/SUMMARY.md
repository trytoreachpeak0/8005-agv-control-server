# cs#772 证据：车在桩上周期外充电、RIoT 报 currentPosition=0 时桩仍被预留

## 坐实：0 落成了什么

代码路径（全部只读核对）：

1. RIoT `GET /api/task/vehicles/getVehicleInfoByDeviceKey` 返回的 `currentPosition` 在 SDK 生成模型里是 `int?`
   （riot-sdk `fp/v2-facade`，`RIoT.Sdk.Generated/TaskApi/Models/U673au5668u4ebau5361u7247u8fd4u56deObject.cs:298`，`GetIntValue()`）。
2. Facade `TaskClient.GetVehicleCardAsync` 把它原样放进 `VehicleCard.CurrentPosition`。
3. `src/ControlServer.Infrastructure/Adapters/HttpRiotMovementGateway.cs:304`：`CurrentStationId: vehicle.CurrentPosition`。
4. 修复前 `ChargerOccupancy.cs` 只判 `CurrentStationId is not int`：0 是 int，于是记成 `standing[车] = (地图, 0)`——
   既不进 unknown，也不算站在任何桩上。

所以 `currentPosition=0` 落成了「站在 0 号站」，没有任何一层把它当「没有当前站」。

现场对照（`field/`）：

- `field/01-...ndjson`：factory01 v2 实例日志 `controlserver-20261018*.ndjson`（只读），05:20～06:19Z 之间
  `Runtime.Charging` 来源的全部记录只有一条：06:11:23.111Z `2240 LogCommitted`，agv02（老厂前线新多仓位2）→ 211，电量 39%。
  同一时段没有 `CHARGER_OCCUPANCY_UNKNOWN`、`CHARGER_OCCUPIED_BY_VEHICLE` 的任何记录。说明那一轮 agv01 的位置被读成了
  「有站号、且不是 211」，与「站在 0 号站」一致。车辆 GET 读数本身不写日志，agv01 那一刻的原始读数无法从日志直接看到；
  这一条结论靠代码路径、日志旁证与票面的现场读数三者一致。
- `field/02-...txt`：10-18 凌晨巡检的 RIoT 原始读数。车在 `PROCESSING_ORDER`（行驶中）时一律报 `st=0`，
  例如 `bat=70 batState=NO_CHARGE st=0 proc=PROCESSING_ORDER move=MT_RUNNING`。

## 修法为什么不是「0 一律记 unknown」

票面第 2 步的做法是「0 按没有当前站处理、记进 unknown」。按上面第二条读数，车只要在路上就报 0；三辆车几乎总有一辆在跑，
那样做等于充电分配常年答 `CHARGER_OCCUPANCY_UNKNOWN`，全车队分不到桩。所以：

- 0（含负数）视为「不在任何站」：不挡任何桩，也不是未知。
- 0 且 `batteryState=CHARGING`：充电只发生在桩上，却说不出是哪一个——记 unknown 事实
  `VEHICLE_CHARGING_WITHOUT_STATION`，所有桩答 `CHARGER_OCCUPANCY_UNKNOWN`（现有原因码，看板已有中文说明，不新增原因码）。

## L1 红绿

| 文件 | 内容 |
| --- | --- |
| `red/01-allocation-tests-09bd8a2a.txt` | 用例先提交（`09bd8a2a`），实现仍是 `c0389b68`：分配侧 4 条失败，现状 `CHARGING_COMMITTED`。其中 `AReservedChargerIsNotSentTo...` 在 `55b3beef` 改名改写为 `AChargerCommittedBeforeAnotherVehicle...`（见 red/03） |
| `red/02-failed-cycle-release-09bd8a2a.txt` | 失败周期留下的预占：另一辆车「充电中、位置 0」时被释放了（期望保持） |
| `red/03-departure-recheck-55b3beef.txt` | 出发前复核两条（按既有撤回口径改写后），只在这一轮把 `ChargerOccupancy.cs` 临时还原成修复前：都建出了去 211 的单 |
| `green/01-0689122f.txt` | 修复后 `ChargingAllocationTests` 66/66、`ChargingExecutionTests` 64/64 |

全量走 CI，见 PR。
