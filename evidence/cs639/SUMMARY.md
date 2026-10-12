# cs#639 证据摘要：开机 MT_NA 经模式与抱闸判停稳

## 问题

车刚开机时 RIoT 报 `movementState=MT_NA`。车载安全投影（`HttpRiotMovementGateway.Safety()`）只认
`MT_FINISHED`，于是车载端 `vehicleStopped=false`、`unknownPresent=true`，会话停在 `DEPARTURE_UNSAFE`，
不派任何单，必须有人先在 RIoT 上手动把车开一段（cs#566 续场三，2026-10-12）。

## 改法的依据

用户 2026-10-12 陈述（原话）：

> MT_NA确实不能判定为停稳，但是车子除了正常移动时可以移动（RIOT或者matrix下达指令），处于手动模式或解抱闸时也可以移动，其他情况均无法移动，所以只要判断其在不在手动模式和解抱闸状态就行了

实验室事实（读到的，8005-agv-program `rcs/riot-behavior-lab/`）：

- `MT_NA` 是上电后到第一个移动任务之前的状态（原始量 `moveTaskNo=0`；第 22、30、33～36 轮）。
  它本身不说明车静止：第 44 轮有一条 `MT_NA`、`MODE_MANUAL`、速度 0.43、两次采样间位移约 9 m 的样本。
- `breakSwitchState=MOVABLE` 是抱闸抱住，`UNMOVABLE` 是解抱闸（第 22 轮，Q-031 注明「命名与直觉相反」）。
  解抱闸时 `mode` 仍是 `MODE_AUTO`，所以模式与抱闸两项缺一不可。
- `getVehicleInfo` 的 `vehicle.mode` 取值 `MODE_AUTO`／`MODE_MANUAL`／`UNKNOWN`（第 6 轮样本、SDK 生成层枚举）。

## 真车只读核对

`read-mode-brake.ps1`，只调 `GET /api/task/v1/task/getVehicleInfo/{deviceKey}`，不读 agv01，不调写接口，
密钥只从环境变量取、不进输出。结果在 `01-agv02-agv03-mode-brake.txt`（2026-10-12 12:09）：agv02、agv03
都是 `MODE_AUTO`、`MOVABLE`、`MT_FINISHED`、速度 0、`IDLE`、`processingOrder=false`、`ON_LINE`。
同日 11:43 的第一次读数相同。结论：加上「必须自动模式」之后，正常停着的车不会被卡住。

## 判据改动

- 新原因码 `RIOT_MODE_NOT_AUTO`：`mode` 必须恰好是 `MODE_AUTO`，对所有运动状态生效（含 `MT_FINISHED`）。
  缺字段、`UNKNOWN`、SDK 不认识的值（读成 null）都不放行。
- `MT_NA` 只在 `MODE_AUTO` 且 `MOVABLE` 时不记 `RIOT_MOVEMENT_NOT_FINISHED`，其余条件照旧全部要满足。
  这样放行的 `STOPPED` 的 source 是 `RIOT_BEHAVIOR_LAB_R41_MT_NA_AUTO_BRAKED`。
- 速度不为 0 或 `MT_RUNNING` 仍判 `MOVING`，与模式无关。
- `ReadMotion`（REQ-0247 停稳证明）不动。
- 作用范围：`Safety()` 是所有车辆安全读取共用的判据，不只车载安全投影。开机 `MT_NA` 放行与「必须自动模式」同样作用到：
  车载安全投影（`OnboardVehicleSafetyEndpoints`）；经 `NonBusinessDepartureGate.RiotStandstillGapsAsync` 的充电分配
  （`ChargingAllocator`）与自建单重建（`JourneyRuntimeEngine.OwnOrderRebuild`）；充电前的停稳证明
  （`JourneyRuntimeEngine.Charging` 的 `ProvenStoppedWithoutOrderAsync`）；空闲返回（`JourneyRuntimeEngine.IdleReturn`）；
  等待点到达结算（`WaitingPointArrivalSettlement`）。`git grep` 核过只有这六处调用。

## 剩余风险（前提在哪里失效）

1. RIoT 或 matrix 下发移动指令后、`movementState` 离开 `MT_NA` 之前，车可能已经在动。现行判据里
   `procState`、`processingOrder`、`hasNonFinalOrder` 都兜不住：单机去站点时 `procState=IDLE`、
   `processingOrder=false`（读到的，第 38 轮 M1），单机任务也不是 RIoT 订单（推的）。只有 `movementState`
   变成 `MT_RUNNING` 才判 `MOVING`。这个窗口对 `MT_FINISHED` 同样存在（同一份样本里也是从 `MT_FINISHED`
   直接跳到 `MT_RUNNING`），是现行判据本来就有的风险，不是本次新开的口子。实验室采样间隔约 1.3 秒，窗口长度未测。
2. `mode` 读数过期：车离线时字段会冻结（第 32 轮），只靠 `ON_LINE` 那一项挡住。
3. 用户陈述之外的移动来源，本次判据不覆盖。
4. `mode` 不是 JSON 字符串（数字、布尔、对象、数组）时，此前 SDK 生成层会抛异常，这里记 `RIOT_READ_FAILED`；
   SDK 0.2.0-fp.5 起读成 null，这里记 `RIOT_MODE_NOT_AUTO`。两者都让车保持未知（riot-sdk#3）。

## 验证

- 手动模式挡住自建单重建，走真实判据：`OwnOrderRebuildTests.ARebuildWaitsWhileTheVehicleIsInManualModeThroughTheRealSafetyPredicate`
  让车辆安全读取经真的 `HttpRiotMovementGateway` 读 FakeRiot 的车，`MODE_MANUAL` 时重建等车、记 `RIOT_MODE_NOT_AUTO`，切回
  `MODE_AUTO` 后建单。网关不记 `RIOT_MODE_NOT_AUTO` 时只有这一条红（原来按字符串注入原因码的那条照样绿）。
- 车载安全接口原样回传网关的 source：`OnboardVehicleSafetyEndpointsTests.TheGatewaysSourceIsPassedThroughAsGiven`，接口把 source
  写死时变红。
- 钉包来源测试堵空子：`RiotSdkPackageProvenanceTests.TheCheckedVersionIsTheVersionTheBuildConsumes`。
- 定向测试全过（判据、车载安全接口、假 RIoT、自建单重建、`NonBusinessDeparture`、架构测试、钉包来源、RIoT 调用白名单），
  正式包 0.2.0-fp.5。
- 变异 8 条全部被抓住：去掉模式原因码、MT_NA 分支去掉抱闸、去掉模式、MT_NA 单独放行、模式改成只排除
  MANUAL、退回旧判据、source 不区分、去掉抱闸原因码。

## 不受影响

- MVP 线（`ControlServer_MVP`）是另一条分支，本次不改。
