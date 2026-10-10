# cs#186 第一步：真 RIoT 上核实不带 mapJson 的地图列表接口

- 时间：2026-09-28 23:07（CST），一次性只读，用户经调度（Coordinator 8）批准，仅这两条请求。
- 目标：`http://172.19.206.222:8888`，路由走 `Wi-Fi`（`route.txt`），不经 Clash TUN。
- 请求（`requests.jsonl`）：
  1. `GET /api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson` → 200，`code=0`，2101 字节；
  2. `GET /api/imap/v1/mapInfo/26` → 200，`code=0`，264292 字节。
- 没有读任何车辆卡片（不涉及 `agv01`），没有建单、发命令，也没有经过 ControlServer。原始响应体没有保存，脚本只抽取了下面这些字段（`fields.json`）。

## 结论

| 问题 | 结论 | 依据 |
| --- | --- | --- |
| 列表接口在真 RIoT 上能用吗 | 能用：200，`code=0`，`result` 是 8 个元素的数组 | 读到的 |
| 列表项带不带 `mapJson` | 不带：8 项的键全集是 `description floor gmtCreate gmtUpdate id mapError name source state syncState url`，里面没有 `mapJson`；响应只有 2101 字节，而 `mapInfo/26` 有 264292 字节 | 读到的 |
| 26 号图的名称与 `mapInfo/26` 一致吗 | 一致：两边都是 `老厂前线new_wk`，UTF-8 `E88081E58E82E5898DE7BABF6E65775F776B`，按 Ordinal 比较结果为 `same` | 读到的 |

## 顺带看到的

- 8 张图的 `gmtUpdate` 全是 `2026-09-28 17:49:34`（读到的）。所有图在同一秒被更新，更像是 RIoT 侧的一次批量同步，而不是 8 张图各自被编辑过（推的）。所以 `gmtUpdate` 不能拿来判断「这张图改没改」，改名检测只比名称。
- `mapInfo/26` 从 09-19 #179 读到的 258404 字节变成了 264292 字节（读到的），说明图的内容在这之间又改过（推的）。这不在本票范围内：站点级变化由 #162 负责。
