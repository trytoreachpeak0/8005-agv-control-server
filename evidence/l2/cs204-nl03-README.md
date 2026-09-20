# `L2-NL-03` 的四次对照（control-server#204，条目 9）

票面条目 9 只要求在「车在跑」的 PUT 之后加 `Wait-L2Iterations -Count 2`，并预期这样就能让
「不看车在不在动就采信到站」的缺陷版本变红。**实跑下来不能**，原因和取样时机无关，所以这里多做了一步。

## 挡在前面的不是运动状态，是订单状态

`JourneyRuntimeEngine.CheckArrivalAsync` 判到站分两道门：

1. `exactOrder` —— 订单必须是 Terminal 且 `Success`，`OrderId`、`VehicleKey`、`MapId`、目的站都对得上；
2. `trusted` —— 车 `IDLE`、`Speed == 0`、`OrderTaskId` 空、位置在目标站、车载端报 `VehicleStopped`，等等。

场景原来在「车在跑」那一刻取样，那时订单是 `orderState = 3`，**第一道门就没过**。
于是「服务端会不会看车在不在动」这个问题根本轮不到被问——判据必然绿，与服务端怎么实现无关。

## 四次运行

| 目录 | 场景写法 | 服务端 | `L2-NL-03` | 说明 |
| --- | --- | --- | --- | --- |
| `cs204-nl03-before-clean` | 只加了 `Wait-L2Iterations` | 干净 | PASS | — |
| `cs204-nl03-before-defect-still-green` | 同上 | **缺陷** | **PASS** | 这一格是本节的论据：判据看不见那个缺陷 |
| `cs204-nl03-after-clean` | 再插一拍「单已成功、位置已到、车还在动」 | 干净 | PASS | — |
| `cs204-nl03-after-defect-red` | 同上 | **缺陷** | **FAIL**，`AwaitingPickupArrival` → `AwaitingLoadResult` | 只有这一条红，另外 8 条全绿 |

缺陷是从 `trusted` 里同时删掉四个与运动有关的条件（`ProcState == "IDLE"`、`Speed == 0`、
`OrderTaskId` 为空、`onboard.VehicleStopped`），其余条件一个不动——所以红点指的就是
「服务端不再要求车停稳」这一件事。红的那次实际值是 `AwaitingLoadResult`：服务端在车还在动的时候
采信了到站，一路推到了装货。

## 补的那一拍是什么

RIoT 把订单标成成功、位置也报到了取货点，而车还在减速。静态证据全齐，**只剩运动证据说它没停稳**——
这正是「采信到站」最危险的窗口，也正是那四个条件存在的理由。原来的时间线（车停稳之后订单才成功）
不经过这个窗口。

现在两拍都在：先是「订单还没成功」那一拍（保留原样，不带判据，只作时间线），再是这一拍带
`L2-NL-03`，最后车停稳。
