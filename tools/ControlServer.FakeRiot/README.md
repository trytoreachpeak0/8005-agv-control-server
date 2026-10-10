# ControlServer.FakeRiot

RIoT RCS 的测试替身。ControlServer 把它当作真的 RIoT 来读，测试脚本则通过一个 loopback 控制面
决定它看到什么。

这个工具是
[`8005-agv-program/docs/wire-to-gate-test-automation.md`](https://github.com/trytoreachpeak0/8005-agv-program/blob/main/docs/wire-to-gate-test-automation.md)
第 3 节「缺口 1」的答案：2026-09-03 现场挖出的三个缺陷里有两个都需要**让车动起来再停下**，而当时
整个测试环境里没有任何东西能做到这件事，只能站在真车前面一层层手工排查。

## 跑起来

```bash
dotnet run --project tools/ControlServer.FakeRiot
```

默认监听 `127.0.0.1:58008`。**没有 `appsettings.json`**——种子值就写在 `FakeRiotSeed` 的默认值里，
再放一份到配置文件就成了两处真相；而且那个文件会落进每一个引用本项目的输出目录，把 Host 自己的
`appsettings.json` 盖掉（这不是假设，加进去当场打挂了两条断言）。要改配置就用命令行开关或环境变量：

```bash
dotnet run --project tools/ControlServer.FakeRiot -- \
  --FakeRiot:port=58008 \
  --FakeRiot:Seed:vehicleKey=BROKERX-0c20ff0600d644869a6a80c186065d85 \
  --FakeRiot:Seed:mapId=25
```

默认种子复现的就是 2026-09-03 现场那台车与那张图：map 25、关卡站 210、一台已绑定的车停在关卡、
电量 80%。

让 ControlServer 连过来，只要把它的 `RIoT:baseUrl` 指到这里：

```json
{ "RIoT": { "baseUrl": "http://127.0.0.1:58008" } }
```

`callApiKey` 随便给一个非空值，本工具不校验凭据——它也不该校验，凭据不是它要证明的东西。

## 强制边界

- **非 loopback 监听默认拒绝启动。**一个会回答「车在哪、订单到没到」的东西如果厂区网能访问到，
  那边某个程序就可能把它的回答当成 RIoT 的。要覆盖得显式设 `FakeRiot:allowNonLoopbackListen`。
- **控制面不提供任何「让车动」「把订单标记完成」的业务指令。**它只能说明 RIoT *观测到* 什么。
  派车仍然只能由 ControlServer 通过 `POST /api/order/v1/add/byDefaultMissions` 发起——这条边界和
  `slots-simulator` 那条「HTTP 不提供开锁」是同一条：绕过被测方自己摆出终态，测的就只是脚本自己。
- **模拟器 PASS 不代表现场合格。**这里没有真实 RCS、没有真车、没有交通管制。

## 两个面

### RIoT 数据面

ControlServer 的 SDK 实际调用的六个端点，响应形状与 `HttpRiotMovementGatewayTests` 钉住的一致，
而那些形状来自 2026-09-03 从真实 RCS 抓到的响应。

| 方法 | 路径 |
| --- | --- |
| `GET` | `/api/task/vehicles/getVehicleInfoByDeviceKey?key=` |
| `GET` | `/api/task/v1/task/getVehicleInfo/{deviceKey}` |
| `GET` | `/api/imap/v1/mapInfo/stations/{mapId}` |
| `GET` | `/api/order/v1/orderRecord/detailByUpperId/{upperId}` |
| `GET` | `/api/order/v1/orderRecord?filterByState=…` |
| `POST` | `/api/order/v1/add/byDefaultMissions` |

三处不规则的地方是**故意保留**的，因为真实 RCS 就是这样：

- `getVehicleInfo` 那条**没有** `code`/`result` 外壳，返回裸对象；
- QUEUEING 状态的订单在 `executeVehicleKey` 里报 `"--"` 占位符，不是真车 key（BC-ORDER-012，
  把它当真 key 读已经坑过一次）；
- 重复建单返回业务码 `0610008` 而不是 HTTP 409（BC-ORDER-004），ControlServer 据此转去对账。

### 控制面

`http://127.0.0.1:58008/control/v1`，机器契约见 `openapi.json`（运行时 `/control/v1/openapi.json`）。

形状照抄 `slots-simulator` 的
`docs/EXTERNAL_AUTOMATION_CONTROL_API.md`，这样一套编排器可以同时驱动两边，不用记两套约定。
这套规则的实现在 `tools/ControlServer.TestDoubles`，三个替身共用一份——四份幂等规则的拷贝就是
四个走样的机会：

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| `GET` | `/health` | 存活与当前故障模式 |
| `GET` | `/snapshot` | 车辆、订单、地图站点、故障模式、`mapStationReads` |
| `GET` | `/openapi.json` | 机器契约 |
| `POST` | `/reset` | 新一轮，`revision` 回到 1 |
| `PUT` | `/vehicle` | 位置、`procState`、速度、运动/控制/急停状态、电量、锁、订单占用 |
| `PUT` | `/orders/{upperId}` | 推进 `orderState`、绑定执行车辆、改终点站 |
| `PUT` | `/maps/{mapId}/stations` | 替换站点目录 |
| `PUT` | `/faults/http` | `Normal` / `NoResponse` / `ServerError` / `Delay` |
| `PUT` | `/chargers` | 登记充电桩与耗电速率（control-server#402），见下面「充电」 |
| `PUT` | `/charging/faults` | 按车或按单注入充电故障（control-server#402），见下面「充电」 |
| `PUT` | `/faults/absent-order-reads` | 接下来 `count` 次按 upperId 读一张不存在的单时回 503 而不是 404（control-server#375）；只打这一种读，不动数据面别的接口，也不推进 `revision`；`/snapshot` 的 `absentOrderReadFaults` 给出剩余次数与打中了哪些 upperId |

每个响应都带 `schemaVersion`、`instanceId`、`runId`、`revision`、`observedAt`；写命令必须带
`runId` 与 `commandId`，可带 `expectedRevision`。写响应还带 `changed`、`replayed`、
`appliedRevision`。

### `mapStationReads`：让否定判据不必 sleep

快照里的 `mapStationReads` 是 Map 站点目录被读了多少次的单调计数。
`JourneyRuntimeEngine.ExecuteOnceAsync` 每一轮开头都读一次它——**包括 journey 已经 Blocked、它
再没别的事可做的那些轮**——所以这是唯一一个「运行时又有机会了」的可观测量。L2 场景要说「这台车
不再受理任何新需求」，等的就是它（`Wait-L2Iterations`），而不是 sleep 一段拍脑袋的时间。

**它刻意不走 command engine，因此不推进 `revision`。**每次轮询都动一下 `revision`，会让
`expectedRevision` 对真正携带变化的命令彻底失去意义。计数在故障注入之前累加：一个在等轮次的场景
要知道运行时确实又来过，哪怕它这次拿到的是一个注入的失败。

处理顺序与模拟器一致：

1. `runId` 不是当前轮次 → 409 `RUN_ID_MISMATCH`
2. `runId + commandId` 已存在且内容指纹相同 → 重放首次结果，`replayed=true`，**即使
   `expectedRevision` 已经过期**（这正是重试一条响应丢失的命令的意义）
3. 已存在但内容不同 → 409 `COMMAND_ID_CONFLICT`
4. 否则再校验 `expectedRevision` → 不一致 409 `REVISION_CONFLICT`
5. 执行一次，缓存结果

`reset` 是唯一特例：先查实例级回执再校验 `runId`，所以响应丢失后用原 `commandId` 重试会拿到同一个
新 `runId`，而不是又开一轮。

**`revision` 只在可观测状态真的变化时递增。**把一个字段设成它已经是的值返回成功、`changed=false`、
`revision` 不动；查询、健康检查、409、重放都不递增。

### 一个例子：车动起来再停下

```bash
RUN=$(curl -s localhost:58008/control/v1/snapshot | jq -r .runId)

curl -s -X PUT localhost:58008/control/v1/vehicle -H 'content-type: application/json' -d "{
  \"runId\": \"$RUN\", \"commandId\": \"cmd-depart\",
  \"procState\": \"RUNNING\", \"movementState\": \"MT_RUNNING\", \"speed\": 0.8
}"

curl -s -X PUT localhost:58008/control/v1/vehicle -H 'content-type: application/json' -d "{
  \"runId\": \"$RUN\", \"commandId\": \"cmd-arrive\",
  \"procState\": \"IDLE\", \"movementState\": \"MT_FINISHED\", \"speed\": 0, \"currentPosition\": 12
}"
```

## 充电

control-server#402 加的，给批次 9 的充电 L2 与契约测试用。**不登记桩、不注入故障，一切照旧**：没有桩就没有充电，
电量就是 `PUT /vehicle` 写进去的那个静态数（默认 80），单段移动单的每个应答逐字节不变
（`FakeRiotChargingTests.WithNothingRegisteredASingleMoveOrderIsAnsweredByteForByteAsBefore` 钉着）。

### 照着真 RIoT 的哪些事实做的

- 充电单是 `move(桩) + act(78, 1, 0)`；离桩时 RIoT 在下一张单队首自动插 `act(78, 2, 0)`（Round 25）。
- 站配了 `user_define_properties.enter_exit` 时，RIoT 把去这个站的单展开成 `move(进出点) → move(桩) → act(78)`。
  26 号图上 211「充电点1」配的是 `"212"`（MVP 缺陷记录 2026-09-12；批次 9 计划第七节第 1 条，09-29 只读核过）。
- 订单详情里 act 段的字段：`actionId`、`actionParam1`、`actionParam2`、`resultCode`、`resultStr`、`missionState`，
  `mapId` 与 `destination` 是 0。成功时 `missionState = 2`、`resultCode = 0`；充不上时 `missionState = 1`、
  `resultCode = 407802`（Round 24 `S1b-detail-final.json`、Round 25 `S2-final.json`）。`resultStr` 照抄 RIoT
  那句「未知类型错误,导致订单挂起:错误编码为:N」——成功时也带「挂起」字样，以 `resultCode` 为准。
- **到桩不等于充电。**只有带 `act(78,1,0)` 的单被推到 5 才报 `CHARGING`；只有移动的单推到 5，车停在桩上也是
  `NO_CHARGE`。这是 MVP 09-12 缺陷第三层的形状，假件若到桩就报 `CHARGING` 就测不出它。

move 段在详情里仍然只有 `type`、`mapId`、`destination` 三个字段。

### `PUT /chargers`：登记桩

```json
{
  "runId": "…", "commandId": "…",
  "chargers": [
    { "mapId": 26, "stationId": 211, "enterExitStationId": 212,
      "chargeIntervalSeconds": 60, "chargePercentPerInterval": 1, "expandDeparture": false }
  ],
  "dischargeIntervalSeconds": 0, "dischargePercentPerInterval": 0
}
```

| 字段 | 缺省 | 含义 |
| --- | --- | --- |
| `mapId`、`stationId` | 必填 | 哪张图上哪个站是桩 |
| `enterExitStationId` | 空 | 进出点。配了就展开，站目录里这个站的 `user_define_properties` 报 `{"enter_exit":"212"}` |
| `chargeIntervalSeconds`、`chargePercentPerInterval` | 必填 | 每多少秒涨几个百分点，封顶 100 |
| `expandDeparture` | `false` | 离桩那张单是否也在 `act(78,2,0)` 之后经过进出点。缺省不展开：真 RIoT 上离桩单的形状没有实测 |
| `dischargeIntervalSeconds`、`dischargePercentPerInterval` | 0、0 | 不在充电的车每多少秒掉几个百分点，最低 0。两个都是 0 就是电量不变 |

整表替换，给空表就是撤掉所有桩。换速率之前，每辆车的电量先按旧速率结算到此刻，新速率不会倒着作用到已经过去的时间上。

### 充电怎么走

1. 建单时，去已登记且配了进出点的桩的 move 前面插一段 `move(进出点)`；`endStationNo` 取**最后一段 move** 的站
   （以前取最后一段，尾部是 act 时会变成 0）。真 RIoT 对没配进出点的站建充电单会拒（10008），假件照收，只是不展开——
   去没配进出点的桩的单是否该被拒，由服务端或场景自己判。
2. 带 `act(78,1,0)` 的单经 `PUT /orders/{upperId}` 推到 5：act 段记成功，车（`executeVehicleKey`，没绑时用
   `appointVehicleKey`）停在 act 前最后一段 move 的站上。那个站是已登记的桩，车就报 `CHARGING`，电量按桩的速率涨。
   act 在没登记的站上完成时车不变——真 RIoT 在那种情况下怎么回答没人测过，假件不替它编。
3. 这辆车在桩上时再给它建单，那张单队首自动插 `act(78,2,0)`；这张单推到 3 或 5 时车离桩，回 `NO_CHARGE`，电量停在
   那一刻的值（有耗电速率就从那里开始掉）。「在桩上」指充电动作完成过、之后还没离桩，**不看此刻是不是 `CHARGING`**：
   充电中断或无进展的车，下一张单队首照样插 `act(78,2,0)`。离桩先于充电：在桩上的车拿到一张新的充电单，从 1 直接推到
   5 也是先离旧桩、再上新桩，最后报 `CHARGING`；这张新单若挂在充电动作上（停在 9，无论是注入的还是场景自己推的 9），
   队首的 `act(78,2,0)` 已经执行过，车同样算离了旧桩。
4. 电量读的是注入的时钟（`FakeRiotHost.TryCreate(args, clock)`，可执行文件用系统时钟），**时间流逝不推进
   `revision`**：数值在每次读的时候算出来。`PUT /vehicle` 仍可随时写 `battery`，写进去的值覆盖模拟值，模拟从它接着走；
   写 `batteryState` 则结束模拟中的充电，照写进去的字面值报。

占桩互斥不由假件判：两辆车都完成了去同一个桩的充电单，两辆都会报 `CHARGING`。排队与预占是服务端的事。

### `PUT /charging/faults`：注入

`vehicleKey` 与 `upperId` 二选一。只改带了的字段，设了就一直有效，直到再改。

| 字段 | 效果 | 对应需求 |
| --- | --- | --- |
| `startOutcome: "CannotCharge"` | 带 `act(78,1,0)` 的单推到 5（或 9）时停在 9（HANG），act 段 `resultCode = 407802`、`missionState = 1`，车始终不报 `CHARGING`，也不算在桩上 | `REQ-0174` |
| `startOutcome: "HangOnly"` | 同样停在 9，act 段缺省没有 `resultCode`（`null`），全程没有 407802 | `REQ-0175` 的负向 |
| `hangResultCode: N` | 挂起时 act 段带这个码（给 B9-08 造「HANG＋非 407802 的码」）。对 `HangOnly` 和场景自己推到 9 的单都生效；不许是 407802（那是 `CannotCharge`）。`clearHangResultCode: true` 撤掉 | `REQ-0175` |
| `startOutcome: "Normal"` | 撤掉上面两种 | |
| `interruptAtPercent: N` | 充电中电量到 N 时自己回 `NO_CHARGE`，不需要离桩单；开始充电时已经 ≥ N 就立刻中断。**一直有效**：之后每次充电到 N 都会再断，要让车恢复正常充电，先 `clearInterrupt: true` 撤掉 | `REQ-0285` 已确认中断 |
| `noProgress: true` | 报 `CHARGING`，电量不动 | `REQ-0285` 已确认无进展 |
| `batteryUnreadable: "Battery"`／`"BatteryState"`／`"Both"`／`"None"` | 车卡片里去掉 `battery`、`batteryState` 或两个都去掉（键不出现，不是 `null`）。整张卡读不到用 `/faults/http` | `REQ-0287` 电量遥测丢失 |

按单（`upperId`）只收 `startOutcome` 与 `hangResultCode`，整条替换，而且优先于那辆车的设定；其余几种是车的属性。故障从下命令那一刻起作用，
之前的时间按旧设定结算。

以上状态全部按车独立：种子里 `AdditionalVehicleKeys` 的每辆车都能各自充电、各自注入，「1 桩 3 车」就是这样搭的。

### 快照

`/snapshot` 的 `vehicles` 报的是读取那一刻的模拟电量；新增的 `charging` 带出已登记的桩、耗电速率、每辆有模拟的车
（`battery`、`batteryState`、`docked`、所在的桩、各项故障）以及按单设的 `startOutcome`。

## 测试

黑盒测试在 `tests/ControlServer.Tests/FakeRiotTests.cs`：起真实 Kestrel，用**生产的**
`HttpRiotMovementGateway` 去读，控制面走 HTTP 驱动。用捷径断言只能证明捷径，证明不了替身。
端到端的用法见 `scripts/l2/`——那里这个替身和真 ControlServer 一起跑完整条链路。跑法与全仓一致：

```powershell
dotnet test .\tests\ControlServer.Tests\ControlServer.Tests.csproj -c Release
```
