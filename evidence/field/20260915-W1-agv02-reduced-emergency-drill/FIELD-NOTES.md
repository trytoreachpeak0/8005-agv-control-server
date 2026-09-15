# 现场补记：run `20260915T063522Z-0760`（第一次，判定无效）

本文件由操作会话在演练结束后追加，不改动工具生成的 `SUMMARY.md` 与各 `.jsonl`。

## 结论

**这次急停没有证明「RIoT 让行驶中的车停下」，判定为无效运行，证据原样保留。**
`triggerEmergency` 确实只发了一次（`Accepted`，98 ms），急停锁 2157 ms 后读到 `CAN_RECOVER`，
但发令时车还没有离开站点 210。用户 2026-09-15 批准改好工具后用新证据目录重做一次。

## 经过（时刻为 +08:00）

| 时刻 | 事件 | 来源 |
| --- | --- | --- |
| 14:35 | 用户在现场手动驾驶 agv02；卡片读到 `speed=0.349`、`MT_RUNNING`、站点 0 | `timeline.jsonl`（status） |
| 14:42:16 | 车停在 210（`MT_FINISHED`）；`create-move` 建单 210 → 212，订单 `order-2099750634810114048` 排队（orderState 1） | `create-move` |
| 14:42～14:44 | 车未接单，`watch-moving` 120 s 窗口错过，未发急停。用户确认当时车处于手动模式 | `watch-moving`、对话 |
| 14:48:57 | 用户切回自动后车接单：连续两个采样 `speed=0`、`MT_RUNNING`、`currentStationId=0`，工具判为「两站之间行驶」 | `watch-moving` |
| 14:48:58 | `triggerEmergency` 发出一次，`Accepted`，98 ms | `trigger`、`wire.jsonl` |
| 14:49:00 | `emergencyState=CAN_RECOVER` | `trigger` |
| 14:49～14:53 | 一直是 `speed=0`、`MT_RUNNING`、站点 0、orderState 3、`PROCESSING_ORDER`；工具停稳判据不认 `MT_RUNNING`，判 `stopped=False` | `trigger`、status 轮询 |
| — | **现场目视（用户）：车在站点 210 上原地旋转、尚未开出站点时就被急停，没有离开 210** | 对话 |
| 约 14:53～15:00 | 用户在 RIoT 界面手动收尾：先取消演练单，再解除急停。工具的 `cancel-order`／`release` 未调用（停稳判据会拒绝） | 对话 |
| 15:00:00 | 订单终态 orderState 2（已取消）；车 `IDLE`、`MT_FINISHED`；急停锁仍 `CAN_RECOVER` | status 轮询 |
| 15:00:11 | `emergencyState=OK`；该采样 `MT_RUNNING`、`speed=0`、站点 0（推测为旋转收尾） | status 轮询 |
| 15:00:34 | 车在 210，`MT_FINISHED`，`speed=0`，急停锁 `OK`，无订单 | status |

## 暴露的工具缺陷（在下一次运行前修复）

1. **「两站之间行驶」判得太松。**车在站点上原地旋转时，RIoT 就报 `MT_RUNNING`，站点号也变成 0，但线速度为 0。工具只看这两项，把原地旋转当成了行驶。修法：另要求上报车速大于阈值。
2. **急停锁住后停稳判据永远不成立。**订单仍在执行、急停锁住时，RIoT 持续报 `MT_RUNNING` 加 `speed=0`；工具要求 `movementState` 不是 `MT_RUNNING`，导致 `cancel-order` 与 `release` 都会被拒。修法：急停锁已锁住时，`speed=0` 加 `MT_RUNNING` 算静止。

## 本次得到的 RIoT 事实

- `triggerEmergency` 在车旋转阶段也被接受并立即闩锁（`CAN_RECOVER`）。
- 急停锁住、订单仍在执行时：`MT_RUNNING`、`speed=0`、`PROCESSING_ORDER`、orderState 3 保持不变。
- 急停锁住时取消订单：订单转 orderState 2，车转 `IDLE`、`MT_FINISHED`，急停锁保持 `CAN_RECOVER`。
- 解除后车不接着走（订单已终态），回到站点 210。
