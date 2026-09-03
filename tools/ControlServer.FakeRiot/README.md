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

## 测试

黑盒测试在 `tests/ControlServer.Tests/FakeRiotTests.cs`：起真实 Kestrel，用**生产的**
`HttpRiotMovementGateway` 去读，控制面走 HTTP 驱动。用捷径断言只能证明捷径，证明不了替身。
端到端的用法见 `scripts/l2/`——那里这个替身和真 ControlServer 一起跑完整条链路。跑法与全仓一致：

```powershell
dotnet test .\tests\ControlServer.Tests\ControlServer.Tests.csproj -c Release
```
