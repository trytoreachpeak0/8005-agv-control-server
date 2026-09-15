# ControlServer.EmergencyDrill

批次 2 票 19「W1 空载急停演练」**缩减版**的受控现场工具。**只对 agv02，只用这一次。**

## 为什么有它

产品路径发不出这次急停：8005 只有在 RIoT 把一张在途单报成 `orderState=4`（FAILED）时才会升级到
`triggerEmergency`，而 RIoT 的 FAILED 没法人为造出来。2026-09-14 产品负责人（用户）裁定改做缩减版，
并**批准对 `vendor/8005-agv-program/docs/riot-call-allowlist.md` 第 1.5 节做一次性破例，仅限本次演练、
仅限 agv02**：

1. 工具经白名单内的 `POST /api/order/v1/add/byDefaultMissions` 为 agv02 建一张 map 25 两站之间的移动单，让空载车走起来；
2. 车在两站之间行驶时，发送**且只发送一次** `triggerEmergency`；
3. 观察 RIoT 确实停车，读急停闩锁 `emergencyState`（`CAN_RECOVER`／`CAN_NOT_RECOVER`）；
4. 急停锁着、车停稳时，以 `CMD_ORDER_CANCEL` 取消演练单——只作清理，**不代替停车**；`CAN_NOT_RECOVER` 时同样取消；
5. 演练单进入终态、现场人员确认停稳、无货、仓门已关（代替白名单要求的车载端确认）后，**仅当** `CAN_RECOVER`
   时调一次 `cancelEmergency` 并回查 `emergencyState=OK`；`CAN_NOT_RECOVER` 时禁止解除，转 RIoT 人工。

**收尾顺序是先取消演练单、再解除急停**（用户同日裁定，取代最初「先解除后取消」的写法）：先解除的话，
闩锁一清 RIoT 可能让车接着开往终点，而现场人员此时正在车旁。工具在两头都用守卫强制这个顺序。

白名单文档本身不改。「8005 只发一次」在产品路径上的证明是 L2 场景 `emergency-stop-single-trigger`
（服务端 CI 三连跑，`evidence/l2/20260914-ci-34815736635-emergency-stop-single-trigger-01..03`），不是这个工具。

工具放在 `tools/` 下，是因为 `RiotCallAllowlistArchitectureTests` 只检查 `src/` 的五个产品程序集；它仍然只调白名单里点名的端点。

## 复用产品适配器

工具引用 `src/ControlServer.Infrastructure`，按 `ControlServer.Host/Runtime/RiotSdkRegistration.cs` 的方式
构造 `RiotSession`（`CallApiKey` 取自环境变量 `CONTROL_SERVER_RIOT_CALL_API_KEY`），再直接用产品的两个适配器：

| 用途 | 适配器方法 | RIoT 端点 |
| --- | --- | --- |
| 车辆卡片 | `HttpRiotMovementGateway.ReadVehicleAsync` | `GET /api/task/vehicles/getVehicleInfoByDeviceKey` |
| 运动采样（产品的 `ReadMotion` 判读） | `HttpRiotMovementGateway.SampleMotionAsync` | `getVehicleInfo` ＋ 车辆卡片 |
| 车辆安全事实（有无非终态单） | `HttpRiotMovementGateway.ReadVehicleSafetyAsync` | `getVehicleInfo` ＋ `GET /api/order/v1/orderRecord` |
| 站点目录 | `HttpRiotMovementGateway.ReadMapStationsAsync`（内部 `ListStationsStrictAsync`） | `GET /api/imap/v1/mapInfo/stations/25` |
| 建单 | `HttpRiotMovementGateway.CreateAsync`（内部 `CreateMoveOrderAsync`） | `POST /api/order/v1/add/byDefaultMissions` |
| 按 upperId 回读 | `HttpRiotMovementGateway.ReconcileByUpperIdAsync`（内部 `FindOrderByUpperIdAsync`） | `GET /api/order/v1/orderRecord/detailByUpperId/{upperId}` |
| 急停／解除 | `HttpRiotOrderCommandGateway.IssueEmergencyCommandAsync` | `POST /api/device/v1/command/sync/service/{deviceKey}/triggerEmergency`／`cancelEmergency` |
| 闩锁 | `HttpRiotOrderCommandGateway.ReadEmergencyStateAsync` | `getVehicleInfo` |
| 取消演练单 | `HttpRiotOrderCommandGateway.IssueOrderCommandAsync(Cancel)` | `POST /api/task/v1/order/command/{orderId}`（`CMD_ORDER_CANCEL`） |
| 暂停演练单（`hold`） | `HttpRiotOrderCommandGateway.IssueOrderCommandAsync(Hold)` | `POST /api/task/v1/order/command/{orderId}`（`CMD_ORDER_HELD`） |

唯一绕过适配器的是 `preflight` 的延迟探测：它直接调 Facade 的 `GetVehicleCardAsync`（同一个白名单端点），
因为适配器会把读失败变成「未连接」的卡片，延迟探测需要看到失败本身。

## 网络：`UseProxy=false` 与 `preflight`

`remote-ops/fleet.md` 记着控制端到 RIoT 走 Clash TUN，探测和请求结果都不可信。两层处理：

- 工具的 HTTP 传输 `UseProxy=false`：不走 `HTTP(S)_PROXY`／`ALL_PROXY` 环境变量，也不走 WinINET 系统代理。
  **这绕不开 TUN**——TUN 在网络层接管报文。
- 所以有 `preflight`（只读），`create-move`、`hold` 与 `trigger` 要求**本 run、本机、60 分钟内**有一次通过的预检：
  - 记本机主机名；用 iphlpapi `GetBestRoute`（与 `Find-NetRoute` 读同一张路由表）查到 RIoT 地址的下一跳和出口网卡；
  - 下一跳或网卡地址落在 `198.18.0.0/15`、出口网卡名称／描述含 `clash`／`mihomo`／`wintun`、或域名解析到
    `198.18.0.0/15`（fake-ip）→ `VIA_TUN`；查不到路由 → `UNKNOWN`。两者都不通过，除非给
    `--accept-tun "<理由>"`（理由写进证据与摘要的异常一节）；
  - 连续 5 次读车辆卡片，记往返耗时的中位数与最大值；任一次失败或最大值超过 1000 ms 不通过；
  - 顺带记下一个默认 HttpClient 本会用的系统代理，以及哪些代理环境变量被设置了（只记变量名）。

运行主机由用户定：控制端、厂区服务器上的独立控制台进程、或厂区网内别的机器都行，**以预检结论为准**。

## 守卫一览

| 命令 | 拒绝条件 |
| --- | --- |
| 所有命令 | 未知选项／重复选项（用法错误，退出码 2）；同一证据目录已有另一条命令在跑（`drill.lock`）；`--riot-base-url` 与 init 时不一致；状态文件里的 key 不是 agv02、map 不是 25 |
| `init` | key 不是 agv02（agv01、agv03 及其它一律拒）；自测 key 未带 `--fake-riot`，或带了但地址不是字面回环 IP；map 不是 25；目录已存在且非空（已用过的 run 目录永不复用） |
| `create-move` | 本 run 已建过单或已发过令；预检未通过／不是本机／超过 60 分钟；车未连接、未启用、`procState≠IDLE`、不在 `老厂前线new`、速度非 0、运动读数不是 `NotMoving`、卡片上有 `orderTaskId`、RIoT 有该车的非终态单（或查不清）、电量 < 30、不在站点上（`currentStationId≤0`）；终点等于当前站或不在实时站点目录里；`emergencyState≠OK` |
| `watch-moving` | 只读，不拒；连续 2 个采样「两站之间运动中」才报 `READY TO TRIGGER`：运动读数 `Moving`、**上报速度严格大于 `MinimumMovingSpeed`=0.05**（RIoT 自己的速度单位）、`currentStationId` 为 0／空；到站或超时报 `WINDOW_MISSED`。接单起步时车在起点站原地旋转，这段会一直等过去 |
| `hold` | 本 run 已记下暂停（**先写状态文件再调用**，崩溃也算用掉）；本 run 已发令、已取消单或已解除；key 不是批准的；预检不合格；没有带 orderId 的演练单；现读闩锁不是 `OK`；现采样不是「两站之间运动中」（判法同 `watch-moving`，含速度 > 0.05）。**没有任何放行选项**。发出后每 300 ms 读一轮订单、闩锁、运动采样，一直看到观察窗（`--observe-seconds`，默认 20，3–120）结束，订单进入终态才提前结束；记下首次读到 `orderState=7`（HELD）的耗时、停稳判定（闩锁 `OK` 时 `MT_RUNNING` 不算静止）与耗时、产品读数是否 `NotMoving`、依次出现的 `movementState`。调用未被拒且读到 7 报 `OK`，否则 `NOT_CONFIRMED`（退出码 3，不再发第二次） |
| `trigger` | 本 run 已记下发令（**先写状态文件再调用**，崩溃也算用掉）；key 不是批准的；预检不合格；没有演练单；发令前闩锁不是 `OK`；发令前现采样不是「两站之间运动中」（判法同 `watch-moving`，含速度 > 0.05）**且**本 run 的 `hold` 没有读到订单 HELD——`hold` 读到 `orderState=7` 时守卫以 `-- or: drill order confirmed HELD earlier in this run (hold-then-emergency)` 放行并记 `afterHold=true`，`hold` 发了但没读到 7 时不放行；`--allow-stationary` 可放行，并记入证据；**`--allow-stationary` 仅限自测**：run 不是「自测 key + `--fake-riot` + 字面回环地址」时带它即拒绝，什么都不发、不记发令 |
| `cancel-order` | 本 run 已取消过；**没有发令记录**；**本 run 已解除过**；没有 orderId；现读闩锁没有锁着（不是 `CAN_RECOVER`／`CAN_NOT_RECOVER`）；现采 3 个样本（跨度 ≥1 s）不能证明停稳。订单已是终态则不发命令。拒绝文案固定带 `CMD_ORDER_CANCEL is cleanup after the stop, never a substitute for it` |
| `release` | 本 run 已解除过；没有发令记录；**本 run 没有 cancel-order，或演练单没有观察到终态**（提示先 cancel-order）；`--field-confirmed` 不是 `"<姓名> stopped,empty,doors-closed"`；现读闩锁不是 `CAN_RECOVER`（`CAN_NOT_RECOVER` 明确提示转 RIoT 人工）；现采 3 个样本不能证明停稳 |
| `summarize` | `SUMMARY.md` 已存在（现场记录是手填的，不覆盖） |

**取消单没成功时怎么办**：`cancel-order` 若被 RIoT 拒绝，或 20 秒内没看到订单进入终态，返回
`NOT_CONFIRMED`（退出码 3），文案是「STOP: report to the user. Do NOT switch to releasing the emergency stop first」。
此时 `release` 一直拒绝——**停下回报用户，不要改用先解除的顺序**。

**停稳判据**：连续至少 3 个采样、跨度至少 1 秒，每个采样 `speed=0`、`movementState` 已上报，且满足下面一条；
`currentMap` 与 `currentStationId` 不变，读数失败打断连续：

- `movementState` 不是 `MT_RUNNING`；或
- `movementState` 是 `MT_RUNNING`，但**该采样时急停闩锁已知锁着**（`CAN_RECOVER`／`CAN_NOT_RECOVER`）。`cancel-order`、`release`
  用采样前刚读的那次闩锁；`trigger` 的观察循环每轮先读闩锁再采样，用本轮那次读数。闩锁 `OK` 或没读到时 `MT_RUNNING` 一律不算静止。
  证据里记 `stillWhileLatchedRunning`（`drill-state.json` 的发令／取消单／解除记录、`timeline.jsonl` 的 `stopVerdict` 行、守卫明细），
  用到这一条时 `SUMMARY.md` 的判定依据与异常一节都会写明。

这比产品 `ReadMotion` 的 `NotMoving`（只认 `MT_FINISHED`／`MT_PAUSED`）宽；产品是否也读成 `NotMoving` 另记一栏。
车辆卡片不带坐标，「位置不变」只能按站点号判。

**2026-09-15 现场发现**（`evidence\field\20260915-W1-agv02-reduced-emergency-drill`），上面两处判法因此修改：

- agv02 接单后**没有离开 210 站，只是在站上原地旋转**，RIoT 却一直报 `movementState=MT_RUNNING`、`speed=0`、`currentStationId=0`。
  旧判法据此报了 `READY TO TRIGGER` 并发了令。所以 `MT_RUNNING` 不等于在走，`currentStationId=0` 也不等于已离站，真正的判别是速度
  （手动驾驶时读到过 0.349）。现在 `watch-moving` 会一直等过起步旋转，直到速度 > 0.05。
- 发令后 2.2 秒闩锁 `CAN_RECOVER`，此后 20 多秒每个采样都是 `speed=0`、`MT_RUNNING`、站 0、订单仍在执行：急停锁着、单还没取消的车
  持续报 `MT_RUNNING`。旧判法要求非 `MT_RUNNING`，`trigger` 报不出停稳，`cancel-order`／`release` 也永远过不了停稳守卫，于是加了上面第二条。

## 输出、退出码与证据

每条命令先打几行给人看的结果和逐项守卫（`[ok]`／`[NO]`），最后一行是一个 JSON 对象。

| 退出码 | 含义 |
| --- | --- |
| 0 | 做成（`watch-moving` 是 `READY`） |
| 1 | 被拒、窗口错过、预检未通过——**什么都没发** |
| 2 | 用法错误 |
| 3 | 调用已发出，但回读没有证实结果——**这条命令不会再发第二次**，看 `status`、回报用户／转人工 |

证据目录（`init` 创建，只增不改）：

| 文件 | 内容 |
| --- | --- |
| `drill-state.json` | 本 run 做过什么；每一版都追加到 `state-history.jsonl` |
| `commands.jsonl` | 每条命令一行（选项、守卫、结论）；每次 RIoT 写调用发出前另记一行 `phase=SENDING` |
| `timeline.jsonl` | 全部观测：卡片、运动采样、闩锁、订单、站点、预检路由与延迟 |
| `wire.jsonl` | 每个 HTTP 请求的方法、主机、路径、状态码、耗时；**不记请求头** |
| `SUMMARY.md` | `summarize` 生成：两条判定（有 `hold` 时另加「OrderHold 受理且订单进入 HELD」「HELD 后车辆停稳」两条）、取消单与解除结论、破例原文、现场记录（待填）、命令记录、调用计数、预检、异常 |

API key 只从环境变量读，从不打印、从不写入证据；所有写入证据与 stdout 的文本还会把 key 的值替换成
`***REDACTED***` 作为第二道墙。

## 现场操作顺序

证据目录按票 19 落 `evidence\field\<日期>-W1-<描述>\`，必须是新目录。下面 `<EXE>` 是
`tools\ControlServer.EmergencyDrill\bin\Release\net8.0\win-x64\ControlServer.EmergencyDrill.exe`，
`<RUN>` 是证据目录，`<RIOT>` 是 `http://172.19.206.222:8888`。运行前本会话要有
`CONTROL_SERVER_RIOT_CALL_API_KEY`（不要回显它）。

| # | 命令 | 性质 |
| --- | --- | --- |
| 1 | `<EXE> init --evidence <RUN> --device-key BROKERX-f38975561adf46ccb1d2f23833c7d0e4 --map-id 25 --riot-base-url <RIOT>` | 只建目录 |
| 2 | `<EXE> preflight --evidence <RUN>` | 只读 |
| 3 | `<EXE> status --evidence <RUN> --stations` | 只读；据此选起终点（避开 agv01 的路段）。车必须已连接、在 `老厂前线new`、闩锁 `OK`，否则第 4 步会被拒 |
| 4 | `<EXE> create-move --evidence <RUN> --to <终点站 id>` | **会让车移动——逐次授权** |
| 5 | `<EXE> watch-moving --evidence <RUN> --timeout 120` | 只读；看到 `READY TO TRIGGER` 立即进行第 6 步 |
| 6 | `<EXE> trigger --evidence <RUN>` | **发 `triggerEmergency`——逐次授权**（本 run 最多一次） |
| 7 | `<EXE> status --evidence <RUN>` | 只读；目视确认车已停下 |
| 8 | `<EXE> cancel-order --evidence <RUN>` | **发 `CMD_ORDER_CANCEL`——逐次授权**；急停锁着时取消演练单。`NOT_CONFIRMED` 时停下回报用户，不做第 9 步 |
| 9 | `<EXE> release --evidence <RUN> --field-confirmed "<姓名> stopped,empty,doors-closed"` | **发 `cancelEmergency`——逐次授权**；只在演练单已终态、闩锁 `CAN_RECOVER` 时放行；`CAN_NOT_RECOVER` 时工具拒绝，转 RIoT 人工 |
| 10 | `<EXE> summarize --evidence <RUN>` | 写 `SUMMARY.md`，再由现场补填第三节 |

## 先暂停再急停（issue control-server#63，2026-09-15 批准）

2026-09-15 现场发现：急停锁着、演练单仍在执行的车持续报 `movementState=MT_RUNNING`、`speed=0`，产品
`HttpRiotMovementGateway.ReadMotion` 把它读成 `Moving`，产品因此永远证明不了停车。而产品真正的停车路径是先
`OrderHold`（`CMD_ORDER_HELD`），停车证不出来才升级 `triggerEmergency`。RIoT 对 HELD 订单、以及 HELD 后再急停的订单报什么，没人知道。

同日产品负责人（用户）在对话中批准，**仅限 issue control-server#63、仅限 agv02**，做一次后续运行：一张移动单；车在两站之间行驶时发一次
`CMD_ORDER_HELD`；对已 HELD 的订单发一次 `triggerEmergency`；之后照旧 `CMD_ORDER_CANCEL`、`cancelEmergency`。

`hold` 读到 `orderState=7` 之后，同一 run 的 `trigger` 不再要求车在两站之间行驶（车此时应已停下），发令记录 `afterHold=true`；
`hold` 发了但没读到 7 时不放行。`cancel-order`、`release` 的守卫不变。第 1–3 步同上表（新证据目录、`init`、`preflight`、`status --stations`），之后：

| # | 命令 | 性质 |
| --- | --- | --- |
| 4 | `<EXE> create-move --evidence <RUN> --to <终点站 id>` | **会让车移动——逐次授权** |
| 5 | `<EXE> watch-moving --evidence <RUN> --timeout 120` | 只读；看到 `READY TO TRIGGER` 立即进行第 6 步（此时发的是暂停，不是急停） |
| 6 | `<EXE> hold --evidence <RUN>` | **发 `CMD_ORDER_HELD`——逐次授权**（本 run 最多一次）；看满 20 秒观察窗，打印 held／stopped／ms 与 `movementState` 序列。`NOT_CONFIRMED` 时停下回报用户 |
| 7 | `<EXE> status --evidence <RUN>` | 只读；run 行应为 `hold=Accepted`；目视确认车已停下 |
| 8 | `<EXE> trigger --evidence <RUN>` | **发 `triggerEmergency`——逐次授权**（本 run 最多一次）；守卫行应为 `[ok] … -- or: drill order confirmed HELD earlier in this run (hold-then-emergency)` |
| 9 | `<EXE> status --evidence <RUN>` | 只读；目视确认车仍停着 |
| 10 | `<EXE> cancel-order --evidence <RUN>` | **发 `CMD_ORDER_CANCEL`——逐次授权**；`NOT_CONFIRMED` 时停下回报用户，不做第 11 步 |
| 11 | `<EXE> release --evidence <RUN> --field-confirmed "<姓名> stopped,empty,doors-closed"` | **发 `cancelEmergency`——逐次授权**；条件同上表第 9 步 |
| 12 | `<EXE> summarize --evidence <RUN>` | 写 `SUMMARY.md`：多出两条暂停判定，异常一节记下 HELD 后与对 HELD 订单急停后的 `movementState` |

## 自测

```powershell
pwsh .\tools\ControlServer.EmergencyDrill\Test-EmergencyDrillAgainstFakeRiot.ps1
```

在本机回环的空闲端口（避开 L2 端口段）起 `ControlServer.FakeRiot`，只对它跑正向全流程
（`trigger → cancel-order → release → summarize`）、「闩锁不来 → CAN_NOT_RECOVER」反向流程、「取消单迟迟不到终态」
流程、2026-09-15 现场形态（原地旋转不算窗口；闩锁锁着时 `speed=0` + `MT_RUNNING` 算停稳，闩锁 `OK` 时不算）、先暂停再急停（`hold` 读到 HELD 后 `trigger` 放行停着的车；车没在行驶、第二次 `hold`、发令后 `hold` 都拒绝；`hold` 未证实 HELD 或本 run 没有 `hold` 时停着的车 `trigger` 拒绝）与 `init` 离线守卫，断言写到 `-EvidenceRoot`（默认 `%TEMP%` 下新目录）的 `assertions.json`。FakeRiot 只记录急停与
订单命令、不模拟后果，闩锁、停车、取消单、解除的状态由脚本在看到调用落地后从控制面写出来。
**自测 PASS 不代表现场合格**，也永远不连真实 RIoT。
