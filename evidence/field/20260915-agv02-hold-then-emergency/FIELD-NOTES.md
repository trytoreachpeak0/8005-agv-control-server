# 现场补记：run `20260915T075223Z-ca7b`（先暂停再急停，issue control-server#63）

本文件由操作会话在运行结束后追加，不改动工具生成的 `SUMMARY.md` 与各 `.jsonl`。

## 为什么做

票 19 第二次运行（`20260915-W1-agv02-reduced-emergency-drill-2/`）发现：订单仍在执行、急停锁住时，RIoT 持续报
`MT_RUNNING` 加 `speed=0`，产品 `HttpRiotMovementGateway.ReadMotion` 会读成 `Moving`，停稳证不出来（control-server#63）。
但产品真正的路径是先 `OrderHold`、证不出停住才急停，暂停后 RIoT 报什么没人测过。本次就测这条路径。

## 参与人与环境

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-09-15 |
| 现场人员（安全确认、解除前确认） | trytoreachpeak0（产品负责人，本人在现场） |
| 批准 | 用户在对话中批准整个序列（建单、行驶中暂停一次、确认暂停后急停一次），开跑前再次确认现场；取消单、解除急停分别单独批准 |
| 工具操作 | Claude Code 会话，运行主机 `LAB-WIN-01`；工具为未提交的加 `hold` 命令版（15:47 Release 构建） |
| 车辆与路线 | agv02，map 25（`老厂前线new`），站 210（关卡）→ 212（充电准备点1），空载 |
| 照片 | 无 |

## 时刻（+08:00）

| 时刻 | 事件 |
| --- | --- |
| 15:52:23～24 | `init`、`preflight`（直连 RIoT，卡片读中位 21 ms）、`status`：车在 210，`IDLE`，急停锁 `OK`，电量 87 |
| 约 15:54 | 现场人员确认条件：车上无货、仓门关着、人员在路线外、agv01 不去充电；批准开始 |
| 15:54:56 | `create-move` 210 → 212，订单 `order-2099768924827680768` |
| 15:55:03～05 | 原地旋转：`MT_RUNNING`、`speed=0`、站点 0 |
| 15:55:05.6 | `speed=0.18`；15:55:06.1 `speed=0.349`，`watch-moving` READY |
| 15:55:06.942 | `CMD_ORDER_HELD` 发出一次，`Accepted`，31 ms；发前采样 `speed=0.349`、`MT_RUNNING` |
| 15:55:07.047 | 订单 `orderState=7`（暂停），发出后约 0.1 s |
| 15:55:07.593 | `speed=0`、`movementState=MT_PAUSED`，产品读数 `NotMoving`（发出后约 0.65 s）；此后 20 s 观察窗内一直如此，`procState=USER_FORCE_IDLE` |
| 15:55:28.245 | 订单仍 `orderState=7`，`triggerEmergency` 发出一次，`Accepted`，93 ms；发前 `MT_PAUSED`、`speed=0` |
| 15:55:28.949 | `emergencyState=CAN_RECOVER`（发出后约 0.7 s）；`movementState` 保持 `MT_PAUSED`、`speed=0`，产品读数 `NotMoving` |
| 约 15:57 | **现场目视：车已停下**（现场人员答复「车已停，批准取消演练单」） |
| 15:58:12 | `CMD_ORDER_CANCEL` 发出一次，`Accepted`，36 ms；订单 `orderState=2`；车转 `IDLE`、`MT_FINISHED`；急停锁保持 `CAN_RECOVER` |
| 约 15:58 | 现场人员确认车已停稳、无货、仓门全部关好，批准解除（`trytoreachpeak0 stopped,empty,doors-closed`） |
| 15:58:59 | `cancelEmergency` 发出一次，`Accepted`，106 ms；3576 ms 后 `emergencyState=OK`；车 `IDLE`、`MT_FINISHED`、站点 0（停在 210 与 212 之间），安全原因 `RIOT_CONTROL_NOT_OK` |

## 结论（回答 control-server#63）

- **先暂停时，RIoT 报 `MT_PAUSED`**：`CMD_ORDER_HELD` 后约 0.1 s 订单进入暂停，约 0.65 s 车速归零，`movementState` 从 `MT_RUNNING` 变为 `MT_PAUSED`，产品 `ReadMotion` 读成 `NotMoving`。
- **暂停后再急停，仍报 `MT_PAUSED`**：急停锁住后没有变回 `MT_RUNNING`，产品读数保持 `NotMoving`。
- 所以产品「先 `OrderHold`、再急停」这条路径上，停稳证明在真车上**可以成立**；#63 描述的 `MT_RUNNING` 加 `speed=0` 只出现在「订单仍在执行时直接急停」的情况。
- 本次没有验证的：产品在 `OrderHold` 未被 RIoT 接受或未确认时直接急停的情况（那时订单仍在执行，按票 19 第二次运行的观察，会报 `MT_RUNNING` 加 `speed=0`）。
