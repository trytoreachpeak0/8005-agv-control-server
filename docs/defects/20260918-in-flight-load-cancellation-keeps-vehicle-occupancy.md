# 缺陷：在途装货经操作员取消终结后车辆占用不释放，同一台车再也派不出单

Status: open（修复票 [control-server#131](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/131)）
Owner repository: `8005-agv-control-server`
Found by: 真装置 L2 场景 `real-onboard-load-door-closed-empty-reopens`（control-server#86）本机调试跑
`C:\Users\szy\Desktop\8005-workspace-v2\evidence\cs86-l2\load-door-closed-empty-reopens-003\SUMMARY.md`
（工作区侧，不入库；同目录 `-003-stage\controlserver.db` 是当时的服务端库）
Product at discovery: `fp/v2-impl@bc5c8e78`（场景跑在 `fp/b5-30-adr0058-real-rig-scenarios@aad3c2c1`，产品代码相同）
Peers: 车载端 `w2g/fp-v2-impl@8153946b`、模拟器 `main@fb5f7c59`、`protocol-v2.0.0@8657545`（`AGV_FULL_PRODUCT`）

## 现象

program#55 之后，期限过后放弃装货的唯一出口是操作员按「取消装货」。场景走的正是这条路：期限前空关一次、
期限后空关两次，车都自己重开；然后按取消。取消本身收敛正确，判据 `L2-DC-07`～`-09`、`-11` 全部 PASS：
需求 `Cancelled`，旅程 `Completed / CANCELLED_BY_OPERATOR`，目标仓 `CLOSED/EMPTY/1/0`，全程没有 `FAILED`。

红的是车辆释放：

| 判据 | 期望 | 实际 |
| --- | --- | --- |
| `L2-DC-10` 调度租约与车辆占用都释放 | 租约已释放 / 占用已释放 / TO_GATE 0 | `ReleasedAt='2026-09-18 08:43:32…'` / `VehicleOccupancyReleasedAt=''` / TO_GATE 0 |
| `L2-DC-12` 同一台车 60 秒内接下一单 | 下一单 `AwaitingPickupArrival` | 下一单 `Blocked VEHICLE_OCCUPANCY_CONFLICT` |

服务端日志（`logs/control-server.out.log`）：

```
[16:39:25 ERR] Vehicle AGV-L2-001 already holds an in-flight order; the claim for W2G-2b839ee3-fe22-4e13-984a-bb55511b9b0c-PICKUP-1 was refused by the occupancy index.
```

`OrderIntents` 上有一个按 `VehicleKey` 的唯一部分索引（`VehicleOccupancyClaimedAt IS NOT NULL AND VehicleOccupancyReleasedAt IS NULL`），
一台车同一时刻只能占着一张单。被取消的那张 TO_PICKUP 单占用一直不释放，这台车此后的每一单都会被这个索引拒掉。

## 原因

`OnboardRecoveryCoordinator.ApplyCurrentResultAsync` 对已经下发过仓位命令的结果是手写终结：需求 `Cancelled`、
租约 `ReleasedAt`、仓位操作 `Cancelled`、旅程 `Completed`，唯独没有释放车辆占用。同一段代码服务三种结果：

1. 在途装货取消（`LoadCancellationResult`，`CANCELLED_BY_OPERATOR`）——本次复现的这条；
2. 补偿清空（`LoadCompensationResult`，`CANCELLED_BY_LOAD_COMPENSATION`）；
3. 故障货物交接（`FaultCargoRecoveryResult`，`TERMINATED_BY_FAULT_CARGO_HANDOFF`）。

后两条是读代码得出的，没有单独跑过。`PickupStopTermination.cs` 的类注释早就写明这条路径
"minus the vehicle occupancy"，并把收敛留给 control-server#81；#81 关闭时没有做这一步。扫码前取消
（control-server#83）已经改走 `PickupStopTermination`，会释放占用，所以 `load-cancelled-before-sublot` 是绿的。

合成场景没抓到它，是因为在途取消在合成装置上没有覆盖；G3 的 `g3-load-cancellation` 只断言租约释放，没有断言占用，
也没有在取消之后再派一单。

## 三次运行

| 目录 | 服务端 | 结果 | 说明 |
| --- | --- | --- | --- |
| `load-door-closed-empty-reopens-001` | `043463dd` | FAIL 1 条（`L2-DC-10`） | 第一次看到占用未释放 |
| `load-door-closed-empty-reopens-002` | `64191181` | FAIL 1 条 | 补了 `L2-DC-12`「同一台车接下一单」，但当时只要求旅程行存在，把 `Blocked` 的旅程误判成 PASS |
| `load-door-closed-empty-reopens-003` | `aad3c2c1` | FAIL 2 条（`L2-DC-10`、`-12`） | `L2-DC-12` 收紧为必须 `AwaitingPickupArrival`；其余 10 条 PASS |

三份证据都保留在 `C:\Users\szy\Desktop\8005-workspace-v2\evidence\cs86-l2\`。判据只加了、收紧了，没有放宽。

## 影响

program#55 把操作员按取消定为期限后放弃装货的唯一出口，所以这不是边角路径：在 v2 上每一次「操作员不装了」都会让
这台车永久失去派车资格，直到有人手工清库。它挡批次 5 出口（control-server#90 要重跑全部真装置场景）。

## 修复

不在 control-server#86 修（那张票只写场景）。方向见 control-server#131：三条路径收敛到 `PickupStopTermination`，
或在同一次提交里补上 TO_PICKUP 单的占用释放；另加 L1 钉住三种结果都释放占用。修复后
`real-onboard-load-door-closed-empty-reopens` 应整条变绿。
