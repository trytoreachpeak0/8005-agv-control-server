# 11:03:58 预约被撤回（CHARGING_WITHDRAWN_CHARGER_NO_LONGER_ELIGIBLE）的原因

以下分两类标注：「读到」是代码或日志里实读的，「推测」是没有直接证据的。

## 读到的

- 日志（`20-log-withdrawal-1104-1107.txt`）：11:07:16 记 2254，原文为 `"CHARGING_WITHDRAWN_CHARGER_NO_LONGER_ELIGIBLE": the charging commitment "charging:…:20261012T030358707Z" … is withdrawn before any order was created ("CHARGER_OCCUPANCY_UNKNOWN")`。两秒后，11:07:18，agv03 记 2240 重新预约 211，成功。
- 撤回发生在 `JourneyRuntimeEngine.Charging.cs:418` 的 `WithdrawChargingIfItNoLongerStandsAsync`，也就是出发前复核，独立审查 M1。它对已承诺的桩重跑一遍桩侧判定（`WhyCommittedChargerNoLongerStandsAsync`），只要答非空，就撤回承诺：周期结束、用途与预占在同一次保存里释放，下一轮再重新评估。这类撤回不计入「两次即停」，也不进冷却（`ChargingExecutionReasons.cs:17-20`）。
- `CHARGER_OCCUPANCY_UNKNOWN` 有两处来源，都在 `ChargingAllocator.cs:858-862` 和 `ChargerOccupancy.cs:43-46`：
  1. 占用快照里本车的观测缺失，或读不到当前站；
  2. 占用快照的 `Unknown` 非空。
  第 2 条覆盖三样事实中任何一样没读全：本项目车辆的位置、RIoT 未结束订单清单（状态 1、3、7、9）、订单目的站。只要有一样没读全，每个桩都答「未知」，按 REQ-0170 处理：fail-closed，未知就不分配。
- 结论：211 没有在哪一刻真的「变得不合格」。是某一轮出发前复核时，占用事实没读全，于是按「未知即不成立」撤回了承诺；下一轮事实读全了，又重新承诺。名册、桩暂停、路线这几项都没有变。

## 推测（没有直接证据）

- 是三样事实里的哪一样没读全，日志没有记录：2254 只写了原因码，没写 `Unknown` 的具体内容。最可能的是 **RIoT 未结束订单清单读不全**。依据有两点：同一时段 11:04:02，agv02 的空闲返回出发门报了 `RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN`；当时 RIoT 上约有 250 张未结束单，分页读取时偶尔会对不上（昨晚也见过）。这一条没有被直接证实。
- 闸门关着时，承诺发不出去，于是每轮都会重做出发前复核。所以等待越久，碰上一次「读不全」的机会越多。这可以解释为什么三分钟后才撤回；但复核在闸门关着时的实际频率，我没有逐轮核对。

## 影响

只是来回抖动：撤回后两秒就重新承诺，没有发出过单，车也没有动；211 的独占前后都只有一行。这符合撤回类的设计（「什么也没发出过……预占当场放」）。如果要让日志能直接说出是哪样事实没读全，需要在 2254 的细节里带上 `Unknown`。要不要开票由调度定。
