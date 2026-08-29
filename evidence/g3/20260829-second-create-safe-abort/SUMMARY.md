# 第二次授权建单：因 `MT_NA` 安全中止，未建单未移动

## 运行类型

`AUTHORIZED_SINGLE_REAL_CREATE`。目的是验证
[`"--"` 占位符修复](../../docs/defects/20260829-queueing-order-placeholder-vehicle-key.md)
之后「建单 → `CONFIRMED` → 到站 → `AwaitingSublot`」的完整链路。用户已把车辆开回关卡并再次
给出同一路线的现场 GO 与逐次授权。

## 结果：在任何 RIoT mutation 之前安全中止

会话 fail-close 在 `Readiness=RecoveryRequired` / `ReasonCode=DEPARTURE_SAFETY_NOT_READY`，
`DepartureSafe=0`，`/health/ready` 为 HTTP 503。

- `RiotDispatchAuditEvents` **0 行**——未 arm、未 `CREATE_REQUEST`、未建单
- `AcceptedDemands`／`OrderIntents`／`JourneyRuntimes` 均为 0 行
- 车辆未移动，仍在站点 210
- stderr 为空，58105／58107／1502／58006 四端口全部回收

因此本次未验证到目标链路，但产品的失败关闭行为正确。

## 中止原因

安全投影（`RIOT_BEHAVIOR_LAB_R41` 谓词）返回
`motionState=UNKNOWN`，`reasonCodes=["RIOT_MOVEMENT_NOT_FINISHED"]`——**只有这一条**不满足，
其余全部通过：

| 谓词 | 当前值 | 结果 |
| --- | --- | --- |
| `speed == 0` | `0` | 通过 |
| `emergencyState == OK` | `OK` | 通过 |
| `breakSwitchState == MOVABLE` | `MOVABLE` | 通过 |
| `controlState == CONTROL_STATE_OK` | `CONTROL_STATE_OK` | 通过 |
| `locationState == LOCATION_STATE_RUNNING` | `LOCATION_STATE_RUNNING` | 通过 |
| 无非终态订单 | 无 | 通过 |
| `movementState == MT_FINISHED` | **`MT_NA`** | **不通过** |

`MT_NA` 表示当前没有移动任务（`moveTaskNo=0`）。用户是手动把车开回 210 的，没有产生一个
完成的 RIoT 移动任务，因此缺少「上一次移动已结束」的正向证据。首次真实建单那次之所以能
通过，是因为车辆当时刚由 RIoT 订单完成移动，处于 `MT_FINISHED`。

## 这是否为产品缺陷

目前判断**不是**。RIoT Behavior Lab 中没有任何契约把 `MT_NA` 认定为安全停稳状态；唯一一次
`MT_NA` 观测（Round 35）反而出现在「`orderState=3` 执行中但车未真正跑起来」的情形，正是不该
被当作安全的状态。在缺少证据前放宽该谓词等于削弱安全门禁，故未改动。

若后续要把 `MT_NA` 纳入安全停稳，须先在 Behavior Lab 中以实验确认其语义并升级为 `OBSERVED`
契约，再由本仓库按契约放宽，不得反过来先改代码。

## 复跑前置

车辆需完成一次 RIoT 移动订单以进入 `MT_FINISHED`。由于受理门禁本身依赖该安全事实，无法经
ControlServer 自举，须由现场／RIoT 侧直接下发一次移动（任意短程即可）。

另需注意电量：本次读数为 31%，已逼近已批准的 30% 阈值；若低于阈值，受理将改为
`BATTERY_POLICY_NOT_SATISFIED` 阻断。

## 绑定输入

- ControlServer 产品：`ControlServer_MVP@addc2fab11d506b19d9a94c83537ed24d4233ef8`
- package manifest SHA-256：`db0d1f91433f25fe0b647bccd20565343d9fb90106c61ba75c42725244fe37d2`
- OnboardHmi `84b7f3f`（配置未覆盖）、slots-simulator `fb5f7c5`、协议 `protocol-v0.1.1@1531489`

W2G-IS-00～07 的正式 G3 与 RC 保持 `INCONCLUSIVE`。
