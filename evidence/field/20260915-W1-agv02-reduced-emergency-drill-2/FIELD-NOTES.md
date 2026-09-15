# 现场补记：run `20260915T070351Z-333b`（第二次，有效）

本文件由操作会话在演练结束后追加，不改动工具生成的 `SUMMARY.md` 与各 `.jsonl`。
第一次运行 `20260915T063522Z-0760` 判为无效（车在站点原地旋转时即被急停），见同级目录
`20260915-W1-agv02-reduced-emergency-drill/FIELD-NOTES.md`。本次用修复了两处判据的工具重做，
重做与第二次急停由用户 2026-09-15 在对话中批准。

## 参与人与环境

| 项 | 内容 |
| --- | --- |
| 日期 | 2026-09-15 |
| 现场人员（驾驶、安全确认、解除前确认） | trytoreachpeak0（产品负责人，本人在现场） |
| 工具操作 | Claude Code 会话，运行主机 `LAB-WIN-01`，工具为未提交的修复版（15:01 Release 构建） |
| RIoT 配合人 | 无（第一次运行的手动收尾由现场人员在 RIoT 界面完成） |
| 车辆与路线 | agv02，map 25（`老厂前线new`），站 210（关卡）→ 212（充电准备点1），空载 |
| 照片 | 本次未拍摄，无照片指针 |

## 时刻（+08:00）

| 时刻 | 事件 | 来源 |
| --- | --- | --- |
| 15:03:51～53 | `init`、`preflight`（直连 RIoT，卡片读中位 19.6 ms）、`status`：车在 210，`IDLE`，急停锁 `OK`，电量 94 | 工具 |
| 约 15:07 | 现场人员确认条件：车上无货、仓门关着、人员在路线外、agv01 不去充电；批准开始 | 对话 |
| 15:07:43 | `create-move` 210 → 212，订单 `order-2099757039231303680` | 工具 |
| 15:07:45.5～51.0 | 车接单后在站点原地旋转：`MT_RUNNING`、`speed=0`、站点 0，工具持续等待，未发令 | `timeline.jsonl` |
| 15:07:51.3 | `speed=0.17`；15:07:51.9 `speed=0.349`，`watch-moving` READY | `timeline.jsonl` |
| 15:07:52.344 | `triggerEmergency` 发出一次，`Accepted`，84 ms；发令前采样 `speed=0.349` | `trigger`、`wire.jsonl` |
| 15:07:53.050 | `speed=0.172`（减速中） | `timeline.jsonl` |
| 15:07:53.373 | `emergencyState=CAN_RECOVER`（发令后约 1.0 s） | `timeline.jsonl` |
| 15:07:53.400 | `speed=0`，此后持续为 0（发令后约 1.06 s） | `timeline.jsonl` |
| 约 15:10 | **现场目视：车已停下**（现场人员答复「车已停，批准取消演练单」） | 对话 |
| 15:10:32 | `CMD_ORDER_CANCEL` 发出一次，`Accepted`，37 ms；订单 orderState 2（已取消）；车转 `IDLE`、`MT_FINISHED`；急停锁保持 `CAN_RECOVER` | 工具 |
| 约 15:12 | 现场人员确认车已停稳、无货、仓门全部关好，批准解除（`trytoreachpeak0 stopped,empty,doors-closed`） | 对话 |
| 15:12:26 | `cancelEmergency` 发出一次，`Accepted`，106 ms；3584 ms 后 `emergencyState=OK` | 工具 |
| 15:12:27 | 车 `IDLE`、`MT_FINISHED`、`speed=0`、站点 0（停在 210 与 212 之间），无订单；安全原因 `RIOT_CONTROL_NOT_OK` | 工具 |

## 结论

- **RIoT 侧确实停车：PASS，双向确认。**RIoT 侧：发令时车速 0.349，约 1.06 s 降为 0 并保持，急停锁 `CAN_RECOVER`；现场侧：现场人员目视确认车已停下。
- **8005 侧只发一次：PASS**（本演练的工具调用 `SENDING` 1 条、`POST triggerEmergency` 1 次）。产品重试逻辑不重复发令由 L2 `emergency-stop-single-trigger` 证明，不由本演练证明。
- 收尾按「先取消演练单、后解除急停」完成，解除后车未再移动。
- 停稳判定用的是本工具判据，不是产品 `REQ-0247` 组合判定；取消单时停稳依赖「急停锁锁着时 `MT_RUNNING` + `speed=0` 算静止」这一条（`stillWhileLatchedRunning=true`）。

## RIoT 行为记录（两次运行合并）

- 接单后先原地旋转约 6 s，期间 `MT_RUNNING`、`speed=0`、`currentStationId=0`。
- 急停锁锁着、订单仍在执行时持续报 `MT_RUNNING`、`speed=0`、`PROCESSING_ORDER`。
- 锁着时取消订单：订单转已取消，车转 `IDLE`、`MT_FINISHED`，急停锁不变。
- 解除后车不接着走；本次解除后安全原因里仍有 `RIOT_CONTROL_NOT_OK`，车停在两站之间，需现场人员接管移回站点。
