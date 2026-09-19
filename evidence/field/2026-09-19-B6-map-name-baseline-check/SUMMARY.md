# 26 号图地图名、车辆 CurrentMap 与 mapIdentity 只读核实

票：`trytoreachpeak0/8005-agv-control-server#179`。本目录只有核实记录，不改产品代码与配置。

## 结论

1. **三个字面值不是同一个。**RIoT 地图对象 26 的 `name` 是 `老厂前线new_wk`；配置 `JourneyRuntime:mapIdentity` 的出厂值是
   `老厂前线new`，比前者少了结尾的 `_wk` 三个字节。所以「拿 `mapIdentity` 当 Map 名称基线」在今天的数据下不成立：
   照做的话每一轮比对都判为改名，26 号图全部任务类型被暂停。
2. **agv02、agv03 两台车都离线**（车辆卡片 `status=2`，在线是 `1`），卡片上残留的 `CurrentMap` 是 `老厂前线new`，与
   `mapIdentity` 逐字节相同、与 26 号图的 `name` 不同。这是离线车上一次上报留下的值，**不能当作「车在 26 号图上会报什么」**。
   在线车在 26 号图上报的 `CurrentMap` 本次**未取到**：两台备用车没开机，`agv01` 按批准范围不读。
3. **26 号图现在是 208 个站点，不是 `#67` 记录的 209 个。**地图对象的 `gmtUpdate` 是 `2026-09-18 22:01:54`，晚于 `#67`
   那次读取（`2026-09-18 21:27:46`，见 [`2026-09-18-B4-site-prerequisites/02-station-split-check.md`](../2026-09-18-B4-site-prerequisites/02-station-split-check.md)）。
   也就是说那次读取之后地图又被改过一次。少了哪个站本次没有记录（脚本只记站点数与「关卡」站，没有存站点清单），要知道需要再读一次，见「遗留」。
4. **「关卡」站在 26 号图上的 `stationId` 是 `210`**，只有这一个同名站，与配置 `gateStationRiotId=210` 一致。

## 授权

| 项 | 内容 |
| --- | --- |
| 批准人 | 用户本人，2026-09-19，在调度会话中批准「批准只读核实」，调度记在票上 `issuecomment-5739243447` |
| 范围 | 只从控制端向 `172.19.206.222` 发只读 GET：`mapInfo/26`、26 号图站点目录、agv02 与 agv03 的车辆卡片；不建单、不发命令、不起 ControlServer、不读 agv01；一次性 |
| 实际执行 | 在范围内，共 4 个 GET，见下表；`mapInfo/all` 没有用到 |

## 路由核对

请求前在控制端执行 `Find-NetRoute -RemoteIPAddress 172.19.206.222`，结果（[`real-riot/route.txt`](real-riot/route.txt)）：

```
checkedAt: 2026-09-19T13:05:34.7984388+08:00
host: 172.19.206.222
InterfaceAlias: Wi-Fi
InterfaceIndex: 19
IPAddress: 172.19.162.241
```

走 `Wi-Fi` 网卡，不经 Clash 虚拟网卡，响应是真实的。脚本在网卡名含 `Clash` 时会直接拒绝发请求。
请求用 `-NoProxy` 发出，不经系统代理。

## 请求清单

全部是 `GET`，原文见 [`real-riot/requests.jsonl`](real-riot/requests.jsonl)（一行一次请求，含时间、状态、字节数与 SHA-256）。
基地址 `http://172.19.206.222:8888`，凭据取自控制端环境变量 `CONTROL_SERVER_RIOT_CALL_API_KEY`，不落任何文件。

| 时间（+08:00） | 请求 | HTTP | 业务码 | 字节 |
| --- | --- | --- | --- | --- |
| 13:05:39.720 | `/api/imap/v1/mapInfo/26` | 200 | `0` 成功 | 258404 |
| 13:05:41.188 | `/api/imap/v1/mapInfo/stations/26` | 200 | `0` 成功 | 97224 |
| 13:05:41.447 | `/api/task/vehicles/getVehicleInfoByDeviceKey?key=BROKERX-f38975561adf46ccb1d2f23833c7d0e4`（agv02，RIoT `id` 59） | 200 | `0` 成功 | 865 |
| 13:05:41.497 | `/api/task/vehicles/getVehicleInfoByDeviceKey?key=BROKERX-7daca4ee91da498d8026c68b7b941127`（agv03，RIoT `id` 60） | 200 | `0` 成功 | 831 |

车辆身份按 `remote-ops/fleet.md` 的 `deviceKey` 取。脚本对 agv01 的 `deviceKey` 硬拒绝，并且只放行上表三类路径。

原始响应体没有入库：地图对象带 `mapJson`（整张图，约 250 KB），站点目录带坐标。抽出的字段见 [`real-riot/fields.json`](real-riot/fields.json)。

## 三个字面值并排

逐字节比较（`StringComparison.Ordinal`），十六进制是 UTF-8 编码：

| 编号 | 来源 | 原文 | UTF-8 十六进制 |
| --- | --- | --- | --- |
| ① | `mapInfo/26` 的 `MapInfoObject.name` | `老厂前线new_wk` | `E88081E58E82E5898DE7BABF6E65775F776B` |
| ② | agv02 车辆卡片 `currentMap`（离线，`status=2`） | `老厂前线new` | `E88081E58E82E5898DE7BABF6E6577` |
| ② | agv03 车辆卡片 `currentMap`（离线，`status=2`） | `老厂前线new` | `E88081E58E82E5898DE7BABF6E6577` |
| ③ | `src/ControlServer.Host/appsettings.json` 的 `JourneyRuntime:mapIdentity`（`fp/v2-impl@b740d319`） | `老厂前线new` | `E88081E58E82E5898DE7BABF6E6577` |

| 比较 | 结果 | 差在哪 |
| --- | --- | --- |
| ① 对 ③ | **不同** | ① 多出结尾 `5F776B`，即 `_wk` |
| ② 对 ③ | 相同 | 但 ② 是离线车的残留值 |
| ② 对 ① | 不同 | 同上，差 `_wk` |

地图对象 26 的其他字段：`id=26`、`state=activated`、`syncState=asynced`、`gmtUpdate=2026-09-18 22:01:54`。

两台车卡片的其他字段：`enable=true`、`procState=IDLE`、`currentPosition=0`。

## 顺带核实

| 项 | `#67` 记录（2026-09-18 21:27:46） | 本次（2026-09-19 13:05:41） |
| --- | --- | --- |
| 26 号图站点数 | 209 | **208** |
| 「关卡」站 `stationId` | 210 | 210（唯一一个同名站） |

响应的 SHA-256 也变了：`#67` 那次 `05ef58a7…`（97678 字节），本次 `333d4f88…`（97224 字节）。

## 这些结果能说明什么、不能说明什么

- 能说明：RIoT 上 26 号图今天的名字不是配置里的 `mapIdentity`；把两者直接比较会判为不同。
- 能说明：`关卡` 仍是 210，批次6-02、批次6-04 的出厂配置与切生产配置可以照用这个 id。
- 不能说明：一台在 26 号图上运行的在线车会报出什么 `CurrentMap`。离线卡片上的 `老厂前线new` 可能是车上一次跑 25 号图时留下的，
  也可能是车载地图文件自己的名字，与 RIoT 地图对象的 `name` 本来就不是一回事。两种解释本次都分不开。
- 不能说明：26 号图少的是哪个站、它是不是某个区域号唯一的取货站。

## 遗留（需要另行批准）

1. **少了哪个站。**要再读一次 `mapInfo/stations/26`，与 `#67` 的 [`map-26/stations.csv`](../2026-09-18-B4-site-prerequisites/map-26/stations.csv) 逐行比对。
   如果少的是机台站点，`#67` 的分区归属表与「每个区域号只落在一个站点上」的结论要复核。本次批准是一次性的，没有做。
2. **在线车在 26 号图上报的 `CurrentMap`。**要等 agv02 或 agv03 开机上线再读车辆卡片；读 agv01 属 Ask first 第 2 类，要另问。
   这个值决定切到 26 号图时 `mapIdentity` 该填什么：`VehicleDynamicFactsCriterion` 用它与车辆 `CurrentMap` 做 `Ordinal` 比较，填错就挡住全部派车。

## 假 RIoT 试跑

真跑之前，同一个脚本在 L2 的假 RIoT（`tools/ControlServer.FakeRiot`，loopback，种子改成 26 号图与 agv02、agv03）上跑通过，
输出见 [`dry-run-fake-riot/`](dry-run-fake-riot/)。**假 RIoT 不提供 `GET /api/imap/v1/mapInfo/{mapId}`，返回 404**，按票面要求只记下、不改假 RIoT。
试跑时发现 `pwsh -File` 把逗号分隔的 `-DeviceKeys` 当成一个字符串传入（agv01 拦截因此会被绕过），脚本已改为先拆分再检查，并用 agv01 的 key 验证了拦截生效。

## 文件

| 文件 | 内容 |
| --- | --- |
| `Invoke-MapNameBaselineCheck.ps1` | 只读核实脚本 |
| `real-riot/route.txt` | 路由核对 |
| `real-riot/requests.jsonl` | 请求清单 |
| `real-riot/fields.json` | 抽出的字段、十六进制与比较结论 |
| `dry-run-fake-riot/` | 假 RIoT 试跑的同样三份输出 |
