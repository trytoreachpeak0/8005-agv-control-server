# 缺陷：补偿清空收尾之后车辆占用不释放，同一台车派不出下一单（control-server#131 的补偿路径复现）

Status: open，修复在 [control-server#131](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/131)（修复 PR #133）
Owner repository: `8005-agv-control-server`（`OnboardRecoveryCoordinator.ApplyCurrentResultAsync`）
Found by: control-server#88 的真装置 L2 `real-onboard-compensate-then-reconnect`，
`C:\Users\szy\Desktop\8005-workspace-v2\evidence\cs88-l2\compensate-then-reconnect-004\SUMMARY.md`（控制端本机，不入库）
Product at discovery: control-server `fp/v2-impl@bc5c8e78` 的产品代码（场景分支 `fp/b5-32-program61-paired-real-rig-scenarios@09d0bd60` 只加了场景与测试替身）；
onboard-hmi `w2g/fp-v2-impl@8153946b`；slots-simulator `main@fb5f7c59`；协议 `protocol-v2.0.0@86575456`

**同一个缺陷，已有记录，这里只补它在补偿路径上的真装置复现。**原记录是 control-server#86 分支上的
`docs/defects/20260918-in-flight-load-cancellation-keeps-vehicle-occupancy.md`（在途装货取消那条路径）。那份记录还没合入，
两个分支改同一个文件会冲突，所以另写这一份短的。#131 票面把补偿清空与故障货物交接两条列为「读代码得出，没有单独跑过」；
补偿清空这一条现在跑过了。

## 现象

`real-onboard-compensate-then-reconnect`：等人时杀车载端、门被空着关上，重启后中断结算报 `UNKNOWN`，维护人员按「补偿清空」。
补偿本身收敛正确，program#61 ② 要验的也都成立（`-004` 的 `L2-CR-00`～`06`、`08` 全部 PASS）：

- 工作流 `Reconciled`、`ALL_EMPTY`，需求 `Cancelled`，旅程 `Completed / CANCELLED_BY_LOAD_COMPENSATION`；
- CLOSED 的恢复会话快照被车确认，三份旧 revision 被取代，补偿命令被结算；
- 经协议故障代理断一次链路之后会话在新世代回到 `Ready`，五条恢复报文一条都没被重放。

唯一的红是 `L2-CR-07`「重连之后车还接得了单」：

```
L2-CR-07 FAIL | 未走到：Timed out after 120s waiting for: the next journey waits for a sublot. Last observed: "Blocked"
```

意思是：发布下一条需求后，服务端建了旅程，却一直停在 `Blocked`，车没有被派去取货点。收尾快照
（`snapshots/db-JourneyRuntimes.json`、`db-OrderIntents.json`）给出原因：

| 需求 | 旅程 | 停摆原因 | 该需求 `TO_PICKUP` 的 `VehicleOccupancyReleasedAt` |
| --- | --- | --- | --- |
| 被补偿的第一单 | `Completed` | `CANCELLED_BY_LOAD_COMPENSATION` | **空** |
| 下一单 | `Blocked` | `VEHICLE_OCCUPANCY_CONFLICT` | 空（从没派出） |

第一单补偿收尾时没有释放车辆占用，于是这台车被当成仍有在途订单，下一单派不出去。与 #131 在途取消那条的症状一字不差。

## 三次红，同一原因

| 证据 | 服务端提交 | 结论 | `L2-CR-07` 以外的红 |
| --- | --- | --- | --- |
| `compensate-then-reconnect-002` | `1fec68ab` | FAIL 7/9 | `L2-CR-04`：场景脚本把查询函数包进 `@()` 数错了行数（数据本身全部已结清），`1193d125` 已修 |
| `compensate-then-reconnect-003` | `1193d125` | FAIL 8/9 | 无 |
| `compensate-then-reconnect-004` | `09d0bd60` | FAIL 8/9 | 无 |

三次的服务端产品代码相同，`L2-CR-07` 都红在同一个 `VEHICLE_OCCUPANCY_CONFLICT`。同目录的 `*-stage\controlserver.db` 是当时的服务端库。
`-001`（`297bde97`）死在场景脚本读了补偿命令上不存在的 `state` 字段，没走到 `L2-CR-07`，只留在会话临时目录。

## 原因与修复

原因见 #131：`OnboardRecoveryCoordinator.ApplyCurrentResultAsync` 对在途取消、补偿清空、故障货物交接三种结果的手写终结不释放车辆占用。
场景判据不改，产品代码不在本票修。#131 合入后重跑本场景，`L2-CR-07` 应转绿。
